#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Storyboards;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The lazer storyboard layer hosts osu!lazer's real storyboard renderer: real fixture
    /// .osu/.osb content decodes through LegacyStoryboardDecoder (which owns the osb/osu merge,
    /// variables and layers), malformed files fall back to an empty storyboard instead of
    /// crashing, and storyboard Sample events resolve. Deep rendering assertions are intentionally
    /// absent — lazer tests its own storyboard pipeline; these cover our hosting/decode/fallback
    /// arrangement.
    /// </summary>
    [TestFixture]
    public partial class TestSceneLazerStoryboard : JukeBoxTestScene
    {
        private readonly ManualClock manual = new ManualClock();

        private string dir = null!;
        private Container host = null!;
        private LazerStoryboardLayer layer = null!;

        /// <summary>The game-wide services the runner (a real JukeBoxGameBase) caches — the same
        /// ones the app's own settings write to, which is what makes these tests exercise the real
        /// path rather than a stand-in.</summary>
        [Resolved]
        private StoryboardLayerVisibility layerVisibility { get; set; } = null!;

        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

        [TearDownSteps]
        public void TearDownSteps()
        {
            // This scene writes into the shared test-browser config — put back what the rest of the
            // suite expects.
            AddStep("show every storyboard layer again",
                () => config.SetValue(JukeBoxSetting.HiddenStoryboardLayers, string.Empty));
        }

        private static string fixture(string name)
            => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name);

        // Takes a factory: the set is built inside an earlier AddStep, so its value must be read
        // at step-run time, not at step-registration time.
        private void createLayer(System.Func<CachedBeatmapSet> set)
        {
            AddStep("create layer", () =>
            {
                manual.CurrentTime = 0;

                Add(host = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerStoryboardLayer(set()),
                });
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded);
        }

        private void removeLayer() => AddStep("remove layer", () => Remove(host, true));

        // ---- the storyboard / video split, and the per-layer toggles ----
        //
        // lazer models the video as one more storyboard layer alongside Background/Fail/Pass/
        // Foreground/Overlay, which is exactly what makes "video without storyboard" (and the
        // reverse) possible: both settings switch layers, they just switch different ones.

        /// <summary>The layer set is fixed by the decoder, not by what a map happens to use — so the
        /// settings can offer a stable list rather than one that changes with the song.</summary>
        [Test]
        public void EveryStoryboardCarriesTheSameFixedLayerSet()
        {
            AddAssert("an empty storyboard already has all six layers", () =>
                new Storyboard().Layers.Select(l => l.Name).OrderBy(n => n)
                                .SequenceEqual(new[] { "Background", "Fail", "Foreground", "Overlay", "Pass", "Video" }));

            AddAssert("and the five we offer are the non-video ones", () =>
                StoryboardLayerVisibility.All.Select(l => l.ToString()).OrderBy(n => n)
                                         .SequenceEqual(new[] { "Background", "Fail", "Foreground", "Overlay", "Pass" }));
        }

        [Test]
        public void VideoAndStoryboardSwitchIndependently()
        {
            createHeronLayer();

            AddAssert("everything drawn to begin with",
                () => layer.LayerAlpha("Video") == 1 && layer.LayerAlpha("Foreground") == 1);

            AddStep("video off", () => layer.VideoShown.Value = false);
            AddUntilStep("only the video went", () => layer.LayerAlpha("Video") == 0 && layer.LayerAlpha("Foreground") == 1);

            AddStep("video back, storyboard off", () =>
            {
                layer.VideoShown.Value = true;
                layer.StoryboardShown.Value = false;
            });
            AddUntilStep("now only the video is drawn", () =>
                layer.LayerAlpha("Video") == 1
                && StoryboardLayerVisibility.All.All(l => layer.LayerAlpha(l.ToString()) == 0));

            AddStep("storyboard back", () => layer.StoryboardShown.Value = true);
            AddUntilStep("everything drawn again", () =>
                layer.LayerAlpha("Video") == 1 && layer.LayerAlpha("Foreground") == 1);

            removeLayer();
        }

        [Test]
        public void ALayerToggleHidesExactlyThatLayerLive()
        {
            createHeronLayer();

            AddStep("hide the foreground", () => layerVisibility.Shown(StoryboardLayerKind.Foreground).Value = false);
            AddUntilStep("foreground gone, the rest untouched", () =>
                layer.LayerAlpha("Foreground") == 0
                && layer.LayerAlpha("Background") == 1
                && layer.LayerAlpha("Overlay") == 1
                && layer.LayerAlpha("Video") == 1);

            AddStep("hide the background too", () => layerVisibility.Shown(StoryboardLayerKind.Background).Value = false);
            AddUntilStep("both gone", () => layer.LayerAlpha("Foreground") == 0 && layer.LayerAlpha("Background") == 0);

            AddStep("show the foreground again", () => layerVisibility.Shown(StoryboardLayerKind.Foreground).Value = true);
            AddUntilStep("it comes straight back, live", () => layer.LayerAlpha("Foreground") == 1 && layer.LayerAlpha("Background") == 0);

            removeLayer();
        }

        /// <summary>A per-layer choice is a choice about the storyboard, so it cannot resurrect one
        /// the master toggle has switched off.</summary>
        [Test]
        public void LayerChoicesCannotOverrideTheMasterStoryboardToggle()
        {
            createHeronLayer();

            AddStep("storyboard off, every layer wanted", () =>
            {
                layer.StoryboardShown.Value = false;

                foreach (var kind in StoryboardLayerVisibility.All)
                    layerVisibility.Shown(kind).Value = true;
            });

            AddUntilStep("nothing storyboard-side is drawn",
                () => StoryboardLayerVisibility.All.All(l => layer.LayerAlpha(l.ToString()) == 0));

            removeLayer();
        }

        [Test]
        public void HiddenLayersRoundTripThroughTheConfigList()
        {
            AddStep("hide two layers", () =>
            {
                layerVisibility.Shown(StoryboardLayerKind.Overlay).Value = false;
                layerVisibility.Shown(StoryboardLayerKind.Pass).Value = false;
            });

            AddAssert("persisted in layer order", () => config.Get<string>(JukeBoxSetting.HiddenStoryboardLayers) == "Pass,Overlay");

            AddStep("write a list back", () => config.SetValue(JukeBoxSetting.HiddenStoryboardLayers, "Background"));
            AddAssert("service followed", () => layerVisibility.IsHidden(StoryboardLayerKind.Background)
                                                && !layerVisibility.IsHidden(StoryboardLayerKind.Overlay)
                                                && !layerVisibility.IsHidden(StoryboardLayerKind.Pass));

            AddStep("junk names are dropped, not fatal", () => config.SetValue(JukeBoxSetting.HiddenStoryboardLayers, "Nonsense,Overlay"));
            AddAssert("only the real one applied", () => layerVisibility.IsHidden(StoryboardLayerKind.Overlay)
                                                         && !layerVisibility.IsHidden(StoryboardLayerKind.Background));

            AddStep("restore", () => config.SetValue(JukeBoxSetting.HiddenStoryboardLayers, string.Empty));
        }

        /// <summary>
        /// The user's report: with "Remove storyboard mask" on, storyboard content outside the box
        /// still would not draw. Releasing OUR box was never enough — every storyboard layer but
        /// Video declares Masking, so lazer clips each one's elements to the storyboard's own area
        /// whatever the app around it does. The release has to reach that.
        /// </summary>
        [Test]
        public void ReleasingTheStoryboardTurnsOffLazersOwnPerLayerMasking()
        {
            createHeronLayer();

            AddUntilStep("lazer masks its layers to begin with",
                () => layer.LayerMasking("Foreground") == true && layer.LayerMasking("Background") == true);

            AddStep("release the storyboard", () => layer.StoryboardReleased.Value = true);
            AddUntilStep("every storyboard layer stops masking itself",
                () => StoryboardLayerVisibility.All.All(l => layer.LayerMasking(l.ToString()) == false));

            AddStep("mask it again", () => layer.StoryboardReleased.Value = false);
            AddUntilStep("and lazer's masking is back",
                () => StoryboardLayerVisibility.All.All(l => layer.LayerMasking(l.ToString()) == true));

            removeLayer();
        }

        /// <summary>
        /// The Fail layer is failing-only, so lazer keeps it switched off over a passing play —
        /// which made its toggle a dead row. Switching it on now forces the layer drawn (user
        /// request); it stays off by default.
        /// </summary>
        [Test]
        public void TheFailLayerIsForcedOnWhenItsToggleIsOn()
        {
            createHeronLayer();

            AddStep("everything at its default", () => config.SetValue(JukeBoxSetting.HiddenStoryboardLayers,
                string.Join(',', StoryboardLayerVisibility.HiddenByDefault)));

            AddUntilStep("Fail starts hidden, and lazer keeps it disabled",
                () => layer.LayerAlpha("Fail") == 0 && layer.LayerEnabled("Fail") == false);
            AddAssert("while the passing layers are drawn",
                () => layer.LayerAlpha("Pass") == 1 && layer.LayerEnabled("Pass") == true);

            AddStep("switch Fail on", () => layerVisibility.Shown(StoryboardLayerKind.Fail).Value = true);

            AddUntilStep("it is forced drawn despite never failing",
                () => layer.LayerAlpha("Fail") == 1 && layer.LayerEnabled("Fail") == true);

            AddStep("switch it off again", () => layerVisibility.Shown(StoryboardLayerKind.Fail).Value = false);
            AddUntilStep("and it goes back to hidden", () => layer.LayerAlpha("Fail") == 0);

            removeLayer();
        }

        /// <summary>
        /// Diagnostic: what is actually masking a storyboard sprite, all the way up to our own
        /// layer. "Release the storyboard" can only be true if this comes back empty.
        /// </summary>
        [Test]
        public void NothingMasksAStoryboardSpriteOnceReleased()
        {
            createHeronLayer();

            AddUntilStep("sprites exist", () => layer.ChildrenOfType<osu.Game.Storyboards.Drawables.DrawableStoryboardSprite>().Any());

            AddStep("release the storyboard", () => layer.StoryboardReleased.Value = true);

            AddAssert("nothing between the sprite and our layer masks", () =>
            {
                var sprite = layer.ChildrenOfType<osu.Game.Storyboards.Drawables.DrawableStoryboardSprite>().First();
                var masking = new List<string>();

                for (CompositeDrawable? p = sprite.Parent; p != null; p = p.Parent)
                {
                    if (p.Masking)
                        masking.Add(p.GetType().Name);

                    if (ReferenceEquals(p, layer))
                        break;
                }

                if (masking.Count == 0)
                    return true;

                throw new Exception("still masked by: " + string.Join(", ", masking));
            });

            removeLayer();
        }

        private void createHeronLayer()
        {
            CachedBeatmapSet set = null!;

            AddStep("show every layer, Fail included", () => config.SetValue(JukeBoxSetting.HiddenStoryboardLayers, string.Empty));

            AddStep("extract heron fixture", () =>
            {
                File.Copy(fixture("heron_beginner.osu"), Path.Combine(dir, "heron [Beginner].osu"));
                File.Copy(fixture("heron.osb"), Path.Combine(dir, "heron.osb"));

                set = new CachedBeatmapSet
                {
                    SetId = 165202,
                    Directory = dir,
                    OsbFile = Path.Combine(dir, "heron.osb"),
                    OsuFiles = { Path.Combine(dir, "heron [Beginner].osu") },
                    PreferredOsuFile = Path.Combine(dir, "heron [Beginner].osu"),
                };
            });

            createLayer(() => set);
            AddUntilStep("storyboard built its layers", () => layer.LayerAlpha("Foreground") != null);
        }

        [Test]
        public void RealFixtureStoryboardLoads()
        {
            CachedBeatmapSet set = null!;

            AddStep("extract heron fixture", () =>
            {
                File.Copy(fixture("heron_beginner.osu"), Path.Combine(dir, "heron [Beginner].osu"));
                File.Copy(fixture("heron.osb"), Path.Combine(dir, "heron.osb"));

                set = new CachedBeatmapSet
                {
                    SetId = 165202,
                    Directory = dir,
                    OsbFile = Path.Combine(dir, "heron.osb"),
                    OsuFiles = { Path.Combine(dir, "heron [Beginner].osu") },
                    PreferredOsuFile = Path.Combine(dir, "heron [Beginner].osu"),
                };
            });

            createLayer(() => set);

            AddAssert("storyboard has drawables", () => layer.HasObjects);
            AddAssert("elements decoded", () => layer.ElementCount > 0);
            AddStep("advance mid-storyboard", () => manual.CurrentTime = 30000);
            AddUntilStep("still alive mid-storyboard", () => layer.IsLoaded && !layer.Storyboard.Layers.Sum(l => l.Elements.Count).Equals(0));

            removeLayer();
        }

        [Test]
        public void OsbOnlySetLoads()
        {
            CachedBeatmapSet set = null!;

            AddStep("build osb-only set", () =>
            {
                string osb = Path.Combine(dir, "only.osb");
                File.WriteAllText(osb, """
                    osu file format v14

                    [Events]
                    Sprite,Foreground,Centre,"sb\pic.png",320,240
                    _F,0,0,5000,0,1
                    """);

                set = new CachedBeatmapSet { SetId = 1, Directory = dir, OsbFile = osb };
            });

            createLayer(() => set);

            AddAssert("element decoded from osb", () => layer.ElementCount == 1);

            removeLayer();
        }

        // osu's spec allows an Animation object line to omit its trailing loopType column —
        // lazer's decoder defaults it to LoopForever natively (the old Core-based parser threw).
        [Test]
        public void AnimationWithoutLoopTypeParses()
        {
            CachedBeatmapSet set = null!;

            AddStep("build 8-column animation set", () =>
            {
                string osb = Path.Combine(dir, "anim.osb");
                File.WriteAllText(osb, """
                    osu file format v14

                    [Events]
                    Animation,Foreground,Centre,"frame.png",320,240,4,110
                    _F,0,0,5000,0,1
                    """);

                set = new CachedBeatmapSet { SetId = 2, Directory = dir, OsbFile = osb };
            });

            createLayer(() => set);

            AddAssert("animation decoded", () => layer.ElementCount == 1);

            removeLayer();
        }

        // Radio downloads arbitrary community content; hostile/garbage .osb files must degrade to
        // an empty storyboard, never crash BackgroundDependencyLoader.
        [Test]
        public void GarbageStoryboardFallsBackToEmpty()
        {
            CachedBeatmapSet set = null!;

            AddStep("build garbage set", () =>
            {
                string osb = Path.Combine(dir, "garbage.osb");
                File.WriteAllBytes(osb, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 });

                set = new CachedBeatmapSet { SetId = 3, Directory = dir, OsbFile = osb };
            });

            createLayer(() => set);

            AddAssert("fell back to empty storyboard", () => layer.ElementCount == 0 && !layer.HasObjects);

            removeLayer();
        }

        // Storyboard Sample audio events (keysounded storyboards like set 92190) decode and build
        // their drawable; the sample itself resolves from the beatmap folder through the folder
        // skin at play time.
        [Test]
        public void StoryboardSampleEventLoads()
        {
            CachedBeatmapSet set = null!;

            AddStep("build keysound set", () =>
            {
                string osb = Path.Combine(dir, "keysound.osb");
                File.WriteAllText(osb, """
                    osu file format v14

                    [Events]
                    Sample,1000,3,"key.wav",80
                    """);

                set = new CachedBeatmapSet { SetId = 92190, Directory = dir, OsbFile = osb };
            });

            createLayer(() => set);

            AddAssert("sample event decoded", () => layer.Storyboard.Layers.SelectMany(l => l.Elements).OfType<StoryboardSampleInfo>().Count() == 1);
            AddStep("advance past sample time", () => manual.CurrentTime = 1500);
            AddUntilStep("layer alive after sample fired", () => layer.IsLoaded);

            removeLayer();
        }

        // The decoder owns the .osu/.osb merge (lazer's WorkingBeatmapCache pattern) — elements
        // from both files end up in the one storyboard.
        [Test]
        public void DecodeMergesOsuAndOsbEvents()
        {
            AddAssert("both files' events merged", () =>
            {
                string osu = Path.Combine(dir, "merge [x].osu");
                string osb = Path.Combine(dir, "merge.osb");

                File.WriteAllText(osu, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 0

                    [Events]
                    Sprite,Foreground,Centre,"from_osu.png",320,240
                    _F,0,0,5000,0,1
                    """);

                File.WriteAllText(osb, """
                    osu file format v14

                    [Events]
                    Sprite,Foreground,Centre,"from_osb.png",320,240
                    _F,0,0,5000,0,1
                    """);

                var storyboard = LazerStoryboardLayer.DecodeStoryboard(osu, osb);
                var paths = storyboard.Layers.SelectMany(l => l.Elements).Select(e => e.Path).ToList();

                return paths.Contains("from_osu.png") && paths.Contains("from_osb.png");
            });
        }
    }
}
