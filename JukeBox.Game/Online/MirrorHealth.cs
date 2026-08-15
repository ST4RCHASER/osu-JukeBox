#nullable enable

using System;
using System.Collections.Generic;

namespace JukeBox.Game.Online
{
    /// <summary>
    /// Short-lived memory of which mirrors just failed, so a mirror that is currently unreachable
    /// stops being retried on every single keystroke — and starts being retried again by itself as
    /// soon as the window lapses.
    ///
    /// <para>
    /// Two real conditions motivated this, both live at the time of writing. api.nerinyan.moe was
    /// answering Cloudflare 530 (its origin down) — a transient outage that will clear on its own,
    /// so nothing may permanently blacklist it. catboy.best is TLS-1.3-only, and .NET on macOS
    /// speaks TLS through Apple SecureTransport, which has no TLS 1.3 at all — so on that platform
    /// every attempt costs a ~400ms handshake that cannot ever succeed. (The same mirror works
    /// normally from .NET on Linux and Windows, so it must NOT be disabled in the code.)
    /// </para>
    ///
    /// <para>
    /// Deliberately a COOLDOWN and not a circuit breaker with a failure count: one failure is
    /// enough evidence to stop asking for a minute, and the recovery path has to be automatic
    /// because nothing else in the app would ever re-enable a mirror. Keyed by mirror INSTANCE
    /// rather than <see cref="IBeatmapMirror.Name"/> — the mirrors are singletons, and names are
    /// not guaranteed distinct (test fakes share one).
    /// </para>
    /// </summary>
    public class MirrorHealth
    {
        /// <summary>
        /// How long a failed mirror is passed over. Short enough that a transient outage costs the
        /// user at most one stale minute, long enough that a permanently unreachable mirror is
        /// probed roughly once a minute instead of once per search.
        /// </summary>
        public static readonly TimeSpan DEFAULT_COOLDOWN = TimeSpan.FromSeconds(60);

        private readonly TimeSpan cooldown;
        private readonly Func<DateTimeOffset> clock;

        // Reference-keyed: see the class summary for why not by name.
        private readonly Dictionary<IBeatmapMirror, DateTimeOffset> coolingUntil
            = new Dictionary<IBeatmapMirror, DateTimeOffset>(ReferenceEqualityComparer.Instance);

        private readonly object mutex = new object();

        /// <param name="cooldown">Overridable for tests; defaults to <see cref="DEFAULT_COOLDOWN"/>.</param>
        /// <param name="clock">Overridable for tests, so a cooldown can lapse without waiting.</param>
        public MirrorHealth(TimeSpan? cooldown = null, Func<DateTimeOffset>? clock = null)
        {
            this.cooldown = cooldown ?? DEFAULT_COOLDOWN;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>Whether <paramref name="mirror"/> failed recently enough to be passed over.</summary>
        public bool IsCoolingDown(IBeatmapMirror mirror)
        {
            lock (mutex)
                return coolingUntil.TryGetValue(mirror, out var until) && clock() < until;
        }

        public void RecordFailure(IBeatmapMirror mirror)
        {
            lock (mutex)
                coolingUntil[mirror] = clock() + cooldown;
        }

        /// <summary>Clears the cooldown — a mirror that just answered is healthy by definition, and
        /// this is what makes recovery immediate once one succeeds again.</summary>
        public void RecordSuccess(IBeatmapMirror mirror)
        {
            lock (mutex)
                coolingUntil.Remove(mirror);
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<IBeatmapMirror>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public bool Equals(IBeatmapMirror? x, IBeatmapMirror? y) => ReferenceEquals(x, y);

            public int GetHashCode(IBeatmapMirror obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
