#nullable enable

// Regression coverage for reopen #6's second issue: "the orange hit-glow that blooms around the
// drum on hits is clipped to the drum square's bounds". Empirically identified the exact
// mechanism: osu.Game.Rulesets.Taiko.Skinning.Legacy.LegacyHalfDrum (internal — reached here only
// through its public Container surface, via the fixed "Left Half"/"Right Half" names its own
// source gives its two instances) sets Masking = true unconditionally — fine for skins whose
// "taiko-drum-outer"/"taiko-drum-inner" hit-flash textures fit inside the drum's own ~180-unit-
// wide box, but any skin (like the one behind this report) that intentionally ships LARGER
// textures for an outward-blooming flash gets that flash clipped flush to the drum's tight box
// instead. LazerChartLayer.unmaskLegacyTaikoDrumFlash() drops that masking after load.

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;
using osu.Framework.Timing;
using SixLabors.ImageSharp;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneTaikoDrumFlashMasking : JukeBoxTestScene
    {
        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        private string tmp = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
        }

        [Test]
        public void DrumFlashIsNotClippedToTheDrumsOwnBox()
        {
            BeatmapVisuals visuals = null!;
            var manual = new ManualClock();
            FramedClock? clock = null;

            AddStep("enable chart, Classic skin", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Classic);
            });

            AddStep("create visuals with a legacy beatmap skin providing an oversized drum flash texture", () =>
            {
                string mapDir = Path.Combine(tmp, "drumglow");
                Directory.CreateDirectory(mapDir);

                // A texture DELIBERATELY much larger than the drum's own box, mimicking a
                // "glow/bloom" design meant to bleed outward when the drum is struck.
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-drum-outer.png"), solidPng(512, 512));
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-drum-inner.png"), solidPng(512, 512));
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-bar-left.png"), solidPng(64, 64));
                File.WriteAllBytes(Path.Combine(mapDir, "bg.png"), solidPng(4, 4));

                string osuFile = Path.Combine(mapDir, "taiko [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 1

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,5000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 17,
                    Directory = mapDir,
                    BackgroundFile = Path.Combine(mapDir, "bg.png"),
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                clock = new FramedClock(manual);
                Add(visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);

            // Drive past the hit so autoplay's replay actually PRESSES the drum (triggers
            // LegacyHalfDrum.OnPressed via the normal input pipeline, not just judging the note).
            AddStep("advance past the hit", () =>
            {
                manual.CurrentTime = 5010;
                clock!.ProcessFrame();
            });

            AddAssert("both half-drum containers found", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                return playfield.ChildrenOfType<Container>().Count(c => c.Name is "Left Half" or "Right Half") == 2;
            });

            AddAssert("half-drum containers are NOT masking (drum flash allowed to bloom outward)", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                return playfield.ChildrenOfType<Container>()
                                 .Where(c => c.Name is "Left Half" or "Right Half")
                                 .All(c => !c.Masking);
            });

            AddAssert("the oversized flash texture genuinely exceeds the drum's own box (fixture is meaningful)", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                var half = playfield.ChildrenOfType<Container>().First(c => c.Name is "Left Half" or "Right Half");
                var sprite = half.ChildrenOfType<Sprite>().First();

                float halfWidth = half.ScreenSpaceDrawQuad.TopRight.X - half.ScreenSpaceDrawQuad.TopLeft.X;
                float spriteWidth = Math.Abs(sprite.ScreenSpaceDrawQuad.TopRight.X - sprite.ScreenSpaceDrawQuad.TopLeft.X);

                return spriteWidth > halfWidth * 1.5f;
            });

            AddStep("remove visuals", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                Remove(visuals, true);
            });
        }

        private static byte[] solidPng(int width, int height)
        {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height,
                new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 128, 0, 255));
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }
    }
}
