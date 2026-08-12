#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneBeatmapListing : ManualInputManagerTestScene
    {
        private BeatmapListingOverlay overlay = null!;
        private StubMirror mirror = null!;
        private BeatmapSetInfo? picked;

        // CreateChildDependencies runs once for the whole scene (shared across every [Test] in
        // this fixture), so StubMirror's contents are reset in SetUpSteps below rather than here
        // — otherwise one test mutating it would leak into another.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var deps = new DependencyContainer(parent);
            deps.CacheAs<IBeatmapMirror>(mirror = new StubMirror());
            return deps;
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create overlay", () =>
            {
                picked = null;
                mirror.Sets.Clear();
                mirror.Requests.Clear();
                mirror.Sets.AddRange(StubMirror.DefaultSets());
                Child = overlay = new BeatmapListingOverlay { RelativeSizeAxes = Axes.Both };
                overlay.SetPicked += set => picked = set;
            });
        }

        [Test]
        public void TypingShowsCardsAndEnterQueuesFirst()
        {
            AddStep("type 'a' (opens + seeds keyword box)", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("3 cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);

            AddStep("press enter", () => InputManager.Key(Key.Enter));
            AddUntilStep("first set picked", () => picked?.Id == mirror.Sets[0].Id);
            AddAssert("overlay hidden (keyboard flow closes it)", () => overlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void ClickingCardQueuesButKeepsListingOpen()
        {
            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("3 cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);

            AddStep("click the first card", () =>
            {
                var card = overlay.ChildrenOfType<BeatmapCard>().First();
                InputManager.MoveMouseTo(card);
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("set picked", () => picked != null);
            AddAssert("overlay still visible (mouse flow keeps it open)", () => overlay.State.Value == Visibility.Visible);
        }

        [Test]
        public void CategoryChipChangeTriggersNewSearch()
        {
            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("initial search ran", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);
            AddAssert("initial request used the ranked default", () => mirror.LastRequest!.Status == "ranked");

            AddStep("click the 'Loved' category chip",
                () => overlay.ChildrenOfType<FilterChip>().Single(c => c.Text == "Loved").TriggerClick());

            AddUntilStep("new search issued with s=loved", () => mirror.LastRequest?.Status == "loved");
            AddAssert("new search restarted at page 0", () => mirror.LastRequest!.Page == 0);
        }

        [Test]
        public void ModeChipMapsToSingleLetterModeParam()
        {
            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("initial search ran", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);
            AddAssert("initial request has no mode", () => mirror.LastRequest!.Mode == null);

            AddStep("click the 'mania' mode chip",
                () => overlay.ChildrenOfType<FilterChip>().Single(c => c.Text == "mania").TriggerClick());

            AddUntilStep("new search issued with m=m", () => mirror.LastRequest?.Mode == "m");
        }

        [Test]
        public void GenreChipFiltersLoadedResultsWithoutNewRequest()
        {
            AddStep("mirror serves one anime and one rock set", () =>
            {
                mirror.Sets.Clear();
                mirror.Sets.Add(new BeatmapSetInfo { Id = 1, Title = "Anime Song", Artist = "a", Creator = "c", Status = "ranked", Genre = new NamedIdInfo { Id = 3, Name = "Anime" } });
                mirror.Sets.Add(new BeatmapSetInfo { Id = 2, Title = "Rock Song", Artist = "a", Creator = "c", Status = "ranked", Genre = new NamedIdInfo { Id = 4, Name = "Rock" } });
            });

            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("2 cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 2);

            int requestsBefore = 0;
            AddStep("record request count", () => requestsBefore = mirror.Requests.Count);

            AddStep("click the 'Anime' genre chip",
                () => overlay.ChildrenOfType<FilterChip>().Single(c => c.Text == "Anime").TriggerClick());

            AddUntilStep("only the anime set remains", () =>
                overlay.ChildrenOfType<BeatmapCard>().Count() == 1 && overlay.ChildrenOfType<BeatmapCard>().Single().Set.Id == 1);
            AddAssert("no new request was issued (client-side filter)", () => mirror.Requests.Count == requestsBefore);
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

            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("1 card shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 1);

            BeatmapCard card = null!;
            AddStep("grab the card", () => card = overlay.ChildrenOfType<BeatmapCard>().Single());
            AddAssert("card reports disabled (framework-level, not just dimmed)", () => card.Enabled.Value == false);

            AddStep("click the disabled card", () =>
            {
                InputManager.MoveMouseTo(card);
                InputManager.Click(MouseButton.Left);
            });

            AddAssert("no set was picked", () => picked == null);
            AddAssert("overlay still visible (click was a no-op)", () => overlay.State.Value == Visibility.Visible);
        }

        [Test]
        public void ScrollingToBottomRequestsNextPage()
        {
            AddStep("mirror serves a full page of sets", () =>
            {
                mirror.Sets.Clear();
                // 30 == the overlay's page size: a full first page marks more results available.
                for (int i = 1; i <= 30; i++)
                    mirror.Sets.Add(new BeatmapSetInfo { Id = i, Title = $"Song {i}", Artist = "a", Creator = "c", Status = "ranked" });
            });

            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("first page rendered", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 30);
            AddAssert("only page 0 requested so far", () => mirror.Requests.All(r => r.Page == 0));

            AddStep("scroll to the bottom", () => overlay.ChildrenOfType<BasicScrollContainer>().Single().ScrollToEnd(false));

            // >= rather than == : reaching the bottom of the appended page can legitimately chain
            // straight into the next one (the scroll target is still at the end while the longer
            // content is being laid out), so more than one extra page may load.
            AddUntilStep("page 1 requested", () => mirror.Requests.Any(r => r.Page == 1));
            AddUntilStep("second page appended", () => overlay.ChildrenOfType<BeatmapCard>().Count() >= 60);
        }

        // Serves fixed sets for any query (recording every request) — enough to exercise the
        // debounce → search → render pipeline and the filter/pagination request shapes without
        // touching the network.
        private class StubMirror : IBeatmapMirror
        {
            public string Name => "stub";

            public List<BeatmapSetInfo> Sets { get; } = new();

            public List<SearchRequest> Requests { get; } = new();

            public SearchRequest? LastRequest => Requests.Count == 0 ? null : Requests[^1];

            public static List<BeatmapSetInfo> DefaultSets() => new()
            {
                new BeatmapSetInfo { Id = 1, Title = "Alpha Song", Artist = "Artist A", Creator = "mapperA", Status = "ranked" },
                new BeatmapSetInfo { Id = 2, Title = "Beta Song", Artist = "Artist B", Creator = "mapperB", Status = "ranked" },
                new BeatmapSetInfo { Id = 3, Title = "Gamma Song", Artist = "Artist C", Creator = "mapperC", Status = "ranked" },
            };

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                Requests.Add(request);
                return Task.FromResult(new List<BeatmapSetInfo>(Sets));
            }

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
