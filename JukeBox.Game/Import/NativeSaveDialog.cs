#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using osu.Framework;
using osu.Framework.Logging;

namespace JukeBox.Game.Import;

/// <summary>
/// The operating system's own "save file" panel — what the Render dialog's Browse… drives to pick a
/// save location. osu!framework exposes no native SAVE dialog at all (its system file SELECTOR is
/// an open-style picker that never presents on macOS), so on macOS this drives the standard
/// NSSavePanel through <c>osascript</c> (<c>choose file name</c>) — the same proven route
/// <see cref="NativeOpenDialog"/> takes for File → Open…. The user names the file in the panel; the
/// answer comes back as one POSIX path. Elsewhere <see cref="IsAvailable"/> is false and the caller
/// falls back to whatever it has (for Browse…, the framework selector, then the text field).
/// </summary>
public static class NativeSaveDialog
{
    /// <summary>Whether this platform has a native save panel to offer. macOS only for now.</summary>
    public static bool IsAvailable => RuntimeInfo.IsApple;

    /// <summary>
    /// Shows the panel and returns the chosen path — null when the user cancelled. Never throws for
    /// a cancel; a genuinely failed launch is logged and also reads as "nothing chosen".
    /// </summary>
    public static async Task<string?> PickSaveAsync(string? initialDirectory, string? defaultFileName)
    {
        if (!IsAvailable)
            return null;

        try
        {
            var start = new ProcessStartInfo("osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (string line in Script(initialDirectory, defaultFileName))
            {
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add(line);
            }

            using var process = Process.Start(start);

            if (process == null)
                return null;

            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            // A cancelled panel is osascript exiting non-zero with "User canceled" on stderr — the
            // normal way out, not an error worth a toast.
            return process.ExitCode == 0 ? ParseOutput(output) : null;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[render] the native save dialog could not be shown");
            return null;
        }
    }

    /// <summary>The AppleScript, one statement per <c>-e</c>: the save panel (seeded with the
    /// current file name and folder where they exist), then the chosen location as a POSIX path.
    /// Internal for the tests.</summary>
    internal static IEnumerable<string> Script(string? initialDirectory, string? defaultFileName)
    {
        string choose = "set chosen to choose file name with prompt \"Choose where to save the rendered video\"";

        if (!string.IsNullOrEmpty(defaultFileName))
            choose += $" default name \"{escape(defaultFileName)}\"";

        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            choose += $" default location POSIX file \"{escape(initialDirectory)}\"";

        yield return choose;
        yield return "return POSIX path of chosen";
    }

    private static string escape(string value) => value.Replace("\"", "\\\"");

    /// <summary>The panel's one-line answer as a path, or null when it is blank. Internal for the
    /// tests.</summary>
    internal static string? ParseOutput(string output)
    {
        string trimmed = output.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
