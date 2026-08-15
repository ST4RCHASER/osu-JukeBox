#nullable enable

// Machine-local end-to-end check: queues the REAL cached keysound-only set 92190 (Imperishable
// Night 2006 — AudioFilename: virtual, no music file, ~116 keysound .ogg samples) by id through a
// REAL JukeBoxGame, exactly as typing the id into the map-id overlay does. Skips when that set
// isn't cached on this machine — an opt-in extra layer of confidence on dev machines, not a CI
// gate, same arrangement as TestSceneRealCacheSmoke.

using System.IO;
using System.Linq;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Game.Skinning;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneRealVirtualAudioSet : JukeBoxTestScene
    {
        private const int virtual_audio_set_id = 92190;

        [Resolved]
        private GameHost host { get; set; } = null!;

        private JukeBoxGame game = null!;
        private Jukebox jukebox = null!;
        private PlaybackController playback = null!;

        private int maxPlayingSamples;

        // The headless test host gets its own storage root, so the set the real app downloaded
        // isn't in the cache the hosted game will read. Linked (not copied — it's ~30MB of
        // samples) into place so the game loads the genuine files through its own real cache.
        private static string realAppCacheDir => Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "JukeBox", "cache", virtual_audio_set_id.ToString());

        private string hostCacheDir => host.Storage.GetFullPath(Path.Combine("cache", virtual_audio_set_id.ToString()));

        [Test]
        public void RealKeysoundedSetPlaysWithAudibleSamples()
        {
            AddStep("link real cached set into host storage", () =>
            {
                if (Directory.Exists(hostCacheDir) || !Directory.Exists(realAppCacheDir))
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(hostCacheDir)!);
                Directory.CreateSymbolicLink(hostCacheDir, realAppCacheDir);
            });

            AddAssert("real set is available", () =>
            {
                if (!Directory.Exists(hostCacheDir))
                    Assert.Ignore($"set {virtual_audio_set_id} is not cached on this machine (looked in {realAppCacheDir})");

                return true;
            });

            AddStep("create real game", () =>
            {
                maxPlayingSamples = 0;
                AddGame(game = new JukeBoxGame());
            });

            AddUntilStep("game loaded", () => game.IsLoaded);

            AddStep("resolve playback services", () =>
            {
                jukebox = game.Dependencies.Get<Jukebox>();
                playback = game.Dependencies.Get<PlaybackController>();
            });

            AddStep("queue set by id", () => jukebox.EnqueueAndMaybePlayAsync(
                new BeatmapSetInfo { Id = virtual_audio_set_id, Title = "Imperishable Night 2006" }));

            AddUntilStep("set is playing", () => playback.Current.Value?.SetId == virtual_audio_set_id);
            AddAssert("recognised as virtual audio", () => playback.Current.Value?.HasVirtualAudio == true);
            AddAssert("no error reported", () => jukebox.LastError.Value == null);

            // The whole point: the clock has to run even though nothing was decoded from a file.
            AddUntilStep("clock advances", () => playback.CurrentTimeMs > 0);
            AddAssert("track sized from map content", () => playback.LengthMs > 100_000);

            // Straight into the dense keysounded section rather than waiting through the intro.
            AddStep("seek into the song", () => playback.Seek(30_000));
            AddUntilStep("clock past the seek", () => playback.CurrentTimeMs > 30_000);

            // The samples ARE the song here. Sampled every frame because each keysound is a short
            // one-shot: any frame catching one playing proves the map is audible.
            AddUntilStep("keysounds playing", () =>
            {
                int playing = game.ChildrenOfType<SkinnableSound>().Count(s => s.IsPlaying);
                maxPlayingSamples = System.Math.Max(maxPlayingSamples, playing);
                return maxPlayingSamples > 0;
            });

            AddAssert("still no error reported", () => jukebox.LastError.Value == null);
            AddStep("report", () => osu.Framework.Logging.Logger.Log(
                $"REAL-APP virtual audio: set {virtual_audio_set_id} playing, clock {playback.CurrentTimeMs:0}ms of {playback.LengthMs:0}ms, peak concurrent keysounds {maxPlayingSamples}, errors: {jukebox.LastError.Value ?? "none"}"));
        }
    }
}
