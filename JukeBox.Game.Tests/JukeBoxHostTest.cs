#nullable enable

using NUnit.Framework;

namespace JukeBox.Game.Tests
{
    // The OS window used to be titled `osu!framework (running "JukeBox")` — osu!framework's own
    // placeholder, which it fills HostOptions.FriendlyGameName with when the game doesn't set one.
    [TestFixture]
    public class JukeBoxHostTest
    {
        [Test]
        public void MainWindowIsTitledWithTheProductName()
        {
            Assert.That(JukeBoxHost.OptionsFor(viewer: false).FriendlyGameName, Is.EqualTo("osu!JukeBox"));
        }

        [Test]
        public void ViewerWindowIsTitledAsThePlayer()
        {
            Assert.That(JukeBoxHost.OptionsFor(viewer: true).FriendlyGameName, Is.EqualTo("osu!JukeBox — Player"));
        }

        [Test]
        public void NeitherWindowFallsBackToTheFrameworkPlaceholder()
        {
            Assert.That(JukeBoxHost.OptionsFor(viewer: false).FriendlyGameName, Does.Not.Contain("osu!framework"));
            Assert.That(JukeBoxHost.OptionsFor(viewer: true).FriendlyGameName, Does.Not.Contain("osu!framework"));
        }

        // The storage identity is a SEPARATE thing from the titlebar name and must not follow it:
        // GameHost picks the config/realm/log directory off the host name, so renaming it would
        // strand every existing install's settings in an orphaned folder.
        [Test]
        public void HostNamesAreUnchangedSoExistingStorageStillResolves()
        {
            Assert.That(JukeBoxHost.HostNameFor(viewer: false), Is.EqualTo("JukeBox"));
            Assert.That(JukeBoxHost.HostNameFor(viewer: true), Is.EqualTo("JukeBox-Viewer"));
            Assert.That(JukeBoxHost.HostNameFor(viewer: false), Is.Not.EqualTo(JukeBoxHost.HostNameFor(viewer: true)),
                "the detached player needs its own storage so it never contends with the main instance");
        }
    }
}
