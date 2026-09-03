#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Objects;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Plays every replay through at many times normal speed, off screen, and records what each
/// player's scoreboard read at every moment. The result is a <see cref="ReplayTimeline"/> per
/// player that the visible scoreboard reads by time.
///
/// <para>
/// The alternative — a live score processor per player, fed as the replay plays — is what the grid
/// does, and it is wrong in a way that only shows up when the user touches the seek bar: judgements
/// arrive exactly once, so skipping forward loses the ones that were jumped over and going back
/// un-judges nothing. Recorded up front, the numbers are a lookup, and a lookup cannot be out of
/// sync with the clock however the viewer got there.
/// </para>
///
/// <para>
/// Simulating is not free, but the ratio is what makes it work: a 185-second map with 600 objects
/// takes about 870ms to play through, roughly 210x faster than watching it. Spread across frames on
/// a budget, the simulation runs far ahead of the playhead within the first second or two, so what
/// the viewer is looking at has always already been computed.
/// </para>
/// </summary>
public partial class ReplaySimulator : CompositeDrawable
{
    private readonly string osuFile;
    private readonly IReadOnlyList<ReplayAttachment> replays;

    private readonly List<Simulation> simulations = new List<Simulation>();

    /// <summary>One recorded play per replay, in the order they were given.</summary>
    public IReadOnlyList<ReplayTimeline> Timelines { get; private set; } = Array.Empty<ReplayTimeline>();

    /// <summary>Whether every play has been recorded to the end.</summary>
    public bool AllComplete => simulations.Count > 0 && simulations.All(s => s.Timeline.Complete);

    /// <summary>How far the LEAST advanced simulation has got — the point up to which every
    /// player's numbers are known, and therefore how far the viewer can be trusted to look.</summary>
    public double SimulatedTo => simulations.Count == 0 ? 0 : simulations.Min(s => s.Timeline.SimulatedTo);

    /// <summary>
    /// Milliseconds of real time per frame this may spend, shared across every replay. Four is
    /// about a quarter of a 60fps frame, which at the measured speed still buys tens of seconds of
    /// gameplay per second of wall clock — far more headroom than staying ahead of playback needs.
    /// </summary>
    internal double BudgetMs { get; init; } = 4;

    /// <summary>Gameplay time each simulation step advances. Matches a 60fps frame, because that
    /// is the granularity real gameplay is judged at and a coarser step changes the result.</summary>
    private const double step_ms = 16;

    /// <summary>Test hook: total simulation steps run, for measuring the cost of this.</summary>
    internal int StepsRun { get; private set; }

    /// <summary>
    /// Test hook: hidden renderers still in the tree. Falls to zero once every play is recorded —
    /// they are scaffolding, not part of the running view.
    ///
    /// <para>
    /// Counted from the actual CHILDREN rather than from a "retired" flag. The flag version of this
    /// was satisfied by setting the flag, so deleting the line that actually removes the renderer
    /// left the test passing while the renderers all stayed alive.
    /// </para>
    /// </summary>
    internal int LiveRenderers => InternalChildren.OfType<LazerChartLayer>().Count();

    public ReplaySimulator(string osuFile, IReadOnlyList<ReplayAttachment> replays)
    {
        this.osuFile = osuFile;
        this.replays = replays;

        RelativeSizeAxes = Axes.Both;

        // Nothing here is meant to be seen. AlwaysPresent keeps the framework updating it anyway —
        // an absent drawable never updates, and a simulation that does not update never finishes.
        Alpha = 0;
        AlwaysPresent = true;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        foreach (var replay in replays)
        {
            var manual = new ManualClock();
            var layer = new LazerChartLayer(new FlatWorkingBeatmap(osuFile), osuFile, replay.Score)
            {
                RelativeSizeAxes = Axes.Both,
                AlwaysPresent = true,
                TrackLiveScore = true,
                Clock = new FramedClock(manual),
            };

            simulations.Add(new Simulation(layer, manual, (FramedClock)layer.Clock));
            AddInternal(layer);
        }

        Timelines = simulations.Select(s => s.Timeline).ToList();
    }

