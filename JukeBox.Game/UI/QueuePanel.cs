#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
using osu.Game.Graphics.Containers;
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

    // canBeNull for the same reason as `cache`: a scene that only exercises the list's contents
    // needs no jukebox at all. Without one the play-now button simply isn't built (see QueueRow) —
    // there is nothing it could do.
    [Resolved(canBeNull: true)]
    private Jukebox? jukebox { get; set; }

    private SpriteText headerText = null!;
    private FillFlowContainer contentFlow = null!;
    private QueueList rowsList = null!;

    private bool shown;

    /// <summary>
    /// Guards <see cref="syncListFromQueue"/>'s own writes to the list from being read back as user
    /// drags — see <see cref="LoadComplete"/>, where the two directions are wired.
    /// </summary>
    private bool syncingFromQueue;

    public QueuePanel(bool docked = false)
    {
        this.docked = docked;
    }

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to how many rows are
    /// currently rendered, and to the header text, without depending on <see cref="QueueRow"/>
    /// (a private nested type not reachable from outside this class even with InternalsVisibleTo).
    /// </summary>
    /// <summary>Rows that are actually built and laid out — NOT the item count, which the list
    /// takes on the instant a set is queued, a frame or more before the row drawable exists.</summary>
    internal int RowCount => rowsList.LoadedRowCount;

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
    internal string ProgressTextAt(int index) => rowAt(index).ProgressText;

    /// <summary>Test-only: the 0..1 fill of the row at <paramref name="index"/>'s progress bar.</summary>
    internal float ProgressFillAt(int index) => rowAt(index).ProgressFill;

    /// <summary>Test-only: whether the row at <paramref name="index"/> is showing the indeterminate
    /// <see cref="LoadingSpinner"/> rather than a percentage.</summary>
    internal bool SpinnerShownAt(int index) => rowAt(index).SpinnerShown;

    /// <summary>
    /// Test-only: clicks the ✕ button on the row at <paramref name="index"/>, exercising the same
    /// removal path a real click would.
    /// </summary>
    internal void TriggerRemoveAt(int index) => rowAt(index).TriggerRemove();

    /// <summary>Test-only: clicks the ▶ button on the row at <paramref name="index"/>.</summary>
    internal void TriggerPlayNowAt(int index) => rowAt(index).TriggerPlayNow();

    /// <summary>Test-only: whether every listed row actually offers a ▶.</summary>
    internal bool EveryRowHasPlayNow => rowsList.Items.All(set => rowsList.RowFor(set).HasPlayNowButton);

    /// <summary>Test-only: the queue order as the LIST currently draws it, which is the thing a
    /// drag actually rearranges — asserting on it (rather than on the queue) is what catches the
    /// view and the queue disagreeing.</summary>
    internal IReadOnlyList<BeatmapSetInfo> ListedOrder => rowsList.Items.ToList();

    /// <summary>Test-only: moves the row at <paramref name="from"/> to <paramref name="to"/> the
    /// same way a completed drag does — the container rearranges its own Items and we mirror that
    /// onto the queue, so this exercises the real reorder path without synthesising mouse motion.
    /// </summary>
    internal void TriggerReorder(int from, int to) => rowsList.Items.Move(from, to);

    /// <summary>Rows in the order the list draws them — the container keeps its flow children in
    /// arbitrary order and sorts by depth, so this maps back through Items rather than reading
    /// child order.</summary>
    private QueueRow rowAt(int index) => rowsList.RowFor(rowsList.Items[index]);

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
                        rowsList = new QueueList(set => new QueueRow(set, cache, () => queue.Items.Remove(set), playNow(set)))
                        {
                            RelativeSizeAxes = Axes.X,
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

        // Scheduled rather than reconciling inline: CollectionChanged fires synchronously on
        // whatever thread mutated queue.Items, and the reconcile writes to the list container,
        // which mutates drawables — update thread only, and its item map throws
        // KeyNotFoundException when a change arrives for an item it never saw. Every current
        // mutator of Items is expected to already be on the update thread (see Jukebox's
        // onUpdateThread), but Schedule is safe to call from any thread and a no-op-cost
        // same-thread defer when we're already on it — this is defense-in-depth so a future or
        // overlooked off-thread mutation degrades to a deferred sync instead of crashing.
        //
        // Deliberately NOT rowsList.Items.BindTo(queue.Items), which would be shorter but would
        // deliver those changes straight onto the mutating thread and lose exactly that guard.
        queue.Items.BindCollectionChanged((_, _) => Schedule(syncListFromQueue), true);

        // The other direction. A drag is the ONLY thing that rearranges the list on its own (the
        // container calls Items.Move as the dragged row passes its neighbours), so a Move that
        // isn't ours is a user reorder, and the queue has to learn about it or the next song would
        // come off a stale order. Input runs on the update thread, so this needs no marshalling.
        rowsList.Items.BindCollectionChanged((_, e) =>
        {
            if (syncingFromQueue || e.Action != NotifyCollectionChangedAction.Move)
                return;

            queue.Items.Move(e.OldStartingIndex, e.NewStartingIndex);
        });
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
    /// What the ▶ button on <paramref name="set"/>'s row does, or null when there is no jukebox to
    /// ask — the row then draws no button at all rather than a dead one.
    /// </summary>
    private System.Action? playNow(BeatmapSetInfo set) => jukebox == null ? null : () => jukebox.PlayNow(set);

    /// <summary>
    /// Brings the list in line with <see cref="MusicQueue.Items"/> by the smallest set of edits that
    /// gets there — remove what left, insert what arrived, move what changed places — rather than
    /// clearing and refilling. The container rebuilds a row's whole drawable for every item it is
    /// handed, so a wholesale refill would restart every row's entrance animation and throw away
    /// in-flight download progress on rows that never actually changed.
    /// </summary>
    private void syncListFromQueue()
    {
        headerText.Text = $"Queue ({queue.Items.Count})";

        if (rowsList.Items.SequenceEqual(queue.Items))
            return;

        syncingFromQueue = true;

        try
        {
            foreach (var gone in rowsList.Items.Except(queue.Items).ToList())
                rowsList.Items.Remove(gone);

            for (int i = 0; i < queue.Items.Count; i++)
            {
                var set = queue.Items[i];
                int at = rowsList.Items.IndexOf(set);

                if (at < 0)
                    rowsList.Items.Insert(i, set);
                else if (at != i)
                    rowsList.Items.Move(at, i);
            }
        }
        finally
        {
            syncingFromQueue = false;
        }
    }

    /// <summary>
    /// Lazer's own rearrangeable list (the one its playlists use), so the drag — grab handle, live
    /// gap, drop position, auto-scroll — is the framework's rather than hand-rolled maths.
    ///
    /// <para>
    /// It sizes itself to its content and its scroll is inert, because this list is NOT the thing
    /// that scrolls: the whole Playback tab is one scroll (see <see cref="PlaybackPanel"/>), and a
    /// second one nested inside it would make the wheel's target ambiguous exactly where the pointer
    /// spends its time. The base class insists on a scroll container, so it gets one that declines
    /// the wheel.
    /// </para>
    /// </summary>
    internal partial class QueueList : OsuRearrangeableListContainer<BeatmapSetInfo>
    {
        private readonly Func<BeatmapSetInfo, QueueRow> createRow;
        private readonly Dictionary<BeatmapSetInfo, QueueRow> rows = new();

        public QueueList(Func<BeatmapSetInfo, QueueRow> createRow)
        {
            this.createRow = createRow;
        }

        /// <summary>The live row drawing <paramref name="set"/> — test-only access, and only valid
        /// once the container has built it.</summary>
        public QueueRow RowFor(BeatmapSetInfo set) => rows[set];

        /// <summary>See <see cref="QueuePanel.RowCount"/>.</summary>
        public int LoadedRowCount => ListContainer.Count(r => r.IsLoaded);

        /// <summary>
        /// Builds the row AND loads it here, on the update thread, rather than leaving its load to
        /// the <c>LoadComponentsAsync</c> the base class hands it to next.
        ///
        /// <para>
        /// Left to that path the rows never appeared at all in a populated scene, however long it
        /// was given: the framework loads components on a fixed four-thread scheduler shared across
        /// the whole process, so a queue row queues behind everything else that is loading. A row
        /// that never arrives is far worse than one that costs a frame, and a queue row is cheap and
        /// layout-only — its cover fetches itself afterwards — which is exactly the shape that
        /// belongs on the update thread. It is also what the hand-rolled flow this list replaced
        /// did.
        /// </para>
        ///
        /// <para>
        /// The base still adds the row to the hierarchy from its own scheduled callback, so a queued
        /// set becomes a visible row on the frame AFTER the queue changed — which is why callers
        /// that watch for rows poll rather than assert on the very next frame.
        /// </para>
        /// </summary>
        protected override OsuRearrangeableListItem<BeatmapSetInfo> CreateOsuDrawable(BeatmapSetInfo item)
        {
            var row = createRow(item);

            // Guarded because the base class also builds drawables for items present before this
            // container itself has loaded, when there is nothing to load them with yet — it adds
            // those to the hierarchy directly, which loads them anyway.
            if (LoadState >= LoadState.Loading)
                LoadComponent(row);

            return rows[item] = row;
        }

        protected override FillFlowContainer<RearrangeableListItem<BeatmapSetInfo>> CreateListFillFlowContainer()
            => new FillFlowContainer<RearrangeableListItem<BeatmapSetInfo>>
            {
                Spacing = new Vector2(0, Theme.RowSpacing),
                // The reflow as rows are added, removed or dragged past each other — the same pace
                // as the rest of the app's card motion, and what makes a drag read as rows making
                // room rather than snapping.
                LayoutDuration = (float)Theme.DurationNormal,
                LayoutEasing = Theme.EaseEnter,
            };

        protected override ScrollContainer<Drawable> CreateScrollContainer() => new InertScroll();

        protected override void Update()
        {
            base.Update();

            // Read a frame late, like every other measured height in this app: the list is a slot in
            // the owning tab's single scrolling column, so it has to be exactly as tall as its rows.
            Height = ListContainer.DrawHeight;
        }

        /// <summary>
        /// A scroll container that neither scrolls nor clips: the wheel belongs to the tab's own
        /// scroll (see the class summary), and the clip has to go with it.
        ///
        /// <para>
        /// Masking is what made this list deadlock at zero height. The list sizes itself to its
        /// rows, the rows live inside this container, and a masked container hides — and therefore
        /// never loads — children outside its bounds. At the first frame those bounds are empty, so
        /// the rows never loaded, never gave the list a height, and the list never grew to reveal
        /// them. With nothing to clip against (this container is always exactly as tall as its
        /// content) masking buys nothing anyway.
        /// </para>
        /// </summary>
        private partial class InertScroll : OsuScrollContainer
        {
            public InertScroll()
            {
                Masking = false;
            }

            protected override bool OnScroll(ScrollEvent e) => false;
        }
    }

    /// <summary>
    /// One queued set, drawn in the same language as <see cref="BeatmapCard"/>'s compact variant —
    /// cover thumb on the left, then title / artist / "mapped by X" — minus that card's status pill
    /// and difficulty dots, which say nothing about a set you have already decided to play. The
    /// right edge carries this row's download feedback (see <see cref="updateProgress"/>), a ▶ that
    /// jumps straight to this set, and the ✕ that drops it from the queue.
    ///
    /// <para>
    /// A rearrangeable list item rather than a plain drawable: that is what gives it lazer's drag
    /// handle (left of the cover, revealed on hover) and lets the container move it. The item is the
    /// full-width row; <see cref="CreateContent"/> supplies everything right of the handle.
    /// </para>
    /// </summary>
    internal partial class QueueRow : OsuRearrangeableListItem<BeatmapSetInfo>
    {
        private const float slide_offset = 16;
        private const float row_height = 56;
        private const float thumb_size = 44;
        private const float thumb_margin = 6;

        private const float button_size = 24;

        /// <summary>The ▶/✕ column: one button wide, since they stack rather than sit side by side.</summary>
        private const float action_column_width = button_size + 8;

        /// <summary>Width of the hover-revealed drag handle, which OVERLAYS the card's left edge
        /// rather than reserving a column of its own — see <see cref="dragHandle"/>.</summary>
        private const float drag_handle_width = 22;

        /// <summary>Right inset of the text block with only the ▶ and ✕ to clear.</summary>
        private const float text_inset_idle = action_column_width + 8;

        /// <summary>Right inset of the text block while a percentage/spinner also sits there.</summary>
        private const float text_inset_downloading = action_column_width + 56;

        private const float progress_bar_height = 3;

        private readonly BeatmapSetInfo set;
        private readonly BeatmapCache? cache;
        private readonly System.Action onRemove;

        /// <summary>Null when nothing can act on it — see <see cref="QueuePanel.playNow"/>.</summary>
        private readonly System.Action? onPlayNow;

        private IconButton removeButton = null!;
        private IconButton? playNowButton;
        private Container dragHandle = null!;
        private Container card = null!;

        /// <summary>Test hook (JukeBox.Game.Tests has InternalsVisibleTo): the card itself — the
        /// row's content, which is what must span the full width with no handle gutter beside it.</summary>
        internal Drawable Card => card;

        /// <summary>Test hook: the hover-revealed drag handle that <see cref="IsDraggableAt"/>
        /// hit-tests.</summary>
        internal Drawable DragHandle => dragHandle;

        /// <summary>Test hook: what the list itself asks before starting a drag.</summary>
        internal bool CanBeDraggedAt(Vector2 screenSpacePos) => IsDraggableAt(screenSpacePos);
        private Box surface = null!;
        private Container textBlock = null!;
        private SpriteText percentText = null!;
        private LoadingSpinner spinner = null!;
        private Container progressBar = null!;
        private Box progressFill = null!;

        private bool ready;

        /// <summary>Last state pushed into the drawables below, so <see cref="updateProgress"/> —
        /// which runs every frame — only touches them when something actually changed (a
        /// <see cref="SpriteText"/> re-lays out its glyphs on every write to <c>Text</c>).</summary>
        private DownloadProgress? lastProgress;

        public QueueRow(BeatmapSetInfo set, BeatmapCache? cache, System.Action onRemove, System.Action? onPlayNow)
            : base(set)
        {
            this.set = set;
            this.cache = cache;
            this.onRemove = onRemove;
            this.onPlayNow = onPlayNow;
        }

        /// <summary>
        /// Everything right of the drag handle. The row's own entrance starts it at Alpha 0 (see
        /// <see cref="LoadComplete"/>); without AlwaysPresent, osu!framework throttles
        /// Update()/Scheduler ticking for a not-IsPresent drawable, which stalls that fade
        /// indefinitely instead of letting it progress every frame.
        /// </summary>
        protected override Drawable CreateContent() => card = new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = row_height,
            AlwaysPresent = true,
            Masking = true,
            CornerRadius = Theme.CornerRadius,
            Children = new Drawable[]
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
                // ▶ ABOVE ✕ in a narrow column at the right edge (user request). The destructive
                // action keeps the bottom of the pair, so it is still the one furthest from where
                // the pointer arrives and "remove" stays the deliberate click of the two.
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 2),
                    Margin = new MarginPadding { Right = 4 },
                    Children = new Drawable[]
                    {
                        playNowButton = onPlayNow == null ? null : new IconButton
                        {
                            Size = new Vector2(button_size),
                            Icon = FontAwesome.Solid.Play,
                            Action = onPlayNow,
                            Alpha = 0,
                        },
                        removeButton = new IconButton
                        {
                            Size = new Vector2(button_size),
                            Icon = FontAwesome.Solid.Times,
                            Action = onRemove,
                            Alpha = 0,
                        },
                    }.Where(d => d != null).Select(d => d!).ToArray(),
                },
                // The drag handle OVERLAYS the card's left edge instead of sitting in a reserved
                // column beside it, so the card is full width at rest (user request). It carries its
                // own scrim because it lands on top of the cover thumbnail, and it is what
                // IsDraggableAt hit-tests — see that override.
                dragHandle = new Container
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.Y,
                    Width = drag_handle_width,
                    Alpha = 0,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Theme.PanelSurface,
                            Alpha = 0.85f,
                        },
                        new SpriteIcon
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Size = new Vector2(11),
                            Icon = FontAwesome.Solid.Bars,
                            Colour = Theme.TextTertiary,
                        },
                    },
                },
            }.Where(d => d != null).Select(d => d!).ToArray(),
        };

        /// <summary>
        /// Our own handle replaces the one <see cref="OsuRearrangeableListItem{T}"/> draws, which
        /// lives in an auto-sized grid column and therefore reserves its width even while hidden —
        /// that reserved gutter is what kept the card off the panel's left edge. Switching it off
        /// collapses the column; the drag itself keeps working because the list asks
        /// <see cref="IsDraggableAt"/> where a drag may begin, and that now points at our handle.
        /// </summary>
        protected override bool IsDraggableAt(Vector2 screenSpacePos) => dragHandle.ReceivePositionalInputAt(screenSpacePos);

        protected override void LoadComplete()
        {
            base.LoadComplete();
            ShowDragHandle.Value = false;
            ready = true;
            updateProgress();

            // The entrance, self-driven now that the container owns adding and removing rows. There
            // is deliberately no matching exit: a leaving row is disposed the moment its set leaves
            // the queue, because holding a departing row in the list would leave it occupying an
            // index that reordering then has to reason around. The gap still closes smoothly — the
            // flow's LayoutDuration animates the rows that remain (see CreateListFillFlowContainer).
            Alpha = 0;
            X = slide_offset;
            this.FadeIn(Theme.DurationNormal, Theme.EaseEnter);
            this.MoveToX(0, Theme.DurationNormal, Theme.EaseEnter);
        }

        protected override bool OnHover(HoverEvent e)
        {
            surface.FadeColour(Theme.ElevatedSurface, Theme.HoverFadeDuration);
            fadeButtons(1);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            surface.FadeColour(Theme.PanelSurface, Theme.HoverFadeDuration);
            fadeButtons(0);
            base.OnHoverLost(e);
        }

        private void fadeButtons(float alpha)
        {
            dragHandle.FadeTo(alpha, Theme.HoverFadeDuration);

            foreach (var button in new[] { playNowButton, removeButton })
            {
                if (button == null)
                    continue;

                if (ready)
                    button.FadeTo(alpha, Theme.HoverFadeDuration);
                else
                    button.Alpha = alpha;
            }
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

        public void TriggerPlayNow() => playNowButton?.TriggerClick();

        /// <summary>Test-only: whether this row offers a ▶ at all (it doesn't without a jukebox).</summary>
        internal bool HasPlayNowButton => playNowButton != null;

        internal string ProgressText => percentText.Alpha > 0 ? percentText.Text.ToString() : string.Empty;

        internal float ProgressFill => progressBar.Alpha > 0 ? progressFill.Width : 0;

        internal bool SpinnerShown => spinner.State.Value == Visibility.Visible;
    }
}
