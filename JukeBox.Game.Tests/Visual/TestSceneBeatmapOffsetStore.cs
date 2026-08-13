#nullable enable

using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Per-beatmap audio offset persistence: <see cref="BeatmapOffsetStore.CurrentOffset"/>
    /// follows the playing set (loading its stored value, resetting to 0 for unknown sets),
    /// edits persist under the current set's id, and a fresh store instance reading the same
    /// storage sees them again.
    /// </summary>
    [TestFixture]
    public partial class TestSceneBeatmapOffsetStore : JukeBoxTestScene
    {
        [Cached]
        private readonly PlaybackController controller = new PlaybackController();

        private TemporaryNativeStorage storage = null!;
        private BeatmapOffsetStore store = null!;
        private string tmp = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
            storage = new TemporaryNativeStorage(Path.Combine("jukebox-offset-test", Path.GetRandomFileName()));
        }

        // The controller stays un-parented: the store only consumes its Current bindable (works
        // without the component being loaded), and re-parenting a [Cached] instance across
        // Child reassignments would dispose it.
        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create store", () => Child = store = new BeatmapOffsetStore(storage));
        }

        private CachedBeatmapSet set(int id) => new CachedBeatmapSet { SetId = id, Directory = tmp };

        [Test]
        public void OffsetPersistsPerSetAndSurvivesReload()
        {
            AddStep("play set 42", () => controller.Current.Value = set(42));
            AddAssert("offset starts 0", () => store.CurrentOffset.Value == 0);

            AddStep("set offset +120", () => store.CurrentOffset.Value = 120);
            AddAssert("stored under set 42", () => store.GetOffset(42) == 120);

            AddStep("switch to set 43", () => controller.Current.Value = set(43));
            AddAssert("offset resets for unknown set", () => store.CurrentOffset.Value == 0);

            AddStep("back to set 42", () => controller.Current.Value = set(42));
            AddAssert("stored offset restored", () => store.CurrentOffset.Value == 120);

            // A fresh store over the same storage must read the persisted value back from disk.
            BeatmapOffsetStore reloaded = null!;
            AddStep("create fresh store on same storage", () => Child = reloaded = new BeatmapOffsetStore(storage));
            AddUntilStep("fresh store loaded", () => reloaded.IsLoaded);
            AddAssert("persisted offset read back", () => reloaded.GetOffset(42) == 120);
        }
    }
}
