#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.IO;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.UI;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Skinning;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Hosts osu!lazer's REAL gameplay renderer (<see cref="DrawableRuleset"/>) for one difficulty,
/// driven by autoplay and our shared playback clock — 100% authentic osu! gameplay visuals for
/// all four rulesets, replacing the old hand-rolled chart renderers.
///
/// The heavy machinery lazer normally wires through <c>OsuGameBase</c>/<c>Player</c> is provided
/// here in its minimal standalone form, mirroring what lazer's own DrawableRuleset test scenes
/// cache: the game-level pieces (<c>RealmAccess</c>, <c>OsuConfigManager</c>,
/// <c>IRulesetConfigCache</c>, <c>OsuColour</c>, <c>IStorageResourceProvider</c>) come from
/// <see cref="JukeBoxGameBase"/>, while the per-beatmap pieces (mods list, skin chain, sample
/// gating) are cached by this layer.
/// </summary>
public partial class LazerChartLayer : CompositeDrawable, IBeatSyncProvider
{
    private readonly WorkingBeatmap working;
    private readonly string osuFile;
    private readonly Score? replayScore;

    private DrawableRuleset? drawableRuleset;

    /// <summary>
    /// The score whose replay drives gameplay — a dropped user replay when there is one for this
    /// difficulty, otherwise the ruleset's own generated autoplay. Either way it reaches
    /// <see cref="DrawableRuleset.SetReplayScore"/> the same way in <see cref="LoadComplete"/>;
    /// nothing downstream distinguishes them.
    /// </summary>
    private Score? gameplayScore;

    private IReadOnlyList<Mod> mods = Array.Empty<Mod>();
    private IBeatmap? playableBeatmap;

    /// <summary>Test hook: whether this layer is driven by a real user replay rather than autoplay.</summary>
    internal bool UsingUserReplay => replayScore != null && gameplayScore == replayScore;

    private readonly List<IDisposable> ownedSkins = new List<IDisposable>();

    /// <summary>
    /// Gates lazer's own hitsound/keysound playback (the samples DrawableHitObjects play through
    /// the skin chain). Sample playback is additionally suppressed while the frame-stable clock is
    /// catching up after a seek, exactly like lazer's own gameplay clock does — otherwise every
    /// object skipped over during the catch-up would fire its hitsound.
    /// </summary>
    public readonly BindableBool HitSoundsEnabled = new BindableBool();

    /// <summary>
    /// Whether the chart has been released from its mask
    /// (<see cref="Configuration.JukeBoxSetting.RemoveChartMask"/>), set by
    /// <see cref="Screens.BeatmapVisuals"/>. Our own clips stopping is only half of a release: the
    /// RULESET clips its own playfield too (see <see cref="releaseRulesetMasking"/>), and a child can
    /// never escape an ancestor's mask.
    /// </summary>
    public readonly BindableBool ChartReleased = new BindableBool();

    private readonly BindableBool samplePlaybackDisabled = new BindableBool(true);

    /// <summary>
    /// Test hook (JukeBox.Game.Tests has InternalsVisibleTo): the live state of the gate above —
    /// false means lazer's hit objects are actually allowed to play their samples right now. It
    /// starts shut and is only ever recomputed in <see cref="Update"/>, which makes it the honest
    /// answer to "are hitsounds really playing", including for a layer that is hidden: a hidden
    /// drawable that osu!framework considers absent never updates, so a layer whose gate is still
    /// shut is a layer that is silently doing nothing (see BeatmapVisuals.updateChartVisibility).
    /// </summary>
    internal bool SamplePlaybackDisabled => samplePlaybackDisabled.Value;

    // Big-seek snap: FrameStabilityContainer's frame-stable catch-up advances a bounded slice of
    // gameplay time per real frame, so a large scrub (diff switch mid-song, user seeking the
    // track) would fast-forward visibly for seconds (measured: a 30s jump took 63 frames ≈ 1s at
    // 60fps). Lazer's own fix for exactly this (Player.SetGameplayStartTime) disables
    // FrameStablePlayback for one frame so the container hard-seeks, then re-enables it — but
    // that property is internal to osu.Game, so we reach it via reflection; when unavailable
    // (upstream rename), we degrade gracefully back to the frame-stable crawl.
    private const double seek_snap_threshold_ms = 1000;

