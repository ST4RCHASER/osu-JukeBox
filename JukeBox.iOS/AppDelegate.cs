using osu.Framework.iOS;
using JukeBox.Game;

namespace JukeBox.iOS
{
    /// <inheritdoc />
    public class AppDelegate : GameApplicationDelegate
    {
        /// <inheritdoc />
        protected override osu.Framework.Game CreateGame() => new JukeBoxGame();
    }
}
