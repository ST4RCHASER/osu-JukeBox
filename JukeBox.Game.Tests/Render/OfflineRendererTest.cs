#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.UI.Render;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JukeBox.Game.Tests.Render
{
    /// <summary>
    /// The driver's testable core: the frame plan (count and per-frame clock time), the raw-RGBA
    /// extraction that feeds ffmpeg, and the encode loop itself — the last driven end-to-end through
    /// a real ffmpeg with a synthetic frame provider so a genuine (tiny) video file is produced,
    /// proving the pipeline rather than restating it. The GPU frame capture is out of scope here (see
    /// <see cref="OfflineRenderer"/>'s remarks); everything below runs without a game host.
    /// </summary>
    [TestFixture]
    public class OfflineRendererTest
    {
        private static RenderRequest request(string path, int w = 16, int h = 16, int fps = 2, double startMs = 0, double endMs = 1000)
            => new RenderRequest("mp4", w, h, fps, path, startMs, endMs, 128);

        [Test]
        public void FramePlanCountsFramesOverTheRangeAtFps()
        {
            // 0..2000ms at 30fps → 60 frames.
            var plan = new OfflineRenderer.FramePlan(request("/tmp/x.mp4", fps: 30, startMs: 0, endMs: 2000));
            Assert.That(plan.TotalFrames, Is.EqualTo(60));
        }

        [Test]
        public void FramePlanTimesStepByOneFrameAndNeverOvershootTheEnd()
        {
            var req = request("/tmp/x.mp4", fps: 10, startMs: 5_000, endMs: 6_000); // 10 frames, 100ms step
            var plan = new OfflineRenderer.FramePlan(req);

            Assert.That(plan.TimeAt(0), Is.EqualTo(5_000));
            Assert.That(plan.TimeAt(1), Is.EqualTo(5_100));
            Assert.That(plan.TimeAt(5), Is.EqualTo(5_500));

            // Well past the last index, the time is clamped to the end rather than running away.
            Assert.That(plan.TimeAt(1000), Is.EqualTo(6_000));
        }

        [Test]
        public void ExtractRgbaReturnsExactlyWidthTimesHeightTimesFourBytes()
        {
            using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30, 255));
            byte[] bytes = OfflineRenderer.ExtractRgba(image, 2, 2);

            Assert.That(bytes.Length, Is.EqualTo(2 * 2 * 4));
            // First pixel is the fill colour, RGBA order.
            Assert.That(bytes[0], Is.EqualTo(10));
            Assert.That(bytes[1], Is.EqualTo(20));
            Assert.That(bytes[2], Is.EqualTo(30));
            Assert.That(bytes[3], Is.EqualTo(255));
        }

        [Test]
        public void ExtractRgbaResizesASourceOfADifferentSize()
        {
            using var image = new Image<Rgba32>(2, 2, new Rgba32(1, 2, 3, 255));
            byte[] bytes = OfflineRenderer.ExtractRgba(image, 8, 8);
            Assert.That(bytes.Length, Is.EqualTo(8 * 8 * 4));
        }

        [Test]
        public void IsSupportedReportsWhetherFfmpegIsPresent()
        {
            bool supported = OfflineRenderer.IsSupported(out string reason);

            if (FfmpegEncoder.IsFfmpegAvailable(out _))
            {
                Assert.That(supported, Is.True);
                Assert.That(reason, Is.Empty);
            }
            else
            {
                Assert.That(supported, Is.False);
                Assert.That(reason, Does.Contain("ffmpeg"));
            }
        }

        [Test]
        public async Task EncodeAsyncProducesARealFileFromSyntheticFrames()
        {
            if (!FfmpegEncoder.IsFfmpegAvailable(out _))
                Assert.Ignore("ffmpeg not installed on this machine");

            string outputPath = Path.Combine(Path.GetTempPath(), $"jukebox-render-test-{Guid.NewGuid():N}.mp4");
            var req = request(outputPath);

            // A solid RGBA frame per index — the encode loop and ffmpeg do the rest.
            var frame = new byte[req.Width * req.Height * 4];
            Array.Fill(frame, (byte)128);

            int lastDone = 0, lastTotal = 0;

            try
            {
                var result = await OfflineRenderer.EncodeAsync(
                    req,
                    audioPath: null,
                    frameProvider: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(frame),
                    onFrame: (done, total) => { lastDone = done; lastTotal = total; },
                    cancellationToken: CancellationToken.None);

                Assert.That(result.Kind, Is.EqualTo(OfflineRenderer.ResultKind.Completed), result.Error);
                Assert.That(lastTotal, Is.EqualTo(req.TotalFrames));
                Assert.That(lastDone, Is.EqualTo(req.TotalFrames));
                Assert.That(File.Exists(outputPath), Is.True);
                Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(0));
            }
            finally
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
        }

        [Test]
        public async Task EncodeAsyncCancellingDeletesThePartialOutput()
        {
            if (!FfmpegEncoder.IsFfmpegAvailable(out _))
                Assert.Ignore("ffmpeg not installed on this machine");

            string outputPath = Path.Combine(Path.GetTempPath(), $"jukebox-render-cancel-{Guid.NewGuid():N}.mp4");
            var req = request(outputPath, fps: 30, startMs: 0, endMs: 10_000); // many frames, so we can cancel mid-way

            var frame = new byte[req.Width * req.Height * 4];
            using var cts = new CancellationTokenSource();

            var result = await OfflineRenderer.EncodeAsync(
                req,
                audioPath: null,
                frameProvider: (index, _) =>
                {
                    if (index >= 3)
                        cts.Cancel();
                    return Task.FromResult<ReadOnlyMemory<byte>>(frame);
                },
                onFrame: null,
                cancellationToken: cts.Token);

            Assert.That(result.Kind, Is.EqualTo(OfflineRenderer.ResultKind.Cancelled));
            Assert.That(File.Exists(outputPath), Is.False, "the partial file should have been deleted");
        }
    }
}
