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
                config.SetValue(JukeBoxSetting.UiLayout, UiLayout.FullscreenOverlay);
                screen = new MainScreen { RelativeSizeAxes = Axes.Both };
                // MainScreen is a Screen — osu!framework requires a Screen to be hosted by a
                // ScreenStack (see JukeBoxGame's own top-level screenStack.Push(new MainScreen())).
                uiContainer.Child = new ScreenStack(screen) { RelativeSizeAxes = Axes.Both };
            });
        }

        [Test]
        public void TypingOpensListingWithFullscreenLayout()
        {
            AddAssert("listing starts hidden", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().State.Value == Visibility.Hidden);

            AddStep("press 'a'", () => InputManager.Key(Key.A));
            AddUntilStep("listing visible", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().State.Value == Visibility.Visible);
        }

        [Test]
        public void TabTogglesSplitLayout()
        {
            AddAssert("split layout hidden initially", () => screen.SplitLayoutContainer.Alpha == 0);
            AddAssert("fullscreen layout visible initially", () => screen.FullscreenLayoutContainer.Alpha == 1);

            AddStep("press tab", () => InputManager.Key(Key.Tab));
            AddAssert("split layout shown", () => screen.SplitLayoutContainer.Alpha == 1);
            AddAssert("fullscreen layout hidden", () => screen.FullscreenLayoutContainer.Alpha == 0);
            AddAssert("config persisted the switch", () => config.Get<UiLayout>(JukeBoxSetting.UiLayout) == UiLayout.Split);

            AddStep("press tab again", () => InputManager.Key(Key.Tab));
            AddAssert("split layout hidden again", () => screen.SplitLayoutContainer.Alpha == 0);
            AddAssert("fullscreen layout visible again", () => screen.FullscreenLayoutContainer.Alpha == 1);
        }

        // The listing replaces the old always-docked Split search panel: in Split the left column
        // keeps only a compact search button that opens the (dismissable, visuals-covering)
        // listing, and Escape closes it again in both layouts.
        [Test]
        public void SplitLayoutCompactSearchOpensListingAndEscapeClosesIt()
        {
            AddStep("press tab (switch to split)", () => InputManager.Key(Key.Tab));
            AddUntilStep("split shown", () => screen.SplitLayoutContainer.Alpha == 1);
            AddAssert("listing starts hidden", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().State.Value == Visibility.Hidden);

            AddStep("click the compact search button",
                () => screen.ChildrenOfType<CompactSearchButton>().Single().TriggerClick());
            AddUntilStep("listing visible", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().State.Value == Visibility.Visible);

            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddUntilStep("listing hidden again", () => screen.ChildrenOfType<BeatmapListingOverlay>().Single().State.Value == Visibility.Hidden);
        }

        // Regression test for the queue drawer being unreachable in the fullscreen layout:
        // QueuePanel.ToggleVisibility previously had no caller at all outside the layout switch
        // (which only ever forces it open in Split via SetShown). Both new entry points — the
        // Ctrl+Q hotkey and the corner "queue" button — must actually reach it while fullscreen.
        [Test]
        public void QueueDrawerReachableViaHotkeyAndButtonInFullscreenLayout()
        {
            QueuePanel panel = null!;
            AddStep("grab queue panel", () => panel = screen.ChildrenOfType<QueuePanel>().Single());

            AddAssert("starts off-screen (hidden) in fullscreen layout", () => panel.X > 0);

            AddStep("press ctrl+q", () =>
            {
                InputManager.PressKey(Key.ControlLeft);
                InputManager.Key(Key.Q);
                InputManager.ReleaseKey(Key.ControlLeft);
            });
            AddUntilStep("panel slid into view", () => panel.X == 0);

            AddStep("press ctrl+q again", () =>
            {
                InputManager.PressKey(Key.ControlLeft);
                InputManager.Key(Key.Q);
                InputManager.ReleaseKey(Key.ControlLeft);
            });
            AddUntilStep("panel slid back out", () => panel.X > 0);

            AddStep("click the corner queue button",
                () => screen.ChildrenOfType<BasicButton>().Single(b => b.Text == "queue").TriggerClick());
            AddUntilStep("panel visible again", () => panel.X == 0);
        }

        // Regression test for stored-geometry bugs on layout round trips: applyLayout's Split
        // branch switches QueuePanel to fully-relative sizing (Width/Height 1 inside the left
        // panel's queue host); the fullscreen branch must restore the absolute drawer width and
        // Y-only relative axes explicitly, since switching RelativeSizeAxes back never resets the
        // stored Width/Height values.
        [Test]
        public void QueuePanelGeometryRestoredAfterSplitFullscreenRoundTrip()
        {
            QueuePanel panel = null!;
            AddStep("grab queue panel", () => panel = screen.ChildrenOfType<QueuePanel>().Single());

            AddAssert("starts at absolute drawer width in fullscreen layout", () => panel.Width == 320f);

            AddStep("switch to split layout", () => InputManager.Key(Key.Tab));
            AddUntilStep("split shown", () => screen.SplitLayoutContainer.Alpha == 1);
            AddAssert("split layout sets fully-relative geometry", () => panel.RelativeSizeAxes == Axes.Both && panel.Width == 1f && panel.Height == 1f);

            AddStep("switch back to fullscreen layout", () => InputManager.Key(Key.Tab));
            AddUntilStep("fullscreen shown", () => screen.FullscreenLayoutContainer.Alpha == 1);

            AddAssert("width restored to the absolute drawer width", () => panel.Width == 320f);
            AddAssert("height restored to full", () => panel.Height == 1f);
            AddAssert("relative axes restored to Y-only", () => panel.RelativeSizeAxes == Axes.Y);
        }

        // Regression coverage for the settings gear button: it must be reachable (and the overlay
        // it opens must actually be present) in both layouts, not just whichever one MainScreen
        // starts in.
        [Test]
        public void GearButtonTogglesSettingsOverlayInBothLayouts()
        {
            SettingsOverlay overlay = null!;
            AddStep("grab settings overlay", () => overlay = screen.ChildrenOfType<SettingsOverlay>().Single());

            AddAssert("starts hidden", () => overlay.State.Value == Visibility.Hidden);

            AddStep("click the corner gear button",
                () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Cog)).TriggerClick());
            AddAssert("overlay visible", () => overlay.State.Value == Visibility.Visible);

            AddStep("click the gear button again", () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Cog)).TriggerClick());
            AddAssert("overlay hidden again", () => overlay.State.Value == Visibility.Hidden);

            AddStep("switch to split layout", () => InputManager.Key(Key.Tab));
            AddUntilStep("split shown", () => screen.SplitLayoutContainer.Alpha == 1);

            AddStep("click the corner gear button in split layout",
                () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Cog)).TriggerClick());
            AddAssert("overlay visible in split layout too", () => overlay.State.Value == Visibility.Visible);
        }

        // Regression coverage for the map-ID button: it must be reachable (and the overlay it
        // opens must actually be present) in both layouts, same as the settings gear button.
        [Test]
        public void HashtagButtonTogglesMapIdOverlayInBothLayouts()
        {
            MapIdOverlay overlay = null!;
            AddStep("grab map-id overlay", () => overlay = screen.ChildrenOfType<MapIdOverlay>().Single());

            AddAssert("starts hidden", () => overlay.State.Value == Visibility.Hidden);

            AddStep("click the corner hashtag button",
                () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Hashtag)).TriggerClick());
            AddAssert("overlay visible", () => overlay.State.Value == Visibility.Visible);

            AddStep("click the hashtag button again", () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Hashtag)).TriggerClick());
            AddAssert("overlay hidden again", () => overlay.State.Value == Visibility.Hidden);

            AddStep("switch to split layout", () => InputManager.Key(Key.Tab));
            AddUntilStep("split shown", () => screen.SplitLayoutContainer.Alpha == 1);

            AddStep("click the corner hashtag button in split layout",
                () => screen.ChildrenOfType<IconButton>().Single(b => b.Icon.Equals(FontAwesome.Solid.Hashtag)).TriggerClick());
            AddAssert("overlay visible in split layout too", () => overlay.State.Value == Visibility.Visible);
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
