#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using osu.Framework.Logging;

namespace JukeBox.Game.Import;

/// <summary>
/// The shared plumbing under <see cref="NativeOpenDialog"/>: one process runner for whichever tool
/// a platform's dialog is driven through (macOS <c>osascript</c>, Windows PowerShell's WinForms
/// dialogs, Linux <c>zenity</c>/<c>kdialog</c>), plus the PATH probe that decides whether a Linux
/// desktop has a dialog tool at all. Every tool speaks the same simple protocol — chosen paths on
/// stdout, one per line, a cancel being a non-zero exit or no output — so the dialogs only differ
/// in the argv they build (pure, per-platform, unit-tested).
/// </summary>
internal static class NativeFileDialogs
{
    /// <summary>One dialog invocation: the executable and its argv, never shell-quoted.</summary>
    internal sealed record Command(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>The Linux dialog tool to drive, in preference order — <c>zenity</c> (GNOME and most
    /// desktops), then <c>kdialog</c> (KDE) — or null when the system has neither, which callers
    /// surface as "no native dialog" so their in-app fallbacks take over.</summary>
    internal static string? LinuxDialogTool()
        => ExistsOnPath("zenity") ? "zenity"
            : ExistsOnPath("kdialog") ? "kdialog"
            : null;

    internal static bool ExistsOnPath(string executable)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathVar))
            return false;

        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            if (File.Exists(Path.Combine(dir.Trim(), executable)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs one dialog command and returns its stdout — empty for a cancelled panel (the tools exit
    /// non-zero or print nothing) and for a genuinely failed launch, which is logged. Never throws.
    /// </summary>
    internal static async Task<string> RunAsync(Command command, string logContext)
    {
        try
        {
            var start = new ProcessStartInfo(command.FileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (string argument in command.Arguments)
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);

            if (process == null)
                return string.Empty;

            // stderr is drained so a chatty tool can never fill the pipe and stall the dialog.
            _ = process.StandardError.ReadToEndAsync();

            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (Exception e)
        {
            Logger.Error(e, $"{logContext} the native file dialog could not be shown");
            return string.Empty;
        }
    }

    /// <summary>Escapes a value for a single-quoted PowerShell string literal.</summary>
    internal static string EscapePowerShell(string value) => value.Replace("'", "''");
}
