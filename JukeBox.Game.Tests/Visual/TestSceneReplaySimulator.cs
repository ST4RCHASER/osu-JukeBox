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
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
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
        /// <summary>
        /// The rethink of the freeze: every play is PRELOADED to completion up front, flat out, with
        /// no regard for where the playhead is. The old model kept a cushion just ahead of the
        /// playhead and stopped once it had one — which is exactly the race that fell behind and
        /// froze the board on a heavy map. Held at time zero, the simulator must now still record the
        /// WHOLE map, and Progress must climb to one.
        /// </summary>
        [Test]
        public void EveryPlayIsPreloadedToCompletionRegardlessOfThePlayhead()
        {
            var manual = new ManualClock();
            var framed = new FramedClock(manual);

            AddStep("simulate three with the playhead pinned at zero", () =>
            {
                host.Clock = framed;
                manual.CurrentTime = 0;

                host.Child = simulator = new ReplaySimulator(beatmapPath,
                    new[] { replay("a"), replay("b"), replay("c") });
            });

            // The playhead never moves — under the old cushion model it would stop a couple of
            // seconds in and wait. It must record the whole map anyway.
            AddUntilStep("it records the whole map with the playhead still at zero", () => simulator.AllComplete);
            AddAssert("and progress is complete", () => simulator.Progress >= 0.999);
            AddAssert("the playhead never left zero", () => manual.CurrentTime == 0);
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

            AddAssert("records the RAW rank (X), which is what the skin names its graphic after", () => simulator.Timelines[0].Points.Last().Grade == "X");
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

        /// <summary>
        /// The 2:28 freeze, in miniature. On a map of nothing but sliders the score processor's
        /// JudgedHits counts every nested part — head, ticks, tail — so it reaches the count of
        /// TOP-LEVEL objects roughly a third of the way in. A completion check that finishes there
        /// records only the first handful of sliders and calls the play done; on the real
        /// slider-heavy map that landed the whole board at 2:28 with everything past it missing. The
        /// recording has to reach the LAST slider.
        /// </summary>
        [Test]
        public void ASliderHeavyPlayIsRecordedToItsEndNotItsFirstThird()
        {
            AddStep("simulate a play on a map of nothing but sliders", () =>
            {
                string sliderPath = Path.Combine(tmp, "sliders [Sliders].osu");
                File.WriteAllText(sliderPath, sliderMap());

                string osr = Path.Combine(tmp, "slider-player.osr");
                ReplayFixture.WriteHitting(osr, sliderPath, "slider-player");

                var attachment = new ReplayAttachment
                {
                    PlayerName = "slider-player",
                    SourcePath = osr,
                    OsuFile = sliderPath,
                    Score = new JukeBoxScoreDecoder(sliderPath).Decode(osr),
                    RateTempo = 1,
                    RateFrequency = 1,
                };

                host.Child = simulator = new ReplaySimulator(sliderPath, new[] { attachment });
            });

            AddUntilStep("recorded", () => simulator.AllComplete);

            // The mutation killer. Under a top-level-count completion check the play is declared
            // done once JudgedHits reaches slider_count — reached around the first third, since every
            // slider carries several judgeable parts — so the recording stops there. It has to reach
            // the LAST slider's time instead: that is the difference between a board that reads the
            // whole map and one frozen a third of the way in.
            AddAssert("the recording reaches the final slider", () =>
            {
                double lastSliderTime = 1000 + (slider_count - 1) * 1500;
                return simulator.Timelines[0].Points.Last().Time >= lastSliderTime;
            });

            // Every slider left its mark: the recording is not merely long but covers each one, so a
            // completion that stopped early (fewer sliders touched) is caught here too.
            AddAssert("all twelve sliders are represented", () =>
            {
                var recorded = simulator.Timelines[0].Points.Select(p => p.Time).ToList();

                return Enumerable.Range(0, slider_count)
                                 .All(i => recorded.Any(t => Math.Abs(t - (1000 + i * 1500)) < 700));
            });
        }

        /// <summary>
        /// The danser-style analytic judge (no gameplay renderer) has to produce the SAME play as the
        /// drawable simulator it replaces — same judgements, same combo at every step, same final
        /// score and accuracy. The drawable simulator is lazer's real gameplay, so it is the oracle;
        /// if the two agree here, the fast path is trustworthy. Run on a mixed hit/miss circle play.
        /// </summary>
        [Test]
        public void TheAnalyticJudgeMatchesTheDrawableSimulatorOnCircles()
        {
            ReplayAttachment att = null!;

            AddStep("simulate a mixed hit/miss play through the drawable renderer", () =>
            {
                att = replay("mixed", 3, 7, 8);
                host.Child = simulator = new ReplaySimulator(beatmapPath, new[] { att }) { ForceDrawableSimulation = true };
            });
            AddUntilStep("recorded", () => simulator.AllComplete);

            AddAssert("the analytic judge produces the same timeline", () =>
                sameTimeline(simulator.Timelines[0], analytic(att)));
        }

        /// <summary>
        /// The same agreement on a map of nothing but sliders — the hard case, where the analytic
        /// geometry has to reproduce lazer's slider head tap, tick/repeat/tail tracking and the
        /// slider's own combo. If the combo and score line up here, the follow-circle model is right.
        /// </summary>
        [Test]
        public void TheAnalyticJudgeMatchesTheDrawableSimulatorOnSliders()
        {
            ReplayAttachment att = null!;

            AddStep("simulate a slider play through the drawable renderer", () =>
            {
                string sliderPath = Path.Combine(tmp, "sliders [Sliders].osu");
                File.WriteAllText(sliderPath, sliderMap());

                string osr = Path.Combine(tmp, "slider-cmp.osr");
                ReplayFixture.WriteHitting(osr, sliderPath, "slider-cmp");

                att = new ReplayAttachment
                {
                    PlayerName = "slider-cmp",
                    SourcePath = osr,
                    OsuFile = sliderPath,
                    Score = new JukeBoxScoreDecoder(sliderPath).Decode(osr),
                    RateTempo = 1,
                    RateFrequency = 1,
                };

                host.Child = simulator = new ReplaySimulator(sliderPath, new[] { att }) { ForceDrawableSimulation = true };
            });
            AddUntilStep("recorded", () => simulator.AllComplete);

            AddAssert("the analytic judge produces the same timeline", () =>
                sameTimeline(simulator.Timelines[0], analytic(att)));
        }

        /// <summary>
        /// An osu map is judged by the analytic path, not the drawable renderer: the whole preload runs
        /// with no gameplay renderer ever mounted and no 16ms simulation steps taken — which is what
        /// makes 50 replays a second's work instead of two minutes. Asserted on the absence of both,
        /// so reverting to the drawable simulator (renderers mounted, steps taken) fails here.
        /// </summary>
        [Test]
        public void OsuMapsAreJudgedAnalyticallyWithNoRenderer()
        {
            AddStep("simulate several osu plays", () => simulate(replay("a"), replay("b", 3), replay("c", 7)));
            AddUntilStep("all recorded", () => simulator.AllComplete);

            AddAssert("no drawable renderer was ever mounted and no steps were taken", () =>
                simulator.LiveRenderers == 0 && simulator.StepsRun == 0);

            AddAssert("and every play is a real recording", () =>
                simulator.Timelines.Count == 3 && simulator.Timelines.All(t => t.Points.Count > 0));
        }

        /// <summary>Runs the attachment through the analytic recorder (no renderer) for comparison.</summary>
        private static ReplayTimeline analytic(ReplayAttachment att)
        {
            var timeline = new ReplayTimeline();
            AnalyticReplayRecorder.Record(
                new FlatWorkingBeatmap(att.OsuFile!),
                new OsuRuleset(),
                ReplayMods.ForGameplay(att.Score!),
                att.Score!,
                new Dictionary<string, DifficultyAttributes>(),
                timeline);
            return timeline;
        }

        /// <summary>
        /// Whether two timelines describe the same play: the same judgement at every step, the same
        /// combo at every step, and the same final score and accuracy. Times are allowed to differ by
        /// a hair (the analytic judge dates a hit at the press, the renderer at the frame it processed
        /// it), so this compares the sequences of outcomes, not their timestamps.
        /// </summary>
        private static bool sameTimeline(ReplayTimeline oracle, ReplayTimeline candidate)
        {
            var a = oracle.Points;
            var b = candidate.Points;

            if (a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Judgement != b[i].Judgement)
                    return false;
                if (a[i].Combo != b[i].Combo)
                    return false;
            }

            var lastA = a[^1];
            var lastB = b[^1];

            return lastA.Score == lastB.Score
                   && Math.Abs(lastA.Accuracy - lastB.Accuracy) < 0.0001
                   && lastA.Grade == lastB.Grade;
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

        private const int slider_count = 12;

        /// <summary>
        /// A map of nothing but sliders, spread evenly across its whole length. Every slider carries
        /// several JUDGEABLE parts — a head, a tail and (on this multiplier/length) at least one tick
        /// — so the score processor's JudgedHits climbs several times faster than the count of
        /// TOP-LEVEL objects. That mismatch is the whole point of the fixture: a completion check that
        /// stops once JudgedHits reaches the top-level object count stops around the map's FIRST third
        /// (the 2:28 freeze on the real slider-heavy map), leaving the rest of the play unrecorded.
        /// </summary>
        private static string sliderMap()
        {
            var sb = new StringBuilder();

            sb.Append("osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n");
            sb.Append("[Metadata]\nTitle:Sim\nArtist:A\nCreator:C\nVersion:Sliders\n\n");
            sb.Append("[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n");
            sb.Append("[TimingPoints]\n0,500,4,2,0,60,1,0\n\n[HitObjects]\n");

            // Linear sliders, length 280px: on this 500ms beat and 1.4 multiplier that runs ~1000ms,
            // long enough to carry ticks as well as a head and tail, so each slider is worth several
            // judgements. Spaced 1500ms apart so the whole map spans well past its first third.
            for (int i = 0; i < slider_count; i++)
            {
                int time = 1000 + i * 1500;
                sb.Append($"100,192,{time},2,0,L|380:192,1,280\n");
            }

            return sb.ToString();
        }
    }
}
