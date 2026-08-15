#nullable enable

using System.IO;
using System.IO.Compression;
using JukeBox.Game.Configuration;
using JukeBox.Game.Import;
using NUnit.Framework;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Import
{
    [TestFixture]
    public class SkinArchiveTest
    {
        private string tmp = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
        }

        private string skinsRoot => Path.Combine(tmp, "skins");

        /// <param name="wrapped">Zip the skin's files one level down inside a folder, the way an
        /// .osk exported from the parent directory looks.</param>
        private string makeOsk(string name, bool wrapped = false)
        {
            string build = Path.Combine(tmp, "build-" + name);
            string content = wrapped ? Path.Combine(build, "My Skin") : build;
            Directory.CreateDirectory(Path.Combine(content, "sounds"));

            File.WriteAllText(Path.Combine(content, "skin.ini"), "[General]\nName: " + name + "\nVersion: 2.5\n");
            File.WriteAllBytes(Path.Combine(content, "cursor.png"), new byte[] { 0x89, 0x50 });
            File.WriteAllBytes(Path.Combine(content, "sounds", "normal-hitnormal.wav"), new byte[] { 0x52 });

            string osk = Path.Combine(tmp, name + ".osk");
            ZipFile.CreateFromDirectory(build, osk);
            return osk;
        }

        [Test]
        public void ExtractsIntoANamedFolderUnderTheSkinsRoot()
        {
            string dir = SkinArchive.Extract(makeOsk("Aristia"), skinsRoot, "Aristia");

            Assert.That(Path.GetFileName(dir), Is.EqualTo("Aristia"));
            Assert.That(File.Exists(Path.Combine(dir, "skin.ini")), Is.True);
            Assert.That(File.Exists(Path.Combine(dir, "cursor.png")), Is.True);
            Assert.That(File.Exists(Path.Combine(dir, "sounds", "normal-hitnormal.wav")), Is.True, "subdirectories must survive extraction");
        }

        // An .osk zipped from the parent directory puts everything one level down; skin.ini and the
        // element textures have to end up at the folder ROOT or the skin resolves nothing.
        [Test]
        public void ASingleWrapperFolderIsHoistedAway()
        {
            string dir = SkinArchive.Extract(makeOsk("Wrapped", wrapped: true), skinsRoot, "Wrapped");

            Assert.That(File.Exists(Path.Combine(dir, "skin.ini")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(dir, "My Skin")), Is.False);
            Assert.That(File.Exists(Path.Combine(dir, "sounds", "normal-hitnormal.wav")), Is.True);
        }

        [Test]
        public void ReimportingReplacesTheExistingFolder()
        {
            string dir = SkinArchive.Extract(makeOsk("Replace"), skinsRoot, "Replace");
            File.WriteAllText(Path.Combine(dir, "stray.png"), "x");

            string again = SkinArchive.Extract(makeOsk("Replace2"), skinsRoot, "Replace");

            Assert.That(again, Is.EqualTo(dir));
            Assert.That(File.Exists(Path.Combine(again, "stray.png")), Is.False);
        }

        [Test]
        public void AnEmptyArchiveIsRejectedAndLeavesNothingBehind()
        {
            string build = Path.Combine(tmp, "build-empty");
            Directory.CreateDirectory(build);
            string osk = Path.Combine(tmp, "empty.osk");
            ZipFile.CreateFromDirectory(build, osk);

            Assert.That(() => SkinArchive.Extract(osk, skinsRoot, "Empty"), Throws.InstanceOf<InvalidDataException>());
            Assert.That(Directory.Exists(Path.Combine(skinsRoot, "Empty")), Is.False);
            Assert.That(Directory.EnumerateFileSystemEntries(skinsRoot), Is.Empty, "the staging directory must not be left behind");
        }

        [TestCase("Aristia", "Aristia")]
        [TestCase("- # Rafis 2018 -", "- # Rafis 2018 -")]
        [TestCase("a/b\\c", "a_b_c")]
        [TestCase("   ", "skin")]
        [TestCase("", "skin")]
        [TestCase("...", "skin")]
        public void NamesAreSanitisedIntoUsableFolderNames(string raw, string expected)
            => Assert.That(SkinArchive.SanitiseName(raw), Is.EqualTo(expected));

        // The imported skin's identity is persisted as a plain string setting, so it survives a
        // restart — the whole point of storing a folder name rather than holding a live skin.
        [Test]
        public void TheImportedSkinChoiceRoundTripsThroughConfigStorage()
        {
            var storage = new TemporaryNativeStorage(Path.Combine("jukebox-skin-config-test", Path.GetRandomFileName()));

            using (var config = new JukeBoxConfigManager(storage))
            {
                Assert.That(config.Get<string>(JukeBoxSetting.CustomSkinPath), Is.Empty, "nothing imported by default");

                config.SetValue(JukeBoxSetting.CustomSkinPath, "Aristia");
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Custom);
            }

            using (var reloaded = new JukeBoxConfigManager(storage))
            {
                Assert.That(reloaded.Get<string>(JukeBoxSetting.CustomSkinPath), Is.EqualTo("Aristia"));
                Assert.That(reloaded.Get<JukeBoxSkin>(JukeBoxSetting.Skin), Is.EqualTo(JukeBoxSkin.Custom));
            }
        }
    }
}
