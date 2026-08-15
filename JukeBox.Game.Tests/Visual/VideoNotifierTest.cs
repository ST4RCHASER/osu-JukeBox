#nullable enable

using System.Collections.Generic;
using JukeBox.Game.Screens;
using NUnit.Framework;

// NOTE: deliberately in the Tests.Visual namespace rather than a new Tests.Screens one, despite
// testing a Screens type. A `JukeBox.Game.Tests.Screens` namespace shadows the relative
// `Screens.MainScreen` crefs used elsewhere in these tests (they resolve through
// JukeBox.Game.Tests -> JukeBox.Game -> Screens), turning them into CS1574 build warnings.
namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The once-per-beatmap memory behind the "this video can't be played" notice. It lives outside
    /// the visual stack because that stack is rebuilt per song AND per difficulty, so it cannot
    /// remember anything itself.
    /// </summary>
    [TestFixture]
    public class VideoNotifierTest
    {
        private static List<string> announcements(VideoNotifier notifier)
        {
            var seen = new List<string>();
            notifier.Notice.BindValueChanged(e =>
            {
                if (e.NewValue != null)
                    seen.Add(e.NewValue);
            });
            return seen;
        }

        [Test]
        public void ABeatmapIsAnnouncedOnce()
        {
            var notifier = new VideoNotifier();
            var seen = announcements(notifier);

            notifier.ReportUnplayableVideo(1);

            Assert.That(seen, Has.Count.EqualTo(1));
            Assert.That(seen[0], Does.Contain("background"));
        }

        // The case a per-drawable guard would miss: the visual stack is rebuilt on every difficulty
        // switch and reports again each time.
        [Test]
        public void RepeatedReportsForTheSameBeatmapSaySomethingOnlyOnce()
        {
            var notifier = new VideoNotifier();
            var seen = announcements(notifier);

            notifier.ReportUnplayableVideo(1);
            notifier.ReportUnplayableVideo(1);
            notifier.ReportUnplayableVideo(1);

            Assert.That(seen, Has.Count.EqualTo(1));
        }

        // ...but a different beatmap is a different problem. This is the case that catches the
        // bindable trap: the message is identical text, and a bindable set to the value it already
        // holds reports no change, so the second beatmap's notice would never reach anyone.
        [Test]
        public void ADifferentBeatmapIsAnnouncedEvenThoughTheMessageIsIdentical()
        {
            var notifier = new VideoNotifier();
            var seen = announcements(notifier);

            notifier.ReportUnplayableVideo(1);
            notifier.ReportUnplayableVideo(2);

            Assert.That(seen, Has.Count.EqualTo(2));
            Assert.That(seen[0], Is.EqualTo(seen[1]));
        }

        [Test]
        public void ReturningToAnEarlierBeatmapAnnouncesAgain()
        {
            var notifier = new VideoNotifier();
            var seen = announcements(notifier);

            notifier.ReportUnplayableVideo(1);
            notifier.ReportUnplayableVideo(2);
            notifier.ReportUnplayableVideo(1);

            Assert.That(seen, Has.Count.EqualTo(3));
        }
    }
}
