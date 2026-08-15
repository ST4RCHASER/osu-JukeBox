#nullable enable

using System.IO;
using JukeBox.Game.Beatmaps;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Beatmaps
{
    /// <summary>
    /// Keysound-only beatmaps (<c>AudioFilename: virtual</c>) have no music file at all — the
    /// whole song is per-note samples. They must be recognised as playable-without-audio rather
    /// than lumped in with genuinely broken sets, and must yield a content-derived length for the
    /// silent track that carries their clock.
    /// </summary>
    [TestFixture]
    public class VirtualAudioTest
    {
        private string tmp = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
        }

        [TestCase("virtual")]
        [TestCase("Virtual")]
        [TestCase("VIRTUAL")]
        [TestCase("virtual.mp3")]
        [TestCase("virtual.ogg")]
        [TestCase(" virtual ")]
        [TestCase("")]
        [TestCase(null)]
        public void VirtualFilenamesAreRecognised(string? audioFilename)
            => Assert.That(OsuFileScanner.IsVirtualAudioFilename(audioFilename), Is.True);

        [TestCase("audio.mp3")]
        [TestCase("virtually.mp3")]
        [TestCase("my virtual song.mp3")]
        [TestCase("virtual/audio.mp3")]
        public void RealFilenamesAreNotVirtual(string audioFilename)
            => Assert.That(OsuFileScanner.IsVirtualAudioFilename(audioFilename), Is.False);

        [Test]
        public void CacheFlagsVirtualAudioSet()
        {
            string dir = writeSet("virtual", withAudioFile: false);

            var set = loadSet(dir);

            Assert.That(set.AudioFile, Is.Null);
            Assert.That(set.HasVirtualAudio, Is.True);
        }

        /// <summary>
        /// The regression guard on the detection rule: a difficulty naming a REAL file that isn't
        /// there is a broken set, not a keysounded one, and has to keep reporting as unplayable.
        /// </summary>
        [Test]
        public void CacheDoesNotFlagGenuinelyMissingAudio()
        {
            string dir = writeSet("audio.mp3", withAudioFile: false);

            var set = loadSet(dir);

            Assert.That(set.AudioFile, Is.Null);
            Assert.That(set.HasVirtualAudio, Is.False);
        }

        /// <summary>A set that really does ship a file called "virtual.mp3" plays that file.</summary>
        [Test]
        public void CacheDoesNotFlagSetWithRealFileNamedVirtual()
        {
            string dir = writeSet("virtual.mp3", withAudioFile: true);

            var set = loadSet(dir);

            Assert.That(set.AudioFile, Does.EndWith("virtual.mp3"));
            Assert.That(set.HasVirtualAudio, Is.False);
        }

        [Test]
        public void DurationCoversLastHitObject()
        {
            double end = BeatmapDurationScanner.ScanEndTime(new[]
            {
                "[HitObjects]",
                "256,192,1000,1,0,0:0:0:0:",
                "256,192,8500,1,0,0:0:0:0:",
                "256,192,4000,1,0,0:0:0:0:",
            });

            Assert.That(end, Is.EqualTo(8500));
        }

        /// <summary>Mania holds (type bit 7) and spinners (bit 3) end later than they start.</summary>
        [Test]
        public void DurationUsesHoldAndSpinnerEndTimes()
        {
            double hold = BeatmapDurationScanner.ScanEndTime(new[]
            {
                "[HitObjects]",
                "64,192,1000,128,0,9000:0:0:0:0:",
            });

            double spinner = BeatmapDurationScanner.ScanEndTime(new[]
            {
                "[HitObjects]",
                "256,192,1000,12,0,7000,0:0:0:0:",
            });

            Assert.That(hold, Is.EqualTo(9000));
            Assert.That(spinner, Is.EqualTo(7000));
        }

        [Test]
        public void DurationCoversStoryboardSamplesAndCommands()
        {
            double end = BeatmapDurationScanner.ScanEndTime(new[]
            {
                "[Events]",
                "Sample,12000,0,\"kick.ogg\",70",
                "Sprite,Background,Centre,\"bg.jpg\",320,240",
                " F,0,500,3000,0,1",
                "[HitObjects]",
                "256,192,1000,1,0,0:0:0:0:",
            });

            Assert.That(end, Is.EqualTo(12000));
        }

        /// <summary>
        /// A loop's inner commands are timed relative to the loop start and repeat, so the loop
        /// really ends at start + count × innerEnd — 2000 + 3 × 1000 here.
        /// </summary>
        [Test]
        public void DurationExpandsStoryboardLoops()
        {
            double end = BeatmapDurationScanner.ScanEndTime(new[]
            {
                "[Events]",
                "Sprite,Foreground,Centre,\"flash.png\",320,240",
                " L,2000,3",
                "  F,0,0,1000,0,1",
            });

            Assert.That(end, Is.EqualTo(5000));
        }

        [Test]
        public void ComputedLengthAddsTailAndSpansOsb()
        {
            string dir = writeSet("virtual", withAudioFile: false);
            File.WriteAllText(Path.Combine(dir, "sb.osb"), "[Events]\nSample,20000,0,\"clap.ogg\",80\n");

            var set = loadSet(dir);

            // .osu content ends at 5000 (see writeSet), the .osb sample at 20000 — the later wins.
            Assert.That(BeatmapDurationScanner.ComputeLength(set, set.PreferredOsuFile),
                Is.EqualTo(20000 + BeatmapDurationScanner.TailMs));
        }

        /// <summary>An unreadable/contentless map still gets a clock that runs, not a zero-length
        /// track that completes instantly and spins the queue.</summary>
        [Test]
        public void ComputedLengthNeverZero()
        {
            var empty = new CachedBeatmapSet { SetId = 1, Directory = tmp };

            Assert.That(BeatmapDurationScanner.ComputeLength(empty, null), Is.EqualTo(BeatmapDurationScanner.TailMs));
        }

        // A minimal mania set whose content ends at 5000ms.
        private string writeSet(string audioFilename, bool withAudioFile)
        {
            string dir = Path.Combine(tmp, Path.GetRandomFileName());
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "test.osu"),
                "osu file format v14\n\n"
                + $"[General]\nAudioFilename: {audioFilename}\nMode: 3\n\n"
                + "[Events]\n"
                + "Sample,3000,0,\"kick.ogg\",80\n\n"
                + "[HitObjects]\n"
                + "64,192,1000,1,0,0:0:0:0:\n"
                + "192,192,5000,1,0,0:0:0:0:\n");

            if (withAudioFile)
                File.WriteAllBytes(Path.Combine(dir, audioFilename), new byte[] { 0xFF });

            return dir;
        }

        private CachedBeatmapSet loadSet(string dir)
        {
            var cache = new BeatmapCache(Path.Combine(tmp, "cache"), new FileMirror(Path.Combine(tmp, "unused.osz")));
            return cache.LoadFromDirectory(99, dir);
        }
    }
}
