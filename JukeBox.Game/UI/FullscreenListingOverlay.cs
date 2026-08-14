#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
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
/// Hovering a card raises its depth in the grid (the grid's layout order follows
/// <see cref="FullscreenBeatmapCard.FlowIndex"/>, not depth — see <see cref="CardFlow"/>) so its
/// expanded difficulty panel draws over the neighbouring rows. The ▶ preview button routes to the
/// owned <see cref="PreviewPlayer"/>, which pauses the main jukebox track for the preview's
/// duration and is always stopped when this overlay closes.
/// </summary>
public partial class FullscreenListingOverlay : FocusedOverlayContainer
{
    private const float label_width = 110;
    private const float scroll_load_threshold = 200;
    private const float min_column_width = 300;
    private const int max_columns = 3;

    private readonly BeatmapSearchEngine engine;

    public event Action<BeatmapSetInfo>? SetPicked;

    private ListingSearchBox searchBox = null!;
    private CardFlow cardsFlow = null!;
    private BasicScrollContainer scroll = null!;
    private SpriteText statusText = null!;
    private PreviewPlayer previewPlayer = null!;
    private Box scrim = null!;
    private Container panel = null!;

    private int selectedIndex = -1;
    private int settleFrames;
    private float lastCardWidth;

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
                Padding = new MarginPadding(Theme.PanelPadding * 1.5f),
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
                            Size = new Vector2(30),
                            Masking = true,
                            CornerRadius = 7,
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
                                    Size = new Vector2(14),
                                    Colour = Theme.TextPrimary,
                                },
                            },
                        },
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = "beatmap listing",
                            Font = FontUsage.Default.With(size: 24),
                            Colour = Theme.TextPrimary,
                        },
                    },
                },
                new Container // big keyword box (text scales with the box height)
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 52,
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
                            Size = new Vector2(18),
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
    /// The osu-web-style labelled filter block. Rows mirror the reference layout where the engine
    /// has real data to back them (Mode/Categories/Genre/Language/Extra + Sort + Stars); rows that
    /// would need data this app doesn't have (Rank Achieved, Played, Recommended difficulty,
    /// Subscribed mappers, Explicit content) are OMITTED rather than rendered dead.
    /// </summary>
    private Drawable createFilterBlock() => new FillFlowContainer
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Vertical,
        Spacing = new Vector2(0, Theme.RowSpacing),
        Children = new[]
        {
            ListingFilterRows.CreateChipRow("Mode", null, label_width, ListingFilterRows.SingleSelect(new (string, string?)[]
            {
                ("Any", null), ("osu!", "o"), ("osu!taiko", "t"), ("osu!catch", "c"), ("osu!mania", "m"),
            }, engine.Mode)),
            ListingFilterRows.CreateChipRow("Categories", null, label_width, ListingFilterRows.SingleSelect(new (string, string)[]
            {
                ("Any", "all"), ("Ranked", "ranked"), ("Qualified", "qualified"), ("Loved", "loved"),
                ("Pending", "pending"), ("WIP", "wip"), ("Graveyard", "graveyard"),
            }, engine.Category)),
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
            ListingFilterRows.CreateChipRow("Extra", null, label_width, new Drawable[]
            {
                ListingFilterRows.Toggle("Has Video", engine.HasVideo),
                ListingFilterRows.Toggle("Has Storyboard", engine.HasStoryboard),
            }),
            ListingFilterRows.CreateChipRow("Sort by", null, label_width, ListingFilterRows.SingleSelect(new (string, string)[]
            {
                ("Ranked", "ranked"), ("Plays", "plays"), ("Favourites", "favourites"),
                ("Difficulty", "difficulty"), ("Updated", "updated"), ("Title", "title"),
            }, engine.SortKey).Concat(ListingFilterRows.SingleSelect(new (string, bool)[]
            {
                ("desc", true), ("asc", false),
            }, engine.SortDescending)).ToArray()),
            createStarsRow(),
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
                ListingFilterRows.CreateRowLabel("Stars", label_width),
                new BasicSliderBar<double>
                {
                    Size = new Vector2(150, 16),
                    Current = engine.MinStars,
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
                    Size = new Vector2(150, 16),
                    Current = engine.MaxStars,
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

        engine.MinStars.BindValueChanged(e => minText.Text = e.NewValue > 0 ? $"min {e.NewValue:0.0}★" : "min any", true);
        engine.MaxStars.BindValueChanged(e => maxText.Text = e.NewValue < 10 ? $"max {e.NewValue:0.0}★" : "max any", true);

        return row;
    }

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
        // "preview can never wedge the jukebox" contract.
        previewPlayer.Stop();

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
            card.HoverChanged += onCardHoverChanged;
            card.PreviewRequested += onPreviewRequested;
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

    /// <summary>Raises a hovered card above its neighbours so the expanded difficulty panel draws
    /// (and hit-tests) over them; layout is unaffected because <see cref="CardFlow"/> flows by
    /// <see cref="FullscreenBeatmapCard.FlowIndex"/>, not depth.</summary>
    private void onCardHoverChanged(FullscreenBeatmapCard card, bool hovered)
    {
        if (card.Parent == cardsFlow)
            cardsFlow.ChangeChildDepth(card, hovered ? -1 : 0);
    }

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
    /// instead of the default depth-derived order — so the hover depth-raise (see
    /// <see cref="onCardHoverChanged"/>) reorders drawing/input WITHOUT reflowing the grid.
    /// </summary>
    private partial class CardFlow : FillFlowContainer<FullscreenBeatmapCard>
    {
        public override IEnumerable<Drawable> FlowingChildren
            => AliveInternalChildren.Where(d => d.IsPresent).OfType<FullscreenBeatmapCard>().OrderBy(c => c.FlowIndex);
    }
}
