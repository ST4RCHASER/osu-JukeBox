#nullable enable

using System;
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
/// concrete bundled skin the chart renderer should build. For the concrete choices
/// <see cref="Effective"/> simply mirrors the config value; for <see cref="JukeBoxSkin.Random"/>
/// it re-rolls one of the four concrete skins on every song change
/// (<see cref="PlaybackController.Current"/>) and immediately when Random is first selected.
/// Consumers (BeatmapVisuals) react to <see cref="Effective"/> changes by rebuilding the chart
/// layer, so a dropdown flip applies live and Random applies per song.
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

    /// <summary>The imported skin's folder name, or empty when nothing has been imported. Drives
    /// the settings dropdown's "Custom: &lt;name&gt;" label.</summary>
    public IBindable<string> CustomSkinName => customSkinName;

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

        choice.BindValueChanged(e => effective.Value = resolve(e.NewValue), true);
        currentSong.BindValueChanged(_ => OnSongChanged());

        // Only meaningful while Custom is what's being built; for any other selection the imported
        // skin isn't in the chain, so there is nothing to rebuild.
        customSkinName.BindValueChanged(_ =>
        {
            if (effective.Value == JukeBoxSkin.Custom)
                revision.Value++;
        });
    }

    /// <summary>
    /// Absolute path of the imported skin folder, or null when nothing has been imported or the
    /// folder has since been deleted from under us (a user tidying up app storage by hand).
    /// </summary>
    public string? CustomSkinDirectory
    {
        get
        {
            if (externalCustomSkinDirectory != null)
                return Directory.Exists(externalCustomSkinDirectory) ? externalCustomSkinDirectory : null;

            if (customSkinName.Value.Length == 0)
                return null;

            string path = Path.Combine(host.Storage.GetFullPath("skins"), customSkinName.Value);
            return Directory.Exists(path) ? path : null;
        }
    }

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
            effective.Value = resolve(JukeBoxSkin.Random);
    }

    private JukeBoxSkin resolve(JukeBoxSkin value)
    {
        if (value != JukeBoxSkin.Random)
            return value;

        // Roll among the four concrete skins, avoiding an immediate repeat so "random per song"
        // visibly changes the chart (a 1-in-4 silent no-op reads as the feature not working).
        JukeBoxSkin[] pool = { JukeBoxSkin.Argon, JukeBoxSkin.ArgonPro, JukeBoxSkin.Triangles, JukeBoxSkin.Classic };
        JukeBoxSkin next;

        do
            next = pool[random.Next(pool.Length)];
        while (next == effective.Value && pool.Length > 1);

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
