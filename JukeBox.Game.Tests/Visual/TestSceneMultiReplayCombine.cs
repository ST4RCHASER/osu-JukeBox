#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using JukeBox.Game.Tests.Import;
using System.Text.RegularExpressions;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Framework.Testing.Input;
using osu.Framework.Timing;
using osu.Game.Graphics.Sprites;
using osuTK.Graphics;

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
        private ManualInputManager input = null!;

        [Resolved]
        private PlayerOverrideStore overrideStore { get; set; } = null!;

        /// <summary>The replays behind the most recent build, so a test can key an override to one.</summary>
        private IReadOnlyList<ReplayAttachment> builtReplays = Array.Empty<ReplayAttachment>();

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

            // Wrapped in a manual input manager so the hover-to-focus test can drive real pointer
            // movement over the rail rows. UseParentInput stays on until a test asks to inject, so
            // every other test is unaffected.
            Add(input = new ManualInputManager
            {
                RelativeSizeAxes = Axes.Both,
                Child = host = new Container { RelativeSizeAxes = Axes.Both },
            });
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

            builtReplays = replays;
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

            // NAMES ARE NOT ON THE CURSORS, and that is the change rather than an oversight. They
            // were, briefly, and with a dozen players the tags overlapped into an unreadable pile —
            // the rail is where names belong. What tells cursors apart on the playfield is colour
            // and a coloured trail; the name comes back only at the instant of a combo break.
            AddAssert("no cursor carries a standing name tag", () =>
                combine.ChildrenOfType<PlayerCursor>()
                       .SelectMany(c => c.ChildrenOfType<OsuSpriteText>())
                       .All(t => !t.Text.ToString()!.StartsWith("player", StringComparison.Ordinal)));

            AddAssert("each has a trail in its own colour", () =>
            {
                var trails = combine.ChildrenOfType<PlayerCursorTrail>().ToList();
                return trails.Count == 4 && trails.Select(t => t.TrailColour).Distinct().Count() == 4;
            });

            AddStep("play a little", () => playTo(timeOf(3)));

            // Existing and coloured is not the same as DRAWING anything: a trail that never records
            // a point is an empty container with a colour, which every assertion above is happy
            // with. This is what makes it a trail.
            AddAssert("and every trail is actually tracking the cursor", () =>
                combine.ChildrenOfType<PlayerCursorTrail>().All(t => t.SegmentCount > 1));
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
        /// The row as the reference draws it, checked against danser's own renderer
        /// (knockoutoverlay.go) and the user's target render.
        ///
        /// <para>
        /// Four things, each visibly off before: the player's NAME carries their colour; the mods
        /// are a separate WHITE run beside it rather than folded into the name string; there is no
        /// dark strip behind a row, because the reference draws rows straight over the playfield and
        /// its background pass paints only the playfield boundary; and pp reads to two decimals.
        /// </para>
        /// </summary>
        [Test]
        public void TheRowIsDrawnTheWayTheReferenceDrawsIt()
        {
            AddStep("build two, one of them modded", buildWithMods);
            AddUntilStep("board built", () => combine.Board?.Rows.Count == 2);
            AddUntilStep("recorded", () => combine.Simulator.AllComplete);
            AddStep("show the end", () => showAt(timeOf(object_count - 1) + 500));

            // Asserted as "each name is its own colour, and none is white" rather than against exact
            // values: what matters is that the colour identifies the player, and an exact-value
            // check here would really be testing colour conversion.
            AddAssert("each name carries its own player's colour", () =>
            {
                var colours = combine.Board.Rows.Select(r => r.NameColour).ToList();

                return colours.Distinct().Count() == 2 && colours.All(c => c != Color4.White);
            });

            AddAssert("mods are a separate run, in white", () =>
            {
                var modded = combine.Board.Rows.Single(r => r.PlayerIndex == 1);

                return modded.ModsText == "+HR"
                       && modded.ModsColour == Color4.White
                       && !modded.NameText.Contains('+');
            });

            AddAssert("no dark strip behind the rows", () =>
                combine.Board.Rows.All(r => r.BackgroundColour.A == 0));

            AddAssert("pp reads to two decimals", () =>
                combine.Board.Rows.All(r => Regex.IsMatch(r.PerformanceText, @"^\d+\.\d\dpp$")));
        }

        private void buildWithMods()
        {
            var replays = new[]
            {
                replayFor("player0"),
                replayForWithMods("player1", new OsuModHardRock()),
            };

            host.Child = combine = new MultiReplayCombine(beatmapPath, replays);
            host.Clock = framed = new FramedClock(manual);
            manual.CurrentTime = 0;
        }

        private ReplayAttachment replayForWithMods(string player, params Mod[] mods)
        {
            string osr = Path.Combine(tmp, player + ".osr");
            ReplayFixture.WriteHitting(osr, beatmapPath, player, osuTK.Vector2.Zero, mods);

            return new ReplayAttachment
            {
                PlayerName = player,
                SourcePath = osr,
                OsuFile = beatmapPath,
                Score = new JukeBoxScoreDecoder(beatmapPath).Decode(osr),
                ModAcronyms = mods.Select(m => m.Acronym).ToArray(),
                RateTempo = 1,
                RateFrequency = 1,
            };
        }

        /// <summary>
        /// The rendered chart runs under the DRIVING player's own recorded mods, matching how their
        /// score is computed. Letting the shared Chart-tab selection edit it would put the play on
        /// screen and the numbers beside it under different mods.
        /// </summary>
        [Test]
        public void TheRenderedChartUsesTheDrivingPlayersOwnMods()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("chart loaded", () => combine.Chart.IsLoaded);

            AddAssert("it does not take its mods from the shared selection", () =>
                combine.Chart.UseRecordedReplayModsOnly);
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
        /// Combine mode does NOT flash the name on a break (that stays the grid's cue). Instead the
        /// player who just missed shows a recent-judgement "X" in the column after their score, so
        /// you can see who is dropping without the name jumping about. Asserted on the drawn column,
        /// not on a flash counter.
        /// </summary>
        [Test]
        public void ARecentMissShowsInTheJudgementColumnRatherThanFlashingTheName()
        {
            AddStep("build two, one of whom misses the fifth object", () =>
                build(2, misses: new[] { Array.Empty<int>(), new[] { 4 } }));

            AddUntilStep("recorded", () => combine.Simulator.AllComplete);

            AddStep("show just before the miss", () => showAt(timeOf(3)));
            AddAssert("nobody shows a judgement yet", () => combine.Board.Rows.All(r => r.JudgementText.Length == 0));

            AddStep("show just after the miss is judged", () => showAt(timeOf(4) + 250));

            AddAssert("the player who missed shows an X, and only them", () =>
                rowFor(1).JudgementText == "X" && rowFor(0).JudgementText.Length == 0);

            AddAssert("and the name was NOT flashed", () => combine.Board.Rows.All(r => r.ComboBreakFlashes == 0));

            AddStep("show well after the miss", () => showAt(timeOf(9)));
            AddAssert("the X has cleared once it is no longer recent", () => rowFor(1).JudgementText.Length == 0);
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

        /// <summary>Advances the song clock by a wall duration in small frames, so transforms timed
        /// on it — a focus fade, a break bubble's slide — actually progress rather than sitting
        /// frozen at whatever moment showAt left the clock.</summary>
        private void advanceBy(double ms)
        {
            double target = manual.CurrentTime + ms;

            while (manual.CurrentTime < target)
            {
                manual.CurrentTime = Math.Min(target, manual.CurrentTime + 16);
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

            // The ROLLED figures — score and pp — as well as the combo, which is not rolled. This
            // distinction is the whole point of the assertion: the rolled ones animate toward their
            // value, and when that animation never advanced they sat at zero for the entire song
            // while the combo beside them read correctly. Every test here passed throughout, because
            // they all happened to assert on the combo.
            AddAssert("and so are the score and pp, not left at zero", () =>
            {
                var timelines = combine.Simulator.Timelines;

                return combine.Board.Rows.All(r =>
                {
                    var point = timelines[r.PlayerIndex].At(timeOf(object_count - 1));

                    // Score is shown abbreviated (danser's 16.85M / 92.98K), so parse it back and
                    // allow the two-decimal rounding rather than an exact long match.
                    return Math.Abs(parseAbbreviatedScore(r.ScoreText) - point.Score) <= Math.Max(point.Score * 0.001, 20)
                           && Math.Abs(double.Parse(r.PerformanceText.Replace("pp", string.Empty)) - point.Performance) < 0.02;
                });
            });
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

        /// <summary>Parses the rail's abbreviated score ("16.85M", "92.98K", "900") back to a number.</summary>
        private static double parseAbbreviatedScore(string text)
            => text.EndsWith("M", StringComparison.Ordinal) ? double.Parse(text[..^1]) * 1_000_000
             : text.EndsWith("K", StringComparison.Ordinal) ? double.Parse(text[..^1]) * 1_000
             : double.Parse(text);

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

        /// <summary>
        /// Hovering a rail row is how you answer "which of these cursors is whose" on a playfield
        /// carrying a swarm of them: that player's cursor stays full strength while every other one
        /// steps back to a whisper, and leaving the row brings them all back. Asserted on the
        /// cursors' own focus channel, which is the effect on screen — not on the fact that a hover
        /// callback was wired.
        /// </summary>
        [Test]
        public void HoveringARailRowFocusesThatPlayersCursor()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("cursors attached", () => combine.CursorsAttached == 3);
            AddStep("settle mid-song", () => showAt(timeOf(5)));

            // Baseline: with nothing hovered every cursor draws at full focus.
            AddAssert("all cursors start at full focus", () =>
                combine.Cursors.Where(c => c != null).All(c => c!.FocusAlpha > 0.9f));

            AddStep("hover the second player's row", () =>
            {
                input.UseParentInput = false;
                input.MoveMouseTo(rowFor(1));
            });
            AddStep("let the focus fade settle", () => advanceBy(400));

            AddAssert("the hovered player's cursor stays bright", () => combine.Cursors[1]!.FocusAlpha > 0.9f);
            AddAssert("every other cursor steps back to a whisper", () =>
                combine.Cursors[0]!.FocusAlpha < 0.2f && combine.Cursors[2]!.FocusAlpha < 0.2f);

            AddStep("move the pointer off the rail", () => input.MoveMouseTo(combine));
            AddStep("let it settle back", () => advanceBy(400));

            AddAssert("all cursors return to full focus", () =>
                combine.Cursors.Where(c => c != null).All(c => c!.FocusAlpha > 0.9f));
        }

        /// <summary>
        /// The combo-break bubble as danser draws it: the breaker's name appears at the miss and
        /// SLIDES downward as it fades, rather than blinking in place. Asserted on the drawn marker
        /// actually travelling down the screen and fading in — a bubble that never moved, or never
        /// showed, would fail this even though the flash "fired".
        /// </summary>
        [Test]
        public void AComboBreakDropsASlidingBubble()
        {
            AddStep("build two, one of whom breaks at the fifth object", () =>
                build(2, misses: new[] { Array.Empty<int>(), new[] { 4 } }));

            AddUntilStep("recorded", () => combine.Simulator.AllComplete);

            // The playfield bubble is gated to sizeable breaks so it does not flicker at high player
            // counts; the fixture's twelve-object map can never reach the default 200, so drop the
            // gate to make the one break here worth announcing.
            AddStep("announce even a small break", () => combine.Rules = new KnockoutRules(BubbleMinimumCombo: 1));
            AddStep("play across the break", () => playTo(timeOf(5)));

            AddAssert("a red break bubble is on the breaker's cursor", () => breakBubble() != null);
            AddAssert("and it has faded in", () => breakBubble()!.Alpha > 0.5f);

            float startY = 0;
            AddStep("note where it starts", () => startY = breakBubble()!.Y);

            AddStep("let it slide", () => advanceBy(400));

            AddAssert("the bubble has slid downward", () => breakBubble()!.Y > startY + 2);
        }

        /// <summary>
        /// A per-player gameplay skin override builds that player's chart under the chosen bundled
        /// skin. Asserted on the skin the rendered chart actually built with — not on the store field.
        /// </summary>
        [Test]
        public void APerPlayerSkinOverrideBuildsTheChartUnderThatSkin()
        {
            AddStep("build two, the driver forced to Classic", () =>
            {
                var replays = new[] { replayFor("player0"), replayFor("player1") };
                overrideStore.SetSkin(replays[0], "Classic");

                builtReplays = replays;
                host.Child = combine = new MultiReplayCombine(beatmapPath, replays);
                host.Clock = framed = new FramedClock(manual);
                manual.CurrentTime = 0;
            });

            AddUntilStep("chart loaded", () => combine.Chart.IsLoaded);

            AddAssert("the rendered chart was built under Classic", () =>
                combine.Chart.SelectedSkin == JukeBox.Game.Configuration.JukeBoxSkin.Classic);
        }

        /// <summary>
        /// The knockout death animation: when a player is eliminated their name falls away on the
        /// playfield, coloured to their cursor, with their peak combo under it in combo-break mode.
        /// </summary>
        [Test]
        public void KnockoutDropsAFallingDeathNameWithTheMaxCombo()
        {
            AddStep("build two, one breaks at the fifth object", () =>
                build(2, misses: new[] { Array.Empty<int>(), new[] { 4 } }));
            AddUntilStep("recorded", () => combine.Simulator.AllComplete);
            AddStep("combo-break knockout, no grace", () =>
                combine.Rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0));

            AddStep("settle before the break", () => showAt(timeOf(3)));
            AddAssert("nobody has died yet", () => combine.DeathNamesShown == 0);

            AddStep("play across the elimination", () => playTo(timeOf(5)));

            AddAssert("a death name was dropped", () => combine.DeathNamesShown >= 1);
            AddAssert("it is the breaker's name and colour, with a combo line", () =>
            {
                var death = combine.ChildrenOfType<PlayerDeathName>().FirstOrDefault();
                if (death == null) return false;

                var lines = death.ChildrenOfType<OsuSpriteText>().ToList();
                return lines.Count == 2
                       && lines[0].Text.ToString() == "player1"
                       && lines[0].Colour == MultiReplayCombine.ColourFor(1, 2)
                       && lines[1].Text.ToString()!.EndsWith("x", StringComparison.Ordinal);
            });
        }

        /// <summary>Imperfection knockout shows the falling name ALONE — no combo line, matching
        /// danser.</summary>
        [Test]
        public void ImperfectionKnockoutDropsANameWithNoCombo()
        {
            AddStep("build two, one misses the fifth object", () =>
                build(2, misses: new[] { Array.Empty<int>(), new[] { 4 } }));
            AddUntilStep("recorded", () => combine.Simulator.AllComplete);
            AddStep("imperfection knockout", () => combine.Rules = new KnockoutRules(KnockoutMode.Imperfection));

            AddStep("settle at the very start", () => showAt(0));
            AddStep("play across the first imperfection", () => playTo(timeOf(6)));

            AddAssert("a death name was dropped", () => combine.DeathNamesShown >= 1);
            AddAssert("it is the name alone — no combo line", () =>
            {
                var death = combine.ChildrenOfType<PlayerDeathName>().FirstOrDefault();
                return death != null && death.ChildrenOfType<OsuSpriteText>().Count() == 1;
            });
        }

        /// <summary>The transient red name marker on the player who broke (index 1), or null when
        /// none is present.</summary>
        private OsuSpriteText? breakBubble() =>
            combine.Cursors[1]?.ChildrenOfType<OsuSpriteText>().FirstOrDefault(t => t.Colour == Color4.Red);

        private void buildWithNames(params string[] names)
        {
            var replays = names.Select(n => replayFor(n)).ToList();
            builtReplays = replays;
            host.Child = combine = new MultiReplayCombine(beatmapPath, replays);
            host.Clock = framed = new FramedClock(manual);
            manual.CurrentTime = 0;
        }

        /// <summary>
        /// The grade is drawn as a rank GRAPHIC (lazer's leaderboard badge), not a bare letter. The
        /// old skin-texture lookup returned nothing under Argon and every row fell back to a letter,
        /// which is what the user reported.
        /// </summary>
        [Test]
        public void TheGradeIsDrawnAsAGraphicNotALetter()
        {
            AddStep("build two clean plays", () => build(2));
            AddUntilStep("recorded", () => combine.Simulator.AllComplete);
            AddStep("show the end", () => showAt(timeOf(object_count - 1) + 500));

            AddAssert("every row shows a grade graphic", () => combine.Board.Rows.All(r => r.GradeIsImage));
        }

        /// <summary>
        /// The numeric columns use a tabular figure font, so a rolling value changes its digits in
        /// place without the text reflowing — the "numbers shake" report.
        /// </summary>
        [Test]
        public void TheNumericColumnsAreTabularSoRollingDoesNotShake()
        {
            AddStep("build two", () => build(2));
            AddUntilStep("recorded", () => combine.Simulator.AllComplete);
            AddStep("show a moment", () => showAt(timeOf(5)));

            AddAssert("all numeric columns are fixed-width", () => combine.Board.Rows.All(r => r.NumbersAreFixedWidth));
        }

        /// <summary>
        /// The score column lines up down the board no matter how long each player's name is — the
        /// "ragged columns" report. With the old left-to-right flow a long name shoved the numbers
        /// after it; the right group is now pinned, so this holds for wildly different name lengths.
        /// </summary>
        [Test]
        public void TheScoreColumnAlignsWhateverTheNameLengths()
        {
            AddStep("build three with very different name lengths", () => buildWithNames("a", "abcdefghijklmnop", "xy"));
            AddUntilStep("recorded", () => combine.Simulator.AllComplete);
            AddStep("show the end", () => showAt(timeOf(object_count - 1) + 500));

            AddAssert("every row's score shares one right edge", () =>
            {
                var edges = combine.Board.Rows.Select(r => r.ScoreRightEdge).ToList();
                return edges.Max() - edges.Min() < 0.5f;
            });
        }

        /// <summary>
        /// A per-player cursor colour override set BEFORE the view builds reaches all three places
        /// that carry a player's identity colour: their cursor dot, its trail, and their rail name —
        /// and only that player, leaving everyone else on the hue-spread default.
        /// </summary>
        [Test]
        public void APerPlayerColourOverrideTintsThatPlayersCursorTrailAndRailName()
        {
            AddStep("build two, the second forced lime", () =>
            {
                var replays = new[] { replayFor("player0"), replayFor("player1") };
                overrideStore.SetCursorColour(replays[1], Color4.Lime);

                builtReplays = replays;
                host.Child = combine = new MultiReplayCombine(beatmapPath, replays);
                host.Clock = framed = new FramedClock(manual);
                manual.CurrentTime = 0;
            });

            AddUntilStep("cursors attached", () => combine.CursorsAttached == 2);
            AddUntilStep("board built", () => combine.Board?.Rows.Count == 2);

            AddAssert("the overridden cursor and its trail are lime", () =>
                combine.Cursors[1]!.Colour4 == Color4.Lime
                && combine.Cursors[1]!.ChildrenOfType<PlayerCursorTrail>().First().TrailColour == Color4.Lime);

            AddAssert("its rail name is lime too", () => rowFor(1).NameColour == Color4.Lime);

            AddAssert("the other player keeps the hue-spread default", () =>
                combine.Cursors[0]!.Colour4 == MultiReplayCombine.ColourFor(0, 2)
                && combine.Cursors[0]!.Colour4 != Color4.Lime);
        }

        /// <summary>
        /// Changing a colour override while the view is live re-tints that player in place — cursor,
        /// trail and rail name — with no rebuild.
        /// </summary>
        [Test]
        public void AColourOverrideAppliedLiveReTintsInPlace()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("cursors attached", () => combine.CursorsAttached == 3);
            AddUntilStep("board built", () => combine.Board?.Rows.Count == 3);

            AddAssert("player 0 starts on its default colour", () =>
                combine.Cursors[0]!.Colour4 == MultiReplayCombine.ColourFor(0, 3));

            AddStep("recolour player 0 to magenta", () => overrideStore.SetCursorColour(builtReplays[0], Color4.Magenta));

            AddAssert("its cursor and trail re-tint at once", () =>
                combine.Cursors[0]!.Colour4 == Color4.Magenta
                && combine.Cursors[0]!.ChildrenOfType<PlayerCursorTrail>().First().TrailColour == Color4.Magenta);

            AddAssert("and its rail name re-tints", () => rowFor(0).NameColour == Color4.Magenta);
        }

        /// <summary>
        /// A per-player MOD override re-scores that one play under the chosen mods, leaving every
        /// other player on the mods they recorded. Asserted on the mods the simulator actually
        /// computed each player's numbers under — the effect, not the fact that a field was set.
        /// </summary>
        [Test]
        public void APerPlayerModOverrideReScoresOnlyThatPlayer()
        {
            AddStep("build two, the second forced to HardRock", () =>
            {
                var replays = new[] { replayFor("player0"), replayFor("player1") };
                overrideStore.SetMods(replays[1], new Mod[] { new OsuModHardRock() });

                builtReplays = replays;
                host.Child = combine = new MultiReplayCombine(beatmapPath, replays);
                host.Clock = framed = new FramedClock(manual);
                manual.CurrentTime = 0;
            });

            AddUntilStep("recorded", () => combine.Simulator.AllComplete);

            AddAssert("player 1 was simulated under HardRock", () =>
                combine.Simulator.SimulatedMods[1].Any(m => m.Acronym == "HR"));

            AddAssert("player 0 was not — it kept its recorded mods", () =>
                combine.Simulator.SimulatedMods[0].All(m => m.Acronym != "HR"));
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
