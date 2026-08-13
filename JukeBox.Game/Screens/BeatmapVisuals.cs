#nullable enable

using System;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
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
    private readonly string? osuFile;
    private readonly IFrameBasedClock playbackClock;

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

    // Config-bound (when a config manager is present — test scenes without one keep the
    // defaults). Fields, not locals: config bindables use weak references back to the master.
    private readonly Bindable<bool> renderChart = new();
    private readonly Bindable<bool> playHitSounds = new();
    private readonly BindableDouble backgroundDim = new();
    private readonly BindableDouble backgroundBlur = new();
    private readonly Bindable<bool> showStoryboardVideo = new(true);
    private readonly IBindable<JukeBoxSkin> effectiveSkin = new Bindable<JukeBoxSkin>();

    // Lazer's own background-blur scale: setting 0..1 maps to a gaussian sigma of 0..25.
    private const float max_blur_sigma = 25;

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    [Resolved(canBeNull: true)]
    private SkinSelection? skinSelection { get; set; }

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
    /// Test-only: whether a WORKING video is present (a storyboard Video event whose decoder
    /// hasn't faulted). Faulted videos render nothing and stop counting for background auto-hide.
    /// </summary>
    internal bool HasVideoLayer => storyboardLayer.HasVideo && !storyboardLayer.VideoFaulted;

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
    /// Test-only: whether our own background <see cref="Sprite"/> is currently visible. Auto-hidden
    /// when the storyboard explicitly replaces the background (draws it as one of its own sprites —
    /// lazer's <c>Storyboard.ReplacesBackground</c>) or a working video plays behind it, matching
    /// real osu! behaviour. Plain sprite storyboards leave the background visible underneath.
    /// </summary>
    internal bool BackgroundVisible => backgroundSprite is { Alpha: > 0 };

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

        // PlaybackController pumps this clock's ProcessFrame() itself every Update() — leaving
        // ProcessCustomClock at its default (true) would pump it a second time here and corrupt
        // its ElapsedFrameTime bookkeeping.
        Clock = playbackClock;
        ProcessCustomClock = false;

        // Plain black backdrop, always present and always fully opaque — video (FillMode.Fit) and
        // the storyboard (uniform-scaled, possibly narrower than widescreen) don't necessarily
        // cover the full width/height, so without this the letterboxing would show whatever's
        // behind this drawable instead of black, same as osu! stable/lazer's own letterboxing.
        AddInternal(new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Color4.Black,
        });

        if (set.BackgroundFile != null)
        {
            backgroundTextures = new TextureStore(host.Renderer,
                host.CreateTextureLoaderStore(new StorageBackedResourceStore(new NativeStorage(set.Directory, host))),
                useAtlas: false, scaleAdjust: 1);

            string relative = Path.GetRelativePath(set.Directory, set.BackgroundFile).Replace('\\', '/');
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
        // settings toggle, so their z-position above the scrim is stable.
        audioAdjustments.Add(chartContainer = new Container
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(512, 384),
        });

        // All four rulesets (0 std / 1 taiko / 2 catch / 3 mania) render through lazer's real
        // gameplay renderer. Every skip reason is recorded and logged — a silently-absent chart
        // cost a real debugging round (storyboard-heavy sets are often mania/taiko-only, which
        // used to fall through here without a trace).
        if (osuFile == null)
        {
            markChartUnavailable($"set {set.SetId} has no .osu difficulty to chart");
        }
        else
        {
            try
            {
                int mode = OsuFileScanner.Scan(osuFile).Mode;

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
        }

        if (skinSelection != null)
            effectiveSkin.BindTo(skinSelection.Effective);

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

        // A skin choice change (dropdown flip, or Random's per-song re-roll) rebuilds the chart
        // layer — the skin chain is constructed once per LazerChartLayer, so a rebuild is the
        // application mechanism (documented; the ruleset checkboxes by contrast apply live).
        effectiveSkin.BindValueChanged(_ =>
        {
            if (chartLayer == null)
                return;

            chartContainer.Remove(chartLayer, true);
            chartLayer = null;
            updateLazerLayer();
        });
    }

    /// <summary>
    /// Hides our own background sprite when the storyboard replaces it or a working video plays,
    /// matching real osu! behaviour (see <see cref="LazerStoryboardLayer.ShouldHideBackground"/>).
    /// Re-evaluated every <see cref="Update"/>: a video decoder fault surfaces asynchronously on
    /// the decoder's own thread, and the background must come back rather than leaving the user
    /// staring at pure black.
    /// </summary>
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
        bool wantLayer = (renderChart.Value || playHitSounds.Value) && chartWorking != null && osuFile != null;

        if (wantLayer && chartLayer == null)
            chartContainer.Add(chartLayer = new LazerChartLayer(chartWorking!, osuFile!));
        else if (!wantLayer && chartLayer != null)
        {
            chartContainer.Remove(chartLayer, true);
            chartLayer = null;
        }

        if (chartLayer != null)
        {
            chartLayer.Alpha = renderChart.Value ? 1 : 0;
            chartLayer.HitSoundsEnabled.Value = playHitSounds.Value;
        }
    }

    protected override void Update()
    {
        base.Update();

        // A storyboard video's decoder fault only surfaces asynchronously — keep the background
        // rule live so a faulted (black) video brings the background back.
        updateBackgroundVisibility();

        // The 512×384 playfield lives in the storyboard's 480-tall space, centred — osu!'s
        // standard placement (512×384 within 640×480) scaled to fit with margins. (The lazer
        // storyboard layer performs the equivalent scaling internally via its own DrawScale.)
        chartContainer.Scale = new Vector2(DrawHeight / 480f);
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        backgroundTextures?.Dispose();
    }
}
