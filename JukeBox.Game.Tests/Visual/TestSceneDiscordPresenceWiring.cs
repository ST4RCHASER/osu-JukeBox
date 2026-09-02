#nullable enable

using System.IO;
using System.Linq;
using JukeBox.Game.Presence;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Rich presence belongs to the MAIN process alone. The detached viewer window is a second
    /// process of this same binary, and if it published too, the two would fight over the user's
    /// single Discord activity slot — so the service is added by <see cref="JukeBoxGame"/> and
    /// nowhere else.
    ///
    /// <para>
    /// The scene deliberately adds nothing of its own: it runs on
    /// <see cref="JukeBoxGameBase"/> — the very class <see cref="JukeBoxViewerGame"/> derives from,
    /// and the one whose <c>load()</c> a future change would most plausibly move the service into —
    /// so an empty tree here is the assertion that neither the base nor the viewer wires it up.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneDiscordPresenceWiring : JukeBoxTestScene
    {
        [Test]
        public void TheBaseGameWiresNoPresenceService()
        {
            AddAssert("nothing publishes presence on the base game", () =>
                hostGame()!.ChildrenOfType<DiscordPresenceService>().Any() == false);
        }

        /// <summary>
        /// The viewer game's own construction, for the same reason
        /// <see cref="Detach.ViewerGameConstructionTest"/> exists: it happens before the --viewer
        /// process has a host, and it must not reach for Discord (or anything else) on the way up.
        /// </summary>
        [Test]
        public void TheViewerGameConstructsWithoutTouchingPresence()
        {
            AddAssert("viewer game constructs cleanly", () =>
            {
                using var viewer = new JukeBoxViewerGame(TextReader.Null);
                return viewer.ChildrenOfType<DiscordPresenceService>().Any() == false;
            });
        }

        private JukeBoxGameBase? hostGame()
        {
            for (Drawable? drawable = this; drawable != null; drawable = drawable.Parent)
            {
                if (drawable is JukeBoxGameBase game)
                    return game;
            }

            return null;
        }
    }
}
