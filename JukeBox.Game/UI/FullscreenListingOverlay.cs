#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays;
using osu.Game.Overlays.BeatmapListing;
using osu.Game.Resources.Localisation.Web;
using osu.Game.Rulesets;
using osuTK;
using osuTK.Input;
using SearchExtra = osu.Game.Overlays.BeatmapListing.SearchExtra;

namespace JukeBox.Game.UI;

/// <summary>
/// The app's ONE search surface: an osu-web-style "beatmap listing" page presented as a TRUE
/// fullscreen modal — hosted at <see cref="Screens.MainScreen"/>'s top level so it covers the
/// ENTIRE window (both side columns included), a centred listing panel above a dim
/// <see cref="Theme.ModalScrim"/> with nothing else interactive while open. Opening (type-anywhere
/// via <see cref="ShowWithInitialChar"/>, or the sidebar's search button via
/// <see cref="ShowSearch"/>) slides the panel UP from past the window's bottom edge while the
/// scrim fades in (see <see cref="PopIn"/>); Escape and the Enter-queue flow reverse it (slide
/// down + fade) back to the normal layout.
///
/// A pure VIEW over the shared <see cref="BeatmapSearchEngine"/> (the same instance the compact
/// <see cref="BeatmapListingOverlay"/> sidebar renders results from, which is why those results
/// are still there after this closes): the big keyword box binds the same
/// <see cref="BeatmapSearchEngine.Query"/>, the labelled filter block binds the same filter
/// bindables (rows the engine can't back with real data — rank achieved, played state, etc. — are
/// omitted, not faked), and the three-column grid of <see cref="FullscreenBeatmapCard"/>s rebuilds
/// off the same results with the same infinite-scroll/auto-chain behaviour.
///
/// Hovering a card expands it osu-web style: the card's grid footprint NEVER changes — a single
/// expansion happens INSIDE the card's own single growing surface (lazer's BeatmapCardContent
/// shape — see <see cref="FullscreenBeatmapCard"/>): this overlay only enforces that exactly ONE
/// card is expanded at a time (<see cref="onCardExpansionHoverChanged"/>, 100ms debounce both
/// ways) and raises the expanded card's depth so its grown surface draws over the neighbouring
/// rows — the grid's layout order is pinned by <see cref="FullscreenBeatmapCard.FlowIndex"/>
/// (see <see cref="CardFlow"/>), so depth changes can never reflow it. The ▶ preview button
/// routes to the owned <see cref="PreviewPlayer"/>, which pauses the main jukebox track for the
/// preview's duration and is always stopped when this overlay closes.
/// </summary>
public partial class FullscreenListingOverlay : FocusedOverlayContainer
{
    private const float scroll_load_threshold = 200;
    private const float min_column_width = 260;
    private const int max_columns = 3;

    /// <summary>Width of the star-range sliders (the one row lazer's listing doesn't have).</summary>
    private const float slider_width = 150;

    /// <summary>The label column width lazer's own <see cref="BeatmapSearchFilterRow{T}"/> uses —
    /// our custom Stars row matches it so every row aligns.</summary>
    private const float filter_label_width = 100;

    // The exact DI lazer's own beatmap listing provides its filter rows (FilterTabItem and the
    // sort control resolve this for the accent text colours; SettingsOverlay caches the same
    // scheme for its panel).
    [Cached]
    private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

    // Lazer's BeatmapSearchRulesetFilterRow resolves a RulesetStore for its Any/osu!/taiko/catch/
    // mania items. The realm-backed store doesn't exist in this app; AssemblyRulesetStore is the
    // DI-free equivalent, populating from the four ruleset assemblies (force-loaded in the
    // constructor — the store scans the AppDomain, and lazily-loaded references wouldn't be there
    // yet on a cold path).
    [Cached(typeof(RulesetStore))]
    private readonly RulesetStore rulesetStore;

    private readonly BeatmapSearchEngine engine;

    [Resolved]
    private osu.Framework.Platform.GameHost host { get; set; } = null!;

    public event Action<BeatmapSetInfo>? SetPicked;

    /// <summary>Debounce for the difficulty-strip expansion, both directions — lazer's
    /// <c>BeatmapCardContent</c> queues its expanded-state changes with this same 100ms.</summary>
    private const double expand_delay_ms = 100;

    private ListingSearchBox searchBox = null!;
    private CardFlow cardsFlow = null!;
    private BasicScrollContainer scroll = null!;
    private SpriteText statusText = null!;
    private PreviewPlayer previewPlayer = null!;
    private Box scrim = null!;
    private Container panel = null!;

    // Fetches read as real lazer spinners, never as text, and neither is ever drawn over a card
    // (see BeatmapListingOverlay for the same split): a FRESH search takes the grid away and spins
    // centred in the space it left, a page append grows the grid by one spinner ROW at its end.
    private FillFlowContainer resultsFlow = null!;
    private Container appendSpinnerRow = null!;
    private LoadingSpinner appendSpinner = null!;
    private LoadingSpinner freshSpinner = null!;

    /// <summary>Height the "load more" row reserves at the end of the grid while a page is being
    /// fetched.</summary>
    private const float append_spinner_row_height = 52;

