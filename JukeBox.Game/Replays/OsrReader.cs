#nullable enable

using System;
using System.IO;
using System.Text;

namespace JukeBox.Game.Replays;

/// <summary>
/// The fixed-layout part of a .osr file, up to and including the mods — everything needed to work
/// out WHICH beatmap a replay belongs to and who played it.
/// </summary>
/// <param name="RulesetId">0 osu! / 1 taiko / 2 catch / 3 mania.</param>
/// <param name="Version">The osu! client build date that wrote the replay (e.g. 20231025).</param>
/// <param name="BeatmapMd5">MD5 of the exact .osu file played — the only identity in the file, and
/// what the mirror checksum lookup searches on.</param>
/// <param name="PlayerName">The player's name as the client recorded it. May be empty.</param>
/// <param name="LegacyMods">The stable mod bitmask.</param>
/// <param name="PlayedAt">When the play happened.</param>
public readonly record struct OsrHeader(
    int RulesetId,
    int Version,
    string BeatmapMd5,
    string PlayerName,
    int LegacyMods,
    DateTimeOffset PlayedAt);

/// <summary>
/// Minimal reader for a .osr replay header.
///
/// <para>
/// lazer's own <c>LegacyScoreDecoder</c> is what ultimately parses these, but it can't be used
/// first: its <c>GetBeatmap(md5)</c> hook has to hand back a real <c>WorkingBeatmap</c> during the
/// parse, and the whole point of reading a dropped replay is that we don't know which beatmap it
/// is yet — that's what the MD5 in this header tells us. So the header is read directly (the layout
/// is fixed and documented), the beatmap resolved and downloaded from it, and only then is the full
/// decode handed to lazer with a beatmap it can actually load. See
/// <see cref="JukeBoxScoreDecoder"/>.
/// </para>
/// </summary>
public static class OsrReader
{
    /// <exception cref="InvalidDataException">The file isn't a readable replay.</exception>
    public static OsrHeader ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadHeader(stream);
    }

    /// <exception cref="InvalidDataException">The stream isn't a readable replay.</exception>
    public static OsrHeader ReadHeader(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            int rulesetId = reader.ReadByte();
            int version = reader.ReadInt32();
            string beatmapMd5 = readString(reader);
            string playerName = readString(reader);
            readString(reader); // replay MD5 — not the beatmap's, and of no use here

            // 300 / 100 / 50 / geki / katu / miss, then score, max combo, perfect flag.
            for (int i = 0; i < 6; i++)
                reader.ReadInt16();

            reader.ReadInt32();
            reader.ReadInt16();
            reader.ReadByte();

            int mods = reader.ReadInt32();

            readString(reader); // life-bar graph

            // Windows FILETIME-style .NET ticks. Values outside DateTime's range appear in
            // replays written by non-osu! tooling; clamp rather than fail the whole import over a
            // field nothing depends on.
            long ticks = reader.ReadInt64();
            var playedAt = ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
                ? new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc))
                : DateTimeOffset.MinValue;

            if (rulesetId is < 0 or > 3)
                throw new InvalidDataException($"unknown ruleset id {rulesetId}");

            if (beatmapMd5.Length != 32)
                throw new InvalidDataException("no beatmap checksum in replay header");

            return new OsrHeader(rulesetId, version, beatmapMd5, playerName, mods, playedAt);
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException)
        {
            throw new InvalidDataException("replay file is truncated or malformed", e);
        }
    }

    /// <summary>
    /// osu!'s string encoding: a single marker byte — 0x00 for "absent" (read as empty), 0x0b for
    /// "present", followed by a ULEB128 byte length and the UTF-8 bytes themselves.
    /// </summary>
    private static string readString(BinaryReader reader)
    {
        byte marker = reader.ReadByte();

        switch (marker)
        {
            case 0x00:
                return string.Empty;

            case 0x0b:
                // BinaryReader.ReadString reads exactly this format (7-bit-encoded length prefix
                // then UTF-8 bytes), which is what osu!'s own writer produced in the first place.
                return reader.ReadString();

            default:
                throw new InvalidDataException($"unexpected string marker 0x{marker:x2} in replay header");
        }
    }
}
