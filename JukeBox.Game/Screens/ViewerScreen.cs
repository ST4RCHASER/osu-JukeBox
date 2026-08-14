#nullable enable

using System;
using System.IO;
using System.Threading;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Detach;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osuTK;

namespace JukeBox.Game.Screens;

/// <summary>
/// The detached viewer window's only screen: a black-bedded replica of MainScreen's player box
/// (same fixed design canvas, same contain-scaling, same <see cref="BeatmapVisuals"/> stack),
/// driven not by local playback but by <see cref="ViewerSyncState"/> snapshots read line-by-line
/// from a <see cref="TextReader"/> (the viewer process's stdin; tests substitute their own).
/// EOF means the main process is gone or detached us on purpose — the viewer exits; conversely
/// the main process watches for OUR process exit to uncheck the setting, so either side dying
/// resolves cleanly.
/// </summary>
public partial class ViewerScreen : Screen
{
    // Same design canvas as MainScreen's player box (keep in sync with MainScreen.scene_width /
    // scene_height — see that class's remarks for why 854×480 covers every beatmap).
    private const float scene_width = 854;
    private const float scene_height = 480;

    private readonly TextReader input;
    private readonly ViewerClock viewerClock = new ViewerClock();

    [Resolved]
    private BeatmapCache cache { get; set; } = null!;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private GameHost host { get; set; } = null!;

    private Container sceneContainer = null!;
    private SpriteText waitingText = null!;

    private BeatmapVisuals? visuals;

    // Latest snapshot handed over from the reader thread; latest-wins is correct because every
    // snapshot is full and idempotent — intermediate ones carry nothing a later one doesn't.
    private ViewerSyncState? pending;
    private readonly object pendingLock = new object();
    private volatile bool inputClosed;

    // What the current visuals were built for — snapshots repeat the same set/difficulty a few
    // times per second, and only an actual change may rebuild the (expensive) visual stack.
    private int builtSetId = -1;
    private string? builtOsuFile;

    // Same async-load arbitration as NowPlayingScreen: only the load whose generation is still
    // current when it completes is allowed to swap in.
    private int generation;

    private bool lastPlaying;
    private int syncLogCounter;

    /// <summary>Test-only exit interception — the real screen exits its host, which a test
    /// browser must not do (JukeBox.Game.Tests has InternalsVisibleTo).</summary>
    internal Action? ExitAction;

    /// <summary>Test-only: the currently-hosted visuals.</summary>
    internal BeatmapVisuals? CurrentVisuals => visuals;

    /// <summary>Test-only: the sync-corrected clock driving the visuals.</summary>
    internal ViewerClock SyncClock => viewerClock;

