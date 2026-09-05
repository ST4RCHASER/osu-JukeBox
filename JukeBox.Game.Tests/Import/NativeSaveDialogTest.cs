using System;
using System.Linq;
using JukeBox.Game.Import;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Import
{
    /// <summary>
    /// The Render dialog's Browse… save panel: the AppleScript it drives and how its answer is read
    /// back. The panel itself is the OS's; what is ours (and testable) is asking for a SAVE-style
    /// "choose file name" panel seeded with the current name/folder, and turning its one-line POSIX
    /// path answer into the save-location field's value (or a no-op on cancel).
    /// </summary>
    [TestFixture]
    public class NativeSaveDialogTest
    {
        [Test]
        public void TheScriptAsksForASaveStylePanelAndEchoesThePath()
        {
            var script = NativeSaveDialog.Script(null, null).ToList();

            // "choose file name" is the SAVE panel (a name the user may invent), not "choose file"
            // (an existing file) — the distinction is the whole point of this dialog.
            Assert.That(script[0], Does.Contain("choose file name"));
            Assert.That(script[0], Does.Not.Contain("choose file with"));
            Assert.That(script, Has.Some.Contains("POSIX path of chosen"));
        }

        [Test]
        public void TheScriptSeedsTheDefaultNameOnlyWhenGiven()
        {
            Assert.That(NativeSaveDialog.Script(null, "render.mp4").First(), Does.Contain("default name \"render.mp4\""));
            Assert.That(NativeSaveDialog.Script(null, null).First(), Does.Not.Contain("default name"));
            Assert.That(NativeSaveDialog.Script(null, "").First(), Does.Not.Contain("default name"));
        }

        [Test]
        public void TheScriptQuotesADefaultNameContainingQuotes()
        {
            Assert.That(NativeSaveDialog.Script(null, "a \"b\".mp4").First(), Does.Contain("default name \"a \\\"b\\\".mp4\""));
        }

        [Test]
        public void TheScriptStartsInTheGivenFolderOnlyWhenItExists()
        {
            string existing = System.IO.Path.GetTempPath();

            Assert.That(NativeSaveDialog.Script(existing, null).First(), Does.Contain("default location POSIX file"));
            Assert.That(NativeSaveDialog.Script("/definitely/not/here", null).First(), Does.Not.Contain("default location"));
            Assert.That(NativeSaveDialog.Script(null, null).First(), Does.Not.Contain("default location"));
        }

        [Test]
        public void ThePanelsAnswerReadsAsOneTrimmedPath()
        {
            Assert.That(NativeSaveDialog.ParseOutput("/Users/someone/Movies/render.mp4\n"), Is.EqualTo("/Users/someone/Movies/render.mp4"));
        }

        [Test]
        public void AnEmptyAnswerIsNoPath()
        {
            Assert.That(NativeSaveDialog.ParseOutput(string.Empty), Is.Null);
            Assert.That(NativeSaveDialog.ParseOutput("\n"), Is.Null);
        }

        [Test]
        public void EachDesktopPlatformDrivesItsOwnSavePanel()
        {
            var mac = NativeSaveDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.macOS, null, "render.mp4", null);
            Assert.That(mac!.FileName, Is.EqualTo("osascript"));
            Assert.That(mac.Arguments, Has.Some.Contains("choose file name"));

            var windows = NativeSaveDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Windows, null, "render.mp4", null);
            Assert.That(windows!.FileName, Is.EqualTo("powershell"));
            // -STA because WinForms dialogs refuse to show on an MTA thread.
            Assert.That(windows.Arguments, Does.Contain("-STA"));
            string script = windows.Arguments.Last();
            Assert.That(script, Does.Contain("SaveFileDialog"));
            Assert.That(script, Does.Contain("$d.FileName = 'render.mp4'"));
            Assert.That(script, Does.Contain("Write-Output $d.FileName"));

            string existing = System.IO.Path.GetTempPath();

            var zenity = NativeSaveDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Linux, existing, "render.mp4", "zenity");
            Assert.That(zenity!.FileName, Is.EqualTo("zenity"));
            Assert.That(zenity.Arguments, Does.Contain("--file-selection"));
            Assert.That(zenity.Arguments, Does.Contain("--save"));
            Assert.That(zenity.Arguments, Has.Some.Matches<string>(a => a.StartsWith("--filename=", StringComparison.Ordinal) && a.EndsWith("render.mp4", StringComparison.Ordinal)));

            var kdialog = NativeSaveDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Linux, existing, "render.mp4", "kdialog");
            Assert.That(kdialog!.FileName, Is.EqualTo("kdialog"));
            Assert.That(kdialog.Arguments, Does.Contain("--getsavefilename"));
            Assert.That(kdialog.Arguments, Has.Some.EndsWith("render.mp4"));
        }

        [Test]
        public void APlatformWithNoSavePanelBuildsNoCommand()
        {
            Assert.That(NativeSaveDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Linux, null, "render.mp4", null), Is.Null, "a Linux desktop with neither zenity nor kdialog has no panel");
            Assert.That(NativeSaveDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Android, null, "render.mp4", null), Is.Null);
        }

        [Test]
        public void AWindowsNameWithAQuoteIsEscapedForPowerShell()
        {
            string script = NativeSaveDialog.BuildCommand(osu.Framework.RuntimeInfo.Platform.Windows, null, "it's.mp4", null)!.Arguments.Last();
            Assert.That(script, Does.Contain("it''s.mp4"));
        }
    }
}
