#nullable enable

using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
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
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// The <see cref="Configuration.SearchStyle.Fullscreen"/> presentation: an osu-web-style "beatmap
/// listing" page presented as a TRUE fullscreen modal — hosted at <see cref="Screens.MainScreen"/>'s
/// top level so it covers the ENTIRE window (side columns and bottom bar included), a centred
/// listing panel above a dim <see cref="Theme.ModalScrim"/> with nothing else interactive while
/// open. Opening (type-anywhere via <see cref="ShowWithInitialChar"/>, the docked search box
/// gaining focus, or the left column's search icon button — all via <see cref="ShowSearch"/>)
/// slides the panel UP from past the window's bottom edge while the scrim fades in (see
/// <see cref="PopIn"/>); Escape and the Enter-queue flow reverse it (slide down + fade) back to
/// the normal layout.
///
/// A pure VIEW over the shared <see cref="BeatmapSearchEngine"/> (the same instance driving the
/// docked <see cref="BeatmapListingOverlay"/>): the big keyword box binds the same
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
    private const float label_width = 92;
    private const float scroll_load_threshold = 200;
    private const float min_column_width = 260;
    private const int max_columns = 3;

    /// <summary>Width of the lazer-styled filter controls (dropdowns, sliders, checkboxes).</summary>
    private const float control_width = 150;

    /// <summary>
    /// The engine's ruleset filter as a dropdown-friendly enum — the engine speaks NeriNyan's raw
    /// single-letter strings (see <see cref="BeatmapSearchEngine.Mode"/>), adapted both ways in
    /// LoadComplete so this stays a pure presentation shape.
    /// </summary>
    public enum SearchMode
    {
        Any,

        [Description("osu!")]
        Osu,

        [Description("osu!taiko")]
        Taiko,

        [Description("osu!catch")]
        Catch,

        [Description("osu!mania")]
        Mania,
    }

    /// <summary>The engine's category filter (<see cref="BeatmapSearchEngine.Category"/>) as a
    /// dropdown-friendly enum, adapted both ways in LoadComplete.</summary>
    public enum SearchCategory
    {
        Any,
        Ranked,
        Qualified,
        Loved,
        Pending,

        [Description("WIP")]
        Wip,

        Graveyard,
    }

    /// <summary>The engine's sort key (<see cref="BeatmapSearchEngine.SortKey"/>) as a
    /// dropdown-friendly enum, adapted both ways in LoadComplete.</summary>
    public enum SortField
    {
        Ranked,
        Plays,
        Favourites,
        Difficulty,
        Updated,
        Title,
    }

    // The exact DI lazer's SettingsPanel provides its subtree (and SettingsOverlay already
    // reuses): every lazer control below (OsuEnumDropdown/RoundedSliderBar/OsuCheckbox) resolves
    // this for the purple pill/slider/dropdown palette.
    [Cached]
    private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

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

    private OsuEnumDropdown<SearchMode> modeDropdown = null!;
    private OsuEnumDropdown<SearchCategory> categoryDropdown = null!;
    private OsuEnumDropdown<SortField> sortDropdown = null!;
    private OsuCheckbox hasVideoCheckbox = null!;
    private OsuCheckbox hasStoryboardCheckbox = null!;
    private RoundedSliderBar<double> minStarsSlider = null!;
    private RoundedSliderBar<double> maxStarsSlider = null!;

    // Two-way adapters between the dropdowns' enum shapes and the engine's raw string bindables
    // (same shape as SettingsOverlay's hardwareAccelerationEnabled adapter) — wired in
    // LoadComplete; changes from either presentation's controls round-trip through the engine, so
    // the docked listing's chips and these dropdowns stay in sync.
    private readonly Bindable<SearchMode> modeAdapter = new Bindable<SearchMode>();
    private readonly Bindable<SearchCategory> categoryAdapter = new Bindable<SearchCategory>();
    private readonly Bindable<SortField> sortAdapter = new Bindable<SortField>();

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

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the currently
    /// expanded card (null when nothing is expanded) — there can only ever be one.</summary>
    internal FullscreenBeatmapCard? ExpandedCard => expandedCard;

    // Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the lazer-styled filter
    // controls, to drive/assert them without depending on internal layout.
    internal OsuEnumDropdown<SearchMode> ModeDropdown => modeDropdown;
    internal OsuEnumDropdown<SearchCategory> CategoryDropdown => categoryDropdown;
    internal OsuEnumDropdown<SortField> SortDropdown => sortDropdown;
    internal OsuCheckbox HasVideoCheckbox => hasVideoCheckbox;
    internal OsuCheckbox HasStoryboardCheckbox => hasStoryboardCheckbox;
    internal RoundedSliderBar<double> MinStarsSlider => minStarsSlider;
    internal RoundedSliderBar<double> MaxStarsSlider => maxStarsSlider;

    public FullscreenListingOverlay(BeatmapSearchEngine engine)
    {
        this.engine = engine;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChildren = new Drawable[]
        {
            previewPlayer = new PreviewPlayer(),
            // Dims the whole window (columns and bottom bar included — this overlay sits at
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
                                        scroll = new BasicScrollContainer
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            // A full section of clearance between the (fixed)
                                            // filter block above and the scrolling grid — the
                                            // grid's masked viewport starts below this padding,
                                            // so cards can never collide with the filter rows.
                                            Padding = new MarginPadding { Top = Theme.SectionSpacing + 4 },
                                            Child = cardsFlow = new CardFlow
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

        // Dropdown-enum <-> engine-string two-way sync (see the adapter fields): seed from the
        // engine's current values, then mirror changes in both directions. The value writes are
        // idempotent, so the echo of each direction is a no-op rather than a loop.
        modeAdapter.Value = FromEngineMode(engine.Mode.Value);
        modeAdapter.BindValueChanged(e => engine.Mode.Value = ToEngineMode(e.NewValue));
        engine.Mode.BindValueChanged(e => modeAdapter.Value = FromEngineMode(e.NewValue));
        modeDropdown.Current = modeAdapter;

        categoryAdapter.Value = FromEngineCategory(engine.Category.Value);
        categoryAdapter.BindValueChanged(e => engine.Category.Value = ToEngineCategory(e.NewValue));
        engine.Category.BindValueChanged(e => categoryAdapter.Value = FromEngineCategory(e.NewValue));
        categoryDropdown.Current = categoryAdapter;

        sortAdapter.Value = FromEngineSort(engine.SortKey.Value);
        sortAdapter.BindValueChanged(e => engine.SortKey.Value = ToEngineSort(e.NewValue));
        engine.SortKey.BindValueChanged(e => sortAdapter.Value = FromEngineSort(e.NewValue));
        sortDropdown.Current = sortAdapter;

        // The toggles and sliders bind the engine's bindables directly — no adapters needed.
        hasVideoCheckbox.Current = engine.HasVideo;
        hasStoryboardCheckbox.Current = engine.HasStoryboard;

        // Pushed (rather than each card binding the player's bindable) because cards are
        // rebuilt wholesale on every result change — pushing the current value at rebuild plus
        // this one subscription keeps every generation of cards in sync with no per-card unbinds.
        previewPlayer.PlayingSetId.BindValueChanged(e =>
        {
            foreach (var card in cardsFlow)
                card.PreviewingSetId.Value = e.NewValue;
        });
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
    /// The osu-web-style labelled filter block, presented with lazer's real settings controls
    /// (the same components/theme <see cref="SettingsOverlay"/> established, coloured by the
    /// cached purple <see cref="OverlayColourProvider"/>): dropdowns for the single-choice
    /// Mode/Categories/Sort rows, <see cref="RoundedSliderBar{T}"/>s for the star range and
    /// <see cref="OsuCheckbox"/> pills for the Extra toggles. Genre/Language keep the osu-web
    /// chip rows — 14 always-visible options read better as chips than a dropdown. Rows that
    /// would need data this app doesn't have (Rank Achieved, Played, Recommended difficulty,
    /// Subscribed mappers, Explicit content) are OMITTED rather than rendered dead.
    /// </summary>
    private Drawable createFilterBlock() => new FillFlowContainer
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Vertical,
        Spacing = new Vector2(0, 6),
        Children = new[]
        {
            controlRow("Mode", modeDropdown = new OsuEnumDropdown<SearchMode>
            {
                RelativeSizeAxes = Axes.None,
                Width = control_width,
            }),
            controlRow("Categories", categoryDropdown = new OsuEnumDropdown<SearchCategory>
            {
                RelativeSizeAxes = Axes.None,
                Width = control_width,
            }),
            ListingFilterRows.CreateChipRow("Genre", "filters loaded results", label_width, ListingFilterRows.SingleSelect(new (string, int?)[]
            {
                ("Any", null), ("Video Game", 2), ("Anime", 3), ("Rock", 4), ("Pop", 5), ("Other", 6),
                ("Novelty", 7), ("Hip Hop", 9), ("Electronic", 10), ("Metal", 11), ("Classical", 12),
                ("Folk", 13), ("Jazz", 14),
            }, engine.GenreId)),
            ListingFilterRows.CreateChipRow("Language", "filters loaded results", label_width, ListingFilterRows.SingleSelect(new (string, int?)[]
            {
                ("Any", null), ("English", 2), ("Japanese", 3), ("Chinese", 4), ("Korean", 6),
                ("Instrumental", 5), ("German", 8), ("French", 7), ("Italian", 11), ("Spanish", 10),
                ("Swedish", 9), ("Russian", 12), ("Polish", 13), ("Other", 14),
            }, engine.LanguageId)),
            controlRow("Extra",
                hasVideoCheckbox = createFilterCheckbox("Has Video"),
                hasStoryboardCheckbox = createFilterCheckbox("Has Storyboard")),
            controlRow("Sort by", new Drawable[]
            {
                sortDropdown = new OsuEnumDropdown<SortField>
                {
                    RelativeSizeAxes = Axes.None,
                    Width = control_width,
                },
            }.Concat(ListingFilterRows.SingleSelect(new (string, bool)[]
            {
                ("desc", true), ("asc", false),
            }, engine.SortDescending)).ToArray()),
            createStarsRow(),
        },
    };

    /// <summary>One labelled filter row hosting arbitrary controls, matching the chip rows'
    /// osu-web layout (same shared label column).</summary>
    private static Drawable controlRow(string label, params Drawable[] controls)
    {
        var row = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Full,
            Spacing = new Vector2(8, 6),
        };

        row.Add(ListingFilterRows.CreateRowLabel(label, label_width));

        foreach (var control in controls)
            row.Add(control);

        return row;
    }

    /// <summary>An <see cref="OsuCheckbox"/> pill sized for an inline filter row — its
    /// constructor assumes a full-width settings row (RelativeSizeAxes.X), overridden here to the
    /// shared fixed control width.</summary>
    private static OsuCheckbox createFilterCheckbox(string label) => new OsuCheckbox
    {
        LabelText = label,
        RelativeSizeAxes = Axes.None,
        AutoSizeAxes = Axes.Y,
        Width = control_width,
    };

    private Drawable createStarsRow()
    {
        SpriteText minText, maxText;

        var row = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Full,
            Spacing = new Vector2(8, 6),
            Children = new[]
            {
                ListingFilterRows.CreateRowLabel("Stars", label_width),
                minStarsSlider = new RoundedSliderBar<double>
                {
                    Width = control_width,
                    Current = engine.MinStars,
                },
                minText = new SpriteText
                {
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextSecondary,
                    Margin = new MarginPadding { Top = 5 },
                },
                maxStarsSlider = new RoundedSliderBar<double>
                {
                    Width = control_width,
                    Current = engine.MaxStars,
                },
                maxText = new SpriteText
                {
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextSecondary,
                    Margin = new MarginPadding { Top = 5 },
                },
            },
        };

        engine.MinStars.BindValueChanged(e => minText.Text = e.NewValue > 0 ? $"min {e.NewValue:0.0}★" : "min any", true);
        engine.MaxStars.BindValueChanged(e => maxText.Text = e.NewValue < 10 ? $"max {e.NewValue:0.0}★" : "max any", true);

        return row;
    }

    // ---- Dropdown-enum <-> engine-string adapters --------------------------------------------

    internal static string? ToEngineMode(SearchMode mode) => mode switch
    {
        SearchMode.Osu => "o",
        SearchMode.Taiko => "t",
        SearchMode.Catch => "c",
        SearchMode.Mania => "m",
        _ => null,
    };

    internal static SearchMode FromEngineMode(string? mode) => mode switch
    {
        "o" => SearchMode.Osu,
        "t" => SearchMode.Taiko,
        "c" => SearchMode.Catch,
        "m" => SearchMode.Mania,
        _ => SearchMode.Any,
    };

    internal static string ToEngineCategory(SearchCategory category)
        => category == SearchCategory.Any ? "all" : category.ToString().ToLowerInvariant();

    internal static SearchCategory FromEngineCategory(string category) => category switch
    {
        "all" => SearchCategory.Any,
        "qualified" => SearchCategory.Qualified,
        "loved" => SearchCategory.Loved,
        "pending" => SearchCategory.Pending,
        "wip" => SearchCategory.Wip,
        "graveyard" => SearchCategory.Graveyard,
        _ => SearchCategory.Ranked,
    };

    internal static string ToEngineSort(SortField field) => field.ToString().ToLowerInvariant();

    internal static SortField FromEngineSort(string key) => key switch
    {
        "plays" => SortField.Plays,
        "favourites" => SortField.Favourites,
        "difficulty" => SortField.Difficulty,
        "updated" => SortField.Updated,
        "title" => SortField.Title,
        _ => SortField.Ranked,
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
