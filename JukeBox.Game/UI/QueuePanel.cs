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
using osu.Game.Graphics.UserInterface;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// Queue list: a "Queue (N)" header and one removable row per <see cref="MusicQueue.Items"/> entry
/// (see <see cref="QueueRow"/> for the row's own design), rebuilt whenever the list changes. Two
/// presentations, chosen by the constructor:
///
/// <list type="bullet">
/// <item>Floating (default, <c>docked: false</c>) — a right-anchored drawer, off-screen (past its
/// own right edge) until <see cref="ToggleVisibility"/>/<see cref="SetShown"/> slides it in.</item>
/// <item>Docked (<c>docked: true</c>) — the right column's "Playback" tab embeds this as its Queue
/// section (see <see cref="PlaybackPanel"/>): fully-relative geometry filling whatever parent it's
/// given (chrome-less — the surrounding tab already owns the card), parked at X = 0
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

    /// <summary>The floating drawer pads its own card; a docked instance is already inside one.</summary>
    private float contentPadding => docked ? 0 : Theme.PanelPadding;

    [Resolved]
    private MusicQueue queue { get; set; } = null!;

    // canBeNull: not every host of this panel caches a BeatmapCache (e.g. some test scenes don't
    // need real caching wired up at all) — rows then simply never show download progress.
    [Resolved(canBeNull: true)]
    private BeatmapCache? cache { get; set; }

    private SpriteText headerText = null!;
    private FillFlowContainer contentFlow = null!;
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
    /// The height this panel's content (header + however many rows are currently listed) actually
    /// occupies, including its own padding — the panel itself always fills whatever slot it's given,
    /// so a host that has to SIZE that slot (see <see cref="PlaybackPanel"/>, which stacks the queue
    /// under the playback controls in one scrolling column) needs this to know how tall the slot has
    /// to be for nothing to get clipped.
    /// </summary>
    internal float ContentHeight => contentFlow.DrawHeight + contentPadding * 2;

    /// <summary>
    /// Test-only: the row at <paramref name="index"/>'s download percentage ("42%"), or empty when
    /// that row isn't downloading or its download has no known total (see
    /// <see cref="Beatmaps.DownloadProgress.Indeterminate"/>).
    /// </summary>
    internal string ProgressTextAt(int index) => ((QueueRow)rowsFlow.Children[index]).ProgressText;

    /// <summary>Test-only: the 0..1 fill of the row at <paramref name="index"/>'s progress bar.</summary>
    internal float ProgressFillAt(int index) => ((QueueRow)rowsFlow.Children[index]).ProgressFill;

    /// <summary>Test-only: whether the row at <paramref name="index"/> is showing the indeterminate
    /// <see cref="LoadingSpinner"/> rather than a percentage.</summary>
    internal bool SpinnerShownAt(int index) => ((QueueRow)rowsFlow.Children[index]).SpinnerShown;

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

        // The floating drawer is a card in its own right — surface, shadow, inner padding. A docked
        // instance is a section INSIDE an already-padded panel card (see PlaybackPanel), so it draws
        // none of that: a second PanelSurface over the identical column surface only adds a stray
        // shadow halo, and a second ring of padding would push its header out of line with the
        // content stacked above it.
        if (!docked)
        {
            EdgeEffect = Theme.PanelShadow;

            AddInternal(new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.PanelSurface,
            });
        }

        AddInternal(
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(contentPadding),
                Child = contentFlow = new FillFlowContainer
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
        );
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

    /// <summary>
    /// One queued set, drawn in the same language as <see cref="BeatmapCard"/>'s compact variant —
    /// cover thumb on the left, then title / artist / "mapped by X" — minus that card's status pill
    /// and difficulty dots, which say nothing about a set you have already decided to play. The
    /// right edge carries this row's download feedback (see <see cref="updateProgress"/>) and the ✕
    /// that drops the set from the queue.
    /// </summary>
    private partial class QueueRow : CompositeDrawable
    {
        private const float slide_offset = 16;
        private const float row_height = 56;
        private const float thumb_size = 44;
        private const float thumb_margin = 6;

        /// <summary>Right inset of the text block with only the ✕ to clear.</summary>
        private const float text_inset_idle = 34;

        /// <summary>Right inset of the text block while a percentage/spinner also sits there.</summary>
        private const float text_inset_downloading = 82;

        private const float progress_bar_height = 3;

        private readonly BeatmapSetInfo set;
        private readonly BeatmapCache? cache;
        private readonly IconButton removeButton;
        private readonly Box surface;
        private readonly Container textBlock;
        private readonly SpriteText percentText;
        private readonly LoadingSpinner spinner;
        private readonly Container progressBar;
        private readonly Box progressFill;

        private bool ready;

        /// <summary>Last state pushed into the drawables below, so <see cref="updateProgress"/> —
        /// which runs every frame — only touches them when something actually changed (a
        /// <see cref="SpriteText"/> re-lays out its glyphs on every write to <c>Text</c>).</summary>
        private DownloadProgress? lastProgress;

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
                new CoverThumbnail(set.Id, cornerRadius: 5)
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Size = new Vector2(thumb_size),
                    Margin = new MarginPadding { Left = thumb_margin },
                },
                // Padding (rather than a positioned child) reserves the thumbnail's column and the
                // right-hand controls' room: the text inside is relatively sized and truncating, so
                // it needs a parent whose width is already the space actually left for it.
                textBlock = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding
                    {
                        Left = thumb_margin + thumb_size + 10,
                        Right = text_inset_idle,
                    },
                    Child = new FillFlowContainer
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, 1),
                        Children = new Drawable[]
                        {
                            new SpriteText
                            {
                                RelativeSizeAxes = Axes.X,
                                Truncate = true,
                                Font = FontUsage.Default.With(family: "Roboto", weight: "Bold", size: Theme.RowSecondaryTextSize),
                                Colour = Theme.TextPrimary,
                                Text = set.DisplayTitle,
                            },
                            new SpriteText
                            {
                                RelativeSizeAxes = Axes.X,
                                Truncate = true,
                                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                Colour = Theme.TextSecondary,
                                Text = set.DisplayArtist,
                            },
                            // A dropped replay REPLACES the mapper credit on this line rather
                            // than adding a fourth: the row is a fixed 56px holding three tight
                            // lines already, and for a set you queued by dropping someone's
                            // replay, who played it is the more useful of the two.
                            // NowPlayingPanel, which has the room, shows both.
                            new SpriteText
                            {
                                RelativeSizeAxes = Axes.X,
                                Truncate = true,
                                Font = FontUsage.Default.With(size: Theme.CaptionTextSize - 1),
                                Colour = set.Replay != null ? Theme.Accent : Theme.TextTertiary,
                                Text = set.Replay != null
                                    ? replayCredit(set.Replay)
                                    : $"mapped by {set.Creator}",
                            },
                        },
                    },
                },
                percentText = new SpriteText
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new Vector2(-text_inset_idle, 0),
                    Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                    Colour = Theme.Accent,
                    Alpha = 0,
                },
                spinner = new LoadingSpinner
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new Vector2(-text_inset_idle - 4, 0),
                    Size = new Vector2(18),
                },
                progressBar = new Container
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    Height = progress_bar_height,
                    Alpha = 0,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Theme.ElevatedSurface,
                        },
                        progressFill = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Width = 0,
                            Colour = Theme.Accent,
                        },
                    },
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
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            ready = true;
            updateProgress();
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

        // Polled rather than pushed: BeatmapCache reports progress from whichever thread is
        // draining the mirror response, so a push would have to marshal onto the update thread per
        // chunk (thousands of scheduled callbacks per download). Reading the snapshot here instead
        // costs one lock-free dictionary lookup per row per frame — and unlike the status text this
        // replaced, there's no directory scan behind it, so it needs no frame throttle either.
        protected override void Update()
        {
            base.Update();

            if (cache != null)
                updateProgress();
        }

        /// <summary>
        /// Shows this row's download state: a bottom-edge bar filled to the fraction downloaded
        /// plus a percentage when the mirror advertised a total, or a <see cref="LoadingSpinner"/>
        /// when it didn't (see <see cref="DownloadProgress.Indeterminate"/>). Nothing at all when
        /// the set isn't downloading — a queued set that is merely waiting its turn, or one already
        /// cached, needs no ornament.
        /// </summary>
        private void updateProgress()
        {
            DownloadProgress? current = cache != null && cache.TryGetDownloadProgress(set.Id, out var progress)
                ? progress
                : null;

            if (current == lastProgress)
                return;

            lastProgress = current;

            bool determinate = current is { Indeterminate: false };

            textBlock.Padding = textBlock.Padding with
            {
                Right = current == null ? text_inset_idle : text_inset_downloading,
            };

            progressBar.Alpha = determinate ? 1 : 0;
            progressFill.Width = determinate ? (float)current!.Value.Value : 0;

            percentText.Alpha = determinate ? 1 : 0;

            if (determinate)
                percentText.Text = $"{current!.Value.Value * 100:0}%";

            // LoadingSpinner is a VisibilityContainer — it spins/fades itself in and out through
            // Show()/Hide() rather than a raw Alpha write.
            if (current is { Indeterminate: true })
                spinner.Show();
            else
                spinner.Hide();
        }

        /// <summary>
        /// "Played by Cookiezi · HD HR DT" — the mods share this one line rather than getting their
        /// own, since the row is a fixed three-line card. The text truncates like every other line
        /// here, so a long mod list on a narrow column simply clips after the player's name, which
        /// is the part that identifies the entry.
        /// </summary>
        private static string replayCredit(Replays.ReplayAttachment replay)
            => replay.ModAcronyms.Count > 0
                ? $"Played by {replay.PlayerName} · {string.Join(" ", replay.ModAcronyms)}"
                : $"Played by {replay.PlayerName}";

        public void TriggerRemove() => removeButton.TriggerClick();

        internal string ProgressText => percentText.Alpha > 0 ? percentText.Text.ToString() : string.Empty;

        internal float ProgressFill => progressBar.Alpha > 0 ? progressFill.Width : 0;

        internal bool SpinnerShown => spinner.State.Value == Visibility.Visible;
    }
}
