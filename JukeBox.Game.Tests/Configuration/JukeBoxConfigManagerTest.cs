using System.IO;
using JukeBox.Game.Configuration;
using NUnit.Framework;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Configuration
{
    [TestFixture]
    public class JukeBoxConfigManagerTest
    {
        [Test]
        public void ShowFpsDefaultsToFalse()
        {
            var config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-config-test", Path.GetRandomFileName())));

            Assert.That(config.Get<bool>(JukeBoxSetting.ShowFps), Is.False);
        }
    }
}
