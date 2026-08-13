#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// osu!-web-style beatmap listing. A large keyword box drives a 300ms-debounced search against
/// <see cref="IBeatmapMirror"/>, refined by collapsible chip filter rows (mode, category, genre,
/// language, extras, sort, star range — see <see cref="createFiltersToggle"/>) and rendered as a
/// scrollable grid of <see cref="BeatmapCard"/>s (two columns when wide enough, one when narrow —
/// see <see cref="two_column_threshold"/>) with infinite scroll (the next page is requested
/// whenever the scroll nears the bottom and the last page came back full).
///
/// Mode/category/extras/sort/stars are server-side parameters — changing them restarts the search
/// at page 0. Genre and language are CLIENT-SIDE filters over the already-loaded results (the
/// legacy mirror API can't express them), so their rows are captioned accordingly and flipping
/// them never issues a request directly — but if the filtered results leave the viewport
/// underfilled (possibly empty) while more pages exist, further pages are auto-chained, bounded
/// by <see cref="max_auto_chain_pages"/> per user action, so a match on a later page still
/// surfaces without the user being stuck on an unscrollable "no results".
///
/// Interaction contract (kept from the old SearchOverlay): typing anywhere opens it seeded with
/// the char (<see cref="ShowWithInitialChar"/>); Up/Down move the highlighted card; Enter fires
/// <see cref="SetPicked"/> with the selected card (falling back to the first) and closes the
/// overlay; Escape closes it. Clicking a card fires <see cref="SetPicked"/> but keeps the listing
/// open, so several sets can be queued in one browsing session. Download-disabled sets are dimmed
/// and non-clickable (<see cref="ClickableContainer.Action"/> stays null). A slower, older search
/// response can never overwrite a newer one (<see cref="searchSequence"/> guard).
///
/// <para>
/// Also usable <b>docked</b> (see the constructor) — the three-column layout's permanent left
/// column embeds this same content instead of a dismissable overlay. Docked mode is shown once at
/// load and never hidden again: <see cref="ShowWithInitialChar"/> only focuses and seeds the
/// keyword box (no popping in/out), Escape blurs the keyword box instead of closing anything, and
/// confirming a selection with Enter no longer closes the (non-existent, in this mode) overlay.
/// </para>
/// </summary>
public partial class BeatmapListingOverlay : FocusedOverlayContainer
{
    /// <summary>Columns render single-file below this content width — the docked left column
    /// (~380px minus panel padding) never reaches the two-column threshold, matching the "grid ->
    /// 1-col in narrow width" contract; the wider standalone/full-visuals overlay still gets two.</summary>
    private const float two_column_threshold = 560;

    /// <summary>Whether this instance is permanently embedded (three-column layout's left column)
    /// rather than a dismissable floating overlay. See the class summary.</summary>
    private readonly bool docked;

    private Container filtersBody = null!;
    private bool filtersExpanded = true;
    private SpriteText filtersToggleText = null!;

    public BeatmapListingOverlay(bool docked = false)
    {
        this.docked = docked;
    }
    private const double debounce_ms = 300;
    private const int page_size = 30;

    /// <summary>Cards that weren't already on screen fade+rise in, staggered by this many ms per
    /// card — capped at <see cref="max_stagger_cards"/> so a large page doesn't feel sluggish.</summary>
    private const double card_stagger_interval = 30;

    /// <summary>Cap on how many cards' worth of stagger delay accumulates before further cards
    /// all start together (see <see cref="card_stagger_interval"/>).</summary>
    private const int max_stagger_cards = 10;

    /// <summary>Vertical offset a card rises from as it fades in.</summary>
    private const float card_rise_offset = 16;

    /// <summary>How close (px) to the bottom of the scroll the user must be before the next page
    /// is requested.</summary>
    private const float scroll_load_threshold = 200;

    /// <summary>
    /// Cap on consecutive automatically-chained page fetches (pages requested without the user
    /// scrolling — e.g. when the client-side genre/language filter reduces every loaded page to
    /// fewer cards than fill the viewport, possibly zero). Bounded per user action so an
    /// all-filtered result stream can't hammer the mirror indefinitely; the budget refreshes
    /// whenever content becomes scrollable again or the user changes the search/filters.
    /// </summary>
    private const int max_auto_chain_pages = 5;

