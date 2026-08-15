#nullable enable

using System.Linq;
using JukeBox.Game.Playback;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Graphics.Cursor;
using osu.Game.Overlays;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The playback-speed row on its own (rather than through the whole Playback tab): everything
    /// asserted here is about the row and the bindable behind it, and hosting just those two keeps
    /// the fixture from depending on the queue/jukebox graph the full tab pulls in.
    /// </summary>
    [TestFixture]
    public partial class TestScenePlaybackSpeed : JukeBoxTestScene
    {
        // The DI lazer's SettingsPanel provides its subtree, same as PlaybackPanel caches for the
        // real row — the slider resolves it for its pill/track palette.
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        private PlaybackController playback = null!;
        private PlaybackPanel.PlaybackSpeedSlider row = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("build row", () =>
            {
                Children = new Drawable[]
                {
                    playback = new PlaybackController(),
                    // Tooltip host, same wrapper PlaybackPanel puts around its rows: lazer's
                    // RoundedSliderBar surfaces its value through a tooltip, which only renders
                    // inside a TooltipContainer ancestor.
                    new OsuTooltipContainer(null!)
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = row = new PlaybackSpeedSliderRowHost(),
                    },
                };

                row.Current = playback.PlaybackRate;
            });
        }

        [Test]
        public void RangeIsPointOneToTwoAndAHalfAroundADefaultOfOne()
        {
            AddAssert("default is 1×", () => playback.PlaybackRate.Default == 1);
            AddAssert("starts at the default", () => playback.PlaybackRate.Value == 1);
            AddAssert("bottoms out at 0.1×", () => playback.PlaybackRate.MinValue == 0.1);
            AddAssert("tops out at 2.5×", () => playback.PlaybackRate.MaxValue == 2.5);

            AddStep("try to go below the floor", () => playback.PlaybackRate.Value = 0);
            AddAssert("clamped to 0.1×", () => playback.PlaybackRate.Value == 0.1);

            AddStep("try to go above the ceiling", () => playback.PlaybackRate.Value = 99);
            AddAssert("clamped to 2.5×", () => playback.PlaybackRate.Value == 2.5);

            // The row's own bindable has to carry the same range — it is what the slider's handle
            // travels over, and it picks the range up by binding rather than declaring it.
            AddAssert("the row's slider spans the same range",
                () => row.Current.Value == 2.5 && ((osu.Framework.Bindables.BindableNumber<double>)row.Current).MinValue == 0.1);
        }

        [Test]
        public void ValueLabelPrintsTheRateWithAMultiplicationSign()
        {
            AddAssert("shows 1.00× at the default", () => row.ValueText.Text.ToString() == "1.00×");

            AddStep("set 1.25×", () => playback.PlaybackRate.Value = 1.25);
            AddUntilStep("label follows", () => row.ValueText.Text.ToString() == "1.25×");

            AddStep("set 0.1×", () => playback.PlaybackRate.Value = 0.1);
            AddUntilStep("label follows to the floor", () => row.ValueText.Text.ToString() == "0.10×");
        }

        // Lazer's revert arrow: invisible while the setting is untouched, faded in once it is off
        // its default, and it puts the setting back when clicked.
        [Test]
        public void RevertArrowAppearsOnlyOffDefaultAndRestoresIt()
        {
            AddUntilStep("hidden at the default", () => row.RevertButton.Alpha == 0);

            AddStep("set 1.75×", () => playback.PlaybackRate.Value = 1.75);
            AddUntilStep("arrow shown", () => row.RevertButton.Alpha > 0);

            AddStep("click the arrow", () => row.RevertButton.TriggerClick());
            AddAssert("rate back to 1×", () => playback.PlaybackRate.Value == 1);
            AddUntilStep("arrow hidden again", () => row.RevertButton.Alpha == 0);
        }

        // The arrow is re-hosted on the right precisely because the row cancels lazer's left content
        // margin; asserting the side is what keeps a future revert to ShowsDefaultIndicator from
        // silently pushing it back off the panel.
        [Test]
        public void RevertArrowSitsOnTheRightBesideTheValue()
        {
            AddStep("set 1.75× so the arrow is visible", () => playback.PlaybackRate.Value = 1.75);
            AddUntilStep("arrow shown", () => row.RevertButton.Alpha > 0);

            AddAssert("lazer's own left-gutter indicator is off", () => !row.ShowsDefaultIndicator);
            AddAssert("arrow is in the row's right half",
                () => row.RevertButton.ScreenSpaceDrawQuad.Centre.X > row.ScreenSpaceDrawQuad.Centre.X);
            AddAssert("value label sits to the right of the arrow",
                () => row.ValueText.ScreenSpaceDrawQuad.TopLeft.X >= row.RevertButton.ScreenSpaceDrawQuad.TopRight.X);
            AddAssert("both stay inside the row",
                () => row.ValueText.ScreenSpaceDrawQuad.TopRight.X <= row.ScreenSpaceDrawQuad.TopRight.X + 1);
        }

        /// <summary>The row as PlaybackPanel builds it, so the label the panel gives it is part of
        /// what this scene covers.</summary>
        private partial class PlaybackSpeedSliderRowHost : PlaybackPanel.PlaybackSpeedSlider
        {
            public PlaybackSpeedSliderRowHost()
            {
                LabelText = "Playback speed";
                KeyboardStep = 0.05f;
                RelativeSizeAxes = Axes.X;
            }
        }
    }
}
