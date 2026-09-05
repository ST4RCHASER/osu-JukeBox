#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework;
using osu.Framework.Logging;

namespace JukeBox.Game.Import;

/// <summary>
/// The operating system's own "open files" dialog, with MULTI-SELECT — what File → Open… uses
/// where one is available. osu!framework provides no native picker of its own, so on macOS this
/// drives the system panel through <c>osascript</c> (<c>choose file … with multiple selections
/// allowed</c>), which is the standard NSOpenPanel the user already knows: any mix of .osz / .osk /
/// .osr, several at once, Cmd-click and Shift-click included. Elsewhere <see cref="IsAvailable"/>
/// is false and the caller falls back to the in-app picker.
///
/// <para>
/// The paths come back in the order the panel returns them and go through the SAME importer as a
/// drop (<see cref="DroppedFileImporter.ImportMany"/>), so replays imported alongside their map
/// still get the replays-first / difficulty-lock treatment a drop gets.
/// </para>
/// </summary>
public static class NativeOpenDialog
{
    /// <summary>Whether this platform has a native dialog to offer. macOS only for now.</summary>
    public static bool IsAvailable => RuntimeInfo.IsApple;

    /// <summary>
    /// Shows the dialog and returns the chosen files — empty when the user cancelled. Never throws
    /// for a cancel; a genuinely failed launch is logged and also reads as "nothing chosen".
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(string? initialDirectory)
    {
        if (!IsAvailable)
            return Array.Empty<string>();

        try
        {
            var start = new ProcessStartInfo("osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (string line in Script(initialDirectory))
            {
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add(line);
            }

            using var process = Process.Start(start);

            if (process == null)
                return Array.Empty<string>();

            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            // A cancelled panel is osascript exiting non-zero with "User canceled" on stderr — the
            // normal way out, not an error worth a toast.
            return process.ExitCode == 0 ? ParseOutput(output) : Array.Empty<string>();
        }
        catch (Exception e)
        {
            Logger.Error(e, "[open] the native file dialog could not be shown");
            return Array.Empty<string>();
        }
    }

    /// <summary>The AppleScript, one statement per <c>-e</c>: the multi-select panel, then every
    /// chosen file as a POSIX path on its own line. Internal for the tests.</summary>
    internal static IEnumerable<string> Script(string? initialDirectory)
    {
        string choose = "set chosen to choose file with prompt \"Open beatmaps (.osz), skins (.osk) or replays (.osr)\" with multiple selections allowed";

        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            choose += $" default location POSIX file \"{initialDirectory.Replace("\"", "\\\"")}\"";

        yield return choose;
        yield return "set output to \"\"";
        yield return "repeat with f in chosen";
        yield return "set output to output & POSIX path of f & linefeed";
        yield return "end repeat";
        yield return "return output";
    }

    /// <summary>One path per line, blanks dropped, in the panel's order. Internal for the tests.</summary>
    internal static IReadOnlyList<string> ParseOutput(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(p => p.Length > 0)
                 .ToList();
}
