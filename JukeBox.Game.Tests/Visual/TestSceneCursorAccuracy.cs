#nullable enable

using System;
using System.Collections.Generic;
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
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osuTK;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// A player's cursor has to be drawn where that player's cursor WAS, in the same space the hit
    /// objects are drawn in.
    ///
    /// <para>
    /// Checked by putting a cursor and a hit object at the same osu! coordinate and comparing where
    /// each of them lands on screen. Nothing else settles it: the cursors are positioned in the
    /// playfield's own 512x384 space, and whether that space lines up with the one the objects use
    /// is exactly the thing that can be wrong.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneCursorAccuracy : JukeBoxTestScene
    {
        private string tmp = null!;
        private string beatmapPath = null!;
        private Container host = null!;
        private MultiReplayCombine combine = null!;

        private readonly ManualClock manual = new ManualClock();
        private FramedClock framed = null!;

        /// <summary>The one object on the map, and therefore the one place the cursor should be.</summary>
        private static readonly Vector2 object_position = new Vector2(256, 192);

        private const double object_time = 2000;

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

        /// <summary>
        /// The cursor sits exactly on the object it is clicking, so its screen position and the
        /// object's must agree. They are drawn by different code into what is supposed to be one
        /// coordinate space; this is the assertion that says so.
        /// </summary>
        [Test]
        public void ACursorIsDrawnWhereTheObjectItIsClickingIsDrawn()
        {
            AddStep("build one player who clicks the object dead centre", () =>
            {
                string osr = Path.Combine(tmp, "p.osr");
                ReplayFixture.WriteHitting(osr, beatmapPath, "p");

                host.Child = combine = new MultiReplayCombine(beatmapPath, new List<ReplayAttachment>
                {
                    new ReplayAttachment
                    {
                        PlayerName = "p",
                        SourcePath = osr,
                        OsuFile = beatmapPath,
                        Score = new JukeBoxScoreDecoder(beatmapPath).Decode(osr),
                        RateTempo = 1,
                        RateFrequency = 1,
                    },
                });

                host.Clock = framed = new FramedClock(manual);
                manual.CurrentTime = 0;
            });

            AddUntilStep("cursor attached", () => combine.CursorsAttached == 1);

            AddStep("run to the moment of the hit", () =>
            {
                while (manual.CurrentTime < object_time)
                {
                    manual.CurrentTime = Math.Min(object_time, manual.CurrentTime + 16);
                    framed.ProcessFrame();
                    host.UpdateSubTree();
                }
            });

            // The object sits at (256,192), the dead centre of osu!'s 512x384 playfield, and the
            // replay clicks it dead centre — so the cursor belongs at the centre of the playfield
            // as it is drawn on screen. Compared against the PLAYFIELD rather than against a live
            // hit object because an object is only on screen for a moment, and this has to be
            // answerable at any time.
            AddAssert("the cursor is drawn at the centre of the playfield", () =>
            {
                var cursor = cursorScreenPosition();
                var centre = playfield().ScreenSpaceDrawQuad.Centre;

                // Generous: the cursor is a dot with a name beside it, so a few pixels is
                // presentation. The reported failure had cursors in the corner of the screen,
                // hundreds of pixels outside the play area.
                return Vector2.Distance(cursor, centre) < 25;
            });
        }

        private Drawable playfield()
            => ((osu.Game.Rulesets.Osu.UI.DrawableOsuRuleset)combine.Chart.DrawableRuleset!).Playfield;

        /// <summary>Where the cursor's dot actually lands on screen.</summary>
        private Vector2 cursorScreenPosition()
        {
            var cursor = combine.ChildrenOfType<PlayerCursor>().First();

            // The dot's own container — the auto-sized one that carries the position, not the
            // full-size content wrapper the cursor now nests its trail and dot inside.
            var body = cursor.ChildrenOfType<Container>().First(c => c.AutoSizeAxes == Axes.Both);

            return body.ScreenSpaceDrawQuad.Centre;
        }

        private static string map()
        {
            var sb = new StringBuilder();

            sb.Append("osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n");
            sb.Append("[Metadata]\nTitle:Accuracy\nArtist:A\nCreator:C\nVersion:Hard\n\n");
            sb.Append("[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n");
            sb.Append("[TimingPoints]\n0,500,4,2,0,60,1,0\n\n[HitObjects]\n");
            sb.Append($"{(int)object_position.X},{(int)object_position.Y},{(int)object_time},1,0,0:0:0:0:\n");

            return sb.ToString();
        }
    }
}
