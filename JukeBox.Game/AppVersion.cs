#nullable enable

using System.Linq;
using System.Reflection;
using osu.Framework.Development;

namespace JukeBox.Game;

/// <summary>
/// The build's own version, for display (see <see cref="UI.SettingsOverlay"/>'s footer).
///
/// <para>
/// Read from <see cref="AssemblyInformationalVersionAttribute"/> rather than
/// <c>AssemblyVersion</c>/<c>FileVersion</c>, because only the informational one can carry a
/// prerelease suffix — a tag like <c>v1.0.0-rc1</c> stamps as <c>1.0.0-rc1</c> here while the other
/// two are forced to plain <c>1.0.0</c>.
/// </para>
/// </summary>
public static class AppVersion
{
    /// <summary>The version of the running application.</summary>
    /// <remarks>Falls back to this assembly when there is no entry assembly to ask — the case under
    /// a test runner, where the entry assembly is the test host rather than the app.</remarks>
    public static string DisplayString => For(Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly);

    internal static string For(Assembly assembly) => Format(
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assembly.GetName().Version?.Major ?? 0,
        DebugUtils.IsDebugBuild);

    /// <summary>
    /// Builds the displayed string. Split out from <see cref="For"/> so the interesting part — what
    /// each kind of build reads as — is testable without fabricating assemblies.
    /// </summary>
    /// <param name="informationalVersion">The raw attribute value, e.g. <c>1.0.0-rc1</c> or
    /// <c>0.0.0+9f6d37194b86c5fcc96acc32eef195d2867695ec</c>. The <c>+</c> suffix is the source
    /// revision the SDK appends by itself for a repo checkout — free, so it is shown, shortened.</param>
    /// <param name="major">The assembly version's major component. Zero means an unstamped build:
    /// the same test lazer uses for "is this deployed" (see its <c>OsuGameBase.IsDeployedBuild</c>),
    /// which matters because <c>JukeBox.Desktop.csproj</c> carries 0.0.0 until a release tag stamps
    /// a real one over it.</param>
    /// <param name="debugBuild">Whether this is a Debug configuration.</param>
    internal static string Format(string? informationalVersion, int major, bool debugBuild)
    {
        string version = informationalVersion ?? string.Empty;
        string? revision = null;

        // "1.0.0+<40-char sha>" — keep the version, and keep the sha short enough to read.
        int plus = version.IndexOf('+');

        if (plus >= 0)
        {
            revision = version[(plus + 1)..];
            version = version[..plus];

            if (revision.Length > 7)
                revision = revision[..7];
        }

        if (version.Length == 0)
            version = "unknown";

        // An unstamped build says so. Showing a bare "0.0.0" would read as a release that happens to
        // be numbered zero, which is exactly the thing worth not implying.
        string? build = major > 0 ? null : "local " + (debugBuild ? "debug" : "release");

        string[] notes = new[] { build, revision }.Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToArray();

        return notes.Length > 0
            ? $"v{version} ({string.Join(" · ", notes)})"
            : $"v{version}";
    }
}
