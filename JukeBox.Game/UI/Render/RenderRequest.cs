#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// The fully-validated parameters of one render — the value the dialog hands off once every field
/// has passed validation, and the input <see cref="FfmpegEncoder.BuildArgs"/> and
/// <see cref="OfflineRenderer"/> both read. Every field is already a clean, in-range value here:
/// parsing and range-checking live entirely in <see cref="RenderValidation"/>, so nothing
/// downstream re-parses a string or re-checks a bound.
/// </summary>
public sealed record RenderRequest(
    string Format,
    int Width,
    int Height,
    int Fps,
    string Path,
    double StartMs,
    double EndMs,
    int AudioBitrateKbps)
{
    /// <summary>Duration of the rendered range, always positive (end &gt; start is enforced on build).</summary>
    public double DurationMs => EndMs - StartMs;

    /// <summary>Total frames this render produces at its fps over its range — the denominator the
    /// progress bar and ETA are measured against. At least one frame for any non-empty range.</summary>
    public int TotalFrames => Math.Max(1, (int)Math.Round(DurationMs / 1000.0 * Fps));

    /// <summary>Milliseconds between consecutive frames — the fixed step the frame clock advances by.</summary>
    public double FrameStepMs => 1000.0 / Fps;
}

/// <summary>Which field an error belongs to, so the dialog can pin each message under its own input.</summary>
public enum RenderField
{
    Format,
    Resolution,
    Fps,
    Path,
    StartTime,
    EndTime,
    AudioBitrate,
}

/// <summary>The raw, still-unparsed contents of the dialog's fields — what validation turns into a
/// <see cref="RenderRequest"/> or a set of per-field errors. Strings (not ints) on purpose: a
/// half-typed "19x" resolution or "1:2:" timecode is a validation FAILURE to report, not something
/// that should have been made un-typeable.</summary>
public sealed record RenderFormValues(
    string Format,
    string Resolution,
    string Fps,
    string Path,
    string StartTime,
    string EndTime,
    string AudioBitrate);

/// <summary>The outcome of validating a <see cref="RenderFormValues"/>: either a clean
/// <see cref="Request"/> (with no errors), or a per-field <see cref="Errors"/> map and a null
/// request. Never both.</summary>
public sealed class RenderValidationResult
{
    public IReadOnlyDictionary<RenderField, string> Errors { get; }

    public RenderRequest? Request { get; }

    public bool IsValid => Request != null;

    public RenderValidationResult(RenderRequest request)
    {
        Request = request;
        Errors = new Dictionary<RenderField, string>();
    }

    public RenderValidationResult(IReadOnlyDictionary<RenderField, string> errors)
    {
        Errors = errors;
        Request = null;
    }
}

/// <summary>
/// Pure validation for the Render dialog: parse and range-check every field, report a message for
/// each one that fails, and only build a <see cref="RenderRequest"/> when they ALL pass. No
/// drawables and no I/O beyond inspecting the path string, so every rule below is exercised directly
/// by <c>RenderValidationTest</c> rather than through the dialog.
/// </summary>
public static class RenderValidation
{
    /// <summary>The container formats offered, each mapped to its file extension by
    /// <see cref="FfmpegEncoder"/>. Lower-case; the dropdown only ever supplies one of these.</summary>
    public static readonly IReadOnlyList<string> Formats = new[] { "mp4", "webm", "mov" };

    // Sane encoder bounds. Dimensions are capped at 8K and required EVEN because the H.264/VP9
    // 4:2:0 chroma subsampling every target codec uses cannot encode an odd width or height.
    private const int min_dimension = 16;
    private const int max_dimension = 7680;
    private const int min_fps = 1;
    private const int max_fps = 240;
    private const int min_audio_kbps = 32;
    private const int max_audio_kbps = 512;

