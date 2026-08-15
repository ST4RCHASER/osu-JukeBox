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
using osu.Framework.Graphics.Shapes;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Game.Overlays.Settings;
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
            AddStep("reset FpsDisplayMode/PreferredMirror and create overlay", () =>
            {
                config.SetValue(JukeBoxSetting.FpsDisplayMode, FpsDisplayMode.Off);
                config.SetValue(JukeBoxSetting.PreferredMirror, MirrorSource.Auto);
                config.SetValue(JukeBoxSetting.SearchApi, SearchApi.Mirror);
                config.SetValue(JukeBoxSetting.OsuClientId, string.Empty);
                config.SetValue(JukeBoxSetting.OsuClientSecret, string.Empty);
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
        public void ChangingFpsDisplayDropdownUpdatesConfigBindable()
        {
            AddStep("show overlay", () => overlay.Show());
            AddAssert("config starts Off", () => config.Get<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode) == FpsDisplayMode.Off);
            AddAssert("dropdown starts Off", () => overlay.FpsDisplayDropdown.Current.Value == FpsDisplayMode.Off);

            AddStep("select Compact in dropdown", () => overlay.FpsDisplayDropdown.Current.Value = FpsDisplayMode.Compact);
            AddAssert("config bindable flipped to Compact", () => config.Get<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode) == FpsDisplayMode.Compact);

            AddStep("select Details in dropdown", () => overlay.FpsDisplayDropdown.Current.Value = FpsDisplayMode.Details);
            AddAssert("config bindable flipped to Details", () => config.Get<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode) == FpsDisplayMode.Details);

            AddStep("select Graph in dropdown", () => overlay.FpsDisplayDropdown.Current.Value = FpsDisplayMode.Graph);
            AddAssert("config bindable flipped to Graph", () => config.Get<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode) == FpsDisplayMode.Graph);

            AddStep("select Off in dropdown", () => overlay.FpsDisplayDropdown.Current.Value = FpsDisplayMode.Off);
            AddAssert("config bindable flipped back to Off", () => config.Get<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode) == FpsDisplayMode.Off);
        }

        [Test]
        public void ConfigStartingWithNonOffFpsDisplaySelectsItOnCreation()
        {
            AddStep("set FpsDisplayMode Graph then recreate overlay", () =>
            {
                config.SetValue(JukeBoxSetting.FpsDisplayMode, FpsDisplayMode.Graph);
                Child = overlay = new SettingsOverlay();
            });

            AddAssert("dropdown starts Graph", () => overlay.FpsDisplayDropdown.Current.Value == FpsDisplayMode.Graph);
        }

        [Test]
        public void ChangingPlayfieldZoomSliderUpdatesConfigValueAndPersistsAcrossRecreation()
        {
            AddStep("show overlay", () => overlay.Show());
            AddAssert("config starts 100%", () => config.Get<double>(JukeBoxSetting.PlayfieldZoom) == 1.0);
            AddAssert("slider starts 100%", () => overlay.PlayfieldZoomSlider.Current.Value == 1.0);

            AddStep("set playfield zoom to 60%", () => overlay.PlayfieldZoomSlider.Current.Value = 0.6);
            AddAssert("config bindable updated to 60%", () => config.Get<double>(JukeBoxSetting.PlayfieldZoom) == 0.6);

            AddStep("recreate overlay", () => Child = overlay = new SettingsOverlay());
            AddAssert("slider starts at the persisted 60%", () => overlay.PlayfieldZoomSlider.Current.Value == 0.6);

            AddStep("restore default 100%", () => overlay.PlayfieldZoomSlider.Current.Value = 1.0);
        }

        // Regression coverage for the ChartZoom -> PlayfieldZoom rework's widened 1%-200% range
        // (was 50%-150%) — the slider must actually reach both new extremes, not just clamp back
        // to the old window.
        [Test]
        public void PlayfieldZoomSliderReachesWidenedRangeExtremes()
        {
            AddStep("show overlay", () => overlay.Show());

            AddStep("set playfield zoom to 1%", () => overlay.PlayfieldZoomSlider.Current.Value = 0.01);
            AddAssert("config bindable updated to 1%", () => config.Get<double>(JukeBoxSetting.PlayfieldZoom) == 0.01);

            AddStep("set playfield zoom to 200%", () => overlay.PlayfieldZoomSlider.Current.Value = 2.0);
            AddAssert("config bindable updated to 200%", () => config.Get<double>(JukeBoxSetting.PlayfieldZoom) == 2.0);

            AddStep("restore default 100%", () => overlay.PlayfieldZoomSlider.Current.Value = 1.0);
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

        // The "Search API" row and the credentials only the official backend needs. Mirror is the
        // default precisely so a fresh install needs no credentials at all, which is why the whole
        // credential block is absent rather than greyed until the user opts in.
        [Test]
        public void SearchApiDefaultsToMirrorAndHidesTheCredentials()
        {
            AddStep("show overlay", () => overlay.Show());

            AddAssert("config starts Mirror", () => config.Get<SearchApi>(JukeBoxSetting.SearchApi) == SearchApi.Mirror);
            AddAssert("dropdown starts Mirror", () => overlay.SearchApiDropdown.Current.Value == SearchApi.Mirror);
            AddAssert("credentials hidden", () => overlay.OfficialCredentials.Alpha == 0);
        }

        [Test]
        public void SelectingOfficialRevealsTheCredentialsAndPersistsThem()
        {
            AddStep("show overlay", () => overlay.Show());
            AddStep("select the official API", () => overlay.SearchApiDropdown.Current.Value = SearchApi.Official);

            AddAssert("config updated", () => config.Get<SearchApi>(JukeBoxSetting.SearchApi) == SearchApi.Official);
            AddAssert("credentials revealed", () => overlay.OfficialCredentials.Alpha == 1);

            AddStep("type credentials", () =>
            {
                overlay.ClientIdTextBox.Current.Value = "9999";
                overlay.ClientSecretTextBox.Current.Value = "hunter2";
            });

            AddAssert("client id persisted", () => config.Get<string>(JukeBoxSetting.OsuClientId) == "9999");
            AddAssert("client secret persisted", () => config.Get<string>(JukeBoxSetting.OsuClientSecret) == "hunter2");

            AddStep("back to the mirror", () => overlay.SearchApiDropdown.Current.Value = SearchApi.Mirror);
            AddAssert("credentials hidden again", () => overlay.OfficialCredentials.Alpha == 0);
            AddAssert("but not forgotten", () => config.Get<string>(JukeBoxSetting.OsuClientId) == "9999");
        }

        [Test]
        public void SecretIsMasked()
        {
            AddStep("show overlay", () => overlay.Show());
            AddStep("select the official API", () => overlay.SearchApiDropdown.Current.Value = SearchApi.Official);

            // A plain SettingsTextBox would render the secret in clear text on a panel the user may
            // well be screen-sharing.
            AddAssert("secret row uses a masked control", () => overlay.ClientSecretTextBox
                .ChildrenOfType<osu.Game.Graphics.UserInterface.OsuPasswordTextBox>().Any());
            AddAssert("id row does not", () => !overlay.ClientIdTextBox
                .ChildrenOfType<osu.Game.Graphics.UserInterface.OsuPasswordTextBox>().Any());
        }

        [Test]
        public void OauthLinkOpensTheAccountPage()
        {
            string? opened = null;

            AddStep("show overlay with a stubbed browser", () =>
            {
                overlay.OpenUrl = url => opened = url;
                overlay.Show();
            });
            AddStep("select the official API", () => overlay.SearchApiDropdown.Current.Value = SearchApi.Official);

            // Triggered through the button's own Action rather than a real click: the row sits at
            // the very bottom of a long scrolling panel, and driving the mouse there would be
            // testing the framework's scrolling rather than this wiring. That the row is REACHABLE
            // at all (present only under the official backend) is what the tests above cover.
            AddStep("press the OAuth button", () => overlay.OfficialCredentials.ChildrenOfType<SettingsButton>().Single().Action!.Invoke());

            AddAssert("account page opened", () => opened == SettingsOverlay.oauth_application_url);
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
        // same bindables, just different chrome around them. Drives a real mouse click (rather
        // than a value-set) so the checkbox's own click handling is exercised for real too.
        [Test]
        public void DockedInstanceChecksHardwareAccelerationBoxUpdatesConfigBindable()
        {
            SettingsOverlay dockedOverlay = null!;
            AddStep("reset HardwareVideoDecoder and create docked overlay", () =>
            {
                frameworkConfig.SetValue(osu.Framework.Configuration.FrameworkSetting.HardwareVideoDecoder, osu.Framework.Graphics.Video.HardwareVideoDecoder.None);
                Child = dockedOverlay = new SettingsOverlay(docked: true);
            });
            AddAssert("config starts None", () => frameworkConfig.Get<osu.Framework.Graphics.Video.HardwareVideoDecoder>(osu.Framework.Configuration.FrameworkSetting.HardwareVideoDecoder) == osu.Framework.Graphics.Video.HardwareVideoDecoder.None);
            AddAssert("checkbox starts unchecked", () => dockedOverlay.HardwareAccelerationCheckbox.Current.Value == false);

            AddStep("scroll checkbox into view", () => dockedOverlay.ScrollControlIntoView(dockedOverlay.HardwareAccelerationCheckbox));
            AddStep("click the checkbox", () =>
            {
                InputManager.MoveMouseTo(dockedOverlay.HardwareAccelerationCheckbox);
                InputManager.Click(MouseButton.Left);
            });

            AddAssert("checkbox now checked", () => dockedOverlay.HardwareAccelerationCheckbox.Current.Value);
            AddAssert("config bindable flipped to Any", () => frameworkConfig.Get<osu.Framework.Graphics.Video.HardwareVideoDecoder>(osu.Framework.Configuration.FrameworkSetting.HardwareVideoDecoder) == osu.Framework.Graphics.Video.HardwareVideoDecoder.Any);

            AddStep("click the checkbox again", () => InputManager.Click(MouseButton.Left));
            AddAssert("config bindable flipped back to None", () => frameworkConfig.Get<osu.Framework.Graphics.Video.HardwareVideoDecoder>(osu.Framework.Configuration.FrameworkSetting.HardwareVideoDecoder) == osu.Framework.Graphics.Video.HardwareVideoDecoder.None);
        }

        [Test]
        public void DockedInstanceChangingFpsDisplayUpdatesConfigBindable()
        {
            SettingsOverlay dockedOverlay = null!;
            AddStep("create docked overlay", () => Child = dockedOverlay = new SettingsOverlay(docked: true));
            AddAssert("config starts Off", () => config.Get<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode) == FpsDisplayMode.Off);

            AddStep("select Details in dropdown", () => dockedOverlay.FpsDisplayDropdown.Current.Value = FpsDisplayMode.Details);
            AddAssert("config bindable flipped to Details", () => config.Get<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode) == FpsDisplayMode.Details);
        }

        // Hardware acceleration is now a checkbox (checked = Any, unchecked = None) rather than a
        // dropdown over the full HardwareVideoDecoder flags enum — a specific external value
        // (e.g. just NVDEC, as a platform-detected default might set) must still read as checked.
        [Test]
        public void SpecificExternalDecoderValueReadsAsChecked()
        {
            AddStep("set HardwareVideoDecoder to NVDEC then recreate overlay", () =>
            {
                frameworkConfig.SetValue(osu.Framework.Configuration.FrameworkSetting.HardwareVideoDecoder, osu.Framework.Graphics.Video.HardwareVideoDecoder.NVDEC);
                Child = overlay = new SettingsOverlay();
            });

            AddAssert("checkbox reads checked", () => overlay.HardwareAccelerationCheckbox.Current.Value);

            AddStep("restore None", () => frameworkConfig.SetValue(osu.Framework.Configuration.FrameworkSetting.HardwareVideoDecoder, osu.Framework.Graphics.Video.HardwareVideoDecoder.None));
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

            // Counted off the enum rather than hardcoded, so adding a skin (e.g. Custom) doesn't
            // silently turn this into a timeout with no hint of why.
            int skinCount = System.Enum.GetValues<JukeBoxSkin>().Length;

            AddUntilStep("menu open with all skin entries", () => overlay.SkinDropdown
                .ChildrenOfType<osu.Framework.Graphics.UserInterface.Menu>().Any(m =>
                    m.State == osu.Framework.Graphics.UserInterface.MenuState.Open && m.Items.Count == skinCount));

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

        // "Play on main window too" is a dependent row: enabled exactly while "Detach player
        // window" is on, greyed (Current.Disabled — lazer's dependent-setting pattern) while off,
        // with its remembered value untouched by the disable.
        [Test]
        public void PlayOnMainRowFollowsDetachRowEnabledState()
        {
            AddStep("show overlay", () => overlay.Show());

            AddAssert("row disabled while detach off", () => overlay.PlayOnMainCheckbox.Current.Disabled);

            AddStep("turn detach on", () => config.SetValue(JukeBoxSetting.DetachPlayer, true));
            AddAssert("row enabled", () => !overlay.PlayOnMainCheckbox.Current.Disabled);

            AddStep("check play-on-main", () => overlay.PlayOnMainCheckbox.Current.Value = true);
            AddAssert("config updated", () => config.Get<bool>(JukeBoxSetting.DetachPlayOnMain));

            AddStep("turn detach off", () => config.SetValue(JukeBoxSetting.DetachPlayer, false));
            AddAssert("row disabled again", () => overlay.PlayOnMainCheckbox.Current.Disabled);
            AddAssert("remembered value intact", () => config.Get<bool>(JukeBoxSetting.DetachPlayOnMain));

            AddStep("restore defaults", () =>
            {
                config.SetValue(JukeBoxSetting.DetachPlayer, false);
                config.SetValue(JukeBoxSetting.DetachPlayOnMain, false);
            });
        }

        // The docked presentation is inline tab-body content: the right column has already painted
        // an opaque surface behind the whole tab, so painting a second ground here stacked a card on
        // that card and turned lazer's section separators into the edges of per-section sub-cards.
        // Structural form of "one surface layer behind a section header": walk from each section up
        // to the overlay and count the full-size opaque boxes drawn behind it on the way.
        [Test]
        public void DockedSectionsSitOnASingleSurface()
        {
            SettingsOverlay docked = null!;

            AddStep("create docked overlay", () => Child = docked = new SettingsOverlay(docked: true));
            AddUntilStep("sections built", () => docked.ChildrenOfType<SettingsSection>().Any());

            AddAssert("docked paints no ground of its own", () => docked.CardBackground == null);
            AddAssert("no surface stacked behind any section",
                () => docked.ChildrenOfType<SettingsSection>().All(s => surfacesBehind(s, docked) == 0));

            // The floating modal IS a card in its own right, over a scrim — it keeps lazer's ground,
            // so this assert is what stops the fix above from being "delete all backgrounds".
            AddAssert("the floating card still has one", () => overlay.CardBackground != null);
        }

        /// <summary>
        /// How many full-size opaque <see cref="Box"/>es are painted between <paramref name="child"/>
        /// and <paramref name="root"/> — i.e. surfaces the child is drawn on top of. Only siblings
        /// EARLIER in each ancestor's child list count: later ones are drawn over the child, not
        /// behind it.
        /// </summary>
        private static int surfacesBehind(Drawable child, Drawable root)
        {
            int count = 0;

            for (var current = child; current != null && current != root; current = current.Parent)
            {
                if (current.Parent is not Container<Drawable> parent)
                    continue;

                foreach (var sibling in parent)
                {
                    if (sibling == current)
                        break;

                    if (sibling is Box box && box.RelativeSizeAxes == Axes.Both && box.Alpha > 0)
                        count++;
                }
            }

            return count;
        }
    }
}
