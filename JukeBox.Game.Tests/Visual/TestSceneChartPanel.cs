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
using osu.Game.Rulesets.Mods;

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
        /// Only the ruleset on screen gets its element list — hiding a slider ball means nothing on
        /// a taiko map. The shared block additionally filters its own rows: osu!catch draws no
        /// hit-score popups, so the judgements toggle is not offered there even though it is for
        /// every other mode.
        /// </summary>
        [TestCase(0, PlayfieldElement.OsuSliderFollowRing)]
        [TestCase(1, PlayfieldElement.TaikoInputDrum)]
        [TestCase(2, PlayfieldElement.CatchCatcher)]
        [TestCase(3, PlayfieldElement.ManiaKeyArea)]
        public void OnlyTheElementsOfTheRulesetOnScreenAreOffered(int mode, PlayfieldElement own)
        {
            AddStep($"play a mode-{mode} difficulty", () => playing(mode));

            AddAssert("its own group is shown", () => panel.ElementGroupVisible(mode));
            AddAssert("its own elements are offered", () => panel.ElementCheckbox(own).Alpha == 1);

            AddAssert("no other ruleset's group is shown",
                () => new[] { 0, 1, 2, 3 }.Where(r => r != mode).All(r => !panel.ElementGroupVisible(r)));

            AddAssert("the shared group is shown", () => panel.ElementGroupVisible(PlayfieldElementCatalog.all_rulesets));

            // The one shared element that isn't actually shared.
            AddAssert("judgements offered only where they are drawn",
                () => (panel.ElementCheckbox(PlayfieldElement.Judgements).Alpha > 0) == (mode != 2));
        }

        [Test]
        public void SwitchingDifficultyToAnotherModeSwapsTheGroup()
        {
            AddStep("play an osu! difficulty", () => playing(0));
            AddAssert("osu! group shown", () => panel.ElementGroupVisible(0) && !panel.ElementGroupVisible(3));

            AddStep("switch to the mania difficulty of the same set", () => playback.SelectedOsuFile.Value = "/set/diff3.osu");

            AddAssert("mania group shown instead", () => panel.ElementGroupVisible(3) && !panel.ElementGroupVisible(0));
        }

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
