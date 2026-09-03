#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.Screens;

/// <summary>
/// The full visual stack for one beatmap set — dimmed background, osu!lazer's real storyboard
/// renderer (which itself carries storyboard video and Sample audio events), a configurable
/// background-dim scrim, and (optionally) lazer's real gameplay chart and hitsounds of the
/// selected difficulty — all driven off a single shared <see cref="IFrameBasedClock"/> so they
/// stay in lockstep with music playback (pause/seek/rate changes included).
/// </summary>
public partial class BeatmapVisuals : CompositeDrawable
{
    private readonly CachedBeatmapSet set;

    /// <summary>Local latch so <see cref="reportUnplayableVideo"/> costs one bool per frame once it
    /// has fired; the cross-rebuild memory lives in <see cref="VideoNotifier"/>.</summary>
    private bool reportedUnplayableVideo;
    private readonly string? osuFile;
    private readonly IFrameBasedClock playbackClock;
    private FramedOffsetClock offsetClock = null!;

    private TextureStore? backgroundTextures;
    private Sprite? backgroundSprite;
    private BufferedContainer? backgroundBlurContainer;
    private LazerStoryboardLayer storyboardLayer = null!;
    private AudioContainer storyboardAudio = null!;

    private Box dimScrim = null!;
    private Container chartContainer = null!;
    private LazerChartLayer? chartLayer;

    /// <summary>
    /// The side-by-side grid, built instead of <see cref="chartLayer"/> when this difficulty has
    /// SEVERAL replays and the user prefers the grid. At most one of the three layer fields is ever
    /// non-null.
    /// </summary>
    private MultiReplayGrid? multiGrid;

    /// <summary>Everyone's cursor over one chart — the other multi-replay shape.</summary>
    private MultiReplayCombine? multiCombine;

    private readonly Bindable<MultiReplayMode> multiReplayMode = new Bindable<MultiReplayMode>();

    private readonly Bindable<KnockoutMode> knockoutMode = new Bindable<KnockoutMode>();
    private readonly Bindable<KnockoutSort> knockoutSortBy = new Bindable<KnockoutSort>();
    private readonly BindableBool knockoutLiveSort = new BindableBool();

    // One clip per layer, each sized to MainScreen's player box (see updateLayerClips) and each
    // masking only while that box has stopped doing it itself. They exist for the two
    // "Remove ... mask" settings: a child can never escape an ancestor's mask, so releasing the
    // storyboard (or the chart) past the box's edges means the BOX stops clipping — and then
    // everything else here still has to be clipped exactly where it was, which is what these do.
    // With neither setting on they are inert (Masking false), leaving the box's own single mask in
    // charge exactly as before.
    private Container backdropClip = null!;
    private Container storyboardClip = null!;
    private Container dimClip = null!;
    private Container chartClip = null!;

    // The canvas-sized inner container of backdropClip. The clip itself is box-sized, so the black
    // bed and the background have to be sized to this drawable's own design canvas explicitly
    // rather than relatively — otherwise the background would stop being scaled by PlayfieldZoom
    // along with the rest of the scene.
    private Container backdropCanvas = null!;

    // Carries the app Volume setting down to every lazer-rendered audio component (storyboard
    // Sample events / keysounds in storyboardLayer, chart hitsounds in chartLayer) — those are
    // DrawableAudioWrapper-based (lazer's PausableSkinnableSound chain) and walk up the Drawable
    // parent chain for the nearest IAggregateAudioAdjustment, which this container provides.
    private AudioContainer audioAdjustments = null!;

    [Resolved]
    private PlaybackController playbackController { get; set; } = null!;

    // The decoded difficulty backing the lazer gameplay layer, or null when charting is
    // unavailable for this difficulty (no diff / unknown mode / zero objects / parse failure).
    private osu.Game.Beatmaps.WorkingBeatmap? chartWorking;

    // The scanned ruleset mode (0 std / 1 taiko / 2 catch / 3 mania, -1 unknown/unscanned) — kept
    // around (rather than a load()-local) so Update() can special-case catch's own scale. See
    // catch_reserved_height's remarks.
    private int chartMode = -1;

    // The height, in game units, of the coordinate space lazer hands a ruleset: osu!'s own
    // ScalingContainerTargetDrawSize is 1024×768, and a widescreen window is simply WIDER than
    // 1024 at the same 768 height. Only catch cares, and it cares completely:
    // CatchPlayfieldAdjustmentContainer places everything with ABSOLUTE constants against that
    // space — its "Visible area" is 968 units tall offset 100 down, and the playable area inside
    // it is 614.4 units tall at y=115.2 (768 × 0.8, positioned stable's "three fourths of the
    // difference" way) — so the catcher's height on screen is decided by how many of these units
    // the container it lives in is tall, not by any fraction of it. Handing catch our own 480-unit
    // design canvas would therefore place the catcher roughly a third of a screen BELOW the scene;
    // handing it 768 puts every piece exactly where the real game puts it (measured: playable area
    // spanning 15%–95% of the box, catcher plate at 85%–87%, fruits spawning ~6% above the top).
    //
    // A previous tuning divided by 680 instead, to make catch bigger — which pushed the catcher's
    // plate down to ~89%–92% and its surrounding sprite (a legacy skin's catcher is a whole
    // character, not Argon's thin plate) past the bottom edge entirely. That is the "y offset" this
    // replaced: the user asked for the default game placement back.
    private const float catch_game_height = 768f;

    // Config-bound (when a config manager is present — test scenes without one keep the
    // defaults). Fields, not locals: config bindables use weak references back to the master.
    private readonly Bindable<bool> renderChart = new();
    private readonly Bindable<bool> playHitSounds = new();
    private readonly BindableDouble backgroundDim = new();
    private readonly BindableDouble backgroundBlur = new();
    private readonly Bindable<bool> showStoryboard = new(true);
    private readonly Bindable<bool> showVideo = new(true);
    private readonly BindableDouble playfieldZoom = new(1.0);
    private readonly BindableDouble chartOpacity = new(1.0);
    private readonly Bindable<bool> removeChartMask = new();
    private readonly Bindable<bool> removeStoryboardMask = new();
    private readonly IBindable<JukeBoxSkin> effectiveSkin = new Bindable<JukeBoxSkin>();

    // Same rebuild trigger as effectiveSkin, for a skin change the enum value can't express: a
    // freshly-imported .osk replacing the custom skin while Custom is already selected.
    private readonly IBindable<int> skinRevision = new Bindable<int>();

