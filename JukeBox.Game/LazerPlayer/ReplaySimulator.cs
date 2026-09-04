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
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Records what every replay's scoreboard read at every moment, so the visible board is a LOOKUP by
/// time rather than a running total — the only thing that survives a seek intact.
///
/// <para>
/// osu! standard maps are judged by <see cref="AnalyticReplayRecorder"/>: no gameplay renderer, one
/// linear pass over the frames, a whole map in milliseconds. This is the danser-speed path — 50
/// replays that took two minutes through 47 full <c>DrawableRuleset</c>s stepped 16ms at a time now
/// preload in about a second, and there is no drawable to stall on a slider-heavy section. The other
/// rulesets (grid only — combine is osu-only) have no analytic judge here, so they fall back to the
/// old drawable simulation, which is also kept as the ORACLE the analytic path is validated against
/// (see TestSceneReplaySimulator).
/// </para>
/// </summary>
public partial class ReplaySimulator : CompositeDrawable
{
    private readonly string osuFile;
    private readonly IReadOnlyList<ReplayAttachment> replays;

    /// <summary>The recorded plays, one per replay in the order given — stable instances the board
    /// captures once and watches fill in.</summary>
    private readonly List<ReplayTimeline> timelines = new List<ReplayTimeline>();

    /// <summary>The drawable simulations (non-osu fallback, or the forced oracle). Empty on the
    /// analytic path.</summary>
    private readonly List<Simulation> simulations = new List<Simulation>();

    /// <summary>The analytic jobs (osu). Empty on the drawable path.</summary>
    private readonly List<AnalyticJob> analyticJobs = new List<AnalyticJob>();

    /// <summary>Difficulty attributes are shared across plays of the same mod set — one pass per mod
    /// set, not per player (see <see cref="ReplayPerformance"/>).</summary>
    private readonly Dictionary<string, DifficultyAttributes> attributeCache = new Dictionary<string, DifficultyAttributes>(StringComparer.Ordinal);

    private IWorkingBeatmap working = null!;
    private Ruleset ruleset = null!;

    /// <summary>Whether this map is judged analytically (osu) rather than by the drawable renderer.</summary>
    private bool analytic;

    /// <summary>
    /// Opt-in: use the fast analytic judge for osu maps. OFF by default — the DRAWABLE renderer is the
    /// shipping path because its numbers are exactly lazer's. The analytic judge now matches the
    /// drawable oracle on the real beatmap to the point that grades and combos agree and accuracy is
    /// within ~1%, but that last ~1% on hard plays is not yet bit-exact, so it stays behind this flag
    /// until it fully matches (then the default can flip). Kept in-tree and validated against the
    /// oracle so the remaining gap (circle timing-grade + slider-tick edges) can be closed later.
    /// </summary>
    internal bool UseAnalyticJudge { get; init; }

    /// <summary>
    /// Test seam: force the drawable simulation even for an osu map. The cross-validation tests set it
    /// to get lazer's real gameplay as the oracle the analytic judge is checked against.
    /// </summary>
    internal bool ForceDrawableSimulation { get; init; }

    /// <summary>One recorded play per replay, in the order they were given.</summary>
    public IReadOnlyList<ReplayTimeline> Timelines => timelines;

    /// <summary>Whether every play has been recorded to the end.</summary>
    public bool AllComplete => timelines.Count > 0 && timelines.All(t => t.Complete);

    /// <summary>How far the LEAST advanced play's recording has got — the point up to which every
    /// player's numbers are known.</summary>
    public double SimulatedTo => timelines.Count == 0 ? 0 : timelines.Min(t => t.SimulatedTo);

    /// <summary>Milliseconds per frame spent recording while the preload runs — a big slice, so the
    /// finite work finishes as fast as the machine allows without freezing a single frame.</summary>
    internal double PreloadBudgetMs { get; init; } = 40;

    /// <summary>
    /// How far the preload has got, 0 to 1. On the analytic path that is the fraction of plays
    /// recorded (each is recorded whole, in one step); on the drawable path it is the least-advanced
    /// play's recording as a fraction of the map length.
    /// </summary>
    public double Progress
    {
        get
        {
            if (AllComplete)
                return 1;

            if (analytic)
                return analyticJobs.Count == 0 ? 0 : (double)analyticJobs.Count(j => j.Recorded) / analyticJobs.Count;

            double end = mapEndTime;

            if (end <= 0 || simulations.Count == 0)
                return 0;

            double least = simulations.Min(s => Math.Min(s.Timeline.SimulatedTo, end));
            return Math.Clamp(least / end, 0, 1);
        }
    }

    /// <summary>The map's last object time, cached from the first drawable simulation to load.</summary>
    private double mapEndTime;

    private const int steps_per_slice = 24;

    private const double step_ms = 16;

    /// <summary>Test hook: total drawable simulation steps run. Zero on the analytic path, which does
    /// not step.</summary>
    internal int StepsRun { get; private set; }

    /// <summary>Test hook: hidden drawable renderers still in the tree. Always zero on the analytic
    /// path (there are none), and falls to zero on the drawable path once every play is recorded.</summary>
    internal int LiveRenderers => InternalChildren.OfType<LazerChartLayer>().Count();