    private static readonly System.Reflection.PropertyInfo? frame_stable_playback_property =
        typeof(DrawableRuleset).GetProperty("FrameStablePlayback",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private double? lastClockTime;
    private osu.Framework.Threading.ScheduledDelegate? frameStableResetDelegate;

    /// <summary>Test hook: disable the big-seek snap to measure the frame-stable crawl baseline.</summary>
    internal bool SnapOnBigSeeks = true;

    /// <summary>
    /// Instrumentation (also logged): how many layer updates the last big seek took until the
    /// frame-stable clock was back within 200ms of the driving clock. -1 until a big seek happened.
    /// </summary>
    internal int LastSeekCatchupFrames { get; private set; } = -1;

    /// <summary>
    /// Test hook: how many times the big-seek snap actually engaged (i.e. the internal
    /// FrameStablePlayback hook was found via reflection AND toggled). Stays 0 when snapping is
    /// disabled or the reflection hook is unavailable — lets tests assert the MECHANISM rather
    /// than racy frame-count comparisons.
    /// </summary>
    internal int SeekSnapsEngaged { get; private set; }

    /// <summary>
    /// Test hook: where GAMEPLAY actually is, which is not where the driving clock is. The
    /// frame-stable clock runs its own catch-up, so a test that steps the driving clock and then
    /// reasons about hit objects is reasoning about the wrong number.
    /// </summary>
    internal double? FrameStableTime => drawableRuleset?.FrameStableClock.CurrentTime;

    private int seekCatchupFrames = -1;

    /// <summary>The ruleset instance rendering this difficulty (test hook; assigned during load).</summary>
    internal Ruleset? Ruleset { get; private set; }

    /// <summary>Number of hit objects in the playable (converted) beatmap, 0 until loaded (test hook).</summary>
    internal int ObjectCount => playableBeatmap?.HitObjects.Count ?? 0;

    /// <summary>The mod-converted beatmap gameplay actually runs on (test hook) — lets a test see
    /// that a replay's difficulty mods reached the conversion, not just the mods list.</summary>
    internal IBeatmap? PlayableBeatmap => playableBeatmap;

    /// <summary>The hosted lazer DrawableRuleset (test hook).</summary>
    internal DrawableRuleset? DrawableRuleset => drawableRuleset;

    private LazerSkinProvider? skinProvider;
    private BeatmapSkinGate? beatmapSkinGate;

    /// <summary>
    /// Test hook (JukeBox.Game.Tests has InternalsVisibleTo): the COMPOSED skin chain, in the order
    /// lazer will consult it — highest priority first. Read straight off the containers that were
    /// actually built (<see cref="ISkinSource.AllSources"/> walks a provider's own sources and then
    /// its parent's), so a test asserting the order is asserting the real chain rather than a
    /// second description of it that could stay right while the chain drifted.
    ///
    /// <para>
    /// Read from the beatmap-skin gate when there is one, since that is the innermost link and
    /// therefore the whole chain; from the user-skin provider otherwise. The per-element visibility
    /// filter is deliberately not the starting point — it is a lookup interceptor rather than a
    /// skin, and including it would put an implementation detail of a different feature in the
    /// middle of every assertion about skin priority.
    /// </para>
    /// </summary>
    internal IReadOnlyList<ISkin> SkinChain
        => ((ISkinSource?)beatmapSkinGate ?? skinProvider)?.AllSources.ToArray() ?? Array.Empty<ISkin>();

    /// <summary>
    /// The same chain with each entry unwrapped to the skin underneath it: every source is wrapped
    /// in the ruleset's own skin transformer (see <see cref="LazerSkinProvider"/>), and it is the
    /// skin inside that a test means when it says "Classic comes after the user's skin".
    /// </summary>
    internal IReadOnlyList<ISkin> UnwrappedSkinChain
        => SkinChain.Select(s => s is SkinTransformer transformer ? transformer.Skin : s).ToArray();

    /// <summary>Whether the osu! replay-analysis overlay got attached (test hook).</summary>
    internal bool HasAnalysisOverlay => this.ChildrenOfType<osu.Game.Rulesets.Osu.UI.ReplayAnalysisOverlay>().Any();

    private DependencyContainer dependencies = null!;

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        => dependencies = new DependencyContainer(base.CreateChildDependencies(parent));

    // IBeatSyncProvider (auto-cached to children via the interface's [Cached] attribute): kiai-
    // synced skin pieces (BeatSyncedContainer) beat off the playable beatmap's control points and
    // the same frame-stable clock the gameplay runs on. No audio amplitudes available — our track
    // playback lives outside lazer.
    ControlPointInfo? IBeatSyncProvider.ControlPoints => playableBeatmap?.ControlPointInfo;
    IClock IBeatSyncProvider.Clock => drawableRuleset?.FrameStableClock ?? Clock;
    ChannelAmplitudes IHasAmplitudes.CurrentAmplitudes => ChannelAmplitudes.Empty;

    /// <param name="working">The decoded difficulty to render. Must contain at least one hit object.</param>
    /// <param name="osuFile">Absolute path of the .osu file (locates the beatmap folder for
    /// beatmap-provided skin elements and hitsound samples).</param>
    /// <param name="replayScore">A decoded user replay to play back instead of autoplay (see
    /// <see cref="Replays.ReplayStore"/>). Null — the normal case — keeps the autoplay behaviour
    /// unchanged.</param>
    public LazerChartLayer(WorkingBeatmap working, string osuFile, Score? replayScore = null)
    {
        this.working = working;
        this.osuFile = osuFile;
        this.replayScore = replayScore;

        RelativeSizeAxes = Axes.Both;
    }

    [Resolved(canBeNull: true)]
    private SkinSelection? skinSelection { get; set; }

    [Resolved(canBeNull: true)]
    private osu.Game.Configuration.OsuConfigManager? lazerConfig { get; set; }

    [Resolved(canBeNull: true)]
    private ChartModSelection? chartMods { get; set; }

    [Resolved(canBeNull: true)]
    private PlayfieldElementVisibility? elementVisibility { get; set; }

    [Resolved(canBeNull: true)]
    private ChartConversion? conversion { get; set; }

    /// <summary>The playfield-element filter wrapping the ruleset, when one is in the chain (test
    /// hook; null in bare test scenes with no visibility service cached).</summary>
    internal PlayfieldElementFilter? ElementFilter { get; private set; }

    [BackgroundDependencyLoader]
    private void load(GameHost host, AudioManager audio, IStorageResourceProvider resourceProvider)
    {
        // The ruleset the chart is BUILT with, which is the beatmap's own unless the user has asked
        // for a conversion and the target ruleset accepts this beatmap (see ChartConversion). A
        // replay is never converted: its frames belong to the ruleset it was played on.
        var ruleset = conversion?.EffectiveRulesetFor(working, allowConversion: replayScore == null)
                      ?? CreateRuleset(working.BeatmapInfo.Ruleset.OnlineID);

        Ruleset = ruleset;

        if (ruleset.RulesetInfo.OnlineID != working.BeatmapInfo.Ruleset.OnlineID)
            osu.Framework.Logging.Logger.Log($"[LazerChartLayer] converting this beatmap to {ruleset.ShortName}");

        ModAutoplay? autoplay = null;

        if (replayScore != null)
            mods = replayMods(ruleset);
        else
        {
            autoplay = ruleset.GetAutoplayMod()
                       ?? throw new InvalidOperationException($"{ruleset.ShortName} provides no autoplay mod");

            // The user's Chart-tab mods ride ALONGSIDE autoplay — the mod list is what
            // GetPlayableBeatmap converts against, so EZ/HR's altered difficulty and HR's mirrored
            // playfield are baked into what autoplay then plays. Fresh instances every build (see
            // ChartModSelection.CreateFor); a replay's own mods win outright, since a replay is a
            // record of a play that already happened under mods of its own.
            var selection = chartMods?.CreateFor(ruleset) ?? Array.Empty<Mod>();

            mods = selection.Count > 0
                ? new Mod[] { autoplay }.Concat(selection).ToArray()
                : new Mod[] { autoplay };

            if (selection.Count > 0)
                osu.Framework.Logging.Logger.Log($"[LazerChartLayer] chart mods applied: {string.Join(", ", selection.Select(m => m.Acronym))}");
        }

        playableBeatmap = working.GetPlayableBeatmap(ruleset.RulesetInfo, mods);

        // Per-beatmap dependencies for the DrawableRuleset subtree, mirroring lazer's Player:
        // the active mods list, and the sample-playback gate (lazer's GameplayClockContainer
        // normally provides the latter — ours additionally implements the hitsound setting).
        dependencies.CacheAs(mods);
        dependencies.CacheAs<ISamplePlaybackDisabler>(new SamplePlaybackGate(samplePlaybackDisabled));

        drawableRuleset = ruleset.CreateDrawableRulesetWith(playableBeatmap, mods);

        if (replayScore != null)
            gameplayScore = replayScore;
        else
        {
            var replay = autoplay!.CreateReplayData(playableBeatmap, mods);
            gameplayScore = new Score { Replay = replay.Replay };
        }

        // Skin chain in lazer's lookup order: beatmap-folder skin (beatmap-provided elements,
        // combo colours from the .osu [Colours] section and custom hitsound samples, gated live by
        // the Beatmap skins/colours/hitsounds settings), then the user-selected bundled skin, then
        // the classic legacy skin as the final legacy-name fallback — the same stack
        // RulesetSkinProvidingContainer + BeatmapSkinProvidingContainer build, minus the
        // realm-backed SkinManager they resolve (we have no realm-imported skins to offer anyway).
        ISkin? beatmapSkin = null;

        try
        {
            var folderSkin = new BeatmapFolderSkin(osuFile, resourceProvider, host);
            ownedSkins.Add(folderSkin);
            beatmapSkin = folderSkin;
        }
        catch (Exception e)
        {
            osu.Framework.Logging.Logger.Error(e, $"Failed to load beatmap folder skin for '{osuFile}' — continuing with default skin only");
        }

        // The user's bundled-skin choice (settings panel; Random is already resolved to a concrete
        // entry by SkinSelection). Selection changes rebuild this whole layer (BeatmapVisuals),
        // so the choice is read once here. Argon when no service is cached (bare test scenes).
        var selectedChoice = skinSelection?.Effective.Value ?? JukeBoxSkin.Argon;
        SelectedSkin = selectedChoice;
        osu.Framework.Logging.Logger.Log($"[LazerChartLayer] building {ruleset.ShortName} chart with skin: {selectedChoice}");

        // Routed through the service (not the static) so JukeBoxSkin.Custom can resolve the
        // user-imported .osk folder, which needs storage access this layer has no business doing.
        // With no service cached (bare test scenes) the choice is always Argon anyway.
        var selected = skinSelection?.CreateEffectiveSkin(resourceProvider)
                       ?? SkinSelection.CreateSkin(selectedChoice, resourceProvider);
        var rulesetResources = new ResourceStoreBackedSkin(ruleset.CreateResourceStore(), host, audio);
        ownedSkins.Add(selected);
        ownedSkins.Add(rulesetResources);

        // Classic stays the final legacy-name fallback even under non-legacy user skins, exactly
        // as before — unless classic IS the selection (no point stacking it twice).
        DefaultLegacySkin? classic = null;
        var userSkins = new List<ISkin> { selected };

        if (selectedChoice != JukeBoxSkin.Classic)
        {
            classic = new DefaultLegacySkin(resourceProvider);
            ownedSkins.Add(classic);
            userSkins.Add(classic);
        }

        skinProvider = new LazerSkinProvider(ruleset, playableBeatmap, userSkins, rulesetResources)
        {
            RelativeSizeAxes = Axes.Both,
        };

        // The per-element visibility filter is the INNERMOST link of the chain — nearest ancestor
        // wins in lazer's skin lookup, so this is the only position from which it can answer a
        // lookup before the real skins do. See PlayfieldElementFilter for why hiding happens here
        // rather than by walking the ruleset's (pooled) drawables.
        Drawable rulesetHost = drawableRuleset;

        if (elementVisibility != null)
        {
            rulesetHost = ElementFilter = new PlayfieldElementFilter(elementVisibility)
            {
                Child = drawableRuleset,
            };
        }

        // The beatmap skin gets its own providing layer nested INSIDE the user-skin provider
        // (highest lookup priority, parent fallback reaching the user skins) so the three
        // "Beatmap ..." settings gate it live — mirroring lazer's BeatmapSkinProvidingContainer,
        // which we can't use directly: its loader hard-resolves the realm-backed SkinManager.
        if (beatmapSkin != null)
        {
            var beatmapSkinTransformed = ruleset.CreateSkinTransformer(beatmapSkin, playableBeatmap) ?? beatmapSkin;
            var gateSources = new List<ISkin> { beatmapSkinTransformed };

            // Mirrors BeatmapSkinProvidingContainer's own same-priority safety net: a beatmap
            // that provides only a PARTIAL selection of legacy skin elements (e.g. just a
            // taiko lane background/banner, a common "decorative flair only" authoring
            // pattern) expects everything it doesn't cover to fall back to a consistent
            // classic/legacy look — not to whatever non-legacy skin the user happens to have
            // selected. Without this, a partially-legacy beatmap skin under e.g. Argon renders
            // a jarring mix: the beatmap's own legacy backdrop pieces alongside Argon's
            // completely different note/drum/target style for whatever it didn't cover (see
            // TaikoArgonSkinTransformer — it unconditionally overrides every taiko component,
            // unlike Triangles/ArgonPro's lighter touch). Only kicks in when the beatmap is
            // actually offering legacy resources and the user's own selection isn't already
            // legacy (nothing to add in that case — same condition BeatmapSkinProvidingContainer
            // itself checks).
            if (classic != null && selected is not LegacySkin
                                 && beatmapSkinTransformed is LegacySkinTransformer { IsProvidingLegacyResources: true })
            {
                gateSources.Add(ruleset.CreateSkinTransformer(classic, playableBeatmap) ?? classic);
            }

            var gated = beatmapSkinGate = new BeatmapSkinGate(gateSources)
            {
                RelativeSizeAxes = Axes.Both,
                Child = rulesetHost,
            };

            if (lazerConfig != null)
            {
                gated.BeatmapSkins.Current = lazerConfig.GetBindable<bool>(osu.Game.Configuration.OsuSetting.BeatmapSkins);
                gated.BeatmapColours.Current = lazerConfig.GetBindable<bool>(osu.Game.Configuration.OsuSetting.BeatmapColours);
                gated.BeatmapHitsounds.Current = lazerConfig.GetBindable<bool>(osu.Game.Configuration.OsuSetting.BeatmapHitsounds);
            }

            skinProvider.Child = gated;
        }
        else
            skinProvider.Child = rulesetHost;

        InternalChild = skinProvider;
    }

    /// <summary>
    /// The replay's own mods, in full — the difficulty-affecting ones that change what is drawn
    /// (HR's flipped playfield, EZ/HR's altered approach and size, mania key mods), the visual ones
    /// (HD's fades, FL's flashlight), AND the rate-changing ones, which reach the ruleset for
    /// completeness even though they change nothing here: lazer applies those to the TRACK, which
    /// in this app is <see cref="Playback.PlaybackController.ReplayTempo"/> and
    /// <see cref="Playback.PlaybackController.ReplayFrequency"/>'s job. Only autoplay is dropped,
    /// since it would fight the replay for the input handler.
    ///
    /// <para>
    /// Applying the real mod list is what makes the replay's cursor line up with the objects it was
    /// aiming at. A mod list that fails to materialise (an unrecognised or unconvertible mod)
    /// degrades to no mods rather than failing the whole chart.
    /// </para>
    /// </summary>
    private IReadOnlyList<Mod> replayMods(Ruleset ruleset)
    {
        try
        {
            var recorded = Replays.ReplayMods.ForGameplay(replayScore);

            // Only once the selection has actually taken this replay on (ChartModSelection follows
            // the now-playing item, and a bare test scene has no selection service at all) is it the
            // thing to ask. Until then the recorded set is the only honest answer — and it is also
            // exactly what the selection is about to be seeded with, so the two agree.
            if (chartMods?.ReplayActive.Value != true)
                return recorded;

            // The Chart tab's selection is seeded from these very mods the moment the replay starts
            // (see ChartModSelection.applyReplayState), so with nothing edited this IS the recorded
            // set — and once the user turns a row off, it is what they asked for instead.
            var selected = chartMods.CreateFor(ruleset);

            // Recorded mods the tab has no row for stay exactly as recorded: there is no toggle the
            // user could have turned them off with, so dropping them would be this code editing the
            // play rather than the user doing it.
            var unmodelled = recorded.Where(m => !ChartModCatalog.Models(m.Acronym));

            var kept = ChartModCatalog.Compatible(selected.Concat(unmodelled), ruleset);

            if (kept.Count > 0 || recorded.Count > 0)
            {
                osu.Framework.Logging.Logger.Log(
                    $"[LazerChartLayer] replay mods: {string.Join(", ", kept.Select(m => m.Acronym))}"
                    + $" (recorded {string.Join(", ", recorded.Select(m => m.Acronym))};"
                    + $" playback rate {Replays.ReplayMods.RateFor(recorded):0.##}× stays as recorded)");
            }

            return kept;
        }
        catch (Exception e)
        {
            osu.Framework.Logging.Logger.Error(e, $"Failed to resolve replay mods for '{ruleset.ShortName}' — rendering the replay unmodded");
            return Array.Empty<Mod>();
        }
    }

    /// <summary>The concrete bundled skin this layer was built with (test hook).</summary>
    internal JukeBoxSkin SelectedSkin { get; private set; }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Same timing as lazer's ReplayPlayer.PrepareReplay: the ruleset's input manager and
        // frame-stability container exist once loaded, so the replay can be attached now. From
        // here the replay (the user's, or the generated autoplay one) drives gameplay entirely off
        // our inherited playback clock.
        // What was ACTUALLY built is the last word on which ruleset is on screen — BeatmapVisuals
        // publishes the same answer for a difficulty with no chart layer at all (hitsounds only, or
        // rendering switched off), but when there is one, this is the thing that decided.
        conversion?.Publish(working, allowConversion: replayScore == null);

        if (gameplayScore != null)
            drawableRuleset?.SetReplayScore(gameplayScore);

        startLiveScoring();
        attachOsuReplayAnalysis();
    }

