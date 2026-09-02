#nullable enable

using System;

namespace JukeBox.Game.Presence;

/// <summary>
/// The IPC boundary, behind an interface so <see cref="DiscordPresenceService"/> can be driven in
/// tests without a Discord process (and so the service never has to care that Discord might not be
/// there at all — every implementation is required to swallow that itself).
/// </summary>
public interface IPresenceClient : IDisposable
{
    /// <summary>
    /// Opens the connection. Must not throw and must not block on Discord being present: with no
    /// Discord running this is expected to be a no-op that quietly connects later.
    /// </summary>
    void Start();

    /// <summary>Show this presence, replacing whatever was showing.</summary>
    void Publish(PresenceState state);

    /// <summary>Take the presence down (the user turned the setting off, or nothing is playing).</summary>
    void Clear();
}
