using System;
using System.Linq;
using osu.Framework.Platform;
using osu.Framework;
using osu.Framework.Extensions;
using JukeBox.Game;
using JukeBox.Game.Import;

namespace JukeBox.Desktop
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            // The detached player window is a second process of this same binary (spawned by
            // DetachedViewerManager with our stdin redirected). A separate host name gives it
            // its own config/realm/log storage — nothing contends with the main instance's
            // files; sync state arrives as JSON lines on stdin. Both windows are titled from
            // JukeBoxHost (see there for why the titlebar name and the storage name differ).
            bool viewer = args.Contains("--viewer");

            using (GameHost host = Host.GetSuitableDesktopHost(JukeBoxHost.HostNameFor(viewer), JukeBoxHost.OptionsFor(viewer)))
            {
                // Single instance. Reading IsPrimaryInstance is what actually attempts the bind
                // (DesktopGameHost binds lazily, on first read), so this both asks the question
                // and claims the pipe when the answer is yes.
                //
                // The viewer is exempt: it runs with no pipe at all, so it is always "primary" by
                // construction and can never be mistaken for the instance to forward to.
                if (!viewer && !host.IsPrimaryInstance)
                {
                    forwardToRunningInstance(host, args);
                    return;
                }

                using (osu.Framework.Game game = viewer ? new JukeBoxViewerGame(Console.In) : new JukeBoxDesktopGame(args))
                    host.Run(game);
            }
        }

        /// <summary>
        /// Hands this launch's arguments to the instance already running, then returns — so the
        /// app the user is already looking at is the one that acts on them, and no second process
        /// ever opens the same storage.
        ///
        /// <para>
        /// The whole batch goes as ONE message. Order is the point (arguments queue in the order
        /// they were typed), and N separate messages would race down the pipe to arrive in
        /// whatever order the listener happened to accept them.
        /// </para>
        ///
        /// <para>
        /// A second launch with NO arguments still exits without sending anything: there is
        /// nothing to forward, and booting a rival app over the same realm and config is exactly
        /// what this path exists to prevent.
        /// </para>
        /// </summary>
        private static void forwardToRunningInstance(GameHost host, string[] args)
        {
            string[] content = args.Where(a => !LaunchArgument.IsSwitch(a)).ToArray();

            if (content.Length == 0)
                return;

            using (var channel = new IpcChannel<LaunchArgumentMessage>(host))
                channel.SendMessageAsync(new LaunchArgumentMessage { Arguments = content }).WaitSafely();
        }
    }
}
