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
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using osuTK.Input;
using LoadingLayer = osu.Game.Graphics.UserInterface.LoadingLayer;
using LoadingSpinner = osu.Game.Graphics.UserInterface.LoadingSpinner;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Covers the compact sidebar (<see cref="BeatmapListingOverlay"/>) after the search rework:
    /// it is a RESULTS-ONLY view over the shared engine — no keyword box, no filter section — with
    /// a search button that only asks its host to open the fullscreen listing and a "#" button
    /// that only asks for the map-ID dialog. Everything that used to be driven by its own chips is
    /// driven straight on the engine here, which is exactly how the fullscreen listing drives it.
    /// </summary>
    [TestFixture]
    public partial class TestSceneBeatmapListing : JukeBoxManualInputTestScene
    {
        /// <summary>The real docked column's width — every test hosts the sidebar at it, so the
        /// single-column/paging geometry under test is the one that actually ships.</summary>
        private const float docked_column_width = 380;

        private BeatmapListingOverlay overlay = null!;

        /// <summary>The sidebar sizes itself relatively (its BDL sets RelativeSizeAxes.Both), so
        /// the column width under test is imposed by this host rather than on the overlay itself
        /// — exactly how MainScreen's left column does it.</summary>
        private Container host = null!;

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
            AddStep("create sidebar", () =>
            {
                picked = null;
                mirror.Sets.Clear();
                mirror.Requests.Clear();
                mirror.PageFactory = null;
                mirror.Gate = null;
                mirror.GatePage = -1;
                mirror.Sets.AddRange(StubMirror.DefaultSets());
                Child = host = new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = docked_column_width,
                    Child = overlay = new BeatmapListingOverlay(docked: true),
                };
                overlay.SetPicked += set => picked = set;
            });
        }

        /// <summary>Drives a search the way the app now does — on the shared engine, from the
        /// fullscreen listing's keyword box — rather than through a sidebar box that no longer
        /// exists.</summary>
        private void search(string query) => AddStep($"engine searches '{query}'", () => overlay.Engine.Query.Value = query);

        // The whole point of the rework: the sidebar is results-only. No text input and no filter
        // controls of any kind live in it any more.
        [Test]
        public void SidebarHasNoSearchBoxAndNoFilterControls()
        {
            AddAssert("no text box in the sidebar", () => !overlay.ChildrenOfType<TextBox>().Any());
            AddAssert("no filter row labels or chips", () => !overlay.ChildrenOfType<ClickableContainer>()
                                                                    .Any(c => c.GetType().Name.Contains("Chip") || c.GetType().Name.Contains("Filter")));
            AddAssert("exactly two buttons in the top row", () =>
                overlay.ChildrenOfType<BeatmapListingOverlay.SearchButton>().Count() == 1
                && overlay.ChildrenOfType<IconButton>().Count(b => b.Icon.Equals(FontAwesome.Solid.Hashtag)) == 1);
        }

        // The search button's ONLY job is to ask the host to open the fullscreen listing; it does
        // not search, focus or filter anything itself.
        [Test]
        public void SearchButtonRaisesSearchOpenRequested()
        {
            int opened = 0;
            AddStep("listen for open requests", () => overlay.SearchOpenRequested += () => opened++);

            AddStep("click the search button", () => overlay.ChildrenOfType<BeatmapListingOverlay.SearchButton>().Single().TriggerClick());

            AddAssert("host was asked to open search exactly once", () => opened == 1);
            AddAssert("no request was issued by the click itself", () => mirror.Requests.Count == 0);
        }

        [Test]
        public void HashButtonRaisesMapIdRequested()
        {
            int requested = 0;
            AddStep("listen for map-id requests", () => overlay.MapIdRequested += () => requested++);

            AddStep("click the # button", () => overlay.ChildrenOfType<IconButton>()
                                                       .Single(b => b.Icon.Equals(FontAwesome.Solid.Hashtag)).TriggerClick());

            AddAssert("host was asked for the map-id dialog exactly once", () => requested == 1);
        }

        // The sidebar renders whatever the shared engine last produced — which is what makes the
        // results survive the fullscreen listing being closed.
        [Test]
        public void SidebarRendersWhateverTheEngineLastProduced()
        {
            search("a");
            AddUntilStep("3 cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);
            AddAssert("cards are the dense variant", () =>
                overlay.ChildrenOfType<BeatmapCard>().All(c => c.Compact && c.Height == BeatmapCard.COMPACT_HEIGHT));
        }

        [Test]
        public void EnterQueuesTheFirstCardWithoutClosingWhenDocked()
        {
            search("a");
            AddUntilStep("3 cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);

            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("first set picked", () => picked?.Id == mirror.Sets[0].Id);
            AddAssert("stays visible (nothing to close when docked)", () => overlay.State.Value == Visibility.Visible);
        }

        [Test]
        public void ClickingCardQueuesButKeepsListingOpen()
        {
            search("a");
            AddUntilStep("3 cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);

            AddStep("click the first card", () =>
            {
                var card = overlay.ChildrenOfType<BeatmapCard>().First();
                InputManager.MoveMouseTo(card);
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("set picked", () => picked != null);
            AddAssert("sidebar still visible (mouse flow keeps it open)", () => overlay.State.Value == Visibility.Visible);
        }

        // Docked mode (the three-column layout's permanent left column) is permanently visible from
        // the moment it's loaded — no pop-in, no Show()/Hide() toggling.
        [Test]
        public void DockedInstanceStartsVisibleWithoutBeingShown()
        {
            AddAssert("starts visible", () => overlay.State.Value == Visibility.Visible);
        }

        // Escape must be swallowed rather than left unhandled: an unhandled Escape reaches the
        // input manager's clear-focus fallback, which hides a FocusedOverlayContainer — and this
        // one is a permanent column that must never disappear.
        [Test]
        public void EscapeNeverHidesTheDockedColumn()
        {
            AddStep("press escape", () => InputManager.Key(Key.Escape));
            AddWaitStep("let any hide animation run", 5);
            AddAssert("still visible", () => overlay.State.Value == Visibility.Visible);
        }

        // The floating (non-docked) variant keeps its old overlay semantics, since it's still the
        // shape the class supports for standalone hosting.
        [Test]
        public void FloatingInstanceEnterQueuesAndCloses()
        {
            BeatmapListingOverlay floating = null!;
            BeatmapSetInfo? floatingPicked = null;

            AddStep("create floating sidebar", () =>
            {
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = docked_column_width,
                    Child = floating = new BeatmapListingOverlay(),
                };
                floating.SetPicked += set => floatingPicked = set;
                floating.Show();
            });

            AddStep("engine searches 'a'", () => floating.Engine.Query.Value = "a");
            AddUntilStep("3 cards shown", () => floating.ChildrenOfType<BeatmapCard>().Count() == 3);

            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("first set picked", () => floatingPicked?.Id == mirror.Sets[0].Id);
            AddUntilStep("overlay hidden (keyboard flow closes it)", () => floating.State.Value == Visibility.Hidden);
        }

        // Fetches are spinners now, never text: a fresh search covers the whole result area, a
        // page append only puts a small spinner in the footer.
        [Test]
        public void FreshSearchShowsTheLoadingLayerUntilResultsArrive()
        {
            var gate = new TaskCompletionSource<bool>();

            AddStep("mirror gated on a pending task", () => mirror.Gate = gate);

            search("a");

            AddUntilStep("loading layer covers the results", () => loadingLayer().State.Value == Visibility.Visible);
            AddAssert("its spinner is spinning", () => overlay.ChildrenOfType<LoadingSpinner>().Any(s => s.State.Value == Visibility.Visible));
            AddAssert("no progress TEXT — the spinner is the progress", () => overlay.Engine.Status.Value.Length == 0);

            AddStep("release the gate", () => gate.SetResult(true));

            AddUntilStep("cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);
            AddUntilStep("loading layer gone", () => loadingLayer().State.Value == Visibility.Hidden);
        }

        [Test]
        public void LoadMoreShowsAFooterSpinnerRatherThanCoveringTheResults()
        {
            var gate = new TaskCompletionSource<bool>();

            AddStep("mirror serves full pages, gating page 1", () =>
            {
                mirror.PageFactory = fullPage;
                mirror.Gate = gate;
                mirror.GatePage = 1;
            });

            search("a");
            AddUntilStep("first page rendered", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 30);

            AddStep("scroll to the bottom", () => overlay.ChildrenOfType<BasicScrollContainer>().Single().ScrollToEnd(false));

            AddUntilStep("page 1 requested", () => mirror.Requests.Any(r => r.Page == 1));
            AddAssert("results are NOT covered (append, not replace)", () => loadingLayer().State.Value == Visibility.Hidden);
            AddAssert("a footer spinner is spinning", () => overlay.ChildrenOfType<LoadingSpinner>().Any(s => s.State.Value == Visibility.Visible));
            AddAssert("the first page's cards are still on screen", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 30);

            AddStep("release the gate", () => gate.SetResult(true));
            AddUntilStep("second page appended", () => overlay.ChildrenOfType<BeatmapCard>().Count() >= 60);
            AddUntilStep("footer spinner gone", () => overlay.ChildrenOfType<LoadingSpinner>().All(s => s.State.Value == Visibility.Hidden));
        }

        private LoadingLayer loadingLayer() => overlay.ChildrenOfType<LoadingLayer>().Single();

        [Test]
        public void ScrollingToBottomRequestsNextPage()
        {
            AddStep("mirror serves full pages", () => mirror.PageFactory = fullPage);

            search("a");
            AddUntilStep("first page rendered", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 30);
            AddAssert("only page 0 requested so far", () => mirror.Requests.All(r => r.Page == 0));

            AddStep("scroll to the bottom", () => overlay.ChildrenOfType<BasicScrollContainer>().Single().ScrollToEnd(false));

            // >= rather than == : reaching the bottom of the appended page can legitimately chain
            // straight into the next one (the scroll target is still at the end while the longer
            // content is being laid out), so more than one extra page may load.
            AddUntilStep("page 1 requested", () => mirror.Requests.Any(r => r.Page == 1));
            AddUntilStep("second page appended", () => overlay.ChildrenOfType<BeatmapCard>().Count() >= 60);
        }

        // Regression test for the client-filter scroll stall: a genre filter can reduce a full raw
        // page to zero visible cards — with nothing to scroll, infinite scroll could never trigger
        // and the user would be stuck on "no results" even though a later page matches. The view
        // must auto-chain further page fetches until a match surfaces. The filter itself is now set
        // in the fullscreen listing, so it's driven straight on the shared engine here.
        [Test]
        public void GenreFilterAutoChainsToMatchOnLaterPage()
        {
            AddStep("mirror pages: page 0 all rock, page 1 contains one anime set", () =>
            {
                mirror.PageFactory = page =>
                {
                    var list = fullPage(page);
                    if (page == 1)
                        list[0] = new BeatmapSetInfo { Id = 1000, Title = "Anime Song", Artist = "a", Creator = "c", Status = "ranked", Genre = new NamedIdInfo { Id = 3, Name = "Anime" } };
                    return list;
                };
            });

            search("a");
            AddUntilStep("first page rendered", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 30);

            AddStep("engine filters to Anime", () => overlay.Engine.GenreId.Value = 3);

            AddUntilStep("page-1 match auto-loaded without user interaction",
                () => overlay.ChildrenOfType<BeatmapCard>().Any(c => c.Set.Id == 1000));
            AddAssert("page 1 was requested automatically", () => mirror.Requests.Any(r => r.Page == 1));
        }

        [Test]
        public void AutoChainStopsAtCapWhenNothingMatches()
        {
            AddStep("mirror serves endless all-rock pages", () => mirror.PageFactory = fullPage);

            search("a");
            AddUntilStep("first page rendered", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 30);

            AddStep("engine filters to Anime", () => overlay.Engine.GenreId.Value = 3);

            AddUntilStep("auto-chained exactly up to the cap (5 pages)",
                () => mirror.Requests.Count(r => r.Page > 0) == 5);
            AddWaitStep("let more frames pass", 5);
            AddAssert("no further requests past the cap", () => mirror.Requests.Count(r => r.Page > 0) == 5);
            AddAssert("still zero visible cards", () => !overlay.ChildrenOfType<BeatmapCard>().Any());
        }

        // "grid -> 1-col in narrow width": the docked left column never reaches the two-column
        // threshold, so results render single-file there, while a wide host still gets two per row.
        [Test]
        public void CardsRenderSingleColumnWhenNarrowAndTwoWhenWide()
        {
            search("a");
            AddUntilStep("3 cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);

            // Compared against the cards' own flow container, not the overlay itself — the flow
            // sits inside the overlay's own padding, so its DrawWidth is narrower.
            AddUntilStep("cards span the full (narrow) width", () =>
            {
                float flowWidth = overlay.ChildrenOfType<FillFlowContainer<BeatmapCard>>().Single().DrawWidth;
                return overlay.ChildrenOfType<BeatmapCard>().All(c => Math.Abs(c.Width - flowWidth) < 0.5f);
            });

            AddStep("widen the host past the two-column threshold", () => host.Width = 900);

            AddUntilStep("wide host renders two columns", () =>
            {
                float flowWidth = overlay.ChildrenOfType<FillFlowContainer<BeatmapCard>>().Single().DrawWidth;
                return overlay.ChildrenOfType<BeatmapCard>().All(c => Math.Abs(c.Width - flowWidth / 2) < 0.5f);
            });
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

            search("a");
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
            AddAssert("sidebar still visible (click was a no-op)", () => overlay.State.Value == Visibility.Visible);
        }

        private static List<BeatmapSetInfo> fullPage(int page)
        {
            var list = new List<BeatmapSetInfo>();

            // 30 == the engine's page size, so every page reads as "more available".
            for (int i = 0; i < 30; i++)
                list.Add(new BeatmapSetInfo { Id = page * 100 + i + 1, Title = $"Rock {page}-{i}", Artist = "a", Creator = "c", Status = "ranked", Genre = new NamedIdInfo { Id = 4, Name = "Rock" } });

            return list;
        }

        // Serves fixed sets for any query (recording every request) — enough to exercise the
        // debounce → search → render pipeline and the pagination request shapes without touching
        // the network. Gate holds a response open so the in-flight spinners can be asserted.
        private class StubMirror : IBeatmapMirror
        {
            public string Name => "stub";

            public List<BeatmapSetInfo> Sets { get; } = new();

            public List<SearchRequest> Requests { get; } = new();

            /// <summary>When set, produces the response for a given page number instead of
            /// <see cref="Sets"/> — for pagination/auto-chain tests.</summary>
            public Func<int, List<BeatmapSetInfo>>? PageFactory;

            /// <summary>When set, responses block on this until the test releases it.</summary>
            public TaskCompletionSource<bool>? Gate;

            /// <summary>Restricts <see cref="Gate"/> to one page number; -1 gates every page.</summary>
            public int GatePage = -1;

            public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                Requests.Add(request);

                if (Gate != null && (GatePage < 0 || request.Page == GatePage))
                    await Gate.Task.ConfigureAwait(false);

                return PageFactory?.Invoke(request.Page) ?? new List<BeatmapSetInfo>(Sets);
            }

            public static List<BeatmapSetInfo> DefaultSets() => new()
            {
                new BeatmapSetInfo { Id = 1, Title = "Alpha Song", Artist = "Artist A", Creator = "mapperA", Status = "ranked" },
                new BeatmapSetInfo { Id = 2, Title = "Beta Song", Artist = "Artist B", Creator = "mapperB", Status = "ranked" },
                new BeatmapSetInfo { Id = 3, Title = "Gamma Song", Artist = "Artist C", Creator = "mapperC", Status = "ranked" },
            };

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