    public ViewerScreen(TextReader input)
    {
        this.input = input;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black,
            },
            sceneContainer = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(scene_width, scene_height),
            },
            waitingText = new SpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Text = "Waiting for the main window…",
                Colour = Colour4.White.Opacity(0.4f),
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Blocking reads stay off the update thread; IsBackground so a blocked read never keeps
        // the process alive once the host is gone.
        new Thread(readLoop)
        {
            IsBackground = true,
            Name = "ViewerScreen input",
        }.Start();
    }

    private void readLoop()
    {
        try
        {
            string? line;

            while ((line = input.ReadLine()) != null)
            {
                var state = ViewerSyncState.FromJson(line);

                if (state == null)
                    continue;

                lock (pendingLock)
                    pending = state;
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "Viewer: input reader failed");
        }

        inputClosed = true;
    }

    protected override void Update()
    {
        base.Update();

        if (inputClosed)
        {
            inputClosed = false;
            Logger.Log("Viewer: input closed — exiting.");
            exit();
            return;
        }

        ViewerSyncState? state;

        lock (pendingLock)
        {
            state = pending;
            pending = null;
        }

        if (state != null)
            apply(state);

        // Pumped here — before this drawable's children update — mirroring how
        // PlaybackController pumps the clock BeatmapVisuals normally runs on.
        viewerClock.ProcessFrame();
        updateSceneScale();
    }

    private void exit() => (ExitAction ?? host.Exit)();

    private void apply(ViewerSyncState state)
    {
        if (state.Version != ViewerSyncState.PROTOCOL_VERSION)
        {
            Logger.Log($"Viewer: protocol version mismatch (got {state.Version}, expected {ViewerSyncState.PROTOCOL_VERSION}) — exiting.");
            exit();
            return;
        }

        // Mirror the main app's visual settings into OUR config instance — BeatmapVisuals binds
        // these already, so they apply live exactly as they would in the main window. (This
        // config belongs to the viewer's own separate storage, so persisting them is harmless.)
        config.SetValue(JukeBoxSetting.BackgroundDim, state.BackgroundDim);
        config.SetValue(JukeBoxSetting.BackgroundBlur, state.BackgroundBlur);
        config.SetValue(JukeBoxSetting.PlayfieldZoom, state.PlayfieldZoom);
        config.SetValue(JukeBoxSetting.GlobalAudioOffset, state.GlobalAudioOffset);
        config.SetValue(JukeBoxSetting.RenderChart, state.RenderChart);
        config.SetValue(JukeBoxSetting.ShowStoryboardVideo, state.ShowStoryboardVideo);

        if (Enum.TryParse<JukeBoxSkin>(state.Skin, out var skin))
            config.SetValue(JukeBoxSetting.Skin, skin);

        int snapsBefore = viewerClock.SnapCount;
        viewerClock.Apply(state.PositionMs, state.Rate, state.Playing);

        // The sync evidence any "is it in sync?" report greps for: signed local-vs-main clock
        // delta per correction, plus pipe transport delay. Corrections arrive at 4 Hz, so the
        // steady state is throttled to a heartbeat; anything eventful always logs.
        bool eventful = viewerClock.SnapCount != snapsBefore || state.Playing != lastPlaying;
        lastPlaying = state.Playing;

        if (eventful || syncLogCounter++ % 40 == 0)
            Logger.Log($"[viewer-sync] pos={state.PositionMs:F0}ms delta={viewerClock.LastDeltaMs:+0;-0;0}ms snaps={viewerClock.SnapCount} transport={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.SentAtUnixMs}ms playing={state.Playing}");

        if (state.SetId != builtSetId || state.OsuFile != builtOsuFile)
            rebuild(state);
    }

    private void rebuild(ViewerSyncState state)
    {
        builtSetId = state.SetId;
        builtOsuFile = state.OsuFile;

        int myGeneration = ++generation;

        if (state.SetDirectory == null || !Directory.Exists(state.SetDirectory))
        {
            Logger.Log($"Viewer: no loadable set directory ('{state.SetDirectory}', exists={state.SetDirectory != null && Directory.Exists(state.SetDirectory)})");
            sceneContainer.Clear();
            visuals = null;
            waitingText.Alpha = 1;
            return;
        }

        CachedBeatmapSet set;

        try
        {
            set = cache.LoadFromDirectory(state.SetId, state.SetDirectory);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Viewer: failed to load set {state.SetId} from '{state.SetDirectory}'");
            return;
        }

        // A null OsuFile builds the set default — record which file that IS, so the main app's
        // follow-up snapshot naming that same file explicitly (Current and SelectedOsuFile
        // change as a pair over there) doesn't trigger a second identical rebuild.
        builtOsuFile = state.OsuFile ?? set.PreferredOsuFile;

        var incoming = new BeatmapVisuals(set, viewerClock.FramedClock, state.OsuFile)
        {
            RelativeSizeAxes = Axes.Both,
        };

        Logger.Log($"Viewer: rebuild scheduling load for set {state.SetId} ({set.OsuFiles.Count} difficulties)");

        LoadComponentAsync(incoming, loaded =>
        {
            Logger.Log($"Viewer: load callback for set {state.SetId} (gen {myGeneration}/{generation})");
            if (myGeneration != generation)
            {
                loaded.Dispose();
                return;
            }

            var outgoing = visuals;
            visuals = loaded;
            sceneContainer.Add(loaded);

            if (outgoing != null)
                sceneContainer.Remove(outgoing, true);

            waitingText.Alpha = 0;
        });
    }

    // Same contain-scaling MainScreen.updateSceneScale applies to its player box, with the
    // window itself as the box.
    private void updateSceneScale()
    {
        if (DrawWidth <= 0 || DrawHeight <= 0)
            return;

        float scale = Math.Min(DrawWidth / scene_width, DrawHeight / scene_height);
        float zoom = (float)config.Get<double>(JukeBoxSetting.PlayfieldZoom);

        sceneContainer.Scale = new Vector2(scale * zoom);
    }
}
