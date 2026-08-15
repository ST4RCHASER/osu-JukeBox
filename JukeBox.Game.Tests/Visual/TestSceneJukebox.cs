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

        #region next-button feedback

        /// <summary>
        /// Every line the status has shown since recording began, in order. Transient phases (the
        /// prepare especially) are far too quick to catch by polling, so the phase tests assert
        /// against the recorded SEQUENCE rather than trying to sample a moment.
        /// </summary>
        private List<string?> statusLog = null!;

        private void recordStatus() => AddStep("record status changes", () =>
        {
            statusLog = new List<string?>();
            jukebox.Status.BindValueChanged(e => statusLog.Add(e.NewValue), true);
        });

        /// <summary>Whether the recorded lines contain these, in this order (other lines may sit
        /// between them).</summary>
        private bool loggedInOrder(params string[] expected)
        {
            int i = 0;

            foreach (string? line in statusLog)
            {
                if (i < expected.Length && line != null && line.StartsWith(expected[i], StringComparison.Ordinal))
                    i++;
            }

            return i == expected.Length;
        }

        // The reported bug, verbatim: "when has no queue and i try click next it's nothing happen
        // for 5 secs no download status show i think is looking up need to show status". The radio
        // search is the longest phase of an empty-queue round and was the only one that announced
        // nothing at all — no line, no spinner — so the app looked hung.
        [Test]
        public void TheLookupPhaseAnnouncesItselfInsteadOfSittingSilent()
        {
            AddStep("radio has a candidate, but searching hangs", () =>
            {
                mirror.SetSearchResults(new List<BeatmapSetInfo> { set1 });
                mirror.GateSearch();
            });

            recordStatus();
            AddStep("next, with an empty queue", () => jukebox.SkipCurrent());

            AddUntilStep("the lookup is announced", () => jukebox.Status.Value == "Looking for a song…");
            AddAssert("with no percentage to attach, since no set is picked yet", () => jukebox.DownloadingSetId.Value == null);

            AddStep("let the search finish", () => mirror.ReleaseSearch());
            AddUntilStep("set1 plays", () => playback.Current.Value?.SetId == set1.Id);
            AddAssert("and the line is handed back", () => jukebox.Status.Value == null);
        }

        // The phases in order, on one empty-queue round: look up, download, prepare. The prepare
        // phase covers the gap between the last byte arriving and audio starting (extract + track
        // load), during which the download line used to sit at 100% reading as stuck.
        [Test]
        public void EachPhaseOfAnEmptyQueueRoundAnnouncesInOrder()
        {
            AddStep("radio has a candidate", () => mirror.SetSearchResults(new List<BeatmapSetInfo> { set1 }));

            recordStatus();
            AddStep("next, with an empty queue", () => jukebox.SkipCurrent());
            AddUntilStep("set1 plays", () => playback.Current.Value?.SetId == set1.Id);

            AddAssert("looking up, then downloading, then preparing",
                () => loggedInOrder("Looking for a song…", "Downloading One…", "Preparing One…"));
            AddAssert("and the line is empty at the end", () => jukebox.Status.Value == null);
        }

        // The fast path stays visually still. A cache hit reaches playback in milliseconds, so a
        // line that flashes on and back off for every skip through cached songs would be worse than
        // no line — and it would flicker the spinner and the next button along with it.
        [Test]
        public void ACacheHitAnnouncesNothingAtAll()
        {
            AddStep("play set1 once, so it is cached", () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddUntilStep("set1 playing", () => playback.Current.Value?.SetId == set1.Id);
            AddUntilStep("its download line is gone", () => jukebox.Status.Value == null);

            recordStatus();
            AddStep("queue and advance to it again, now cached", () =>
            {
                queue.Enqueue(set1);
                jukebox.AdvanceAsync();
            });
            AddUntilStep("set1 playing again", () => playback.Current.Value?.SetId == set1.Id);

            AddAssert("nothing was ever put on the line", () => statusLog.All(l => l == null));
        }

        // The user's second ask: "when it downloading please grayout next button". Applied to the
        // LOOKUP with an empty queue — see CanSkipNext's own doc for why that is the phase where
        // pressing next genuinely cannot change anything.
        [Test]
        public void NextIsDisabledWhileLookingUpWithAnEmptyQueue()
        {
            AddStep("radio has a candidate, but searching hangs", () =>
            {
                mirror.SetSearchResults(new List<BeatmapSetInfo> { set1 });
                mirror.GateSearch();
            });

            AddAssert("next starts available", () => jukebox.CanSkipNext.Value);

            AddStep("next, with an empty queue", () => jukebox.SkipCurrent());
            AddUntilStep("next goes unavailable while the lookup runs", () => !jukebox.CanSkipNext.Value);

            AddStep("let the search finish", () => mirror.ReleaseSearch());
            AddUntilStep("set1 plays", () => playback.Current.Value?.SetId == set1.Id);
            AddAssert("and next is available again", () => jukebox.CanSkipNext.Value);
        }

        // The failure the brief called out, and the realistic one right now: every mirror down.
        // It must not sit silent forever OR leave next permanently dead — the state has to be
        // visible and recoverable.
        [Test]
        public void AFailedLookupReportsAnErrorAndLeavesNextUsable()
        {
            AddStep("every search attempt fails", () =>
            {
                mirror.SetSearchResults(new List<BeatmapSetInfo> { set1 });
                mirror.SearchFails = true;
            });

            AddStep("next, with an empty queue", () => jukebox.SkipCurrent());

            AddUntilStep("the failure is reported, and says why", () => jukebox.LastError.Value?.Contains("Can't reach any beatmap source") == true);
            AddAssert("the lookup line is not left behind under it", () => jukebox.Status.Value == null);
            AddAssert("and next is usable again, so the user is not stuck", () => jukebox.CanSkipNext.Value);

            // Recoverable: the round retries by itself, and once the mirror comes back it plays.
            AddStep("the mirror comes back", () => mirror.SearchFails = false);
            AddUntilStep("a later retry succeeds on its own", () => playback.Current.Value?.SetId == set1.Id);
        }

        // Guards the instant-skip work (per-caller cache tokens) against being undone by the
        // disabled treatment: with something queued there is a real song to skip TO, so next stays
        // live even while a download is in flight — and still actually skips.
        [Test]
        public void NextStaysAvailableAndWorksWhileAQueuedSongDownloads()
        {
            AddStep("gate set1's download, with set2 waiting behind it", () =>
            {
                mirror.GateDownload(1);
                queue.Enqueue(set1);
                queue.Enqueue(set2);
                jukebox.AdvanceAsync();
            });

            AddUntilStep("set1's download is announced", () => jukebox.Status.Value == "Downloading One…");
            AddAssert("next stays available during it", () => jukebox.CanSkipNext.Value);

            AddStep("press next", () => jukebox.SkipCurrent());
            AddUntilStep("set2 plays, without waiting for set1's download", () => playback.Current.Value?.SetId == set2.Id);
        }

        // The queue half of CanSkipNext: enqueueing during a lookup hands the user something
        // concrete to skip to, so next must come back without waiting for the search.
        [Test]
        public void EnqueueingDuringALookupMakesNextAvailableAgain()
        {
            AddStep("searching hangs, with no candidates behind it", () => mirror.GateSearch());

            AddStep("next, with an empty queue", () => jukebox.SkipCurrent());
            AddUntilStep("next goes unavailable", () => !jukebox.CanSkipNext.Value);

            AddStep("enqueue something", () => queue.Enqueue(set1));
            AddAssert("next is available again immediately", () => jukebox.CanSkipNext.Value);

            AddStep("release the search", () => mirror.ReleaseSearch());
        }

        // A superseded lookup must be called off, not run to completion. RadioService's retry loop
        // caught everything, cancellation included, so a cancelled search was treated as a failed
        // attempt: the round waited out all three retries and then reported "no tracks available"
        // for a search nobody wanted.
        [Test]
        public void ASupersededLookupIsCalledOffRatherThanBurningItsRetries()
        {
            AddStep("searching hangs", () => mirror.GateSearch());
            AddStep("next, with an empty queue", () => jukebox.SkipCurrent());
            AddUntilStep("the lookup is running", () => jukebox.Status.Value == "Looking for a song…");

            AddStep("supersede it with a queued song", () =>
            {
                queue.Enqueue(set1);
                jukebox.AdvanceAsync();
            });

            AddUntilStep("set1 plays", () => playback.Current.Value?.SetId == set1.Id);

            // The count is the whole point, and the reason a "was it cancelled at all" assertion
            // would not do: the cancellation reaches the mirror either way. What the rethrow changes
            // is whether RadioService treats it as a failed attempt and tries again — without it,
            // all three attempts are cancelled in turn and the search then reports "no tracks
            // available" for a round nobody is waiting on. (That error is real but transient: the
            // superseding round clears it on entry, which is exactly why it cannot be asserted on.)
            AddAssert("the search stopped at the first attempt", () => mirror.SearchesCancelled == 1);
        }

        #endregion

        #region radio recovery

        /// <summary>How many times the error line has CHANGED — the number of toasts the user sees,
        /// since the toast is pushed from this bindable's value changes.</summary>
        private List<string> errorsShown = null!;

        private int errorChanges => errorsShown.Count;

        private void countErrorChanges() => AddStep("record error-line changes", () =>
        {
            errorsShown = new List<string>();
            jukebox.LastError.BindValueChanged(e =>
            {
                if (e.NewValue != null)
                    errorsShown.Add(e.NewValue);
            });
        });

        // The reported bug, verbatim: "it loop error like this why" — the same
        // "No tracks available; retrying radio shortly." toast over and over. Each retry cleared the
        // error line and re-set the identical text, and a clear-then-set is a value CHANGE either
        // way, so every retry pushed a fresh toast for one unchanged problem.
        [Test]
        public void ARepeatingRadioFailureIsAnnouncedOnceNotOncePerRetry()
        {
            AddStep("every search fails", () => mirror.SearchFails = true);

            countErrorChanges();

            // Stands in for the retries the scheduler would run; each is a full failing round.
            for (int i = 0; i < 4; i++)
            {
                AddStep($"failing round {i + 1}", () => jukebox.AdvanceAsync());
                AddUntilStep("round reported", () => jukebox.LastError.Value != null);
            }

            AddAssert("the user was told once, not four times", () => errorChanges == 1);
        }

        // ...but a DIFFERENT problem must still get through, or the collapsing above would hide a
        // change the user needs to know about.
        [Test]
        public void AChangedFailureIsStillAnnounced()
        {
            AddStep("every search fails", () => mirror.SearchFails = true);
            countErrorChanges();

            AddStep("failing round", () => jukebox.AdvanceAsync());
            AddUntilStep("reported", () => jukebox.LastError.Value != null);
            AddAssert("announced once", () => errorChanges == 1);

            // A queued set that cannot be downloaded reports a different message entirely.
            AddStep("queue a set whose download fails", () =>
            {
                queue.Enqueue(setFailing);
                jukebox.AdvanceAsync();
            });

            // The download failure is a DIFFERENT problem, so it must reach the user. (The round
            // then finds the queue empty and falls back to the radio, whose failure is announced
            // again after it — hence "contains", not a count: what is under test is that a changed
            // problem is not swallowed, not how many distinct problems one round can hit.)
            AddUntilStep("the new problem is announced too",
                () => errorsShown.Any(e => e.Contains("Failing", StringComparison.Ordinal)));
        }

        // "Retrying shortly" every five seconds forever hammered dead mirrors and was not even true
        // of what happened next. The delay now doubles per consecutive failure and is capped.
        [Test]
        public void TheRetryDelayGrowsWithEachFailureAndIsCapped()
        {
            var seen = new List<double>();

            AddStep("every search fails", () =>
            {
                mirror.SearchFails = true;
                seen.Clear();
            });

            for (int i = 0; i < 6; i++)
            {
                AddStep($"failing round {i + 1}", () => jukebox.AdvanceAsync());
                AddUntilStep("round reported", () => jukebox.LastError.Value != null);
                AddStep("record the delay it would retry with", () => seen.Add(jukebox.RadioRetryDelayMs));
            }

            AddAssert("it doubles: 5s, 10s, 20s, 40s", () => seen.Take(4).SequenceEqual(new double[] { 5000, 10000, 20000, 40000 }));
            AddAssert("and is capped at a minute", () => seen.Skip(4).All(d => d == 60000));
        }

        // Recovery has to be automatic: nothing else in the app would ever re-enable the radio, and
        // a mirror coming back must not leave the user on a minute-long backoff forever.
        [Test]
        public void TheRadioRecoversByItselfWhenAMirrorComesBack()
        {
            AddStep("every search fails", () =>
            {
                mirror.SetSearchResults(new List<BeatmapSetInfo> { set1 });
                mirror.SearchFails = true;
            });

            AddStep("failing round", () => jukebox.AdvanceAsync());
            AddUntilStep("reported", () => jukebox.LastError.Value != null);
            AddAssert("backed off", () => jukebox.RadioRetryDelayMs > 5000 || jukebox.RadioRetryDelayMs == 5000);

            AddStep("the mirror comes back", () => mirror.SearchFails = false);
            AddStep("next round", () => jukebox.AdvanceAsync());

            AddUntilStep("set1 plays", () => playback.Current.Value?.SetId == set1.Id);
            AddAssert("and the backoff is reset for next time", () => jukebox.RadioRetryDelayMs == 5000);
        }

        // The worst version of this bug would be a looping error AND a dead next button. Guards the
        // interaction with the skip-disabling work: a failing radio must always hand next back.
        [Test]
        public void NextStaysUsableThroughoutAFailingRadio()
        {
            AddStep("every search fails", () => mirror.SearchFails = true);

            for (int i = 0; i < 3; i++)
            {
                AddStep($"failing round {i + 1}", () => jukebox.AdvanceAsync());
                AddUntilStep("round reported", () => jukebox.LastError.Value != null);
                AddUntilStep("next is usable again", () => jukebox.CanSkipNext.Value);
            }
        }

        #endregion

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

            private TaskCompletionSource<bool>? searchGate;

            /// <summary>Whether every search attempt throws — the realistic "all mirrors are down"
            /// case (NeriNyan 530, catboy TLS-blocked), which drives RadioService through its
            /// retries to a null pick.</summary>
            public bool SearchFails;

            /// <summary>How many searches were aborted by their token, as opposed to failing.</summary>
            public int SearchesCancelled;

            /// <summary>Holds every search open until released, so the lookup phase — otherwise far
            /// too quick to observe — can be inspected while it is in flight.</summary>
            public void GateSearch() => searchGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public void ReleaseSearch() => searchGate?.TrySetResult(true);

            public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
            {
                if (searchGate != null)
                {
                    try
                    {
                        await searchGate.Task.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref SearchesCancelled);
                        throw;
                    }
                }

                if (SearchFails)
                    throw new IOException("fixture mirror search is down");

                return searchResults;
            }

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
