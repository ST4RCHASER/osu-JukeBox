#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Framework.Utils;

namespace JukeBox.Game.Tests.Visual
{
    // NOTE: lives under Visual/ with the TestScene* naming convention (not
    // JukeBox.Game.Tests/Playback/JukeboxTest.cs as sketched in the task brief) because Jukebox
    // needs a real PlaybackController, which needs framework context (AudioManager) to load a
    // track — same constraint TestScenePlaybackController documents. Pure-NUnit tests in
    // Playback/ can't provide that; this follows the existing TestScene pattern instead.
    [TestFixture]
    public partial class TestSceneJukebox : JukeBoxTestScene
    {
        private MusicQueue queue = null!;
        private RadioService radio = null!;
        private BeatmapCache cache = null!;
        private PlaybackController playback = null!;
        private Jukebox jukebox = null!;

        private string tmp = null!;
        private FixtureMirror mirror = null!;

        private BeatmapSetInfo set1 = null!;
        private BeatmapSetInfo set2 = null!;
        private BeatmapSetInfo setFailing = null!;
        private BeatmapSetInfo set4 = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            mirror = new FixtureMirror();
            mirror.Register(1, makeOsz("one", "audio1.wav"));
            mirror.Register(2, makeOsz("two", "audio2.wav"));
            mirror.Register(4, makeOsz("four", "audio4.wav"));
            // 3 is deliberately unregistered: FixtureMirror throws on DownloadAsync for it.

            set1 = new BeatmapSetInfo { Id = 1, Title = "One" };
            set2 = new BeatmapSetInfo { Id = 2, Title = "Two" };
            setFailing = new BeatmapSetInfo { Id = 3, Title = "Failing" };
            set4 = new BeatmapSetInfo { Id = 4, Title = "Four" };

