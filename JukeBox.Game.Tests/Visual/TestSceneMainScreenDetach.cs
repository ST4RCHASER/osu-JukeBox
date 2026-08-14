#nullable enable

using System.IO;
using JukeBox.Game.Configuration;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// MainScreen's player-box presentation as a function of the (DetachPlayer, DetachPlayOnMain)
    /// pair: the "playing in detached window" placeholder replaces the scene ONLY when the player
    /// is detached without play-on-main; every other combination shows the scene. Driven on
    /// MainScreen directly (not through JukeBoxGame) — under a test host DetachedViewerManager
    /// refuses to spawn a process and immediately bounces DetachPlayer back off, which would
    /// fight the matrix.
    /// </summary>
    [TestFixture]
    public partial class TestSceneMainScreenDetach : JukeBoxTestScene
    {
        private JukeBoxConfigManager config = null!;
        private MainScreen screen = null!;

        // Isolated ini-in-temp-storage config, same as TestSceneSettingsOverlay: these tests
        // flip settings freely and must never touch the shared test-browser config.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-main-detach-test", Path.GetRandomFileName())));

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            return deps;
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create main screen", () =>
            {
                config.SetValue(JukeBoxSetting.DetachPlayer, false);
                config.SetValue(JukeBoxSetting.DetachPlayOnMain, false);

                var stack = new ScreenStack { RelativeSizeAxes = Axes.Both };
                Child = stack;
                stack.Push(screen = new MainScreen());
            });

            AddUntilStep("screen loaded", () => screen.IsLoaded);
        }

        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        public void SceneShownExactlyUnlessDetachedWithoutPlayOnMain(bool detach, bool playOnMain, bool sceneShown)
        {
            AddStep($"detach={detach} playOnMain={playOnMain}", () =>
            {
                config.SetValue(JukeBoxSetting.DetachPlayer, detach);
                config.SetValue(JukeBoxSetting.DetachPlayOnMain, playOnMain);
            });

            AddAssert($"scene alpha {(sceneShown ? 1 : 0)}", () => screen.SceneAlpha == (sceneShown ? 1 : 0));
            AddAssert($"placeholder alpha {(sceneShown ? 0 : 1)}", () => screen.PlaceholderAlpha == (sceneShown ? 0 : 1));
        }

        // Flipping play-on-main while already detached must swap live in both directions —
        // that's the exact toggle a user reaches for with the second window already up.
        [Test]
        public void TogglingPlayOnMainWhileDetachedSwapsLive()
        {
            AddStep("detach", () => config.SetValue(JukeBoxSetting.DetachPlayer, true));
            AddAssert("placeholder shown", () => screen.PlaceholderAlpha == 1 && screen.SceneAlpha == 0);

            AddStep("enable play-on-main", () => config.SetValue(JukeBoxSetting.DetachPlayOnMain, true));
            AddAssert("scene back", () => screen.SceneAlpha == 1 && screen.PlaceholderAlpha == 0);

            AddStep("disable play-on-main", () => config.SetValue(JukeBoxSetting.DetachPlayOnMain, false));
            AddAssert("placeholder again", () => screen.PlaceholderAlpha == 1 && screen.SceneAlpha == 0);
        }
    }
}
