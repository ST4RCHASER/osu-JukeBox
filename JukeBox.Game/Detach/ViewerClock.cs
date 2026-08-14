#nullable enable

using System;
using osu.Framework.Timing;

namespace JukeBox.Game.Detach;

/// <summary>
/// The detached viewer's playback clock: a locally-running stopwatch (so visuals animate
/// smoothly between sync messages) corrected against the main process's reported position.
/// Small drift is left alone — the local stopwatch and the main app's track clock tick at the
/// same real-time rate, so re-seeking on every correction would only add jitter; drift beyond
/// <see cref="SnapThresholdMs"/> (a user seek, a stall, or accumulated error) snaps hard, and
/// the visual stack's existing big-seek machinery (LazerChartLayer's seek-snap) absorbs it.
/// Plain class rather than a Component so tests can drive it without a host; ViewerScreen pumps
/// <see cref="ProcessFrame"/> once per update, the same external-pump contract
/// PlaybackController's decoupled clock has.
/// </summary>
public class ViewerClock
{
    /// <summary>Beyond this the local clock hard-seeks to the reported position. Comfortably
    /// above the drift a 4 Hz correction interval can accumulate, comfortably below anything a
    /// viewer of the storyboard would perceive as "out of sync".</summary>
    public const double SnapThresholdMs = 120;

    private readonly StopwatchClock stopwatch = new StopwatchClock();
    private readonly FramedClock framed;

    public ViewerClock()
    {
        framed = new FramedClock(stopwatch);
    }

    /// <summary>The frame-based view BeatmapVisuals consumes (same shape as
    /// PlaybackController.PlaybackClock).</summary>
    public IFrameBasedClock FramedClock => framed;

    public double CurrentTime => stopwatch.CurrentTime;
    public bool IsRunning => stopwatch.IsRunning;

    /// <summary>How many drift corrections exceeded <see cref="SnapThresholdMs"/> and forced a
    /// hard seek (run-state changes and paused repositions don't count — those are expected
    /// authoritative seeks, not drift).</summary>
    public int SnapCount { get; private set; }

    /// <summary>Signed drift (local minus reported) observed by the most recent
    /// <see cref="Apply"/>, before any correction it made.</summary>
    public double LastDeltaMs { get; private set; }

    /// <summary>Applies one sync snapshot's clock fields.</summary>
    public void Apply(double positionMs, double rate, bool playing)
    {
        stopwatch.Rate = rate;
        LastDeltaMs = stopwatch.CurrentTime - positionMs;

        bool runStateChanged = playing != stopwatch.IsRunning;

        if (runStateChanged)
        {
            if (playing)
                stopwatch.Start();
            else
                stopwatch.Stop();
        }

        // On a run-state change the reported position is authoritative (pause freezes the main
        // clock at a point this stopwatch overshot; play resumes from wherever the user left
        // it). While paused, corrections are exact. While playing, only real drift seeks.
        if (runStateChanged || !playing || Math.Abs(LastDeltaMs) > SnapThresholdMs)
        {
            stopwatch.Seek(positionMs);

            if (playing && !runStateChanged)
                SnapCount++;
        }
    }

    public void ProcessFrame() => framed.ProcessFrame();
}
