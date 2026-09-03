#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using JukeBox.Game.Tests.Import;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Graphics.Sprites;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Combine mode: one rendered chart carrying everyone's cursor, with names and accuracies down
    /// one side and combo and score down the other.
    /// </summary>
    [TestFixture]
    public partial class TestSceneMultiReplayCombine : JukeBoxTestScene
    {
        private string tmp = null!;
        private string beatmapPath = null!;

        private MultiReplayCombine combine = null!;
        private Container host = null!;

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

        // totalScore is overridden so fixtures differ: the fixture writer gives every replay the
        // SAME score, which would make drop order and score order identical and leave the
        // rail-sorting test unable to tell them apart.
        private ReplayAttachment replayFor(string player, double tempo = 1, long? totalScore = null)
        {
            string osr = Path.Combine(tmp, player + ".osr");
            ReplayFixture.Write(osr, beatmapPath, player);

            var score = new JukeBoxScoreDecoder(beatmapPath).Decode(osr);

            if (totalScore != null)
                score.ScoreInfo.TotalScore = totalScore.Value;

            return new ReplayAttachment
            {
                PlayerName = player,
                SourcePath = osr,
                OsuFile = beatmapPath,
                Score = score,
                RateTempo = tempo,
                RateFrequency = 1,
            };
        }

        private void build(int count, double[]? tempos = null, long[]? scores = null)
        {
            var replays = Enumerable.Range(0, count)
                                    .Select(i => replayFor($"player{i}",
                                        tempos != null ? tempos[i] : 1,
                                        scores != null ? scores[i] : null))
                                    .ToList();

            host.Child = combine = new MultiReplayCombine(beatmapPath, replays);
        }

        /// <summary>
        /// One chart, everyone's cursor. The count is one FEWER than the replays because the first
        /// replay drives the chart itself and already draws a cursor — attaching another for it
        /// would double it.
        /// </summary>
        [Test]
        public void EveryReplayAfterTheFirstBecomesACursorOverTheOneChart()
        {
            AddStep("build four", () => build(4));
            AddUntilStep("chart loaded", () => combine.IsLoaded && combine.Chart.IsLoaded);
            AddUntilStep("cursors attached", () => combine.CursorsAttached == 3);

            AddAssert("exactly one chart", () => combine.ChildrenOfType<LazerChartLayer>().Count() == 1);
        }

        /// <summary>
        /// The dot beside a name is the only thing tying that row to a cursor weaving about the
        /// playfield, so two players must never share one.
        /// </summary>
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
        /// Sorted by score like the reference — the rails are a scoreboard, and a scoreboard in drop
        /// order is not one.
        /// </summary>
        [Test]
        public void TheRailsAreSortedByScore()
        {
            // Deliberately built in ASCENDING score order, so drop order and score order disagree —
            // otherwise the sort could be absent and this would still pass.
            AddStep("build three, worst dropped first", () => build(3, scores: new[] { 100_000L, 900_000L, 500_000L }));
            AddUntilStep("loaded", () => combine.IsLoaded);

            AddAssert("scores descend down the rail", () =>
            {
                var scores = combine.ChildrenOfType<OsuSpriteText>()
                                    .Select(t => t.Text.ToString()!)
                                    .Where(t => t.Length == 8 && t.All(char.IsDigit))
                                    .Select(long.Parse)
                                    .ToList();

                return scores.SequenceEqual(new[] { 900_000L, 500_000L, 100_000L });
            });
        }

        [Test]
        public void EveryPlayerIsNamedOnTheRail()
        {
            AddStep("build three", () => build(3));
            AddUntilStep("loaded", () => combine.IsLoaded);

            AddAssert("all three named once", () =>
            {
                var names = combine.ChildrenOfType<OsuSpriteText>()
                                   .Select(t => t.Text.ToString()!)
                                   .Where(t => t.StartsWith("player", StringComparison.Ordinal))
                                   .ToList();

                return names.Count == 3 && names.Distinct().Count() == 3;
            });
        }

        [Test]
        public void MixedSpeedsWarnHereToo()
        {
            AddStep("build two at different speeds", () => build(2, new[] { 1d, 1.5d }));
            AddUntilStep("loaded", () => combine.IsLoaded);

            AddAssert("warned", () => combine.ChildrenOfType<OsuSpriteText>()
                                             .Any(t => t.Text.ToString()!.StartsWith("Mixed speeds", StringComparison.Ordinal)));
        }

        private static string osuWithObjects() =>
            "osu file format v14\n\n"
            + "[General]\nAudioFilename: audio.wav\nMode: 0\n\n"
            + "[Metadata]\nTitle:Combine Song\nArtist:Some Artist\nCreator:Some Mapper\nVersion:Hard\n\n"
            + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
            + "[TimingPoints]\n0,500,4,2,0,60,1,0\n\n"
            + "[HitObjects]\n256,192,1000,1,0,0:0:0:0:\n128,96,1500,1,0,0:0:0:0:\n320,240,2000,1,0,0:0:0:0:\n";
    }
}
