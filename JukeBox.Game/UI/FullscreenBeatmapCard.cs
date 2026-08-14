#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// One card in <see cref="FullscreenListingOverlay"/>'s three-column grid, styled after osu-web's
/// listing card. Collapsed it shows the cover, title / "by artist" / "mapped by creator", a stats
/// row (▶ play count, ♥ favourite count, ✓ ranked date), the status pill and mini difficulty
/// dots, and a ▶ preview button over the left thumb area. Card hover reveals lazer's detail
/// state at lazer's timing: the statistics row fades in and the right-side icon rail slides in
/// (plus = add to queue via the existing enqueue path, browser = open the set's osu.ppy.sh page).
///
/// The hover EXPANSION (accent border + the per-difficulty list unfolding below) is NOT this
/// card's concern: the DIFFICULTY STRIP reports its hover via <see cref="ExpansionHoverChanged"/>
/// (lazer behaviour — the strip, not the whole card, is the trigger) and the card shows the
/// accent border while <see cref="Expanded"/>. The owning overlay renders the difficulty panel as
/// a floating layer ABOVE the grid at this card's position (see
/// <c>FullscreenListingOverlay.CardExpansion</c>) — the card's own flow footprint NEVER changes,
/// so hovering can never reflow the grid, and the overlay enforces that exactly one card is
/// expanded at a time.
/// </summary>
public partial class FullscreenBeatmapCard : ClickableContainer
{
    public const float HEIGHT = 116;

    /// <summary>Lazer's <c>BeatmapCard.TRANSITION_DURATION</c> — every hover/expand transition on
    /// the card runs at this rate with OutQuint, matching the in-game cards exactly.</summary>
    public const float TRANSITION_DURATION = 360;

    private const float thumb_width = 96;
    private const float gutter = 5;
    private const int max_bars = 10;

    public BeatmapSetInfo Set { get; }

    /// <summary>Driven by the owning overlay's keyboard navigation (accent border highlight).</summary>
    public readonly BindableBool Selected = new();

    /// <summary>Set by the owning overlay while this card owns the (single) floating expansion —
    /// drives the accent border, so the border always matches the actually-expanded card rather
    /// than raw hover.</summary>
    public readonly BindableBool Expanded = new();

    /// <summary>The DIFFICULTY STRIP's hovered state changed (lazer behaviour: the per-difficulty
    /// expansion is triggered by the bottom strip, not the whole card) — the owning overlay uses
    /// this to move its single floating expansion between cards.</summary>
    public event Action<FullscreenBeatmapCard, bool>? ExpansionHoverChanged;

    /// <summary>▶ preview button clicked — the owning overlay routes this to its
    /// <see cref="Playback.PreviewPlayer"/> (toggling if this set is already previewing).</summary>
    public event Action<FullscreenBeatmapCard>? PreviewRequested;

    /// <summary>Browser icon in the hover rail clicked — the owning overlay opens
    /// <c>https://osu.ppy.sh/beatmapsets/{id}</c> externally.</summary>
    public event Action<FullscreenBeatmapCard>? BrowseRequested;

    /// <summary>Bound (by the owning overlay) to <see cref="Playback.PreviewPlayer.PlayingSetId"/> so
    /// the preview button flips to a stop icon while this set's preview is audible.</summary>
    public readonly Bindable<int?> PreviewingSetId = new Bindable<int?>();

    [Resolved(canBeNull: true)]
    private OnlineThumbnailStore? thumbnailStore { get; set; }

    private Container content = null!;
    private Container coverContainer = null!;
    private IconButton previewButton = null!;
    private FillFlowContainer statsRow = null!;
    private Container iconRail = null!;
    private DotsStrip dotsStrip = null!;
    private RailButton plusButton = null!;
    private RailButton browseButton = null!;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the ▶ preview
    /// button, to click it without depending on internal layout.</summary>
    internal IconButton PreviewButton => previewButton;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the difficulty
    /// strip — the hover target that triggers the floating expansion.</summary>
    internal Container DifficultyStrip => dotsStrip;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the hover rail's
    /// add-to-queue button.</summary>
    internal ClickableContainer PlusButton => plusButton;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the hover rail's
    /// open-in-browser button.</summary>
    internal ClickableContainer BrowseButton => browseButton;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the hover rail
    /// itself, to assert its slide-in visibility.</summary>
    internal Container IconRail => iconRail;

    /// <summary>The horizontal/vertical inset between this drawable's bounds and the visible card
    /// (the grid gutter) — the owning overlay's floating expansion aligns to the visible card,
    /// not the padded bounds.</summary>
    public const float GUTTER = gutter;

