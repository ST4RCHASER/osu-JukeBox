#nullable enable

using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
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

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
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
