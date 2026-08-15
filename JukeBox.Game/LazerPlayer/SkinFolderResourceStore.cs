#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using osu.Framework.Extensions;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// A skin folder's files, looked up the way a legacy <c>skin.ini</c> names them rather than the way
/// they happen to sit on disk.
///
/// <para>
/// osu! stable wrote skin.ini on Windows, so its file references carry backslashes and whatever
/// capitalisation the author typed — neither of which need match the actual files. The skin in the
/// user's report asks for <c>Arrownote\left</c> and ships <c>arrownote/left.png</c>. Looked up
/// literally that resolves to nothing on macOS or Linux, and since EVERY mania note and key image in
/// that skin is referenced this way, the stage renders with no notes at all.
/// </para>
///
/// <para>
/// lazer never meets this because its skins are realm-backed, and it resolves files through a
/// lowercased, separator-standardised map (see <c>RealmBackedResourceStore</c>). This is the same
/// idea for a folder: index the folder once, then match on the standardised, lowercased path.
/// Deliberately a whole-folder index rather than a per-lookup directory probe — a skin is asked for
/// hundreds of files during load, and the alternative is a case-insensitive walk per miss.
/// </para>
/// </summary>
internal class SkinFolderResourceStore : ResourceStore<byte[]>
{
    /// <summary>Standardised, lowercased relative path → the path as it actually exists on disk.
    /// Built lazily: a skin is constructed well before anything asks it for a file, and a folder
    /// that is never read should not be walked.</summary>
    private readonly Lazy<Dictionary<string, string>> files;

    public SkinFolderResourceStore(Storage storage)
        : base(new StorageBackedResourceStore(storage))
    {
        files = new Lazy<Dictionary<string, string>>(() => index(storage));
    }

    private static Dictionary<string, string> index(Storage storage)
    {
        var map = new Dictionary<string, string>();

        // Last writer wins, and only for names that collide once lowercased — which on a
        // case-insensitive filesystem cannot happen, and on a case-sensitive one is a skin authoring
        // mistake either way. Matching lazer, which builds its map the same way.
        foreach (string path in storage.GetFiles(string.Empty, "*").Concat(directoriesRecursive(storage)))
            map[path.ToStandardisedPath().ToLowerInvariant()] = path;

        return map;
    }

    /// <summary>Every file below the skin root, relative to it. <see cref="Storage.GetFiles"/> does
    /// not recurse, and skins habitually nest their images (this one keeps every note under
    /// <c>arrownote/</c>).</summary>
    private static IEnumerable<string> directoriesRecursive(Storage storage)
    {
        foreach (string directory in storage.GetDirectories(string.Empty))
        {
            foreach (string file in storage.GetFiles(directory, "*"))
                yield return file;

            foreach (string nested in directoriesRecursive(storage.GetStorageForDirectory(directory)))
                yield return Path.Combine(directory, nested);
        }
    }

    /// <summary>
    /// Maps each name the base store would try — the name itself, then the same name with each
    /// searched extension — onto the file that actually exists, or drops it. Overriding here rather
    /// than at <c>Get</c> is what keeps the extension search working: it happens in the base class,
    /// above this.
    /// </summary>
    protected override IEnumerable<string> GetFilenames(string name)
    {
        foreach (string filename in base.GetFilenames(name))
        {
            if (files.Value.TryGetValue(filename.ToStandardisedPath().ToLowerInvariant(), out string? actual))
                yield return actual;
        }
    }
}
