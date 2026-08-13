using System.Drawing;
using System.Linq;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Platform;

namespace JukeBox.Game.Tests.UI
{
    // Regression coverage for the display-picker "snap back" bug: osu.Framework's
    // IWindow.DisplaysChanged fires even when only the current display's Bounds changed (e.g. a
    // transient resolution/DPI blip while dragging the window across monitors), not just when the
    // actual set of displays changed. Display.Equals compares Bounds, so an equality check that
    // doesn't ignore it would treat that blip as "a real change" too — SettingsOverlay uses this
    // comparer (mirroring lazer's own LayoutSettings.DisplayListComparer) precisely so it doesn't.
    //
    // The overlay-level path (onDisplaysChanged deciding whether to reassign displayDropdown.Items)
    // can't be exercised through TestSceneSettingsOverlay: this test project's GameHost
    // (TestRunHeadlessGameHost) overrides CreateWindow to return null, so host.Window — and
    // therefore displayDropdown itself — never exists there. This comparer is the entire decision
    // the fix hinges on, and it needs no window at all to test directly.
    [TestFixture]
    public class DisplayListComparerTest
    {
        private static Display makeDisplay(int index = 0, string name = "Display", Rectangle? bounds = null, params DisplayMode[] modes)
            => new Display(index, name, bounds ?? new Rectangle(0, 0, 1920, 1080), bounds ?? new Rectangle(0, 0, 1920, 1080), modes);

        [Test]
        public void SameIndexNameAndModesButDifferentBoundsAreEqual()
        {
            var a = makeDisplay(bounds: new Rectangle(0, 0, 1920, 1080));
            var b = makeDisplay(bounds: new Rectangle(0, 0, 2560, 1440));

            Assert.That(SettingsOverlay.DisplayListComparer.Default.Equals(a, b), Is.True);
            Assert.That(SettingsOverlay.DisplayListComparer.Default.GetHashCode(a),
                Is.EqualTo(SettingsOverlay.DisplayListComparer.Default.GetHashCode(b)));
        }

        [Test]
        public void DifferentIndexIsNotEqual()
        {
            var a = makeDisplay(index: 0);
            var b = makeDisplay(index: 1);

            Assert.That(SettingsOverlay.DisplayListComparer.Default.Equals(a, b), Is.False);
        }

        [Test]
        public void DifferentNameIsNotEqual()
        {
            var a = makeDisplay(name: "Monitor A");
            var b = makeDisplay(name: "Monitor B");

            Assert.That(SettingsOverlay.DisplayListComparer.Default.Equals(a, b), Is.False);
        }

        [Test]
        public void DifferentDisplayModesAreNotEqual()
        {
            var a = makeDisplay(modes: new DisplayMode("32", new Size(1920, 1080), 32, 60, 0));
            var b = makeDisplay(modes: new DisplayMode("32", new Size(2560, 1440), 32, 144, 0));

            Assert.That(SettingsOverlay.DisplayListComparer.Default.Equals(a, b), Is.False);
        }

        [Test]
        public void NullHandling()
        {
            var a = makeDisplay();

            Assert.That(SettingsOverlay.DisplayListComparer.Default.Equals(null, null), Is.True);
            Assert.That(SettingsOverlay.DisplayListComparer.Default.Equals(a, null), Is.False);
            Assert.That(SettingsOverlay.DisplayListComparer.Default.Equals(null, a), Is.False);
        }

        // The exact scenario onDisplaysChanged relies on: a single-display list re-fetched with
        // only Bounds having wobbled compares equal via SequenceEqual, so the fix's
        // "!SequenceEqual(..., DisplayListComparer.Default)" guard correctly evaluates to false
        // (skip reassigning Items) instead of true (reassign, and lose the selection).
        [Test]
        public void SingleDisplayListWithOnlyBoundsChangedSequenceEqualsUnderComparer()
        {
            var before = new[] { makeDisplay(bounds: new Rectangle(0, 0, 1920, 1080)) };
            var after = new[] { makeDisplay(bounds: new Rectangle(0, 0, 1920, 1080).With(width: 1921)) };

            Assert.That(before.SequenceEqual(after, SettingsOverlay.DisplayListComparer.Default), Is.True);
        }

        [Test]
        public void GenuinelyAddedDisplayIsNotSequenceEqual()
        {
            var before = new[] { makeDisplay(index: 0, name: "Primary") };
            var after = new[] { makeDisplay(index: 0, name: "Primary"), makeDisplay(index: 1, name: "Secondary") };

            Assert.That(before.SequenceEqual(after, SettingsOverlay.DisplayListComparer.Default), Is.False);
        }
    }

    internal static class RectangleExtensions
    {
        public static Rectangle With(this Rectangle rectangle, int? width = null, int? height = null)
            => new Rectangle(rectangle.X, rectangle.Y, width ?? rectangle.Width, height ?? rectangle.Height);
    }
}
