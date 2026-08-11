using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestScenePlaybackController : JukeBoxTestScene
    {
        private PlaybackController controller = null!;
        private string tmp = null!;
        private CachedBeatmapSet fixtureSet = null!;
        private CachedBeatmapSet fixtureSetA = null!;
        private CachedBeatmapSet fixtureSetB = null!;
        private string sameAudioDiff = null!;
        private string otherAudioDiff = null!;

        private double timeBeforeSwitch;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            string audioFile = Path.Combine(tmp, "audio.wav");
            writeSilentWav(audioFile, 1);

            // A second, longer audio file in the same folder: the "different AudioFilename"
            // difficulty-switch branch is observable through the track length changing.
            writeSilentWav(Path.Combine(tmp, "audio2.wav"), 3);

            // The .osu files themselves never need to exist for SwitchDifficultyAsync — it works
            // off the Difficulties metadata captured at cache-load time.
            string diffA = Path.Combine(tmp, "a.osu");
            string diffB = Path.Combine(tmp, "b.osu");
            string diffC = Path.Combine(tmp, "c.osu");

            fixtureSet = new CachedBeatmapSet
            {
                SetId = 1,
                Directory = tmp,
                AudioFile = audioFile,
                PreferredOsuFile = diffA,
                OsuFiles = new List<string> { diffA, diffB, diffC },
                Difficulties = new List<DifficultyInfo>
                {
                    new DifficultyInfo { Path = diffA, Version = "Easy", Mode = 0, AudioFilename = "audio.wav" },
                    new DifficultyInfo { Path = diffB, Version = "Hard", Mode = 0, AudioFilename = "audio.wav" },
                    new DifficultyInfo { Path = diffC, Version = "Other", Mode = 0, AudioFilename = "audio2.wav" },
                },
            };
            sameAudioDiff = diffB;
            otherAudioDiff = diffC;

            // Two more fixtures in their own subdirectories, for the overlapping-PlayAsync test.
            string dirA = Path.Combine(tmp, "a");
            string dirB = Path.Combine(tmp, "b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            string audioFileA = Path.Combine(dirA, "audio.wav");
            string audioFileB = Path.Combine(dirB, "audio.wav");
            writeSilentWav(audioFileA, 1);
            writeSilentWav(audioFileB, 1);

            fixtureSetA = new CachedBeatmapSet { SetId = 10, Directory = dirA, AudioFile = audioFileA };
            fixtureSetB = new CachedBeatmapSet { SetId = 20, Directory = dirB, AudioFile = audioFileB };
        }

        // NOTE: deliberately NOT deleting `tmp` here. TestScene runs each [Test] method's queued
        // AddStep bodies from a base-class teardown hook that NUnit invokes *after* this derived
        // class's own [TearDown] — so a synchronous delete here would race the fixture files out
        // from under the still-pending steps (confirmed empirically: it caused every track load
        // in this file to silently fail). Test temp dirs are left for the OS to reclaim, matching
        // the same tradeoff already made by TestScene's own step/browser scratch directories.

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create controller", () => Child = controller = new PlaybackController());
        }

        [Test]
        public void PlayThenPauseStopsClock()
        {
            AddStep("play fixture", () => controller.PlayAsync(fixtureSet));
            AddUntilStep("track is active", () => controller.Current.Value?.SetId == fixtureSet.SetId);
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);

            AddStep("pause", () => controller.TogglePause());
            AddAssert("not playing", () => !controller.IsPlaying);
        }

        // Regression test for the PlayAsync overlap race: the most-recently-requested call must
        // win the swap even if its load happens to finish before (or after) the older call's.
        [Test]
        public void OverlappingPlayAsyncSecondCallWins()
        {
            AddStep("play A then B back-to-back", () =>
            {
                controller.PlayAsync(fixtureSetA);
                controller.PlayAsync(fixtureSetB);
            });

            AddUntilStep("second call's track is active", () => controller.Current.Value?.SetId == fixtureSetB.SetId);
            AddAssert("first call never became active", () => controller.Current.Value?.SetId != fixtureSetA.SetId);
        }

        // Same-audio branch: no track swap happens, so playback simply continues — the position
        // stays monotonic (never reset/seeked), the track length is unchanged, and only the
        // selected file retargets.
        [Test]
        public void SwitchDifficultySameAudioContinuesWithoutTrackSwap()
        {
            AddStep("play fixture", () => controller.PlayAsync(fixtureSet));
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);

            double lengthBefore = 0;
            AddStep("switch to same-audio diff", () =>
            {
                timeBeforeSwitch = controller.CurrentTimeMs;
                lengthBefore = controller.LengthMs;
                controller.SwitchDifficultyAsync(sameAudioDiff);
            });

            AddUntilStep("selection retargeted", () => controller.SelectedOsuFile.Value == sameAudioDiff);
            AddAssert("track length unchanged", () => controller.LengthMs == lengthBefore);
            AddAssert("position monotonic (no reset)", () => controller.CurrentTimeMs >= timeBeforeSwitch);
            AddAssert("still playing", () => controller.IsPlaying);
        }

        // Different-audio branch: the new difficulty's AudioFilename resolves to another file, so
        // a new track (observably longer here) swaps in, seeked to the previous position, and the
        // old track/store disposal doesn't disturb subsequent updates.
        [Test]
        public void SwitchDifficultyDifferentAudioResumesAtPreviousPosition()
        {
            AddStep("play fixture", () => controller.PlayAsync(fixtureSet));
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);

            AddStep("switch to other-audio diff", () =>
            {
                timeBeforeSwitch = controller.CurrentTimeMs;
                controller.SwitchDifficultyAsync(otherAudioDiff);
            });

            AddUntilStep("selection retargeted", () => controller.SelectedOsuFile.Value == otherAudioDiff);
            AddUntilStep("new (3s) track active", () => controller.LengthMs > 2000);
            AddAssert("position resumed, not reset to zero", () => controller.CurrentTimeMs >= timeBeforeSwitch);
            AddAssert("position near previous, not wildly ahead", () => controller.CurrentTimeMs < timeBeforeSwitch + 2000);
            AddAssert("still playing", () => controller.IsPlaying);

            // A few more frames with the old track disposed — playback must keep advancing.
            double mark = 0;
            AddStep("mark time", () => mark = controller.CurrentTimeMs);
            AddUntilStep("clock still advances on the new track", () => controller.CurrentTimeMs > mark);
        }

        // Pause state must survive an audio swap: a paused switch stays paused at the same spot.
        [Test]
        public void SwitchDifficultyDifferentAudioPreservesPause()
        {
            AddStep("play fixture", () => controller.PlayAsync(fixtureSet));
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);

            AddStep("pause", () => controller.TogglePause());
            AddAssert("not playing", () => !controller.IsPlaying);

            AddStep("switch to other-audio diff", () =>
            {
                timeBeforeSwitch = controller.CurrentTimeMs;
                controller.SwitchDifficultyAsync(otherAudioDiff);
            });

            AddUntilStep("new (3s) track active", () => controller.LengthMs > 2000);
            AddAssert("still paused", () => !controller.IsPlaying);
            AddAssert("position preserved exactly", () => Math.Abs(controller.CurrentTimeMs - timeBeforeSwitch) < 1);
        }

        // Race arbitration, same pattern as OverlappingPlayAsyncSecondCallWins: a PlayAsync issued
        // right after a difficulty switch claims a newer generation, so the switch's (stale) track
        // must be dropped — latest request wins, nothing wedges.
        [Test]
        public void OverlappingSwitchThenPlayAsyncPlayWins()
        {
            AddStep("play fixture", () => controller.PlayAsync(fixtureSet));
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);

            AddStep("switch then play B back-to-back", () =>
            {
                controller.SwitchDifficultyAsync(otherAudioDiff);
                controller.PlayAsync(fixtureSetB);
            });

            AddUntilStep("PlayAsync's set is active", () => controller.Current.Value?.SetId == fixtureSetB.SetId);
            AddAssert("stale switch selection was dropped", () => controller.SelectedOsuFile.Value != otherAudioDiff);
            AddUntilStep("playback not wedged", () => controller.IsPlaying && controller.CurrentTimeMs >= 0);
        }

        // BASS (the audio backend behind osu!framework's Track) plays WAV directly, so a
        // hand-written 44-byte RIFF header followed by silence is enough to drive playback.
        private static void writeSilentWav(string path, double seconds)
        {
            const int sample_rate = 44100;
            const short channels = 1;
            const short bits_per_sample = 16;

            int dataSize = (int)(sample_rate * channels * (bits_per_sample / 8) * seconds);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sample_rate);
            writer.Write(sample_rate * channels * (bits_per_sample / 8));
            writer.Write((short)(channels * (bits_per_sample / 8)));
            writer.Write(bits_per_sample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }
    }
}
