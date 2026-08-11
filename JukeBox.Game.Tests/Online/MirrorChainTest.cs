using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    public class FakeMirror : IBeatmapMirror
    {
        public string Name => "fake";
        public bool Fail;
        public int SearchCalls;
        public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
        {
            SearchCalls++;
            if (Fail) throw new IOException("down");
            return Task.FromResult(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 42 } });
        }
        public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
        {
            if (Fail) throw new IOException("down");
            destination.WriteByte(1);
            return Task.CompletedTask;
        }
    }

    [TestFixture]
    public class MirrorChainTest
    {
        [Test]
        public async Task FallsBackToSecondMirror()
        {
            var a = new FakeMirror { Fail = true };
            var b = new FakeMirror();
            var chain = new MirrorChain(a, b);
            var results = await chain.SearchAsync(new SearchRequest());
            Assert.That(results[0].Id, Is.EqualTo(42));
            Assert.That(a.SearchCalls, Is.EqualTo(1));
        }

        [Test]
        public void ThrowsWhenAllFail()
        {
            var chain = new MirrorChain(new FakeMirror { Fail = true }, new FakeMirror { Fail = true });
            Assert.ThrowsAsync<AggregateException>(() => chain.SearchAsync(new SearchRequest()));
        }
    }
}
