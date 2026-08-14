#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Configuration;
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
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// osu!-web-style beatmap listing — the docked/compact presentation. All search state (debounced
/// keyword search, server-side filters, client-side genre/language filters, pagination,
/// stale-response guard) lives in the owned <see cref="BeatmapSearchEngine"/>; this class is a
/// VIEW over it: a keyword box and collapsible chip filter rows driving the engine's bindables,
/// and a scrollable grid of <see cref="BeatmapCard"/>s (two columns when wide enough, one when
/// narrow — see <see cref="two_column_threshold"/>) rebuilt off the engine's results, with
/// infinite scroll. The fullscreen search style's <see cref="FullscreenListingOverlay"/> is a
/// second view over the SAME engine (see <see cref="Engine"/>), so filters/query stay in sync
/// across presentations with no duplicated search logic.
///
/// Interaction contract (kept from the old SearchOverlay): typing anywhere opens it seeded with
/// the char (<see cref="ShowWithInitialChar"/>); Up/Down move the highlighted card; Enter fires
/// <see cref="SetPicked"/> with the selected card (falling back to the first) and closes the
/// overlay; Escape closes it. Clicking a card fires <see cref="SetPicked"/> but keeps the listing
/// open, so several sets can be queued in one browsing session. Download-disabled sets are dimmed
/// and non-clickable (<see cref="ClickableContainer.Action"/> stays null).
///
/// <para>
/// Also usable <b>docked</b> (see the constructor) — the three-column layout's permanent left
/// column embeds this same content instead of a dismissable overlay. Docked mode is shown once at
/// load and never hidden again: <see cref="ShowWithInitialChar"/> only focuses and seeds the
/// keyword box (no popping in/out), Escape blurs the keyword box instead of closing anything, and
/// confirming a selection with Enter no longer closes the (non-existent, in this mode) overlay.
/// </para>
///
/// <para>
/// Presentation density follows <see cref="JukeBoxSetting.SearchStyle"/> live:
/// <see cref="SearchStyle.Compact"/> renders dense half-height card rows
/// (<see cref="BeatmapCard.Compact"/>), smaller chips and a filters section that DEFAULTS to
/// collapsed; <see cref="SearchStyle.Fullscreen"/> keeps the roomier original presentation here
/// (the big listing overlay is opened by <see cref="Screens.MainScreen"/> on top).
/// </para>
///
/// <para>
/// The keyword box also carries an inline "#" button docked at its right edge, firing
/// <see cref="MapIdRequested"/> to open <c>MapIdOverlay</c> for queueing a set directly by
/// beatmapset ID — moved here (from a top-right corner button) so it sits with the rest of the
/// search affordances rather than floating separately.
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

    /// <summary>
    /// The search state machine this view renders. Owned here (created at construction, added as
    /// an internal child so its debounce scheduler ticks with this drawable) and shared with the
    /// fullscreen presentation — <see cref="Screens.MainScreen"/> passes it to
    /// <see cref="FullscreenListingOverlay"/> so both views stay one engine.
    /// </summary>
    public BeatmapSearchEngine Engine { get; }

    public BeatmapListingOverlay(bool docked = false)
    {
        this.docked = docked;
        Engine = new BeatmapSearchEngine();
    }

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

    private const float label_width = 92;

    public event Action<BeatmapSetInfo>? SetPicked;

    /// <summary>
    /// Fired when the inline "#" button docked at the right edge of the search box is clicked —
    /// the caller (<see cref="Screens.MainScreen"/>) opens <see cref="MapIdOverlay"/> in response.
    /// Kept as an event (rather than this overlay owning a <see cref="MapIdOverlay"/> itself) so
    /// there's still exactly one overlay instance shared across layout modes, same as before this
    /// button lived in a corner pill instead of here.
    /// </summary>
    public event Action? MapIdRequested;

    /// <summary>
    /// Fired when the keyword box gains keyboard focus — under the fullscreen search style,
    /// <see cref="Screens.MainScreen"/> responds by presenting <see cref="FullscreenListingOverlay"/>
    /// ("opening search" covers both type-anywhere and focusing the docked box).
    /// </summary>
    public event Action? SearchBoxFocused;

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    private ListingSearchBox searchBox = null!;
    private FillFlowContainer<BeatmapCard> cardsFlow = null!;
    private BasicScrollContainer scroll = null!;
    private SpriteText statusText = null!;

    private Bindable<SearchStyle> searchStyle = null!;

    /// <summary>Every filter chip in the section, kept so <see cref="applyStyle"/> can flip their
    /// density live when the style setting changes.</summary>
    private readonly List<FilterChip> allChips = new List<FilterChip>();

    private bool compactDensity;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the keyword text box, to
    /// assert its seeded text/focus state (e.g. after <see cref="ShowWithInitialChar"/>) without
    /// depending on this panel's internal layout. Exposed as the public base type — <see
    /// cref="ListingSearchBox"/> itself is an internal type.
    /// </summary>
    internal AccentTextBox SearchBox => searchBox;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to whether the
    /// "Filters" section is currently expanded.</summary>
    internal bool FiltersExpanded => filtersExpanded;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the animated
    /// Alpha of the "Filters" section's expand/collapse, to assert the animation itself actually
    /// completes rather than only the instant <see cref="FiltersExpanded"/> flag.</summary>
    internal float FiltersBodyAlpha => filtersBody.Alpha;

    private int selectedIndex = -1;

    /// <summary>Sets already rendered as of the last <see cref="rebuildCards"/> call — only cards
    /// for sets NOT in here animate in, so an already-visible card never re-flickers on an
    /// unrelated rebuild (e.g. a page append, or a client-side filter toggle).</summary>
    private readonly HashSet<BeatmapSetInfo> previouslyShownSets = new HashSet<BeatmapSetInfo>();

    /// <summary>Frames to skip auto-fetch decisions for after a card rebuild, while the flow's
    /// autosize/scroll extents still reflect the previous content (or nothing at all).</summary>
    private int settleFrames;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            Engine,
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

        searchBox.Current = Engine.Query;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        searchBox.OnCommit += (_, _) => confirmSelection();

        Engine.ResultsChanged += fresh =>
        {
            if (fresh)
                scroll.ScrollToStart();

            rebuildCards();
        };

        Engine.Status.BindValueChanged(e => statusText.Text = e.NewValue, true);

        // Presentation density (and the filters section's expand default) follows the style
        // setting live — see the class summary. Standalone construction without a config manager
        // (bare test scenes) just stays on the Compact default.
        searchStyle = config?.GetBindable<SearchStyle>(JukeBoxSetting.SearchStyle)
                      ?? new Bindable<SearchStyle>(SearchStyle.Compact);
        searchStyle.BindValueChanged(e => applyStyle(e.NewValue), true);

        // Docked instances are the three-column layout's permanent left column: shown once here
        // and never hidden again (see the class summary and the docked guards throughout).
        if (docked)
            Show();
    }

    /// <summary>
    /// Applies the style's density: compact flips the chips dense, collapses the filters section
    /// (its default state — the user can still expand it) and rebuilds the cards as dense rows;
    /// fullscreen restores the roomier original presentation.
    /// </summary>
    private void applyStyle(SearchStyle style)
    {
        compactDensity = style == SearchStyle.Compact;

        foreach (var chip in allChips)
            chip.Compact = compactDensity;

        filtersExpanded = !compactDensity;
        updateFiltersExpanded();

        // The initial application (the immediate BindValueChanged callback at LoadComplete) must
        // land instantly — a compact-style listing shouldn't open with its filter section
        // visibly fading away.
        if (!styleInitialised)
        {
            styleInitialised = true;
            filtersBody.FinishTransforms();
        }

        rebuildCards();
    }

    private bool styleInitialised;

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
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 44,
                    Children = new Drawable[]
                    {
                        searchBox = new ListingSearchBox
                        {
                            RelativeSizeAxes = Axes.Both,
                            // Leaves room for the docked map-ID button below, matching the same
                            // textbox+docked-button pattern MapIdOverlay's own idBox/addButton use.
                            Padding = new MarginPadding { Right = 48 },
                            PlaceholderText = "type in keywords…",
                            Exit = docked ? unfocusSearch : Hide,
                            Focused = () => SearchBoxFocused?.Invoke(),
                        },
                        new IconButton
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Size = new Vector2(40),
                            Icon = FontAwesome.Solid.Hashtag,
                            Action = () => MapIdRequested?.Invoke(),
                        },
                    },
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
                            chipRow("Mode", null, ListingFilterRows.SingleSelect(new (string, string?)[]
                            {
                                ("Any", null), ("osu!", "o"), ("taiko", "t"), ("catch", "c"), ("mania", "m"),
                            }, Engine.Mode)),
                            chipRow("Categories", null, ListingFilterRows.SingleSelect(new (string, string)[]
                            {
                                ("Any", "all"), ("Ranked", "ranked"), ("Qualified", "qualified"), ("Loved", "loved"),
                                ("Pending", "pending"), ("WIP", "wip"), ("Graveyard", "graveyard"),
                            }, Engine.Category)),
                            chipRow("Genre", "filters loaded results", ListingFilterRows.SingleSelect(new (string, int?)[]
                            {
                                ("Any", null), ("Video Game", 2), ("Anime", 3), ("Rock", 4), ("Pop", 5), ("Other", 6),
                                ("Novelty", 7), ("Hip Hop", 9), ("Electronic", 10), ("Metal", 11), ("Classical", 12),
                                ("Folk", 13), ("Jazz", 14),
                            }, Engine.GenreId)),
                            chipRow("Language", "filters loaded results", ListingFilterRows.SingleSelect(new (string, int?)[]
                            {
                                ("Any", null), ("English", 2), ("Japanese", 3), ("Chinese", 4), ("Korean", 6),
                                ("Instrumental", 5), ("German", 8), ("French", 7), ("Italian", 11), ("Spanish", 10),
                                ("Swedish", 9), ("Russian", 12), ("Polish", 13), ("Other", 14),
                            }, Engine.LanguageId)),
                            chipRow("Extra", null, new[]
                            {
                                ListingFilterRows.Toggle("Has Video", Engine.HasVideo),
                                ListingFilterRows.Toggle("Has Storyboard", Engine.HasStoryboard),
                            }),
                            chipRow("Sort", null, ListingFilterRows.SingleSelect(new (string, string)[]
                            {
                                ("Ranked", "ranked"), ("Plays", "plays"), ("Favourites", "favourites"),
                                ("Difficulty", "difficulty"), ("Updated", "updated"), ("Title", "title"),
                            }, Engine.SortKey).Concat(ListingFilterRows.SingleSelect(new (string, bool)[]
                            {
                                ("desc", true), ("asc", false),
                            }, Engine.SortDescending)).ToArray()),
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

    /// <summary>Builds one labelled chip row via the shared factory, remembering the chips for
    /// live density switching (<see cref="applyStyle"/>).</summary>
    private Drawable chipRow(string label, string? note, FilterChip[] chips)
    {
        allChips.AddRange(chips);
        // ReSharper disable once CoVariantArrayConversion
        return ListingFilterRows.CreateChipRow(label, note, label_width, chips);
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
                ListingFilterRows.CreateRowLabel("Stars", label_width),
                new BasicSliderBar<double>
                {
                    Size = new Vector2(130, 16),
                    Current = Engine.MinStars,
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
                    Current = Engine.MaxStars,
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

        // "any" at the rails (0 for min, 10 for max) — matching how the engine's BuildRequest
        // omits the bound.
        Engine.MinStars.BindValueChanged(e => minText.Text = e.NewValue > 0 ? $"min {e.NewValue:0.0}★" : "min any", true);
        Engine.MaxStars.BindValueChanged(e => maxText.Text = e.NewValue < 10 ? $"max {e.NewValue:0.0}★" : "max any", true);

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
        Engine.CancelPendingSearch();
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

        if (State.Value != Visibility.Visible)
            return;

        Engine.UpdatePaging(
            contentOverflows: scroll.AvailableContent > scroll.DisplayableContent + 1,
            nearEnd: scroll.IsScrolledToEnd(scroll_load_threshold));
    }

    private float lastCardWidth;

    /// <summary>A dedicated (rather than anonymous) type for the "Filters" expander header purely
    /// so tests can locate it via <c>ChildrenOfType&lt;FiltersToggleButton&gt;</c>.</summary>
    private partial class FiltersToggleButton : ClickableContainer
    {
    }

    private void rebuildCards()
    {
        cardsFlow.Clear();
        selectedIndex = -1;
        settleFrames = 2;

        var visible = Engine.VisibleSets.ToList();

        if (visible.Count == 0)
        {
            previouslyShownSets.Clear();
            return;
        }

        int newCardIndex = 0;

        foreach (var set in visible)
        {
            var card = new BeatmapCard(set, compactDensity);

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
