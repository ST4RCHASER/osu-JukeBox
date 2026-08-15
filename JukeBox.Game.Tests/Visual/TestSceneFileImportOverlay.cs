#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.Import;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Graphics.UserInterfaceV2;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Covers the in-app file picker (<see cref="FileImportOverlay"/>): it offers exactly the
    /// formats the drop importer can take, hands the chosen path to its host and closes, cancels
    /// without importing, and remembers where it was last browsing.
    /// </summary>
    [TestFixture]
    public partial class TestSceneFileImportOverlay : JukeBoxManualInputTestScene
    {
        private FileImportOverlay overlay = null!;
        private JukeBoxConfigManager config = null!;
        private string tempRoot = null!;
        private readonly List<string> selected = new List<string>();

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var deps = new DependencyContainer(parent);
            deps.Cache(config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-file-import-test", Path.GetRandomFileName()))));
            return deps;
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create overlay", () =>
            {
                selected.Clear();
                tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(tempRoot);
                config.SetValue(JukeBoxSetting.LastImportDirectory, string.Empty);

                Child = overlay = new FileImportOverlay();
                overlay.FileSelected += path => selected.Add(path);
            });
        }

        /// <summary>
        /// Drives a real selection the way a click in the list does — through the selector's own
        /// <c>CurrentFile</c> — rather than simulating the overlay's reaction. Takes a factory
        /// rather than a path because steps are BUILT before any of them run, so a path produced
        /// by an earlier step is still null at build time.
        /// </summary>
        private void choose(Func<string> path) => AddStep("choose the file", () =>
            overlay.Selector.CurrentFile.Value = new FileInfo(path()));

        // The picker must never offer a file the importer would only turn around and reject, and
        // must offer every format it accepts — so the filter is asserted against the classifier
        // itself rather than against a copy of the list.
        [Test]
        public void PickerOffersExactlyWhatTheImporterAccepts()
        {
            AddAssert("every offered extension is importable", () =>
                FileImportOverlay.SupportedExtensions.All(e => DroppedFile.Classify("file" + e) != DroppedFileKind.Unsupported));

            AddAssert("every importable kind is offered", () =>
                FileImportOverlay.SupportedExtensions
                                 .Select(e => DroppedFile.Classify("file" + e))
                                 .Distinct()
                                 .Count() == Enum.GetValues<DroppedFileKind>().Length - 1);

            AddAssert("the three osu! extensions specifically", () =>
                FileImportOverlay.SupportedExtensions.SequenceEqual(new[] { ".osz", ".osk", ".osr" }));

            AddAssert("the selector was built (it is what applies the filter)", () =>
                overlay.ChildrenOfType<OsuFileSelector>().Count() == 1);
        }

        [Test]
        public void ChoosingAFileHandsOverThePathAndCloses()
        {
            string file = null!;
            AddStep("create a .osz in the temp tree", () =>
            {
                file = Path.Combine(tempRoot, "song.osz");
                File.WriteAllText(file, "not really a zip, but the picker doesn't care");
            });

            AddStep("show overlay", () => overlay.Show());
            choose(() => file);

            AddUntilStep("path handed over exactly once", () => selected.Count == 1);
            AddAssert("and it is the full path of the chosen file", () => selected[0] == file);
            AddUntilStep("overlay closed itself", () => overlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void CancelClosesWithoutImporting()
        {
            AddStep("show overlay", () => overlay.Show());
            AddStep("click Cancel", () => overlay.CancelButton.TriggerClick());

            AddUntilStep("overlay hidden", () => overlay.State.Value == Visibility.Hidden);
            AddAssert("nothing was handed over", () => selected.Count == 0);
        }

        [Test]
        public void EscapeCancelsTheSameWayTheButtonDoes()
        {
            AddStep("show overlay", () => overlay.Show());
            AddStep("press escape", () => InputManager.Key(Key.Escape));

            AddUntilStep("overlay hidden", () => overlay.State.Value == Visibility.Hidden);
            AddAssert("nothing was handed over", () => selected.Count == 0);
        }

        // Browsing is remembered even when the user backs out, so reopening lands where they left
        // off rather than back at the default.
        [Test]
        public void LastBrowsedDirectoryPersistsAcrossReopening()
        {
            AddStep("show overlay", () => overlay.Show());
            AddStep("browse into the temp tree", () => overlay.Selector.CurrentPath.Value = new DirectoryInfo(tempRoot));

            AddUntilStep("directory written to config", () => config.Get<string>(JukeBoxSetting.LastImportDirectory) == tempRoot);

            AddStep("cancel out", () => overlay.CancelButton.TriggerClick());
            AddAssert("cancelling did not forget it", () => config.Get<string>(JukeBoxSetting.LastImportDirectory) == tempRoot);

            // A fresh instance stands in for the next launch. It must be SHOWN to settle: the
            // selector reads its starting directory in LoadComplete, which a hidden (non-present)
            // parent defers until its first visible frame.
            FileImportOverlay reopened = null!;
            AddStep("build and show a fresh overlay", () =>
            {
                Child = reopened = new FileImportOverlay();
                reopened.Show();
            });
            AddUntilStep("it opens where we left off", () => reopened.Selector.CurrentPath.Value?.FullName == tempRoot);
        }

        // The fallback chain has to survive a remembered directory that no longer exists — the
        // picker must always land somewhere real.
        [Test]
        public void InitialPathFallsBackWhenTheRememberedDirectoryIsGone()
        {
            AddAssert("a live remembered directory wins", () => FileImportOverlay.ResolveInitialPath(tempRoot) == tempRoot);

            AddAssert("a deleted one falls through to something that exists", () =>
            {
                string gone = Path.Combine(Path.GetTempPath(), "jukebox-definitely-not-here-" + Guid.NewGuid());
                return Directory.Exists(FileImportOverlay.ResolveInitialPath(gone));
            });

            AddAssert("so does no memory at all", () => Directory.Exists(FileImportOverlay.ResolveInitialPath(string.Empty)));
            AddAssert("and null", () => Directory.Exists(FileImportOverlay.ResolveInitialPath(null)));

            AddAssert("Downloads is preferred over home when it exists", () =>
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloads = Path.Combine(home, "Downloads");
                return FileImportOverlay.ResolveInitialPath(null) == (Directory.Exists(downloads) ? downloads : home);
            });
        }
    }
}
