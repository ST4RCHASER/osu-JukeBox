#nullable enable

using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
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
            colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            deps.CacheAs(playback);
            deps.Cache(replays);
            deps.Cache(overrides);
            deps.CacheAs(colourProvider);
            return deps;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
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

        [Test]
        public void TheRateModsAreMutuallyExclusive()
        {
            AddStep("build two", () => build(2));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("target player 0", () => panel.SelectTarget(0));
            AddStep("add Double Time", () => panel.SetMod("DT", true));
            AddStep("then add Half Time", () => panel.SetMod("HT", true));

            AddAssert("only Half Time remains — Double Time was turned off", () =>
            {
                var acronyms = overrides.Peek(players[0])?.Mods?.Select(m => m.Acronym).ToArray() ?? System.Array.Empty<string>();
                return acronyms.Contains("HT") && !acronyms.Contains("DT");
            });
        }
    }
}
