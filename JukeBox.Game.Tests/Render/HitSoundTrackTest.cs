#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.UI.Render;
using NUnit.Framework;
using osu.Framework.IO.Stores;
using osu.Game.Audio;

namespace JukeBox.Game.Tests.Render
{
    /// <summary>
    /// The render's hitsound audio pipeline, piece by piece: sample-name resolution against real
    /// folders and lazer's real embedded resources, the WAV mix (header, placement, volume,
    /// clamping) against synthetic PCM, the ffmpeg decode boundary against a WAV of known content,
    /// and the whole track end-to-end off the fixture map.
    /// </summary>
    [TestFixture]
    public class HitSoundTrackTest
    {
        private static readonly Func<string, byte[]?> stock_resources = new DllResourceStore(osu.Game.Resources.OsuResources.ResourceAssembly).Get;

        private static HitSampleInfo sample(string name = HitSampleInfo.HIT_NORMAL, string bank = HitSampleInfo.BANK_SOFT, int volume = 100)
            => new HitSampleInfo(name, bank, volume: volume);

        // ---- resolution ---------------------------------------------------------------------------

        [Test]
        public void CandidateNamesFollowLazersLookupOrderWithoutTheGameplayPrefix()
        {
            var names = HitSoundTrack.CandidateNames(sample()).ToList();

            Assert.That(names, Is.Not.Empty);
            Assert.That(names, Has.All.Not.StartsWith("Gameplay/"));
            Assert.That(names, Does.Contain("soft-hitnormal"));
        }

