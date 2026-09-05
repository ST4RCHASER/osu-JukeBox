#nullable enable

using System.IO;
using JukeBox.Game.UI.Render;
using NUnit.Framework;
using osu.Framework.Graphics.Containers;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The render dialog's Browse… button opens the IN-APP save browser (the one Browse behaviour
    /// on every platform): picking a folder and naming the file lands the combined path in the
    /// save-location field through the same validation typing takes, and a cancel changes nothing.
    /// </summary>
    [TestFixture]
    public partial class TestSceneRenderDialogBrowse : JukeBoxTestScene
    {
        private RenderDialog dialog = null!;
        private string startDir = null!;
        private string pickedDir = null!;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            startDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            pickedDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(startDir);
            Directory.CreateDirectory(pickedDir);

            Clear();
            Add(dialog = new RenderDialog());
            dialog.Open(120_000, startDir, "my render");
        });

        [TearDown]
        public void TearDown()
        {
            foreach (string dir in new[] { startDir, pickedDir })
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [Test]
        public void BrowseOpensTheInAppBrowserSeededFromTheCurrentPath()
        {
            AddStep("click Browse…", () => dialog.BrowseButton.TriggerClick());

            AddUntilStep("the in-app browser is shown", () => dialog.SaveOverlay.State.Value == Visibility.Visible);
            AddUntilStep("seeded with the current folder", () =>
                dialog.SaveOverlay.Directories.CurrentPath.Value?.FullName.TrimEnd(Path.DirectorySeparatorChar) == startDir.TrimEnd(Path.DirectorySeparatorChar));
            AddAssert("and the current file name", () => dialog.SaveOverlay.NameBox.Text == "my render.mp4");
        }

        [Test]
        public void PickingAFolderAndANameLandsTheValidatedPath()
        {
            AddStep("click Browse…", () => dialog.BrowseButton.TriggerClick());
            AddUntilStep("browser shown", () => dialog.SaveOverlay.State.Value == Visibility.Visible);

            AddStep("pick another folder and name the file", () =>
            {
                dialog.SaveOverlay.Directories.CurrentPath.Value = new DirectoryInfo(pickedDir);
                dialog.SaveOverlay.NameBox.Text = "picked.mp4";
            });
            AddStep("Save here", () => dialog.SaveOverlay.SaveButton.TriggerClick());

            AddAssert("the combined path landed in the save field", () => dialog.PathBox.Text == Path.Combine(pickedDir, "picked.mp4"));
            AddAssert("validation ran on it — Render is enabled", () => dialog.RenderButton.Enabled.Value);
            AddUntilStep("and the browser closed", () => dialog.SaveOverlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void CancellingTheBrowserChangesNothing()
        {
            string before = null!;

            AddStep("note the current path and open Browse…", () =>
            {
                before = dialog.PathBox.Text;
                dialog.BrowseButton.TriggerClick();
            });
            AddUntilStep("browser shown", () => dialog.SaveOverlay.State.Value == Visibility.Visible);

            AddStep("wander somewhere else, then Cancel", () =>
            {
                dialog.SaveOverlay.Directories.CurrentPath.Value = new DirectoryInfo(pickedDir);
                dialog.SaveOverlay.NameBox.Text = "changed-my-mind.mp4";
                dialog.SaveOverlay.CancelButton.TriggerClick();
            });

            AddAssert("the save field is untouched", () => dialog.PathBox.Text == before);
            AddUntilStep("and the browser closed", () => dialog.SaveOverlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void AnEmptyNameDoesNotConfirm()
        {
            AddStep("click Browse…", () => dialog.BrowseButton.TriggerClick());
            AddUntilStep("browser shown", () => dialog.SaveOverlay.State.Value == Visibility.Visible);

            AddStep("clear the name and try to save", () =>
            {
                dialog.SaveOverlay.NameBox.Text = "  ";
                dialog.SaveOverlay.SaveButton.TriggerClick();
            });

            AddAssert("the browser stays open for a name to be typed", () => dialog.SaveOverlay.State.Value == Visibility.Visible);
        }
    }
}
