#nullable enable

using System;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Framework.Timing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Repro for "No playable audio" on keysound-only beatmaps (e.g. set 92190, Imperishable
    /// Night 2006): every difficulty declares <c>AudioFilename: virtual</c> and the folder holds
    /// no music file at all — the song is hundreds of per-note samples. Such a set must play on a
    /// silent track sized from its own content, with hitsounds forced on so it isn't silent, while
    /// a set that names a real file that isn't there keeps reporting as unplayable.
    /// </summary>
    [TestFixture]
    public partial class TestSceneVirtualAudio : JukeBoxTestScene
    {
        private readonly FramedClock idleClock = new FramedClock(new ManualClock());

        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        [Cached]
        private readonly PlaybackController controller = new PlaybackController();

        private string tmp = null!;
        private CachedBeatmapSet virtualSet = null!;
        private CachedBeatmapSet brokenSet = null!;
        private CachedBeatmapSet plainSet = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            // A real committed difficulty, repointed at "virtual" with no audio file present —
            // exactly the on-disk shape of a keysounded set.
            virtualSet = buildSet("virtual", 92190, "virtual", withAudio: false);

            // Same map, but naming a real file that isn't there: still genuinely broken.
            brokenSet = buildSet("broken", 92191, "audio.mp3", withAudio: false);

            plainSet = buildSet("plain", 5880, "audio.wav", withAudio: true);
        }

        private CachedBeatmapSet buildSet(string name, int setId, string audioFilename, bool withAudio)
        {
            string dir = Path.Combine(tmp, name);
            Directory.CreateDirectory(dir);

            string osu = Path.Combine(dir, "happy [Easy].osu");
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "happy_people_easy.osu"), osu);

            string[] lines = File.ReadAllLines(osu);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("AudioFilename:", StringComparison.Ordinal))
                    lines[i] = $"AudioFilename: {audioFilename}";
            }

            File.WriteAllLines(osu, lines);

            if (withAudio)
                writeSilentWav(Path.Combine(dir, audioFilename));

            var cache = new BeatmapCache(Path.Combine(tmp, "unused"), new JukeBox.Game.Tests.Beatmaps.FileMirror(Path.Combine(tmp, "unused.osz")));
            return cache.LoadFromDirectory(setId, dir);
        }

        // Same 44-byte-RIFF-header trick as TestScenePlaybackController: BASS plays WAV directly.
        private static void writeSilentWav(string path)
        {
            const int sample_rate = 44100;
            int dataSize = sample_rate * 2; // 1s, 16-bit mono

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sample_rate);
            writer.Write(sample_rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }

        // PlayAsync's track swap runs through Schedule, and the playback clock is pumped from
        // Update — both need the controller actually in the scene graph, not merely cached.
        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("add controller", () =>
            {
                if (controller.Parent == null)
                    Add(controller);
            });
        }

        [Test]
        public void VirtualSetPlaysAndClockAdvances()
        {
            bool? played = null;

            AddStep("play virtual set", () => controller.PlayAsync(virtualSet).ContinueWith(t => played = t.Result));

            AddUntilStep("PlayAsync reported success", () => played == true);
            AddUntilStep("set is current", () => controller.Current.Value?.SetId == 92190);
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);
            AddAssert("is playing", () => controller.IsPlaying);

            // The silent track is sized from the map's own content, not from a file's duration.
            AddAssert("track length matches map content", () =>
                Math.Abs(controller.LengthMs - BeatmapDurationScanner.ComputeLength(virtualSet, virtualSet.PreferredOsuFile)) < 1);
            AddAssert("track length is substantial", () => controller.LengthMs > BeatmapDurationScanner.TailMs);

            AddStep("stop", () => controller.Stop());
        }

        /// <summary>Seeking a silent track has to work like any other, or the progress bar and the
        /// storyboard/chart it drives would be dead on these maps.</summary>
        [Test]
        public void VirtualTrackSeeks()
        {
            AddStep("play virtual set", () => controller.PlayAsync(virtualSet));
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);

            AddStep("seek to 10s", () => controller.Seek(10000));
            AddUntilStep("clock is at 10s or later", () => controller.CurrentTimeMs >= 10000);

            AddStep("stop", () => controller.Stop());
        }

        [Test]
        public void MissingRealAudioStaysUnplayable()
        {
            bool? played = null;

            AddStep("play broken set", () => controller.PlayAsync(brokenSet).ContinueWith(t => played = t.Result));

            AddUntilStep("PlayAsync reported failure", () => played == false);
            AddAssert("set never became current", () => controller.Current.Value?.SetId != 92191);
        }

        /// <summary>
        /// The map IS its hitsounds — with the user's "Play hit sounds" setting off (the default),
        /// a keysounded set would otherwise play in total silence.
        /// </summary>
        [Test]
        public void HitSoundsForcedOnForVirtualSet()
        {
            BeatmapVisuals visuals = null!;

            AddStep("chart and hitsounds both off", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayHitSounds, false);
            });

            AddStep("create visuals for virtual set", () => Add(visuals = new BeatmapVisuals(virtualSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("hitsound player present", () => visuals.HasHitSoundPlayer);
            AddAssert("chart has objects", () => visuals.ChartObjectCount > 0);
            AddAssert("chart stays hidden (setting is off)", () => !visuals.HasChartLayer);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        /// <summary>The forcing is scoped to virtual sets — an ordinary map still obeys the setting.</summary>
        [Test]
        public void HitSoundsStillFollowSettingForNormalSet()
        {
            BeatmapVisuals visuals = null!;

            AddStep("chart and hitsounds both off", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayHitSounds, false);
            });

            AddStep("create visuals for plain set", () => Add(visuals = new BeatmapVisuals(plainSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("no hitsound player", () => !visuals.HasHitSoundPlayer);

            AddStep("turn hitsounds on", () => config.SetValue(JukeBoxSetting.PlayHitSounds, true));
            AddUntilStep("hitsound player appears", () => visuals.HasHitSoundPlayer);

            AddStep("restore setting", () => config.SetValue(JukeBoxSetting.PlayHitSounds, false));
            AddStep("remove visuals", () => Remove(visuals, true));
        }
    }
}
