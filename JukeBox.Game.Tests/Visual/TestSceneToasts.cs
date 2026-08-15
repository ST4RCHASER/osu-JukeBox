#nullable enable

using System;
using System.Linq;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Transforms;
using osu.Framework.Testing;
using osu.Framework.Utils;
using osuTK;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Covers the bottom-right toast stack in isolation from <see cref="Screens.MainScreen"/>: a
    /// fixed-size host so every geometric assertion here is exact, with no dependence on the window
    /// size or the three-column layout (where the toasts land relative to the player area and the
    /// side columns is asserted in <c>TestSceneMainScreen</c> instead).
    /// </summary>
    [TestFixture]
    public partial class TestSceneToasts : JukeBoxTestScene
    {
        private const float host_width = 640;
        private const float host_height = 420;

        private Container host = null!;
        private ToastOverlay overlay = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("fresh toast host", () =>
            {
                Child = host = new Container
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new Vector2(host_width, host_height),
                    Children = new Drawable[]
                    {
                        // Stands in for the player box underneath — deliberately WHITE, the worst
                        // case the old bare-text notification was unreadable against.
                        new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.White },
                        overlay = new ToastOverlay(),
                    },
                };
            });

            AddUntilStep("overlay loaded", () => overlay.IsLoaded);
        }

        // The reported bug: two notifications arriving inside the same dwell window were drawn on
        // top of each other, because the old implementation parked every one of them at the same
        // fixed spot. They must now occupy separate, non-overlapping slots.
        [Test]
        public void TwoSimultaneousToastsDoNotOverlap()
        {
            AddStep("push two at once", () =>
            {
                overlay.Push("First message");
                overlay.Push("Second message");
            });

            AddUntilStep("both on screen", () => settled(2));

            AddAssert("their boxes are disjoint", () =>
            {
                var (first, second) = pair();
                return !box(first).IntersectsWith(box(second));
            });

            AddAssert("they are stacked vertically, not side by side", () =>
            {
                var (first, second) = pair();
                // Same column (identical left/right edges), separated on Y.
                return Math.Abs(box(first).Left - box(second).Left) < 0.5f
                       && Math.Abs(box(first).Right - box(second).Right) < 0.5f
                       && box(first).Bottom <= box(second).Top;
            });
        }

        // Stacking convention: lazer's NotificationOverlay puts the newest notification nearest the
        // edge its panel is anchored to. This stack is anchored bottom-right, so the newest belongs
        // at the BOTTOM and the older ones ride upward.
        [Test]
        public void TheNewestToastSitsAtTheBottomOfTheStack()
        {
            AddStep("push two at once", () =>
            {
                overlay.Push("Older");
                overlay.Push("Newer");
            });

            AddUntilStep("both on screen", () => settled(2));

            AddAssert("newer is below older", () => box(toast("Newer")).Top > box(toast("Older")).Top);
        }

        // The old notification was bare text with nothing behind it — illegible the moment a bright
        // storyboard frame or video sat underneath. Every toast must now carry a real surface.
        [Test]
        public void EachToastHasABackgroundSurface()
        {
            AddStep("push one", () => overlay.Push("Needs a background"));
            AddUntilStep("on screen", () => settled(1));

            AddAssert("filled with an opaque background box", () =>
            {
                var background = overlay.LiveToasts.Single().ChildrenOfType<Box>().First();

                return background.RelativeSizeAxes == Axes.Both
                       && background.Alpha > 0.9f
                       && background.Colour.TopLeft.Linear.A > 0.9f;
            });

            AddAssert("that background actually covers the whole toast", () =>
            {
                var toast = overlay.LiveToasts.Single();
                var background = toast.ChildrenOfType<Box>().First();

                return background.ScreenSpaceDrawQuad.AABBFloat.Contains(box(toast));
            });

            AddAssert("rounded, masked, and lifted off the visuals by a shadow", () =>
            {
                var toast = overlay.LiveToasts.Single();

                return toast.Masking
                       && toast.CornerRadius > 0
                       && toast.BorderThickness > 0
                       && toast.EdgeEffect.Type == EdgeEffectType.Shadow
                       && toast.EdgeEffect.Radius > 0;
            });
        }

        [Test]
        public void ToastsAreAnchoredToTheBottomRightOfTheirHost()
        {
            AddStep("push one", () => overlay.Push("Bottom right"));
            AddUntilStep("on screen", () => settled(1));

            AddAssert("inset from the host's bottom-right corner by the standard padding", () =>
            {
                var hostBox = host.ScreenSpaceDrawQuad.AABBFloat;
                var toastBox = box(overlay.LiveToasts.Single());

                return Math.Abs(toastBox.Right - (hostBox.Right - Theme.PanelPadding)) < 0.5f
                       && Math.Abs(toastBox.Bottom - (hostBox.Bottom - Theme.PanelPadding)) < 0.5f;
            });

            AddAssert("nowhere near the top of the host, where it used to sit", () =>
                box(overlay.LiveToasts.Single()).Top > host.ScreenSpaceDrawQuad.AABBFloat.Centre.Y);
        }

        // A toast leaving must not drop the stack on the floor: the survivors glide into the freed
        // slot and end up correctly re-anchored, rather than either jumping or leaving a hole.
        [Test]
        public void DismissingAToastReflowsTheRestIntoTheFreedSlot()
        {
            AddStep("push two at once", () =>
            {
                overlay.Push("Survivor");
                overlay.Push("Doomed");
            });

            AddUntilStep("both on screen", () => settled(2));

            float vacatedBottom = 0;
            float survivorBottomBefore = 0;

            AddStep("dismiss the newest (bottom) one", () =>
            {
                vacatedBottom = box(toast("Doomed")).Bottom;
                survivorBottomBefore = box(toast("Survivor")).Bottom;
                toast("Doomed").Dismiss();
            });

            AddAssert("the survivor really does have somewhere to move to", () => survivorBottomBefore < vacatedBottom - 1);

            AddUntilStep("the dismissed one is gone", () => overlay.AllToasts.Count == 1);

            AddUntilStep("the survivor slid down into the vacated slot", () =>
                Math.Abs(box(toast("Survivor")).Bottom - vacatedBottom) < 1f);

            AddAssert("and is re-anchored to the host's bottom-right corner", () =>
            {
                var hostBox = host.ScreenSpaceDrawQuad.AABBFloat;
                return Math.Abs(box(toast("Survivor")).Bottom - (hostBox.Bottom - Theme.PanelPadding)) < 1f;
            });
        }

        // The normal case is a toast timing out at the TOP of the stack (the oldest goes first), and
        // the survivors below it must not move by so much as a pixel — not even transiently. The
        // bottom-anchored flow shrinks from its top edge by exactly what each survivor's in-flow
        // position changes by, so the two cancel out; they only cancel out FRAME BY FRAME if the
        // flow's auto-size and layout animate over the same duration and easing. Mismatch them and
        // the whole stack visibly lurches down and creeps back up while one toast leaves.
        [Test]
        public void DismissingTheOldestLeavesTheRestOfTheStackExactlyWhereItWas()
        {
            AddStep("push two at once", () =>
            {
                overlay.Push("Older");
                overlay.Push("Newer");
            });

            AddUntilStep("both on screen", () => settled(2));

            float baseline = 0;
            float maxDrift = 0;

            AddStep("dismiss the oldest (top) one", () =>
            {
                baseline = box(toast("Newer")).Bottom;
                toast("Older").Dismiss();
            });

            // Sampled every frame through the exit AND for several frames past the removal, not just
            // at the end: the reflow only starts once the slot is freed, so an end-state check would
            // sail straight past a lurch that settles back.
            int framesAfterRemoval = 0;

            AddUntilStep("watch the survivor across the whole exit and reflow", () =>
            {
                maxDrift = Math.Max(maxDrift, Math.Abs(box(toast("Newer")).Bottom - baseline));
                return overlay.AllToasts.Count == 1 && ++framesAfterRemoval > 3;
            });

            AddAssert("the survivor never budged", () => maxDrift < 1f);
        }

        // A folder of .osz files dropped at once would otherwise paper the player over with toasts.
        // The cap keeps the NEWEST MaxVisible: the older ones have had their moment, and the newest
        // message is the one the user is waiting on.
        [Test]
        public void PushingPastTheCapEvictsTheOldestToasts()
        {
            AddStep("push twice the cap in one go", () =>
            {
                for (int i = 1; i <= ToastOverlay.MaxVisible * 2; i++)
                    overlay.Push($"Message {i}");
            });

            AddUntilStep("stack settles at the cap", () => overlay.AllToasts.Count == ToastOverlay.MaxVisible);

            AddAssert("none of them are still leaving", () => overlay.LiveToasts.Count() == ToastOverlay.MaxVisible);

            AddAssert("the survivors are the newest ones, still in stack order", () =>
                overlay.AllToasts.Select(t => t.Message).SequenceEqual(
                    Enumerable.Range(ToastOverlay.MaxVisible + 1, ToastOverlay.MaxVisible).Select(i => $"Message {i}")));

            AddAssert("and they still do not overlap each other", () =>
            {
                var boxes = overlay.AllToasts.Select(box).ToList();

                return boxes.Zip(boxes.Skip(1)).All(p => p.First.Bottom <= p.Second.Top);
            });
        }

        // Guards the app-wide lesson that Theme.EaseExit (InQuint) barely moves at the START of a
        // transform: a 150ms fade using it still reads as ~fully opaque well into its run, so the
        // toast looks like it is refusing to leave. Anything LEAVING needs an ease-OUT shape.
        [Test]
        public void TheExitFadeDropsImmediatelyInsteadOfHangingAtFullOpacity()
        {
            AddStep("push one", () => overlay.Push("Going away"));
            AddUntilStep("on screen", () => settled(1));

            ToastOverlay.Toast dismissed = null!;

            AddStep("dismiss it", () =>
            {
                dismissed = overlay.LiveToasts.Single();
                dismissed.Dismiss();
            });

            // The fade has to actually happen; the SHAPE it is given is checked below, off the one
            // constant animateOut applies (a Transform doesn't expose its easing to read back).
            // Asserted as "started fading", not "reached zero": the toast expires out of the flow
            // partway through, and an unparented drawable stops being updated at all.
            AddUntilStep("dismissing really does fade the surface out", () => dismissed.Alpha < 1);
            AddUntilStep("and the toast leaves the stack", () => overlay.AllToasts.Count == 0);

            AddAssert("a quarter of the way out, most of the fade has already happened",
                () => Interpolation.ApplyEasing(ToastOverlay.Toast.ExitEasing, 0.25) > 0.5);

            AddAssert("it is not the ease-IN shape used elsewhere for exits",
                () => Interpolation.ApplyEasing(ToastOverlay.Toast.ExitEasing, 0.25)
                      > Interpolation.ApplyEasing(Theme.EaseExit, 0.25));
        }

        /// <summary>All expected toasts present, loaded, and parked at their final in-flow positions
        /// (the flow animates layout, so a freshly pushed toast is briefly mid-move).</summary>
        private bool settled(int count)
            => overlay.AllToasts.Count == count
               && overlay.AllToasts.All(t => t.IsLoaded && !t.Transforms.Any() && t.Alpha >= 1);

        private (ToastOverlay.Toast first, ToastOverlay.Toast second) pair()
            => (overlay.AllToasts[0], overlay.AllToasts[1]);

        private ToastOverlay.Toast toast(string message) => overlay.AllToasts.Single(t => t.Message == message);

        private static osu.Framework.Graphics.Primitives.RectangleF box(ToastOverlay.Toast toast)
            => toast.ScreenSpaceDrawQuad.AABBFloat;
    }
}
