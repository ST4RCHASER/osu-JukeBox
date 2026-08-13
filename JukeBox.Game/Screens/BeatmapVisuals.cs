#nullable enable

using System;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
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
    private LazerChartLayer? chartLayer;

    // The decoded difficulty backing the lazer gameplay layer, or null when charting is
    // unavailable for this difficulty (no diff / unknown mode / zero objects / parse failure).
    private osu.Game.Beatmaps.WorkingBeatmap? chartWorking;

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

    // Video's own per-frame out-of-sync check (osu.Framework.Graphics.Video.Video.Update) issues
    // a fresh decoder seek every time PlaybackPosition has drifted from the newest decoded frame
    // by more than ~2.5s. When a set is (re)built with playbackClock already well past 0 — the
    // normal case here: radio songs start mid-track and diff switches land mid-song — the very
    // first frames the decoder produces are near its own 0, so that first out-of-sync check fires
    // immediately. If PlaybackPosition keeps climbing on every subsequent Update() while the
    // decoder is still mid-catch-up (decoding forward through the GOP to reach the seek target),
    // each new frame re-seeks to a slightly-later target before the previous seek ever converges —
    // a moving target the decoder can never land on, observed in production as a permanent black
    // video stuck re-seeking every frame.
    //
    // Video.IsPlaying=false holds PlaybackPosition at a fixed value, giving the decoder a stable
    // target instead of a moving one — once it's confirmed caught up (FramesProcessed > 0: a
    // decoded frame actually passed Video.checkNextFrameValid, i.e. landed at-or-before that fixed
    // target), it's safe to resume live tracking: the decoder is warm and any further drift is
    // ordinary steady-state playback, which osu.Framework's own per-frame check already handles
    // fine (confirmed empirically across several real cached maps — never storms once already
    // caught up, only during that first cold catch-up). But two more things go wrong if the freeze
    // is otherwise left exactly as simple as "hold it, wait for a frame, unpause":
    //
    //  1. AnimationClockComposite.Update() still calls consumeClockTime() every frame while paused
    //     (updating its internal lastConsumedTime bookkeeping) — it just doesn't *apply* the
    //     elapsed delta to PlaybackPosition. So real time keeps passing while frozen, and blindly
    //     flipping IsPlaying back on resumes tracking from the STALE frozen value with zero
    //     catch-up: the entire freeze duration becomes a permanent lag baked into PlaybackPosition
    //     forever (Video's own out-of-sync check only ever compares against its own
    //     PlaybackPosition, never against the true clock, so a lag under lenience_before_seek never
    //     self-corrects). Fix: snap PlaybackPosition to video.Time.Current (the real target — the
    //     same clock startAtCurrentTime:false's LoadComplete seed reads from) right before
    //     unpausing, cancelling the lag out in one shot.
    //
    //  2. Video.checkNextFrameValid requires frame.Time <= PlaybackPosition to display a decoded
    //     frame. With PlaybackPosition held perfectly still at its initial seed, a landed frame
    //     whose timestamp overshoots that exact value by even a few ms (unavoidable — decode lands
    //     on frame boundaries, not our arbitrary seeded millisecond) never satisfies that check, so
    //     FramesProcessed never increments and "wait for a frame" never fires — even though the
    //     decoder is sitting there ready. Confirmed empirically: a real cached .avi map sat at
    //     FramesProcessed=0 for the entire 5s timeout despite its own out-of-sync log showing the
    //     decoder had reached the target within a fraction of a second. Fix: nudge PlaybackPosition
    //     forward by a small fixed margin (video_warm_up_nudge_ms, roughly a frame's worth) every
    //     video_warm_up_nudge_interval_ms while still waiting — enough to clear a boundary-hair
    //     overshoot on an already-decoded frame, nowhere near the lenience_before_seek(2500ms)
    //     needed to provoke a fresh decoder seek, and utterly unlike the ~16ms-cadence, unbounded
    //     climb that produced the original storm.
    //
    // Bounded by videoWarmUpDeadline regardless, so a genuinely stuck decoder (as opposed to one
    // merely catching up or clearing the boundary hair above) still starts tracking eventually
    // instead of being held back forever.
    private bool videoWarmedUp = true;
    private double videoWarmUpDeadline;
    private double videoNextWarmUpNudge;

    private const double video_warm_up_timeout_ms = 5000;
    private const double video_warm_up_nudge_interval_ms = 150;
    private const double video_warm_up_nudge_ms = 100;

    /// <summary>
    /// Test-only access to disposal state (JukeBox.Game.Tests has InternalsVisibleTo) —
    /// <see cref="Drawable.IsDisposed"/> itself is protected.
    /// </summary>
    internal bool Disposed => IsDisposed;

    /// <summary>Test-only: frames actually rendered by the video layer, or null with no video.
    /// Only increments once a decoded frame within sync of the playback position is displayed
    /// (see <see cref="Video.FramesProcessed"/>) — the definitive signal that the video isn't
    /// stuck re-seeking. Used to regression-test the mid-song seek-storm fix.</summary>
    internal int? VideoFramesProcessed => video?.FramesProcessed;

    /// <summary>
    /// Test-only access to whether the video layer is currently present (JukeBox.Game.Tests has
    /// InternalsVisibleTo), to assert it gets torn down after a decoder fault.
    /// </summary>
    internal bool HasVideoLayer => videoContainer != null;

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
                    // See videoWarmedUp: held paused until the decoder proves it has caught up to
                    // the seeded start position, so the sync-seek target doesn't move out from
                    // under it while it's still decoding forward to reach that position.
                    IsPlaying = false,
                };
                videoWarmedUp = false;
                videoWarmUpDeadline = Clock.CurrentTime + video_warm_up_timeout_ms;
                videoNextWarmUpNudge = Clock.CurrentTime + video_warm_up_nudge_interval_ms;

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
        }

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

        // See videoWarmedUp: FramesProcessed > 0 is the real confirmation the decoder has caught
        // up to the frozen target (a frame genuinely passed Video's frame.Time <= PlaybackPosition
        // check) — accept it, cancel the freeze-duration lag with one snap to the live target, and
        // resume real-time tracking. Otherwise nudge the frozen target forward a little so a
        // boundary-hair overshoot on an already-decoded frame doesn't deadlock the wait, or give up
        // waiting once the bounded deadline passes regardless.
        if (!videoWarmedUp && video != null)
        {
            bool timedOut = Clock.CurrentTime >= videoWarmUpDeadline;

            if (video.FramesProcessed > 0 || timedOut)
            {
                video.PlaybackPosition = video.Time.Current;
                video.IsPlaying = true;
                videoWarmedUp = true;
            }
            else if (Clock.CurrentTime >= videoNextWarmUpNudge)
            {
                video.PlaybackPosition += video_warm_up_nudge_ms;
                videoNextWarmUpNudge = Clock.CurrentTime + video_warm_up_nudge_interval_ms;
            }
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