    /// <summary>
    /// Live score, accuracy and combo for this layer's play — null unless
    /// <see cref="TrackLiveScore"/> asked for it. The bindables move as the replay is judged, so a
    /// display bound to them counts up the way a spectate client does.
    /// </summary>
    internal ScoreProcessor? LiveScore { get; private set; }

    /// <summary>
    /// Whether to run a <see cref="ScoreProcessor"/> alongside the play. Off by default: the single
    /// chart shows no numbers, so judging for it would be bookkeeping nobody reads. Set before load.
    /// </summary>
    internal bool TrackLiveScore { get; init; }

    /// <summary>
    /// Feeds this layer's judgements into a score processor, which is the only way to get numbers
    /// that MOVE: the .osr header carries the final totals alone, so anything drawn from it is
    /// correct exactly once — at the end.
    ///
    /// <para>
    /// This is lazer's own wiring minus the Player screen: a processor applied to the playable
    /// beatmap, then fed the ruleset's results. RevertResult matters as much as NewResult — it is
    /// what unwinds the numbers when the frame-stable clock runs backwards, which is what a seek
    /// backwards is.
    /// </para>
    /// </summary>
    private void startLiveScoring()
    {
        if (!TrackLiveScore || drawableRuleset == null || Ruleset == null || playableBeatmap == null)
            return;

        var processor = Ruleset.CreateScoreProcessor();

        processor.Mods.Value = mods;
        processor.ApplyBeatmap(playableBeatmap);

        drawableRuleset.NewResult += processor.ApplyResult;
        drawableRuleset.RevertResult += processor.RevertResult;

        LiveScore = processor;
    }

