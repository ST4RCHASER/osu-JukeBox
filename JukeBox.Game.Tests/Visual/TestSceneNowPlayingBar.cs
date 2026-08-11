#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneNowPlayingBar : JukeBoxManualInputTestScene
    {
        private PlaybackController playback = null!;
        private MusicQueue queue = null!;
        private Jukebox jukebox = null!;

        private NowPlayingBar bar = null!;
        private QueuePanel panel = null!;

        private string tmp = null!;
        private CachedBeatmapSet fixtureSet = null!;
        private CachedBeatmapSet fixtureSetLong = null!;
        private BeatmapSetInfo fixtureInfo = null!;

        // CreateChildDependencies runs once for the whole scene (shared across every [Test] in
        // this fixture, see TestSceneSearchOverlay) — playback/jukebox/queue are created here once
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
            var mirror = new EmptyMirror();
            jukebox = new Jukebox(queue, new RadioService(mirror), new BeatmapCache(Path.Combine(tmp, "cache"), mirror), playback);

            var deps = new DependencyContainer(parent);
            deps.CacheAs(playback);
            deps.CacheAs(jukebox);
            deps.CacheAs(queue);
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