    private const float label_width = 92;

    public event Action<BeatmapSetInfo>? SetPicked;

    [Resolved]
    private IBeatmapMirror mirror { get; set; } = null!;

    private ListingSearchBox searchBox = null!;
    private FillFlowContainer<BeatmapCard> cardsFlow = null!;
    private BasicScrollContainer scroll = null!;
    private SpriteText statusText = null!;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the keyword text box, to
    /// assert its seeded text/focus state (e.g. after <see cref="ShowWithInitialChar"/>) without
    /// depending on this panel's internal layout. Exposed as the public base type — <see
    /// cref="ListingSearchBox"/> itself is a private nested type not reachable even with
    /// InternalsVisibleTo.
    /// </summary>
    internal AccentTextBox SearchBox => searchBox;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to whether the
    /// "Filters" section is currently expanded.</summary>
    internal bool FiltersExpanded => filtersExpanded;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the animated
    /// Alpha of the "Filters" section's expand/collapse, to assert the animation itself actually
    /// completes rather than only the instant <see cref="FiltersExpanded"/> flag.</summary>
    internal float FiltersBodyAlpha => filtersBody.Alpha;

    private ScheduledDelegate? debounceDelegate;

    // Guards against a slower, older search response overwriting the results of a newer one that
    // happened to complete first. Bumped by every fetch (fresh search or next page), so a stale
    // page-append can't interleave with a newer fresh search either.
    private int searchSequence;

    private int selectedIndex = -1;

    // ---- Server-side filter state (any change restarts the search at page 0) -----------------

    private string? mode;                                  // NeriNyan `m`: "o"/"t"/"c"/"m", null = any
    private string category = "ranked";                    // NeriNyan `s`
    private bool hasVideo, hasStoryboard;                  // NeriNyan `e`
    private string sortKey = "ranked";                     // `sort` = {key}_{asc|desc}
    private bool sortDescending = true;

    private readonly BindableDouble minStars = new BindableDouble { MinValue = 0, MaxValue = 10, Precision = 0.1 };
    private readonly BindableDouble maxStars = new BindableDouble(10) { MinValue = 0, MaxValue = 10, Precision = 0.1 };

    // ---- Client-side filter state (re-filters loaded results, never issues a request) --------

    private int? genreId;
    private int? languageId;

    // ---- Pagination -----------------------------------------------------------------------------

    private readonly List<BeatmapSetInfo> loadedSets = new List<BeatmapSetInfo>();
    private int currentPage;
    private bool isLoading;
    private bool hasMore;

    /// <summary>Sets already rendered as of the last <see cref="rebuildCards"/> call — only cards
    /// for sets NOT in here animate in, so an already-visible card never re-flickers on an
    /// unrelated rebuild (e.g. a page append, or a client-side filter toggle).</summary>
    private readonly HashSet<BeatmapSetInfo> previouslyShownSets = new HashSet<BeatmapSetInfo>();

    /// <summary>Auto-chained fetches consumed since the last user action — see
    /// <see cref="max_auto_chain_pages"/>.</summary>
    private int autoChainedPages;

