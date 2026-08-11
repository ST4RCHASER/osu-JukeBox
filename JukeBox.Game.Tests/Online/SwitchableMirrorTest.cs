using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;
using osu.Framework.Bindables;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// Always fails after recording its own name, so a chain built from several of these
    /// captures the full attempted order (MirrorChain only stops early on the first success).
    /// </summary>
    public class OrderTrackingMirror : IBeatmapMirror
    {
        private readonly List<string> callOrder;
        public string Name { get; }
        public OrderTrackingMirror(string name, List<string> callOrder)
        {
            Name = name;
            this.callOrder = callOrder;
        }

        public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
        {
            callOrder.Add(Name);
            throw new IOException("down");
        }

        public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
        {
            callOrder.Add(Name);
            throw new IOException("down");
        }
    }

    [TestFixture]
    public class SwitchableMirrorTest
    {
        private List<string> callOrder = null!;
        private OrderTrackingMirror nerinyan = null!;
        private OrderTrackingMirror catboy = null!;
        private OrderTrackingMirror osuDirect = null!;
        private Bindable<MirrorSource> preferred = null!;
        private SwitchableMirror mirror = null!;

        [SetUp]
        public void SetUp()
        {
            callOrder = new List<string>();
            nerinyan = new OrderTrackingMirror("NeriNyan", callOrder);
            catboy = new OrderTrackingMirror("catboy.best", callOrder);
            osuDirect = new OrderTrackingMirror("osu.direct", callOrder);
            preferred = new Bindable<MirrorSource>();
            mirror = new SwitchableMirror(nerinyan, catboy, osuDirect, preferred);
        }

        [Test]
        public async Task AutoUsesDefaultOrder()
        {
            preferred.Value = MirrorSource.Auto;
            await assertThrowsAggregate(() => mirror.SearchAsync(new SearchRequest()));
            Assert.That(callOrder, Is.EqualTo(new[] { "NeriNyan", "catboy.best", "osu.direct" }));
        }

        [Test]
        public async Task NerinyanPreferredKeepsDefaultOrder()
        {
            preferred.Value = MirrorSource.Nerinyan;
            await assertThrowsAggregate(() => mirror.SearchAsync(new SearchRequest()));
            Assert.That(callOrder, Is.EqualTo(new[] { "NeriNyan", "catboy.best", "osu.direct" }));
        }

        [Test]
        public async Task CatboyPreferredIsTriedFirst()
        {
            preferred.Value = MirrorSource.Catboy;
            await assertThrowsAggregate(() => mirror.SearchAsync(new SearchRequest()));
            Assert.That(callOrder, Is.EqualTo(new[] { "catboy.best", "NeriNyan", "osu.direct" }));
        }

        [Test]
        public async Task OsuDirectPreferredIsTriedFirst()
        {
            preferred.Value = MirrorSource.OsuDirect;
            await assertThrowsAggregate(() => mirror.SearchAsync(new SearchRequest()));
            Assert.That(callOrder, Is.EqualTo(new[] { "osu.direct", "NeriNyan", "catboy.best" }));
        }

        [Test]
        public async Task FallbackStillWorksWhenPreferredFails()
        {
            preferred.Value = MirrorSource.Catboy;
            var fallback = new FakeMirror();
            var switchable = new SwitchableMirror(nerinyan, catboy, fallback, preferred);

            var results = await switchable.SearchAsync(new SearchRequest());

            Assert.That(results[0].Id, Is.EqualTo(42));
            Assert.That(callOrder, Is.EqualTo(new[] { "catboy.best", "NeriNyan" }));
        }

        [Test]
        public async Task LiveSwitchChangesOrderBetweenCalls()
        {
            preferred.Value = MirrorSource.Auto;
            await assertThrowsAggregate(() => mirror.SearchAsync(new SearchRequest()));
            Assert.That(callOrder, Is.EqualTo(new[] { "NeriNyan", "catboy.best", "osu.direct" }));

            callOrder.Clear();
            preferred.Value = MirrorSource.OsuDirect;
            await assertThrowsAggregate(() => mirror.SearchAsync(new SearchRequest()));
            Assert.That(callOrder, Is.EqualTo(new[] { "osu.direct", "NeriNyan", "catboy.best" }));
        }

        private static async Task assertThrowsAggregate(Func<Task> action)
        {
            try
            {
                await action();
                Assert.Fail("expected AggregateException");
            }
            catch (AggregateException)
            {
            }
        }
    }
}
