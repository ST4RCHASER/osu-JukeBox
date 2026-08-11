#nullable enable

using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// Right-anchored drawer listing <see cref="MusicQueue.Items"/>: a "Queue (N)" header and one
/// removable row per queued set, rebuilt whenever the list changes. Starts off-screen (past its
/// own right edge) and slides in/out on <see cref="ToggleVisibility"/>.
/// </summary>
public partial class QueuePanel : CompositeDrawable
{
    private const float panel_width = 320;
    private const float slide_duration = 200;

    [Resolved]
    private MusicQueue queue { get; set; } = null!;

    private SpriteText headerText = null!;
    private FillFlowContainer rowsFlow = null!;

    private bool shown;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to how many rows are
    /// currently rendered, and to the header text, without depending on <see cref="QueueRow"/>
    /// (a private nested type not reachable from outside this class even with InternalsVisibleTo).
    /// </summary>
    internal int RowCount => rowsFlow.Count;

    internal string HeaderText => headerText.Text.ToString();

    /// <summary>
    /// Test-only: clicks the ✕ button on the row at <paramref name="index"/>, exercising the same
    /// removal path a real click would.
    /// </summary>
    internal void TriggerRemoveAt(int index) => ((QueueRow)rowsFlow.Children[index]).TriggerRemove();

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Y;
        Width = panel_width;
        Anchor = Anchor.TopRight;
        Origin = Anchor.TopRight;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = new Color4(28, 28, 28, 255),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(8),
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 4),
                    Children = new Drawable[]
                    {
                        headerText = new SpriteText { Font = FontUsage.Default.With(size: 18) },
                        rowsFlow = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 4),
                        }
                    }
                }
            }
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Off-screen to the right until first toggled — done here rather than in load() because
        // MoveToX needs this Drawable's own Width, which auto-size/relative sizing only settles
        // by LoadComplete.
        X = panel_width;

        queue.Items.BindCollectionChanged((_, _) => rebuildRows(), true);
    }

    /// <summary>
    /// Slides the panel fully into view if hidden, or fully out (past its own right edge) if shown.
    /// </summary>
    public void ToggleVisibility()
    {
        shown = !shown;
        this.MoveToX(shown ? 0 : panel_width, slide_duration, Easing.OutQuint);
    }

    /// <summary>
    /// Forces the panel to a specific shown/hidden state, e.g. so a layout switch can dock it
    /// permanently open without depending on <see cref="ToggleVisibility"/>'s toggle parity.
    /// </summary>
    public void SetShown(bool value)
    {
        if (shown == value)
            return;

        ToggleVisibility();
    }

    private void rebuildRows()
    {
        headerText.Text = $"Queue ({queue.Items.Count})";

        rowsFlow.Clear();

        foreach (var set in queue.Items)
            rowsFlow.Add(new QueueRow(set, () => queue.Items.Remove(set)));
    }

    private partial class QueueRow : CompositeDrawable
    {
        private readonly BeatmapSetInfo set;
        private readonly IconButton removeButton;

        public QueueRow(BeatmapSetInfo set, System.Action onRemove)
        {
            this.set = set;

            RelativeSizeAxes = Axes.X;
            Height = 28;

            InternalChildren = new Drawable[]
            {
                new SpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Font = FontUsage.Default.With(size: 14),
                    Text = $"{set.DisplayTitle} — {set.DisplayArtist}",
                },
                removeButton = new IconButton
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Size = new Vector2(24, 24),
                    Icon = FontAwesome.Solid.Times,
                    Action = onRemove,
                }
            };
        }

        public void TriggerRemove() => removeButton.TriggerClick();
    }
}
