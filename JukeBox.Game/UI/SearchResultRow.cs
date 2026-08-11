#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Bindables;
using osu.Framework.Logging;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// One row in <see cref="SearchOverlay"/>'s results list. Shows a cover thumbnail (fetched
/// async; a placeholder box remains underneath until it loads or if it never does), title/artist,
/// "mapped by" creator, status and [SB]/[VID] markers; highlights while <see cref="Selected"/>,
/// and is dimmed with no click action when the set can't be downloaded.
/// </summary>
public partial class SearchResultRow : ClickableContainer
{
    private const float cover_size = 36;

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

    private Box highlight = null!;

    public SearchResultRow(BeatmapSetInfo set)
    {
        Set = set;
        RelativeSizeAxes = Axes.X;
        Height = 44;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        bool disabled = Set.DownloadDisabled;

        string subLine = $"mapped by {Set.Creator} · {Set.Status}";
        if (Set.Storyboard) subLine += " [SB]";
        if (Set.Video) subLine += " [VID]";

        InternalChildren = new Drawable[]
        {
            highlight = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Color4.White,
                Alpha = 0,
            },
            new Box // cover thumbnail placeholder; stays visible underneath until/unless the real one loads.
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Position = new Vector2(8, 0),
                Size = new Vector2(cover_size, cover_size),
                Colour = Color4.DarkSlateGray,
            },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Padding = new MarginPadding { Left = 8 + cover_size + 8, Right = 8, Vertical = 4 },
                Children = new Drawable[]
                {
                    new SpriteText
                    {
                        Text = $"{Set.DisplayTitle} — {Set.DisplayArtist}",
                        Font = FontUsage.Default.With(size: 18),
                    },
                    new SpriteText
                    {
                        Text = subLine,
                        Font = FontUsage.Default.With(size: 13),
                        Colour = Color4.Gray,
                    },
                }
            }
        };

        if (disabled)
            Alpha = 0.4f;

        Selected.BindValueChanged(e => highlight.Alpha = e.NewValue ? 0.25f : 0, true);

        _ = loadThumbnailAsync();
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
            AddInternal(new Sprite
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Position = new Vector2(8, 0),
                Size = new Vector2(cover_size, cover_size),
                FillMode = FillMode.Fill,
                Texture = texture,
            });
        });
    }
}
