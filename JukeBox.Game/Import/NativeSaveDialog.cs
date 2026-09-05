#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using osu.Framework;

namespace JukeBox.Game.Import;

/// <summary>
/// The operating system's own "save file" panel — what the Render dialog's Browse… drives to pick a
/// save location. osu!framework exposes no native SAVE dialog on any desktop OS, so each platform
/// drives its own, exactly as <see cref="NativeOpenDialog"/> does for opening: macOS the standard
/// NSSavePanel through <c>osascript</c> (<c>choose file name</c>), Windows the stock WinForms
/// SaveFileDialog through PowerShell, Linux <c>zenity</c>/<c>kdialog</c>. The user names the file
/// in the panel; the answer comes back as one path. A Linux box with neither tool reports
/// <see cref="IsAvailable"/> false and the caller falls back to typing the path.
/// </summary>
public static class NativeSaveDialog
{
    /// <summary>Whether this platform has a native save panel to offer: macOS and Windows always,
    /// Linux when a dialog tool (zenity/kdialog) is installed.</summary>
    public static bool IsAvailable => RuntimeInfo.OS switch
    {
        RuntimeInfo.Platform.macOS => true,
        RuntimeInfo.Platform.Windows => true,
        RuntimeInfo.Platform.Linux => NativeFileDialogs.LinuxDialogTool() != null,
        _ => false,
    };

    /// <summary>
    /// Shows the panel and returns the chosen path — null when the user cancelled. Never throws for
    /// a cancel; a genuinely failed launch is logged and also reads as "nothing chosen".
    /// </summary>
    public static async Task<string?> PickSaveAsync(string? initialDirectory, string? defaultFileName)
    {
        var command = BuildCommand(RuntimeInfo.OS, initialDirectory, defaultFileName, RuntimeInfo.OS == RuntimeInfo.Platform.Linux ? NativeFileDialogs.LinuxDialogTool() : null);

        if (command == null)
            return null;

        string output = await NativeFileDialogs.RunAsync(command, "[render]").ConfigureAwait(false);
        return ParseOutput(output);
    }

    /// <summary>
    /// The exact process invocation for one platform's save panel — every tool prints the chosen
    /// location as one path, so <see cref="ParseOutput"/> is shared. Pure and internal so the tests
    /// exercise every platform's argv from any machine. Null when the platform has no dialog.
    /// </summary>
    internal static NativeFileDialogs.Command? BuildCommand(RuntimeInfo.Platform platform, string? initialDirectory, string? defaultFileName, string? linuxTool)
    {
        bool haveDirectory = !string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory);
        bool haveName = !string.IsNullOrEmpty(defaultFileName);

        switch (platform)
        {
            case RuntimeInfo.Platform.macOS:
            {
                var arguments = new List<string>();

                foreach (string line in Script(initialDirectory, defaultFileName))
                {
                    arguments.Add("-e");
                    arguments.Add(line);
                }

                return new NativeFileDialogs.Command("osascript", arguments);
            }

            case RuntimeInfo.Platform.Windows:
            {
                // The stock WinForms dialog via PowerShell (present on every Windows), seeded with
                // the current name/folder; a cancel prints nothing. Overwrite confirmation is the
                // dialog's own default.
                string seedName = haveName ? $"$d.FileName = '{NativeFileDialogs.EscapePowerShell(defaultFileName!)}'; " : string.Empty;
                string seedDirectory = haveDirectory ? $"$d.InitialDirectory = '{NativeFileDialogs.EscapePowerShell(initialDirectory!)}'; " : string.Empty;

                string script =
                    "Add-Type -AssemblyName System.Windows.Forms | Out-Null; " +
                    "$d = New-Object System.Windows.Forms.SaveFileDialog; " +
                    "$d.Title = 'Choose where to save the rendered video'; " +
                    "$d.Filter = 'Video files (*.mp4;*.webm;*.mov)|*.mp4;*.webm;*.mov|All files (*.*)|*.*'; " +
                    seedName + seedDirectory +
                    "if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $d.FileName }";

                return new NativeFileDialogs.Command("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-Command", script });
            }

            case RuntimeInfo.Platform.Linux when linuxTool == "zenity":
            {
                var arguments = new List<string>
                {
                    "--file-selection",
                    "--save",
                    "--title=Choose where to save the rendered video",
                };

                if (haveDirectory || haveName)
                    arguments.Add($"--filename={Path.Combine(haveDirectory ? initialDirectory! : ".", haveName ? defaultFileName! : string.Empty)}");

                return new NativeFileDialogs.Command("zenity", arguments);
            }

            case RuntimeInfo.Platform.Linux when linuxTool == "kdialog":
                return new NativeFileDialogs.Command("kdialog", new[]
                {
                    "--getsavefilename",
                    Path.Combine(haveDirectory ? initialDirectory! : ".", haveName ? defaultFileName! : string.Empty),
                });

            default:
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
