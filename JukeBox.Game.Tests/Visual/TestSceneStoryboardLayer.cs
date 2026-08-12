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
                // Clock is assigned BEFORE attaching: Add/Child= loads the layer synchronously
                // against the current clock, and framework transform-loops bake Time.Current at
                // creation (TransformSequence.makeTransformsLooping consumes iterations that lie
                // before it) — compiling against the scene's wall-time clock would corrupt loop
                // playback. Production (BeatmapVisuals) likewise installs its playback clock
                // before children load.
                layer = new TransformStoryboardLayer(fixtureSet) { Clock = new FramedClock(manual) };
                Child = layer;
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

        // "L" loops are compiled as framework transform-loops (one set of transforms replayed by
        // evaluation), not unrolled — this asserts the repeat actually happens: a 0→1 fade over
        // each 1000ms iteration must be mid-fade again during the SECOND iteration (without the
        // loop, alpha would already be pinned at 1 there).
        [Test]
        public void LoopCommandsRepeatAcrossIterations()
        {
            TransformStoryboardLayer loopLayer = null!;

            AddStep("create looped-fade layer", () =>
            {
                string osbFile = Path.Combine(tmp, "loop.osb");
                File.WriteAllText(osbFile, """
                    osu file format v14

                    [Events]
                    //Storyboard Layer 0 (Background)
                    Sprite,Background,Centre,"bg.png",320,240
                    _L,1000,3
                    __F,0,0,1000,0,1
                    """);

                var loopSet = new CachedBeatmapSet
                {
                    SetId = 4,
                    Directory = tmp,
                    OsbFile = osbFile,
                };

                // Clock before Add — see SetUpSteps for why (loop transforms bake load-time clock).
                loopLayer = new TransformStoryboardLayer(loopSet) { Clock = new FramedClock(manual) };
                Add(loopLayer);
            });

            AddStep("t=2500 (mid 2nd iteration)", () => manual.CurrentTime = 2500);
            AddUntilStep("sprite visible", () => loopLayer.VisibleSpriteCount == 1);
            AddAssert("alpha mid-fade again (~0.5), i.e. the loop repeated",
                () => Math.Abs(loopLayer.FirstSprite!.Alpha - 0.5f) < 0.02f);

            // Lifetime covers all 3 iterations (1000 + 1000*3 = 4000) and ends after them.
            AddStep("t=4500 (past last iteration)", () => manual.CurrentTime = 4500);
            AddUntilStep("no sprite visible", () => loopLayer.VisibleSpriteCount == 0);
        }

        // Regression test for the hostile-loop-count review finding: Core's loop *unrolling*
        // (SubCommandExpand) would allocate LoopCount copies of every sub-command — LoopCount
        // 2,000,000,000 = OOM/hang inside load with no exception for the malformed-osb fallback
        // to catch. Compiled transform-loops store one set of transforms regardless of iteration
        // count, so this must load promptly. (The huge count also int-overflows Core's
        // FrameEndTime into the negative; the drawable's lifetime clamp turns that into
        // "never alive", same as the old updater which never admitted such objects.)
        [Test]
        public void HugeLoopCountLoadsWithoutUnrolling()
        {
            TransformStoryboardLayer hostileLayer = null!;

            AddStep("create layer with LoopCount=2,000,000,000", () =>
            {
                string osbFile = Path.Combine(tmp, "hostile-loop.osb");
                File.WriteAllText(osbFile, """
                    osu file format v14

                    [Events]
                    //Storyboard Layer 0 (Background)
                    Sprite,Background,Centre,"bg.png",320,240
                    _L,0,2000000000
                    __F,0,0,1000,0,1
                    """);

                var hostileSet = new CachedBeatmapSet
                {
                    SetId = 5,
                    Directory = tmp,
                    OsbFile = osbFile,
                };

                // Clock before Add — see SetUpSteps for why (loop transforms bake load-time clock).
                hostileLayer = new TransformStoryboardLayer(hostileSet) { Clock = new FramedClock(manual) };
                Add(hostileLayer);
            });

            // The until-step's own timeout is the time box: with unrolling this would OOM/hang
            // long before IsLoaded; with transform-loops it loads in milliseconds.
            AddUntilStep("layer loads promptly without unrolling", () => hostileLayer.IsLoaded);

            AddStep("t=500", () => manual.CurrentTime = 500);
            AddAssert("hostile object never becomes alive (overflowed lifetime clamped)",
                () => hostileLayer.VisibleSpriteCount == 0);
        }

        // Regression test for the zero-frame-animation review finding: an Animation whose frame
        // files are all missing must not construct a frameless TextureAnimation (unguarded
        // framework exception in Update/Draw) — the layer skips the drawable entirely, same rule
        // as missing-texture sprites.
        [Test]
        public void AnimationWithMissingFramesDoesNotCrash()
        {
            TransformStoryboardLayer animLayer = null!;

            AddStep("create layer with frameless animation", () =>
            {
                string osbFile = Path.Combine(tmp, "anim-missing.osb");
                File.WriteAllText(osbFile, """
                    osu file format v14

                    [Events]
                    //Storyboard Layer 0 (Background)
                    Animation,Background,Centre,"missing.png",320,240,4,100,LoopForever
                    _F,0,0,5000,1,1
                    """);

                var animSet = new CachedBeatmapSet
                {
                    SetId = 6,
                    Directory = tmp,
                    OsbFile = osbFile,
                };

                // Clock before Add — see SetUpSteps for why (loop transforms bake load-time clock).
                animLayer = new TransformStoryboardLayer(animSet) { Clock = new FramedClock(manual) };
                Add(animLayer);
            });

            AddUntilStep("layer loads without throwing", () => animLayer.IsLoaded);

            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddAssert("animation not present (no frame textures resolved)",
                () => animLayer.VisibleSpriteCount == 0);
        }

        // Positive animation path: frames that DO exist render (frame files use Core's
        // FrameBaseImagePath + index + extension naming).
        [Test]
        public void AnimationWithExistingFramesIsVisible()
        {
            TransformStoryboardLayer animLayer = null!;

            AddStep("create layer with real animation frames", () =>
            {
                File.WriteAllBytes(Path.Combine(tmp, "anim0.png"), solidPng());
                File.WriteAllBytes(Path.Combine(tmp, "anim1.png"), solidPng());

                string osbFile = Path.Combine(tmp, "anim-ok.osb");
                File.WriteAllText(osbFile, """
                    osu file format v14

                    [Events]
                    //Storyboard Layer 0 (Background)
                    Animation,Background,Centre,"anim.png",320,240,2,100,LoopForever
                    _F,0,0,5000,1,1
                    """);

                var animSet = new CachedBeatmapSet
                {
                    SetId = 7,
                    Directory = tmp,
                    OsbFile = osbFile,
                };

                // Clock before Add — see SetUpSteps for why (loop transforms bake load-time clock).
                animLayer = new TransformStoryboardLayer(animSet) { Clock = new FramedClock(manual) };
                Add(animLayer);
            });

            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddUntilStep("animation visible", () => animLayer.VisibleSpriteCount == 1);
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

                // Clock before Add — see SetUpSteps for why (loop transforms bake load-time clock).
                topLeftLayer = new TransformStoryboardLayer(topLeftSet) { Clock = new FramedClock(manual) };
                Add(topLeftLayer);
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

                // Clock before Add — see SetUpSteps for why (loop transforms bake load-time clock).
                garbageLayer = new TransformStoryboardLayer(garbageSet) { Clock = new FramedClock(manual) };
                Add(garbageLayer);
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

                // Clock before Add — see SetUpSteps for why (loop transforms bake load-time clock).
                perfLayer = new TransformStoryboardLayer(perfSet) { Clock = new FramedClock(perfClock) };
                Add(perfLayer);
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

        // Regression test: maps with BOTH a video and a storyboard background event must not have
        // that background painted as a storyboard sprite (Z=-1, above the video layer) — real osu
        // shows the video in place of the background. The .osu Background event ("0,0,...") is
        // parsed as a StoryboardBackgroundObject, which is otherwise compiled into a normal
        // DrawableStoryboardSprite, so with a video present and no other objects the layer must
        // compile nothing at all.
        [Test]
        public void BackgroundObjectDroppedWhenSetHasVideo()
        {
            TransformStoryboardLayer videoBgLayer = null!;

            AddStep("create layer with background event + VideoFile set", () =>
            {
                string osuFile = Path.Combine(tmp, "video-bg.osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [Events]
                    0,0,"bg.png",0,0
                    """);

                var videoBgSet = new CachedBeatmapSet
                {
                    SetId = 8,
                    Directory = tmp,
                    OsbFile = null,
                    VideoFile = Path.Combine(tmp, "movie.mp4"),
                };

                // Clock before Add — see SetUpSteps for why (loop transforms bake load-time clock).
                videoBgLayer = new TransformStoryboardLayer(videoBgSet, osuFile) { Clock = new FramedClock(manual) };
                Add(videoBgLayer);
            });

            AddUntilStep("layer loaded", () => videoBgLayer.IsLoaded);

            AddAssert("no objects compiled (background object dropped)", () => !videoBgLayer.HasObjects);

            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddAssert("nothing visible", () => videoBgLayer.VisibleSpriteCount == 0);
        }

        // Same fixture, but without VideoFile: existing behavior is preserved — the background
        // event still compiles into a visible sprite (Z=-1, behind everything else).
        [Test]
        public void BackgroundObjectKeptWhenSetHasNoVideo()
        {
            TransformStoryboardLayer noVideoBgLayer = null!;

            AddStep("create layer with background event, no VideoFile", () =>
            {
                string osuFile = Path.Combine(tmp, "no-video-bg.osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [Events]
                    0,0,"bg.png",0,0
                    """);

                var noVideoBgSet = new CachedBeatmapSet
                {
                    SetId = 9,
                    Directory = tmp,
                    OsbFile = null,
                };

                // Clock before Add — see SetUpSteps for why (loop transforms bake load-time clock).
                noVideoBgLayer = new TransformStoryboardLayer(noVideoBgSet, osuFile) { Clock = new FramedClock(manual) };
                Add(noVideoBgLayer);
            });

            AddUntilStep("layer loaded", () => noVideoBgLayer.IsLoaded);

            AddAssert("background object compiled (no video to replace it)", () => noVideoBgLayer.HasObjects);

            AddStep("t=2500", () => manual.CurrentTime = 2500);
            AddAssert("background sprite visible", () => noVideoBgLayer.VisibleSpriteCount == 1);
        }

        // 1x1 red pixel PNG — content is irrelevant, only that it decodes to a valid texture.
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }
}