    protected override void Update()
    {
        base.Update();

        if (AllComplete)
            return;

        var spent = Stopwatch.StartNew();

        // Round robin rather than finishing one player before starting the next: the scoreboard
        // needs EVERY player's numbers at the current moment, so the useful measure of progress is
        // the least advanced simulation, and running them together is what raises it.
        while (spent.Elapsed.TotalMilliseconds < BudgetMs)
        {
            bool anyAdvanced = false;

            foreach (var simulation in simulations)
            {
                if (simulation.Timeline.Complete)
                    continue;

                advance(simulation);
                anyAdvanced = true;
            }

            if (!anyAdvanced)
                break;
        }
    }

    private void advance(Simulation simulation)
    {
        if (!simulation.Layer.IsLoaded)
            return;

        simulation.Subscribe();

        simulation.Manual.CurrentTime += step_ms;
        simulation.Framed.ProcessFrame();
        simulation.Layer.UpdateSubTree();
        StepsRun++;

        int objects = simulation.Layer.ObjectCount;
        var score = simulation.Layer.LiveScore;

        if (objects > 0 && score != null && score.JudgedHits >= objects)
        {
            finish(simulation, simulation.Manual.CurrentTime);
            return;
        }

        // Backstop: a replay whose frames run out before the map does leaves objects unjudged
        // forever, and without this the simulation would grind on to the heat death of the song.
        if (simulation.EndTime is { } end && simulation.Manual.CurrentTime > end + 5000)
            finish(simulation, end);
    }

    /// <summary>
    /// Records the play as finished and THROWS AWAY the renderer that produced it.
    ///
    /// <para>
    /// A simulation layer is a whole gameplay renderer — the same weight as a visible one — and the
    /// only thing anyone wants from it is the timeline, which is a list of numbers. Keeping it
    /// alive would double the renderers for the entire song for no benefit; dropping it means the
    /// doubled cost lasts only the few seconds the recording takes. Measured on a twelve-cell grid,
    /// that is the difference between paying for twenty-four renderers all song and paying for
    /// twelve after the opening.
    /// </para>
    /// </summary>
    private void finish(Simulation simulation, double endTime)
    {
        simulation.Timeline.MarkComplete(endTime);

        RemoveInternal(simulation.Layer, true);
        simulation.Retired = true;
    }

    /// <summary>One player's off-screen play and the record being taken of it.</summary>
    private sealed class Simulation
    {
        public readonly LazerChartLayer Layer;
        public readonly ManualClock Manual;
        public readonly FramedClock Framed;
        public readonly ReplayTimeline Timeline = new ReplayTimeline();

        /// <summary>Whether the renderer behind this play has been disposed, its work done.</summary>
        public bool Retired;

        private bool subscribed;
        private int lastCombo;

        public double? EndTime => Layer.PlayableBeatmap?.HitObjects.LastOrDefault()?.GetEndTime();

        public Simulation(LazerChartLayer layer, ManualClock manual, FramedClock framed)
        {
            Layer = layer;
            Manual = manual;
            Framed = framed;
        }

        /// <summary>
        /// Starts recording, once the ruleset exists. Subscribed AFTER the layer's own score
        /// processor deliberately: handlers run in order, so this one sees the totals already
        /// updated for the judgement that triggered it rather than the previous state.
        /// </summary>
        public void Subscribe()
        {
            if (subscribed || Layer.DrawableRuleset == null || Layer.LiveScore == null)
                return;

            subscribed = true;
            var score = Layer.LiveScore;

            Layer.DrawableRuleset.NewResult += result =>
            {
                int combo = score.Combo.Value;

                // A break is a combo the player HELD going to nothing. Judging it on the result
                // type instead would count a miss on the very first object, where there was no
                // combo to break.
                bool broke = lastCombo > 0 && combo == 0;
                lastCombo = combo;

                Timeline.Record(new TimelinePoint(
                    result.TimeAbsolute,
                    score.TotalScore.Value,
                    combo,
                    score.Accuracy.Value,
                    broke));
            };
        }
    }
}
