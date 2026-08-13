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
