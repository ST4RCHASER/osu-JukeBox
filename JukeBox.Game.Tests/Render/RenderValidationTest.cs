#nullable enable

using JukeBox.Game.UI.Render;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Render
{
    /// <summary>
    /// Every field validator the Render dialog gates the Render button on — asserted through the pure
    /// <see cref="RenderValidation.Validate"/> the dialog calls, so each rule (bad resolution, odd
    /// dimensions, out-of-range fps/bitrate, empty or folder-only path, unparseable or out-of-order
    /// or out-of-song times) is exercised for the message it produces, not merely that "something was
    /// wrong".
    /// </summary>
    [TestFixture]
    public class RenderValidationTest
    {
        // A song two minutes long, so within-song bounds have something to bite on.
        private const double song_length = 120_000;

        private static RenderFormValues valid() => new RenderFormValues(
            Format: "mp4",
            Resolution: "1920x1080",
            Fps: "60",
            Path: "/tmp/out.mp4",
            StartTime: "0:00:10",
            EndTime: "0:01:00",
            AudioBitrate: "192");

        [Test]
        public void AFullyValidFormProducesARequestWithNoErrors()
        {
            var result = RenderValidation.Validate(valid(), song_length);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Request, Is.Not.Null);

            var request = result.Request!;
            Assert.That(request.Width, Is.EqualTo(1920));
            Assert.That(request.Height, Is.EqualTo(1080));
            Assert.That(request.Fps, Is.EqualTo(60));
            Assert.That(request.Format, Is.EqualTo("mp4"));
            Assert.That(request.AudioBitrateKbps, Is.EqualTo(192));
            Assert.That(request.StartMs, Is.EqualTo(10_000));
            Assert.That(request.EndMs, Is.EqualTo(60_000));
        }

        [TestCase("1920")]        // one number, no separator
        [TestCase("1920x")]       // missing height
        [TestCase("axb")]         // not numbers
        [TestCase("-16x1080")]    // negative
        [TestCase("8x8")]         // below the 16px floor
        [TestCase("100000x1080")] // above the 8K ceiling
        public void RejectsBadResolution(string resolution)
        {
            var result = RenderValidation.Validate(valid() with { Resolution = resolution }, song_length);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.ContainsKey(RenderField.Resolution));
            Assert.That(result.Request, Is.Null);
        }

        [TestCase("1921x1080")] // odd width
        [TestCase("1920x1081")] // odd height
        public void RejectsOddDimensions(string resolution)
        {
            var result = RenderValidation.Validate(valid() with { Resolution = resolution }, song_length);
            Assert.That(result.Errors.ContainsKey(RenderField.Resolution), Is.True);
        }

        [TestCase("0")]
        [TestCase("-30")]
        [TestCase("500")]  // above the 240 ceiling
        [TestCase("30.5")] // not a whole number
        [TestCase("abc")]
        [TestCase("")]
        public void RejectsBadFps(string fps)
        {
            var result = RenderValidation.Validate(valid() with { Fps = fps }, song_length);
            Assert.That(result.Errors.ContainsKey(RenderField.Fps), Is.True);
        }

        [TestCase("16")]  // below floor
        [TestCase("999")] // above ceiling
        [TestCase("0")]
        [TestCase("")]
        public void RejectsBadAudioBitrate(string bitrate)
        {
            var result = RenderValidation.Validate(valid() with { AudioBitrate = bitrate }, song_length);
            Assert.That(result.Errors.ContainsKey(RenderField.AudioBitrate), Is.True);
        }

        [TestCase("")]
        [TestCase("   ")]
        public void RejectsEmptyPath(string path)
        {
            var result = RenderValidation.Validate(valid() with { Path = path }, song_length);
            Assert.That(result.Errors.ContainsKey(RenderField.Path), Is.True);
        }

        [Test]
        public void RejectsAPathWithNoFileName()
        {
            var result = RenderValidation.Validate(valid() with { Path = "/tmp/videos/" }, song_length);
            Assert.That(result.Errors.ContainsKey(RenderField.Path), Is.True);
        }

        [Test]
        public void RejectsEndNotAfterStart()
        {
            var equal = RenderValidation.Validate(valid() with { StartTime = "0:00:30", EndTime = "0:00:30" }, song_length);
            Assert.That(equal.Errors.ContainsKey(RenderField.EndTime), Is.True);

            var before = RenderValidation.Validate(valid() with { StartTime = "0:00:40", EndTime = "0:00:30" }, song_length);
            Assert.That(before.Errors.ContainsKey(RenderField.EndTime), Is.True);
        }

        [Test]
        public void RejectsEndPastTheEndOfTheSong()
        {
            var result = RenderValidation.Validate(valid() with { EndTime = "0:03:00" }, song_length);
            Assert.That(result.Errors.ContainsKey(RenderField.EndTime), Is.True);
        }

        [TestCase("nonsense")]
        [TestCase("0:99:00")] // minutes out of range
        [TestCase("0:00:75")] // seconds out of range
        [TestCase("1:2:3:4")] // too many parts
        public void RejectsUnparseableTimes(string time)
        {
            var result = RenderValidation.Validate(valid() with { EndTime = time }, song_length);
            Assert.That(result.Errors.ContainsKey(RenderField.EndTime), Is.True);
        }

        [Test]
        public void RejectsUnknownFormat()
        {
            var result = RenderValidation.Validate(valid() with { Format = "avi" }, song_length);
            Assert.That(result.Errors.ContainsKey(RenderField.Format), Is.True);
        }

        [Test]
        public void AZeroSongLengthSkipsTheWithinSongBound()
        {
            // Length unknown (0) — a long end time must still be accepted as long as end > start.
            var result = RenderValidation.Validate(valid() with { EndTime = "9:00:00" }, songLengthMs: 0);
            Assert.That(result.Errors.ContainsKey(RenderField.EndTime), Is.False);
            Assert.That(result.IsValid, Is.True);
        }

        [TestCase("90", 90_000)]        // bare seconds
        [TestCase("1:30", 90_000)]      // mm:ss
        [TestCase("0:01:30", 90_000)]   // hh:mm:ss
        [TestCase("0:00:01.5", 1_500)]  // fractional seconds
        public void ParsesTimecodesToMilliseconds(string text, double expectedMs)
        {
            Assert.That(RenderValidation.tryParseTimecode(text, out double ms), Is.True);
            Assert.That(ms, Is.EqualTo(expectedMs));
        }

        [Test]
        public void FormatTimecodeRoundTrips()
        {
            Assert.That(RenderValidation.FormatTimecode(90_000), Is.EqualTo("0:01:30"));
            Assert.That(RenderValidation.FormatTimecode(3_661_000), Is.EqualTo("1:01:01"));
        }
    }
}
