#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        public void IncludesModeParamWhenSet()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Query = "camellia", Mode = "t" });
            Assert.That(url, Does.Contain("m=t"));
        }

        [Test]
        public void OmitsModeParamWhenNull()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Query = "camellia" });
            Assert.That(url, Does.Not.Contain("m="));
        }

        [Test]
        public void StarRangeRoutesViaB64Body()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest
            {
                Query = "camellia",
                Mode = "o",
                Status = "loved",
                Sort = "plays_asc",
                Extra = SearchExtra.Video,
                Page = 3,
                PageSize = 30,
                MinStars = 4.5,
                MaxStars = 6.0,
            });

            // Everything rides inside the b64 body — no plain legacy params alongside it.
            Assert.That(url, Does.StartWith("https://api.nerinyan.moe/search?b64="));
            Assert.That(url, Does.Not.Contain("&"));

            string b64 = Uri.UnescapeDataString(url.Split("b64=")[1]);
            using var body = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            var root = body.RootElement;

            Assert.That(root.GetProperty("query").GetString(), Is.EqualTo("camellia"));
            Assert.That(root.GetProperty("m").GetString(), Is.EqualTo("o"));
            Assert.That(root.GetProperty("s").GetString(), Is.EqualTo("loved"));
            Assert.That(root.GetProperty("sort").GetString(), Is.EqualTo("plays_asc"));
            Assert.That(root.GetProperty("extra").GetString(), Is.EqualTo("video"));
            Assert.That(root.GetProperty("page").GetInt32(), Is.EqualTo(3));
            Assert.That(root.GetProperty("pageSize").GetInt32(), Is.EqualTo(30));
            Assert.That(root.GetProperty("difficultyRating").GetProperty("min").GetDouble(), Is.EqualTo(4.5));
            Assert.That(root.GetProperty("difficultyRating").GetProperty("max").GetDouble(), Is.EqualTo(6.0));
        }

        [Test]
        public void B64BodyDefaultsOpenEndedBoundsAndClampsPage()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Page = 500, PageSize = 50, MinStars = 5 });

            string b64 = Uri.UnescapeDataString(url.Split("b64=")[1]);
            using var body = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            var root = body.RootElement;

            Assert.That(root.GetProperty("difficultyRating").GetProperty("min").GetDouble(), Is.EqualTo(5));
            Assert.That(root.GetProperty("difficultyRating").GetProperty("max").GetDouble(), Is.EqualTo(10));
            Assert.That(root.GetProperty("page").GetInt32(), Is.EqualTo(199)); // 199*50 < 10000, 200*50 hits the cap
        }

        [Test]
        public void NoB64WithoutStarRange()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Query = "camellia", Mode = "o" });
            Assert.That(url, Does.Not.Contain("b64="));
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
