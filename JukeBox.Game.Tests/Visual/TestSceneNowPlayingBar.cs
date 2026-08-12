#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.Tests.Beatmaps;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneNowPlayingBar : JukeBoxManualInputTestScene
    {
        private PlaybackController playback = null!;
        private MusicQueue queue = null!;
        private BeatmapCache cache = null!;
        private Jukebox jukebox = null!;

        private NowPlayingBar bar = null!;
        private QueuePanel panel = null!;

        private string tmp = null!;
        private CachedBeatmapSet fixtureSet = null!;
        private CachedBeatmapSet fixtureSetLong = null!;
        private BeatmapSetInfo fixtureInfo = null!;

        // CreateChildDependencies runs once for the whole scene (shared across every [Test] in
        // this fixture, see TestSceneBeatmapListing) — playback/jukebox/queue are created here once
        // and reused/reset across tests in SetUpSteps rather than recreated, so the cached
        // instances [Resolved] fields pick up stay valid.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            string audioFile = Path.Combine(tmp, "audio.wav");
            writeSilentWav(audioFile, 1);
            fixtureSet = new CachedBeatmapSet { SetId = 1, Directory = tmp, AudioFile = audioFile };
            fixtureInfo = new BeatmapSetInfo { Id = 1, Title = "Fixture Title", Artist = "Fixture Artist" };

            // A separate, much longer fixture for the drag test: with only ~1s of track, the real
            // wall-clock time spent stepping through AddStep/AddWaitStep could itself carry
            // CurrentTimeMs a meaningful fraction of the way through LengthMs, making the "did the
            // handle stay put mid-drag / did it land near the drag target" assertions flaky.
            string longDir = Path.Combine(tmp, "long");
            Directory.CreateDirectory(longDir);
            string longAudioFile = Path.Combine(longDir, "audio.wav");
            writeSilentWav(longAudioFile, 20);
            fixtureSetLong = new CachedBeatmapSet { SetId = 2, Directory = longDir, AudioFile = longAudioFile };

            playback = new PlaybackController();
            queue = new MusicQueue();
            var emptyMirror = new EmptyMirror();

            // Backed by a real (if fake-content) mirror rather than EmptyMirror, so
            // QueuePanelRowShowsDownloadingThenReadyStatus below can exercise a real
            // download/extract round trip through GetAsync.
            cache = new BeatmapCache(Path.Combine(tmp, "cache"), new FileMirror(makeOsz()));

            jukebox = new Jukebox(queue, new RadioService(emptyMirror), cache, playback);

            var deps = new DependencyContainer(parent);
            deps.CacheAs(playback);
            deps.CacheAs(jukebox);
            deps.CacheAs(queue);
            deps.CacheAs(cache);
            return deps;
        }

        // NOTE: deliberately NOT deleting `tmp` here — see TestScenePlaybackController for why
        // (TestScene runs queued AddStep bodies from a base-class teardown hook that fires after
        // this derived class's own [TearDown], so a synchronous delete here would race the
        // fixture files out from under still-pending steps).

        private Container uiContainer = null!;

        // playback/jukebox are added exactly once, here, for the TestScene's whole lifetime — NOT
        // inside SetUpSteps. SetUpSteps' `uiContainer.Children = ...` reassignment below disposes
        // whatever it previously held on every re-run (once per [Test]); doing that to playback/
        // jukebox themselves would dispose the very instances CreateChildDependencies cached for
        // [Resolved] to hand out, breaking every [Test] after the first with an
        // ObjectDisposedException. uiContainer exists precisely to give SetUpSteps something
        // disposable to rebuild each test without touching its playback/jukebox siblings.
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Add(playback);
            Add(jukebox);
            Add(uiContainer = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset queue, build UI", () =>
            {
                queue.Items.Clear();

                uiContainer.Children = new Drawable[]
                {
                    bar = new NowPlayingBar(),
                    panel = new QueuePanel(),
                };
            });
        }

        [Test]
        public void PlayPauseButtonFlipsIsPlaying()
        {
            AddStep("play fixture", () => playback.PlayAsync(fixtureSet));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == fixtureSet.SetId);
            AddAssert("playing", () => playback.IsPlaying);

            AddStep("click play/pause button", () => bar.PlayPauseButton.TriggerClick());
            AddAssert("no longer playing", () => !playback.IsPlaying);

            AddStep("click play/pause button again", () => bar.PlayPauseButton.TriggerClick());
            AddAssert("playing again", () => playback.IsPlaying);
        }

        // Regression test for the periodic Update() write fighting a live drag: SliderBar<T>'s
        // TransferValueOnCommit only gates user-drag-input reaching `Current`, not the reverse
        // direction — `current.ValueChanged` unconditionally pushes into the drag-preview value
        // (confirmed by decompiling SliderBar<T>'s constructor; no local framework source is
        // available to read directly). Without also checking progressBar.IsDragged before writing
        // `progress.Value` in Update(), that periodic write would snap the handle back to playback
        // position on every frame while a real drag is in progress.
        [Test]
        public void DraggingProgressBarDoesNotSnapBackAndSeeksOnRelease()
        {
            AddStep("play long fixture", () => playback.PlayAsync(fixtureSetLong));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == fixtureSetLong.SetId);
            AddUntilStep("clock advancing", () => playback.CurrentTimeMs > 0);

            AddStep("press down near the left of the progress bar", () =>
            {
                var bounds = bar.ProgressBar;
                Vector2 leftLocal = new Vector2(bounds.DrawWidth * 0.05f, bounds.DrawHeight / 2);
                InputManager.MoveMouseTo(bounds.ToScreenSpace(leftLocal));
                InputManager.PressButton(MouseButton.Left);
            });

            AddStep("drag to the centre", () =>
            {
                var bounds = bar.ProgressBar;
                Vector2 centreLocal = new Vector2(bounds.DrawWidth * 0.5f, bounds.DrawHeight / 2);
                InputManager.MoveMouseTo(bounds.ToScreenSpace(centreLocal));
            });

            AddAssert("bar reports it is being dragged", () => bar.ProgressBar.IsDragged);

            double valueDuringDrag = 0;
            AddStep("capture progress value mid-drag", () => valueDuringDrag = bar.ProgressBar.Current.Value);

            // Several frames pass while the drag is still held. With the bug present, Update()'s
            // periodic write would overwrite Current.Value with playback's advancing position on
            // every one of these frames; with the fix, it's skipped entirely while IsDragged.
            AddWaitStep("hold the drag while frames pass", 10);
            AddAssert("progress value untouched by playback advancing mid-drag",
                () => bar.ProgressBar.Current.Value == valueDuringDrag);

            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));
            AddAssert("no longer dragging", () => !bar.ProgressBar.IsDragged);

            AddUntilStep("seeked to roughly the drag target (~50%)",
                () => Math.Abs(playback.CurrentTimeMs / playback.LengthMs - 0.5) < 0.2);
        }

        [Test]
        public void BarShowsNowPlayingTitleAndArtist()
        {
            AddStep("set NowPlaying", () => jukebox.NowPlaying.Value = fixtureInfo);
            AddUntilStep("title shown", () => bar.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>()
                .Any(t => t.Text.ToString() == fixtureInfo.DisplayTitle));
        }

        [Test]
        public void BarShowsAndClearsStatusText()
        {
            AddStep("set Status", () => jukebox.Status.Value = "Downloading Something…");
            AddUntilStep("status shown", () => bar.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>()
                .Any(t => t.Text.ToString() == "Downloading Something…"));

            AddStep("clear Status", () => jukebox.Status.Value = null);
            AddUntilStep("status cleared", () => bar.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>()
                .All(t => t.Text.ToString() != "Downloading Something…"));
        }

        [Test]
        public void QueuePanelShowsRowsAfterEnqueue()
        {
            AddAssert("starts empty", () => panel.RowCount == 0);
            AddAssert("header shows 0", () => panel.HeaderText == "Queue (0)");

            AddStep("enqueue two sets", () =>
            {
                queue.Enqueue(new BeatmapSetInfo { Id = 1, Title = "One", Artist = "Artist One" });
                queue.Enqueue(new BeatmapSetInfo { Id = 2, Title = "Two", Artist = "Artist Two" });
            });

            AddAssert("2 rows shown", () => panel.RowCount == 2);
            AddAssert("header shows 2", () => panel.HeaderText == "Queue (2)");

            AddStep("remove first row via its ✕ button", () => panel.TriggerRemoveAt(0));
            AddAssert("1 row left", () => panel.RowCount == 1);
            AddAssert("removed set is gone from the queue itself", () => queue.Items.All(i => i.Id != 1));
        }

        // Regression test for a production crash: MusicQueue.Items is a BindableList that
        // QueuePanel binds CollectionChanged against to rebuild rowsFlow's InternalChildren.
        // Jukebox's advanceRoundAsync pops the queue from a ConfigureAwait(false) continuation
        // (i.e. off the update thread) on its second-and-later candidate within a round, which
        // used to run that rebuild inline on the popping thread — mutating a Loaded Drawable's
        // InternalChildren off the update thread, which the framework throws
        // Drawable.InvalidThreadForMutationException for (and, in production, left the panel's
        // internal Drawable bookkeeping corrupted enough to crash later with an unrelated
        // KeyNotFoundException). This reproduces that class of bug directly at the queue/panel
        // boundary — mutating Items from a background thread while the panel is loaded — without
        // needing to drive Jukebox's own retry/coalescing paths to land a PopNext call off-thread.
        [Test]
        public void QueuePanelSurvivesQueueMutationFromBackgroundThread()
        {
            AddStep("enqueue two sets", () =>
            {
                queue.Enqueue(new BeatmapSetInfo { Id = 1, Title = "One", Artist = "Artist One" });
                queue.Enqueue(new BeatmapSetInfo { Id = 2, Title = "Two", Artist = "Artist Two" });
            });
            AddAssert("2 rows shown", () => panel.RowCount == 2);

            Exception? caught = null;

            AddStep("pop the queue from a background thread while the panel is loaded", () =>
            {
                caught = null;
                try
                {
                    Task.Run(() => queue.PopNext()).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });

            AddAssert("no exception escaped the off-thread mutation", () => caught == null);
            AddUntilStep("panel reflects the removal", () => panel.RowCount == 1);
            AddAssert("header reflects the removal", () => panel.HeaderText == "Queue (1)");
        }

        // Regression coverage for per-row download status: a set with no cache entry and nothing
        // in flight for it shows "waiting"; once a download for it is kicked off it shows
        // "downloading…"; once GetAsync completes (extracted to disk with a *.osu file) it flips
        // to "ready". Uses set id 999 — distinct from fixtureSet/fixtureSetLong's 1/2 and from
        // whatever CreateChildDependencies' cache fixture osz was downloaded as — so nothing else
        // in this fixture has touched its cache state.
        [Test]
        public void QueuePanelRowShowsDownloadingThenReadyStatus()
        {
            const int waiting_id = 999;
            const int downloading_id = 998;

            AddStep("enqueue a set with no cache activity", () =>
                queue.Enqueue(new BeatmapSetInfo { Id = waiting_id, Title = "Waiting", Artist = "Artist" }));

            AddAssert("row shows waiting", () => panel.StatusTextAt(indexOf(waiting_id)) == "waiting");

            AddStep("enqueue another set and start caching it", () =>
            {
                queue.Enqueue(new BeatmapSetInfo { Id = downloading_id, Title = "Downloading", Artist = "Artist" });
                _ = cache.GetAsync(downloading_id);
            });

            AddUntilStep("row shows ready once GetAsync completes",
                () => panel.StatusTextAt(indexOf(downloading_id)) == "ready");
        }

        private int indexOf(int setId)
        {
            int index = queue.Items.ToList().FindIndex(i => i.Id == setId);
            Assert.That(index, Is.Not.EqualTo(-1), $"set {setId} not found in queue");
            return index;
        }

        // Builds a minimal but real .osz (a zip containing a *.osu file) so BeatmapCache.GetAsync
        // has something genuine to download/extract/scan — mirrors BeatmapCacheTest.makeOsz.
        private string makeOsz()
        {
            string dir = Path.Combine(tmp, "osz-build");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "test.osu"),
                "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\n\n[Events]\n");
            File.WriteAllBytes(Path.Combine(dir, "audio.mp3"), new byte[] { 0xFF });
            string osz = Path.Combine(tmp, "fixture.osz");
            ZipFile.CreateFromDirectory(dir, osz);
            return osz;
        }

        // BASS (the audio backend behind osu!framework's Track) plays WAV directly, so a
        // hand-written 44-byte RIFF header followed by silence is enough to drive playback.
        private static void writeSilentWav(string path, double seconds)
        {
            const int sample_rate = 44100;
            const short channels = 1;
            const short bits_per_sample = 16;

            int dataSize = (int)(sample_rate * channels * (bits_per_sample / 8) * seconds);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sample_rate);
            writer.Write(sample_rate * channels * (bits_per_sample / 8));
            writer.Write((short)(channels * (bits_per_sample / 8)));
            writer.Write(bits_per_sample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }

        // Never exercised (Jukebox's advance loop isn't driven in this test — NowPlaying is set
        // directly, and playback is driven directly via PlaybackController.PlayAsync), only
        // present to satisfy Jukebox/RadioService/BeatmapCache's constructors.
        private class EmptyMirror : IBeatmapMirror
        {
            public string Name => "empty";

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>());

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
