#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using JukeBox.Game.Tests.Import;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Graphics.Sprites;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Combine mode in its knockout shape: one rendered chart carrying everyone's cursor, with a
    /// live scoreboard that re-orders itself as the plays diverge.
    ///
    /// <para>
    /// These are unusually exact for scene tests, and that is the point of the design. The board
    /// reads each player's RECORDED play at whatever time the clock says, so once the simulation
    /// has finished, moving the clock to a chosen moment and asserting what the board shows is
    /// deterministic — no stepping gameplay, no waiting to see what happens.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneMultiReplayCombine : JukeBoxTestScene
    {
        private string tmp = null!;
        private string beatmapPath = null!;

        private MultiReplayCombine combine = null!;
        private Container host = null!;

        private readonly ManualClock manual = new ManualClock();
        private FramedClock framed = null!;

        private const int object_count = 12;
        private const int first_object_ms = 1000;
        private const int spacing_ms = 400;

        private static double timeOf(int index) => first_object_ms + index * spacing_ms;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            beatmapPath = Path.Combine(tmp, "map [Hard].osu");
            File.WriteAllText(beatmapPath, osuWithObjects());

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

        private ReplayAttachment replayFor(string player, double tempo = 1, int[]? misses = null)
        {
            string osr = Path.Combine(tmp, player + ".osr");
            ReplayFixture.WriteHitting(osr, beatmapPath, player, misses ?? Array.Empty<int>());

            return new ReplayAttachment
            {
                PlayerName = player,
                SourcePath = osr,
                OsuFile = beatmapPath,
                Score = new JukeBoxScoreDecoder(beatmapPath).Decode(osr),
                RateTempo = tempo,
                RateFrequency = 1,
            };
        }

        private void build(int count, double[]? tempos = null, int[][]? misses = null)
        {
            var replays = Enumerable.Range(0, count)
                                    .Select(i => replayFor($"player{i}",
                                        tempos != null ? tempos[i] : 1,
                                        misses?[i]))
                                    .ToList();

            host.Child = combine = new MultiReplayCombine(beatmapPath, replays);

            // The test takes the clock IMMEDIATELY, at zero. Left on the scene's real-time clock
            // while the simulation finishes, the song plays itself for a few seconds in the
            // background — long enough to cross a combo break and fire its flash before the test
            // has looked at anything.
            host.Clock = framed = new FramedClock(manual);
            manual.CurrentTime = 0;
        }

        /// <summary>Puts the board at a chosen moment in the song and lets it settle there.</summary>
        private void showAt(double time)
        {
            manual.CurrentTime = time;

            // Enough frames for the re-order transforms to finish, so the board is asserted where
            // it has settled rather than mid-animation.
            for (int i = 0; i < 60; i++)
            {
                framed.ProcessFrame();
                host.UpdateSubTree();
            }
        }

        /// <summary>
        /// Every replay becomes a cursor over the ONE rendered chart — the driver included.
        ///
        /// <para>
        /// This used to expect one fewer, on the reasoning that the player driving the chart already
        /// had a cursor from the ruleset. They did, but it was the playfield's own white one, which
        /// cannot be tinted — so that player was the odd one out on a board where colour is the only
        /// thing tying a row to a cursor. All N are ours now.
        /// </para>
        /// </summary>
        [Test]
        public void EveryReplayBecomesACursorOverTheOneChart()
        {
            AddStep("build four", () => build(4));
            AddUntilStep("chart loaded", () => combine.IsLoaded && combine.Chart.IsLoaded);
            AddUntilStep("cursors attached", () => combine.CursorsAttached == 4);

            // Counted as "all the chart layers, minus the simulator's hidden ones". The simulator
            // runs a chart per replay off screen, so a bare count of LazerChartLayer would be
            // satisfied by those alone even if the visible chart were missing entirely.
            AddAssert("exactly one RENDERED chart", () =>
                combine.ChildrenOfType<LazerChartLayer>().Count()
                - combine.Simulator.ChildrenOfType<LazerChartLayer>().Count() == 1);
        }

        [Test]
        public void EveryPlayerGetsADistinctColour()
        {
            AddAssert("eight colours, all different", () =>
            {
                var colours = Enumerable.Range(0, 8).Select(i => MultiReplayCombine.ColourFor(i, 8)).ToList();
                return colours.Distinct().Count() == 8;
            });
        }

        /// <summary>
        /// The user's report: "cursor color i can still see single not see many color".
        ///
        /// <para>
        /// The cause was that lazer's ReplayAnalysisOverlay builds its cursor and path only when the
        /// osu! replay-analysis settings are switched on, and they are off by default — so combine
        /// mounted four EMPTY containers and the only cursor on screen was the playfield's own white
        /// one. Asserted here on cursors that exist as drawables carrying distinct colours, which an
        /// empty overlay cannot satisfy however it is tinted.
        /// </para>
        /// </summary>
        [Test]
        public void EveryPlayerGetsTheirOwnCursorInTheirOwnColour()
        {
            AddStep("build four", () => build(4));
            AddUntilStep("cursors attached", () => combine.CursorsAttached == 4);

            // FOUR, not three. The player driving the chart gets one of ours too — leaving them to
            // the playfield's own cursor left one player permanently white.
            AddAssert("one cursor per player", () => combine.ChildrenOfType<PlayerCursor>().Count() == 4);

            AddAssert("all four colours differ", () =>
                combine.Cursors.Where(c => c != null).Select(c => c!.Colour4).Distinct().Count() == 4);

            AddAssert("and each carries its player's name", () =>
            {
                var names = combine.ChildrenOfType<PlayerCursor>()
                                   .SelectMany(c => c.ChildrenOfType<OsuSpriteText>())
                                   .Select(t => t.Text.ToString()!)
                                   .Where(t => t.StartsWith("player", StringComparison.Ordinal))
                                   .ToList();

                return names.Distinct().Count() == 4;
            });
        }

        /// <summary>
        /// A cursor must be drawn where ITS player's hand was. Colour alone does not prove that:
        /// four correctly-coloured cursors reading one player's replay would look right in every
        /// assertion above and be a lie on screen.
        /// </summary>
        [Test]
        public void EachCursorFollowsItsOwnPlayersReplay()
        {
            // Each player's cursor sits a different distance from the object centres, so their
            // positions have to differ at any moment they are all on screen.
            AddStep("build three who play from different places", () => buildWithOffsets(3));
            AddUntilStep("cursors attached", () => combine.CursorsAttached == 3);

            AddStep("run the play", () => playTo(timeOf(4)));

            AddAssert("all three cursors are somewhere", () =>
                combine.ChildrenOfType<PlayerCursor>().All(c => c.HasPosition));

            AddAssert("and no two are in the same place", () =>
            {
                var positions = combine.ChildrenOfType<PlayerCursor>()
                                       .SelectMany(c => c.ChildrenOfType<Container>())
                                       .Select(c => c.Position)
                                       .ToList();

                return positions.Distinct().Count() >= 3;
            });
        }

        private void buildWithOffsets(int count)
        {
            var replays = Enumerable.Range(0, count).Select(i =>
            {
                string osr = Path.Combine(tmp, $"player{i}.osr");
                var offset = new osuTK.Vector2(i * 15, i * -10);

                ReplayFixture.WriteHitting(osr, beatmapPath, $"player{i}", offset);

                return new ReplayAttachment
                {
                    PlayerName = $"player{i}",
                    SourcePath = osr,
                    OsuFile = beatmapPath,
                    Score = new JukeBoxScoreDecoder(beatmapPath).Decode(osr),
                    RateTempo = 1,
                    RateFrequency = 1,
                };
            }).ToList();

            host.Child = combine = new MultiReplayCombine(beatmapPath, replays);
            host.Clock = framed = new FramedClock(manual);
            manual.CurrentTime = 0;
        }

        /// <summary>
        /// The playfield's own cursor is white and cannot be tinted, so it goes — otherwise the
        /// driving player has two cursors and the wrong one is colourless.
        /// </summary>
        [Test]
        public void ThePlayfieldsOwnCursorIsHidden()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("cursors attached", () => combine.CursorsAttached == 3);

            // Null is an acceptable answer as well as zero: the ruleset only builds a cursor when
            // the skin provides one, so a headless run can legitimately have none. What must never
            // happen is a VISIBLE one, which is the case this rules out.
            AddAssert("the ruleset's own cursor is not visible", () =>
                combine.Chart.DrawableRuleset is osu.Game.Rulesets.Osu.UI.DrawableOsuRuleset osuRuleset
                && osuRuleset.Playfield.Cursor?.Alpha is null or 0);
        }

        /// <summary>
        /// The user's other request: on a combo break the player's NAME blinks and flashes red for
        /// about a second, "like osu touny". A transient cue rather than the elimination state — it
        /// fires with knockout switched off, which is the default.
        /// </summary>
        [Test]
        public void ABrokenComboFlashesThatPlayersName()
        {
            AddStep("build two, one of whom breaks at the fifth object", () =>
                build(2, misses: new[] { Array.Empty<int>(), new[] { 4 } }));

            AddUntilStep("recorded", () => combine.Simulator.AllComplete);

            AddStep("play up to just before the break", () => playTo(timeOf(3)));
            AddAssert("nothing has flashed yet", () => combine.Board.Rows.All(r => r.ComboBreakFlashes == 0));

            AddStep("play across it", () => playTo(timeOf(7)));

            AddAssert("the player who broke is flashed, and only them", () =>
                rowFor(1).ComboBreakFlashes == 1 && rowFor(0).ComboBreakFlashes == 0);

            AddAssert("with knockout OFF, so they carry on playing", () =>
                combine.Rules.Mode == KnockoutMode.Showcase && rowFor(1).ShownAlive);
        }

        /// <summary>
        /// Advances the clock in steps rather than jumping. A break is spotted by the playhead
        /// CROSSING it, so a jump straight past one would step over the crossing entirely.
        /// </summary>
        private void playTo(double time)
        {
            while (manual.CurrentTime < time)
            {
                manual.CurrentTime = Math.Min(time, manual.CurrentTime + 50);
                framed.ProcessFrame();
                host.UpdateSubTree();
            }
        }

        [Test]
        public void EveryPlayerGetsARowOnTheBoard()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("board built", () => combine.Board?.Rows.Count == 3);

            // Scoped to the BOARD's rows. Searching the whole of combine now also finds the name
            // tags riding the cursors, so every player is legitimately named twice.
            AddAssert("all three named once on the board", () =>
            {
                var names = combine.Board.Rows
                                   .SelectMany(r => r.ChildrenOfType<OsuSpriteText>())
                                   .Select(t => t.Text.ToString()!)
                                   .Where(t => t.StartsWith("player", StringComparison.Ordinal))
                                   .ToList();

                return names.Count == 3 && names.Distinct().Count() == 3;
            });
        }

        /// <summary>
        /// The board shows the score a player HAD at this point in the song, not the one they
        /// finished with. This is the difference between a scoreboard and a results screen, and it
        /// is what a static rail reading the .osr header could never do.
        /// </summary>
        [Test]
        public void TheBoardShowsWhatEachPlayerHadAtThatMomentInTheSong()
        {
            AddStep("build two clean plays", () => build(2));
            AddUntilStep("recorded", () => combine.Simulator.AllComplete);

            AddStep("show the start of the song", () => showAt(0));
            AddAssert("nobody has scored yet", () => combine.Board.Rows.All(r => r.ComboText == "0x"));

            AddStep("show the middle", () => showAt(timeOf(5)));
            AddAssert("six hits in", () => combine.Board.Rows.All(r => r.ComboText == "6x"));

            AddStep("show the end", () => showAt(timeOf(object_count - 1)));
            AddAssert("the full combo is up", () => combine.Board.Rows.All(r => r.ComboText == $"{object_count}x"));
        }

        /// <summary>
        /// Seeking is the case the whole recorded-timeline design exists for. Arriving at a moment
        /// by jumping backwards from the end must read exactly the same as arriving by playing
        /// forwards — the running totals this replaced could not do that.
        /// </summary>
        [Test]
        public void TheBoardReadsTheSameWhicheverDirectionTheUserSeeksFrom()
        {
            AddStep("build two", () => build(2));
            AddUntilStep("recorded", () => combine.Simulator.AllComplete);

            string forwards = string.Empty;

            AddStep("play forwards to the middle", () =>
            {
                showAt(timeOf(2));
                showAt(timeOf(5));
                forwards = boardReading();
            });

            AddStep("jump to the end and come back", () =>
            {
                showAt(timeOf(object_count - 1));
                showAt(timeOf(5));
            });

            AddAssert("the board reads identically", () => boardReading() == forwards);
        }

        private string boardReading() => string.Join("|", combine.Board.Rows.Select(r => $"{r.ScoreText}/{r.ComboText}/{r.AccuracyText}"));

        /// <summary>
        /// Live sorting, which is the thing that makes a knockout watchable. The player who leads
        /// early must not be the one who leads late, or the test cannot tell a sorting board from a
        /// fixed one.
        /// </summary>
        [Test]
        public void TheBoardReordersAsPlayersOvertakeEachOther()
        {
            // player0 fumbles early and recovers; player1 is clean until late. So player1 leads at
            // the midpoint and player0 has caught back up by the end.
            AddStep("build two who fail at opposite ends", () =>
                build(2, misses: new[] { new[] { 1 }, new[] { 10 } }));

            AddUntilStep("recorded", () => combine.Simulator.AllComplete);

            AddStep("show the middle", () => showAt(timeOf(6)));
            AddAssert("the clean player leads", () => combine.Board.DisplayOrder[0] == 1);

            AddStep("show the end", () => showAt(timeOf(object_count - 1) + 500));
            AddAssert("and is overtaken once they drop theirs", () => combine.Board.DisplayOrder[0] == 0);
        }

        [Test]
        public void ShowcaseIsTheDefaultAndKnocksNobodyOut()
        {
            AddStep("build two, one of whom breaks combo", () =>
                build(2, misses: new[] { Array.Empty<int>(), new[] { 4 } }));

            AddUntilStep("recorded", () => combine.Simulator.AllComplete);
            AddStep("show the end", () => showAt(timeOf(object_count - 1) + 500));

            AddAssert("elimination is off unless asked for", () => combine.Rules.Mode == KnockoutMode.Showcase);
            AddAssert("so both players are still in it", () => combine.Board.Rows.All(r => r.ShownAlive));
        }

        /// <summary>
        /// The knockout itself: a player is in it before their break and out after, with the moment
        /// of the change being their own break rather than the map's end.
        /// </summary>
        [Test]
        public void TurningKnockoutOnEliminatesThePlayerWhoBreaksCombo()
        {
            AddStep("build two, one of whom breaks at the fifth object", () =>
                build(2, misses: new[] { Array.Empty<int>(), new[] { 4 } }));

            AddUntilStep("recorded", () => combine.Simulator.AllComplete);

            AddStep("switch to combo-break knockout", () =>
                combine.Rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0));

            AddStep("show a moment BEFORE the break", () => showAt(timeOf(3)));
            AddAssert("both still in it", () => combine.Board.Rows.All(r => r.ShownAlive));

            AddStep("show a moment after it", () => showAt(timeOf(6)));
            AddAssert("the one who broke is out", () => rowFor(1).ShownAlive == false);
            AddAssert("and the clean player is not", () => rowFor(0).ShownAlive);
            AddAssert("the eliminated player is still on the board", () => combine.Board.Rows.Count == 2);
            AddAssert("but has sunk below the survivor", () => combine.Board.DisplayOrder.Last() == 1);
        }

        /// <summary>
        /// Changing the rule mid-song must re-read the recorded plays rather than needing a reload —
        /// which is only possible because who is out is a question about data already gathered.
        /// </summary>
        [Test]
        public void ChangingTheRuleMidSongTakesEffectImmediately()
        {
            AddStep("build two, one of whom breaks", () =>
                build(2, misses: new[] { Array.Empty<int>(), new[] { 4 } }));

            AddUntilStep("recorded", () => combine.Simulator.AllComplete);
            AddStep("show a late moment", () => showAt(timeOf(9)));

            AddAssert("showcase: both in", () => combine.Board.Rows.All(r => r.ShownAlive));

            AddStep("turn knockout on without rebuilding", () =>
            {
                combine.Rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0);
                showAt(timeOf(9));
            });

            AddAssert("the same moment now reads as one player out", () => rowFor(1).ShownAlive == false);
        }

        private KnockoutBoard.Row rowFor(int playerIndex) => combine.Board.Rows.Single(r => r.PlayerIndex == playerIndex);

        /// <summary>
        /// Every eliminated player must be DRAWN as eliminated, not merely ranked below the
        /// survivors. Found by looking at a real capture: three players had broken combo, the board
        /// ordered all three below the one clean play — so the rule and the sort agreed — yet one of
        /// them was still drawn bright, because a row only restyles when its own state CHANGES and
        /// that comparison can be missed.
        /// </summary>
        [Test]
        public void EveryPlayerWhoBrokeComboIsDrawnAsOutNotJustRankedBelow()
        {
            AddStep("build four who break at different points", () => build(4, misses: new[]
            {
                Array.Empty<int>(),
                new[] { 3 },
                new[] { 6 },
                new[] { 9 },
            }));

            AddUntilStep("recorded", () => combine.Simulator.AllComplete);

            AddStep("knockout on, late in the map", () =>
            {
                combine.Rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0);
                showAt(timeOf(object_count - 1) + 500);
            });

            AddAssert("only the clean player is drawn alive", () =>
                rowFor(0).ShownAlive
                && !rowFor(1).ShownAlive
                && !rowFor(2).ShownAlive
                && !rowFor(3).ShownAlive);

            AddAssert("and the ranking agrees with the drawing", () =>
                combine.Board.DisplayOrder[0] == 0);
        }

        /// <summary>Same rule as the grid: mixed speeds play the map's own 1.0x, with nothing on
        /// screen about it.</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void NoSpeedWarningEverAppears(bool mixed)
        {
            AddStep("build two", () => build(2, mixed ? new[] { 1d, 1.5d } : null));
            AddUntilStep("loaded", () => combine.IsLoaded);

            AddAssert("nothing on screen mentions speeds", () => !combine.ChildrenOfType<OsuSpriteText>()
                                                                     .Any(t => t.Text.ToString()!.Contains("speed", StringComparison.OrdinalIgnoreCase)));
        }

        private static string osuWithObjects()
        {
            var sb = new StringBuilder();

            sb.Append("osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n");
            sb.Append("[Metadata]\nTitle:Combine Song\nArtist:Some Artist\nCreator:Some Mapper\nVersion:Hard\n\n");
            sb.Append("[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n");
            sb.Append("[TimingPoints]\n0,500,4,2,0,60,1,0\n\n[HitObjects]\n");

            for (int i = 0; i < object_count; i++)
                sb.Append($"{100 + i * 37 % 350},{80 + i * 53 % 240},{(int)timeOf(i)},1,0,0:0:0:0:\n");

            return sb.ToString();
        }
    }
}