    /// <summary>The mods each play was recorded under, indexed like <see cref="Timelines"/>.</summary>
    internal IReadOnlyList<IReadOnlyList<Mod>> SimulatedMods
        => analytic ? analyticJobs.Select(j => j.Mods).ToList() : simulations.Select(s => s.Mods).ToList();

    /// <summary>Test hook: the scoring mode each play was recorded under — Classic for a CL play,
    /// Standardised otherwise.</summary>
    internal IReadOnlyList<osu.Game.Rulesets.Scoring.ScoringMode> ScoringModes
        => analytic ? analyticJobs.Select(j => j.ScoringMode).ToList() : simulations.Select(s => s.ScoringMode).ToList();

    /// <summary>Test hook: whether every play was recorded under its OWN mods rather than a shared
    /// selection. Always true on the analytic path, which only ever reads a replay's recorded mods
    /// (plus any per-player override).</summary>
    internal bool EveryPlaySimulatedUnderItsOwnMods
        => analytic ? analyticJobs.Count > 0 : simulations.Count > 0 && simulations.All(s => s.RecordedModsOnly);

    /// <summary>Per-player overrides. A play is SCORED under its override mods if one is set, on both
    /// paths. Null in a bare test host.</summary>
    [Resolved(canBeNull: true)]
    private Replays.PlayerOverrideStore? overrideStore { get; set; }

    public ReplaySimulator(string osuFile, IReadOnlyList<ReplayAttachment> replays)
    {
        this.osuFile = osuFile;
        this.replays = replays;

        RelativeSizeAxes = Axes.Both;

        // Nothing here is meant to be seen. AlwaysPresent keeps the framework updating it anyway — an
        // absent drawable never updates, and a drawable simulation that does not update never finishes.
        Alpha = 0;
        AlwaysPresent = true;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        working = new FlatWorkingBeatmap(osuFile);

        // A replay is never converted (its frames belong to the ruleset it was played on), so an osu
        // beatmap means osu replays — and osu is the only ruleset with an analytic judge here. The
        // other three fall back to the drawable simulation, as does the forced-oracle test seam.
        bool osu = working.BeatmapInfo.Ruleset.OnlineID == 0;
        analytic = osu && UseAnalyticJudge && !ForceDrawableSimulation;

        if (analytic)
        {
            ruleset = new OsuRuleset();

            foreach (var replay in replays)
            {
                var job = new AnalyticJob(replay);
                analyticJobs.Add(job);
                timelines.Add(job.Timeline);
            }

            return;
        }

        foreach (var replay in replays)
        {
            var manual = new ManualClock();
            var layerBeatmap = new FlatWorkingBeatmap(osuFile);
            var layer = new LazerChartLayer(layerBeatmap, osuFile, replay.Score)
            {
                RelativeSizeAxes = Axes.Both,
                AlwaysPresent = true,
                TrackLiveScore = true,
                Clock = new FramedClock(manual),
                UseRecordedReplayModsOnly = true,
                OverrideMods = overrideStore?.Peek(replay)?.Mods,
            };

            var simulation = new Simulation(layer, manual, (FramedClock)layer.Clock, layerBeatmap, attributeCache);
            simulations.Add(simulation);
            timelines.Add(simulation.Timeline);
            AddInternal(layer);
        }
    }

    protected override void Update()
    {
        base.Update();

        if (AllComplete)
            return;

        if (analytic)
        {
            runAnalyticBudget();
            return;
        }

        // Drawable path: flat out to completion, spending a big slice every frame on whichever play is
        // furthest behind until every timeline is recorded.
        var spent = Stopwatch.StartNew();

        while (spent.Elapsed.TotalMilliseconds < PreloadBudgetMs)
        {
            var laggard = leastAdvanced();

            if (laggard == null)
                break;

            if (mapEndTime <= 0 && laggard.EndTime is { } end)
                mapEndTime = end;

            for (int i = 0; i < steps_per_slice && !laggard.Timeline.Complete; i++)
                advance(laggard);
        }
    }

    /// <summary>Records analytic jobs within this frame's budget — at least one per frame so progress
    /// is always made, then as many more as fit, since each job is a whole play recorded in one go.</summary>
    private void runAnalyticBudget()
    {
        var spent = Stopwatch.StartNew();
        bool first = true;

        foreach (var job in analyticJobs)
        {
            if (job.Recorded)
                continue;

            if (!first && spent.Elapsed.TotalMilliseconds >= PreloadBudgetMs)
                break;

            first = false;
            record(job);
        }
    }

    private void record(AnalyticJob job)
    {
        var score = job.Replay.Score;

        // A replay that never decoded has no frames to judge; record it as an instantly-complete empty
        // play rather than leaving it pending forever.
        if (score == null)
        {
            job.Timeline.MarkComplete(0);
            job.Recorded = true;
            return;
        }

        // The replay's own recorded mods, unless the user set a per-player override for this one — the
        // same rule the drawable layer follows under UseRecordedReplayModsOnly.
        var mods = overrideStore?.Peek(job.Replay)?.Mods ?? Replays.ReplayMods.ForGameplay(score);

        var recorded = AnalyticReplayRecorder.Record(working, ruleset, mods, score, attributeCache, job.Timeline);

        job.Mods = recorded.Mods;
        job.ScoringMode = recorded.ScoringMode;
        job.Recorded = true;
    }

