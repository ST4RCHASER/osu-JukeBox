using System;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Storyboard;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Timing;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneStoryboardLayer : JukeBoxTestScene
    {
        private readonly ManualClock manual = new ManualClock();

        private StoryboardLayer layer = null!;
        private string tmp = null!;
        private CachedBeatmapSet fixtureSet = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            string imageFile = Path.Combine(tmp, "bg.png");
            File.WriteAllBytes(imageFile, solidPng());

            string osbFile = Path.Combine(tmp, "map.osb");
            File.WriteAllText(osbFile, """
                osu file format v14

                [Events]
                //Storyboard Layer 0 (Background)
                Sprite,Background,Centre,"bg.png",320,240
                _F,0,0,5000,0,1
                _M,0,0,5000,320,240,320,240
                """);

            fixtureSet = new CachedBeatmapSet
            {
                SetId = 1,
                Directory = tmp,
                OsbFile = osbFile,
            };

            manual.CurrentTime = 0;
        }

        // NOTE: deliberately NOT deleting `tmp` here — see TestScenePlaybackController for why
        // (TestScene runs queued AddStep bodies from a base-class teardown hook that fires after
        // this derived class's own [TearDown], so a synchronous delete here would race the
        // fixture files out from under still-pending steps).

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create layer", () =>
            {
                Child = layer = new StoryboardLayer(fixtureSet);
                layer.Clock = new FramedClock(manual);
            });
        }

        [Test]
        public void SpriteVisibleDuringActiveWindowOnly()
        {
            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddUntilStep("one sprite visible", () => layer.VisibleSpriteCount == 1);

            AddStep("t=6000", () => manual.CurrentTime = 6000);
            AddUntilStep("no sprite visible", () => layer.VisibleSpriteCount == 0);
        }

        // 1x1 red pixel PNG — content is irrelevant, only that it decodes to a valid texture.
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }
}
