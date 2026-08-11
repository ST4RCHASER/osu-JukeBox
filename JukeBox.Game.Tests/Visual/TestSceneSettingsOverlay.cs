#nullable enable

using System.IO;
using JukeBox.Game.Configuration;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneSettingsOverlay : ManualInputManagerTestScene
    {
        private JukeBoxConfigManager config = null!;
        private SettingsOverlay overlay = null!;

        // CreateChildDependencies runs once for the whole scene — own isolated JukeBoxConfigManager
        // (TemporaryNativeStorage, same approach as TestSceneMainScreen) so tests never touch the
        // real user config file.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-settings-overlay-test", Path.GetRandomFileName())));

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            return deps;
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset ShowFps and create overlay", () =>
            {
                config.SetValue(JukeBoxSetting.ShowFps, false);
                Child = overlay = new SettingsOverlay();
            });
        }

        [Test]
        public void StartsHidden()
        {
            AddAssert("overlay starts hidden", () => overlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void ToggleVisibilityShowsAndEscapeHides()
        {
            AddStep("toggle visible", () => overlay.ToggleVisibility());
            AddAssert("overlay visible", () => overlay.State.Value == Visibility.Visible);

            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddAssert("overlay hidden again", () => overlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void CheckingShowFpsBoxUpdatesConfigBindable()
        {
            AddStep("show overlay", () => overlay.Show());
            AddAssert("config starts false", () => config.Get<bool>(JukeBoxSetting.ShowFps) == false);
            AddAssert("checkbox starts unchecked", () => overlay.ShowFpsCheckbox.Current.Value == false);

            AddStep("click the checkbox", () =>
            {
                InputManager.MoveMouseTo(overlay.ShowFpsCheckbox);
                InputManager.Click(MouseButton.Left);
            });

            AddAssert("checkbox now checked", () => overlay.ShowFpsCheckbox.Current.Value);
            AddAssert("config bindable flipped to true", () => config.Get<bool>(JukeBoxSetting.ShowFps));

            AddStep("click the checkbox again", () => InputManager.Click(MouseButton.Left));
            AddAssert("config bindable flipped back to false", () => config.Get<bool>(JukeBoxSetting.ShowFps) == false);
        }

        [Test]
        public void ConfigStartingTrueChecksTheBoxOnCreation()
        {
            AddStep("set ShowFps true then recreate overlay", () =>
            {
                config.SetValue(JukeBoxSetting.ShowFps, true);
                Child = overlay = new SettingsOverlay();
            });

            AddAssert("checkbox starts checked", () => overlay.ShowFpsCheckbox.Current.Value);
        }
    }
}
