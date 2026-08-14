#nullable enable

using JukeBox.Game.Detach;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Detach
{
    [TestFixture]
    public class ViewerSyncStateTest
    {
        [Test]
        public void RoundTripPreservesAllFields()
        {
            var state = new ViewerSyncState
            {
                SetId = 165202,
                SetDirectory = "/tmp/cache/165202",
                OsuFile = "/tmp/cache/165202/heron [Beginner].osu",
                PositionMs = 12345.678,
                Rate = 1.25,
                Playing = true,
                SentAtUnixMs = 1755000000123,
                BackgroundDim = 0.45,
                BackgroundBlur = 0.2,
                PlayfieldZoom = 1.5,
                GlobalAudioOffset = -30,
                RenderChart = true,
                ShowStoryboardVideo = false,
                Skin = "Triangles",
            };

            var parsed = ViewerSyncState.FromJson(state.ToJson());

            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.Version, Is.EqualTo(ViewerSyncState.PROTOCOL_VERSION));
            Assert.That(parsed.SetId, Is.EqualTo(state.SetId));
            Assert.That(parsed.SetDirectory, Is.EqualTo(state.SetDirectory));
            Assert.That(parsed.OsuFile, Is.EqualTo(state.OsuFile));
            Assert.That(parsed.PositionMs, Is.EqualTo(state.PositionMs));
            Assert.That(parsed.Rate, Is.EqualTo(state.Rate));
            Assert.That(parsed.Playing, Is.EqualTo(state.Playing));
            Assert.That(parsed.SentAtUnixMs, Is.EqualTo(state.SentAtUnixMs));
            Assert.That(parsed.BackgroundDim, Is.EqualTo(state.BackgroundDim));
            Assert.That(parsed.BackgroundBlur, Is.EqualTo(state.BackgroundBlur));
            Assert.That(parsed.PlayfieldZoom, Is.EqualTo(state.PlayfieldZoom));
            Assert.That(parsed.GlobalAudioOffset, Is.EqualTo(state.GlobalAudioOffset));
            Assert.That(parsed.RenderChart, Is.EqualTo(state.RenderChart));
            Assert.That(parsed.ShowStoryboardVideo, Is.EqualTo(state.ShowStoryboardVideo));
            Assert.That(parsed.Skin, Is.EqualTo(state.Skin));
        }

        [Test]
        public void NullFieldsRoundTrip()
        {
            var parsed = ViewerSyncState.FromJson(new ViewerSyncState().ToJson());

            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.SetDirectory, Is.Null);
            Assert.That(parsed.OsuFile, Is.Null);
        }

        // The protocol is line-delimited: a snapshot containing a newline would be read as two
        // torn (and both discarded) messages.
        [Test]
        public void SerializedFormIsASingleLine()
        {
            string json = new ViewerSyncState { SetDirectory = "/tmp/x", OsuFile = "/tmp/x/a.osu" }.ToJson();

            Assert.That(json, Does.Not.Contain('\n'));
            Assert.That(json, Does.Not.Contain('\r'));
        }

        [Test]
        public void MalformedInputReturnsNullInsteadOfThrowing()
        {
            Assert.That(ViewerSyncState.FromJson("{ torn json"), Is.Null);
            Assert.That(ViewerSyncState.FromJson(""), Is.Null);
            Assert.That(ViewerSyncState.FromJson("null"), Is.Null);
        }

        // A NEWER main process may add fields within the same protocol version; an older viewer
        // must parse around them rather than choke.
        [Test]
        public void UnknownFieldsAreIgnored()
        {
            string json = new ViewerSyncState { SetId = 7 }.ToJson();
            json = json.Insert(json.Length - 1, ",\"FutureField\":42");

            var parsed = ViewerSyncState.FromJson(json);

            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.SetId, Is.EqualTo(7));
        }

        [Test]
        public void ForeignVersionSurvivesParsingForTheVersionCheck()
        {
            var parsed = ViewerSyncState.FromJson("{\"Version\":999}");

            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.Version, Is.EqualTo(999));
        }
    }
}
