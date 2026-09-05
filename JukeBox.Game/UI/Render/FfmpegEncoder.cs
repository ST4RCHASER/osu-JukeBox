#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// Locates an ffmpeg binary and drives it to encode a render: raw RGBA frames pushed in over stdin,
/// the song's audio muxed in over the chosen range, out to the target container at the chosen codec
/// and bitrate.
///
/// <para>
/// The argument list is built by the pure, side-effect-free <see cref="BuildArgs"/> so the exact
/// codec / container / size / fps / bitrate / audio-mux flags are asserted in unit tests without any
/// ffmpeg process ever running. The <see cref="Process"/> plumbing that actually feeds frames is
/// kept deliberately thin around it.
/// </para>
/// </summary>
public sealed class FfmpegEncoder : IDisposable
{
    /// <summary>
    /// Finds a usable ffmpeg the same way on every OS: the user's own <c>PATH</c> first (their
    /// install wins), then a copy shipped next to the app, then the OS's usual install spots
    /// (<see cref="CandidateLocations"/> — a Finder-launched mac app has no Homebrew on its PATH).
    /// Returns false only when nothing is found, so the dialog can say "install ffmpeg" rather than
    /// failing mid-render.
    /// </summary>
    public static bool IsFfmpegAvailable(out string path)
    {
        foreach (string name in ExecutableNames(RuntimeInfo.OS))
        {
            if (existsOnPath(name, out string? resolved))
            {
                path = resolved!;
                return true;
            }
        }

        foreach (string candidate in CandidateLocations(RuntimeInfo.OS, AppContext.BaseDirectory))
        {
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    /// <summary>The binary names ffmpeg goes by on <paramref name="platform"/> — <c>ffmpeg.exe</c>
    /// (then a bare <c>ffmpeg</c>) on Windows, <c>ffmpeg</c> elsewhere. Internal for the tests.</summary>
    internal static IReadOnlyList<string> ExecutableNames(RuntimeInfo.Platform platform)
        => platform == RuntimeInfo.Platform.Windows
            ? new[] { "ffmpeg.exe", "ffmpeg" }
            : new[] { "ffmpeg" };

    /// <summary>
    /// The on-disk locations probed after <c>PATH</c>, in order: next to the app (a bundled copy on
    /// any OS), then the platform's usual installs — Homebrew's two prefixes and the system bin on
    /// macOS, the distro and local bins on Linux, nothing further on Windows (installs there live
    /// on PATH or beside the app). Pure and internal for the tests.
    /// </summary>
    internal static IEnumerable<string> CandidateLocations(RuntimeInfo.Platform platform, string baseDirectory)
    {
        foreach (string name in ExecutableNames(platform))
            yield return Path.Combine(baseDirectory, name);

        switch (platform)
        {
            case RuntimeInfo.Platform.macOS:
                yield return "/opt/homebrew/bin/ffmpeg";
                yield return "/usr/local/bin/ffmpeg";
                yield return "/usr/bin/ffmpeg";
                break;

            case RuntimeInfo.Platform.Linux:
                yield return "/usr/bin/ffmpeg";
                yield return "/usr/local/bin/ffmpeg";
                break;
        }
    }

    private static bool existsOnPath(string executable, out string? resolved)
    {
        resolved = null;

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return false;

        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            string full = Path.Combine(dir.Trim(), executable);
            if (File.Exists(full))
            {
                resolved = full;
                return true;
            }
        }

        return false;
    }

    /// <summary>The video codec, audio codec and file extension a container maps to. VP9/Opus in
    /// WebM, H.264/AAC in the two MP4-family containers.</summary>
    private static (string videoCodec, string audioCodec, string extension) codecsFor(string format) =>
        format.ToLowerInvariant() switch
        {
            "webm" => ("libvpx-vp9", "libopus", "webm"),
            "mov" => ("libx264", "aac", "mov"),
            _ => ("libx264", "aac", "mp4"),
        };

    /// <summary>The file extension the chosen container writes — used to correct a save path whose
    /// extension doesn't match the selected format.</summary>
    public static string ExtensionFor(string format) => codecsFor(format).extension;

    /// <summary>
    /// Builds the full ffmpeg argument vector (as an argv array, so no value is ever shell-quoted or
    /// re-split) for one render. Video is a raw RGBA frame stream on stdin at the request's exact
    /// size and fps; audio is <paramref name="audioPath"/> seeked to the render's start, or a
    /// generated silent track when there is no audio file. When <paramref name="hitSoundPath"/> is
    /// given (a WAV already covering exactly the render range — see <see cref="HitSoundTrack"/>),
    /// it is mixed over the song into the one output audio stream. The output is limited to the
    /// render's duration so a longer song is cut to the chosen range.
    /// </summary>
    public static string[] BuildArgs(RenderRequest request, string? audioPath, string? hitSoundPath = null)
    {
        var (videoCodec, audioCodec, _) = codecsFor(request.Format);

        double startSeconds = request.StartMs / 1000.0;
        double durationSeconds = request.DurationMs / 1000.0;

        var args = new List<string>
        {
            // Overwrite the output without the interactive "file exists" prompt — the dialog already
            // owns overwrite intent.
            "-y",

            // Input 0: raw RGBA frames on stdin, at the exact render size and rate.
            "-f", "rawvideo",
            "-pixel_format", "rgba",
            "-video_size", $"{request.Width}x{request.Height}",
            "-framerate", request.Fps.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
        };

        bool hasAudio = !string.IsNullOrEmpty(audioPath);

        if (hasAudio)
        {
            // Input 1: the song, fast-seeked to the render's start so audio and video line up at
            // frame 0. -ss BEFORE -i is the fast (keyframe) seek.
            args.Add("-ss");
            args.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add(audioPath!);
        }
        else
        {
            // No audio file (a virtual-audio / silent set): synthesise a silent stereo track so the
            // container still gets a well-formed audio stream.
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add("anullsrc=channel_layout=stereo:sample_rate=44100");
        }

        bool hasHitSounds = !string.IsNullOrEmpty(hitSoundPath);

        if (hasHitSounds)
        {
            // Input 2: the pre-mixed hitsound track. It covers exactly the render range (built that
            // way — see HitSoundTrack), so unlike the song it is not seeked.
            args.Add("-i");
            args.Add(hitSoundPath!);
        }

        // Take video from the frame stream and audio explicitly, so ffmpeg's automatic stream
        // selection can't pick a stray stream.
        args.Add("-map");
        args.Add("0:v:0");

        if (hasHitSounds)
        {
            // Sum the song and the hitsound track into the one output stream — a STRAIGHT sum,
            // exactly like live playback's float mixer. Both are pinned to a single format first so
            // amix never has to reconcile a mono song against the stereo track; normalize=0 and
            // dropout_transition=0 pin amix to plain summation with no adaptive gain of any kind
            // (its defaults rescale as inputs come and go — audible as the music ducking under the
            // hitsounds). duration=longest so hitsounds past a short song's end still sound
            // (-t bounds the range either way).
            //
            // The constant volume=0.5 is HEADROOM, not balance: a full-scale song plus a full-scale
            // hitsound sums to 2.0, and everything above 1.0 is clamped by whatever plays the file —
            // which shaved the music at every loud hit (a real render measured +5.7 dBFS peaks).
            // Halving the sum bounds the worst case at exactly full scale, keeps the music/effect
            // balance untouched, and never varies — live playback has the same fixed headroom in the
            // master volume sitting ahead of the DAC.
            args.Add("-filter_complex");
            args.Add("[1:a]aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[mus];"
                     + "[2:a]aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[hit];"
                     + "[mus][hit]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,volume=0.5[mix]");
            args.Add("-map");
            args.Add("[mix]");
        }
        else
        {
            args.Add("-map");
            args.Add("1:a:0");
        }

        // Video: chosen codec, 4:2:0 for player compatibility, a fixed visually-lossless-ish quality.
        args.Add("-c:v");
        args.Add(videoCodec);
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        args.Add("-crf");
        args.Add(videoCodec == "libvpx-vp9" ? "30" : "18");

        if (videoCodec == "libvpx-vp9")
        {
            // VP9 needs -b:v 0 for constant-quality (CRF-only) mode.
            args.Add("-b:v");
            args.Add("0");
        }

        // Audio: chosen codec at the requested bitrate.
        args.Add("-c:a");
        args.Add(audioCodec);
        args.Add("-b:a");
        args.Add($"{request.AudioBitrateKbps}k");

        // Bound the output to the render range — a song longer than end-start is cut here.
        args.Add("-t");
        args.Add(durationSeconds.ToString("0.###", CultureInfo.InvariantCulture));

        args.Add(request.Path);

        return args.ToArray();
    }

    private readonly Process process;
    private readonly Stream input;

    /// <summary>The stderr ffmpeg produced, kept for a failure message. Drained on a background task
    /// so a full pipe can never block the encoder.</summary>
    private readonly Task<string> stderr;

    private FfmpegEncoder(Process process)
    {
        this.process = process;
        input = process.StandardInput.BaseStream;
        stderr = process.StandardError.ReadToEndAsync();
    }

    /// <summary>
    /// Starts ffmpeg for the given request, returning an encoder whose <see cref="WriteFrameAsync"/>
    /// accepts one RGBA frame at a time. Throws with a clear message when no ffmpeg binary is found
    /// — never silently produces nothing.
    /// </summary>
    public static FfmpegEncoder Start(RenderRequest request, string? audioPath, string? hitSoundPath = null)
    {
        if (!IsFfmpegAvailable(out string ffmpegPath))
            throw new InvalidOperationException("ffmpeg was not found. Install it (e.g. `brew install ffmpeg`) and try again.");

        var info = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in BuildArgs(request, audioPath, hitSoundPath))
            info.ArgumentList.Add(arg);

        var process = new Process { StartInfo = info };

        if (!process.Start())
            throw new InvalidOperationException("ffmpeg failed to start.");

        // stdout carries nothing useful (encoding goes to the file); drain it so it can't back up.
        _ = process.StandardOutput.ReadToEndAsync();

        return new FfmpegEncoder(process);
    }

    /// <summary>Pushes one frame's raw RGBA bytes (width*height*4, top-to-bottom) into ffmpeg.</summary>
    public async Task WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        => await input.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Closes stdin and waits for ffmpeg to finish flushing the file. Returns true on a clean exit;
    /// on a non-zero exit the ffmpeg stderr is available via <see cref="LastError"/>.
    /// </summary>
    public async Task<bool> CompleteAsync(CancellationToken cancellationToken)
    {
        input.Close();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        LastError = await stderr.ConfigureAwait(false);
        return process.ExitCode == 0;
    }

    /// <summary>Kills ffmpeg mid-render (the Cancel path) — the half-written output is deleted by the
    /// caller, not here.</summary>
    public void Abort()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone / never fully started — nothing to kill.
        }
    }

    /// <summary>ffmpeg's stderr from the last <see cref="CompleteAsync"/>, for a failure message.</summary>
    public string? LastError { get; private set; }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort.
        }

        process.Dispose();
    }
}
