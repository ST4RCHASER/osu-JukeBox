using System;
using System.Linq;
using osu.Framework.Platform;
using osu.Framework;
using JukeBox.Game;

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
            using (osu.Framework.Game game = viewer ? new JukeBoxViewerGame(Console.In) : new JukeBoxGame())
                host.Run(game);
        }
    }
}