    /// <summary>The incomplete drawable play whose recording has got the least far, or null when all
    /// are done.</summary>
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

        // The play is done only when every judgement has landed, so compare against the TOTAL
        // judgeable count (nested slider judgements included), not the top-level object count.
        int objects = simulation.Layer.JudgeableObjectCount;
        var score = simulation.Layer.LiveScore;

        if (objects > 0 && score != null && score.JudgedHits >= objects)
        {
            finish(simulation, simulation.Manual.CurrentTime);
            return;
        }

        // Backstop: a replay whose frames run out before the map does leaves objects unjudged forever.
        if (simulation.EndTime is { } end && simulation.Manual.CurrentTime > end + 5000)
            finish(simulation, end);
    }

    /// <summary>Records the drawable play as finished and THROWS AWAY the renderer that produced it —
    /// a whole gameplay renderer kept alive for a list of numbers would double the cost all song.</summary>
    private void finish(Simulation simulation, double endTime)
    {
        simulation.Timeline.MarkComplete(endTime);

        RemoveInternal(simulation.Layer, true);
        simulation.Retired = true;
    }

    /// <summary>One osu play waiting to be (or already) judged analytically.</summary>
    private sealed class AnalyticJob
    {
        public readonly ReplayAttachment Replay;
        public readonly ReplayTimeline Timeline = new ReplayTimeline();

        public bool Recorded;
        public IReadOnlyList<Mod> Mods { get; set; } = Array.Empty<Mod>();
        public osu.Game.Rulesets.Scoring.ScoringMode ScoringMode { get; set; }

        public AnalyticJob(ReplayAttachment replay)
        {
            Replay = replay;
        }
    }

    /// <summary>One player's off-screen DRAWABLE play and the record being taken of it (non-osu
    /// fallback, and the oracle the analytic path is validated against).</summary>
    private sealed class Simulation
    {
        public readonly LazerChartLayer Layer;
        public readonly ManualClock Manual;
        public readonly FramedClock Framed;
        public readonly ReplayTimeline Timeline = new ReplayTimeline();

        public bool Retired;

        public IReadOnlyList<Mod> Mods { get; private set; } = Array.Empty<Mod>();

        public osu.Game.Rulesets.Scoring.ScoringMode ScoringMode { get; private set; }

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

        /// <summary>Starts recording, once the ruleset exists. Subscribed AFTER the layer's own score
        /// processor so this handler sees the totals already updated for the triggering judgement.</summary>
        public void Subscribe()
        {
            if (subscribed || Layer.DrawableRuleset == null || Layer.LiveScore == null)
                return;

            subscribed = true;
            Mods = Layer.ActiveMods;
            RecordedModsOnly = Layer.UseRecordedReplayModsOnly;

            // The scoring lineage — read off the replay, NOT "has the CL mod" (lazer attaches CL to
            // every stable .osr, which would score genuine lazer plays as stable too; see
            // Replays.ScoringVersions.Detect). A STABLE play uses stable ScoreV1; every other lineage
            // uses lazer's own processor total in the matching display mode.
            var version = Layer.ReplayScoringVersion;
            ScoringMode = version is Replays.ScoringVersion.Classic or Replays.ScoringVersion.V1
                ? osu.Game.Rulesets.Scoring.ScoringMode.Classic
                : osu.Game.Rulesets.Scoring.ScoringMode.Standardised;

            var score = Layer.LiveScore;

            // A STABLE play is scored as osu!STABLE ScoreV1 (what the play scored on stable / what
            // danser shows), driven off the renderer's own per-object results — NOT lazer's
            // GetDisplayScore(Classic), a remap that does not match stable. Difficulty multiplier is on
            // the map's ORIGINAL stats, so it reads the unmodified beatmap.
            var scoreV1 = version.UsesStableScoreV1() ? new StableScoreV1(beatmap.Beatmap, Mods) : null;

            var performance = performanceFor(Layer);

            Layer.DrawableRuleset.NewResult += result =>
            {
                scoreV1?.Apply(result.HitObject, result.Type);

                int combo = score.Combo.Value;

                bool broke = lastCombo > 0 && combo == 0;
                int lost = broke ? lastCombo : 0;

                lastCombo = combo;

                long total = scoreV1 != null ? scoreV1.Score : score.GetDisplayScore(ScoringMode);

                var position = result.HitObject is osu.Game.Rulesets.Osu.Objects.OsuHitObject osuObject
                    ? osuObject.StackedPosition
                    : osuTK.Vector2.Zero;

                Timeline.Record(new TimelinePoint(
                    result.TimeAbsolute,
                    total,
                    combo,
                    score.Accuracy.Value,
                    broke,
                    score.Rank.Value.ToString(),
                    performance?.PointsFor(score) ?? 0,
                    lost,
                    result.Type,
                    position));
            };
        }
    }
}
