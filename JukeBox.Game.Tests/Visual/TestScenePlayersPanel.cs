#nullable enable

using System.IO;
using System.Linq;
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

        /// <summary>
        /// The preload buffer bar (YouTube-style): it shows while the replays are being recorded, its
        /// grey fill tracking the fraction preloaded, and hides once every timeline is complete. Driven
        /// by the shared <see cref="PreloadProgressTracker"/> the combine publishes to.
        /// </summary>
        [Test]
        public void ThePreloadBufferBarFillsWithProgressThenHides()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("report a half-recorded preload", () => preloadTracker.Report(0.5));
            AddUntilStep("the bar shows, filled about halfway", () =>
                panel.PreloadBar.Showing && System.Math.Abs(panel.PreloadBar.FillFraction - 0.5f) < 0.02f);

            AddStep("report nearly done", () => preloadTracker.Report(0.9));
            AddUntilStep("the fill grows and it is still shown", () =>
                panel.PreloadBar.Showing && panel.PreloadBar.FillFraction > 0.85f);

            AddStep("report complete", () => preloadTracker.Report(1.0));
            AddUntilStep("the bar hides once fully buffered", () => !panel.PreloadBar.Showing);
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

            AddStep("turn on remove-name-after-knockout", () => panel.RemoveNameCheckbox.Current.Value = true);
            AddAssert("config took remove-name", () => config.Get<bool>(JukeBoxSetting.RemoveNameAfterKnockout));
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
                panel.ColourPicker.Current.Value = new osu.Framework.Graphics.Colour4(0.1f, 0.7f, 0.3f, 1f);
                panel.ApplyPickedColour();
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

            AddStep("pick the very same colour again", () =>
            {
                panel.ColourPicker.Current.Value = new osu.Framework.Graphics.Colour4(0.1f, 0.7f, 0.3f, 1f);
                panel.ApplyPickedColour();
            });
            AddAssert("no duplicate swatch is added", () => panel.SwatchCount == baseSwatches + 1);

            AddStep("pick a different colour", () =>
            {
                panel.ColourPicker.Current.Value = new osu.Framework.Graphics.Colour4(0.9f, 0.2f, 0.5f, 1f);
                panel.ApplyPickedColour();
            });
            AddAssert("that one adds another swatch", () => panel.SwatchCount == baseSwatches + 2);
        }

        /// <summary>
        /// The colour picker is a small bounded control, not a giant block filling the panel. It used
        /// to stretch to the full panel width, which made its saturation/value square render as a huge
        /// white rectangle. Asserted on its drawn width staying compact — far under the panel width.
        /// </summary>
        [Test]
        public void TheColourPickerIsBoundedNotAFullPanelBlock()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("panel loaded", () => panel.IsLoaded);
            AddUntilStep("picker laid out", () => panel.ColourPicker.DrawWidth > 0);

            AddAssert("the picker is a small bounded control, not the full panel width", () =>
                panel.ColourPicker.DrawWidth <= 260 && panel.ColourPicker.DrawWidth < host.DrawWidth * 0.9f);

            AddAssert("and its height is bounded too (no runaway square)", () =>
                panel.ColourPicker.DrawHeight > 0 && panel.ColourPicker.DrawHeight <= 420);
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
