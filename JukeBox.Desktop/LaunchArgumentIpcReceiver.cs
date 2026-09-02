#nullable enable

using JukeBox.Game.Import;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Platform;

namespace JukeBox.Desktop
{
    /// <summary>
    /// Listens for a second launch handing over its command-line arguments, and feeds them to the
    /// importer that the running instance already owns.
    ///
    /// <para>
    /// Lives in JukeBox.Desktop, not JukeBox.Game, for the same reason rich presence does (see
    /// <see cref="JukeBoxDesktopGame"/>): JukeBox.Game.Tests references JukeBox.Game only, and two
    /// fixtures construct a real game under a headless host. Wiring a live IPC channel where those
    /// can reach it is precisely the mistake that once took the suite down mid-fixture. Here it is
    /// impossible by construction rather than by care — the ARGUMENT HANDLING is fully testable in
    /// JukeBox.Game; only the pipe is not.
    /// </para>
    /// </summary>
    internal partial class LaunchArgumentIpcReceiver : Component
    {
        private readonly string[] initial;

        /// <param name="initial">This process's own argv, handled once at startup.</param>
        public LaunchArgumentIpcReceiver(string[] initial)
        {
            this.initial = initial;
        }

        [Resolved]
        private GameHost host { get; set; } = null!;

        [Resolved]
        private LaunchArgumentImporter arguments { get; set; } = null!;

        private IpcChannel<LaunchArgumentMessage>? channel;

        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (initial.Length > 0)
                Schedule(() => _ = arguments.HandleAsync(initial));

            channel = new IpcChannel<LaunchArgumentMessage>(host);

            channel.MessageReceived += message =>
            {
                // Arrives on the named pipe's own listener thread. Everything downstream reaches
                // the queue, which is update-thread-only, so it is scheduled rather than run here.
                Schedule(() => _ = arguments.HandleAsync(message.Arguments));

                // Fire-and-forget: the sending process has already exited by the time the batch
                // finishes, so there is nothing meaningful to answer it with.
                return null;
            };
        }

        protected override void Dispose(bool isDisposing)
        {
            channel?.Dispose();
            base.Dispose(isDisposing);
        }
    }
}
