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
    }
}
