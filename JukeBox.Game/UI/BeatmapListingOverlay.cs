#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// The compact beatmap-results sidebar — a RESULTS-ONLY view over the shared
/// <see cref="BeatmapSearchEngine"/>. There is exactly one search model in the app: searching and
/// filtering happen in <see cref="FullscreenListingOverlay"/>, and this column shows whatever that
/// engine last produced. It therefore carries no keyword box and no filter section at all; its top
/// row is just two buttons — a wide <see cref="SearchButton"/> that asks the host to open the
/// fullscreen listing (<see cref="SearchOpenRequested"/>) and a narrow "#" button that opens the
/// manual ID/link dialog (<see cref="MapIdRequested"/>) — above a scrollable list of dense
/// <see cref="BeatmapCard"/> rows.
///
/// Because the engine is shared and outlives any one view, closing the fullscreen listing leaves
/// this column still showing that search's results; nothing is re-fetched and nothing is cleared.
///
/// Interaction contract (unchanged from when this class also hosted the search box): Up/Down move
/// the highlighted card; Enter fires <see cref="SetPicked"/> with the selected card (falling back
/// to the first); clicking a card fires <see cref="SetPicked"/> but keeps the listing open, so
/// several sets can be queued in one browsing session. Download-disabled sets are dimmed and
/// non-clickable (<see cref="ClickableContainer.Action"/> stays null). Results render in two
/// columns when the host is wide enough and single-file when narrow (see
/// <see cref="two_column_threshold"/> — the real ~380px docked column is always single-file).
///
/// <para>
/// Also usable <b>docked</b> (see the constructor) — the three-column layout's permanent left
/// column embeds this instead of a dismissable overlay. Docked mode is shown once at load and
/// never hidden again: Escape is swallowed (rather than closing anything), and confirming a
/// selection with Enter no longer closes the (non-existent, in this mode) overlay.
/// </para>
///
/// <para>
/// Fetches are shown as real lazer <see cref="LoadingSpinner"/>s, never as text, and neither
/// spinner is ever drawn over a card. A FRESH search takes the list away (its contents are about
/// to be replaced wholesale) and spins centred in the space it left; a page append keeps every
/// card on screen and grows the list by one spinner ROW at its end (see
/// <see cref="createAppendSpinnerRow"/>).
/// </para>
/// </summary>
public partial class BeatmapListingOverlay : FocusedOverlayContainer
{
    /// <summary>Columns render single-file below this content width — the docked left column
    /// (~380px minus panel padding) never reaches the two-column threshold, matching the "grid ->
    /// 1-col in narrow width" contract; a wider standalone host still gets two.</summary>
    private const float two_column_threshold = 560;

    /// <summary>Height of the search/"#" button row.</summary>
    private const float button_row_height = 44;

    /// <summary>Height the "load more" row reserves at the end of the results flow while a page
    /// is being fetched.</summary>
    private const float append_spinner_row_height = 44;

    /// <summary>Share of the button row's width taken by the "#" button — the rest goes to the
    /// search button, giving the ~80/20 split the row is designed around.</summary>
    private const float map_id_button_width_ratio = 0.2f;

    /// <summary>Whether this instance is permanently embedded (three-column layout's left column)
    /// rather than a dismissable floating overlay. See the class summary.</summary>
    private readonly bool docked;

    /// <summary>
    /// The search state machine this view renders. Self-owned by default (created at construction,
    /// added as an internal child so its debounce scheduler ticks with this drawable) — or
    /// EXTERNALLY owned when the constructor is given one: <see cref="Screens.MainScreen"/> hosts
    /// the engine itself and hands the same instance to both this view and
    /// <see cref="FullscreenListingOverlay"/>, which is what keeps the two in sync.
    /// </summary>
    public BeatmapSearchEngine Engine { get; }

    /// <summary>Whether <see cref="Engine"/> was created (and is therefore hosted) by this
    /// listing — see the property's remarks.</summary>
    private readonly bool ownsEngine;

