#nullable enable

using System.Linq;
using JukeBox.Game.Online;
using JukeBox.Game.Replays;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// The "Played by …" credit a set shows for its replays: up to two players are named, and more
    /// than two collapse to a count so a big multi-replay set does not spill a paragraph of names
    /// into the one-line credit. Pure, so the wording is pinned without a game host.
    /// </summary>
    [TestFixture]
    public class BeatmapSetInfoTest
    {
        private static BeatmapSetInfo withPlayers(params string[] names) => new BeatmapSetInfo
        {
            Replays = names.Select(n => new ReplayAttachment { PlayerName = n }).ToArray(),
        };

        [Test]
        public void NoReplaysCreditsNobody()
            => Assert.That(new BeatmapSetInfo().PlayerRoster, Is.Empty);

        [Test]
        public void OneReplayNamesThePlayer()
            => Assert.That(withPlayers("Alice").PlayerRoster, Is.EqualTo("Alice"));

        [Test]
        public void TwoReplaysListBothNames()
            => Assert.That(withPlayers("Alice", "Bob").PlayerRoster, Is.EqualTo("Alice, Bob"));

        [Test]
        public void MoreThanTwoReplaysCollapseToACount()
        {
            Assert.That(withPlayers("Alice", "Bob", "Carol").PlayerRoster, Is.EqualTo("3 players"));
            Assert.That(withPlayers("a", "b", "c", "d", "e").PlayerRoster, Is.EqualTo("5 players"));
        }

        [Test]
        public void AnEmptyNameStillCounts()
            => Assert.That(withPlayers("Alice", "").PlayerRoster, Is.EqualTo("Alice, an unknown player"));
    }
}
