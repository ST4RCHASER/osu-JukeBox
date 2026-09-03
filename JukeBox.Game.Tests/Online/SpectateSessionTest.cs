#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Tests.Import;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// The poller, driven one round at a time against a fake osu! and an explicit clock.
    ///
    /// <para>
    /// The fake answers with real data shapes and the beatmap and replay on disk are REAL — the
    /// .osu is a fixture and the .osr is written by lazer's own encoder — so what is stubbed out is
    /// the network and nothing else. The decode, the difficulty match and the entry construction
    /// are the production ones.
    /// </para>
    /// </summary>
    [TestFixture]
    public class SpectateSessionTest
    {
        private static readonly DateTimeOffset start = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

        private string root = null!;
        private string osuFile = null!;
        private string checksum = null!;
        private CachedBeatmapSet set = null!;
        private DateTimeOffset now;

        [SetUp]
        public void SetUp()
        {
            now = start;

            root = Path.Combine(Path.GetTempPath(), "jukebox-spectate-" + Guid.NewGuid().ToString("N"));
            string setDirectory = Path.Combine(root, "1");
            Directory.CreateDirectory(setDirectory);

            osuFile = Path.Combine(setDirectory, "map.osu");
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "happy_people_easy.osu"), osuFile);

            checksum = ReplayFixture.Md5OfFile(osuFile);

            set = new CachedBeatmapSet
            {
                SetId = 1,
                Directory = setDirectory,
                OsuFiles = new List<string> { osuFile },
                PreferredOsuFile = osuFile,
            };
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }

        private SpectateSession session(FakeApi api, ReplayDownloadBudget? budget = null)
        {
            api.OsuFile = osuFile;

            return new SpectateSession(api, budget ?? new ReplayDownloadBudget(),
                (_, _) => Task.FromResult(set),
                Path.Combine(root, "replays"),
                () => now);
        }

        private SpectateScore score(long id, DateTimeOffset endedAt, bool passed = true, bool hasReplay = true)
            => new SpectateScore(id, 1, checksum, "Easy", endedAt, passed, hasReplay);

        [Test]
        public async Task AUsernameBecomesAnIdAndOsusOwnSpellingOfTheName()
        {
            var api = new FakeApi();
            api.Users["mrekk"] = new SpectateUser(7562902, "mrekk", new SpectatePresence(true, null));

            var poller = session(api);
            poller.SetWatched(new[] { "MREKK" });

            await poller.PollAsync();

            var player = poller.Players.Single();

            // The typed name is replaced by osu!'s, so the row reads the way the player's profile
            // does rather than the way it was typed.
            Assert.That(player.UserId, Is.EqualTo(7562902));
            Assert.That(player.Username, Is.EqualTo("mrekk"));
        }

        [Test]
        public async Task AnUnknownUsernameIsAskedAboutOnceAndThenLeftAlone()
        {
            var api = new FakeApi();

            var poller = session(api);
            poller.SetWatched(new[] { "nosuchplayer" });

            await poller.PollAsync();
            await poller.PollAsync();
            await poller.PollAsync();

            Assert.That(api.ResolveCalls, Is.EqualTo(1), "a name that does not exist must not be re-asked every round");
            Assert.That(poller.Players.Single().Activity, Is.EqualTo(SpectateState.Unknown_User));
        }

        [Test]
        public async Task TheSameScoreIsNeverDownloadedTwice()
        {
            var api = new FakeApi();
            api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));
            api.Scores[1] = score(500, start);

            var poller = session(api);
            poller.SetWatched(new[] { "mrekk" });

            await poller.PollAsync();
            await poller.PollAsync();
            await poller.PollAsync();

            Assert.That(api.Downloads, Is.EqualTo(new[] { 500L }));
        }

        [Test]
        public async Task ANewScoreIdIsWhatTriggersTheNextDownload()
        {
            var api = new FakeApi();
            api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));
            api.Scores[1] = score(500, start);

            var poller = session(api);
            poller.SetWatched(new[] { "mrekk" });

            await poller.PollAsync();

            now += TimeSpan.FromSeconds(30);
            api.Scores[1] = score(501, now);

            await poller.PollAsync();

            Assert.That(api.Downloads, Is.EqualTo(new[] { 500L, 501L }));
            Assert.That(poller.Rendered.Single().Replay.SourcePath, Does.EndWith("501.osr"));
        }

        [Test]
        public async Task NoMoreThanTheBudgetIsSpentInAMinuteHoweverManyPlayersLand()
        {
            var api = new FakeApi();

            var names = new List<string>();

            for (int i = 1; i <= ReplayDownloadBudget.MAX_PER_WINDOW + 4; i++)
            {
                string name = $"player{i}";
                names.Add(name);
                api.Users[name] = new SpectateUser(i, name, new SpectatePresence(true, null));
                api.Scores[i] = score(1000 + i, start);
            }

            var poller = session(api);
            poller.SetWatched(names);

            await poller.PollAsync();

            Assert.That(api.Downloads.Count, Is.EqualTo(ReplayDownloadBudget.MAX_PER_WINDOW));

            // Still nothing more within the same minute, however many rounds run.
            now += TimeSpan.FromSeconds(30);
            await poller.PollAsync();

            Assert.That(api.Downloads.Count, Is.EqualTo(ReplayDownloadBudget.MAX_PER_WINDOW));

            // Past the window, the ones that were held off get their turn.
            now = start + ReplayDownloadBudget.WINDOW + TimeSpan.FromSeconds(1);
            await poller.PollAsync();

            Assert.That(api.Downloads.Count, Is.EqualTo(ReplayDownloadBudget.MAX_PER_WINDOW + 4));
        }

        [Test]
        public async Task APlayerWaitingOnTheBudgetIsToldSoRatherThanLookingIdle()
        {
            var api = new FakeApi();

            var names = new List<string>();

            for (int i = 1; i <= ReplayDownloadBudget.MAX_PER_WINDOW + 1; i++)
            {
                string name = $"player{i}";
                names.Add(name);
                api.Users[name] = new SpectateUser(i, name, new SpectatePresence(true, null));
                api.Scores[i] = score(1000 + i, start);
            }

            var poller = session(api);
            poller.SetWatched(names);

            await poller.PollAsync();

            var waiting = poller.Players.Single(p => p.Entry == null);

            Assert.That(waiting.Status, Does.Contain("download slot"));
        }

        [Test]
        public async Task A429StopsDownloadingAndSaysWhy()
        {
            var api = new FakeApi { ThrottleDownloads = true };
            api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));
            api.Scores[1] = score(500, start);

            var poller = session(api);
            poller.SetWatched(new[] { "mrekk" });

            await poller.PollAsync();

            Assert.That(poller.Players.Single().Status, Does.Contain("download limit"));

            // The next round must not try again — the backoff outlasts a whole window.
            api.ThrottleDownloads = false;
            now += TimeSpan.FromSeconds(30);
            await poller.PollAsync();

            Assert.That(api.Downloads, Is.Empty, "a 429 has to stop the NEXT round too, not just the one it happened in");
        }

        [Test]
        public async Task APlayWithNoReplayCostsNoBudget()
        {
            var api = new FakeApi();
            api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));
            api.Scores[1] = score(500, start, hasReplay: false);

            var budget = new ReplayDownloadBudget();
            var poller = session(api, budget);
            poller.SetWatched(new[] { "mrekk" });

            await poller.PollAsync();

            Assert.That(api.Downloads, Is.Empty);
            Assert.That(budget.Remaining(now), Is.EqualTo(ReplayDownloadBudget.MAX_PER_WINDOW),
                "a play with nothing to watch must not spend a download that another player could use");
            Assert.That(poller.Players.Single().Status, Does.Contain("no replay"));
        }

        [Test]
        public async Task AFailedPlayStillGetsAPaneAndReadsAsFailed()
        {
            var api = new FakeApi();
            api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));
            api.Scores[1] = score(500, start, passed: false);

            var poller = session(api);
            poller.SetWatched(new[] { "mrekk" });

            await poller.PollAsync();

            Assert.That(poller.Players.Single().Activity, Is.EqualTo(SpectateState.Failed));
            Assert.That(poller.Rendered.Count, Is.EqualTo(1), "a failed play is still a play worth watching");
        }

        [Test]
        public async Task APlayerWhoStopsPlayingGivesUpTheirPaneWithoutLeavingTheList()
        {
            var api = new FakeApi();
            api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));
            api.Scores[1] = score(500, start);

            var poller = session(api);
            poller.SetWatched(new[] { "mrekk" });

            await poller.PollAsync();
            Assert.That(poller.Rendered.Count, Is.EqualTo(1));

            now = start + SpectateStateRules.ACTIVE_WINDOW + TimeSpan.FromMinutes(1);
            await poller.PollAsync();

            Assert.That(poller.Rendered, Is.Empty, "a stale play must release its pane for someone who is playing");
            Assert.That(poller.Players.Single().Activity, Is.EqualTo(SpectateState.Idle));

            // And the play itself is let go, not merely hidden: an entry still held here keeps its
            // .osr protected from the sweep for as long as the app runs.
            Assert.That(poller.Players.Single().Entry, Is.Null);
        }

        [Test]
        public async Task PresenceIsReportedSeparatelyFromWhatTheySeemToBeDoing()
        {
            var api = new FakeApi();
            api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));

            var poller = session(api);
            poller.SetWatched(new[] { "mrekk" });

            await poller.PollAsync();

            var player = poller.Players.Single();

            // Online is a fact osu! stated; idle is our inference from having seen no play. Both
            // have to survive into the status, or the row would be forced to pick one.
            Assert.That(player.Presence.IsOnline, Is.True);
            Assert.That(player.Activity, Is.EqualTo(SpectateState.Idle));
            Assert.That(player.Status, Is.EqualTo("online · idle"));
        }

        [Test]
        public async Task PresenceComesFromOneRequestForTheWholeList()
        {
            var api = new FakeApi();

            for (int i = 1; i <= 5; i++)
                api.Users[$"player{i}"] = new SpectateUser(i, $"player{i}", new SpectatePresence(true, null));

            var poller = session(api);
            poller.SetWatched(api.Users.Keys.ToList());

            await poller.PollAsync();

            Assert.That(api.PresenceCalls, Is.EqualTo(1), "five players must not cost five presence requests");
            Assert.That(api.PresenceBatchSizes.Single(), Is.EqualTo(5));
        }

        [Test]
        public async Task OnlyFourPlaysAreRenderedAndTheyAreTheNewest()
        {
            var api = new FakeApi();

            for (int i = 1; i <= 6; i++)
            {
                api.Users[$"player{i}"] = new SpectateUser(i, $"player{i}", new SpectatePresence(true, null));

                // player6 finished most recently, player1 longest ago.
                api.Scores[i] = score(1000 + i, start - TimeSpan.FromMinutes(6 - i));
            }

            var poller = session(api);
            poller.SetWatched(api.Users.Keys.ToList());

            await poller.PollAsync();

            var shown = poller.Rendered.Select(e => e.DisplayName).ToList();

            Assert.That(shown.Count, Is.EqualTo(Game.Replays.SpectatePanePlan.MAX_PANES));
            Assert.That(shown, Is.EquivalentTo(new[] { "player3", "player4", "player5", "player6" }));
        }

        [Test]
        public async Task TheWallIsOrderedByNameSoALandedPlayDoesNotReshuffleIt()
        {
            var api = new FakeApi();

            foreach (string name in new[] { "carol", "alice", "bob" })
            {
                int id = api.Users.Count + 1;
                api.Users[name] = new SpectateUser(id, name, new SpectatePresence(true, null));
                api.Scores[id] = score(1000 + id, start - TimeSpan.FromSeconds(id));
            }

            var poller = session(api);
            poller.SetWatched(new[] { "carol", "alice", "bob" });

            await poller.PollAsync();

            // Recency picks WHO is on the wall; it must not decide WHERE, because a pane cannot be
            // moved without being rebuilt, and rebuilding restarts its audio.
            Assert.That(poller.Rendered.Select(e => e.DisplayName), Is.EqualTo(new[] { "alice", "bob", "carol" }));
        }

        [Test]
        public async Task EditingTheListKeepsWhatIsAlreadyLoadedForEveryoneWhoStays()
        {
            var api = new FakeApi();
            api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));
            api.Users["peppy"] = new SpectateUser(2, "peppy", new SpectatePresence(true, null));
            api.Scores[1] = score(500, start);
            api.Scores[2] = score(600, start);

            var poller = session(api);
            poller.SetWatched(new[] { "mrekk", "peppy" });

            await poller.PollAsync();
            Assert.That(api.Downloads.Count, Is.EqualTo(2));

            poller.SetWatched(new[] { "mrekk" });
            await poller.PollAsync();

            // Removing one player must not re-download the other's play — an edit would otherwise
            // cost the whole budget.
            Assert.That(api.Downloads.Count, Is.EqualTo(2));
            Assert.That(api.ResolveCalls, Is.EqualTo(2));
        }

        [Test]
        public async Task ABeatmapThatCannotBeFetchedIsReportedAndNotRetriedForever()
        {
            var api = new FakeApi();
            api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));
            api.Scores[1] = score(500, start);

            api.OsuFile = osuFile;

            var poller = new SpectateSession(api, new ReplayDownloadBudget(),
                (_, _) => Task.FromException<CachedBeatmapSet>(new IOException("mirror down")),
                Path.Combine(root, "replays"), () => now);

            poller.SetWatched(new[] { "mrekk" });

            await poller.PollAsync();
            await poller.PollAsync();

            Assert.That(poller.Players.Single().Entry, Is.Null);
            Assert.That(poller.Players.Single().Status, Does.Contain("couldn't load"));
            Assert.That(api.Downloads.Count, Is.EqualTo(1), "a play whose beatmap will not come must not be retried every round");
        }

        [Test]
        public async Task APollWithNobodyWatchedTouchesNothing()
        {
            var api = new FakeApi();

            var poller = session(api);

            Assert.That(await poller.PollAsync(), Is.False);
            Assert.That(api.PresenceCalls, Is.Zero);
        }

        /// <summary>osu!, stubbed at the network boundary and nowhere deeper.</summary>
        private class FakeApi : ISpectateApi
        {
            public readonly Dictionary<string, SpectateUser> Users = new Dictionary<string, SpectateUser>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<int, SpectateScore> Scores = new Dictionary<int, SpectateScore>();
            public readonly List<long> Downloads = new List<long>();
            public readonly List<int> PresenceBatchSizes = new List<int>();

            public int ResolveCalls;
            public int PresenceCalls;
            public bool ThrottleDownloads;

            public Task<SpectateUser?> ResolveUserAsync(string username, CancellationToken ct = default)
            {
                ResolveCalls++;
                return Task.FromResult(Users.TryGetValue(username, out var user) ? user : (SpectateUser?)null);
            }

            public Task<IReadOnlyList<SpectateUser>> PresenceAsync(IReadOnlyList<int> userIds, CancellationToken ct = default)
            {
                PresenceCalls++;
                PresenceBatchSizes.Add(userIds.Count);

                IReadOnlyList<SpectateUser> found = Users.Values.Where(u => userIds.Contains(u.Id)).ToList();
                return Task.FromResult(found);
            }

            public Task<SpectateScore?> LatestScoreAsync(int userId, CancellationToken ct = default)
                => Task.FromResult(Scores.TryGetValue(userId, out var score) ? score : (SpectateScore?)null);

            public Task DownloadReplayAsync(long scoreId, string destinationPath, CancellationToken ct = default)
            {
                if (ThrottleDownloads)
                    throw new SpectateThrottledException("slow down");

                Downloads.Add(scoreId);

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                // A REAL .osr, written by lazer's own encoder — so the decode, the mods and the
                // rate that follow are the production path rather than a stub agreeing with itself.
                ReplayFixture.WriteHitting(destinationPath, OsuFile, $"player{scoreId}");

                return Task.CompletedTask;
            }

            public string OsuFile = string.Empty;
        }
    }
}
