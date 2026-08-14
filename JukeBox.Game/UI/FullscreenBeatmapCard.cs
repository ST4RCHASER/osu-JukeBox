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
/// dots, a ▶ preview button over the left thumb area, a ♥+count (display only — no account, so
/// non-interactive) and a ⬇ queue button. On hover it EXPANDS (accent border + a panel unfolding
/// below, drawn over the following grid rows — see <see cref="ExpandedPanel"/> and the owning
/// overlay's depth-raising) listing every difficulty as its own row: mode icon, a star pill
/// coloured by <see cref="Theme.DifficultyColour"/> (value to 2dp) and the version name, sorted
/// ascending by stars.
///
/// The expanded panel deliberately does NOT change this card's flow size (the grid would reflow) —
/// it overhangs the card's bounds, so <see cref="ReceivePositionalInputAt"/> is widened to keep
/// hover alive while the cursor is over the panel.
/// </summary>
public partial class FullscreenBeatmapCard : ClickableContainer
{
    public const float HEIGHT = 116;

    private const float thumb_width = 96;
    private const float gutter = 5;
    private const int max_dots = 10;

    public BeatmapSetInfo Set { get; }

    /// <summary>Layout position in the owning grid — the grid's flow order follows this (not
    /// Depth), so the owning overlay can raise a hovered card's Depth to draw its expanded panel
    /// over the neighbouring cards without reflowing the grid.</summary>
    public int FlowIndex { get; }

    /// <summary>Driven by the owning overlay's keyboard navigation (accent border highlight).</summary>
    public readonly BindableBool Selected = new();

    /// <summary>The hovered state changed — the owning overlay raises/restores this card's depth
    /// in the grid so the expanded panel overlays its neighbours.</summary>
    public event Action<FullscreenBeatmapCard, bool>? HoverChanged;

    /// <summary>▶ preview button clicked — the owning overlay routes this to its
    /// <see cref="Playback.PreviewPlayer"/> (toggling if this set is already previewing).</summary>
    public event Action<FullscreenBeatmapCard>? PreviewRequested;

    /// <summary>Bound (by the owning overlay) to <see cref="Playback.PreviewPlayer.PlayingSetId"/> so
    /// the preview button flips to a stop icon while this set's preview is audible.</summary>
    public readonly Bindable<int?> PreviewingSetId = new Bindable<int?>();

    [Resolved(canBeNull: true)]
    private OnlineThumbnailStore? thumbnailStore { get; set; }

