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
    public partial class TestSceneMapIdOverlay : ManualInputManagerTestScene
    {
        private MapIdOverlay overlay = null!;
        private StubMirror mirror = null!;
        private BeatmapSetInfo? resolved;

        // CreateChildDependencies runs once for the whole scene (shared across every [Test] in
        // this fixture), so StubMirror's contents are reset in SetUpSteps below rather than here
        // — same approach as TestSceneSearchOverlay.
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
                resolved = null;
                mirror.Sets.Clear();
                mirror.Requests.Clear();
                mirror.Gate = null;
                Child = overlay = new MapIdOverlay();
                overlay.SetResolved += set => resolved = set;
            });
        }

        [Test]
        public void ValidIdResolvesAndClosesOverlay()
        {
            AddStep("mirror has set 1000", () => mirror.Sets.Add(new BeatmapSetInfo { Id = 1000, Title = "T", Artist = "A", Creator = "C" }));

            AddStep("show overlay and enter id", () =>
            {
                overlay.Show();
                overlay.IdBox.Text = "1000";
            });
            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("set resolved", () => resolved?.Id == 1000);
            AddAssert("overlay hidden", () => overlay.State.Value == Visibility.Hidden);
        }

        [Test]
        public void NotFoundIdShowsErrorAndStaysOpen()
        {
            AddStep("show overlay and enter unknown id", () =>
            {
                overlay.Show();
                overlay.IdBox.Text = "999999";
            });
            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("error text visible", () => overlay.ErrorText.Text.ToString().Length > 0);
            AddAssert("nothing resolved", () => resolved == null);
            AddAssert("overlay still visible", () => overlay.State.Value == Visibility.Visible);
        }

        [Test]
        public void NonNumericInputShowsErrorWithoutRequest()
        {
            AddStep("show overlay and enter non-numeric text", () =>
            {
                overlay.Show();
                overlay.IdBox.Text = "abc";
            });
            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddAssert("error text visible", () => overlay.ErrorText.Text.ToString().Length > 0);
            AddAssert("no request made", () => mirror.Requests.Count == 0);
            AddAssert("overlay still visible", () => overlay.State.Value == Visibility.Visible);
        }

        [Test]
        public void FallsBackToPlainQueryWhenRestrictedOptionMisses()
        {
            // Simulates a mirror (e.g. a fallback that ignores Option) whose "setId"-restricted
            // search doesn't return the exact id as its first result, but a plain query does.
            AddStep("mirror only matches on plain query", () =>
            {
                mirror.IgnoreOption = true;
                mirror.Sets.Add(new BeatmapSetInfo { Id = 42, Title = "T", Artist = "A", Creator = "C" });
            });

            AddStep("show overlay and enter id", () =>
            {
                overlay.Show();
                overlay.IdBox.Text = "42";
            });
            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("set resolved via fallback", () => resolved?.Id == 42);
            AddAssert("both a restricted and a plain request were made", () =>
                mirror.Requests.Any(r => r.Option == "setId") && mirror.Requests.Any(r => r.Option == null));
        }

        [Test]
        public void LookupInProgressDisablesInputAndShowsStatus()
        {
            var gate = new TaskCompletionSource<bool>();

            AddStep("mirror gated on a pending task", () =>
            {
                mirror.Gate = gate;
                mirror.Sets.Add(new BeatmapSetInfo { Id = 7, Title = "T", Artist = "A", Creator = "C" });
            });

            AddStep("show overlay and enter id", () =>
            {
                overlay.Show();
                overlay.IdBox.Text = "7";
            });
            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("status text shows looking up", () => overlay.ErrorText.Text.ToString().Contains("looking up"));
            AddAssert("input box disabled while in flight", () => overlay.IdBox.Current.Disabled);

            AddStep("release the gate", () => gate.SetResult(true));
            AddUntilStep("set resolved", () => resolved?.Id == 7);
        }

        // Serves fixed sets, matching on Id.ToString() == Query. Records every request so tests can
        // assert on the restricted-then-fallback lookup sequence. IgnoreOption simulates a mirror
        // (like the non-NeriNyan fallbacks) that doesn't honour SearchRequest.Option, so a
        // "setId"-restricted call finds nothing and MapIdOverlay must retry with a plain query.
        private class StubMirror : IBeatmapMirror
        {
            public string Name => "stub";
            public List<BeatmapSetInfo> Sets { get; } = new();
            public List<SearchRequest> Requests { get; } = new();
            public bool IgnoreOption;
            public TaskCompletionSource<bool>? Gate;

            public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                Requests.Add(request);

                if (Gate != null)
                    await Gate.Task.ConfigureAwait(false);

                if (request.Option == "setId" && IgnoreOption)
                    return new List<BeatmapSetInfo>();

                return Sets.Where(s => s.Id.ToString() == request.Query).ToList();
            }

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
