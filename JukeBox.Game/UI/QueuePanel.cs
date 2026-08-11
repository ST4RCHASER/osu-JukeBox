#nullable enable

using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;

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

    // canBeNull: not every host of this panel caches a BeatmapCache (e.g. some test scenes don't
    // need real caching wired up at all) — rows just hide their status text when this is null.
    [Resolved(canBeNull: true)]
    private BeatmapCache? cache { get; set; }

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
    /// Test-only: the row at <paramref name="index"/>'s current status text ("ready",
    /// "downloading…", "waiting", or empty when no <see cref="BeatmapCache"/> is resolved).
    /// </summary>
    internal string StatusTextAt(int index) => ((QueueRow)rowsFlow.Children[index]).StatusText;

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

        Masking = true;
        CornerRadius = Theme.CornerRadius;
        EdgeEffect = Theme.PanelShadow;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.PanelSurface,
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(Theme.PanelPadding),
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, Theme.RowSpacing),
                    Children = new Drawable[]
                    {
                        headerText = new SpriteText
                        {
                            Font = FontUsage.Default.With(size: Theme.HeaderTextSize),
                            Colour = Theme.TextPrimary,
                        },
                        rowsFlow = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, Theme.RowSpacing),
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
            rowsFlow.Add(new QueueRow(set, cache, () => queue.Items.Remove(set)));
    }

    private partial class QueueRow : CompositeDrawable
    {
        // Polling a dict lookup + a directory scan every frame per row is cheap at queue scale,
        // but there's no need to do it 60 times a second either — throttled to roughly twice a
        // second, which is plenty responsive for a status label a human is watching.
        private const int poll_interval_frames = 30;

        private readonly BeatmapSetInfo set;
        private readonly BeatmapCache? cache;
        private readonly IconButton removeButton;
        private readonly SpriteText statusText;
        private readonly Box surface;

        private int framesSincePoll;
        private bool ready;

        public QueueRow(BeatmapSetInfo set, BeatmapCache? cache, System.Action onRemove)
        {
            this.set = set;
            this.cache = cache;

            RelativeSizeAxes = Axes.X;
            Height = 32;

            Masking = true;
            CornerRadius = Theme.CornerRadius;

            InternalChildren = new Drawable[]
            {
                surface = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Theme.PanelSurface,
                },
                new SpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Position = new Vector2(8, 0),
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextPrimary,
                    Text = $"{set.DisplayTitle} — {set.DisplayArtist}",
                },
                statusText = new SpriteText
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new Vector2(-32, 0),
                    Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                    Colour = Theme.TextTertiary,
                },
                removeButton = new IconButton
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new Vector2(-4, 0),
                    Size = new Vector2(24, 24),
                    Icon = FontAwesome.Solid.Times,
                    Action = onRemove,
                    Alpha = 0,
                }
            };

            updateStatus();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            ready = true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            surface.FadeColour(Theme.ElevatedSurface, Theme.HoverFadeDuration);
            fadeRemoveButton(1);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            surface.FadeColour(Theme.PanelSurface, Theme.HoverFadeDuration);
            fadeRemoveButton(0);
            base.OnHoverLost(e);
        }

        private void fadeRemoveButton(float alpha)
        {
            if (ready)
                removeButton.FadeTo(alpha, Theme.HoverFadeDuration);
            else
                removeButton.Alpha = alpha;
        }

        protected override void Update()
        {
            base.Update();

            if (cache == null)
                return;

            if (framesSincePoll++ < poll_interval_frames)
                return;

            framesSincePoll = 0;
            updateStatus();
        }

        private void updateStatus()
        {
            bool isCached = cache != null && cache.IsCached(set.Id);
            bool downloading = cache != null && cache.IsDownloading(set.Id);

            statusText.Text = cache == null
                ? string.Empty
                : isCached
                    ? "ready"
                    : downloading
                        ? "downloading…"
                        : "waiting";

            statusText.Colour = isCached ? Theme.Accent : Theme.TextTertiary;
        }

        public void TriggerRemove() => removeButton.TriggerClick();

        internal string StatusText => statusText.Text.ToString();
    }
}
