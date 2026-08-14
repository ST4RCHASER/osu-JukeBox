#nullable enable

using System.IO;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Detach
{
    [TestFixture]
    public class ViewerGameConstructionTest
    {
        // The viewer game must be constructible without a host (its load/LoadComplete are
        // host-bound, but construction happens before the --viewer process's host exists, and a
        // construction-time crash there would present as a window that flashes and dies).
        [Test]
        public void ConstructsWithoutAHost()
        {
            Assert.DoesNotThrow(() =>
            {
                using var game = new JukeBoxViewerGame(TextReader.Null);
            });
        }
    }
}
