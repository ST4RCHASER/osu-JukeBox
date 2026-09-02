#nullable enable

using System;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// The right column's "Playback" tab body — everything about what is playing and how, in one
/// scrollable column, in two <see cref="Theme"/>-headed sections:
///
/// <list type="bullet">
/// <item><b>Playback</b> — <see cref="NowPlayingPanel"/> (cover, title/artist, status, the
/// transport strip with its open-in-browser button, seekable progress, difficulty dropdown) and
/// the playback-speed slider.</item>
/// <item><b>Queue</b> — the existing <see cref="QueuePanel"/> (list, per-row download status,
/// removal), filling whatever height is left below the playback section.</item>
/// </list>
///
/// <para>
/// This replaces the old bottom playback bar (which spanned the window under the columns) and the
/// old "Queue" tab: the bar's content moved here, as did Settings' former "Playback" section (speed
/// slider + transport). The GLOBAL audio offset deliberately stayed in Settings → Audio: it
/// calibrates the output path (device, drivers, headphones) alongside the device picker and
/// volumes, rather than describing the song being played. The PER-BEATMAP offset has no row at
/// all any more (user request): <see cref="BeatmapOffsetStore"/> and its persistence are untouched
/// and still applied to playback — only the slider is gone.
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

    private NowPlayingPanel nowPlaying = null!;
    private QueuePanel queuePanel = null!;
    private QueueSection queueSection = null!;
    private OsuScrollContainer scroll = null!;
    private PlaybackSpeedSlider playbackRateRow = null!;
    private SpriteText virtualAudioNote = null!;

    /// <summary>The keyboard-shortcut hint under the transport — see where it is built.</summary>
    private SpriteText shortcutHint = null!;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the tab's pieces,
    /// so tests can assert what this section contains without depending on its internal layout.</summary>
    internal NowPlayingPanel NowPlaying => nowPlaying;

    internal QueuePanel Queue => queuePanel;

    internal PlaybackSpeedSlider PlaybackRateSlider => playbackRateRow;

    /// <summary>Test-only: the keyboard-shortcut hint. Exposed so the one place the shortcuts are
    /// discoverable from can't quietly disappear in a layout edit.</summary>
    internal SpriteText ShortcutHint => shortcutHint;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        playbackRateRow = new PlaybackSpeedSlider { LabelText = "Playback speed", KeyboardStep = 0.05f };

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
                        // Shown only for keysound-only sets, where "no music is playing" is the
                        // correct state rather than a fault, and where hitsounds run regardless of
                        // the setting. Alpha 0 keeps it out of the flow entirely otherwise.
                        virtualAudioNote = new SpriteText
                        {
                            Text = "Keysounded map — no music track; hitsounds forced on",
                            Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                            Colour = Theme.TextTertiary,
                            Alpha = 0,
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            // Lazer's settings rows inset their own content by CONTENT_MARGINS on
                            // both sides (they're built for a panel that pads its header the same
                            // way). Pulling that back out here is what keeps the label/slider flush
                            // with the Theme-styled content stacked above them — and what forces the
                            // revert arrow onto the right instead of into the left gutter that
                            // cancellation moves off-panel (see PlaybackRateSlider).
                            Padding = new MarginPadding { Horizontal = -SettingsPanel.CONTENT_MARGINS },
                            Child = playbackRateRow,
                        },
                        // Where the keyboard shortcuts are discoverable from: one line, directly
                        // under the very controls they drive, rather than a section in Settings the
                        // user would have to already suspect exists. Only the everyday five —
                        // the full set (meter selection, mute, zoom reset, media keys) is in
                        // Input.PlaybackShortcuts, and a line long enough to list all of them would
                        // wrap into a paragraph nobody reads.
                        shortcutHint = new SpriteText
                        {
                            Text = "Space play/pause · ←→ seek · ↑↓ volume · PgUp/PgDn speed",
                            Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                            Colour = Theme.TextTertiary,
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

        playback.Current.BindValueChanged(e => virtualAudioNote.Alpha = e.NewValue?.HasVirtualAudio == true ? 1 : 0, true);
    }

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo): whether the
    /// keysounded-map note is showing.</summary>
    internal bool VirtualAudioNoteVisible => virtualAudioNote.Alpha > 0;

    /// <summary>
    /// The playback-speed row: lazer's <see cref="SettingsSlider{T}"/> with the live rate ("1.25×")
    /// and a revert-to-default arrow pinned to the right end of its label line.
    ///
    /// <para>
    /// Lazer parks its revert arrow in a 20px gutter to the LEFT of every settings label
    /// (<see cref="SettingsItem{T}.ShowsDefaultIndicator"/>), which is unreachable here: this row
    /// cancels the settings content margins (see its wrapper in <see cref="load"/>) so its label and
    /// slider sit flush with the <see cref="Theme"/>-styled content stacked above it in the tab, and
    /// that same cancellation pushes the gutter off the panel's left edge. So the built-in indicator
    /// is switched off and lazer's own <see cref="RevertToDefaultButton{T}"/> — same drawable, same
    /// self-managed "visible only when off-default" fade — is re-hosted on the right instead, beside
    /// the value it reverts.
    /// </para>
    /// </summary>
    internal partial class PlaybackSpeedSlider : SettingsSlider<double>
    {
        private readonly OsuSpriteText valueText;
        private readonly RevertToDefaultButton<double> revertButton;

        /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo).</summary>
        internal OsuSpriteText ValueText => valueText;

        internal RevertToDefaultButton<double> RevertButton => revertButton;

        public PlaybackSpeedSlider()
        {
            ShowsDefaultIndicator = false;

            AddInternal(new FillFlowContainer
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(6, 0),
                Children = new Drawable[]
                {
                    revertButton = new RevertToDefaultButton<double>
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                    },
                    valueText = new OsuSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Font = OsuFont.Default.With(size: 14, weight: FontWeight.SemiBold),
                    },
                },
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            revertButton.Current = Current;

            // Two decimals unconditionally ("1.00×", not "1×"): the label is pinned to the panel's
            // right edge, so a width that changed with the digit count would visibly shuffle it
            // every step of a drag.
            Current.BindValueChanged(e => valueText.Text = $"{e.NewValue:0.00}×", true);
        }
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
