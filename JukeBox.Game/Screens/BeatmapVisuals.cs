#nullable enable

using System;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Storyboard;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Video;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osuTK;

namespace JukeBox.Game.Screens;

/// <summary>
/// The full visual stack for one beatmap set — dimmed background, storyboard video (if any) and
/// the storyboard itself — all driven off a single shared <see cref="IFrameBasedClock"/> so they
/// stay in lockstep with music playback (pause/seek/rate changes included).
/// </summary>
public partial class BeatmapVisuals : CompositeDrawable
{
    private readonly CachedBeatmapSet set;
    private readonly IFrameBasedClock playbackClock;

    private TextureStore? backgroundTextures;
    private TransformStoryboardLayer storyboardLayer = null!;

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

    public BeatmapVisuals(CachedBeatmapSet set, IFrameBasedClock playbackClock)
    {
        this.set = set;
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

        if (set.BackgroundFile != null)
        {
            backgroundTextures = new TextureStore(host.Renderer,
                host.CreateTextureLoaderStore(new StorageBackedResourceStore(new NativeStorage(set.Directory, host))),
                useAtlas: false, scaleAdjust: 1);

            string relative = Path.GetRelativePath(set.Directory, set.BackgroundFile).Replace('\\', '/');
            var texture = backgroundTextures.Get(relative);

            if (texture != null)
            {
                AddInternal(new Sprite
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
                double offsetMs = set.PreferredOsuFile != null
                    ? OsuFileScanner.Scan(set.PreferredOsuFile).VideoOffsetMs
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

        AddInternal(storyboardLayer = new TransformStoryboardLayer(set)
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
        });
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
        }

        // Storyboard space is always 480 units tall (640 or 854 wide); scale it uniformly to fit
        // this drawable's height and centre it, same as osu!'s own storyboard letterboxing.
        storyboardLayer.Scale = new Vector2(DrawHeight / 480f);
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        backgroundTextures?.Dispose();
    }
}
