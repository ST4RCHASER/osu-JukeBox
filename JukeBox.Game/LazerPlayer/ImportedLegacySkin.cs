#nullable enable

using System.IO;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Game.IO;
using osu.Game.Skinning;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// A user-imported legacy skin folder (an extracted .osk — see
/// <see cref="Import.SkinArchive"/>) as a <see cref="LegacySkin"/>: the folder is the skin root and
/// its <c>skin.ini</c> is the skin configuration.
///
/// <para>
/// This is the same shape as <see cref="BeatmapFolderSkin"/> — a folder-backed
/// <see cref="StorageBackedResourceStore"/> over an <see cref="IStorageResourceProvider"/> — and
/// exists for the same reason: lazer's own realm-backed skin import (<c>SkinManager.Import</c>)
/// would need a realm-managed <c>SkinInfo</c> with per-file hashes, which this app has no use for.
/// A folder with no <c>skin.ini</c> is fine; <see cref="LegacySkin"/> falls back to its defaults,
/// which is exactly how osu! stable treats such a skin too.
/// </para>
/// </summary>
internal class ImportedLegacySkin : LegacySkin
{
    /// <param name="directory">Absolute path of the extracted skin folder.</param>
    /// <param name="resources">Game-level resource provider.</param>
    /// <param name="host">Host for storage access.</param>
    public ImportedLegacySkin(string directory, IStorageResourceProvider resources, GameHost host)
        : base(
            new SkinInfo { Name = Path.GetFileName(directory) },
            resources,
            // SkinFolderResourceStore rather than a bare StorageBackedResourceStore: a legacy
            // skin.ini names its files as osu! stable wrote them, with Windows separators and the
            // author's own capitalisation, neither of which need match the folder. See that class.
            new SkinFolderResourceStore(new NativeStorage(directory, host)),
            "skin.ini")
    {
    }
}