    public BeatmapListingOverlay(bool docked = false, BeatmapSearchEngine? engine = null)
    {
        this.docked = docked;
        ownsEngine = engine == null;
        Engine = engine ?? new BeatmapSearchEngine();
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

    public event Action<BeatmapSetInfo>? SetPicked;

    /// <summary>
    /// Fired when the "#" button is clicked — the caller (<see cref="Screens.MainScreen"/>) opens
    /// <see cref="MapIdOverlay"/> in response. Kept as an event (rather than this overlay owning a
    /// <see cref="MapIdOverlay"/> itself) so there's still exactly one overlay instance shared
    /// across the app.
    /// </summary>
    public event Action? MapIdRequested;

    /// <summary>
    /// Fired by the big search button — <see cref="Screens.MainScreen"/> responds by presenting
    /// <see cref="FullscreenListingOverlay"/>, the one place search and filters live.
    /// </summary>
    public event Action? SearchOpenRequested;

    private FillFlowContainer<BeatmapCard> cardsFlow = null!;

    /// <summary>The scrolled column: the cards, then the "load more" row (see
    /// <see cref="createAppendSpinnerRow"/>).</summary>
    private FillFlowContainer resultsFlow = null!;

    private BasicScrollContainer scroll = null!;
    private SpriteText statusText = null!;
    private Container appendSpinnerRow = null!;
    private LoadingSpinner appendSpinner = null!;
    private LoadingSpinner freshSpinner = null!;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the scrolled
    /// column, so a test can assert the "load more" row is really taking space in it rather than
    /// floating over the cards.</summary>
    internal FillFlowContainer ResultsFlow => resultsFlow;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the two spinners,
    /// which are otherwise indistinguishable by type.</summary>
    internal LoadingSpinner AppendSpinner => appendSpinner;

    internal LoadingSpinner FreshSpinner => freshSpinner;

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
                            new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                Padding = new MarginPadding { Top = Theme.SectionSpacing },
                                Children = new Drawable[]
                                {
                                    scroll = new BasicScrollContainer
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Child = resultsFlow = new FillFlowContainer
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Direction = FillDirection.Vertical,
                                            Children = new Drawable[]
                                            {
                                                cardsFlow = new FillFlowContainer<BeatmapCard>
                                                {
                                                    RelativeSizeAxes = Axes.X,
                                                    AutoSizeAxes = Axes.Y,
                                                    Direction = FillDirection.Full,
                                                },
                                                appendSpinnerRow = createAppendSpinnerRow(out appendSpinner),
                                            },
                                        },
                                    },
                                    // A fresh search discards everything below it, so the list is
                                    // taken away and this sits centred in the space it left —
                                    // there is nothing for it to cover.
                                    freshSpinner = new LoadingSpinner
                                    {
                                        Anchor = Anchor.Centre,
                                        Origin = Anchor.Centre,
                                        Size = new Vector2(32),
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        // A self-owned engine ticks with this drawable; an externally-owned one is hosted by its
        // owner instead — see the Engine property.
        if (ownsEngine)
            AddInternal(Engine);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        Engine.ResultsChanged += fresh =>
        {
            if (fresh)
                scroll.ScrollToStart();

            rebuildCards();
        };

        Engine.Status.BindValueChanged(e => statusText.Text = e.NewValue, true);

        Engine.IsLoading.BindValueChanged(_ => updateLoadingSpinners(), true);
        Engine.LoadingFresh.BindValueChanged(_ => updateLoadingSpinners(), true);

        // Docked instances are the three-column layout's permanent left column: shown once here
        // and never hidden again (see the class summary and the docked guards throughout).
        if (docked)
            Show();
    }

    /// <summary>
    /// The "load more" footer: a real row at the END of the results flow rather than a spinner
    /// floating over the viewport. A floating one sat wherever the viewport's bottom edge happened
    /// to be, which — once the user had scrolled — was on top of a card. As a flow child it takes
    /// its own height instead, so the list simply grows by a row while a page is on its way.
    /// </summary>
    private static Container createAppendSpinnerRow(out LoadingSpinner spinner) => new Container
    {
        RelativeSizeAxes = Axes.X,
        Height = append_spinner_row_height,
        // Not present until it's needed: a FillFlowContainer skips non-present children when
        // flowing, so this costs no vertical space at all while nothing is loading.
        Alpha = 0,
        Child = spinner = new LoadingSpinner
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(24),
        },
    };

    private void updateLoadingSpinners()
    {
        bool loading = Engine.IsLoading.Value;
        bool fresh = loading && Engine.LoadingFresh.Value;
        bool appending = loading && !Engine.LoadingFresh.Value;

        // The list itself goes away for a fresh search — its contents are about to be replaced
        // wholesale, and an empty area is what lets the spinner be centred in it rather than
        // drawn over stale cards.
        scroll.Alpha = fresh ? 0 : 1;

        if (fresh)
            freshSpinner.Show();
        else
            freshSpinner.Hide();

        // Presence first, then the spinner's own entrance: a drawable inside a non-present parent
        // doesn't tick, so a fade started before the row is present would never progress.
        appendSpinnerRow.Alpha = appending ? 1 : 0;

        if (appending)
            appendSpinner.Show();
        else
            appendSpinner.Hide();
    }

    private Drawable createHeader()
    {
        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, Theme.RowSpacing),
            Children = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = button_row_height,
                    // The two buttons split the row's full width (~80/20). Each sits inside its
                    // own padded cell rather than carrying a Margin: a Margin on a
                    // relatively-sized child offsets it without shrinking it, which would leave
                    // the pair overlapping by the gutter width.
                    Child = new GridContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        ColumnDimensions = new[]
                        {
                            new Dimension(),
                            new Dimension(GridSizeMode.Relative, size: map_id_button_width_ratio),
                        },
                        Content = new[]
                        {
                            new Drawable[]
                            {
                                new Container
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Padding = new MarginPadding { Right = Theme.RowSpacing / 2 },
                                    Child = new SearchButton
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Action = () => SearchOpenRequested?.Invoke(),
                                    },
                                },
                                new Container
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Padding = new MarginPadding { Left = Theme.RowSpacing / 2 },
                                    Child = new IconButton
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Icon = FontAwesome.Solid.Hashtag,
                                        Action = () => MapIdRequested?.Invoke(),
                                    },
                                },
                            },
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

    // Deliberately instant (no fade animation): PopIn() must leave every descendant IsPresent
    // synchronously, since focus can only land on a present drawable.
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
                // Docked: swallowed on purpose. This is a permanent column with nothing to close,
                // and letting Escape go unhandled would reach the input manager's own
                // "clear focus" fallback — which, on a FocusedOverlayContainer, hides the overlay.
                if (!docked)
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
    /// Enter — queues the highlighted card, falling back to the first, then closes the listing
    /// (the quick keyboard flow; mouse clicks keep it open). Docked instances have no overlay to
    /// close, so they just queue and stay put.
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

    /// <summary>The sidebar's primary affordance: opens the fullscreen listing (see
    /// <see cref="SearchOpenRequested"/>). A dedicated type purely so tests can locate it without
    /// depending on this panel's internal layout.</summary>
    internal partial class SearchButton : TextButton
    {
        public SearchButton()
            : base("Search…", FontAwesome.Solid.Search)
        {
        }
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
            // Always the dense variant: this column is the compact presentation, full stop — the
            // roomy cards live in the fullscreen listing.
            var card = new BeatmapCard(set, compact: true);

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