    // NOTE: a previous fix here removed LegacyHalfDrum's own Masking, on the theory that some
    // legacy skins intentionally ship an oversized drum-flash texture meant to bloom outward past
    // the drum. That was wrong: LegacyHalfDrum's masking isn't cropping an oversized bloom down —
    // each "half" is designed to show only ITS OWN semicircle of a combined flash effect (their
    // Rim/Centre sprites are separately Origin/Scale-flipped per side for exactly this), so
    // removing the masking exposed unclipped, wrongly-scaled sprite geometry instead of fixing
    // anything (confirmed against a real screenshot: misshapen, offset crescents spilling past the
    // drum). Reverted; see fix/taiko-flash for the real investigation.

    // Live binding for the "Hide gameplay cursor" replay setting (field: config bindables are
    // weak-referenced back to the master).
    private Bindable<bool>? cursorHideEnabled;

    [Resolved(canBeNull: true)]
    private IRulesetConfigCache rulesetConfigs { get; set; } = null!;

    /// <summary>
    /// osu!-only replay-analysis overlays (click markers / frame markers / cursor path / display
    /// length) plus the hide-cursor toggle — the exact wiring lazer's DrawableOsuRuleset performs
    /// when a ReplayPlayer is present in DI. That gate never opens here (ReplayPlayer is the full
    /// Player screen; we host the bare DrawableRuleset), but ReplayAnalysisOverlay itself only
    /// resolves OsuRulesetConfigManager — provided inside the DrawableRuleset subtree — so
    /// attaching it from outside reproduces lazer's behaviour 1:1, driven by our autoplay replay.
    /// </summary>
    /// <summary>
    /// Adds ANOTHER person's cursor over this same chart, tinted — the mechanism behind combine
    /// mode, where one rendered beatmap carries every replay's cursor at once.
    ///
    /// <para>
    /// It reuses the very attachment lazer's own analysis overlay uses, and that is the whole point:
    /// the overlay lives inside <c>PlayfieldAdjustmentContainer</c>, so it inherits the playfield's
    /// transform and lands in exactly the right place. Deriving that transform by hand to position
    /// cursors ourselves would be the expensive kind of wrong — right until the map has a different
    /// aspect, or the zoom setting moves.
    /// </para>
    ///
    /// <para>
    /// osu! only, unavoidably: the other three rulesets have no cursor to draw. Returns null when
    /// there is nothing to attach to, so callers can say so rather than silently showing one cursor.
    /// </para>
    /// </summary>
    /// <param name="replay">The frames to draw a cursor from.</param>
    /// <param name="tint">Colour for this player's cursor and trail.</param>
    /// <returns>The overlay, so the caller can keep changing it — knockout scales the survivors'
    /// cursors up as the field thins and fades out the eliminated, which needs a handle on the
    /// thing it drew rather than just the news that it exists.</returns>
    internal osu.Game.Rulesets.Osu.UI.ReplayAnalysisOverlay? AddCursorOverlay(osu.Game.Replays.Replay replay, osuTK.Graphics.Color4 tint)
    {
        if (drawableRuleset is not osu.Game.Rulesets.Osu.UI.DrawableOsuRuleset osuRuleset)
            return null;

        var overlay = new osu.Game.Rulesets.Osu.UI.ReplayAnalysisOverlay(replay) { Colour = tint };

        osuRuleset.PlayfieldAdjustmentContainer.Add(overlay);
        osuRuleset.Overlays.Add(overlay.CreateProxy().With(p => p.Depth = float.NegativeInfinity));

        return overlay;
    }

