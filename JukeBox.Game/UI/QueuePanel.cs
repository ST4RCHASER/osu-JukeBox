#nullable enable

using System.Collections.Generic;
using System.Linq;
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
/// Queue list: a "Queue (N)" header and one removable row per <see cref="MusicQueue.Items"/> entry,
/// rebuilt whenever the list changes. Two presentations, chosen by the constructor:
///
/// <list type="bullet">
/// <item>Floating (default, <c>docked: false</c>) — a right-anchored drawer, off-screen (past its
/// own right edge) until <see cref="ToggleVisibility"/>/<see cref="SetShown"/> slides it in.</item>
/// <item>Docked (<c>docked: true</c>) — the three-column layout's right panel embeds this as its
/// "Queue" tab body: fully-relative geometry filling whatever parent it's given, parked at X = 0
/// from the start (no slide-in — the owning tab strip toggles Alpha instead). Configured entirely
/// here rather than by an external post-construction override, since the parent composite's own
/// child-loading order isn't guaranteed relative to a caller's LoadComplete (e.g. inside a
/// GridContainer cell, which loads its content lazily) — self-configuring sidesteps that race
/// entirely.</item>
/// </list>
/// </summary>
public partial class QueuePanel : CompositeDrawable
{
    private const float panel_width = 320;
    private const float slide_duration = 200;

    /// <summary>See the class summary.</summary>
    private readonly bool docked;

    [Resolved]
    private MusicQueue queue { get; set; } = null!;

    // canBeNull: not every host of this panel caches a BeatmapCache (e.g. some test scenes don't
    // need real caching wired up at all) — rows just hide their status text when this is null.
    [Resolved(canBeNull: true)]
    private BeatmapCache? cache { get; set; }

    private SpriteText headerText = null!;
    private FillFlowContainer rowsFlow = null!;

    private bool shown;

    /// <summary>Live rows keyed by their set (reference identity — matches how removal itself is
    /// keyed via <c>queue.Items.Remove(set)</c>), so <see cref="rebuildRows"/> can diff against
    /// <see cref="MusicQueue.Items"/> instead of tearing down and recreating every row on every
    /// mutation — needed so only the row that actually changed animates.</summary>
    private readonly Dictionary<BeatmapSetInfo, QueueRow> rows = new();

    public QueuePanel(bool docked = false)
    {
        this.docked = docked;
    }

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
        if (docked)
        {
            Anchor = Anchor.TopLeft;
            Origin = Anchor.TopLeft;
            RelativeSizeAxes = Axes.Both;
            Width = 1f;
            Height = 1f;
        }
        else
        {
            RelativeSizeAxes = Axes.Y;
            Width = panel_width;
            Anchor = Anchor.TopRight;
            Origin = Anchor.TopRight;
        }

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
                            // Animates the reflow as rows are added/removed around each other —
                            // this is what actually makes a removed row's departure read as a
                            // "collapse" rather than the remaining rows instantly snapping up.
                            LayoutDuration = (float)Theme.DurationNormal,
                            LayoutEasing = Theme.EaseEnter,
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
        // by LoadComplete. Docked instances never slide — they're parked at X = 0 from the start
        // (see the class summary).
        if (!docked)
            X = panel_width;

        // Scheduled rather than rebuilding inline: CollectionChanged fires synchronously on
        // whatever thread mutated queue.Items, and rebuildRows() mutates rowsFlow's
        // InternalChildren, which the framework only allows on the update thread. Every current
        // mutator of Items is expected to already be on the update thread (see Jukebox's
        // onUpdateThread), but Schedule is safe to call from any thread and a no-op-cost
        // same-thread defer when we're already on it — this is defense-in-depth so a future or
        // overlooked off-thread mutation degrades to a deferred rebuild instead of crashing.
        queue.Items.BindCollectionChanged((_, _) => Schedule(rebuildRows), true);
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

    /// <summary>
    /// Diffs against <see cref="MusicQueue.Items"/> instead of clearing and rebuilding wholesale —
    /// a set that's still queued keeps its existing <see cref="QueueRow"/> untouched (so it never
    /// re-flickers on an unrelated mutation elsewhere in the queue); a set that dropped out
    /// animates its row out instead of vanishing instantly; a newly-queued set gets a fresh row
    /// that animates in. New rows are always appended (queueing only ever adds at the end — see
    /// <see cref="MusicQueue.Enqueue"/>), so flow order stays correct without needing to
    /// re-sequence existing rows.
    /// </summary>
    private void rebuildRows()
    {
        headerText.Text = $"Queue ({queue.Items.Count})";

        var current = new HashSet<BeatmapSetInfo>(queue.Items);

        foreach (var (set, row) in rows.ToList())
        {
            if (current.Contains(set))
                continue;

            rows.Remove(set);

            // Scheduled rather than removing inline: onComplete fires from inside the row's own
            // transform-completion processing (itself inside that row's Update() call), so
            // disposing it synchronously right there corrupts the in-progress update walk
            // (ObjectDisposedException on its children later in that same frame). Scheduling
            // defers the removal to a fresh frame, outside that call stack.
            row.AnimateOut(() => Schedule(() => rowsFlow.Remove(row, true)));
        }

        foreach (var set in queue.Items)
        {
            if (rows.ContainsKey(set))
                continue;

            var row = new QueueRow(set, cache, () => queue.Items.Remove(set));
            rows[set] = row;
            rowsFlow.Add(row);
            row.AnimateIn();
        }
    }

    private partial class QueueRow : CompositeDrawable
    {
        // Polling a dict lookup + a directory scan every frame per row is cheap at queue scale,
        // but there's no need to do it 60 times a second either — throttled to roughly twice a
        // second, which is plenty responsive for a status label a human is watching.
        private const int poll_interval_frames = 30;

        private const float slide_offset = 16;
        private const float row_height = 32;

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
            Height = row_height;

            // AnimateIn starts this row at Alpha 0 and AnimateOut fades it back down before
            // removal — without AlwaysPresent, osu!framework throttles Update()/Scheduler
            // ticking for a not-IsPresent (Alpha 0) drawable, which stalls its own FadeIn/FadeOut
            // transforms indefinitely instead of letting them progress every frame as intended.
            AlwaysPresent = true;

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

        /// <summary>Fades + slides the row in from the side — called once, right after this row
        /// is first added to <c>rowsFlow</c> by <see cref="QueuePanel.rebuildRows"/>.</summary>
        public void AnimateIn()
        {
            Alpha = 0;
            X = slide_offset;
            this.FadeIn(Theme.DurationNormal, Theme.EaseEnter);
            this.MoveToX(0, Theme.DurationNormal, Theme.EaseEnter);
        }

        /// <summary>Fades out and collapses this row's own height (letting <c>rowsFlow</c>'s
        /// <c>LayoutDuration</c> animate the remaining rows sliding up to fill the gap), then
        /// invokes <paramref name="onComplete"/> — which the caller uses to actually remove this
        /// row from its parent — once that animation finishes. Called once the owning set has
        /// left the queue.</summary>
        public void AnimateOut(System.Action onComplete)
        {
            this.FadeOut(Theme.DurationFast, Theme.EaseExit);
            this.ResizeHeightTo(0, Theme.DurationNormal, Theme.EaseExit).OnComplete(_ => onComplete());
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
