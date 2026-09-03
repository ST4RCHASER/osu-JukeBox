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
    /// The spectate controls, driven the way a person drives them — typing a name, pressing Watch,
    /// pressing Start — against a fake osu! so the whole path from the button to the status text is
    /// the production one with only the network stubbed.
    /// </summary>
    [TestFixture]
    public partial class TestSceneSpectatePanel : JukeBoxTestScene
    {
        private readonly FakeApi api = new FakeApi();

        private SpectateController spectate = null!;
        private SpectatePanel panel = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("build panel", () =>
            {
                api.Reset();

                spectate = new SpectateController(api);

                // The controller is rebuilt per test and cached by a container inside the scene
                // rather than by a [Cached] field: TestScene disposes its children between tests, so
                // a field-cached component would reach the second test already dead.
                Child = new ControllerHost(spectate)
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        spectate,
                        new Container
                        {
                            // Fixed width so the panel's own RelativeSizeAxes.X has something to
                            // resolve against, at roughly the right column's real width.
                            Width = 380,
                            AutoSizeAxes = Axes.Y,
                            Margin = new MarginPadding(20),
                            Child = panel = new SpectatePanel(),
                        },
                    },
                };
            });

            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddStep("start from an empty list", () =>
            {
                spectate.Active.Value = false;

                foreach (string name in spectate.Watched.ToList())
                    spectate.Remove(name);
            });
        }

        [Test]
        public void WatchingSomeoneAddsTheirRowAndClearsTheBox()
        {
            AddStep("type a name", () => panel.NameBox.Text = "mrekk");
            AddStep("press Watch", () => panel.AddButton.Action!.Invoke());

            AddAssert("a row appeared for them", () => rowNames().SequenceEqual(new[] { "mrekk" }));
            AddAssert("the box is ready for the next name", () => panel.NameBox.Text.Length == 0);
        }

        [Test]
        public void WatchingTheSamePersonTwiceLeavesOneRowAndKeepsWhatWasTyped()
        {
            AddStep("watch mrekk", () => watch("mrekk"));
            AddStep("type the same name differently", () => panel.NameBox.Text = "MREKK");
            AddStep("press Watch", () => panel.AddButton.Action!.Invoke());

            AddAssert("still one row", () => rowNames().Count == 1);

            // The text is deliberately left in place: clearing it would look like the add worked.
            AddAssert("what was typed is still there", () => panel.NameBox.Text == "MREKK");
        }

        [Test]
        public void RemovingAPlayerTakesTheirRowAway()
        {
            AddStep("watch two", () =>
            {
                watch("mrekk");
                watch("peppy");
            });

            AddAssert("two rows", () => rowNames().Count == 2);

            AddStep("remove the first", () => spectate.Remove("mrekk"));

            AddAssert("only the other is left", () => rowNames().SequenceEqual(new[] { "peppy" }));
        }

        [Test]
        public void TheOneButtonSaysWhichWayItWillGo()
        {
            AddAssert("offers to start", () => panel.StartButton.Text == "Start spectating");

            AddStep("press it", () => panel.StartButton.Action!.Invoke());

            AddAssert("spectating is on", () => spectate.Active.Value);
            AddAssert("now offers to stop", () => panel.StartButton.Text == "Stop spectating");

            AddStep("press it again", () => panel.StartButton.Action!.Invoke());

            AddAssert("spectating is off", () => !spectate.Active.Value);
            AddAssert("offers to start again", () => panel.StartButton.Text == "Start spectating");
        }

        [Test]
        public void AWatchedPlayerEndsUpShowingWhatOsuSaysAndWhatWeInferred()
        {
            AddStep("osu! knows them, online, just failed", () =>
            {
                api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null));

                // No replay to fetch, so this test needs no beatmap and no download — what it is
                // about is the two facts reaching the row, not the rendering.
                api.Scores[1] = new SpectateScore(500, 1, "checksum", "Easy", DateTimeOffset.UtcNow, false, false);
            });

            AddStep("watch them", () => watch("mrekk"));
            AddStep("start", () => spectate.Active.Value = true);

            AddUntilStep("the row reports the play", () => rowStatuses().Any(s => s.Contains("no replay")));

            AddStep("stop", () => spectate.Active.Value = false);
        }

        [Test]
        public void AddingAnotherPlayerDoesNotThrowAwayWhatIsKnownAboutTheFirst()
        {
            AddStep("osu! knows one of them", () =>
                api.Users["mrekk"] = new SpectateUser(1, "mrekk", new SpectatePresence(true, null)));

            AddStep("watch and start", () =>
            {
                watch("mrekk");
                spectate.Active.Value = true;
            });

            AddUntilStep("they resolve as online", () => rowStatuses().Any(s => s.StartsWith("online")));

            AddStep("stop polling, then watch someone else", () =>
            {
                spectate.Active.Value = false;
                watch("peppy");
            });

            // Editing the list is a Clear plus an AddRange internally. If that is observable, the
            // session is handed an empty roster and everything already resolved is thrown away —
            // which with polling stopped would leave the first player back at "offline · …".
            AddAssert("the first player is still resolved", () => rowStatuses().Any(s => s.StartsWith("online")));
            AddAssert("and both are listed", () => rowNames().Count == 2);
        }

        [Test]
        public void AnUnknownNameIsCalledOutRatherThanLeftBlank()
        {
            AddStep("watch a name osu! does not have", () => watch("definitelynotarealuser"));
            AddStep("start", () => spectate.Active.Value = true);

            AddUntilStep("the row says so", () => rowStatuses().Any(s => s.Contains("no such player")));

            AddStep("stop", () => spectate.Active.Value = false);
        }

        [Test]
        public void TheHintExplainsWhatSpectatingHereActuallyIs()
        {
            // The honesty note is load-bearing: this feature replays FINISHED plays, and a user who
            // expects a live feed would otherwise read the delay as a bug.
            AddAssert("empty list invites names", () => panel.Hint.Contains("Add up to"));

            AddStep("watch someone", () => watch("mrekk"));

            AddAssert("says what it shows", () => panel.Hint.Contains("most recent completed play"));
        }

        private void watch(string name)
        {
            panel.NameBox.Text = name;
            panel.AddButton.Action!.Invoke();
        }

        /// <summary>The names the rows actually render, read out of the drawables rather than out
        /// of the list they were built from.</summary>
        private List<string> rowNames()
            => panel.Rows.ChildrenOfType<SpriteText>()
                    .Where(t => t.Font.Size == Theme.RowSecondaryTextSize)
                    .Select(t => t.Text.ToString())
                    .ToList();

        private List<string> rowStatuses()
            => panel.Rows.ChildrenOfType<SpriteText>()
                    .Where(t => t.Font.Size == Theme.CaptionTextSize)
                    .Select(t => t.Text.ToString())
                    .ToList();

        /// <summary>
        /// Hands its subtree the controller this test built. A fresh dependency container per
        /// instance, so rebuilding it between tests re-registers rather than colliding with the
        /// previous test's registration.
        /// </summary>
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

        /// <summary>osu!, stubbed at the network boundary.</summary>
        private class FakeApi : ISpectateApi
        {
            public readonly Dictionary<string, SpectateUser> Users = new Dictionary<string, SpectateUser>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<int, SpectateScore> Scores = new Dictionary<int, SpectateScore>();

            public void Reset()
            {
                Users.Clear();
                Scores.Clear();
            }

            public Task<SpectateUser?> ResolveUserAsync(string username, CancellationToken ct = default)
                => Task.FromResult(Users.TryGetValue(username, out var user) ? user : (SpectateUser?)null);

            public Task<IReadOnlyList<SpectateUser>> PresenceAsync(IReadOnlyList<int> userIds, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<SpectateUser>>(Users.Values.Where(u => userIds.Contains(u.Id)).ToList());

            public Task<SpectateScore?> LatestScoreAsync(int userId, CancellationToken ct = default)
                => Task.FromResult(Scores.TryGetValue(userId, out var score) ? score : (SpectateScore?)null);

            public Task DownloadReplayAsync(long scoreId, string destinationPath, CancellationToken ct = default)
                => throw new NotSupportedException("this fixture never has a replay to fetch");
        }
    }
}
