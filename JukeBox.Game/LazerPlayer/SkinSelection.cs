#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.IO;
using osu.Game.Skinning;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Game-lifetime service resolving the user's <see cref="JukeBoxSetting.Skin"/> choice into the
/// concrete skin the chart renderer should build. A resolved skin is a PAIR — see
/// <see cref="Effective"/> and <see cref="EffectiveCustomFolder"/> — because
/// <see cref="JukeBoxSkin.Custom"/> alone does not say which imported skin is meant. For the
/// concrete choices the pair mirrors config; for <see cref="JukeBoxSkin.Random"/> it re-rolls the
/// whole library (bundled skins plus every import) on every song change
/// (<see cref="PlaybackController.Current"/>) and immediately when Random is first selected.
/// Consumers (BeatmapVisuals) rebuild the chart layer on <see cref="Effective"/> and
/// <see cref="Revision"/> changes, so a dropdown flip applies live and Random applies per song.
/// </summary>
public partial class SkinSelection : Component
{
    private readonly Bindable<JukeBoxSkin> choice = new Bindable<JukeBoxSkin>();
    private readonly Bindable<JukeBoxSkin> effective = new Bindable<JukeBoxSkin>(JukeBoxSkin.Argon);
    private readonly Bindable<CachedBeatmapSet?> currentSong = new Bindable<CachedBeatmapSet?>();
    private readonly Bindable<string> customSkinName = new Bindable<string>(string.Empty);

    private readonly Random random = new Random();

    /// <summary>The resolved skin to build — never <see cref="JukeBoxSkin.Random"/>.</summary>
    public IBindable<JukeBoxSkin> Effective => effective;

    /// <summary>
    /// Bumped whenever the skin to build changes CONTENT without <see cref="Effective"/> itself
    /// changing — i.e. a freshly-imported .osk replacing the custom skin while
    /// <see cref="JukeBoxSkin.Custom"/> is already selected. Consumers that rebuild on
    /// <see cref="Effective"/> (BeatmapVisuals) rebuild on this too, so a dropped skin applies
    /// immediately instead of waiting for the next song.
    /// </summary>
    public IBindable<int> Revision => revision;

    private readonly Bindable<int> revision = new Bindable<int>();

    /// <summary>The SELECTED imported skin's folder name, straight off
    /// <see cref="JukeBoxSetting.CustomSkinPath"/>, or empty when none is selected. This is the
    /// user's choice, which is not the same thing as what is being rendered — see
    /// <see cref="EffectiveCustomFolder"/>.</summary>
    public IBindable<string> CustomSkinName => customSkinName;

    /// <summary>
    /// The RESOLVED imported skin's folder name — what <see cref="CustomSkinDirectory"/> points at
    /// and what the chart is actually built from — or empty when the effective skin is a bundled
    /// one. Distinct from <see cref="CustomSkinName"/> because <see cref="JukeBoxSkin.Random"/>
    /// rolls the whole library, so the imported skin on screen is frequently not the imported skin
    /// the user last selected.
    /// </summary>
    public IBindable<string> EffectiveCustomFolder => effectiveCustomFolder;