    // Lazer's REAL beatmap-listing filter rows (label-left + horizontal text tab items) — wired
    // both ways to the shared engine's raw bindables in LoadComplete, so selections here and the
    // docked listing's chips round-trip through the one engine.
    private BeatmapSearchRulesetFilterRow rulesetRow = null!;
    private BeatmapSearchFilterRow<SearchCategory> categoryRow = null!;
    private BeatmapSearchFilterRow<SearchGenre> genreRow = null!;
    private BeatmapSearchFilterRow<SearchLanguage> languageRow = null!;
    private BeatmapSearchMultipleSelectionFilterRow<SearchExtra> extraRow = null!;
    private BeatmapListingSortTabControl sortControl = null!;
    private RoundedSliderBar<double> minStarsSlider = null!;
    private RoundedSliderBar<double> maxStarsSlider = null!;

    /// <summary>The caption beside the sort strip naming the active search backend and what it
    /// costs — see <see cref="updateFilterAvailability"/>.</summary>
    private SpriteText backendHint = null!;

    // The two rows that aren't lazer BeatmapSearchFilterRows, kept so they can be shown and hidden
    // on exactly the same terms as the ones that are.
    private Drawable starsRow = null!;
    private Container sortStrip = null!;

    /// <summary>Shown in place of the whole filter block when the reachable source can do nothing
    /// but keyword search, so the empty block reads as deliberate rather than broken.</summary>
    private SpriteText keywordOnlyHint = null!;

    private FillFlowContainer filterBlock = null!;

    /// <summary>The (category, hasQuery) pair the sort strip's item list was last built for — see
    /// <see cref="updateSortAvailability"/>.</summary>
    private (SearchCategory category, bool hasQuery)? appliedSortParameters;

    private int selectedIndex = -1;
    private int settleFrames;
    private float lastCardWidth;

    // ---- Hover expansion state (see onCardExpansionHoverChanged) -----------------------------

    private FullscreenBeatmapCard? expandedCard;
    private osu.Framework.Threading.ScheduledDelegate? pendingExpand;
    private osu.Framework.Threading.ScheduledDelegate? pendingCollapse;

    /// <summary>Sets already rendered as of the last rebuild — cards for these don't re-animate
    /// (same contract as the docked listing's rebuildCards).</summary>
    private readonly HashSet<BeatmapSetInfo> previouslyShownSets = new HashSet<BeatmapSetInfo>();

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the keyword box.</summary>
    internal AccentTextBox SearchBox => searchBox;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the preview
    /// player, so tests can stub its track loading and assert its state.</summary>
    internal PreviewPlayer Preview => previewPlayer;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the sliding
    /// listing panel, to assert the slide-up entrance / slide-down exit (its Y is offscreen-bottom
    /// while closed and 0 once the entrance settles).</summary>
    internal Container SlidePanel => panel;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the scrolled
    /// column and the two spinners, so a test can assert the "load more" row really takes space
    /// in the grid rather than floating over the cards.</summary>
    internal FillFlowContainer ResultsFlow => resultsFlow;

    internal LoadingSpinner AppendSpinner => appendSpinner;

    internal LoadingSpinner FreshSpinner => freshSpinner;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the currently
    /// expanded card (null when nothing is expanded) — there can only ever be one.</summary>
    internal FullscreenBeatmapCard? ExpandedCard => expandedCard;

    // Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the lazer-styled filter
    // controls, to drive/assert them without depending on internal layout.
    internal BeatmapSearchRulesetFilterRow RulesetRow => rulesetRow;
    internal BeatmapSearchFilterRow<SearchCategory> CategoryRow => categoryRow;
    internal BeatmapSearchFilterRow<SearchGenre> GenreRow => genreRow;
    internal BeatmapSearchFilterRow<SearchLanguage> LanguageRow => languageRow;
    internal BeatmapSearchMultipleSelectionFilterRow<SearchExtra> ExtraRow => extraRow;
    internal BeatmapListingSortTabControl SortControl => sortControl;
    internal RoundedSliderBar<double> MinStarsSlider => minStarsSlider;

    // The rows that aren't lazer filter rows, plus the keyword-only line, exposed so the
    // per-backend visibility rules can be asserted without depending on internal layout.
    internal Drawable StarsRow => starsRow;
    internal Container SortStrip => sortStrip;
    internal SpriteText KeywordOnlyHint => keywordOnlyHint;

    /// <summary>Test-only: the vertical flow holding the filter rows, so a test can assert the
    /// block really closes over a hidden row rather than leaving a hole.</summary>
    internal FillFlowContainer FilterBlock => filterBlock;
    internal RoundedSliderBar<double> MaxStarsSlider => maxStarsSlider;

