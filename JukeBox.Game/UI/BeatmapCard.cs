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
/// One osu!-web-style card in <see cref="BeatmapListingOverlay"/>'s two-column result grid. Sized
/// at half its parent's width (the 4px internal padding forms the grid gutter, so a plain
/// <see cref="FillFlowContainer"/> wraps exactly two per row with no spacing arithmetic). Shows the
/// set's cover (osu! CDN <c>covers/card.jpg</c>, fetched async through the shared
/// <see cref="OnlineThumbnailStore"/>; an elevated-surface placeholder stays underneath) behind a
/// dark gradient for text legibility, title (bold) / "by artist" / "mapped by creator", a
/// status-coloured pill, accent-dim [SB]/[VID] badges and up to <see cref="max_dots"/> per-difficulty
/// dots coloured by star rating (a "+N" caption absorbs the overflow). Highlights with an accent
/// border while <see cref="Selected"/>, and is dimmed to 40% alpha with no click action when the
/// set can't be downloaded (the owning overlay leaves <see cref="ClickableContainer.Action"/> null).
///
/// The <see cref="Compact"/> variant swaps the cover-backed card for a dense half-height row —
/// small square thumbnail, tight text, mini difficulty dots — used by the compact search style.
/// </summary>
public partial class BeatmapCard : ClickableContainer
{
    public const float HEIGHT = 112;

    /// <summary>Row height of the dense variant (see <see cref="Compact"/>).</summary>
    public const float COMPACT_HEIGHT = 56;

    private const int max_dots = 10;
    private const float gutter = 4;

    /// <summary>Thumbnail edge length in the compact row layout.</summary>
    private const float compact_thumb_size = 48;

    /// <summary>
    /// Driven by <see cref="BeatmapListingOverlay"/>'s keyboard navigation to control the accent
    /// border highlight.
    /// </summary>
    public readonly BindableBool Selected = new();

    public BeatmapSetInfo Set { get; }

    /// <summary>
    /// The dense row layout used by the compact search style
    /// (<see cref="Configuration.SearchStyle.Compact"/>): a small square thumbnail on the left
    /// (<see cref="compact_thumb_size"/>) with tightly-spaced title/artist text and mini
    /// difficulty dots beside it, at half the regular card's height — instead of the full
    /// cover-backed card. Fixed at construction; the owning listing rebuilds its cards when the
    /// style setting changes.
    /// </summary>
    public bool Compact { get; }

    // [Resolved(canBeNull: true)] rather than a hard [Resolved]: only JukeBoxGame's own
    // [BackgroundDependencyLoader] (not JukeBoxGameBase's, shared with every test scene) caches
    // this — see the field comment on JukeBoxGameBase.dependencies — so every test scene must keep
    // constructing this card fine with no store present at all.
    [Resolved(canBeNull: true)]
    private OnlineThumbnailStore? thumbnailStore { get; set; }

    private Container content = null!;
    private Container coverContainer = null!;
    private Box hoverOverlay = null!;

    // Width is absolute (not RelativeSizeAxes.X): the owning grid is a FillDirection.Full flow,
    // which forbids relative sizing along its flow axes — BeatmapListingOverlay keeps every
    // card's width synced to half the grid's width instead. The default only matters for
    // standalone/test construction.
    public BeatmapCard(BeatmapSetInfo set, bool compact = false)
    {
        Set = set;
        Compact = compact;
        Width = 400;
        Height = compact ? COMPACT_HEIGHT : HEIGHT;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Padding = new MarginPadding(Compact ? 2 : gutter);

        // The listing's entrance animation (BeatmapListingOverlay.rebuildCards) fades a newly
        // added card in from Alpha 0 rather than adding it fully visible. Without AlwaysPresent,
        // osu!framework throttles Update()/transform ticking for a not-present (Alpha 0) drawable,
        // which stalls that very FadeIn indefinitely instead of letting it progress every frame —
        // and separately, a not-present card is also un-hittable (IsPresent requires Alpha > 0)
        // for real mouse-driven clicks (as opposed to TriggerClick()) going through actual
        // hit-testing.
        AlwaysPresent = true;

        Child = content = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Masking = true,
            CornerRadius = Compact ? 6 : Theme.CornerRadius,
            BorderColour = Theme.Accent,
            Children = Compact ? createCompactLayout() : createRegularLayout(),
        };

