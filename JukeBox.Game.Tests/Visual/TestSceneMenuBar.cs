#nullable enable

using System.Collections.Generic;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osuTK;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The top menu bar, driven the way a person drives it — the cursor arriving at the top edge to
    /// reveal it, leaving to dismiss it, a dropdown pinning it open — plus that every item calls the
    /// action it was handed and that File → Render greys out when told it is unavailable.
    ///
    /// <para>
    /// Real input throughout (<see cref="osu.Framework.Testing.ManualInputManagerTestScene"/>): the
    /// reveal is a function of the actual mouse position, so a test that only poked internal state
    /// would not exercise the behaviour that matters.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneMenuBar : JukeBoxManualInputTestScene
    {
        private MenuBar bar = null!;

        // What each item did, counted so a click can be shown to have reached its delegate.
        private readonly Dictionary<string, int> fired = new Dictionary<string, int>();

        private BindableBool renderEnabled = null!;
        private BindableBool spectating = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("build bar", () =>
            {
                fired.Clear();
                renderEnabled = new BindableBool(true);
                spectating = new BindableBool();

                bar = new MenuBar
                {
                    Actions = new MenuBarActions
                    {
                        OpenFiles = () => count("Open…"),
                        OpenRender = () => count("Render…"),
                        Quit = () => count("Quit"),
                        Play = () => count("Play"),
                        Pause = () => count("Pause"),
                        Next = () => count("Next"),
                        Restart = () => count("Restart"),
                        OpenBeatmapPage = () => count("Open beatmap page"),
                        LookupById = () => count("Lookup by id…"),
                        SearchBeatmaps = () => count("Search…"),
                        ToggleSpectate = () => count("toggle-spectate"),
                        SetupPlayers = () => count("Setup players…"),
                        ShowShortcuts = () => count("Show all shortcut keys"),
                        RenderEnabled = renderEnabled,
                        Spectating = spectating,
                    },
                };

                Child = bar;
            });

            AddUntilStep("bar loaded", () => bar.IsLoaded);

            // Park the cursor well clear of the top so every test starts from the hidden state.
            AddStep("cursor to centre", () => InputManager.MoveMouseTo(bar.ScreenSpaceDrawQuad.Centre));
            AddUntilStep("bar hidden", () => !bar.BarShown);
        }

        private void count(string key) => fired[key] = fired.TryGetValue(key, out int n) ? n + 1 : 1;

        private int firedFor(string key) => fired.TryGetValue(key, out int n) ? n : 0;

        [Test]
        public void RevealsAtTheTopEdgeAndHidesOnLeave()
        {
            AddStep("cursor to top edge", () => InputManager.MoveMouseTo(bar.ScreenSpaceDrawQuad.TopLeft + new Vector2(60, 2)));
            AddUntilStep("bar reveals", () => bar.BarShown);

            AddStep("cursor away", () => InputManager.MoveMouseTo(bar.ScreenSpaceDrawQuad.Centre));
            AddUntilStep("bar hides again", () => !bar.BarShown);
        }

        [Test]
        public void AnOpenDropdownKeepsTheBarVisibleEvenWithTheCursorAway()
        {
            AddStep("cursor to top edge", () => InputManager.MoveMouseTo(bar.ScreenSpaceDrawQuad.TopLeft + new Vector2(20, 2)));
            AddUntilStep("bar reveals", () => bar.BarShown);

            AddStep("open File", () => bar.Headers[0].TriggerClick());
            AddAssert("menu is open", () => bar.IsMenuOpen);

            AddStep("cursor far away", () => InputManager.MoveMouseTo(bar.ScreenSpaceDrawQuad.Centre));
            // Well past the grace delay — an open menu must still hold the bar down.
            AddWaitStep("let the grace lapse", 30);

            AddAssert("still open", () => bar.IsMenuOpen);
            AddAssert("still shown", () => bar.BarShown);

            AddStep("close it", () => bar.Headers[0].TriggerClick());
            AddAssert("menu closed", () => !bar.IsMenuOpen);
            AddUntilStep("now hides", () => !bar.BarShown);
        }

        [Test]
        public void EveryItemCallsTheActionItWasHanded()
        {
            foreach (string label in new[]
                     {
                         "Open…", "Render…", "Quit",
                         "Play", "Pause", "Next", "Restart", "Open beatmap page",
                         "Lookup by id…", "Search…",
                         "Setup players…",
                         "Show all shortcut keys",
                     })
            {
                string captured = label;
                AddStep($"click {captured}", () => bar.FindRow(captured)!.TriggerClick());
                AddAssert($"{captured} fired", () => firedFor(captured) == 1);
            }
        }

        [Test]
        public void TheSpectateToggleFlipsItsWordingWithTheBindable()
        {
            AddAssert("offers to start", () => bar.FindRow("Start spectating") != null);

            AddStep("click it", () => bar.FindRow("Start spectating")!.TriggerClick());
            AddAssert("toggle action fired", () => firedFor("toggle-spectate") == 1);

            AddStep("spectating turns on", () => spectating.Value = true);
            AddAssert("now offers to stop", () => bar.FindRow("Stop spectating") != null);
            AddAssert("no longer offers to start", () => bar.FindRow("Start spectating") == null);
        }

        [Test]
        public void RenderGreysOutAndRefusesClicksWhileUnavailable()
        {
            AddAssert("enabled to begin with", () => bar.FindRow("Render…")!.Enabled.Value);

            AddStep("mark unavailable", () => renderEnabled.Value = false);
            AddAssert("Render greys out", () => !bar.FindRow("Render…")!.Enabled.Value);

            AddStep("try to click it", () => bar.FindRow("Render…")!.TriggerClick());
            AddAssert("nothing happened", () => firedFor("Render…") == 0);

            AddStep("mark available again", () => renderEnabled.Value = true);
            AddAssert("Render enabled again", () => bar.FindRow("Render…")!.Enabled.Value);

            AddStep("click it", () => bar.FindRow("Render…")!.TriggerClick());
            AddAssert("now it fires", () => firedFor("Render…") == 1);
        }
    }
}
