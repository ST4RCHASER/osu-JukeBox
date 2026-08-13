using System.IO;
using JukeBox.Game.Configuration;
using NUnit.Framework;
using osu.Framework.Platform;
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

        [Test]
        public void LazerSettingsPanelDefaults()
        {
            var config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-config-test", Path.GetRandomFileName())));

            Assert.That(config.Get<JukeBoxSkin>(JukeBoxSetting.Skin), Is.EqualTo(JukeBoxSkin.Argon));
            Assert.That(config.Get<double>(JukeBoxSetting.BackgroundBlur), Is.EqualTo(0.0));
            Assert.That(config.Get<bool>(JukeBoxSetting.ShowStoryboardVideo), Is.True);
            Assert.That(config.Get<double>(JukeBoxSetting.UiScale), Is.EqualTo(1.0));
            Assert.That(config.Get<bool>(JukeBoxSetting.VolumeMigrated), Is.False);
            Assert.That(config.Get<double>(JukeBoxSetting.GlobalAudioOffset), Is.EqualTo(0.0));

            // UiScale range must clamp to the supported 0.8–1.6 window.
            config.SetValue(JukeBoxSetting.UiScale, 5.0);
            Assert.That(config.Get<double>(JukeBoxSetting.UiScale), Is.EqualTo(1.6));
            config.SetValue(JukeBoxSetting.UiScale, 0.1);
            Assert.That(config.Get<double>(JukeBoxSetting.UiScale), Is.EqualTo(0.8));
        }

        [Test]
        public void UiLayoutDefaultsToThreeColumn()
        {
            var config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-config-test", Path.GetRandomFileName())));

            Assert.That(config.Get<UiLayout>(JukeBoxSetting.UiLayout), Is.EqualTo(UiLayout.ThreeColumn));
        }

        // Regression coverage for the FullscreenOverlay/Split -> ThreeColumn/Focus rename: an ini
        // file written by a previous version of the app names an enum value ("Split") that no
        // longer exists. Loading it must not throw, and must fall back to the freshly-declared
        // default (ThreeColumn) rather than silently resurrecting the old layout mode under a name
        // that happens to still parse.
        [Test]
        public void LegacyUiLayoutValueMigratesSafelyToThreeColumnDefault()
        {
            string directory = Path.Combine("jukebox-config-test", Path.GetRandomFileName());
            var storage = new TemporaryNativeStorage(directory);

            using (var stream = storage.CreateFileSafely("jukebox.ini"))
            using (var writer = new StreamWriter(stream))
                writer.Write("UiLayout = Split\n");

            JukeBoxConfigManager config = null!;
            Assert.DoesNotThrow(() => config = new JukeBoxConfigManager(storage));

            Assert.That(config.Get<UiLayout>(JukeBoxSetting.UiLayout), Is.EqualTo(UiLayout.ThreeColumn));
        }
    }
}
