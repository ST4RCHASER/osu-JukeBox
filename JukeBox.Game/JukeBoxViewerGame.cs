#nullable enable

using System.IO;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;

namespace JukeBox.Game
{
    /// <summary>
    /// The slim game run by the detached "--viewer" process: the full <see cref="JukeBoxGameBase"/>
    /// dependency stack (the lazer renderers need all of it) but no jukebox UI and no audio —
    /// just a <see cref="ViewerScreen"/> mirroring the main process. Runs under its own host and
    /// storage name (see JukeBox.Desktop's Program), so its config/realm/logs never contend with
    /// the main app's files; the only shared paths are the beatmap cache directories the sync
    /// states point at, which are read-only here. The Jukebox component the base wires stays
    /// dormant — only MainScreen calls Jukebox.Start(), and there is no MainScreen here.
    /// </summary>
    public partial class JukeBoxViewerGame : JukeBoxGameBase
    {
        private readonly TextReader input;
        private ScreenStack screenStack = null!;

        /// <param name="input">The sync-state line stream — stdin in the real viewer process;
        /// tests can substitute any reader.</param>
        public JukeBoxViewerGame(TextReader input)
        {
            this.input = input;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Add(screenStack = new ScreenStack { RelativeSizeAxes = Axes.Both });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (Host.Window != null)
                Host.Window.Title = "osu!JukeBox — Player";

            // Audio lives in the main process only. The viewer never starts a track, but
            // BeatmapVisuals carries lazer-native audio (storyboard keysounds, chart hitsounds)
            // routed through PlaybackController.Volume — zero it at that single choke point.
            dependencies.Get<PlaybackController>().Volume.Value = 0;

            screenStack.Push(new ViewerScreen(input));
        }
    }
}