        if (Set.DownloadDisabled)
            Alpha = 0.4f;

        Selected.BindValueChanged(e => content.BorderThickness = e.NewValue ? 3 : 0, true);

        _ = loadCoverAsync();
    }

    private Drawable[] createRegularLayout() => new Drawable[]
    {
        new Box // placeholder; stays visible underneath until/unless the cover loads.
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Theme.ElevatedSurface,
        },
        coverContainer = new Container { RelativeSizeAxes = Axes.Both },
        new Box // dark gradient so the text stays legible over any cover art.
        {
            RelativeSizeAxes = Axes.Both,
            Colour = ColourInfo.GradientVertical(Color4.Black.Opacity(0.45f), Color4.Black.Opacity(0.85f)),
        },
        new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Padding = new MarginPadding { Horizontal = 10, Top = 8 },
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
            },
        },
        new FillFlowContainer
        {
            Anchor = Anchor.BottomLeft,
            Origin = Anchor.BottomLeft,
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Padding = new MarginPadding { Horizontal = 10, Bottom = 8 },
            Spacing = new Vector2(6, 0),
            Children = createBottomLine(dotSize: 7),
        },
        hoverOverlay = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Color4.White.Opacity(0.08f),
            Alpha = 0,
        },
    };

    /// <summary>
    /// The dense row (see <see cref="Compact"/>): elevated-surface row, 48px thumbnail (the same
    /// async cover fetch, just masked small), two tight text lines and a mini status/dots line —
    /// no full-card cover or gradient.
    /// </summary>
    private Drawable[] createCompactLayout() => new Drawable[]
    {
        new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Theme.ElevatedSurface,
        },
        new Container // thumbnail frame
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Size = new Vector2(compact_thumb_size),
            Margin = new MarginPadding { Left = 3 },
            Masking = true,
            CornerRadius = 5,
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Theme.PanelSurface,
                },
                coverContainer = new Container { RelativeSizeAxes = Axes.Both },
            },
        },
        new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Direction = FillDirection.Vertical,
            Padding = new MarginPadding { Left = compact_thumb_size + 10, Right = 6 },
            Spacing = new Vector2(0, 1),
            Children = new Drawable[]
            {
                new SpriteText
                {
                    Text = Set.DisplayTitle,
                    Font = FontUsage.Default.With(family: "Roboto", weight: "Bold", size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextPrimary,
                    Truncate = true,
                    RelativeSizeAxes = Axes.X,
                },
                new SpriteText
                {
                    Text = $"{Set.DisplayArtist} · {Set.Creator}",
                    Font = FontUsage.Default.With(size: Theme.CaptionTextSize - 1),
                    Colour = Theme.TextTertiary,
                    Truncate = true,
                    RelativeSizeAxes = Axes.X,
                },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(4, 0),
                    Margin = new MarginPadding { Top = 1 },
                    Children = createBottomLine(dotSize: 5),
                },
            },
        },
        hoverOverlay = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Color4.White.Opacity(0.08f),
            Alpha = 0,
        },
    };

    private Drawable[] createBottomLine(float dotSize)
    {
        var children = new System.Collections.Generic.List<Drawable>
        {
            createPill(Set.Status.Length > 0 ? Set.Status : "unknown", Theme.StatusColour(Set.Status)),
        };

        if (Set.Storyboard)
            children.Add(createPill("SB", Theme.AccentDim));

        if (Set.Video)
            children.Add(createPill("VID", Theme.AccentDim));

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
                    Size = new Vector2(dotSize),
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

    private Drawable createPill(string text, Color4 colour) => new CircularContainer
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
                Font = FontUsage.Default.With(size: Compact ? Theme.CaptionTextSize - 2 : Theme.CaptionTextSize),
                Colour = Color4.Black.Opacity(0.85f),
                Margin = Compact
                    ? new MarginPadding { Horizontal = 5, Vertical = 1 }
                    : new MarginPadding { Horizontal = 7, Vertical = 2 },
            },
        },
    };

    protected override bool OnHover(HoverEvent e)
    {
        if (Enabled.Value)
            hoverOverlay.FadeIn(Theme.HoverFadeDuration);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        hoverOverlay.FadeOut(Theme.HoverFadeDuration);
        base.OnHoverLost(e);
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
            // Missing/unreachable cover — not fatal, the placeholder box stays up.
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
