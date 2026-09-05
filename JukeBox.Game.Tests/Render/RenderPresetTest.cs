#nullable enable

using JukeBox.Game.UI.Render;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Render
{
    /// <summary>
    /// The Render dialog's preset behaviour, at the pure level the dialog delegates to: what each
    /// built-in preset fills in, that filling leaves the fields a preset doesn't own alone, and the
    /// snap rule — a named preset only while every field still matches it, Custom the instant one
    /// differs (which is exactly "editing any field switches the dropdown to Custom").
    /// </summary>
    [TestFixture]
    public class RenderPresetTest
    {
        [Test]
        public void BuiltInPresetsCarryTheirPlatformSpec()
        {
            Assert.That(RenderPreset.YouTube.Width, Is.EqualTo(1920));
            Assert.That(RenderPreset.YouTube.Height, Is.EqualTo(1080));
            Assert.That(RenderPreset.YouTube.Fps, Is.EqualTo(60));

            // TikTok is the vertical one — height greater than width.
            Assert.That(RenderPreset.TikTok.Height, Is.GreaterThan(RenderPreset.TikTok.Width));
            Assert.That(RenderPreset.TikTok.Width, Is.EqualTo(1080));
            Assert.That(RenderPreset.TikTok.Height, Is.EqualTo(1920));

            Assert.That(RenderPreset.Facebook.Width, Is.EqualTo(1280));
            Assert.That(RenderPreset.Facebook.Height, Is.EqualTo(720));

            Assert.That(RenderPreset.Custom.HasValues, Is.False);
            Assert.That(RenderPreset.YouTube.HasValues, Is.True);
        }

        [Test]
        public void ApplyToFillsOwnFieldsAndLeavesTheRestAlone()
        {
            var current = new RenderFormValues(
                Format: "webm",
                Resolution: "640x480",
                Fps: "24",
                Path: "/tmp/my clip.mov",
                StartTime: "0:00:05",
                EndTime: "0:00:30",
                AudioBitrate: "96");

            var filled = RenderPreset.YouTube.ApplyTo(current);

            // Preset's own fields get overwritten...
            Assert.That(filled.Resolution, Is.EqualTo("1920x1080"));
            Assert.That(filled.Fps, Is.EqualTo("60"));
            Assert.That(filled.Format, Is.EqualTo("mp4"));
            Assert.That(filled.AudioBitrate, Is.EqualTo("192"));

            // ...while path and the time range (which a preset says nothing about) are preserved.
            Assert.That(filled.Path, Is.EqualTo("/tmp/my clip.mov"));
            Assert.That(filled.StartTime, Is.EqualTo("0:00:05"));
            Assert.That(filled.EndTime, Is.EqualTo("0:00:30"));
        }

        [Test]
        public void ApplyingCustomChangesNothing()
        {
            var current = new RenderFormValues("mp4", "800x600", "50", "/o.mp4", "0:00:00", "0:01:00", "160");
            Assert.That(RenderPreset.Custom.ApplyTo(current), Is.EqualTo(current));
        }

        [Test]
        public void MatchIdentifiesAPresetOnlyWhenEveryFieldMatches()
        {
            Assert.That(RenderPreset.Match(1920, 1080, 60, "mp4", 192), Is.EqualTo(RenderPreset.YouTube));
            Assert.That(RenderPreset.Match(1080, 1920, 30, "mp4", 128), Is.EqualTo(RenderPreset.TikTok));

            // Case of the format string doesn't break the match.
            Assert.That(RenderPreset.Match(1280, 720, 30, "MP4", 128), Is.EqualTo(RenderPreset.Facebook));
        }

        [Test]
        public void EditingAnyFieldSnapsToCustom()
        {
            // Start on an exact YouTube match, then change ONE field at a time — each drops to Custom.
            Assert.That(RenderPreset.Match(1920, 1080, 60, "mp4", 192), Is.EqualTo(RenderPreset.YouTube));

            Assert.That(RenderPreset.Match(1920, 1080, 60, "mp4", 320), Is.EqualTo(RenderPreset.Custom), "audio bitrate edit");
            Assert.That(RenderPreset.Match(1920, 1080, 30, "mp4", 192), Is.EqualTo(RenderPreset.Custom), "fps edit");
            Assert.That(RenderPreset.Match(1920, 1080, 60, "webm", 192), Is.EqualTo(RenderPreset.Custom), "format edit");
            Assert.That(RenderPreset.Match(1280, 1080, 60, "mp4", 192), Is.EqualTo(RenderPreset.Custom), "width edit");
            Assert.That(RenderPreset.Match(1920, 720, 60, "mp4", 192), Is.EqualTo(RenderPreset.Custom), "height edit");
        }
    }
}
