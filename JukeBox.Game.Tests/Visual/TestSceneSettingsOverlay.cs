#nullable enable

using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    // JukeBoxManualInputTestScene (not plain ManualInputManagerTestScene): the runner is a real
    // JukeBoxGameBase, so the overlay's lazer-side sections (OsuConfigManager, the ruleset config
    // cache, skin selection) resolve and get exercised here rather than silently omitted.
    [TestFixture]
    public partial class TestSceneSettingsOverlay : JukeBoxManualInputTestScene
    {
        private JukeBoxConfigManager config = null!;
        private SettingsOverlay overlay = null!;

        [Resolved]
        private osu.Game.Rulesets.IRulesetConfigCache rulesetConfigs { get; set; } = null!;

        [Resolved]
        private osu.Framework.Configuration.FrameworkConfigManager frameworkConfig { get; set; } = null!;

        [Resolved]
        private GameHost host { get; set; } = null!;

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

            AddStep("scroll checkbox into view", () => overlay.ScrollControlIntoView(overlay.ShowFpsCheckbox));
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

            AddStep("scroll checkbox into view", () => dockedOverlay.ScrollControlIntoView(dockedOverlay.ShowFpsCheckbox));
            AddStep("click the checkbox", () =>
            {
                InputManager.MoveMouseTo(dockedOverlay.ShowFpsCheckbox);
                InputManager.Click(MouseButton.Left);
            });

            AddAssert("config bindable flipped to true", () => config.Get<bool>(JukeBoxSetting.ShowFps));
        }

        // The lazer dropdown's menu subtree (OsuDropdownMenu + its item drawables) only loads its
        // items when the menu opens — a DI gap there would crash the real app on first click while
        // every value-set test stays green. Open one for real.
        [Test]
        public void SkinDropdownMenuOpensOnClick()
        {
            AddStep("show overlay", () => overlay.Show());
            AddStep("scroll dropdown into view", () => overlay.ScrollControlIntoView(overlay.SkinDropdown));

            AddStep("click dropdown header", () =>
            {
                InputManager.MoveMouseTo(overlay.SkinDropdown.ChildrenOfType<osu.Game.Graphics.UserInterface.OsuDropdown<JukeBoxSkin>.OsuDropdownHeader>().First());
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("menu open with all skin entries", () => overlay.SkinDropdown
                .ChildrenOfType<osu.Framework.Graphics.UserInterface.Menu>().Any(m =>
                    m.State == osu.Framework.Graphics.UserInterface.MenuState.Open && m.Items.Count == 5));

            AddStep("close menu", () => InputManager.Key(Key.Escape));
        }

        [Test]
        public void SkinChoicePersistsToConfig()
        {
            AddStep("show overlay", () => overlay.Show());
            AddAssert("config starts Argon", () => config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Argon);

            AddStep("select Triangles", () => overlay.SkinDropdown.Current.Value = JukeBoxSkin.Triangles);
            AddAssert("config updated to Triangles", () => config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Triangles);

            AddStep("recreate overlay", () => Child = overlay = new SettingsOverlay());
            AddAssert("dropdown starts Triangles", () => overlay.SkinDropdown.Current.Value == JukeBoxSkin.Triangles);
        }

        [Test]
        public void ManiaScrollSpeedReachesRulesetConfig()
        {
            AddStep("show overlay", () => overlay.Show());

            // Ruleset bindings attach once the realm-backed config cache has loaded (scheduled
            // retry in the overlay) — bound is observable as the slider taking the config's range.
            AddUntilStep("mania slider bound", () => overlay.ManiaScrollSpeedSlider?.Current.Value >= 1);

            AddStep("set scroll speed 20", () => overlay.ManiaScrollSpeedSlider!.Current.Value = 20);
            AddAssert("ruleset config holds 20", () =>
                (rulesetConfigs.GetConfigFor(new osu.Game.Rulesets.Mania.ManiaRuleset()) as osu.Game.Rulesets.Mania.Configuration.ManiaRulesetConfigManager)!
                .Get<double>(osu.Game.Rulesets.Mania.Configuration.ManiaRulesetSetting.ScrollSpeed) == 20);

            AddStep("restore default 8", () => overlay.ManiaScrollSpeedSlider!.Current.Value = 8);
        }

        [Test]
        public void MasterSliderBindsFrameworkVolume()
        {
            AddStep("show overlay", () => overlay.Show());

            AddStep("set master 37%", () => overlay.MasterVolumeSlider.Current.Value = 0.37);
            AddAssert("framework VolumeUniversal follows", () =>
                System.Math.Abs(frameworkConfig.Get<double>(osu.Framework.Configuration.FrameworkSetting.VolumeUniversal) - 0.37) < 0.001);

            AddStep("restore master 100%", () => overlay.MasterVolumeSlider.Current.Value = 1);
        }

        [Test]
        public void AudioDeviceDropdownAlwaysOffersSystemDefault()
        {
            AddStep("show overlay", () => overlay.Show());

            // A headless host reports no devices; the "System default" (empty string) entry must
            // exist regardless, per AudioManager's documented convention.
            AddAssert("system default entry present", () => overlay.AudioDeviceDropdown.Items.Contains(string.Empty));
        }

        // This test project's GameHost (TestRunHeadlessGameHost, via JukeBoxManualInputTestScene)
        // overrides CreateWindow to return null (osu.Framework.Platform.HeadlessGameHost) — so
        // host.Window, and therefore the overlay's whole displayDropdown/onDisplaysChanged path,
        // never exists here. The display-picker "snap back" fix itself (SettingsOverlay's
        // DisplayListComparer, used to decide whether onDisplaysChanged should reassign
        // displayDropdown.Items) is covered without a window in DisplayListComparerTest instead.
        // What IS assertable here: the graphics section still builds cleanly with no window, and
        // the window-bound rows are correctly absent rather than null-reffing.
        [Test]
        public void GraphicsSectionOmitsWindowBoundRowsWithNoWindow()
        {
            AddStep("show overlay", () => overlay.Show());

            AddAssert("host has no window in this test environment", () => host.Window == null);
            AddAssert("display dropdown was not created", () => overlay.ChildrenOfType<SettingsOverlay.DisplaySettingsDropdown>().Any() == false);
        }
    }
}
