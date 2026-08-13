#nullable enable

using System.IO;
using System.Linq;
using JukeBox.Game.Storyboard;
using NUnit.Framework;
using ReOsuStoryboardPlayer.Core.Base;

namespace JukeBox.Game.Tests.Storyboard
{
    // Regression coverage for the production crash on set 43466: an Animation object line
    // missing its optional trailing loopType column threw IndexOutOfRangeException out of Core's
    // ParseStoryboardAnimation, which the caller's whole-file catch turned into "drop the entire
    // storyboard" for one bad object among (usually) hundreds of good ones.
    [TestFixture]
    public class StoryboardLoaderTest
    {
        private string tmp = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(tmp, recursive: true);
        }

        private string writeOsb(string name, string contents)
        {
            string path = Path.Combine(tmp, name);
            File.WriteAllText(path, contents);
            return path;
        }

        // osu's spec allows "Animation,layer,origin,path,x,y,frameCount,frameDelay" (8 columns)
        // without the trailing loopType, which defaults to LoopForever.
        [Test]
        public void AnimationLineMissingLoopTypeDefaultsToLoopForever()
        {
            string osb = writeOsb("anim.osb", """
                osu file format v14

                [Events]
                //Storyboard Layer 0 (Background)
                Animation,Background,Centre,"anim.png",320,240,4,100
                """);

            var objects = StoryboardLoader.Load(osb, null);

            Assert.That(objects, Has.Count.EqualTo(1));
            Assert.That(objects[0], Is.InstanceOf<StoryboardAnimation>());

            var animation = (StoryboardAnimation)objects[0];
            Assert.That(animation.LoopType, Is.EqualTo(LoopType.LoopForever));
            Assert.That(animation.FrameCount, Is.EqualTo(4));
            Assert.That(animation.FrameDelay, Is.EqualTo(100f));
        }

        // An explicit loopType column must be left untouched (not doubled up).
        [Test]
        public void AnimationLineWithExplicitLoopTypeIsUnchanged()
        {
            string osb = writeOsb("anim-explicit.osb", """
                osu file format v14

                [Events]
                //Storyboard Layer 0 (Background)
                Animation,Background,Centre,"anim.png",320,240,4,100,LoopOnce
                """);

            var objects = StoryboardLoader.Load(osb, null);

            Assert.That(objects, Has.Count.EqualTo(1));
            var animation = (StoryboardAnimation)objects[0];
            Assert.That(animation.LoopType, Is.EqualTo(LoopType.LoopOnce));
        }

        // One malformed object line (too few columns to even resolve its image path) must not
        // drop the sprites around it — only that one object is skipped.
        [Test]
        public void MalformedObjectLineIsSkippedWithoutDroppingValidSprites()
        {
            string osb = writeOsb("mixed.osb", """
                osu file format v14

                [Events]
                //Storyboard Layer 0 (Background)
                Sprite,Background,Centre,"bg1.png",100,100
                Sprite,Background,Centre
                Sprite,Background,Centre,"bg2.png",200,200
                """);

            var objects = StoryboardLoader.Load(osb, null);

            Assert.That(objects, Has.Count.EqualTo(2));
            Assert.That(objects.Select(o => o.ImageFilePath), Is.EquivalentTo(new[] { "bg1.png", "bg2.png" }));
        }

        // A quoted path containing a literal comma must be counted as a single column by the
        // loopType-normalization pre-pass (not split on the embedded comma) — otherwise a
        // comma-free Animation line elsewhere in the same file could be mis-normalized too, or
        // the pre-pass itself could throw and take the whole file down. Core's own per-line
        // parser is not quote-aware (a pre-existing, separate limitation of the untouched
        // submodule), so the comma-path object itself still fails downstream and is skipped by
        // the per-object resilience — but everything else in the file must be unaffected.
        [Test]
        public void QuotedCommaInPathDoesNotCorruptNormalizationOfOtherLines()
        {
            string osb = writeOsb("quoted-comma.osb", """
                osu file format v14

                [Events]
                //Storyboard Layer 0 (Background)
                Sprite,Background,Centre,"before.png",10,10
                Animation,Background,Centre,"anim,with,comma.png",320,240,4,100
                Animation,Background,Centre,"anim-clean.png",320,240,4,100
                Sprite,Background,Centre,"after.png",20,20
                """);

            var objects = StoryboardLoader.Load(osb, null);

            // before-sprite, after-sprite, and the comma-free animation (LoopForever-normalized)
            // survive; only the comma-path animation is skipped.
            Assert.That(objects, Has.Count.EqualTo(3));
            Assert.That(objects.Select(o => o.ImageFilePath), Is.EquivalentTo(new[]
            {
                "before.png", "anim-clean.png", "after.png"
            }));

            var animation = objects.OfType<StoryboardAnimation>().Single();
            Assert.That(animation.LoopType, Is.EqualTo(LoopType.LoopForever));
        }
    }
}
