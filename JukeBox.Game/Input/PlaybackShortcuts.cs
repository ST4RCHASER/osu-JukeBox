#nullable enable

using System;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Game.Input.Bindings;
using osu.Game.Overlays;
using osuTK.Input;

namespace JukeBox.Game.Input;

/// <summary>
/// Every keyboard shortcut that drives playback, in one place rather than scattered through
/// whichever drawable happened to be listening.
///
/// <para>
/// Hosted near the FRONT of <see cref="Screens.MainScreen"/>'s children so it sees keys before the
/// screen's own handling (type-anywhere-to-search) and before the hosted ruleset. The framework
/// still gives the FOCUSED drawable first refusal on non-positional input, which is what makes
/// typing in a text box safe without this having to know which boxes exist — but only for keys that
/// box actually consumes, so <see cref="suppressed"/> covers the rest explicitly.
/// </para>
///
/// <para>
/// Volume is delegated to lazer's own <see cref="VolumeOverlay"/> (hosted as-is), driven through its
/// public <c>Adjust</c> with lazer's REAL default combos — Alt+Up/Down to change, Alt+Left/Right to
/// move between the master/effect/music meters, Ctrl+F4 to mute. Plain Up/Down is bound to the same
/// increase/decrease on top of that, because it is what the app's own user asked for and lazer
/// leaves it free here (it means "select previous/next" in a song list this app does not have).
/// </para>
/// </summary>
public partial class PlaybackShortcuts : CompositeDrawable
{
    /// <summary>Small seek, for nudging past an intro. Matches the arrow keys' "one step" feel.</summary>
    public const double SmallSeekMs = 5000;

    /// <summary>Big seek (Ctrl held), for crossing a song rather than nudging through it.</summary>
    public const double BigSeekMs = 30000;

    /// <summary>Speed step. Deliberately <see cref="PlaybackController.PlaybackRate"/>'s own
    /// Precision: a step finer than the bindable's granularity would round away to nothing on some
    /// presses and not others, which reads as the key randomly not working.</summary>
    public const double SpeedStep = 0.05;

    /// <summary>Playfield zoom step, in the same units as
    /// <see cref="JukeBoxSetting.PlayfieldZoom"/> (1.0 = 100%).</summary>
    public const double ZoomStep = 0.05;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    // Absent in bare test scenes that exercise the transport without a full conductor.
    [Resolved(canBeNull: true)]
    private Jukebox? jukebox { get; set; }

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    /// <summary>
    /// Lazer's real volume overlay, and the shared speed/zoom readout.
    ///
    /// <para>
    /// Handed in rather than resolved, because neither can be: both are SIBLINGS of this drawable in
    /// the main screen's children, and a <c>[Cached]</c> drawable (which <see cref="VolumeOverlay"/>
    /// is) only provides itself to its own subtree. Resolving them looked right, compiled, and left
    /// every volume key silently dead — the whole feature failing on a DI subtlety no test that
    /// constructed them together would have caught.
    /// </para>
    ///
    /// <para>Null in scenes that host neither, where those keys do nothing rather than throwing.</para>
    /// </summary>
    private readonly VolumeOverlay? volume;

    private readonly TransientValueOverlay? readout;

    /// <summary>
    /// The zoom setting, typed as the ranged bindable the config actually declares it as. Cast
    /// rather than assumed: as a plain <c>Bindable&lt;double&gt;</c> the range is invisible, and it
    /// is the range that both CLAMPS the stepping and gives the readout's bar something to be a
    /// fraction of. A failed cast leaves zoom inert rather than stepping an unbounded value.
    /// </summary>
    private BindableDouble? playfieldZoom;

    public PlaybackShortcuts(VolumeOverlay? volume = null, TransientValueOverlay? readout = null)
    {
        this.volume = volume;
        this.readout = readout;

        RelativeSizeAxes = Axes.Both;

        // Draws nothing, but must stay PRESENT: the framework builds its non-positional input
        // queue from present drawables, so a zero-alpha, auto-sized handler would silently stop
        // receiving keys.
        AlwaysPresent = true;
        Alpha = 0;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        // Held in a field: ConfigManager references the copy it hands back only weakly, so one
        // nobody keeps alive is collected and the setting quietly stops moving.
        playfieldZoom = config?.GetBindable<double>(JukeBoxSetting.PlayfieldZoom) as BindableDouble;
    }

    /// <summary>
    /// Whether keys should be left alone entirely because the user is typing.
    ///
    /// <para>
    /// Deliberately a test against the framework's <see cref="TextBox"/> base rather than against
    /// the specific boxes this app has: the search box, the map-id box and anything added later all
    /// derive from it, so a new text field is covered the day it appears instead of the day someone
    /// remembers to add it here.
    /// </para>
    /// </summary>
    private bool suppressed => GetContainingInputManager()?.FocusedDrawable is TextBox;

    /// <summary>
    /// Whether <paramref name="key"/> should keep acting while it is held down.
    ///
    /// <para>
    /// True only for the continuous adjustments, where holding to ramp a value is the point. Every
    /// other binding is a toggle or a jump: a held Space that toggled pause at the key-repeat rate
    /// is precisely the bug this exists to stop, and a held Home would restart the song forever.
    /// </para>
    /// </summary>
    internal static bool RepeatableKey(Key key)
        => key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.PageUp or Key.PageDown
            or Key.Plus or Key.Minus or Key.KeypadPlus or Key.KeypadMinus;

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (suppressed)
            return false;

