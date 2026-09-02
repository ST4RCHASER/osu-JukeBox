#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using JukeBox.Game.Tests.Import;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The tournament-style grid: N gameplay renders of one beatmap, each playing a different
    /// person's replay, all on one clock.
    ///
    /// <para>
    /// Replays are SYNTHESIZED here rather than downloaded — a real .osr written against a fixture
    /// difficulty and decoded through the same decoder the importer uses, so the cells are driven by
    /// genuine frames without the test needing the network or anyone's account.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneMultiReplayGrid : JukeBoxTestScene
    {
        private string tmp = null!;
        private string beatmapPath = null!;

        private MultiReplayGrid grid = null!;
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

        private ReplayAttachment replayFor(string player, double tempo = 1)
        {
            string osr = Path.Combine(tmp, player + ".osr");
            ReplayFixture.Write(osr, beatmapPath, player);

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

        private void buildGrid(int count, double[]? tempos = null)
        {
            var replays = Enumerable.Range(0, count)
                                    .Select(i => replayFor($"player{i}", tempos != null ? tempos[i] : 1))
                                    .ToList();

            host.Child = grid = new MultiReplayGrid(beatmapPath, replays);
        }

        [Test]
        public void EveryReplayGetsItsOwnCellOnOneSharedClock()
        {
            AddStep("build four", () => buildGrid(4));
            AddUntilStep("grid loaded", () => grid.IsLoaded && grid.Cells.Count == 4);

            AddAssert("laid out 2x2", () => grid.Shape.Equals(new GridShape(2, 2)));

            // The comparison is worthless if the cells run on separate clocks. None of them owns
            // one, so they all inherit the grid's — asserted rather than assumed.
            AddAssert("every cell reads the same clock", () =>
                grid.Cells.Select(c => c.Clock).Distinct().Count() == 1);
        }

        /// <summary>
        /// N gameplay layers hitting the same samples a few milliseconds apart is a flam, not N
        /// times louder. Exactly one cell may sound.
        /// </summary>
        [Test]
        public void OnlyTheFirstCellMakesAnySound()
        {
            AddStep("build three", () => buildGrid(3));
            AddUntilStep("grid loaded", () => grid.Cells.Count == 3);

            AddStep("turn hitsounds on", () => grid.HitSoundsEnabled = true);

            AddAssert("only the first cell sounds", () =>
                grid.Cells[0].HitSoundsEnabled.Value
                && grid.Cells.Skip(1).All(c => !c.HitSoundsEnabled.Value));

            AddStep("turn them off", () => grid.HitSoundsEnabled = false);
            AddAssert("and then none of them do", () => grid.Cells.All(c => !c.HitSoundsEnabled.Value));
        }

        /// <summary>
        /// Each cell is a whole gameplay renderer, so the cap is a budget rather than a preference.
        /// Replays past it keep their credit elsewhere but get no cell.
        /// </summary>
        [Test]
        public void ReplaysBeyondTheCapAreNotRendered()
        {
            AddStep("build more than the cap", () => buildGrid(MultiReplayLayout.MAX_GRID_CELLS + 3));
            AddUntilStep("grid loaded", () => grid.IsLoaded);

            AddAssert("only the cap is rendered", () => grid.Cells.Count == MultiReplayLayout.MAX_GRID_CELLS);
        }

        [Test]
        public void MixedSpeedsPutAWarningOnTheGrid()
        {
            AddStep("build two at different speeds", () => buildGrid(2, new[] { 1d, 1.5d }));
            AddUntilStep("grid loaded", () => grid.IsLoaded);

            AddAssert("the warning is on screen", () => grid.ChildrenOfType<osu.Game.Graphics.Sprites.OsuSpriteText>()
                                                            .Any(t => t.Text.ToString()!.StartsWith("Mixed speeds", StringComparison.Ordinal)));
        }

        [Test]
        public void MatchedSpeedsShowNoWarning()
        {
            AddStep("build two at the same speed", () => buildGrid(2));
            AddUntilStep("grid loaded", () => grid.IsLoaded);

            AddAssert("no warning anywhere", () => !grid.ChildrenOfType<osu.Game.Graphics.Sprites.OsuSpriteText>()
                                                        .Any(t => t.Text.ToString()!.StartsWith("Mixed speeds", StringComparison.Ordinal)));
        }

        /// <summary>
        /// Each cell's numbers come from its own replay, so a cell can never show another player's
        /// score — the failure that would make the whole comparison a lie.
        /// </summary>
        [Test]
        public void EachCellShowsItsOwnPlayer()
        {
            AddStep("build three", () => buildGrid(3));
            AddUntilStep("grid loaded", () => grid.Cells.Count == 3);

            AddAssert("all three names present, none duplicated", () =>
            {
                var names = grid.ChildrenOfType<osu.Game.Graphics.Sprites.OsuSpriteText>()
                                .Select(t => t.Text.ToString()!)
                                .Where(t => t.StartsWith("player", StringComparison.Ordinal))
                                .ToList();

                return names.Count == 3 && names.Distinct().Count() == 3;
            });
        }

        // The cost measurement that set MAX_GRID_CELLS was run here as a one-off and its numbers
        // written into that constant's doc comment. It is deliberately NOT a committed test: it
        // asserted nothing, so it would have cost every future CI run a full grid build to record
        // figures nobody reads.

        private static string osuWithObjects() =>
            "osu file format v14\n\n"
            + "[General]\nAudioFilename: audio.wav\nMode: 0\n\n"
            + "[Metadata]\nTitle:Grid Song\nArtist:Some Artist\nCreator:Some Mapper\nVersion:Hard\n\n"
            + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
            + "[TimingPoints]\n0,500,4,2,0,60,1,0\n\n"
            + "[HitObjects]\n256,192,1000,1,0,0:0:0:0:\n128,96,1500,1,0,0:0:0:0:\n320,240,2000,1,0,0:0:0:0:\n";
    }
}
