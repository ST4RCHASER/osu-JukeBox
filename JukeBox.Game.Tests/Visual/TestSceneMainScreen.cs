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
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Shapes;
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

                // Both mask releases and anything a previous test put on screen: back to the
                // resting state every other test here assumes (boxed player, nothing playing).
                config.SetValue(JukeBoxSetting.RemoveChartMask, false);
                config.SetValue(JukeBoxSetting.RemoveStoryboardMask, false);
                playback.Current.Value = null;
                playback.SelectedOsuFile.Value = null;

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

        // The strip's spacing, measured off the buttons' actual on-screen rectangles rather than off
        // the values handed to the layout. The bug this guards was invisible in the source — three
        // symmetric-looking 3px margins — and existed only in the rendered geometry, because Margin
        // does not shrink a RelativeSizeAxes child: it left a gap on one side of Chart, none on the
        // other, and pushed Settings past the panel's padding.
        //
        // Two panel widths because the strip divides the panel's inner width by three and neither
        // width divides evenly; evenness at 340 alone would be arithmetic luck rather than layout.
        [TestCase(340f)]
        [TestCase(299f)]
        public void TabStripHasEvenGapsAndEvenOuterPadding(float panelWidth)
        {
            AddStep($"set the right column to {panelWidth}px", () => screen.RightColumn.Width = panelWidth);
            AddWaitStep("let the strip settle", 3);

            AddAssert("the three buttons are equal width", () =>
            {
                var r = tabButtonRects();
                return Math.Abs(r[0].Width - r[1].Width) < 0.5f && Math.Abs(r[1].Width - r[2].Width) < 0.5f;
            });

            AddAssert("Chart has the same gap on both sides", () =>
            {
                var r = tabButtonRects();
                return Math.Abs((r[1].Left - r[0].Right) - (r[2].Left - r[1].Right)) < 0.5f;
            });

            AddAssert("the strip is inset by the same amount at both ends of the panel", () =>
            {
                var r = tabButtonRects();
                var panel = screen.RightColumn.ScreenSpaceDrawQuad.AABBFloat;
                return Math.Abs((r[0].Left - panel.Left) - (panel.Right - r[2].Right)) < 0.5f;
            });

            // The outer inset is deliberately NOT the inter-button gap. It is the right column's own
            // PanelPadding, shared with every tab body below the strip, so the buttons line up with
            // the content they switch between; the gap is the narrower sibling-control spacing.
            AddAssert("outer inset is the panel padding, gap is the row spacing", () =>
            {
                var r = tabButtonRects();
                var panel = screen.RightColumn.ScreenSpaceDrawQuad.AABBFloat;
                return Math.Abs((r[0].Left - panel.Left) - Theme.PanelPadding) < 0.5f
                       && Math.Abs((r[1].Left - r[0].Right) - Theme.RowSpacing) < 0.5f;
            });
        }

        // The accent underline is a child of its button, so it can only sit right if the button
        // itself does. That is precisely what the margin bug broke for Settings, whose button — and
        // with it the underline — hung past the panel's padded content box.
        [TestCase(RightPanelTabName.Playback)]
        [TestCase(RightPanelTabName.Chart)]
        [TestCase(RightPanelTabName.Settings)]
        public void ActiveTabUnderlineSpansItsButtonAndStaysInsideThePanel(RightPanelTabName tab)
        {
            AddStep($"select {tab}", () => clickTab(tab));
            AddUntilStep("underline fully shown", () => activeTabUnderline() != null);
            AddWaitStep("let the strip settle", 3);

            AddAssert("underline spans exactly its own button", () =>
            {
                var button = tabButtons()[(int)tab].ScreenSpaceDrawQuad.AABBFloat;
                var line = activeTabUnderline()!.ScreenSpaceDrawQuad.AABBFloat;
                return Math.Abs(line.Left - button.Left) < 0.5f && Math.Abs(line.Right - button.Right) < 0.5f;
            });

            AddAssert("underline sits inside the panel's padded content box", () =>
            {
                var panel = screen.RightColumn.ScreenSpaceDrawQuad.AABBFloat;
                var line = activeTabUnderline()!.ScreenSpaceDrawQuad.AABBFloat;
                return line.Left >= panel.Left + Theme.PanelPadding - 0.5f
                       && line.Right <= panel.Right - Theme.PanelPadding + 0.5f;
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

        /// <summary>
        /// The bug this guards: all three bodies live in one container, so without depth management
        /// they draw in declaration order and switching to an earlier-declared tab put the ARRIVING
        /// body underneath the departing one — two panels visibly stacked mid-transition, whatever
        /// the fades were doing. Checked in both directions, since only one of them was ever wrong.
        /// </summary>
        [TestCase(RightPanelTabName.Settings, RightPanelTabName.Playback)]
        [TestCase(RightPanelTabName.Playback, RightPanelTabName.Settings)]
        [TestCase(RightPanelTabName.Chart, RightPanelTabName.Playback)]
        public void TheArrivingTabBodyIsDrawnInFrontOfTheLeavingOne(RightPanelTabName from, RightPanelTabName to)
        {
            AddStep($"go to {from}", () => clickTab(from));
            AddUntilStep("settled", () => bodyFor(from).Alpha == 1);

            AddStep($"switch to {to}", () => clickTab(to));

            // Immediately, while both are still on screen: the arriving one must be in front.
            AddAssert("the arriving body is in front of the leaving one",
                () => bodyFor(to).Depth < bodyFor(from).Depth);
        }

        /// <summary>
        /// The other half of "the old one is still there": it used to fade while sitting perfectly
        /// still at x=0, so it read as a panel that never left while the new one slid over it. It
        /// must travel out in the same direction the incoming travels in.
        /// </summary>
        [Test]
        public void TheLeavingTabBodyAnimatesOutInsteadOfSittingStill()
        {
            AddStep("go to Playback", () => clickTab(RightPanelTabName.Playback));
            AddUntilStep("settled", () => playbackPanel().Alpha == 1 && playbackPanel().X == 0);

            AddStep("switch to Chart", () => clickTab(RightPanelTabName.Chart));

            AddUntilStep("the leaving body is both fading AND moving away",
                () => playbackPanel().Alpha < 1 && playbackPanel().X < 0);

            // Same direction for both: content travels leftward across the swap.
            AddAssert("and the arriving body is coming from the other side, moving the same way",
                () => chartPanel().X >= 0);

            AddUntilStep("the swap settles", () => chartPanel().Alpha == 1 && chartPanel().X == 0);
            AddAssert("with the old body gone", () => playbackPanel().Alpha == 0);
        }

        /// <summary>Exactly one body is ever left visible — the case a ghost would show up in.</summary>
        [TestCase(RightPanelTabName.Playback)]
        [TestCase(RightPanelTabName.Chart)]
        [TestCase(RightPanelTabName.Settings)]
        public void AfterASwitchExactlyOneBodyIsVisible(RightPanelTabName to)
        {
            AddStep($"switch to {to}", () => clickTab(to));
            AddUntilStep("the swap settles", () => bodyFor(to).Alpha == 1);

            AddAssert("exactly one body is visible", () => allBodies().Count(b => b.Alpha > 0) == 1);
            AddAssert("and it is the right one", () => bodyFor(to).Alpha == 1);
        }

        /// <summary>
        /// Clicking through every tab as fast as the input can be delivered — the case most likely
        /// to strand a body at a partial alpha or an offset x, since each switch interrupts the
        /// last one's transforms mid-flight.
        /// </summary>
        [Test]
        public void RapidSwitchingSettlesOnTheRightBodyWithNothingOrphaned()
        {
            AddStep("click through all three without waiting", () =>
            {
                clickTab(RightPanelTabName.Chart);
                clickTab(RightPanelTabName.Settings);
                clickTab(RightPanelTabName.Playback);
                clickTab(RightPanelTabName.Chart);
            });

            AddUntilStep("it settles on the last one clicked",
                () => chartPanel().Alpha == 1 && chartPanel().X == 0);

            AddAssert("with nothing else left visible", () => allBodies().Count(b => b.Alpha > 0) == 1);
            AddAssert("and no body stranded off-position",
                () => allBodies().All(b => b.Alpha > 0 || b.Alpha == 0));
        }

        public enum RightPanelTabName
        {
            Playback,
            Chart,
            Settings,
        }

        private void clickTab(RightPanelTabName tab) => tabButtons()[(int)tab].Action?.Invoke();

        private Drawable bodyFor(RightPanelTabName tab) => tab switch
        {
            RightPanelTabName.Playback => playbackPanel(),
            RightPanelTabName.Chart => chartPanel(),
            _ => screen.ChildrenOfType<SettingsOverlay>().Single(),
        };

        private Drawable[] allBodies()
            => new[] { bodyFor(RightPanelTabName.Playback), bodyFor(RightPanelTabName.Chart), bodyFor(RightPanelTabName.Settings) };

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
               // SkinChoice, not JukeBoxSkin: the gameplay-skin row lists imported skins
               // individually, so a row is a bundled skin OR a specific import, which no enum can
               // express on its own.
               .Concat(labelsOf<SkinChoice>(panel))
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

        private List<RectangleF> tabButtonRects()
            => tabButtons().Select(c => c.ScreenSpaceDrawQuad.AABBFloat).OrderBy(r => r.Left).ToList();

        // A tab button holds two Boxes in declaration order — the full-bleed background first, then
        // the accent underline at its foot (see RightPanelTabButton's constructor) — and only the
        // active tab's underline is opaque. Null while a switch is still crossfading.
        private Box? activeTabUnderline()
            => tabButtons().Select(c => c.ChildrenOfType<Box>().Last()).SingleOrDefault(u => u.Alpha == 1);

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

        // ---- the two mask releases (Settings → Gameplay) ----
        //
        // A child can never escape an ancestor's mask, so releasing a layer past the box's edges
        // means the BOX stops clipping and BeatmapVisuals' own per-layer clips — each sized to that
        // same box — take over for whatever the user did NOT release. These tests drive that from
        // the real config, through the real screen, with a real beatmap's visual stack on screen,
        // and ask the only question that matters of each layer: is anything still clipping it?

        private CachedBeatmapSet? maskFixtureSet;

        /// <summary>
        /// Puts a real <see cref="BeatmapVisuals"/> on screen through the same bindable
        /// <see cref="NowPlayingScreen"/> listens to, so the clips under test are the ones the app
        /// actually builds rather than a stand-in. A one-hit-object difficulty is enough: nothing
        /// here depends on what the chart contains.
        /// </summary>
        private void addRealVisuals()
        {
            AddStep("put a real beatmap on screen", () =>
            {
                if (maskFixtureSet == null)
                {
                    string dir = Path.Combine(tmp, "mask-fixture");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "mask [Normal].osu"),
                        "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n[Metadata]\nVersion:Normal\n\n"
                        + "[Difficulty]\nCircleSize:4\n\n[TimingPoints]\n0,500,4,1,0,100,1,0\n\n[HitObjects]\n"
                        // Objects at the playfield's CORNERS, not its middle: a circle sitting at
                        // the edge is the one whose approach ring reaches past the playfield, and a
                        // fixture that only ever placed one in the centre is exactly why the leak
                        // the user hit was invisible to these tests. Spread over ten seconds so one
                        // is always alive whatever the clock has reached.
                        + string.Join(string.Empty, Enumerable.Range(0, 40).Select(i =>
                            $"{(i % 2 == 0 ? 0 : 512)},{(i % 4 < 2 ? 0 : 384)},{500 + i * 250},1,0\n")));

                    maskFixtureSet = cache.LoadFromDirectory(424242, dir);
                }

                playback.Current.Value = maskFixtureSet;
                playback.SelectedOsuFile.Value = maskFixtureSet.PreferredOsuFile;
            });

            AddUntilStep("visuals loaded into the box", () => screen.ChildrenOfType<BeatmapVisuals>().Any(v => v.IsLoaded));
        }

        private CachedBeatmapSet? catchFixtureSet;

        /// <summary>
        /// The same as <see cref="addRealVisuals"/>, in osu!catch. Catch is the ruleset whose own
        /// playfield container clips its contents (see LazerChartLayer's mask release), so it is the
        /// one that can tell whether releasing the chart mask really frees the chart.
        /// </summary>
        private void addRealCatchVisuals()
        {
            AddStep("put a real catch beatmap on screen", () =>
            {
                if (catchFixtureSet == null)
                {
                    string dir = Path.Combine(tmp, "catch-fixture");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "catch [Salad].osu"),
                        "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 2\n\n[Metadata]\nVersion:Salad\n\n"
                        + "[Difficulty]\nCircleSize:4\n\n[TimingPoints]\n0,500,4,1,0,100,1,0\n\n[HitObjects]\n"
                        + string.Join(string.Empty, Enumerable.Range(0, 40).Select(i =>
                            $"{(i % 2 == 0 ? 0 : 512)},192,{500 + i * 250},1,0\n")));

                    catchFixtureSet = cache.LoadFromDirectory(424243, dir);
                }

                playback.Current.Value = catchFixtureSet;
                playback.SelectedOsuFile.Value = catchFixtureSet.PreferredOsuFile;
            });

            AddUntilStep("visuals loaded into the box", () => screen.ChildrenOfType<BeatmapVisuals>().Any(v => v.IsLoaded));
        }

        /// <summary>
        /// Every masking container between <paramref name="d"/> and the screen, ours and lazer's
        /// alike — a superset of <see cref="clippers"/>, which only sees <see cref="Container"/>s.
        /// A ruleset clips its own playfield with whatever composite it likes.
        /// </summary>
        private List<CompositeDrawable> maskingAncestors(Drawable d)
        {
            var found = new List<CompositeDrawable>();

            for (CompositeDrawable? p = d.Parent; p != null; p = p.Parent)
            {
                if (p.Masking)
                    found.Add(p);

                if (ReferenceEquals(p, screen))
                    break;
            }

            return found;
        }

        /// <summary>Whatever is clipping <paramref name="d"/> that is NOT one of ours — i.e. the
        /// ruleset's own clipping of its playfield.</summary>
        private List<CompositeDrawable> lazersOwnClips(Drawable d)
            => maskingAncestors(d)
               .Where(m => !ReferenceEquals(m, visuals().ChartClip) && !ReferenceEquals(m, screen.PlayerBox))
               .ToList();

        /// <summary>
        /// The user's report against osu!catch: with the chart mask released the fruits still got
        /// cut in half along a horizontal line above the frame. Everything of OURS was already
        /// released by then — what was left is the RULESET's own clip (catch's playfield adjustment
        /// container holds a "visible area" that clips the whole playfield), and a child can never
        /// escape an ancestor's mask. Releasing means releasing those too, and putting them back
        /// when the setting goes off.
        /// </summary>
        [Test]
        public void NothingMasksAChartObjectOnceReleased()
        {
            AddStep("render the chart, zoomed out", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.PlayfieldZoom, 0.5);
            });

            addRealCatchVisuals();
            AddUntilStep("the chart is drawing something", () => chartDrawables().Any());

            // The condition that makes this test mean anything: catch really does clip its own
            // playfield, over and above our clip and the box.
            AddAssert("lazer clips its own playfield to begin with",
                () => lazersOwnClips(chartDrawables().First()).Any());

            AddStep("release the chart mask", () => config.SetValue(JukeBoxSetting.RemoveChartMask, true));

            AddUntilStep("nothing masks a hit object any more",
                () => !maskingAncestors(chartDrawables().First()).Any());
            AddAssert("and the chart really does draw outside the scene now",
                () => chartDrawables().Any(d => !insideOf(d, screen.VisualsStack)));

            AddStep("put the chart mask back", () => config.SetValue(JukeBoxSetting.RemoveChartMask, false));

            AddUntilStep("lazer's own clip is back, exactly as it built it",
                () => lazersOwnClips(chartDrawables().First()).Any() && visuals().ChartClip.Masking);
            AddAssert("and the release is holding nothing open any more",
                () => visuals().ChartRenderer!.ReleasedMaskCount == 0);

            AddStep("restore", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0);
            });
        }

        private BeatmapVisuals visuals() => screen.ChildrenOfType<BeatmapVisuals>().First(v => v.IsLoaded);

        /// <summary>
        /// Every drawable the hosted ruleset actually puts on screen — the playfield's own content,
        /// which is what a user means by "the chart". Measured rather than reasoned about: the leak
        /// this covers was invisible to tests that only asked the clip containers what they had been
        /// set to.
        /// </summary>
        private IEnumerable<Drawable> chartDrawables()
            => visuals().ChartRenderer?.DrawableRuleset?.Playfield.HitObjectContainer.Objects.Cast<Drawable>()
               ?? Enumerable.Empty<Drawable>();

        private static bool insideOf(Drawable inner, Drawable outer, float tolerance = 1f)
        {
            var a = inner.ScreenSpaceDrawQuad.AABBFloat;
            var b = outer.ScreenSpaceDrawQuad.AABBFloat;

            return a.Left >= b.Left - tolerance && a.Right <= b.Right + tolerance
                   && a.Top >= b.Top - tolerance && a.Bottom <= b.Bottom + tolerance;
        }

        /// <summary>
        /// The user's report: with "Remove playfield/chart mask" OFF — the default — hit circles
        /// still drew outside the scene, on the black around it, once the playfield was zoomed out.
        /// The scene is what the background and storyboard fill, so it is what a user reads as "the
        /// screen"; that is what the chart is clipped to while the mask is on. Checked at several
        /// zooms because the leak only shows once the scene is smaller than the box.
        /// </summary>
        [TestCase(0.5)]
        [TestCase(0.8)]
        [TestCase(1.0)]
        [TestCase(1.6)]
        public void ByDefaultTheChartNeverDrawsOutsideTheScene(double zoom)
        {
            AddStep("render the chart", () => config.SetValue(JukeBoxSetting.RenderChart, true));
            AddStep($"zoom {zoom:0.0}x", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, zoom));

            addRealVisuals();

            AddUntilStep("the chart is drawing something", () => chartDrawables().Any());

            // The condition that makes the clip matter, asserted rather than assumed: a circle at
            // the playfield's edge really does reach past the scene, so "nothing is outside" can
            // only be true because something clips it.
            AddAssert("some chart drawable really does overflow the scene",
                () => chartDrawables().Any(d => !insideOf(d, screen.VisualsStack)));

            AddAssert("and the scene itself is what clips the chart", () =>
            {
                var clipping = clippers(chartDrawables().First());

                return clipping.Count > 0 && coversTheSameRect(clipping[0], screen.VisualsStack);
            });

            AddStep("restore", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0);
            });
        }

        /// <summary>Releasing the chart mask is what lets it spill: nothing between the chart and
        /// the screen clips it any more, the box included.</summary>
        [Test]
        public void ReleasingTheChartMaskLetsItLeaveTheSceneAndTheBox()
        {
            AddStep("render the chart, zoomed out", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.PlayfieldZoom, 0.5);
            });

            addRealVisuals();
            AddUntilStep("the chart is drawing something", () => chartDrawables().Any());

            AddAssert("clipped to the scene to begin with",
                () => coversTheSameRect(clippers(chartDrawables().First())[0], screen.VisualsStack));

            AddStep("release the chart mask", () => config.SetValue(JukeBoxSetting.RemoveChartMask, true));

            AddUntilStep("nothing clips it any more", () => !clippers(chartDrawables().First()).Any());

            AddStep("restore", () =>
            {
                config.SetValue(JukeBoxSetting.RemoveChartMask, false);
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0);
            });
        }

        /// <summary>A marker parked far outside the player box, so "is this clipped" has a definite
        /// answer instead of depending on what a particular beatmap happens to draw.</summary>
        private static Box outsideProbe() => new Box
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new osuTK.Vector2(20),
            X = 4000,
        };

        private bool outsideTheBox(Drawable probe)
            => probe.ScreenSpaceDrawQuad.TopLeft.X > screen.PlayerBox.ScreenSpaceDrawQuad.TopRight.X;

        /// <summary>
        /// Every masking container between <paramref name="d"/> and the screen — that is, everything
        /// that would actually clip it. Empty means the drawable is free to render wherever it
        /// lands (up to the window itself).
        /// </summary>
        private List<Container> clippers(Drawable d)
        {
            var found = new List<Container>();

            for (CompositeDrawable? p = d.Parent; p != null; p = p.Parent)
            {
                if (p is Container { Masking: true } c)
                    found.Add(c);

                if (p == screen)
                    break;
            }

            return found;
        }

        private static bool coversTheSameRect(Drawable a, Drawable b)
        {
            var one = a.ScreenSpaceDrawQuad.AABBFloat;
            var other = b.ScreenSpaceDrawQuad.AABBFloat;

            return Math.Abs(one.X - other.X) < 1 && Math.Abs(one.Y - other.Y) < 1
                   && Math.Abs(one.Width - other.Width) < 1 && Math.Abs(one.Height - other.Height) < 1;
        }

        [Test]
        public void MaskReleasesAreOffByDefaultAndLeaveTheBoxInSoleCharge()
        {
            addRealVisuals();

            AddAssert("box clips its content", () => screen.PlayerBox.Masking);
            AddAssert("and no per-layer clip is standing in for it", () =>
                !visuals().StoryboardClip.Masking && !visuals().BackdropClip.Masking && !visuals().DimClip.Masking);

            // The chart's clip is the exception, and not a stand-in for the box at all: it bounds
            // the chart to the SCENE whenever the user has not released it, box or no box.
            AddAssert("the chart is clipped to the scene", () => visuals().ChartClip.Masking);
        }

        [Test]
        public void EachMaskReleaseFreesOnlyItsOwnLayer()
        {
            addRealVisuals();

            Box storyboardProbe = null!;
            Box chartProbe = null!;

            AddStep("park a probe past the box's edge in each layer", () =>
            {
                visuals().StoryboardClip.Add(storyboardProbe = outsideProbe());
                visuals().ChartClip.Add(chartProbe = outsideProbe());
            });

            AddUntilStep("both probes really are outside the box",
                () => outsideTheBox(storyboardProbe) && outsideTheBox(chartProbe));
            AddAssert("and both are clipped to begin with",
                () => clippers(storyboardProbe).Any() && clippers(chartProbe).Any());

            AddStep("remove the storyboard mask", () => config.SetValue(JukeBoxSetting.RemoveStoryboardMask, true));

            AddUntilStep("nothing clips the storyboard any more", () => !clippers(storyboardProbe).Any());
            // The chart's own clip is the SCENE, which is the rectangle it is bounded to whether or
            // not the box is masking — releasing the storyboard changed nothing about it.
            AddAssert("the chart is still clipped, and by the scene", () =>
                clippers(chartProbe).SequenceEqual(new[] { visuals().ChartClip })
                && coversTheSameRect(visuals().ChartClip, screen.VisualsStack));
            AddAssert("the background and the dim keep clipping too",
                () => visuals().BackdropClip.Masking && visuals().DimClip.Masking
                      && coversTheSameRect(visuals().BackdropClip, screen.PlayerBox));

            AddStep("remove the chart mask as well", () => config.SetValue(JukeBoxSetting.RemoveChartMask, true));
            AddUntilStep("now neither is clipped",
                () => !clippers(storyboardProbe).Any() && !clippers(chartProbe).Any());

            AddStep("put the storyboard mask back", () => config.SetValue(JukeBoxSetting.RemoveStoryboardMask, false));
            AddUntilStep("and only the storyboard is clipped again",
                () => clippers(storyboardProbe).Any() && !clippers(chartProbe).Any());

            AddStep("put the chart mask back", () => config.SetValue(JukeBoxSetting.RemoveChartMask, false));
            AddUntilStep("the box masks again, with the chart bounded to the scene inside it",
                () => screen.PlayerBox.Masking
                      && clippers(chartProbe).SequenceEqual(new[] { visuals().ChartClip, screen.PlayerBox }));
        }

        // ---- the "everything released" look ----
        //
        // With BOTH masks off the box is no longer the frame anything is bounded by, so its card
        // (rounded corners, shadow, black bed) would be a rectangle drawn over content deliberately
        // spilling past it — it goes; and the columns turn slightly see-through so that content
        // reads as continuing behind them.

        private void setReleases(bool chart, bool storyboard)
        {
            AddStep($"chart mask {(chart ? "off" : "on")}, storyboard mask {(storyboard ? "off" : "on")}", () =>
            {
                config.SetValue(JukeBoxSetting.RemoveChartMask, chart);
                config.SetValue(JukeBoxSetting.RemoveStoryboardMask, storyboard);
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void TheCardStaysWhileEitherLayerIsStillClipped(bool chart, bool storyboard)
        {
            addRealVisuals();
            setReleases(chart, storyboard);

            AddUntilStep("the card is fully there", () => screen.BoxFrame.Alpha == 1);
            AddAssert("and both column surfaces are opaque",
                () => screen.LeftColumnSurface.Alpha == 1 && screen.RightColumnSurface.Alpha == 1);

            AddStep("restore", () => setReleasesNow(false, false));
        }

        [Test]
        public void ReleasingBothMasksDropsTheCardAndLetsTheColumnsSeeThrough()
        {
            addRealVisuals();

            AddAssert("the card is there to begin with", () => screen.BoxFrame.Alpha == 1);

            setReleases(chart: true, storyboard: true);

            AddUntilStep("the card fades away entirely", () => screen.BoxFrame.Alpha == 0);
            AddUntilStep("and both column surfaces settle slightly see-through",
                () => screen.LeftColumnSurface.Alpha == MainScreen.released_surface_alpha
                      && screen.RightColumnSurface.Alpha == MainScreen.released_surface_alpha);

            // Only the SURFACE fades: a column's own content (the docked listing, the tab bodies)
            // has to stay fully legible on top of it.
            AddAssert("the columns themselves, and their content, stay opaque",
                () => screen.LeftColumn.Alpha == 1 && screen.RightColumn.Alpha == 1
                      && screen.ChildrenOfType<BeatmapListingOverlay>().First().Alpha == 1);

            setReleases(chart: true, storyboard: false);

            AddUntilStep("putting either mask back brings the card and the surfaces back",
                () => screen.BoxFrame.Alpha == 1
                      && screen.LeftColumnSurface.Alpha == 1 && screen.RightColumnSurface.Alpha == 1);

            AddStep("restore", () => setReleasesNow(false, false));
        }

        /// <summary>
        /// Focus mode fades the whole columns and animates the card's radius; the released look
        /// fades the card and the column SURFACES. The two have to compose rather than fight over
        /// the same drawables.
        /// </summary>
        [Test]
        public void TheReleasedLookAndFocusModeDoNotFightOverTheSameDrawables()
        {
            addRealVisuals();
            setReleases(chart: true, storyboard: true);

            AddUntilStep("card gone, surfaces see-through", () =>
                screen.BoxFrame.Alpha == 0 && screen.LeftColumnSurface.Alpha == MainScreen.released_surface_alpha);

            AddStep("enter focus mode", () => InputManager.Key(Key.Tab));

            AddUntilStep("the columns leave and the card stays gone",
                () => screen.LeftColumn.Alpha == 0 && screen.RightColumn.Alpha == 0 && screen.BoxFrame.Alpha == 0);

            AddStep("put the masks back", () => setReleasesNow(false, false));

            AddUntilStep("the card returns even with the columns still away",
                () => screen.BoxFrame.Alpha == 1 && screen.LeftColumn.Alpha == 0);
        }

        private void setReleasesNow(bool chart, bool storyboard)
        {
            config.SetValue(JukeBoxSetting.RemoveChartMask, chart);
            config.SetValue(JukeBoxSetting.RemoveStoryboardMask, storyboard);
        }

        // A released layer reaching over the gutter must go UNDER the side columns, never over
        // them: the columns are later children of this screen than the player's host and every one
        // of the three sits at the same depth, so child order is what decides — which is exactly
        // what this pins down.
        [Test]
        public void ReleasedContentStillDrawsBehindTheSideColumns()
        {
            addRealVisuals();

            Box probe = null!;

            AddStep("release the storyboard and add a probe", () =>
            {
                config.SetValue(JukeBoxSetting.RemoveStoryboardMask, true);
                visuals().StoryboardClip.Add(probe = outsideProbe());
            });

            // Aimed from live geometry rather than a guessed offset: the probe has to land ON the
            // right column (outside the box, inside the panel) for "released content goes under the
            // panel, not over it" to be a question at all.
            AddStep("aim it at the middle of the right column", () =>
            {
                var box = screen.PlayerBox.ScreenSpaceDrawQuad;
                var column = screen.RightColumn.ScreenSpaceDrawQuad;

                float columnCentre = (column.TopLeft.X + column.TopRight.X) / 2;
                float boxCentre = (box.TopLeft.X + box.TopRight.X) / 2;

                probe.X = (columnCentre - boxCentre) / screen.SceneScale.X;
            });

            AddUntilStep("the probe is unclipped and really overlaps the column", () =>
                !clippers(probe).Any()
                && probe.ScreenSpaceDrawQuad.AABBFloat.IntersectsWith(screen.RightColumn.ScreenSpaceDrawQuad.AABBFloat));

            AddAssert("but the player's host is drawn before both columns", () =>
            {
                var order = screen.ChildrenOfType<Drawable>().ToList();
                var host = screen.PlayerBox.Parent!;

                return order.IndexOf(host) >= 0
                       && order.IndexOf(host) < order.IndexOf(screen.LeftColumn)
                       && order.IndexOf(host) < order.IndexOf(screen.RightColumn)
                       && host.Depth == screen.LeftColumn.Depth && host.Depth == screen.RightColumn.Depth;
            });
        }

        // Releasing the content mask must not cost the player its card look: the rounding and the
        // drop shadow live on a frame INSIDE the box for exactly this reason, and both radii still
        // animate away together on the way into focus mode.
        [Test]
        public void TheCardKeepsItsRoundedShadowedFrameWhileTheBoxIsReleased()
        {
            AddAssert("card frame masks, rounds and casts the shadow", () =>
                screen.BoxFrame.Masking && screen.BoxFrame.CornerRadius > 0
                && screen.BoxFrame.EdgeEffect.Type == EdgeEffectType.Shadow && screen.BoxFrame.EdgeEffect.Radius > 0);

            AddStep("release the chart mask", () => config.SetValue(JukeBoxSetting.RemoveChartMask, true));
            AddUntilStep("the box stops clipping", () => !screen.PlayerBox.Masking);

            AddAssert("the card is untouched", () =>
                screen.BoxFrame.Masking && screen.BoxFrame.CornerRadius > 0
                && screen.BoxFrame.EdgeEffect.Type == EdgeEffectType.Shadow && screen.BoxFrame.EdgeEffect.Radius > 0);
            AddAssert("and it still covers exactly the box", () => coversTheSameRect(screen.BoxFrame, screen.PlayerBox));

            AddStep("enter focus mode", () => InputManager.Key(Key.Tab));
            AddUntilStep("both radii animate away together",
                () => screen.PlayerBox.CornerRadius == 0 && screen.BoxFrame.CornerRadius == 0);
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

        // The user's actual complaint: with a list of options in front of you, typing means "find
        // one of these" — not "throw a beatmap listing over the top". Routing follows the tab.
        [TestCase(RightPanelTabName.Chart)]
        [TestCase(RightPanelTabName.Settings)]
        public void TypingInATabWithOptionsSearchesThatTabInsteadOfBeatmaps(RightPanelTabName tab)
        {
            AddStep($"open the {tab} tab", () => clickTab(tab));

            AddStep("press 'd'", () => InputManager.Key(Key.D));

            AddUntilStep("the tab's own search took it", () => searchTermOf(tab) == "d");
            AddAssert("and the beatmap listing stayed shut", () => fullscreenListing().State.Value == Visibility.Hidden);

            AddStep("press 'i' then 'm'", () =>
            {
                InputManager.Key(Key.I);
                InputManager.Key(Key.M);
            });

            AddUntilStep("the term builds up rather than replacing itself", () => searchTermOf(tab) == "dim");
        }

        // The Playback tab has no list of options to filter, so it keeps the behaviour it had.
        [Test]
        public void TypingOnThePlaybackTabStillOpensTheBeatmapListing()
        {
            AddStep("open the Playback tab", () => clickTab(RightPanelTabName.Playback));

            AddStep("press 'a'", () => InputManager.Key(Key.A));

            AddUntilStep("fullscreen listing shown", () => fullscreenListing().State.Value == Visibility.Visible);
            AddAssert("seeded with what was typed", () => fullscreenListing().SearchBox.Text == "a");
        }

        // A filter left behind on a tab you cannot see is a trap — you come back later to a panel
        // missing half its rows for no visible reason.
        [Test]
        public void SwitchingAwayFromATabClearsItsFilter()
        {
            AddStep("open Settings and filter it", () =>
            {
                clickTab(RightPanelTabName.Settings);
                InputManager.Key(Key.D);
            });

            AddUntilStep("filtered", () => searchTermOf(RightPanelTabName.Settings) == "d");

            AddStep("switch to Playback", () => clickTab(RightPanelTabName.Playback));
            AddStep("switch back to Settings", () => clickTab(RightPanelTabName.Settings));

            AddAssert("the filter did not survive", () => searchTermOf(RightPanelTabName.Settings).Length == 0);
        }

        private string searchTermOf(RightPanelTabName tab) => tab == RightPanelTabName.Chart
            ? chartPanel().SearchTerm
            : screen.ChildrenOfType<SettingsOverlay>().Single().SearchTerm;

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

        // The map-ID lookup moved from a sidebar "#" button to the menu bar (Queue > Lookup by id…);
        // invoking that menu action opens the one shared MapIdOverlay.
        [Test]
        public void LookupByIdMenuActionOpensMapIdOverlay()
        {
            MapIdOverlay overlay = null!;
            AddStep("grab map-id overlay", () => overlay = screen.ChildrenOfType<MapIdOverlay>().Single());

            AddAssert("starts hidden", () => overlay.State.Value == Visibility.Hidden);

            AddStep("invoke Queue > Lookup by id",
                () => screen.ChildrenOfType<MenuBar>().Single().Actions.LookupById!());
            AddAssert("overlay visible", () => overlay.State.Value == Visibility.Visible);
        }

        // File open moved from a sidebar folder button to the menu bar (File > Open…); it must still
        // land in the SAME importer — asserted end to end: an unsupported path picked in the overlay
        // comes back as the drop importer's own "can't import" toast, which only happens if MainScreen
        // routed it through DroppedFileImporter.
        [Test]
        public void OpenFilesMenuActionRoutesThroughTheDropImporter()
        {
            FileImportOverlay picker = null!;
            AddStep("grab the file-import overlay", () =>
            {
                // The in-app picker path (every platform without a native dialog): a real OS panel
                // cannot be driven from here and would sit over the test host.
                screen.UseNativeOpenDialog = false;
                picker = screen.ChildrenOfType<FileImportOverlay>().Single();
            });

            AddAssert("starts hidden", () => picker.State.Value == Visibility.Hidden);

            AddStep("invoke File > Open",
                () => screen.ChildrenOfType<MenuBar>().Single().Actions.OpenFiles!());
            AddAssert("picker visible", () => picker.State.Value == Visibility.Visible);

            // Deliberately an extension the importer rejects: it reports the rejection without
            // touching storage, the mirror or the queue, so this asserts the wiring and nothing else.
            // Resolved inside the step: `tmp` is created with the scene's dependencies, which have
            // not been built yet when this method merely REGISTERS its steps (run in isolation).
            string unimportable = null!;
            AddStep("pick a file the importer can't take", () =>
            {
                unimportable = Path.Combine(tmp, "not-a-beatmap.txt");
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
        // distinct from the error toast that shares the same presentation: the two now differ by
        // their accent (bar + icon) rather than by the message text's own colour, since the text is
        // white on a surface in both cases.
        [Test]
        public void EnqueueShowsAToastDistinctFromErrorToasts()
        {
            AddStep("enqueue a set", () => jukebox.EnqueueAndMaybePlayAsync(
                new BeatmapSetInfo { Id = 4242, Title = "Toasted", Artist = "Artist" }));

            AddUntilStep("toast names the set", () => toast("Added to queue: Toasted") != null);
            AddAssert("toast is not error-coloured", () => toast("Added to queue: Toasted")!.AccentColour == Theme.Accent);

            AddStep("report an error", () => jukebox.LastError.Value = "Something broke");
            AddUntilStep("error toast shown", () => toast("Something broke") != null);
            AddAssert("error toast stays red", () => toast("Something broke")!.AccentColour == Theme.Error);
        }

        // The last mile of the video fallback: BeatmapVisuals reporting an unplayable video has to
        // actually reach the user. Driven through the notifier rather than a real broken beatmap —
        // the visual stack's own reporting is covered in TestSceneBeatmapVisuals, and what's under
        // test here is that MainScreen turns that report into exactly one toast.
        [Test]
        public void AnUnplayableVideoBecomesExactlyOneToast()
        {
            AddStep("report an unplayable video", () => screen.VideoNotifier.ReportUnplayableVideo(777));

            AddUntilStep("a toast says so", () => screen.Toasts.LiveToasts.Any(t => t.Message.Contains("video can't be played")));
            AddAssert("and it is error-coloured",
                () => screen.Toasts.LiveToasts.First(t => t.Message.Contains("video can't be played")).AccentColour == Theme.Error);

            AddStep("report the same set again", () => screen.VideoNotifier.ReportUnplayableVideo(777));
            AddWaitStep("give a second toast time to appear", 5);
            AddAssert("still only one toast for it",
                () => screen.Toasts.LiveToasts.Count(t => t.Message.Contains("video can't be played")) == 1);

            // A different beatmap is a different problem and does get its own notice.
            AddStep("report a different set", () => screen.VideoNotifier.ReportUnplayableVideo(778));
            AddUntilStep("which is announced too",
                () => screen.Toasts.LiveToasts.Count(t => t.Message.Contains("video can't be played")) == 2);
        }

        /// <summary>
        /// Toasts belong to the WINDOW's bottom-right, not the player's (user request). The player
        /// box moves and resizes with the sidebars, focus mode and PlayfieldZoom; the strip must not
        /// follow it.
        /// </summary>
        [Test]
        public void ToastsSitAtTheWindowsBottomRightAndDoNotFollowThePlayer()
        {
            // Pushed straight at the overlay rather than through jukebox.LastError: that bindable is
            // fixture-scoped and survives between tests.
            AddStep("push a toast", () => screen.Toasts.Push("Bottom right, please"));

            RectangleF toastBox = default;
            RectangleF windowBox = default;
            RectangleF playerBefore = default;

            AddUntilStep("a toast settles", () => settledToast(ref toastBox, ref windowBox));

            AddAssert("it hugs the window's bottom-right corner",
                () => windowBox.Right - toastBox.Right < 40 && windowBox.Bottom - toastBox.Bottom < 40);

            AddStep("remember how big the player is", () => playerBefore = screen.PlayerBox.ScreenSpaceDrawQuad.AABBFloat);
            AddStep("collapse the sidebars (focus mode)", () => config.SetValue(JukeBoxSetting.UiLayout, UiLayout.Focus));

            AddUntilStep("the player box really did change size",
                () => System.Math.Abs(screen.PlayerBox.ScreenSpaceDrawQuad.AABBFloat.Width - playerBefore.Width) > 50);

            RectangleF toastAfter = default;
            RectangleF windowAfter = default;

            AddUntilStep("the toast settles again", () => settledToast(ref toastAfter, ref windowAfter));

            AddAssert("the toast did not move with it",
                () => System.Math.Abs(toastAfter.Right - toastBox.Right) < 1
                      && System.Math.Abs(toastAfter.Bottom - toastBox.Bottom) < 1);

            AddStep("restore the layout", () => config.SetValue(JukeBoxSetting.UiLayout, UiLayout.ThreeColumn));
        }

        /// <summary>
        /// The other half of the report: toasts were painted UNDER the fullscreen listing, so a
        /// message arriving while searching was invisible. Asserted as draw order rather than mere
        /// visibility — lower Depth is nearer the viewer, and every other child of this screen
        /// leaves its depth at 0.
        /// </summary>
        [Test]
        public void ToastsDrawAboveTheFullscreenSearchAndEveryOtherOverlay()
        {
            AddStep("open the fullscreen search", () => screen.ChildrenOfType<FullscreenListingOverlay>().Single().ShowSearch());
            AddUntilStep("it is showing", () => screen.ChildrenOfType<FullscreenListingOverlay>().Single().Alpha > 0);

            AddStep("push a toast", () => screen.Toasts.Push("Above the search"));

            AddAssert("the toast strip is nearer the viewer than the listing",
                () => screen.Toasts.Depth < screen.ChildrenOfType<FullscreenListingOverlay>().Single().Depth);

            AddAssert("and than every other top-level overlay",
                () => screen.Toasts.Depth < screen.ChildrenOfType<MapIdOverlay>().Single().Depth
                      && screen.Toasts.Depth < screen.ChildrenOfType<FileImportOverlay>().Single().Depth
                      && screen.Toasts.Depth < screen.ChildrenOfType<SettingsOverlay>().Single().Depth);

            AddStep("close the search", () => screen.ChildrenOfType<FullscreenListingOverlay>().Single().Hide());
        }

        private int toastSettledFrames;

        /// <summary>
        /// Waits for a toast to finish its entrance before measuring — it slides in from the right
        /// at 0.95 scale and legitimately overhangs mid-entrance — then waits a further settled
        /// frame, since a quad read on the frame a value settles is still built from the previous
        /// frame's layout. Measures whichever toast is newest rather than one specific message: the
        /// fixture's jukebox keeps running radio retries, and a burst of its error toasts can evict
        /// this test's own message while it watches.
        /// </summary>
        private bool settledToast(ref RectangleF toastBox, ref RectangleF windowBox)
        {
            var t = screen.Toasts.AllToasts.LastOrDefault();

            if (t is not { Alpha: >= 1, X: 0 } || t.Scale.X < 1)
            {
                toastSettledFrames = 0;
                return false;
            }

            if (++toastSettledFrames < 3)
                return false;

            toastBox = t.ScreenSpaceDrawQuad.AABBFloat;
            windowBox = screen.ScreenSpaceDrawQuad.AABBFloat;
            toastSettledFrames = 0;
            return true;
        }

        private ToastOverlay.Toast? toast(string message)
            => screen.Toasts.AllToasts.FirstOrDefault(t => t.Message == message);

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
