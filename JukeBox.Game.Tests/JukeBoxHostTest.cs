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

        // The main instance binds a pipe so a SECOND launch can hand over its arguments instead of
        // booting a rival app over the same realm and config.
        [Test]
        public void TheMainWindowBindsTheArgumentPipe()
        {
            Assert.That(JukeBoxHost.OptionsFor(viewer: false).IPCPipeName, Is.EqualTo(JukeBoxHost.IPC_PIPE_NAME));
            Assert.That(JukeBoxHost.IPC_PIPE_NAME, Is.Not.Empty);
        }

        // The viewer must NOT. It is a legitimate second process of the same binary, so a viewer
        // holding the pipe would make the next real launch believe an instance was already
        // running — and swallow that launch's arguments into a window that cannot queue anything.
        [Test]
        public void TheViewerBindsNoPipeSoItIsNeverMistakenForTheRunningApp()
        {
            Assert.That(JukeBoxHost.OptionsFor(viewer: true).IPCPipeName, Is.Null);
        }

        // The pipe is an identity for "who owns this desktop session"; the host names are storage
        // directories. Keeping them distinct stops someone renaming one along with the other.
        [Test]
        public void ThePipeNameIsItsOwnIdentityNotAStorageName()
        {
            Assert.That(JukeBoxHost.IPC_PIPE_NAME, Is.Not.EqualTo(JukeBoxHost.MAIN_HOST_NAME));
            Assert.That(JukeBoxHost.IPC_PIPE_NAME, Is.Not.EqualTo(JukeBoxHost.VIEWER_HOST_NAME));
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
