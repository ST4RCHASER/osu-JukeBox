using System.Linq;
using JukeBox.Game.Import;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Import
{
    /// <summary>
    /// File → Open…'s native multi-select dialog: the AppleScript it drives and how its answer is read
    /// back. The dialog itself is the OS's; what is ours (and testable) is asking for multi-select and
    /// turning the panel's line-per-file answer into paths in order.
    /// </summary>
    [TestFixture]
    public class NativeOpenDialogTest
    {
        [Test]
        public void TheScriptAsksForMultipleSelections()
        {
            var script = NativeOpenDialog.Script(null).ToList();

            Assert.That(script[0], Does.Contain("choose file"));
            Assert.That(script[0], Does.Contain("with multiple selections allowed"));
            // Every chosen file is echoed back as a POSIX path, one per line.
            Assert.That(script, Has.Some.Contains("POSIX path of f"));
        }

        [Test]
        public void TheScriptStartsInTheGivenFolderOnlyWhenItExists()
        {
            string existing = System.IO.Path.GetTempPath();

            Assert.That(NativeOpenDialog.Script(existing).First(), Does.Contain("default location POSIX file"));
            Assert.That(NativeOpenDialog.Script("/definitely/not/here").First(), Does.Not.Contain("default location"));
            Assert.That(NativeOpenDialog.Script(null).First(), Does.Not.Contain("default location"));
        }

        [Test]
        public void ThePanelsAnswerReadsAsOnePathPerLineInOrder()
        {
            var paths = NativeOpenDialog.ParseOutput("/a/first.osr\n/b/second.osz\n\n/c/third.osk\n");

            Assert.That(paths, Is.EqualTo(new[] { "/a/first.osr", "/b/second.osz", "/c/third.osk" }));
        }

        [Test]
        public void AnEmptyAnswerIsNoFiles()
        {
            Assert.That(NativeOpenDialog.ParseOutput(string.Empty), Is.Empty);
            Assert.That(NativeOpenDialog.ParseOutput("\n"), Is.Empty);
        }

        [Test]
        public void EachDesktopPlatformDrivesItsOwnMultiSelectDialog()
        {
            var mac = NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.macOS, null, null);
            Assert.That(mac!.FileName, Is.EqualTo("osascript"));
            Assert.That(mac.Arguments, Has.Some.Contains("choose file"));

            var windows = NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Windows, null, null);
            Assert.That(windows!.FileName, Is.EqualTo("powershell"));
            // -STA because WinForms dialogs refuse to show on an MTA thread.
            Assert.That(windows.Arguments, Does.Contain("-STA"));
            string script = windows.Arguments.Last();
            Assert.That(script, Does.Contain("OpenFileDialog"));
            Assert.That(script, Does.Contain("$d.Multiselect = $true"));
            Assert.That(script, Does.Contain("$d.FileNames"));

            var zenity = NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Linux, null, "zenity");
            Assert.That(zenity!.FileName, Is.EqualTo("zenity"));
            Assert.That(zenity.Arguments, Does.Contain("--file-selection"));
            Assert.That(zenity.Arguments, Does.Contain("--multiple"));
            // One path per line, so ParseOutput stays shared with the other platforms.
            Assert.That(zenity.Arguments, Does.Contain("--separator=\n"));

            var kdialog = NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Linux, null, "kdialog");
            Assert.That(kdialog!.FileName, Is.EqualTo("kdialog"));
            Assert.That(kdialog.Arguments, Does.Contain("--getopenfilename"));
            Assert.That(kdialog.Arguments, Does.Contain("--multiple"));
            Assert.That(kdialog.Arguments, Does.Contain("--separate-output"));
        }

        [Test]
        public void APlatformWithNoDialogBuildsNoCommand()
        {
            Assert.That(NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Linux, null, null), Is.Null, "a Linux desktop with neither zenity nor kdialog has no dialog");
            Assert.That(NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.iOS, null, null), Is.Null);
        }

        [Test]
        public void TheStartingFolderIsSeededOnlyWhenItExistsOnEveryPlatform()
        {
            string existing = System.IO.Path.GetTempPath();

            Assert.That(NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Windows, existing, null)!.Arguments.Last(), Does.Contain("$d.InitialDirectory = '"));
            Assert.That(NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Windows, "/definitely/not/here", null)!.Arguments.Last(), Does.Not.Contain("InitialDirectory"));

            Assert.That(NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Linux, existing, "zenity")!.Arguments, Has.Some.StartsWith("--filename="));
            Assert.That(NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Linux, "/definitely/not/here", "zenity")!.Arguments, Has.None.StartsWith("--filename="));
        }

        [Test]
        public void AWindowsFolderWithAQuoteIsEscapedForPowerShell()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jukebox-quote-'-test");
            System.IO.Directory.CreateDirectory(dir);

            try
            {
                string script = NativeOpenDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Windows, dir, null)!.Arguments.Last();
                Assert.That(script, Does.Contain("jukebox-quote-''-test"));
            }
            finally
            {
                System.IO.Directory.Delete(dir);
            }
        }
    }
}
