#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneMainScreen : JukeBoxManualInputTestScene
    {
        private MusicQueue queue = null!;
        private RadioService radio = null!;
        private BeatmapCache cache = null!;
        private PlaybackController playback = null!;
        private Jukebox jukebox = null!;
        private JukeBoxConfigManager config = null!;
        private StubMirror mirror = null!;

        private string tmp = null!;
        private Container uiContainer = null!;
        private MainScreen screen = null!;

        // Own full local dependency graph (mirror/queue/radio/cache/playback/jukebox/config), same
        // approach as TestSceneNowPlayingBar: MainScreen resolves these via [Resolved], and giving
        // it a StubMirror here (rather than the real network MirrorChain JukeBoxGameBase wires up)
        // keeps this test off the network. See CreateChildDependencies note in TestSceneNowPlayingBar
        // for why this runs once per fixture rather than per-test.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            mirror = new StubMirror();
            playback = new PlaybackController();
            queue = new MusicQueue();
            radio = new RadioService(mirror);
            cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);
            jukebox = new Jukebox(queue, radio, cache, playback);
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-main-screen-test", Path.GetRandomFileName())));

            var deps = new DependencyContainer(parent);
            deps.CacheAs<IBeatmapMirror>(mirror);
            deps.CacheAs(playback);
            deps.CacheAs(queue);
            deps.CacheAs(jukebox);
            deps.Cache(config);
            return deps;
        }

        // playback/jukebox added exactly once, here — NOT inside SetUpSteps, which rebuilds
        // uiContainer's content on every [Test]. See TestSceneNowPlayingBar's LoadComplete for why.
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Add(playback);
            Add(jukebox);
            Add(uiContainer = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create main screen", () =>
            {
                queue.Items.Clear();
                mirror.Sets.Clear();
                config.SetValue(JukeBoxSetting.UiLayout, UiLayout.ThreeColumn);
                config.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Compact);
                screen = new MainScreen { RelativeSizeAxes = Axes.Both };
                // MainScreen is a Screen — osu!framework requires a Screen to be hosted by a
                // ScreenStack (see JukeBoxGame's own top-level screenStack.Push(new MainScreen())).
                uiContainer.Child = new ScreenStack(screen) { RelativeSizeAxes = Axes.Both };
            });
        }

        [Test]
        public void ThreeColumnLayoutStartsWithBothColumnsShownAndQueueTabActive()
        {
            AddAssert("left column shown", () => screen.LeftColumn.Alpha == 1);
            AddAssert("right column shown", () => screen.RightColumn.Alpha == 1);
            AddAssert("queue tab active", () => screen.ChildrenOfType<QueuePanel>().Single().Alpha == 1);
            AddAssert("settings tab inactive", () => screen.ChildrenOfType<SettingsOverlay>().Single().Alpha == 0);
        }

        // Regression coverage for a true contained player: the video/storyboard/background
        // visuals must never render outside the boxed centre panel — not behind the side columns,
        // not behind the bottom bar. Masking on the box is what actually enforces this (it clips
        // everything inside, however far the visuals try to overflow); the geometry assertions
        // below confirm the box itself is never positioned/sized to reach under a panel in the
        // first place.
        [Test]
        public void PlayerBoxMasksVisualsAndNeverExtendsUnderAPanel()
        {
            AddAssert("player box masks its own content", () => screen.PlayerBox.Masking);
            AddAssert("box has a gutter on every side in normal mode", () =>
                screen.VisualsHostPadding.Left > 0 && screen.VisualsHostPadding.Right > 0
                && screen.VisualsHostPadding.Top > 0 && screen.VisualsHostPadding.Bottom > 0);
            AddAssert("the visuals stack lives inside the masked box, not some other unmasked parent",
                () => screen.PlayerBox.ChildrenOfType<ScreenStack>().Any(s => s == screen.VisualsStack));

            AddAssert("box never reaches under the left column", () =>
                screen.PlayerBox.ScreenSpaceDrawQuad.TopLeft.X >= screen.LeftColumn.ScreenSpaceDrawQuad.TopRight.X);
            AddAssert("box never reaches under the right column", () =>
                screen.PlayerBox.ScreenSpaceDrawQuad.TopRight.X <= screen.RightColumn.ScreenSpaceDrawQuad.TopLeft.X);
            AddAssert("box never reaches under the bottom bar", () =>
                screen.PlayerBox.ScreenSpaceDrawQuad.BottomLeft.Y
                <= screen.ChildrenOfType<NowPlayingBar>().Single().ScreenSpaceDrawQuad.TopLeft.Y);

            // Stronger than the check above: the bar must be TILED below the box (its own strip,
            // with a visible gutter), not merely touching/abutting it — the box's own padding is
            // what carves out that strip (visualsHost.Padding.Bottom == bottom_bar_height +
            // Theme.SectionSpacing), so this pins that gutter rather than allowing a zero-gap
            // regression to silently pass the "<=" check above.
            AddAssert("box bottom edge sits a full gutter above the bar's top edge, not just touching it", () =>
            {
                float boxBottom = screen.PlayerBox.ScreenSpaceDrawQuad.BottomLeft.Y;
                float barTop = screen.ChildrenOfType<NowPlayingBar>().Single().ScreenSpaceDrawQuad.TopLeft.Y;
                return barTop - boxBottom >= Theme.SectionSpacing - 0.5f;
            });

            // The bar sits only in the CENTRE strip: the columns own their full height, and the
            // bar never runs underneath them — a full gutter separates it from each column, the
            // same insets as the player box above it.
            AddAssert("bar never reaches under the left column (full gutter between them)", () =>
            {
                float barLeft = screen.ChildrenOfType<NowPlayingBar>().Single().ScreenSpaceDrawQuad.TopLeft.X;
                float leftColumnRight = screen.LeftColumn.ScreenSpaceDrawQuad.TopRight.X;
                return barLeft - leftColumnRight >= Theme.SectionSpacing - 0.5f;
            });
            AddAssert("bar never reaches under the right column (full gutter between them)", () =>
            {
                float barRight = screen.ChildrenOfType<NowPlayingBar>().Single().ScreenSpaceDrawQuad.TopRight.X;
                float rightColumnLeft = screen.RightColumn.ScreenSpaceDrawQuad.TopLeft.X;
                return rightColumnLeft - barRight >= Theme.SectionSpacing - 0.5f;
            });
            AddAssert("columns run the full window height (nothing underneath them)", () =>
            {
                float screenBottom = screen.ScreenSpaceDrawQuad.BottomLeft.Y;
                return Math.Abs(screen.LeftColumn.ScreenSpaceDrawQuad.BottomLeft.Y - screenBottom) < 0.5f
                       && Math.Abs(screen.RightColumn.ScreenSpaceDrawQuad.BottomLeft.Y - screenBottom) < 0.5f;
            });

            // Regression coverage for the fit-scale fix: with the side columns open, the boxed
            // player is normally much narrower than the storyboard's 854x480 design canvas — before
            // the fix the visuals stack was stretched to the box's raw size and its (wider) content
            // overflowed the box horizontally, only saved from view by the mask. Now the whole
            // visuals stack scales down uniformly to CONTAIN within the box, so its own
            // (unmasked) ScreenSpaceDrawQuad must itself already fit inside the box and keep the
            // design aspect ratio — never cropped, never distorted.
            AddAssert("visuals stack fits entirely within the box (no crop)", () =>
            {
                var box = screen.PlayerBox.ScreenSpaceDrawQuad;
                var visuals = screen.VisualsStack.ScreenSpaceDrawQuad;

                return visuals.TopLeft.X >= box.TopLeft.X - 0.5f && visuals.TopRight.X <= box.TopRight.X + 0.5f
                       && visuals.TopLeft.Y >= box.TopLeft.Y - 0.5f && visuals.BottomLeft.Y <= box.BottomLeft.Y + 0.5f;
            });
            AddAssert("visuals stack keeps the design aspect ratio (letterboxed, not stretched)", () =>
            {
                var visuals = screen.VisualsStack.ScreenSpaceDrawQuad;
                float width = visuals.TopRight.X - visuals.TopLeft.X;
                float height = visuals.BottomLeft.Y - visuals.TopLeft.Y;
                const float design_aspect = 854f / 480f;

                return Math.Abs(width / height - design_aspect) < 0.01f;
            });

            AddStep("enter focus mode", () => InputManager.Key(Key.Tab));
            AddUntilStep("box padding animated away to full-bleed", () =>
                screen.VisualsHostPadding.Left == 0 && screen.VisualsHostPadding.Right == 0
                && screen.VisualsHostPadding.Top == 0 && screen.VisualsHostPadding.Bottom == 0);
            AddAssert("still masked in focus mode", () => screen.PlayerBox.Masking);
        }

        // Regression coverage for the content-scale tracking bug: updateSceneScale used to switch
        // formula (height-only in focus, uniform-min otherwise) the instant UiLayout flipped — before
        // the box had moved at all — so the content jumped to near its final scale on the very first
        // frame of ENTERING focus mode while the box (and its mask) were still small, an overflow
        // only hidden by masking until the box caught up moments later (perceived as an instant
        // expand). Sampling every real Update() frame (AddStep/AddRepeatStep boundaries in this
        // harness advance the clock by far more than one frame, too coarse to catch a one-frame
        // jump) via a throwaway sampler drawable, both directions: the content's rendered width must
        // never exceed the box's own current width, at any point in the transition.
        [Test]
        public void ContentNeverOutrunsTheBoxDuringFocusTransition()
        {
            FrameSampler sampler = null!;

            AddStep("add per-frame sampler", () => uiContainer.Add(sampler = new FrameSampler(
                () => (screen.PlayerBox.DrawWidth, screen.VisualsStack.ScreenSpaceDrawQuad.Width))));

            AddStep("enter focus mode", () => InputManager.Key(Key.Tab));
            AddWaitStep("let it animate", 30);
            AddAssert("content never outran the box while entering focus",
                () => sampler.Samples.All(s => s.content <= s.box + 0.5f));

            AddStep("clear samples", () => sampler.Samples.Clear());
            AddStep("leave focus mode", () => InputManager.Key(Key.Tab));
            AddWaitStep("let it animate", 30);
            AddAssert("content never outran the box while leaving focus",
                () => sampler.Samples.All(s => s.content <= s.box + 0.5f));
        }

        private partial class FrameSampler : Drawable
        {
            private readonly Func<(float box, float content)> sample;
            public readonly List<(float box, float content)> Samples = new();

            public FrameSampler(Func<(float, float)> sample)
            {
                this.sample = sample;
            }

            protected override void Update()
            {
                base.Update();
                Samples.Add(sample());
            }
        }

        // Coverage for the JukeBoxSetting.PlayfieldZoom rework: the zoom factor multiplies into
        // sceneContainer's own auto-fit scale (see MainScreen.updateSceneScale), so it must apply
        // live (no rebuild — updateSceneScale already re-reads the config bindable every frame) and
        // default to a no-op, matching the pre-zoom scale exactly.
        [Test]
        public void PlayfieldZoomScalesTheSceneHostLiveAndDefaultsToNoOp()
        {
            float baseline = 0;
            AddUntilStep("scene has settled to a stable scale", () => screen.SceneScale.X > 0);
            AddStep("record the default-zoom scale", () => baseline = screen.SceneScale.X);

            AddStep("zoom out to 1%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 0.01));
            AddUntilStep("scale follows down to 1%", () => Math.Abs(screen.SceneScale.X - baseline * 0.01f) < 0.001f);

            AddStep("zoom in to 200%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 2.0));
            AddUntilStep("scale follows up to 200%", () => Math.Abs(screen.SceneScale.X - baseline * 2f) < 0.01f);

            AddStep("restore default 100%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0));
            AddUntilStep("scale restores to the default no-op baseline", () => Math.Abs(screen.SceneScale.X - baseline) < 0.01f);
        }

        // Regression coverage for the ChartZoom -> PlayfieldZoom rework's widened scope: the WHOLE
        // visuals stack (background, storyboard/video, chart — everything inside visualsStack, see
        // BeatmapVisuals) must zoom together as one unit, not just the chart. VisualsStack's own
        // ScreenSpaceDrawQuad reflects sceneContainer's scale directly (it's unmasked geometry, so
        // this holds even past 100% where playerBox's masking starts clipping what's actually
        // painted) — doubling the zoom must double it.
        [Test]
        public void PlayfieldZoomScalesTheWholeVisualsStackTogether()
        {
            float baselineWidth = 0;
            AddUntilStep("visuals stack has a nonzero width", () => screen.VisualsStack.ScreenSpaceDrawQuad.Width > 0);
            AddStep("record the baseline visuals width", () => baselineWidth = screen.VisualsStack.ScreenSpaceDrawQuad.Width);

            AddStep("zoom to 200%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 2.0));
            AddUntilStep("visuals stack doubles in width", () => screen.VisualsStack.ScreenSpaceDrawQuad.Width >= baselineWidth * 1.9f);

            AddStep("restore default 100%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0));
            AddUntilStep("visuals stack restores to the baseline width", () => Math.Abs(screen.VisualsStack.ScreenSpaceDrawQuad.Width - baselineWidth) < 1f);
        }

        // "box masking still clips at box edges": zooming must never relax or move playerBox's own
        // mask/bounds — only what's painted INSIDE it changes. Complements
        // PlayerBoxMasksVisualsAndNeverExtendsUnderAPanel (which covers the un-zoomed case) at the
        // 200% extreme, where the zoomed-up scene now genuinely overflows the box and relies on that
        // masking to actually get clipped.
        [Test]
        public void PlayerBoxStaysMaskedAndUnmovedWhenPlayfieldIsZoomedIn()
        {
            osuTK.Vector2 boxTopLeft = default, boxBottomRight = default;
            AddUntilStep("box has settled", () => screen.PlayerBox.DrawWidth > 0);
            AddStep("record box bounds", () =>
            {
                var quad = screen.PlayerBox.ScreenSpaceDrawQuad;
                boxTopLeft = quad.TopLeft;
                boxBottomRight = quad.BottomRight;
            });

            AddStep("zoom in to 200%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 2.0));
            AddAssert("box still masks", () => screen.PlayerBox.Masking);
            AddAssert("box bounds unchanged by the zoom", () =>
            {
                var quad = screen.PlayerBox.ScreenSpaceDrawQuad;
                return osuTK.Vector2.Distance(quad.TopLeft, boxTopLeft) < 0.5f && osuTK.Vector2.Distance(quad.BottomRight, boxBottomRight) < 0.5f;
            });

            AddStep("restore default 100%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0));
        }

        // Regression coverage for a focus-mode transition asymmetry: entering focus mode used to
        // snap the player box straight to full-bleed on the very same frame the columns started
        // sliding away, while leaving focus animated the box's restore properly. Both directions
        // must now tween the box's padding over the same orchestrated timeline as the panels.
        // NOTE: the transition is deliberately fast (Theme.DurationFast, linear — user
        // preference), so a whole animation can complete between two coarse test STEPS.
        // These regressions therefore detect gradual motion via a per-engine-frame sampler
        // (engine frames tick far faster than steps) instead of "one step in" assertions.
        [Test]
        public void PlayerBoxExpandsAndRestoresSymmetricallyWithFocusMode()
        {
            float restoredLeft = 0;
            FrameSampler sampler = null!;
            AddStep("record the resting gutter", () => restoredLeft = screen.VisualsHostPadding.Left);
            AddAssert("box has a gutter to start", () => restoredLeft > 0);
            AddStep("add per-frame padding sampler", () => uiContainer.Add(sampler = new FrameSampler(
                () => (screen.VisualsHostPadding.Left, 0))));

            AddStep("enter focus mode", () => InputManager.Key(Key.Tab));
            AddUntilStep("box padding eventually reaches full-bleed", () =>
                screen.VisualsHostPadding.Left == 0 && screen.VisualsHostPadding.Right == 0
                && screen.VisualsHostPadding.Top == 0 && screen.VisualsHostPadding.Bottom == 0);
            AddAssert("padding passed through an intermediate value on the way out", () =>
                sampler.Samples.Any(s => s.box > 0 && s.box < restoredLeft));

            AddStep("clear samples", () => sampler.Samples.Clear());
            AddStep("leave focus mode", () => InputManager.Key(Key.Tab));
            AddUntilStep("box padding eventually restores its gutter", () =>
                screen.VisualsHostPadding.Left == restoredLeft);
            AddAssert("padding passed through an intermediate value on the way back", () =>
                sampler.Samples.Any(s => s.box > 0 && s.box < restoredLeft));
        }

        // Same regression, phrased against the actually-rendered DrawWidth (the pixels a user
        // would see) rather than the target Padding value, in case the two ever diverge.
        [Test]
        public void PlayerBoxDrawWidthGrowsGraduallyOnEnterNotJustPaddingValue()
        {
            float startWidth = 0;
            FrameSampler sampler = null!;
            AddStep("record starting box width", () => startWidth = screen.PlayerBox.DrawWidth);
            AddStep("add per-frame width sampler", () => uiContainer.Add(sampler = new FrameSampler(
                () => (screen.PlayerBox.DrawWidth, 0))));

            AddStep("enter focus mode", () => InputManager.Key(Key.Tab));
            AddUntilStep("box eventually reaches full-bleed width", () =>
                screen.PlayerBox.DrawWidth >= screen.DrawWidth - 1);
            AddAssert("box width passed through an intermediate value (grew gradually, no snap)", () =>
                sampler.Samples.Any(s => s.box > startWidth + 1 && s.box < screen.DrawWidth - 1));
        }

        [Test]
        public void TypingFocusesAndSeedsTheDockedSearchBox()
        {
            AddAssert("search box starts empty", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().SearchBox.Text == string.Empty);

            AddStep("press 'a'", () => InputManager.Key(Key.A));

            AddAssert("search box seeded with 'a'", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().SearchBox.Text == "a");
            AddUntilStep("search box focused", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().SearchBox.HasFocus);
            AddAssert("left column never left shown", () => screen.LeftColumn.Alpha == 1);
        }

        // Escape's new contract: it blurs the docked search box but the column itself — unlike the
        // old dismissable overlay — is never hidden.
        [Test]
        public void EscapeUnfocusesSearchBoxWithoutHidingTheColumn()
        {
            AddStep("press 'a' (focus + seed)", () => InputManager.Key(Key.A));
            AddUntilStep("search box focused", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().SearchBox.HasFocus);

            AddStep("press escape", () => InputManager.Key(Key.Escape));

            AddUntilStep("search box no longer focused", () => !screen.ChildrenOfType<BeatmapListingOverlay>().Single().SearchBox.HasFocus);
            AddAssert("left column still shown", () => screen.LeftColumn.Alpha == 1);
            AddAssert("listing still present in the hierarchy", () => screen.ChildrenOfType<BeatmapListingOverlay>().Any());
        }

        // Focus mode is now an animated transition (side columns + bottom bar all slide/fade out
        // together, reversed on restore — see MainScreen.applyLayout), so the Alpha assertions
        // need to poll (AddUntilStep) rather than land true on the very next frame.
        [Test]
        public void TabTogglesFocusMode()
        {
            AddAssert("both columns shown initially", () => screen.LeftColumn.Alpha == 1 && screen.RightColumn.Alpha == 1);
            AddAssert("bottom bar shown initially", () => screen.ChildrenOfType<NowPlayingBar>().Single().Alpha == 1);
            AddAssert("config starts ThreeColumn", () => config.Get<UiLayout>(JukeBoxSetting.UiLayout) == UiLayout.ThreeColumn);

            AddStep("press tab", () => InputManager.Key(Key.Tab));
            AddAssert("config persisted Focus", () => config.Get<UiLayout>(JukeBoxSetting.UiLayout) == UiLayout.Focus);
            AddUntilStep("both columns hidden (focus mode)", () => screen.LeftColumn.Alpha == 0 && screen.RightColumn.Alpha == 0);
            AddUntilStep("bottom bar hidden too — focus mode is pure fullscreen visuals",
                () => screen.ChildrenOfType<NowPlayingBar>().Single().Alpha == 0);

            AddStep("press tab again", () => InputManager.Key(Key.Tab));
            AddAssert("config back to ThreeColumn", () => config.Get<UiLayout>(JukeBoxSetting.UiLayout) == UiLayout.ThreeColumn);
            AddUntilStep("both columns shown again", () => screen.LeftColumn.Alpha == 1 && screen.RightColumn.Alpha == 1);
            AddUntilStep("bottom bar shown again", () => screen.ChildrenOfType<NowPlayingBar>().Single().Alpha == 1);
        }

        // Type-anywhere-to-search only makes sense while the column hosting the search box is
        // actually reachable — while focus mode hides it, a keypress must fall through untouched
        // rather than silently seeding/focusing a now-invisible box.
        [Test]
        public void TypingDoesNothingWhileInFocusMode()
        {
            AddStep("press tab (enter focus mode)", () => InputManager.Key(Key.Tab));
            AddUntilStep("columns hidden", () => screen.LeftColumn.Alpha == 0);

            AddStep("press 'a'", () => InputManager.Key(Key.A));

            AddAssert("search box was not seeded", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().SearchBox.Text == string.Empty);
        }

        // The SearchStyle setting picks which presentation "opening search" means, live: under
        // Fullscreen, type-anywhere presents the big listing overlay over the player box (seeded
        // with the char — both presentations share one search engine, so the docked box shows the
        // same query); Escape closes it back to the player; flipping the setting to Compact while
        // it's open retires it immediately and the next keypress goes back to the docked column.
        [Test]
        public void SearchStyleSettingRoutesSearchOpeningLive()
        {
            FullscreenListingOverlay fullscreen = null!;
            AddStep("grab fullscreen listing", () => fullscreen = screen.ChildrenOfType<FullscreenListingOverlay>().Single());

            AddAssert("fullscreen listing starts hidden", () => fullscreen.State.Value == Visibility.Hidden);

            AddStep("press 'a' under compact style", () => InputManager.Key(Key.A));
            AddAssert("fullscreen listing stays hidden (compact routes to the docked column)",
                () => fullscreen.State.Value == Visibility.Hidden);
            AddAssert("docked box seeded instead", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().SearchBox.Text == "a");

            AddStep("unfocus and switch to fullscreen style", () =>
            {
                InputManager.Key(Key.Escape);
                config.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Fullscreen);
            });

            AddStep("press 'b'", () => InputManager.Key(Key.B));
            AddUntilStep("fullscreen listing shown", () => fullscreen.State.Value == Visibility.Visible);
            AddAssert("fullscreen box seeded with 'b'", () => fullscreen.SearchBox.Text == "b");
            AddAssert("docked box shows the same shared query", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().SearchBox.Text == "b");

            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddUntilStep("fullscreen listing closed back to the player", () => fullscreen.State.Value == Visibility.Hidden);

            AddStep("press 'c' (reopen)", () => InputManager.Key(Key.C));
            AddUntilStep("fullscreen listing shown again", () => fullscreen.State.Value == Visibility.Visible);

            AddStep("flip the setting to Compact while open", () => config.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Compact));
            AddUntilStep("fullscreen listing retired immediately", () => fullscreen.State.Value == Visibility.Hidden);
        }

        // The fullscreen listing is a TRUE whole-window modal: its quad must cover the entire
        // window — over the side columns and the bottom bar, not just the player-box area — and
        // its entrance must SLIDE the panel up from past the bottom edge (sampled per engine
        // frame, same pattern as the focus-transition regressions above) rather than snapping in.
        [Test]
        public void FullscreenListingCoversTheWholeWindowAndSlidesUpFromBottom()
        {
            FullscreenListingOverlay fullscreen = null!;
            FrameSampler sampler = null!;

            AddStep("switch to fullscreen style and grab the overlay", () =>
            {
                config.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Fullscreen);
                fullscreen = screen.ChildrenOfType<FullscreenListingOverlay>().Single();
            });
            AddStep("add per-frame panel-Y sampler", () => uiContainer.Add(sampler = new FrameSampler(
                () => (fullscreen.SlidePanel.Y, 0))));

            AddStep("press 'a'", () => InputManager.Key(Key.A));
            AddUntilStep("fullscreen listing shown", () => fullscreen.State.Value == Visibility.Visible);
            AddUntilStep("entrance settled (panel at rest)", () => fullscreen.SlidePanel.Y == 0);
            AddAssert("panel slid up from below (gradual entrance, not a snap)",
                () => sampler.Samples.Any(s => s.box > 1));

            AddAssert("overlay covers the entire window", () =>
            {
                var o = fullscreen.ScreenSpaceDrawQuad.AABBFloat;
                var s = screen.ScreenSpaceDrawQuad.AABBFloat;

                return o.Left <= s.Left + 0.5f && o.Top <= s.Top + 0.5f
                       && o.Right >= s.Right - 0.5f && o.Bottom >= s.Bottom - 0.5f;
            });
            AddAssert("covers the side columns and the bottom bar too", () =>
            {
                var o = fullscreen.ScreenSpaceDrawQuad.AABBFloat;

                bool covers(osu.Framework.Graphics.Primitives.RectangleF r)
                    => o.Left <= r.Left + 0.5f && o.Top <= r.Top + 0.5f
                       && o.Right >= r.Right - 0.5f && o.Bottom >= r.Bottom - 0.5f;

                return covers(screen.LeftColumn.ScreenSpaceDrawQuad.AABBFloat)
                       && covers(screen.RightColumn.ScreenSpaceDrawQuad.AABBFloat)
                       && covers(screen.ChildrenOfType<NowPlayingBar>().Single().ScreenSpaceDrawQuad.AABBFloat);
            });

            AddStep("clear samples", () => sampler.Samples.Clear());
            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddUntilStep("closed back to the normal layout", () => fullscreen.State.Value == Visibility.Hidden);
            AddUntilStep("panel slid back down past the bottom", () => fullscreen.SlidePanel.Y > 1);
        }

        // The search-opener icons: under compact style the docked listing's own icon focuses the
        // keyword box; under fullscreen style the collapsed column's rail icon presents the big
        // listing.
        [Test]
        public void SearchIconButtonOpensSearchPerStyle()
        {
            AddStep("click the docked listing's search icon under compact style", () =>
                screen.ChildrenOfType<BeatmapListingOverlay>().Single()
                      .ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Search)).TriggerClick());
            AddUntilStep("docked keyword box focused", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().SearchBox.HasFocus);
            AddAssert("fullscreen listing stayed hidden", () =>
                screen.ChildrenOfType<FullscreenListingOverlay>().Single().State.Value == Visibility.Hidden);

            AddStep("switch to fullscreen style", () => config.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Fullscreen));

            AddStep("click the icon rail's search button", () => clickRailButton());
            AddUntilStep("fullscreen listing shown", () =>
                screen.ChildrenOfType<FullscreenListingOverlay>().Single().State.Value == Visibility.Visible);
        }

        // SearchRailButton is a private nested type (MainScreen) — located by type name the same
        // way the tab buttons are.
        private void clickRailButton()
            => screen.ChildrenOfType<ClickableContainer>().First(c => c.GetType().Name == "SearchRailButton").TriggerClick();

        // The fullscreen style collapses the left column to a slim icon rail: the docked listing
        // crossfades away (ONLY the search icon remains), the column width animates down, and the
        // player box + bottom bar insets follow it so the centre tiling holds. Switching back to
        // compact restores the full column live.
        [Test]
        public void FullscreenStyleCollapsesLeftColumnToIconRail()
        {
            FrameSampler sampler = null!;

            AddAssert("column starts at full width (compact style)", () => screen.LeftColumn.Width == 380);
            AddAssert("docked listing body shown, rail hidden", () => screen.ListingBody.Alpha == 1 && screen.SearchRail.Alpha == 0);

            AddStep("add per-frame column-width sampler", () => uiContainer.Add(sampler = new FrameSampler(
                () => (screen.LeftColumn.Width, 0))));

            AddStep("switch to fullscreen style", () => config.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Fullscreen));

            AddUntilStep("column collapsed to the rail width", () => screen.LeftColumn.Width < 70);
            AddAssert("collapse animated through intermediate widths (no snap)",
                () => sampler.Samples.Any(s => s.box > 70 && s.box < 375));
            AddUntilStep("docked listing crossfaded away", () => screen.ListingBody.Alpha == 0);
            AddUntilStep("only the search icon remains (rail shown)", () => screen.SearchRail.Alpha == 1);

            // The centre insets follow the rail: the player box and the bar now sit a gutter
            // right of the RAIL's edge, not the old full column's.
            AddUntilStep("player box left inset follows the rail", () =>
            {
                float boxLeft = screen.PlayerBox.ScreenSpaceDrawQuad.TopLeft.X;
                float railRight = screen.LeftColumn.ScreenSpaceDrawQuad.TopRight.X;
                return boxLeft - railRight >= Theme.SectionSpacing - 0.5f && boxLeft - railRight <= Theme.SectionSpacing + 1.5f;
            });
            AddUntilStep("bottom bar left inset follows the rail", () =>
            {
                float barLeft = screen.ChildrenOfType<NowPlayingBar>().Single().ScreenSpaceDrawQuad.TopLeft.X;
                float railRight = screen.LeftColumn.ScreenSpaceDrawQuad.TopRight.X;
                return barLeft - railRight >= Theme.SectionSpacing - 0.5f && barLeft - railRight <= Theme.SectionSpacing + 1.5f;
            });

            AddStep("click the rail icon", () => clickRailButton());
            AddUntilStep("fullscreen listing shown", () =>
                screen.ChildrenOfType<FullscreenListingOverlay>().Single().State.Value == Visibility.Visible);
            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddUntilStep("fullscreen listing closed", () =>
                screen.ChildrenOfType<FullscreenListingOverlay>().Single().State.Value == Visibility.Hidden);

            AddStep("switch back to compact", () => config.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Compact));

            AddUntilStep("column restored to full width", () => screen.LeftColumn.Width == 380);
            AddUntilStep("docked listing body restored", () => screen.ListingBody.Alpha == 1);
            AddUntilStep("rail hidden again", () => screen.SearchRail.Alpha == 0);
        }

        // Regression coverage for the (now-removed) corner gear: Settings is reachable purely by
        // clicking its own tab header in the right column — no corner shortcut needed any more.
        [Test]
        public void SettingsTabButtonSwitchesRightPanelToSettingsTab()
        {
            QueuePanel queuePanel = null!;
            SettingsOverlay settingsBody = null!;
            AddStep("grab queue panel and settings body", () =>
            {
                queuePanel = screen.ChildrenOfType<QueuePanel>().Single();
                settingsBody = screen.ChildrenOfType<SettingsOverlay>().Single();
            });

            AddAssert("queue tab active initially", () => queuePanel.Alpha == 1 && settingsBody.Alpha == 0);

            AddStep("click the Settings tab button", () => clickTabButton("Settings"));

            // The tab switch crossfades (see MainScreen.showTabBody) rather than cutting
            // instantly, so this needs to poll rather than assert on the very next frame.
            AddUntilStep("settings tab now active", () => settingsBody.Alpha == 1 && queuePanel.Alpha == 0);
        }

        // Regression coverage for Ctrl+Q: still a "jump to queue" shortcut, now switching the tab
        // rather than sliding a drawer into view.
        [Test]
        public void CtrlQSwitchesRightPanelToQueueTab()
        {
            QueuePanel queuePanel = null!;
            SettingsOverlay settingsBody = null!;
            AddStep("grab queue panel and settings body, switch to settings", () =>
            {
                queuePanel = screen.ChildrenOfType<QueuePanel>().Single();
                settingsBody = screen.ChildrenOfType<SettingsOverlay>().Single();
                clickTabButton("Settings");
            });
            AddUntilStep("settings tab active", () => settingsBody.Alpha == 1);

            AddStep("press ctrl+q", () =>
            {
                InputManager.PressKey(Key.ControlLeft);
                InputManager.Key(Key.Q);
                InputManager.ReleaseKey(Key.ControlLeft);
            });

            AddUntilStep("queue tab active again", () => queuePanel.Alpha == 1 && settingsBody.Alpha == 0);
        }

        // Regression coverage for the map-ID button: now docked inline at the right edge of the
        // search box (left column) rather than a standalone corner button — it opens the same
        // shared MapIdOverlay via BeatmapListingOverlay.MapIdRequested.
        [Test]
        public void HashtagButtonTogglesMapIdOverlay()
        {
            MapIdOverlay overlay = null!;
            AddStep("grab map-id overlay", () => overlay = screen.ChildrenOfType<MapIdOverlay>().Single());

            AddAssert("starts hidden", () => overlay.State.Value == Visibility.Hidden);

            AddStep("click the hashtag button in the search box",
                () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Hashtag)).TriggerClick());
            AddAssert("overlay visible", () => overlay.State.Value == Visibility.Visible);

            AddStep("click the hashtag button again",
                () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Hashtag)).TriggerClick());
            AddAssert("overlay hidden again", () => overlay.State.Value == Visibility.Hidden);
        }

        // RightPanelTabButton is a private nested type (MainScreen) — located by type name the
        // same way TestSceneBeatmapListing locates FiltersToggleButton, then disambiguated by its
        // own label text.
        private void clickTabButton(string label)
            => screen.ChildrenOfType<ClickableContainer>()
                     .First(c => c.GetType().Name == "RightPanelTabButton" && c.ChildrenOfType<SpriteText>().Any(t => t.Text == label))
                     .TriggerClick();

        // Regression coverage for the queue drawer's old floating-drawer geometry (off-screen X,
        // Y-only relative sizing) — MainScreen must override it to fully-relative fill-the-tab-body
        // geometry once at load, since QueuePanel's own LoadComplete otherwise parks it off-screen.
        [Test]
        public void QueuePanelDockedFillsTheRightColumnTabBody()
        {
            AddAssert("queue panel X == 0", () => screen.ChildrenOfType<QueuePanel>().Single().X == 0);
            AddAssert("queue panel anchored top-left", () => screen.ChildrenOfType<QueuePanel>().Single().Anchor == Anchor.TopLeft);
            AddAssert("queue panel origin top-left", () => screen.ChildrenOfType<QueuePanel>().Single().Origin == Anchor.TopLeft);
            AddAssert("queue panel fully relative", () => screen.ChildrenOfType<QueuePanel>().Single().RelativeSizeAxes == Axes.Both);
            AddAssert("queue panel width == 1", () => screen.ChildrenOfType<QueuePanel>().Single().Width == 1f);
            AddAssert("queue panel height == 1", () => screen.ChildrenOfType<QueuePanel>().Single().Height == 1f);
        }

        // Never exercised (queue stays empty and the mirror returns no candidates, so
        // Jukebox.Start()'s automatic radio round finds nothing and just retries later) — only
        // present so Jukebox/RadioService/BeatmapCache have a mirror to construct against without
        // touching the network.
        private class StubMirror : IBeatmapMirror
        {
            public string Name => "stub";
            public List<BeatmapSetInfo> Sets { get; } = new();

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>(Sets));

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
