using osu.Framework.Platform;
using osu.Framework;
using JukeBox.Game;

namespace JukeBox.Desktop
{
    public static class Program
    {
        public static void Main()
        {
            using (GameHost host = Host.GetSuitableDesktopHost(@"JukeBox"))
            using (osu.Framework.Game game = new JukeBoxGame())
                host.Run(game);
        }
    }
}