    public FullscreenListingOverlay(BeatmapSearchEngine engine)
    {
        this.engine = engine;

        // AssemblyRulesetStore scans already-loaded assemblies — touch each ruleset first so
        // lazy assembly loading can't leave the Mode row empty (see the rulesetStore field).
        foreach (string mode in new[] { "osu", "taiko", "fruits", "mania" })
            _ = RulesetIcons.For(mode);

        rulesetStore = new AssemblyRulesetStore();
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChildren = new Drawable[]
        {
            previewPlayer = new PreviewPlayer(),
            // Dims the whole window (both columns included — this overlay sits at
            // MainScreen's top level) so only the listing panel reads as interactive while open.
            scrim = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ModalScrim,
                Alpha = 0,
            },
            // The sliding host: full-window with an even inset, so the card inside reads as one
            // large centred panel. The slide-up entrance / slide-down exit animate this host's Y
            // (see PopIn/PopOut) rather than the overlay root, whose Alpha must flip instantly for
            // focus to land (see PopIn).
            panel = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(Theme.PanelPadding),
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = Theme.CornerRadius,
                    EdgeEffect = Theme.PanelShadow,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Theme.Background.Opacity(0.98f),
                        },
                        // Tooltip host for the panel: lazer's RoundedSliderBar surfaces its value
                        // through a tooltip, which only renders inside a TooltipContainer ancestor
                        // (same wrapper SettingsOverlay uses).
                        new OsuTooltipContainer(null!)
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding(Theme.PanelPadding),
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
                                            // A full section of clearance between the (fixed)
                                            // filter block above and the scrolling grid — the
                                            // grid's masked viewport starts below this padding,
                                            // so cards can never collide with the filter rows.
                                            Padding = new MarginPadding { Top = Theme.SectionSpacing + 4 },
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
                                                            cardsFlow = new CardFlow
                                                            {
                                                                RelativeSizeAxes = Axes.X,
                                                                AutoSizeAxes = Axes.Y,
                                                                Direction = FillDirection.Full,
                                                            },
                                                            appendSpinnerRow = new Container
                                                            {
                                                                RelativeSizeAxes = Axes.X,
                                                                Height = append_spinner_row_height,
                                                                Alpha = 0,
                                                                Child = appendSpinner = new LoadingSpinner
                                                                {
                                                                    Anchor = Anchor.Centre,
                                                                    Origin = Anchor.Centre,
                                                                    Size = new Vector2(28),
                                                                },
                                                            },
                                                        },
                                                    },
                                                },
                                                freshSpinner = new LoadingSpinner
                                                {
                                                    Anchor = Anchor.Centre,
                                                    Origin = Anchor.Centre,
                                                    Size = new Vector2(40),
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        searchBox.Current = engine.Query;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        searchBox.OnCommit += (_, _) => confirmSelection();

        engine.ResultsChanged += fresh =>
        {
            if (fresh)
                scroll.ScrollToStart();

            rebuildCards();
        };

        engine.Status.BindValueChanged(e => statusText.Text = e.NewValue, true);

        engine.IsLoading.BindValueChanged(_ => updateLoadingSpinners(), true);
        engine.LoadingFresh.BindValueChanged(_ => updateLoadingSpinners(), true);

        // Dropdown-enum <-> engine-string two-way sync (see the adapter fields): seed from the
        // engine's current values, then mirror changes in both directions. The value writes are
        // idempotent, so the echo of each direction is a no-op rather than a loop.

        // Mode row (lazer speaks RulesetInfo; the engine speaks NeriNyan's single letters).
        rulesetRow.Current.Value = rulesetInfoForMode(engine.Mode.Value);
        rulesetRow.Current.BindValueChanged(e => engine.Mode.Value = ModeForRuleset(e.NewValue));
        engine.Mode.BindValueChanged(e => rulesetRow.Current.Value = rulesetInfoForMode(e.NewValue));

        // Categories row.
        categoryRow.Current.Value = CategoryFromEngine(engine.Category.Value);
        categoryRow.Current.BindValueChanged(e => engine.Category.Value = CategoryToEngine(e.NewValue));
        engine.Category.BindValueChanged(e => categoryRow.Current.Value = CategoryFromEngine(e.NewValue));

        // Genre/Language rows: lazer's enum values ARE osu-web's ids, which is exactly what the
        // engine's client-side filters hold.
        genreRow.Current.Value = GenreFromEngine(engine.GenreId.Value);
        genreRow.Current.BindValueChanged(e => engine.GenreId.Value = e.NewValue == SearchGenre.Any ? null : (int)e.NewValue);
        engine.GenreId.BindValueChanged(e => genreRow.Current.Value = GenreFromEngine(e.NewValue));

        languageRow.Current.Value = LanguageFromEngine(engine.LanguageId.Value);
        languageRow.Current.BindValueChanged(e => engine.LanguageId.Value = e.NewValue == SearchLanguage.Any ? null : (int)e.NewValue);
        engine.LanguageId.BindValueChanged(e => languageRow.Current.Value = LanguageFromEngine(e.NewValue));

        // Extra row (multi-select list <-> the engine's two bools). The set-membership checks
        // make both directions idempotent.
        extraRow.Current.BindCollectionChanged((_, _) =>
        {
            engine.HasVideo.Value = extraRow.Current.Contains(SearchExtra.Video);
            engine.HasStoryboard.Value = extraRow.Current.Contains(SearchExtra.Storyboard);
        });
        engine.HasVideo.BindValueChanged(e => setExtra(SearchExtra.Video, e.NewValue), true);
        engine.HasStoryboard.BindValueChanged(e => setExtra(SearchExtra.Storyboard, e.NewValue), true);

        // Sort control: the available criteria depend on the backend (see updateSortAvailability).
        // Seeded here with the mirror's fixed set so the engine's persisted key survives load, then
        // maintained by updateSortAvailability from the bindings below.
        sortControl.Reset(SearchCategory.Any, false);
        appliedSortParameters = (SearchCategory.Any, false);
        sortControl.Current.Value = SortFromEngine(engine.SortKey.Value);
        sortControl.Current.BindValueChanged(e => engine.SortKey.Value = e.NewValue.ToString().ToLowerInvariant());
        engine.SortKey.BindValueChanged(e => sortControl.Current.Value = SortFromEngine(e.NewValue));

        sortControl.SortDirection.Value = engine.SortDescending.Value ? SortDirection.Descending : SortDirection.Ascending;
        sortControl.SortDirection.BindValueChanged(e => engine.SortDescending.Value = e.NewValue == SortDirection.Descending);
        engine.SortDescending.BindValueChanged(e => sortControl.SortDirection.Value = e.NewValue ? SortDirection.Descending : SortDirection.Ascending);

        // Which filter rows exist at all is a property of the ACTIVE BACKEND, not of this view —
        // switching the setting re-renders the block live (and the engine re-runs the search).
        // Row visibility follows what the backend about to serve the next search can actually
        // apply — which moves with mirror health, not just with the setting, so this binds the
        // capability set rather than the backend enum.
        engine.AvailableFilters.BindValueChanged(_ => updateFilterAvailability(), true);

        engine.Api.BindValueChanged(_ => updateSortAvailability(), true);

        // Relevance only exists with a query, Nominations only under Pending — both are properties
        // of the current search, so they have to be re-evaluated as it changes.
        engine.Query.BindValueChanged(_ => updateSortAvailability());
        engine.Category.BindValueChanged(_ => updateSortAvailability());

        // Pushed (rather than each card binding the player's bindable) because cards are
        // rebuilt wholesale on every result change — pushing the current value at rebuild plus
        // this one subscription keeps every generation of cards in sync with no per-card unbinds.
        previewPlayer.PlayingSetId.BindValueChanged(e =>
        {
            foreach (var card in cardsFlow)
                card.PreviewingSetId.Value = e.NewValue;
        });
    }

    private void updateLoadingSpinners()
    {
        bool loading = engine.IsLoading.Value;
        bool fresh = loading && engine.LoadingFresh.Value;
        bool appending = loading && !engine.LoadingFresh.Value;

        scroll.Alpha = fresh ? 0 : 1;

        if (fresh)
            freshSpinner.Show();
        else
            freshSpinner.Hide();

        // Presence first, then the spinner's own entrance — see the sidebar's copy of this for
        // why a fade started inside a non-present parent never progresses.
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
            Children = new[]
            {
                new FillFlowContainer // "beatmap listing" page header
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(10, 0),
                    Children = new Drawable[]
                    {
                        new Container
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Size = new Vector2(26),
                            Masking = true,
                            CornerRadius = 6,
                            BorderThickness = 2,
                            BorderColour = Theme.TextPrimary,
                            Children = new Drawable[]
                            {
                                new Box { RelativeSizeAxes = Axes.Both, Colour = Theme.Background.Opacity(0.01f) },
                                new SpriteIcon
                                {
                                    Anchor = Anchor.Centre,
                                    Origin = Anchor.Centre,
                                    Icon = FontAwesome.Solid.Music,
                                    Size = new Vector2(12),
                                    Colour = Theme.TextPrimary,
                                },
                            },
                        },
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = "beatmap listing",
                            Font = FontUsage.Default.With(size: 20),
                            Colour = Theme.TextPrimary,
                        },
                    },
                },
                new Container // big keyword box (text scales with the box height)
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 44,
                    Children = new Drawable[]
                    {
                        searchBox = new ListingSearchBox
                        {
                            RelativeSizeAxes = Axes.Both,
                            PlaceholderText = "type in keywords…",
                            Exit = Hide,
                        },
                        new SpriteIcon
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Margin = new MarginPadding { Right = 16 },
                            Icon = FontAwesome.Solid.Search,
                            Size = new Vector2(16),
                            Colour = Theme.TextTertiary,
                        },
                    },
                },
                createFilterBlock(),
                statusText = new SpriteText
                {
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextTertiary,
                },
            },
        };
    }

    /// <summary>
    /// LAZER'S REAL beatmap-listing filter block (osu.Game.Overlays.BeatmapListing): the
    /// label-left rows with horizontal text tab items — Mode via
    /// <see cref="BeatmapSearchRulesetFilterRow"/> (backed by the cached DI-free
    /// <see cref="AssemblyRulesetStore"/>), Categories (auth-only Favourites/Mine omitted — no
    /// signed-in account to back them), Genre and Language (official backend only — see
    /// <see cref="updateFilterAvailability"/>), the multi-select Extra row, and lazer's
    /// <see cref="BeatmapListingSortTabControl"/> (criteria + direction caret). The one row lazer's
    /// listing doesn't have — the star range — keeps our slider pair, restyled to the same
    /// label-column geometry so every row aligns. Rows osu-web shows but this app can't back with
    /// real data (General/Rank Achieved/Played/Spotlights…) are OMITTED, not faked.
    /// </summary>
    private Drawable createFilterBlock() => filterBlock = new FillFlowContainer
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Vertical,
        Spacing = new Vector2(0, 6),
        Children = new[]
        {
            rulesetRow = new BeatmapSearchRulesetFilterRow(),
            categoryRow = new SupportedCategoryFilterRow(),
            genreRow = new BeatmapSearchFilterRow<SearchGenre>(BeatmapsStrings.ListingSearchFiltersGenre),
            languageRow = new BeatmapSearchFilterRow<SearchLanguage>(BeatmapsStrings.ListingSearchFiltersLanguage),
            extraRow = new BeatmapSearchMultipleSelectionFilterRow<SearchExtra>(BeatmapsStrings.ListingSearchFiltersExtra),
            starsRow = createStarsRow(),
            keywordOnlyHint = new SpriteText
            {
                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                Colour = Theme.TextTertiary,
                Alpha = 0,
            },
            sortStrip = new Container // sort strip below the filters, like lazer/osu-web
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Margin = new MarginPadding { Top = 2 },
                Children = new Drawable[]
                {
                    sortControl = new BeatmapListingSortTabControl
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                    },
                    backendHint = new SpriteText
                    {
                        Anchor = Anchor.CentreRight,
                        Origin = Anchor.CentreRight,
                        Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                        Colour = Theme.TextTertiary,
                    },
                },
            },
        },
    };

    /// <summary>Our star-range row (lazer's listing has no equivalent), matching
    /// <see cref="BeatmapSearchFilterRow{T}"/>'s exact label-column geometry (100px absolute
    /// column, size-13 label) so it aligns with the lazer rows around it.</summary>
    private Drawable createStarsRow()
    {
        SpriteText minText, maxText;

        var row = new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            ColumnDimensions = new[]
            {
                new Dimension(GridSizeMode.Absolute, size: filter_label_width),
                new Dimension(),
            },
            RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
            Content = new[]
            {
                new Drawable[]
                {
                    new OsuTextFlowContainer(t => t.Font = OsuFont.GetFont(size: 13))
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Text = "Stars",
                    },
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Full,
                        Spacing = new Vector2(8, 4),
                        Children = new Drawable[]
                        {
                            minStarsSlider = new RoundedSliderBar<double>
                            {
                                Width = slider_width,
                                Current = engine.MinStars,
                            },
                            minText = new SpriteText
                            {
                                Font = FontUsage.Default.With(size: 12),
                                Colour = Theme.TextSecondary,
                                Margin = new MarginPadding { Top = 5 },
                            },
                            maxStarsSlider = new RoundedSliderBar<double>
                            {
                                Width = slider_width,
                                Current = engine.MaxStars,
                            },
                            maxText = new SpriteText
                            {
                                Font = FontUsage.Default.With(size: 12),
                                Colour = Theme.TextSecondary,
                                Margin = new MarginPadding { Top = 5 },
                            },
                        },
                    },
                },
            },
        };

        engine.MinStars.BindValueChanged(e => minText.Text = e.NewValue > 0 ? $"min {e.NewValue:0.0}\u2605" : "min any", true);
        engine.MaxStars.BindValueChanged(e => maxText.Text = e.NewValue < 10 ? $"max {e.NewValue:0.0}\u2605" : "max any", true);

        return row;
    }

    /// <summary>Lazer's Categories row minus the auth-only items (Favourites/Mine need a
    /// signed-in account this app doesn't have) — omitted rather than shown dead, per the
    /// established rule for unbackable filters.</summary>
    private partial class SupportedCategoryFilterRow : BeatmapSearchFilterRow<SearchCategory>
    {
        public SupportedCategoryFilterRow()
            : base(BeatmapsStrings.ListingSearchFiltersStatus)
        {
        }

        protected override Drawable CreateFilter() => new SupportedCategoryFilter();

        private partial class SupportedCategoryFilter : BeatmapSearchFilter
        {
            public SupportedCategoryFilter()
            {
                // The base ctor added every enum member — drop the two this app can't back.
                RemoveItem(SearchCategory.Favourites);
                RemoveItem(SearchCategory.Mine);
            }
        }
    }

    // ---- Per-backend filter availability -----------------------------------------------------

    /// <summary>
    /// Shows exactly the rows the ACTIVE backend can actually answer — an unsupported filter is
    /// hidden, never shown dead, which is the same rule that already keeps the auth-only rows
    /// (Rank Achieved, Played, Favourites, Mine) off this panel entirely: none of them work with an
    /// app-only token, so neither backend can back them.
    ///
    /// <see cref="SearchApi.Official"/> answers everything the panel offers server-side, Genre and
    /// Language included. The mirrors cannot express those two at all — they used to be applied as
    /// a client-side sieve over loaded results, which is a filter that quietly means something
    /// different from what its label says, so on that backend the rows are simply absent.
    /// </summary>
    private void updateFilterAvailability()
    {
        var available = engine.AvailableFilters.Value;

        bool can(SearchFilters filter) => (available & filter) != 0;

        // Alpha rather than removal: every row carries a two-way binding to the engine that must
        // survive a backend change, and the VALUE must survive too — a filter hidden because the
        // only backend that could apply it went away has to come back exactly as the user left it.
        // A zero-Alpha child is not IsPresent, so the block's flow closes over it rather than
        // leaving a gap or an orphaned label.
        rulesetRow.Alpha = can(SearchFilters.Mode) ? 1 : 0;
        categoryRow.Alpha = can(SearchFilters.Status) ? 1 : 0;
        genreRow.Alpha = can(SearchFilters.Genre) ? 1 : 0;
        languageRow.Alpha = can(SearchFilters.Language) ? 1 : 0;
        extraRow.Alpha = can(SearchFilters.Extra) ? 1 : 0;
        starsRow.Alpha = can(SearchFilters.Stars) ? 1 : 0;
        sortStrip.Alpha = can(SearchFilters.Sort) ? 1 : 0;

        // With every refinement gone the block collapses to the keyword box alone, which would
        // otherwise read as the filters having vanished by accident. The hint moves up into the
        // block (the sort strip it normally sits in is itself hidden) and says what the source is.
        bool keywordOnly = (available & ~(SearchFilters.Keyword | SearchFilters.Paging)) == SearchFilters.None;

        keywordOnlyHint.Alpha = keywordOnly ? 1 : 0;
        keywordOnlyHint.Text = "this beatmap source can only search by keyword";

        backendHint.Text = engine.Api.Value == SearchApi.Official
            ? "official osu! API"
            : "beatmap mirror search";
    }

    /// <summary>
    /// Rebuilds the sort strip's item list when the criteria the backend supports change.
    /// <see cref="SearchApi.Official"/> gets lazer's own rules verbatim (Relevance with a query,
    /// Nominations under Pending, Updated/Ranked by category); the mirrors get the fixed
    /// query-independent set, since neither extra criterion means anything to their search.
    ///
    /// Guarded on the parameter pair because <see cref="BeatmapListingSortTabControl.Reset"/> also
    /// forces its own default criteria and direction — calling it per keystroke would fight the
    /// user's sort choice rather than just maintaining the list.
    /// </summary>
    private void updateSortAvailability()
    {
        var parameters = engine.Api.Value == SearchApi.Official
            ? (category: CategoryFromEngine(engine.Category.Value), hasQuery: engine.Query.Value.Length > 0)
            : (category: SearchCategory.Any, hasQuery: false);

        if (appliedSortParameters == parameters)
            return;

        appliedSortParameters = parameters;

        // Reset settles on its own default (Relevance with a query, else Ranked/Updated) and the
        // two-way binding carries that into the engine — which is exactly osu-web's behaviour when
        // a query appears or the category changes.
        sortControl.Reset(parameters.category, parameters.hasQuery);
    }

    // ---- Lazer filter enum <-> engine adapters -----------------------------------------------

    private void setExtra(SearchExtra extra, bool present)
    {
        if (present && !extraRow.Current.Contains(extra))
            extraRow.Current.Add(extra);
        else if (!present)
            extraRow.Current.Remove(extra);
    }

    private RulesetInfo rulesetInfoForMode(string? mode) => mode switch
    {
        "o" => rulesetStore.GetRuleset(0) ?? new RulesetInfo(),
        "t" => rulesetStore.GetRuleset(1) ?? new RulesetInfo(),
        "c" => rulesetStore.GetRuleset(2) ?? new RulesetInfo(),
        "m" => rulesetStore.GetRuleset(3) ?? new RulesetInfo(),
        // The Any tab item is `new RulesetInfo()` (see lazer's RulesetFilterTabItemAny) — an
        // equal empty instance selects it.
        _ => new RulesetInfo(),
    };

    internal static string? ModeForRuleset(RulesetInfo ruleset) => ruleset.OnlineID switch
    {
        0 => "o",
        1 => "t",
        2 => "c",
        3 => "m",
        _ => null,
    };

    internal static string CategoryToEngine(SearchCategory category) => category switch
    {
        SearchCategory.Any => "all",
        _ => category.ToString().ToLowerInvariant(),
    };

    internal static SearchCategory CategoryFromEngine(string category) => category switch
    {
        "all" => SearchCategory.Any,
        "leaderboard" => SearchCategory.Leaderboard,
        "qualified" => SearchCategory.Qualified,
        "loved" => SearchCategory.Loved,
        "pending" => SearchCategory.Pending,
        "wip" => SearchCategory.Wip,
        "graveyard" => SearchCategory.Graveyard,
        _ => SearchCategory.Ranked,
    };

    internal static SearchGenre GenreFromEngine(int? id)
        => id is int value && Enum.IsDefined(typeof(SearchGenre), value) ? (SearchGenre)value : SearchGenre.Any;

    internal static SearchLanguage LanguageFromEngine(int? id)
        => id is int value && Enum.IsDefined(typeof(SearchLanguage), value) ? (SearchLanguage)value : SearchLanguage.Any;

    internal static SortCriteria SortFromEngine(string key) => key switch
    {
        "title" => SortCriteria.Title,
        "artist" => SortCriteria.Artist,
        "difficulty" => SortCriteria.Difficulty,
        "updated" => SortCriteria.Updated,
        "rating" => SortCriteria.Rating,
        "plays" => SortCriteria.Plays,
        "favourites" => SortCriteria.Favourites,
        // Official-backend-only criteria (the mirrors' sort strip never offers them).
        "relevance" => SortCriteria.Relevance,
        "nominations" => SortCriteria.Nominations,
        _ => SortCriteria.Ranked,
    };

    /// <summary>
    /// Pops the listing open seeded with <paramref name="c"/> — the fullscreen style's
    /// type-anywhere entry point (<see cref="Screens.MainScreen"/> routes the keypress here
    /// instead of the docked column while the style is active).
    /// </summary>
    public void ShowWithInitialChar(char c)
    {
        ShowSearch();
        searchBox.Text = c.ToString();
    }

    /// <summary>
    /// Shows the listing with the keyword box focused (the docked-search-box focus hand-off entry
    /// point). The focus grab must be scheduled AFTER <see cref="VisibilityContainer.Show"/>
    /// returns — <see cref="FocusedOverlayContainer"/>'s own focus-contention pass is scheduled
    /// DURING Show (from UpdateState), unconditionally clears focus, and would otherwise hand it
    /// to the permanently-visible docked listing (which then eats Escape); queueing after Show
    /// makes this run after that pass, same as the docked listing's own scheduleFocus.
    /// </summary>
    public void ShowSearch()
    {
        Show();
        Schedule(() =>
        {
            if (State.Value == Visibility.Visible)
                GetContainingFocusManager()?.ChangeFocus(searchBox);
        });
    }

    /// <summary>The panel's parked Y while closed — just past the window's bottom edge, so the
    /// entrance reads as sliding up from offscreen. Falls back to a safely-large offset for a
    /// pre-layout first show (DrawHeight not yet computed).</summary>
    private float offscreenPanelY() => DrawHeight > 0 ? DrawHeight : 1000;

    // The overlay ROOT flips to Alpha 1 instantly — focus can only land on a present drawable
    // (same reasoning as the docked listing's PopIn), and ShowSearch's scheduled focus grab needs
    // the keyword box present this frame. The visible entrance is carried entirely by the scrim
    // fading in and the panel sliding up from past the bottom edge (position never affects
    // presence, so the box is focusable even mid-slide).
    protected override void PopIn()
    {
        Alpha = 1;

        scrim.FadeIn(Theme.DurationNormal, Theme.EaseEnter);

        panel.MoveToY(offscreenPanelY());
        panel.MoveToY(0, Theme.DurationNormal, Theme.EaseEnter);
    }

    protected override void PopOut()
    {
        // The reverse of the entrance: panel slides back down past the bottom edge while the
        // scrim (and the whole overlay with it) fade away.
        scrim.FadeOut(Theme.DurationNormal, Theme.EaseExit);
        panel.MoveToY(offscreenPanelY(), Theme.DurationNormal, Theme.EaseExit);
        this.FadeOut(Theme.DurationNormal, Theme.EaseExit);

        // The preview must never outlive the overlay (and the main track must resume) — the
        // "preview can never wedge the jukebox" contract. The floating expansion resets with it.
        previewPlayer.Stop();
        collapseExpansion();

        // The docked listing (the other view over this engine) stays alive, so only the pending
        // debounce tied to closing interaction is cancelled; results themselves stay for both views.
        engine.CancelPendingSearch();
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
        selectedIndex = newIndex;

        for (int i = 0; i < cardsFlow.Count; i++)
            cardsFlow.Children[i].Selected.Value = i == newIndex;
    }

    /// <summary>Enter — queue the highlighted card (falling back to the first) and close back to
    /// the player, matching the keyboard flow contract ("Esc/queue closes").</summary>
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

    protected override void Update()
    {
        base.Update();

        // Same manual width sync as the docked listing (FillDirection.Full forbids relative
        // sizing), targeting up to three columns like the osu-web reference.
        int columns = Math.Clamp((int)(cardsFlow.DrawWidth / min_column_width), 1, max_columns);
        float cardWidth = cardsFlow.DrawWidth / columns;

        if (Math.Abs(cardWidth - lastCardWidth) > 0.5f)
        {
            lastCardWidth = cardWidth;

            foreach (var card in cardsFlow)
                card.Width = cardWidth;
        }

        if (settleFrames > 0)
        {
            settleFrames--;
            return;
        }

        if (State.Value != Visibility.Visible)
            return;

        engine.UpdatePaging(
            contentOverflows: scroll.AvailableContent > scroll.DisplayableContent + 1,
            nearEnd: scroll.IsScrolledToEnd(scroll_load_threshold));
    }

    private void rebuildCards()
    {
        // Cards are being replaced wholesale — any floating expansion refers to a card that is
        // about to leave the tree.
        collapseExpansion();

        cardsFlow.Clear();
        selectedIndex = -1;
        settleFrames = 2;

        var visible = engine.VisibleSets.ToList();

        if (visible.Count == 0)
        {
            previouslyShownSets.Clear();
            return;
        }

        int flowIndex = 0;

        foreach (var set in visible)
        {
            var card = new FullscreenBeatmapCard(set, flowIndex++);

            if (lastCardWidth > 0)
                card.Width = lastCardWidth;

            // Same disabled-set contract as the docked listing: no Action means framework-level
            // disabled (dimmed by the card itself, absorbs no clicks).
            card.Action = set.DownloadDisabled ? null : () => pick(card);
            card.ExpansionHoverChanged += onCardExpansionHoverChanged;
            card.PreviewRequested += onPreviewRequested;
            card.BrowseRequested += onBrowseRequested;
            card.PreviewingSetId.Value = previewPlayer.PlayingSetId.Value;
            cardsFlow.Add(card);

            if (!previouslyShownSets.Contains(set))
            {
                float targetAlpha = card.Alpha;
                card.Alpha = 0;
                card.FadeTo(targetAlpha, Theme.DurationNormal, Theme.EaseEnter);
            }
        }

        previouslyShownSets.Clear();
        previouslyShownSets.UnionWith(visible);
    }

    private void pick(FullscreenBeatmapCard card)
    {
        if (card.Set.DownloadDisabled)
            return;

        // Mouse flow keeps the listing open (same as the docked listing) — Enter is what closes.
        SetPicked?.Invoke(card.Set);
    }

    // ---- Hover expansion (single floating overlay above the grid) ----------------------------

    /// <summary>
    /// Moves the SINGLE floating expansion between cards. Lazer behaviour and timing: the trigger
    /// is the card's DIFFICULTY STRIP (not the whole card), and both directions are debounced by
    /// <see cref="expand_delay_ms"/> (lazer's <c>BeatmapCardContent.queueExpandedStateChange</c>
    /// 100ms) — hovering the strip expands after the delay (instantly replacing whichever card
    /// was expanded — never two at once), leaving it schedules a collapse that is cancelled while
    /// the cursor is on the strip or the expansion panel itself (so mousing from the strip down
    /// into the difficulty list keeps it open).
    /// </summary>
    private void onCardExpansionHoverChanged(FullscreenBeatmapCard card, bool hovered)
    {
        if (hovered)
        {
            pendingCollapse?.Cancel();
            pendingExpand?.Cancel();
            // Re-checked at fire time: a strip the cursor merely passed through (or one whose
            // hover-lost arrived after another strip's hover — event ordering isn't guaranteed)
            // must not expand.
            pendingExpand = Scheduler.AddDelayed(() =>
            {
                if (card.DifficultyStrip.IsHovered)
                    expandCard(card);
            }, expand_delay_ms);
        }
        else
            scheduleCollapse();
    }

    private void expandCard(FullscreenBeatmapCard card)
    {
        pendingCollapse?.Cancel();

        if (expandedCard == card)
            return;

        collapseExpansion();

        expandedCard = card;
        card.Expanded.Value = true;

        // Raised so the card's grown surface draws (and hit-tests) over the neighbouring rows —
        // layout is unaffected because CardFlow flows by FlowIndex, not depth.
        if (card.Parent == cardsFlow)
            cardsFlow.ChangeChildDepth(card, -1);
    }

    /// <summary>See <see cref="onCardExpansionHoverChanged"/> — the collapse side of the same
    /// debounce, so the pointer can travel from the strip onto the expansion panel (or vice
    /// versa) without the expansion flickering away in between.</summary>
    private void scheduleCollapse()
    {
        // Deliberately does NOT cancel a pending expand: hover-lost on the previous card's strip
        // can arrive AFTER hover on the next card's strip within the same input pass, and
        // cancelling here would eat that handover. The pending expand self-guards on the strip
        // still being hovered, and expandCard cancels this collapse when it wins.
        pendingCollapse?.Cancel();
        pendingCollapse = Scheduler.AddDelayed(() =>
        {
            if (expandedCard?.DifficultyStrip.IsHovered != true && expandedCard?.ExpansionDropdown.IsHovered != true)
                collapseExpansion();
        }, expand_delay_ms);
    }

    private void collapseExpansion()
    {
        pendingExpand?.Cancel();
        pendingCollapse?.Cancel();

        if (expandedCard != null)
        {
            expandedCard.Expanded.Value = false;

            if (expandedCard.Parent == cardsFlow)
                cardsFlow.ChangeChildDepth(expandedCard, 0);
        }

        expandedCard = null;
    }

    /// <summary>Browser icon on a card's hover rail: opens the set's osu.ppy.sh page in the
    /// system browser. <see cref="OpenUrl"/> is the network-less test seam.</summary>
    private void onBrowseRequested(FullscreenBeatmapCard card)
    {
        string url = $"https://osu.ppy.sh/beatmapsets/{card.Set.Id}";

        if (OpenUrl != null)
            OpenUrl(url);
        else
            host.OpenUrlExternally(url);
    }

    /// <summary>Test seam (JukeBox.Game.Tests has InternalsVisibleTo): replaces
    /// <see cref="osu.Framework.Platform.GameHost.OpenUrlExternally"/> so tests can assert the
    /// browsed URL without actually opening a browser.</summary>
    internal Action<string>? OpenUrl;

    /// <summary>▶ on a card: toggle if this set is already previewing, else start its preview
    /// (implicitly replacing any other set's).</summary>
    private void onPreviewRequested(FullscreenBeatmapCard card)
    {
        if (previewPlayer.PlayingSetId.Value == card.Set.Id)
            previewPlayer.Stop();
        else
            previewPlayer.Play(card.Set.Id);
    }

    /// <summary>
    /// A grid whose layout order is pinned to each card's <see cref="FullscreenBeatmapCard.FlowIndex"/>
    /// instead of the default depth-derived order — so raising an expanded card's depth (see
    /// <see cref="expandCard"/>) reorders drawing/input WITHOUT reflowing the grid.
    /// </summary>
    private partial class CardFlow : FillFlowContainer<FullscreenBeatmapCard>
    {
        public override IEnumerable<Drawable> FlowingChildren
            => AliveInternalChildren.Where(d => d.IsPresent).OfType<FullscreenBeatmapCard>().OrderBy(c => c.FlowIndex);
    }
}
