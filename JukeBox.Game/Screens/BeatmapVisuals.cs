#nullable enable

using System;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Charts;
using JukeBox.Game.Configuration;
using JukeBox.Game.Storyboard;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Video;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.Screens;

/// <summary>
/// The full visual stack for one beatmap set — dimmed background, storyboard video (if any), the
/// storyboard itself, a configurable background-dim scrim, and (optionally) the gameplay chart and
/// hitsounds of the selected difficulty — all driven off a single shared
/// <see cref="IFrameBasedClock"/> so they stay in lockstep with music playback (pause/seek/rate
/// changes included).
/// </summary>
public partial class BeatmapVisuals : CompositeDrawable
{
    private readonly CachedBeatmapSet set;
    private readonly string? osuFile;
    private readonly IFrameBasedClock playbackClock;

    private TextureStore? backgroundTextures;
    private Sprite? backgroundSprite;
    private TransformStoryboardLayer storyboardLayer = null!;

    private Box dimScrim = null!;
    private Container chartContainer = null!;
    private ChartLayer? chartLayer;
    private HitSoundPlayer? hitSoundPlayer;
    private ChartBeatmap? chartBeatmap;

    // Config-bound (when a config manager is present — test scenes without one keep the
    // defaults). Fields, not locals: config bindables use weak references back to the master.
    private readonly Bindable<bool> renderChart = new();
    private readonly Bindable<bool> playHitSounds = new();
    private readonly BindableDouble backgroundDim = new();

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    // Held so Update() can watch for an async decode fault (Video.IsFaulted only becomes true
    // after construction has already succeeded, on the decoder's own thread) and drop the layer.
    private Container? videoContainer;
    private Video? video;

    /// <summary>
    /// Test-only access to disposal state (JukeBox.Game.Tests has InternalsVisibleTo) —
    /// <see cref="Drawable.IsDisposed"/> itself is protected.
    /// </summary>
    internal bool Disposed => IsDisposed;

    /// <summary>
    /// Test-only access to whether the video layer is currently present (JukeBox.Game.Tests has
    /// InternalsVisibleTo), to assert it gets torn down after a decoder fault.
    /// </summary>
    internal bool HasVideoLayer => videoContainer != null;

    /// <summary>Test-only: whether a chart layer is currently present.</summary>
    internal bool HasChartLayer => chartLayer != null;

    /// <summary>Test-only: whether a hitsound player is currently present.</summary>
    internal bool HasHitSoundPlayer => hitSoundPlayer != null;

    /// <summary>Test-only: current alpha of the background-dim scrim.</summary>
    internal float DimAlpha => dimScrim.Alpha;

