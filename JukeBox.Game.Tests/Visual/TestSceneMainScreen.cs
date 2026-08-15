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
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game.Overlays.Settings;
using osu.Game.Rulesets.Mania.UI;
using osu.Game.Rulesets.UI;
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
        // approach as TestSceneNowPlayingPanel: MainScreen resolves these via [Resolved], and giving
        // it a StubMirror here (rather than the real network MirrorChain JukeBoxGameBase wires up)
        // keeps this test off the network. See CreateChildDependencies note in TestSceneNowPlayingPanel
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
        // uiContainer's content on every [Test]. See TestSceneNowPlayingPanel's LoadComplete for why.
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
                screen = new MainScreen { RelativeSizeAxes = Axes.Both };
                // MainScreen is a Screen — osu!framework requires a Screen to be hosted by a
                // ScreenStack (see JukeBoxGame's own top-level screenStack.Push(new MainScreen())).
                uiContainer.Child = new ScreenStack(screen) { RelativeSizeAxes = Axes.Both };
            });
        }

        [Test]
        public void ThreeColumnLayoutStartsWithBothColumnsShownAndPlaybackTabActive()
        {
            AddAssert("left column shown", () => screen.LeftColumn.Alpha == 1);
            AddAssert("right column shown", () => screen.RightColumn.Alpha == 1);
            AddAssert("playback tab active", () => playbackPanel().Alpha == 1);
            AddAssert("settings tab inactive", () => screen.ChildrenOfType<SettingsOverlay>().Single().Alpha == 0);
        }

        // The right column's first tab is "Playback" (it was "Queue" while the queue was all it
        // held); Settings keeps its own name and stays last. "Chart" was added BETWEEN them — the
        // order is the point, so this asserts the sequence rather than mere membership.
        [Test]
        public void RightColumnTabsArePlaybackChartAndSettings()
        {
            AddAssert("tab headers read Playback, Chart, Settings, in that order",
                () => tabButtonLabels().SequenceEqual(new[] { "Playback", "Chart", "Settings" }));

            AddAssert("the Chart button really sits between the other two", () =>
            {
                var buttons = tabButtons();
                return buttons[0].ScreenSpaceDrawQuad.TopLeft.X < buttons[1].ScreenSpaceDrawQuad.TopLeft.X
                       && buttons[1].ScreenSpaceDrawQuad.TopLeft.X < buttons[2].ScreenSpaceDrawQuad.TopLeft.X;
            });
        }

        // Clicking the middle tab shows the chart body and hides both neighbours — the strip is a
        // three-way switch, not a two-way one with a spare button.
        [Test]
        public void ChartTabSwitchesToTheChartBody()
        {
            AddAssert("chart tab starts inactive", () => chartPanel().Alpha == 0);

            AddStep("click the Chart tab", () =>
            {
                InputManager.MoveMouseTo(tabButtons()[1]);
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("chart body shown", () => chartPanel().Alpha == 1);
            AddAssert("playback body hidden", () => playbackPanel().Alpha == 0);
            AddAssert("settings body hidden", () => screen.ChildrenOfType<SettingsOverlay>().Single().Alpha == 0);
        }

        // "Render chart", "Play hit sounds" and "Hit lighting" MOVED to the Chart tab — they must be
        // there and nowhere else. A duplicate would be two controls over one config key, which is
        // exactly the bug this guards.
        [Test]
        public void ChartTabOwnsTheMovedChartRowsAndSettingsNoLongerDoes()
        {
            AddAssert("render chart moved into the Chart tab",
                () => chartPanel().RenderChartCheckbox.LabelText.ToString() == "Render chart");
            AddAssert("play hit sounds moved into the Chart tab",
                () => chartPanel().PlayHitSoundsCheckbox.LabelText.ToString() == "Play hit sounds");
            AddAssert("hit lighting moved into the Chart tab",
                () => checkboxLabels(chartPanel()).Contains("Hit lighting"));

            SettingsOverlay settings = null!;
            AddStep("grab settings body", () => settings = screen.ChildrenOfType<SettingsOverlay>().Single());

            AddAssert("no render chart row left in settings", () => !checkboxLabels(settings).Contains("Render chart"));
            AddAssert("no play hit sounds row left in settings", () => !checkboxLabels(settings).Contains("Play hit sounds"));
            AddAssert("no hit lighting row left in settings", () => !checkboxLabels(settings).Contains("Hit lighting"));

            // The whole Rulesets section and its Analysis (osu!) subsection moved across too.
            AddAssert("no ruleset or analysis rows left in settings", () =>
            {
                var labels = allSettingsLabels(settings);

                return !new[]
                {
                    "Snaking in sliders", "Snaking out sliders", "Hit animations", "Cursor trail", "Cursor ripples",
                    "Playfield border style", "Scrolling direction", "Scroll speed", "Timing-based note colouring",
                    "Show click markers", "Show frame markers", "Show cursor path", "Hide gameplay cursor", "Display length",
                }.Any(labels.Contains);
            });

            AddAssert("and they are all in the Chart tab instead", () =>
            {
                var labels = allSettingsLabels(chartPanel());

                return new[]
                {
                    "Snaking in sliders", "Snaking out sliders", "Hit animations", "Cursor trail", "Cursor ripples",
                    "Playfield border style", "Scrolling direction", "Scroll speed", "Timing-based note colouring",
                    "Show click markers", "Show frame markers", "Show cursor path", "Hide gameplay cursor", "Display length",
                }.All(labels.Contains);
            });

            // The settings that legitimately stayed behind.
            AddAssert("gameplay skin stayed in settings", () => allSettingsLabels(settings).Contains("Gameplay skin"));
            AddAssert("background dim stayed in settings", () => allSettingsLabels(settings).Contains("Background dim"));

            // Same key, so an existing user's value carries over rather than resetting.
            AddStep("tick render chart in the Chart tab", () => chartPanel().RenderChartCheckbox.Current.Value = true);
            AddAssert("it wrote the same config key it always did", () => config.Get<bool>(JukeBoxSetting.RenderChart));
            AddStep("untick it again", () => chartPanel().RenderChartCheckbox.Current.Value = false);
        }

        private ChartPanel chartPanel() => screen.ChildrenOfType<ChartPanel>().Single();

        private static List<string> checkboxLabels(Drawable panel)
            => panel.ChildrenOfType<SettingsCheckbox>().Select(c => c.LabelText.ToString()).ToList();

        /// <summary>
        /// Every labelled settings row in a panel across the value types the moved rows use —
        /// checkboxes, sliders and dropdowns alike, so a row can't slip past this by being a
        /// dropdown rather than a checkbox. <c>SettingsItem&lt;T&gt;</c> is generic with no
        /// non-generic label surface, hence the explicit list.
        /// </summary>
        private static List<string> allSettingsLabels(Drawable panel)
            => labelsOf<bool>(panel)
               .Concat(labelsOf<double>(panel))
               .Concat(labelsOf<int>(panel))
               .Concat(labelsOf<JukeBoxSkin>(panel))
               .Concat(labelsOf<PlayfieldBorderStyle>(panel))
               .Concat(labelsOf<ManiaScrollingDirection>(panel))
               .ToList();

        private static IEnumerable<string> labelsOf<T>(Drawable panel)
            => panel.ChildrenOfType<SettingsItem<T>>().Select(i => i.LabelText.ToString());

        // Everything the deleted bottom bar carried, plus the controls that used to sit in
        // Settings → Playback, now lives in this one tab — with the queue underneath it. The
        // per-beatmap offset slider is deliberately absent (user request): BeatmapOffsetStore still
        // applies its value to playback, it just has no row anywhere in the UI.
        [Test]
        public void PlaybackTabHoldsTheNowPlayingPanelTransportSpeedAndQueue()
        {
            AddAssert("now-playing panel (cover/title/progress/difficulty/browser) is in the tab",
                () => playbackPanel().ChildrenOfType<NowPlayingPanel>().Any());
            AddAssert("transport strip moved in from settings",
                () => playbackPanel().ChildrenOfType<TransportRow>().Any());
            AddAssert("playback speed slider moved in from settings",
                () => playbackPanel().PlaybackRateSlider.LabelText.ToString() == "Playback speed");
            AddAssert("no per-beatmap offset row anywhere in the tab",
                () => !playbackPanel().ChildrenOfType<SettingsItem<double>>()
                                      .Any(i => i.LabelText.ToString() == "Audio offset (this beatmap)"));
            AddAssert("queue is in the tab too", () => playbackPanel().ChildrenOfType<QueuePanel>().Any());

            AddAssert("queue sits BELOW the playback section", () =>
                playbackPanel().Queue.ScreenSpaceDrawQuad.TopLeft.Y
                >= playbackPanel().NowPlaying.ScreenSpaceDrawQuad.BottomLeft.Y);
        }

        // The moved rows must be gone from Settings, not duplicated into both tabs. The GLOBAL
        // offset deliberately stays in Settings → Audio (it calibrates the output path, not the
        // song) — so it's still findable there.
        [Test]
        public void SettingsTabNoLongerCarriesTheMovedPlaybackRows()
        {
            SettingsOverlay settings = null!;
            AddStep("grab settings body", () => settings = screen.ChildrenOfType<SettingsOverlay>().Single());

            AddAssert("no transport strip left in settings", () => !settings.ChildrenOfType<TransportRow>().Any());
            AddAssert("no playback speed row left in settings", () => !settingsLabels(settings).Contains("Playback speed"));
            AddAssert("no per-beatmap offset row left in settings", () => !settingsLabels(settings).Contains("Audio offset (this beatmap)"));
            AddAssert("global offset row stayed in settings", () => settingsLabels(settings).Contains("Audio offset (global)"));
        }

        private PlaybackPanel playbackPanel() => screen.ChildrenOfType<PlaybackPanel>().Single();

        private static List<string> settingsLabels(SettingsOverlay settings)
            => settings.ChildrenOfType<SettingsItem<double>>().Select(i => i.LabelText.ToString()).ToList();

        private List<ClickableContainer> tabButtons()
            => screen.ChildrenOfType<ClickableContainer>()
                     .Where(c => c.GetType().Name == "RightPanelTabButton")
                     .ToList();

        private List<string> tabButtonLabels()
            => tabButtons().Select(c => c.ChildrenOfType<SpriteText>().First().Text.ToString()).ToList();

        // Regression coverage for a true contained player: the video/storyboard/background
        // visuals must never render outside the boxed centre panel, and never behind either side
        // column. Masking on the box is what actually enforces this (it clips everything inside,
        // however far the visuals try to overflow); the geometry assertions below confirm the box
        // itself is never positioned/sized to reach under a panel in the first place.
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

            // The bottom bar is gone: nothing is tiled below the box any more, so it runs down to
            // one gutter above the window's own bottom edge (visualsHost.Padding.Bottom ==
            // Theme.SectionSpacing) rather than stopping short of a bar's strip.
            AddAssert("no playback strip lives outside the right column any more",
                () => screen.ChildrenOfType<NowPlayingPanel>().All(p => screen.RightColumn.ChildrenOfType<NowPlayingPanel>().Contains(p)));
            AddAssert("box bottom edge sits exactly one gutter above the window's bottom", () =>
            {
                float boxBottom = screen.PlayerBox.ScreenSpaceDrawQuad.BottomLeft.Y;
                float screenBottom = screen.ScreenSpaceDrawQuad.BottomLeft.Y;
                return Math.Abs(screenBottom - boxBottom - Theme.SectionSpacing) < 0.5f;
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

        // There is exactly one search surface now: typing anywhere opens the fullscreen listing
        // seeded with the character, rather than seeding a sidebar box that no longer exists.
        [Test]
        public void TypingOpensTheFullscreenListingSeeded()
        {
            AddAssert("fullscreen listing starts hidden", () => fullscreenListing().State.Value == Visibility.Hidden);
            AddAssert("sidebar has no text box at all", () =>
                !screen.ChildrenOfType<BeatmapListingOverlay>().Single().ChildrenOfType<TextBox>().Any());

            AddStep("press 'a'", () => InputManager.Key(Key.A));

            AddUntilStep("fullscreen listing shown", () => fullscreenListing().State.Value == Visibility.Visible);
            AddAssert("its keyword box is seeded with 'a'", () => fullscreenListing().SearchBox.Text == "a");
            AddAssert("shared engine carries the query", () =>
                screen.ChildrenOfType<BeatmapListingOverlay>().Single().Engine.Query.Value == "a");
            AddAssert("left column never left shown", () => screen.LeftColumn.Alpha == 1);
        }

        // Escape's contract: it closes the fullscreen listing back to the player; the permanently
        // docked sidebar is never hidden by it.
        [Test]
        public void EscapeClosesTheFullscreenListingWithoutHidingTheColumn()
        {
            AddStep("press 'a' (opens search)", () => InputManager.Key(Key.A));
            AddUntilStep("fullscreen listing shown", () => fullscreenListing().State.Value == Visibility.Visible);

            AddStep("press escape", () => InputManager.Key(Key.Escape));

            AddUntilStep("fullscreen listing closed", () => fullscreenListing().State.Value == Visibility.Hidden);
            AddAssert("left column still shown", () => screen.LeftColumn.Alpha == 1);
            AddAssert("sidebar still present and visible", () =>
                screen.ChildrenOfType<BeatmapListingOverlay>().Single().State.Value == Visibility.Visible);
        }

        // The engine outlives the modal, which is what lets the sidebar go on showing whatever the
        // user searched for once they close the listing.
        [Test]
        public void SidebarKeepsTheResultsAfterTheFullscreenListingCloses()
        {
            AddStep("mirror serves three sets", () => mirror.Sets.AddRange(new[]
            {
                new BeatmapSetInfo { Id = 1, Title = "Alpha Song", Artist = "a", Creator = "c", Status = "ranked" },
                new BeatmapSetInfo { Id = 2, Title = "Beta Song", Artist = "b", Creator = "c", Status = "ranked" },
                new BeatmapSetInfo { Id = 3, Title = "Gamma Song", Artist = "g", Creator = "c", Status = "ranked" },
            }));

            AddStep("press 'a' (opens search)", () => InputManager.Key(Key.A));
            AddUntilStep("sidebar rendered the results too", () =>
                screen.ChildrenOfType<BeatmapListingOverlay>().Single().ChildrenOfType<BeatmapCard>().Count() == 3);

            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddUntilStep("fullscreen listing closed", () => fullscreenListing().State.Value == Visibility.Hidden);

            AddAssert("sidebar still shows those results", () =>
                screen.ChildrenOfType<BeatmapListingOverlay>().Single().ChildrenOfType<BeatmapCard>().Count() == 3);
            AddAssert("and the query is still on the engine", () =>
                screen.ChildrenOfType<BeatmapListingOverlay>().Single().Engine.Query.Value == "a");
        }

        private FullscreenListingOverlay fullscreenListing() => screen.ChildrenOfType<FullscreenListingOverlay>().Single();

        // Focus mode is an animated transition (both side columns slide/fade out together while
        // the player box expands, reversed on restore — see MainScreen.applyLayout), so the Alpha
        // assertions need to poll (AddUntilStep) rather than land true on the very next frame.
        [Test]
        public void TabTogglesFocusMode()
        {
            AddAssert("both columns shown initially", () => screen.LeftColumn.Alpha == 1 && screen.RightColumn.Alpha == 1);
            AddAssert("config starts ThreeColumn", () => config.Get<UiLayout>(JukeBoxSetting.UiLayout) == UiLayout.ThreeColumn);

            AddStep("press tab", () => InputManager.Key(Key.Tab));
            AddAssert("config persisted Focus", () => config.Get<UiLayout>(JukeBoxSetting.UiLayout) == UiLayout.Focus);
            AddUntilStep("both columns hidden (focus mode)", () => screen.LeftColumn.Alpha == 0 && screen.RightColumn.Alpha == 0);
            AddUntilStep("player fills the window", () => screen.PlayerBox.DrawWidth >= screen.DrawWidth - 1
                                                         && screen.PlayerBox.DrawHeight >= screen.DrawHeight - 1);

            AddStep("press tab again", () => InputManager.Key(Key.Tab));
            AddAssert("config back to ThreeColumn", () => config.Get<UiLayout>(JukeBoxSetting.UiLayout) == UiLayout.ThreeColumn);
            AddUntilStep("both columns shown again", () => screen.LeftColumn.Alpha == 1 && screen.RightColumn.Alpha == 1);
        }

        // Focus mode is pure full-bleed visuals — a stray keypress must not pop the listing modal
        // over them.
        [Test]
        public void TypingDoesNothingWhileInFocusMode()
        {
            AddStep("press tab (enter focus mode)", () => InputManager.Key(Key.Tab));
            AddUntilStep("columns hidden", () => screen.LeftColumn.Alpha == 0);

            AddStep("press 'a'", () => InputManager.Key(Key.A));
            AddWaitStep("let any entrance animate", 5);

            AddAssert("fullscreen listing stayed hidden", () => fullscreenListing().State.Value == Visibility.Hidden);
            AddAssert("nothing was seeded into the engine", () =>
                screen.ChildrenOfType<BeatmapListingOverlay>().Single().Engine.Query.Value == string.Empty);
        }

        // The fullscreen listing is a TRUE whole-window modal: its quad must cover the entire
        // window — over both side columns, not just the player-box area — and
        // its entrance must SLIDE the panel up from past the bottom edge (sampled per engine
        // frame, same pattern as the focus-transition regressions above) rather than snapping in.
        [Test]
        public void FullscreenListingCoversTheWholeWindowAndSlidesUpFromBottom()
        {
            FullscreenListingOverlay fullscreen = null!;
            FrameSampler sampler = null!;

            AddStep("grab the overlay", () => fullscreen = fullscreenListing());
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
            AddAssert("covers both side columns too", () =>
            {
                var o = fullscreen.ScreenSpaceDrawQuad.AABBFloat;

                bool covers(osu.Framework.Graphics.Primitives.RectangleF r)
                    => o.Left <= r.Left + 0.5f && o.Top <= r.Top + 0.5f
                       && o.Right >= r.Right - 0.5f && o.Bottom >= r.Bottom - 0.5f;

                return covers(screen.LeftColumn.ScreenSpaceDrawQuad.AABBFloat)
                       && covers(screen.RightColumn.ScreenSpaceDrawQuad.AABBFloat);
            });

            AddStep("clear samples", () => sampler.Samples.Clear());
            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddUntilStep("closed back to the normal layout", () => fullscreen.State.Value == Visibility.Hidden);
            AddUntilStep("panel slid back down past the bottom", () => fullscreen.SlidePanel.Y > 1);
        }

        // The sidebar's big search button is the mouse-driven way into the one search surface, and
        // the left column keeps its full width doing it (there is no collapsed rail mode any more).
        [Test]
        public void SidebarSearchButtonOpensTheFullscreenListing()
        {
            AddAssert("left column at its full width", () => screen.LeftColumn.Width == 380);
            AddAssert("fullscreen listing starts hidden", () => fullscreenListing().State.Value == Visibility.Hidden);

            AddStep("click the sidebar's search button", () =>
                screen.ChildrenOfType<BeatmapListingOverlay.SearchButton>().Single().TriggerClick());

            AddUntilStep("fullscreen listing shown", () => fullscreenListing().State.Value == Visibility.Visible);
            AddAssert("left column unchanged", () => screen.LeftColumn.Width == 380 && screen.LeftColumn.Alpha == 1);
        }

        // Regression coverage for the (now-removed) corner gear: Settings is reachable purely by
        // clicking its own tab header in the right column — no corner shortcut needed any more.
        [Test]
        public void SettingsTabButtonSwitchesRightPanelToSettingsTab()
        {
            PlaybackPanel playbackBody = null!;
            SettingsOverlay settingsBody = null!;
            AddStep("grab playback tab and settings body", () =>
            {
                playbackBody = playbackPanel();
                settingsBody = screen.ChildrenOfType<SettingsOverlay>().Single();
            });

            AddAssert("playback tab active initially", () => playbackBody.Alpha == 1 && settingsBody.Alpha == 0);

            AddStep("click the Settings tab button", () => clickTabButton("Settings"));

            // The tab switch crossfades (see MainScreen.showTabBody) rather than cutting
            // instantly, so this needs to poll rather than assert on the very next frame.
            AddUntilStep("settings tab now active", () => settingsBody.Alpha == 1 && playbackBody.Alpha == 0);
        }

        // Regression coverage for Ctrl+Q: still a "jump to the queue" shortcut, now landing on the
        // Playback tab that holds the queue rather than sliding a drawer into view.
        [Test]
        public void CtrlQSwitchesRightPanelToPlaybackTab()
        {
            PlaybackPanel playbackBody = null!;
            SettingsOverlay settingsBody = null!;
            AddStep("grab playback tab and settings body, switch to settings", () =>
            {
                playbackBody = playbackPanel();
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

            AddUntilStep("playback tab active again", () => playbackBody.Alpha == 1 && settingsBody.Alpha == 0);
            AddAssert("the queue itself is on that tab", () => playbackBody.ChildrenOfType<QueuePanel>().Any());
        }

        // Regression coverage for the map-ID button: it sits beside the sidebar's search button
        // rather than in a corner, and opens the one shared MapIdOverlay via
        // BeatmapListingOverlay.MapIdRequested.
        [Test]
        public void HashtagButtonTogglesMapIdOverlay()
        {
            MapIdOverlay overlay = null!;
            AddStep("grab map-id overlay", () => overlay = screen.ChildrenOfType<MapIdOverlay>().Single());

            AddAssert("starts hidden", () => overlay.State.Value == Visibility.Hidden);

            AddStep("click the hashtag button beside the search button",
                () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Hashtag)).TriggerClick());
            AddAssert("overlay visible", () => overlay.State.Value == Visibility.Visible);

            AddStep("click the hashtag button again",
                () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Hashtag)).TriggerClick());
            AddAssert("overlay hidden again", () => overlay.State.Value == Visibility.Hidden);
        }

        // The folder button is the click-to-import counterpart to dropping a file on the window,
        // and it must land in the SAME importer — asserted end to end here: an unsupported path
        // picked in the overlay comes back as the drop importer's own "can't import" toast, which
        // only happens if MainScreen really routed it through DroppedFileImporter.
        [Test]
        public void FolderButtonOpensThePickerAndItsChoiceGoesThroughTheDropImporter()
        {
            FileImportOverlay picker = null!;
            AddStep("grab the file-import overlay", () => picker = screen.ChildrenOfType<FileImportOverlay>().Single());

            AddAssert("starts hidden", () => picker.State.Value == Visibility.Hidden);

            AddStep("click the folder button",
                () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.FolderOpen)).TriggerClick());
            AddAssert("picker visible", () => picker.State.Value == Visibility.Visible);

            // Deliberately an extension the importer rejects: it reports the rejection without
            // touching storage, the mirror or the queue, so this asserts the wiring and nothing else.
            string unimportable = Path.Combine(tmp, "not-a-beatmap.txt");
            AddStep("pick a file the importer can't take", () =>
            {
                File.WriteAllText(unimportable, "hello");
                picker.Selector.CurrentFile.Value = new FileInfo(unimportable);
            });

            AddUntilStep("picker closed itself", () => picker.State.Value == Visibility.Hidden);
            AddUntilStep("the drop importer's own rejection surfaced as a toast", () =>
                screen.ChildrenOfType<SpriteText>().Any(t => t.Text.ToString().Contains("not-a-beatmap.txt")
                                                             && t.Text.ToString().Contains(".osz")));
        }

        // RightPanelTabButton is a private nested type (MainScreen) — located by type name the
        // same way TestSceneBeatmapListing locates FiltersToggleButton, then disambiguated by its
        // own label text.
        private void clickTabButton(string label)
            => screen.ChildrenOfType<ClickableContainer>()
                     .First(c => c.GetType().Name == "RightPanelTabButton" && c.ChildrenOfType<SpriteText>().Any(t => t.Text == label))
                     .TriggerClick();

        // Regression coverage for the queue drawer's old floating-drawer geometry (off-screen X,
        // Y-only relative sizing) — the docked presentation must instead fill the queue section it
        // is given inside the Playback tab, since QueuePanel's own LoadComplete otherwise parks it
        // off-screen.
        [Test]
        public void QueuePanelDockedFillsTheQueueSection()
        {
            AddAssert("queue panel X == 0", () => screen.ChildrenOfType<QueuePanel>().Single().X == 0);
            AddAssert("queue panel anchored top-left", () => screen.ChildrenOfType<QueuePanel>().Single().Anchor == Anchor.TopLeft);
            AddAssert("queue panel origin top-left", () => screen.ChildrenOfType<QueuePanel>().Single().Origin == Anchor.TopLeft);
            AddAssert("queue panel fully relative", () => screen.ChildrenOfType<QueuePanel>().Single().RelativeSizeAxes == Axes.Both);
            AddAssert("queue panel width == 1", () => screen.ChildrenOfType<QueuePanel>().Single().Width == 1f);
            AddAssert("queue panel height == 1", () => screen.ChildrenOfType<QueuePanel>().Single().Height == 1f);
        }

        // Queueing something used to be entirely silent — with the listing overlay covering the
        // queue, a pick could look like it did nothing at all. The toast must also stay visually
        // distinct from the error toast that shares the same presentation.
        [Test]
        public void EnqueueShowsAToastDistinctFromErrorToasts()
        {
            AddStep("enqueue a set", () => jukebox.EnqueueAndMaybePlayAsync(
                new BeatmapSetInfo { Id = 4242, Title = "Toasted", Artist = "Artist" }));

            AddUntilStep("toast names the set", () => toast("Added to queue: Toasted") != null);
            AddAssert("toast is not error-coloured", () => toast("Added to queue: Toasted")!.Colour == Theme.Accent);

            AddStep("report an error", () => jukebox.LastError.Value = "Something broke");
            AddUntilStep("error toast shown", () => toast("Something broke") != null);
            AddAssert("error toast stays red", () => toast("Something broke")!.Colour == Theme.Error);
        }

        private SpriteText? toast(string message)
            => screen.ChildrenOfType<SpriteText>().FirstOrDefault(t => t.Text.ToString() == message);

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

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
