#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;

namespace JukeBox.Game.Detach;

/// <summary>
/// Main-process side of the detached player window: spawns/kills the viewer process (this same
/// binary, run with --viewer) as <see cref="JukeBoxSetting.DetachPlayer"/> flips, and streams
/// <see cref="ViewerSyncState"/> snapshots to its stdin — immediately on song/difficulty change
/// and on a fixed heartbeat that doubles as the position-correction tick. stdin (rather than a
/// socket or osu.Framework's TCP IPC) keeps lifecycle handling free: the viewer exits on stdin
/// EOF (main app died or detached it on purpose), and a viewer death is observed here via
/// <see cref="Process.Exited"/>, which unchecks the setting — either side dying resolves
/// cleanly with no ports, no handshakes, no reconnect states.
/// </summary>
public partial class DetachedViewerManager : Component
{
    /// <summary>Position corrections at 4 Hz — drift accumulated between ticks stays well under
    /// <see cref="ViewerClock.SnapThresholdMs"/>, and settings changes apply within a quarter
    /// second, while the write itself stays negligible.</summary>
    private const double sync_interval_ms = 250;

    /// <summary>How long the graceful stdin-EOF shutdown gets before the viewer is killed.</summary>
    private const double kill_grace_ms = 2000;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved]
    private SkinSelection skinSelection { get; set; } = null!;

    [Resolved]
    private SettingsMirror settings { get; set; } = null!;

    [Resolved]
    private BeatmapOffsetStore offsets { get; set; } = null!;

    [Resolved]
    private ReplayStore replays { get; set; } = null!;

    private Bindable<bool> detach = null!;
    private readonly Bindable<CachedBeatmapSet?> current = new Bindable<CachedBeatmapSet?>();
    private readonly Bindable<string?> selectedOsuFile = new Bindable<string?>();

    private Process? viewerProcess;
    private StreamWriter? viewerStdin;

    // Set while WE are tearing the viewer down (setting unchecked, app closing), so the
    // resulting (expected) process exit doesn't re-enter the setting.
    private bool killingViewer;

    /// <summary>Test-only: whether a viewer process is currently believed alive.</summary>
    internal bool HasViewer => viewerProcess != null;

    protected override void LoadComplete()
    {
        base.LoadComplete();

        detach = config.GetBindable<bool>(JukeBoxSetting.DetachPlayer);
        detach.BindValueChanged(e =>
        {
            if (e.NewValue)
                spawnViewer();
            else
                killViewer();
        }, true);

        current.BindTo(playback.Current);
        selectedOsuFile.BindTo(playback.SelectedOsuFile);

        // Immediate pushes on song/difficulty swap so the viewer starts loading the new visual
        // stack right away instead of waiting out the heartbeat.
        current.BindValueChanged(_ => sendState());
        selectedOsuFile.BindValueChanged(_ => sendState());

        Scheduler.AddDelayed(sendState, sync_interval_ms, true);
    }

    /// <summary>
    /// Virtual seam for tests. Returns null when no viewer can be spawned — most notably under
    /// a test host, where ProcessPath is the test runner, not the game.
    /// </summary>
    protected virtual Process? StartViewerProcess()
    {
        string? exe = Environment.ProcessPath;

        if (exe == null || Path.GetFileNameWithoutExtension(exe).Equals("testhost", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log($"Detached player: no spawnable executable (ProcessPath={exe ?? "null"}).");
            return null;
        }

        return Process.Start(new ProcessStartInfo(exe, "--viewer")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
        });
    }

    private void spawnViewer()
    {
        if (viewerProcess != null)
            return;

        try
        {
            viewerProcess = StartViewerProcess();
        }
        catch (Exception e)
        {
            Logger.Error(e, "Detached player: failed to start viewer process");
            viewerProcess = null;
        }

        if (viewerProcess == null)
        {
            // Nothing spawned — reflect reality in the setting rather than show a checked box
            // with no window behind it.
            detach.Value = false;
            return;
        }

        killingViewer = false;
        viewerStdin = viewerProcess.StandardInput;
        viewerProcess.EnableRaisingEvents = true;
        viewerProcess.Exited += (_, _) => Schedule(onViewerExited);

        Logger.Log($"Detached player: viewer spawned (pid {viewerProcess.Id}).");
        sendState();
    }

    private void onViewerExited()
    {
        if (killingViewer || viewerProcess == null)
            return;

        Logger.Log("Detached player: viewer exited — turning the setting off.");
        viewerProcess.Dispose();
        viewerProcess = null;
        viewerStdin = null;
        detach.Value = false;
    }

    private void killViewer()
    {
        if (viewerProcess == null)
            return;

        killingViewer = true;
        var process = viewerProcess;
        viewerProcess = null;
        viewerStdin = null;

        // Graceful first: closing stdin EOFs the viewer's reader thread, which exits its host.
        // The delayed kill only covers a viewer wedged too hard to notice the EOF.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        Scheduler.AddDelayed(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }, kill_grace_ms);
    }

    private void sendState()
    {
        var stdin = viewerStdin;
        if (stdin == null)
            return;

        var set = current.Value;

        // The replay for the difficulty currently selected — the same lookup the rendering side
        // does, so the viewer plays back a real play exactly when the main window does.
        var replay = replays.ForOsuFile(selectedOsuFile.Value);

        var state = new ViewerSyncState
        {
            SetId = set?.SetId ?? 0,
            SetDirectory = set?.Directory,
            OsuFile = selectedOsuFile.Value,
            ReplayOsrPath = replay?.SourcePath.Length > 0 ? replay.SourcePath : null,
            ReplayOsuFile = replay?.SourcePath.Length > 0 ? replay.OsuFile : null,
            PositionMs = playback.CurrentTimeMs,
            Rate = playback.PlaybackRate.Value,
            Playing = playback.IsPlaying,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Settings = settings.Capture(),
            Skin = skinSelection.Effective.Value.ToString(),
            CustomSkinDirectory = skinSelection.CustomSkinDirectory,
            BeatmapAudioOffset = offsets.CurrentOffset.Value,
        };

        try
        {
            stdin.WriteLine(state.ToJson());
            stdin.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Dead pipe: the Exited handler owns the setting transition; just stop writing.
            viewerStdin = null;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        // App closing: the stdin close inside killViewer EOFs the viewer, which exits itself —
        // the delayed hard-kill won't run post-dispose, but it isn't needed for this path.
        killViewer();
        base.Dispose(isDisposing);
    }
}
