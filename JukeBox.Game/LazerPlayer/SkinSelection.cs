#nullable enable

using System;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
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

    private readonly Random random = new Random();

    /// <summary>The resolved skin to build — never <see cref="JukeBoxSkin.Random"/>.</summary>
    public IBindable<JukeBoxSkin> Effective => effective;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    protected override void LoadComplete()
    {
        base.LoadComplete();

        config.BindWith(JukeBoxSetting.Skin, choice);
        currentSong.BindTo(playback.Current);

        choice.BindValueChanged(e => effective.Value = resolve(e.NewValue), true);
        currentSong.BindValueChanged(_ => OnSongChanged());
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
    /// Builds the concrete skin instance for a resolved choice. The caller owns (and must
    /// dispose) the returned skin. <paramref name="skin"/> must not be
    /// <see cref="JukeBoxSkin.Random"/> — resolve through <see cref="Effective"/> first.
    /// </summary>
    public static Skin CreateSkin(JukeBoxSkin skin, IStorageResourceProvider resources) => skin switch
    {
        JukeBoxSkin.Argon => new ArgonSkin(resources),
        JukeBoxSkin.ArgonPro => new ArgonProSkin(resources),
        JukeBoxSkin.Triangles => new TrianglesSkin(resources),
        JukeBoxSkin.Classic => new DefaultLegacySkin(resources),
        _ => throw new ArgumentOutOfRangeException(nameof(skin), skin, "resolve Random via SkinSelection.Effective before constructing"),
    };
}
