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
/// osu!-web-style beatmap listing, replacing the old dropdown search in both layouts. Covers the
/// visuals area (its parent decides the exact region — see MainScreen, which hosts it inside
/// <c>visualsHost</c> so Split's left panel stays uncovered). A large keyword box drives a
/// 300ms-debounced search against <see cref="IBeatmapMirror"/>, refined by chip filter rows (mode,
/// category, genre, language, extras, sort, star range) and rendered as a scrollable two-column
/// grid of <see cref="BeatmapCard"/>s with infinite scroll (the next page is requested whenever
/// the scroll nears the bottom and the last page came back full).
///
/// Mode/category/extras/sort/stars are server-side parameters — changing them restarts the search
/// at page 0. Genre and language are CLIENT-SIDE filters over the already-loaded results (the
/// legacy mirror API can't express them), so their rows are captioned accordingly and flipping
/// them never issues a request.
///
/// Interaction contract (kept from the old SearchOverlay): typing anywhere opens it seeded with
/// the char (<see cref="ShowWithInitialChar"/>); Up/Down move the highlighted card; Enter fires
/// <see cref="SetPicked"/> with the selected card (falling back to the first) and closes the
/// overlay; Escape closes it. Clicking a card fires <see cref="SetPicked"/> but keeps the listing
/// open, so several sets can be queued in one browsing session. Download-disabled sets are dimmed
/// and non-clickable (<see cref="ClickableContainer.Action"/> stays null). A slower, older search
/// response can never overwrite a newer one (<see cref="searchSequence"/> guard).
/// </summary>
public partial class BeatmapListingOverlay : FocusedOverlayContainer
{
    private const double debounce_ms = 300;
    private const int page_size = 30;

    /// <summary>How close (px) to the bottom of the scroll the user must be before the next page
    /// is requested.</summary>
    private const float scroll_load_threshold = 200;

    private const float label_width = 92;

    public event Action<BeatmapSetInfo>? SetPicked;

    [Resolved]
    private IBeatmapMirror mirror { get; set; } = null!;

    private ListingSearchBox searchBox = null!;
    private FillFlowContainer<BeatmapCard> cardsFlow = null!;
    private BasicScrollContainer scroll = null!;
    private SpriteText statusText = null!;

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
    }

    private Drawable createHeader()
    {
        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, Theme.RowSpacing),
            Children = new[]
            {
                searchBox = new ListingSearchBox
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 44,
                    PlaceholderText = "type in keywords…",
                    Exit = Hide,
                },
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
                }, genreId, v => { genreId = v; rebuildCards(); })),
                createChipRow("Language", "filters loaded results", singleSelect(new (string, int?)[]
                {
                    ("Any", null), ("English", 2), ("Japanese", 3), ("Chinese", 4), ("Korean", 6),
                    ("Instrumental", 5), ("German", 8), ("French", 7), ("Italian", 11), ("Spanish", 10),
                    ("Swedish", 9), ("Russian", 12), ("Polish", 13), ("Other", 14),
                }, languageId, v => { languageId = v; rebuildCards(); })),
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
                statusText = new SpriteText
                {
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextTertiary,
                },
            },
        };
    }

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
    /// Opens the listing, seeds the keyword box with <paramref name="c"/> (kicking off the first
    /// debounced search) and gives it keyboard focus — the "type anywhere to search" entry point.
    /// </summary>
    public void ShowWithInitialChar(char c)
    {
        Show();
        searchBox.Text = c.ToString();
        scheduleFocus();
    }

    /// <summary>
    /// Opens the listing without seeding the keyword box (the Split layout's compact search
    /// button). Kicks off an initial search if nothing has been loaded yet, so the grid isn't
    /// empty while the keyword box still is.
    /// </summary>
    public void ShowAndFocus()
    {
        Show();
        scheduleFocus();

        if (loadedSets.Count == 0 && !isLoading)
            scheduleSearch();
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
    /// </summary>
    private void confirmSelection()
    {
        var target = selectedIndex >= 0 && selectedIndex < cardsFlow.Count
            ? cardsFlow.Children[selectedIndex]
            : cardsFlow.Children.FirstOrDefault();

        if (target == null || target.Set.DownloadDisabled)
            return;

        SetPicked?.Invoke(target.Set);
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
        // the flow direction), so their two-per-row width is kept in sync manually here.
        float cardWidth = cardsFlow.DrawWidth / 2;

        if (Math.Abs(cardWidth - lastCardWidth) > 0.5f)
        {
            lastCardWidth = cardWidth;

            foreach (var card in cardsFlow)
                card.Width = cardWidth;
        }

        // Infinite scroll: request the next page once the user nears the bottom (also naturally
        // fills a viewport taller than one page, since an unfilled scroll counts as "at end").
        // The AvailableContent guard skips the first frame after a rebuild, where the flow's
        // autosize hasn't been computed yet — a zero-height content trivially reads as "at end"
        // and would fire a phantom next-page request before anything is even laid out.
        if (State.Value == Visibility.Visible && hasMore && !isLoading && cardsFlow.Count > 0
            && scroll.AvailableContent > 0 && scroll.IsScrolledToEnd(scroll_load_threshold))
        {
            _ = runSearchAsync(fresh: false);
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

    private void rebuildCards()
    {
        cardsFlow.Clear();
        selectedIndex = -1;

        var visible = loadedSets.Where(s => MatchesClientFilters(s, genreId, languageId)).ToList();

        if (visible.Count == 0)
        {
            statusText.Text = loadedSets.Count == 0 ? "no results" : "no results (client-side genre/language filter)";
            return;
        }

        statusText.Text = string.Empty;

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
        }
    }
}
