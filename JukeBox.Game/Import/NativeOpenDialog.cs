#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework;

namespace JukeBox.Game.Import;

/// <summary>
/// The operating system's own "open files" dialog, with MULTI-SELECT — what File → Open… uses
/// where one is available. osu!framework provides no native picker on any desktop OS
/// (<c>GameHost.CreateSystemFileSelector</c> is unimplemented there), so each platform drives its
/// own: macOS the standard NSOpenPanel through <c>osascript</c> (<c>choose file … with multiple
/// selections allowed</c>), Windows the stock WinForms OpenFileDialog through PowerShell, Linux
/// <c>zenity</c> or <c>kdialog</c> — whichever the desktop has. Any mix of .osz / .osk / .osr,
/// several at once. A Linux box with neither tool reports <see cref="IsAvailable"/> false and the
/// caller falls back to the in-app picker.
///
/// <para>
/// The paths come back in the order the panel returns them and go through the SAME importer as a
/// drop (<see cref="DroppedFileImporter.ImportMany"/>), so replays imported alongside their map
/// still get the replays-first / difficulty-lock treatment a drop gets.
/// </para>
/// </summary>
public static class NativeOpenDialog
{
    /// <summary>Whether this platform has a native dialog to offer: macOS and Windows always, Linux
    /// when a dialog tool (zenity/kdialog) is installed.</summary>
    public static bool IsAvailable => RuntimeInfo.OS switch
    {
        RuntimeInfo.Platform.macOS => true,
        RuntimeInfo.Platform.Windows => true,
        RuntimeInfo.Platform.Linux => NativeFileDialogs.LinuxDialogTool() != null,
        _ => false,
    };

    /// <summary>
    /// Shows the dialog and returns the chosen files — empty when the user cancelled. Never throws
    /// for a cancel; a genuinely failed launch is logged and also reads as "nothing chosen".
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(string? initialDirectory)
    {
        var command = BuildCommand(RuntimeInfo.OS, initialDirectory, RuntimeInfo.OS == RuntimeInfo.Platform.Linux ? NativeFileDialogs.LinuxDialogTool() : null);

        if (command == null)
            return Array.Empty<string>();

        string output = await NativeFileDialogs.RunAsync(command, "[open]").ConfigureAwait(false);
        return ParseOutput(output);
    }

    /// <summary>
    /// The exact process invocation for one platform's multi-select panel — every tool prints the
    /// chosen files one POSIX/OS path per line, so <see cref="ParseOutput"/> is shared. Pure and
    /// internal so the tests exercise every platform's argv from any machine. Null when the
    /// platform has no dialog (a Linux desktop with no <paramref name="linuxTool"/>).
    /// </summary>
    internal static NativeFileDialogs.Command? BuildCommand(RuntimeInfo.Platform platform, string? initialDirectory, string? linuxTool)
    {
        bool haveDirectory = !string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory);

        switch (platform)
        {
            case RuntimeInfo.Platform.macOS:
            {
                var arguments = new List<string>();

                foreach (string line in Script(initialDirectory))
                {
                    arguments.Add("-e");
                    arguments.Add(line);
                }

                return new NativeFileDialogs.Command("osascript", arguments);
            }

            case RuntimeInfo.Platform.Windows:
            {
                // The stock WinForms dialog via PowerShell (present on every Windows): multi-select
                // on, chosen files echoed one per line, a cancel printing nothing.
                string initial = haveDirectory ? $"$d.InitialDirectory = '{NativeFileDialogs.EscapePowerShell(initialDirectory!)}'; " : string.Empty;

                string script =
                    "Add-Type -AssemblyName System.Windows.Forms | Out-Null; " +
                    "$d = New-Object System.Windows.Forms.OpenFileDialog; " +
                    "$d.Multiselect = $true; " +
                    "$d.Title = 'Open beatmaps (.osz), skins (.osk) or replays (.osr)'; " +
                    "$d.Filter = 'osu! files (*.osz;*.osk;*.osr)|*.osz;*.osk;*.osr|All files (*.*)|*.*'; " +
                    initial +
                    "if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $d.FileNames | ForEach-Object { Write-Output $_ } }";

                return new NativeFileDialogs.Command("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-Command", script });
            }

            case RuntimeInfo.Platform.Linux when linuxTool == "zenity":
            {
                var arguments = new List<string>
                {
                    "--file-selection",
                    "--multiple",
                    "--separator=\n",
                    "--title=Open beatmaps (.osz), skins (.osk) or replays (.osr)",
                };

                if (haveDirectory)
                    arguments.Add($"--filename={initialDirectory!.TrimEnd('/')}/");

                return new NativeFileDialogs.Command("zenity", arguments);
            }

            case RuntimeInfo.Platform.Linux when linuxTool == "kdialog":
                return new NativeFileDialogs.Command("kdialog", new[]
                {
                    "--multiple",
                    "--separate-output",
                    "--getopenfilename",
                    haveDirectory ? initialDirectory! : ".",
                });

            default:
                return null;
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
