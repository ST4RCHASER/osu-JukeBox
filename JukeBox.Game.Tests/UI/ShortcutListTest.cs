#nullable enable

using System.Linq;
using JukeBox.Game.Screens;
using NUnit.Framework;

namespace JukeBox.Game.Tests.UI
{
    /// <summary>
    /// Help → Shortcuts speaks each platform's own key language: Cmd is a Mac key, so the Mac rows
    /// say Cmd where the binding uses it and every other platform's rows say what actually works
    /// there (Alt for zoom — the binding accepts Cmd OR Alt everywhere — and Ctrl+O for Open).
    /// </summary>
    [TestFixture]
    public class ShortcutListTest
    {
        [Test]
        public void TheMacListUsesCmdWhereTheMacBindingDoes()
        {
            var rows = MainScreen.ShortcutList(true);

            Assert.That(rows.Select(r => r.Keys), Does.Contain("Cmd + O"));
            Assert.That(rows.Select(r => r.Keys), Does.Contain("Cmd/Alt + = / -"));
            Assert.That(rows.Select(r => r.Keys), Does.Contain("Cmd/Alt + 0"));
        }

        [Test]
        public void OtherPlatformsNeverMentionCmd()
        {
            var rows = MainScreen.ShortcutList(false);

            Assert.That(rows.Select(r => r.Keys), Does.Contain("Ctrl + O"));
            Assert.That(rows.Select(r => r.Keys), Does.Contain("Alt + = / -"));
            Assert.That(rows.Select(r => r.Keys), Does.Contain("Alt + 0"));
            Assert.That(rows.Any(r => r.Keys.Contains("Cmd")), Is.False, "Cmd is a Mac key");
        }

        [Test]
        public void ThePlatformNeutralRowsAreIdenticalEverywhere()
        {
            var mac = MainScreen.ShortcutList(true);
            var other = MainScreen.ShortcutList(false);

            Assert.That(mac.Count, Is.EqualTo(other.Count));
            Assert.That(mac.Select(r => r.Action), Is.EqualTo(other.Select(r => r.Action)), "the ACTIONS are the same everywhere; only key labels differ");
        }
    }
}
