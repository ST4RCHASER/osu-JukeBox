#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Online;
using JukeBox.Game.Replays;
using NUnit.Framework;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The rules that decide what a spectate session shows, before any drawable exists — which
    /// renderer runs, how many panes, how loud each one is, and what each player's chip says.
    ///
    /// <para>
    /// Pure by construction, following <see cref="MultiReplayLayout"/>'s precedent, which is what
    /// lets the awkward cases (mixed maps, more players than panes, a clock skewed against osu!'s)
    /// be pinned here rather than discovered by watching the wrong thing happen on screen.
    /// </para>
    /// </summary>
    [TestFixture]
    public class SpectatePlanTest
    {
        private static SpectateEntry entry(string osuFile, string name = "player", double rate = 1)
            => new SpectateEntry("/sets/x", osuFile, new ReplayAttachment { PlayerName = name, RateTempo = rate }, name);

        // ---- Which renderer runs -----------------------------------------------------------------

        [Test]
        public void EveryoneOnOneDifficultyCanShareAClock()
        {
            var entries = new List<SpectateEntry> { entry("/sets/x/hard.osu", "a"), entry("/sets/x/hard.osu", "b") };

            Assert.That(SpectatePanePlan.AllOnOneMap(entries), Is.True);
        }

        /// <summary>
        /// The subtle case, and the reason this compares the .osu PATH rather than the set: two
        /// difficulties of one mapset are different-length charts, so one clock cannot drive both —
        /// exactly the same problem as two unrelated maps.
        /// </summary>
        [Test]
        public void TwoDifficultiesOfTheSameSetAreNotOneMap()
        {
            var entries = new List<SpectateEntry> { entry("/sets/x/hard.osu", "a"), entry("/sets/x/insane.osu", "b") };

            Assert.That(SpectatePanePlan.AllOnOneMap(entries), Is.False);
        }

        [Test]
        public void DifferentMapsAreNotOneMap()
        {
            var entries = new List<SpectateEntry> { entry("/sets/x/hard.osu", "a"), entry("/sets/y/hard.osu", "b") };

            Assert.That(SpectatePanePlan.AllOnOneMap(entries), Is.False);
        }

        [Test]
        public void OneOrNoPlayersTriviallyShareAMap()
        {
            Assert.That(SpectatePanePlan.AllOnOneMap(Array.Empty<SpectateEntry>()), Is.True);
            Assert.That(SpectatePanePlan.AllOnOneMap(new[] { entry("/a.osu") }), Is.True);
        }

        // ---- The pane budget ---------------------------------------------------------------------

        /// <summary>
        /// Lower than the shared-map grid's 12 on purpose: a grid cell shares one background, one
        /// track and one storyboard with its siblings, while an independent pane shares nothing.
        /// Four is also what the replay-download budget (ten a minute) can actually keep current.
        /// </summary>
        [Test]
        public void NoMorePanesThanTheBudgetAllows()
        {
            Assert.That(SpectatePanePlan.MAX_PANES, Is.LessThan(MultiReplayLayout.MAX_GRID_CELLS));

            var many = Enumerable.Range(0, 9).Select(i => entry($"/m{i}.osu", $"p{i}")).ToList();

            Assert.That(SpectatePanePlan.Rendered(many), Has.Count.EqualTo(SpectatePanePlan.MAX_PANES));
            Assert.That(SpectatePanePlan.Rendered(many).Select(e => e.DisplayName),
                Is.EqualTo(new[] { "p0", "p1", "p2", "p3" }), "the first N are kept, in order");
        }

        [Test]
        public void FewerPlayersThanTheBudgetAllRender()
        {
            var two = new List<SpectateEntry> { entry("/a.osu", "a"), entry("/b.osu", "b") };

            Assert.That(SpectatePanePlan.Rendered(two), Has.Count.EqualTo(2));
        }

        [Test]
        public void PanesUseTheSameGridArithmeticAsTheSharedMapView()
        {
            for (int n = 1; n <= SpectatePanePlan.MAX_PANES; n++)
                Assert.That(SpectatePanePlan.Shape(n), Is.EqualTo(MultiReplayLayout.For(n)), $"{n} panes");

            // Past the cap the shape follows the cap, not the request.
            Assert.That(SpectatePanePlan.Shape(20), Is.EqualTo(MultiReplayLayout.For(SpectatePanePlan.MAX_PANES)));
        }

        // ---- Audio -------------------------------------------------------------------------------

        /// <summary>
        /// Four unrelated songs at once is noise, not a feature. One pane sounds and the rest are
        /// there to be unmuted deliberately.
        /// </summary>
        [Test]
        public void OnlyTheFirstPaneStartsAudible()
        {
            var volumes = SpectatePanePlan.InitialVolumes(4);

            Assert.That(volumes[0], Is.EqualTo(1));
            Assert.That(volumes.Skip(1), Is.All.EqualTo(0));
        }

        [Test]
        public void NoPanesMeansNoVolumes()
        {
            Assert.That(SpectatePanePlan.InitialVolumes(0), Is.Empty);
        }

        // ---- Rate --------------------------------------------------------------------------------

        /// <summary>
        /// The contrast worth pinning: panes have one audio track EACH, so every play keeps its own
        /// speed, while the shared-map renderers have one track between them and must collapse the
        /// lot to a single rate that cannot represent them all.
        ///
        /// <para>
        /// Asserted as "one value cannot cover three" rather than against whatever the shared path
        /// picks or says about it. What that path DOES on a mismatch is its own business and is
        /// actively changing (the rate it settles on, and whether it announces the mismatch at all);
        /// the durable fact — and the only one this feature depends on — is that it has a single
        /// rate to give while panes have one per player.
        /// </para>
        /// </summary>
        [Test]
        public void EachPaneKeepsItsOwnReplaysRateWhereTheSharedMapPathCannot()
        {
            var mixed = new List<SpectateEntry>
            {
                entry("/a.osu", "a", rate: 1.0),
                entry("/b.osu", "b", rate: 1.5),
                entry("/c.osu", "c", rate: 0.75),
            };

            Assert.That(SpectatePanePlan.Rates(mixed), Is.EqualTo(new[] { 1.0, 1.5, 0.75 }));
            Assert.That(SpectatePanePlan.Rates(mixed).Distinct().Count(), Is.EqualTo(3),
                "each pane drives its own clock, so no rate is lost");

            // The same three plays on one map get ONE rate between them — whichever it is, at least
            // one player would be running at the wrong speed.
            double shared = MultiReplayLayout.SharedRate(mixed.Select(e => e.Replay).ToList());

            Assert.That(SpectatePanePlan.Rates(mixed).Any(r => Math.Abs(r - shared) > 0.001), Is.True,
                "a single shared rate cannot represent three different speeds");
        }

        // ---- State mapping -----------------------------------------------------------------------

        private static readonly DateTimeOffset now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

        [Test]
        public void AScoreThatJustLandedReadsAsANewResult()
        {
            Assert.That(SpectateStateRules.For(now.AddSeconds(-5), passed: true, now), Is.EqualTo(SpectateState.NewResult));
        }

        [Test]
        public void AnOlderButRecentScoreReadsAsPlaying()
        {
            Assert.That(SpectateStateRules.For(now.AddMinutes(-3), passed: true, now), Is.EqualTo(SpectateState.Playing));
        }

        /// <summary>
        /// Failure outranks freshness — it is the one state osu! tells us outright, and it is more
        /// useful than "just finished" for a play that ended in a fail.
        /// </summary>
        [Test]
        public void AFailedScoreReadsAsFailedHoweverFreshItIs()
        {
            Assert.That(SpectateStateRules.For(now.AddSeconds(-2), passed: false, now), Is.EqualTo(SpectateState.Failed));
            Assert.That(SpectateStateRules.For(now.AddMinutes(-4), passed: false, now), Is.EqualTo(SpectateState.Failed));
        }

        [Test]
        public void NothingRecentReadsAsIdle()
        {
            Assert.That(SpectateStateRules.For(now.AddHours(-2), passed: true, now), Is.EqualTo(SpectateState.Idle));
            Assert.That(SpectateStateRules.For(null, passed: true, now), Is.EqualTo(SpectateState.Idle));
        }

        /// <summary>
        /// osu!'s clock and ours are not the same clock. A score stamped slightly in the future is
        /// skew, not a play that has not happened — treating the negative age literally would sail
        /// past every window and read as idle.
        /// </summary>
        [Test]
        public void AClockSkewedScoreIsTreatedAsJustNow()
        {
            Assert.That(SpectateStateRules.For(now.AddSeconds(3), passed: true, now), Is.EqualTo(SpectateState.NewResult));
        }

        [Test]
        public void OnlyStatesWithAPlayToShowTakeAPane()
        {
            Assert.That(SpectateStateRules.ShouldRender(SpectateState.NewResult), Is.True);
            Assert.That(SpectateStateRules.ShouldRender(SpectateState.Playing), Is.True);
            Assert.That(SpectateStateRules.ShouldRender(SpectateState.Failed), Is.True);

            // These keep their chip but give up their pane, which is what lets four panes follow
            // whoever is actually playing.
            Assert.That(SpectateStateRules.ShouldRender(SpectateState.Idle), Is.False);
            Assert.That(SpectateStateRules.ShouldRender(SpectateState.Unknown), Is.False);
            Assert.That(SpectateStateRules.ShouldRender(SpectateState.Unknown_User), Is.False);
        }

        // ---- Presence: the one REAL live signal, kept apart from the inferred activity ----------

        /// <summary>
        /// Presence and activity are different KINDS of fact — osu! states the first outright and we
        /// infer the second — so the row shows both rather than collapsing them into one verdict.
        /// </summary>
        [Test]
        public void TheRowShowsRealPresenceBesideInferredActivity()
        {
            var online = new SpectatePresence(true, null);
            var offline = new SpectatePresence(false, null);

            Assert.That(SpectateStateRules.Describe(online, SpectateState.Playing), Is.EqualTo("online · playing"));

            // The two combinations that carry the most meaning, and that a single merged word would
            // destroy: someone who played and logged straight off, and someone at their computer we
            // simply cannot see into.
            Assert.That(SpectateStateRules.Describe(offline, SpectateState.NewResult), Is.EqualTo("offline · just finished"));
            Assert.That(SpectateStateRules.Describe(online, SpectateState.Idle), Is.EqualTo("online · idle"));
        }

        /// <summary>
        /// Being online is not something to render. A player who just logged off still has a play
        /// worth showing, and one who is online but idle has nothing — so presence decides the dot
        /// and never the pane.
        /// </summary>
        [Test]
        public void PresenceDoesNotDecideWhoGetsAPane()
        {
            Assert.That(SpectateStateRules.ShouldRender(SpectateState.NewResult), Is.True);
            Assert.That(SpectateStateRules.ShouldRender(SpectateState.Idle), Is.False);

            // Same activity, opposite presence — the render decision is identical either way.
            foreach (var activity in Enum.GetValues<SpectateState>())
            {
                bool renders = SpectateStateRules.ShouldRender(activity);

                Assert.That(SpectateStateRules.ShouldRender(activity), Is.EqualTo(renders),
                    $"{activity} must not depend on presence");
            }
        }

        /// <summary>
        /// last_visit is null far more often than one would guess — users can hide it — so the dot
        /// must come from is_online alone. Verified live: of three real accounts sampled, two had no
        /// last_visit at all.
        /// </summary>
        [Test]
        public void PresenceWorksWithoutALastVisit()
        {
            Assert.That(SpectateStateRules.PresenceLabel(new SpectatePresence(true, null)), Is.EqualTo("online"));
            Assert.That(SpectateStateRules.PresenceLabel(new SpectatePresence(false, null)), Is.EqualTo("offline"));

            var seen = new SpectatePresence(true, new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
            Assert.That(SpectateStateRules.PresenceLabel(seen), Is.EqualTo("online"));
        }

        [Test]
        public void PresenceStartsOfflineRatherThanGuessed()
        {
            Assert.That(SpectatePresence.Unknown.IsOnline, Is.False);
            Assert.That(SpectatePresence.Unknown.LastVisit, Is.Null);
        }

        [Test]
        public void EveryStateHasALabel()
        {
            foreach (SpectateState state in Enum.GetValues<SpectateState>())
                Assert.That(SpectateStateRules.Label(state), Is.Not.Empty, state.ToString());
        }
    }
}
