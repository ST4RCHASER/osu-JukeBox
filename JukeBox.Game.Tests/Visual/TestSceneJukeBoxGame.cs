using JukeBox.Game.Configuration;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneJukeBoxGame : JukeBoxTestScene
    {
        // Add visual tests to ensure correct behaviour of your game: https://github.com/ppy/osu-framework/wiki/Development-and-Testing
        // You can make changes to classes associated with the tests and they will recompile and update immediately.

        private JukeBoxGame jukeBoxGame = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            AddGame(jukeBoxGame = new JukeBoxGame());
        }

        // Covers the Compact-overlay lifecycle on a real JukeBoxGame instance: the custom
        // FpsCounter overlay (see JukeBoxGameBase.FpsCounter) must be visible if and only if
        // FpsDisplayMode.Compact is selected. Deliberately stays off Details/Graph — those flip
        // the framework's OWN FrameStatistics bindable, which (per FrameStatisticsModeFor's own
        // doc comment) crashes a headless test host's real PerformanceOverlay with a
        // NullReferenceException; that mapping is covered separately, headless-safely, as a pure
        // function in JukeBoxGameBaseTest.
        [Test]
        public void FpsCounterOverlayVisibleIffCompact()
        {
            AddAssert("overlay hidden by default", () => jukeBoxGame.FpsCounter.State.Value == Visibility.Hidden);

            AddStep("select Compact", () => jukeBoxGame.FpsDisplay.Value = FpsDisplayMode.Compact);
            AddAssert("overlay visible", () => jukeBoxGame.FpsCounter.State.Value == Visibility.Visible);

            AddStep("select Off", () => jukeBoxGame.FpsDisplay.Value = FpsDisplayMode.Off);
            AddAssert("overlay hidden again", () => jukeBoxGame.FpsCounter.State.Value == Visibility.Hidden);

            AddStep("select Compact again", () => jukeBoxGame.FpsDisplay.Value = FpsDisplayMode.Compact);
            AddAssert("overlay visible again", () => jukeBoxGame.FpsCounter.State.Value == Visibility.Visible);
        }

        // The official search backend is constructed and cached by the REAL JukeBoxGameBase (this
        // scene's runner is one), which is the one piece TestSceneSearchBackend can't cover — it
        // substitutes its own stub-backed instance. Without this, a wiring mistake there would only
        // show up as the setting silently doing nothing at runtime.
        [Test]
        public void OfficialSearchBackendIsWiredIntoDependencies()
        {
            AddAssert("official search resolvable", () => officialSearch != null);
        }

        [Resolved]
        private JukeBox.Game.Online.OfficialBeatmapSearch officialSearch { get; set; } = null!;
    }
}