            queue = new MusicQueue();
            radio = new RadioService(mirror);
            cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);
        }

        // NOTE: deliberately NOT deleting `tmp` here — see TestScenePlaybackController for why
        // (TestScene runs queued AddStep bodies from a base-class teardown hook that fires after
        // this derived class's own [TearDown], so a synchronous delete here would race the
        // fixture files out from under still-pending steps).

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create jukebox", () =>
            {
                playback = new PlaybackController();
                jukebox = new Jukebox(queue, radio, cache, playback);
                Children = new Drawable[] { playback, jukebox };
            });
        }

        [Test]
        public void EnqueueWhilePlayingStartsFirstSetThenAdvancesThroughQueue()
        {
            AddStep("enqueue set1", () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddUntilStep("set1 playing", () => playback.Current.Value?.SetId == set1.Id);

            AddStep("enqueue set2 (idle already false, just queues)", () => jukebox.EnqueueAndMaybePlayAsync(set2));
            AddStep("advance", () => jukebox.AdvanceAsync());
            AddUntilStep("set2 playing", () => playback.Current.Value?.SetId == set2.Id);
        }

        // Regression test for the "no download/status feedback" UX complaint: Status should show
        // progress while a cache-miss download is in flight, and clear once playback actually
        // starts, so a first-run download doesn't look like the app hung.
        [Test]
        public void StatusShowsDownloadingWhileCacheMissInFlightAndClearsAfterwards()
        {
            AddStep("gate set1's download", () => mirror.GateDownload(1));
            AddStep("enqueue set1 (advance starts, blocks mid-download)", () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddUntilStep("status shows downloading set1", () => jukebox.Status.Value != null && jukebox.Status.Value.Contains(set1.DisplayTitle));

            AddStep("release set1's download", () => mirror.ReleaseGate(1));
            AddUntilStep("set1 playing", () => playback.Current.Value?.SetId == set1.Id);
            AddAssert("status cleared once playing", () => jukebox.Status.Value == null);
        }

        [Test]
        public void LastErrorIsStickyWithinAFailingRoundButClearedByTheNextSuccessfulOne()
        {
            AddStep("queue failing set then set4", () =>
            {
                queue.Enqueue(setFailing);
                queue.Enqueue(set4);
            });
            AddStep("advance (single round: pops failing set, then set4)", () => jukebox.AdvanceAsync());
            AddUntilStep("set4 playing", () => playback.Current.Value?.SetId == set4.Id);
            AddAssert("failure earlier in this round is still visible", () => jukebox.LastError.Value != null && jukebox.LastError.Value.Contains("Failing"));

            AddStep("queue a clean set1 and advance again", () =>
            {
                queue.Enqueue(set1);
                jukebox.AdvanceAsync();
            });
            AddUntilStep("set1 playing", () => playback.Current.Value?.SetId == set1.Id);
            AddAssert("a fully-successful round clears the stale error", () => jukebox.LastError.Value == null);
        }

        // Regression test for the reentrancy guard's latest-wins coalescing: a SkipCurrent (or
        // TrackCompleted) that arrives while an advance round is still stuck downloading must not
        // be dropped — it must run one more round once the in-flight one finishes.
        [Test]
        public void SkipDuringInFlightAdvanceIsNotLostAndTriggersAnotherRoundAfterward()
        {
            AddStep("gate set1's download", () => mirror.GateDownload(1));
            AddStep("enqueue set1 (advance starts, blocks mid-download)", () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddStep("queue set2 behind it", () => queue.Enqueue(set2));

            AddStep("skip while the advance for set1 is still in flight", () => jukebox.SkipCurrent());

            AddStep("release set1's download", () => mirror.ReleaseGate(1));

            // Whichever of set1/set2 actually wins the visible swap is PlaybackController's own
            // "most-recently-requested call wins" arbitration (see OverlappingPlayAsyncSecondCallWins) —
            // not what's under test here. What matters is that the skip wasn't silently dropped:
            // a second round ran and drained the queue.
            AddUntilStep("coalesced skip eventually plays set2", () => playback.Current.Value?.SetId == set2.Id);
            AddAssert("queue drained (set2 was actually popped and played, not left queued)", () => queue.Items.Count == 0);
        }

        // Regression test for the guard's exception-safety: an unhandled fault from a round
        // (i.e. something other than the cache-download failure already handled inside
        // advanceRoundAsync) must not leave `advancing` stuck true forever. RadioService never
        // propagates a mirror fault (it swallows every attempt internally and returns null), and
        // a cache/download fault is already fully absorbed by advanceRoundAsync's own try/catch
        // regardless of exception type — verified by reading both, so neither can reach the new
        // guard. PlaybackController.PlayAsync also doesn't throw for real (empirically checked:
        // it returns silently for both a garbage audio file and a missing directory). So this
        // uses a throwing PlaybackController test double via the `virtual` seam added to
        // PlaybackController.PlayAsync — the only realistic way to exercise this path.
        [Test]
        public void UnhandledExceptionDuringAdvanceReleasesGuardAndSurfacesError()
        {
            AddStep("swap in a playback controller that throws unexpectedly", () =>
            {
                playback = new ThrowingPlaybackController();
                jukebox = new Jukebox(queue, radio, cache, playback);
                Children = new Drawable[] { playback, jukebox };
            });

            AddStep("enqueue set1 (round pops it, caches fine, then PlayAsync throws)", () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddUntilStep("unexpected error surfaced via LastError", () => jukebox.LastError.Value != null && jukebox.LastError.Value.Contains("Unexpected error"));

            AddStep("queue set2 and skip", () =>
            {
                queue.Enqueue(set2);
                jukebox.SkipCurrent();
            });
            // set2's round will also throw (same throwing controller), but the queue only empties
            // if a second round actually ran at all — which only happens if the guard was
            // released after the first round's unhandled exception, not left stuck "advancing".
            AddUntilStep("a second round ran (guard was released, not wedged)", () => queue.Items.Count == 0);
        }

        // The other half of "downloading status stuck when i skip when download": the skip itself
        // used to do nothing visible until the download it was skipping had finished — and then the
        // skipped song played for a moment before the advance the user asked for finally ran.
        [Test]
        public void SkippingMidDownloadStartsTheNextSongWithoutPlayingTheSkippedOne()
        {
            var everPlayed = new List<int>();

            AddStep("record everything that reaches playback", () =>
            {
                everPlayed.Clear();
                playback.Current.BindValueChanged(e =>
                {
                    if (e.NewValue != null)
                        everPlayed.Add(e.NewValue.SetId);
                });
            });

            AddStep("gate set1's download and start it", () =>
            {
                mirror.GateDownload(1);
                jukebox.EnqueueAndMaybePlayAsync(set1);
            });

            AddUntilStep("set1's download is in flight", () => cache.IsDownloading(set1.Id));

            AddStep("queue set2 and skip", () =>
            {
                queue.Enqueue(set2);
                jukebox.SkipCurrent();
            });

            // set1's gate is never released: if the skip still waited for that download, this could
            // not pass at all.
            AddUntilStep("set2 plays without set1's download ever completing",
                () => playback.Current.Value?.SetId == set2.Id);

            AddAssert("set1's download was actually aborted", () => mirror.Cancelled > 0);
            AddAssert("and set1 never reached playback", () => !everPlayed.Contains(set1.Id),
                "the song being skipped must not get its moment of playback");
        }

        // Rapid skipping through several gated downloads has to settle on the last one asked for,
        // with nothing left over.
        [Test]
        public void RapidSkippingThroughDownloadsSettlesOnTheLastOneAskedFor()
        {
            AddStep("gate the first two, leave the third free", () =>
            {
                mirror.GateDownload(1);
                mirror.GateDownload(2);
            });

            AddStep("queue three and start", () =>
            {
                queue.Enqueue(set2);
                queue.Enqueue(set4);
                jukebox.EnqueueAndMaybePlayAsync(set1);
            });

            AddUntilStep("the first download is in flight", () => cache.IsDownloading(set1.Id));

            AddStep("skip twice in quick succession", () =>
            {
                jukebox.SkipCurrent();
                jukebox.SkipCurrent();
            });

            // Neither gate is ever released — only set4 can actually complete.
            AddUntilStep("reaches the one that can actually play", () => playback.Current.Value?.SetId == set4.Id);

            AddAssert("the downloads it skipped past were aborted", () => mirror.Cancelled > 0);

            // Not "the line is empty": these fixture tracks are a fraction of a second long, so
            // playback legitimately runs on to the next queued set — which IS downloading. What must
            // hold is that any line showing names a download that is genuinely still in flight,
            // rather than one of the ones skipped past.
            AddUntilStep("any line still showing is a real download", () => jukebox.Status.Value == null
                                                                           || jukebox.DownloadingSetId.Value is int id && cache.IsDownloading(id));
        }

        // The reported bug: "downloading status stuck when i skip when download". A round writes
        // "Downloading X…" before fetching, and clears it on each of its own exits — but an
        // exception thrown ANYWHERE after that write (PlayAsync, the replay difficulty switch, the
        // prefetch read) escapes to the loop's catch, which surfaces an error and never clears the
        // status. The next round then clears LastError, the panel falls back to Status, and a dead
        // download's title sits under whatever is now playing.
        [Test]
        public void AStatusIsNotLeftBehindWhenARoundFailsUnexpectedly()
        {
            AddStep("swap in a playback controller that throws unexpectedly", () =>
            {
                playback = new ThrowingPlaybackController();
                jukebox = new Jukebox(queue, radio, cache, playback);
                Children = new Drawable[] { playback, jukebox };
            });

            AddStep("enqueue set1 (round writes its download status, then PlayAsync throws)",
                () => jukebox.EnqueueAndMaybePlayAsync(set1));

            AddUntilStep("the round failed", () => jukebox.LastError.Value?.Contains("Unexpected error") == true);

            AddUntilStep("no download status left behind", () => jukebox.Status.Value == null);
            AddUntilStep("and no download id left behind", () => jukebox.DownloadingSetId.Value == null);
        }

        // The status a round puts up must belong to the song that round is actually working on. A
        // superseded round releasing its status late must not wipe the CURRENT one's — which is why
        // every write carries the round's own token rather than writing unconditionally.
        [Test]
        public void ASupersededRoundsLateReleaseDoesNotWipeTheCurrentStatus()
        {
            AddStep("gate both downloads", () =>
            {
                mirror.GateDownload(1);
                mirror.GateDownload(2);
            });

            AddStep("enqueue set1 (its round announces the download and blocks)",
                () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddUntilStep("set1's download announced", () => jukebox.Status.Value == "Downloading One…");

            AddStep("queue set2 and skip mid-download", () =>
            {
                queue.Enqueue(set2);
                jukebox.SkipCurrent();
            });

            AddStep("let set1's download finish", () => mirror.ReleaseGate(1));

            // set2's round is now the owner and is blocked on its own download, so the line has to
            // read set2 — not blank (set1's round releasing over the top) and not still set1.
            AddUntilStep("the line follows the song actually being worked on now",
                () => jukebox.Status.Value == "Downloading Two…" && jukebox.DownloadingSetId.Value == set2.Id);

            AddStep("let set2's download finish", () => mirror.ReleaseGate(2));

            AddUntilStep("set2 plays", () => playback.Current.Value?.SetId == set2.Id);
            AddUntilStep("and the status clears with it", () => jukebox.Status.Value == null);
        }

        // Skipping repeatedly through downloading songs has to settle on the state of whatever is
        // actually current at the end — not on a leftover from any of the rounds skipped past.
        [Test]
        public void RapidSkippingThroughDownloadsSettlesOnTheCurrentSong()
        {
            AddStep("gate every download", () =>
            {
                mirror.GateDownload(1);
                mirror.GateDownload(2);
                mirror.GateDownload(4);
            });

            AddStep("queue three and start", () =>
            {
                queue.Enqueue(set2);
                queue.Enqueue(set4);
                jukebox.EnqueueAndMaybePlayAsync(set1);
            });

            AddUntilStep("first download announced", () => jukebox.Status.Value != null);

            AddStep("skip three times in a row", () =>
            {
                jukebox.SkipCurrent();
                jukebox.SkipCurrent();
                jukebox.SkipCurrent();
            });

            AddStep("release everything", () =>
            {
                mirror.ReleaseGate(1);
                mirror.ReleaseGate(2);
                mirror.ReleaseGate(4);
            });

            AddUntilStep("playback settles on something", () => playback.Current.Value != null);
            AddUntilStep("queue drained", () => queue.Items.Count == 0);

            // The point of the test: whatever ended up playing, no download line is left over — and
            // if one IS shown it names a download that is genuinely still running.
            AddUntilStep("no stale download line", () => jukebox.Status.Value == null
                                                        || jukebox.DownloadingSetId.Value is int id && cache.IsDownloading(id));
        }

        // The fix must not work by suppressing the status: a download that IS the current one still
        // has to announce itself, and keep announcing while it runs.
        [Test]
        public void TheCurrentSongsDownloadStillAnnouncesItself()
        {
            AddStep("gate set1's download", () => mirror.GateDownload(1));
            AddStep("enqueue set1", () => jukebox.EnqueueAndMaybePlayAsync(set1));

            AddUntilStep("announced", () => jukebox.Status.Value == "Downloading One…");
            AddAssert("with the id the progress readout needs", () => jukebox.DownloadingSetId.Value == set1.Id);

            AddWaitStep("while it keeps downloading", 20);
            AddAssert("still announced", () => jukebox.Status.Value == "Downloading One…");

            AddStep("release", () => mirror.ReleaseGate(1));
            AddUntilStep("plays", () => playback.Current.Value?.SetId == set1.Id);
            AddUntilStep("and the line clears once it is playing", () => jukebox.Status.Value == null);
        }

        // Regression test for the silent-stall bug: a set with no loadable audio (AudioFilename
        // missing, or pointing at a file that doesn't exist) used to make PlaybackController.PlayAsync
        // return silently, and Jukebox would count the round a success anyway — NowPlaying got set,
        // nothing was actually playing, and TrackCompleted would never fire again to advance past it.
        // PlayAsync now reports failure and advanceRoundAsync treats it exactly like a cache/download
        // failure: report LastError and keep popping instead of wedging on the dead set.
        [Test]
        public void SetWithNoLoadableAudioReportsErrorAndAdvancesToNextQueuedSet()
        {
            AddStep("register a set whose .osu has no AudioFilename", () => mirror.Register(5, makeOszNoAudio("noaudio")));

            var setNoAudio = new BeatmapSetInfo { Id = 5, Title = "Silent" };

            AddStep("queue the silent set then a normal one", () =>
            {
                queue.Enqueue(setNoAudio);
                queue.Enqueue(set1);
            });
            AddStep("advance (single round: pops silent set, then set1)", () => jukebox.AdvanceAsync());

            AddUntilStep("set1 playing (silent set was skipped, not wedged on)", () => playback.Current.Value?.SetId == set1.Id);
            AddAssert("failure reported for the silent set", () => jukebox.LastError.Value != null && jukebox.LastError.Value.Contains("Silent"));
        }

        // Regression test for the "queued song doesn't play until radio song ends" UX complaint:
        // a set the user explicitly enqueued should interrupt radio filler immediately (SkipCurrent
        // semantics), rather than wait for the radio-picked track to finish on its own.
        [Test]
        public void EnqueueInterruptsRadioSourcedPlayback()
        {
            AddStep("radio has one pickable candidate (set2)", () => mirror.SetSearchResults(new List<BeatmapSetInfo> { set2 }));
            AddStep("start with an empty queue (radio picks set2)", () => jukebox.Start());
            AddUntilStep("set2 (radio-sourced) playing", () => playback.Current.Value?.SetId == set2.Id);

            AddStep("enqueue set1 while the radio track is playing", () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddUntilStep("set1 plays immediately, interrupting the radio filler", () => playback.Current.Value?.SetId == set1.Id);
        }

        // Companion regression test: playback sourced from the queue must NOT be interrupted by a
        // later enqueue — the newly-enqueued set simply waits its turn, as before this fix.
        [Test]
        public void EnqueueDoesNotInterruptQueueSourcedPlayback()
        {
            AddStep("enqueue set1 (queue-sourced, starts playing)", () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddUntilStep("set1 playing", () => playback.Current.Value?.SetId == set1.Id);

            AddStep("enqueue set2 while set1 (queue-sourced) is still playing", () => jukebox.EnqueueAndMaybePlayAsync(set2));
            AddWaitStep("let a few frames pass", 5);
            AddAssert("set1 is still playing, not interrupted", () => playback.Current.Value?.SetId == set1.Id);
            AddAssert("set2 waits queued instead of playing immediately", () => queue.Items.Any(i => i.Id == set2.Id));
        }

        // Feeds MainScreen's "Added to queue: X" toast. Deliberately silent for a pick that was
        // already queued: MusicQueue.Enqueue dedupes by set id, so a second announcement would
        // claim something happened when the queue is unchanged.
        [Test]
        public void EnqueueRaisesTheEnqueuedEventOncePerNewlyQueuedSet()
        {
            var announced = new List<BeatmapSetInfo>();

            AddStep("listen for enqueues", () => jukebox.Enqueued += set => announced.Add(set));

            AddStep("enqueue set1", () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddUntilStep("set1 announced", () => announced.Count == 1 && announced[0].Id == set1.Id);

            // Both calls in one step body: MusicQueue.Enqueue and the event both run synchronously
            // before EnqueueAndMaybePlayAsync's first await, so the second call is guaranteed to
            // see set2 still queued — an advance round could only pop it a frame later.
            AddStep("enqueue set2 twice in the same frame", () =>
            {
                _ = jukebox.EnqueueAndMaybePlayAsync(set2);
                _ = jukebox.EnqueueAndMaybePlayAsync(set2);
            });

            AddWaitStep("let a few frames pass", 5);
            AddAssert("set2 announced exactly once", () => announced.Count(a => a.Id == set2.Id) == 1);
            AddAssert("nothing else announced", () => announced.Count == 2);
        }

        // A replay's rate mods have to move actual PLAYBACK, not the chart: in osu! a rate mod
        // speeds up the track and gameplay follows the track's clock, which is this app's
        // arrangement too. Speeding the chart alone would desync it from music that never changed.
        [Test]
        public void AReplaysRateModsSetThePlaybackRateAndReleaseItAfterwards()
        {
            var doubleTimeSet = new BeatmapSetInfo
            {
                Id = 1,
                Title = "One",
                Replay = new ReplayAttachment { PlayerName = "Cookiezi", RateTempo = 1.5 },
            };

            AddAssert("nothing forcing a rate to begin with", () => playback.ReplayTempo.Value == 1 && playback.ReplayFrequency.Value == 1);

            AddStep("enqueue the replay's set", () => jukebox.EnqueueAndMaybePlayAsync(doubleTimeSet));
            AddUntilStep("its set is playing", () => playback.Current.Value?.SetId == doubleTimeSet.Id);
            AddAssert("playback runs at the replay's rate", () => playback.ReplayTempo.Value == 1.5);

            AddStep("queue an ordinary set and advance", () =>
            {
                queue.Enqueue(set2);
                jukebox.SkipCurrent();
            });

            AddUntilStep("the ordinary set is playing", () => playback.Current.Value?.SetId == set2.Id);
            AddAssert("the forced rate was released", () => playback.ReplayTempo.Value == 1 && playback.ReplayFrequency.Value == 1,
                "a previous replay's rate must never leak into the next song");
        }

        // The user's own speed slider is a separate adjustment, so the two multiply — and crucially
        // the slider itself is never moved behind the user's back.
        [Test]
        public void AReplaysRateDoesNotClobberTheUsersOwnSpeedSetting()
        {
            var doubleTimeSet = new BeatmapSetInfo
            {
                Id = 4,
                Title = "Four",
                Replay = new ReplayAttachment { PlayerName = "Cookiezi", RateTempo = 1.5 },
            };

            AddStep("user sets their own speed to 0.5x", () => playback.PlaybackRate.Value = 0.5);
            AddStep("enqueue the replay's set", () => jukebox.EnqueueAndMaybePlayAsync(doubleTimeSet));
            AddUntilStep("its set is playing", () => playback.Current.Value?.SetId == doubleTimeSet.Id);

            AddAssert("the user's slider is untouched", () => playback.PlaybackRate.Value == 0.5);
            AddAssert("and the replay rate sits alongside it", () => playback.ReplayTempo.Value == 1.5);

            AddStep("restore the default speed", () => playback.PlaybackRate.Value = 1);
        }

        // The bindables are only half the story — what matters is which property of the REAL track
        // they move. DoubleTime must stretch time without touching pitch; Nightcore must shift
        // both. Asserted on the live track's aggregates, so a mis-wired AddAdjustment is caught.
        [TestCase("DT", 1.5, 1.0)]
        [TestCase("NC", 1.0, 1.5)]
        [TestCase("HT", 0.75, 1.0)]
        public void TheReplaysRateLandsOnTheRightTrackProperty(string label, double tempo, double frequency)
        {
            var ratedSet = new BeatmapSetInfo
            {
                Id = 1,
                Title = "One",
                Replay = new ReplayAttachment { PlayerName = "Cookiezi", RateTempo = tempo, RateFrequency = frequency },
            };

            AddStep($"enqueue a {label} replay's set", () => jukebox.EnqueueAndMaybePlayAsync(ratedSet));
            AddUntilStep("its set is playing", () => playback.Current.Value?.SetId == ratedSet.Id);
            AddUntilStep("a track is loaded", () => playback.CurrentTrack != null);

            AddAssert($"{label}: track tempo is {tempo}", () => Precision.AlmostEquals(playback.CurrentTrack!.AggregateTempo.Value, tempo, 0.0001));
            AddAssert($"{label}: track frequency is {frequency}", () => Precision.AlmostEquals(playback.CurrentTrack!.AggregateFrequency.Value, frequency, 0.0001));
        }

        // Builds a fixture .osz whose only difficulty has no AudioFilename key at all, so
        // BeatmapCache.LoadFromDirectory leaves CachedBeatmapSet.AudioFile null — the "no loadable
        // audio" case, distinct from `setFailing` above (which fails at the download/cache stage).
        private string makeOszNoAudio(string name)
        {
            string dir = Path.Combine(tmp, "build_" + name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "diff.osu"), "osu file format v14\n\n[General]\nMode: 0\n");
            string osz = Path.Combine(tmp, name + ".osz");
            ZipFile.CreateFromDirectory(dir, osz);
            return osz;
        }

        private partial class ThrowingPlaybackController : PlaybackController
        {
            public override Task<bool> PlayAsync(CachedBeatmapSet set) => throw new InvalidOperationException("simulated unexpected playback fault");
        }

        private string makeOsz(string name, string audioFileName)
        {
            string dir = Path.Combine(tmp, "build_" + name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "diff.osu"),
                $"osu file format v14\n\n[General]\nAudioFilename: {audioFileName}\nMode: 0\n");
            writeSilentWav(Path.Combine(dir, audioFileName), 1);
            string osz = Path.Combine(tmp, name + ".osz");
            ZipFile.CreateFromDirectory(dir, osz);
            return osz;
        }

        // BASS (the audio backend behind osu!framework's Track) plays WAV directly, so a
        // hand-written 44-byte RIFF header followed by silence is enough to drive playback.
        private static void writeSilentWav(string path, double seconds)
        {
            const int sample_rate = 44100;
            const short channels = 1;
            const short bits_per_sample = 16;

            int dataSize = (int)(sample_rate * channels * (bits_per_sample / 8) * seconds);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sample_rate);
            writer.Write(sample_rate * channels * (bits_per_sample / 8));
            writer.Write((short)(channels * (bits_per_sample / 8)));
            writer.Write(bits_per_sample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }

        // Serves distinct fixture .osz files per setId; download for an unregistered id throws,
        // simulating a mirror/extract failure for the "failing set" case. SearchAsync always
        // returns empty (radio isn't exercised by this test's happy paths). DownloadAsync for a
        // gated setId blocks until released, giving tests a deterministic window in which an
        // advance round is "in flight".
        private class FixtureMirror : IBeatmapMirror
        {
            private readonly Dictionary<int, string> paths = new();
            private readonly Dictionary<int, TaskCompletionSource<bool>> gates = new();

            // Empty by default: most of this fixture's tests exercise the queue path only and
            // rely on RadioService.PickRandomAsync returning null (empty candidates) rather than
            // wedging on an unexpected radio pick. Tests exercising radio-sourced playback set
            // this via SetSearchResults.
            private List<BeatmapSetInfo> searchResults = new();

            public string Name => "fixture";

            public void Register(int setId, string oszPath) => paths[setId] = oszPath;

            public void SetSearchResults(List<BeatmapSetInfo> sets) => searchResults = sets;

            public void GateDownload(int setId) => gates[setId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public void ReleaseGate(int setId)
            {
                if (gates.TryGetValue(setId, out var tcs))
                    tcs.TrySetResult(true);
            }

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
                => Task.FromResult(searchResults);

            /// <summary>How many downloads were aborted rather than finishing — "the caller stopped
            /// waiting" and "the request was actually cancelled" are different claims.</summary>
            public int Cancelled;

            public async Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
            {
                if (gates.TryGetValue(setId, out var gate))
                {
                    try
                    {
                        // Honours the token, so a gated download can be abandoned mid-flight the way
                        // a real one is rather than parking forever.
                        await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref Cancelled);
                        throw;
                    }
                }

                if (!paths.TryGetValue(setId, out string? path))
                    throw new IOException($"fixture mirror has no set {setId}");

                using var fs = File.OpenRead(path);
                await fs.CopyToAsync(destination, ct);
            }
        }
    }
}
