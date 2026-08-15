using System;
using System.Net.Http;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Import;
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
        // never wires up a real network-backed store, and BeatmapCard/NowPlayingPanel's
        // [Resolved(canBeNull: true)] reliably resolves null across every existing test scene.
        protected DependencyContainer dependencies = null!;

        private readonly HttpClient http = new HttpClient();

        private PlaybackController playback = null!;
        private Jukebox jukebox = null!;

        // Kept as a field (rather than a load()-local) because osu.Framework's config-manager
        // bindables use a weak-reference chain back to the master value — an unrooted local would
        // be eligible for collection, silently dropping this binding. See JukeBoxSetting.FpsDisplayMode.
        private Bindable<FpsDisplayMode> fpsDisplay = null!;
        private Bindable<double> uiScale = null!;
        private Bindable<double> volumeInactive = null!;

        // Lazer's own compact FPS/frame-time readout (osu.Game.Graphics.UserInterface.FPSCounter):
        // reused as-is rather than hand-rolled — its DI needs (OsuColour, OsuConfigManager,
        // GameHost) are all already satisfied by dependencies cached earlier in load() below.
        // Window-level (added to base.Content, NOT the DPI-scaled `Content` property — see its
        // construction below) and shown/hidden purely by FpsDisplayMode.Compact, independent of
        // FrameStatistics/FrameStatisticsModeFor (which deliberately excludes Compact).
        private osu.Game.Graphics.UserInterface.FPSCounter fpsCounter = null!;

        // Multiplied into ALL audio (lazer's OsuGame does the same): faded towards
        // OsuSetting.VolumeInactive while the window is unfocused, back to 1 on focus.
        private readonly BindableDouble inactiveVolumeDuck = new BindableDouble(1);
        private IBindable<bool> isActive = null!;

        /// <summary>
        /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the FpsDisplayMode -&gt;
        /// FrameStatisticsMode mapping used by the binding below, isolated from
        /// <see cref="osu.Framework.Game.FrameStatistics"/> itself: actually flipping that bindable
        /// activates the framework's real PerformanceOverlay, which isn't safe to run under a
        /// headless test host (crashes with a NullReferenceException — no real renderer/GPU).
        /// <see cref="FpsDisplayMode.Compact"/> stays <see cref="FrameStatisticsMode.None"/> here —
        /// it's driven entirely by <see cref="fpsCounter"/> instead, not the framework overlay.
        /// </summary>
        internal static FrameStatisticsMode FrameStatisticsModeFor(FpsDisplayMode fpsDisplay) => fpsDisplay switch
        {
            FpsDisplayMode.Details => FrameStatisticsMode.Minimal,
            FpsDisplayMode.Graph => FrameStatisticsMode.Full,
            _ => FrameStatisticsMode.None,
        };

        /// <summary>
        /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the one-shot
        /// <see cref="JukeBoxSetting.ShowFps"/> → <see cref="JukeBoxSetting.FpsDisplay"/> (legacy)
        /// migration mapping used below, isolated from the config manager itself. Unchanged by the
        /// Compact-overlay/Graph rename — still lands in the legacy shape, one hop before
        /// <see cref="MigrateLegacyFpsDisplay"/> takes it the rest of the way.
        /// </summary>
        internal static LegacyFpsDisplayMode MigrateShowFps(bool showFps) => showFps ? LegacyFpsDisplayMode.Details : LegacyFpsDisplayMode.Off;

        /// <summary>
        /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the one-shot
        /// <see cref="JukeBoxSetting.FpsDisplay"/> (legacy) → <see cref="JukeBoxSetting.FpsDisplayMode"/>
        /// migration mapping used below, isolated from the config manager itself. The legacy
        /// Compact (single-line Minimal counter) now means <see cref="FpsDisplayMode.Details"/>;
        /// the legacy Details (frame-time Graph) now means <see cref="FpsDisplayMode.Graph"/>. Any
        /// other/unrecognised legacy value (including <see cref="LegacyFpsDisplayMode.Off"/>, and
        /// covering the framework's own catch-and-default-to-Off behaviour for ini text that no
        /// longer parses at all) lands on <see cref="FpsDisplayMode.Off"/>.
        /// </summary>
        internal static FpsDisplayMode MigrateLegacyFpsDisplay(LegacyFpsDisplayMode legacy) => legacy switch
        {
            LegacyFpsDisplayMode.Compact => FpsDisplayMode.Details,
            LegacyFpsDisplayMode.Details => FpsDisplayMode.Graph,
            _ => FpsDisplayMode.Off,
        };

        /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the Compact-mode
        /// overlay, to assert its visibility without depending on internal layout.</summary>
        internal osu.Game.Graphics.UserInterface.FPSCounter FpsCounter => fpsCounter;

        /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the live
        /// FpsDisplayMode bindable, to drive it directly without needing a settings UI in the tree.</summary>
        internal Bindable<FpsDisplayMode> FpsDisplay => fpsDisplay;

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

        /// <summary>
        /// See the cache site in load(): a beat-sync provider with nothing to sync to. The clock
        /// must be non-null — BeatSyncedContainer.Update dereferences it unconditionally (lazer's
        /// Beatmap-backed provider always has a track clock); a never-started stopwatch keeps
        /// IsBeatSyncedWithTrack false, which is the documented idle-animation path.
        /// </summary>
        private class SilentBeatSyncProvider : osu.Game.Beatmaps.IBeatSyncProvider
        {
            private readonly osu.Framework.Timing.StopwatchClock stoppedClock = new osu.Framework.Timing.StopwatchClock();

            public osu.Game.Beatmaps.ControlPoints.ControlPointInfo? ControlPoints => null;
            public osu.Framework.Timing.IClock? Clock => stoppedClock;
            public osu.Framework.Audio.Track.ChannelAmplitudes CurrentAmplitudes => osu.Framework.Audio.Track.ChannelAmplitudes.Empty;
        }

        // Lazer-side dependencies backing the LazerChartLayer's DrawableRuleset host — owned (and
        // disposed) here because they're game-lifetime singletons, exactly like OsuGameBase owns
        // its equivalents. The realm database only ever stores lazer-side settings (ruleset
        // configs, key bindings if any) under its own "lazer" subdirectory.
        private RealmAccess lazerRealm = null!;
        private OsuConfigManager lazerConfig = null!;
        private LazerRulesetConfigCache lazerRulesetConfigs = null!;
        private SkinSelection skinSelection = null!;
        private ChartModSelection chartMods = null!;
        private PlayfieldElementVisibility playfieldElements = null!;
        private ChartConversion chartConversion = null!;
        private osu.Game.Skinning.SkinManager skinManager = null!;
        private DroppedFileImporter fileImporter = null!;

        [BackgroundDependencyLoader]
        private void load(osu.Framework.Configuration.FrameworkConfigManager frameworkConfig)
        {
            Resources.AddStore(new DllResourceStore(typeof(JukeBoxResources).Assembly));

            // osu!lazer's default skin assets + fonts (osu.Game.Resources, CC-BY-NC — see
            // docs/ATTRIBUTION.md). The gameplay renderer's skins resolve default textures and
            // hitsound samples out of this store; the fonts are the exact set OsuGameBase.load
            // registers (osu-resources has no "Fonts/osuFont" — the legacy icon font is gone
            // upstream, and requesting it NREs GlyphStore in a real windowed run).
            Resources.AddStore(new DllResourceStore(osu.Game.Resources.OsuResources.ResourceAssembly));

            AddFont(Resources, @"Fonts/Torus/Torus-Regular");
            AddFont(Resources, @"Fonts/Torus/Torus-Light");
            AddFont(Resources, @"Fonts/Torus/Torus-SemiBold");
            AddFont(Resources, @"Fonts/Torus/Torus-Bold");

            AddFont(Resources, @"Fonts/Torus-Alternate/Torus-Alternate-Regular");
            AddFont(Resources, @"Fonts/Torus-Alternate/Torus-Alternate-Light");
            AddFont(Resources, @"Fonts/Torus-Alternate/Torus-Alternate-SemiBold");
            AddFont(Resources, @"Fonts/Torus-Alternate/Torus-Alternate-Bold");

            AddFont(Resources, @"Fonts/Inter/Inter-Regular");
            AddFont(Resources, @"Fonts/Inter/Inter-RegularItalic");
            AddFont(Resources, @"Fonts/Inter/Inter-Light");
            AddFont(Resources, @"Fonts/Inter/Inter-LightItalic");
            AddFont(Resources, @"Fonts/Inter/Inter-SemiBold");
            AddFont(Resources, @"Fonts/Inter/Inter-SemiBoldItalic");
            AddFont(Resources, @"Fonts/Inter/Inter-Bold");
            AddFont(Resources, @"Fonts/Inter/Inter-BoldItalic");

            AddFont(Resources, @"Fonts/Noto/Noto-Basic");
            AddFont(Resources, @"Fonts/Noto/Noto-Bopomofo");
            AddFont(Resources, @"Fonts/Noto/Noto-CJK-Basic");
            AddFont(Resources, @"Fonts/Noto/Noto-CJK-Compatibility");
            AddFont(Resources, @"Fonts/Noto/Noto-Hangul");
            AddFont(Resources, @"Fonts/Noto/Noto-Thai");

            AddFont(Resources, @"Fonts/Venera/Venera-Light");
            AddFont(Resources, @"Fonts/Venera/Venera-Bold");
            AddFont(Resources, @"Fonts/Venera/Venera-Black");

            // Lazer's texture-backed icon "font" (OsuIcon.FONT_NAME = "Icons"): OsuIcon glyphs —
            // including every Ruleset.CreateIcon()'s — resolve through this store over the
            // osu-resources Textures/Icons files, exactly as OsuGameBase registers it. Without
            // it, any SpriteIcon using an OsuIcon glyph silently renders nothing.
            Fonts.AddStore(new osu.Game.Graphics.OsuIcon.OsuIconStore(Textures));

            // The minimal game-level dependency set lazer's DrawableRuleset subtree resolves
            // (mirroring what lazer's own DrawableRuleset test scenes cache): a realm instance
            // (required non-null by DatabasedKeyBindingContainer; empty database means default key
            // bindings, which is all autoplay needs), an OsuConfigManager (gameplay visual
            // settings, kept isolated in the lazer subdirectory), the per-ruleset config cache and
            // the game colour palette.
            var lazerStorage = Host.Storage.GetStorageForDirectory("lazer");
            lazerRealm = CreateLazerRealmWithRecovery(lazerStorage, Host.UpdateThread);
            dependencies.Cache(lazerRealm);
            dependencies.Cache(lazerConfig = new OsuConfigManager(lazerStorage));
            // DrawableHitObject resolves the game-level IGameplaySettings for combo-colour
            // normalisation; OsuConfigManager is lazer's implementation (same as OsuGameBase).
            dependencies.CacheAs<IGameplaySettings>(lazerConfig);
            // A Component (added below) so its LoadComplete populates the configs on the update
            // thread — see LazerRulesetConfigCache remarks; this game load runs on an async
            // loader thread where realm reads are forbidden.
            dependencies.CacheAs<IRulesetConfigCache>(lazerRulesetConfigs = new LazerRulesetConfigCache(lazerRealm));
            Add(lazerRulesetConfigs);
            dependencies.Cache(new OsuColour());
            dependencies.CacheAs<osu.Game.IO.IStorageResourceProvider>(
                new LazerResourceProvider(Host, Audio, Resources, lazerRealm));

            // Added to base.Content (the framework's own unscaled root), NOT the `Content`
            // property this class overrides (the DrawSizePreservingFillContainer from the
            // constructor, subject to JukeBoxSetting.UiScale) — window-level and above everything
            // else in the draw order, deliberately kept out of the player box, exactly like the
            // constructor's own base.Content.Add for that scaling container. Bottom-right, inset by
            // a small margin: there is no bottom bar left to clear (its content moved into the right
            // column's Playback tab), so the counter simply sits in the window's own corner.
            // Visibility is entirely owned by fpsDisplay's binding below (runOnceImmediately, so it
            // sets the correct initial Show()/Hide() state itself) — no explicit Hide() needed here.
            base.Content.Add(fpsCounter = new osu.Game.Graphics.UserInterface.FPSCounter
            {
                Anchor = Anchor.BottomRight,
                Origin = Anchor.BottomRight,
                Margin = new MarginPadding { Right = 10, Bottom = 10 },
            });

            // The realm-backed skin store, cached exactly as OsuGameBase caches it. Nothing in our
            // own skin chain consults it (LazerSkinProvider never falls back to parent lookups) —
            // it exists because scattered lazer gameplay pieces hard-resolve it for default-skin
            // textures (e.g. catch's LegacyHitExplosion reads DefaultClassicSkin), which crashed
            // the first catch map that drew a legacy hit explosion.
            dependencies.Cache(skinManager = new osu.Game.Skinning.SkinManager(lazerStorage, lazerRealm, Host, Resources, Audio, Scheduler));
            dependencies.CacheAs<osu.Game.Skinning.ISkinSource>(skinManager);

            // Game-level beat-sync fallback: lazer's BeatSyncedContainer pieces (the Nub inside
            // OsuCheckbox/RoundedSliderBar used by the settings panel, kiai-reactive skin pieces)
            // hard-resolve IBeatSyncProvider. OsuGameBase provides its Beatmap-backed one; ours is
            // silent (no control points → the containers simply never pulse). LazerChartLayer
            // still overrides this within its own subtree via the interface's [Cached] attribute,
            // so in-chart pieces keep real beat sync.
            dependencies.CacheAs<osu.Game.Beatmaps.IBeatSyncProvider>(new SilentBeatSyncProvider());

            // Session-lifetime statics OsuGameBase caches: lazer's hover/click sound components
            // (HoverSampleDebounceComponent, used by every lazer settings control) resolve this.
            dependencies.Cache(new SessionStatics());

            // Menu open/close sounds: every lazer OsuMenu (settings dropdowns included) hard-resolves
            // this Component; OsuGameBase caches + adds it, so we must too or opening any dropdown
            // crashes with DependencyNotRegisteredException.
            var menuSamples = new osu.Game.Graphics.UserInterface.OsuMenuSamples();
            dependencies.Cache(menuSamples);
            Add(menuSamples);

            var config = new JukeBoxConfigManager(Host.Storage);
            dependencies.Cache(config);

            var mirror = new SwitchableMirror(new NerinyanMirror(http), new CatboyMirror(http), new OsuDirectMirror(http),
                config.GetBindable<MirrorSource>(JukeBoxSetting.PreferredMirror));
            dependencies.CacheAs<IBeatmapMirror>(mirror);

            // The alternative SEARCH backend (downloads always stay on the mirror chain above).
            // Cached unconditionally even with no credentials configured: BeatmapSearchEngine only
            // reaches for it when the user picked it, and it reports the missing credentials as an
            // ordinary search failure — which already falls back to the mirror.
            dependencies.Cache(new OfficialBeatmapSearch(http,
                config.GetBindable<string>(JukeBoxSetting.OsuClientId),
                config.GetBindable<string>(JukeBoxSetting.OsuClientSecret)));

            var cache = new BeatmapCache(Host.Storage.GetFullPath("cache"), mirror, config.Get<bool>(JukeBoxSetting.NoVideoDownloads));
            dependencies.Cache(cache);

            var queue = new MusicQueue();
            dependencies.Cache(queue);

            // Session-lifetime registry of dropped .osr replays, keyed by the difficulty each was
            // played on. Cached before the visuals stack can resolve it (see BeatmapVisuals).
            dependencies.Cache(new Replays.ReplayStore());

            var radio = new RadioService(mirror);
            dependencies.Cache(radio);

            // Game.AddInternal is sealed to throw ("Use Add or Content instead") — Add routes
            // through the overridden Content property (the DPI-scaling container from the
            // constructor) instead.
            Add(playback = new PlaybackController());
            dependencies.Cache(playback);

            Add(jukebox = new Jukebox(queue, radio, cache, playback));
            dependencies.Cache(jukebox);

            // Volume source of truth is the framework's master volume (VolumeUniversal) — one
            // knob shared by the settings panel and the now-playing bar, multiplying every audio
            // component (track, storyboard samples, chart hitsounds) through AudioManager itself.
            // The old app-level JukeBoxSetting.Volume is deprecated: its persisted value is copied
            // into VolumeUniversal exactly once (so an existing user's volume survives the
            // upgrade), after which the ini key is left untouched. playback.Volume (a per-track
            // adjustment) stays at its default 1 and is no longer user-facing.
            if (!config.Get<bool>(JukeBoxSetting.VolumeMigrated))
            {
                frameworkConfig.SetValue(osu.Framework.Configuration.FrameworkSetting.VolumeUniversal, config.Get<double>(JukeBoxSetting.Volume));
                config.SetValue(JukeBoxSetting.VolumeMigrated, true);
            }

            // ShowFps -> FpsDisplay (legacy): same one-shot copy-then-guard shape as the volume
            // migration above. The old bool key is left untouched afterwards (still readable, just
            // unused).
            if (!config.Get<bool>(JukeBoxSetting.FpsDisplayMigrated))
            {
                config.SetValue(JukeBoxSetting.FpsDisplay, MigrateShowFps(config.Get<bool>(JukeBoxSetting.ShowFps)));
                config.SetValue(JukeBoxSetting.FpsDisplayMigrated, true);
            }

            // FpsDisplay (legacy) -> FpsDisplayMode: chained onto the migration above so an
            // upgrade from the very first ShowFps release still lands correctly, one hop at a
            // time. Same one-shot copy-then-guard shape; the legacy key is left untouched
            // afterwards (still readable, just unused). See MigrateLegacyFpsDisplay for why this
            // can't just be a straight Enum.Parse of the old text against the new type.
            if (!config.Get<bool>(JukeBoxSetting.FpsDisplayModeMigrated))
            {
                config.SetValue(JukeBoxSetting.FpsDisplayMode, MigrateLegacyFpsDisplay(config.Get<LegacyFpsDisplayMode>(JukeBoxSetting.FpsDisplay)));
                config.SetValue(JukeBoxSetting.FpsDisplayModeMigrated, true);
            }

            Add(skinSelection = new SkinSelection());
            dependencies.Cache(skinSelection);

            // Chart-tab state: the mod selection applied to the autoplay chart (and, for the rate
            // mods, to playback itself) and the per-element playfield visibility. Both are added
            // AFTER the playback controller and jukebox above, which they resolve.
            Add(chartMods = new ChartModSelection());
            dependencies.Cache(chartMods);

            Add(playfieldElements = new PlayfieldElementVisibility());
            dependencies.Cache(playfieldElements);

            Add(chartConversion = new ChartConversion());
            dependencies.Cache(chartConversion);

            // Drag-and-drop importer. Wired here (rather than in JukeBoxGame) so the test browser
            // and visual test scenes get it too — it resolves host.Window itself and simply has no
            // OS-level drop source when there isn't one (headless), so nothing here spawns
            // window-thread work a test host can't service.
            Add(fileImporter = new DroppedFileImporter());
            dependencies.Cache(fileImporter);

            var offsetStore = new BeatmapOffsetStore();
            Add(offsetStore);
            dependencies.Cache(offsetStore);

            // UI scaling, lazer's ScalingContainer trick applied to our DPI-scaling root: Scale
            // enlarges the whole UI while the inverse relative Size keeps it filling the window.
            Content.Anchor = Anchor.Centre;
            Content.Origin = Anchor.Centre;
            uiScale = config.GetBindable<double>(JukeBoxSetting.UiScale);
            uiScale.BindValueChanged(e =>
            {
                float s = (float)e.NewValue;
                Content.Scale = new Vector2(s);
                Content.Size = new Vector2(1f / s);
            }, true);

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
            //
            // Compact is deliberately excluded from that framework overlay (FrameStatisticsModeFor
            // maps it to None) — it's driven by fpsCounter below instead, so both arms of this one
            // bindable stay in lockstep without a second BindValueChanged subscription.
            fpsDisplay = config.GetBindable<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode);
            fpsDisplay.BindValueChanged(e =>
            {
                FrameStatistics.Value = FrameStatisticsModeFor(e.NewValue);

                if (e.NewValue == FpsDisplayMode.Compact)
                    fpsCounter.Show();
                else
                    fpsCounter.Hide();
            }, true);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // "Master (window inactive)": same mechanism (and fade durations) as lazer's OsuGame —
            // an extra volume adjustment on the whole AudioManager, faded on focus change. The
            // duck target follows OsuSetting.VolumeInactive live via the transform re-running on
            // the next focus flip.
            volumeInactive = lazerConfig.GetBindable<double>(OsuSetting.VolumeInactive);
            Audio.AddAdjustment(osu.Framework.Audio.AdjustableProperty.Volume, inactiveVolumeDuck);

            // Schedule: focus changes arrive on the SDL window thread (SDL2Window.set_Focused),
            // and bindable transforms hard-require the update thread.
            isActive = Host.IsActive.GetBoundCopy();
            isActive.BindValueChanged(e => Schedule(() =>
            {
                if (e.NewValue)
                    this.TransformBindableTo(inactiveVolumeDuck, 1, 400, Easing.OutQuint);
                else
                    this.TransformBindableTo(inactiveVolumeDuck, volumeInactive.Value, 4000, Easing.OutQuint);
            }), true);
        }

        /// <summary>
        /// Opens the lazer-side realm database, recovering from a corrupt/unopenable file by
        /// deleting it and retrying once. A hard-crash on corrupt realm is a known lazer startup
        /// failure mode (ppy/osu#16441) — but unlike lazer, this database is entirely throwaway
        /// (default key bindings and ruleset configs regenerate), so deletion is always safe.
        /// A second failure (a genuinely broken environment) propagates.
        /// Internal for testing (JukeBox.Game.Tests has InternalsVisibleTo).
        /// </summary>
        internal static RealmAccess CreateLazerRealmWithRecovery(osu.Framework.Platform.Storage storage, osu.Framework.Threading.GameThread updateThread)
        {
            try
            {
                return openAndProbeRealm(storage, updateThread);
            }
            catch (Exception e)
            {
                osu.Framework.Logging.Logger.Error(e, "lazer realm database failed to open — deleting it and retrying (it only holds regenerable key-binding/ruleset-config data)");

                try
                {
                    foreach (string f in storage.GetFiles(string.Empty, "client.realm*"))
                        storage.Delete(f);

                    foreach (string d in storage.GetDirectories(string.Empty))
                    {
                        if (d.StartsWith("client.realm", StringComparison.Ordinal))
                            storage.DeleteDirectory(d);
                    }
                }
                catch (Exception deleteError)
                {
                    // Deletion failing means the retry below will fail too and propagate the
                    // real, more useful error — don't mask it with the cleanup failure.
                    osu.Framework.Logging.Logger.Error(deleteError, "failed to delete lazer realm files");
                }

                return openAndProbeRealm(storage, updateThread);
            }
        }

        private static RealmAccess openAndProbeRealm(osu.Framework.Platform.Storage storage, osu.Framework.Threading.GameThread updateThread)
        {
            var realm = new RealmAccess(storage, "client.realm", updateThread);

            try
            {
                // Corruption can surface on first real access rather than construction — force it
                // now so the recovery path above sees it.
                realm.Run(_ => { });
                return realm;
            }
            catch
            {
                realm.Dispose();
                throw;
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            http.Dispose();
            // lazerRulesetConfigs is a child Component — the drawable tree disposes it (and with
            // it the ruleset configs) before we tear the realm down here.
            lazerConfig?.Dispose();
            lazerRealm?.Dispose();
        }
    }
}
