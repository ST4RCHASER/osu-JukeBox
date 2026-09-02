#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Online;
using osu.Game.Overlays.BeatmapListing;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Game.Overlays.Dialog;
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
        private StoryboardLayerVisibility storyboardLayers = null!;


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

            // The storyboard-layer service belongs to the ISOLATED config above, not to the
            // runner's: the panel's layer rows go through it, and a service still bound to the real
            // game's config would be reading and writing a different file than these tests assert on.
            storyboardLayers = new StoryboardLayerVisibility();

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            deps.Cache(storyboardLayers);
            return deps;
        }

        /// <summary>The overlay gets its own host container: the tests below swap overlays by
        /// assigning a Child, and doing that on the scene itself would take the service below it
        /// along too (and dispose it). Constructed with the field so a step can fill it in before
        /// the scene itself has finished loading.</summary>
        private readonly Container overlayHost = new Container { RelativeSizeAxes = Axes.Both };

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Add(storyboardLayers);
            Add(overlayHost);
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

                // Reset the skin choice too: the library tests below select imported skins, and a
                // leftover Skin=Custom would otherwise be the starting state of whatever runs next.
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
                config.SetValue(JukeBoxSetting.CustomSkinPath, string.Empty);

                overlayHost.Child = overlay = new SettingsOverlay();
            });
        }

        [Test]
        public void StartsHidden()
        {
            AddAssert("overlay starts hidden", () => overlay.State.Value == Visibility.Hidden);
        }

        #region Radio section

        [Test]
        public void RadioTogglesRoundTripThroughConfig()
        {
            AddStep("show overlay", () => overlay.Show());

            // Both start on: each describes behaviour the app already had before it was a setting.
            AddAssert("empty-queue radio starts on", () => overlay.RadioOnEmptyQueueCheckbox.Current.Value);
            AddAssert("on-start starts on", () => overlay.RadioOnStartCheckbox.Current.Value);

            AddStep("switch the empty-queue radio off", () => overlay.RadioOnEmptyQueueCheckbox.Current.Value = false);
            AddAssert("config followed", () => !config.Get<bool>(JukeBoxSetting.RadioOnEmptyQueue));

            AddStep("switch on-start off", () => overlay.RadioOnStartCheckbox.Current.Value = false);
            AddAssert("config followed", () => !config.Get<bool>(JukeBoxSetting.RadioOnStart));
        }

        [Test]
        public void RadioFiltersRoundTripThroughConfig()
        {
            AddStep("show overlay", () => overlay.Show());

            AddStep("set a mania, loved, 4-6 star, featured-artists station", () =>
            {
                overlay.RadioModeDropdown.Current.Value = RadioRuleset.Mania;
                overlay.RadioCategoryDropdown.Current.Value = SearchCategory.Loved;
                overlay.RadioGenreDropdown.Current.Value = SearchGenre.Anime;
                overlay.RadioLanguageDropdown.Current.Value = SearchLanguage.Japanese;
                overlay.RadioHasStoryboardCheckbox.Current.Value = true;
                overlay.RadioFeaturedArtistsCheckbox.Current.Value = true;
                overlay.RadioMinStarsSlider.Current.Value = 4;
                overlay.RadioMaxStarsSlider.Current.Value = 6;
            });

            AddAssert("every dimension reached config", () =>
                config.Get<RadioRuleset>(JukeBoxSetting.RadioMode) == RadioRuleset.Mania
                && config.Get<SearchCategory>(JukeBoxSetting.RadioCategory) == SearchCategory.Loved
                && config.Get<SearchGenre>(JukeBoxSetting.RadioGenre) == SearchGenre.Anime
                && config.Get<SearchLanguage>(JukeBoxSetting.RadioLanguage) == SearchLanguage.Japanese
                && config.Get<bool>(JukeBoxSetting.RadioHasStoryboard)
                && config.Get<bool>(JukeBoxSetting.RadioFeaturedArtists)
                && config.Get<double>(JukeBoxSetting.RadioMinStars) == 4
                && config.Get<double>(JukeBoxSetting.RadioMaxStars) == 6);
        }

        /// <summary>
        /// The Categories dropdown drops the two entries that need a signed-in account, exactly as
        /// the listing's own Categories row does — offering "Favourites" to a station that can
        /// never match one would be a filter guaranteed to return nothing.
        /// </summary>
        [Test]
        public void TheRadioCategoryDropdownOmitsTheAuthOnlyEntries()
        {
            AddStep("show overlay", () => overlay.Show());

            AddAssert("no Favourites or Mine", () =>
                !overlay.RadioCategoryDropdown.Items.Contains(SearchCategory.Favourites)
                && !overlay.RadioCategoryDropdown.Items.Contains(SearchCategory.Mine));

            AddAssert("but the rest are there", () =>
                overlay.RadioCategoryDropdown.Items.Contains(SearchCategory.Any)
                && overlay.RadioCategoryDropdown.Items.Contains(SearchCategory.Ranked)
                && overlay.RadioCategoryDropdown.Items.Contains(SearchCategory.Loved));
        }

        /// <summary>
        /// The radio's rows follow the SAME per-backend capability signal the listing's filter block
        /// does. Offering the radio a filter the backend will ignore is worse than doing so in the
        /// listing: the listing at least shows the broader results it got back, while the radio
        /// silently picks one of them and plays it.
        /// </summary>
        [Test]
        public void RadioFilterRowsFollowWhatTheBackendCanExpress()
        {
            BeatmapSearchEngine engine = null!;

            AddStep("rebuild the panel over a search engine", () =>
            {
                // Never added to the tree: only its AvailableFilters bindable is wanted here, and
                // loading the engine would drag in a mirror this scene doesn't cache.
                engine = new BeatmapSearchEngine();
                Child = overlay = new SettingsOverlay(searchEngine: engine);
            });

            AddStep("show overlay", () => overlay.Show());

            AddStep("official backend: everything is expressible", () => engine.AvailableFilters.Value = SearchFilters.All);
            AddAssert("every row shows", () =>
                overlay.RadioModeDropdown.Alpha == 1 && overlay.RadioCategoryDropdown.Alpha == 1
                && overlay.RadioGenreDropdown.Alpha == 1 && overlay.RadioLanguageDropdown.Alpha == 1
                && overlay.RadioHasVideoCheckbox.Alpha == 1 && overlay.RadioFeaturedArtistsCheckbox.Alpha == 1
                && overlay.RadioMinStarsSlider.Alpha == 1);
            AddAssert("and the empty-block hint is away", () => overlay.RadioNoFiltersHint.Alpha == 0);

            AddStep("drop to a mirror's capability", () => engine.AvailableFilters.Value = SearchFilters.AllMirror);
            AddAssert("genre, language and featured artists are gone", () =>
                overlay.RadioGenreDropdown.Alpha == 0 && overlay.RadioLanguageDropdown.Alpha == 0
                && overlay.RadioFeaturedArtistsCheckbox.Alpha == 0);
            AddAssert("the rows a mirror CAN serve stayed", () =>
                overlay.RadioModeDropdown.Alpha == 1 && overlay.RadioCategoryDropdown.Alpha == 1
                && overlay.RadioHasVideoCheckbox.Alpha == 1 && overlay.RadioMinStarsSlider.Alpha == 1);

            // A hidden row is not merely invisible — it must take no space, or the section keeps a
            // gap where the control was.
            AddAssert("hidden rows take no space", () => !overlay.RadioGenreDropdown.IsPresent);

            AddStep("down to a keyword-only source", () => engine.AvailableFilters.Value = SearchFilters.Keyword);
            AddAssert("no filter row is left", () =>
                overlay.RadioModeDropdown.Alpha == 0 && overlay.RadioCategoryDropdown.Alpha == 0
                && overlay.RadioMinStarsSlider.Alpha == 0);
            AddAssert("so the section says why rather than looking empty", () => overlay.RadioNoFiltersHint.Alpha == 1);

            // The two behaviour toggles are properties of THIS app, not of any backend — they must
            // never disappear with the filters.
            AddAssert("the behaviour toggles are untouched throughout", () =>
                overlay.RadioOnEmptyQueueCheckbox.Alpha == 1 && overlay.RadioOnStartCheckbox.Alpha == 1);
        }

        #endregion

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
                overlayHost.Child = overlay = new SettingsOverlay();
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

            AddStep("recreate overlay", () => overlayHost.Child = overlay = new SettingsOverlay());
            AddAssert("slider starts at the persisted 60%", () => overlay.PlayfieldZoomSlider.Current.Value == 0.6);

            AddStep("restore default 100%", () => overlay.PlayfieldZoomSlider.Current.Value = 1.0);
        }

        /// <summary>
        /// The two mask releases sit beside the zoom they interact with, are off out of the box
        /// (the boxed player every previous version had), and each carries its own setting — the
        /// whole point being that a user can release the storyboard without releasing the chart.
        /// </summary>
        [Test]
        public void MaskReleaseCheckboxesAreIndependentAndPersistAcrossRecreation()
        {
            AddStep("show overlay", () => overlay.Show());

            AddAssert("both start off", () => !overlay.RemoveChartMaskCheckbox.Current.Value
                                              && !overlay.RemoveStoryboardMaskCheckbox.Current.Value);
            AddAssert("and so does the config", () => !config.Get<bool>(JukeBoxSetting.RemoveChartMask)
                                                      && !config.Get<bool>(JukeBoxSetting.RemoveStoryboardMask));

            AddStep("tick the storyboard one only", () => overlay.RemoveStoryboardMaskCheckbox.Current.Value = true);
            AddAssert("only its setting moved", () => config.Get<bool>(JukeBoxSetting.RemoveStoryboardMask)
                                                      && !config.Get<bool>(JukeBoxSetting.RemoveChartMask));

            AddStep("recreate overlay", () => overlayHost.Child = overlay = new SettingsOverlay());
            AddAssert("the choice came back", () => overlay.RemoveStoryboardMaskCheckbox.Current.Value
                                                    && !overlay.RemoveChartMaskCheckbox.Current.Value);

            AddStep("tick the chart one too", () => overlay.RemoveChartMaskCheckbox.Current.Value = true);
            AddAssert("both settings on", () => config.Get<bool>(JukeBoxSetting.RemoveChartMask)
                                                && config.Get<bool>(JukeBoxSetting.RemoveStoryboardMask));

            AddStep("restore both", () =>
            {
                overlay.RemoveChartMaskCheckbox.Current.Value = false;
                overlay.RemoveStoryboardMaskCheckbox.Current.Value = false;
            });
        }

        /// <summary>
        /// "Storyboard / video" became two rows, each carrying only its own setting. A single
        /// combined key is exactly what the user asked to be rid of, so what matters here is that
        /// moving one leaves the other alone.
        /// </summary>
        [Test]
        public void StoryboardAndVideoRowsCarryTheirOwnSettings()
        {
            AddStep("show overlay", () => overlay.Show());

            AddAssert("both start on", () => overlay.ShowStoryboardCheckbox.Current.Value
                                             && overlay.ShowVideoCheckbox.Current.Value);

            AddStep("video off", () => overlay.ShowVideoCheckbox.Current.Value = false);
            AddAssert("only the video setting moved", () => !config.Get<bool>(JukeBoxSetting.ShowVideo)
                                                            && config.Get<bool>(JukeBoxSetting.ShowStoryboard));

            AddStep("storyboard off too", () => overlay.ShowStoryboardCheckbox.Current.Value = false);
            AddAssert("both off now", () => !config.Get<bool>(JukeBoxSetting.ShowVideo)
                                            && !config.Get<bool>(JukeBoxSetting.ShowStoryboard));

            AddStep("recreate overlay", () => overlayHost.Child = overlay = new SettingsOverlay());
            AddAssert("both choices came back", () => !overlay.ShowStoryboardCheckbox.Current.Value
                                                      && !overlay.ShowVideoCheckbox.Current.Value);

            AddStep("restore", () =>
            {
                overlay.ShowStoryboardCheckbox.Current.Value = true;
                overlay.ShowVideoCheckbox.Current.Value = true;
            });
        }

        /// <summary>The per-layer block is a dependent row of the storyboard toggle: with the
        /// storyboard off there is nothing for a layer choice to act on.</summary>
        [Test]
        public void StoryboardLayerRowsFollowTheMasterToggleBothWays()
        {
            AddStep("show overlay, storyboard on", () =>
            {
                overlay.Show();
                overlay.ShowStoryboardCheckbox.Current.Value = true;
            });

            AddUntilStep("layer rows are live", () => !overlay.StoryboardLayersInert);

            AddStep("storyboard off", () => overlay.ShowStoryboardCheckbox.Current.Value = false);
            AddUntilStep("layer rows go inert", () => overlay.StoryboardLayersInert);

            AddStep("storyboard on again", () => overlay.ShowStoryboardCheckbox.Current.Value = true);
            AddUntilStep("and live again", () => !overlay.StoryboardLayersInert);
        }

        [Test]
        public void StoryboardLayerRowsRoundTripTheHiddenList()
        {
            AddStep("show overlay, storyboard on", () =>
            {
                overlay.Show();
                overlay.ShowStoryboardCheckbox.Current.Value = true;
                config.SetValue(JukeBoxSetting.HiddenStoryboardLayers, string.Empty);
            });

            AddStep("untick Overlay", () => overlay.LayerCheckbox(StoryboardLayerKind.Overlay).Current.Value = false);
            AddUntilStep("persisted as hidden", () => config.Get<string>(JukeBoxSetting.HiddenStoryboardLayers) == "Overlay");

            AddStep("write a different list into config", () => config.SetValue(JukeBoxSetting.HiddenStoryboardLayers, "Background"));
            AddUntilStep("the rows followed", () => !overlay.LayerCheckbox(StoryboardLayerKind.Background).Current.Value
                                                    && overlay.LayerCheckbox(StoryboardLayerKind.Overlay).Current.Value);

            AddStep("restore", () => config.SetValue(JukeBoxSetting.HiddenStoryboardLayers, string.Empty));
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
                overlayHost.Child = overlay = new SettingsOverlay();
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

        // User request: "add version at bottom of settings too". It has to be the LAST thing in the
        // body (below every section), readable once scrolled there, and it must not read as a
        // setting row — hence a bare centred sprite rather than a SettingsItem.
        [Test]
        public void TheVersionSitsAtTheVeryBottomOfSettings()
        {
            SettingsOverlay dockedOverlay = null!;
            AddStep("create docked overlay", () => overlayHost.Child = dockedOverlay = new SettingsOverlay(docked: true));

            AddAssert("it shows the build's own version", () => dockedOverlay.VersionText == AppVersion.DisplayString);
            AddAssert("which is not empty", () => dockedOverlay.VersionText.Length > 1);

            // Below everything: no settings control sits lower than it.
            AddUntilStep("nothing in the body sits below it", () =>
            {
                float versionTop = dockedOverlay.VersionDrawable.ScreenSpaceDrawQuad.AABBFloat.Top;

                return dockedOverlay.ChildrenOfType<osu.Game.Overlays.Settings.SettingsItem<bool>>()
                                    .All(item => item.ScreenSpaceDrawQuad.AABBFloat.Top < versionTop);
            });

            // ...and it is reachable rather than clipped off the end of the scroll.
            AddStep("scroll the version into view", () => dockedOverlay.ScrollControlIntoView(dockedOverlay.VersionDrawable));
            AddUntilStep("it is fully inside the panel", () =>
            {
                var panel = dockedOverlay.ScreenSpaceDrawQuad.AABBFloat;
                var version = dockedOverlay.VersionDrawable.ScreenSpaceDrawQuad.AABBFloat;

                return version.Top >= panel.Top - 0.5f && version.Bottom <= panel.Bottom + 0.5f
                       && dockedOverlay.VersionDrawable.DrawWidth > 0;
            });
        }

        // Docked mode (the three-column layout's "Settings" tab body): permanently visible from
        // load, no scrim/floating card, and Escape is a no-op (there's nothing here to close — the
        // owning tab strip controls visibility via Alpha instead).
        [Test]
        public void DockedInstanceStartsVisibleAndEscapeDoesNotHideIt()
        {
            SettingsOverlay dockedOverlay = null!;
            AddStep("create docked overlay", () => overlayHost.Child = dockedOverlay = new SettingsOverlay(docked: true));

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
                overlayHost.Child = dockedOverlay = new SettingsOverlay(docked: true);
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
            AddStep("create docked overlay", () => overlayHost.Child = dockedOverlay = new SettingsOverlay(docked: true));
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
                overlayHost.Child = overlay = new SettingsOverlay();
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
                InputManager.MoveMouseTo(overlay.SkinDropdown.ChildrenOfType<osu.Game.Graphics.UserInterface.OsuDropdown<SkinChoice>.OsuDropdownHeader>().First());
                InputManager.Click(MouseButton.Left);
            });

            // Five bundled rows (every enum member except Custom, which is no longer a row of its
            // own now that each import is listed individually), plus one row per installed skin.
            // Counted rather than hardcoded so this cannot silently become a timeout, and counted
            // when the assert RUNS rather than when the steps are built, so it reflects the
            // library as it actually is by then.
            AddUntilStep("menu open with all skin entries", () => overlay.SkinDropdown
                .ChildrenOfType<osu.Framework.Graphics.UserInterface.Menu>().Any(m =>
                    m.State == osu.Framework.Graphics.UserInterface.MenuState.Open
                    && m.Items.Count == System.Enum.GetValues<JukeBoxSkin>().Length - 1 + skinLibrary.Skins.Count));

            AddStep("close menu", () => InputManager.Key(Key.Escape));
        }

        /// <summary>
        /// The user's request, in short: imported skins are listed the way osu! lists them — by the
        /// name each one declares for itself, one row each — rather than as a single generic
        /// "Custom (imported)" slot.
        /// </summary>
        [Test]
        public void ImportedSkinsAreListedByTheirRealNames()
        {
            AddStep("install two skins", () =>
            {
                installSkin("first-archive", "Rafis");
                installSkin("second-archive", "Aristia");
                skinLibrary.Refresh();
            });

            AddStep("show overlay", () => overlay.Show());

            AddAssert("the bundled rows still come first, in their own order", () => menuItems()
                .TakeWhile(i => !i.Value.IsImported)
                .Select(i => i.Value.Builtin)
                .SequenceEqual(new[] { JukeBoxSkin.Argon, JukeBoxSkin.ArgonPro, JukeBoxSkin.Triangles, JukeBoxSkin.Classic, JukeBoxSkin.Random }));

            AddAssert("both are listed, after the bundled rows, alphabetically", () =>
                importedLabels().SequenceEqual(new[] { "Aristia", "Rafis" }));

            AddAssert("and no generic Custom row is offered", () => !overlay.SkinDropdown.Items
                .Any(i => !i.IsImported && i.Builtin == JukeBoxSkin.Custom));
        }

        [Test]
        public void SkinsSharingANameStayTellableApartAndSeparatelySelectable()
        {
            AddStep("install two skins both called Aristia", () =>
            {
                installSkin("aristia-2016", "Aristia");
                installSkin("aristia-2019", "Aristia");
                skinLibrary.Refresh();
            });

            AddStep("show overlay", () => overlay.Show());

            AddAssert("the second is suffixed", () => importedLabels().SequenceEqual(new[] { "Aristia", "Aristia (2)" }));

            AddStep("pick the second one", () => overlay.SkinDropdown.Current.Value = SkinChoice.Imported("aristia-2019"));
            AddAssert("the folder is what persists, not the shared name",
                () => config.Get<string>(JukeBoxSetting.CustomSkinPath) == "aristia-2019");
        }

        [Test]
        public void PickingAnImportedSkinRoundTripsThroughConfig()
        {
            AddStep("install a skin", () =>
            {
                installSkin("aristia-archive", "Aristia");
                skinLibrary.Refresh();
            });

            AddStep("show overlay", () => overlay.Show());
            AddStep("pick it", () => overlay.SkinDropdown.Current.Value = SkinChoice.Imported("aristia-archive"));

            AddAssert("the kind persists as Custom", () => config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Custom);
            AddAssert("and the folder names which one", () => config.Get<string>(JukeBoxSetting.CustomSkinPath) == "aristia-archive");

            AddStep("recreate overlay", () => overlayHost.Child = overlay = new SettingsOverlay());
            AddAssert("it comes back selected", () => overlay.SkinDropdown.Current.Value == SkinChoice.Imported("aristia-archive"));

            // Switching away deliberately leaves CustomSkinPath alone, so the library selection is
            // still remembered rather than reset to "no import".
            AddStep("switch to a bundled skin", () => overlay.SkinDropdown.Current.Value = SkinChoice.Bundled(JukeBoxSkin.Triangles));
            AddAssert("the kind moved", () => config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Triangles);
            AddAssert("the remembered import did not", () => config.Get<string>(JukeBoxSetting.CustomSkinPath) == "aristia-archive");
        }

        /// <summary>
        /// The upgrade path: a config written before skins had a library — Skin=Custom plus a
        /// CustomSkinPath folder — must come up with that skin still selected, now under its real
        /// name. Nobody's selection quietly reverts to Argon.
        /// </summary>
        [Test]
        public void AnExistingCustomSkinPathStaysSelectedUnderItsRealName()
        {
            AddStep("write a pre-library config and install its skin", () =>
            {
                installSkin("Aristia v2", "Aristia");
                skinLibrary.Refresh();

                config.SetValue(JukeBoxSetting.CustomSkinPath, "Aristia v2");
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Custom);
            });

            AddStep("recreate overlay as if freshly launched", () => overlayHost.Child = overlay = new SettingsOverlay());
            AddStep("show overlay", () => overlay.Show());

            AddAssert("the imported skin is still selected",
                () => overlay.SkinDropdown.Current.Value == SkinChoice.Imported("Aristia v2"));
            AddAssert("under the name it declares", () => labelOf(SkinChoice.Imported("Aristia v2")) == "Aristia");
            AddAssert("and config was not rewritten", () => config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Custom
                                                            && config.Get<string>(JukeBoxSetting.CustomSkinPath) == "Aristia v2");
        }

        // A skin the user deleted from app storage by hand. The row has to survive, or the control
        // falls back onto some other value and overwrites a choice they never changed.
        [Test]
        public void ASelectedSkinMissingFromDiskKeepsItsRowAndItsSelection()
        {
            AddStep("select a skin that is not installed", () =>
            {
                config.SetValue(JukeBoxSetting.CustomSkinPath, "deleted-by-hand");
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Custom);
            });

            AddStep("recreate overlay", () => overlayHost.Child = overlay = new SettingsOverlay());
            AddStep("show overlay", () => overlay.Show());

            AddAssert("the selection stands", () => overlay.SkinDropdown.Current.Value == SkinChoice.Imported("deleted-by-hand"));
            AddAssert("shown under its folder name", () => labelOf(SkinChoice.Imported("deleted-by-hand")) == "deleted-by-hand");
            AddAssert("config untouched", () => config.Get<string>(JukeBoxSetting.CustomSkinPath) == "deleted-by-hand");
        }

        // ---- Maintenance ----------------------------------------------------------------

        // The destructive actions are one-shot things you DO, so they sit at the very bottom —
        // but the build stamp stays the last thing in the body regardless.
        [Test]
        public void MaintenanceIsTheLastSectionAndTheVersionStampStaysBelowIt()
        {
            AddStep("show overlay", () => overlay.Show());
            AddStep("scroll maintenance into view", () => overlay.ScrollControlIntoView(overlay.MaintenanceSection));
            AddUntilStep("laid out", () => overlay.MaintenanceSection.DrawHeight > 0);

            AddAssert("the version stamp sits below Maintenance", () =>
                overlay.VersionDrawable.ScreenSpaceDrawQuad.AABBFloat.Top
                >= overlay.MaintenanceSection.ScreenSpaceDrawQuad.AABBFloat.Bottom - 1);
        }

        // Nothing is deleted on the button press alone — the press only asks.
        [Test]
        public void ClearingTheCacheDoesNothingUntilItIsConfirmed()
        {
            AddStep("put a set in the cache", () => writeCachedSet(4242));
            AddStep("show overlay", () => overlay.Show());

            AddStep("press clear", () => overlay.MaintenanceSection.ClearCacheButton.TriggerClick());

            AddUntilStep("a confirmation is up", () => currentDialog() != null);
            AddAssert("and the set is still there", () => Directory.Exists(cachedSetPath(4242)));

            AddStep("cancel", () => currentDialog()!.PerformAction<PopupDialogCancelButton>());
            AddUntilStep("dialog dismissed", () => currentDialog() == null);
            AddAssert("still there after cancelling", () => Directory.Exists(cachedSetPath(4242)));
        }

        [Test]
        public void ConfirmingTheClearEmptiesTheCacheAndReportsWhatItFreed()
        {
            AddStep("put two sets in the cache", () =>
            {
                writeCachedSet(4242);
                writeCachedSet(4243);
            });

            AddStep("show overlay", () => overlay.Show());
            AddStep("press clear", () => overlay.MaintenanceSection.ClearCacheButton.TriggerClick());
            AddUntilStep("a confirmation is up", () => currentDialog() != null);
            AddStep("confirm", () => currentDialog()!.PerformAction<PopupDialogDangerousButton>());

            AddUntilStep("both sets are gone", () => !Directory.Exists(cachedSetPath(4242)) && !Directory.Exists(cachedSetPath(4243)));
            AddUntilStep("and it says how much it freed", () => overlay.MaintenanceSection.Status.Contains("Cleared 2 beatmaps")
                                                               && overlay.MaintenanceSection.Status.Contains("KB"));
        }

        // One button per imported skin, named so it cannot be misread.
        [Test]
        public void EachImportedSkinGetsItsOwnRemoveButton()
        {
            AddStep("install two skins", () =>
            {
                installSkin("aristia-archive", "Aristia");
                installSkin("rafis-2019", "Rafis");
                skinLibrary.Refresh();
            });

            AddStep("show overlay", () => overlay.Show());

            AddUntilStep("one button each, by name", () => overlay.MaintenanceSection.SkinRemoveButtons
                .Select(b => b.Text.ToString())
                .SequenceEqual(new[] { "Remove skin \"Aristia\"", "Remove skin \"Rafis\"" }));
        }

        /// <summary>
        /// The danger buttons must not touch. lazer's SettingsButton carries a NEGATIVE vertical
        /// margin (-5, so -10 across the pair) which a FillFlowContainer takes straight off the
        /// step between children — so the obvious Spacing of 8 renders as a 2px overlap and three
        /// buttons read as one continuous pink slab. Measured off the rendered rectangles, because
        /// that is the only place the bug was visible.
        /// </summary>
        [Test]
        public void TheDangerButtonsAreEvenlySpacedAndNeverOverlap()
        {
            AddStep("install two skins", () =>
            {
                installSkin("aristia-archive", "Aristia");
                installSkin("rafis-2019", "Rafis");
                skinLibrary.Refresh();
            });

            AddStep("show overlay", () => overlay.Show());
            AddUntilStep("all three buttons laid out", () => overlay.MaintenanceSection.SkinRemoveButtons.Count == 2
                                                            && overlay.MaintenanceSection.ClearCacheButton.DrawHeight > 0);
            AddWaitStep("let the flow settle", 3);

            AddAssert("every gap is the row spacing, and positive", () =>
            {
                var rects = maintenanceButtonRects();

                for (int i = 1; i < rects.Count; i++)
                {
                    float gap = rects[i].Top - rects[i - 1].Bottom;

                    if (gap <= 0 || Math.Abs(gap - Theme.RowSpacing) > 0.5f)
                        return false;
                }

                return rects.Count == 3;
            });
        }

        [Test]
        public void RemovingASkinDeletesOnlyItsOwnFolderAndOnlyOnceConfirmed()
        {
            AddStep("install two skins", () =>
            {
                installSkin("aristia-archive", "Aristia");
                installSkin("rafis-2019", "Rafis");
                skinLibrary.Refresh();
            });

            AddStep("show overlay", () => overlay.Show());
            AddStep("press remove on Aristia", () => overlay.MaintenanceSection.SkinRemoveButtons[0].TriggerClick());

            AddUntilStep("a confirmation is up", () => currentDialog() != null);
            AddAssert("nothing deleted yet", () => Directory.Exists(Path.Combine(skinsRoot, "aristia-archive")));

            AddStep("confirm", () => currentDialog()!.PerformAction<PopupDialogDangerousButton>());

            AddUntilStep("Aristia is gone", () => !Directory.Exists(Path.Combine(skinsRoot, "aristia-archive")));
            AddAssert("Rafis is untouched", () => Directory.Exists(Path.Combine(skinsRoot, "rafis-2019")));
            AddUntilStep("the library dropped it", () => skinLibrary.Skins.Select(s => s.Folder).SequenceEqual(new[] { "rafis-2019" }));
        }

        // Deleting the skin that is SELECTED must leave a working selection behind. Classic, not
        // Argon: it is this app's legacy-fidelity default, and someone running an imported legacy
        // skin lands closer to where they were.
        [Test]
        public void RemovingTheSelectedSkinFallsBackToClassicAndSaysSo()
        {
            AddStep("install and select a skin", () =>
            {
                installSkin("aristia-archive", "Aristia");
                skinLibrary.Refresh();
                config.SetValue(JukeBoxSetting.CustomSkinPath, "aristia-archive");
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Custom);
            });

            AddStep("show overlay", () => overlay.Show());
            AddStep("press remove", () => overlay.MaintenanceSection.SkinRemoveButtons[0].TriggerClick());
            AddUntilStep("a confirmation is up", () => currentDialog() != null);
            AddStep("confirm", () => currentDialog()!.PerformAction<PopupDialogDangerousButton>());

            AddUntilStep("gone from disk", () => !Directory.Exists(Path.Combine(skinsRoot, "aristia-archive")));
            AddAssert("selection fell back to Classic", () => config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Classic);
            AddAssert("and no import is left selected", () => config.Get<string>(JukeBoxSetting.CustomSkinPath).Length == 0);
            AddAssert("the status says which and what happened", () => overlay.MaintenanceSection.Status == "Removed \"Aristia\". Gameplay skin is now Classic.");
        }

        [Test]
        public void RemovingTheLastSkinLeavesTheDropdownWithBuiltInsOnly()
        {
            AddStep("install one skin", () =>
            {
                installSkin("solo-skin", "Solo");
                skinLibrary.Refresh();
            });

            AddStep("show overlay", () => overlay.Show());
            AddStep("press remove", () => overlay.MaintenanceSection.SkinRemoveButtons[0].TriggerClick());
            AddUntilStep("a confirmation is up", () => currentDialog() != null);
            AddStep("confirm", () => currentDialog()!.PerformAction<PopupDialogDangerousButton>());

            AddUntilStep("no removal buttons left", () => overlay.MaintenanceSection.SkinRemoveButtons.Count == 0);
            AddUntilStep("dropdown is bundled skins only", () => overlay.SkinDropdown.Items.All(i => !i.IsImported));
        }

        [Resolved]
        private osu.Game.Overlays.IDialogOverlay dialogOverlay { get; set; } = null!;

        private PopupDialog? currentDialog() => dialogOverlay.CurrentDialog;

        /// <summary>The Maintenance section's buttons as rendered, top to bottom.</summary>
        private List<osu.Framework.Graphics.Primitives.RectangleF> maintenanceButtonRects()
            => new[] { overlay.MaintenanceSection.ClearCacheButton }
               .Concat(overlay.MaintenanceSection.SkinRemoveButtons)
               .Select(b => b.ScreenSpaceDrawQuad.AABBFloat)
               .OrderBy(r => r.Top)
               .ToList();

        private string cachedSetPath(int setId) => Path.Combine(host.Storage.GetFullPath("cache"), setId.ToString());

        /// <summary>A cache folder that looks enough like a set for BeatmapCache to count it.</summary>
        private void writeCachedSet(int setId)
        {
            string dir = cachedSetPath(setId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "map.osu"), "osu file format v14\n");
            File.WriteAllBytes(Path.Combine(dir, "audio.mp3"), new byte[4096]);
            cachedSets.Add(setId);
        }

        private readonly List<int> cachedSets = new List<int>();

        [Resolved]
        private SkinLibrary skinLibrary { get; set; } = null!;

        private string skinsRoot => host.Storage.GetFullPath(SkinLibrary.STORAGE_DIRECTORY);

        private readonly List<string> installedSkins = new List<string>();

        private void installSkin(string folder, string declaredName)
        {
            string directory = Path.Combine(skinsRoot, folder);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "skin.ini"), $"[General]\nName: {declaredName}\nVersion: 2.5\n");
            installedSkins.Add(folder);
        }

        /// <summary>
        /// The dropdown's rows as the user actually reads them — the menu's own item drawables'
        /// text, not the values behind them, since rendering the right NAME is the whole point.
        /// </summary>
        private List<osu.Framework.Graphics.UserInterface.DropdownMenuItem<SkinChoice>> menuItems()
            => overlay.SkinDropdown
                      .ChildrenOfType<osu.Framework.Graphics.UserInterface.Menu>()
                      .First().Items
                      .OfType<osu.Framework.Graphics.UserInterface.DropdownMenuItem<SkinChoice>>()
                      .ToList();

        /// <summary>The imported rows only, in listed order.</summary>
        private List<string> importedLabels()
            => menuItems().Where(i => i.Value.IsImported).Select(i => i.Text.Value.ToString()).ToList();

        private string labelOf(SkinChoice choice)
            => menuItems().First(i => i.Value.Equals(choice)).Text.Value.ToString();

        // The skins directory belongs to the runner game's real storage, shared with every other
        // fixture in this assembly, so installs are undone however the test ended. Skipped
        // entirely when nothing was installed — TearDown also runs for TestConstructor, which
        // never loads the scene and so never gets a host to ask for the storage path.
        [TearDown]
        public void RemoveInstalledSkins()
        {
            // Nothing was written, so nothing to undo — and nothing to ask `host` for. TearDown
            // also runs for TestConstructor, which never loads the scene and so never gets one.
            if (installedSkins.Count == 0 && cachedSets.Count == 0)
                return;

            foreach (int setId in cachedSets)
            {
                string dir = cachedSetPath(setId);

                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }

            cachedSets.Clear();

            foreach (string folder in installedSkins)
            {
                string directory = Path.Combine(skinsRoot, folder);

                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }

            installedSkins.Clear();
        }

        [Test]
        public void SkinChoicePersistsToConfig()
        {
            AddStep("show overlay", () => overlay.Show());
            AddAssert("config starts Argon", () => config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Argon);

            AddStep("select Triangles", () => overlay.SkinDropdown.Current.Value = SkinChoice.Bundled(JukeBoxSkin.Triangles));
            AddAssert("config updated to Triangles", () => config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Triangles);

            AddStep("recreate overlay", () => overlayHost.Child = overlay = new SettingsOverlay());
            AddAssert("dropdown starts Triangles", () => overlay.SkinDropdown.Current.Value == SkinChoice.Bundled(JukeBoxSkin.Triangles));
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

            AddStep("create docked overlay", () => overlayHost.Child = docked = new SettingsOverlay(docked: true));
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
