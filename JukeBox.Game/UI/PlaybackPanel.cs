#nullable enable

using System;
using System.Collections.Generic;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Cursor;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// The right column's "Playback" tab body — everything about what is playing and how, in one
/// scrollable column, in two <see cref="Theme"/>-headed sections:
///
/// <list type="bullet">
/// <item><b>Playback</b> — <see cref="NowPlayingPanel"/> (cover, title/artist, status, seekable
/// progress, difficulty dropdown, open-in-browser), the <see cref="TransportRow"/> transport strip,
/// the playback-speed slider and the per-beatmap audio offset row.</item>
/// <item><b>Queue</b> — the existing <see cref="QueuePanel"/> (list, per-row download status,
/// removal), filling whatever height is left below the playback section.</item>
/// </list>
///
/// <para>
/// This replaces the old bottom playback bar (which spanned the window under the columns) and the
/// old "Queue" tab: the bar's content moved here, as did Settings' former "Playback" section (speed
/// slider + transport) and its per-beatmap offset row. The GLOBAL audio offset deliberately stayed
/// in Settings → Audio: it calibrates the output path (device, drivers, headphones) alongside the
/// device picker and volumes, rather than describing the song being played.
/// </para>
///
/// <para>
/// One scroll for the whole tab (rather than a fixed playback header over an independently
/// scrolling queue): on a short window the playback controls must stay reachable, and nesting a
/// second scroll inside the first would make the wheel's target ambiguous over the queue. The queue
/// section instead sizes itself (see <see cref="QueueSection"/>) to the largest of its own content,
/// a sensible minimum, and whatever height is left in the viewport — so it fills the column when
/// there's room and pushes the page into scrolling when there isn't.
/// </para>
/// </summary>
public partial class PlaybackPanel : CompositeDrawable
{
    /// <summary>The queue never collapses below this, even with an empty queue on a short window —
    /// a queue section shorter than a row or two reads as broken rather than empty.</summary>
    private const float queue_min_height = 150;

    // The exact DI lazer's SettingsPanel provides its subtree (same scheme SettingsOverlay caches):
    // the two lazer settings sliders below resolve this for their pill/slider palette.
    [Cached]
    private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    // canBeNull: the offset store is only cached by the real game (see JukeBoxGame), not by every
    // test scene — its row is simply omitted rather than rendered dead.
    [Resolved(canBeNull: true)]
    private BeatmapOffsetStore? offsetStore { get; set; }

    private NowPlayingPanel nowPlaying = null!;
    private QueuePanel queuePanel = null!;
    private QueueSection queueSection = null!;
    private OsuScrollContainer scroll = null!;
    private SettingsSlider<double> playbackRateRow = null!;
    private SettingsSlider<double>? beatmapOffsetRow;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the tab's pieces,
    /// so tests can assert what this section contains without depending on its internal layout.</summary>
    internal NowPlayingPanel NowPlaying => nowPlaying;

    internal QueuePanel Queue => queuePanel;

    internal SettingsSlider<double> PlaybackRateSlider => playbackRateRow;

    /// <summary>Null when no <see cref="BeatmapOffsetStore"/> is cached (see the field).</summary>
    internal SettingsSlider<double>? BeatmapOffsetSlider => beatmapOffsetRow;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        playbackRateRow = new SettingsSlider<double> { LabelText = "Playback speed", KeyboardStep = 0.05f };

        var playbackRows = new List<Drawable> { playbackRateRow };

        if (offsetStore != null)
            playbackRows.Add(beatmapOffsetRow = new SettingsSlider<double> { LabelText = "Audio offset (this beatmap)", KeyboardStep = 1 });

        InternalChild = new OsuTooltipContainer(null!)
        {
            // Tooltip host for the tab: lazer's RoundedSliderBar surfaces its value through a
            // tooltip, which only renders inside a TooltipContainer ancestor (same wrapper
            // SettingsOverlay uses for its own rows).
            RelativeSizeAxes = Axes.Both,
            Child = scroll = new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                ScrollbarVisible = true,
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, Theme.SectionSpacing),
                    Children = new Drawable[]
                    {
                        sectionHeader("Playback"),
                        nowPlaying = new NowPlayingPanel(),
                        new TransportRow(playback, jukebox),
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            // Lazer's settings rows inset their own content by CONTENT_MARGINS on
                            // both sides (they're built for a panel that pads its header the same
                            // way). Pulling that back out here is what keeps their labels/sliders
                            // flush with the Theme-styled content stacked above them.
                            Padding = new MarginPadding { Horizontal = -SettingsPanel.CONTENT_MARGINS },
                            Children = playbackRows,
                        },
                        // The queue keeps its own "Queue (N)" header — that count IS this section's
                        // header, so there is no second one here.
                        queueSection = new QueueSection(
                            () => queuePanel.ContentHeight,
                            () => scroll.DrawHeight - queueSection.DrawPosition.Y)
                        {
                            RelativeSizeAxes = Axes.X,
                            MinHeight = queue_min_height,
                            Child = queuePanel = new QueuePanel(docked: true),
                        },
                    },
                },
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Session-only, like lazer's replay playback control (deliberately not persisted).
        playbackRateRow.Current = playback.PlaybackRate;

        // Per-set offset: the store retargets this bindable on every song change and persists edits.
        if (beatmapOffsetRow != null)
            beatmapOffsetRow.Current = offsetStore!.CurrentOffset;
    }

    private static SpriteText sectionHeader(string text) => new SpriteText
    {
        Font = FontUsage.Default.With(size: Theme.HeaderTextSize),
        Colour = Theme.TextPrimary,
        Text = text,
    };

    /// <summary>
    /// The queue's slot in the scrolling column: as tall as the largest of its own content, its
    /// <see cref="MinHeight"/>, and the height still unused in the viewport below it. Filling the
    /// leftover space is what makes the queue read as the bottom half of the tab rather than a card
    /// floating under the playback controls; growing past it (and letting the page scroll) is what
    /// keeps a long queue fully reachable.
    /// </summary>
    private partial class QueueSection : Container
    {
        /// <summary>See <see cref="PlaybackPanel.queue_min_height"/>.</summary>
        public float MinHeight;

        private readonly Func<float> contentHeight;
        private readonly Func<float> availableHeight;

        public QueueSection(Func<float> contentHeight, Func<float> availableHeight)
        {
            this.contentHeight = contentHeight;
            this.availableHeight = availableHeight;
        }

        protected override void Update()
        {
            base.Update();

            // Both inputs are read a frame late (the child's own auto-size and this drawable's flow
            // position are only resolved after this Update runs), which is invisible: a queue
            // mutation or a resize settles on the very next frame. This is the last child of its
            // flow, so its own height can never feed back into its position.
            Height = Math.Max(MinHeight, Math.Max(contentHeight(), availableHeight()));
        }
    }
}
