#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Download feedback end to end, driven by a mirror the test steps byte by byte
    /// (<see cref="GatedMirror"/>) rather than by a real transfer: a queue row's progress bar and
    /// percentage, its indeterminate fallback when a mirror sends no <c>Content-Length</c>, and the
    /// "this song is still coming down" indicator on the currently-playing panel.
    /// </summary>
    [TestFixture]
    public partial class TestSceneQueueDownloadProgress : JukeBoxTestScene
    {
        /// <summary>Every test uses its own set id: <see cref="BeatmapCache"/> short-circuits a set
        /// already extracted on disk, and this fixture's cache directory is shared for its whole
        /// lifetime (see <see cref="CreateChildDependencies"/>).</summary>
        private const int determinate_id = 101;

        private const int indeterminate_id = 102;
        private const int current_song_id = 103;

        private const long total_bytes = 100;

        private PlaybackController playback = null!;
        private MusicQueue queue = null!;
        private BeatmapCache cache = null!;
        private Jukebox jukebox = null!;
        private GatedMirror mirror = null!;

        private QueuePanel queuePanel = null!;
        private NowPlayingPanel nowPlaying = null!;

        private string tmp = null!;

        /// <summary>The real right-column content width (see MainScreen) — the row lays out for a
        /// narrow column, so anything that only breaks when space is tight breaks here too.</summary>
        private const float column_content_width = 340 - 2 * Theme.PanelPadding;

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            playback = new PlaybackController();
            queue = new MusicQueue();
            mirror = new GatedMirror(makeOsz());
            cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);
            jukebox = new Jukebox(queue, new RadioService(mirror), cache, playback);

            var deps = new DependencyContainer(parent);
            deps.CacheAs(playback);
            deps.CacheAs(jukebox);
            deps.CacheAs(queue);
            deps.CacheAs(cache);
            return deps;
        }

        private Container uiContainer = null!;

        // playback/jukebox are added exactly once, for this scene's whole lifetime, rather than in
        // SetUpSteps — see TestSceneNowPlayingPanel's LoadComplete for why rebuilding them per test
        // would dispose the very instances CreateChildDependencies cached for [Resolved].
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
            AddStep("reset queue and status, build UI", () =>
            {
                queue.Items.Clear();
                jukebox.Status.Value = null;
                jukebox.DownloadingSetId.Value = null;

                uiContainer.Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = column_content_width,
                    Direction = FillDirection.Vertical,
                    Children = new Drawable[]
                    {
                        nowPlaying = new NowPlayingPanel(),
                        // Docked (rather than the floating drawer) so the rows are actually on
                        // screen at the width they ship at in the Playback tab.
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 300,
                            Child = queuePanel = new QueuePanel(docked: true),
                        },
                    },
                };
            });
        }

        // The headline of the reworked row: a real percentage backed by real byte counts, not a
        // "downloading…" label that says nothing about how long is left.
        [Test]
        public void RowShowsProgressBarAndPercentageWhileDownloading()
        {
            AddStep("enqueue a set and start its download", () =>
            {
                mirror.Arm(determinate_id, total_bytes);
                queue.Enqueue(new BeatmapSetInfo { Id = determinate_id, Title = "Determinate", Artist = "Artist", Creator = "Mapper" });
                _ = cache.GetAsync(determinate_id);
            });

            AddUntilStep("row starts at 0%", () => queuePanel.ProgressTextAt(0) == "0%");
            AddAssert("bar starts empty", () => queuePanel.ProgressFillAt(0) == 0);

            AddStep("mirror reports 50 of 100 bytes", () => mirror.Report(determinate_id, 50));
            AddUntilStep("row shows 50%", () => queuePanel.ProgressTextAt(0) == "50%");
            AddAssert("bar is half filled", () => Math.Abs(queuePanel.ProgressFillAt(0) - 0.5f) < 0.001f);
            AddAssert("no spinner while a percentage is known", () => !queuePanel.SpinnerShownAt(0));

            AddStep("mirror reports 90 of 100 bytes", () => mirror.Report(determinate_id, 90));
            AddUntilStep("row shows 90%", () => queuePanel.ProgressTextAt(0) == "90%");
            AddAssert("bar is 90% filled", () => Math.Abs(queuePanel.ProgressFillAt(0) - 0.9f) < 0.001f);

            AddStep("let the download finish", () => mirror.Complete(determinate_id));

            // The whole indicator goes away once the set is cached — a queued set that is ready to
            // play carries no ornament at all.
            AddUntilStep("progress clears once the set is cached",
                () => queuePanel.ProgressTextAt(0).Length == 0 && queuePanel.ProgressFillAt(0) == 0 && !queuePanel.SpinnerShownAt(0));
        }

        // Not every mirror sends a Content-Length; with no denominator there is no honest
        // percentage to draw, so the row falls back to lazer's own spinner rather than a bar
        // frozen at 0%.
        [Test]
        public void RowShowsSpinnerWhenTheMirrorSendsNoContentLength()
        {
            AddStep("enqueue a set whose mirror reports no total", () =>
            {
                mirror.Arm(indeterminate_id, null);
                queue.Enqueue(new BeatmapSetInfo { Id = indeterminate_id, Title = "Indeterminate", Artist = "Artist", Creator = "Mapper" });
                _ = cache.GetAsync(indeterminate_id);
            });

            AddUntilStep("spinner shown", () => queuePanel.SpinnerShownAt(0));
            AddAssert("no percentage", () => queuePanel.ProgressTextAt(0).Length == 0);
            AddAssert("no progress bar", () => queuePanel.ProgressFillAt(0) == 0);

            AddStep("mirror streams bytes, still with no total", () => mirror.Report(indeterminate_id, 50));
            AddAssert("still indeterminate", () => queuePanel.SpinnerShownAt(0) && queuePanel.ProgressTextAt(0).Length == 0);

            AddStep("let the download finish", () => mirror.Complete(indeterminate_id));
            AddUntilStep("spinner clears once the set is cached", () => !queuePanel.SpinnerShownAt(0));
        }

        // The queue row tells you about queued sets; this is the same story for the song you are
        // waiting on right now, where "nothing is playing and nothing is moving" would otherwise
        // read as a hang.
        [Test]
        public void CurrentSongIndicatorAppearsWhileDownloadingAndClearsOnPlay()
        {
            AddAssert("no indicator while idle", () => !nowPlaying.DownloadSpinnerShown);

            AddStep("jukebox starts waiting on this set's download", () =>
            {
                mirror.Arm(current_song_id, total_bytes);
                jukebox.Status.Value = "Downloading Current Song…";
                jukebox.DownloadingSetId.Value = current_song_id;
                _ = cache.GetAsync(current_song_id);
            });

            AddUntilStep("spinner shown beside the status line", () => nowPlaying.DownloadSpinnerShown);

            AddStep("mirror reports 42 of 100 bytes", () => mirror.Report(current_song_id, 42));
            AddUntilStep("percentage shown", () => nowPlaying.DownloadPercentText == "42%");
            AddAssert("the status line still names the song", () => nowPlaying.StatusText == "Downloading Current Song…");

            AddStep("download finishes and playback starts", () =>
            {
                mirror.Complete(current_song_id);
                jukebox.Status.Value = null;
                jukebox.DownloadingSetId.Value = null;
            });

            AddUntilStep("indicator cleared",
                () => !nowPlaying.DownloadSpinnerShown && nowPlaying.DownloadPercentText.Length == 0);
        }

        // Builds a minimal but real .osz (a zip containing a *.osu file) so the gated download has
        // something genuine for BeatmapCache to extract and scan once released — mirrors
        // TestSceneNowPlayingPanel.makeOsz.
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

        /// <summary>
        /// A mirror whose downloads are driven entirely by the test rather than by a real transfer:
        /// <see cref="DownloadAsync"/> reports whatever total <see cref="Arm"/> registered, then
        /// parks until <see cref="Complete"/> releases it, with <see cref="Report"/> pushing exact
        /// byte counts through the real progress callback in between. Keyed per set id (rather than
        /// one gate for the whole mirror) so each test drives its own download without racing the
        /// others through the shared <see cref="BeatmapCache"/>.
        /// </summary>
        private class GatedMirror : IBeatmapMirror
        {
            private readonly string oszPath;
            private readonly ConcurrentDictionary<int, TaskCompletionSource> gates = new();
            private readonly ConcurrentDictionary<int, DownloadProgressCallback?> callbacks = new();

            /// <summary>Per-set <c>Content-Length</c>; a registered null emulates a mirror that
            /// sends none (chunked), which is what drives the indeterminate path.</summary>
            private readonly ConcurrentDictionary<int, long?> totals = new();

            public GatedMirror(string oszPath) => this.oszPath = oszPath;

            public string Name => "gated";

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>());

            /// <summary>Registers the total this set's download will advertise. Must be called
            /// before the download starts.</summary>
            public void Arm(int setId, long? totalBytes) => totals[setId] = totalBytes;

            public async Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
            {
                callbacks[setId] = progress;
                progress?.Invoke(0, totals.GetValueOrDefault(setId));

                await gate(setId).Task.ConfigureAwait(false);

                using var fs = File.OpenRead(oszPath);
                await fs.CopyToAsync(destination, ct).ConfigureAwait(false);
            }

            /// <summary>Pushes <paramref name="bytesRead"/> through the live download's progress
            /// callback, against the total <see cref="Arm"/> registered.</summary>
            public void Report(int setId, long bytesRead)
                => callbacks.GetValueOrDefault(setId)?.Invoke(bytesRead, totals.GetValueOrDefault(setId));

            /// <summary>Releases the parked download so it writes the real .osz and completes.</summary>
            public void Complete(int setId) => gate(setId).TrySetResult();

            private TaskCompletionSource gate(int setId)
                => gates.GetOrAdd(setId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }
}