    private readonly Bindable<string> effectiveCustomFolder = new Bindable<string>(string.Empty);

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved]
    private GameHost host { get; set; } = null!;

    protected override void LoadComplete()
    {
        base.LoadComplete();

        config.BindWith(JukeBoxSetting.Skin, choice);
        config.BindWith(JukeBoxSetting.CustomSkinPath, customSkinName);
        currentSong.BindTo(playback.Current);

        choice.BindValueChanged(e => apply(resolve(e.NewValue)), true);
        currentSong.BindValueChanged(_ => OnSongChanged());

        // A different imported skin selected out of the library. Only meaningful while the user's
        // own choice is what's being built: under Random the roll owns the effective folder, and
        // re-resolving here would swap the rolled skin for the selected one mid-song.
        customSkinName.BindValueChanged(_ =>
        {
            if (choice.Value == JukeBoxSkin.Custom)
                apply(resolve(JukeBoxSkin.Custom));
        });
    }

    /// <summary>
    /// Absolute path of the imported skin folder currently being built, or null when the effective
    /// skin is a bundled one, nothing has been imported, or the folder has since been deleted from
    /// under us (a user tidying up app storage by hand).
    ///
    /// <para>
    /// Resolved from <see cref="EffectiveCustomFolder"/>, not <see cref="CustomSkinName"/>: under
    /// <see cref="JukeBoxSkin.Random"/> the rolled skin is the one on screen, and this is what the
    /// detached viewer is told to load (see <c>DetachedViewerManager.BuildState</c>), so pointing
    /// it at the user's selection instead would show the two windows different skins.
    /// </para>
    /// </summary>
    public string? CustomSkinDirectory
    {
        get
        {
            if (externalCustomSkinDirectory != null)
                return Directory.Exists(externalCustomSkinDirectory) ? externalCustomSkinDirectory : null;

            if (effectiveCustomFolder.Value.Length == 0)
                return null;

            string path = Path.Combine(skinsRoot, effectiveCustomFolder.Value);
            return Directory.Exists(path) ? path : null;
        }
    }

    private string skinsRoot => host.Storage.GetFullPath(SkinLibrary.STORAGE_DIRECTORY);

    private string? externalCustomSkinDirectory;

    /// <summary>
    /// Points <see cref="CustomSkinDirectory"/> at a folder OUTSIDE this process's storage,
    /// overriding the <see cref="JukeBoxSetting.CustomSkinPath"/> lookup entirely. Exists for the
    /// detached viewer window, which runs under its own storage: the setting holds a folder name
    /// resolved against the MAIN process's <c>skins/</c> directory, so mirroring the name alone
    /// would resolve to nothing and silently degrade the imported skin to Argon. Setting it bumps
    /// <see cref="Revision"/> when Custom is what's being built, so a change applies to the chart
    /// already on screen rather than at the next song.
    /// </summary>
    public void SetExternalCustomSkinDirectory(string? directory)
    {
        if (externalCustomSkinDirectory == directory)
            return;

        externalCustomSkinDirectory = directory;

        if (effective.Value == JukeBoxSkin.Custom)
            revision.Value++;
    }

    /// <summary>
    /// The per-song Random re-roll (no-op for concrete choices). Internal so tests can drive the
    /// song-change path without real playback (JukeBox.Game.Tests has InternalsVisibleTo).
    /// </summary>
    internal void OnSongChanged()
    {
        if (choice.Value == JukeBoxSkin.Random)
            apply(resolve(JukeBoxSkin.Random));
    }

    /// <summary>
    /// Publishes a resolved (skin, imported-folder) pair. Both halves move together, and the
    /// folder is set FIRST so anything rebuilding off <see cref="Effective"/> already sees the
    /// folder that goes with it. When only the folder moved — Random rolling from one imported
    /// skin straight to another, both of which are <see cref="JukeBoxSkin.Custom"/> —
    /// <see cref="Effective"/> does not fire at all, so the rebuild has to come from
    /// <see cref="Revision"/> instead.
    /// </summary>
    private void apply((JukeBoxSkin skin, string folder) resolved)
    {
        bool folderChanged = effectiveCustomFolder.Value != resolved.folder;

        effectiveCustomFolder.Value = resolved.folder;

        if (effective.Value != resolved.skin)
            effective.Value = resolved.skin;
        else if (folderChanged)
            revision.Value++;
    }

    private (JukeBoxSkin skin, string folder) resolve(JukeBoxSkin value)
    {
        if (value == JukeBoxSkin.Custom)
            return (JukeBoxSkin.Custom, customSkinName.Value);

        if (value != JukeBoxSkin.Random)
            return (value, string.Empty);

        // Random draws from the whole library — the four bundled skins plus every imported one, so
        // a user who imported skins sees them come up too rather than Random quietly meaning "one
        // of the four that shipped". Read off disk (rather than through the SkinLibrary component)
        // so the pool is whatever is installed right now and this service needs no dependency on
        // the settings UI's listing.
        var pool = new List<(JukeBoxSkin skin, string folder)>
        {
            (JukeBoxSkin.Argon, string.Empty),
            (JukeBoxSkin.ArgonPro, string.Empty),
            (JukeBoxSkin.Triangles, string.Empty),
            (JukeBoxSkin.Classic, string.Empty),
        };

        foreach (var imported in SkinLibrary.Scan(skinsRoot))
            pool.Add((JukeBoxSkin.Custom, imported.Folder));

        // Avoid an immediate repeat so "random per song" visibly changes the chart (a silent
        // no-op reads as the feature not working). Compared on the PAIR, so rolling the same
        // imported skin twice running counts as a repeat too.
        var current = (effective.Value, effectiveCustomFolder.Value);
        (JukeBoxSkin skin, string folder) next;

        do
            next = pool[random.Next(pool.Count)];
        while (next == current && pool.Count > 1);

        return next;
    }

    /// <summary>
    /// Builds the skin instance for the current <see cref="Effective"/> value, including the
    /// user-imported <see cref="JukeBoxSkin.Custom"/> one — which <see cref="CreateSkin"/> can't
    /// build, since it needs this instance's storage-resolved folder. The caller owns (and must
    /// dispose) the returned skin.
    ///
    /// <para>
    /// Custom with nothing imported (or an imported folder that has since disappeared, or one that
    /// fails to load) degrades to <see cref="JukeBoxSkin.Argon"/> rather than rendering an empty
    /// chart — the selection is reachable from the dropdown regardless of whether an import ever
    /// happened, so this is a normal state, not an error.
    /// </para>
    /// </summary>
    public Skin CreateEffectiveSkin(IStorageResourceProvider resources)
    {
        if (effective.Value != JukeBoxSkin.Custom)
            return CreateSkin(effective.Value, resources);

        string? directory = CustomSkinDirectory;

        if (directory != null)
        {
            try
            {
                return new ImportedLegacySkin(directory, resources, host);
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Failed to load imported skin '{directory}' — falling back to Argon");
            }
        }
        else
            Logger.Log("Custom skin selected but none is imported — falling back to Argon");

        return CreateSkin(JukeBoxSkin.Argon, resources);
    }

    /// <summary>
    /// Builds the concrete BUNDLED skin instance for a resolved choice. The caller owns (and must
    /// dispose) the returned skin. <paramref name="skin"/> must be neither
    /// <see cref="JukeBoxSkin.Random"/> (resolve through <see cref="Effective"/> first) nor
    /// <see cref="JukeBoxSkin.Custom"/> (build it through <see cref="CreateEffectiveSkin"/>, which
    /// has the storage access this static doesn't).
    /// </summary>
    public static Skin CreateSkin(JukeBoxSkin skin, IStorageResourceProvider resources) => skin switch
    {
        JukeBoxSkin.Argon => new ArgonSkin(resources),
        JukeBoxSkin.ArgonPro => new ArgonProSkin(resources),
        JukeBoxSkin.Triangles => new TrianglesSkin(resources),
        JukeBoxSkin.Classic => new DefaultLegacySkin(resources),
        _ => throw new ArgumentOutOfRangeException(nameof(skin), skin, "resolve Random via SkinSelection.Effective, and Custom via CreateEffectiveSkin, before constructing"),
    };
}
