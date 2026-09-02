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
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays.BeatmapListing;
using osuTK.Input;
using SearchExtra = osu.Game.Overlays.BeatmapListing.SearchExtra;

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
                mirror.Gate = null;

                // Fixture-scoped mirror: a test that degraded its capability must not leave the
                // next one's filter rows hidden.
                mirror.Supported = SearchFilters.All;
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

        // The filter block is presented with lazer's real settings controls (the same
        // components/theme SettingsOverlay uses), each bound — directly or through the
        // enum<->string adapters — to the SHARED engine, so changes round-trip between the
        // dropdowns here and the docked listing's chips.
        [Test]
        public void FiltersUseLazerControlsBoundToTheSharedEngine()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            // The rows are LAZER'S REAL components, populated: the Mode row carries Any + the
            // four legacy rulesets, and the Categories row omits the auth-only entries.
            AddAssert("mode row is lazer's ruleset filter with Any + 4 rulesets", () =>
                fullscreen.RulesetRow.IsLoaded
                && fullscreen.RulesetRow.ChildrenOfType<FilterTabItem<osu.Game.Rulesets.RulesetInfo>>().Count() == 5);
            AddAssert("category row omits the auth-only Favourites/Mine items", () =>
                fullscreen.CategoryRow.ChildrenOfType<FilterTabItem<SearchCategory>>().Any()
                && !fullscreen.CategoryRow.ChildrenOfType<FilterTabItem<SearchCategory>>()
                              .Any(i => i.Value == SearchCategory.Favourites || i.Value == SearchCategory.Mine));
            AddAssert("genre/language/extra rows and the sort control are loaded lazer components", () =>
                fullscreen.GenreRow.IsLoaded && fullscreen.LanguageRow.IsLoaded
                && fullscreen.ExtraRow.IsLoaded && fullscreen.SortControl.IsLoaded);
            AddAssert("two rounded sliders for the star range", () =>
                fullscreen.ChildrenOfType<RoundedSliderBar<double>>().Count() == 2);

            // Row labels must never wrap (fixed 100px label column, single 13px line).
            AddAssert("row labels stay on one line", () =>
                new Drawable[] { fullscreen.RulesetRow, fullscreen.CategoryRow, fullscreen.GenreRow, fullscreen.LanguageRow, fullscreen.ExtraRow }
                    .All(r => r.ChildrenOfType<osu.Game.Graphics.Containers.OsuTextFlowContainer>().First().DrawHeight < 20));

            // Selections round-trip through the shared engine, both directions.
            AddStep("pick osu!mania on the mode row", () => fullscreen.RulesetRow.Current.Value =
                fullscreen.RulesetRow.ChildrenOfType<FilterTabItem<osu.Game.Rulesets.RulesetInfo>>()
                          .Select(i => i.Value).Single(r => r.OnlineID == 3));
            AddUntilStep("engine mode follows the row", () => docked.Engine.Mode.Value == "m");
            AddStep("engine mode cleared elsewhere", () => docked.Engine.Mode.Value = null);
            AddUntilStep("mode row back on Any", () => fullscreen.RulesetRow.Current.Value.OnlineID < 0);

            AddStep("pick Loved on the category row", () => fullscreen.CategoryRow.Current.Value = SearchCategory.Loved);
            AddUntilStep("engine category follows", () => docked.Engine.Category.Value == "loved");
            AddStep("category changed on the engine (e.g. via the docked chips)", () => docked.Engine.Category.Value = "qualified");
            AddUntilStep("category row follows the engine", () => fullscreen.CategoryRow.Current.Value == SearchCategory.Qualified);

            AddStep("pick Anime on the genre row", () => fullscreen.GenreRow.Current.Value = SearchGenre.Anime);
            AddAssert("engine genre id follows (lazer enum values ARE osu-web ids)", () => docked.Engine.GenreId.Value == 3);

            AddStep("pick English on the language row", () => fullscreen.LanguageRow.Current.Value = SearchLanguage.English);
            AddAssert("engine language id follows", () => docked.Engine.LanguageId.Value == 2);

            AddStep("toggle Has Video on the extra row", () => fullscreen.ExtraRow.Current.Add(SearchExtra.Video));
            AddAssert("engine extra follows", () => docked.Engine.HasVideo.Value);
            AddStep("untoggle it", () => fullscreen.ExtraRow.Current.Remove(SearchExtra.Video));
            AddAssert("engine extra cleared", () => !docked.Engine.HasVideo.Value);

            AddStep("pick sort by plays", () => fullscreen.SortControl.Current.Value = SortCriteria.Plays);
            AddUntilStep("engine sort key follows", () => docked.Engine.SortKey.Value == "plays");
            AddStep("flip direction to ascending", () => fullscreen.SortControl.SortDirection.Value = osu.Game.Overlays.SortDirection.Ascending);
            AddAssert("engine direction follows", () => !docked.Engine.SortDescending.Value);

            AddStep("raise min stars through the slider's bindable", () => fullscreen.MinStarsSlider.Current.Value = 3);
            AddAssert("engine min stars follows", () => Math.Abs(docked.Engine.MinStars.Value - 3) < 0.001);
        }

        /// <summary>
        /// osu-web's General row, carrying the one item this app can back. The other four
        /// (Recommended difficulty, Include converts, Subscribed mappers, Spotlights) each need
        /// something an app-only token or this app's feature set can't provide, so they are absent
        /// rather than dead — the same rule the Categories row already follows.
        /// </summary>
        [Test]
        public void TheGeneralRowOffersFeaturedArtistsAndNothingElse()
        {
            AddStep("open", () => fullscreen.ShowSearch());
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            AddAssert("exactly one item, Featured Artists", () =>
                fullscreen.GeneralRow.ChildrenOfType<FilterTabItem<SearchGeneral>>()
                          .Select(i => i.Value).SequenceEqual(new[] { SearchGeneral.FeaturedArtists }));

            AddStep("toggle Featured Artists on", () => fullscreen.GeneralRow.Current.Add(SearchGeneral.FeaturedArtists));
            AddAssert("engine follows", () => docked.Engine.FeaturedArtists.Value);

            AddStep("cleared on the engine instead", () => docked.Engine.FeaturedArtists.Value = false);
            AddUntilStep("the row follows back", () => !fullscreen.GeneralRow.Current.Contains(SearchGeneral.FeaturedArtists));
        }

        /// <summary>
        /// Featured Artists is official-only, so its row must vanish on the mirror backend exactly
        /// as genre and language do — a control the backend will ignore is a control that lies. The
        /// VALUE survives the row going away, so a user who set it before switching backends gets
        /// it back rather than silently losing it.
        /// </summary>
        [Test]
        public void TheFeaturedArtistsRowFollowsWhatTheBackendCanExpress()
        {
            AddStep("open", () => fullscreen.ShowSearch());
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            AddStep("set it, then let the source degrade to a mirror's vocabulary", () =>
            {
                docked.Engine.FeaturedArtists.Value = true;

                // Through the mirror's own capability, so the engine recomputes its offer the way
                // it does in the app — writing AvailableFilters directly would be undone by the
                // next search, which recomputes it before building every request.
                mirror.Supported = SearchFilters.AllMirror;
                docked.Engine.ScheduleSearch();
            });

            AddUntilStep("the row is gone", () => fullscreen.GeneralRow.Alpha == 0 && !fullscreen.GeneralRow.IsPresent);
            AddUntilStep("genre and language went with it", () => fullscreen.GenreRow.Alpha == 0 && fullscreen.LanguageRow.Alpha == 0);
            AddAssert("the rows a mirror CAN serve stayed", () => fullscreen.RulesetRow.Alpha == 1 && fullscreen.StarsRow.Alpha == 1);
            AddAssert("but the value is kept, not cleared", () => docked.Engine.FeaturedArtists.Value);

            AddStep("the official backend's vocabulary is back", () =>
            {
                mirror.Supported = SearchFilters.All;
                docked.Engine.ScheduleSearch();
            });

            AddUntilStep("the row returns", () => fullscreen.GeneralRow.Alpha == 1);
            AddAssert("still set, exactly as it was left", () =>
                fullscreen.GeneralRow.Current.Contains(SearchGeneral.FeaturedArtists));
        }

        // The lazer-style hover icon rail: sliding in on card hover with a plus button (existing
        // enqueue path — listing stays open) and a browser button (opens the set's osu.ppy.sh
        // page externally, via a test seam here).
        [Test]
        public void HoverIconRailQueuesAndOpensBrowser()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            var browsedUrls = new List<string>();
            AddStep("stub the browser opener", () => fullscreen.OpenUrl = browsedUrls.Add);

            FullscreenBeatmapCard card = null!;
            AddStep("grab a card", () => card = fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Single(c => c.Set.Id == 1));

            AddAssert("icon rail hidden before hover", () => card.IconRail.Alpha == 0);

            AddStep("hover the card", () => InputManager.MoveMouseTo(card));
            AddUntilStep("icon rail slid in", () => card.IconRail.Alpha == 1 && card.IconRail.X == 0);

            AddStep("click the plus button", () => card.PlusButton.TriggerClick());
            AddUntilStep("set queued via the existing enqueue path", () => picked?.Id == 1);
            AddAssert("listing stayed open", () => fullscreen.State.Value == Visibility.Visible);

            AddStep("click the browser button", () => card.BrowseButton.TriggerClick());
            AddAssert("set page opened externally with the right URL",
                () => browsedUrls.SequenceEqual(new[] { "https://osu.ppy.sh/beatmapsets/1" }));

            AddStep("unhover", () => InputManager.MoveMouseTo(fullscreen.SearchBox));
            AddUntilStep("icon rail slid back out", () => card.IconRail.Alpha == 0);
        }

        [Test]
        public void TypingShowsThreeColumnCardsAndSyncsQueryToDockedBox()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            AddAssert("query landed on the shared engine", () => docked.Engine.Query.Value == "a");
            AddAssert("sidebar rendered the same results", () => docked.ChildrenOfType<BeatmapCard>().Count() == 3);

            // The wide test host must actually get the osu-web-style 3-column grid.
            AddAssert("three cards per row", () =>
            {
                var cards = fullscreen.ChildrenOfType<FullscreenBeatmapCard>().ToList();
                var flowWidth = fullscreen.ChildrenOfType<FillFlowContainer<FullscreenBeatmapCard>>().Single().DrawWidth;
                return cards.All(c => Math.Abs(c.Width - flowWidth / 3) < 0.5f);
            });
        }

        // The grid gets the same spinner placement as the sidebar (see
        // TestSceneBeatmapListing.LoadMoreSpinnerTakesItsOwnRowAndNeverOverlapsACard for the bug
        // this guards): a fresh fetch takes the grid away and spins in the space it left, so no
        // spinner is ever drawn over a card here either.
        [Test]
        public void FreshFetchTakesTheGridAwayRatherThanSpinningOverIt()
        {
            var gate = new TaskCompletionSource<bool>();

            AddStep("gate the mirror", () => mirror.Gate = gate);
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));

            AddUntilStep("fresh spinner spinning", () => fullscreen.FreshSpinner.State.Value == Visibility.Visible);
            AddAssert("grid taken away, nothing to spin over", () =>
                !fullscreen.ChildrenOfType<BasicScrollContainer>().First().IsPresent);
            AddAssert("no card is on screen to overlap", () => !fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Any());

            AddStep("release the gate", () => gate.SetResult(true));

            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("fresh spinner gone", () => fullscreen.FreshSpinner.State.Value == Visibility.Hidden);
            AddAssert("the append row is collapsed, so the grid ends at its last card", () =>
                Math.Abs(fullscreen.ResultsFlow.DrawHeight
                         - fullscreen.ChildrenOfType<FillFlowContainer<FullscreenBeatmapCard>>().Single().DrawHeight) < 0.5f);
        }

        [Test]
        public void HoveringTheStripExpandsOneContinuousCardWithoutReflowingTheGrid()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            var positionsBefore = new Dictionary<int, osuTK.Vector2>();
            AddStep("record every card's screen position", () => positionsBefore = fullscreen.ChildrenOfType<FullscreenBeatmapCard>()
                .ToDictionary(c => c.Set.Id, c => c.ScreenSpaceDrawQuad.TopLeft));

            FullscreenBeatmapCard card1 = null!, card2 = null!;
            AddStep("grab two cards", () =>
            {
                card1 = fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Single(c => c.Set.Id == 1);
                card2 = fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Single(c => c.Set.Id == 2);
            });

            AddAssert("nothing expanded before hover", () => fullscreen.ExpandedCard == null);

            // Lazer behaviour: hovering the card BODY shows details (stats/icon rail) but does
            // NOT expand — only the difficulty strip triggers the expansion.
            AddStep("hover the card body", () => InputManager.MoveMouseTo(card1));
            AddWaitStep("give any (wrong) expansion time to trigger", 5);
            AddAssert("no expansion from body hover", () => fullscreen.ExpandedCard == null);

            AddStep("hover the difficulty strip", () => InputManager.MoveMouseTo(card1.DifficultyStrip));
            AddUntilStep("its expansion dropdown is fully visible", () => card1.ExpansionDropdown.Alpha == 1);
            AddAssert("that card owns the expansion (accent border state)", () => fullscreen.ExpandedCard == card1 && card1.Expanded.Value);

            // The lazer fix this round pins: ONE continuous card, not two stacked boxes — the
            // dropdown lives INSIDE the card's single masked+bordered surface, which grows
            // around it (single border, no seam, no second panel).
            AddAssert("dropdown is part of the card's one bordered surface", () =>
                card1.CardSurface.ChildrenOfType<FullscreenBeatmapCard.DifficultyRow>().Any()
                && card1.CardSurface.Masking && card1.CardSurface.BorderThickness == 3);
            AddAssert("the grown surface is one continuous quad (body + dropdown share edges)", () =>
            {
                var surface = card1.CardSurface.ScreenSpaceDrawQuad.AABBFloat;
                var drop = card1.ExpansionDropdown.ScreenSpaceDrawQuad.AABBFloat;
                return drop.Left >= surface.Left - 0.5f && drop.Right <= surface.Right + 0.5f
                       && Math.Abs(drop.Bottom - surface.Bottom) < 0.5f;
            });

            AddAssert("one row per difficulty, sourced from beatmaps[]", () =>
                card1.ExpansionDropdown.ChildrenOfType<FullscreenBeatmapCard.DifficultyRow>().Count() == card1.Set.Beatmaps.Count);
            AddAssert("rows sorted ascending by stars", () =>
            {
                var stars = card1.ExpansionDropdown.ChildrenOfType<FullscreenBeatmapCard.DifficultyRow>()
                                .Select(r => r.Beatmap.DifficultyRating).ToList();
                return stars.SequenceEqual(stars.OrderBy(s => s));
            });

            // The core of the reflow bug: expansion must never move ANY card in the grid.
            AddAssert("every card's grid position is unchanged while expanded", () =>
                fullscreen.ChildrenOfType<FullscreenBeatmapCard>().All(c =>
                    osuTK.Vector2.Distance(c.ScreenSpaceDrawQuad.TopLeft, positionsBefore[c.Set.Id]) < 0.5f));

            // Exactly one expansion at a time: hovering another card's strip MOVES it.
            AddStep("hover the neighbouring card's difficulty strip", () => InputManager.MoveMouseTo(card2.DifficultyStrip));
            AddUntilStep("expansion moved to the neighbour", () => fullscreen.ExpandedCard == card2 && card2.Expanded.Value);
            AddAssert("previous card released its expanded state", () => !card1.Expanded.Value);
            AddUntilStep("exactly one card expanded", () =>
                fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count(c => c.Expanded.Value) == 1);
            AddAssert("grid still never moved", () =>
                fullscreen.ChildrenOfType<FullscreenBeatmapCard>().All(c =>
                    osuTK.Vector2.Distance(c.ScreenSpaceDrawQuad.TopLeft, positionsBefore[c.Set.Id]) < 0.5f));

            AddStep("move the mouse away", () => InputManager.MoveMouseTo(fullscreen.SearchBox));
            AddUntilStep("expansion collapsed", () => fullscreen.ExpandedCard == null);
            AddUntilStep("no card left with a visible dropdown or expanded border", () =>
                fullscreen.ChildrenOfType<FullscreenBeatmapCard>().All(c => !c.Expanded.Value && c.ExpansionDropdown.Alpha == 0));
        }

        // The scaled-down density and the filter/grid clearance: cards stay compact, and the
        // scrolling grid's masked viewport starts strictly below the filter block's last row —
        // scrolled cards can never collide with the Stars/Sort rows.
        [Test]
        public void GridStartsBelowFilterBlockAndCardsAreCompact()
        {
            AddStep("open seeded with 'a'", () => fullscreen.ShowWithInitialChar('a'));
            AddUntilStep("cards shown", () => fullscreen.ChildrenOfType<FullscreenBeatmapCard>().Count() == 3);
            AddUntilStep("entrance settled", () => fullscreen.SlidePanel.Y == 0);

            AddAssert("card height is the scaled-down target", () =>
                FullscreenBeatmapCard.HEIGHT <= 100
                && fullscreen.ChildrenOfType<FullscreenBeatmapCard>().All(c => c.DrawHeight == FullscreenBeatmapCard.HEIGHT));

            // Polled, not asserted on the very next frame: "entrance settled" above only pins the
            // slide panel's own Y, while the filter block it sits under (auto-sizing rows, the sort
            // control's own layout pass) can still be resolving the grid viewport's top for another
            // frame or two — which made these three intermittently read a mid-layout value under
            // full-suite load. A genuinely wrong layout still fails, on the poll's timeout.
            AddUntilStep("grid viewport starts a clear gap below the stars row", () =>
            {
                float scrollTop = fullscreen.ChildrenOfType<osu.Framework.Graphics.Containers.BasicScrollContainer>().First().ScreenSpaceDrawQuad.TopLeft.Y;
                float starsBottom = fullscreen.MinStarsSlider.ScreenSpaceDrawQuad.BottomLeft.Y;
                return scrollTop >= starsBottom;
            });
            AddUntilStep("grid viewport starts below the sort dropdown too", () =>
            {
                float scrollTop = fullscreen.ChildrenOfType<osu.Framework.Graphics.Containers.BasicScrollContainer>().First().ScreenSpaceDrawQuad.TopLeft.Y;
                float sortBottom = fullscreen.SortControl.ScreenSpaceDrawQuad.BottomLeft.Y;
                return scrollTop >= sortBottom;
            });
            AddUntilStep("no card is drawn above the grid viewport's top", () =>
            {
                float scrollTop = fullscreen.ChildrenOfType<osu.Framework.Graphics.Containers.BasicScrollContainer>().First().ScreenSpaceDrawQuad.TopLeft.Y;
                return fullscreen.ChildrenOfType<FullscreenBeatmapCard>().All(c => c.ScreenSpaceDrawQuad.TopLeft.Y >= scrollTop - 0.5f);
            });
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

            /// <summary>
            /// What this stub claims it can filter on. Settable so a test can stand in for a
            /// degraded mirror chain and drive the listing's row visibility through the REAL path
            /// (the engine recomputing its offer from the mirror), rather than by writing the
            /// engine's published answer directly — which the next search would overwrite.
            /// </summary>
            public SearchFilters Supported = SearchFilters.All;

            public SearchFilters SupportedFilters => Supported;

            public List<BeatmapSetInfo> Sets { get; } = new();

            /// <summary>When set, responses block on this until the test releases it — for the
            /// spinner-placement coverage, which needs a fetch held in flight.</summary>
            public TaskCompletionSource<bool>? Gate;

            public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                if (Gate != null)
                    await Gate.Task.ConfigureAwait(false);

                return new List<BeatmapSetInfo>(Sets);
            }

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
