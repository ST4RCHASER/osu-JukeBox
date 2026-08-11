#nullable enable

using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Bindables;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// One row in <see cref="SearchOverlay"/>'s results list. Shows title/artist, "mapped by"
/// creator, status and [SB]/[VID] markers; highlights while <see cref="Selected"/>, and is
/// dimmed with no click action when the set can't be downloaded.
/// </summary>
public partial class SearchResultRow : ClickableContainer
{
    /// <summary>
    /// Driven by <see cref="SearchOverlay"/>'s keyboard navigation to control the highlight.
    /// </summary>
    public readonly BindableBool Selected = new();

    public BeatmapSetInfo Set { get; }

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
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Padding = new MarginPadding { Left = 8, Right = 8, Vertical = 4 },
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
    }
}
