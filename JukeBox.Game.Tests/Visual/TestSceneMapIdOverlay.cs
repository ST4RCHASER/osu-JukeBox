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
using osu.Game.Graphics.UserInterface;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Covers the redesigned map-ID dialog: one input taking either a beatmapset ID or an
    /// osu.ppy.sh link (parsing itself is unit-tested in <see cref="Online.BeatmapLinkParseTest"/>),
    /// Cancel/Lookup buttons with Escape/Enter bound to them, and a real spinner — not a "loading…"
    /// line — while a lookup is in flight.
    /// </summary>
    [TestFixture]
    public partial class TestSceneMapIdOverlay : JukeBoxManualInputTestScene
    {
        private MapIdOverlay overlay = null!;
        private StubMirror mirror = null!;
        private BeatmapSetInfo? resolved;

        // CreateChildDependencies runs once for the whole scene (shared across every [Test] in
        // this fixture), so StubMirror's contents are reset in SetUpSteps below rather than here
        // — same approach as TestSceneBeatmapListing.
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
                mirror.IgnoreOption = false;
                Child = overlay = new MapIdOverlay();
                overlay.SetResolved += set => resolved = set;
            });
        }

        private void showWith(string text) => AddStep($"show overlay with '{text}'", () =>
        {
            overlay.Show();
            overlay.IdBox.Text = text;
        });

        [Test]
        public void ValidIdResolvesAndClosesOverlay()
        {
            AddStep("mirror has set 1000", () => mirror.Sets.Add(new BeatmapSetInfo { Id = 1000, Title = "T", Artist = "A", Creator = "C" }));

            showWith("1000");
            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("set resolved", () => resolved?.Id == 1000);
            AddAssert("overlay hidden", () => overlay.State.Value == Visibility.Hidden);
        }

        // The headline of the redesign: a pasted set link works exactly like the bare id.
        [Test]
        public void PastedBeatmapsetLinkResolvesTheSameAsABareId()
        {
            AddStep("mirror has set 1000", () => mirror.Sets.Add(new BeatmapSetInfo { Id = 1000, Title = "T", Artist = "A", Creator = "C" }));

            showWith("https://osu.ppy.sh/beatmapsets/1000#osu/54321");
            AddStep("click Lookup", () => overlay.LookupButton.TriggerClick());

            AddUntilStep("set resolved", () => resolved?.Id == 1000);
            AddAssert("the mirror was queried with the SET id, not the difficulty id",
                () => mirror.Requests.All(r => r.Query == "1000"));
        }

        // A difficulty link can't be resolved by any mirror here (no beatmap-id endpoint), so it
        // must tell the user what to paste instead rather than firing a doomed request.
        [Test]
        public void DifficultyLinkExplainsItselfWithoutQueryingTheMirror()
        {
            showWith("https://osu.ppy.sh/b/67890");
            AddStep("click Lookup", () => overlay.LookupButton.TriggerClick());

            AddAssert("guidance mentions the beatmapset link",
                () => overlay.ErrorText.Text.ToString().Contains("beatmapset link"));
            AddAssert("no request made", () => mirror.Requests.Count == 0);
            AddAssert("overlay still visible", () => overlay.State.Value == Visibility.Visible);
        }

        [Test]
        public void NotFoundIdShowsErrorAndStaysOpen()
        {
            showWith("999999");
            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("error text visible", () => overlay.ErrorText.Text.ToString().Length > 0);
            AddAssert("nothing resolved", () => resolved == null);
            AddAssert("overlay still visible", () => overlay.State.Value == Visibility.Visible);
        }

        [Test]
        public void UnparseableInputShowsErrorWithoutRequest()
        {
            showWith("not a map at all");
            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddAssert("error text visible", () => overlay.ErrorText.Text.ToString().Length > 0);
            AddAssert("no request made", () => mirror.Requests.Count == 0);
            AddAssert("overlay still visible", () => overlay.State.Value == Visibility.Visible);
        }

        [Test]
        public void CancelButtonClosesWithoutResolvingAnything()
        {
            AddStep("mirror has set 1000", () => mirror.Sets.Add(new BeatmapSetInfo { Id = 1000, Title = "T", Artist = "A", Creator = "C" }));

            showWith("1000");
            AddStep("click Cancel", () => overlay.CancelButton.TriggerClick());

            AddUntilStep("overlay hidden", () => overlay.State.Value == Visibility.Hidden);
            AddAssert("nothing resolved", () => resolved == null);
            AddAssert("no request made", () => mirror.Requests.Count == 0);
        }

        [Test]
        public void EscapeCancelsTheSameWayTheButtonDoes()
        {
            showWith("1000");
            AddStep("press escape", () => InputManager.Key(Key.Escape));

            AddUntilStep("overlay hidden", () => overlay.State.Value == Visibility.Hidden);
            AddAssert("nothing resolved", () => resolved == null);
            AddAssert("no request made", () => mirror.Requests.Count == 0);
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

            showWith("42");
            AddStep("press enter", () => InputManager.Key(Key.Enter));

            AddUntilStep("set resolved via fallback", () => resolved?.Id == 42);
            AddAssert("both a restricted and a plain request were made", () =>
                mirror.Requests.Any(r => r.Option == "setId") && mirror.Requests.Any(r => r.Option == null));
        }

        [Test]
        public void LookupInProgressSpinsASpinnerAndDisablesInput()
        {
            var gate = new TaskCompletionSource<bool>();

            AddStep("mirror gated on a pending task", () =>
            {
                mirror.Gate = gate;
                mirror.Sets.Add(new BeatmapSetInfo { Id = 7, Title = "T", Artist = "A", Creator = "C" });
            });

            showWith("7");
            AddStep("click Lookup", () => overlay.LookupButton.TriggerClick());

            AddUntilStep("spinner spinning", () => overlay.ChildrenOfType<LoadingSpinner>().Any(s => s.State.Value == Visibility.Visible));
            AddAssert("no progress TEXT — the spinner is the progress", () => overlay.ErrorText.Text.ToString().Length == 0);
            AddAssert("input box disabled while in flight", () => overlay.IdBox.Current.Disabled);
            AddAssert("Lookup disabled while in flight", () => !overlay.LookupButton.Enabled.Value);
            AddAssert("Cancel stays available", () => overlay.CancelButton.Enabled.Value);

            AddStep("release the gate", () => gate.SetResult(true));
            AddUntilStep("set resolved", () => resolved?.Id == 7);
            AddUntilStep("spinner stopped", () => overlay.ChildrenOfType<LoadingSpinner>().All(s => s.State.Value == Visibility.Hidden));
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

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
