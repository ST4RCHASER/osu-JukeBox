#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// Looking a beatmap up by its .osu checksum — the only identity a dropped .osr carries. The
    /// point of these is the CHAIN behaviour: a mirror that can't perform the lookup must not be
    /// allowed to answer "no such beatmap" on behalf of one that can.
    /// </summary>
    [TestFixture]
    public class ChecksumLookupTest
    {
        private const string md5 = "af01ef4b45cc6d13cfa4a585ed03acae";

        [Test]
        public void NerinyanIssuesItAsAFieldRestrictedLegacyQuery()
        {
            string url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Query = md5, Option = SearchRequest.CHECKSUM_OPTION });

            Assert.That(url, Does.Contain("option=checksum"));
            Assert.That(url, Does.Contain($"q={md5}"));
        }

        [Test]
        public void CatboyRefusesRatherThanAnsweringEmpty()
        {
            var catboy = new CatboyMirror(new System.Net.Http.HttpClient());

            Assert.That(async () => await catboy.SearchAsync(new SearchRequest { Query = md5, Option = SearchRequest.CHECKSUM_OPTION }),
                Throws.InstanceOf<System.NotSupportedException>(),
                "answering empty would end the chain and lose the mirror that can actually resolve it");
        }

        // The regression this guards: a refusal must be a refusal, so the chain keeps going. If
        // CatboyMirror ever returns an empty list here instead of throwing, MirrorChain would take
        // that as the answer and never reach osu.direct.
        [Test]
        public async Task AMirrorThatCannotAnswerLetsTheNextOneResolveIt()
        {
            var resolving = new ChecksumOnlyMirror(md5, setId: 2320665);
            var chain = new MirrorChain(new CatboyMirror(new System.Net.Http.HttpClient()), resolving);

            var results = await chain.SearchAsync(new SearchRequest { Query = md5, Option = SearchRequest.CHECKSUM_OPTION });

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo(2320665));
            Assert.That(resolving.Calls, Is.EqualTo(1));
        }

        // An unknown checksum is a real answer ("no beatmap has this hash"), not a mirror failure —
        // so it comes back as no results and the importer reports it to the user, rather than
        // throwing and being retried against every other mirror.
        [Test]
        public async Task AnUnknownChecksumResolvesToNoResults()
        {
            var chain = new MirrorChain(new ChecksumOnlyMirror("0000000000000000000000000000dead", setId: 1));

            var results = await chain.SearchAsync(new SearchRequest { Query = md5, Option = SearchRequest.CHECKSUM_OPTION });

            Assert.That(results, Is.Empty);
        }

        /// <summary>Stands in for osu.direct: resolves exactly one known checksum.</summary>
        private class ChecksumOnlyMirror : IBeatmapMirror
        {
            private readonly string knownMd5;
            private readonly int setId;

            public int Calls { get; private set; }

            public string Name => "checksum-capable";

            public ChecksumOnlyMirror(string knownMd5, int setId)
            {
                this.knownMd5 = knownMd5;
                this.setId = setId;
            }

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
            {
                Calls++;

                return Task.FromResult(r.Query == knownMd5
                    ? new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = setId } }
                    : new List<BeatmapSetInfo>());
            }

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new System.NotSupportedException();
        }
    }
}
