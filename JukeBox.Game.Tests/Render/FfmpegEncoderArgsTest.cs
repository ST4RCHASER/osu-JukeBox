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
        public void WithAHitSoundTrackTheSongAndTrackAreMixedIntoOneStream()
        {
            var args = FfmpegEncoder.BuildArgs(request("mp4"), "/music/song.mp3", "/tmp/hits.wav");

            // The track is a third input, not seeked (it already covers exactly the render range).
            int trackIndex = Array.IndexOf(args, "/tmp/hits.wav");
            Assert.That(trackIndex, Is.GreaterThan(0));
            Assert.That(args[trackIndex - 1], Is.EqualTo("-i"));

            // Both audio inputs feed one amix pinned to a PLAIN SUM — no input normalisation, no
            // dropout gain ramps (either reads as the music ducking under hitsounds) — followed by
            // a constant half-scale headroom so a full-scale song plus a full-scale hitsound can
            // never exceed full scale (the clamping of that overshoot was itself an audible duck).
            string filter = valueAfter(args, "-filter_complex");
            Assert.That(filter, Does.Contain("[1:a]"));
            Assert.That(filter, Does.Contain("[2:a]"));
            Assert.That(filter, Does.Contain("amix=inputs=2"));
            Assert.That(filter, Does.Contain("normalize=0"));
            Assert.That(filter, Does.Contain("dropout_transition=0"));
            Assert.That(filter, Does.Contain("volume=0.5"));

            // The mixed stream replaces the direct song mapping.
            Assert.That(args, Does.Contain("[mix]"));
            Assert.That(args.Contains("1:a:0"), Is.False);

            // Everything else is untouched: seek, range bound, codec, bitrate.
            Assert.That(valueAfter(args, "-ss"), Is.EqualTo("10"));
            Assert.That(valueAfter(args, "-t"), Is.EqualTo("30"));
            Assert.That(valueAfter(args, "-c:a"), Is.EqualTo("aac"));
            Assert.That(valueAfter(args, "-b:a"), Is.EqualTo("192k"));
        }

        [Test]
        public void WithoutAHitSoundTrackTheArgsAreExactlyTheLegacyOnes()
        {
            var req = request("mp4");

            Assert.That(FfmpegEncoder.BuildArgs(req, "/music/song.mp3", null), Is.EqualTo(FfmpegEncoder.BuildArgs(req, "/music/song.mp3")));
            Assert.That(FfmpegEncoder.BuildArgs(req, "/music/song.mp3"), Does.Not.Contain("-filter_complex"));
        }

        [Test]
        public void AHitSoundTrackStillMixesOverTheSynthesisedSilenceWhenThereIsNoSong()
        {
            var args = FfmpegEncoder.BuildArgs(request("mp4"), audioPath: null, hitSoundPath: "/tmp/hits.wav");

            Assert.That(args.Any(a => a.StartsWith("anullsrc")), Is.True);
            Assert.That(args, Does.Contain("/tmp/hits.wav"));
            Assert.That(valueAfter(args, "-filter_complex"), Does.Contain("amix=inputs=2"));
            Assert.That(args, Does.Contain("[mix]"));
        }

        [Test]
        public void ExtensionMatchesTheContainer()
        {
            Assert.That(FfmpegEncoder.ExtensionFor("mp4"), Is.EqualTo("mp4"));
            Assert.That(FfmpegEncoder.ExtensionFor("webm"), Is.EqualTo("webm"));
            Assert.That(FfmpegEncoder.ExtensionFor("mov"), Is.EqualTo("mov"));
        }

        [Test]
        public void WindowsLooksForTheExeNameFirst()
        {
            // A Windows PATH holds ffmpeg.exe, not a bare ffmpeg — probing only the bare name is
            // how a present install goes "not found".
            Assert.That(FfmpegEncoder.ExecutableNames(osu.Framework.RuntimeInfo.Platform.Windows), Is.EqualTo(new[] { "ffmpeg.exe", "ffmpeg" }));
            Assert.That(FfmpegEncoder.ExecutableNames(osu.Framework.RuntimeInfo.Platform.macOS), Is.EqualTo(new[] { "ffmpeg" }));
            Assert.That(FfmpegEncoder.ExecutableNames(osu.Framework.RuntimeInfo.Platform.Linux), Is.EqualTo(new[] { "ffmpeg" }));
        }

        [Test]
        public void CandidateLocationsStartBesideTheAppThenTheOsUsualInstalls()
        {
            var mac = FfmpegEncoder.CandidateLocations(osu.Framework.RuntimeInfo.Platform.macOS, "/app").ToArray();
            Assert.That(mac, Is.EqualTo(new[]
            {
                System.IO.Path.Combine("/app", "ffmpeg"),
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/bin/ffmpeg",
            }));

            var linux = FfmpegEncoder.CandidateLocations(osu.Framework.RuntimeInfo.Platform.Linux, "/app").ToArray();
            Assert.That(linux, Is.EqualTo(new[]
            {
                System.IO.Path.Combine("/app", "ffmpeg"),
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
            }));

            // Windows installs live on PATH or beside the app — no unix-style spots to probe.
            var windows = FfmpegEncoder.CandidateLocations(osu.Framework.RuntimeInfo.Platform.Windows, "C:\\app").ToArray();
            Assert.That(windows, Is.EqualTo(new[]
            {
                System.IO.Path.Combine("C:\\app", "ffmpeg.exe"),
                System.IO.Path.Combine("C:\\app", "ffmpeg"),
            }));
        }
    }
}
