#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// One row in <see cref="SearchOverlay"/>'s results list. Shows a rounded 60×45 cover thumbnail
/// (fetched async; a placeholder box remains underneath until it loads or if it never does),
/// title/artist, "mapped by" creator, status and accent-dim [SB]/[VID] badges; highlights while
/// <see cref="Selected"/> with an accent left border strip + elevated surface, lightens on hover,
/// and is dimmed to 40% alpha with no click action when the set can't be downloaded.
/// </summary>
public partial class SearchResultRow : ClickableContainer
{
    private const float cover_width = 60;
    private const float cover_height = 45;
    private const float selection_strip_width = 4;

    /// <summary>
    /// Driven by <see cref="SearchOverlay"/>'s keyboard navigation to control the highlight.
    /// </summary>
    public readonly BindableBool Selected = new();

    public BeatmapSetInfo Set { get; }

    // [Resolved(canBeNull: true)] rather than a hard [Resolved]: only JukeBoxGame's own
    // [BackgroundDependencyLoader] (not JukeBoxGameBase's, shared with every test scene) caches
    // this — see the field comment on JukeBoxGameBase.dependencies — so every existing test scene
    // must keep constructing/resolving this row fine with no store present at all.
    [Resolved(canBeNull: true)]
    private OnlineThumbnailStore? thumbnailStore { get; set; }

    private Box surface = null!;
    private Box selectionStrip = null!;
    private Container coverContainer = null!;

    // Transforms (FadeColour) must only run after LoadComplete — see IconButton's `ready` field
    // for the same guard and reasoning.
    private bool ready;

    public SearchResultRow(BeatmapSetInfo set)
    {
        Set = set;
        RelativeSizeAxes = Axes.X;
        Height = 64;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        bool disabled = Set.DownloadDisabled;

        Masking = true;
        CornerRadius = Theme.CornerRadius;

        InternalChildren = new Drawable[]
        {
            surface = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.PanelSurface,
            },
            selectionStrip = new Box
            {
                RelativeSizeAxes = Axes.Y,
                Width = selection_strip_width,
                Colour = Theme.Accent,
                Alpha = 0,
            },
            coverContainer = new Container
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Position = new Vector2(10, 0),
                Size = new Vector2(cover_width, cover_height),
                Masking = true,
                CornerRadius = Theme.CornerRadius,
                Child = new Box // placeholder; stays visible underneath until/unless the real one loads.
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Theme.ElevatedSurface,
                },
            },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Padding = new MarginPadding { Left = 10 + cover_width + 10, Right = 8, Vertical = 6 },
                Spacing = new Vector2(0, 2),
                Children = new Drawable[]
                {
                    new SpriteText
                    {
                        Text = $"{Set.DisplayTitle} — {Set.DisplayArtist}",
                        Font = FontUsage.Default.With(size: Theme.RowTitleTextSize),
                        Colour = Theme.TextPrimary,
                    },
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new Vector2(6, 0),
                        Children = createSubLine(),
                    },
                }
            }
        };

        if (disabled)
            Alpha = 0.4f;

        Selected.BindValueChanged(e => updateSelected(e.NewValue), true);

        _ = loadThumbnailAsync();
    }

    private Drawable[] createSubLine()
    {
        var children = new System.Collections.Generic.List<Drawable>
        {
            new SpriteText
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Text = $"mapped by {Set.Creator} · {Set.Status}",
                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                Colour = Theme.TextSecondary,
            }
        };

        if (Set.Storyboard)
            children.Add(createBadge("SB"));

        if (Set.Video)
            children.Add(createBadge("VID"));

        return children.ToArray();
    }

    private static Drawable createBadge(string text) => new Container
    {
        Anchor = Anchor.CentreLeft,
        Origin = Anchor.CentreLeft,
        AutoSizeAxes = Axes.Both,
        Masking = true,
        CornerRadius = 4,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = Theme.AccentDim },
            new SpriteText
            {
                Text = text,
                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                Colour = Theme.TextPrimary,
                Margin = new MarginPadding { Horizontal = 5, Vertical = 1 },
            }
        }
    };

    private void updateSelected(bool selected)
    {
        selectionStrip.Alpha = selected ? 1 : 0;
        var targetColour = selected ? Theme.ElevatedSurface : Theme.PanelSurface;

        if (ready)
            surface.FadeColour(targetColour, Theme.HoverFadeDuration);
        else
            surface.Colour = targetColour;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        ready = true;
    }

    protected override bool OnHover(HoverEvent e)
    {
        if (!Selected.Value)
            surface.FadeColour(Theme.ElevatedSurface, Theme.HoverFadeDuration);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        if (!Selected.Value)
            surface.FadeColour(Theme.PanelSurface, Theme.HoverFadeDuration);
        base.OnHoverLost(e);
    }

    private async Task loadThumbnailAsync()
    {
        if (thumbnailStore == null)
            return;

        Texture? texture;

        try
        {
            texture = await thumbnailStore.Store.GetAsync($"https://b.ppy.sh/thumb/{Set.Id}l.jpg", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Missing/unreachable thumbnail — not fatal, the placeholder box stays up.
            Logger.Error(ex, $"Failed to load cover thumbnail for set {Set.Id}");
            return;
        }

        if (texture == null)
            return;

        Schedule(() =>
        {
            // Drawn on top of (added after) the placeholder box above, so it simply covers it.
            coverContainer.Add(new Sprite
            {
                RelativeSizeAxes = Axes.Both,
                FillMode = FillMode.Fill,
                Texture = texture,
            });
        });
    }
}