    // And the same trigger again for the Chart tab's mod selection. Mods can't be applied to a
    // live DrawableRuleset: they change the beatmap CONVERSION (EZ/HR's difficulty, HR's mirrored
    // playfield, mania's key mods) and the generated autoplay replay that is walked over it, both
    // of which are built once per layer. Per-element visibility, by contrast, applies live — see
    // PlayfieldElementFilter.
    private readonly IBindable<int> chartModRevision = new Bindable<int>();

    // And once more for the "Convert to" choice, which changes which RULESET the beatmap is built
    // for — the same reason mods can't be applied to a live DrawableRuleset applies to this.
    private readonly IBindable<int> chartConversionRevision = new Bindable<int>();

    // Lazer's own background-blur scale: setting 0..1 maps to a gaussian sigma of 0..25.
    private const float max_blur_sigma = 25;

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    [Resolved(canBeNull: true)]
    private SkinSelection? skinSelection { get; set; }

    [Resolved(canBeNull: true)]
    private ChartModSelection? chartMods { get; set; }

    [Resolved(canBeNull: true)]
    private ChartConversion? chartConversion { get; set; }

    [Resolved(canBeNull: true)]
    private BeatmapOffsetStore? offsetStore { get; set; }

    // canBeNull like the rest: a bare test scene has no replay registry, which simply means no
    // difficulty ever has a replay and the chart is always autoplay-driven.
    [Resolved(canBeNull: true)]
    private ReplayStore? replays { get; set; }

    // Only MainScreen caches this, which is deliberate: the detached VIEWER window builds its own
    // visual stack and resolves nothing here, so the notice is raised once, in the master window
    // the user is actually interacting with. See VideoNotifier.
    [Resolved(canBeNull: true)]
    private VideoNotifier? videoNotifier { get; set; }

    // MainScreen's playerBox live pixel size (see its own [Cached] remarks) — resolved as
    // nullable since only MainScreen's real hosting caches it; falls back to this Drawable's
    // own (fixed-canvas) DrawSize wherever it isn't available (visual tests, catch mode).
    [Resolved(canBeNull: true)]
    private Bindable<Vector2>? playerBoxSize { get; set; }

    // The box's live corner radius, from the same source and just as optional — the per-layer clips
    // round their corners the way the box would have while standing in for it.
    [Resolved(canBeNull: true, name: MainScreen.player_box_corner_radius)]
    private Bindable<float>? playerBoxCornerRadius { get; set; }

    private readonly BindableDouble beatmapOffset = new();
    private readonly BindableDouble globalOffset = new();

    /// <summary>Test-only: the offset-adjusted clock time driving the visual stack.</summary>
    internal double VisualClockTime => offsetClock.CurrentTime;

    /// <summary>
    /// Test-only access to disposal state (JukeBox.Game.Tests has InternalsVisibleTo) —
    /// <see cref="Drawable.IsDisposed"/> itself is protected.
    /// </summary>
    internal bool Disposed => IsDisposed;

    /// <summary>Test-only: frames actually rendered by the storyboard's video, or null with no
    /// video. Only increments once a decoded frame within sync of the playback position is
    /// displayed — the definitive signal the video isn't stuck re-seeking after a mid-song
    /// start.</summary>
    internal int? VideoFramesProcessed => storyboardLayer.VideoFramesProcessed;

    /// <summary>
    /// Test-only: whether a WORKING video is present — a storyboard Video event whose file actually
    /// resolved and whose decoder hasn't faulted. A video that renders nothing (missing file or
    /// faulted decoder) stops counting for background auto-hide, or the result is a black screen.
    /// </summary>
    internal bool HasVideoLayer => storyboardLayer.VideoPlayable;

    /// <summary>Test-only: the lazer storyboard layer.</summary>
    internal LazerStoryboardLayer StoryboardLayer => storyboardLayer;

    /// <summary>Test-only: whether this stack has been retired (see <see cref="Retire"/>).</summary>
    internal bool Retired => retired;

    private bool retired;


    /// <summary>
    /// Stops this stack being what the user is watching, immediately, without waiting for its
    /// replacement to finish loading.
    ///
    /// <para>
    /// The track swaps the instant the next song is ready, but building the next visual stack
    /// (decoding a storyboard, converting the beatmap, building lazer's ruleset and its skin) takes
    /// seconds on a heavy set — and for all of those seconds this stack was still on screen,
    /// animating a storyboard and drawing a chart belonging to a song that had already stopped
    /// playing. Hiding it here is what makes the visuals honest: black (or whatever the incoming
    /// stack shows once it arrives) says "nothing to show yet", the previous song's storyboard says
    /// something false.
    /// </para>
    ///
    /// <para>
    /// Sound is silenced explicitly rather than left to the fade: hitsounds run off the chart
    /// layer, which is deliberately <see cref="Drawable.AlwaysPresent"/> so it keeps updating while
    /// invisible (that is what makes "hitsounds with the chart hidden" work at all), so a layer
    /// that was only faded out would go on playing the old song's hitsounds over the new one.
    /// </para>
    /// </summary>
    internal void Retire()
    {
        if (retired)
            return;

        retired = true;

        if (chartLayer != null)
        {
            chartLayer.HitSoundsEnabled.Value = false;
            chartLayer.AlwaysPresent = false;
        }

        // Storyboard Sample events and keysounds, which reach the audio graph through this.
        audioAdjustments.Volume.Value = 0;

        // Hidden outright rather than faded: this drawable's clock IS the playback clock (see
        // load's FramedOffsetClock), so a transform here would only run while the track is moving —
        // retiring during a pause, a seek, or the gap between two songs would leave the old stack
        // sitting on screen indefinitely, which is the bug rather than a softer version of the fix.
        Alpha = 0;
    }

    /// <summary>Test-only: whether a VISIBLE chart layer is currently present. The lazer layer
    /// also exists invisibly when only hitsounds are enabled (lazer's DrawableRuleset is the
    /// hitsound source), so visibility is the equivalent of the old chart-layer presence.</summary>
    internal bool HasChartLayer => chartLayer is { Alpha: > 0 };

    /// <summary>Test-only: whether the lazer gameplay layer EXISTS, visible or not — the thing
    /// hitsounds actually need (see <see cref="updateChartVisibility"/>).</summary>
    internal bool ChartLayerBuilt => chartLayer != null;

    /// <summary>Test-only: the gameplay layer's current alpha, 0 with no layer.</summary>
    internal float ChartLayerAlpha => chartLayer?.Alpha ?? 0;

