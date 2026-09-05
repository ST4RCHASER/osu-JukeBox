#nullable enable

using System;
using System.IO;
using System.Linq;
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
                    hitSoundPath: null,
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
                hitSoundPath: null,
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

        [Test]
        public async Task EncodeAsyncMixesAHitSoundTrackIntoTheOutput()
        {
            if (!FfmpegEncoder.IsFfmpegAvailable(out _))
                Assert.Ignore("ffmpeg not installed on this machine");

            string outputPath = Path.Combine(Path.GetTempPath(), $"jukebox-render-hs-{Guid.NewGuid():N}.mp4");
            string hitSoundPath = Path.Combine(Path.GetTempPath(), $"jukebox-render-hs-{Guid.NewGuid():N}.wav");
            var req = request(outputPath);

            // A real (tiny) hitsound WAV through the same mixer the app uses, so the amix filter
            // graph the args build is proven against a real ffmpeg, not just string-asserted.
            var sample = new osu.Game.Audio.HitSampleInfo(osu.Game.Audio.HitSampleInfo.HIT_NORMAL);
            var schedule = new[] { new HitSoundSchedule.Entry(200, new[] { sample }) };
            Assert.That(HitSoundTrack.MixToWavFile(schedule, _ => new float[] { 0.5f, 0.5f, 0.5f, 0.5f }, req.StartMs, req.EndMs, 1, hitSoundPath), Is.True);

            var frame = new byte[req.Width * req.Height * 4];

            try
            {
                var result = await OfflineRenderer.EncodeAsync(
                    req,
                    audioPath: null,
                    hitSoundPath: hitSoundPath,
                    frameProvider: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(frame),
                    onFrame: null,
                    cancellationToken: CancellationToken.None);

                Assert.That(result.Kind, Is.EqualTo(OfflineRenderer.ResultKind.Completed), result.Error);
                Assert.That(File.Exists(outputPath), Is.True);
                Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(0));
            }
            finally
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                if (File.Exists(hitSoundPath))
                    File.Delete(hitSoundPath);
            }
        }

        [Test]
        public async Task TheMixNeverDucksTheMusicUnderHitSoundsAndNeverClips()
        {
            if (!FfmpegEncoder.IsFfmpegAvailable(out _))
                Assert.Ignore("ffmpeg not installed on this machine");

            // "Music": a steady near-full-scale 440Hz tone, like real mastered audio. "Hitsounds":
            // two loud 200ms 1kHz bursts. A straight sum peaks at 1.89 — without the constant
            // headroom the output overshoots full scale exactly at the hits and every player clamps
            // it there, which is the audible "music ducks under hitsounds" bug; with any ADAPTIVE
            // gain (amix normalisation / dropout ramps) the tone's level would wobble around them.
            const int rate = HitSoundTrack.SAMPLE_RATE;
            const float tone_amp = 0.9f;
            const float burst_amp = 0.99f;
            const double headroom = 0.5;

            string tonePath = Path.Combine(Path.GetTempPath(), $"jukebox-mix-tone-{Guid.NewGuid():N}.wav");
            string burstPath = Path.Combine(Path.GetTempPath(), $"jukebox-mix-hits-{Guid.NewGuid():N}.wav");
            string outputPath = Path.Combine(Path.GetTempPath(), $"jukebox-mix-out-{Guid.NewGuid():N}.mp4");

            static float[] sine(double frequency, double seconds, float amplitude)
            {
                var pcm = new float[(int)(rate * seconds) * 2];

                for (int i = 0; i < pcm.Length / 2; i++)
                {
                    float v = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / rate);
                    pcm[2 * i] = v;
                    pcm[2 * i + 1] = v;
                }

                return pcm;
            }

            var anySample = new osu.Game.Audio.HitSampleInfo(osu.Game.Audio.HitSampleInfo.HIT_NORMAL);

            // Author both inputs through the same WAV writer the app uses.
            Assert.That(HitSoundTrack.MixToWavFile(
                new[] { new HitSoundSchedule.Entry(0, new[] { anySample }) },
                _ => sine(440, 3.0, tone_amp), 0, 3000, 1.0, tonePath), Is.True);

            Assert.That(HitSoundTrack.MixToWavFile(
                new[] { new HitSoundSchedule.Entry(1000, new[] { anySample }), new HitSoundSchedule.Entry(2000, new[] { anySample }) },
                _ => sine(1000, 0.2, burst_amp), 0, 3000, 1.0, burstPath), Is.True);

            var frame = new byte[16 * 16 * 4];

            try
            {
                var result = await OfflineRenderer.EncodeAsync(
                    new RenderRequest("mp4", 16, 16, 10, outputPath, 0, 3000, 128),
                    audioPath: tonePath,
                    hitSoundPath: burstPath,
                    frameProvider: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(frame),
                    onFrame: null,
                    cancellationToken: CancellationToken.None);

                Assert.That(result.Kind, Is.EqualTo(OfflineRenderer.ResultKind.Completed), result.Error);

                float[]? mixed = HitSoundTrack.DecodePcm(outputPath);
                Assert.That(mixed, Is.Not.Null);
                Assert.That(mixed!.Length, Is.GreaterThanOrEqualTo((int)(2.9 * rate) * 2), "the decoded mix must cover the whole range");

                static double rms(float[] pcm, double fromSeconds, double toSeconds)
                {
                    int from = (int)(fromSeconds * rate) * 2, to = (int)(toSeconds * rate) * 2;
                    double sum = 0;
                    for (int i = from; i < to; i++)
                        sum += (double)pcm[i] * pcm[i];
                    return Math.Sqrt(sum / (to - from));
                }

                // Never a sample above full scale — the overshoot IS what players clamp into a duck.
                Assert.That(mixed!.Max(Math.Abs), Is.LessThanOrEqualTo(1f));

                // The tone holds its absolute level (amp/√2, halved by the fixed headroom) and stays
                // CONSTANT across windows before, between and after the hits — any adaptive gain
                // shows up here as a wobble.
                double expectedTone = tone_amp / Math.Sqrt(2) * headroom;
                double[] quiet = { rms(mixed, 0.3, 0.9), rms(mixed, 1.4, 1.9), rms(mixed, 2.4, 2.9) };

                foreach (double level in quiet)
                    Assert.That(level, Is.EqualTo(expectedTone).Within(0.1 * expectedTone), "tone level must sit at the fixed-headroom sum level");

                Assert.That(quiet.Max() - quiet.Min(), Is.LessThanOrEqualTo(0.05 * quiet[0]), "tone level must not wobble around the hits");

                // While a hit sounds, energies ADD — the straight-sum signature. An adaptive mix
                // (or a clamped overshoot) lands measurably below this.
                double expectedDuringHit = Math.Sqrt(expectedTone * expectedTone + Math.Pow(burst_amp / Math.Sqrt(2) * headroom, 2));
                Assert.That(rms(mixed, 1.05, 1.15), Is.EqualTo(expectedDuringHit).Within(0.15 * expectedDuringHit));
            }
            finally
            {
                foreach (string file in new[] { tonePath, burstPath, outputPath })
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }
            }
        }

        [Test]
        public void TheRenderSceneIsMutedToTheSpeakers()
        {
            // While a render runs the scene's real gameplay lives in the game tree; its whole
            // subtree hangs inside a zero-volume audio wrapper so none of it reaches the speakers.
            var scene = new OfflineRenderer.RenderScene(new JukeBox.Game.Beatmaps.CachedBeatmapSet(), request("/tmp/x.mp4"), null);

            Assert.That(scene.LiveAudio.Volume.Value, Is.Zero);
            Assert.That(scene.LiveAudio.Child, Is.SameAs(scene.Capture), "the capture must live inside the muted wrapper");
        }

        [Test]
        public void TheSceneLaysOutAtLogical1080pWhateverTheOutputResolution()
        {
            // 4K is a 2x-scaled 1080p layout: identical composition, only sharper — never the same
            // pixel-sized UI marooned in a frame twice the size.
            var at4K = new OfflineRenderer.RenderScene(new JukeBox.Game.Beatmaps.CachedBeatmapSet(), request("/tmp/x.mp4", w: 3840, h: 2160), null);

            Assert.That(at4K.LogicalLayout.Scale, Is.EqualTo(new osuTK.Vector2(2)));
            Assert.That(at4K.LogicalLayout.Size, Is.EqualTo(new osuTK.Vector2(1920, 1080)));
            Assert.That(at4K.Capture.Child, Is.SameAs(at4K.LogicalLayout), "the logical layout must be what the capture draws");

            // 1080p is the identity case — the layout IS the buffer.
            var at1080 = new OfflineRenderer.RenderScene(new JukeBox.Game.Beatmaps.CachedBeatmapSet(), request("/tmp/x.mp4", w: 1920, h: 1080), null);

            Assert.That(at1080.LogicalLayout.Scale, Is.EqualTo(new osuTK.Vector2(1)));
            Assert.That(at1080.LogicalLayout.Size, Is.EqualTo(new osuTK.Vector2(1920, 1080)));

            // A vertical render keeps the 1080 logical HEIGHT; the width follows the aspect.
            var vertical = new OfflineRenderer.RenderScene(new JukeBox.Game.Beatmaps.CachedBeatmapSet(), request("/tmp/x.mp4", w: 1080, h: 1920), null);

            Assert.That(vertical.LogicalLayout.Size.Y, Is.EqualTo(1080));
            Assert.That(vertical.LogicalLayout.Scale.X, Is.EqualTo(1920f / 1080).Within(1e-5));
            Assert.That(vertical.LogicalLayout.Size.X * vertical.LogicalLayout.Scale.X, Is.EqualTo(1080).Within(1e-2), "scaled width must land exactly on the buffer width");
        }
    }
}
