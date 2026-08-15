#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JukeBox.Game.Detach;

/// <summary>
/// One full snapshot of everything the detached viewer window needs to mirror the main app:
/// which set/difficulty is playing, where the playback clock is, and the visual settings that
/// affect rendering. Sent as a single JSON line over the viewer process's stdin — always a full
/// snapshot (never a delta) so a dropped or late message can't leave the viewer permanently out
/// of sync: applying any snapshot is idempotent and self-contained.
/// </summary>
public sealed class ViewerSyncState
{
    /// <summary>
    /// Bumped on any breaking change to this type's shape. A viewer receiving a different
    /// version exits rather than misinterpret fields (a stale binary can be running as the
    /// viewer after an upgrade); the main process observes the exit and turns the setting off.
    /// </summary>
    public const int PROTOCOL_VERSION = 2;

    public int Version { get; set; } = PROTOCOL_VERSION;

    // ---- what's playing ----

    public int SetId { get; set; }

    /// <summary>Absolute path of the extracted set directory. Same machine, same beatmap cache —
    /// the viewer re-scans it read-only via BeatmapCache.LoadFromDirectory. Null while nothing
    /// is playing yet.</summary>
    public string? SetDirectory { get; set; }

    /// <summary>Absolute path of the selected .osu difficulty, or null for the set default.</summary>
    public string? OsuFile { get; set; }

    /// <summary>
    /// Absolute path of the .osr the user dropped for <see cref="ReplayOsuFile"/>, or null when no
    /// replay is attached to anything currently loadable. The viewer decodes it itself (same
    /// decoder, same local file) rather than receiving serialized frames — a replay's frames are
    /// megabytes, and both processes can read the same path.
    /// </summary>
    public string? ReplayOsrPath { get; set; }

    /// <summary>The difficulty <see cref="ReplayOsrPath"/> was played on — the key the rendering
    /// side looks a replay up by. Null whenever <see cref="ReplayOsrPath"/> is.</summary>
    public string? ReplayOsuFile { get; set; }

    // ---- clock ----

    public double PositionMs { get; set; }
    public double Rate { get; set; } = 1;
    public bool Playing { get; set; }

    /// <summary>Sender wall-clock at send time (Unix ms) — lets the viewer log transport delay
    /// alongside its clock delta.</summary>
    public long SentAtUnixMs { get; set; }

    // ---- settings, mirrored into the viewer's own config managers ----

    /// <summary>
    /// Every setting <see cref="SettingsMirror"/> knows about, keyed by its registry key. This is
    /// the single channel for the whole settings surface — our own config, lazer's game-wide one
    /// and the per-ruleset ones — so syncing a new setting means adding it to that registry, not
    /// adding a field here. See <see cref="SettingsMirror"/> for why the values are strings.
    /// </summary>
    public Dictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();

    /// <summary>The main app's RESOLVED skin name (never Random — the main process rolls it, so
    /// both windows show the same concrete skin). Outside <see cref="Settings"/> for exactly that
    /// reason: the raw config value is the unresolved choice.</summary>
    public string Skin { get; set; } = nameof(Configuration.JukeBoxSkin.Argon);

    /// <summary>
    /// Absolute path of the imported .osk folder backing <see cref="Configuration.JukeBoxSkin.Custom"/>,
    /// or null when nothing is imported. Also outside <see cref="Settings"/>: the config value is a
    /// folder NAME resolved against the sending process's storage, and the viewer's storage is a
    /// different directory that has no skins in it.
    /// </summary>
    public string? CustomSkinDirectory { get; set; }

    /// <summary>
    /// The per-mapset audio offset in force for what's playing, in ms. Outside <see cref="Settings"/>
    /// because it isn't a config key at all: it's whichever entry of the main process's
    /// <c>beatmap-offsets.json</c> matches the current set (see <c>BeatmapOffsetStore</c>), a file
    /// the viewer's storage doesn't have.
    /// </summary>
    public double BeatmapAudioOffset { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, ViewerSyncStateJsonContext.Default.ViewerSyncState);

    /// <summary>
    /// Parses one received line. Returns null for malformed input (the reader skips it) rather
    /// than throwing — a torn or partial line must not take the viewer down.
    /// </summary>
    public static ViewerSyncState? FromJson(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, ViewerSyncStateJsonContext.Default.ViewerSyncState);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Source-generated (reflection-free) serialization for the sync protocol.</summary>
[JsonSerializable(typeof(ViewerSyncState))]
internal partial class ViewerSyncStateJsonContext : JsonSerializerContext;
