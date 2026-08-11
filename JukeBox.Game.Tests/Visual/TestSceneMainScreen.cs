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
        public void TypingOpensSearchOverlayWithFullscreenLayout()
        {
            AddAssert("search overlay starts hidden", () => screen.ChildrenOfType<SearchOverlay>().Single().State.Value == Visibility.Hidden);

            AddStep("press 'a'", () => InputManager.Key(Key.A));
            AddUntilStep("search overlay visible", () => screen.ChildrenOfType<SearchOverlay>().Single().State.Value == Visibility.Visible);
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

        // Regression test for the docked-search-panel-vanishing bug: Split's left column embeds
        // the same SearchOverlay used as a fullscreen modal in the other layout, and that
        // overlay's own pick()/Escape both Hide() unconditionally unless told it's docked. This
        // drives Escape (not a real pick) deliberately: picking here would go through the real
        // search.SetPicked -> jukebox.EnqueueAndMaybePlayAsync -> cache.GetAsync ->
        // mirror.DownloadAsync wiring, and StubMirror's DownloadAsync always throws — Jukebox's
        // advance loop would then retry forever against the same still-searchable stub set with
        // no yield point, hanging the test. Escape exercises the same Docked gate without that risk.
        [Test]
        public void SplitLayoutSearchStaysVisibleAfterEscape()
        {
            AddStep("press tab (switch to split)", () => InputManager.Key(Key.Tab));
            AddUntilStep("split shown", () => screen.SplitLayoutContainer.Alpha == 1);
            AddUntilStep("search overlay visible in split layout", () => screen.ChildrenOfType<SearchOverlay>().Single().State.Value == Visibility.Visible);

            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddAssert("search overlay still visible (docked)", () => screen.ChildrenOfType<SearchOverlay>().Single().State.Value == Visibility.Visible);
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

        // Regression test for the Height bug: applyLayout's Split branch sets QueuePanel.Height to
        // a 0.3f *relative* fraction; its fullscreen branch restored RelativeSizeAxes but, before
        // the fix, never reset that stored Height back to full — leaving the drawer stuck at 30%
        // height after any Split -> Fullscreen round trip.
        [Test]
        public void QueuePanelGeometryRestoredAfterSplitFullscreenRoundTrip()
        {
            QueuePanel panel = null!;
            AddStep("grab queue panel", () => panel = screen.ChildrenOfType<QueuePanel>().Single());

            AddAssert("starts full height in fullscreen layout", () => panel.Height == 1f);

            AddStep("switch to split layout", () => InputManager.Key(Key.Tab));
            AddUntilStep("split shown", () => screen.SplitLayoutContainer.Alpha == 1);
            AddAssert("split layout sets 30% relative height", () => panel.Height == 0.3f);

            AddStep("switch back to fullscreen layout", () => InputManager.Key(Key.Tab));
            AddUntilStep("fullscreen shown", () => screen.FullscreenLayoutContainer.Alpha == 1);

            AddAssert("height restored to full", () => panel.Height == 1f);
            AddAssert("relative axes restored to Y-only", () => panel.RelativeSizeAxes == Axes.Y);
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