        [Test]
        public void ASkinFolderFileResolvesWhateverItsExtension()
        {
            string dir = freshDir();

            try
            {
                File.WriteAllBytes(Path.Combine(dir, "soft-hitnormal.ogg"), new byte[] { 1 });

                Assert.That(HitSoundTrack.ResolveFile(sample(), null, dir), Is.EqualTo(Path.Combine(dir, "soft-hitnormal.ogg")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void TheBeatmapFolderOutranksTheSkinExactlyWhenTheSampleOptsIn()
        {
            string beatmapDir = freshDir();
            string skinDir = freshDir();

            try
            {
                File.WriteAllBytes(Path.Combine(beatmapDir, "soft-hitnormal.wav"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(skinDir, "soft-hitnormal.wav"), new byte[] { 2 });

                var s = sample();
                string expectedDir = s.UseBeatmapSamples ? beatmapDir : skinDir;

                Assert.That(HitSoundTrack.ResolveFile(s, beatmapDir, skinDir), Is.EqualTo(Path.Combine(expectedDir, "soft-hitnormal.wav")));
            }
            finally
            {
                Directory.Delete(beatmapDir, true);
                Directory.Delete(skinDir, true);
            }
        }

        [Test]
        public void NothingOnDiskResolvesToNull()
        {
            Assert.That(HitSoundTrack.ResolveFile(sample(), null, null), Is.Null);
            Assert.That(HitSoundTrack.ResolveFile(sample(), "/definitely/not/here", "/nor/here"), Is.Null);
        }

        [Test]
        public void EveryStockBankAndNameResolvesFromLazersEmbeddedResources()
        {
            // The last link of the chain must never miss for the standard set — otherwise a plain
            // map on a bundled skin renders silent hitsounds.
            foreach (string bank in new[] { HitSampleInfo.BANK_NORMAL, HitSampleInfo.BANK_SOFT, HitSampleInfo.BANK_DRUM })
            {
                foreach (string name in new[] { HitSampleInfo.HIT_NORMAL, HitSampleInfo.HIT_WHISTLE, HitSampleInfo.HIT_FINISH, HitSampleInfo.HIT_CLAP })
                {
                    foreach (var skin in new[] { JukeBoxSkin.Argon, JukeBoxSkin.Triangles, JukeBoxSkin.Classic })
                    {
                        byte[]? bytes = HitSoundTrack.ResolveDefault(sample(name, bank), HitSoundTrack.DefaultPrefixesFor(skin), stock_resources);
                        Assert.That(bytes, Is.Not.Null.And.Not.Empty, $"{bank}-{name} under {skin}");
                    }
                }
            }
        }

        [Test]
        public void ArgonResolvesItsOwnSampleSetNotTheBaseOne()
        {
            byte[]? argon = HitSoundTrack.ResolveDefault(sample(), HitSoundTrack.DefaultPrefixesFor(JukeBoxSkin.Argon), stock_resources);
            byte[]? triangles = HitSoundTrack.ResolveDefault(sample(), HitSoundTrack.DefaultPrefixesFor(JukeBoxSkin.Triangles), stock_resources);

            Assert.That(argon, Is.Not.Null);
            Assert.That(triangles, Is.Not.Null);
            Assert.That(argon!.SequenceEqual(triangles!), Is.False, "Argon ships its own soft-hitnormal");
        }

        // ---- the mix ------------------------------------------------------------------------------

        [Test]
        public void TheMixPlacesASampleAtItsTimeScaledByGainAndVolume()
        {
            string path = tempWav();

            try
            {
                // One sample of two frames at constant 0.5, landing 100ms in, at 50% object volume.
                var schedule = new[] { new HitSoundSchedule.Entry(100, new[] { sample(volume: 50) }) };

                Assert.That(HitSoundTrack.MixToWavFile(schedule, _ => new[] { 0.5f, 0.5f, 0.5f, 0.5f }, 0, 1000, 1.0, path), Is.True);

                var (header, samples) = readWav(path);

                Assert.That(header.sampleRate, Is.EqualTo(HitSoundTrack.SAMPLE_RATE));
                Assert.That(header.channels, Is.EqualTo(2));
                Assert.That(header.bitsPerSample, Is.EqualTo(16));
                Assert.That(samples.Length, Is.EqualTo((int)Math.Ceiling(1.0 * HitSoundTrack.SAMPLE_RATE) * 2));

                int offset = (int)Math.Round(0.1 * HitSoundTrack.SAMPLE_RATE) * 2;
                short expected = (short)Math.Round(0.5 * 0.5 * short.MaxValue);

                Assert.That(samples[offset], Is.EqualTo(expected).Within(1));
                Assert.That(samples[offset + 1], Is.EqualTo(expected).Within(1));
                Assert.That(samples[offset - 2], Is.Zero, "nothing sounds before the event");
                Assert.That(samples[offset + 4], Is.Zero, "nothing sounds after the sample ends");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void TheMixIsRelativeToTheRangeStartAndRespectsTheOverallGain()
        {
            string path = tempWav();

            try
            {
                // An event at 5.1s in a 5s..6s render lands 100ms into the track, at half gain.
                var schedule = new[] { new HitSoundSchedule.Entry(5100, new[] { sample() }) };

                Assert.That(HitSoundTrack.MixToWavFile(schedule, _ => new[] { 1f, 1f }, 5000, 6000, 0.5, path), Is.True);

                var (_, samples) = readWav(path);
                int offset = (int)Math.Round(0.1 * HitSoundTrack.SAMPLE_RATE) * 2;

                Assert.That(samples[offset], Is.EqualTo((short)Math.Round(0.5 * short.MaxValue)).Within(1));
                Assert.That(samples[0], Is.Zero);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void OverlappingSamplesSumAndClampInsteadOfWrapping()
        {
            string path = tempWav();

            try
            {
                // Two full-scale samples at the same instant: 2.0 must clamp to full scale, not wrap.
                var schedule = new[]
                {
                    new HitSoundSchedule.Entry(0, new[] { sample() }),
                    new HitSoundSchedule.Entry(0, new[] { sample() }),
                };

                Assert.That(HitSoundTrack.MixToWavFile(schedule, _ => new[] { 1f, 1f }, 0, 100, 1.0, path), Is.True);

                var (_, samples) = readWav(path);
                Assert.That(samples[0], Is.EqualTo(short.MaxValue));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void AScheduleWhoseSamplesAllResolveNowhereProducesNoTrack()
        {
            string path = tempWav();
            var schedule = new[] { new HitSoundSchedule.Entry(0, new[] { sample() }) };

            Assert.That(HitSoundTrack.MixToWavFile(schedule, _ => null, 0, 1000, 1.0, path), Is.False);
        }

        // ---- the ffmpeg boundaries ----------------------------------------------------------------

        [Test]
        public void DecodePcmRoundTripsAWavOfKnownContent()
        {
            if (!FfmpegEncoder.IsFfmpegAvailable(out _))
                Assert.Ignore("ffmpeg not installed on this machine");

            string path = tempWav();

            try
            {
                // Author a track through our own writer, then decode it back through ffmpeg.
                var schedule = new[] { new HitSoundSchedule.Entry(0, new[] { sample() }) };
                Assert.That(HitSoundTrack.MixToWavFile(schedule, _ => new[] { 0.5f, 0.5f, 0.5f, 0.5f }, 0, 100, 1.0, path), Is.True);

                float[]? pcm = HitSoundTrack.DecodePcm(path);

                Assert.That(pcm, Is.Not.Null);
                Assert.That(pcm!.Length, Is.EqualTo((int)Math.Ceiling(0.1 * HitSoundTrack.SAMPLE_RATE) * 2));
                Assert.That(pcm[0], Is.EqualTo(0.5f).Within(0.01f));
                Assert.That(pcm[4], Is.EqualTo(0f).Within(0.001f));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void BuildTrackFileProducesAWavCoveringTheRangeFromTheFixtureMap()
        {
            if (!FfmpegEncoder.IsFfmpegAvailable(out _))
                Assert.Ignore("ffmpeg not installed on this machine");

            string osuFile = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "happy_people_easy.osu");
            var request = new RenderRequest("mp4", 16, 16, 30, "/tmp/out.mp4", 10_000, 20_000, 128);
            var sources = new HitSoundTrack.Sources(null, null, HitSoundTrack.DefaultPrefixesFor(JukeBoxSkin.Argon), stock_resources);

            string? path = HitSoundTrack.BuildTrackFile(osuFile, null, request, sources, 0.6);

            Assert.That(path, Is.Not.Null, "the fixture map has objects in 10s..20s and stock samples always resolve");

            try
            {
                var (header, samples) = readWav(path!);

                Assert.That(header.sampleRate, Is.EqualTo(HitSoundTrack.SAMPLE_RATE));
                Assert.That(samples.Length, Is.EqualTo((int)Math.Ceiling(10.0 * HitSoundTrack.SAMPLE_RATE) * 2));
                Assert.That(samples.Any(s => s != 0), Is.True, "the track must actually carry sound");
            }
            finally
            {
                File.Delete(path!);
            }
        }

        // ---- helpers ------------------------------------------------------------------------------

        private static string freshDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"jukebox-hs-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string tempWav() => Path.Combine(Path.GetTempPath(), $"jukebox-hs-test-{Guid.NewGuid():N}.wav");

        private static ((int sampleRate, int channels, int bitsPerSample) header, short[] samples) readWav(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));

            Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("RIFF"));
            reader.ReadUInt32(); // riff size
            Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("WAVE"));
            Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("fmt "));
            Assert.That(reader.ReadUInt32(), Is.EqualTo(16), "PCM fmt chunk");
            Assert.That(reader.ReadUInt16(), Is.EqualTo(1), "PCM format tag");

            int channels = reader.ReadUInt16();
            int sampleRate = (int)reader.ReadUInt32();
            reader.ReadUInt32(); // byte rate
            reader.ReadUInt16(); // block align
            int bits = reader.ReadUInt16();

            Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("data"));
            uint dataBytes = reader.ReadUInt32();

            var samples = new short[dataBytes / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = reader.ReadInt16();

            return ((sampleRate, channels, bits), samples);
        }
    }
}
