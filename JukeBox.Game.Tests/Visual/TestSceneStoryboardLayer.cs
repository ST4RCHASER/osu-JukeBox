using System;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Storyboard;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osuTK;

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

        // Regression test for the OriginOffset Y-flip fix: Core's AnchorConvert expresses
        // "TopLeft" origin as a Y-up (-0.5, +0.5) offset from sprite centre, but osu!framework's
        // Sprite.OriginPosition is Y-down pixel space (0,0 == texture top-left). A naive
        // `0.5f + offset.Y` mapping would place the origin at the texture's *bottom*-left instead.
        [Test]
        public void TopLeftOriginIsTextureTopLeftCorner()
        {
            StoryboardLayer topLeftLayer = null!;

            AddStep("create top-left-origin layer", () =>
            {
                string osbFile = Path.Combine(tmp, "topleft.osb");
                File.WriteAllText(osbFile, """
                    osu file format v14

                    [Events]
                    Sprite,Background,TopLeft,"bg.png",320,240
                    _F,0,0,5000,1,1
                    """);

                var topLeftSet = new CachedBeatmapSet
                {
                    SetId = 2,
                    Directory = tmp,
                    OsbFile = osbFile,
                };

                Add(topLeftLayer = new StoryboardLayer(topLeftSet));
                topLeftLayer.Clock = new FramedClock(manual);
            });

            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddUntilStep("sprite realised", () => topLeftLayer.FirstSprite != null);

            // Texture is 1x1, so OriginPosition.Y is exactly the origin fraction times 1: the
            // fixed formula gives fracY = 0.5 - (+0.5) = 0 (top, i.e. pixel Y=0); the pre-fix
            // formula would have given fracY = 0.5 + (+0.5) = 1 (bottom, i.e. pixel Y=1) instead.
            AddAssert("origin at texture top-left corner (0,0), not bottom-left",
                () => topLeftLayer.FirstSprite!.OriginPosition == Vector2.Zero);
        }

        // Regression test for the crash-on-malformed-storyboard bug: Radio auto-downloads
        // arbitrary third-party .osz files, and Core's parser is strict — e.g. an unrecognised
        // Layer token makes StoryboardReader's Enum.Parse throw outright, uncaught, straight out
        // of StoryboardLoader.Load. That must not take the whole app down; StoryboardLayer.load
        // now catches it and falls back to an empty (nothing-visible) storyboard instead.
        [Test]
        public void MalformedOsbDoesNotCrashAndFallsBackToEmptyStoryboard()
        {
            StoryboardLayer garbageLayer = null!;

            AddStep("create layer from malformed .osb", () =>
            {
                string osbFile = Path.Combine(tmp, "garbage.osb");
                File.WriteAllText(osbFile, """
                    osu file format v14

                    [Events]
                    //Storyboard Layer 0 (Background)
                    Sprite,NotARealLayer,Centre,"bg.png",320,240
                    _M,0,0,5000,320,240,320,240
                    """);

                var garbageSet = new CachedBeatmapSet
                {
                    SetId = 3,
                    Directory = tmp,
                    OsbFile = osbFile,
                };

                Add(garbageLayer = new StoryboardLayer(garbageSet));
                garbageLayer.Clock = new FramedClock(manual);
            });

            AddUntilStep("layer loads without throwing", () => garbageLayer.IsLoaded);

            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddAssert("no sprites visible (fell back to empty storyboard)", () => garbageLayer.VisibleSpriteCount == 0);
        }

        // 1x1 red pixel PNG — content is irrelevant, only that it decodes to a valid texture.
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }
}
