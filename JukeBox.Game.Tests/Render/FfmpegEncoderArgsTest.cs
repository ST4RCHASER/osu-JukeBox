#nullable enable

using System;
using System.Linq;
using JukeBox.Game.UI.Render;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Render
{
    /// <summary>
    /// The exact ffmpeg argument vector <see cref="FfmpegEncoder.BuildArgs"/> produces per format —
    /// the codec, container, input size, frame rate, audio bitrate and the audio mux — asserted
    /// without ever launching ffmpeg. These are the flags a wrong edit would silently corrupt the
    /// output with, so each is pinned to a value rather than "present".
    /// </summary>
    [TestFixture]
    public class FfmpegEncoderArgsTest
    {
        private static RenderRequest request(string format, int w = 1920, int h = 1080, int fps = 60, int audio = 192)
            => new RenderRequest(format, w, h, fps, "/tmp/out." + FfmpegEncoder.ExtensionFor(format), 10_000, 40_000, audio);

        /// <summary>Value that follows the first occurrence of <paramref name="flag"/> in the argv.</summary>
        private static string valueAfter(string[] args, string flag)
        {
            int i = Array.IndexOf(args, flag);
            Assert.That(i, Is.GreaterThanOrEqualTo(0), $"expected flag {flag}");
            Assert.That(i + 1, Is.LessThan(args.Length), $"expected a value after {flag}");
            return args[i + 1];
        }

        [Test]
        public void Mp4UsesH264AndAacAtTheRequestedSizeRateAndBitrate()
        {
            var args = FfmpegEncoder.BuildArgs(request("mp4", 1920, 1080, 60, 192), "/music/song.mp3");

            Assert.That(valueAfter(args, "-c:v"), Is.EqualTo("libx264"));
            Assert.That(valueAfter(args, "-c:a"), Is.EqualTo("aac"));
            Assert.That(valueAfter(args, "-video_size"), Is.EqualTo("1920x1080"));
            Assert.That(valueAfter(args, "-framerate"), Is.EqualTo("60"));
            Assert.That(valueAfter(args, "-b:a"), Is.EqualTo("192k"));
            Assert.That(valueAfter(args, "-pixel_format"), Is.EqualTo("rgba"));
            Assert.That(valueAfter(args, "-pix_fmt"), Is.EqualTo("yuv420p"));

            // Output path is the final argument.
            Assert.That(args[^1], Is.EqualTo("/tmp/out.mp4"));
        }

        [Test]
        public void WebmUsesVp9AndOpus()
        {
            var args = FfmpegEncoder.BuildArgs(request("webm", 1080, 1920, 30, 128), "/music/song.ogg");

            Assert.That(valueAfter(args, "-c:v"), Is.EqualTo("libvpx-vp9"));
            Assert.That(valueAfter(args, "-c:a"), Is.EqualTo("libopus"));
            Assert.That(valueAfter(args, "-video_size"), Is.EqualTo("1080x1920"));
            Assert.That(valueAfter(args, "-framerate"), Is.EqualTo("30"));
            Assert.That(valueAfter(args, "-b:a"), Is.EqualTo("128k"));

            // VP9 constant-quality needs -b:v 0.
            Assert.That(valueAfter(args, "-b:v"), Is.EqualTo("0"));
        }

        [Test]
        public void MovUsesH264InAMovContainer()
        {
            var args = FfmpegEncoder.BuildArgs(request("mov"), "/music/song.wav");

            Assert.That(valueAfter(args, "-c:v"), Is.EqualTo("libx264"));
            Assert.That(valueAfter(args, "-c:a"), Is.EqualTo("aac"));
            Assert.That(args[^1], Does.EndWith(".mov"));
        }

        [Test]
        public void AudioIsMuxedFromTheSongSeekedToTheStartAndBoundedToTheRange()
        {
            // 10s..40s range → audio seeked to 10, output duration 30.
            var args = FfmpegEncoder.BuildArgs(request("mp4"), "/music/song.mp3");

            Assert.That(valueAfter(args, "-ss"), Is.EqualTo("10"));
            Assert.That(valueAfter(args, "-t"), Is.EqualTo("30"));

            // The song is the second input, mapped explicitly as the audio stream.
            Assert.That(args, Does.Contain("/music/song.mp3"));
            Assert.That(valueAfter(args, "-map"), Is.EqualTo("0:v:0"));
            Assert.That(args.Contains("1:a:0"), Is.True);
        }

        [Test]
        public void FramesComeFromStdinAsRawVideo()
        {
            var args = FfmpegEncoder.BuildArgs(request("mp4"), "/music/song.mp3");

            Assert.That(valueAfter(args, "-f"), Is.EqualTo("rawvideo"));
            Assert.That(valueAfter(args, "-i"), Is.EqualTo("pipe:0"));
        }

        [Test]
        public void WithNoAudioFileASilentTrackIsSynthesised()
        {
            var args = FfmpegEncoder.BuildArgs(request("mp4"), audioPath: null);

            Assert.That(args.Any(a => a.StartsWith("anullsrc")), Is.True);
            // Still a well-formed audio stream mapped and encoded.
            Assert.That(args.Contains("1:a:0"), Is.True);
            Assert.That(valueAfter(args, "-c:a"), Is.EqualTo("aac"));
        }

        [Test]
        public void ExtensionMatchesTheContainer()
        {
            Assert.That(FfmpegEncoder.ExtensionFor("mp4"), Is.EqualTo("mp4"));
            Assert.That(FfmpegEncoder.ExtensionFor("webm"), Is.EqualTo("webm"));
            Assert.That(FfmpegEncoder.ExtensionFor("mov"), Is.EqualTo("mov"));
        }
    }
}
