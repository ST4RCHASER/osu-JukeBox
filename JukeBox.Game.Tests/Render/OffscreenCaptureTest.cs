#nullable enable

using JukeBox.Game.UI.Render;
using NUnit.Framework;
using osu.Framework.Graphics.Primitives;
using osuTK;

namespace JukeBox.Game.Tests.Render
{
    /// <summary>
    /// The capture's shrink-to-fit rule: a render buffer LARGER than the window must lay its scene
    /// out scaled down inside the root drawable's bounds, because nested frame-buffer draws (a
    /// slider body's buffered path) clip themselves to the root and simply lose anything laid out
    /// past the window — the broken giant slider-body blobs of the first 4K renders. A buffer that
    /// fits stays exactly 1 unit : 1 pixel.
    /// </summary>
    [TestFixture]
    public class OffscreenCaptureTest
    {
        [Test]
        public void ABufferThatFitsTheRootStaysOneToOne()
        {
            Assert.That(OffscreenCapture.FitScale(new RectangleF(0, 0, 2400, 1500), Vector2.Zero, new Vector2(1920, 1080)), Is.EqualTo(1));

            // Exactly fitting is still 1:1.
            Assert.That(OffscreenCapture.FitScale(new RectangleF(0, 0, 1920, 1080), Vector2.Zero, new Vector2(1920, 1080)), Is.EqualTo(1));
        }

        [Test]
        public void ABufferBiggerThanTheRootShrinksByTheLimitingAxis()
        {
            // 4K buffer in a 1200x1000 root: width is the limiting axis.
            Assert.That(OffscreenCapture.FitScale(new RectangleF(0, 0, 1200, 1000), Vector2.Zero, new Vector2(3840, 2160)), Is.EqualTo(1200f / 3840).Within(1e-5));

            // A short root limits by height instead.
            Assert.That(OffscreenCapture.FitScale(new RectangleF(0, 0, 3000, 540), Vector2.Zero, new Vector2(1920, 1080)), Is.EqualTo(540f / 1080).Within(1e-5));
        }

        [Test]
        public void TheCapturesOwnScreenPositionEatsIntoTheAvailableSpace()
        {
            // Sitting 200px in from the left leaves only 1000px of a 1200px root.
            Assert.That(OffscreenCapture.FitScale(new RectangleF(0, 0, 1200, 2000), new Vector2(200, 0), new Vector2(1920, 1080)), Is.EqualTo(1000f / 1920).Within(1e-5));
        }

        [Test]
        public void DegenerateGeometryNeverProducesANonPositiveScale()
        {
            // Positioned past the root's edge: nothing sensible to fit to — stay 1:1 rather than 0.
            Assert.That(OffscreenCapture.FitScale(new RectangleF(0, 0, 1200, 1000), new Vector2(1300, 0), new Vector2(1920, 1080)), Is.EqualTo(1));
            Assert.That(OffscreenCapture.FitScale(new RectangleF(0, 0, 1200, 1000), Vector2.Zero, Vector2.Zero), Is.EqualTo(1));
        }
    }
}
