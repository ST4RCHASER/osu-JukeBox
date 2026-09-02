#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Import;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.IO;
using osu.Game.Skinning;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// One imported skin, as the settings dropdown lists it.
/// </summary>
/// <param name="Folder">
/// The skin's folder name under the app storage's <c>skins/</c> directory. This — not
/// <paramref name="Name"/> — is the skin's IDENTITY: it is what
/// <see cref="Configuration.JukeBoxSetting.CustomSkinPath"/> persists, it is unique by
/// construction (it is a directory name), and it survives the author editing the display name in
/// <c>skin.ini</c>. Two different skins can and do declare the same name.
/// </param>
/// <param name="Name">The name the skin declares for itself, or the folder name as a fallback.</param>
/// <param name="Label">
/// What to actually show in the dropdown: <paramref name="Name"/>, suffixed "(2)", "(3)" … when
/// other skins in the library declare that same name. Without the suffix a library holding two
/// skins both called "Aristia" would offer two rows a user cannot tell apart.
/// </param>
public sealed record ImportedSkin(string Folder, string Name, string Label);

/// <summary>
/// The user's imported skins, read off disk rather than tracked in config: the <c>skins/</c>
/// directory IS the library, so a folder copied in by hand shows up and a folder deleted by hand
/// disappears, with nothing to keep in sync. Config remembers only WHICH one is selected (see
/// <see cref="Configuration.JukeBoxSetting.CustomSkinPath"/>).
///
/// <para>
/// The scan itself is <see cref="Scan"/>, a static: <see cref="SkinSelection"/> calls it directly
/// when rolling a random skin, so the two never disagree about what is installed and neither has
/// to depend on the other. This component exists for the settings dropdown, which needs the list
/// to be a bindable that updates the moment a .osk is imported (see
/// <see cref="DroppedFileImporter"/>, which calls <see cref="Refresh"/>).
/// </para>
/// </summary>
public partial class SkinLibrary : Component
{
    /// <summary>
    /// The app storage subdirectory holding imported skins — the same one
    /// <see cref="SkinArchive.Extract"/> writes into.
    /// </summary>
    public const string STORAGE_DIRECTORY = "skins";

    private readonly BindableList<ImportedSkin> skins = new BindableList<ImportedSkin>();

    /// <summary>Every imported skin, ordered as the dropdown lists them (see <see cref="Scan"/>).</summary>
    public IBindableList<ImportedSkin> Skins => skins;

    [Resolved]
    private GameHost host { get; set; } = null!;

    /// <summary>Absolute path of the skins directory. It need not exist yet.</summary>
    public string Root => host.Storage.GetFullPath(STORAGE_DIRECTORY);

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Refresh();
    }

    /// <summary>
    /// Re-reads the skins directory. Called on load and after an import; cheap enough to call
    /// freely (a handful of directories and one small ini each), and a no-op for bindable
    /// subscribers when nothing actually changed — <see cref="ImportedSkin"/> is a record, so an
    /// unchanged library compares equal element-wise and the list is left alone rather than
    /// cleared and refilled, which would otherwise reset the dropdown's selection on every call.
    /// </summary>
    public void Refresh()
    {
        var scanned = Scan(Root);

        if (skins.SequenceEqual(scanned))
            return;

        skins.Clear();
        skins.AddRange(scanned);
    }

    /// <summary>
    /// Deletes one imported skin's folder and re-lists. Returns false when the folder was already
    /// gone or could not be removed, so the caller reports what happened rather than claiming a
    /// deletion that did not.
    ///
    /// <para>
    /// Deleting the SELECTED skin is deliberately NOT this method's business: the library only
    /// knows what is installed, and nothing here reads or writes
    /// <see cref="Configuration.JukeBoxSetting.Skin"/>. <c>MaintenanceSection</c> moves the
    /// selection off the doomed skin first, and only then deletes.
    /// </para>
    /// </summary>
    public bool Delete(string folder)
    {
        // A plain folder name, never a path. A caller passing "../../something" or an absolute
        // path must not be able to steer a recursive delete out of the skins directory.
        if (folder.Length == 0 || folder != Path.GetFileName(folder))
        {
            Logger.Log($"SkinLibrary: refusing to delete '{folder}' — not a plain folder name");
            return false;
        }

        string directory = Path.Combine(Root, folder);

        try
        {
            if (!Directory.Exists(directory))
                return false;

            Directory.Delete(directory, true);
            Logger.Log($"SkinLibrary: deleted imported skin '{folder}'");
            return true;
        }
        catch (Exception e)
        {
            Logger.Error(e, $"SkinLibrary: failed to delete imported skin '{folder}'");
            return false;
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>
    /// Lists the skins installed under <paramref name="skinsRoot"/>, ordered by display name
    /// (case-insensitive, ties broken by folder name so the order is stable across runs) and
    /// labelled with duplicate names disambiguated.
    /// </summary>
    public static IReadOnlyList<ImportedSkin> Scan(string skinsRoot)
    {
        var found = new List<(string folder, string name)>();

        try
        {
            if (!Directory.Exists(skinsRoot))
                return Array.Empty<ImportedSkin>();

            foreach (string directory in Directory.EnumerateDirectories(skinsRoot))
            {
                string folder = Path.GetFileName(directory);

                // SkinArchive extracts into a staging folder and only renames it into place once
                // the archive turns out to be usable, so a staging folder is either an import in
                // flight or the wreckage of a failed one — never something to offer the user.
                if (folder.EndsWith(SkinArchive.STAGING_SUFFIX, StringComparison.Ordinal))
                    continue;

                found.Add((folder, ReadDisplayName(directory)));
            }
        }
        catch (Exception e)
        {
            // A directory disappearing mid-enumeration, or one we cannot read. Report whatever was
            // collected before the failure rather than losing the whole library to one bad folder.
            Logger.Error(e, $"Could not list imported skins under '{skinsRoot}'");
        }

        return label(found);
    }

    /// <summary>
    /// The name a skin folder declares for itself — <c>[General] Name:</c> in its <c>skin.ini</c>,
    /// parsed by lazer's own <see cref="LegacySkinDecoder"/> so this agrees with what osu! itself
    /// would call the skin. Falls back to the folder name when there is no skin.ini, when it
    /// declares no name, or when it cannot be read: the folder name is what the entry was called
    /// before this existed, so the fallback is never worse than the old behaviour.
    /// </summary>
    public static string ReadDisplayName(string skinDirectory)
    {
        string folder = Path.GetFileName(skinDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string ini = Path.Combine(skinDirectory, "skin.ini");

        try
        {
            if (File.Exists(ini))
            {
                using var stream = File.OpenRead(ini);
                using var reader = new LineBufferedReader(stream);

                string declared = new LegacySkinDecoder().Decode(reader).SkinInfo.Name.Trim();

                if (declared.Length > 0)
                    return declared;
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Could not read skin.ini for '{folder}' — falling back to the folder name");
        }

        return folder;
    }

    private static IReadOnlyList<ImportedSkin> label(List<(string folder, string name)> found)
    {
        var ordered = found
                      .OrderBy(f => f.name, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(f => f.folder, StringComparer.Ordinal)
                      .ToList();

        var timesSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ImportedSkin>(ordered.Count);

        foreach ((string folder, string name) in ordered)
        {
            int occurrence = timesSeen.TryGetValue(name, out int previous) ? previous + 1 : 1;
            timesSeen[name] = occurrence;

            result.Add(new ImportedSkin(folder, name, occurrence == 1 ? name : $"{name} ({occurrence})"));
        }

        return result;
    }
}
