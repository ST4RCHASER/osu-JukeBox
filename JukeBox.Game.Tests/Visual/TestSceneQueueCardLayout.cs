#nullable enable

using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osuTK.Input;
using System.Collections.Generic;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The queue card's shape: full width at rest with no gutter reserved for the drag handle, the
    /// handle revealed on hover and still able to start a drag, and the two actions stacked in a
    /// column at the right edge.
    /// </summary>
    [TestFixture]
    public partial class TestSceneQueueCardLayout : JukeBoxManualInputTestScene
    {
        private PlaybackController playback = null!;
        private MusicQueue queue = null!;
        private Jukebox jukebox = null!;
        private QueuePanel queuePanel = null!;
        private Container uiContainer = null!;

        private string tmp = null!;

        /// <summary>The real right-column content width (see MainScreen).</summary>
        private const float column_content_width = 340 - 2 * Theme.PanelPadding;

        /// <summary>
        /// Where the pointer is parked between tests: a definite point INSIDE the window and well
        /// clear of the queue column, rather than a position off the window's edge. An off-window
        /// move can be clamped to wherever the cursor already was, and a move that doesn't actually
        /// move the cursor produces no event — so the next hover never fired and the test sat
        /// waiting for a fade that was never going to start.
        /// </summary>
        private osuTK.Vector2 restingPointerPosition
            => uiContainer.ToScreenSpace(new osuTK.Vector2(column_content_width + 120, 20));

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            var mirror = new StubMirror();

            playback = new PlaybackController();
            queue = new MusicQueue();
            var cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);
            jukebox = new Jukebox(queue, new RadioService(mirror), cache, playback);

            var deps = new DependencyContainer(parent);
            deps.CacheAs<IBeatmapMirror>(mirror);
            deps.CacheAs(playback);
            deps.CacheAs(jukebox);
            deps.CacheAs(queue);
            deps.CacheAs(cache);
            return deps;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            Add(playback);
            Add(jukebox);
            Add(uiContainer = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            // Park the pointer away from the list and tear the old panel down before building the
            // next one: a panel replaced while one of its rows is hovered (or mid-entrance) leaves
            // that row animating in a subtree that is still being disposed, and the fresh rows then
            // take a frame longer to appear than the wait below allows.
            AddStep("clear the pointer and the old panel", () =>
            {
                // A previous test may have left a button down (the drag one presses, moves and
                // releases, and a release that lands mid-rearrangement can leave the input manager
                // still treating the pointer as dragging — and a dragging pointer delivers no hover
                // to anything, which is what made the next test sit waiting for one).
                InputManager.ReleaseButton(MouseButton.Left);
                InputManager.MoveMouseTo(restingPointerPosition);
                uiContainer.Clear();
            });

            // Give the release a few frames to actually end the drag before the next panel is
            // built. Under the full suite the process is busy enough that the rearrangement is
            // still running when the button comes up, and a pointer the input manager still
            // considers to be dragging delivers no hover at all to whatever is built next.
            AddWaitStep("let any drag settle", 5);

            AddStep("queue three sets", () =>
            {
                queue.Items.Clear();

                for (int i = 1; i <= 3; i++)
                {
                    queue.Items.Add(new BeatmapSetInfo
                    {
                        Id = i,
                        Title = $"Song {i}",
                        Artist = $"Artist {i}",
                        Creator = "mapper",
                    });
                }

                uiContainer.Child = new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = column_content_width,
                    Child = queuePanel = new QueuePanel(docked: true),
                };
            });

            AddUntilStep("rows built", () => rows().Length == 3);
        }

        /// <summary>
        /// The user's complaint: the drag handle sat in an auto-sized column beside the card, so the
        /// card never reached the panel's left edge even with nothing hovered. The card must span
        /// the full width of the list it is in.
        /// </summary>
        [Test]
        public void AtRestTheCardSpansTheFullWidth()
        {
            AddAssert("nothing is hovered", () => !rows().Any(r => r.IsHovered));
            AddAssert("so no drag handle is showing", () => rows().All(r => handleOf(r).Alpha == 0));

            AddUntilStep("each card is as wide as the list", () =>
            {
                float listWidth = queuePanel.ChildrenOfType<QueuePanel.QueueList>().Single().DrawWidth;

                return rows().All(r => cardOf(r).DrawWidth >= listWidth - 0.5f);
            });

            AddUntilStep("and starts at the list's own left edge", () =>
            {
                float listLeft = queuePanel.ChildrenOfType<QueuePanel.QueueList>().Single().ScreenSpaceDrawQuad.TopLeft.X;

                return rows().All(r => cardOf(r).ScreenSpaceDrawQuad.TopLeft.X <= listLeft + 0.5f);
            });
        }

        /// <summary>
        /// The reported bug, verbatim: "this order icon are got caped". The handle used to be a
        /// child of the card and so lived inside the card's Masking, which cropped it against the
        /// card's left edge — only the right part of the glyph drew. The fix makes the card inset on
        /// hover so the handle occupies real space beside it rather than territory on top of it.
        ///
        /// <para>
        /// Asserted as CONTAINMENT rather than visibility, because a clipped drawable still reports
        /// a full quad and a non-zero alpha: what proves it is not cropped is that its bounds sit
        /// entirely within every ancestor that could crop them.
        /// </para>
        /// </summary>
        [Test]
        public void OnHoverTheCardMakesRoomAndTheHandleIsNotClipped()
        {
            QueuePanel.QueueRow row = null!;
            AddStep("take the first row", () => row = rows()[0]);

            AddStep("hover the first card", () => InputManager.MoveMouseTo(cardOf(row)));
            AddUntilStep("the row is hovered", () => row.IsHovered);
            AddUntilStep("the handle faded in", () => handleOf(row).Alpha == 1);
            AddUntilStep("and the card has finished insetting", () => insetOf(row) >= 23.5f);

            // The card pulled its left edge in; the handle is in the space that freed up. If the
            // handle still overlapped the card, it would be sitting on top of content again — the
            // arrangement this change exists to replace.
            AddAssert("the handle does not overlap the card", () =>
                handleOf(row).ScreenSpaceDrawQuad.AABBFloat.Right <= cardOf(row).ScreenSpaceDrawQuad.AABBFloat.Left + 0.5f);

            // The clipping assertion proper: nothing that masks can crop the handle's bounds.
            AddAssert("the handle is wholly inside the row", () => contains(row.ScreenSpaceDrawQuad, handleOf(row).ScreenSpaceDrawQuad));
            AddAssert("and wholly inside the list that masks it",
                () => contains(queuePanel.ChildrenOfType<QueuePanel.QueueList>().Single().ScreenSpaceDrawQuad, handleOf(row).ScreenSpaceDrawQuad));

            AddAssert("the handle is a comfortable target", () => handleOf(row).DrawWidth >= 20 && handleOf(row).DrawHeight >= 20);

            parkPointer();
        }

        /// <summary>Hover-out gives the width back — the full-width rest state was its own request.</summary>
        [Test]
        public void HoverOutRestoresTheFullWidthCard()
        {
            QueuePanel.QueueRow row = null!;
            AddStep("take the first row", () => row = rows()[0]);

            AddStep("hover it", () => InputManager.MoveMouseTo(cardOf(row)));
            AddUntilStep("the card insets", () => insetOf(row) >= 23.5f);

            AddStep("move away", () => InputManager.MoveMouseTo(restingPointerPosition));
            AddUntilStep("the row is no longer hovered", () => !row.IsHovered);
            AddUntilStep("the card is back to full width", () => insetOf(row) <= 0.5f);

            AddUntilStep("and starts at the list's own left edge again", () =>
            {
                float listLeft = queuePanel.ChildrenOfType<QueuePanel.QueueList>().Single().ScreenSpaceDrawQuad.TopLeft.X;

                return cardOf(row).ScreenSpaceDrawQuad.TopLeft.X <= listLeft + 0.5f;
            });
        }

        /// <summary>
        /// The oscillation this layout could easily have caused: if hover were handled on the CARD,
        /// insetting it would pull the card out from under a pointer near the left edge, un-hover,
        /// snap back, re-hover, forever. Hover lives on the row, whose bounds never move — so moving
        /// onto the handle (which is outside the card) has to keep the row hovered and the inset put.
        /// </summary>
        [Test]
        public void MovingOntoTheHandleDoesNotUnHoverOrOscillate()
        {
            QueuePanel.QueueRow row = null!;
            AddStep("take the first row", () => row = rows()[0]);

            AddStep("hover the card", () => InputManager.MoveMouseTo(cardOf(row)));
            AddUntilStep("inset settled", () => insetOf(row) >= 23.5f);

            AddStep("move onto the handle itself", () => InputManager.MoveMouseTo(handleOf(row)));
            AddWaitStep("let any oscillation show itself", 10);

            AddAssert("still hovered", () => row.IsHovered);
            AddAssert("and the inset never gave way", () => insetOf(row) >= 23.5f);

            // Also pin the left edge of the row itself: if the ROW had been what shrank, the pointer
            // would be outside it by now.
            AddAssert("the handle is still under the pointer",
                () => handleOf(row).ReceivePositionalInputAt(InputManager.CurrentState.Mouse.Position));

            parkPointer();
        }

        /// <summary>
        /// Leaves the pointer clear of the list. SetUpSteps parks it too, but in the same step that
        /// tears the panel down — so a test that ends while a row is still hovered hits exactly the
        /// hazard documented there (a row animating inside a subtree being disposed), and the next
        /// fixture's rows then take longer to appear than its wait allows.
        /// </summary>
        private void parkPointer()
        {
            AddStep("park the pointer clear of the list", () => InputManager.MoveMouseTo(restingPointerPosition));
            AddUntilStep("nothing is hovered", () => !rows().Any(r => r.IsHovered));
        }

        /// <summary>How far the card currently sits from the row's left edge.</summary>
        private static float insetOf(QueuePanel.QueueRow row)
            => cardOf(row).ScreenSpaceDrawQuad.AABBFloat.Left - row.ScreenSpaceDrawQuad.AABBFloat.Left;

        private static bool contains(osu.Framework.Graphics.Primitives.Quad outer, osu.Framework.Graphics.Primitives.Quad inner)
        {
            var o = outer.AABBFloat;
            var i = inner.AABBFloat;

            return i.Left >= o.Left - 0.5f && i.Right <= o.Right + 0.5f
                   && i.Top >= o.Top - 0.5f && i.Bottom <= o.Bottom + 0.5f;
        }

        /// <summary>Play above remove, in a column — the destructive one keeps the bottom.</summary>
        [Test]
        public void TheTwoActionsAreStackedVertically()
        {
            QueuePanel.QueueRow row = null!;
            AddStep("take the first row", () => row = rows()[0]);

            AddStep("hover the first card", () => InputManager.MoveMouseTo(cardOf(row)));
            AddUntilStep("the row is hovered", () => row.IsHovered);
            AddUntilStep("buttons visible", () => buttonsOf(row).All(b => b.Alpha == 1));

            AddAssert("there are exactly two", () => buttonsOf(row).Length == 2);

            AddUntilStep("play sits ABOVE remove, not beside it", () =>
            {
                var buttons = buttonsOf(row);
                var play = buttons[0].ScreenSpaceDrawQuad;
                var remove = buttons[1].ScreenSpaceDrawQuad;

                return play.Centre.Y < remove.Centre.Y
                       // Same column: their horizontal centres line up.
                       && System.Math.Abs(play.Centre.X - remove.Centre.X) < 1f;
            });

            AddAssert("both stay comfortably clickable", () => buttonsOf(row).All(b => b.DrawWidth >= 20 && b.DrawHeight >= 20));

            // Pinned exactly, not just "comfortable": the 24px sizing was its own request, and a
            // drift upwards is what made them read as oversized in the reported screenshot.
            AddAssert("both are exactly 24px", () => buttonsOf(row).All(b => b.DrawWidth == 24 && b.DrawHeight == 24));

            AddAssert("stacked tightly, not spread apart", () =>
            {
                var buttons = buttonsOf(row);
                float gap = buttons[1].ScreenSpaceDrawQuad.AABBFloat.Top - buttons[0].ScreenSpaceDrawQuad.AABBFloat.Bottom;

                // Screen space, so compare against the row's own scale rather than raw pixels.
                float scale = buttons[0].ScreenSpaceDrawQuad.AABBFloat.Height / 24f;

                return gap <= 4 * scale + 0.5f;
            });

            // Right-aligned, expressed against the card's own width rather than a pixel budget:
            // both buttons live in the last tenth of the card, which is what "in a column at the
            // right edge" means and does not need the exact inset to be pinned here.
            AddUntilStep("and they sit at the card's right edge", () =>
            {
                var card = cardOf(row).ScreenSpaceDrawQuad;

                return buttonsOf(row).All(b => b.ScreenSpaceDrawQuad.TopRight.X <= card.TopRight.X + 0.5f
                                               && b.ScreenSpaceDrawQuad.TopLeft.X >= card.TopRight.X - card.Width * 0.1f - 40);
            });
        }

        /// <summary>Both actions still do their jobs from their new positions.</summary>
        [Test]
        public void BothStackedActionsStillFire()
        {
            QueuePanel.QueueRow second = null!;
            AddStep("take the second row", () => second = rows()[1]);

            AddStep("hover the second card", () => InputManager.MoveMouseTo(cardOf(second)));
            AddUntilStep("the row is hovered", () => second.IsHovered);
            AddUntilStep("buttons visible", () => buttonsOf(second).All(b => b.Alpha == 1));

            AddStep("click remove", () =>
            {
                InputManager.MoveMouseTo(buttonsOf(second)[1]);
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("that set left the queue", () => queue.Items.All(i => i.Id != 2));
            AddUntilStep("and its row went with it", () => rows().Length == 2);

            QueuePanel.QueueRow nowSecond = null!;
            AddStep("take what is now the second row", () => nowSecond = rows()[1]);

            AddStep("hover what is now the second card", () => InputManager.MoveMouseTo(cardOf(nowSecond)));
            AddUntilStep("the row is hovered", () => nowSecond.IsHovered);
            AddUntilStep("buttons visible", () => buttonsOf(nowSecond).All(b => b.Alpha == 1));

            AddStep("click play", () =>
            {
                InputManager.MoveMouseTo(buttonsOf(nowSecond)[0]);
                InputManager.Click(MouseButton.Left);
            });

            // Playing pulls the set out of the queue, which is the observable half of "it fired"
            // without needing a real download to complete.
            AddUntilStep("play was acted on", () => queue.Items.All(i => i.Id != 3));
        }

        /// <summary>The handle still drives the list's rearrangement, which is the contract the
        /// hover-reveal must not have broken.</summary>
        [Test]
        public void DraggingTheRevealedHandleReordersTheQueue()
        {
            AddAssert("starts in queue order", () => queue.Items.Select(i => i.Id).SequenceEqual(new[] { 1, 2, 3 }));

            QueuePanel.QueueRow first = null!;
            AddStep("take the first row", () => first = rows()[0]);

            AddStep("hover the first card", () => InputManager.MoveMouseTo(cardOf(first)));
            AddUntilStep("the row is hovered", () => first.IsHovered);
            AddUntilStep("the handle faded in", () => handleOf(first).Alpha == 1);

            // The list asks IsDraggableAt before starting a drag: it must say yes over the handle
            // and no over the rest of the card, or the whole row becomes a drag target.
            AddAssert("the list will start a drag from the handle",
                () => first.CanBeDraggedAt(handleOf(first).ScreenSpaceDrawQuad.Centre));
            AddAssert("but not from the middle of the card",
                () => !first.CanBeDraggedAt(cardOf(first).ScreenSpaceDrawQuad.Centre));

            AddStep("press on the handle", () =>
            {
                InputManager.MoveMouseTo(handleOf(first));
                InputManager.PressButton(MouseButton.Left);
            });

            AddStep("drag it past the third row", () => InputManager.MoveMouseTo(rows()[2].ScreenSpaceDrawQuad.Centre));
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));

            AddUntilStep("the first set moved down the queue", () => queue.Items.First().Id != 1);
            AddAssert("and every set is still there", () => queue.Items.Select(i => i.Id).OrderBy(i => i).SequenceEqual(new[] { 1, 2, 3 }));
        }

        private QueuePanel.QueueRow[] rows() => queuePanel.ChildrenOfType<QueuePanel.QueueRow>().ToArray();

        /// <summary>The card itself — the row's content, which is everything the handle used to sit
        /// beside.</summary>
        private static Drawable cardOf(QueuePanel.QueueRow row) => row.Card;

        private static Drawable handleOf(QueuePanel.QueueRow row) => row.DragHandle;

        private static IconButton[] buttonsOf(QueuePanel.QueueRow row) => row.ChildrenOfType<IconButton>().ToArray();

        /// <summary>Nothing in this fixture downloads — the rows only need a mirror to exist.</summary>
        private class StubMirror : IBeatmapMirror
        {
            public string Name => "stub";

            public System.Threading.Tasks.Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, System.Threading.CancellationToken ct = default)
                => System.Threading.Tasks.Task.FromResult(new List<BeatmapSetInfo>());

            public System.Threading.Tasks.Task DownloadAsync(int setId, bool noVideo, Stream destination, System.Threading.CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => System.Threading.Tasks.Task.FromException(new System.NotSupportedException());
        }
    }
}