    /// <summary>
    /// Test-only: whether our own background <see cref="Sprite"/> is currently visible. Auto-hidden
    /// whenever a video or non-empty storyboard is present (matching osu! stable/lazer behaviour),
    /// so video/storyboard render as the top visual layer instead of fighting the flat background
    /// for prominence. Storyboards that intentionally draw the background image as one of their own
    /// sprites are unaffected — that dedup already happens inside <see cref="TransformStoryboardLayer"/>.
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
                AddInternal(backgroundSprite = new Sprite
                {
                    RelativeSizeAxes = Axes.Both,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    FillMode = FillMode.Fill,
                    Texture = texture,
                    Colour = new Colour4(0.7f, 0.7f, 0.7f, 1f),
                });
            }
        }

        if (set.VideoFile != null)
        {
            try
            {
                double offsetMs = osuFile != null
                    ? OsuFileScanner.Scan(osuFile).VideoOffsetMs
                    : 0;

                video = new Video(set.VideoFile, startAtCurrentTime: false)
                {
                    RelativeSizeAxes = Axes.Both,
                    FillMode = FillMode.Fit,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                };

                videoContainer = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    // The .osu Video event's offset is the song time at which the video's own
                    // frame 0 should appear — i.e. the video's local clock lags playbackClock by
                    // that many ms, hence the negative offset (FramedOffsetClock.CurrentTime ==
                    // Source.CurrentTime + Offset). processSource: false for the same reason as
                    // above — playbackClock is already pumped by PlaybackController.
                    Clock = new FramedOffsetClock(playbackClock, false) { Offset = -offsetMs },
                    Child = video,
                };

                AddInternal(videoContainer);
            }
            catch (Exception e)
            {
                // Decode failure (corrupt/unsupported video) — drop the layer, keep bg + storyboard.
                video = null;
                videoContainer = null;
                Logger.Error(e, $"Failed to load storyboard video '{set.VideoFile}'");
            }
        }

        AddInternal(storyboardLayer = new TransformStoryboardLayer(set, osuFile)
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
        });

        // Real osu! behaviour: a video or a non-empty storyboard renders as the top visual layer,
        // and the flat background image auto-hides behind it (the black Box above still backs the
        // letterboxing). AddInternal above runs the storyboard's own BackgroundDependencyLoader
        // synchronously, so HasObjects already reflects the compiled result here.
        updateBackgroundVisibility();

        // Background-dim scrim: sits between the storyboard/video/background stack and the chart
        // so the chart stays readable. Applies whenever the setting is > 0, even with chart off.
        AddInternal(dimScrim = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Color4.Black,
            Alpha = 0,
        });

        // The chart (and hitsound player) get added/removed inside this fixed container as the
        // settings toggle, so their z-position above the scrim is stable.
        AddInternal(chartContainer = new Container
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(512, 384),
        });

        // Only Mode 0 (osu!std) difficulties are chart-renderable.
        if (osuFile != null)
        {
            try
            {
                if (OsuFileScanner.Scan(osuFile).Mode == 0)
                    chartBeatmap = BeatmapParser.Parse(osuFile);
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Failed to parse chart for '{osuFile}'; chart disabled for this difficulty");
                chartBeatmap = null;
            }
        }

        if (config != null)
        {
            config.BindWith(JukeBoxSetting.RenderChart, renderChart);
            config.BindWith(JukeBoxSetting.PlayHitSounds, playHitSounds);
            config.BindWith(JukeBoxSetting.BackgroundDim, backgroundDim);
        }
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Live-react to settings changes without a rebuild.
        renderChart.BindValueChanged(_ => updateChartLayer(), true);
        playHitSounds.BindValueChanged(_ => updateHitSoundPlayer(), true);
        backgroundDim.BindValueChanged(e => dimScrim.Alpha = (float)e.NewValue, true);
    }

    /// <summary>
    /// Hides our own background sprite whenever a video layer or a non-empty storyboard is
    /// present, so they render as the top visual layer instead of the flat background competing
    /// with them — matching real osu! behaviour. Re-run after the video layer's presence changes
    /// (initial load, and torn down on decoder fault) so a fault with no storyboard restores it.
    /// </summary>
    private void updateBackgroundVisibility()
    {
        if (backgroundSprite == null)
            return;

        backgroundSprite.Alpha = videoContainer != null || storyboardLayer.HasObjects ? 0 : 1;
    }

    private void updateChartLayer()
    {
        if (renderChart.Value && chartBeatmap != null)
        {
            if (chartLayer == null)
                chartContainer.Add(chartLayer = new ChartLayer(chartBeatmap));
        }
        else if (chartLayer != null)
        {
            chartContainer.Remove(chartLayer, true);
            chartLayer = null;
        }
    }

    private void updateHitSoundPlayer()
    {
        if (playHitSounds.Value && chartBeatmap != null)
        {
            if (hitSoundPlayer == null)
                AddInternal(hitSoundPlayer = new HitSoundPlayer(chartBeatmap, set));
        }
        else if (hitSoundPlayer != null)
        {
            RemoveInternal(hitSoundPlayer, true);
            hitSoundPlayer = null;
        }
    }

    protected override void Update()
    {
        base.Update();

        // Video decoding happens on its own thread; construction can succeed and the fault only
        // shows up later via IsFaulted. Catch that here and drop the (now-frozen) layer.
        if (video?.IsFaulted == true && videoContainer != null)
        {
            RemoveInternal(videoContainer, true);
            Logger.Log($"Storyboard video decoder faulted for '{set.VideoFile}', removing layer",
                LoggingTarget.Runtime, LogLevel.Error);
            videoContainer = null;
            video = null;

            // No video anymore — if there's no storyboard either, the background must come back
            // rather than leaving the user staring at pure black.
            updateBackgroundVisibility();
        }

        // Storyboard space is always 480 units tall (640 or 854 wide); scale it uniformly to fit
        // this drawable's height and centre it, same as osu!'s own storyboard letterboxing.
        storyboardLayer.Scale = new Vector2(DrawHeight / 480f);

        // The 512×384 playfield lives in the same 480-tall space, centred — osu!'s standard
        // placement (512×384 within 640×480) scaled to fit with margins.
        chartContainer.Scale = new Vector2(DrawHeight / 480f);
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        backgroundTextures?.Dispose();
    }
}
