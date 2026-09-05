#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Screens;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Timing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// Drives an offline render: it steps a <see cref="BeatmapVisuals"/> stack deterministically off a
/// <see cref="ManualClock"/> + <see cref="FramedClock"/> (the exact frame-stepper pattern
/// <see cref="JukeBox.Game.LazerPlayer.ReplaySimulator"/> uses — advance the manual clock a fixed
/// step, <c>ProcessFrame()</c>, <c>UpdateSubTree()</c>), captures each frame, and feeds the frames
/// to <see cref="FfmpegEncoder"/> alongside the song audio over the chosen range.
///
/// <para>
/// <b>What is real and tested here:</b> the frame plan (<see cref="FramePlan"/> — how many frames and
/// the exact clock time of each), the raw-RGBA extraction (<see cref="ExtractRgba"/>) that turns a
/// captured image into the byte buffer ffmpeg expects at the requested size, and the whole
/// <see cref="EncodeAsync"/> encode loop (start ffmpeg, push every frame from a provider, progress,
/// finalise, and on cancel abort + delete the partial file). Those run in unit tests with a
/// synthetic frame provider and, given a real ffmpeg, produce a real video file.
/// </para>
///
/// <para>
/// <b>Capture:</b> the scene is drawn into an off-screen frame buffer of exactly the requested size
/// (<see cref="OffscreenCapture"/>) and never to the window, so a frame is the render scene's own
/// pixels at the requested resolution — not a resized screenshot of the composited window.
/// <see cref="IsSupported"/> refuses (with a reason) rather than fake output when ffmpeg is missing.
/// </para>
/// </summary>
public sealed class OfflineRenderer
{
    /// <summary>
    /// Whether a render can run at all: false with a clear, user-facing reason when no ffmpeg binary
    /// can be found (the dialog shows the reason instead of starting a render that would produce
    /// nothing). ffmpeg present ⇒ supported.
    /// </summary>
    public static bool IsSupported(out string reason)
    {
        if (!FfmpegEncoder.IsFfmpegAvailable(out _))
        {
            reason = "ffmpeg was not found. Install it (e.g. `brew install ffmpeg`) to render video.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// The exact schedule of frames for a request: how many, and the clock time of each. Pure and
    /// tested — the render loop asks it for frame <c>i</c>'s time and steps the scene clock there.
    /// </summary>
    public readonly struct FramePlan
    {
        private readonly RenderRequest request;

        public FramePlan(RenderRequest request)
        {
            this.request = request;
        }

        public int TotalFrames => request.TotalFrames;

        /// <summary>The playback time (ms) frame <paramref name="index"/> is rendered at, clamped so
        /// the last frame never overshoots the chosen end.</summary>
        public double TimeAt(int index)
            => Math.Min(request.EndMs, request.StartMs + index * request.FrameStepMs);
    }

    /// <summary>
    /// Turns a captured image into the exact raw RGBA byte buffer ffmpeg's rawvideo input expects at
    /// <paramref name="width"/>×<paramref name="height"/> — resizing the source if its size differs.
    /// Pure (no GPU, no host); the encode loop and its tests both go through it.
    /// </summary>
    public static byte[] ExtractRgba(Image<Rgba32> image, int width, int height)
    {
        Image<Rgba32> sized = image;
        bool ownsSized = false;

        if (image.Width != width || image.Height != height)
        {
            sized = image.Clone(ctx => ctx.Resize(width, height));
            ownsSized = true;
        }

        try
        {
            var buffer = new byte[width * height * 4];
            sized.CopyPixelDataTo(buffer);
            return buffer;
        }
        finally
        {
            if (ownsSized)
                sized.Dispose();
        }
    }

    /// <summary>How a render ended.</summary>
    public enum ResultKind
    {
        Completed,
        Cancelled,
        Failed,
    }

    public sealed record RenderResult(ResultKind Kind, string? Error = null);

    /// <summary>
    /// The full encode: start ffmpeg for <paramref name="request"/>, pull every frame in order from
    /// <paramref name="frameProvider"/> (which returns the raw RGBA bytes for a given frame index),
    /// push it in, report <paramref name="onFrame"/> after each, then finalise. On cancellation it
    /// aborts ffmpeg and DELETES the partial output; on an ffmpeg failure it returns the stderr. No
    /// path here silently produces nothing.
    ///
    /// <para>
    /// This is the reusable heart of the driver, deliberately decoupled from where frames come from:
    /// tests drive it with a synthetic provider, the live render drives it from <see cref="Capture"/>.
    /// </para>
    /// </summary>
    public static async Task<RenderResult> EncodeAsync(
        RenderRequest request,
        string? audioPath,
        Func<int, CancellationToken, Task<ReadOnlyMemory<byte>>> frameProvider,
        Action<int, int>? onFrame,
        CancellationToken cancellationToken)
    {
        if (!IsSupported(out string reason))
            return new RenderResult(ResultKind.Failed, reason);

        var plan = new FramePlan(request);
        FfmpegEncoder? encoder = null;

        try
        {
            encoder = FfmpegEncoder.Start(request, audioPath);

            for (int i = 0; i < plan.TotalFrames; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var frame = await frameProvider(i, cancellationToken).ConfigureAwait(false);
                await encoder.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);

                onFrame?.Invoke(i + 1, plan.TotalFrames);
            }

            bool ok = await encoder.CompleteAsync(cancellationToken).ConfigureAwait(false);

            if (!ok)
            {
                deletePartial(request.Path);
                return new RenderResult(ResultKind.Failed, encoder.LastError ?? "ffmpeg exited with an error.");
            }

            return new RenderResult(ResultKind.Completed);
        }
        catch (OperationCanceledException)
        {
            encoder?.Abort();
            deletePartial(request.Path);
            return new RenderResult(ResultKind.Cancelled);
        }
        catch (Exception e)
        {
            encoder?.Abort();
            deletePartial(request.Path);
            return new RenderResult(ResultKind.Failed, e.Message);
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    private static void deletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A file we can't remove is not worth failing the (already-ending) render over.
        }
    }

    /// <summary>
    /// The deterministic, off-clock visual stack driven for the render: a <see cref="BeatmapVisuals"/>
    /// built off a <see cref="ManualClock"/> wrapped in a <see cref="FramedClock"/>, sized to the
    /// render resolution, stepped a fixed <see cref="RenderRequest.FrameStepMs"/> at a time exactly as
    /// <see cref="JukeBox.Game.LazerPlayer.ReplaySimulator"/> steps its off-screen simulations. Add it
    /// to the game tree (so it inherits the app's DI — skin, config, replays, playback controller)
    /// and call <see cref="StepTo"/> per frame between captures.
    /// </summary>
    public sealed partial class RenderScene : CompositeDrawable
    {
        private readonly CachedBeatmapSet set;
        private readonly string? osuFile;
        private readonly ManualClock manual = new ManualClock();
        private readonly FramedClock framed;
        private readonly OffscreenCapture capture;

        private BeatmapVisuals visuals = null!;

        public RenderScene(CachedBeatmapSet set, RenderRequest request, string? osuFile)
        {
            this.set = set;
            this.osuFile = osuFile;
            framed = new FramedClock(manual);

            // The whole stack lives inside an off-screen capture: it is drawn into a buffer of the
            // requested pixel size and never to the window, so the user sees nothing of it and the
            // frames are exactly the requested resolution whatever the window is. AlwaysPresent so
            // the framework keeps updating it (an absent drawable never advances).
            capture = new OffscreenCapture(request.Width, request.Height);
            AutoSizeAxes = Axes.Both;
            AlwaysPresent = true;

            manual.CurrentTime = request.StartMs;
        }

        // Never in the way of the user's mouse while a render runs.
        public override bool ReceivePositionalInputAt(osuTK.Vector2 screenSpacePos) => false;

        /// <summary>
        /// The scene's combine simulates its own copy of the replays, and a combine publishes its
        /// preload into the <see cref="Replays.PreloadProgressTracker"/> it resolves. Handing it a
        /// PRIVATE tracker keeps that off the app's own playback bar — otherwise the user watches the
        /// buffer fill and "Simulating replays…" restart for a scene they can't see.
        /// </summary>
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            dependencies.Cache(new Replays.PreloadProgressTracker());
            return dependencies;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            capture.Child = visuals = new BeatmapVisuals(set, framed, osuFile)
            {
                RelativeSizeAxes = Axes.Both,
            };

            AddInternal(capture);
        }

        /// <summary>Reads back the frame for the most recent <see cref="StepTo"/> — call after the
        /// update frame that stepped has ended, so the draw nodes it generated are what gets drawn.</summary>
        public Task<Image<Rgba32>> CaptureAsync(GameHost host) => capture.CaptureAsync(host);

        /// <summary>Advances the whole stack to <paramref name="timeMs"/> and lets it settle — the
        /// ReplaySimulator.advance() pattern: set the manual clock, pump the framed clock, update the
        /// subtree so every layer processes the new time.</summary>
        public void StepTo(double timeMs)
        {
            manual.CurrentTime = timeMs;
            framed.ProcessFrame();
            UpdateSubTree();
        }

        /// <summary>Whether the visual stack has finished loading and is ready to be stepped.</summary>
        public bool Ready => visuals.IsLoaded;

        internal BeatmapVisuals Visuals => visuals;
    }
}
