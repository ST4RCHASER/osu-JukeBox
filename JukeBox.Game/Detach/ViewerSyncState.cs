#nullable enable

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
    public const int PROTOCOL_VERSION = 1;

    public int Version { get; set; } = PROTOCOL_VERSION;

    // ---- what's playing ----

    public int SetId { get; set; }

    /// <summary>Absolute path of the extracted set directory. Same machine, same beatmap cache —
    /// the viewer re-scans it read-only via BeatmapCache.LoadFromDirectory. Null while nothing
    /// is playing yet.</summary>
    public string? SetDirectory { get; set; }

    /// <summary>Absolute path of the selected .osu difficulty, or null for the set default.</summary>
    public string? OsuFile { get; set; }

    // ---- clock ----

    public double PositionMs { get; set; }
    public double Rate { get; set; } = 1;
    public bool Playing { get; set; }

    /// <summary>Sender wall-clock at send time (Unix ms) — lets the viewer log transport delay
    /// alongside its clock delta.</summary>
    public long SentAtUnixMs { get; set; }

    // ---- visual settings, mirrored into the viewer's own config instance ----

    public double BackgroundDim { get; set; }
    public double BackgroundBlur { get; set; }
    public double PlayfieldZoom { get; set; } = 1;
    public double GlobalAudioOffset { get; set; }
    public bool RenderChart { get; set; }
    public bool ShowStoryboardVideo { get; set; } = true;

    /// <summary>The main app's RESOLVED skin name (never Random — the main process rolls it, so
    /// both windows show the same concrete skin).</summary>
    public string Skin { get; set; } = nameof(Configuration.JukeBoxSkin.Argon);

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
