using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osuTK.Graphics;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The modal cursor-colour picker the rainbow swatch opens. Actually SHOWN here — the first
    /// in-window open threw from the framework's flow layout (mixed anchors in one vertical flow)
    /// and took the app down, which nothing exercising the panel's Apply path alone can see.
    /// </summary>
    [TestFixture]
    public partial class TestSceneCursorColourPickerOverlay : JukeBoxTestScene
    {
        private CursorColourPickerOverlay overlay = null!;
        private Color4? applied;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create overlay", () =>
            {
                applied = null;
                Child = overlay = new CursorColourPickerOverlay();
            });
            AddUntilStep("overlay loaded", () => overlay.IsLoaded);
        }

        [Test]
        public void OpeningShowsTheModalSeededAtTheStartColour()
        {
            AddStep("open at red", () => overlay.Open(Color4.Red, c => applied = c));
            AddUntilStep("modal is shown", () => overlay.State.Value == Visibility.Visible && overlay.Alpha > 0.99f);
            // A frame or two of layout with the modal present — this is where the mixed-anchor flow threw.
            AddWaitStep("let it lay out", 3);
            AddAssert("still shown, seeded at red", () => overlay.State.Value == Visibility.Visible && overlay.Picker.Current.Value.R > 0.99f);
        }

        [Test]
        public void ApplyHandsBackThePickedColourAndCloses()
        {
            AddStep("open at red", () => overlay.Open(Color4.Red, c => applied = c));
            AddUntilStep("modal is shown", () => overlay.State.Value == Visibility.Visible);

            AddStep("pick green", () => overlay.Picker.Current.Value = Color4.Lime);
            AddStep("apply", () => overlay.ApplyButton.TriggerClick());

            AddAssert("green came back through the callback", () => applied.HasValue && applied.Value.G > 0.99f && applied.Value.R < 0.01f);
            AddUntilStep("modal closed", () => overlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void CancelClosesWithoutApplying()
        {
            AddStep("open at red", () => overlay.Open(Color4.Red, c => applied = c));
            AddUntilStep("modal is shown", () => overlay.State.Value == Visibility.Visible);

            AddStep("pick green, then cancel", () =>
            {
                overlay.Picker.Current.Value = Color4.Lime;
                overlay.CancelButton.TriggerClick();
            });

            AddAssert("nothing applied", () => applied == null);
            AddUntilStep("modal closed", () => overlay.State.Value == Visibility.Hidden);
        }
    }
}
