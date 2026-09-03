#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using JukeBox.Game.Tests.Import;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Panes for people playing DIFFERENT maps — the case the shared-map grid cannot express.
    ///
    /// <para>
    /// What matters here is independence, so that is what the assertions are about: a clock per
    /// pane rather than one between them, each replay's own rate surviving, and one pane audible
    /// rather than four songs at once. Replays are synthesized against real fixture difficulties
    /// (the same approach the grid's scene uses) so the panes are driven by genuine frames without
    /// the network or anyone's account.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneIndependentReplayPanes : JukeBoxTestScene
    {
        private string tmp = null!;
        private readonly List<string> maps = new List<string>();

        private IndependentReplayPanes panes = null!;
        private Container host = null!;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            maps.Clear();

            // Four DIFFERENT difficulties in their own folders — the whole point is that no two
            // panes share a beatmap, so a fixture with one map could not fail the way this must.
            for (int i = 0; i < 4; i++)
            {
                string dir = Path.Combine(tmp, $"set{i}");
                Directory.CreateDirectory(dir);

                // A real, decodable WAV per set: without one no Track is ever created, and the
                // rate assertion would silently have nothing to check.
                File.WriteAllBytes(Path.Combine(dir, "audio.wav"), silentWav());

                string map = Path.Combine(dir, $"map{i} [Hard].osu");
                File.WriteAllText(map, osuWithObjects());
                maps.Add(map);
            }

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

        private SpectateEntry entryFor(int index, string player, double tempo = 1)
        {
            string map = maps[index % maps.Count];
            string osr = Path.Combine(tmp, player + ".osr");

            ReplayFixture.WriteHitting(osr, map, player, Array.Empty<int>());

            return new SpectateEntry(
                Path.GetDirectoryName(map)!,
                map,
                new ReplayAttachment
                {
                    PlayerName = player,
                    SourcePath = osr,
                    OsuFile = map,
                    Score = new JukeBoxScoreDecoder(map).Decode(osr),
                    RateTempo = tempo,
                    RateFrequency = 1,
                },
                player);
        }

        private void build(int count, double[]? tempos = null)
        {
            var entries = Enumerable.Range(0, count)
                                    .Select(i => entryFor(i, $"player{i}", tempos != null ? tempos[i] : 1))
                                    .ToList();

            host.Child = panes = new IndependentReplayPanes(entries);
        }

        [Test]
        public void EveryPlayerGetsAPaneOnTheirOwnMap()
        {
            AddStep("three players, three maps", () => build(3));
            AddUntilStep("panes built", () => panes.Panes.Count == 3);

            AddAssert("laid out like the shared grid would be", () => panes.Shape.Cells >= 3);
            AddAssert("each pane charts a different map", () =>
                panes.Panes.Select(p => p.Chart).Distinct().Count() == 3);
        }

        /// <summary>
        /// The structural claim the whole component exists for. The shared-map grid drives every
        /// cell from ONE clock — that is what keeps its plays comparable. Panes must not, because
        /// different maps have different lengths and there is nothing to be in sync about.
        /// </summary>
        [Test]
        public void EachPaneRunsOnItsOwnClock()
        {
            AddStep("three players", () => build(3));
            AddUntilStep("panes built", () => panes.Panes.Count == 3);

            // Read off the CHART's effective clock, not off a field: a pane that constructed a
            // clock but never attached it to its subtree would pass the field check while every
            // pane actually ran on the parent's timeline.
            AddAssert("no two panes' content shares a clock", () =>
                panes.Panes.Select(p => (object)p.EffectiveClock).Distinct().Count() == 3);

            AddAssert("and none of them runs on the scene's clock", () =>
                panes.Panes.All(p => !ReferenceEquals(p.EffectiveClock, host.Clock)));
        }

        /// <summary>
        /// Each pane owns a track, so each keeps its replay's speed. The shared-map grid has to
        /// collapse mixed rates to one and leave someone playing at the wrong speed; that
        /// compromise has no equivalent here.
        /// </summary>
        [Test]
        public void MixedSpeedsAreKeptRatherThanReconciled()
        {
            AddStep("three players at different speeds", () => build(3, new[] { 1.0, 1.5, 0.75 }));
            AddUntilStep("panes built", () => panes.Panes.Count == 3);

            // Waits for the real tracks, because the claim is about what each pane PLAYS at. The
            // entry's own Rate property is just the input echoed back and would pass even if every
            // track had been forced to 1.
            AddUntilStep("every pane's track loaded", () => panes.Panes.All(p => p.LoadedTrack != null));

            AddAssert("every track kept its own replay's tempo", () =>
                panes.Panes.Select(p => Math.Round(p.LoadedTrack!.Tempo.Value, 3))
                     .SequenceEqual(new[] { 1.0, 1.5, 0.75 }));
        }

        /// <summary>Four unrelated songs at once is noise; the rest are there to be unmuted.</summary>
        [Test]
        public void OnlyTheFirstPaneStartsAudible()
        {
            AddStep("four players", () => build(4));
            AddUntilStep("panes built", () => panes.Panes.Count == 4);

            AddAssert("first pane audible", () => panes.Panes[0].Volume.Value > 0);
            AddAssert("the rest silent", () => panes.Panes.Skip(1).All(p => p.Volume.Value == 0));
        }

        [Test]
        public void UnmutingAPaneIsPerPlayer()
        {
            AddStep("three players", () => build(3));
            AddUntilStep("panes built", () => panes.Panes.Count == 3);

            AddStep("unmute the second", () => panes.Panes[1].Volume.Value = 1);

            AddAssert("only that one changed", () =>
                panes.Panes[0].Volume.Value == 1 && panes.Panes[1].Volume.Value == 1 && panes.Panes[2].Volume.Value == 0);
        }

        /// <summary>
        /// The budget is spent by the constructor, so it is enforced there — a caller that forgot
        /// to ask the plan for a capped list must not be able to build twenty full renders.
        /// </summary>
        [Test]
        public void MorePlayersThanTheBudgetStillBuildsOnlyTheBudget()
        {
            AddStep("nine players", () => build(9));
            AddUntilStep("panes built", () => panes.Panes.Count > 0);

            AddAssert("capped", () => panes.Panes.Count == SpectatePanePlan.MAX_PANES);
        }

        [Test]
        public void NameAndNumbersHideIndependentlyPerPane()
        {
            AddStep("two players", () => build(2));
            AddUntilStep("panes built", () => panes.Panes.Count == 2);

            AddStep("hide the first player's name only", () => panes.Panes[0].ShowName.Value = false);

            // The LABELS, not the bindables — a toggle wired to nothing still flips its bindable.
            AddAssert("that pane's name is hidden, the other's is drawn",
                () => panes.Panes[0].NameLabel.Alpha == 0 && panes.Panes[1].NameLabel.Alpha == 1);

            AddStep("hide the second player's numbers only", () => panes.Panes[1].ShowNumbers.Value = false);

            AddAssert("numbers follow their own pane",
                () => panes.Panes[0].ScoreLabel.Alpha == 1 && panes.Panes[1].ScoreLabel.Alpha == 0);
        }

        /// <summary>
        /// A second of silent 8-bit mono PCM. Written by hand rather than copied from a fixture
        /// path, so the test carries its own audio instead of depending on a file that happens to
        /// exist on the machine.
        /// </summary>
        private static byte[] silentWav()
        {
            const int rate = 8000;
            const int samples = rate;

            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);

            w.Write("RIFF"u8.ToArray());
            w.Write(36 + samples);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);                 // PCM header size
            w.Write((short)1);           // PCM
            w.Write((short)1);           // mono
            w.Write(rate);
            w.Write(rate);               // byte rate (8-bit mono)
            w.Write((short)1);           // block align
            w.Write((short)8);           // bits per sample
            w.Write("data"u8.ToArray());
            w.Write(samples);

            // 0x80 is silence for unsigned 8-bit PCM, not 0x00.
            w.Write(Enumerable.Repeat((byte)0x80, samples).ToArray());

            w.Flush();
            return stream.ToArray();
        }

        /// <summary>A tiny but REAL map — the panes must be driven by genuine frames, not a header.</summary>
        private static string osuWithObjects()
        {
            var lines = new List<string>
            {
                "osu file format v14",
                "",
                "[General]",
                "AudioFilename: audio.wav",
                "Mode: 0",
                "",
                "[Difficulty]",
                "HPDrainRate:5",
                "CircleSize:4",
                "OverallDifficulty:5",
                "ApproachRate:5",
                "SliderMultiplier:1.4",
                "SliderTickRate:1",
                "",
                "[TimingPoints]",
                "0,500,4,2,0,100,1,0",
                "",
                "[HitObjects]",
            };

            for (int i = 0; i < 12; i++)
                lines.Add($"{100 + i * 20},192,{1000 + i * 500},1,0,0:0:0:0:");

            return string.Join("\n", lines) + "\n";
        }
    }
}
