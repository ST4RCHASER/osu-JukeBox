#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// A named bundle of encoder settings the Render dialog's preset dropdown fills the fields from —
/// the "start from a platform's recommended upload spec, then tweak" shortcut. Everything here is
/// pure data (no drawables, no I/O) so the preset→fields mapping and the reverse "do these field
/// values match a preset?" check are both unit-testable without a game host.
///
/// <para>
/// The four presets follow each platform's own published recommended upload spec: YouTube's 1080p60
/// at a high bitrate, Facebook's 720p30, TikTok's vertical 1080×1920. <see cref="Custom"/> is the
/// escape hatch — it carries no values (its dimensions are zero) and simply means "the fields are
/// whatever the user set", which is what the dropdown snaps to the moment any field is edited.
/// </para>
/// </summary>
public sealed record RenderPreset(
    string Name,
    int Width,
    int Height,
    int Fps,
    string Format,
    int AudioBitrateKbps)
{
    /// <summary>The "none of the built-ins" option. Carries no dimensions — selecting it changes no
    /// field, and editing any field switches the dropdown to it.</summary>
    public static readonly RenderPreset Custom = new RenderPreset("Custom", 0, 0, 0, "mp4", 0);

    public static readonly RenderPreset YouTube = new RenderPreset("YouTube", 1920, 1080, 60, "mp4", 192);

    public static readonly RenderPreset Facebook = new RenderPreset("Facebook", 1280, 720, 30, "mp4", 128);

    public static readonly RenderPreset TikTok = new RenderPreset("TikTok", 1080, 1920, 30, "mp4", 128);

    /// <summary>The built-in presets in dropdown order, with <see cref="Custom"/> last.</summary>
    public static readonly IReadOnlyList<RenderPreset> All = new[] { YouTube, Facebook, TikTok, Custom };

    /// <summary>Whether this preset actually carries settings (everything but <see cref="Custom"/>).</summary>
    public bool HasValues => this != Custom;

    /// <summary>The dropdown shows the preset by name (a bare record would print all its fields).</summary>
    public override string ToString() => Name;

    /// <summary>
    /// Fills a preset's own fields (resolution, fps, format, audio bitrate) into a copy of the given
    /// form values, leaving the fields a preset says nothing about — save path, start and end time —
    /// untouched. This is what "picking a preset fills the fields" does; <see cref="Custom"/> changes
    /// nothing. Pure, so the mapping is asserted without a dialog.
    /// </summary>
    public RenderFormValues ApplyTo(RenderFormValues current)
    {
        if (!HasValues)
            return current;

        return current with
        {
            Resolution = $"{Width}x{Height}",
            Fps = Fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Format = Format,
            AudioBitrate = AudioBitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// The preset whose full settings match the given field values exactly, or <see cref="Custom"/>
    /// when none do — the rule that snaps the dropdown back to a named preset only when every field
    /// still matches it, and to Custom the instant one differs.
    /// </summary>
    public static RenderPreset Match(int width, int height, int fps, string format, int audioBitrateKbps)
        => All.FirstOrDefault(p =>
               p.HasValues
               && p.Width == width
               && p.Height == height
               && p.Fps == fps
               && string.Equals(p.Format, format, System.StringComparison.OrdinalIgnoreCase)
               && p.AudioBitrateKbps == audioBitrateKbps)
           ?? Custom;
}