    /// <summary>Test-only: whether the gameplay layer keeps updating (and so sounding) while
    /// invisible — false with no layer.</summary>
    internal bool ChartLayerAlwaysPresent => chartLayer?.AlwaysPresent == true;

    /// <summary>Test-only: the per-layer clips, so tests can assert what each one is masking
    /// rather than re-deriving the rule.</summary>
    internal Container StoryboardClip => storyboardClip;

    internal Container ChartClip => chartClip;

    internal Container BackdropClip => backdropClip;

    internal Container DimClip => dimClip;

    /// <summary>Test-only: whether lazer-native hitsound playback is currently enabled.</summary>
    internal bool HasHitSoundPlayer => chartLayer?.HitSoundsEnabled.Value == true;

    /// <summary>Test-only: number of hit objects in the playable beatmap (0 = none/absent/not loaded yet).</summary>
    internal int ChartObjectCount => chartLayer?.ObjectCount ?? 0;

    /// <summary>Test-only: the current lazer gameplay layer, if any.</summary>
    internal LazerChartLayer? ChartRenderer => chartLayer;

    /// <summary>Test-only: the side-by-side grid, non-null only when this difficulty has several replays.</summary>
    internal MultiReplayGrid? MultiGrid => multiGrid;

    /// <summary>Test-only: the one-chart combine view, the other multi-replay shape.</summary>
    internal MultiReplayCombine? MultiCombine => multiCombine;

    /// <summary>Test-only: the container carrying the app Volume setting to lazer-rendered audio.</summary>
    internal AudioContainer AudioAdjustments => audioAdjustments;

    /// <summary>
    /// Why the chart (and hitsounds) are unavailable for this difficulty, or null when a chart
    /// beatmap was parsed. Always logged — "Render chart is on but nothing shows" must never be
    /// silent again.
    /// </summary>
    internal string? ChartUnavailableReason { get; private set; }

    private void markChartUnavailable(string reason)
    {
        ChartUnavailableReason = reason;
        Logger.Log($"Chart unavailable: {reason} (chart + hitsounds stay off for this track; storyboard/audio unaffected)");
    }

    /// <summary>Test-only: current alpha of the background-dim scrim.</summary>
    internal float DimAlpha => dimScrim.Alpha;

    /// <summary>
    /// Test-only: how many times a <see cref="JukeBoxSetting.PlayfieldZoom"/> change has forced the
    /// background blur's framebuffer to redraw (regression coverage for the stale low-res buffer
    /// bug — see the wiring in <see cref="LoadComplete"/>). 0 with no background (no
    /// <c>backgroundBlurContainer</c> to redraw) or before any zoom change.
    /// </summary>
    internal int BackgroundZoomForceRedrawCount { get; private set; }

    /// <summary>
    /// Test-only: whether our own background <see cref="Sprite"/> is currently visible. Auto-hidden
    /// when the storyboard explicitly replaces the background (draws it as one of its own sprites —
    /// lazer's <c>Storyboard.ReplacesBackground</c>) or a working video plays behind it, matching
    /// real osu! behaviour. Plain sprite storyboards leave the background visible underneath.
    /// </summary>
    internal bool BackgroundVisible => backgroundSprite is { Alpha: > 0 };

    /// <summary>
    /// Test-only: the background image file this stack actually resolved for its difficulty — the
    /// selected .osu's own [Events] background where it declares one, else the set-level fallback.
    /// Null when neither exists.
    /// </summary>
    internal string? BackgroundFile { get; private set; }

    /// <summary>Test-only: the texture the background sprite was built from (null with no background).</summary>
    internal Texture? BackgroundTexture => backgroundSprite?.Texture;

    /// <summary>The .osu file this stack was built for (selected difficulty).</summary>
    internal string? OsuFile => osuFile;

    /// <param name="set">The beatmap set to visualise.</param>
    /// <param name="playbackClock">The shared playback clock.</param>
    /// <param name="osuFile">The selected difficulty (defaults to
    /// <see cref="CachedBeatmapSet.PreferredOsuFile"/>).</param>
    public BeatmapVisuals(CachedBeatmapSet set, IFrameBasedClock playbackClock, string? osuFile = null)
    {
        this.set = set;
        this.osuFile = osuFile ?? set.PreferredOsuFile;
        this.playbackClock = playbackClock;
    }

