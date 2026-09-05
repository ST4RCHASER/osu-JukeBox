#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Overlays;
using osuTK.Graphics;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The Players section of the Playback tab: the multi-replay settings that moved here, and the
    /// per-player colour and mod overrides. Asserted on the EFFECT — the config value a dropdown
    /// wrote, or the override the store now holds for one player and not another — rather than on the
    /// controls having been built.
    /// </summary>
    [TestFixture]
    public partial class TestScenePlayersPanel : JukeBoxTestScene
    {
        private JukeBoxConfigManager config = null!;
        private PlaybackController playback = null!;
        private ReplayStore replays = null!;
        private PlayerOverrideStore overrides = null!;
        private PreloadProgressTracker preloadTracker = null!;
        private SkinLibrary skinLibrary = null!;
        private OverlayColourProvider colourProvider = null!;

        private Container host = null!;
        private PlayersPanel panel = null!;

        private const string osu_file = "/players-panel-test/map [Hard].osu";

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-players-panel-test", Path.GetRandomFileName())));
            playback = new PlaybackController();
            replays = new ReplayStore();
            overrides = new PlayerOverrideStore();
            preloadTracker = new PreloadProgressTracker();
            skinLibrary = new SkinLibrary();
            colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            deps.CacheAs(playback);
            deps.Cache(replays);
            deps.Cache(overrides);
            deps.Cache(preloadTracker);
            deps.Cache(skinLibrary);
            deps.CacheAs(colourProvider);
            return deps;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Add(skinLibrary);
            Add(playback);
            Add(host = new Container { RelativeSizeAxes = Axes.Both });
        }

        private ReplayAttachment register(string name)
        {
            var attachment = new ReplayAttachment { PlayerName = name, OsuFile = osu_file, SourcePath = $"/tmp/{name}.osr" };
            replays.Register(attachment);
            return attachment;
        }

        private void build(int count)
        {
            replays.ClearForOsuFile(osu_file);

            for (int i = 0; i < count; i++)
                register($"player{i}");

            playback.SelectedOsuFile.Value = null;
            host.Child = panel = new PlayersPanel();
            playback.SelectedOsuFile.Value = osu_file;
        }

        private ReplayAttachment[] players => panel.CurrentPlayers.ToArray();

        [Test]
        public void TheSectionOnlyShowsWhenSeveralReplaysAreOnScreen()
        {
            AddStep("build with one player", () => build(1));
            AddUntilStep("panel loaded", () => panel.IsLoaded);
            AddAssert("hidden with a single player", () => !panel.IsShowing);

            AddStep("build with three", () => build(3));
            AddUntilStep("panel loaded", () => panel.IsLoaded);
            AddAssert("now showing", () => panel.IsShowing && panel.CurrentPlayers.Count == 3);
        }

        // The preload buffer bar moved off the Players panel onto the playback progress bar (item 2/10)
        // and its status line onto the now-playing panel (item 3); those are covered by
        // TestSceneProgressSliderBar and TestSceneNowPlayingPanel respectively.

        [Test]
        public void TheMovedMultiReplaySettingsWriteThroughToConfig()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("switch to grid", () => panel.MultiReplayModeDropdown.Current.Value = MultiReplayMode.Grid);
            AddAssert("config took the mode", () => config.Get<MultiReplayMode>(JukeBoxSetting.MultiReplayMode) == MultiReplayMode.Grid);

            AddStep("turn knockout on", () => panel.KnockoutModeDropdown.Current.Value = KnockoutMode.ComboBreak);
            AddAssert("config took knockout", () => config.Get<KnockoutMode>(JukeBoxSetting.KnockoutMode) == KnockoutMode.ComboBreak);

            AddStep("rank by accuracy", () => panel.KnockoutSortDropdown.Current.Value = KnockoutSort.Accuracy);
            AddAssert("config took the sort", () => config.Get<KnockoutSort>(JukeBoxSetting.KnockoutSortBy) == KnockoutSort.Accuracy);

            AddStep("stop re-ordering", () => panel.KnockoutLiveSortCheckbox.Current.Value = false);
            AddAssert("config took live-sort", () => !config.Get<bool>(JukeBoxSetting.KnockoutLiveSort));

            AddStep("turn on remove-name-after-knockout", () => panel.RemoveNameCheckbox.Current.Value = true);
            AddAssert("config took remove-name", () => config.Get<bool>(JukeBoxSetting.RemoveNameAfterKnockout));

            // The two round-9 options live here too, at their documented defaults (flip ON, result OFF).
            AddAssert("flip-HR starts on", () => panel.FlipHrCheckbox.Current.Value && config.Get<bool>(JukeBoxSetting.FlipHrReplay));
            AddStep("turn flip-HR off", () => panel.FlipHrCheckbox.Current.Value = false);
            AddAssert("config took flip-HR", () => !config.Get<bool>(JukeBoxSetting.FlipHrReplay));

            AddAssert("show-result starts off", () => !panel.ShowResultCheckbox.Current.Value && !config.Get<bool>(JukeBoxSetting.ShowPlayerResult));
            AddStep("turn show-result on", () => panel.ShowResultCheckbox.Current.Value = true);
            AddAssert("config took show-result", () => config.Get<bool>(JukeBoxSetting.ShowPlayerResult));
        }

        [Test]
        public void AColourSwatchOverridesOnlyTheTargetedPlayer()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("target player 1, pick lime", () =>
            {
                panel.SelectTarget(1);
                panel.PickColour(Color4.Lime);
            });

            AddAssert("player 1 has the override", () => overrides.Peek(players[1])?.CursorColour == Color4.Lime);
            AddAssert("players 0 and 2 do not", () =>
                overrides.Peek(players[0])?.CursorColour == null && overrides.Peek(players[2])?.CursorColour == null);

            AddStep("reset player 1", () =>
            {
                panel.SelectTarget(1);
                panel.PickColour(null);
            });
            AddAssert("the override is cleared", () => overrides.Peek(players[1])?.CursorColour == null);
        }

        /// <summary>
        /// The colour picker sets any colour on the target and REMEMBERS it as a reusable swatch —
        /// picked colours accumulate, de-duplicated. Asserted on the override the store now holds and
        /// on the remembered set growing (then not growing on a repeat pick).
        /// </summary>
        [Test]
        public void PickingAColourAppliesItAndRemembersItAsASwatch()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            int baseSwatches = 0;
            AddStep("note the swatch count", () => baseSwatches = panel.SwatchCount);

            AddStep("target player 0, pick a custom colour", () =>
            {
                panel.SelectTarget(0);
                panel.ApplyPickedColour(new Color4(0.1f, 0.7f, 0.3f, 1f));
            });

            AddAssert("player 0 got the picked colour", () =>
            {
                var c = overrides.Peek(players[0])?.CursorColour;
                return c.HasValue
                       && System.Math.Abs(c.Value.R - 0.1f) < 0.01f
                       && System.Math.Abs(c.Value.G - 0.7f) < 0.01f
                       && System.Math.Abs(c.Value.B - 0.3f) < 0.01f;
            });
            AddAssert("players 1 and 2 unaffected", () =>
                overrides.Peek(players[1])?.CursorColour == null && overrides.Peek(players[2])?.CursorColour == null);

            AddAssert("it was remembered, as one new swatch", () =>
                config.Get<string>(JukeBoxSetting.RememberedCursorColours).Length > 0 && panel.SwatchCount == baseSwatches + 1);

            AddStep("pick the very same colour again", () => panel.ApplyPickedColour(new Color4(0.1f, 0.7f, 0.3f, 1f)));
            AddAssert("no duplicate swatch is added", () => panel.SwatchCount == baseSwatches + 1);

            AddStep("pick a different colour", () => panel.ApplyPickedColour(new Color4(0.9f, 0.2f, 0.5f, 1f)));
            AddAssert("that one adds another swatch", () => panel.SwatchCount == baseSwatches + 2);
        }

        [Test]
        public void TargetingAllPlayersColoursEveryone()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("target all, pick cyan", () =>
            {
                panel.SelectTarget(-1);
                panel.PickColour(Color4.Cyan);
            });

            AddAssert("every player is cyan", () => players.All(p => overrides.Peek(p)?.CursorColour == Color4.Cyan));
        }

        [Test]
        public void AModToggleReScoresOnlyTheTargetedPlayerAndClearsBackToRecorded()
        {
            AddStep("build two", () => build(2));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("target player 0, add Hard Rock", () =>
            {
                panel.SelectTarget(0);
                panel.SetMod("HR", true);
            });

            AddAssert("player 0 is overridden to HR", () =>
                overrides.Peek(players[0])?.Mods?.Any(m => m.Acronym == "HR") == true);
            AddAssert("player 1 has no override", () => overrides.Peek(players[1])?.Mods == null);

            AddStep("remove Hard Rock again", () => panel.SetMod("HR", false));
            AddAssert("player 0 falls back to recorded", () => overrides.Peek(players[0])?.Mods == null);
        }

        /// <summary>
        /// A user's IMPORTED skin can be given to one player, not just the bundled skins. The dropdown
        /// lists imported skins alongside the bundled ones, and picking one stores a per-player key by
        /// FOLDER (so a custom skin actually resolves at render time). Asserted on the stored key — the
        /// bug was that only bundled skins were selectable/applied per player.
        /// </summary>
        [Test]
        public void APerPlayerImportedSkinCanBeSelectedAndIsStoredByFolder()
        {
            const string folder = "test-imported-skin";

            AddStep("install a fake imported skin", () =>
            {
                string dir = Path.Combine(skinLibrary.Root, folder);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "skin.ini"), "[General]\nName: My Test Skin\n");
                skinLibrary.Refresh();
            });

            AddStep("build three", () => build(3));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddAssert("the imported skin is offered in the dropdown", () =>
                panel.SkinChoiceKeys.Contains(LazerChartLayer.CustomSkinKey(folder)));

            AddStep("target player 0, pick the imported skin", () =>
            {
                panel.SelectTarget(0);
                panel.SelectSkinKey(LazerChartLayer.CustomSkinKey(folder));
            });

            AddAssert("player 0's override stores the custom folder key", () =>
                overrides.Peek(players[0])?.SkinKey == LazerChartLayer.CustomSkinKey(folder));
            AddAssert("players 1 and 2 keep the global skin", () =>
                overrides.Peek(players[1])?.SkinKey == null && overrides.Peek(players[2])?.SkinKey == null);
        }

        [Test]
        public void ASkinChoiceOverridesOnlyTheTargetedPlayer()
        {
            AddStep("build two", () => build(2));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("target player 0, pick Classic", () =>
            {
                panel.SelectTarget(0);
                panel.SelectSkinKey("Classic");
            });

            AddAssert("player 0's skin is overridden", () => overrides.Peek(players[0])?.SkinKey == "Classic");
            AddAssert("player 1 keeps the global skin", () => overrides.Peek(players[1])?.SkinKey == null);

            AddStep("reset player 0 to the global skin", () =>
            {
                panel.SelectTarget(0);
                panel.SelectSkinKey(null);
            });
            AddAssert("the override is cleared", () => overrides.Peek(players[0])?.SkinKey == null);
        }

        [Test]
        public void TheDifficultyModsAreMutuallyExclusive()
        {
            AddStep("build two", () => build(2));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("target player 0", () => panel.SelectTarget(0));
            AddStep("add Easy", () => panel.SetMod("EZ", true));
            AddStep("then add Hard Rock", () => panel.SetMod("HR", true));

            AddAssert("only Hard Rock remains — Easy was turned off", () =>
            {
                var acronyms = overrides.Peek(players[0])?.Mods?.Select(m => m.Acronym).ToArray() ?? System.Array.Empty<string>();
                return acronyms.Contains("HR") && !acronyms.Contains("EZ");
            });
        }

        /// <summary>
        /// The rate mods — Double Time, Nightcore, Half Time — are NOT offered per player: they change
        /// the shared playback speed, which cannot differ between replays on one clock. Only Easy, Hard
        /// Rock, Hidden and Flashlight (the per-player-renderable mods) are shown.
        /// </summary>
        [Test]
        public void TheRateModsAreNotOfferedPerPlayer()
        {
            AddStep("build two", () => build(2));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddAssert("the per-player mod list is exactly EZ / HR / HD / FL", () =>
            {
                var offered = panel.OfferedModAcronyms.OrderBy(a => a).ToArray();
                return offered.SequenceEqual(new[] { "EZ", "FL", "HD", "HR" });
            });
        }

        /// <summary>
        /// The settings follow the multi-replay mode: the knockout/rail settings show in COMBINE and
        /// hide in GRID (there is no rail in grid); the per-player gameplay-skin and visual-mod controls
        /// show in GRID and hide in COMBINE (one shared chart can't take a per-player skin/mod).
        /// </summary>
        [Test]
        public void SettingsFollowTheMultiReplayMode()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("combine mode", () => panel.MultiReplayModeDropdown.Current.Value = MultiReplayMode.Combine);
            AddAssert("rail settings shown, per-player skin/mods hidden", () =>
                panel.RailSettingsShown && !panel.PerPlayerSkinAndModsShown);
            // Flip-HR acts on the shared combine chart, so it shows with the rail settings; the
            // result-screen option applies to either mode and stays.
            AddAssert("flip-HR and show-result shown in combine", () => panel.FlipHrShown && panel.ShowResultShown);

            AddStep("grid mode", () => panel.MultiReplayModeDropdown.Current.Value = MultiReplayMode.Grid);
            AddAssert("rail settings hidden, per-player skin/mods shown", () =>
                !panel.RailSettingsShown && panel.PerPlayerSkinAndModsShown);
            AddAssert("flip-HR hidden in grid, show-result still shown", () => !panel.FlipHrShown && panel.ShowResultShown);
        }
    }
}
