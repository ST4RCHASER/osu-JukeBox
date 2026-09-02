using JukeBox.Game;
using JukeBox.Game.Import;
using JukeBox.Game.Presence;

namespace JukeBox.Desktop
{
    /// <summary>
    /// The game as the real desktop app runs it: <see cref="JukeBoxGame"/> plus the integrations
    /// that only make sense when a person is actually sitting in front of a window.
    ///
    /// <para>
    /// This class exists for one reason: rich presence must be unreachable from the test project.
    /// <see cref="JukeBoxGame"/> is NOT a safe home for it — two fixtures
    /// (<c>TestSceneJukeBoxGame</c> and <c>TestSceneRealVirtualAudioSet</c>) construct a real
    /// <see cref="JukeBoxGame"/> under a headless host, so presence wired there opened a genuine
    /// Discord IPC connection during the suite and took the host down mid-fixture with cancelled
    /// IO. JukeBox.Game.Tests references JukeBox.Game and not JukeBox.Desktop, so putting the
    /// wiring HERE makes that impossible by construction rather than by care.
    /// </para>
    ///
    /// <para>
    /// It is also where osu!lazer puts the same thing, and why lazer's own tests never see it:
    /// <c>osu.Desktop</c>'s desktop game subclass wires <c>DiscordRichPresence</c>, not
    /// <c>OsuGame</c>.
    /// </para>
    /// </summary>
    public partial class JukeBoxDesktopGame : JukeBoxGame
    {
        private readonly string[] launchArguments;

        /// <param name="launchArguments">
        /// This process's own argv. Required rather than optional: the entry point always has it,
        /// and a defaulted parameter would quietly give a future caller an app that ignores the
        /// command line.
        /// </param>
        public JukeBoxDesktopGame(string[] launchArguments)
        {
            this.launchArguments = launchArguments;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Main process only: the detached viewer runs JukeBoxViewerGame, so it never reaches
            // this, and the two windows can't fight over the user's single Discord activity slot.
            Add(new DiscordPresenceService());

            // Arguments arrive from two places and both land in the same importer: this process's
            // own argv, and any LATER launch's argv forwarded over the pipe. One component owns
            // both, and both are desktop-only concerns — see LaunchArgumentIpcReceiver.
            Add(new LaunchArgumentIpcReceiver(launchArguments));
        }
    }
}
