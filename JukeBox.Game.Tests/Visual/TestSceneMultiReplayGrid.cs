#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using JukeBox.Game.Tests.Import;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Framework.Utils;

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

            // A real (tiny) image, so "the cell carries the background" is testing a loaded texture
            // rather than a path that happens to be non-null.
            backgroundPath = Path.Combine(tmp, "bg.png");
            File.WriteAllBytes(backgroundPath, onePixelPng);

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

        /// <param name="misses">Objects this player fails to click. Every replay built here PLAYS
        /// the map: the header-only fixture never presses a button, so its score, combo and
        /// accuracy sit at zero for the entire play and no assertion about them can fail.</param>
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

        private void buildGrid(int count, double[]? tempos = null, int[][]? misses = null)
        {
            var replays = Enumerable.Range(0, count)
                                    .Select(i => replayFor($"player{i}", tempos != null ? tempos[i] : 1, misses?[i]))
                                    .ToList();

            host.Child = grid = new MultiReplayGrid(cachedSet(), beatmapPath, replays);
        }

        /// <summary>
        /// The minimum a cell needs to draw its own visuals: where the files are, and which one is
        /// the background. Hand-built rather than loaded through the cache — nothing here is testing
        /// the cache.
        /// </summary>
        private JukeBox.Game.Beatmaps.CachedBeatmapSet cachedSet() => new JukeBox.Game.Beatmaps.CachedBeatmapSet
        {
            SetId = 1,
            Directory = tmp,
            BackgroundFile = backgroundPath,
            PreferredOsuFile = beatmapPath,
            OsuFiles = { beatmapPath },
        };

        private string backgroundPath = null!;

        /// <summary>Smallest valid PNG — one opaque pixel.</summary>
        private static readonly byte[] onePixelPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

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

        /// <summary>
        /// Mixed speeds are neither announced nor adopted: the grid plays the map's own 1.0x and
        /// says nothing about it. The chip that used to sit here warned about a decision the user
        /// never made, and taking the first replay's rate let one DoubleTime drop set everyone
        /// else's playback speed.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void NoSpeedWarningEverAppears(bool mixed)
        {
            AddStep("build two", () => buildGrid(2, mixed ? new[] { 1d, 1.5d } : null));
            AddUntilStep("grid loaded", () => grid.IsLoaded);

            AddAssert("nothing on screen mentions speeds", () => !grid.ChildrenOfType<osu.Game.Graphics.Sprites.OsuSpriteText>()
                                                                     .Any(t => t.Text.ToString()!.Contains("speed", StringComparison.OrdinalIgnoreCase)));
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

        /// <summary>
        /// The "all black" fix. A cell is the whole visual stack — the map's own background behind
        /// the play — not gameplay on a black box, which is exactly what it used to be.
        /// </summary>
        [Test]
        public void EveryCellDrawsTheMapsBackground()
        {
            AddStep("build four", () => buildGrid(4));
            AddUntilStep("grid loaded", () => grid.IsLoaded && grid.Cells.Count == 4);

            // Counted by TEXTURE IDENTITY, not "a sprite with some texture": the gameplay layers are
            // full of skin sprites, so the loose version of this assertion passes with no background
            // in the grid at all — which it did, until a mutant pointed it out.
            AddAssert("one background sprite per cell, drawing the map's own art", () =>
                backgroundSpriteCount() == 4);
        }

        private int backgroundSpriteCount() => grid.ChildrenOfType<osu.Framework.Graphics.Sprites.Sprite>()
                                                   .Count(s => s.Texture != null
                                                               && grid.SharedBackgroundTexture != null
                                                               && ReferenceEquals(s.Texture, grid.SharedBackgroundTexture));

        /// <summary>
        /// The cells are read against the map's own art, so they need the dim the single chart gets
        /// — and it has to follow the setting rather than being baked in at build.
        /// </summary>
        [Test]
        public void TheBackgroundDimSettingReachesEveryCell()
        {
            AddStep("dim to 70%", () => config.SetValue(JukeBoxSetting.BackgroundDim, 0.7));
            AddStep("build three", () => buildGrid(3));
            AddUntilStep("grid loaded", () => grid.Cells.Count == 3);

            AddAssert("every cell dimmed to match", () => cellDimAlphas().Count(a => Precision.AlmostEquals(a, 0.7f, 0.01f)) >= 3);

            AddStep("dim to 20%", () => config.SetValue(JukeBoxSetting.BackgroundDim, 0.2));
            AddAssert("and it follows live", () => cellDimAlphas().Count(a => Precision.AlmostEquals(a, 0.2f, 0.01f)) >= 3);
        }

        /// <summary>
        /// Storyboards are per-cell decoded trees and videos are per-cell decoders, so past the
        /// limit they go — ALL of them, not the tail. Inconsistent cells would be worse for a
        /// comparison than uniformly plain ones.
        /// </summary>
        [Test]
        public void StoryboardsAreAllOrNothingByCellCount()
        {
            AddStep("build at the limit", () => buildGrid(MultiReplayLayout.STORYBOARD_CELL_LIMIT));
            AddUntilStep("loaded", () => grid.IsLoaded);
            AddAssert("every cell has one", () => grid.StoryboardCells == MultiReplayLayout.STORYBOARD_CELL_LIMIT);

            AddStep("build one over the limit", () => buildGrid(MultiReplayLayout.STORYBOARD_CELL_LIMIT + 1));
            AddUntilStep("loaded", () => grid.IsLoaded);
            AddAssert("none of them do", () => grid.StoryboardCells == 0);

            AddAssert("but they all still have a background", () =>
                backgroundSpriteCount() == MultiReplayLayout.STORYBOARD_CELL_LIMIT + 1);
        }

        /// <summary>
        /// The "static, not running" complaint, asserted on what a cell SHOWS rather than on any
        /// internal it happens to read from.
        ///
        /// <para>
        /// The .osr header is the discriminator. This fixture records TotalScore 1, MaxCombo 1 and
        /// accuracy 1 — absurd values no real play of the map produces — and the first
        /// implementation displayed exactly those from the first second of the song. Reading back a
        /// full twenty-object combo with a real score is only possible if the numbers came from the
        /// play itself.
        /// </para>
        /// </summary>
        [Test]
        public void TheCellNumbersShowThePlayRatherThanTheOsrHeader()
        {
            AddStep("build three", () => buildGrid(3));
            AddUntilStep("plays recorded", () => grid.Simulator?.AllComplete == true);

            AddStep("show the end of the map", () => showAt(endOfMap));

            AddAssert("every cell reads a played score", () => Enumerable.Range(0, 3).All(cell =>
                grid.ComboTextFor(cell) == $"{fixture_object_count}x"
                && grid.AccuracyTextFor(cell) == "100.00%"
                && long.Parse(grid.ScoreTextFor(cell)) > 1));
        }

        /// <summary>
        /// The defect a screenshot found: the numbers must survive the user touching the seek bar.
        ///
        /// <para>
        /// They used to come from a score processor fed judgements as the replay played, which is
        /// correct only while playing forwards from the start. A seek hard-seeks gameplay past
        /// objects that are then never judged, and going backwards un-judges nothing — so after a
        /// scrub the cells showed whatever had accumulated BEFORE it. Seeking early enough left all
        /// of them reading 00000000 and 0x over gameplay visibly forty objects in: the user's
        /// original complaint, under a different trigger.
        /// </para>
        /// </summary>
        [Test]
        public void TheCellNumbersSurviveTheUserSeeking()
        {
            AddStep("build three", () => buildGrid(3));
            AddUntilStep("plays recorded", () => grid.Simulator?.AllComplete == true);

            AddStep("jump straight to the end without playing there", () => showAt(endOfMap));
            AddAssert("the cells show the finished play", () =>
                grid.ComboTextFor(0) == $"{fixture_object_count}x");

            AddStep("drag back to the middle", () => showAt(timeOfObject(9)));
            AddAssert("and the numbers come back DOWN with it", () =>
                grid.ComboTextFor(0) == "10x");

            AddStep("and back to the very start", () => showAt(0));
            AddAssert("reading nothing scored yet", () =>
                grid.ComboTextFor(0) == "0x" && long.Parse(grid.ScoreTextFor(0)) == 0);

            AddStep("forward again", () => showAt(timeOfObject(14)));
            AddAssert("which is the same answer as arriving there by playing", () =>
                grid.ComboTextFor(0) == "15x");
        }

        private static double timeOfObject(int index) => fixture_first_object_ms + index * fixture_object_spacing_ms;

        private static double endOfMap => timeOfObject(fixture_object_count - 1) + 2000;

        /// <summary>Puts the grid at a moment in the song and lets it settle there.</summary>
        private void showAt(double time)
        {
            host.Clock = seekClock = new FramedClock(seekSource);
            seekSource.CurrentTime = time;

            for (int i = 0; i < 5; i++)
            {
                seekClock.ProcessFrame();
                host.UpdateSubTree();
            }
        }

        private readonly ManualClock seekSource = new ManualClock();
        private FramedClock seekClock = null!;

        /// <summary>
        /// A cell must show ITS player's play and nobody else's — the failure that would make the
        /// whole side-by-side comparison a lie.
        ///
        /// <para>
        /// Asserted with plays that genuinely differ. The previous version checked only that the
        /// three cells held three DISTINCT score objects, which is satisfied by three cells wired
        /// to three of the wrong player's numbers.
        /// </para>
        /// </summary>
        [Test]
        public void EachCellShowsItsOwnPlayAndNobodyElses()
        {
            // Each player drops exactly one object, at a different point, so at the end their
            // combos are 19, 14 and 9 — three values that cannot be confused for one another.
            AddStep("build three who fail at different points", () => buildGrid(3, misses: new[]
            {
                new[] { 0 },
                new[] { 5 },
                new[] { 10 },
            }));

            AddUntilStep("plays recorded", () => grid.Simulator?.AllComplete == true);
            AddStep("show the end", () => showAt(endOfMap));

            AddAssert("each cell carries its own player's combo", () =>
                grid.ComboTextFor(0) == "19x"
                && grid.ComboTextFor(1) == "14x"
                && grid.ComboTextFor(2) == "9x");
        }

        private float[] cellDimAlphas() => grid.ChildrenOfType<osu.Framework.Graphics.Shapes.Box>()
                                               .Select(b => b.Alpha)
                                               .ToArray();

        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        // The cost measurement that set MAX_GRID_CELLS was run here as a one-off and its numbers
        // written into that constant's doc comment. It is deliberately NOT a committed test: it
        // asserted nothing, so it would have cost every future CI run a full grid build to record
        // figures nobody reads.

        /// <summary>
        /// Objects a play can be WATCHED progressing through, which three could not be.
        ///
        /// <para>
        /// The frame-stable container races ahead of the driving clock by a variable amount while
        /// the grid is still loading — enough, on a three-object map, to sometimes finish the whole
        /// play before the test drives a single frame, leaving nothing left to climb and the test
        /// failing at random. Twenty objects spread over six seconds outlast that transient, so
        /// "the numbers climb" is measuring the play rather than the load.
        /// </para>
        /// </summary>
        internal const int fixture_object_count = 20;

        internal const int fixture_first_object_ms = 1000;

        internal const int fixture_object_spacing_ms = 300;

        private static string osuWithObjects()
        {
            var sb = new System.Text.StringBuilder();

            sb.Append("osu file format v14\n\n");
            sb.Append("[General]\nAudioFilename: audio.wav\nMode: 0\n\n");
            sb.Append("[Metadata]\nTitle:Grid Song\nArtist:Some Artist\nCreator:Some Mapper\nVersion:Hard\n\n");
            sb.Append("[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n");
            sb.Append("[TimingPoints]\n0,500,4,2,0,60,1,0\n\n[HitObjects]\n");

            for (int i = 0; i < fixture_object_count; i++)
                sb.Append($"{100 + i * 37 % 350},{80 + i * 53 % 240},{fixture_first_object_ms + i * fixture_object_spacing_ms},1,0,0:0:0:0:\n");

            return sb.ToString();
        }
    }
}
