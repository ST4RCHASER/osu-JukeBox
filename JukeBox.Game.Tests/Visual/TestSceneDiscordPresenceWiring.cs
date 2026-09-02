#nullable enable

using System.IO;
using System.Linq;
using JukeBox.Game.Presence;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Nothing reachable from the test project may wire rich presence, because wiring it means
    /// opening a real Discord IPC connection inside a headless test host — which killed the host
    /// mid-fixture and failed 30 unrelated tests when presence lived in <see cref="JukeBoxGame"/>.
    ///
    /// <para>
    /// The first version of this fixture asserted only that <see cref="JukeBoxGameBase"/> wired
    /// nothing, and reasoned from there that every fixture was safe. That reasoning was wrong:
    /// <see cref="TestSceneJukeBoxGame"/> and <see cref="TestSceneRealVirtualAudioSet"/> both
    /// <c>AddGame(new JukeBoxGame())</c>. So this now builds the REAL game the way those fixtures
    /// do, which is the shape that actually broke. Presence lives in JukeBox.Desktop's
    /// <c>JukeBoxDesktopGame</c>, a project the test assembly cannot reference.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneDiscordPresenceWiring : JukeBoxTestScene
    {
        private JukeBoxGame game = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            AddGame(game = new JukeBoxGame());
        }

        /// <summary>
        /// The regression pin. A real JukeBoxGame under a headless host — exactly what the two
        /// fixtures above construct — must contain no presence service anywhere in its tree.
        /// </summary>
        [Test]
        public void TheRealGameWiresNoPresenceService()
        {
            AddUntilStep("game loaded", () => game.IsLoaded);
            AddAssert("no presence service in the game's tree",
                () => !game.ChildrenOfType<DiscordPresenceService>().Any());
        }

        /// <summary>
        /// And nothing else the test host builds wires one either — the runner is a
        /// <see cref="JukeBoxGameBase"/>, the same class <see cref="JukeBoxViewerGame"/> derives
        /// from, so an empty tree here covers the viewer process too.
        /// </summary>
        [Test]
        public void NothingElseInTheTestHostWiresPresenceEither()
        {
            AddAssert("none anywhere under the test scene", () => !this.ChildrenOfType<DiscordPresenceService>().Any());
        }

        [Test]
        public void TheViewerGameConstructsWithoutTouchingPresence()
        {
            AddAssert("viewer game constructs cleanly", () =>
            {
                using var viewer = new JukeBoxViewerGame(TextReader.Null);
                return !viewer.ChildrenOfType<DiscordPresenceService>().Any();
            });
        }
    }
}
