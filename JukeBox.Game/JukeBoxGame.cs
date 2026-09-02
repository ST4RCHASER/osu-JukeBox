using JukeBox.Game.Detach;
using JukeBox.Game.Online;
using JukeBox.Game.Screens;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Textures;
using osu.Framework.Screens;

namespace JukeBox.Game
{
    public partial class JukeBoxGame : JukeBoxGameBase
    {
        private ScreenStack screenStack;

        [BackgroundDependencyLoader]
        private void load()
        {
            // Add your top-level game components here.
            // IMPORTANT: use Add, not `Child =` — the Child setter clears and DISPOSES everything
            // JukeBoxGameBase.load() already added to Content (PlaybackController, Jukebox), which
            // silently kills playback: their Schedule callbacks never run, so PlayAsync never
            // completes and the jukebox wedges with nothing playing.
            Add(screenStack = new ScreenStack { RelativeSizeAxes = Axes.Both });

            // Wired here rather than in JukeBoxGameBase.load() so no test host ever tries to
            // spawn a real OS process off the DetachPlayer setting.
            Add(new DetachedViewerManager());

            // Discord rich presence is deliberately NOT wired here — it lives in
            // JukeBox.Desktop's JukeBoxDesktopGame. DetachedViewerManager's "no test host does
            // this" reasoning does not reach far enough for it: TestSceneJukeBoxGame and
            // TestSceneRealVirtualAudioSet both AddGame(new JukeBoxGame()), so anything wired in
            // THIS class still runs under a headless test host. Presence opening its real IPC
            // connection there took the host down mid-fixture. See JukeBoxDesktopGame.

            // Single online TextureStore for beatmap set cover thumbnails
            // (https://b.ppy.sh/thumb/{setId}l.jpg), shared by every BeatmapCard/NowPlayingPanel
            // rather than one per row. CreateOnlineStore() (not CreateTextureLoaderStore's own
            // NativeStorage-backed overload) is what actually knows how to fetch a bare https://
            // URL as raw bytes. Deliberately wired here rather than in JukeBoxGameBase.load() —
            // see the field comment on `dependencies` for why.
            var thumbnailStore = new TextureStore(Host.Renderer,
                Host.CreateTextureLoaderStore(CreateOnlineStore()), useAtlas: false);
            dependencies.Cache(new OnlineThumbnailStore { Store = thumbnailStore });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            screenStack.Push(new MainScreen());
        }
    }
}
