#nullable enable

using osu.Framework;

namespace JukeBox.Game
{
    /// <summary>
    /// The names the two desktop processes run under. Two DIFFERENT things live here and must not
    /// be conflated:
    ///
    /// <list type="bullet">
    /// <item>The <b>host name</b> (<see cref="MAIN_HOST_NAME"/>/<see cref="VIEWER_HOST_NAME"/>) is
    /// the storage identity — <c>GameHost.Name</c> picks the config/realm/log directory off it. It
    /// is deliberately unchanged and must stay that way: renaming it would strand every existing
    /// user's settings and lazer realm in an orphaned folder.</item>
    /// <item>The <b>friendly name</b> (<see cref="PRODUCT_NAME"/>) is what the OS window titlebar
    /// shows. Left unset, osu!framework fills it in as <c>osu!framework (running "JukeBox")</c> —
    /// its own placeholder, not a product name (see <c>GameHost</c>'s constructor).</item>
    /// </list>
    /// </summary>
    public static class JukeBoxHost
    {
        public const string PRODUCT_NAME = "osu!JukeBox";

        /// <summary>The detached player process's titlebar, distinguishing the second window from
        /// the main one when both are on screen.</summary>
        public const string VIEWER_WINDOW_TITLE = PRODUCT_NAME + " — Player";

        public const string MAIN_HOST_NAME = @"JukeBox";

        /// <summary>The detached player runs under its own storage name so nothing contends with
        /// the main instance's config/realm/log files — see JukeBox.Desktop's Program.</summary>
        public const string VIEWER_HOST_NAME = @"JukeBox-Viewer";

        public static string HostNameFor(bool viewer) => viewer ? VIEWER_HOST_NAME : MAIN_HOST_NAME;

        public static HostOptions OptionsFor(bool viewer) => new HostOptions
        {
            FriendlyGameName = viewer ? VIEWER_WINDOW_TITLE : PRODUCT_NAME,
        };
    }
}
