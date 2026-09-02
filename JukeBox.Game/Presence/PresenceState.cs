#nullable enable

using System;

namespace JukeBox.Game.Presence;

/// <summary>
/// What the app is doing with the current track, in Discord's own activity vocabulary. Which one
/// applies is decided by <see cref="DiscordPresenceService.Build"/> — see there for the precedence.
/// </summary>
public enum PresenceActivity
{
    /// <summary>Audio only: Discord shows "Listening to osu!JukeBox", Spotify-style.</summary>
    Listening,

    /// <summary>The map's storyboard is on screen: "Watching osu!JukeBox".</summary>
    WatchingStoryboard,

    /// <summary>The rendered chart is on screen: "Watching osu!JukeBox".</summary>
    WatchingChart,
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
public sealed record PresenceState(
    PresenceActivity Activity,
    string Details,
    string State,
    DateTime? StartUtc,
    DateTime? EndUtc);