    private void attachOsuReplayAnalysis()
    {
        if (drawableRuleset is not osu.Game.Rulesets.Osu.UI.DrawableOsuRuleset osuRuleset || gameplayScore?.Replay == null)
            return;

        var analysisOverlay = new osu.Game.Rulesets.Osu.UI.ReplayAnalysisOverlay(gameplayScore.Replay);
        osuRuleset.PlayfieldAdjustmentContainer.Add(analysisOverlay);
        osuRuleset.Overlays.Add(analysisOverlay.CreateProxy().With(p => p.Depth = float.NegativeInfinity));

        if (rulesetConfigs?.GetConfigFor(Ruleset!) is osu.Game.Rulesets.Osu.Configuration.OsuRulesetConfigManager osuConfig)
        {
            cursorHideEnabled = osuConfig.GetBindable<bool>(osu.Game.Rulesets.Osu.Configuration.OsuRulesetSetting.ReplayCursorHideEnabled);
            cursorHideEnabled.BindValueChanged(e => osuRuleset.Playfield.Cursor?.FadeTo(e.NewValue ? 0 : 1), true);
        }
    }

    protected override void Update()
    {
        base.Update();

        double current = Clock.CurrentTime;

        if (drawableRuleset != null)
        {
            if (lastClockTime != null)
            {
                // Live seek: the driving clock jumped while we were attached.
                if (Math.Abs(current - lastClockTime.Value) > seek_snap_threshold_ms)
                {
                    if (SnapOnBigSeeks)
                        snapThroughSeek(current - lastClockTime.Value);

                    seekCatchupFrames = 0;
                }
            }
            else
            {
                // Construction catch-up: a FRESHLY ATTACHED layer (RenderChart toggled on
                // mid-song, difficulty/skin-change rebuilds) starts its FrameStabilityContainer
                // (and the autoplay replay walk) at the song start and would visibly fast-forward
                // to the current position. Same crawl, same cure: if the driving clock is already
                // meaningfully ahead of the freshly-born frame-stable clock on our first update,
                // engage the snap for the initial catch-up too.
                double gap = current - drawableRuleset.FrameStableClock.CurrentTime;

                if (Math.Abs(gap) > seek_snap_threshold_ms)
                {
                    if (SnapOnBigSeeks)
                        snapThroughSeek(gap);

                    seekCatchupFrames = 0;
                }
            }
        }

        lastClockTime = current;

        // Seek catch-up instrumentation: count layer updates until the frame-stable clock is back
        // within sync of the driving clock after a big jump.
        if (seekCatchupFrames >= 0 && drawableRuleset != null)
        {
            seekCatchupFrames++;

            if (Math.Abs(drawableRuleset.FrameStableClock.CurrentTime - current) <= 200)
            {
                LastSeekCatchupFrames = seekCatchupFrames;
                osu.Framework.Logging.Logger.Log($"[LazerChartLayer] seek caught up in {seekCatchupFrames} frame(s)");
                seekCatchupFrames = -1;
            }
        }

        samplePlaybackDisabled.Value =
            !HitSoundsEnabled.Value || drawableRuleset?.FrameStableClock.IsCatchingUp.Value == true;

        releaseRulesetMasking();
    }

