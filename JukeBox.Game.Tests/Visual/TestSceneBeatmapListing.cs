#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Configuration;
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

        // Own throwaway-storage config (this fixture's runner isn't JukeBoxGameBase, so nothing
        // caches one) — the overlay resolves it for the SearchStyle-driven density; tests flip the
        // style through here and SetUpSteps resets it to the default for isolation.
        private JukeBoxConfigManager gameConfig = null!;

        // CreateChildDependencies runs once for the whole scene (shared across every [Test] in
        // this fixture), so StubMirror's contents are reset in SetUpSteps below rather than here
        // — otherwise one test mutating it would leak into another.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var deps = new DependencyContainer(parent);
            deps.CacheAs<IBeatmapMirror>(mirror = new StubMirror());
            deps.Cache(gameConfig = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-listing-test", Path.GetRandomFileName()))));
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
                mirror.PageFactory = null;
                mirror.Sets.AddRange(StubMirror.DefaultSets());
                gameConfig.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Compact);
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

        // Regression test for the client-filter scroll stall: a genre filter can reduce a full
        // raw page to zero visible cards — with nothing to scroll, infinite scroll could never
        // trigger and the user would be stuck on "no results" even though a later page matches.
        // The overlay must auto-chain further page fetches until a match surfaces.
        [Test]
        public void GenreFilterAutoChainsToMatchOnLaterPage()
        {
            // Regular density: these pagination tests were designed around full-height cards
            // (30 of them decisively overflow the viewport, so paging is purely scroll-driven);
            // compact rows in this WIDE two-column host barely overflow, which legitimately trips
            // the near-end threshold without scrolling — a geometry the real (narrow, one-column)
            // docked compact listing never produces.
            AddStep("use regular density", () => gameConfig.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Fullscreen));

            AddStep("mirror pages: page 0 all rock, page 1 contains one anime set", () =>
            {
                mirror.PageFactory = page =>
                {
                    var list = fullRockPage(page);
                    if (page == 1)
                        list[0] = new BeatmapSetInfo { Id = 1000, Title = "Anime Song", Artist = "a", Creator = "c", Status = "ranked", Genre = new NamedIdInfo { Id = 3, Name = "Anime" } };
                    return list;
                };
            });

            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("first page rendered", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 30);

            AddStep("click the 'Anime' genre chip",
                () => overlay.ChildrenOfType<FilterChip>().Single(c => c.Text == "Anime").TriggerClick());

            AddUntilStep("page-1 match auto-loaded without user interaction",
                () => overlay.ChildrenOfType<BeatmapCard>().Any(c => c.Set.Id == 1000));
            AddAssert("page 1 was requested automatically", () => mirror.Requests.Any(r => r.Page == 1));
        }

        [Test]
        public void AutoChainStopsAtCapWhenNothingMatches()
        {
            // Regular density — see GenreFilterAutoChainsToMatchOnLaterPage's first step.
            AddStep("use regular density", () => gameConfig.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Fullscreen));

            AddStep("mirror serves endless all-rock pages", () => mirror.PageFactory = fullRockPage);

            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("first page rendered", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 30);

            AddStep("click the 'Anime' genre chip",
                () => overlay.ChildrenOfType<FilterChip>().Single(c => c.Text == "Anime").TriggerClick());

            AddUntilStep("auto-chained exactly up to the cap (5 pages)",
                () => mirror.Requests.Count(r => r.Page > 0) == 5);
            AddWaitStep("let more frames pass", 5);
            AddAssert("no further requests past the cap", () => mirror.Requests.Count(r => r.Page > 0) == 5);
            AddAssert("still zero visible cards", () => !overlay.ChildrenOfType<BeatmapCard>().Any());
        }

        // Docked mode (the three-column layout's permanent left column) is permanently visible from
        // the moment it's loaded — no pop-in, no Show()/Hide() toggling.
        [Test]
        public void DockedInstanceStartsVisibleWithoutBeingShown()
        {
            BeatmapListingOverlay docked = null!;
            AddStep("create docked overlay", () => Child = docked = new BeatmapListingOverlay(docked: true) { RelativeSizeAxes = Axes.Both });

            AddAssert("starts visible", () => docked.State.Value == Visibility.Visible);
        }

        // Docked's Escape contract: blur the search box, never hide anything (there's nothing to
        // hide — it's a permanent column, not an overlay).
        [Test]
        public void DockedInstanceEscapeUnfocusesWithoutHiding()
        {
            BeatmapListingOverlay docked = null!;
            AddStep("create docked overlay and focus the search box", () =>
            {
                Child = docked = new BeatmapListingOverlay(docked: true) { RelativeSizeAxes = Axes.Both };
            });
            AddStep("focus + seed", () => docked.ShowWithInitialChar('a'));
            AddUntilStep("search box focused", () => docked.SearchBox.HasFocus);

            AddStep("press escape", () => InputManager.Key(Key.Escape));

            AddUntilStep("search box unfocused", () => !docked.SearchBox.HasFocus);
            AddAssert("still visible", () => docked.State.Value == Visibility.Visible);
        }

        // Docked's Enter contract: queues the selection but never closes anything (there being
        // nothing to close), unlike the floating overlay's Enter-closes behaviour.
        [Test]
        public void DockedInstanceEnterQueuesWithoutClosing()
        {
            BeatmapListingOverlay docked = null!;
            BeatmapSetInfo? dockedPicked = null;
            AddStep("create docked overlay", () =>
            {
                Child = docked = new BeatmapListingOverlay(docked: true) { RelativeSizeAxes = Axes.Both };
                docked.SetPicked += set => dockedPicked = set;
            });

            AddStep("type 'a' (focus + seed)", () => docked.ShowWithInitialChar('a'));
            AddUntilStep("3 cards shown", () => docked.ChildrenOfType<BeatmapCard>().Count() == 3);

            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("first set picked", () => dockedPicked?.Id == mirror.Sets[0].Id);
            AddAssert("stays visible (nothing to close)", () => docked.State.Value == Visibility.Visible);
        }

        // The "Filters" expander: under the (default) compact search style the section DEFAULTS
        // to collapsed, reclaiming vertical room for the results list in the narrow docked
        // column. Expanding restores the chip rows without disturbing the chips' own state, and
        // TriggerClick still reaches a collapsed chip (filter changes aren't gated on visibility).
        [Test]
        public void FiltersExpanderCollapsesAndRestoresChipRows()
        {
            // Shown first (as every other search/filter test in this fixture does) — the debounced
            // search's Scheduler only actually runs while the overlay is visible/present.
            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("initial search ran", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);

            AddAssert("filters start collapsed (compact style default)", () => !overlay.FiltersExpanded);

            AddStep("click a filter chip while collapsed (a change must still apply)",
                () => overlay.ChildrenOfType<FilterChip>().Single(c => c.Text == "Loved").TriggerClick());
            AddUntilStep("new search issued with the collapsed section's chip change", () => mirror.LastRequest?.Status == "loved");

            AddStep("click the Filters toggle",
                () => overlay.ChildrenOfType<ClickableContainer>().First(c => c.GetType().Name == "FiltersToggleButton").TriggerClick());
            AddAssert("filters now expanded", () => overlay.FiltersExpanded);

            // The section fades in (see updateFiltersExpanded) rather than snapping — assert
            // the animation itself actually reaches full opacity, not just the instant flag.
            AddUntilStep("filters section fully visible", () => overlay.FiltersBodyAlpha == 1);
            AddAssert("chip selection survived the collapse/expand round-trip",
                () => overlay.ChildrenOfType<FilterChip>().Single(c => c.Text == "Loved").Active.Value);

            AddStep("click the Filters toggle again",
                () => overlay.ChildrenOfType<ClickableContainer>().First(c => c.GetType().Name == "FiltersToggleButton").TriggerClick());
            AddAssert("filters collapsed again", () => !overlay.FiltersExpanded);
        }

        // Regression coverage for search text drawing across/under the search-icon and "#"
        // buttons: the input's own masking is what clips its text, and masking clips at the
        // drawable's BOUNDS — so those bounds must genuinely end before the buttons (the old
        // layout padded a full-row textbox instead, leaving its mask spanning the button area).
        [Test]
        public void LongSearchTextStaysClippedInsideTheInputBounds()
        {
            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("search ran", () => overlay.ChildrenOfType<BeatmapCard>().Any());

            AddStep("fill the box with overflowing text",
                () => overlay.SearchBox.Text = string.Concat(Enumerable.Repeat("Okaerinasai overflow ", 20)));

            AddAssert("the input masks its own text", () => overlay.SearchBox.Masking);
            AddAssert("the input's bounds end before the search-icon button", () =>
            {
                var searchIcon = overlay.ChildrenOfType<IconButton>()
                                        .Single(b => b.Icon.Equals(osu.Framework.Graphics.Sprites.FontAwesome.Solid.Search));
                return overlay.SearchBox.ScreenSpaceDrawQuad.TopRight.X <= searchIcon.ScreenSpaceDrawQuad.TopLeft.X + 0.5f;
            });
            AddAssert("the input's bounds end before the '#' button too", () =>
            {
                var hashtag = overlay.ChildrenOfType<IconButton>()
                                     .Single(b => b.Icon.Equals(osu.Framework.Graphics.Sprites.FontAwesome.Solid.Hashtag));
                return overlay.SearchBox.ScreenSpaceDrawQuad.TopRight.X <= hashtag.ScreenSpaceDrawQuad.TopLeft.X + 0.5f;
            });
        }

        // The SearchStyle setting is live-switchable and drives the docked listing's density:
        // Compact (the default) renders dense half-height rows with small chips and a collapsed
        // filter section; Fullscreen restores the roomier original card presentation here (the
        // big overlay itself is MainScreen's concern, covered in TestSceneMainScreen).
        [Test]
        public void SearchStyleSettingSwitchesDensityLive()
        {
            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);

            AddAssert("compact style renders dense rows", () =>
                overlay.ChildrenOfType<BeatmapCard>().All(c => c.Compact && c.Height == BeatmapCard.COMPACT_HEIGHT));
            AddAssert("chips are dense too", () => overlay.ChildrenOfType<FilterChip>().All(c => c.Compact));

            AddStep("switch the setting to Fullscreen", () => gameConfig.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Fullscreen));

            AddUntilStep("cards rebuilt at regular height", () =>
                overlay.ChildrenOfType<BeatmapCard>().Count() == 3
                && overlay.ChildrenOfType<BeatmapCard>().All(c => !c.Compact && c.Height == BeatmapCard.HEIGHT));
            AddAssert("chips back to regular density", () => overlay.ChildrenOfType<FilterChip>().All(c => !c.Compact));
            AddAssert("filters default expanded under fullscreen style", () => overlay.FiltersExpanded);

            AddStep("switch back to Compact", () => gameConfig.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Compact));

            AddUntilStep("dense rows again", () =>
                overlay.ChildrenOfType<BeatmapCard>().Count() == 3
                && overlay.ChildrenOfType<BeatmapCard>().All(c => c.Compact && c.Height == BeatmapCard.COMPACT_HEIGHT));
            AddAssert("filters collapsed again (compact default)", () => !overlay.FiltersExpanded);
        }

        // "grid -> 1-col in narrow width": the docked left column (~380px, minus panel padding)
        // never reaches the two-column threshold, so results render single-file there, while a wide
        // host (like this test's own full-size Child) still gets two per row.
        [Test]
        public void CardsRenderSingleColumnWhenNarrow()
        {
            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("3 cards shown", () => overlay.ChildrenOfType<BeatmapCard>().Count() == 3);
            AddAssert("wide host renders two columns", () =>
            {
                var cards = overlay.ChildrenOfType<BeatmapCard>().ToList();
                return Math.Abs(cards[0].Width - cards[1].Width) < 0.5f && cards[0].Width < overlay.DrawWidth;
            });

            AddStep("narrow the host to the docked column's rough width", () =>
            {
                // Switch to an absolute width — RelativeSizeAxes.X (this scene's default, matching
                // how the standalone/full-visuals overlay is hosted) would otherwise reinterpret
                // 380 as 380x the parent's width instead of 380px.
                overlay.RelativeSizeAxes = Axes.Y;
                overlay.Width = 380;
            });
            // Compared against the cards' own flow container, not the overlay itself — the flow
            // sits inside the overlay's own padding, so its DrawWidth is narrower than the
            // overlay's.
            AddUntilStep("cards now span the full (narrow) width", () =>
            {
                float flowWidth = overlay.ChildrenOfType<FillFlowContainer<BeatmapCard>>().Single().DrawWidth;
                return overlay.ChildrenOfType<BeatmapCard>().All(c => Math.Abs(c.Width - flowWidth) < 0.5f);
            });
        }

        private static List<BeatmapSetInfo> fullRockPage(int page)
        {
            var list = new List<BeatmapSetInfo>();

            // 30 == the overlay's page size, so every page reads as "more available".
            for (int i = 0; i < 30; i++)
                list.Add(new BeatmapSetInfo { Id = page * 100 + i + 1, Title = $"Rock {page}-{i}", Artist = "a", Creator = "c", Status = "ranked", Genre = new NamedIdInfo { Id = 4, Name = "Rock" } });

            return list;
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
            // Regular density — see GenreFilterAutoChainsToMatchOnLaterPage's first step.
            AddStep("use regular density", () => gameConfig.SetValue(JukeBoxSetting.SearchStyle, SearchStyle.Fullscreen));

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

            /// <summary>When set, produces the response for a given page number instead of
            /// <see cref="Sets"/> — for pagination/auto-chain tests.</summary>
            public Func<int, List<BeatmapSetInfo>>? PageFactory;

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
                return Task.FromResult(PageFactory?.Invoke(request.Page) ?? new List<BeatmapSetInfo>(Sets));
            }

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