    public static RenderValidationResult Validate(RenderFormValues values, double songLengthMs)
    {
        var errors = new Dictionary<RenderField, string>();

        string format = (values.Format ?? string.Empty).Trim().ToLowerInvariant();
        if (!Formats.Contains(format))
            errors[RenderField.Format] = "choose mp4, webm or mov";

        (int width, int height) resolution = default;
        if (!tryParseResolution(values.Resolution, out resolution, out string? resolutionError))
            errors[RenderField.Resolution] = resolutionError!;

        int fps = 0;
        if (!tryParsePositiveInt(values.Fps, min_fps, max_fps, out fps))
            errors[RenderField.Fps] = $"whole number between {min_fps} and {max_fps}";

        if (string.IsNullOrWhiteSpace(values.Path))
            errors[RenderField.Path] = "choose where to save the file";
        else if (string.IsNullOrEmpty(Path.GetFileName(values.Path.Trim())))
            errors[RenderField.Path] = "the path needs a file name, not just a folder";

        int audioBitrate = 0;
        if (!tryParsePositiveInt(values.AudioBitrate, min_audio_kbps, max_audio_kbps, out audioBitrate))
            errors[RenderField.AudioBitrate] = $"whole number of kbps between {min_audio_kbps} and {max_audio_kbps}";

        bool startOk = tryParseTimecode(values.StartTime, out double startMs);
        if (!startOk)
            errors[RenderField.StartTime] = "time as hh:mm:ss";

        bool endOk = tryParseTimecode(values.EndTime, out double endMs);
        if (!endOk)
            errors[RenderField.EndTime] = "time as hh:mm:ss";

        // Range checks only once both times parsed — otherwise "end before start" would fire on a
        // half-typed field and drown out the real "that isn't a time" message.
        if (startOk && endOk)
        {
            if (startMs < 0)
                errors[RenderField.StartTime] = "cannot start before 0:00:00";

            // A song length of 0 means "unknown" (no track loaded in a bare test) — skip the
            // within-song bound rather than reject every time against a zero length.
            if (songLengthMs > 0 && endMs > songLengthMs)
                errors[RenderField.EndTime] = $"the song is only {FormatTimecode(songLengthMs)} long";

            if (endMs <= startMs)
                errors[RenderField.EndTime] = "end must be after start";
        }

        if (errors.Count > 0)
            return new RenderValidationResult(errors);

        return new RenderValidationResult(new RenderRequest(
            format,
            resolution.width,
            resolution.height,
            fps,
            values.Path.Trim(),
            startMs,
            endMs,
            audioBitrate));
    }

    private static bool tryParseResolution(string? text, out (int width, int height) resolution, out string? error)
    {
        resolution = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "size as WIDTHxHEIGHT, e.g. 1920x1080";
            return false;
        }

        // Accept the 'x' separator in either case, and tolerate surrounding spaces around it.
        string[] parts = text.Trim().Split(new[] { 'x', 'X', '×' }, StringSplitOptions.None);

        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int height))
        {
            error = "size as WIDTHxHEIGHT, e.g. 1920x1080";
            return false;
        }

        if (width < min_dimension || height < min_dimension || width > max_dimension || height > max_dimension)
        {
            error = $"each side must be {min_dimension}–{max_dimension} pixels";
            return false;
        }

        if (width % 2 != 0 || height % 2 != 0)
        {
            error = "width and height must both be even";
            return false;
        }

        resolution = (width, height);
        return true;
    }

    private static bool tryParsePositiveInt(string? text, int min, int max, out int value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(text)
            || !int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            return false;

        if (parsed < min || parsed > max)
            return false;

        value = parsed;
        return true;
    }

    /// <summary>
    /// Parses <c>hh:mm:ss</c> (or <c>mm:ss</c>, or a bare seconds count) to milliseconds. Minutes and
    /// seconds must be two digits and under 60; anything else is a parse failure the caller reports
    /// as "not a time". Fractional seconds after a dot are accepted so an end time can land mid-second.
    /// </summary>
    public static bool tryParseTimecode(string? text, out double ms)
    {
        ms = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Trim().Split(':');

        if (parts.Length is < 1 or > 3)
            return false;

        double hours = 0, minutes = 0, seconds;

        // The seconds component is always the last part and may carry a fraction.
        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds))
            return false;

        if (seconds < 0)
            return false;

        // A single bare number is a total-seconds shorthand ("90" = 1:30), so it may exceed 60; only
        // once minutes/hours are present must the seconds component wrap under 60.
        if (parts.Length >= 2 && seconds >= 60)
            return false;

        if (parts.Length >= 2)
        {
            if (!double.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
                || minutes < 0 || minutes >= 60)
                return false;
        }

        if (parts.Length == 3)
        {
            if (!double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)
                || hours < 0)
                return false;
        }

        ms = ((hours * 60 + minutes) * 60 + seconds) * 1000.0;
        return true;
    }

    /// <summary>Renders milliseconds back to <c>h:mm:ss</c> for the dialog's default End field and
    /// the "song is only … long" message. The inverse of <see cref="tryParseTimecode"/>.</summary>
    public static string FormatTimecode(double ms)
    {
        if (ms < 0)
            ms = 0;

        var span = TimeSpan.FromMilliseconds(ms);
        return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
    }
}