    /// <summary>
    /// osu!framework's <c>Masking</c> setter, reached the same way (and for the same reason)
    /// LazerStoryboardLayer reaches it: the containers lazer clips its playfield with are internal
    /// to osu.Game and expose no public setter. If upstream renames the property we simply leave
    /// lazer's own masking alone — the pre-release behaviour, not a crash.
    /// </summary>
    private static readonly System.Reflection.PropertyInfo? masking_property =
        typeof(CompositeDrawable).GetProperty("Masking",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    // Same lookup for the two things that are only legal WHILE masking, so a container carrying
    // either can be left alone instead of crashed (see canRelease).
    private static readonly System.Reflection.PropertyInfo? border_thickness_property =
        typeof(CompositeDrawable).GetProperty("BorderThickness",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    private static readonly System.Reflection.PropertyInfo? edge_effect_property =
        typeof(CompositeDrawable).GetProperty("EdgeEffect",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    /// <summary>Everything we have switched masking off on, to switch back on when the release ends.
    /// Each entry was masking when we found it, so restoring is putting back what lazer built.</summary>
    private readonly List<CompositeDrawable> releasedMasks = new List<CompositeDrawable>();

    /// <summary>Test hook: how many of lazer's own masks the release is currently holding open.</summary>
    internal int ReleasedMaskCount => releasedMasks.Count;

    /// <summary>
    /// Releases (or restores) every mask lazer puts between the playfield's contents and us.
    ///
    /// <para>
    /// Our own clips stopping is only half the job: the rulesets clip their playfields themselves —
    /// osu!catch is the visible case, whose PlayfieldAdjustmentContainer holds a "visible area"
    /// container that cut fruits in half along a horizontal line above the frame even with the
    /// chart mask released — and a child can never escape an ancestor's mask. Walking up from each
    /// playfield's hit object container finds exactly the containers that form that frame, whatever
    /// the ruleset builds (mania's per-column stages included), without naming a lazer-internal
    /// type.
    /// </para>
    ///
    /// <para>
    /// Re-scanned every frame rather than cached: playfields (and, in mania, columns) appear a frame
    /// or more after the ruleset does, and a set collected too early would leave the mask that was
    /// not built yet clipping forever. A released container drops out of the scan by construction —
    /// it is no longer masking — so the walk stays a handful of pointer hops per frame.
    /// </para>
    /// </summary>
    private void releaseRulesetMasking()
    {
        if (masking_property == null || drawableRuleset == null)
            return;

        if (!ChartReleased.Value)
        {
            foreach (var target in releasedMasks)
                masking_property.SetValue(target, true);

            releasedMasks.Clear();
            return;
        }

        foreach (var playfield in playfields(drawableRuleset.Playfield))
        {
            for (CompositeDrawable? p = playfield.HitObjectContainer; p != null && !ReferenceEquals(p, this); p = p.Parent)
            {
                if (!p.Masking || !canRelease(p))
                    continue;

                masking_property.SetValue(p, false);
                releasedMasks.Add(p);
            }
        }
    }

    /// <summary>
    /// Whether switching this container's mask off is safe: a border or an edge effect is only legal
    /// WHILE masking (osu!framework refuses the combination outright, at draw time), so a container
    /// carrying either keeps its mask instead of taking the whole scene down with it.
    /// </summary>
    private static bool canRelease(CompositeDrawable target)
    {
        if (border_thickness_property?.GetValue(target) is float thickness && thickness != 0)
            return false;

        if (edge_effect_property?.GetValue(target) is EdgeEffectParameters edge && edge.Type != EdgeEffectType.None)
            return false;

        return true;
    }

    /// <summary>A playfield and every playfield nested inside it (mania's columns, taiko's swells).</summary>
    private static IEnumerable<Playfield> playfields(Playfield playfield)
    {
        yield return playfield;

        foreach (var nested in playfield.NestedPlayfields)
        {
            foreach (var inner in playfields(nested))
                yield return inner;
        }
    }

    /// <summary>
    /// Mirrors lazer's Player.SetGameplayStartTime: disable frame-stable playback so the
    /// FrameStabilityContainer hard-seeks this frame (non-frame-stable — intermediate judgements
    /// may not apply/revert perfectly, same trade-off lazer accepts for seeks), then restore it
    /// one frame later.
    /// </summary>
    private void snapThroughSeek(double jump)
    {
        if (frame_stable_playback_property == null)
            return;

        // A snap may already be in flight from a previous frame's jump — complete it first,
        // exactly as Player does with its pending reset delegate.
        if (frameStableResetDelegate?.Cancelled == false && !frameStableResetDelegate.Completed)
            frameStableResetDelegate.RunTask();

        frame_stable_playback_property.SetValue(drawableRuleset, false);
        frameStableResetDelegate = ScheduleAfterChildren(() => frame_stable_playback_property.SetValue(drawableRuleset, true));

        SeekSnapsEngaged++;
        osu.Framework.Logging.Logger.Log($"[LazerChartLayer] seek snap: {jump:+0;-0}ms clock jump, bypassing frame-stable catch-up for one frame");
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);

        foreach (var skin in ownedSkins)
            skin.Dispose();
        ownedSkins.Clear();
    }

    /// <summary>The ruleset for an online mode id (0 osu! / 1 taiko / 2 catch / 3 mania), unknown
    /// ids falling back to osu!. Shared with the replay decoder, which needs the same mapping to
    /// convert legacy replay frames and mods.</summary>
    internal static Ruleset CreateRuleset(int onlineId) => onlineId switch
    {
        1 => new TaikoRuleset(),
        2 => new CatchRuleset(),
        3 => new ManiaRuleset(),
        _ => new OsuRuleset(),
    };

    /// <summary>
    /// Trivial <see cref="ISamplePlaybackDisabler"/> handing lazer's PausableSkinnableSound a
    /// bindable we control from <see cref="Update"/>.
    /// </summary>
    private class SamplePlaybackGate : ISamplePlaybackDisabler
    {
        private readonly IBindable<bool> disabled;

        public SamplePlaybackGate(IBindable<bool> disabled)
        {
            this.disabled = disabled;
        }

        public IBindable<bool> SamplePlaybackDisabled => disabled;
    }

    /// <summary>
    /// A standalone stand-in for lazer's <c>RulesetSkinProvidingContainer</c>: every skin source
    /// wrapped in the ruleset's own skin transformer (legacy/argon per-ruleset adaptations), plus
    /// the ruleset's bundled resources as the final source — minus the realm-backed SkinManager
    /// lookup that class requires.
    /// </summary>
    private partial class LazerSkinProvider : SkinProvidingContainer
    {
        // Matches RulesetSkinProvidingContainer: sources are complete here; never consult parents.
        protected override bool AllowFallingBackToParent => false;

        public LazerSkinProvider(Ruleset ruleset, IBeatmap beatmap, IEnumerable<ISkin> userSkins, ISkin rulesetResources)
        {
            var sources = new List<ISkin>();

            sources.AddRange(userSkins.Select(s => transform(ruleset, beatmap, s)));
            sources.Add(rulesetResources);

            SetSources(sources);
        }

        private static ISkin transform(Ruleset ruleset, IBeatmap beatmap, ISkin skin)
            => ruleset.CreateSkinTransformer(skin, beatmap) ?? skin;
    }

    /// <summary>
    /// The beatmap-folder skin's providing layer, replicating the lookup gating of lazer's
    /// <c>BeatmapSkinProvidingContainer</c> (same overrides, same storyboard-sample exemption,
    /// same same-priority classic-fallback-for-partial-legacy-skins behaviour — see its call
    /// site) — that class itself is unusable here because its loader hard-resolves the
    /// realm-backed <c>SkinManager</c>. Falls back to the parent <see cref="LazerSkinProvider"/>
    /// (user skins) for anything none of its own sources provide or the settings disallow. The
    /// three bindables gate live: flipping a "Beatmap ..." setting triggers a source-change
    /// re-lookup, no rebuild.
    /// </summary>
    private partial class BeatmapSkinGate : SkinProvidingContainer
    {
        public readonly BindableWithCurrent<bool> BeatmapSkins = new BindableWithCurrent<bool>(true);
        public readonly BindableWithCurrent<bool> BeatmapColours = new BindableWithCurrent<bool>(true);
        public readonly BindableWithCurrent<bool> BeatmapHitsounds = new BindableWithCurrent<bool>(true);

        protected override bool AllowConfigurationLookup => BeatmapSkins.Value;
        protected override bool AllowColourLookup => BeatmapColours.Value;
        protected override bool AllowDrawableLookup(osu.Game.Skinning.ISkinComponentLookup lookup) => BeatmapSkins.Value;
        protected override bool AllowTextureLookup(string componentName) => BeatmapSkins.Value;

        protected override bool AllowSampleLookup(osu.Game.Audio.ISampleInfo sampleInfo)
            => sampleInfo is osu.Game.Storyboards.StoryboardSampleInfo || BeatmapHitsounds.Value;

        /// <param name="sources">The beatmap skin first, optionally followed by a same-priority
        /// classic fallback (see the call site) — tried in order, first match wins, exactly like
        /// lazer's own <c>BeatmapSkinProvidingContainer</c>.</param>
        public BeatmapSkinGate(IEnumerable<ISkin> sources)
        {
            SetSources(sources);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            BeatmapSkins.BindValueChanged(_ => TriggerSourceChanged());
            BeatmapColours.BindValueChanged(_ => TriggerSourceChanged());
            BeatmapHitsounds.BindValueChanged(_ => TriggerSourceChanged());
        }
    }
}
