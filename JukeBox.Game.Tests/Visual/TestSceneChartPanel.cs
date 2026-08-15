#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Game.Overlays.Settings;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Configuration;
using osu.Game.Rulesets.Mania.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.UI;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The Chart tab itself: which element groups it offers for what is playing, and how it presents
    /// a chart whose mods are not the user's to choose.
    /// </summary>
    [TestFixture]
    public partial class TestSceneChartPanel : JukeBoxTestScene
    {
        private JukeBoxConfigManager config = null!;
        private PlaybackController playback = null!;
        private ChartModSelection chartMods = null!;
        private PlayfieldElementVisibility visibility = null!;
        private ChartPanel panel = null!;

        /// <summary>The panel gets its own host container: assigning <c>Child</c> on the scene
        /// itself would clear the services added below it, silently unbinding them.</summary>
        private Container panelHost = null!;

        [Resolved]
        private Jukebox jukebox { get; set; } = null!;

        /// <summary>lazer's realm-backed per-ruleset config cache — the Rulesets rows that moved
        /// into this tab bind its bindables directly, so the tests read it back the same way.</summary>
        [Resolved]
        private IRulesetConfigCache rulesetConfigs { get; set; } = null!;

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-chart-panel-test", Path.GetRandomFileName())));
            playback = new PlaybackController();
            chartMods = new ChartModSelection();
            visibility = new PlayfieldElementVisibility();

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            deps.CacheAs(playback);
            deps.Cache(chartMods);
            deps.Cache(visibility);
            return deps;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Add(playback);
            Add(chartMods);
            Add(visibility);
            Add(panelHost = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset state", () =>
            {
                config.SetValue(JukeBoxSetting.ChartMods, string.Empty);
                config.SetValue(JukeBoxSetting.HiddenPlayfieldElements, string.Empty);
                jukebox.NowPlaying.Value = null;
                playback.Current.Value = null;
                playback.SelectedOsuFile.Value = null;

                panelHost.Child = panel = new ChartPanel { RelativeSizeAxes = Axes.Both };
            });

            AddUntilStep("panel loaded", () => panel.IsLoaded);
        }

        /// <summary>
        /// Every ruleset's element group is listed at all times, whatever is playing (user request):
        /// the section is a complete inventory of what can be hidden, not a view of the current
        /// song. The one row that still comes and goes is the shared judgements toggle, and that is
        /// a real absence rather than scoping — osu!catch draws no hit-score popups at all.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryRulesetsElementGroupIsListedWhateverIsPlaying(int mode)
        {
            AddStep($"play a mode-{mode} difficulty", () => playing(mode));

            AddAssert("all four ruleset groups are shown",
                () => new[] { 0, 1, 2, 3 }.All(panel.ElementGroupVisible));

            AddAssert("and the shared group with them",
                () => panel.ElementGroupVisible(PlayfieldElementCatalog.all_rulesets));

            AddAssert("every ruleset's own rows are offered, not just the playing one's",
                () => new[]
                {
                    PlayfieldElement.OsuSliderFollowRing,
                    PlayfieldElement.TaikoInputDrum,
                    PlayfieldElement.CatchCatcher,
                    PlayfieldElement.ManiaKeyArea,
                }.All(e => panel.ElementCheckbox(e).Alpha == 1));

            AddAssert("judgements offered only where they are drawn",
                () => (panel.ElementCheckbox(PlayfieldElement.Judgements).Alpha > 0) == (mode != 2));
        }

        /// <summary>The panel must not reshuffle under the user when the song changes — the set of
        /// groups is identical before and after a ruleset change.</summary>
        [Test]
        public void ChangingRulesetDoesNotReshuffleTheElementGroups()
        {
            bool[] before = Array.Empty<bool>();

            AddStep("play an osu! difficulty", () => playing(0));
            AddStep("record which groups are shown", () => before = groupVisibility());

            AddStep("switch to the mania difficulty of the same set", () => playback.SelectedOsuFile.Value = "/set/diff3.osu");
            AddAssert("the same groups are shown", () => groupVisibility().SequenceEqual(before));

            AddStep("switch to the taiko difficulty", () => playback.SelectedOsuFile.Value = "/set/diff1.osu");
            AddAssert("still the same groups", () => groupVisibility().SequenceEqual(before));
        }

        /// <summary>
        /// The point of an always-complete list: a toggle set while another ruleset is playing is
        /// remembered, persisted, and in force the moment a map of its own ruleset starts. The
        /// filter never consults what is playing — it reads the shared visibility service — so this
        /// is really a check that nothing re-scopes the value behind the user's back.
        /// </summary>
        [Test]
        public void AToggleSetForAnotherRulesetIsHonouredWhenThatRulesetPlays()
        {
            AddStep("play an osu! difficulty", () => playing(0));

            AddStep("hide mania's key area while osu! is on screen",
                () => panel.ElementCheckbox(PlayfieldElement.ManiaKeyArea).Current.Value = false);

            AddAssert("the service took it", () => visibility.IsHidden(PlayfieldElement.ManiaKeyArea));
            AddAssert("and it persisted",
                () => config.Get<string>(JukeBoxSetting.HiddenPlayfieldElements).Contains("ManiaKeyArea"));

            AddStep("now play the mania difficulty", () => playback.SelectedOsuFile.Value = "/set/diff3.osu");

            AddAssert("it is still hidden, and still ticked off in the tab",
                () => visibility.IsHidden(PlayfieldElement.ManiaKeyArea)
                      && !panel.ElementCheckbox(PlayfieldElement.ManiaKeyArea).Current.Value);
        }

        private bool[] groupVisibility()
            => new[] { PlayfieldElementCatalog.all_rulesets, 0, 1, 2, 3 }.Select(panel.ElementGroupVisible).ToArray();

        /// <summary>
        /// The Rulesets and Analysis sections moved here wholesale. They keep their real lazer
        /// per-ruleset config bindables, so a change here is a change to what the hosted ruleset
        /// itself reads — nothing is mirrored into our own config.
        /// </summary>
        [Test]
        public void ManiaScrollSpeedReachesLazersOwnRulesetConfig()
        {
            // Ruleset bindings attach once the realm-backed config cache has loaded (scheduled
            // retry in the panel) — bound is observable as the slider taking the config's range.
            AddUntilStep("mania slider bound", () => panel.ManiaScrollSpeedSlider.Current.Value >= 1);

            AddStep("set scroll speed 20", () => panel.ManiaScrollSpeedSlider.Current.Value = 20);

            AddAssert("lazer's mania ruleset config holds 20", () => maniaConfig().Get<double>(ManiaRulesetSetting.ScrollSpeed) == 20);

            // And the other direction: whatever writes that config drives the slider.
            AddStep("something else writes the config", () => maniaConfig().SetValue(ManiaRulesetSetting.ScrollSpeed, 12.0));
            AddAssert("the slider followed", () => panel.ManiaScrollSpeedSlider.Current.Value == 12);

            AddStep("restore default 8", () => panel.ManiaScrollSpeedSlider.Current.Value = 8);
        }

        [Test]
        public void EveryMovedRulesetRowIsInTheChartTab()
        {
            AddUntilStep("ruleset rows bound", () => panel.ManiaScrollSpeedSlider.Current.Value >= 1);

            AddAssert("all of Rulesets and Analysis came across", () =>
            {
                var labels = panel.ChildrenOfType<SettingsItem<bool>>().Select(i => i.LabelText.ToString())
                                  .Concat(panel.ChildrenOfType<SettingsItem<double>>().Select(i => i.LabelText.ToString()))
                                  .Concat(panel.ChildrenOfType<SettingsItem<int>>().Select(i => i.LabelText.ToString()))
                                  .Concat(panel.ChildrenOfType<SettingsItem<PlayfieldBorderStyle>>().Select(i => i.LabelText.ToString()))
                                  .Concat(panel.ChildrenOfType<SettingsItem<ManiaScrollingDirection>>().Select(i => i.LabelText.ToString()))
                                  .ToList();

                return new[]
                {
                    "Snaking in sliders", "Snaking out sliders", "Cursor trail", "Cursor ripples", "Playfield border style",
                    "Scrolling direction", "Scroll speed", "Timing-based note colouring",
                    "Show click markers", "Show frame markers", "Show cursor path", "Hide gameplay cursor", "Display length",
                }.All(labels.Contains)
                // "Hit animations" appears twice — once for osu!, once for osu!taiko.
                && labels.Count(l => l == "Hit animations") == 2;
            });
        }

        private ManiaRulesetConfigManager maniaConfig()
            => (ManiaRulesetConfigManager)rulesetConfigs.GetConfigFor(new ManiaRuleset())!;

        /// <summary>
        /// A replay was played under mods of its own, so the toggles are not the user's to move
        /// while one drives playback — locked and dimmed like the difficulty switcher's own replay
        /// state, with the replay's mods named in their place.
        /// </summary>
        [Test]
        public void AReplayLocksTheModTogglesAndNamesItsOwnMods()
        {
            AddStep("enable HD", () => panel.ModCheckbox(ChartMod.Hidden).Current.Value = true);
            AddAssert("nothing is locked to begin with", () => !panel.ModsLocked && panel.ReplayModsNote.Length == 0);

            AddStep("a replay starts playing", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 7,
                Replay = new ReplayAttachment { PlayerName = "Cookiezi", ModAcronyms = new[] { "HD", "HR", "DT" } },
            });

            AddUntilStep("the mod rows locked and dimmed", () => panel.ModsLocked);
            AddAssert("and name the replay's own mods", () => panel.ReplayModsNote.Contains("HD HR DT"));

            // Disabled is what stops the user: lazer's checkbox refuses input while its Current is,
            // and a programmatic write throws outright.
            AddAssert("every toggle refuses to move", () => Enum.GetValues<ChartMod>().All(m => panel.ModCheckbox(m).Current.Disabled));
            AddAssert("nothing new got selected behind the lock", () => !chartMods.Enabled(ChartMod.Flashlight).Value);

            AddStep("replay stops", () => jukebox.NowPlaying.Value = new BeatmapSetInfo { Id = 8 });

            AddUntilStep("the toggles came back", () => !panel.ModsLocked);
            AddAssert("with the user's own selection intact", () => panel.ModCheckbox(ChartMod.Hidden).Current.Value);
            AddAssert("and no replay note left", () => panel.ReplayModsNote.Length == 0);
        }

        /// <summary>
        /// Mod rows narrow to what the playing ruleset actually offers, asked of lazer rather than
        /// declared. The key counts, Co-op and Fade In really are osu!mania-only; Mirror and Random
        /// are NOT — lazer gives Mirror to osu! and osu!catch and Random to osu! and osu!taiko, so
        /// scoping those two to mania would have hidden working toggles.
        /// </summary>
        [TestCase(0, false, true, true)]
        [TestCase(1, false, false, true)]
        [TestCase(2, false, true, false)]
        [TestCase(3, true, true, true)]
        public void ModRowsNarrowToWhatTheRulesetOffers(int mode, bool maniaOnly, bool mirror, bool random)
        {
            AddStep($"play a mode-{mode} difficulty", () => playing(mode));

            AddAssert("the shared mods are always offered",
                () => new[] { ChartMod.Easy, ChartMod.HalfTime, ChartMod.HardRock, ChartMod.Hidden, ChartMod.DoubleTime, ChartMod.Nightcore, ChartMod.Flashlight }
                    .All(panel.ModOffered));

            AddAssert($"mania-only mods offered: {maniaOnly}",
                () => new[] { ChartMod.Key1, ChartMod.Key4, ChartMod.Key7, ChartMod.Key9, ChartMod.DualStages, ChartMod.FadeIn }
                    .All(m => panel.ModOffered(m) == maniaOnly));

            AddAssert($"Mirror offered: {mirror}", () => panel.ModOffered(ChartMod.Mirror) == mirror);
            AddAssert($"Random offered: {random}", () => panel.ModOffered(ChartMod.Random) == random);
        }

        /// <summary>Grouped by lazer's own <see cref="ModType"/>, which is also what stable's mania
        /// mod screen shows — its "Special" column is lazer's Conversion.</summary>
        [Test]
        public void ModsAreGroupedByLazersOwnCategories()
        {
            AddStep("play a mania difficulty", () => playing(3));

            AddAssert("the three categories are all on screen",
                () => panel.ModCategoryVisible(ModType.DifficultyReduction)
                      && panel.ModCategoryVisible(ModType.DifficultyIncrease)
                      && panel.ModCategoryVisible(ModType.Conversion));

            AddAssert("Fade In sits with the difficulty increases, not the conversions",
                () => ChartModCatalog.TypeOf(ChartMod.FadeIn) == ModType.DifficultyIncrease);

            AddAssert("the key counts, Co-op, Mirror and Random are conversions",
                () => new[] { ChartMod.Key4, ChartMod.DualStages, ChartMod.Mirror, ChartMod.Random }
                    .All(m => ChartModCatalog.TypeOf(m) == ModType.Conversion));
        }

        /// <summary>
        /// The key counts and Co-op can only act on a converted beatmap, and this app never renders
        /// one — so rather than offering toggles that silently do nothing, the tab greys them and
        /// explains why. Mirror and Random sit in the same category and stay live.
        /// </summary>
        [Test]
        public void ConversionOnlyModsAreMarkedInapplicable()
        {
            AddStep("play a mania difficulty", () => playing(3));

            AddAssert("key counts and Co-op refuse input",
                () => new[] { ChartMod.Key1, ChartMod.Key4, ChartMod.Key9, ChartMod.DualStages }.All(panel.ModInert));

            AddAssert("and the reason is on screen", () => panel.ConvertsOnlyNoteVisible);

            AddAssert("Mirror and Random are still live",
                () => !panel.ModCheckbox(ChartMod.Mirror).Current.Disabled
                      && !panel.ModCheckbox(ChartMod.Random).Current.Disabled);

            AddStep("switch to an osu! difficulty", () => playing(0));

            AddAssert("the note goes with the rows it explains", () => !panel.ConvertsOnlyNoteVisible);
        }

        [Test]
        public void ElementTogglesDriveTheSharedVisibilityService()
        {
            AddStep("play an osu! difficulty", () => playing(0));

            AddStep("untick the cursor", () => panel.ElementCheckbox(PlayfieldElement.OsuCursor).Current.Value = false);

            AddAssert("the service hid it", () => visibility.IsHidden(PlayfieldElement.OsuCursor));
            AddAssert("and it was persisted",
                () => config.Get<string>(JukeBoxSetting.HiddenPlayfieldElements).Contains("OsuCursor"));

            AddStep("something else hides the spinner",
                () => visibility.Shown(PlayfieldElement.OsuSpinner).Value = false);

            AddAssert("the tab's checkbox followed", () => !panel.ElementCheckbox(PlayfieldElement.OsuSpinner).Current.Value);
        }

        /// <summary>A set whose difficulties cover every mode, with <paramref name="mode"/>'s one
        /// selected — enough for the tab, which only reads the selected difficulty's mode.</summary>
        private void playing(int mode)
        {
            playback.Current.Value = new CachedBeatmapSet
            {
                Directory = "/set",
                PreferredOsuFile = "/set/diff0.osu",
                OsuFiles = new List<string> { "/set/diff0.osu", "/set/diff1.osu", "/set/diff2.osu", "/set/diff3.osu" },
                Difficulties = Enumerable.Range(0, 4)
                                         .Select(m => new DifficultyInfo { Path = $"/set/diff{m}.osu", Version = $"mode {m}", Mode = m })
                                         .ToList(),
            };

            playback.SelectedOsuFile.Value = $"/set/diff{mode}.osu";
        }
    }
}