    /// <summary>Frames to skip auto-fetch decisions for after a card rebuild, while the flow's
    /// autosize/scroll extents still reflect the previous content (or nothing at all).</summary>
    private int settleFrames;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.Background.Opacity(0.97f),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(Theme.PanelPadding * 1.5f),
                Child = new GridContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    RowDimensions = new[]
                    {
                        new Dimension(GridSizeMode.AutoSize),
                        new Dimension(),
                    },
                    Content = new[]
                    {
                        new Drawable[] { createHeader() },
                        new Drawable[]
                        {
                            scroll = new BasicScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Padding = new MarginPadding { Top = Theme.SectionSpacing },
                                Child = cardsFlow = new FillFlowContainer<BeatmapCard>
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Full,
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        searchBox.Current.BindValueChanged(_ => scheduleSearch());
        searchBox.OnCommit += (_, _) => confirmSelection();

        minStars.BindValueChanged(_ => scheduleSearch());
        maxStars.BindValueChanged(_ => scheduleSearch());

        updateFiltersExpanded();

        // Docked instances are the three-column layout's permanent left column: shown once here
        // and never hidden again (see the class summary and the docked guards throughout).
        if (docked)
            Show();
    }

    private Drawable createHeader()
    {
        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, Theme.RowSpacing),
            // Animates the reflow when filtersBody's presence toggles (see updateFiltersExpanded)
            // so the "Filters" section reads as an expand/collapse rather than an instant jump.
            LayoutDuration = (float)Theme.DurationNormal,
            LayoutEasing = Theme.EaseEnter,
            Children = new Drawable[]
            {
                searchBox = new ListingSearchBox
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 44,
                    PlaceholderText = "type in keywords…",
                    Exit = docked ? unfocusSearch : Hide,
                },
                createFiltersToggle(),
                // Wrapping the chip rows in their own FillFlowContainer (rather than flattening
                // them into the outer one) lets collapsing reclaim their vertical space for free:
                // a FillFlowContainer skips non-IsPresent children (Alpha 0, no AlwaysPresent) when
                // flowing, so hiding this single child collapses the whole section instead of
                // leaving a blank gap the size of every individual row.
                filtersBody = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Child = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, Theme.RowSpacing),
                        Children = new[]
                        {
                            createChipRow("Mode", null, singleSelect(new (string, string?)[]
                            {
                                ("Any", null), ("osu!", "o"), ("taiko", "t"), ("catch", "c"), ("mania", "m"),
                            }, mode, v => { mode = v; scheduleSearch(); })),
                            createChipRow("Categories", null, singleSelect(new (string, string)[]
                            {
                                ("Any", "all"), ("Ranked", "ranked"), ("Qualified", "qualified"), ("Loved", "loved"),
                                ("Pending", "pending"), ("WIP", "wip"), ("Graveyard", "graveyard"),
                            }, category, v => { category = v; scheduleSearch(); })),
                            createChipRow("Genre", "filters loaded results", singleSelect(new (string, int?)[]
                            {
                                ("Any", null), ("Video Game", 2), ("Anime", 3), ("Rock", 4), ("Pop", 5), ("Other", 6),
                                ("Novelty", 7), ("Hip Hop", 9), ("Electronic", 10), ("Metal", 11), ("Classical", 12),
                                ("Folk", 13), ("Jazz", 14),
                            }, genreId, v => { genreId = v; applyClientFilterChange(); })),
                            createChipRow("Language", "filters loaded results", singleSelect(new (string, int?)[]
                            {
                                ("Any", null), ("English", 2), ("Japanese", 3), ("Chinese", 4), ("Korean", 6),
                                ("Instrumental", 5), ("German", 8), ("French", 7), ("Italian", 11), ("Spanish", 10),
                                ("Swedish", 9), ("Russian", 12), ("Polish", 13), ("Other", 14),
                            }, languageId, v => { languageId = v; applyClientFilterChange(); })),
                            createChipRow("Extra", null, new[]
                            {
                                toggleChip("Has Video", v => { hasVideo = v; scheduleSearch(); }),
                                toggleChip("Has Storyboard", v => { hasStoryboard = v; scheduleSearch(); }),
                            }),
                            createChipRow("Sort", null, singleSelect(new (string, string)[]
                            {
                                ("Ranked", "ranked"), ("Plays", "plays"), ("Favourites", "favourites"),
                                ("Difficulty", "difficulty"), ("Updated", "updated"), ("Title", "title"),
                            }, sortKey, v => { sortKey = v; scheduleSearch(); }).Concat(singleSelect(new (string, bool)[]
                            {
                                ("desc", true), ("asc", false),
                            }, sortDescending, v => { sortDescending = v; scheduleSearch(); })).ToArray()),
                            createStarsRow(),
                        },
                    },
                },
                statusText = new SpriteText
                {
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextTertiary,
                },
            },
        };
    }

    /// <summary>
    /// The "Filters" expander header — a clickable row toggling <see cref="filtersBody"/> between
    /// shown and collapsed, to save vertical room for the results list in the narrow docked column
    /// (the wide standalone overlay has room to spare, but stays collapsible there too for
    /// consistency — one behaviour, not a docked-only special case).
    /// </summary>
    private Drawable createFiltersToggle()
    {
        var toggle = new FiltersToggleButton
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Child = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(6, 0),
                Children = new Drawable[]
                {
                    filtersToggleText = new SpriteText
                    {
                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                        Colour = Theme.TextSecondary,
                    },
                },
            },
        };

        toggle.Action = () =>
        {
            filtersExpanded = !filtersExpanded;
            updateFiltersExpanded();
        };

        return toggle;
    }

    private void updateFiltersExpanded()
    {
        // filtersBody deliberately has no AlwaysPresent (see its construction comment — a
        // FillFlowContainer skips non-present children when flowing, which is what makes the
        // collapsed state reclaim its space). But that same not-present state also throttles the
        // drawable's own Update()/transform ticking, so re-expanding via a fade starting exactly
        // at Alpha 0 would never progress — nudging it to a barely-nonzero value first restores
        // presence (and ticking) immediately, with no visible difference from 0.
        if (filtersExpanded && filtersBody.Alpha <= 0)
            filtersBody.Alpha = 0.0001f;

        filtersBody.FadeTo(filtersExpanded ? 1 : 0, Theme.DurationNormal, filtersExpanded ? Theme.EaseEnter : Theme.EaseExit);
        filtersToggleText.Text = filtersExpanded ? "▾ Filters" : "▸ Filters";
    }

    private void unfocusSearch() => GetContainingFocusManager()?.ChangeFocus(null);

    /// <summary>
    /// Builds a mutually-exclusive chip group: clicking a chip activates it, deactivates its
    /// siblings and applies its value. The chip matching <paramref name="initial"/> starts active.
    /// </summary>
    private static FilterChip[] singleSelect<T>((string text, T value)[] options, T initial, Action<T> apply)
    {
        var chips = options.Select(o => new FilterChip(o.text)).ToArray();

        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            chips[i].Active.Value = EqualityComparer<T>.Default.Equals(options[i].value, initial);
            chips[i].Action = () =>
            {
                if (chips[index].Active.Value)
                    return; // already selected — don't restart the search for a no-op.

                foreach (var c in chips)
                    c.Active.Value = false;

                chips[index].Active.Value = true;
                apply(options[index].value);
            };
        }

        return chips;
    }

    /// <summary>An independently-toggleable chip (the multi-select Extra row).</summary>
    private static FilterChip toggleChip(string text, Action<bool> apply)
    {
        var chip = new FilterChip(text);
        chip.Action = () =>
        {
            chip.Active.Value = !chip.Active.Value;
            apply(chip.Active.Value);
        };
        return chip;
    }

    private static Drawable createChipRow(string label, string? note, FilterChip[] chips)
    {
        var row = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Full,
            Spacing = new Vector2(6, 6),
        };

        row.Add(createRowLabel(label));

        foreach (var chip in chips)
            row.Add(chip);

        if (note != null)
        {
            row.Add(new SpriteText
            {
                Text = $"({note})",
                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                Colour = Theme.TextTertiary,
                Margin = new MarginPadding { Left = 4, Top = 4 },
            });
        }

        return row;
    }

    private static Drawable createRowLabel(string label) => new Container
    {
        Width = label_width,
        AutoSizeAxes = Axes.Y,
        Child = new SpriteText
        {
            Text = label,
            Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
            Colour = Theme.TextSecondary,
            Margin = new MarginPadding { Top = 3 },
        },
    };

    private Drawable createStarsRow()
    {
        SpriteText minText, maxText;

        var row = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Full,
            Spacing = new Vector2(6, 6),
            Children = new[]
            {
                createRowLabel("Stars"),
                new BasicSliderBar<double>
                {
                    Size = new Vector2(130, 16),
                    Current = minStars,
                    BackgroundColour = Theme.ElevatedSurface,
                    SelectionColour = Theme.AccentDim,
                },
                minText = new SpriteText
                {
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextSecondary,
                    Margin = new MarginPadding { Top = 1 },
                },
                new BasicSliderBar<double>
                {
                    Size = new Vector2(130, 16),
                    Current = maxStars,
                    BackgroundColour = Theme.ElevatedSurface,
                    SelectionColour = Theme.AccentDim,
                },
                maxText = new SpriteText
                {
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextSecondary,
                    Margin = new MarginPadding { Top = 1 },
                },
            },
        };

        // "any" at the rails (0 for min, 10 for max) — matching how buildRequest omits the bound.
        minStars.BindValueChanged(e => minText.Text = e.NewValue > 0 ? $"min {e.NewValue:0.0}★" : "min any", true);
        maxStars.BindValueChanged(e => maxText.Text = e.NewValue < 10 ? $"max {e.NewValue:0.0}★" : "max any", true);

        return row;
    }

    /// <summary>
    /// Seeds the keyword box with <paramref name="c"/> (kicking off the first debounced search)
    /// and gives it keyboard focus — the "type anywhere to search" entry point. When docked, the
    /// column is already permanently visible, so this only focuses+seeds; when floating, it also
    /// pops the overlay in (<c>Show()</c> is a no-op if already visible either way).
    /// </summary>
    public void ShowWithInitialChar(char c)
    {
        Show();
        searchBox.Text = c.ToString();
        scheduleFocus();
    }

    // FocusedOverlayContainer.UpdateState schedules its own focus-contention pass (which
    // unconditionally clears whatever is currently focused, including a synchronous focus
    // grab made right here) whenever State flips to Visible — see UpdateState/PopIn. Scheduling
    // this call too means it runs after that pass instead of getting immediately wiped by it,
    // so the text box (not the overlay itself) ends up with focus.
    private void scheduleFocus() => Schedule(() => GetContainingFocusManager()?.ChangeFocus(searchBox));

    // Deliberately instant (no fade animation): PopIn() must leave every descendant (searchBox
    // in particular) IsPresent synchronously, since focus can only land on a present drawable.
    protected override void PopIn() => Alpha = 1;

    protected override void PopOut()
    {
        Alpha = 0;

        // Cancel any pending debounced search so it doesn't fire (and mutate results) after the
        // overlay has closed.
        debounceDelegate?.Cancel();
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.Repeat)
            return base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape:
                // Docked: never hides the column — just clears keyboard focus off the search box
                // (the "clears/unfocuses search" contract). Floating: closes as before.
                if (docked)
                    unfocusSearch();
                else
                    Hide();
                return true;

            case Key.Up:
                moveSelection(-1);
                return true;

            case Key.Down:
                moveSelection(1);
                return true;

            case Key.Enter:
            case Key.KeypadEnter:
                // Also handled via searchBox.OnCommit when the text box itself has focus; kept
                // here too as a fallback for whenever the overlay itself is focused instead.
                confirmSelection();
                return true;
        }

        return base.OnKeyDown(e);
    }

    private void moveSelection(int delta)
    {
        if (cardsFlow.Count == 0)
            return;

        int newIndex = selectedIndex < 0 ? 0 : Math.Clamp(selectedIndex + delta, 0, cardsFlow.Count - 1);
        setSelectedIndex(newIndex);
    }

    private void setSelectedIndex(int index)
    {
        selectedIndex = index;

        for (int i = 0; i < cardsFlow.Count; i++)
            cardsFlow.Children[i].Selected.Value = i == index;
    }

    /// <summary>
    /// Enter was pressed (via the text box's commit) — queues the highlighted card, falling back
    /// to the first, then closes the listing (the quick keyboard flow; mouse clicks keep it open).
    /// Docked instances have no overlay to close, so they just queue and stay put.
    /// </summary>
    private void confirmSelection()
    {
        var target = selectedIndex >= 0 && selectedIndex < cardsFlow.Count
            ? cardsFlow.Children[selectedIndex]
            : cardsFlow.Children.FirstOrDefault();

        if (target == null || target.Set.DownloadDisabled)
            return;

        SetPicked?.Invoke(target.Set);

        if (!docked)
            Hide();
    }

    private void pick(BeatmapCard card)
    {
        if (card.Set.DownloadDisabled)
            return;

        SetPicked?.Invoke(card.Set);
    }

    // ---- Search / pagination ---------------------------------------------------------------------

    private void scheduleSearch()
    {
        debounceDelegate?.Cancel();
        debounceDelegate = Scheduler.AddDelayed(() => { _ = runSearchAsync(fresh: true); }, debounce_ms);
    }

    protected override void Update()
    {
        base.Update();

        // Cards can't be relatively sized (their FillDirection.Full flow forbids relative axes in
        // the flow direction), so their per-row width is kept in sync manually here. Narrow widths
        // (the docked left column) fall back to a single column rather than squeezing two —
        // "grid -> 1-col in narrow width".
        int columns = cardsFlow.DrawWidth >= two_column_threshold ? 2 : 1;
        float cardWidth = cardsFlow.DrawWidth / columns;

        if (Math.Abs(cardWidth - lastCardWidth) > 0.5f)
        {
            lastCardWidth = cardWidth;

            foreach (var card in cardsFlow)
                card.Width = cardWidth;
        }

        // Skip auto-fetch decisions while the scroll extents still describe the previous content
        // (autosize is computed after Update) — a not-yet-laid-out content trivially reads as
        // "at end"/"underfilled" and would fire phantom page requests.
        if (settleFrames > 0)
        {
            settleFrames--;
            return;
        }

        // Driven off the RAW loaded count, not the filtered card count: the client-side
        // genre/language filter can legitimately reduce a full page to zero visible cards, and
        // later pages may still contain matches.
        if (State.Value != Visibility.Visible || isLoading || !hasMore || loadedSets.Count == 0)
            return;

        if (scroll.AvailableContent > scroll.DisplayableContent + 1)
        {
            // Content overflows the viewport: further paging is user-driven (infinite scroll), so
            // the auto-chain budget refreshes for whenever the next stall happens.
            autoChainedPages = 0;

            if (scroll.IsScrolledToEnd(scroll_load_threshold))
                _ = runSearchAsync(fresh: false);
        }
        else if (autoChainedPages < max_auto_chain_pages)
        {
            // Loaded content doesn't fill the viewport — possibly zero visible cards after the
            // client-side filter — so the user has nothing to scroll and infinite scroll could
            // never trigger. Chain the next page automatically, bounded per user action.
            autoChainedPages++;
            _ = runSearchAsync(fresh: false);
        }
        else if (cardsFlow.Count == 0)
        {
            statusText.Text = $"no matches in the first {loadedSets.Count} loaded results — refine the filters or keywords";
        }
    }

    private float lastCardWidth;

    internal SearchRequest BuildRequest(int page)
    {
        double? min = minStars.Value > 0 ? minStars.Value : null;
        double? max = maxStars.Value < 10 ? maxStars.Value : null;

        // The two sliders are independent; if they cross, treat the crossed pair as an
        // empty-but-valid band rather than sending an inverted range.
        if (min != null && max != null && min > max)
            (min, max) = (max, min);

        return new SearchRequest
        {
            Query = searchBox.Text,
            Page = page,
            PageSize = page_size,
            Status = category,
            Sort = $"{sortKey}_{(sortDescending ? "desc" : "asc")}",
            Mode = mode,
            Extra = hasVideo && hasStoryboard ? SearchExtra.VideoAndStoryboard
                : hasVideo ? SearchExtra.Video
                : hasStoryboard ? SearchExtra.Storyboard
                : SearchExtra.None,
            MinStars = min,
            MaxStars = max,
        };
    }

    private async Task runSearchAsync(bool fresh)
    {
        int mySequence = ++searchSequence;
        int page = fresh ? 0 : currentPage + 1;

        isLoading = true;
        Schedule(() => statusText.Text = fresh ? "searching…" : "loading more…");

        var request = BuildRequest(page);

        List<BeatmapSetInfo> results;

        try
        {
            results = await mirror.SearchAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Beatmap search failed");
            results = new List<BeatmapSetInfo>();
        }

        Schedule(() =>
        {
            // A newer search superseded this one while it was in flight — drop this response
            // (the superseding search resets isLoading when it completes).
            if (mySequence != searchSequence)
                return;

            isLoading = false;
            currentPage = page;
            hasMore = results.Count >= page_size;

            if (fresh)
            {
                loadedSets.Clear();
                scroll.ScrollToStart();

                // A fresh search is a user action — the auto-chain budget starts over.
                autoChainedPages = 0;
            }

            loadedSets.AddRange(results);
            rebuildCards();
        });
    }

    /// <summary>
    /// Whether <paramref name="set"/> passes the client-side genre/language filters — the legacy
    /// mirror search can't express either, so they're applied to loaded results instead. A set
    /// with no genre/language assigned (NeriNyan serves nulls) only passes the "Any" filter.
    /// </summary>
    internal static bool MatchesClientFilters(BeatmapSetInfo set, int? genreId, int? languageId)
        => (genreId == null || set.Genre?.Id == genreId)
           && (languageId == null || set.Language?.Id == languageId);

    /// <summary>
    /// The base <see cref="osu.Framework.Graphics.UserInterface.TextBox"/> consumes the first
    /// Escape itself (killing only its own focus), which would force a second press to actually
    /// close the listing. Redirecting Escape to <see cref="Exit"/> makes a single press close it,
    /// matching the "Esc closes" contract.
    /// </summary>
    private partial class ListingSearchBox : AccentTextBox
    {
        public Action? Exit;

        protected override bool OnKeyDown(KeyDownEvent e)
        {
            if (e.Key == Key.Escape)
            {
                if (!e.Repeat)
                    Exit?.Invoke();
                return true;
            }

            return base.OnKeyDown(e);
        }
    }

    /// <summary>A dedicated (rather than anonymous) type for the "Filters" expander header purely
    /// so tests can locate it via <c>ChildrenOfType&lt;FiltersToggleButton&gt;</c>.</summary>
    private partial class FiltersToggleButton : ClickableContainer
    {
    }

    /// <summary>Genre/language changed — a user action: reset the auto-chain budget and
    /// re-filter the loaded results. If the new filter leaves the viewport underfilled (or
    /// empty) with more pages available, <see cref="Update"/> auto-chains further fetches.</summary>
    private void applyClientFilterChange()
    {
        autoChainedPages = 0;
        rebuildCards();
    }

    private void rebuildCards()
    {
        cardsFlow.Clear();
        selectedIndex = -1;
        settleFrames = 2;

        var visible = loadedSets.Where(s => MatchesClientFilters(s, genreId, languageId)).ToList();

        if (visible.Count == 0)
        {
            statusText.Text = loadedSets.Count == 0 ? "no results" : "no results (client-side genre/language filter)";
            previouslyShownSets.Clear();
            return;
        }

        statusText.Text = string.Empty;

        int newCardIndex = 0;

        foreach (var set in visible)
        {
            var card = new BeatmapCard(set);

            if (lastCardWidth > 0)
                card.Width = lastCardWidth;

            // ClickableContainer.Action's setter also drives its Enabled bindable
            // (Enabled.Value = action != null) — leaving Action non-null for a disabled set would
            // dim it visually but still let it absorb clicks and show hover/press feedback.
            card.Action = set.DownloadDisabled ? null : () => pick(card);
            cardsFlow.Add(card);

            // Only cards that weren't already rendered animate in — a card that was already on
            // screen before this rebuild (e.g. every existing card during a page-append, or a
            // still-matching card after a client-side filter toggle) is left exactly as it was,
            // rather than flickering out and back in for no visual reason.
            if (!previouslyShownSets.Contains(set))
            {
                double delay = Math.Min(newCardIndex, max_stagger_cards - 1) * card_stagger_interval;
                newCardIndex++;

                // Fade to the card's own resting alpha, not a hardcoded 1 — BeatmapCard.load()
                // already dims download-disabled sets to 0.4, and this entrance animation must
                // not undo that once it finishes.
                float targetAlpha = card.Alpha;

                card.Alpha = 0;
                card.Y = card_rise_offset;
                card.Delay(delay).FadeTo(targetAlpha, Theme.DurationNormal, Theme.EaseEnter);
                card.Delay(delay).MoveToY(0, Theme.DurationNormal, Theme.EaseEnter);
            }
        }

        previouslyShownSets.Clear();
        previouslyShownSets.UnionWith(visible);
    }
}
