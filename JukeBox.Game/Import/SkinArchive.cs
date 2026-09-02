#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using osu.Framework.Logging;

namespace JukeBox.Game.Import;

/// <summary>
/// Extraction of a dropped .osk (a plain zip) into the app's own <c>skins/</c> storage, ready for
/// <see cref="LazerPlayer.ImportedLegacySkin"/> to read as a folder-backed legacy skin.
/// </summary>
public static class SkinArchive
{
    /// <summary>
    /// A skin's folder (and therefore its persisted identity — see
    /// <see cref="Configuration.JukeBoxSetting.CustomSkinPath"/>) is its archive's file name with
    /// anything that can't appear in a path element replaced by <c>_</c>. Empty or all-invalid
    /// names fall back to <c>skin</c> rather than producing an unusable directory name.
    /// </summary>
    public static string SanitiseName(string rawName)
    {
        // Both separators explicitly, on top of the platform's own list: '\' is a perfectly legal
        // file-name character on macOS/Linux, so a skin named "a\b" would otherwise produce a
        // folder name that behaves differently depending on where the app runs.
        char[] invalid = Path.GetInvalidFileNameChars().Append('/').Append('\\').ToArray();

        var builder = new StringBuilder(rawName.Length);

        foreach (char c in rawName)
            builder.Append(invalid.Contains(c) ? '_' : c);

        string name = builder.ToString().Trim().Trim('.');

        return name.Length == 0 ? "skin" : name;
    }

    /// <summary>
    /// Suffix marking a half-extracted staging folder. An import materialises under this name and
    /// is renamed into place only once the archive proves usable, so anything still carrying it is
    /// either an import in flight or the wreckage of a failed one — never an installed skin. See
    /// <see cref="LazerPlayer.SkinLibrary.Scan"/>, which skips these when listing the library.
    /// </summary>
    public const string STAGING_SUFFIX = ".extracting";

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <c>{skinsRoot}/{name}</c>, replacing any
    /// existing folder of that name (re-dropping a skin is the natural way to update it), and
    /// returns the absolute path of the extracted folder.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive holds nothing that looks like a skin.</exception>
    public static string Extract(string archivePath, string skinsRoot, string name)
    {
        Directory.CreateDirectory(skinsRoot);

        string staging = Path.Combine(skinsRoot, $"import-{Guid.NewGuid():N}{STAGING_SUFFIX}");

        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging);

            // An archive with no entries at all doesn't even leave the target directory behind.
            if (!Directory.Exists(staging) || !Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("skin archive is empty");

            unwrapSingleTopLevelDirectory(staging);

            string target = Path.Combine(skinsRoot, name);

            if (Directory.Exists(target))
                Directory.Delete(target, true);

            Directory.Move(staging, target);
            Logger.Log($"SkinArchive: imported '{Path.GetFileName(archivePath)}' as skin '{name}'");

            return target;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, true);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"SkinArchive: failed to clean up staging directory '{staging}'");
                }
            }
        }
    }

    /// <summary>
    /// Some .osk files are zipped from the parent directory, so everything sits one level down
    /// inside a single wrapper folder. <see cref="LazerPlayer.ImportedLegacySkin"/> looks for
    /// <c>skin.ini</c> and element textures at the folder ROOT, so such a skin would silently
    /// resolve nothing. Hoisting the wrapper's contents up one level is safe precisely because the
    /// condition is narrow: no files at the top level and exactly one directory there.
    /// </summary>
    private static void unwrapSingleTopLevelDirectory(string dir)
    {
        if (Directory.EnumerateFiles(dir).Any())
            return;

        string[] subdirectories = Directory.GetDirectories(dir);

        if (subdirectories.Length != 1)
            return;

        string inner = subdirectories[0];

        foreach (string file in Directory.GetFiles(inner))
            File.Move(file, Path.Combine(dir, Path.GetFileName(file)));

        foreach (string subdirectory in Directory.GetDirectories(inner))
            Directory.Move(subdirectory, Path.Combine(dir, Path.GetFileName(subdirectory)));

        Directory.Delete(inner, true);
    }
}
