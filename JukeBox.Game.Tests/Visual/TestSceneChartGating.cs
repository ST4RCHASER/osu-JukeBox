#nullable enable

using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Framework.Timing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Repro for the "storyboard maps render no chart and play no hitsounds" report: a REAL
    /// .osb-bearing set (committed fixture: S-C-U – heron [Beginner], set 165202) must still get
    /// a chart layer with objects and a hitsound player when both settings are on — both when
    /// BeatmapVisuals is built directly and through the real PlayAsync → NowPlayingScreen flow
    /// (including the sequence "no-storyboard map first, storyboard map second", where a stale
    /// difficulty selection could poison the second build).
    /// </summary>
    [TestFixture]
    public partial class TestSceneChartGating : JukeBoxTestScene
    {
        private readonly FramedClock idleClock = new FramedClock(new ManualClock());

        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        [Cached]
        private readonly PlaybackController controller = new PlaybackController();

        private string tmp = null!;
        private CachedBeatmapSet storyboardSet = null!;
        private CachedBeatmapSet plainSet = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            // Storyboard set: the real fixture .osu + .osb extracted the same way the cache
            // would lay them out (textures absent — storyboard drawables just resolve to none).
            string sbDir = Path.Combine(tmp, "sb");
            Directory.CreateDirectory(sbDir);
            File.Copy(fixture("heron_beginner.osu"), Path.Combine(sbDir, "heron [Beginner].osu"));
            File.Copy(fixture("heron.osb"), Path.Combine(sbDir, "heron.osb"));
            writeSilentWav(Path.Combine(sbDir, "audio.wav"));
            patchAudioFilename(Path.Combine(sbDir, "heron [Beginner].osu"));

            // Plain set (no .osb): the user believes charts work here — the flow test confirms it.
            string plainDir = Path.Combine(tmp, "plain");
            Directory.CreateDirectory(plainDir);
            File.Copy(fixture("happy_people_easy.osu"), Path.Combine(plainDir, "happy [Easy].osu"));
            writeSilentWav(Path.Combine(plainDir, "audio.wav"));
            patchAudioFilename(Path.Combine(plainDir, "happy [Easy].osu"));

            // Build the sets through the REAL cache loader, not hand-rolled fixtures.
            var cache = new BeatmapCache(Path.Combine(tmp, "unused"), new JukeBox.Game.Tests.Beatmaps.FileMirror(Path.Combine(tmp, "unused.osz")));
            storyboardSet = cache.LoadFromDirectory(165202, sbDir);
            plainSet = cache.LoadFromDirectory(5880, plainDir);
        }

        private static string fixture(string name)
            => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name);

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

        // Point AudioFilename at the silent wav the flow test can actually play.
        private static void patchAudioFilename(string osuPath)
        {
            string[] lines = File.ReadAllLines(osuPath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("AudioFilename:"))
                    lines[i] = "AudioFilename: audio.wav";
            }

            File.WriteAllLines(osuPath, lines);
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("enable chart + hitsounds", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.PlayHitSounds, true);
            });
        }

        [TearDownSteps]
        public void TearDownSteps()
        {
            AddStep("restore settings", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayHitSounds, false);
            });
        }

        [Test]
        public void StoryboardSetGetsChartAndHitsoundsDirectly()
        {
            BeatmapVisuals visuals = null!;

            AddAssert("fixture set has an osb", () => storyboardSet.OsbFile != null);
            AddAssert("fixture set has a preferred std diff", () => storyboardSet.PreferredOsuFile != null);

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(storyboardSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddUntilStep("chart layer present", () => visuals.HasChartLayer);
            AddUntilStep("chart has objects", () => visuals.ChartObjectCount > 0);
            AddAssert("hitsound player present", () => visuals.HasHitSoundPlayer);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // Mania (and taiko/catch) sets render through lazer's real gameplay renderer — the old
        // "mode 3 is chartless" gate is gone. The storyboard-heavy mania-only sets that motivated
        // the silent-gate fix (cached 154156, 1986088) render real mania gameplay + hitsounds.
        [Test]
        public void ManiaOnlyStoryboardSetGetsAManiaChart()
        {
            BeatmapVisuals visuals = null!;
            CachedBeatmapSet maniaSet = null!;

            AddStep("build mania-only storyboard set", () =>
            {
                string dir = Path.Combine(tmp, "mania");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "mania [4K].osu"),
                    "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 3\n\n[Metadata]\nVersion:4K\n\n[Difficulty]\nCircleSize:4\n\n" +
                    "[TimingPoints]\n0,500,4,1,0,100,1,0\n\n[HitObjects]\n64,192,1000,1,0\n448,192,1500,128,0,2500:0:0:0:0:\n");
                File.Copy(fixture("heron.osb"), Path.Combine(dir, "mania.osb"));

                var cache = new BeatmapCache(Path.Combine(tmp, "unused2"), new JukeBox.Game.Tests.Beatmaps.FileMirror(Path.Combine(tmp, "unused.osz")));
                maniaSet = cache.LoadFromDirectory(154156, dir);
            });

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(maniaSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);

            AddUntilStep("mania chart present", () => visuals.ChartRenderer?.Ruleset is osu.Game.Rulesets.Mania.ManiaRuleset);
            AddAssert("both objects converted (note + hold)", () => visuals.ChartObjectCount == 2);
            AddAssert("hitsound player present", () => visuals.HasHitSoundPlayer);
            AddAssert("no unavailability reason", () => visuals.ChartUnavailableReason == null);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // Unknown FUTURE modes remain chartless — and, per the silent-gate post-mortem, never
        // silently: the reason is still recorded and logged.
        [Test]
        public void UnknownModeSetReportsWhyChartIsUnavailable()
        {
            BeatmapVisuals visuals = null!;
            CachedBeatmapSet weirdSet = null!;

            AddStep("build unknown-mode set", () =>
            {
                string dir = Path.Combine(tmp, "weird");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "weird [??].osu"),
                    "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 7\n\n[Metadata]\nVersion:??\n\n" +
                    "[TimingPoints]\n0,500,4,1,0,100,1,0\n\n[HitObjects]\n64,192,1000,1,0\n");

                var cache = new BeatmapCache(Path.Combine(tmp, "unused3"), new JukeBox.Game.Tests.Beatmaps.FileMirror(Path.Combine(tmp, "unused.osz")));
                weirdSet = cache.LoadFromDirectory(999999, dir);
            });

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(weirdSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);

            AddAssert("no chart layer", () => !visuals.HasChartLayer);
            AddAssert("no hitsound player", () => !visuals.HasHitSoundPlayer);
            AddAssert("unavailability reason names the unknown mode",
                () => visuals.ChartUnavailableReason?.Contains("unknown game mode 7") == true);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        [Test]
        public void StoryboardSetGetsChartThroughTheRealPlaybackFlow()
        {
            NowPlayingScreen screen = null!;
            ScreenStack stack = null!;

            AddStep("create controller + screen", () =>
            {
                if (controller.Parent == null)
                    Add(controller);

                Add(stack = new ScreenStack(screen = new NowPlayingScreen()) { RelativeSizeAxes = Axes.Both });
            });

            // First a PLAIN map (confirming the user's belief that this case works)...
            AddStep("play plain set", () => controller.PlayAsync(plainSet));
            AddUntilStep("plain visuals loaded", () => screen.CurrentVisuals?.IsLoaded == true);
            AddUntilStep("plain chart present", () => screen.CurrentVisuals?.HasChartLayer == true && screen.CurrentVisuals.ChartObjectCount > 0);

            // ...then the STORYBOARD map, exactly as the jukebox would advance to it.
            AddStep("play storyboard set", () => controller.PlayAsync(storyboardSet));
            AddUntilStep("storyboard visuals loaded",
                () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == storyboardSet.PreferredOsuFile);
            AddUntilStep("storyboard chart present", () => screen.CurrentVisuals?.HasChartLayer == true && screen.CurrentVisuals.ChartObjectCount > 0);
            AddAssert("storyboard hitsounds present", () => screen.CurrentVisuals?.HasHitSoundPlayer == true);

            AddStep("clean up", () => Remove(stack, true));
        }
    }
}
