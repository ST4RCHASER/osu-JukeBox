using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Playback
{
    [TestFixture]
    public class MusicQueueTest
    {
        [Test]
        public void EnqueuePopFifoAndDedupe()
        {
            var q = new MusicQueue();
            var a = new BeatmapSetInfo { Id = 1 };
            var b = new BeatmapSetInfo { Id = 2 };
            q.Enqueue(a);
            q.Enqueue(b);
            q.Enqueue(new BeatmapSetInfo { Id = 1 });
            Assert.That(q.Items, Has.Count.EqualTo(2));
            Assert.That(q.PopNext()!.Id, Is.EqualTo(1));
            Assert.That(q.PopNext()!.Id, Is.EqualTo(2));
            Assert.That(q.PopNext(), Is.Null);
        }
    }
}
