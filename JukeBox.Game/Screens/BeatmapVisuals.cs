#nullable enable

using System;
using System.IO;
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

    // Tuned against MEASURED real-lazer catch geometry (a diagnostic test hosted a real
    // DrawableCatchRuleset in an exact 1024×768 box and read back Catcher/Playfield
    // ScreenSpaceDrawQuad fractions), not lazer's own extra_bottom_space (200) constant — that
    // constant sizes catch's *internal* safety-clip container ("Visible area" in
    // CatchPlayfieldAdjustmentContainer), which is far more generous than what's actually
    // rendered and was never the thing clipping anything of ours. Using it as our own divisor
    // (768+200=968) instead forced that internal safety container to fit EXACTLY flush with
    // chartContainer's own edges (zero margin) — turning its normally-invisible clip boundary
    // into a hard, visible seam exactly at the scene edge, clipping fruits mid-sprite as they
    // entered from the top. 680 instead targets the CATCHER's own measured top edge landing at
    // ~90% of the box height (real lazer: catcher top ≈85.8%, bottom ≈111.2%, fruit spawn top
    // ≈-5.8%, all measured relative to an unscaled 1024×768 box) — big enough to read as "the
    // catcher sits near the bottom" without unnecessarily inflating catch's on-screen size
    // relative to the other three rulesets. The resulting top/bottom overflow is deliberately
    // NOT compensated for further: nothing between chartContainer and MainScreen's playerBox
    // masks (see BeatmapVisuals class summary / MainScreen.sceneContainer), so it renders
    // unclipped into the surrounding letterbox margin — matching real lazer's own "off-screen
    // spawn"/catcher-past-the-nominal-canvas look — and is clipped only if it ever reaches
    // playerBox's own edge, same as everything else in the boxed player.
    private const float catch_reserved_height = 680f;

    // Config-bound (when a config manager is present — test scenes without one keep the
    // defaults). Fields, not locals: config bindables use weak references back to the master.
    private readonly Bindable<bool> renderChart = new();
    private readonly Bindable<bool> playHitSounds = new();
    private readonly BindableDouble backgroundDim = new();
    private readonly BindableDouble backgroundBlur = new();
    private readonly Bindable<bool> showStoryboardVideo = new(true);
    private readonly BindableDouble playfieldZoom = new(1.0);
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

    /// <summary>Test-only: whether a VISIBLE chart layer is currently present. The lazer layer
    /// also exists invisibly when only hitsounds are enabled (lazer's DrawableRuleset is the
    /// hitsound source), so visibility is the equivalent of the old chart-layer presence.</summary>
    internal bool HasChartLayer => chartLayer is { Alpha: > 0 };

    /// <summary>Test-only: whether lazer-native hitsound playback is currently enabled.</summary>
    internal bool HasHitSoundPlayer => chartLayer?.HitSoundsEnabled.Value == true;

    /// <summary>Test-only: number of hit objects in the playable beatmap (0 = none/absent/not loaded yet).</summary>
    internal int ChartObjectCount => chartLayer?.ObjectCount ?? 0;

    /// <summary>Test-only: the current lazer gameplay layer, if any.</summary>
    internal LazerChartLayer? ChartRenderer => chartLayer;

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
        AddInternal(new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Color4.Black,
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
                AddInternal(backgroundBlurContainer = new BufferedContainer(cachedFrameBuffer: true)
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
        audioAdjustments.Add(storyboardAudio = new AudioContainer
        {
            RelativeSizeAxes = Axes.Both,
            Child = storyboardLayer = new LazerStoryboardLayer(set, osuFile),
        });

        // Real osu! behaviour: the flat background auto-hides only when the storyboard REPLACES
        // it (draws it as one of its own sprites) or when a working video plays behind the
        // storyboard. AddInternal above runs the layer's BackgroundDependencyLoader synchronously,
        // so the decode result is already available here. Re-checked every Update — a video
        // decoder fault surfaces asynchronously and must bring the background back.
        updateBackgroundVisibility();

        // Background-dim scrim: sits between the storyboard/video/background stack and the chart
        // so the chart stays readable. Applies whenever the setting is > 0, even with chart off.
        audioAdjustments.Add(dimScrim = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Color4.Black,
            Alpha = 0,
        });

        // The chart (and hitsound player) get added/removed inside this fixed container as the
        // settings toggle, so their z-position above the scrim is stable. Sized properly (matching
        // catch's absolute-pixel needs vs the other three rulesets' real-aspect needs) every
        // Update() — see its own remarks there; the placeholder size here only matters for the
        // very first layout pass before Update() has run once.
        audioAdjustments.Add(chartContainer = new Container
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(1024, 768),
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
            config.BindWith(JukeBoxSetting.PlayHitSounds, playHitSounds);
            config.BindWith(JukeBoxSetting.BackgroundDim, backgroundDim);
            config.BindWith(JukeBoxSetting.BackgroundBlur, backgroundBlur);
            config.BindWith(JukeBoxSetting.ShowStoryboardVideo, showStoryboardVideo);
            config.BindWith(JukeBoxSetting.PlayfieldZoom, playfieldZoom);
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
        playHitSounds.BindValueChanged(_ => updateLazerLayer());
        backgroundDim.BindValueChanged(e => dimScrim.Alpha = (float)e.NewValue, true);
        backgroundBlur.BindValueChanged(e =>
        {
            if (backgroundBlurContainer != null)
                backgroundBlurContainer.BlurSigma = new Vector2((float)e.NewValue * max_blur_sigma);
        }, true);
        showStoryboardVideo.BindValueChanged(e =>
        {
            storyboardLayer.Alpha = e.NewValue ? 1 : 0;
            storyboardAudio.Volume.Value = e.NewValue ? 1 : 0;
            updateBackgroundVisibility();
        }, true);

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

        // A hidden storyboard/video layer can't be covering the background, whatever it claims.
        backgroundSprite.Alpha = showStoryboardVideo.Value && storyboardLayer.ShouldHideBackground ? 0 : 1;
    }

    /// <summary>
    /// One lazer gameplay layer serves both settings: RenderChart shows lazer's rendered gameplay,
    /// PlayHitSounds enables lazer's native hitsound/keysound playback. Hitsounds without chart
    /// keeps the layer alive but invisible — the DrawableRuleset is what plays the samples.
    /// </summary>
    private void updateLazerLayer()
    {
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
            var replay = replays?.ForOsuFile(osuFile);

            if (replay?.Score != null)
                Logger.Log($"Playing back {replay.PlayerName}'s replay on '{Path.GetFileName(osuFile)}' instead of autoplay");

            chartContainer.Add(chartLayer = new LazerChartLayer(chartWorking!, osuFile!, replay?.Score));
        }
        else if (!wantLayer && chartLayer != null)
        {
            chartContainer.Remove(chartLayer, true);
            chartLayer = null;
        }

        if (chartLayer != null)
        {
            chartLayer.Alpha = renderChart.Value ? 1 : 0;
            chartLayer.HitSoundsEnabled.Value = hitSounds;
        }
    }

    protected override void Update()
    {
        base.Update();

        // A storyboard video's decoder fault only surfaces asynchronously — keep the background
        // rule live so a faulted (black) video brings the background back.
        updateBackgroundVisibility();
        reportUnplayableVideo();

        // Catch alone needs chartContainer to actually BE a fixed 1024×768-ish canvas: its
        // PlayfieldAdjustmentContainer positions the catcher/fruits with ABSOLUTE pixel constants
        // calibrated against that exact reference size (see catch_reserved_height's remarks) —
        // give it anything else and the catcher/fruits render at the wrong scale outright.
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
        if (chartMode == 2)
        {
            chartContainer.Size = new Vector2(1024, 768);
            chartContainer.Scale = new Vector2(DrawHeight / catch_reserved_height);
        }
        else
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

            chartContainer.Size = available;
            chartContainer.Scale = Vector2.One;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        backgroundTextures?.Dispose();
    }
}
