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
            // files; sync state arrives as JSON lines on stdin.
            if (args.Contains("--viewer"))
            {
                using (GameHost host = Host.GetSuitableDesktopHost(@"JukeBox-Viewer"))
                using (osu.Framework.Game game = new JukeBoxViewerGame(Console.In))
                    host.Run(game);

                return;
            }

            using (GameHost host = Host.GetSuitableDesktopHost(@"JukeBox"))
            using (osu.Framework.Game game = new JukeBoxGame())
                host.Run(game);
        }
    }
}
