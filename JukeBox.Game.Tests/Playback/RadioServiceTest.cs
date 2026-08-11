using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Playback
{
    public class ListMirror : IBeatmapMirror
    {
        private readonly List<BeatmapSetInfo> items;
        public string Name => "list";
        public bool Fail;
        public int SearchCalls;

        public ListMirror(List<BeatmapSetInfo> items) => this.items = items;

        public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
        {
            SearchCalls++;
            if (Fail) throw new IOException("down");
            return Task.FromResult(items);
        }

        public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
            => throw new System.NotSupportedException();
    }

    [TestFixture]
    public class RadioServiceTest
    {
        [Test]
        public async Task RadioSkipsDownloadDisabled()
        {
            var mirror = new ListMirror(new List<BeatmapSetInfo>
            {
                new BeatmapSetInfo { Id = 1, Availability = new AvailabilityInfo { DownloadDisabled = true } },
                new BeatmapSetInfo { Id = 2 },
            });
            var radio = new RadioService(mirror, (min, max) => min); // deterministic rng
            var pick = await radio.PickRandomAsync();
            Assert.That(pick!.Id, Is.EqualTo(2));
        }

        [Test]
        public async Task ReturnsNullAfterThreeFailedAttempts()
        {
            var mirror = new ListMirror(new List<BeatmapSetInfo>()) { Fail = true };
            var radio = new RadioService(mirror, (min, max) => min);
            var pick = await radio.PickRandomAsync();
            Assert.That(pick, Is.Null);
            Assert.That(mirror.SearchCalls, Is.EqualTo(3));
        }
    }
}
