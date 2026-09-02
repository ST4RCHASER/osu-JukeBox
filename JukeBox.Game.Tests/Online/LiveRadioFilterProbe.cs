#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Bindables;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// Live probe against osu.ppy.sh, run by hand — [Explicit] so the suite never depends on the
    /// network or on the machine having credentials. It exists because every other test in this
    /// area asserts on a URL or a bindable, and the one thing those cannot show is whether osu!
    /// actually HONOURS the filters we send: an ignored parameter (which is precisely how
    /// general=featured_artists behaves) produces a perfectly well-formed request and completely
    /// unfiltered songs.
    /// </summary>
    [TestFixture]
    [Explicit("hits the live osu! API using the local install's credentials")]
    public class LiveRadioFilterProbe
    {
        private static (string id, string secret) credentials()
        {
            string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "JukeBox", "game.ini");

            string id = "", secret = "";

            foreach (string line in File.ReadAllLines(ini))
            {
                string[] parts = line.Split('=', 2);

                if (parts.Length != 2)
                    continue;

                if (parts[0].Trim() == nameof(JukeBoxSetting.OsuClientId)) id = parts[1].Trim();
                if (parts[0].Trim() == nameof(JukeBoxSetting.OsuClientSecret)) secret = parts[1].Trim();
            }

            return (id, secret);
        }

        [Test]
        public async Task ManiaFourToSixStarPicksReallyAreManiaFourToSix()
        {
            var (id, secret) = credentials();
            Assert.That(id, Is.Not.Empty, "no osu! client id configured locally");

            var official = new OfficialBeatmapSearch(new HttpClient(),
                new Bindable<string>(id), new Bindable<string>(secret));

            var filters = new RadioFilters();
            filters.Mode.Value = RadioRuleset.Mania;
            filters.MinStars.Value = 4;
            filters.MaxStars.Value = 6;

            var radio = new RadioService(new NerinyanMirror(new HttpClient()),
                official: official, searchApi: new Bindable<SearchApi>(SearchApi.Official), filters: filters);

            var picks = new List<BeatmapSetInfo>();

            for (int i = 0; i < 5; i++)
            {
                var pick = await radio.PickRandomAsync();

                Assert.That(pick.Set, Is.Not.Null, $"pick {i + 1} came back empty: {pick.Failure}");
                Assert.That(pick.FromCache, Is.False, "this probe is about the live search, not the cache");

                picks.Add(pick.Set!);
            }

            foreach (var set in picks)
            {
                var matching = set.Beatmaps.Where(b => b.Mode == "mania"
                                                      && b.DifficultyRating >= 4 && b.DifficultyRating <= 6).ToList();

                TestContext.Out.WriteLine(
                    $"{set.Id,8}  {set.DisplayArtist} - {set.DisplayTitle}  |  matching mania diffs: " +
                    string.Join(", ", matching.Select(b => $"{b.Version} {b.DifficultyRating:0.00}*")));

                Assert.That(matching, Is.Not.Empty,
                    $"set {set.Id} has no mania difficulty between 4 and 6 stars — the filter was ignored");
            }

            Assert.That(picks.Select(s => s.Id).Distinct().Count(), Is.GreaterThan(1),
                "every pick was the same set — the filters narrowed it to a single answer");
        }

        [Test]
        public async Task FeaturedArtistsPicksReallyAreFeaturedArtistTracks()
        {
            var (id, secret) = credentials();
            Assert.That(id, Is.Not.Empty, "no osu! client id configured locally");

            var official = new OfficialBeatmapSearch(new HttpClient(),
                new Bindable<string>(id), new Bindable<string>(secret));

            var filters = new RadioFilters();
            filters.FeaturedArtists.Value = true;

            var radio = new RadioService(new NerinyanMirror(new HttpClient()),
                official: official, searchApi: new Bindable<SearchApi>(SearchApi.Official), filters: filters);

            for (int i = 0; i < 4; i++)
            {
                var pick = await radio.PickRandomAsync();

                Assert.That(pick.Set, Is.Not.Null, $"pick {i + 1} came back empty: {pick.Failure}");

                // TrackId is osu-web's Featured Artist link — the very field c=featured_artists
                // filters on, and the only proof the parameter did anything.
                TestContext.Out.WriteLine($"{pick.Set!.Id,8}  {pick.Set.DisplayArtist} - {pick.Set.DisplayTitle}  |  track_id: {pick.Set.TrackId?.ToString() ?? "none"}");

                Assert.That(pick.Set.TrackId, Is.Not.Null,
                    $"set {pick.Set.Id} is not a Featured Artist track — the filter was ignored");
            }
        }
    }
}
