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

        /// <summary>
        /// The named pipe the running instance binds so a SECOND launch can hand over its
        /// command-line arguments instead of booting a rival app. Two instances on one storage
        /// directory is not a cosmetic problem — they would share a realm and a config file — so
        /// binding this is what makes "click a link, it lands in the player already open" work at
        /// all. See JukeBox.Desktop's Program.
        ///
        /// <para>
        /// A THIRD name, deliberately separate from the two storage names above: the pipe is an
        /// identity for "who owns this desktop session", and tying it to a storage directory would
        /// tempt someone into renaming it along with one.
        /// </para>
        /// </summary>
        public const string IPC_PIPE_NAME = @"JukeBox-args";

        public static string HostNameFor(bool viewer) => viewer ? VIEWER_HOST_NAME : MAIN_HOST_NAME;

        public static HostOptions OptionsFor(bool viewer) => new HostOptions
        {
            FriendlyGameName = viewer ? VIEWER_WINDOW_TITLE : PRODUCT_NAME,

            // The viewer gets NO pipe. It is a legitimate second process of this same binary
            // (spawned by DetachedViewerManager), so a viewer that bound the pipe would both make
            // the next real launch believe an instance was already running and swallow its
            // arguments into a window that cannot queue anything.
            IPCPipeName = viewer ? null : IPC_PIPE_NAME,
        };
    }
}
