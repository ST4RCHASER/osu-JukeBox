#nullable enable

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    public class StubHandler : HttpMessageHandler
    {
        public string ResponseBody = "[]";
        public string? LastUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody)
            });
        }
    }

    [TestFixture]
    public class NerinyanMirrorTest
    {
        [Test]
        public void BuildsLegacySearchUrl()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Query = "camellia", Extra = SearchExtra.Storyboard, Page = 2, PageSize = 50 });
            Assert.That(url, Does.StartWith("https://api.nerinyan.moe/search?"));
            Assert.That(url, Does.Contain("q=camellia"));
            Assert.That(url, Does.Contain("e=storyboard"));
            Assert.That(url, Does.Contain("p=2"));
            Assert.That(url, Does.Not.Contain("/v2/"));
        }

        [Test]
        public void ClampsPageBelow10kWindow()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Page = 500, PageSize = 50 });
            Assert.That(url, Does.Contain("p=199"));  // 199*50 < 10000, 200*50 hits the cap
        }

        [Test]
        public void IncludesOptionParamWhenSet()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Query = "123", Option = "setId" });
            Assert.That(url, Does.Contain("option=setId"));
        }

        [Test]
        public void OmitsOptionParamWhenNull()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Query = "camellia" });
            Assert.That(url, Does.Not.Contain("option="));
        }

        [Test]
        public async Task SearchParsesResponse()
        {
            var handler = new StubHandler { ResponseBody = "[{\"id\":1,\"title\":\"t\",\"artist\":\"a\",\"creator\":\"c\",\"beatmaps\":[]}]" };
            var mirror = new NerinyanMirror(new HttpClient(handler));
            var results = await mirror.SearchAsync(new SearchRequest());
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo(1));
        }
    }
}
