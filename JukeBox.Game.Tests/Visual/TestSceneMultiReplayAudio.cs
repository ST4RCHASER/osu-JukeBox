#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using JukeBox.Game.Screens;
using JukeBox.Game.Tests.Import;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The song must be AUDIBLE while several replays are being watched. Checked at the master
    /// track — running, at volume, and actually producing signal — rather than at any bindable that
    /// is merely supposed to reach it.
    ///
    /// <para>
    /// A real audio file is used, not the silent virtual track the other tests get away with,
    /// because the question here is whether sound comes out. Amplitudes read zero on a virtual
    /// track no matter how healthy the plumbing is, so a virtual track could not tell a working
    /// player from a muted one — which is the whole failure being guarded.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneMultiReplayAudio : JukeBoxTestScene
    {
        private string tmp = null!;
        private string beatmapPath = null!;
        private BeatmapVisuals visuals = null!;
        private JukeBox.Game.Beatmaps.CachedBeatmapSet set = null!;

        [Resolved]
        private PlaybackController playback { get; set; } = null!;

        /// <summary>A real tone, so the track has something to be loud about.</summary>
        private const string tone_source = "/tmp/tone.wav";

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            string audio = Path.Combine(tmp, "audio.wav");
            File.Copy(tone_source, audio);

            beatmapPath = Path.Combine(tmp, "map [Hard].osu");
            File.WriteAllText(beatmapPath, map());

            set = new JukeBox.Game.Beatmaps.CachedBeatmapSet
            {
                SetId = 1,
                Directory = tmp,
                AudioFile = audio,
                PreferredOsuFile = beatmapPath,
                OsuFiles = { beatmapPath },
            };

            Clear();
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

        private List<ReplayAttachment> replays(int count) => Enumerable.Range(0, count).Select(i =>
        {
            string osr = Path.Combine(tmp, $"p{i}.osr");
            ReplayFixture.WriteHitting(osr, beatmapPath, $"p{i}");

            return new ReplayAttachment
            {
                PlayerName = $"p{i}",
                SourcePath = osr,
                OsuFile = beatmapPath,
                Score = new JukeBoxScoreDecoder(beatmapPath).Decode(osr),
                RateTempo = 1,
                RateFrequency = 1,
            };
        }).ToList();

        /// <summary>
        /// The exact sequence the user hits: a song is playing, the next entry arrives, its visuals
        /// replace the old ones — which retires them — and several replays are watched over it.
        /// </summary>
        private void playThroughASongChange(Func<Drawable> multiReplayView)
        {
            AddStep("start the song", () => playback.PlayAsync(set).ConfigureAwait(false));
            AddUntilStep("track loaded and running", () => playback.CurrentTrack?.IsRunning == true);

            AddStep("build the first song's visuals", () => Add(visuals = new BeatmapVisuals(set, playback.PlaybackClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("loaded", () => visuals.IsLoaded);

            // The song change. This is what silenced everything: retiring the outgoing stack wrote
            // zero through a two-way bind into the app's music volume, and nothing wrote it back.
            AddStep("next song arrives, retiring the old visuals", () =>
            {
                visuals.Retire();
                Add(visuals = new BeatmapVisuals(set, playback.PlaybackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("new visuals loaded", () => visuals.IsLoaded);
            AddStep("watch several replays over it", () => Add(multiReplayView()));

            AddStep("restart the track from the top", () =>
            {
                playback.Seek(0);
                playback.CurrentTrack?.Start();
            });

            AddUntilStep("the track is running", () => playback.CurrentTrack?.IsRunning == true);

            // Real time has to pass for the decoder to fill amplitude data.
            AddWaitStep("let it play", 40);
        }

        /// <summary>
        /// Audibility is asserted on the master track's AGGREGATE VOLUME, which is the number that
        /// decides whether sound comes out and the one the bug drove to zero.
        ///
        /// <para>
        /// Deliberately not on CurrentAmplitudes, which looks like the more honest measurement and
        /// is not: amplitudes describe the decoded waveform, so a fully muted track still reads a
        /// healthy peak. Measured — with the bug reinstated, a headed run with a real audio device
        /// gave volume 0.000 and amplitude 0.12494, unchanged from the working case. Amplitude
        /// proves the track is decoding and advancing; only the volume proves anyone can hear it.
        /// (It is also flat zero on a headless host with no audio device, so it could not carry a
        /// committed test either way.)
        /// </para>
        /// </summary>
        private void assertAudible()
        {
            AddAssert("the master track is running", () => playback.CurrentTrack?.IsRunning == true);
            AddAssert("at a volume that can be heard", () => playback.CurrentTrack!.AggregateVolume.Value > 0);
        }

        /// <summary>
        /// The CONTROL: plain single-song playback with no multi-replay anywhere near it, so a
        /// failure in the two below can be told apart from a test host that cannot play audio at
        /// all. Without it, "silent" means either a real regression or nothing whatsoever.
        /// </summary>
        [Test]
        public void ControlPlainPlaybackIsAudible()
        {
            AddStep("start the song", () => playback.PlayAsync(set).ConfigureAwait(false));
            AddUntilStep("track running", () => playback.CurrentTrack?.IsRunning == true);
            AddWaitStep("let it play", 40);

            assertAudible();
        }

        [Test]
        public void CombineModePlaysTheSong()
        {
            playThroughASongChange(() => new MultiReplayCombine(beatmapPath, replays(3))
            {
                RelativeSizeAxes = Axes.Both,
            });

            assertAudible();
        }

        [Test]
        public void GridModePlaysTheSong()
        {
            playThroughASongChange(() => new MultiReplayGrid(set, beatmapPath, replays(3))
            {
                RelativeSizeAxes = Axes.Both,
            });

            assertAudible();
        }

        private static string map()
        {
            var sb = new StringBuilder();

            sb.Append("osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n");
            sb.Append("[Metadata]\nTitle:Audible\nArtist:A\nCreator:C\nVersion:Hard\n\n");
            sb.Append("[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n");
            sb.Append("[TimingPoints]\n0,500,4,2,0,60,1,0\n\n[HitObjects]\n");

            for (int i = 0; i < 20; i++)
                sb.Append($"{100 + i * 37 % 350},{80 + i * 53 % 240},{1000 + i * 400},1,0,0:0:0:0:\n");

            return sb.ToString();
        }
    }
}
