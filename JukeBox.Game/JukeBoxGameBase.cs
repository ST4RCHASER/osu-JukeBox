using System.Net.Http;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Resources;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.IO.Stores;
using osuTK;

namespace JukeBox.Game
{
    public partial class JukeBoxGameBase : osu.Framework.Game
    {
        // Anything in this class is shared between the test browser and the game implementation.
        // It allows for caching global dependencies that should be accessible to tests, or changing
        // the screen scaling for all components including the test browser and framework overlays.

        protected override Container<Drawable> Content { get; }

        private DependencyContainer dependencies = null!;

        private readonly HttpClient http = new HttpClient();

        private PlaybackController playback = null!;
        private Jukebox jukebox = null!;

        protected JukeBoxGameBase()
        {
            // Ensure game and tests scale with window size and screen DPI.
            base.Content.Add(Content = new DrawSizePreservingFillContainer
            {
                // You may want to change TargetDrawSize to your "default" resolution, which will decide how things scale and position when using absolute coordinates.
                TargetDrawSize = new Vector2(1366, 768)
            });
        }

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
            => dependencies = new DependencyContainer(base.CreateChildDependencies(parent));

        [BackgroundDependencyLoader]
        private void load()
        {
            Resources.AddStore(new DllResourceStore(typeof(JukeBoxResources).Assembly));

            var mirror = new MirrorChain(new NerinyanMirror(http), new CatboyMirror(http), new OsuDirectMirror(http));
            dependencies.CacheAs<IBeatmapMirror>(mirror);

            var config = new JukeBoxConfigManager(Host.Storage);
            dependencies.Cache(config);

            var cache = new BeatmapCache(Host.Storage.GetFullPath("cache"), mirror, config.Get<bool>(JukeBoxSetting.NoVideoDownloads));
            dependencies.Cache(cache);

            var queue = new MusicQueue();
            dependencies.Cache(queue);

            var radio = new RadioService(mirror);
            dependencies.Cache(radio);

            // Game.AddInternal is sealed to throw ("Use Add or Content instead") — Add routes
            // through the overridden Content property (the DPI-scaling container from the
            // constructor) instead.
            Add(playback = new PlaybackController());
            dependencies.Cache(playback);

            Add(jukebox = new Jukebox(queue, radio, cache, playback));
            dependencies.Cache(jukebox);

            config.BindWith(JukeBoxSetting.Volume, playback.Volume);

            // CacheSizeGb -> bytes: startup value only (eviction runs once per advance round, so
            // a live-updating bindable isn't worth the extra wiring here).
            jukebox.CacheLimitBytes = (long)(config.Get<double>(JukeBoxSetting.CacheSizeGb) * 1024 * 1024 * 1024);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            http.Dispose();
        }
    }
}
