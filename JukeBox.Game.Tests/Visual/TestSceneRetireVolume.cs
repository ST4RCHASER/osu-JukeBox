#nullable enable

using System.IO;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Timing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Retiring a visual stack must silence THAT STACK, not the app.
    ///
    /// <para>
    /// The stack's audio container has its volume bound to the playback controller's, and a
    /// two-way bind means writing zero into one writes zero into the other. Retire sets the
    /// container's volume to zero to stop the outgoing song's keysounds — so the question this
    /// answers is whether that write reaches the master volume and takes the music with it.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneRetireVolume : JukeBoxTestScene
    {
        private string tmp = null!;
        private BeatmapVisuals visuals = null!;

        [Resolved]
        private PlaybackController playback { get; set; } = null!;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            File.WriteAllText(Path.Combine(tmp, "map [Hard].osu"),
                "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n"
                + "[Metadata]\nTitle:Retire\nArtist:A\nCreator:C\nVersion:Hard\n\n"
                + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
                + "[TimingPoints]\n0,500,4,2,0,60,1,0\n\n[HitObjects]\n256,192,1000,1,0,0:0:0:0:\n");

            Clear();
        });

        [Test]
        public void RetiringAStackDoesNotSilenceTheAppsMusic()
        {
            var manual = new ManualClock();
            var framed = new FramedClock(manual);

            AddStep("master volume up", () => playback.Volume.Value = 1);

            AddStep("build a stack", () => Add(visuals = new BeatmapVisuals(new JukeBox.Game.Beatmaps.CachedBeatmapSet
            {
                SetId = 1,
                Directory = tmp,
                PreferredOsuFile = Path.Combine(tmp, "map [Hard].osu"),
                OsuFiles = { Path.Combine(tmp, "map [Hard].osu") },
            }, framed)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("loaded", () => visuals.IsLoaded);
            AddAssert("master volume still up", () => playback.Volume.Value == 1);

            AddStep("retire it", () => visuals.Retire());

            AddAssert("the stack itself is silenced", () => visuals.AudioAdjustments.Volume.Value == 0);

            // The whole bug, in one line: the song must keep playing when the previous song's
            // visuals are retired, which happens on EVERY track change.
            AddAssert("but the app's music volume is untouched", () => playback.Volume.Value == 1);
        }

        /// <summary>
        /// The sequence the user actually hits — one song ends, the next begins — and the half a
        /// naive fix breaks: unbinding the retiring stack must not stop the INCOMING one following
        /// the volume setting, or keysounds go silent instead of the music.
        /// </summary>
        [Test]
        public void TheNextSongsStackStillFollowsTheVolumeSetting()
        {
            var framed = new FramedClock(new ManualClock());

            AddStep("master volume up", () => playback.Volume.Value = 1);
            AddStep("build the first stack", () => Add(visuals = stack(framed)));
            AddUntilStep("loaded", () => visuals.IsLoaded);
            AddStep("retire it, as a track change does", () => visuals.Retire());

            BeatmapVisuals second = null!;

            AddStep("build the next song's stack", () => Add(second = stack(framed)));
            AddUntilStep("loaded", () => second.IsLoaded);

            AddAssert("it hears the master volume", () => second.AudioAdjustments.Volume.Value == 1);

            AddStep("turn the volume down", () => playback.Volume.Value = 0.3);
            AddAssert("and follows it live", () => second.AudioAdjustments.Volume.Value == 0.3);

            AddAssert("while the retired stack stays silent", () => visuals.AudioAdjustments.Volume.Value == 0);
        }

        private BeatmapVisuals stack(FramedClock clock) => new BeatmapVisuals(new JukeBox.Game.Beatmaps.CachedBeatmapSet
        {
            SetId = 1,
            Directory = tmp,
            PreferredOsuFile = Path.Combine(tmp, "map [Hard].osu"),
            OsuFiles = { Path.Combine(tmp, "map [Hard].osu") },
        }, clock)
        {
            RelativeSizeAxes = Axes.Both,
        };

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
    }
}
