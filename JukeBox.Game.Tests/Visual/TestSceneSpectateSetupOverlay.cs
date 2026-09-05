#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The "Setup players…" modal that replaced the sidebar's inline username field — adding a name,
    /// removing one, and the <see cref="SpectateWatchList.MAX_WATCHED"/> cap being enforced (the
    /// controller refuses the overflow and the box keeps what was typed). Driven against a fake osu!
    /// so the whole path from the field to the controller is the production one.
    /// </summary>
    [TestFixture]
    public partial class TestSceneSpectateSetupOverlay : JukeBoxTestScene
    {
        private readonly FakeApi api = new FakeApi();

        private SpectateController spectate = null!;
        private SpectateSetupOverlay overlay = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("build overlay", () =>
            {
                api.Reset();

                spectate = new SpectateController(api);

                Child = new ControllerHost(spectate)
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        spectate,
                        overlay = new SpectateSetupOverlay(),
                    },
                };
            });

            AddUntilStep("overlay loaded", () => overlay.IsLoaded);

            AddStep("show it from an empty list", () =>
            {
                foreach (string name in spectate.Watched.ToList())
                    spectate.Remove(name);

                overlay.Show();
            });
        }

        [Test]
        public void AddingANameListsItAndClearsTheBox()
        {
            AddStep("type and add", () => add("mrekk"));

            AddAssert("a row appeared", () => rowNames().SequenceEqual(new[] { "mrekk" }));
            AddAssert("box cleared for the next", () => overlay.NameBox.Text.Length == 0);
        }

        [Test]
        public void RemovingANameTakesItsRowAway()
        {
            AddStep("add two", () =>
            {
                add("mrekk");
                add("peppy");
            });
            AddAssert("two rows", () => rowNames().Count == 2);

            AddStep("remove the first", () => spectate.Remove("mrekk"));
            AddAssert("only the other remains", () => rowNames().SequenceEqual(new[] { "peppy" }));
        }

        [Test]
        public void TheEightCapIsEnforcedAndTheOverflowNameIsKept()
        {
            AddStep("fill to the cap", () =>
            {
                for (int i = 1; i <= SpectateWatchList.MAX_WATCHED; i++)
                    add("player" + i);
            });

            AddAssert("exactly the cap is listed", () => rowNames().Count == SpectateWatchList.MAX_WATCHED);

            AddStep("try one more", () => add("overflow"));

            AddAssert("still capped", () => rowNames().Count == SpectateWatchList.MAX_WATCHED);
            AddAssert("the refused name was not added", () => !rowNames().Contains("overflow"));

            // The add was refused, so — as with a duplicate — what was typed is deliberately left in
            // place rather than cleared, which would have looked like it worked.
            AddAssert("the overflow name is still in the box", () => overlay.NameBox.Text == "overflow");
        }

        private void add(string name)
        {
            overlay.NameBox.Text = name;
            overlay.AddButton.Action!.Invoke();
        }

        private List<string> rowNames()
            => overlay.Rows.ChildrenOfType<SpriteText>()
                      .Where(t => t.Font.Size == Theme.RowSecondaryTextSize)
                      .Select(t => t.Text.ToString())
                      .ToList();

        /// <summary>Hands its subtree the controller this test built — a fresh dependency container
        /// per instance, matching <see cref="TestSceneSpectatePanel"/>.</summary>
        private partial class ControllerHost : Container
        {
            private readonly SpectateController controller;

            public ControllerHost(SpectateController controller)
            {
                this.controller = controller;
            }

            protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
            {
                var dependencies = new DependencyContainer(parent);
                dependencies.CacheAs(controller);
                return dependencies;
            }
        }

        /// <summary>osu!, stubbed at the network boundary — the setup modal never needs a real user
        /// resolved, only the watch list edited, so every call returns nothing.</summary>
        private class FakeApi : ISpectateApi
        {
            public void Reset()
            {
            }

            public Task<SpectateUser?> ResolveUserAsync(string username, CancellationToken ct = default)
                => Task.FromResult<SpectateUser?>(null);

            public Task<IReadOnlyList<SpectateUser>> PresenceAsync(IReadOnlyList<int> userIds, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<SpectateUser>>(Array.Empty<SpectateUser>());

            public Task<SpectateScore?> LatestScoreAsync(int userId, CancellationToken ct = default)
                => Task.FromResult<SpectateScore?>(null);

            public Task DownloadReplayAsync(long scoreId, string destinationPath, CancellationToken ct = default)
                => throw new NotSupportedException("the setup modal never fetches a replay");
        }
    }
}
