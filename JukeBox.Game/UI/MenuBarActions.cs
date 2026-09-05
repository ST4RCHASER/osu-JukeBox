#nullable enable

using System;
using osu.Framework.Bindables;

namespace JukeBox.Game.UI;

/// <summary>
/// The complete set of things the top <see cref="MenuBar"/> can ask the app to do, handed in as
/// plain delegates and bindables so the bar itself knows nothing about the screen it drives — it
/// renders items and, when one is chosen, calls back through here. <see cref="Screens.MainScreen"/>
/// (which owns the overlays, the playback controller and the spectate controller) populates every
/// field; the bar only reads them.
///
/// <para>
/// The two bindables are state the bar has to REFLECT rather than trigger: <see cref="RenderEnabled"/>
/// greys the File → Render… item while it is false (the app clears it during spectating), and
/// <see cref="Spectating"/> flips the Spectate menu's one toggle between "Start" and "Stop". They are
/// <see cref="IBindable{T}"/> so the bar can bind without being able to write the source of truth.
/// </para>
/// </summary>
public record MenuBarActions
{
    // ---- File ------------------------------------------------------------------------------------

    /// <summary>Opens the native multi-select file browser (replays/skins/beatmaps), routed through
    /// the same importer as drag-drop.</summary>
    public Action? OpenFiles { get; init; }

    /// <summary>Opens the Render dialog. Presented greyed while <see cref="RenderEnabled"/> is false.</summary>
    public Action? OpenRender { get; init; }

    /// <summary>Exits the app.</summary>
    public Action? Quit { get; init; }

    // ---- Playback --------------------------------------------------------------------------------

    public Action? Play { get; init; }
    public Action? Pause { get; init; }
    public Action? Next { get; init; }
    public Action? Restart { get; init; }

    /// <summary>Opens the current beatmap's osu.ppy.sh page in the browser.</summary>
    public Action? OpenBeatmapPage { get; init; }

    // ---- Queue -----------------------------------------------------------------------------------

    /// <summary>Opens the existing "Add a beatmap" by-id/link dialog (<see cref="MapIdOverlay"/>).</summary>
    public Action? LookupById { get; init; }

    /// <summary>Opens the fullscreen beatmap search.</summary>
    public Action? SearchBeatmaps { get; init; }

    // ---- Spectate --------------------------------------------------------------------------------

    /// <summary>Starts or stops spectating — the single control the <see cref="Spectating"/> bindable
    /// describes the current side of.</summary>
    public Action? ToggleSpectate { get; init; }

    /// <summary>Opens the <see cref="SpectateSetupOverlay"/> to manage the watch list.</summary>
    public Action? SetupPlayers { get; init; }

    // ---- Help ------------------------------------------------------------------------------------

    /// <summary>Opens the <see cref="ShortcutsOverlay"/> listing every shortcut.</summary>
    public Action? ShowShortcuts { get; init; }

    // ---- Reflected state -------------------------------------------------------------------------

    /// <summary>Whether File → Render… is available. Defaults to enabled so a bar built without a
    /// source (tests, bare scenes) is fully usable.</summary>
    public IBindable<bool> RenderEnabled { get; init; } = new BindableBool(true);

    /// <summary>Whether spectating is currently on, so the Spectate toggle can read "Stop" rather
    /// than "Start".</summary>
    public IBindable<bool> Spectating { get; init; } = new BindableBool();
}
