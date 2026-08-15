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
                InputManager.MoveMouseTo(uiContainer.ScreenSpaceDrawQuad.TopRight + new osuTK.Vector2(50, 0));
                uiContainer.Clear();
            });

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

            AddAssert("each card is as wide as the list", () =>
            {
                float listWidth = queuePanel.ChildrenOfType<QueuePanel.QueueList>().Single().DrawWidth;

                return rows().All(r => cardOf(r).DrawWidth >= listWidth - 0.5f);
            });

            AddAssert("and starts at the list's own left edge", () =>
            {
                float listLeft = queuePanel.ChildrenOfType<QueuePanel.QueueList>().Single().ScreenSpaceDrawQuad.TopLeft.X;

                return rows().All(r => cardOf(r).ScreenSpaceDrawQuad.TopLeft.X <= listLeft + 0.5f);
            });
        }

        /// <summary>Hovering reveals the handle — and it must be a real hit target, because that is
        /// what the list asks about when deciding whether a drag may begin.</summary>
        [Test]
        public void HoveringRevealsAHandleThatCanStartADrag()
        {
            AddAssert("the handle is hidden at rest", () => handleOf(rows()[0]).Alpha == 0);

            AddStep("hover the first card", () => InputManager.MoveMouseTo(cardOf(rows()[0])));

            AddUntilStep("the handle faded in", () => handleOf(rows()[0]).Alpha == 1);

            AddAssert("and the list will start a drag from it",
                () => rows()[0].CanBeDraggedAt(handleOf(rows()[0]).ScreenSpaceDrawQuad.Centre));

            AddAssert("but not from the middle of the card",
                () => !rows()[0].CanBeDraggedAt(cardOf(rows()[0]).ScreenSpaceDrawQuad.Centre));

            AddStep("stop hovering", () => InputManager.MoveMouseTo(uiContainer.ScreenSpaceDrawQuad.TopRight + new osuTK.Vector2(50, 0)));
        }

        /// <summary>Play above remove, in a column — the destructive one keeps the bottom.</summary>
        [Test]
        public void TheTwoActionsAreStackedVertically()
        {
            AddStep("hover the first card", () => InputManager.MoveMouseTo(cardOf(rows()[0])));
            AddUntilStep("buttons visible", () => buttonsOf(rows()[0]).All(b => b.Alpha == 1));

            AddAssert("there are exactly two", () => buttonsOf(rows()[0]).Length == 2);

            AddAssert("play sits ABOVE remove, not beside it", () =>
            {
                var buttons = buttonsOf(rows()[0]);
                var play = buttons[0].ScreenSpaceDrawQuad;
                var remove = buttons[1].ScreenSpaceDrawQuad;

                return play.Centre.Y < remove.Centre.Y
                       // Same column: their horizontal centres line up.
                       && System.Math.Abs(play.Centre.X - remove.Centre.X) < 1f;
            });

            AddAssert("both stay comfortably clickable", () => buttonsOf(rows()[0]).All(b => b.DrawWidth >= 20 && b.DrawHeight >= 20));

            AddAssert("and they sit at the card's right edge", () =>
            {
                float cardRight = cardOf(rows()[0]).ScreenSpaceDrawQuad.TopRight.X;

                return buttonsOf(rows()[0]).All(b => b.ScreenSpaceDrawQuad.TopRight.X <= cardRight + 0.5f
                                                     && b.ScreenSpaceDrawQuad.TopRight.X > cardRight - 40);
            });
        }

        /// <summary>Both actions still do their jobs from their new positions.</summary>
        [Test]
        public void BothStackedActionsStillFire()
        {
            AddStep("hover the second card", () => InputManager.MoveMouseTo(cardOf(rows()[1])));
            AddUntilStep("buttons visible", () => buttonsOf(rows()[1]).All(b => b.Alpha == 1));

            AddStep("click remove", () =>
            {
                InputManager.MoveMouseTo(buttonsOf(rows()[1])[1]);
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("that set left the queue", () => queue.Items.All(i => i.Id != 2));
            AddUntilStep("and its row went with it", () => rows().Length == 2);

            AddStep("hover what is now the second card", () => InputManager.MoveMouseTo(cardOf(rows()[1])));
            AddUntilStep("buttons visible", () => buttonsOf(rows()[1]).All(b => b.Alpha == 1));

            AddStep("click play", () =>
            {
                InputManager.MoveMouseTo(buttonsOf(rows()[1])[0]);
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

            AddStep("hover the first card", () => InputManager.MoveMouseTo(cardOf(rows()[0])));
            AddUntilStep("the handle faded in", () => handleOf(rows()[0]).Alpha == 1);

            AddStep("press on the handle", () =>
            {
                InputManager.MoveMouseTo(handleOf(rows()[0]));
                InputManager.PressButton(MouseButton.Left);
            });

            AddStep("drag it past the third row", () => InputManager.MoveMouseTo(cardOf(rows()[2]).ScreenSpaceDrawQuad.Centre));
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
