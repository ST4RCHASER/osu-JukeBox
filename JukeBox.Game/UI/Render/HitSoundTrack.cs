#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using JukeBox.Game.Configuration;
using JukeBox.Game.Replays;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Scoring;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// Builds the hitsound AUDIO a render muxes over the song: every sample the play would sound
/// (<see cref="HitSoundSchedule"/>) is resolved to a real sound the way gameplay's skin chain
/// resolves it (beatmap folder for beatmap-provided samples, then the active skin's folder, then
/// lazer's stock samples out of osu.Game.Resources), decoded once through ffmpeg, and mixed at its
/// judgement time into one 16-bit WAV covering exactly the render range —
/// <see cref="FfmpegEncoder.BuildArgs"/> then amixes that WAV over the song into the output's audio
/// stream. The effect-volume balance is baked in here (the app's effect volume times each sample's
/// own volume), so the encode side mixes at unity.
///
/// <para>
/// The mix is streamed to disk in blocks rather than held whole — an hour-long render's track is
/// hundreds of megabytes of PCM, which must never sit in memory. Everything except the two process
/// boundaries (ffmpeg decode, embedded-resource read — both injectable) is pure and covered by
/// <c>HitSoundTrackTest</c>.
/// </para>
/// </summary>
public static class HitSoundTrack
{
    /// <summary>The track's fixed sample rate — the rate every sample is decoded to and the WAV is
    /// written at. The encode side pins both amix inputs to it.</summary>
    public const int SAMPLE_RATE = 44100;

    private const int channels = 2;

    /// <summary>The extensions a hitsound file may carry, in the order they are probed.</summary>
    private static readonly string[] audio_extensions = { ".wav", ".mp3", ".ogg" };

    /// <summary>
    /// Where samples resolve from, in gameplay's order: the beatmap's folder (only for samples that
    /// opt into beatmap samples — the same gate <see cref="LazerPlayer.BeatmapFolderSkin"/> applies),
    /// the active skin's folder when an imported skin is in effect, and finally the stock samples —
    /// <paramref name="ReadResource"/> reads an embedded osu.Game.Resources entry (null when absent)
    /// under the <paramref name="DefaultPrefixes"/> the active bundled skin would use.
    /// </summary>
    public sealed record Sources(
        string? BeatmapDirectory,
        string? SkinDirectory,
        IReadOnlyList<string> DefaultPrefixes,
        Func<string, byte[]?> ReadResource);

    /// <summary>
    /// The embedded-resource prefixes a bundled skin resolves its stock samples under, most specific
    /// first — Argon ships its own set layered over the base one, Classic (and the classic fallback
    /// behind every imported skin) uses the legacy set, Triangles the base set.
    /// </summary>
    public static IReadOnlyList<string> DefaultPrefixesFor(JukeBoxSkin skin) => skin switch
    {
        JukeBoxSkin.Argon => new[] { "Samples/Gameplay/Argon/", "Samples/Gameplay/" },
        JukeBoxSkin.ArgonPro => new[] { "Samples/Gameplay/ArgonPro/", "Samples/Gameplay/Argon/", "Samples/Gameplay/" },
        JukeBoxSkin.Triangles => new[] { "Samples/Gameplay/" },
        _ => new[] { "Skins/Legacy/", "Samples/Gameplay/" },
    };

    /// <summary>
    /// The file names one sample may resolve as, in lazer's lookup priority (custom-bank name, plain
    /// bank name, bare name), relative to a folder — the <c>Gameplay/</c> namespace prefix skins
    /// don't use on disk is stripped. Internal for the tests.
    /// </summary>
    internal static IEnumerable<string> CandidateNames(HitSampleInfo sample)
        => sample.LookupNames.Select(n => n.StartsWith("Gameplay/", StringComparison.Ordinal)
            ? n.Substring("Gameplay/".Length)
            : n);

    /// <summary>
    /// The sound file <paramref name="sample"/> resolves to on disk, or null when neither folder has
    /// one: the beatmap folder first for samples that opt into beatmap samples, then the skin folder
    /// — each probed for every candidate name with every known extension.
    /// </summary>
    public static string? ResolveFile(HitSampleInfo sample, string? beatmapDirectory, string? skinDirectory)
    {
        string? fromBeatmap = sample.UseBeatmapSamples ? probe(beatmapDirectory, sample) : null;
        return fromBeatmap ?? probe(skinDirectory, sample);
    }

    private static string? probe(string? directory, HitSampleInfo sample)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return null;

