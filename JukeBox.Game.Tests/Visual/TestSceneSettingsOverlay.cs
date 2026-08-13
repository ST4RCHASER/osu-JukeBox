#nullable enable

using System.IO;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
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
            AddStep("reset ShowFps/PreferredMirror and create overlay", () =>
            {
                config.SetValue(JukeBoxSetting.ShowFps, false);
                config.SetValue(JukeBoxSetting.PreferredMirror, MirrorSource.Auto);
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

        [Test]
        public void ChangingMirrorDropdownUpdatesConfigValue()
        {
            AddStep("show overlay", () => overlay.Show());
            AddAssert("config starts Auto", () => config.Get<MirrorSource>(JukeBoxSetting.PreferredMirror) == MirrorSource.Auto);
            AddAssert("dropdown starts Auto", () => overlay.MirrorDropdown.Current.Value == MirrorSource.Auto);

            AddStep("select Catboy in dropdown", () => overlay.MirrorDropdown.Current.Value = MirrorSource.Catboy);

            AddAssert("config bindable updated to Catboy", () => config.Get<MirrorSource>(JukeBoxSetting.PreferredMirror) == MirrorSource.Catboy);
        }

        [Test]
        public void ConfigStartingWithNonAutoMirrorSelectsItOnCreation()
        {
            AddStep("set PreferredMirror to OsuDirect then recreate overlay", () =>
            {
                config.SetValue(JukeBoxSetting.PreferredMirror, MirrorSource.OsuDirect);
                Child = overlay = new SettingsOverlay();
            });

            AddAssert("dropdown starts OsuDirect", () => overlay.MirrorDropdown.Current.Value == MirrorSource.OsuDirect);
        }

        // Docked mode (the three-column layout's "Settings" tab body): permanently visible from
        // load, no scrim/floating card, and Escape is a no-op (there's nothing here to close — the
        // owning tab strip controls visibility via Alpha instead).
        [Test]
        public void DockedInstanceStartsVisibleAndEscapeDoesNotHideIt()
        {
            SettingsOverlay dockedOverlay = null!;
            AddStep("create docked overlay", () => Child = dockedOverlay = new SettingsOverlay(docked: true));

            AddAssert("starts visible", () => dockedOverlay.State.Value == Visibility.Visible);

            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddAssert("still visible", () => dockedOverlay.State.Value == Visibility.Visible);
        }

        // Docked content wires up to config exactly like the floating modal — same checkboxes,
        // same bindables, just different chrome around them.
        [Test]
        public void DockedInstanceChecksBoxUpdatesConfigBindable()
        {
            SettingsOverlay dockedOverlay = null!;
            AddStep("create docked overlay", () => Child = dockedOverlay = new SettingsOverlay(docked: true));
            AddAssert("config starts false", () => config.Get<bool>(JukeBoxSetting.ShowFps) == false);

            AddStep("click the checkbox", () =>
            {
                InputManager.MoveMouseTo(dockedOverlay.ShowFpsCheckbox);
                InputManager.Click(MouseButton.Left);
            });

            AddAssert("config bindable flipped to true", () => config.Get<bool>(JukeBoxSetting.ShowFps));
        }
    }
}