    private Container content = null!;
    private Container expandedPanel = null!;
    private Container coverContainer = null!;
    private IconButton previewButton = null!;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the hover
    /// expansion panel, to assert its visibility and difficulty rows.</summary>
    internal Container ExpandedPanel => expandedPanel;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the ▶ preview
    /// button, to click it without depending on internal layout.</summary>
    internal IconButton PreviewButton => previewButton;

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
                            createStatsRow(),
                        },
                    },
                    new FillFlowContainer // status pill + difficulty dots
                    {
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Padding = new MarginPadding { Left = thumb_width + 10, Bottom = 8 },
                        Spacing = new Vector2(6, 0),
                        Children = createStatusLine(),
                    },
                    // ♥ + favourite count: display only — there's no signed-in account to
                    // favourite with, so this is deliberately not a button.
                    new FillFlowContainer
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new Vector2(4, 0),
                        Padding = new MarginPadding { Top = 10, Right = 10 },
                        Children = new Drawable[]
                        {
                            new SpriteIcon
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Icon = FontAwesome.Regular.Heart,
                                Size = new Vector2(12),
                                Colour = Theme.Accent,
                            },
                            new SpriteText
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Text = Set.FavouriteCount.ToString("N0"),
                                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                Colour = Theme.TextSecondary,
                            },
                        },
                    },
                    new IconButton // ⬇ queue (the existing enqueue path, same as clicking the card)
                    {
                        Anchor = Anchor.BottomRight,
                        Origin = Anchor.BottomRight,
                        Margin = new MarginPadding(8),
                        Size = new Vector2(28),
                        Icon = FontAwesome.Solid.Download,
                        Action = () => TriggerClick(),
                    },
                },
            },
            expandedPanel = createExpandedPanel(),
        };

        if (Set.DownloadDisabled)
            Alpha = 0.4f;

        Selected.BindValueChanged(_ => updateBorder(), true);
        PreviewingSetId.BindValueChanged(e => previewButton.Icon = e.NewValue == Set.Id ? FontAwesome.Solid.Stop : FontAwesome.Solid.Play, true);

        _ = loadCoverAsync();
    }

    private Drawable createStatsRow()
    {
        var row = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(10, 0),
            Margin = new MarginPadding { Top = 3 },
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

    private Drawable[] createStatusLine()
    {
        var children = new System.Collections.Generic.List<Drawable>
        {
            createPill(Set.Status.Length > 0 ? Set.Status : "unknown", Theme.StatusColour(Set.Status)),
        };

        var ratings = Set.Beatmaps.Select(b => b.DifficultyRating).OrderBy(r => r).ToList();

        if (ratings.Count > 0)
        {
            var dots = new FillFlowContainer
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(3, 0),
            };

            foreach (double rating in ratings.Take(max_dots))
            {
                dots.Add(new Circle
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Size = new Vector2(7),
                    Colour = Theme.DifficultyColour(rating),
                });
            }

            if (ratings.Count > max_dots)
            {
                dots.Add(new SpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Text = $"+{ratings.Count - max_dots}",
                    Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                    Colour = Theme.TextSecondary,
                });
            }

            children.Add(dots);
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

    /// <summary>
    /// The per-difficulty list unfolding below the card on hover. Positioned just past the card's
    /// own (fixed) height, so it overhangs the grid row below — the owning overlay raises this
    /// card's depth while hovered so the panel draws (and hit-tests) above the neighbours.
    /// </summary>
    private Container createExpandedPanel()
    {
        var rows = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Padding = new MarginPadding(8),
            Spacing = new Vector2(0, 4),
        };

        foreach (var beatmap in Set.Beatmaps.OrderBy(b => b.DifficultyRating))
            rows.Add(new DifficultyRow(beatmap));

        return new Container
        {
            Anchor = Anchor.BottomLeft,
            Origin = Anchor.TopLeft,
            // Tucks slightly under the card so the pair reads as one grown card, not two stacked.
            Y = -Theme.CornerRadius,
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Masking = true,
            CornerRadius = Theme.CornerRadius,
            BorderColour = Theme.Accent,
            Alpha = 0,
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Theme.PanelSurface,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Top = Theme.CornerRadius },
                    Child = rows,
                },
            },
        };
    }

    /// <summary>One difficulty in the hover expansion: mode icon + star pill + version name.</summary>
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

    /// <summary>Widened so hover survives while the cursor is over the (overhanging) expanded
    /// panel — see the class summary.</summary>
    public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
        => base.ReceivePositionalInputAt(screenSpacePos)
           || (expandedPanel.Alpha > 0 && expandedPanel.ScreenSpaceDrawQuad.Contains(screenSpacePos));

    protected override bool OnHover(HoverEvent e)
    {
        if (Enabled.Value)
        {
            expandedPanel.FadeIn(Theme.DurationFast, Theme.EaseEnter);
            this.ScaleTo(1.01f, Theme.DurationFast, Theme.EaseEnter);
            updateBorder();
            HoverChanged?.Invoke(this, true);
        }

        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        expandedPanel.FadeOut(Theme.DurationFast, Theme.EaseExit);
        this.ScaleTo(1f, Theme.DurationFast, Theme.EaseExit);
        updateBorder();
        HoverChanged?.Invoke(this, false);
        base.OnHoverLost(e);
    }

    private void updateBorder()
    {
        bool highlighted = Selected.Value || (IsHovered && Enabled.Value);
        content.BorderThickness = highlighted ? 3 : 0;
        expandedPanel.BorderThickness = IsHovered && Enabled.Value ? 3 : 0;
    }

    private async Task loadCoverAsync()
    {
        if (thumbnailStore == null)
            return;

        Texture? texture;

        try
        {
            texture = await thumbnailStore.Store.GetAsync($"https://assets.ppy.sh/beatmaps/{Set.Id}/covers/card.jpg", CancellationToken.None).ConfigureAwait(false);
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