        foreach (string name in CandidateNames(sample))
        {
            // A name that already carries an extension (a storyboard-style filename) is meant
            // literally; bank names get the known extensions probed.
            if (Path.HasExtension(name))
            {
                string exact = Path.Combine(directory, name);
                if (File.Exists(exact))
                    return exact;
            }

            foreach (string extension in audio_extensions)
            {
                string candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The stock sample bytes for <paramref name="sample"/> out of the embedded resources, or null
    /// when no prefix/name/extension combination exists — the last link of the chain, so a plain
    /// hitnormal always resolves somewhere.
    /// </summary>
    public static byte[]? ResolveDefault(HitSampleInfo sample, IReadOnlyList<string> defaultPrefixes, Func<string, byte[]?> readResource)
    {
        foreach (string prefix in defaultPrefixes)
        {
            foreach (string name in CandidateNames(sample))
            {
                if (Path.HasExtension(name))
                {
                    byte[]? exact = readResource(prefix + name);
                    if (exact != null)
                        return exact;
                }

                foreach (string extension in audio_extensions)
                {
                    byte[]? bytes = readResource(prefix + name + extension);
                    if (bytes != null)
                        return bytes;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes one sound file to interleaved stereo float PCM at <see cref="SAMPLE_RATE"/> through
    /// ffmpeg (the formats span wav/mp3/ogg, and ffmpeg is already this feature's hard dependency).
    /// Null when ffmpeg is missing or the file doesn't decode. Hitsounds are sub-second; a sample is
    /// capped at 10 seconds so a mislabelled song file can never balloon the mix.
    /// </summary>
    public static float[]? DecodePcm(string path)
    {
        if (!FfmpegEncoder.IsFfmpegAvailable(out string ffmpegPath))
            return null;

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string arg in new[]
                     {
                         "-v", "error",
                         "-i", path,
                         "-t", "10",
                         "-f", "f32le",
                         "-acodec", "pcm_f32le",
                         "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "-ar", SAMPLE_RATE.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "pipe:1",
                     })
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info);

            if (process == null)
                return null;

            // Drain stderr on a background task so a chatty decode can't deadlock the pipe.
            _ = process.StandardError.ReadToEndAsync();

            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            process.WaitForExit();

            if (process.ExitCode != 0 || buffer.Length < sizeof(float))
                return null;

            byte[] bytes = buffer.GetBuffer();
            var pcm = new float[buffer.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * sizeof(float));
            return pcm;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Mixes the schedule into a 16-bit stereo WAV at <paramref name="path"/> covering exactly
    /// <paramref name="startMs"/>–<paramref name="endMs"/>: each entry's samples (as decoded by
    /// <paramref name="pcm"/> — null skips a sample that resolved nowhere) land at their entry's
    /// time, scaled by <paramref name="gain"/> times the sample's own volume, summed and clamped.
    /// Streamed block-by-block so an hour-long track never sits in memory. False when nothing at
    /// all could be mixed (the caller then renders music-only rather than muxing pure silence).
    /// </summary>
    public static bool MixToWavFile(IReadOnlyList<HitSoundSchedule.Entry> schedule, Func<HitSampleInfo, float[]?> pcm, double startMs, double endMs, double gain, string path)
    {
        long totalFrames = Math.Max(1, (long)Math.Ceiling((endMs - startMs) / 1000.0 * SAMPLE_RATE));

        // One "voice" per (entry, sample): where in the output it starts and at what gain.
        var voices = new List<(long startFrame, float[] data, float gain)>();

        foreach (var entry in schedule)
        {
            foreach (var sample in entry.Samples)
            {
                float[]? data = pcm(sample);

                if (data == null || data.Length < channels)
                    continue;

                long startFrame = (long)Math.Round((entry.TimeMs - startMs) / 1000.0 * SAMPLE_RATE);

                // A sample's stored volume is a percentage; a zero means "unset", which sounds full.
                double sampleVolume = sample.Volume > 0 ? sample.Volume / 100.0 : 1.0;

                voices.Add((startFrame, data, (float)(gain * sampleVolume)));
            }
        }

        if (voices.Count == 0)
            return false;

        voices.Sort((a, b) => a.startFrame.CompareTo(b.startFrame));

        using var stream = File.Create(path);
        writeWavHeader(stream, totalFrames);

        const int block_frames = 1 << 16;
        var mix = new float[block_frames * channels];
        var outBytes = new byte[block_frames * channels * sizeof(short)];
        var active = new List<(long startFrame, float[] data, float gain)>();
        int next = 0;

        for (long blockStart = 0; blockStart < totalFrames; blockStart += block_frames)
        {
            long blockEnd = Math.Min(blockStart + block_frames, totalFrames);
            Array.Clear(mix);

            while (next < voices.Count && voices[next].startFrame < blockEnd)
                active.Add(voices[next++]);

            for (int i = active.Count - 1; i >= 0; i--)
            {
                var (startFrame, data, voiceGain) = active[i];
                long voiceFrames = data.Length / channels;
                long from = Math.Max(startFrame, blockStart);
                long to = Math.Min(startFrame + voiceFrames, blockEnd);

                for (long frame = from; frame < to; frame++)
                {
                    int o = (int)(frame - blockStart) * channels;
                    int s = (int)(frame - startFrame) * channels;
                    mix[o] += data[s] * voiceGain;
                    mix[o + 1] += data[s + 1] * voiceGain;
                }

                if (startFrame + voiceFrames <= blockEnd)
                    active.RemoveAt(i);
            }

            int blockSamples = (int)(blockEnd - blockStart) * channels;

            for (int n = 0; n < blockSamples; n++)
            {
                short value = (short)Math.Clamp((int)Math.Round(mix[n] * short.MaxValue), short.MinValue, short.MaxValue);
                outBytes[2 * n] = (byte)value;
                outBytes[2 * n + 1] = (byte)(value >> 8);
            }

            stream.Write(outBytes, 0, blockSamples * sizeof(short));
        }

        return true;
    }

    private static void writeWavHeader(Stream stream, long totalFrames)
    {
        long dataBytes = totalFrames * channels * sizeof(short);

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + dataBytes));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1); // PCM
        writer.Write((ushort)channels);
        writer.Write((uint)SAMPLE_RATE);
        writer.Write((uint)(SAMPLE_RATE * channels * sizeof(short)));
        writer.Write((ushort)(channels * sizeof(short)));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write((uint)dataBytes);
    }

    /// <summary>
    /// The whole track, end to end: judge the replay (or take the beatmap's autoplay schedule),
    /// resolve and decode every distinct sample once, mix, and hand back the temp WAV's path — or
    /// null when there is nothing to mix (no ffmpeg, no objects in range, nothing resolvable), which
    /// the caller reads as "render music-only, exactly as before". The caller owns deleting the file
    /// once the encode is done with it.
    /// </summary>
    public static string? BuildTrackFile(string osuFile, Score? replayScore, RenderRequest request, Sources sources, double effectVolume)
    {
        if (!FfmpegEncoder.IsFfmpegAvailable(out _))
            return null;

        var working = new FlatWorkingBeatmap(osuFile);
        var ruleset = working.BeatmapInfo.Ruleset.CreateInstance();
        var mods = ReplayMods.ForGameplay(replayScore);
        var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo, mods);

        // A replay sounds what it HIT; without one (or on a ruleset the analytic judge doesn't
        // cover) every object sounds — the same fallback live playback makes.
        var frames = replayScore?.Replay?.Frames;

        var schedule = frames is { Count: > 0 } && AnalyticOsuJudge.Supports(ruleset)
            ? HitSoundSchedule.ForJudgements(playable, AnalyticOsuJudge.Evaluate(playable, frames), request.StartMs, request.EndMs)
            : HitSoundSchedule.ForAutoplay(playable, request.StartMs, request.EndMs);

        if (schedule.Count == 0)
            return null;

        // Decode each distinct sample once, keyed by its lookup chain (two samples with the same
        // names resolve identically); a temp file bridges embedded bytes to ffmpeg's file input.
        var cache = new Dictionary<string, float[]?>();
        var tempSampleFiles = new List<string>();

        float[]? decode(HitSampleInfo sample)
        {
            string key = string.Join('|', sample.LookupNames);

            if (cache.TryGetValue(key, out float[]? cached))
                return cached;

            float[]? pcm = null;

            string? file = ResolveFile(sample, sources.BeatmapDirectory, sources.SkinDirectory);

            if (file == null)
            {
                byte[]? stock = ResolveDefault(sample, sources.DefaultPrefixes, sources.ReadResource);

                if (stock != null)
                {
                    file = Path.Combine(Path.GetTempPath(), $"jukebox-sample-{Guid.NewGuid():N}");
                    File.WriteAllBytes(file, stock);
                    tempSampleFiles.Add(file);
                }
            }

            if (file != null)
                pcm = DecodePcm(file);

            cache[key] = pcm;
            return pcm;
        }

        string path = Path.Combine(Path.GetTempPath(), $"jukebox-hitsounds-{Guid.NewGuid():N}.wav");

        try
        {
            if (MixToWavFile(schedule, decode, request.StartMs, request.EndMs, effectVolume, path))
                return path;

            deleteQuietly(path);
            return null;
        }
        catch
        {
            deleteQuietly(path);
            throw;
        }
        finally
        {
            foreach (string file in tempSampleFiles)
                deleteQuietly(file);
        }
    }

    private static void deleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A temp file we can't remove is not worth failing a render over.
        }
    }
}
