#nullable enable

using osu.Framework.Bindables;

namespace JukeBox.Game.Replays;

/// <summary>
/// The one place the multi-replay preload's progress is published, so the piece DOING the preload
/// (the combine layer's <c>ReplaySimulator</c>, off in the player box) and the piece SHOWING it (the
/// Players panel's buffer bar, over in the right column) do not need to know about each other. Both
/// resolve this from DI: the combine <see cref="Report"/>s each frame while it records, the panel
/// binds to <see cref="Progress"/> and <see cref="Active"/> and draws a YouTube-style buffer bar.
///
/// <para>
/// <see cref="Progress"/> is the fraction of every timeline recorded (0 to 1), and <see cref="Active"/>
/// is whether a preload is going on at all — false when no multi-replay is mounted, or once the
/// recording is finished, so the bar only shows while there is something to buffer.
/// </para>
/// </summary>
public sealed class PreloadProgressTracker
{
    /// <summary>Fraction of the multi-replay preload complete, 0 to 1. Sits at 1 when idle.</summary>
    public readonly Bindable<double> Progress = new Bindable<double>(1);

    /// <summary>Whether a preload is currently running — the buffer bar shows only while this is set.</summary>
    public readonly BindableBool Active = new BindableBool();

    /// <summary>Publishes the current fraction. Marks the preload active until it reaches completion,
    /// at which point the bar has nothing left to show and hides itself.</summary>
    public void Report(double progress)
    {
        Progress.Value = progress;
        Active.Value = progress < 0.999;
    }

    /// <summary>Preload gone — no multi-replay mounted. The bar hides and the fraction resets.</summary>
    public void Clear()
    {
        Active.Value = false;
        Progress.Value = 1;
    }
}
