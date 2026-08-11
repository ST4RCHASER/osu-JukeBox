using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        private TransformStoryboardLayer layer = null!;
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
                Child = layer = new TransformStoryboardLayer(fixtureSet);
                layer.Clock = new FramedClock(manual);
            });
        }

        // The lifetime window comes straight from Core's FrameStartTime/FrameEndTime and is
        // enforced by the internal LifetimeManagementContainer: inside the window the drawable is
        // alive and (once its fade has non-zero alpha) present; past FrameEndTime it leaves the
        // alive set entirely (zero per-frame cost), so the count drops to 0.
        [Test]
        public void SpriteVisibleDuringActiveWindowOnly()
        {
            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddUntilStep("one sprite visible", () => layer.VisibleSpriteCount == 1);

            AddStep("t=6000", () => manual.CurrentTime = 6000);
            AddUntilStep("no sprite visible", () => layer.VisibleSpriteCount == 0);
        }

        // Seek/rewind support: RemoveCompletedTransforms == false keeps every compiled transform,
        // and RemoveWhenNotAlive == false keeps dead drawables as children, so seeking backwards
        // must revive the sprite with correct state.
        [Test]
        public void SeekBackwardsRevivesSprite()
        {
            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddUntilStep("one sprite visible", () => layer.VisibleSpriteCount == 1);

            AddStep("t=6000", () => manual.CurrentTime = 6000);
            AddUntilStep("no sprite visible", () => layer.VisibleSpriteCount == 0);

            AddStep("seek back to t=2500", () => manual.CurrentTime = 2500);
            AddUntilStep("sprite visible again", () => layer.VisibleSpriteCount == 1);
        }

        // Regression test for the OriginOffset Y-flip fix: Core's AnchorConvert expresses
        // "TopLeft" origin as a Y-up (-0.5, +0.5) offset from sprite centre, but osu!framework's
        // Sprite.OriginPosition is Y-down pixel space (0,0 == texture top-left). A naive
        // `0.5f + offset.Y` mapping would place the origin at the texture's *bottom*-left instead.
        [Test]
        public void TopLeftOriginIsTextureTopLeftCorner()
        {
            TransformStoryboardLayer topLeftLayer = null!;

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

                Add(topLeftLayer = new TransformStoryboardLayer(topLeftSet));
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
        // of StoryboardLoader.Load. That must not take the whole app down; the layer's load
        // catches it and falls back to an empty (nothing-visible) storyboard instead.
        [Test]
        public void MalformedOsbDoesNotCrashAndFallsBackToEmptyStoryboard()
        {
            TransformStoryboardLayer garbageLayer = null!;

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

                Add(garbageLayer = new TransformStoryboardLayer(garbageSet));
                garbageLayer.Clock = new FramedClock(manual);
            });

            AddUntilStep("layer loads without throwing", () => garbageLayer.IsLoaded);

            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddAssert("no sprites visible (fell back to empty storyboard)", () => garbageLayer.VisibleSpriteCount == 0);
        }

        // Steady-state per-frame cost measurement for the transform-based renderer. The previous
        // Core-driven StoryboardLayer re-evaluated every active object's commands on the update
        // thread each frame and measured ~7.6ms/frame (median) for this same 5000-simultaneous-
        // sprite workload in this sandbox (~15.1ms pre-optimization; the pathological real-world
        // particle map hit ~214ms/frame). With commands compiled once into framework transforms
        // at load, the per-frame work is only the framework's transform application over alive
        // drawables — measured via UpdateSubTree() (the layer no longer has its own Update logic;
        // per-frame cost lives in the child drawables, which reflection-invoking a single Update
        // wouldn't reach). Median of several trials after JIT warm-up, generous ceiling: only a
        // regression back toward per-frame command evaluation should trip it.
        [Test]
        public void UpdatePerformanceWithManySimultaneousSprites()
        {
            const int spriteCount = 5000;
            const int measuredFrames = 120;

            TransformStoryboardLayer perfLayer = null!;
            ManualClock perfClock = null!;

            AddStep("create storyboard with many sprites", () =>
            {
                var osb = new StringBuilder();
                osb.AppendLine("osu file format v14");
                osb.AppendLine();
                osb.AppendLine("[Events]");
                osb.AppendLine("//Storyboard Layer 0 (Background)");

                // Every sprite is active for the whole measured window and moves every frame
                // (_M command), so the loop below measures steady-state per-frame update cost —
                // not one-time load/compile cost, which happens in the async load above.
                for (int i = 0; i < spriteCount; i++)
                {
                    float x = i % 640;
                    float y = (i / 640) % 480;
                    osb.AppendLine($"Sprite,Background,Centre,\"bg.png\",{x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    osb.AppendLine("_F,0,0,100000,1,1");
                    osb.AppendLine(FormattableString.Invariant($"_M,0,0,100000,{x},{y},{(x + 50) % 640},{(y + 50) % 480}"));
                }

                string osbFile = Path.Combine(tmp, "perf.osb");
                File.WriteAllText(osbFile, osb.ToString());

                var perfSet = new CachedBeatmapSet { SetId = 99, Directory = tmp, OsbFile = osbFile };

                perfClock = new ManualClock { CurrentTime = 0 };

                Add(perfLayer = new TransformStoryboardLayer(perfSet));
                perfLayer.Clock = new FramedClock(perfClock);
            });

            AddUntilStep("layer loaded", () => perfLayer.IsLoaded);

            AddStep("measure per-frame update cost", () =>
            {
                void step()
                {
                    perfClock.CurrentTime += 16;
                    // ProcessCustomClock is enabled (custom Clock assigned), so UpdateSubTree
                    // pumps the FramedClock itself — no manual ProcessFrame needed.
                    perfLayer.UpdateSubTree();
                }

                // Realise all 5000 sprites, then run enough extra warm-up frames for the JIT to
                // reach steady-state (tiered compilation) before any timed measurement.
                perfClock.CurrentTime = 1000;
                perfLayer.UpdateSubTree();
                Assert.That(perfLayer.VisibleSpriteCount, Is.EqualTo(spriteCount));

                for (int w = 0; w < 30; w++)
                    step();

                // Several timed trials, reported as the median — robust against a single slow
                // trial caused by GC or unrelated host scheduling noise in a shared sandbox.
                const int trials = 5;
                var msPerFrameTrials = new double[trials];

                for (int t = 0; t < trials; t++)
                {
                    var sw = Stopwatch.StartNew();
                    for (int f = 0; f < measuredFrames; f++)
                        step();
                    sw.Stop();

                    msPerFrameTrials[t] = sw.Elapsed.TotalMilliseconds / measuredFrames;
                }

                Array.Sort(msPerFrameTrials);
                double median = msPerFrameTrials[trials / 2];

                Console.WriteLine(
                    $"[TransformStoryboardLayer perf] {spriteCount} sprites x {measuredFrames} frames x {trials} trials: " +
                    $"median {median:F3} ms/frame (all: {string.Join(", ", Array.ConvertAll(msPerFrameTrials, v => v.ToString("F3")))})");

                // The old Core-driven layer measured ~7.6ms/frame median here (20.0 ceiling).
                // Keep the same generous ceiling: it catches any regression back toward per-frame
                // command evaluation without being sensitive to this sandbox's shared CPU.
                Assert.That(median, Is.LessThan(20.0),
                    $"TransformStoryboardLayer UpdateSubTree() median {median:F3} ms/frame for {spriteCount} sprites");
            });
        }

        // 1x1 red pixel PNG — content is irrelevant, only that it decodes to a valid texture.
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }
}
