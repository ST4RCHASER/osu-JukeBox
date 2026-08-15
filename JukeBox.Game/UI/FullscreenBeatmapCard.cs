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
/// One card in <see cref="FullscreenListingOverlay"/>'s grid, replicating lazer's in-game
/// <c>BeatmapCardNormal</c>. Collapsed it shows the cover, title / "by artist" / "mapped by
/// creator", the status pill + per-ruleset icons + star-coloured difficulty bars, and a ▶ preview
/// button over the left thumb area. Card hover reveals lazer's detail state at lazer's timing:
/// the statistics row fades in and the right-side icon rail slides in (plus = add to queue via
/// the existing enqueue path, browser = open the set's osu.ppy.sh page).
///
/// Expansion follows lazer's REAL structure (<c>BeatmapCardContent</c>): this drawable's own
/// size — its grid footprint — is FIXED, while the inner <see cref="CardSurface"/> is an
/// auto-sized, masked, bordered container holding the card body on top and the per-difficulty
/// dropdown below. Expanding simply makes the dropdown present, so the ONE surface grows
/// downward past this drawable's bounds (drawn over the neighbouring rows, never reflowing the
/// grid) with a single continuous border and background — not a separate stacked panel. The
/// trigger is the DIFFICULTY STRIP's hover (via <see cref="ExpansionHoverChanged"/>, debounced by
/// the owning overlay, which also enforces exactly one expanded card and raises its depth so the
/// grown surface draws above the neighbours).
/// </summary>
public partial class FullscreenBeatmapCard : ClickableContainer
{
    /// <summary>The card's fixed grid footprint height (scaled-down density — see the overlay's
    /// class summary).</summary>
    public const float HEIGHT = 92;

    /// <summary>Lazer's <c>BeatmapCard.TRANSITION_DURATION</c> — every hover/expand transition on
    /// the card runs at this rate with OutQuint, matching the in-game cards exactly.</summary>
    public const float TRANSITION_DURATION = 360;

    private const float thumb_width = 76;
    private const float gutter = 4;
    private const float body_height = HEIGHT - 2 * gutter;
    private const int max_bars = 10;

    public BeatmapSetInfo Set { get; }

    /// <summary>Layout position in the owning grid — the grid's flow order follows this (not
    /// Depth), so the owning overlay can raise an expanded card's Depth to draw its grown
    /// surface over the neighbouring cards without reflowing the grid.</summary>
    public int FlowIndex { get; }

    /// <summary>Driven by the owning overlay's keyboard navigation (accent border highlight).</summary>
    public readonly BindableBool Selected = new();

    /// <summary>Set by the owning overlay while this card is the (single) expanded one — drives
    /// the dropdown's visibility, the accent border and keeping the detail state shown.</summary>
    public readonly BindableBool Expanded = new();

    /// <summary>The DIFFICULTY STRIP's (or the open dropdown's) hovered state changed — the
    /// owning overlay debounces these into expand/collapse decisions.</summary>
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

    // lazer's palette, for the star-rating spectrum the difficulty bars are coloured by — see
    // StarRatingPill for why the app samples lazer's own gradient rather than banding it.
    [Resolved]
    private osu.Game.Graphics.OsuColour colours { get; set; } = null!;

    private Container content = null!;
    private Container coverContainer = null!;
    private IconButton previewButton = null!;
    private FillFlowContainer statsRow = null!;
    private Container iconRail = null!;
    private HoverReporter dotsStrip = null!;
    private HoverReporter dropdown = null!;
    private RailButton plusButton = null!;
    private RailButton browseButton = null!;

    // Transforms must only run after LoadComplete — see IconButton's `ready` field.
    private bool ready;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the ▶ preview
    /// button, to click it without depending on internal layout.</summary>
    internal IconButton PreviewButton => previewButton;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the difficulty
    /// strip — the hover target that triggers the expansion.</summary>
    internal Container DifficultyStrip => dotsStrip;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the ONE growing
    /// card surface (masked + bordered container holding body and dropdown together).</summary>
    internal Container CardSurface => content;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the in-surface
    /// per-difficulty dropdown.</summary>
    internal Container ExpansionDropdown => dropdown;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the hover rail's
    /// add-to-queue button.</summary>
    internal ClickableContainer PlusButton => plusButton;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the hover rail's
    /// open-in-browser button.</summary>
    internal ClickableContainer BrowseButton => browseButton;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the hover rail
    /// itself, to assert its slide-in visibility.</summary>
    internal Container IconRail => iconRail;