    [BackgroundDependencyLoader]
    private void load(GameHost host)
    {
        RelativeSizeAxes = Axes.Both;

        // Audio-offset wrapper around the shared playback clock: shifts the ENTIRE visual stack
        // (storyboard, chart, hitsound timing) relative to the track — positive offset runs the
        // visuals earlier (compensates audio that sounds late). processSource: false because
        // PlaybackController pumps the underlying clock's ProcessFrame() itself every Update() —
        // pumping it from here too would corrupt its ElapsedFrameTime bookkeeping. The wrapper
        // still needs its own per-frame latch, hence ProcessCustomClock stays true.
        Clock = offsetClock = new FramedOffsetClock(playbackClock, false);
        ProcessCustomClock = true;

        // Plain black backdrop, always present and always fully opaque — video (FillMode.Fit) and
        // the storyboard (uniform-scaled, possibly narrower than widescreen) don't necessarily
        // cover the full width/height, so without this the letterboxing would show whatever's
        // behind this drawable instead of black, same as osu! stable/lazer's own letterboxing.
        // Inside backdropClip with the background, since neither ever wants releasing: both are
        // canvas-shaped by construction and the background's FillMode.Fill overspill (plus anything
        // PlayfieldZoom magnifies past the box) is exactly what the box's mask was cropping.
        AddInternal(backdropClip = new Container
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Child = backdropCanvas = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Child = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Color4.Black,
                },
            },
        });

        // [General] Mode and [Events] background both belong to the SELECTED difficulty, so scan it
        // once here and let both the background and the chart read off the same result.
        OsuFileInfo? selectedInfo = null;

        if (osuFile != null)
        {
            try
            {
                selectedInfo = OsuFileScanner.Scan(osuFile);
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Failed to scan '{osuFile}'; falling back to the set-level background, no chart");
            }
        }

        BackgroundFile = resolveBackgroundFile(selectedInfo);

        if (BackgroundFile != null)
        {
            backgroundTextures = new TextureStore(host.Renderer,
                host.CreateTextureLoaderStore(new StorageBackedResourceStore(new NativeStorage(set.Directory, host))),
                useAtlas: false, scaleAdjust: 1);

            string relative = Path.GetRelativePath(set.Directory, BackgroundFile).Replace('\\', '/');
            var texture = backgroundTextures.Get(relative);

            if (texture != null)
            {
                // The blur wrapper only affects our flat background image — storyboard/video
                // content draws above it in its own layer and is never blurred (matching lazer,
                // whose background blur also leaves storyboards/videos sharp). One extra
                // framebuffer while a background is shown; the content is static, so the buffer
                // only re-renders when the blur radius itself changes.
                backdropCanvas.Add(backgroundBlurContainer = new BufferedContainer(cachedFrameBuffer: true)
                {
                    RelativeSizeAxes = Axes.Both,
                    RedrawOnScale = false,
                    Child = backgroundSprite = new Sprite
                    {
                        RelativeSizeAxes = Axes.Both,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        FillMode = FillMode.Fill,
                        Texture = texture,
                        Colour = new Colour4(0.7f, 0.7f, 0.7f, 1f),
                    },
                });
            }
        }

        // Everything that can carry lazer-native audio (storyboard Sample events / keysounds,
        // chart hitsounds) lives inside this container so the app Volume setting reaches them —
        // see the audioAdjustments field remarks. BindTo is live: later Volume changes propagate.
        AddInternal(audioAdjustments = new AudioContainer
        {
            RelativeSizeAxes = Axes.Both,
        });
        audioAdjustments.Volume.BindTo(playbackController.Volume);

        // Lazer's storyboard renderer carries the whole stack the old hand-rolled path split up:
        // sprites/animations, TRIGGER commands, storyboard Sample audio events (keysounds), and
        // the storyboard Video event (with its start-time offset) — video is just another element
        // inside DrawableStoryboard, so the separate hand-synced video layer (and its warm-up
        // machinery) is gone. Sizing is lazer's own: the storyboard scales itself off our height.
        // The storyboard gets its own audio sub-container so the "Storyboard / video" toggle can
        // silence its Sample events (keysounds) while hidden — Alpha alone leaves audio running.
        audioAdjustments.Add(storyboardClip = new Container
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Child = storyboardAudio = new AudioContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Child = storyboardLayer = new LazerStoryboardLayer(set, osuFile),
            },
        });

        // Real osu! behaviour: the flat background auto-hides only when the storyboard REPLACES
        // it (draws it as one of its own sprites) or when a working video plays behind the
        // storyboard. AddInternal above runs the layer's BackgroundDependencyLoader synchronously,
        // so the decode result is already available here. Re-checked every Update — a video
        // decoder fault surfaces asynchronously and must bring the background back.
        updateBackgroundVisibility();

        // Background-dim scrim: sits between the storyboard/video/background stack and the chart
        // so the chart stays readable. Applies whenever the setting is > 0, even with chart off.
        audioAdjustments.Add(dimClip = new Container
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Child = dimScrim = new Box
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Colour = Color4.Black,
                Alpha = 0,
            },
        });

        // The chart (and hitsound player) get added/removed inside this fixed container as the
        // settings toggle, so their z-position above the scrim is stable. Sized properly (matching
        // catch's absolute-pixel needs vs the other three rulesets' real-aspect needs) every
        // Update() — see its own remarks there; the placeholder size here only matters for the
        // very first layout pass before Update() has run once.
        audioAdjustments.Add(chartClip = new Container
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Child = chartContainer = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(1024, 768),
            },
        });

        // All four rulesets (0 std / 1 taiko / 2 catch / 3 mania) render through lazer's real
        // gameplay renderer. Every skip reason is recorded and logged — a silently-absent chart
        // cost a real debugging round (storyboard-heavy sets are often mania/taiko-only, which
        // used to fall through here without a trace).
        if (osuFile == null)
        {
            markChartUnavailable($"set {set.SetId} has no .osu difficulty to chart");
        }
        else if (selectedInfo == null)
        {
            markChartUnavailable($"'{Path.GetFileName(osuFile)}' could not be scanned");
        }
        else
        {
            try
            {
                int mode = selectedInfo.Mode;
                chartMode = mode;

                if (mode is < 0 or > 3)
                {
                    markChartUnavailable($"'{Path.GetFileName(osuFile)}' is unknown game mode {mode} — no chart renderer for it");
                }
                else
                {
                    var working = new osu.Game.Beatmaps.FlatWorkingBeatmap(osuFile);

                    if (working.Beatmap.HitObjects.Count == 0)
                        markChartUnavailable($"'{Path.GetFileName(osuFile)}' parsed to 0 hit objects");
                    else
                        chartWorking = working;
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Failed to parse chart for '{osuFile}'; chart disabled for this difficulty");
                chartWorking = null;
            }
        }

        if (config != null)
        {
            config.BindWith(JukeBoxSetting.RenderChart, renderChart);
            config.BindWith(JukeBoxSetting.MultiReplayMode, multiReplayMode);
        config.BindWith(JukeBoxSetting.KnockoutMode, knockoutMode);
        config.BindWith(JukeBoxSetting.KnockoutSortBy, knockoutSortBy);
        config.BindWith(JukeBoxSetting.KnockoutLiveSort, knockoutLiveSort);
            config.BindWith(JukeBoxSetting.PlayHitSounds, playHitSounds);
            config.BindWith(JukeBoxSetting.BackgroundDim, backgroundDim);
            config.BindWith(JukeBoxSetting.BackgroundBlur, backgroundBlur);
            config.BindWith(JukeBoxSetting.ShowStoryboard, showStoryboard);
            config.BindWith(JukeBoxSetting.ShowVideo, showVideo);
            config.BindWith(JukeBoxSetting.PlayfieldZoom, playfieldZoom);
            config.BindWith(JukeBoxSetting.ChartOpacity, chartOpacity);
            config.BindWith(JukeBoxSetting.RemoveChartMask, removeChartMask);
            config.BindWith(JukeBoxSetting.RemoveStoryboardMask, removeStoryboardMask);
        }

        if (skinSelection != null)
        {
            effectiveSkin.BindTo(skinSelection.Effective);
            skinRevision.BindTo(skinSelection.Revision);
        }

        if (chartMods != null)
            chartModRevision.BindTo(chartMods.Revision);

        if (chartConversion != null)
            chartConversionRevision.BindTo(chartConversion.Revision);

        if (offsetStore != null)
            beatmapOffset.BindTo(offsetStore.CurrentOffset);

        config?.BindWith(JukeBoxSetting.GlobalAudioOffset, globalOffset);

        // Build the lazer layer during async load when a setting already wants it, so the
        // (conversion + autoplay-generation) cost stays off the update thread for the common path.
        updateLazerLayer();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Live-react to settings changes without a rebuild (initial state already applied in load()).
        renderChart.BindValueChanged(_ => updateLazerLayer());
        multiReplayMode.BindValueChanged(_ => rebuildForMultiReplayMode());

        // Knockout settings do NOT rebuild. Who is out is a question about plays that have already
        // been recorded, so changing the rule mid-song is a re-reading of data that is all there —
        // rebuilding would throw away the simulation and make the user watch it happen again.
        knockoutMode.BindValueChanged(_ => updateKnockoutRules());
        knockoutSortBy.BindValueChanged(_ => updateKnockoutRules());
        knockoutLiveSort.BindValueChanged(_ => updateKnockoutRules());
        playHitSounds.BindValueChanged(_ => updateLazerLayer());

        // Opacity is pure alpha on the layer already on screen — no rebuild, unlike mods or a
        // conversion (see chartModRevision), which change what the layer IS rather than how
        // opaque it is.
        chartOpacity.BindValueChanged(_ => updateChartVisibility());
        backgroundDim.BindValueChanged(e => dimScrim.Alpha = (float)e.NewValue, true);
        backgroundBlur.BindValueChanged(e =>
        {
            if (backgroundBlurContainer != null)
                backgroundBlurContainer.BlurSigma = new Vector2((float)e.NewValue * max_blur_sigma);
        }, true);
        showStoryboard.BindValueChanged(_ => updateStoryboardDisplay(), true);
        showVideo.BindValueChanged(_ => updateStoryboardDisplay(), true);
        removeStoryboardMask.BindValueChanged(_ => updateStoryboardDisplay(), true);

        // Regression fix: backgroundBlurContainer sets RedrawOnScale = false (see its own
        // construction comment) so MainScreen's continuous, purely-cosmetic scale changes —
        // window resizes, the focus-mode transition tween — never force a re-blur of a background
        // that hasn't actually changed. That's fine for those small, incidental scale wobbles, but
        // JukeBoxSetting.PlayfieldZoom spans a much wider, user-driven 1%-200% range applied to the
        // very same ancestor chain (MainScreen.sceneContainer) — osu.Framework's own
        // BufferedContainer.Update() cancels an ancestor scale change back to "local units" via the
        // current DrawInfo's inverse before deciding whether to re-render (that's the whole point of
        // RedrawOnScale=false), and that cancellation loses enough float precision at this zoom
        // range's extremes that a redraw can end up baking the framebuffer at whatever tiny
        // effective size was on screen at that moment — after which RedrawOnScale=false means
        // returning to a larger zoom is never itself a reason to redraw again, so that stale
        // low-res bake stays cached, visibly blurry/pixelated once stretched back up. Explicitly
        // forcing a redraw on every PlayfieldZoom change sidesteps that precision-sensitive
        // cancellation for exactly this known cause, while leaving RedrawOnScale=false doing its
        // job for the resize/transition wobbles it was actually meant for — no cost at steady state
        // (this only fires on an actual value change), and no rebuild.
        playfieldZoom.BindValueChanged(_ =>
        {
            if (backgroundBlurContainer == null)
                return;

            backgroundBlurContainer.ForceRedraw();
            BackgroundZoomForceRedrawCount++;
        });

        beatmapOffset.BindValueChanged(_ => updateClockOffset());
        globalOffset.BindValueChanged(_ => updateClockOffset());
        updateClockOffset();

        // A skin choice change (dropdown flip, or Random's per-song re-roll) rebuilds the chart
        // layer — the skin chain is constructed once per LazerChartLayer, so a rebuild is the
        // application mechanism (documented; the ruleset checkboxes by contrast apply live).
        effectiveSkin.BindValueChanged(_ => rebuildChartLayer());
        skinRevision.BindValueChanged(_ => rebuildChartLayer());

        // Same mechanism for a mod-selection change — see chartModRevision's own remarks for why
        // mods need the rebuild that element visibility doesn't.
        chartModRevision.BindValueChanged(_ => rebuildChartLayer());

        chartConversionRevision.BindValueChanged(_ =>
        {
            publishConversionState();
            rebuildChartLayer();
        });

        // This difficulty's own convertibility is only knowable from a decoded beatmap, which is
        // what this class holds — so the Chart tab reads the answer from here rather than decoding
        // anything itself.
        publishConversionState();
    }

    /// <summary>Tells the shared conversion service what is actually on screen for this
    /// difficulty. A difficulty with no chart (unparseable, or none selected) publishes nothing
    /// convertible, which greys the control rather than offering a conversion of nothing.</summary>
    private void publishConversionState()
        => chartConversion?.Publish(chartWorking, allowConversion: replays?.ForOsuFile(osuFile)?.Score == null);

    private void rebuildChartLayer()
    {
        if (chartLayer == null)
            return;

        chartContainer.Remove(chartLayer, true);
        chartLayer = null;
        updateLazerLayer();
    }

    private void updateClockOffset() => offsetClock.Offset = beatmapOffset.Value + globalOffset.Value;

    /// <summary>
    /// The background image for the SELECTED difficulty. Every .osu in a set declares its own
    /// [Events] background and they routinely differ, but <see cref="CachedBeatmapSet.BackgroundFile"/>
    /// is scanned once off the set's default difficulty (<see cref="CachedBeatmapSet.PreferredOsuFile"/>) —
    /// reading it directly here left the previous difficulty's image on screen after a mid-song
    /// difficulty switch, even though the rebuilt stack was otherwise correct for the new diff.
    /// (Video and widescreen sizing were never affected: both come from the storyboard lazer
    /// decodes out of the selected .osu — see <see cref="LazerStoryboardLayer.DecodeStoryboard"/>.)
    /// Falls back to the set-level value when the selected difficulty declares no background of its
    /// own, or names a file that isn't actually in the folder.
    /// </summary>
    private string? resolveBackgroundFile(OsuFileInfo? info)
    {
        if (osuFile != null && !string.IsNullOrEmpty(info?.BackgroundFilename))
        {
            string path = Path.Combine(Path.GetDirectoryName(osuFile) ?? set.Directory, info.BackgroundFilename);

            if (File.Exists(path))
                return path;
        }

        return set.BackgroundFile;
    }

    /// <summary>
    /// Hides our own background sprite when the storyboard replaces it or a working video plays,
    /// matching real osu! behaviour (see <see cref="LazerStoryboardLayer.ShouldHideBackground"/>).
    /// Re-evaluated every <see cref="Update"/>: a video decoder fault surfaces asynchronously on
    /// the decoder's own thread, and the background must come back rather than leaving the user
    /// staring at pure black.
    /// </summary>
    /// <summary>
    /// Tells the user once when this beatmap declares a video that cannot play, so the background
    /// they end up looking at reads as a deliberate fallback rather than a broken video.
    ///
    /// <para>
    /// Polled from <see cref="Update"/> alongside the background rule because neither failure is
    /// known at load: a decoder fault surfaces asynchronously on the decoder's thread, and a missing
    /// file is only distinguishable once the storyboard's children exist (see
    /// <see cref="LazerStoryboardLayer.VideoMissing"/>). Until then <c>VideoPlayable</c> is true, so
    /// a loading video is never mistaken for a broken one.
    /// </para>
    ///
    /// <para>
    /// The once-per-song guarantee is <see cref="VideoNotifier"/>'s, not this flag's: this drawable
    /// is rebuilt on every difficulty switch, so a local guard alone would re-announce.
    /// </para>
    /// </summary>
    private void reportUnplayableVideo()
    {
        if (reportedUnplayableVideo || videoNotifier == null)
            return;

        if (!storyboardLayer.HasVideo || storyboardLayer.VideoPlayable)
            return;

        reportedUnplayableVideo = true;
        videoNotifier.ReportUnplayableVideo(set.SetId);
    }

    private void updateBackgroundVisibility()
    {
        if (backgroundSprite == null)
            return;

        // A hidden storyboard (or video) can't be covering the background, whatever it claims —
        // which half is hidden is the layer's own business now that they switch independently, so
        // it answers with both toggles already accounted for.
        backgroundSprite.Alpha = storyboardLayer.ShouldHideBackground ? 0 : 1;
    }

    /// <summary>
    /// Hands the two display settings to the storyboard layer, which switches lazer's own layers:
    /// the video is one of those layers, so "video but no storyboard" (and the reverse) is a matter
    /// of which layers draw, not of building anything differently.
    ///
    /// <para>
    /// The whole layer is hidden only when NEITHER half is wanted, which stops the storyboard's
    /// subtree updating at all in that case. Samples follow the STORYBOARD toggle alone: they are
    /// storyboard events, and a storyboard you have switched off should not still be audible.
    /// </para>
    /// </summary>
    private void updateStoryboardDisplay()
    {
        storyboardLayer.StoryboardShown.Value = showStoryboard.Value;
        storyboardLayer.VideoShown.Value = showVideo.Value;

        // The release has to reach lazer's own per-layer masking, not just our clips: every
        // storyboard layer masks its own elements, so releasing the player box around it changed
        // nothing on its own (see LazerStoryboardLayer.releaseLayerMasking).
        storyboardLayer.StoryboardReleased.Value = removeStoryboardMask.Value;

        storyboardLayer.Alpha = showStoryboard.Value || showVideo.Value ? 1 : 0;
        storyboardAudio.Volume.Value = showStoryboard.Value ? 1 : 0;

        updateBackgroundVisibility();
    }

    /// <summary>
    /// One lazer gameplay layer serves both settings: RenderChart shows lazer's rendered gameplay,
    /// PlayHitSounds enables lazer's native hitsound/keysound playback. Hitsounds without chart
    /// keeps the layer alive but invisible — the DrawableRuleset is what plays the samples.
    ///
    /// <para>
    /// The rule, in full: the layer is BUILT whenever either setting wants it (neither wanting it
    /// builds nothing at all, so the conversion + autoplay-generation cost is never paid for a
    /// track nobody is charting or listening to); it is VISIBLE at
    /// <see cref="JukeBoxSetting.ChartOpacity"/> while RenderChart is on and invisible otherwise;
    /// and its audio is independent of both, because it is <see cref="Drawable.AlwaysPresent"/> —
    /// see <see cref="updateChartVisibility"/> for why that single flag is what makes
    /// hitsounds-without-chart work at all.
    /// </para>
    /// </summary>
    private void updateLazerLayer()
    {
        // A retired stack is on its way off screen and has already been silenced (see Retire) —
        // a setting changing under it must not build a layer or switch its hitsounds back on.
        if (retired)
            return;

        // A keysound-only set IS its hitsounds — the silent track carries no music, so leaving
        // them off the user's setting would play the map in total silence. Forced on for these
        // sets only; the setting is untouched and governs everything else as before.
        bool hitSounds = playHitSounds.Value || set.HasVirtualAudio;

        bool wantLayer = (renderChart.Value || hitSounds) && chartWorking != null && osuFile != null;

        if (wantLayer && chartLayer == null)
        {
            // Only a replay registered against THIS difficulty applies: a dropped .osr identifies
            // one exact .osu by checksum, so switching to any other difficulty of the same set
            // correctly falls back to autoplay (and switching back restores the replay).
            var forThisDifficulty = replays?.AllForOsuFile(osuFile) ?? Array.Empty<Replays.ReplayAttachment>();
            var replay = forThisDifficulty.FirstOrDefault();

            if (replay?.Score != null)
                Logger.Log($"Playing back {replay.PlayerName}'s replay on '{Path.GetFileName(osuFile)}' instead of autoplay");

            // Several replays of this exact difficulty are watched TOGETHER rather than one being
            // picked — as everyone's cursor over one chart, or as a grid of separate renders,
            // whichever the user prefers. One replay (or none) keeps the single layer it always had.
            if (forThisDifficulty.Count > 1 && multiReplayMode.Value == MultiReplayMode.Combine)
            {
                Logger.Log($"Playing {forThisDifficulty.Count} replays of '{Path.GetFileName(osuFile)}' over one chart");

                chartContainer.Add(multiCombine = new MultiReplayCombine(osuFile!, forThisDifficulty)
                {
                    AlwaysPresent = true,
                    Rules = currentKnockoutRules(),
                });
            }
            else if (forThisDifficulty.Count > 1)
            {
                Logger.Log($"Playing {forThisDifficulty.Count} replays of '{Path.GetFileName(osuFile)}' side by side"
                           + $" ({MultiReplayLayout.RenderedCount(forThisDifficulty.Count)} rendered)");

                chartContainer.Add(multiGrid = new MultiReplayGrid(set, osuFile!, forThisDifficulty) { AlwaysPresent = true });
            }
            else
            {
                chartContainer.Add(chartLayer = new LazerChartLayer(chartWorking!, osuFile!, replay?.Score)
                {
                    // See updateChartVisibility: this is what keeps the layer updating — and
                    // therefore sounding — while it is invisible.
                    AlwaysPresent = true,
                });
            }
        }
        else if (!wantLayer && chartLayer != null)
        {
            chartContainer.Remove(chartLayer, true);
            chartLayer = null;
        }
        else if (!wantLayer && multiGrid != null)
        {
            chartContainer.Remove(multiGrid, true);
            multiGrid = null;
        }
        else if (!wantLayer && multiCombine != null)
        {
            chartContainer.Remove(multiCombine, true);
            multiCombine = null;
        }

        if (chartLayer != null)
        {
            chartLayer.HitSoundsEnabled.Value = hitSounds;
            updateChartVisibility();
        }

        if (multiGrid != null)
        {
            // The grid decides internally that only ONE of its cells may sound — N layers hitting
            // the same samples milliseconds apart is a flam, not N times louder.
            multiGrid.HitSoundsEnabled = hitSounds;
            updateChartVisibility();
        }

        if (multiCombine != null)
        {
            // Combine has one chart, so there is no flam to avoid — it sounds like any single layer.
            multiCombine.HitSoundsEnabled = hitSounds;
            updateChartVisibility();
        }
    }

    /// <summary>The knockout rules as the settings currently have them.</summary>
    private KnockoutRules currentKnockoutRules() => new KnockoutRules(
        knockoutMode.Value,
        LiveSort: knockoutLiveSort.Value,
        SortBy: knockoutSortBy.Value);

    /// <summary>
    /// Pushes a changed rule to a combine view that is already on screen, with no rebuild — see the
    /// binding in LoadComplete for why. Does nothing in grid mode, which has no board.
    /// </summary>
    private void updateKnockoutRules()
    {
        if (multiCombine != null)
            multiCombine.Rules = currentKnockoutRules();
    }

    /// <summary>
    /// Switching between combine and grid REBUILDS, because the two are different renderers rather
    /// than two looks of one — there is no state to carry across, and rebuilding is what a mode
    /// change already costs when the user changes it mid-song.
    /// </summary>
    private void rebuildForMultiReplayMode()
    {
        if (multiGrid != null)
        {
            chartContainer.Remove(multiGrid, true);
            multiGrid = null;
        }

        if (multiCombine != null)
        {
            chartContainer.Remove(multiCombine, true);
            multiCombine = null;
        }

        updateLazerLayer();
    }

    /// <summary>
    /// How opaque the gameplay layer is: the user's <see cref="JukeBoxSetting.ChartOpacity"/> while
    /// the chart is being rendered, and nothing at all when it isn't. Applied live to the layer on
    /// screen — opacity never rebuilds anything.
    ///
    /// <para>
    /// The layer stays <see cref="Drawable.AlwaysPresent"/> at every alpha, which is the whole
    /// mechanism behind "hit sounds keep playing with Render chart off". osu!framework treats a
    /// drawable at alpha 0 as absent and skips its entire subtree in <c>UpdateSubTree</c> — so the
    /// hidden layer's own <c>Update</c> never ran, the DrawableRuleset's frame-stable clock never
    /// advanced, and <c>LazerChartLayer</c>'s sample gate stayed shut at its initial "disabled".
    /// The layer was alive and silent. AlwaysPresent keeps it updating (and therefore seeking,
    /// following the rate, and sounding) exactly as the visible one does at the cost of drawing a
    /// fully transparent subtree, which is a cost only a user who asked for hitsounds without a
    /// chart — or dragged the opacity to zero, the same state by another route — ever pays.
    /// </para>
    /// </summary>
    private void updateChartVisibility()
    {
        float alpha = renderChart.Value ? (float)chartOpacity.Value : 0;

        if (chartLayer != null)
            chartLayer.Alpha = alpha;

        // Both multi-replay shapes follow the same rule: whichever is up IS the chart, so "render
        // chart" and the opacity slider govern it exactly as they govern a single layer.
        if (multiGrid != null)
            multiGrid.Alpha = alpha;

        if (multiCombine != null)
            multiCombine.Alpha = alpha;
    }

    protected override void Update()
    {
        base.Update();

        // Nothing here is worth doing for a stack that is fading out and about to be removed, and
        // the layer rules below would undo what Retire just switched off.
        if (retired)
            return;

        // A storyboard video's decoder fault only surfaces asynchronously — keep the background
        // rule live so a faulted (black) video brings the background back.
        updateBackgroundVisibility();
        reportUnplayableVideo();

        // Re-read every frame for the same reason the chart sizing below is: the box's size, the
        // zoom and the corner radius all move continuously (window resize, the focus-mode
        // transition, a dragged zoom slider), and the clips have to track them, not a value
        // sampled when a setting last changed.
        updateLayerClips();

        // Every ruleset gets the same rectangle — the player box's REAL aspect — and catch alone
        // gets it in different UNITS: it positions the catcher and the fruits with absolute
        // constants against lazer's own 768-tall game space (see catch_game_height), so the
        // container has to be that many units tall for its arrangement to land where the real game
        // puts it. Same on-screen rectangle either way; only the coordinate scale differs.
        //
        // Standard/taiko/mania are the opposite: their PlayfieldAdjustmentContainers size
        // themselves in RELATIVE, scale-invariant fractions of whatever box they're actually
        // given — a fixed 1024×768 (4:3) canvas here previously meant taiko's own
        // TaikoPlayfieldAdjustmentContainer (which explicitly reads its parent's ASPECT to decide
        // its own width — see its MINIMUM_ASPECT/MAXIMUM_ASPECT clamp, matching lazer's real
        // Player-screen convention of a widescreen game window) was always told it lived in a 4:3
        // box, regardless of what aspect the surrounding player UI actually has. Since our own
        // player box is a 854×480 (16:9, see MainScreen.scene_width/height) design canvas, that
        // mismatch let taiko compute a WIDTH matching a 4:3 box's shorter side, then centre that
        // narrower box within the real (wider) one — visible as dead margins down both sides of
        // the lane, ~12.5% each, with the note/drum/target confined to the narrower centre strip
        // while non-taiko-playfield elements (the storyboard, which isn't a chartContainer child)
        // correctly spanned the full width. Sizing chartContainer to the REAL available box
        // (DrawSize, matching this Drawable's own actual aspect) instead of a fixed 1024×768 fixes
        // this directly — std/mania's own on-screen footprint is unaffected (their sizing is
        // scale-invariant, so this is a no-op for them beyond a harmless coordinate-scale change).
        {
            // Taiko's own PlayfieldAdjustmentContainer already scales itself as a scale-invariant
            // fraction of whatever aspect its parent hands it, including its own [5:4, 16:9]
            // widescreen clamp — matching how real lazer's Player screen exposes the ruleset to
            // the game window's REAL aspect directly, never an intermediate fixed-canvas one.
            // Handing chartContainer this Drawable's own DrawSize (locked to MainScreen's fixed
            // scene_width×scene_height design canvas) instead meant that whenever playerBox was
            // wider than that canvas — the common case once a window/monitor is wide enough, or
            // in focus mode where the box goes full-bleed — the CANVAS's own contain-fit
            // letterboxing (purely an artefact of fitting a fixed-aspect canvas into a wider box,
            // nothing to do with taiko's own clamp) left a margin down both sides of the lane
            // that taiko's own clamp was never even asked about; the storyboard (not a
            // chartContainer child) showed the exact same margin, confirming it wasn't
            // taiko-specific. playerBoxSize is the box's REAL, live pixel size; dividing out the
            // same base contain-fit scale MainScreen applies to sceneContainer (before its zoom
            // multiply) converts it into this Drawable's own local units, handing chartContainer
            // a size whose ASPECT matches the box's real aspect exactly — so it's taiko's own
            // clamp, not an extra intermediate letterbox, that decides whether (and how) to
            // letterbox from here on. std/mania are unaffected either way (their sizing is
            // scale-invariant, see the catch-branch remarks above).
            Vector2 available = DrawSize;

            if (playerBoxSize != null)
            {
                Vector2 box = playerBoxSize.Value;

                if (box.X > 0 && box.Y > 0 && DrawSize.X > 0 && DrawSize.Y > 0)
                {
                    float baseScale = Math.Min(box.X / DrawSize.X, box.Y / DrawSize.Y);

                    if (baseScale > 0)
                        available = new Vector2(box.X / baseScale, DrawSize.Y);
                }
            }

            // Catch's units, everyone else's as-is. The rectangle is identical either way: a
            // container sized `available / unitScale` and scaled by `unitScale` covers exactly what
            // `available` at scale one covers, which is what lets catch reason in lazer's 768-tall
            // space without changing anyone's on-screen footprint.
            float unitScale = chartMode == 2 && DrawHeight > 0 ? DrawHeight / catch_game_height : 1;

            chartContainer.Size = available / unitScale;
            chartContainer.Scale = new Vector2(unitScale);
        }
    }

    /// <summary>
    /// Keeps the four per-layer clips (and the canvas-sized containers inside them) matched to the
    /// player box, and decides which of them is actually masking.
    ///
    /// <para>
    /// A clip masks ONLY while <see cref="Screens.MainScreen"/>'s box has stopped masking on behalf
    /// of a "Remove ... mask" setting — with neither setting on, all four are inert and the box's
    /// own single mask does the work exactly as it did before this feature existed, which is worth
    /// more than the redundancy would be: two masks at nominally the same rectangle are two chances
    /// for a rounding difference to show up as a seam along an edge.
    /// </para>
    ///
    /// <para>
    /// The rectangle is the box's real pixel size converted into this canvas's own local units by
    /// dividing out the scale <c>MainScreen.updateSceneScale</c> applies to the whole scene (its
    /// contain-fit factor times <see cref="JukeBoxSetting.PlayfieldZoom"/>) — so a clip covers
    /// precisely what the box covers, whatever the window size, the layout or the zoom. Without a
    /// box to measure (the detached viewer, a bare test scene) nothing here masks at all: there is
    /// no box in those hosts for content to be released FROM.
    /// </para>
    /// </summary>
    private void updateLayerClips()
    {
        var canvas = DrawSize;

        // The clips are box-sized, so their contents can't size themselves relatively any more —
        // these three are the layers whose footprint IS the design canvas.
        backdropCanvas.Size = canvas;
        storyboardAudio.Size = canvas;
        dimScrim.Size = canvas;

        var boxLocal = canvas;
        float cornerRadiusLocal = 0;
        bool boxKnown = false;

        if (playerBoxSize != null && canvas.X > 0 && canvas.Y > 0)
        {
            var box = playerBoxSize.Value;

            if (box.X > 0 && box.Y > 0)
            {
                float sceneScale = Math.Min(box.X / canvas.X, box.Y / canvas.Y) * (float)playfieldZoom.Value;

                if (sceneScale > 0)
                {
                    boxLocal = box / sceneScale;
                    cornerRadiusLocal = (playerBoxCornerRadius?.Value ?? 0) / sceneScale;
                    boxKnown = true;
                }
            }
        }

        // "Stand in for the box" — true exactly when the box has been released for one layer or the
        // other, since that is the only time anything here has to clip on its behalf.
        bool standIn = boxKnown && (removeChartMask.Value || removeStoryboardMask.Value);

        applyClip(backdropClip, standIn, boxLocal, cornerRadiusLocal);
        applyClip(storyboardClip, standIn && !removeStoryboardMask.Value, boxLocal, cornerRadiusLocal);
        applyClip(dimClip, standIn, boxLocal, cornerRadiusLocal);

        // The chart's own mask is the SCENE, not the box — and it is on whenever the user has not
        // released it, box or no box.
        //
        // A hit object sitting at the playfield's edge reaches past the playfield by its own radius,
        // and its approach circle by several times that; with nothing clipping until the box, those
        // drew on the black AROUND the scene, which is what a user reads as "outside the screen"
        // (reported against a zoomed-out playfield, where the box is much larger than the scene, but
        // it was never zoom-specific — the overflow was simply less obvious at 100%). Clipping to
        // the scene is what makes "the chart stays on screen" true; releasing the mask is what lets
        // it spill, which is the whole point of the setting.
        //
        // The box's own mask still applies on top whenever it is masking, so zooming the scene past
        // the box's edges is caught there as before. osu!catch's fruit-spawn and catcher overflow
        // (see catch_reserved_height) is clipped by this too, deliberately: it is exactly the
        // "chart outside the screen" being complained about, and the release toggle is how someone
        // who wants lazer's off-screen-spawn look asks for it.
        applyClip(chartClip, !removeChartMask.Value, canvas, 0);

        // And the other half of the same release: the ruleset masks its own playfield (osu!catch cut
        // fruits in half along a line above the frame with everything of OURS already released), so
        // the layer switches lazer's own masks off for as long as the setting is on.
        if (chartLayer != null)
            chartLayer.ChartReleased.Value = removeChartMask.Value;
    }

    private static void applyClip(Container clip, bool masking, Vector2 size, float cornerRadius)
    {
        clip.Size = size;
        clip.Masking = masking;
        clip.CornerRadius = masking ? cornerRadius : 0;
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        backgroundTextures?.Dispose();
    }
}