    public FullscreenBeatmapCard(BeatmapSetInfo set)
    {
        Set = set;
        Width = 400;
        Height = HEIGHT;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Padding = new MarginPadding(gutter);

        // Same reasoning as BeatmapCard: the entrance fade starts from Alpha 0, and hover/click
        // hit-testing needs the drawable present.
        AlwaysPresent = true;

        InternalChildren = new Drawable[]
        {
            content = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = Theme.CornerRadius,
                BorderColour = Theme.Accent,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Theme.ElevatedSurface,
                    },
                    coverContainer = new Container { RelativeSizeAxes = Axes.Both },
                    new Box // dark gradient for text legibility over any cover art.
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = ColourInfo.GradientVertical(Color4.Black.Opacity(0.5f), Color4.Black.Opacity(0.85f)),
                    },
                    previewButton = new IconButton
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.Centre,
                        X = thumb_width / 2,
                        Size = new Vector2(40),
                        Icon = FontAwesome.Solid.Play,
                        IdleColour = Color4.Black.Opacity(0.35f),
                        HoverColour = Theme.AccentDim,
                        Action = () => PreviewRequested?.Invoke(this),
                    },
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Padding = new MarginPadding { Left = thumb_width + 10, Right = 40, Top = 8 },
                        Spacing = new Vector2(0, 1),
                        Children = new Drawable[]
                        {
                            new SpriteText
                            {
                                Text = Set.DisplayTitle,
                                Font = FontUsage.Default.With(family: "Roboto", weight: "Bold", size: Theme.RowTitleTextSize),
                                Colour = Theme.TextPrimary,
                                Truncate = true,
                                RelativeSizeAxes = Axes.X,
                            },
                            new SpriteText
                            {
                                Text = $"by {Set.DisplayArtist}",
                                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                Colour = Theme.TextSecondary,
                                Truncate = true,
                                RelativeSizeAxes = Axes.X,
                            },
                            new SpriteText
                            {
                                Text = $"mapped by {Set.Creator}",
                                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                Colour = Theme.TextTertiary,
                                Truncate = true,
                                RelativeSizeAxes = Axes.X,
                            },
                            // Lazer shows the statistics row only while the card is hovered
                            // (statisticsContainer fade in BeatmapCardNormal.UpdateState).
                            statsRow = createStatsRow(),
                        },
                    },
                    // The difficulty strip (status pill + mode icons + coloured bars) — its OWN
                    // hover-receiving container, because in lazer's card THIS strip (not the
                    // whole card) is what triggers the per-difficulty expansion.
                    dotsStrip = new DotsStrip
                    {
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        AutoSizeAxes = Axes.Both,
                        Child = new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Padding = new MarginPadding { Left = thumb_width + 10, Bottom = 8, Top = 4, Right = 8 },
                            Spacing = new Vector2(6, 0),
                            Children = createStatusLine(),
                        },
                    },
                    // The hover icon rail (lazer's CollapsibleButtonContainer buttons, our
                    // actions): plus = add to queue (existing enqueue path, listing stays open),
                    // browser = open the set's osu.ppy.sh page externally. Slides in from the
                    // right edge while the card is hovered.
                    iconRail = new Container
                    {
                        Anchor = Anchor.CentreRight,
                        Origin = Anchor.CentreRight,
                        RelativeSizeAxes = Axes.Y,
                        Width = 30,
                        Alpha = 0,
                        X = 10,
                        Child = new FillFlowContainer
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 14),
                            Children = new Drawable[]
                            {
                                plusButton = new RailButton(FontAwesome.Solid.Plus)
                                {
                                    // Same enqueue path as clicking the card (no-op while the
                                    // set is download-disabled, since Action stays null there).
                                    Action = () => TriggerClick(),
                                },
                                browseButton = new RailButton(FontAwesome.Solid.ExternalLinkAlt)
                                {
                                    Action = () => BrowseRequested?.Invoke(this),
                                },
                            },
                        },
                    },
                },
            },
        };

        if (Set.DownloadDisabled)
            Alpha = 0.4f;

        Selected.BindValueChanged(_ => updateBorder(), true);
        Expanded.BindValueChanged(_ => updateBorder(), true);
        PreviewingSetId.BindValueChanged(e => previewButton.Icon = e.NewValue == Set.Id ? FontAwesome.Solid.Stop : FontAwesome.Solid.Play, true);

        dotsStrip.HoverChanged += hovered => ExpansionHoverChanged?.Invoke(this, hovered);

        _ = loadCoverAsync();
    }

    private FillFlowContainer createStatsRow()
    {
        var row = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(10, 0),
            Margin = new MarginPadding { Top = 3 },
            // Shown on hover only, like lazer's statisticsContainer.
            Alpha = 0,
        };

        row.Add(stat(FontAwesome.Solid.Play, Set.PlayCount.ToString("N0")));
        row.Add(stat(FontAwesome.Solid.Heart, Set.FavouriteCount.ToString("N0")));

        if (Set.RankedDate != null)
            row.Add(stat(FontAwesome.Solid.CheckCircle, Set.RankedDate.Value.ToString("dd MMM yyyy")));

        return row;
    }

    private static Drawable stat(IconUsage icon, string text) => new FillFlowContainer
    {
        AutoSizeAxes = Axes.Both,
        Direction = FillDirection.Horizontal,
        Spacing = new Vector2(4, 0),
        Children = new Drawable[]
        {
            new SpriteIcon
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Icon = icon,
                Size = new Vector2(10),
                Colour = Theme.TextSecondary,
            },
            new SpriteText
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Text = text,
                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                Colour = Theme.TextSecondary,
            },
        },
    };

    /// <summary>The lazer-style bottom strip: status pill, one icon per ruleset present in the
    /// set, then small vertical bars (not dots) coloured by star rating, sorted ascending.</summary>
    private Drawable[] createStatusLine()
    {
        var children = new System.Collections.Generic.List<Drawable>
        {
            createPill(Set.Status.Length > 0 ? Set.Status : "unknown", Theme.StatusColour(Set.Status)),
        };

        foreach (string mode in Set.Beatmaps.Select(b => b.Mode).Distinct())
        {
            children.Add(new SpriteIcon
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Icon = DifficultyRow.ModeIcon(mode),
                Size = new Vector2(14),
                Colour = Theme.TextPrimary,
            });
        }

        var ratings = Set.Beatmaps.Select(b => b.DifficultyRating).OrderBy(r => r).ToList();

        if (ratings.Count > 0)
        {
            var bars = new FillFlowContainer
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(3, 0),
            };

            foreach (double rating in ratings.Take(max_bars))
            {
                bars.Add(new Container
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Size = new Vector2(5, 14),
                    Masking = true,
                    CornerRadius = 2.5f,
                    Child = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Theme.DifficultyColour(rating),
                    },
                });
            }

            if (ratings.Count > max_bars)
            {
                bars.Add(new SpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Text = $"+{ratings.Count - max_bars}",
                    Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                    Colour = Theme.TextSecondary,
                });
            }

            children.Add(bars);
        }

        return children.ToArray();
    }

    private static Drawable createPill(string text, Color4 colour) => new CircularContainer
    {
        Anchor = Anchor.CentreLeft,
        Origin = Anchor.CentreLeft,
        AutoSizeAxes = Axes.Both,
        Masking = true,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = colour },
            new SpriteText
            {
                Text = text,
                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                Colour = Color4.Black.Opacity(0.85f),
                Margin = new MarginPadding { Horizontal = 7, Vertical = 2 },
            },
        },
    };

    /// <summary>One difficulty in the hover expansion: mode icon + star pill + version name.
    /// Constructed by the owning overlay's floating expansion (see the class summary).</summary>
    internal partial class DifficultyRow : FillFlowContainer
    {
        public BeatmapInfo Beatmap { get; }

        public DifficultyRow(BeatmapInfo beatmap)
        {
            Beatmap = beatmap;

            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Horizontal;
            Spacing = new Vector2(6, 0);

            Children = new Drawable[]
            {
                new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Icon = ModeIcon(beatmap.Mode),
                    Size = new Vector2(12),
                    Colour = Theme.TextSecondary,
                },
                new CircularContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.Both,
                    Masking = true,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = Theme.DifficultyColour(beatmap.DifficultyRating) },
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(2, 0),
                            Margin = new MarginPadding { Horizontal = 7, Vertical = 1 },
                            Children = new Drawable[]
                            {
                                new SpriteIcon
                                {
                                    Anchor = Anchor.CentreLeft,
                                    Origin = Anchor.CentreLeft,
                                    Icon = FontAwesome.Solid.Star,
                                    Size = new Vector2(9),
                                    Colour = Color4.Black.Opacity(0.85f),
                                },
                                new SpriteText
                                {
                                    Anchor = Anchor.CentreLeft,
                                    Origin = Anchor.CentreLeft,
                                    Text = beatmap.DifficultyRating.ToString("0.00"),
                                    Font = FontUsage.Default.With(weight: "Bold", size: Theme.CaptionTextSize),
                                    Colour = Color4.Black.Opacity(0.85f),
                                },
                            },
                        },
                    },
                },
                new SpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Text = beatmap.Version,
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextSecondary,
                    Truncate = true,
                },
            };
        }

        /// <summary>Rough FontAwesome stand-ins for the four ruleset icons (the real ruleset icon
        /// font isn't bundled) — "mode icon-ish".</summary>
        internal static IconUsage ModeIcon(string mode) => mode switch
        {
            "taiko" => FontAwesome.Solid.Drum,
            "fruits" or "catch" => FontAwesome.Solid.AppleAlt,
            "mania" => FontAwesome.Solid.Stream,
            _ => FontAwesome.Regular.DotCircle, // "osu" and anything unknown
        };
    }

    // No scale, no size change, no widened hit area on hover — anything that grows this card's
    // flow footprint (even a 1% scale) can push a grid row over the wrap threshold and reflow the
    // whole grid mid-hover (the exact bug the floating-expansion design replaced). Card hover
    // only reveals in-card detail (the stats row and the icon rail sliding in, lazer's
    // showDetails behaviour at lazer's exact timing); the difficulty EXPANSION is the dots
    // strip's concern (see DotsStrip / ExpansionHoverChanged).
    protected override bool OnHover(HoverEvent e)
    {
        if (Enabled.Value)
            updateHoverDetails(true);

        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        updateHoverDetails(false);
        base.OnHoverLost(e);
    }

    private void updateHoverDetails(bool shown)
    {
        statsRow.FadeTo(shown ? 1 : 0, TRANSITION_DURATION, Easing.OutQuint);
        iconRail.FadeTo(shown ? 1 : 0, TRANSITION_DURATION, Easing.OutQuint);
        iconRail.MoveToX(shown ? 0 : 10, TRANSITION_DURATION, Easing.OutQuint);
    }

    private void updateBorder() => content.BorderThickness = Selected.Value || Expanded.Value ? 3 : 0;

    /// <summary>The bottom difficulty strip — a dedicated hover-reporting container because THIS
    /// (not the whole card) is the expansion trigger, matching lazer.</summary>
    internal partial class DotsStrip : Container
    {
        public event Action<bool>? HoverChanged;

        protected override bool OnHover(HoverEvent e)
        {
            HoverChanged?.Invoke(true);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            HoverChanged?.Invoke(false);
            base.OnHoverLost(e);
        }
    }

    /// <summary>
    /// One icon in the hover rail, replicating lazer's <c>BeatmapCardIconButton</c> look and
    /// timing: a bare 14px icon (no background) that brightens and slightly grows on hover with
    /// an additive glow, idle/hover colours from the shared purple palette when present.
    /// </summary>
    internal partial class RailButton : ClickableContainer
    {
        private readonly SpriteIcon icon;
        private readonly Box glow;

        [Resolved(canBeNull: true)]
        private osu.Game.Overlays.OverlayColourProvider? colourProvider { get; set; }

        private bool ready;

        public RailButton(IconUsage iconUsage)
        {
            Size = new Vector2(24);

            Children = new Drawable[]
            {
                new CircularContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    Child = glow = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Color4.White.Opacity(0.1f),
                        Blending = BlendingParameters.Additive,
                        Alpha = 0,
                    },
                },
                icon = new SpriteIcon
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Icon = iconUsage,
                    Size = new Vector2(14),
                    Scale = new Vector2(0.8f),
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            icon.Colour = idleColour;
            ready = true;
        }

        private Color4 idleColour => colourProvider?.Light1 ?? Theme.TextSecondary;
        private Color4 hoverColour => colourProvider?.Content1 ?? Theme.TextPrimary;

        protected override bool OnHover(HoverEvent e)
        {
            if (ready)
            {
                glow.FadeIn(500, Easing.OutQuint);
                icon.ScaleTo(0.9f, 500, Easing.OutQuint);
                icon.FadeColour(hoverColour, TRANSITION_DURATION, Easing.OutQuint);
            }

            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            if (ready)
            {
                glow.FadeOut(500, Easing.OutQuint);
                icon.ScaleTo(0.8f, 500, Easing.OutQuint);
                icon.FadeColour(idleColour, TRANSITION_DURATION, Easing.OutQuint);
            }

            base.OnHoverLost(e);
        }
    }

    private async Task loadCoverAsync()
    {
        if (thumbnailStore == null)
            return;

        Texture? texture;

        try
        {
            texture = await thumbnailStore.GetAsync($"https://assets.ppy.sh/beatmaps/{Set.Id}/covers/card.jpg").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Missing/unreachable cover — not fatal, the placeholder stays up.
            Logger.Error(ex, $"Failed to load cover for set {Set.Id}");
            return;
        }

        if (texture == null)
            return;

        Schedule(() =>
        {
            coverContainer.Add(new Sprite
            {
                RelativeSizeAxes = Axes.Both,
                FillMode = FillMode.Fill,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Texture = texture,
            });
        });
    }
}
