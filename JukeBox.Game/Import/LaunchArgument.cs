#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using JukeBox.Game.Online;

namespace JukeBox.Game.Import;

/// <summary>What one command-line argument turned out to point at — see
/// <see cref="LaunchArgument.Classify"/>.</summary>
public enum LaunchArgumentKind
{
    /// <summary>Nothing this app can act on. Reported per-argument rather than silently dropped.</summary>
    Unsupported,

    /// <summary>A beatmapset, by id. <see cref="LaunchArgument.DifficultyId"/> may name one of its
    /// difficulties when the argument was a deep link.</summary>
    BeatmapSet,

    /// <summary>A single difficulty whose SET is not yet known. Resolving one needs osu!'s own API
    /// (see <see cref="BeatmapLinkKind.Beatmap"/>); the mirrors cannot do it.</summary>
    Beatmap,

    /// <summary>An http(s) URL that is not an osu! beatmap link — downloaded, then classified by
    /// what actually arrives.</summary>
    RemoteFile,

    /// <summary>A path on this machine, carrying one of the importable extensions.</summary>
    LocalFile,
}

/// <summary>
/// One command-line argument, classified. Pure and I/O-free — it never touches the disk or the
/// network, so a path that does not exist still classifies as <see cref="LaunchArgumentKind.LocalFile"/>
/// and fails later with "no such file" rather than the much less helpful "unsupported argument".
///
/// <para>
/// Classification is per-argument and independent: one unrecognised argument says so and the rest
/// still run (see <c>LaunchArgumentImporter</c>).
/// </para>
/// </summary>
/// <param name="Kind">What this argument is.</param>
/// <param name="Raw">The argument exactly as it was typed, for error messages.</param>
/// <param name="Id">Beatmapset or beatmap id, by <paramref name="Kind"/>; 0 otherwise.</param>
/// <param name="DifficultyId">
/// The difficulty a set link deep-linked to (<c>…/beatmapsets/1#osu/2</c>), or 0. Only ever set
/// alongside <see cref="LaunchArgumentKind.BeatmapSet"/> — it is a preference, not an identity:
/// the set is what gets queued either way.
/// </param>
/// <param name="Path">Local path or remote URL, by <paramref name="Kind"/>; empty otherwise.</param>
public readonly record struct LaunchArgument(LaunchArgumentKind Kind, string Raw, int Id, int DifficultyId, string Path)
{
    /// <summary>The difficulty named by a deep link, e.g. the <c>67890</c> of <c>#osu/67890</c>.
    /// Anchored on the fragment so a set path can never match it.</summary>
    private static readonly Regex deep_link_difficulty = new Regex(@"#[a-z]*/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extensions this app can import, and the only ones a bare path is accepted for. Matches
    /// <see cref="DroppedFile.Classify"/>'s set exactly — a path arriving by command line and the
    /// same path arriving by drag-and-drop must mean the same thing.
    /// </summary>
    private static readonly string[] importable_extensions = { ".osz", ".osk", ".osr" };

    public static LaunchArgument Unsupported(string raw) => new LaunchArgument(LaunchArgumentKind.Unsupported, raw, 0, 0, string.Empty);

    /// <summary>
    /// True for the switches the app defines for itself (<c>--viewer</c>), which are not content
    /// and must never be classified as arguments to queue. Callers filter these out first.
    /// </summary>
    public static bool IsSwitch(string argument) => argument.StartsWith('-');

    public static LaunchArgument Classify(string? argument)
    {
        string raw = argument?.Trim() ?? string.Empty;

        if (raw.Length == 0 || IsSwitch(raw))
            return Unsupported(raw);

        // file:// first: it is a URL by syntax but a local path by meaning, and Uri is the only
        // thing that decodes its percent-escapes correctly (a path with spaces arrives as %20).
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.IsFile)
            return local(raw, uri.LocalPath);

        if (isWeb(uri))
        {
            // An osu! page is a reference to content, not a file to fetch. Everything else on the
            // web is something to download and then look at.
            var link = BeatmapLink.Parse(raw);

            switch (link.Kind)
            {
                case BeatmapLinkKind.BeatmapSet:
                    return new LaunchArgument(LaunchArgumentKind.BeatmapSet, raw, link.Id, difficultyIn(raw), string.Empty);

                case BeatmapLinkKind.Beatmap:
                    return new LaunchArgument(LaunchArgumentKind.Beatmap, raw, link.Id, 0, string.Empty);
            }

            return new LaunchArgument(LaunchArgumentKind.RemoteFile, raw, 0, 0, raw);
        }

        // A bare number is a beatmapset id, the same reading the map-ID dialog gives it.
        if (int.TryParse(raw, out int bare))
            return bare > 0 ? new LaunchArgument(LaunchArgumentKind.BeatmapSet, raw, bare, 0, string.Empty) : Unsupported(raw);

        return local(raw, raw);
    }

    /// <summary>
    /// A path is only an argument if it carries an extension this app can actually import. Left
    /// deliberately strict: without it, any typo or stray word becomes a "file" and the user is
    /// told a file is missing rather than that their argument made no sense.
    /// </summary>
    private static LaunchArgument local(string raw, string path)
        => hasImportableExtension(path)
            ? new LaunchArgument(LaunchArgumentKind.LocalFile, raw, 0, 0, path)
            : Unsupported(raw);

    private static bool hasImportableExtension(string path)
    {
        string extension = System.IO.Path.GetExtension(path);

        foreach (string importable in importable_extensions)
        {
            if (extension.Equals(importable, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool isWeb(Uri? uri)
        => uri != null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static int difficultyIn(string raw)
    {
        var match = deep_link_difficulty.Match(raw);
        return match.Success && int.TryParse(match.Groups[1].Value, out int id) && id > 0 ? id : 0;
    }
}
