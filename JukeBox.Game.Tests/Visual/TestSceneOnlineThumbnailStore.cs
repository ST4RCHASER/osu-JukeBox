#nullable enable

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Textures;
using osu.Framework.Platform;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Regression coverage for the cover-fetch error storm: <c>TextureStore.GetAsync</c>
    /// runs its lookup on a thread pool thread, and the store's own duplicate-lookup handling
    /// WaitSafely-throws there whenever two async lookups for the SAME url overlap — which the
    /// two listing presentations (both rendering cards for the same result sets) made routine.
    /// <see cref="OnlineThumbnailStore.GetAsync"/> must therefore never issue overlapping
    /// underlying lookups for one url: concurrent requests share a single lookup.
    /// </summary>
    [TestFixture]
    public partial class TestSceneOnlineThumbnailStore : JukeBoxTestScene
    {
        [Resolved]
        private GameHost host { get; set; } = null!;

        [Test]
        public void ConcurrentSameUrlRequestsShareOneUnderlyingLookup()
        {
            CountingTextureStore counting = null!;
            OnlineThumbnailStore store = null!;
            Task[] requests = null!;

            AddStep("create store over a counting stub", () =>
            {
                counting = new CountingTextureStore(host.Renderer);
                store = new OnlineThumbnailStore { Store = counting };
            });

            AddStep("issue 10 concurrent fetches of the same url", () =>
            {
                counting.Release.Reset();
                requests = Enumerable.Range(0, 10).Select(_ => (Task)store.GetAsync("https://example.invalid/cover.jpg")).ToArray();
            });

            AddUntilStep("one underlying lookup started", () => counting.Lookups == 1);
            AddStep("let the lookup complete", () => counting.Release.Set());
            AddUntilStep("every request completed", () => requests.All(r => r.IsCompleted));
            AddAssert("still exactly one underlying lookup", () => counting.Lookups == 1);

            AddStep("fetch the same url again after completion", () => requests = new[] { (Task)store.GetAsync("https://example.invalid/cover.jpg") });
            AddUntilStep("late request completed", () => requests.All(r => r.IsCompleted));
            AddAssert("late request went to the store again (no stale in-flight entry pinned)", () => counting.Lookups == 2);
        }

        /// <summary>Overrides the (virtual) synchronous lookup that <c>TextureStore.GetAsync</c>
        /// dispatches to, counting entries and blocking until released so the test can hold a
        /// lookup "in flight" deterministically. Returns no texture — only call counts matter.</summary>
        private class CountingTextureStore : TextureStore
        {
            public int Lookups;

            public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);

            public CountingTextureStore(osu.Framework.Graphics.Rendering.IRenderer renderer)
                : base(renderer)
            {
            }

            public override Texture Get(string name, WrapMode wrapModeS, WrapMode wrapModeT)
            {
                Interlocked.Increment(ref Lookups);
                Release.Wait(10_000);
                return null!;
            }
        }
    }
}
