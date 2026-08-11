#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneSearchOverlay : ManualInputManagerTestScene
    {
        private SearchOverlay overlay = null!;
        private StubMirror mirror = null!;
        private BeatmapSetInfo? picked;

        // CreateChildDependencies runs once for the whole scene (shared across every [Test] in
        // this fixture), so StubMirror's contents are reset in SetUpSteps below rather than here
        // — otherwise one test mutating it would leak into another.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var deps = new DependencyContainer(parent);
            deps.CacheAs<IBeatmapMirror>(mirror = new StubMirror());
            return deps;
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create overlay", () =>
            {
                picked = null;
                mirror.Sets.Clear();
                mirror.Sets.AddRange(StubMirror.DefaultSets());
                Child = overlay = new SearchOverlay { RelativeSizeAxes = Axes.Both };
                overlay.SetPicked += set => picked = set;
            });
        }

        [Test]
        public void TypingShowsResultsAndEnterPicksFirst()
        {
            AddStep("type 'a' (opens + seeds textbox)", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("3 result rows shown", () => overlay.ChildrenOfType<SearchResultRow>().Count() == 3);

            AddStep("press enter", () => InputManager.Key(Key.Enter));
            AddUntilStep("first set picked", () => picked?.Id == mirror.Sets[0].Id);
            AddAssert("overlay hidden", () => overlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void EmptyMirrorShowsNoResults()
        {
            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("3 result rows shown", () => overlay.ChildrenOfType<SearchResultRow>().Count() == 3);

            AddStep("clear mirror's sets and press backspace to re-trigger a search",
                () =>
                {
                    mirror.Sets.Clear();
                    InputManager.Key(Key.BackSpace);
                });

            AddUntilStep("no rows shown", () => !overlay.ChildrenOfType<SearchResultRow>().Any());
        }

        [Test]
        public void DisabledSetRowIsNotClickable()
        {
            AddStep("mirror returns a single download-disabled set", () =>
            {
                mirror.Sets.Clear();
                mirror.Sets.Add(new BeatmapSetInfo
                {
                    Id = 99,
                    Title = "Locked Song",
                    Artist = "Artist L",
                    Creator = "mapperL",
                    Status = "ranked",
                    Availability = new AvailabilityInfo { DownloadDisabled = true },
                });
            });

            AddStep("type 'a'", () => overlay.ShowWithInitialChar('a'));
            AddUntilStep("1 result row shown", () => overlay.ChildrenOfType<SearchResultRow>().Count() == 1);

            SearchResultRow row = null!;
            AddStep("grab the row", () => row = overlay.ChildrenOfType<SearchResultRow>().Single());
            AddAssert("row reports disabled (framework-level, not just dimmed)", () => row.Enabled.Value == false);

            AddStep("click the disabled row", () =>
            {
                InputManager.MoveMouseTo(row);
                InputManager.Click(MouseButton.Left);
            });

            AddAssert("no set was picked", () => picked == null);
            AddAssert("overlay still visible (click was a no-op, not a pick+hide)", () => overlay.State.Value == Visibility.Visible);
        }

        // Serves fixed sets for any query — enough to exercise the debounce → search → render
        // pipeline without touching the network.
        private class StubMirror : IBeatmapMirror
        {
            public string Name => "stub";

            public List<BeatmapSetInfo> Sets { get; } = new();

            public static List<BeatmapSetInfo> DefaultSets() => new()
            {
                new BeatmapSetInfo { Id = 1, Title = "Alpha Song", Artist = "Artist A", Creator = "mapperA", Status = "ranked" },
                new BeatmapSetInfo { Id = 2, Title = "Beta Song", Artist = "Artist B", Creator = "mapperB", Status = "ranked" },
                new BeatmapSetInfo { Id = 3, Title = "Gamma Song", Artist = "Artist C", Creator = "mapperC", Status = "ranked" },
            };

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>(Sets));

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