    public FullscreenBeatmapCard(BeatmapSetInfo set, int flowIndex)
    {
        Set = set;
        FlowIndex = flowIndex;
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

        // The ONE growing surface (lazer's BeatmapCardContent shape): auto-sized so making the
        // dropdown present grows it past this drawable's fixed bounds; masked+rounded+bordered as
        // a single unit, so card + difficulty list read as one continuous card with one border.
        InternalChild = content = new Container
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Masking = true,
            CornerRadius = Theme.CornerRadius,
            BorderColour = Theme.Accent,
            Children = new Drawable[]
            {
                new Container // card body (fixed height — the collapsed card)
                {
                    RelativeSizeAxes = Axes.X,
                    Height = body_height,
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
                            Size = new Vector2(34),
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
                            Padding = new MarginPadding { Left = thumb_width + 8, Right = 30, Top = 6 },
                            Children = new Drawable[]
                            {
                                new SpriteText
                                {
                                    Text = Set.DisplayTitle,
                                    Font = FontUsage.Default.With(family: "Roboto", weight: "Bold", size: 14),
                                    Colour = Theme.TextPrimary,
                                    Truncate = true,
                                    RelativeSizeAxes = Axes.X,
                                },
                                new SpriteText
                                {
                                    Text = $"by {Set.DisplayArtist}",
                                    Font = FontUsage.Default.With(size: 12),
                                    Colour = Theme.TextSecondary,
                                    Truncate = true,
                                    RelativeSizeAxes = Axes.X,
                                },
                                new SpriteText
                                {
                                    Text = $"mapped by {Set.Creator}",
                                    Font = FontUsage.Default.With(size: 11),
                                    Colour = Theme.TextTertiary,
                                    Truncate = true,
                                    RelativeSizeAxes = Axes.X,
                                },
                                // Lazer shows the statistics row only while the card is hovered
                                // (statisticsContainer fade in BeatmapCardNormal.UpdateState).
                                statsRow = createStatsRow(),
                            },
                        },
                        // The difficulty strip — its OWN hover-reporting container, because in
                        // lazer's card THIS strip (not the whole card) triggers the expansion.
                        dotsStrip = new HoverReporter(consume: false)
                        {
                            Anchor = Anchor.BottomLeft,
                            Origin = Anchor.BottomLeft,
                            AutoSizeAxes = Axes.Both,
                            Child = new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Horizontal,
                                Padding = new MarginPadding { Left = thumb_width + 8, Bottom = 6, Top = 3, Right = 6 },
                                Spacing = new Vector2(5, 0),
                                Children = createStatusLine(),
                            },
                        },
                        // The hover icon rail (lazer's card buttons, our actions): plus = add to
                        // queue (existing enqueue path, listing stays open), browser = open the
                        // set's osu.ppy.sh page externally.
                        iconRail = new Container
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            RelativeSizeAxes = Axes.Y,
                            Width = 26,
                            Alpha = 0,
                            X = 8,
                            Child = new FillFlowContainer
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 10),
                                Children = new Drawable[]
                                {
                                    plusButton = new RailButton(FontAwesome.Solid.Plus)
                                    {
                                        // Same enqueue path as clicking the card (no-op while
                                        // the set is download-disabled — Action stays null).
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
                // The per-difficulty dropdown INSIDE the same surface, directly below the body
                // (lazer's dropdownContent): while collapsed it's Alpha 0 / not present, so the
                // auto-sized surface excludes it; expanding makes it present and the ONE surface
                // grows downward around it. Consumes hover — that keeps the expansion alive while
                // the cursor is over it AND stops hover leaking to the card underneath.
                dropdown = new HoverReporter(consume: true)
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Y = body_height,
                    Alpha = 0,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            // Fully opaque — the grown surface draws over neighbouring cards,
                            // which must not show through.
                            Colour = new Color4(0x21, 0x21, 0x2B, 0xFF),
                        },
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Padding = new MarginPadding { Horizontal = 8, Top = 6, Bottom = 7 },
                            Spacing = new Vector2(0, 3),
                            ChildrenEnumerable = Set.Beatmaps.OrderBy(b => b.DifficultyRating).Select(b => new DifficultyRow(b)),
                        },
                    },
                },
            },
        };

        if (Set.DownloadDisabled)
            Alpha = 0.4f;

        Selected.BindValueChanged(_ => updateBorder(), true);
        Expanded.BindValueChanged(e => updateExpandedState(e.NewValue), true);
        PreviewingSetId.BindValueChanged(e => previewButton.Icon = e.NewValue == Set.Id ? FontAwesome.Solid.Stop : FontAwesome.Solid.Play, true);

        dotsStrip.HoverChanged += hovered => ExpansionHoverChanged?.Invoke(this, hovered);
        dropdown.HoverChanged += hovered => ExpansionHoverChanged?.Invoke(this, hovered);

        _ = loadCoverAsync();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        ready = true;
    }

    private FillFlowContainer createStatsRow()
    {
        var row = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(9, 0),
            Margin = new MarginPadding { Top = 2 },
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
        Spacing = new Vector2(3, 0),
        Children = new Drawable[]
        {
            new SpriteIcon
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Icon = icon,
                Size = new Vector2(9),
                Colour = Theme.TextSecondary,
            },
            new SpriteText
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Text = text,
                Font = FontUsage.Default.With(size: 11),
                Colour = Theme.TextSecondary,
            },
        },
    };

    /// <summary>The lazer-style bottom strip: status pill, one icon per ruleset present in the
    /// set, then small vertical bars coloured by star rating, sorted ascending.</summary>
    private Drawable[] createStatusLine()
    {
        var children = new System.Collections.Generic.List<Drawable>
        {
            createPill(Set.Status.Length > 0 ? Set.Status : "unknown", Theme.StatusColour(Set.Status)),
        };

        foreach (string mode in Set.Beatmaps.Select(b => b.Mode).Distinct())
        {
            children.Add(RulesetIcons.Create(mode).With(icon =>
            {
                icon.Anchor = Anchor.CentreLeft;
                icon.Origin = Anchor.CentreLeft;
                icon.Size = new Vector2(12);
                icon.Colour = Theme.TextPrimary;
            }));
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
                Spacing = new Vector2(2.5f, 0),
            };

            foreach (double rating in ratings.Take(max_bars))
            {
                bars.Add(new Container
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Size = new Vector2(4, 12),
                    Masking = true,
                    CornerRadius = 2,
                    Child = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = colours.ForStarDifficulty(rating),
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
                    Font = FontUsage.Default.With(size: 10),
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
                Font = FontUsage.Default.With(size: 11),
                Colour = Color4.Black.Opacity(0.85f),
                Margin = new MarginPadding { Horizontal = 6, Vertical = 1.5f },
            },
        },
    };

    /// <summary>One difficulty in the expansion dropdown: mode icon + star pill + version name.</summary>
    internal partial class DifficultyRow : FillFlowContainer
    {
        public BeatmapInfo Beatmap { get; }

        public DifficultyRow(BeatmapInfo beatmap)
        {
            Beatmap = beatmap;

            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Horizontal;
            Spacing = new Vector2(5, 0);

            Children = new Drawable[]
            {
                RulesetIcons.Create(beatmap.Mode).With(icon =>
                {
                    icon.Anchor = Anchor.CentreLeft;
                    icon.Origin = Anchor.CentreLeft;
                    icon.Size = new Vector2(11);
                    icon.Colour = Theme.TextSecondary;
                }),
                new StarRatingPill(beatmap.DifficultyRating),
                new SpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Text = beatmap.Version,
                    Font = FontUsage.Default.With(size: 12),
                    Colour = Theme.TextSecondary,
                    Truncate = true,
                },
            };
        }

    }

    // No scale, no size change, no widened hit area on hover — anything that grows this card's
    // flow footprint (even a 1% scale) can push a grid row over the wrap threshold and reflow the
    // whole grid mid-hover. Card hover only reveals in-card detail; the expansion is the
    // difficulty strip's concern (see ExpansionHoverChanged), and the surface growth happens
    // INSIDE the fixed-footprint drawable.
    protected override bool OnHover(HoverEvent e)
    {
        if (Enabled.Value)
            updateHoverDetails();

        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        updateHoverDetails();
        base.OnHoverLost(e);
    }

    /// <summary>Lazer's <c>showDetails</c>: shown while hovered OR expanded.</summary>
    private void updateHoverDetails()
    {
        bool shown = Enabled.Value && (IsHovered || Expanded.Value);

        statsRow.FadeTo(shown ? 1 : 0, TRANSITION_DURATION, Easing.OutQuint);
        iconRail.FadeTo(shown ? 1 : 0, TRANSITION_DURATION, Easing.OutQuint);
        iconRail.MoveToX(shown ? 0 : 8, TRANSITION_DURATION, Easing.OutQuint);
    }

    private void updateExpandedState(bool expanded)
    {
        if (ready)
        {
            // Lazer's dropdownContent fade (expand at full TRANSITION_DURATION, collapse at a
            // third) — presence flips with alpha, which is what grows/shrinks the surface.
            if (expanded)
                dropdown.FadeIn(TRANSITION_DURATION, Easing.OutQuint);
            else
                dropdown.FadeOut(TRANSITION_DURATION / 3f, Easing.OutQuint);
        }
        else
            dropdown.Alpha = expanded ? 1 : 0;

        updateBorder();
        updateHoverDetails();
    }

    private void updateBorder() => content.BorderThickness = Selected.Value || Expanded.Value ? 3 : 0;

    /// <summary>A container that reports its hover to the owner; optionally consumes the hover
    /// (the expansion dropdown does, so the expansion stays alive under the cursor AND the card
    /// underneath the grown surface never sees a hover of its own).</summary>
    internal partial class HoverReporter : Container
    {
        public event Action<bool>? HoverChanged;

        private readonly bool consume;

        public HoverReporter(bool consume)
        {
            this.consume = consume;
        }

        protected override bool OnHover(HoverEvent e)
        {
            HoverChanged?.Invoke(true);
            return consume || base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            HoverChanged?.Invoke(false);
            base.OnHoverLost(e);
        }
    }

    /// <summary>
    /// One icon in the hover rail, replicating lazer's <c>BeatmapCardIconButton</c> look and
    /// timing: a bare icon (no background) that brightens and slightly grows on hover with an
    /// additive glow, idle/hover colours from the shared purple palette when present.
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
            Size = new Vector2(22);

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
                    Size = new Vector2(13),
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
