#nullable enable

using System;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;

namespace JukeBox.Game.UI;

/// <summary>
/// A small rounded beatmap-set cover: an elevated-surface placeholder that stays underneath while
/// the real cover is fetched asynchronously through the shared <see cref="OnlineThumbnailStore"/>,
/// and simply remains if the fetch fails or the set has no cover.
///
/// <para>
/// Deliberately requests the same <c>covers/card.jpg</c> URL <see cref="BeatmapCard"/> does rather
/// than a smaller per-size variant: the store caches by url, so a set that has already been drawn
/// in the search results costs nothing to draw again in the queue.
/// </para>
/// </summary>
public partial class CoverThumbnail : CompositeDrawable
{
    private readonly int setId;

    // [Resolved(canBeNull: true)] rather than a hard [Resolved]: only JukeBoxGame's own
    // [BackgroundDependencyLoader] (not JukeBoxGameBase's, shared with every test scene) caches
    // this — see BeatmapCard's field of the same name — so this must keep working with no store
    // present at all, showing just the placeholder.
    [Resolved(canBeNull: true)]
    private OnlineThumbnailStore? thumbnailStore { get; set; }

    private Container coverContainer = null!;

    public CoverThumbnail(int setId, float cornerRadius)
    {
        this.setId = setId;

        Masking = true;
        CornerRadius = cornerRadius;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ElevatedSurface,
            },
            coverContainer = new Container { RelativeSizeAxes = Axes.Both },
        };

        _ = loadCoverAsync();
    }

    private async Task loadCoverAsync()
    {
        // A non-positive id is a locally-imported set (see BeatmapCache.LocalSetId) — there is no
        // osu! web page behind it, so the request could only ever 404. The placeholder stays.
        if (thumbnailStore == null || setId <= 0)
            return;

        Texture? texture;

        try
        {
            texture = await thumbnailStore.GetAsync($"https://assets.ppy.sh/beatmaps/{setId}/covers/card.jpg").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Missing/unreachable cover — not fatal, the placeholder box stays up.
            Logger.Error(ex, $"Failed to load cover for set {setId}");
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
