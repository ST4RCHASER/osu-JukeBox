#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// Covers the failure memory that keeps an unreachable mirror off the keystroke path — and,
    /// just as importantly, keeps it coming back. Two live conditions motivated it: NeriNyan
    /// answering Cloudflare 530 (transient, must self-heal) and catboy.best being TLS-1.3-only,
    /// which .NET on macOS cannot speak at all (permanent on that platform, fine everywhere else).
    /// </summary>
    [TestFixture]
    public class MirrorHealthTest
    {
        private DateTimeOffset now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private MirrorHealth health(TimeSpan? cooldown = null)
            => new MirrorHealth(cooldown ?? TimeSpan.FromSeconds(60), () => now);

        private class Mirror : IBeatmapMirror
        {
            public Mirror(string name, bool capable = true)
            {
                Name = name;
                Capable = capable;
            }

            public string Name { get; }
            public bool Capable;
            public bool Fail;
            public int SearchCalls;
            public int DownloadCalls;

            public bool CanApplyFilters(SearchRequest request) => Capable;

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                SearchCalls++;

                if (Fail)
                    throw new IOException($"{Name} down");

                return Task.FromResult(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 1 } });
            }

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
            {
                DownloadCalls++;

                if (Fail)
                    throw new IOException($"{Name} down");

                destination.WriteByte(7);
                return Task.CompletedTask;
            }
        }

        // ---- The memory itself -----------------------------------------------------------------

        [Test]
        public void FailureCoolsDownAndLapsesOnItsOwn()
        {
            var h = health();
            var m = new Mirror("catboy.best");

            Assert.That(h.IsCoolingDown(m), Is.False);

            h.RecordFailure(m);
            Assert.That(h.IsCoolingDown(m), Is.True);

            now += TimeSpan.FromSeconds(59);
            Assert.That(h.IsCoolingDown(m), Is.True);

            // Nothing in the app would ever re-enable a mirror by hand, so recovery has to be
            // purely a function of time passing.
            now += TimeSpan.FromSeconds(2);
            Assert.That(h.IsCoolingDown(m), Is.False);
        }

        [Test]
        public void SuccessClearsTheCooldownImmediately()
        {
            var h = health();
            var m = new Mirror("api.nerinyan.moe");

            h.RecordFailure(m);
            h.RecordSuccess(m);

            Assert.That(h.IsCoolingDown(m), Is.False);
        }

        [Test]
        public void MirrorsAreTrackedSeparatelyEvenWhenTheyShareAName()
        {
            var h = health();
            var a = new Mirror("fake");
            var b = new Mirror("fake");

            h.RecordFailure(a);

            Assert.That(h.IsCoolingDown(a), Is.True);
            Assert.That(h.IsCoolingDown(b), Is.False);
        }

        // ---- How the chain uses it --------------------------------------------------------------

        [Test]
        public async Task ADeadMirrorIsNotReprobedOnEverySearch()
        {
            var h = health();
            var dead = new Mirror("catboy.best") { Fail = true };
            var alive = new Mirror("osu.direct");
            var chain = new MirrorChain(h, dead, alive);

            for (int i = 0; i < 5; i++)
                await chain.SearchAsync(new SearchRequest());

            // Probed once, then skipped — this is the whole point: five searches used to mean five
            // doomed TLS handshakes, one per keystroke.
            Assert.That(dead.SearchCalls, Is.EqualTo(1));
            Assert.That(alive.SearchCalls, Is.EqualTo(5));
        }

        [Test]
        public async Task ARecoveredMirrorIsPickedUpAgainWithoutARestart()
        {
            var h = health();
            var flaky = new Mirror("api.nerinyan.moe") { Fail = true };
            var fallback = new Mirror("osu.direct");
            var chain = new MirrorChain(h, flaky, fallback);

            await chain.SearchAsync(new SearchRequest());
            Assert.That(flaky.SearchCalls, Is.EqualTo(1));

            await chain.SearchAsync(new SearchRequest());
            Assert.That(flaky.SearchCalls, Is.EqualTo(1), "still cooling down");

            // Upstream comes back — a 530 is an outage, not a verdict.
            flaky.Fail = false;
            now += TimeSpan.FromSeconds(61);

            await chain.SearchAsync(new SearchRequest());

            Assert.That(flaky.SearchCalls, Is.EqualTo(2));
            Assert.That(fallback.SearchCalls, Is.EqualTo(2), "the fallback answered only while it was needed");

            // And once it answers, it is trusted again immediately rather than after another window.
            await chain.SearchAsync(new SearchRequest());
            Assert.That(flaky.SearchCalls, Is.EqualTo(3));
        }

        [Test]
        public async Task CapabilityStillOutranksHealth()
        {
            var h = health();
            var capable = new Mirror("NeriNyan");
            var limited = new Mirror("osu.direct", capable: false);
            var chain = new MirrorChain(h, limited, capable);

            string? dropped = null;
            await chain.SearchAsync(new SearchRequest { Mode = "m", OnFiltersDropped = n => dropped = n });

            Assert.That(capable.SearchCalls, Is.EqualTo(1));
            Assert.That(limited.SearchCalls, Is.Zero);
            Assert.That(dropped, Is.Null);
        }

        [Test]
        public async Task ACoolingDownMirrorIsStillTriedWhenItIsAllThereIs()
        {
            var h = health();
            var only = new Mirror("osu.direct");
            var chain = new MirrorChain(h, only);

            h.RecordFailure(only);

            // Never dropped outright — degraded results beat an empty listing.
            var results = await chain.SearchAsync(new SearchRequest());

            Assert.That(results, Is.Not.Empty);
            Assert.That(only.SearchCalls, Is.EqualTo(1));
        }

        [Test]
        public async Task DownloadsSkipAMirrorKnownToBeDownToo()
        {
            var h = health();
            var dead = new Mirror("catboy.best") { Fail = true };
            var alive = new Mirror("osu.direct");
            var chain = new MirrorChain(h, dead, alive);

            using var first = new MemoryStream();
            await chain.DownloadAsync(1, false, first);

            using var second = new MemoryStream();
            await chain.DownloadAsync(1, false, second);

            Assert.That(dead.DownloadCalls, Is.EqualTo(1));
            Assert.That(second.ToArray(), Is.EqualTo(new byte[] { 7 }));
        }

        [Test]
        public void CallerCancellationIsNotTreatedAsAMirrorFailure()
        {
            var h = health();
            // Must be a mirror that actually observes the token, which is what the real ones do.
            var m = new StallingMirror();
            var chain = new MirrorChain(h, m);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            // A superseded search (the engine cancels those constantly) must not condemn a mirror
            // that never got the chance to answer, nor fall through to the next one.
            Assert.ThrowsAsync<TaskCanceledException>(() => chain.SearchAsync(new SearchRequest(), cancelled.Token));
            Assert.That(h.IsCoolingDown(m), Is.False);
        }

        [Test]
        public async Task SearchesAreBoundedSoOneStalledMirrorCannotHangTheListing()
        {
            Assert.That(MirrorChain.SEARCH_TIMEOUT, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(30)),
                "a search sits on the keystroke path; HttpClient's 100s default would read as a hang");

            var h = health();
            var stalls = new StallingMirror();
            var alive = new Mirror("osu.direct");
            var chain = new MirrorChain(h, stalls, alive) { SearchTimeout = TimeSpan.FromMilliseconds(200) };

            var results = await chain.SearchAsync(new SearchRequest());

            // The stalled mirror is cancelled by the chain's own timeout, not by the caller, so it
            // counts as a failure and the next mirror answers.
            Assert.That(stalls.Cancelled, Is.True);
            Assert.That(results, Is.Not.Empty);
            Assert.That(h.IsCoolingDown(stalls), Is.True);
        }

        /// <summary>Blocks until the chain's own timeout cancels it.</summary>
        private class StallingMirror : IBeatmapMirror
        {
            public string Name => "stalling";
            public bool Cancelled;

            public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }

                return new List<BeatmapSetInfo>();
            }

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new NotSupportedException();
        }
    }
}
