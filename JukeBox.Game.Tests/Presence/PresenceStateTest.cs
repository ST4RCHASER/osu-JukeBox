#nullable enable

using System;
using System.Text;
using DiscordRPC;
using JukeBox.Game.Presence;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Presence
{
    /// <summary>
    /// What the presence SAYS, decided as a pure function of the playback state — no game host, no
    /// Discord. <c>TestSceneDiscordPresence</c> covers when it is sent.
    /// </summary>
    [TestFixture]
    public class PresenceStateTest
    {
        private static readonly DateTime now = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

        private static PresenceInputs inputs(
            bool renderChart = false,
            bool hasStoryboard = false,
            bool playing = true,
            double position = 30_000,
            double length = 180_000,
            double rate = 1,
            string title = "FREEDOM DIVE",
            string artist = "xi",
            string? difficulty = null,
            int onlineSetId = 0,
            bool showStoryboard = true,
            bool hasVideo = false,
            bool showVideo = true,
            double notPlayingForMs = 0)
            => new PresenceInputs(title, artist, difficulty, hasStoryboard, renderChart, playing, position, length, rate, now,
                onlineSetId, showStoryboard, hasVideo, showVideo, TimeSpan.FromMilliseconds(notPlayingForMs));

        // ---- the activity verb follows what is actually on screen ----

        /// <summary>
        /// Every map has a chart, so the toggle on its own settles it — there is no "does this map
        /// have one" question to ask the way there is for a storyboard or a video.
        /// </summary>
        [Test]
        public void RenderChartOnIsWatching()
        {
            Assert.That(DiscordPresenceService.Build(inputs(renderChart: true))!.Activity, Is.EqualTo(PresenceActivity.Watching));
        }

        [Test]
        public void AStoryboardTheMapHasAndTheSettingDrawsIsWatching()
        {
            Assert.That(DiscordPresenceService.Build(inputs(hasStoryboard: true, showStoryboard: true))!.Activity,
                Is.EqualTo(PresenceActivity.Watching));
        }

        /// <summary>
        /// The case the old rule got wrong: the toggle says "draw storyboards", the map has none, so
        /// nothing is on screen and nothing is being watched.
        /// </summary>
        [Test]
        public void TheStoryboardSettingAloneIsNotEnoughWithoutAStoryboard()
        {
            Assert.That(DiscordPresenceService.Build(inputs(hasStoryboard: false, showStoryboard: true))!.Activity,
                Is.EqualTo(PresenceActivity.Listening));
        }

        [Test]
        public void AStoryboardTheSettingIsHidingIsNotWatching()
        {
            Assert.That(DiscordPresenceService.Build(inputs(hasStoryboard: true, showStoryboard: false))!.Activity,
                Is.EqualTo(PresenceActivity.Listening));
        }

        /// <summary>
        /// A video filling the screen is every bit as watchable as a storyboard, so it counts the
        /// same way — present on the map AND switched on.
        /// </summary>
        [Test]
        public void AVideoTheMapHasAndTheSettingDrawsIsWatching()
        {
            Assert.That(DiscordPresenceService.Build(inputs(hasVideo: true, showVideo: true))!.Activity,
                Is.EqualTo(PresenceActivity.Watching));
        }

        [Test]
        public void AVideoTheSettingIsHidingIsNotWatching()
        {
            Assert.That(DiscordPresenceService.Build(inputs(hasVideo: true, showVideo: false))!.Activity,
                Is.EqualTo(PresenceActivity.Listening));
        }

        [Test]
        public void TheVideoSettingAloneIsNotEnoughWithoutAVideo()
        {
            Assert.That(DiscordPresenceService.Build(inputs(hasVideo: false, showVideo: true))!.Activity,
                Is.EqualTo(PresenceActivity.Listening));
        }

        [Test]
        public void PlainAudioIsListening()
        {
            Assert.That(DiscordPresenceService.Build(inputs())!.Activity, Is.EqualTo(PresenceActivity.Listening));
        }

        /// <summary>
        /// The point of dropping the prefixes: the body of the card is the same Spotify-shaped text
        /// whatever is on screen, and ONLY the activity verb moves. This is what replaces the old
        /// chart-versus-storyboard precedence test — with identical text there is no longer a
        /// question of which one wins.
        /// </summary>
        [Test]
        public void TheTextIsIdenticalAcrossEveryHeader()
        {
            var listening = DiscordPresenceService.Build(inputs(difficulty: "Insane"))!;
            var chart = DiscordPresenceService.Build(inputs(difficulty: "Insane", renderChart: true))!;
            var storyboard = DiscordPresenceService.Build(inputs(difficulty: "Insane", hasStoryboard: true))!;
            var video = DiscordPresenceService.Build(inputs(difficulty: "Insane", hasVideo: true))!;
            var both = DiscordPresenceService.Build(inputs(difficulty: "Insane", renderChart: true, hasStoryboard: true))!;

            foreach (var state in new[] { listening, chart, storyboard, video, both })
            {
                Assert.That(state.Details, Is.EqualTo("FREEDOM DIVE"), "details line");
                Assert.That(state.State, Is.EqualTo("xi · [Insane]"), "state line");
            }

            Assert.That(listening.Activity, Is.EqualTo(PresenceActivity.Listening));
            Assert.That(chart.Activity, Is.EqualTo(PresenceActivity.Watching));
            Assert.That(storyboard.Activity, Is.EqualTo(PresenceActivity.Watching));
            Assert.That(video.Activity, Is.EqualTo(PresenceActivity.Watching));
            Assert.That(both.Activity, Is.EqualTo(PresenceActivity.Watching));
        }

        [Test]
        public void NoPrefixSurvivesAnywhereInTheText()
        {
            foreach (var state in new[]
                     {
                         DiscordPresenceService.Build(inputs(renderChart: true))!,
                         DiscordPresenceService.Build(inputs(hasStoryboard: true))!,
                         DiscordPresenceService.Build(inputs(hasVideo: true))!,
                     })
            {
                Assert.That(state.Details, Does.Not.Contain("chart"));
                Assert.That(state.Details, Does.Not.Contain("storyboard"));
                Assert.That(state.Details, Does.Not.Contain("·"));
            }
        }

        // ---- idle ----

        /// <summary>
        /// Matches lazer exactly, which is not the obvious shape: osu.Desktop's DiscordRichPresence
        /// puts the word on the STATE line and leaves details EMPTY
        /// (<c>presence.State = "Idle"; presence.Details = string.Empty;</c>).
        /// </summary>
        [Test]
        public void IdleShowsLazersShapeAndNoTrack()
        {
            var state = DiscordPresenceService.Build(inputs(playing: false, notPlayingForMs: DiscordPresenceService.IDLE_AFTER_MS))!;

            Assert.That(state.Activity, Is.EqualTo(PresenceActivity.Idle));
            Assert.That(state.State, Is.EqualTo("Idle"));
            Assert.That(state.Details, Is.Empty);
            Assert.That(state.StartUtc, Is.Null);
            Assert.That(state.EndUtc, Is.Null);
            Assert.That(state.ImageUrl, Is.Null, "no cover: idle is not about a track");
        }

        [Test]
        public void APauseShorterThanTheThresholdKeepsTheTrackOnScreen()
        {
            var state = DiscordPresenceService.Build(inputs(playing: false,
                notPlayingForMs: DiscordPresenceService.IDLE_AFTER_MS - 1, onlineSetId: 1084287))!;

            Assert.That(state.Activity, Is.Not.EqualTo(PresenceActivity.Idle));
            Assert.That(state.Details, Is.EqualTo("FREEDOM DIVE"));
            Assert.That(state.StartUtc, Is.Null, "still no progress bar while paused");
            Assert.That(state.ImageUrl, Is.Not.Null, "the cover stays up through a short pause");
        }

        /// <summary>
        /// An empty queue reaches idle by the same timer, with no track metadata to describe.
        /// </summary>
        [Test]
        public void AnEmptyQueueGoesIdleOnTheSameTimer()
        {
            var state = DiscordPresenceService.Build(inputs(title: "", artist: "", playing: false,
                notPlayingForMs: DiscordPresenceService.IDLE_AFTER_MS))!;

            Assert.That(state.Activity, Is.EqualTo(PresenceActivity.Idle));
            Assert.That(state.State, Is.EqualTo("Idle"));
        }

        [Test]
        public void AnEmptyQueueBelowTheThresholdShowsNothingRatherThanIdle()
        {
            Assert.That(DiscordPresenceService.Build(inputs(title: "", artist: "", playing: false,
                notPlayingForMs: DiscordPresenceService.IDLE_AFTER_MS - 1)), Is.Null);
        }

        /// <summary>
        /// Playing again restores the full presence in one step — the timer is reset by playback, not
        /// wound down, so there is no interval where a resumed track still reads as idle.
        /// </summary>
        [Test]
        public void ResumingRestoresTheTrackImmediately()
        {
            var state = DiscordPresenceService.Build(inputs(playing: true, notPlayingForMs: 0, onlineSetId: 1084287))!;

            Assert.That(state.Activity, Is.EqualTo(PresenceActivity.Listening));
            Assert.That(state.Details, Is.EqualTo("FREEDOM DIVE"));
            Assert.That(state.StartUtc, Is.Not.Null, "and the progress bar is back");
        }

        [Test]
        public void GoingIdleAndComingBackAreBothWorthPublishing()
        {
            var playing = DiscordPresenceService.Build(inputs())!;
            var idle = DiscordPresenceService.Build(inputs(playing: false, notPlayingForMs: DiscordPresenceService.IDLE_AFTER_MS))!;

            Assert.That(DiscordPresenceService.NeedsRepublish(playing, idle), Is.True, "going idle");
            Assert.That(DiscordPresenceService.NeedsRepublish(idle, playing), Is.True, "coming back");
        }

        // ---- the idle timer itself ----

        [Test]
        public void TheIdleTimerAccumulatesWhileNothingPlays()
        {
            using var service = new DiscordPresenceService(new NullPresenceClient());

            Assert.That(service.TrackIdleTime(false, now), Is.EqualTo(TimeSpan.Zero), "starts at the moment it stops");
            Assert.That(service.TrackIdleTime(false, now.AddMinutes(3)), Is.EqualTo(TimeSpan.FromMinutes(3)));
            Assert.That(service.TrackIdleTime(false, now.AddMinutes(9)), Is.EqualTo(TimeSpan.FromMinutes(9)));
        }

        /// <summary>
        /// Playing RESETS rather than winds down, which is what makes a resume restore the presence
        /// in one step: a track that plays for a second after ten idle minutes is not still idle.
        /// </summary>
        [Test]
        public void PlayingResetsTheIdleTimerOutright()
        {
            using var service = new DiscordPresenceService(new NullPresenceClient());

            service.TrackIdleTime(false, now);
            Assert.That(service.TrackIdleTime(false, now.AddMinutes(10)), Is.EqualTo(TimeSpan.FromMinutes(10)));

            Assert.That(service.TrackIdleTime(true, now.AddMinutes(10)), Is.EqualTo(TimeSpan.Zero));
            Assert.That(service.TrackIdleTime(false, now.AddMinutes(11)), Is.EqualTo(TimeSpan.Zero),
                "the clock restarts from the pause, not from the original stop");
        }

        private class NullPresenceClient : IPresenceClient
        {
            public void Start() { }
            public void Publish(PresenceState state) { }
            public void Clear() { }
            public void Dispose() { }
        }

        [Test]
        public void IdleCrossesAsThePlainAppPresence()
        {
            var presence = DiscordPresenceClient.BuildRichPresence(DiscordPresenceService.IdleState);

            // Not Listening: nothing is playing, so claiming to listen would be a lie. lazer sets no
            // type at all for an idle user, which is Discord's default of Playing.
            Assert.That(presence.Type, Is.EqualTo(ActivityType.Playing));
            Assert.That(presence.State, Is.EqualTo("Idle"));
            Assert.That(presence.Timestamps, Is.Null);
        }

        // ---- content ----

        [Test]
        public void TheArtistIsTheSecondLine()
        {
            Assert.That(DiscordPresenceService.Build(inputs())!.State, Is.EqualTo("xi"));
        }

        [Test]
        public void TheDifficultyJoinsTheArtistWhenThereIsOne()
        {
            var state = DiscordPresenceService.Build(inputs(difficulty: "FOUR DIMENSIONS"))!;

            Assert.That(state.State, Is.EqualTo("xi · [FOUR DIMENSIONS]"));
        }

        [Test]
        public void ABlankDifficultyIsNotShownAsEmptyBrackets()
        {
            Assert.That(DiscordPresenceService.Build(inputs(difficulty: "   "))!.State, Is.EqualTo("xi"));
        }

        [Test]
        public void NothingIsPublishedWithoutMetadata()
        {
            Assert.That(DiscordPresenceService.Build(inputs(title: "", artist: "")), Is.Null);
        }

        // ---- timestamps ----

        [Test]
        public void PlayingProducesAStartAndEndAroundTheCurrentPosition()
        {
            var state = DiscordPresenceService.Build(inputs(position: 30_000, length: 180_000))!;

            Assert.That(state.StartUtc, Is.EqualTo(now.AddSeconds(-30)));
            Assert.That(state.EndUtc, Is.EqualTo(now.AddSeconds(150)));
        }

        /// <summary>
        /// Discord's progress bar has no paused state — it would keep counting down while the music
        /// sits still. Showing no bar is the honest option, and the one Spotify takes.
        /// </summary>
        [Test]
        public void PausingTakesTheProgressBarAway()
        {
            var state = DiscordPresenceService.Build(inputs(playing: false))!;

            Assert.That(state.StartUtc, Is.Null);
            Assert.That(state.EndUtc, Is.Null);
            // The text is unaffected — a paused track is still the track that's loaded.
            Assert.That(state.Details, Is.EqualTo("FREEDOM DIVE"));
        }

        /// <summary>
        /// At double speed the remaining 150 seconds of music take 75 seconds of wall clock, which is
        /// what Discord's countdown has to show.
        /// </summary>
        [Test]
        public void TheRateScalesBothEnds()
        {
            var state = DiscordPresenceService.Build(inputs(position: 30_000, length: 180_000, rate: 2))!;

            Assert.That(state.StartUtc, Is.EqualTo(now.AddSeconds(-15)));
            Assert.That(state.EndUtc, Is.EqualTo(now.AddSeconds(75)));
        }

        [Test]
        public void AnUnknownLengthProducesNoProgressBar()
        {
            var state = DiscordPresenceService.Build(inputs(length: 0))!;

            Assert.That(state.StartUtc, Is.Null);
            Assert.That(state.EndUtc, Is.Null);
        }

        /// <summary>A stopped clock reports rate 0; dividing by it would collapse the whole track
        /// into a single instant.</summary>
        [Test]
        public void ARateOfZeroProducesNoProgressBar()
        {
            Assert.That(DiscordPresenceService.Build(inputs(rate: 0))!.StartUtc, Is.Null);
        }

        [Test]
        public void APositionPastTheEndIsClampedRatherThanRunningBackwards()
        {
            var state = DiscordPresenceService.Build(inputs(position: 999_000, length: 180_000))!;

            Assert.That(state.EndUtc, Is.EqualTo(now));
            Assert.That(state.StartUtc, Is.EqualTo(now.AddSeconds(-180)));
        }

        // ---- when a change is worth another IPC round trip ----

        [Test]
        public void TheFirstPresenceAlwaysNeedsPublishing()
        {
            Assert.That(DiscordPresenceService.NeedsRepublish(null, DiscordPresenceService.Build(inputs())!), Is.True);
        }

        [Test]
        public void FlippingTheChartOnRepublishes()
        {
            var listening = DiscordPresenceService.Build(inputs())!;
            var watching = DiscordPresenceService.Build(inputs(renderChart: true))!;

            Assert.That(DiscordPresenceService.NeedsRepublish(listening, watching), Is.True);
        }

        [Test]
        public void SwitchingDifficultyRepublishes()
        {
            var easy = DiscordPresenceService.Build(inputs(difficulty: "Easy"))!;
            var insane = DiscordPresenceService.Build(inputs(difficulty: "Insane"))!;

            Assert.That(DiscordPresenceService.NeedsRepublish(easy, insane), Is.True);
        }

        [Test]
        public void SeekingRepublishes()
        {
            var before = DiscordPresenceService.Build(inputs(position: 30_000))!;
            var after = DiscordPresenceService.Build(inputs(position: 120_000))!;

            Assert.That(DiscordPresenceService.NeedsRepublish(before, after), Is.True);
        }

        [Test]
        public void PausingAndResumingBothRepublish()
        {
            var playing = DiscordPresenceService.Build(inputs())!;
            var paused = DiscordPresenceService.Build(inputs(playing: false))!;

            Assert.That(DiscordPresenceService.NeedsRepublish(playing, paused), Is.True, "pausing");
            Assert.That(DiscordPresenceService.NeedsRepublish(paused, playing), Is.True, "resuming");
        }

        [Test]
        public void ChangingTheRateRepublishes()
        {
            var normal = DiscordPresenceService.Build(inputs())!;
            var fast = DiscordPresenceService.Build(inputs(rate: 1.5))!;

            Assert.That(DiscordPresenceService.NeedsRepublish(normal, fast), Is.True);
        }

        /// <summary>
        /// The load-bearing negative case. A track playing normally recomputes to almost — but not
        /// exactly — the same start/end every frame, because both the clock and the anchor are
        /// moving. Treating that as a change would mean an IPC round trip per frame, forever.
        /// </summary>
        [Test]
        public void OrdinaryPlaybackDriftIsNotAChange()
        {
            var published = DiscordPresenceService.Build(inputs(position: 30_000))!;

            // A second later, having played a second: same picture, timestamps a few ms off.
            var later = new PresenceState(published.Activity, published.Details, published.State,
                published.StartUtc!.Value.AddMilliseconds(17), published.EndUtc!.Value.AddMilliseconds(17));

            Assert.That(DiscordPresenceService.NeedsRepublish(published, later), Is.False);
        }

        [Test]
        public void DriftBeyondToleranceIsAChange()
        {
            var published = DiscordPresenceService.Build(inputs(position: 30_000))!;

            var jumped = new PresenceState(published.Activity, published.Details, published.State,
                published.StartUtc!.Value.AddMilliseconds(DiscordPresenceService.TIMESTAMP_TOLERANCE_MS + 1),
                published.EndUtc);

            Assert.That(DiscordPresenceService.NeedsRepublish(published, jumped), Is.True);
        }

        // ---- the wire mapping ----

        [Test]
        public void ListeningCrossesAsDiscordListening()
        {
            var presence = DiscordPresenceClient.BuildRichPresence(DiscordPresenceService.Build(inputs())!);

            Assert.That(presence.Type, Is.EqualTo(ActivityType.Listening));
        }

        [Test]
        public void BothWatchingActivitiesCrossAsDiscordWatching()
        {
            var storyboard = DiscordPresenceClient.BuildRichPresence(DiscordPresenceService.Build(inputs(hasStoryboard: true))!);
            var chart = DiscordPresenceClient.BuildRichPresence(DiscordPresenceService.Build(inputs(renderChart: true))!);

            Assert.That(storyboard.Type, Is.EqualTo(ActivityType.Watching));
            Assert.That(chart.Type, Is.EqualTo(ActivityType.Watching));
        }

        [Test]
        public void TheTextCrossesOnTheTwoPresenceLines()
        {
            var presence = DiscordPresenceClient.BuildRichPresence(
                DiscordPresenceService.Build(inputs(hasStoryboard: true, difficulty: "Insane"))!);

            Assert.That(presence.Details, Is.EqualTo("FREEDOM DIVE"));
            Assert.That(presence.State, Is.EqualTo("xi · [Insane]"));
        }

        [Test]
        public void TimestampsCrossAsAStartEndPair()
        {
            var presence = DiscordPresenceClient.BuildRichPresence(
                DiscordPresenceService.Build(inputs(position: 30_000, length: 180_000))!);

            Assert.That(presence.Timestamps, Is.Not.Null);
            Assert.That(presence.Timestamps!.Start, Is.EqualTo(now.AddSeconds(-30)));
            Assert.That(presence.Timestamps.End, Is.EqualTo(now.AddSeconds(150)));
        }

        /// <summary>
        /// Half a pair would make Discord draw a stopwatch counting up forever instead of a track
        /// progress bar, so a paused track has to send no timestamps at all.
        /// </summary>
        [Test]
        public void APausedTrackCrossesWithNoTimestampsAtAll()
        {
            var presence = DiscordPresenceClient.BuildRichPresence(DiscordPresenceService.Build(inputs(playing: false))!);

            Assert.That(presence.Timestamps, Is.Null);
        }

        // ---- Discord's own string limits ----

        [Test]
        public void ShortStringsArePaddedRatherThanRejected()
        {
            string clamped = DiscordPresenceClient.ClampLength("A");

            Assert.That(clamped, Has.Length.EqualTo(2));
            Assert.That((int)clamped[1], Is.EqualTo(0x200B), "padding must be a zero-width space so nothing visible is added");
        }

        [Test]
        public void LongStringsAreCutByEncodedBytesNotCharacters()
        {
            // 200 CJK characters is 600 UTF-8 bytes but only 200 chars — a character-count clamp
            // would sail past Discord's 128-BYTE limit and the whole update would be rejected.
            string clamped = DiscordPresenceClient.ClampLength(new string('あ', 200));

            Assert.That(Encoding.UTF8.GetByteCount(clamped), Is.LessThanOrEqualTo(128));
            Assert.That(clamped, Does.EndWith("…"));
        }

        [Test]
        public void StringsThatAlreadyFitAreLeftAlone()
        {
            Assert.That(DiscordPresenceClient.ClampLength("storyboard · FREEDOM DIVE"), Is.EqualTo("storyboard · FREEDOM DIVE"));
        }

        [Test]
        public void TheShippedClientIdIsARealApplication()
        {
            // osu!JukeBox's own application. The id IS the app name on every listener's profile, so
            // a build shipping an unusable one would connect to nothing, and one shipping somebody
            // else's would misattribute every user.
            Assert.That(DiscordPresenceClient.CLIENT_ID, Is.EqualTo("1544686568302841997"));
            Assert.That(DiscordPresenceClient.IsUsableClientId(DiscordPresenceClient.CLIENT_ID), Is.True);
        }

        [Test]
        public void AnUnusableClientIdIsRejectedRatherThanHalfConnected()
        {
            Assert.That(DiscordPresenceClient.IsUsableClientId("000000000000000000"), Is.False);
            Assert.That(DiscordPresenceClient.IsUsableClientId(string.Empty), Is.False);
            Assert.That(DiscordPresenceClient.IsUsableClientId("not-an-id"), Is.False);
        }

        // ---- cover art ----

        [Test]
        public void ThePlayingSetsCoverBecomesTheLargeImage()
        {
            var state = DiscordPresenceService.Build(inputs(onlineSetId: 1084287))!;

            Assert.That(state.ImageUrl, Is.EqualTo("https://b.ppy.sh/thumb/1084287l.jpg"));
            Assert.That(state.ImageText, Is.EqualTo("xi - FREEDOM DIVE"));
        }

        /// <summary>
        /// A local or dropped map has no online listing and so no published cover. Carrying no image
        /// is what makes Discord fall back to the application's own icon; a made-up URL would leave
        /// a broken hole on the card instead.
        /// </summary>
        [Test]
        public void AMapWithNoOnlineIdCarriesNoImageAtAll()
        {
            var state = DiscordPresenceService.Build(inputs(onlineSetId: 0))!;

            Assert.That(state.ImageUrl, Is.Null);
            Assert.That(state.ImageText, Is.Null);
        }

        /// <summary>
        /// The cover URL is handed straight to a setter that THROWS past Discord's 256-character
        /// cap, taking the whole update with it. Nothing checks that at runtime because the format
        /// cannot overflow — so the property is asserted here instead, across the widest set ids
        /// representable, and it is this test that would catch a future format change (a title in
        /// the path, say) that no longer holds it.
        /// </summary>
        [Test]
        public void NoSetIdCanProduceACoverUrlOverDiscordsCap()
        {
            foreach (int setId in new[] { 1, 999, 1084287, int.MaxValue })
            {
                Assert.That(DiscordPresenceService.CoverUrl(setId),
                    Has.Length.LessThanOrEqualTo(DiscordPresenceClient.MAX_IMAGE_REFERENCE_LENGTH),
                    $"set id {setId}");
            }
        }

        [Test]
        public void ANonExistentSetIdIsNotACover()
        {
            Assert.That(DiscordPresenceService.CoverUrl(0), Is.Null);
            Assert.That(DiscordPresenceService.CoverUrl(-1), Is.Null);
        }

        /// <summary>
        /// Same title and artist, different mapset — a remap, or a second upload of the same song.
        /// The text lines are identical, so only the cover says the picture changed.
        /// </summary>
        [Test]
        public void ADifferentSetWithTheSameNameRepublishes()
        {
            var first = DiscordPresenceService.Build(inputs(onlineSetId: 1084287))!;
            var second = DiscordPresenceService.Build(inputs(onlineSetId: 999999))!;

            Assert.That(first.Details, Is.EqualTo(second.Details), "precondition: the text is identical");
            Assert.That(DiscordPresenceService.NeedsRepublish(first, second), Is.True);
        }

        [Test]
        public void TheCoverCrossesAsAnExternalLargeImage()
        {
            var assets = DiscordPresenceClient.BuildAssets(DiscordPresenceService.Build(inputs(onlineSetId: 1084287))!)!;

            // Sent as a plain URL. Discord rewrites it into its own signed mp:external/… proxy form
            // on the way in (verified live against a real client); nothing here has to do that.
            Assert.That(assets.LargeImageKey, Is.EqualTo("https://b.ppy.sh/thumb/1084287l.jpg"));
            Assert.That(assets.LargeImageText, Is.EqualTo("xi - FREEDOM DIVE"));
        }

        /// <summary>
        /// The tooltip carries the mapset's own artist and title, which are arbitrary user content
        /// and can be far longer than Discord's 128-character cap. The library's setter throws past
        /// it rather than truncating, so the clamp is what keeps a long-titled map from silently
        /// costing the entire presence update.
        /// </summary>
        [Test]
        public void ALongTitledMapsTooltipIsClampedBeforeItReachesTheSetter()
        {
            var assets = DiscordPresenceClient.BuildAssets(new PresenceState(
                PresenceActivity.Listening, "d", "s",
                null, null,
                DiscordPresenceService.CoverUrl(1084287),
                new string('x', 500)))!;

            Assert.That(assets.LargeImageText, Has.Length.LessThanOrEqualTo(128));
        }

        /// <summary>
        /// Not an EMPTY assets block: an activity carrying no images at all is precisely what makes
        /// Discord show the application icon, which is the intended fallback.
        /// </summary>
        [Test]
        public void NoCoverAndNoBadgeMeansNoAssetsBlockAtAll()
        {
            Assert.That(DiscordPresenceClient.SMALL_IMAGE_KEY, Is.Empty, "precondition: no badge art is uploaded yet");
            Assert.That(DiscordPresenceClient.BuildAssets(DiscordPresenceService.Build(inputs(onlineSetId: 0))!), Is.Null);
        }

        /// <summary>
        /// Presence is decoration; it must never be able to disturb playback. An id that doesn't
        /// parse opens no socket at all, so every entry point is a no-op that cannot throw.
        ///
        /// <para>
        /// NO TEST HERE MAY OPEN A SOCKET. An earlier version of this one connected with a
        /// well-formed but unregistered id to exercise the failed-handshake path, which meant the
        /// suite reached out to whatever Discord was running on the machine — real IPC inside a
        /// test process, and a second source of exactly the trouble that made main red. The
        /// connect, reject and reconnect paths were verified live and out of band instead; what
        /// belongs in the suite is that nothing here touches Discord.
        /// </para>
        /// </summary>
        [Test]
        public void EveryClientEntryPointIsTotalWithoutAnApplicationId()
        {
            var state = DiscordPresenceService.Build(inputs())!;

            Assert.DoesNotThrow(() =>
            {
                using var unusable = new DiscordPresenceClient("000000000000000000");
                unusable.Start();
                unusable.Publish(state);
                unusable.Clear();
            });
        }

        [Test]
        public void PublishingBeforeStartDoesNotThrow()
        {
            using var client = new DiscordPresenceClient("000000000000000000");

            Assert.DoesNotThrow(() => client.Publish(DiscordPresenceService.Build(inputs())!));
        }
    }
}
