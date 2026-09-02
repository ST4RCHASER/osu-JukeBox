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
            string? difficulty = null)
            => new PresenceInputs(title, artist, difficulty, hasStoryboard, renderChart, playing, position, length, rate, now);

        // ---- precedence: the activity follows what is actually on screen ----

        [Test]
        public void RenderChartOnIsWatchingTheChart()
        {
            var state = DiscordPresenceService.Build(inputs(renderChart: true))!;

            Assert.That(state.Activity, Is.EqualTo(PresenceActivity.WatchingChart));
            Assert.That(state.Details, Is.EqualTo("chart · FREEDOM DIVE"));
        }

        [Test]
        public void StoryboardWithoutTheChartIsWatchingTheStoryboard()
        {
            var state = DiscordPresenceService.Build(inputs(hasStoryboard: true))!;

            Assert.That(state.Activity, Is.EqualTo(PresenceActivity.WatchingStoryboard));
            Assert.That(state.Details, Is.EqualTo("storyboard · FREEDOM DIVE"));
        }

        /// <summary>
        /// The chart is drawn ON TOP of the storyboard, so when a map has both, the chart is what
        /// someone looking over the user's shoulder would actually see.
        /// </summary>
        [Test]
        public void TheChartWinsWhenTheMapHasBoth()
        {
            var state = DiscordPresenceService.Build(inputs(renderChart: true, hasStoryboard: true))!;

            Assert.That(state.Activity, Is.EqualTo(PresenceActivity.WatchingChart));
            Assert.That(state.Details, Does.StartWith("chart"));
        }

        [Test]
        public void PlainAudioIsListening()
        {
            var state = DiscordPresenceService.Build(inputs())!;

            Assert.That(state.Activity, Is.EqualTo(PresenceActivity.Listening));
            // No prefix: "Listening to osu!JukeBox / FREEDOM DIVE" is the Spotify shape, and a word
            // in front of the track would only get in the way of it.
            Assert.That(state.Details, Is.EqualTo("FREEDOM DIVE"));
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

            Assert.That(presence.Details, Is.EqualTo("storyboard · FREEDOM DIVE"));
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
        public void TheShippedClientIdIsRecognisedAsAPlaceholder()
        {
            // The feature ships inert on purpose: borrowing another app's id would advertise every
            // listener as using THAT app. Filling CLIENT_ID in is the deliberate step that turns it on.
            Assert.That(DiscordPresenceClient.IsUsableClientId(DiscordPresenceClient.CLIENT_ID), Is.False);
            Assert.That(DiscordPresenceClient.IsUsableClientId("1216669957799018608"), Is.True);
        }

        /// <summary>
        /// Presence is decoration; it must never be able to disturb playback. With no application id
        /// no socket is opened at all, and with a well-formed one and no Discord listening the
        /// library's connect simply fails in the background — neither may surface as an exception.
        /// </summary>
        [Test]
        public void EveryClientEntryPointIsTotalWithoutDiscord()
        {
            var state = DiscordPresenceService.Build(inputs())!;

            Assert.DoesNotThrow(() =>
            {
                using var placeholder = new DiscordPresenceClient();
                placeholder.Start();
                placeholder.Publish(state);
                placeholder.Clear();
            }, "with the placeholder id");

            Assert.DoesNotThrow(() =>
            {
                // Well-formed but registered to nothing, so the handshake can only fail.
                using var unregistered = new DiscordPresenceClient("123456789012345678");
                unregistered.Start();
                unregistered.Publish(state);
                unregistered.Clear();
            }, "with an id Discord will not accept");
        }

        [Test]
        public void PublishingBeforeStartDoesNotThrow()
        {
            using var client = new DiscordPresenceClient();

            Assert.DoesNotThrow(() => client.Publish(DiscordPresenceService.Build(inputs())!));
        }
    }
}
