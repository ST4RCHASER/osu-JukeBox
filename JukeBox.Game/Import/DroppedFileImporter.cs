#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace JukeBox.Game.Import;

/// <summary>
/// The single entry point for files dragged onto the window: subscribes to the SDL window's
/// <see cref="IWindow.DragDrop"/> event (one path per event — the framework raises it once per
/// dropped file), classifies each path by extension and routes it to the matching importer.
///
/// <para>
/// <see cref="IWindow.DragDrop"/> fires on the SDL window's own thread, so every path from here on
/// is marshalled onto the update thread through <c>Schedule</c> before it touches anything the game
/// owns — the same rule <see cref="JukeBoxGameBase"/>'s focus handling follows for
/// <see cref="GameHost.IsActive"/>. The actual import work then runs off the update thread again
/// (zip extraction, mirror lookups and downloads all block), reporting back through
/// <see cref="Notification"/> — which is written on the update thread, since UI binds to it.
/// </para>
/// </summary>
public partial class DroppedFileImporter : Component
{
    /// <summary>
    /// The most recent user-facing outcome of a drop. UI (see <c>MainScreen</c>) binds a copy of
    /// this and turns each new value into a toast. Always written on the update thread.
    /// </summary>
    public readonly Bindable<DropNotification?> Notification = new();

    private int notificationSequence;

    [Resolved]
    private GameHost host { get; set; } = null!;

    private IWindow? subscribedWindow;

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Null under a headless host (tests) and in any environment without a real window — the
        // importer stays fully usable in that case, it just has no OS-level source of drops.
        // Kept in a field rather than re-reading host.Window on dispose: the host tears its window
        // down during shutdown, and unsubscribing from a different (or null) object would leak the
        // handler.
        subscribedWindow = host.Window;

        if (subscribedWindow != null)
            subscribedWindow.DragDrop += onWindowDragDrop;
    }

    private void onWindowDragDrop(string path) => Schedule(() => Import(path));

    /// <summary>
    /// Imports one dropped path. Fire-and-forget by design (a drop has no caller to await it), but
    /// returns the task so tests can await the whole round trip. Safe to call with anything —
    /// unrecognised extensions and unreadable files are reported through <see cref="Notification"/>
    /// rather than thrown.
    /// </summary>
    public Task Import(string path)
    {
        var kind = DroppedFile.Classify(path);
        Logger.Log($"[drop] {Path.GetFileName(path)} classified as {kind}");

        return importAsync(path, kind);
    }

    private async Task importAsync(string path, DroppedFileKind kind)
    {
        try
        {
            switch (kind)
            {
                case DroppedFileKind.Unsupported:
                    Notify($"Can't import {Path.GetFileName(path)} — drop a .osz, .osk or .osr", isError: true);
                    break;

                default:
                    // Phases 1-3 fill these in; every kind that reaches here is one the classifier
                    // recognises but nothing handles yet.
                    Notify($"Nothing handles {kind} yet", isError: true);
                    break;
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, $"[drop] import of '{path}' failed");
            Notify($"Import failed: {e.Message}", isError: true);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes a user-facing outcome. Callable from any thread — the write itself is scheduled
    /// onto the update thread, because UI binds to <see cref="Notification"/> and bindable change
    /// callbacks run wherever the write happened.
    /// </summary>
    protected void Notify(string message, bool isError)
        => Schedule(() => Notification.Value = new DropNotification(++notificationSequence, message, isError));

    protected override void Dispose(bool isDisposing)
    {
        if (subscribedWindow != null)
        {
            subscribedWindow.DragDrop -= onWindowDragDrop;
            subscribedWindow = null;
        }

        base.Dispose(isDisposing);
    }
}
