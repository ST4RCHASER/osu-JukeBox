using System.Net.Http;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Resources;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Performance;
using osu.Framework.IO.Stores;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Rulesets;
using osuTK;

namespace JukeBox.Game
{
    public partial class JukeBoxGameBase : osu.Framework.Game
    {
        // Anything in this class is shared between the test browser and the game implementation.
        // It allows for caching global dependencies that should be accessible to tests, or changing
        // the screen scaling for all components including the test browser and framework overlays.

        protected override Container<Drawable> Content { get; }

        // protected (not private): JukeBoxGame's own [BackgroundDependencyLoader] caches the
        // real online thumbnail store here too — kept out of THIS class's load() specifically so
        // JukeBoxTestScene's test-runner (which derives from JukeBoxGameBase, not JukeBoxGame)
        // never wires up a real network-backed store, and BeatmapCard/NowPlayingBar's
        // [Resolved(canBeNull: true)] reliably resolves null across every existing test scene.
        protected DependencyContainer dependencies = null!;

        private readonly HttpClient http = new HttpClient();

        private PlaybackController playback = null!;
        private Jukebox jukebox = null!;

        // Kept as a field (rather than a load()-local) because osu.Framework's config-manager
        // bindables use a weak-reference chain back to the master value — an unrooted local would
        // be eligible for collection, silently dropping this binding. See JukeBoxSetting.ShowFps.
        private Bindable<bool> showFps = null!;

        /// <summary>
        /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the ShowFps -&gt;
        /// FrameStatisticsMode mapping used by the binding below, isolated from
        /// <see cref="osu.Framework.Game.FrameStatistics"/> itself: actually flipping that bindable
        /// activates the framework's real PerformanceOverlay, which isn't safe to run under a
        /// headless test host (crashes with a NullReferenceException — no real renderer/GPU).
        /// </summary>
        internal static FrameStatisticsMode FrameStatisticsModeFor(bool showFps)
            => showFps ? FrameStatisticsMode.Full : FrameStatisticsMode.None;

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

        // Lazer-side dependencies backing the LazerChartLayer's DrawableRuleset host — owned (and
        // disposed) here because they're game-lifetime singletons, exactly like OsuGameBase owns
        // its equivalents. The realm database only ever stores lazer-side settings (ruleset
        // configs, key bindings if any) under its own "lazer" subdirectory.
        private RealmAccess lazerRealm = null!;
        private OsuConfigManager lazerConfig = null!;
        private LazerRulesetConfigCache lazerRulesetConfigs = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            Resources.AddStore(new DllResourceStore(typeof(JukeBoxResources).Assembly));

            // osu!lazer's default skin assets + fonts (osu.Game.Resources, CC-BY-NC — see
            // docs/ATTRIBUTION.md). The gameplay renderer's skins resolve default textures and
            // hitsound samples out of this store; the fonts below are the ones lazer's in-playfield
            // text (judgements, combo counters) sets via OsuFont.
            Resources.AddStore(new DllResourceStore(osu.Game.Resources.OsuResources.ResourceAssembly));

            AddFont(Resources, @"Fonts/osuFont");
            AddFont(Resources, @"Fonts/Torus/Torus-Regular");
            AddFont(Resources, @"Fonts/Torus/Torus-Light");
            AddFont(Resources, @"Fonts/Torus/Torus-SemiBold");
            AddFont(Resources, @"Fonts/Torus/Torus-Bold");
            AddFont(Resources, @"Fonts/Venera/Venera-Light");
            AddFont(Resources, @"Fonts/Venera/Venera-Bold");
            AddFont(Resources, @"Fonts/Venera/Venera-Black");

            // The minimal game-level dependency set lazer's DrawableRuleset subtree resolves
            // (mirroring what lazer's own DrawableRuleset test scenes cache): a realm instance
            // (required non-null by DatabasedKeyBindingContainer; empty database means default key
            // bindings, which is all autoplay needs), an OsuConfigManager (gameplay visual
            // settings, kept isolated in the lazer subdirectory), the per-ruleset config cache and
            // the game colour palette.
            var lazerStorage = Host.Storage.GetStorageForDirectory("lazer");
            lazerRealm = new RealmAccess(lazerStorage, "client.realm", Host.UpdateThread);
            dependencies.Cache(lazerRealm);
            dependencies.Cache(lazerConfig = new OsuConfigManager(lazerStorage));
            // DrawableHitObject resolves the game-level IGameplaySettings for combo-colour
            // normalisation; OsuConfigManager is lazer's implementation (same as OsuGameBase).
            dependencies.CacheAs<IGameplaySettings>(lazerConfig);
            dependencies.CacheAs<IRulesetConfigCache>(lazerRulesetConfigs = new LazerRulesetConfigCache(lazerRealm));
            dependencies.Cache(new OsuColour());
            dependencies.CacheAs<osu.Game.IO.IStorageResourceProvider>(
                new LazerResourceProvider(Host, Audio, Resources, lazerRealm));

            var config = new JukeBoxConfigManager(Host.Storage);
            dependencies.Cache(config);

            var mirror = new SwitchableMirror(new NerinyanMirror(http), new CatboyMirror(http), new OsuDirectMirror(http),
                config.GetBindable<MirrorSource>(JukeBoxSetting.PreferredMirror));
            dependencies.CacheAs<IBeatmapMirror>(mirror);

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

            // This framework version has no FrameworkSetting for the built-in FPS/frame-statistics
            // overlay — instead osu.Framework.Game itself exposes a protected FrameStatistics
            // bindable (driving a PerformanceOverlay it wires up in its own base.LoadComplete) that
            // only a Game subclass like this one can reach. Setting it here — even before that
            // wiring exists yet, since this runs in load(), well before LoadComplete — is safe: the
            // overlay's own binding uses runOnceImmediately, so it just picks up whatever value is
            // already sitting in FrameStatistics by the time base.LoadComplete() runs.
            showFps = config.GetBindable<bool>(JukeBoxSetting.ShowFps);
            showFps.BindValueChanged(e => FrameStatistics.Value = FrameStatisticsModeFor(e.NewValue), true);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            http.Dispose();
            lazerRulesetConfigs?.Dispose();
            lazerConfig?.Dispose();
            lazerRealm?.Dispose();
        }
    }
}
