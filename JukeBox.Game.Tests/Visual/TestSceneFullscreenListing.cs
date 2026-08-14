#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Covers the fullscreen search style's big listing (<see cref="FullscreenListingOverlay"/>):
    /// it must be a pure view over the docked listing's shared <see cref="BeatmapSearchEngine"/>
    /// (query synced both ways), expand cards on hover with real per-difficulty rows from
    /// <c>beatmaps[]</c>, keep the Enter-queue/Escape contracts, and drive previews through
    /// <see cref="PreviewPlayer"/> without ever wedging the main playback (paused during a
    /// preview, resumed after; preview track disposed on overlay close/next preview). Preview
    /// track loading is stubbed (network-less) to assert the requested URL.
    /// </summary>
    [TestFixture]
    public partial class TestSceneFullscreenListing : JukeBoxManualInputTestScene
    {
        private BeatmapListingOverlay docked = null!;
        private FullscreenListingOverlay fullscreen = null!;
        private StubMirror mirror = null!;
        private PlaybackController playback = null!;
        private BeatmapSetInfo? picked;

        private readonly List<string> requestedPreviewUrls = new List<string>();
        private readonly List<DisposalHandle> previewHandles = new List<DisposalHandle>();

        /// <summary>
        /// Stands in for the preview's track store in the stubbed loader — PreviewPlayer disposes
        /// it synchronously with the track it produced, so its flag is the observable proxy for
        /// "the previous preview's resources were released". (A standalone TrackVirtual's own
        /// IsDisposed can't be asserted: audio components defer disposal to the audio thread's
        /// update queue, which never processes a track that was never routed through it.)
        /// </summary>
        private class DisposalHandle : IDisposable
        {
            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var deps = new DependencyContainer(parent);
            deps.CacheAs<IBeatmapMirror>(mirror = new StubMirror());
            deps.CacheAs(playback = new PlaybackController());
            return deps;
        }

        private Container uiContainer = null!;

        // playback added exactly once, here — NOT inside SetUpSteps, which rebuilds uiContainer's
        // content on every [Test]: assigning this scene's own Child would clear AND DISPOSE the
        // controller, leaving a zombie whose clock never processes another frame. Same pattern
        // (and reasoning) as TestSceneMainScreen/TestSceneNowPlayingBar.
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Add(playback);
            Add(uiContainer = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create docked + fullscreen views over one engine", () =>
            {
                picked = null;
                requestedPreviewUrls.Clear();
                previewHandles.Clear();
                mirror.Sets.Clear();
                mirror.Sets.AddRange(defaultSets());

                if (playback.IsPlaying)
                    playback.TogglePause();

                docked = new BeatmapListingOverlay(docked: true) { RelativeSizeAxes = Axes.Both };

                uiContainer.Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        docked,
                        fullscreen = new FullscreenListingOverlay(docked.Engine) { RelativeSizeAxes = Axes.Both },
                    },
                };

                fullscreen.SetPicked += set => picked = set;

                // Network-less preview seam: record the URL and serve a virtual 30s track.
                fullscreen.Preview.LoadTrack = url =>
                {
                    var handle = new DisposalHandle();

                    lock (requestedPreviewUrls)
                    {
                        requestedPreviewUrls.Add(url);
                        previewHandles.Add(handle);
                    }

                    return (handle, new TrackVirtual(30000));
                };
            });
        }

        [Test]
        public void TypingShowsThreeColumnCardsAndSyncsQueryToDockedBox()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            AddAssert("query synced into the docked view (one shared engine)", () => docked.SearchBox.Text == "a");
            AddAssert("docked view rendered the same results", () => docked.ChildrenOfType<BeatmapCard>().Count() == 3);

            // The wide test host must actually get the osu-web-style 3-column grid.
            AddAssert("three cards per row", () =>
            {
                var cards = fullscreen.ChildrenOfType<FullscreenBeatmapCard>().ToList();
                var flowWidth = fullscreen.ChildrenOfType<FillFlowContainer<FullscreenBeatmapCard>>().Single().DrawWidth;
                return cards.All(c => Math.Abs(c.Width - flowWidth / 3) < 0.5f);
            });
        }

        [Test]
        public void HoveringACardExpandsItsDifficultyList()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            FullscreenBeatmapCard card = null!;
            AddStep("grab the 3-difficulty card", () => card = fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Single(c => c.Set.Id == 1));

            AddAssert("expansion hidden before hover", () => card.ExpandedPanel.Alpha == 0);

            AddStep("hover the card", () => InputManager.MoveMouseTo(card));
            AddUntilStep("expansion fully visible", () => card.ExpandedPanel.Alpha == 1);

            AddAssert("one row per difficulty, sourced from beatmaps[]", () =>
                card.ExpandedPanel.ChildrenOfType<FullscreenBeatmapCard.DifficultyRow>().Count() == card.Set.Beatmaps.Count);
            AddAssert("rows sorted ascending by stars", () =>
            {
                var stars = card.ExpandedPanel.ChildrenOfType<FullscreenBeatmapCard.DifficultyRow>()
                                .Select(r => r.Beatmap.DifficultyRating).ToList();
                return stars.SequenceEqual(stars.OrderBy(s => s));
            });

            AddStep("move the mouse away", () => InputManager.MoveMouseTo(fullscreen.SearchBox));
            AddUntilStep("expansion hidden again", () => card.ExpandedPanel.Alpha == 0);
        }

        [Test]
        public void PreviewRequestsCorrectUrlAndPausesMainPlaybackUntilStopped()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            // The decoupled clock runs source-less, so the controller reads as playing without
            // needing a real audio file — enough to exercise the pause/resume contract.
            AddStep("start main playback (decoupled clock)", () => playback.TogglePause());
            AddAssert("main playback running", () => playback.IsPlaying);

            AddStep("click the preview button on set 1",
                () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Single(c => c.Set.Id == 1).PreviewButton.TriggerClick());

            AddUntilStep("preview playing for set 1", () => fullscreen.Preview.PlayingSetId.Value == 1);
            AddAssert("track was requested from the official preview URL",
                () => requestedPreviewUrls.SequenceEqual(new[] { "https://b.ppy.sh/preview/1.mp3" }));
            AddAssert("main playback paused for the preview", () => !playback.IsPlaying);

            // Switching previews must dispose the previous track (never two audible at once,
            // never a leak) and keep the main playback paused.
            AddStep("click the preview button on set 2",
                () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Single(c => c.Set.Id == 2).PreviewButton.TriggerClick());
            AddUntilStep("preview switched to set 2", () => fullscreen.Preview.PlayingSetId.Value == 2);
            AddAssert("set 2's URL requested too",
                () => requestedPreviewUrls.SequenceEqual(new[] { "https://b.ppy.sh/preview/1.mp3", "https://b.ppy.sh/preview/2.mp3" }));
            AddUntilStep("first preview's resources disposed", () => previewHandles[0].Disposed);
            AddAssert("main playback still paused", () => !playback.IsPlaying);

            // Clicking the active preview's button again toggles it off.
            AddStep("click set 2's preview button again",
                () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Single(c => c.Set.Id == 2).PreviewButton.TriggerClick());
            AddUntilStep("preview stopped", () => fullscreen.Preview.PlayingSetId.Value == null);
            AddUntilStep("second preview's resources disposed", () => previewHandles[1].Disposed);
            AddAssert("main playback resumed", () => playback.IsPlaying);
        }

        [Test]
        public void ClosingOverlayStopsPreviewAndResumesMainPlayback()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            AddStep("start main playback (decoupled clock)", () => playback.TogglePause());

            AddStep("start a preview",
                () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Single(c => c.Set.Id == 1).PreviewButton.TriggerClick());
            AddUntilStep("preview playing", () => fullscreen.Preview.PlayingSetId.Value == 1);
            AddAssert("main playback paused", () => !playback.IsPlaying);

            AddStep("close the overlay", () => fullscreen.Hide());

            AddAssert("preview stopped with the overlay", () => fullscreen.Preview.PlayingSetId.Value == null);
            AddUntilStep("preview's resources disposed", () => previewHandles[0].Disposed);
            AddAssert("main playback resumed", () => playback.IsPlaying);
        }

        [Test]
        public void EnterQueuesSelectionAndClosesBackToPlayer()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("first set picked", () => picked?.Id == mirror.Sets[0].Id);
            AddAssert("overlay closed back to the player", () => fullscreen.State.Value == Visibility.Hidden);
        }

        [Test]
        public void ClickingACardQueuesButKeepsTheListingOpen()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            AddStep("click the first card", () =>
            {
                var card = fullscreen.ChildrenOfType<FullscreenBeatmapCard>().First();
                InputManager.MoveMouseTo(card);
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("set picked", () => picked != null);
            AddAssert("overlay still open (mouse flow keeps it browsing)", () => fullscreen.State.Value == Visibility.Visible);
        }

        [Test]
        public void DisabledSetCardIsNotClickable()
        {
            AddStep("mirror returns a single download-disabled set", () =>
            {
                mirror.Sets.Clear();
                mirror.Sets.Add(new BeatmapSetInfo
                {
                    Id = 99,
                    Title = "Locked Song",
                    Artist = "Artist L",
                    Creator = "mapperL",
                    Status = "ranked",
                    Availability = new AvailabilityInfo { DownloadDisabled = true },
                });
            });

            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("1 card shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 1);

            FullscreenBeatmapCard card = null!;
            AddStep("grab the card", () => card = fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Single());
            AddAssert("card reports disabled", () => card.Enabled.Value == false);

            AddStep("click the disabled card", () =>
            {
                InputManager.MoveMouseTo(card);
                InputManager.Click(MouseButton.Left);
            });

            AddAssert("no set was picked", () => picked == null);
        }

        private static List<BeatmapSetInfo> defaultSets() => new()
        {
            new BeatmapSetInfo
            {
                Id = 1, Title = "Alpha Song", Artist = "Artist A", Creator = "mapperA", Status = "ranked",
                PlayCount = 774, FavouriteCount = 19, RankedDate = new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero),
                Beatmaps = new List<BeatmapInfo>
                {
                    // Deliberately unsorted — the expansion must sort ascending by stars.
                    new BeatmapInfo { Id = 11, Mode = "osu", Version = "Insane", DifficultyRating = 5.06 },
                    new BeatmapInfo { Id = 12, Mode = "osu", Version = "Hard", DifficultyRating = 3.14 },
                    new BeatmapInfo { Id = 13, Mode = "taiko", Version = "Oni", DifficultyRating = 4.2 },
                },
            },
            new BeatmapSetInfo
            {
                Id = 2, Title = "Beta Song", Artist = "Artist B", Creator = "mapperB", Status = "loved",
                Beatmaps = new List<BeatmapInfo>
                {
                    new BeatmapInfo { Id = 21, Mode = "mania", Version = "4K Normal", DifficultyRating = 2.2 },
                },
            },
            new BeatmapSetInfo { Id = 3, Title = "Gamma Song", Artist = "Artist C", Creator = "mapperC", Status = "ranked" },
        };

        private class StubMirror : IBeatmapMirror
        {
            public string Name => "stub";

            public List<BeatmapSetInfo> Sets { get; } = new();

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>(Sets));

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
