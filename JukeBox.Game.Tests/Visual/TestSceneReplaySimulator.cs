#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using JukeBox.Game.Tests.Import;
using osu.Framework.Timing;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The off-screen playthrough that produces each player's recorded timeline.
    ///
    /// <para>
    /// Driven with REAL replays that hit and miss at known objects, because the whole value of the
    /// timeline is that it says what happened and when. A fixture that misses everything (or hits
    /// everything) cannot tell a correct recording from an empty one.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneReplaySimulator : JukeBoxTestScene
    {
        private string tmp = null!;
        private string beatmapPath = null!;
        private Container host = null!;
        private ReplaySimulator simulator = null!;

        private const int object_count = 12;
        private const int first_object_ms = 1000;
        private const int spacing_ms = 400;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
            beatmapPath = Path.Combine(tmp, "map [Hard].osu");
            File.WriteAllText(beatmapPath, map());

            Clear();
            Add(host = new Container { RelativeSizeAxes = Axes.Both });
        });

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(tmp))
                    Directory.Delete(tmp, true);
            }
            catch (IOException)
            {
            }
        }

        private ReplayAttachment replay(string player, params int[] misses)
        {
            string osr = Path.Combine(tmp, player + ".osr");
            ReplayFixture.WriteHitting(osr, beatmapPath, player, misses);

            return new ReplayAttachment
            {
                PlayerName = player,
                SourcePath = osr,
                OsuFile = beatmapPath,
                Score = new JukeBoxScoreDecoder(beatmapPath).Decode(osr),
                RateTempo = 1,
                RateFrequency = 1,
            };
        }

        private ReplayAttachment replayWithMods(string player, params Mod[] mods)
        {
            string osr = Path.Combine(tmp, player + ".osr");
            ReplayFixture.WriteHitting(osr, beatmapPath, player, osuTK.Vector2.Zero, mods);

            return new ReplayAttachment
            {
                PlayerName = player,
                SourcePath = osr,
                OsuFile = beatmapPath,
                Score = new JukeBoxScoreDecoder(beatmapPath).Decode(osr),
                RateTempo = 1,
                RateFrequency = 1,
            };
        }

        private void simulate(params ReplayAttachment[] replays)
            => host.Child = simulator = new ReplaySimulator(beatmapPath, replays);

        /// <summary>Time of the nth hit object, which is where its judgement lands.</summary>
        private static double timeOf(int index) => first_object_ms + index * spacing_ms;

        [Test]
        public void ItRecordsACleanPlayAsPerfectThroughout()
        {
            AddStep("simulate one clean play", () => simulate(replay("clean")));
            AddUntilStep("recorded to the end", () => simulator.AllComplete);

            AddAssert("every object is on the timeline", () =>
                simulator.Timelines[0].Points.Count == object_count);

            AddAssert("combo climbs one per object", () =>
                simulator.Timelines[0].Points.Select(p => p.Combo).SequenceEqual(Enumerable.Range(1, object_count)));

            AddAssert("and nothing ever broke", () =>
                simulator.Timelines[0].Points.All(p => !p.BrokeCombo && Math.Abs(p.Accuracy - 1) < 0.001));
        }

        /// <summary>
        /// The property the design exists for: the recorded answer depends on the TIME asked about
        /// and nothing else. A running total would only match if it happened to have been fed in
        /// step with the question.
        /// </summary>
        [Test]
        public void TheTimelineAnswersByTimeNotByPlaybackOrder()
        {
            AddStep("simulate a clean play", () => simulate(replay("clean")));
            AddUntilStep("recorded", () => simulator.AllComplete);

            AddAssert("early, mid and late read differently and in order", () =>
            {
                var timeline = simulator.Timelines[0];

                return timeline.At(timeOf(0)).Combo == 1
                       && timeline.At(timeOf(5)).Combo == 6
                       && timeline.At(timeOf(11)).Combo == object_count;
            });

            AddAssert("and asking backwards gives the same answers", () =>
            {
                var timeline = simulator.Timelines[0];

                int late = timeline.At(timeOf(11)).Combo;
                int mid = timeline.At(timeOf(5)).Combo;
                int early = timeline.At(timeOf(0)).Combo;

                return early == 1 && mid == 6 && late == object_count;
            });
        }

        /// <summary>
        /// A miss has to land at the object it happened on, not merely somewhere. This is what the
        /// knockout rule reads, so an off-by-one here is a player eliminated at the wrong moment.
        /// </summary>
        [Test]
        public void AMissIsRecordedAsABreakAtTheObjectItHappenedOn()
        {
            AddStep("simulate a play that misses the seventh object", () => simulate(replay("fumbler", 6)));
            AddUntilStep("recorded", () => simulator.AllComplete);

            AddAssert("exactly one break, at that object", () =>
            {
                var breaks = simulator.Timelines[0].Points.Where(p => p.BrokeCombo).ToList();

                return breaks.Count == 1 && Math.Abs(breaks[0].Time - timeOf(6)) < 200;
            });

            // How BIG the break was, which is what decides whether it is worth announcing on the
            // playfield. The combo AFTER a break is always zero and says nothing about its cost, so
            // this has to be captured before it is overwritten. Missing the seventh object ends a
            // run of six.
            AddAssert("the size of the lost combo is recorded", () =>
                simulator.Timelines[0].Points.First(p => p.BrokeCombo).ComboLost == 6);

            AddAssert("and is zero on judgements that broke nothing", () =>
                simulator.Timelines[0].Points.Where(p => !p.BrokeCombo).All(p => p.ComboLost == 0));

            // Read either side of the BREAK's own recorded time rather than the object's. A miss is
            // judged when its hit window expires, which is tens of milliseconds after the object,
            // so asking at the object time still reads the play as intact.
            AddAssert("combo is held right up to the break, then gone", () =>
            {
                var timeline = simulator.Timelines[0];
                double breakAt = timeline.Points.First(p => p.BrokeCombo).Time;

                return timeline.At(breakAt - 1).Combo == 6
                       && timeline.At(breakAt).Combo == 0;
            });

            AddAssert("and is rebuilt from one afterwards", () =>
                simulator.Timelines[0].Points.Last().Combo == object_count - 1 - 6);
        }

        /// <summary>
        /// A miss on the FIRST object is not a combo break — there was no combo to break. Getting
        /// this wrong knocks a player out before the map has started.
        /// </summary>
        [Test]
        public void AMissOnTheVeryFirstObjectIsNotABreak()
        {
            AddStep("simulate a play that misses the opener", () => simulate(replay("slowstarter", 0)));
            AddUntilStep("recorded", () => simulator.AllComplete);

            AddAssert("no break recorded", () => simulator.Timelines[0].Points.All(p => !p.BrokeCombo));
            AddAssert("but the accuracy did drop", () => simulator.Timelines[0].Points[0].Accuracy < 1);
        }

        /// <summary>
        /// The hidden renderers are scaffolding and have to come down. Each is a whole gameplay
        /// renderer — the same weight as a visible cell — and the only thing wanted from it is a
        /// list of numbers. Keeping them alive would double the renderers for the entire song;
        /// measured on a twelve-cell grid, dropping them is the difference between a steady-state
        /// update cost of 1.02ms per frame and 0.27ms.
        /// </summary>
        [Test]
        public void TheHiddenRenderersAreDisposedOnceThePlayIsRecorded()
        {
            AddStep("simulate three", () => simulate(replay("a"), replay("b", 3), replay("c", 7)));

            AddUntilStep("all recorded", () => simulator.AllComplete);
            AddUntilStep("and every renderer is gone", () => simulator.LiveRenderers == 0);

            AddAssert("while the recordings themselves remain", () =>
                simulator.Timelines.Count == 3 && simulator.Timelines.All(t => t.Points.Count > 0));
        }

        /// <summary>
        /// Every play is scored under the mods THAT play was set with.
        ///
        /// <para>
        /// The board's scores are computed in these hidden layers, and they used to consult the
        /// Chart tab's shared mod selection. That selection is a single-replay idea: it follows the
        /// now-playing item, which carries one replay, and seeds itself from that replay's mods. So
        /// with several replays on screen every player was scored under player ONE's mods. It was
        /// reported as "grid applies one player's HD to all cells", but the same shared answer was
        /// corrupting the numbers, which is the worse half — 46 of 47 rows wrong on a board whose
        /// only purpose is comparing them.
        /// </para>
        /// </summary>
        [Test]
        public void EachPlayIsScoredUnderItsOwnRecordedMods()
        {
            AddStep("simulate a no-mod play and a Hard Rock play", () =>
            {
                simulate(replay("plain"), replayWithMods("hardrock", new OsuModHardRock()));
            });

            AddUntilStep("both plays recorded", () => simulator.AllComplete);

            AddAssert("one was scored clean and the other under Hard Rock", () =>
            {
                var acronyms = simulator.SimulatedMods
                                        .Select(m => string.Join(string.Empty, m.Select(x => x.Acronym)))
                                        .ToList();

                return acronyms.Count == 2
                       && acronyms.Count(a => a.Contains("HR", StringComparison.Ordinal)) == 1
                       && acronyms.Count(a => !a.Contains("HR", StringComparison.Ordinal)) == 1;
            });

            // The assertion above is necessary and NOT sufficient, which is worth stating because
            // it fooled me: with no shared selection active in a test scene, each layer falls back
            // to its recorded mods anyway, so it passes whether or not the flag is set. Removing
            // the flag from the construction site left it green. This one reads the flag off the
            // layer that did the scoring, so losing it fails here.
            AddAssert("because each play ignored any shared selection", () =>
                simulator.EveryPlaySimulatedUnderItsOwnMods);
        }

        /// <summary>
        /// The simulation stops once every play has a cushion ahead of the playhead, and picks the
        /// work back up as the playhead advances.
        ///
        /// <para>
        /// Half of the fix for the numbers freezing partway through a song at high replay counts.
        /// The other half is that the budget goes to whichever play is furthest behind: round-robin
        /// advanced all of them in lockstep, so with 47 replays the whole board arrived late
        /// together. Measured on a five-minute map, round-robin at 47 ran 1.72x faster than
        /// realtime with nothing else competing for the update thread — under 1x once the app is
        /// also drawing, which is exactly the freeze that was reported. Laggard-first plus this
        /// cushion took the same case to 5.2x.
        /// </para>
        /// </summary>
        [Test]
        public void SimulationStopsAtItsCushionAndResumesAsThePlayheadMoves()
        {
            var manual = new ManualClock();
            var framed = new FramedClock(manual);

            // A deliberately short cushion, so where it stops is somewhere this test can see rather
            // than most of the way through the map.
            AddStep("simulate three with a short cushion", () =>
            {
                host.Clock = framed;
                manual.CurrentTime = 0;

                host.Child = simulator = new ReplaySimulator(beatmapPath,
                    new[] { replay("a"), replay("b"), replay("c") })
                {
                    LookaheadMs = 2500,
                };
            });

            AddUntilStep("it builds the cushion", () => simulator.SimulatedTo >= 2500);

            AddStep("let it run on", () =>
            {
                for (int i = 0; i < 120; i++)
                    host.UpdateSubTree();
            });

            AddAssert("then stops, rather than recording the whole map", () => !simulator.AllComplete);

            AddStep("move the playhead to the end", () =>
            {
                manual.CurrentTime = timeOf(object_count - 1) + 2000;
                framed.ProcessFrame();
            });

            AddUntilStep("it picks the work back up", () => simulator.AllComplete);
        }

        /// <summary>
        /// The board's grade and pp columns carry real values, and the grade is named the way a
        /// PLAYER names it. lazer's enum calls a perfect play X, which is right internally and on
        /// screen reads as a cross rather than as the best grade there is — the first capture of
        /// this board showed "X" against every 100% row.
        /// </summary>
        [Test]
        public void ACleanPlayIsGradedSSWithRealPerformancePoints()
        {
            AddStep("simulate a clean play", () => simulate(replay("clean")));
            AddUntilStep("recorded", () => simulator.AllComplete);

            AddAssert("graded SS, not X", () => simulator.Timelines[0].Points.Last().Grade == "SS");
            AddAssert("with pp above zero", () => simulator.Timelines[0].Points.Last().Performance > 0);
        }

        [Test]
        public void EachPlayerGetsTheirOwnRecordAndNobodyElses()
        {
            AddStep("simulate three plays that fail at different points", () =>
                simulate(replay("clean"), replay("early", 2), replay("late", 9)));

            AddUntilStep("all recorded", () => simulator.AllComplete);

            AddAssert("the breaks land where each player's own misses were", () =>
            {
                double?[] breaks = simulator.Timelines
                                            .Select(t => t.Points.FirstOrDefault(p => p.BrokeCombo).Time as double?)
                                            .ToArray();

                bool cleanNeverBroke = !simulator.Timelines[0].Points.Any(p => p.BrokeCombo);

                return cleanNeverBroke
                       && Math.Abs(simulator.Timelines[1].Points.First(p => p.BrokeCombo).Time - timeOf(2)) < 200
                       && Math.Abs(simulator.Timelines[2].Points.First(p => p.BrokeCombo).Time - timeOf(9)) < 200;
            });
        }

        /// <summary>
        /// Knockout read off the recorded plays: the rule and the recording have to agree about who
        /// is still in it at a given moment, which is the entire feature in one assertion.
        /// </summary>
        [Test]
        public void TheKnockoutRuleReadsTheRecordedPlaysCorrectly()
        {
            AddStep("simulate a clean play and two that break", () =>
                simulate(replay("survivor"), replay("early", 2), replay("late", 9)));

            AddUntilStep("all recorded", () => simulator.AllComplete);

            AddAssert("the field thins in the right order", () =>
            {
                var rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0);
                var timelines = simulator.Timelines;

                return rules.AliveCount(timelines, 0) == 3
                       && rules.AliveCount(timelines, timeOf(5)) == 2
                       && rules.AliveCount(timelines, timeOf(11)) == 1
                       && rules.AliveAt(timelines[0], timeOf(11));
            });

            AddAssert("and showcase keeps everyone in", () =>
                new KnockoutRules().AliveCount(simulator.Timelines, timeOf(11)) == 3);
        }

        private static string map()
        {
            var sb = new StringBuilder();

            sb.Append("osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n");
            sb.Append("[Metadata]\nTitle:Sim\nArtist:A\nCreator:C\nVersion:Hard\n\n");
            sb.Append("[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n");
            sb.Append("[TimingPoints]\n0,500,4,2,0,60,1,0\n\n[HitObjects]\n");

            for (int i = 0; i < object_count; i++)
                sb.Append($"{100 + i * 37 % 350},{80 + i * 53 % 240},{(int)timeOf(i)},1,0,0:0:0:0:\n");

            return sb.ToString();
        }
    }
}
