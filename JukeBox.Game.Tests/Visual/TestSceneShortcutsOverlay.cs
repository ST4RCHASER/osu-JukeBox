#nullable enable

using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The Help → "Show all shortcut keys" modal: it lays out exactly the rows it is handed, and OK
    /// or Escape closes it — the same close contract as the app's other modals.
    /// </summary>
    [TestFixture]
    public partial class TestSceneShortcutsOverlay : JukeBoxManualInputTestScene
    {
        private static readonly IReadOnlyList<(string Keys, string Action)> shortcuts = new[]
        {
            ("Space", "Play / pause"),
            ("Home", "Restart"),
            ("Ctrl+O", "Open files"),
        };

        private ShortcutsOverlay overlay = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create overlay", () => Child = overlay = new ShortcutsOverlay(shortcuts));
            AddUntilStep("loaded", () => overlay.IsLoaded);
            AddStep("show", () => overlay.Show());
        }

        [Test]
        public void ListsEveryInjectedShortcut()
        {
            AddAssert("every action is shown", () =>
            {
                var shown = overlay.ChildrenOfType<SpriteText>().Select(t => t.Text.ToString()).ToList();
                return shortcuts.All(s => shown.Contains(s.Action) && shown.Contains(s.Keys));
            });
        }

        [Test]
        public void OkClosesIt()
        {
            AddStep("click OK", () => overlay.OkButton.TriggerClick());
            AddUntilStep("hidden", () => overlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void EscapeClosesItTheSameWay()
        {
            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddUntilStep("hidden", () => overlay.State.Value == Visibility.Hidden);
        }
    }
}
