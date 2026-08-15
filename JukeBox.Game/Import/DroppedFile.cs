#nullable enable

using System;
using System.IO;

namespace JukeBox.Game.Import;

/// <summary>
/// Which of the osu! file formats a dropped path is, decided purely by extension (see
/// <see cref="DroppedFile.Classify"/>) — the archives are all plain zips and the replay is a
/// binary blob, so there is no cheap content sniff that would tell them apart any better than the
/// name does, and a wrong guess is recoverable either way (the importer reports the failure).
/// </summary>
public enum DroppedFileKind
{
    /// <summary>A <c>.osz</c> beatmap archive.</summary>
    BeatmapArchive,

    /// <summary>A <c>.osk</c> skin archive.</summary>
    SkinArchive,

    /// <summary>A <c>.osr</c> replay.</summary>
    Replay,

    /// <summary>Anything else — reported to the user rather than silently ignored.</summary>
    Unsupported,
}

/// <summary>
/// One user-facing outcome of a drop, pushed through <see cref="DroppedFileImporter.Notification"/>
/// for the UI (see <c>MainScreen</c>) to surface as a toast.
///
/// <para>
/// <see cref="Sequence"/> exists because this travels on a <see cref="osu.Framework.Bindables.Bindable{T}"/>,
/// which only raises <c>ValueChanged</c> when the value actually differs: dropping the same
/// unsupported file twice in a row produces two identical messages, and the second must still
/// reach the UI. A monotonically increasing sequence makes every notification distinct without
/// the consumer having to care.
/// </para>
/// </summary>
public readonly record struct DropNotification(int Sequence, string Message, bool IsError);

/// <summary>Extension-based dispatch for dropped files, isolated from the importer so it can be
/// asserted directly.</summary>
public static class DroppedFile
{
    public static DroppedFileKind Classify(string path)
    {
        // Path.GetExtension throws on nothing in modern .NET (it returns empty for a null-ish or
        // separator-only path), so a hostile drop payload can't take the window thread down here.
        string extension = Path.GetExtension(path);

        if (extension.Equals(".osz", StringComparison.OrdinalIgnoreCase))
            return DroppedFileKind.BeatmapArchive;

        if (extension.Equals(".osk", StringComparison.OrdinalIgnoreCase))
            return DroppedFileKind.SkinArchive;

        if (extension.Equals(".osr", StringComparison.OrdinalIgnoreCase))
            return DroppedFileKind.Replay;

        return DroppedFileKind.Unsupported;
    }
}
