#nullable enable

using System;
using JukeBox.Game.Playback;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// The playback transport strip: restart, −5s, play/pause, +5s, skip-next — all routed through the
/// existing controller/jukebox methods (no playback machinery of its own). The play/pause icon
/// tracks <see cref="PlaybackController.IsPlaying"/> (a plain property, hence the per-frame
/// refresh), and skip-next only exists when a <see cref="Jukebox"/> is present to skip within.
///
/// <para>
/// Previously a nested control inside <see cref="SettingsOverlay"/>'s "Playback" section, coloured
/// from lazer's <c>OverlayColourProvider</c> to blend with the settings rows around it. It now
/// lives in the right column's "Playback" tab (see <see cref="PlaybackPanel"/>) alongside the rest
/// of the transport-adjacent controls, so it is coloured from <see cref="Theme"/> instead — the
/// play/pause button accent-filled, the seek/skip buttons on the shared elevated surface.
/// </para>
/// </summary>
internal partial class TransportRow : FillFlowContainer
{
    private const double seek_step_ms = 5000;
    private const float button_size = 34;

    private readonly PlaybackController playback;
    private readonly IconButton playPause;

    public TransportRow(PlaybackController playback, Jukebox? jukebox)
    {
        this.playback = playback;

        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        Direction = FillDirection.Horizontal;
        Spacing = new Vector2(Theme.RowSpacing, 0);

        Add(new IconButton
        {
            Icon = FontAwesome.Solid.UndoAlt,
            Size = new Vector2(button_size),
            Action = () => playback.Seek(0),
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
            Add(new IconButton
            {
                Icon = FontAwesome.Solid.StepForward,
                Size = new Vector2(button_size),
                Action = jukebox.SkipCurrent,
            });
        }
    }

    protected override void Update()
    {
        base.Update();
        playPause.Icon = playback.IsPlaying ? FontAwesome.Solid.Pause : FontAwesome.Solid.Play;
    }
}
