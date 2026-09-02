#nullable enable

using System;

namespace JukeBox.Game.Presence;

/// <summary>
/// What the app is doing, in Discord's own activity vocabulary. Which one applies is decided by
/// <see cref="DiscordPresenceService.Build"/>.
/// </summary>
public enum PresenceActivity
{
    /// <summary>Audio only: Discord shows "Listening to osu!JukeBox", Spotify-style.</summary>
    Listening,

    /// <summary>
    /// Something worth looking at is on screen — the rendered chart, the map's storyboard, or its
    /// video: "Watching osu!JukeBox".
    ///
    /// <para>
    /// One value, not one per source. The chart and storyboard cases used to be distinct because the
    /// details line named which it was; now that every case carries the same Spotify-shaped text they
    /// produce byte-identical presences, so the question of which one "wins" for a map that has both
    /// stops existing rather than needing an answer.
    /// </para>
    /// </summary>
    Watching,

    /// <summary>
    /// Nothing has been playing for a while: the app with no track on it at all. See
    /// <see cref="DiscordPresenceService.IDLE_AFTER_MS"/>.
    /// </summary>
    Idle,
}

/// <summary>
/// One publishable presence, as a value. Deliberately carries nothing from the DiscordRPC package:
/// everything that decides WHAT to show is expressed in these terms and unit-testable without a
/// Discord client, and <see cref="DiscordPresenceClient.BuildRichPresence"/> is the only place that
/// knows how it maps onto the wire.
/// </summary>
/// <param name="Activity">The activity verb Discord puts in front of the app name.</param>
/// <param name="Details">First line under the header — the track, prefixed by what's on screen.</param>
/// <param name="State">Second line — artist, plus the difficulty when there is one.</param>
/// <param name="StartUtc">When the track started, back-dated from the current position. Null while
/// paused: Discord's progress bar has no paused state and would keep counting, so the honest thing
/// (and what Spotify does) is to show no bar at all rather than a wrong one.</param>
/// <param name="EndUtc">When the track will finish at the current rate. Null whenever
/// <paramref name="StartUtc"/> is — Discord only draws a progress bar given both.</param>
/// <param name="ImageUrl">The playing set's cover art, as a public URL. Null for a set with no
/// online id — a local or dropped map has no published cover to point at — and Discord then falls
/// back to the application's own icon, which is what it shows for an activity carrying no image.</param>
/// <param name="ImageText">Hover tooltip for that image. Null whenever <paramref name="ImageUrl"/> is.</param>
public sealed record PresenceState(
    PresenceActivity Activity,
    string Details,
    string State,
    DateTime? StartUtc,
    DateTime? EndUtc,
    string? ImageUrl = null,
    string? ImageText = null);
