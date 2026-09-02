#nullable enable

using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
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

        [Resolved]
        private BeatmapOffsetStore offsetStore { get; set; } = null!;

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
                config.SetValue(JukeBoxSetting.ChartOpacity, 1.0);
                config.SetValue(JukeBoxSetting.ShowStoryboard, true);
                config.SetValue(JukeBoxSetting.ShowVideo, true);
            });
        }

        // The per-beatmap/global audio offsets shift the whole visual clock relative to the track:
        // visual time = playback time + (beatmap offset + global offset).
        [Test]
        public void AudioOffsetShiftsVisualClock()
        {
            var manual = new ManualClock();
            var framed = new FramedClock(manual);
            BeatmapVisuals visuals = null!;

            AddStep("reset offsets", () =>
            {
                offsetStore.CurrentOffset.Value = 0;
                config.SetValue(JukeBoxSetting.GlobalAudioOffset, 0.0);
            });

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(plainSet, framed)
            {
                RelativeSizeAxes = Axes.Both,
            }));
            AddUntilStep("visuals loaded", () => visuals.IsLoaded);

            AddStep("advance source to 5000ms", () =>
            {
                manual.CurrentTime = 5000;
                framed.ProcessFrame();
            });
            AddUntilStep("visual clock at source time", () => System.Math.Abs(visuals.VisualClockTime - 5000) < 1);

            AddStep("beatmap offset +150", () => offsetStore.CurrentOffset.Value = 150);
            AddUntilStep("visual clock shifted +150", () => System.Math.Abs(visuals.VisualClockTime - 5150) < 1);

            AddStep("global offset -50", () => config.SetValue(JukeBoxSetting.GlobalAudioOffset, -50.0));
            AddUntilStep("offsets sum to +100", () => System.Math.Abs(visuals.VisualClockTime - 5100) < 1);

            AddStep("restore offsets and remove", () =>
            {
                offsetStore.CurrentOffset.Value = 0;
                config.SetValue(JukeBoxSetting.GlobalAudioOffset, 0.0);
                Remove(visuals, true);
            });
        }

        // Live skin flip: changing the skin setting rebuilds the chart layer with the newly
        // selected skin (the settings dropdown's real application path — no restart, no re-play).
        [Test]
        public void SkinChangeRebuildsChartWithNewSkin()
        {
            BeatmapVisuals visuals = null!;

            AddStep("start from Argon", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon));
            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(plainSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("chart built with Argon", () => visuals.ChartRenderer is { IsLoaded: true, SelectedSkin: JukeBoxSkin.Argon });

            AddStep("switch to Classic", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Classic));
            AddUntilStep("chart rebuilt with Classic", () => visuals.ChartRenderer is { IsLoaded: true, SelectedSkin: JukeBoxSkin.Classic });

            AddStep("restore Argon", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon));
            AddStep("remove visuals", () => Remove(visuals, true));
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

        // User-reported regression: toggling "Render chart" OFF then ON mid-song rebuilt the
        // lazer layer from the song start and visibly fast-forwarded to the current position.
        // A freshly-constructed layer attached with the clock already mid-song must engage the
        // construction snap and land at the current time within a few updates.
        [Test]
        public void TogglingChartOnMidSongSnapsToCurrentTime()
        {
            BeatmapVisuals visuals = null!;
            Container wrapper = null!;
            var manual = new ManualClock();

            AddStep("create visuals at t=0", () =>
            {
                var framed = new FramedClock(manual);

                // BeatmapVisuals expects its playback clock to be pumped externally
                // (PlaybackController does this in production) — the wrapper pumps it per frame.
                Add(wrapper = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = framed,
                    Child = visuals = new BeatmapVisuals(storyboardSet, framed) { RelativeSizeAxes = Axes.Both },
                });
            });

            AddUntilStep("visuals loaded with chart", () => visuals.IsLoaded && visuals.HasChartLayer);

            AddStep("toggle chart + hitsounds off", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayHitSounds, false);
            });
            AddUntilStep("layer removed", () => visuals.ChartRenderer == null);

            AddStep("advance mid-song", () => manual.CurrentTime = 60000);

            AddStep("toggle chart back on", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.PlayHitSounds, true);
            });

            AddUntilStep("fresh layer snapped to current time (no crawl)",
                () => visuals.ChartRenderer?.LastSeekCatchupFrames is >= 1 and <= 3);
            AddAssert("construction snap engaged exactly once", () => visuals.ChartRenderer?.SeekSnapsEngaged == 1);
            AddUntilStep("frame-stable clock at 60s", () =>
            {
                var clock = visuals.ChartRenderer?.DrawableRuleset?.FrameStableClock;
                return clock != null && !clock.IsCatchingUp.Value && System.Math.Abs(clock.CurrentTime - 60000) < 200;
            });

            AddStep("clean up", () => Remove(wrapper, true));
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

        /// <summary>
        /// The user's report: "play hit sound should be playable when render chart off". The layer
        /// was already being kept alive for exactly this — but at Alpha 0 osu!framework treats a
        /// drawable as absent and skips its whole subtree in UpdateSubTree, so the hidden layer
        /// never ran an Update, its DrawableRuleset's clock never advanced, and its sample gate
        /// stayed shut at the "disabled" it is constructed with. The gate, not the layer's mere
        /// existence, is therefore what this asserts.
        /// </summary>
        [Test]
        public void HitSoundsPlayWithTheChartHiddenAndTheLayerStillUpdates()
        {
            BeatmapVisuals visuals = null!;

            AddStep("hit sounds only", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayHitSounds, true);
            });

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(plainSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("layer built", () => visuals.ChartLayerBuilt);
            AddAssert("but invisible", () => visuals.ChartLayerAlpha == 0 && !visuals.HasChartLayer);
            AddAssert("kept present so it keeps updating", () => visuals.ChartLayerAlwaysPresent);
            AddAssert("hitsounds enabled", () => visuals.HasHitSoundPlayer);

            AddUntilStep("the sample gate actually opens", () => visuals.ChartRenderer?.SamplePlaybackDisabled == false);

            AddStep("show the chart too", () => config.SetValue(JukeBoxSetting.RenderChart, true));
            AddUntilStep("same layer, now visible", () => visuals.HasChartLayer && visuals.ChartLayerAlpha == 1);
            AddAssert("gate still open", () => visuals.ChartRenderer?.SamplePlaybackDisabled == false);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        /// <summary>
        /// The other half of that rule: with nothing wanting the layer, none is built at all — the
        /// beatmap conversion and autoplay generation a layer costs are not paid for a track the
        /// user is neither watching nor listening to.
        /// </summary>
        [Test]
        public void NeitherChartNorHitSoundsBuildsNoLayerAtAll()
        {
            BeatmapVisuals visuals = null!;

            AddStep("both off", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayHitSounds, false);
            });

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(plainSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("no layer", () => !visuals.ChartLayerBuilt);

            AddStep("ask for hit sounds", () => config.SetValue(JukeBoxSetting.PlayHitSounds, true));
            AddUntilStep("now there is one", () => visuals.ChartLayerBuilt);

            AddStep("take them away again", () => config.SetValue(JukeBoxSetting.PlayHitSounds, false));
            AddUntilStep("and it is gone", () => !visuals.ChartLayerBuilt);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        /// <summary>
        /// Chart opacity is alpha on the layer already on screen: it applies live, and — unlike a
        /// mod or a conversion — it must NOT rebuild the layer, which is what holding onto the
        /// layer instance across the change asserts.
        /// </summary>
        [Test]
        public void ChartOpacityAppliesLiveWithoutRebuildingTheLayer()
        {
            BeatmapVisuals visuals = null!;
            JukeBox.Game.LazerPlayer.LazerChartLayer built = null!;

            AddStep("render the chart", () => config.SetValue(JukeBoxSetting.RenderChart, true));
            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(plainSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("chart visible at full opacity", () => visuals.ChartLayerAlpha == 1);
            AddStep("remember the layer", () => built = visuals.ChartRenderer!);

            AddStep("40% opacity", () => config.SetValue(JukeBoxSetting.ChartOpacity, 0.4));
            AddUntilStep("layer is 40% opaque", () => System.Math.Abs(visuals.ChartLayerAlpha - 0.4f) < 0.001f);
            AddAssert("and was never rebuilt", () => ReferenceEquals(visuals.ChartRenderer, built));

            // Fully transparent is the user's business, and it is NOT the hitsounds-off state: the
            // layer stays present and audible, exactly as a hidden one does.
            AddStep("0% opacity", () => config.SetValue(JukeBoxSetting.ChartOpacity, 0.0));
            AddUntilStep("invisible", () => visuals.ChartLayerAlpha == 0);
            AddAssert("still the same, still present", () => ReferenceEquals(visuals.ChartRenderer, built) && visuals.ChartLayerAlwaysPresent);

            AddStep("back to full", () => config.SetValue(JukeBoxSetting.ChartOpacity, 1.0));
            AddUntilStep("opaque again", () => visuals.ChartLayerAlpha == 1);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        /// <summary>
        /// The two split display settings reach the storyboard layer, and reach it separately —
        /// this is the wiring between the Settings rows the user ticks and the lazer layers that
        /// actually draw, which the layer-level tests exercise from the other end.
        /// </summary>
        [Test]
        public void StoryboardAndVideoSettingsReachTheLayerIndependently()
        {
            BeatmapVisuals visuals = null!;

            AddStep("both on", () =>
            {
                config.SetValue(JukeBoxSetting.ShowStoryboard, true);
                config.SetValue(JukeBoxSetting.ShowVideo, true);
            });

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(storyboardSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("layer told both are shown",
                () => visuals.StoryboardLayer.StoryboardShown.Value && visuals.StoryboardLayer.VideoShown.Value);

            AddStep("video off only", () => config.SetValue(JukeBoxSetting.ShowVideo, false));
            AddUntilStep("only the video half followed",
                () => visuals.StoryboardLayer.StoryboardShown.Value && !visuals.StoryboardLayer.VideoShown.Value);
            AddAssert("the layer is still on screen for the storyboard", () => visuals.StoryboardLayer.Alpha == 1);

            AddStep("storyboard off too", () => config.SetValue(JukeBoxSetting.ShowStoryboard, false));
            AddUntilStep("with neither wanted the whole layer stands down", () => visuals.StoryboardLayer.Alpha == 0);

            AddStep("storyboard back on", () => config.SetValue(JukeBoxSetting.ShowStoryboard, true));
            AddUntilStep("and it is back", () => visuals.StoryboardLayer.Alpha == 1);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        /// <summary>Opacity only decides how visible a RENDERED chart is — with rendering off the
        /// layer is hidden outright, whatever the slider says.</summary>
        [Test]
        public void ChartOpacityIsIgnoredWhileTheChartIsNotRendered()
        {
            BeatmapVisuals visuals = null!;

            AddStep("full opacity, hit sounds only", () =>
            {
                config.SetValue(JukeBoxSetting.ChartOpacity, 1.0);
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayHitSounds, true);
            });

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(plainSet, idleClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("layer built", () => visuals.ChartLayerBuilt);
            AddAssert("hidden despite 100%", () => visuals.ChartLayerAlpha == 0);

            AddStep("render it", () => config.SetValue(JukeBoxSetting.RenderChart, true));
            AddUntilStep("now the opacity is what shows", () => visuals.ChartLayerAlpha == 1);

            AddStep("remove visuals", () => Remove(visuals, true));
        }
    }
}