        if (e.Repeat && !RepeatableKey(e.Key))
            return false;

        return handle(e);
    }

    private bool handle(KeyDownEvent e)
    {
        bool ctrl = e.ControlPressed;
        bool alt = e.AltPressed;
        bool super = e.SuperPressed;
        bool shift = e.ShiftPressed;

        // Cmd on macOS, Alt elsewhere — accepted interchangeably so one muscle memory works on
        // either platform rather than the shortcut existing only on the machine it was written on.
        bool zoomModifier = super || alt;

        switch (e.Key)
        {
            case Key.Space when !ctrl && !alt && !super:
                playback.TogglePause();
                return true;

            case Key.Home when !ctrl && !alt && !super:
                restart();
                return true;

            case Key.Left when ctrl && !alt && !super:
                seekBy(-BigSeekMs);
                return true;

            case Key.Right when ctrl && !alt && !super:
                seekBy(BigSeekMs);
                return true;

            // Lazer's own meter selection, kept on its real combo.
            case Key.Left when alt && !ctrl && !super:
                return adjustVolume(GlobalAction.PreviousVolumeMeter);

            case Key.Right when alt && !ctrl && !super:
                return adjustVolume(GlobalAction.NextVolumeMeter);

            case Key.Left when !ctrl && !alt && !super:
                seekBy(-SmallSeekMs);
                return true;

            case Key.Right when !ctrl && !alt && !super:
                seekBy(SmallSeekMs);
                return true;

            // Plain arrows as well as lazer's Alt-modified ones — see the class summary.
            case Key.Up when !ctrl && !super:
                return adjustVolume(GlobalAction.IncreaseVolume, shift);

            case Key.Down when !ctrl && !super:
                return adjustVolume(GlobalAction.DecreaseVolume, shift);

            case Key.F4 when ctrl && !alt && !super:
                return adjustVolume(GlobalAction.ToggleMute);

            case Key.PageUp when !ctrl && !alt && !super:
                changeSpeed(SpeedStep);
                return true;

            case Key.PageDown when !ctrl && !alt && !super:
                changeSpeed(-SpeedStep);
                return true;

            case Key.Plus or Key.KeypadPlus when zoomModifier && !ctrl:
                changeZoom(ZoomStep);
                return true;

            case Key.Minus or Key.KeypadMinus when zoomModifier && !ctrl:
                changeZoom(-ZoomStep);
                return true;

            case Key.Number0 or Key.Keypad0 when zoomModifier && !ctrl:
                resetZoom();
                return true;

            // Hardware transport keys. Free of every collision the letter keys have — a bare N or M
            // would have to be stolen from type-anywhere-to-search, which is the app's main way in.
            case Key.PlayPause:
                playback.TogglePause();
                return true;

            case Key.TrackNext:
                jukebox?.SkipCurrent();
                return true;

            // There is no "previous": the queue consumes entries as they play (see MusicQueue), so
            // nothing is kept to go back TO. Restarting the current song is what the key can
            // honestly do, and matches what most players do on a first press.
            case Key.TrackPrevious:
                restart();
                return true;
        }

        return false;
    }

    private void restart() => playback.Seek(0);

    private void seekBy(double deltaMs)
    {
        double target = playback.CurrentTimeMs + deltaMs;

        // The two bounds are applied separately, NOT as one Math.Clamp: the track's length is 0
        // whenever none is loaded, and Math.Clamp(target, 0, length) with a negative target then
        // throws outright ("0 cannot be greater than -4675"), taking the update thread with it.
        // Seeking backwards before anything is playing is an ordinary thing to do.
        if (target < 0)
            target = 0;

        double length = playback.LengthMs;

        // Only an upper bound worth having when there is one. Stopping AT the end rather than past
        // it: seeking beyond a track's length ends the song, so a nudge forward near the end would
        // skip the track instead of moving within it.
        if (length > 0 && target > length)
            target = length;

        playback.Seek(target);
    }

    private bool adjustVolume(GlobalAction action, bool precise = false)
    {
        if (volume == null)
            return false;

        // Lazer's own semantics, kept deliberately: the first press with the meters hidden REVEALS
        // them without changing anything, and presses after that adjust. Copying the component but
        // not its behaviour would be the worse half of both options.
        return volume.Adjust(action, 1, precise);
    }

    private void changeSpeed(double delta)
    {
        var rate = playback.PlaybackRate;

        // Ranged bindable — see changeZoom on why this needs no clamping of its own.
        rate.Value += delta;

        readout?.Display("Speed", $"{rate.Value:0.00}×",
            (float)((rate.Value - rate.MinValue) / (rate.MaxValue - rate.MinValue)));
    }

    private void changeZoom(double delta)
    {
        if (playfieldZoom == null)
            return;

        // No Math.Clamp: a ranged bindable clamps on assignment, so stepping past either end
        // simply lands on it.
        playfieldZoom.Value += delta;
        showZoom();
    }

    private void resetZoom()
    {
        if (playfieldZoom == null)
            return;

        playfieldZoom.Value = 1;
        showZoom();
    }

    private void showZoom()
    {
        if (playfieldZoom == null)
            return;

        readout?.Display("Playfield zoom", $"{playfieldZoom.Value * 100:0}%",
            (float)((playfieldZoom.Value - playfieldZoom.MinValue) / (playfieldZoom.MaxValue - playfieldZoom.MinValue)));
    }
}
