#nullable enable

using System;
using JukeBox.Game.Playback;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// The playback transport strip: restart, −5s, play/pause, +5s, skip-next, and (when the host
/// supplies an action for it) open-in-browser — all routed through the existing controller/jukebox
/// methods (no playback machinery of its own). The play/pause icon tracks
/// <see cref="PlaybackController.IsPlaying"/> (a plain property, hence the per-frame refresh), and
/// skip-next only exists when a <see cref="Jukebox"/> is present to skip within.
///
/// <para>
/// Previously a nested control inside <see cref="SettingsOverlay"/>'s "Playback" section, coloured
/// from lazer's <c>OverlayColourProvider</c> to blend with the settings rows around it. It now sits
/// inside <see cref="NowPlayingPanel"/> in the right column's "Playback" tab, so it is coloured
/// from <see cref="Theme"/> instead — the play/pause button accent-filled, every other button on
/// the shared elevated surface.
/// </para>
///
/// <para>
/// Auto-sized on both axes (rather than filling its host's width) so the host can simply centre it:
/// the strip is a cluster of buttons, and a left-aligned cluster under a full-width progress bar
/// read as adrift.
/// </para>
/// </summary>
internal partial class TransportRow : FillFlowContainer
{
    private const double seek_step_ms = 5000;
    private const float button_size = 34;

    /// <summary>How far a disabled control is dimmed — the same value
    /// <see cref="DifficultySwitcher"/> dims its locked dropdown by, so "you can't use this right
    /// now" looks the same wherever it appears.</summary>
    private const float disabled_alpha = 0.55f;

    private readonly PlaybackController playback;
    private readonly IconButton playPause;

    private readonly Jukebox? jukebox;
    private readonly IconButton? skipNext;

    /// <summary>The trailing open-in-browser button, when <c>openInBrowser</c> was supplied — see
    /// <see cref="NowPlayingPanel.BrowserButton"/>, which forwards to it.</summary>
    internal IconButton? BrowserButton { get; }

    public TransportRow(PlaybackController playback, Jukebox? jukebox, Action? openInBrowser = null)
    {
        this.playback = playback;

        AutoSizeAxes = Axes.Both;
        Direction = FillDirection.Horizontal;
        Spacing = new Vector2(Theme.RowSpacing, 0);

        Add(new IconButton
        {
            Icon = FontAwesome.Solid.UndoAlt,
            Size = new Vector2(button_size),
            Action = () => playback.Restart(),
        });
        Add(new IconButton
        {
            Icon = FontAwesome.Solid.Backward,
            Size = new Vector2(button_size),
            Action = () => playback.Seek(Math.Max(0, playback.CurrentTimeMs - seek_step_ms)),
        });
        Add(playPause = new IconButton
        {
            Icon = FontAwesome.Solid.Play,
            Size = new Vector2(button_size),
            IdleColour = Theme.AccentDim,
            HoverColour = Theme.Accent,
            Action = playback.TogglePause,
        });
        Add(new IconButton
        {
            Icon = FontAwesome.Solid.Forward,
            Size = new Vector2(button_size),
            Action = () => playback.Seek(Math.Min(playback.LengthMs, playback.CurrentTimeMs + seek_step_ms)),
        });

        if (jukebox != null)
        {
            this.jukebox = jukebox;

            Add(skipNext = new IconButton
            {
                Icon = FontAwesome.Solid.StepForward,
                Size = new Vector2(button_size),
                Action = jukebox.SkipCurrent,
            });
        }

        // Trailing the skip button rather than sitting up beside the title: it's a per-song action
        // like the rest of the strip, and moving it here gives the title/artist block the whole
        // width of its own row.
        if (openInBrowser != null)
        {
            IconButton browser;
            Add(browser = new IconButton
            {
                Icon = FontAwesome.Solid.ExternalLinkAlt,
                Size = new Vector2(button_size),
                Action = openInBrowser,
            });
            BrowserButton = browser;
        }
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Bound here rather than in the constructor because ClickableContainer.Action's setter
        // writes Enabled itself (Enabled.Value = action != null) — binding before Action is
        // assigned would be overwritten by it.
        if (jukebox != null && skipNext != null)
        {
            skipNext.Enabled.BindTo(jukebox.CanSkipNext);
            skipNext.Enabled.BindValueChanged(
                e => skipNext.FadeTo(e.NewValue ? 1 : disabled_alpha, Theme.DurationFast, Theme.EaseExit), true);
        }
    }

    protected override void Update()
    {
        base.Update();
        playPause.Icon = playback.IsPlaying ? FontAwesome.Solid.Pause : FontAwesome.Solid.Play;
    }
}
