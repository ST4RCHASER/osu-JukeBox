#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;

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
    /// Milliseconds per frame spent simulating while the PRELOAD is still running — a big slice, on
    /// purpose.
    ///
    /// <para>
    /// This is the rethink of the 47-real-replays freeze. The old model tried to keep a cushion just
    /// ahead of the playhead and back off once it had one; on a slider-heavy map under real load the
    /// per-step cost is high enough that the simulation fell BELOW real time and the board froze
    /// mid-song, no matter how the budget tiers were tuned — racing the playhead is the wrong model.
    /// Now every timeline is simulated flat out to COMPLETION up front (the rail shows a progress
    /// state until then), and once complete every number is a pure lookup with nothing simulating
    /// live, so there is no playhead left to lose the race against. A finite amount of work done as
    /// fast as the machine allows, rather than an open-ended race it can lose.
    /// </para>
    /// </summary>
    internal double PreloadBudgetMs { get; init; } = 40;

    /// <summary>
    /// How far the preload has got, 0 to 1 — the least-advanced play's recording as a fraction of
    /// the map's length, so the rail can show "simulating N%". One when every play is recorded.
    /// </summary>
    public double Progress
    {
        get
        {
            if (AllComplete)
                return 1;

            double end = mapEndTime;

            if (end <= 0 || simulations.Count == 0)
                return 0;

            // The board is only as ready as its slowest player, so progress is the MINIMUM.
            double least = simulations.Min(s => Math.Min(s.Timeline.SimulatedTo, end));
            return Math.Clamp(least / end, 0, 1);
        }
    }

    /// <summary>The map's last object time, cached from the first simulation to have loaded. Used as
    /// the denominator for <see cref="Progress"/>.</summary>
    private double mapEndTime;

    /// <summary>
    /// Steps to run on one play before re-checking which is furthest behind. Picking the laggard
    /// costs a scan of every simulation, so doing it per step would spend the budget on choosing
    /// rather than on simulating.
    /// </summary>
    private const int steps_per_slice = 24;

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

    /// <summary>
    /// The mods each play was actually simulated under, indexed like <see cref="Timelines"/>.
    ///
    /// <para>
    /// Recorded when the play starts rather than read off the layer, because the layers are
    /// disposed the moment their recording finishes — on a short map they can be gone before
    /// anything gets to look at them. These are the mods the scores on the board were computed
    /// with, which is the thing worth being able to check.
    /// </para>
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<Mod>> SimulatedMods
        => simulations.Select(s => s.Mods).ToList();

    /// <summary>Test hook: the scoring mode each play's numbers were recorded under, indexed like
    /// <see cref="Timelines"/> — Classic for a CL play, Standardised otherwise.</summary>
    internal IReadOnlyList<osu.Game.Rulesets.Scoring.ScoringMode> ScoringModes
        => simulations.Select(s => s.ScoringMode).ToList();

    /// <summary>
    /// Test hook: whether every play was simulated ignoring the shared mod selection. False means
    /// somebody's score is being computed under another player's mods.
    /// </summary>
    internal bool EveryPlaySimulatedUnderItsOwnMods
        => simulations.Count > 0 && simulations.All(s => s.RecordedModsOnly);

    /// <summary>Per-player overrides. Each play is SCORED here, so a mod override has to be read at
    /// this point or the board's numbers would not reflect it. Null in a bare test host.</summary>
    [Resolved(canBeNull: true)]
    private Replays.PlayerOverrideStore? overrideStore { get; set; }

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

        // Difficulty attributes are shared across every play that used the same mods — see
        // ReplayPerformance. One pass over the map per mod set, not per player.
        var attributeCache = new Dictionary<string, DifficultyAttributes>(StringComparer.Ordinal);

        foreach (var replay in replays)
        {
            var manual = new ManualClock();
            var working = new FlatWorkingBeatmap(osuFile);
            var layer = new LazerChartLayer(working, osuFile, replay.Score)
            {
                RelativeSizeAxes = Axes.Both,
                AlwaysPresent = true,
                TrackLiveScore = true,
                Clock = new FramedClock(manual),

                // The scores on the board are computed HERE, so this is the flag that decides
                // whether they are right. Under the shared Chart-tab selection every player was
                // being scored with player one's mods.
                UseRecordedReplayModsOnly = true,

                // A per-player mod override re-scores that one play under the mods the user chose,
                // leaving everyone else on what they recorded. Null when unset.
                // Skin is not set here: this play is off-screen and only its numbers are wanted, and
                // the skin changes nothing about the score. The per-player skin reaches the VISIBLE
                // renderers (the combine chart and the grid cells) only.
                OverrideMods = overrideStore?.Peek(replay)?.Mods,
            };

            simulations.Add(new Simulation(layer, manual, (FramedClock)layer.Clock, working, attributeCache));
            AddInternal(layer);
        }

        Timelines = simulations.Select(s => s.Timeline).ToList();
    }

    protected override void Update()
    {
        base.Update();

        if (AllComplete)
            return;

        // Flat out to completion: no playhead, no cushion, no backing off. Spend a big slice every
        // frame on whichever play is furthest behind until every timeline is recorded. The board
        // shows a progress state meanwhile; after this there is nothing left to simulate live.
        var spent = Stopwatch.StartNew();

        while (spent.Elapsed.TotalMilliseconds < PreloadBudgetMs)
        {
            // Always the play that is FURTHEST BEHIND, so the slowest player — the one the board is
            // waiting on — is the one raised, rather than advancing all forty-seven in lockstep.
            var laggard = leastAdvanced();

            if (laggard == null)
                break;

            // Cache the map length off the first loaded play, for the progress fraction.
            if (mapEndTime <= 0 && laggard.EndTime is { } end)
                mapEndTime = end;

            for (int i = 0; i < steps_per_slice && !laggard.Timeline.Complete; i++)
                advance(laggard);
        }
    }

    /// <summary>The incomplete play whose recording has got the least far, or null when all are done.</summary>
    private Simulation? leastAdvanced()
    {
        Simulation? worst = null;

        foreach (var simulation in simulations)
        {
            if (simulation.Timeline.Complete)
                continue;

            if (worst == null || simulation.Timeline.SimulatedTo < worst.Timeline.SimulatedTo)
                worst = simulation;
        }

        return worst;
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

        /// <summary>The mods this play was simulated under, kept past the renderer's disposal.</summary>
        public IReadOnlyList<Mod> Mods { get; private set; } = Array.Empty<Mod>();

        /// <summary>The scoring mode this play's numbers were recorded under — Classic for a play made
        /// under the Classic mod, Standardised otherwise.</summary>
        public osu.Game.Rulesets.Scoring.ScoringMode ScoringMode { get; private set; }

        /// <summary>
        /// Whether this play's renderer was told to ignore the shared mod selection. Captured from
        /// the layer rather than assumed, so that removing the flag at the construction site is
        /// visible here — asserting a constant would prove nothing.
        /// </summary>
        public bool RecordedModsOnly { get; private set; }

        private bool subscribed;
        private int lastCombo;

        public double? EndTime => Layer.PlayableBeatmap?.HitObjects.LastOrDefault()?.GetEndTime();

        public Simulation(LazerChartLayer layer, ManualClock manual, FramedClock framed, IWorkingBeatmap beatmap, Dictionary<string, DifficultyAttributes> attributeCache)
        {
            Layer = layer;
            Manual = manual;
            Framed = framed;
            this.beatmap = beatmap;
            this.attributeCache = attributeCache;
        }

        private readonly IWorkingBeatmap beatmap;
        private readonly Dictionary<string, DifficultyAttributes> attributeCache;

        private ReplayPerformance? performanceFor(LazerChartLayer layer)
            => layer.Ruleset == null ? null : ReplayPerformance.Create(layer.Ruleset, beatmap, layer.ActiveMods, attributeCache);

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
            Mods = Layer.ActiveMods;
            RecordedModsOnly = Layer.UseRecordedReplayModsOnly;

            // A play made under Classic (every stable replay, or a per-player CL override) must be
            // SCORED the classic way too — its rail number and its place in a score-ranked knockout
            // are the classic score, not lazer's standardised one. Non-classic plays stay
            // standardised. Per replay, off its own mods.
            bool classic = Mods.Any(m => m is ModClassic);
            ScoringMode = classic ? osu.Game.Rulesets.Scoring.ScoringMode.Classic : osu.Game.Rulesets.Scoring.ScoringMode.Standardised;

            var score = Layer.LiveScore;

            // For a classic play, each recorded score is lazer's OWN standardised→classic conversion
            // (ScoreInfoExtensions.GetDisplayScore), fed from the live processor via PopulateScore into
            // a single reused ScoreInfo — no per-judgement allocation, no re-implementing the formula.
            ScoreInfo? classicScore = classic && Layer.Ruleset != null
                ? new ScoreInfo { Ruleset = Layer.Ruleset.RulesetInfo }
                : null;

            var performance = performanceFor(Layer);

            Layer.DrawableRuleset.NewResult += result =>
            {
                int combo = score.Combo.Value;

                // A break is a combo the player HELD going to nothing. Judging it on the result
                // type instead would count a miss on the very first object, where there was no
                // combo to break.
                bool broke = lastCombo > 0 && combo == 0;

                // How MUCH was lost, captured before it is overwritten. The combo after a break is
                // always zero and says nothing about the size of the thing that just ended.
                int lost = broke ? lastCombo : 0;

                lastCombo = combo;

                long total;

                if (classicScore != null)
                {
                    score.PopulateScore(classicScore);
                    total = classicScore.GetDisplayScore(osu.Game.Rulesets.Scoring.ScoringMode.Classic);
                }
                else
                {
                    total = score.TotalScore.Value;
                }

                Timeline.Record(new TimelinePoint(
                    result.TimeAbsolute,
                    total,
                    combo,
                    score.Accuracy.Value,
                    broke,
                    // The RAW rank name, not a display letter. Skins name their grade graphics after
                    // it — a perfect play's texture is "ranking-X-small", never "ranking-SS-small" —
                    // so converting here would leave the skin lookup asking for a file that cannot
                    // exist, and every board would silently fall back to text. The row converts for
                    // display; see KnockoutBoard.Row.
                    score.Rank.Value.ToString(),
                    performance?.PointsFor(score) ?? 0,
                    lost,
                    // What this judgement actually landed, so the combine board can show a recent
                    // miss/50/100 beside the player.
                    result.Type));
            };
        }
    }
}
