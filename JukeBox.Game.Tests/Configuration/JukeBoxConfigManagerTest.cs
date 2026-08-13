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
        // ShowFps is deprecated (superseded by FpsDisplay — see that setting's remarks) but the
        // key must keep parsing, and defaulting false, so an old ini value still migrates safely.
        [Test]
        public void LegacyShowFpsKeyDefaultsToFalse()
        {
            var config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-config-test", Path.GetRandomFileName())));

            Assert.That(config.Get<bool>(JukeBoxSetting.ShowFps), Is.False);
        }

        // FpsDisplay (legacy) is deprecated (superseded by FpsDisplayMode — see that setting's
        // remarks) but the key must keep parsing, and defaulting to LegacyFpsDisplayMode.Off, so an
        // old Off/Compact/Details ini value still migrates safely.
        [Test]
        public void LegacyFpsDisplayKeyDefaultsToOff()
        {
            var config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-config-test", Path.GetRandomFileName())));

            Assert.That(config.Get<LegacyFpsDisplayMode>(JukeBoxSetting.FpsDisplay), Is.EqualTo(LegacyFpsDisplayMode.Off));
            Assert.That(config.Get<bool>(JukeBoxSetting.FpsDisplayMigrated), Is.False);
        }

        [Test]
        public void FpsDisplayModeDefaultsToOff()
        {
            var config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-config-test", Path.GetRandomFileName())));

            Assert.That(config.Get<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode), Is.EqualTo(FpsDisplayMode.Off));
            Assert.That(config.Get<bool>(JukeBoxSetting.FpsDisplayModeMigrated), Is.False);
        }

        // Regression coverage for the Compact-overlay/Graph rename: an ini file written by a
        // previous version of the app names an enum value ("Compact") that the CURRENT
        // FpsDisplayMode also has a member named — but the legacy FpsDisplay key is now decoded
        // through LegacyFpsDisplayMode (a distinct type kept solely for this), so the raw text
        // still parses under its ORIGINAL meaning rather than silently landing on the new type's
        // same-named-but-different member. The actual old->new value remap is JukeBoxGameBase's job
        // (MigrateLegacyFpsDisplay, covered in JukeBoxGameBaseTest) — this only covers that the
        // legacy key itself still loads the right legacy value.
        //
        // NOTE: IniConfigManager's default Filename is "game.ini" (JukeBoxConfigManager doesn't
        // override it) — NOT "jukebox.ini", despite the sibling
        // LegacyUiLayoutValueMigratesSafelyToThreeColumnDefault test below using that name.
        [Test]
        public void LegacyFpsDisplayValueStillParsesUnderOriginalMeaning()
        {
            string directory = Path.Combine("jukebox-config-test", Path.GetRandomFileName());
            var storage = new TemporaryNativeStorage(directory);

            using (var stream = storage.CreateFileSafely("game.ini"))
            using (var writer = new StreamWriter(stream))
                writer.Write("FpsDisplay = Compact\n");

            JukeBoxConfigManager config = null!;
            Assert.DoesNotThrow(() => config = new JukeBoxConfigManager(storage));

            Assert.That(config.Get<LegacyFpsDisplayMode>(JukeBoxSetting.FpsDisplay), Is.EqualTo(LegacyFpsDisplayMode.Compact));
        }

        // A genuinely unrecognised legacy value (never a valid Off/Compact/Details name) must not
        // throw, and must fall back to the freshly-declared default (Off) — same
        // catch-and-discard-to-default behaviour as LegacyUiLayoutValueMigratesSafelyToThreeColumnDefault below.
        [Test]
        public void GarbageLegacyFpsDisplayValueMigratesSafelyToOffDefault()
        {
            string directory = Path.Combine("jukebox-config-test", Path.GetRandomFileName());
            var storage = new TemporaryNativeStorage(directory);

            using (var stream = storage.CreateFileSafely("game.ini"))
            using (var writer = new StreamWriter(stream))
                writer.Write("FpsDisplay = TotallyUnknownValue\n");

            JukeBoxConfigManager config = null!;
            Assert.DoesNotThrow(() => config = new JukeBoxConfigManager(storage));

            Assert.That(config.Get<LegacyFpsDisplayMode>(JukeBoxSetting.FpsDisplay), Is.EqualTo(LegacyFpsDisplayMode.Off));
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
        public void ChartZoomDefaultsTo100PercentAndClampsToSupportedRange()
        {
            var config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-config-test", Path.GetRandomFileName())));

            Assert.That(config.Get<double>(JukeBoxSetting.ChartZoom), Is.EqualTo(1.0));

            // ChartZoom range must clamp to the supported 50%–150% window.
            config.SetValue(JukeBoxSetting.ChartZoom, 5.0);
            Assert.That(config.Get<double>(JukeBoxSetting.ChartZoom), Is.EqualTo(1.5));
            config.SetValue(JukeBoxSetting.ChartZoom, 0.1);
            Assert.That(config.Get<double>(JukeBoxSetting.ChartZoom), Is.EqualTo(0.5));
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
