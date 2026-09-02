#nullable enable

using System.IO;
using System.IO.Compression;
using System.Linq;
using JukeBox.Game.Import;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;

namespace JukeBox.Game.Tests.LazerPlayer
{
    /// <summary>
    /// The imported-skin library: what a skin is CALLED (its own skin.ini, the way osu! names a
    /// skin, rather than the archive filename the folder happens to carry), and what the settings
    /// dropdown lists. Exercises the statics against real directories, so no game host is needed —
    /// which is also how <see cref="SkinSelection"/> reads the library when rolling a random skin.
    /// </summary>
    [TestFixture]
    public class SkinLibraryTest
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

        /// <param name="folder">The folder the skin is installed as — its identity.</param>
        /// <param name="declaredName">
        /// What its skin.ini declares. Null writes no skin.ini at all; empty writes one with no
        /// Name key.
        /// </param>
        private string install(string folder, string? declaredName)
        {
            string directory = Path.Combine(skinsRoot, folder);
            Directory.CreateDirectory(directory);

            if (declaredName != null)
            {
                string ini = declaredName.Length > 0
                    ? $"[General]\nName: {declaredName}\nVersion: 2.5\n"
                    : "[General]\nVersion: 2.5\n";

                File.WriteAllText(Path.Combine(directory, "skin.ini"), ini);
            }

            return directory;
        }

        [Test]
        public void TheNameComesFromSkinIniNotTheFolder()
        {
            string directory = install("aristia-v2-final", "Aristia");

            Assert.That(SkinLibrary.ReadDisplayName(directory), Is.EqualTo("Aristia"));
        }

        // Every fallback lands on the folder name, which is what the entry was called before skins
        // had names at all — so a skin that says nothing about itself is never worse off than it
        // used to be, and is never nameless.
        [TestCase(null, TestName = "NameFallsBackToTheFolder_NoSkinIni")]
        [TestCase("", TestName = "NameFallsBackToTheFolder_SkinIniWithoutAName")]
        [TestCase("   ", TestName = "NameFallsBackToTheFolder_BlankName")]
        public void NameFallsBackToTheFolder(string? declared)
        {
            string directory = install("Some Skin", declared);

            Assert.That(SkinLibrary.ReadDisplayName(directory), Is.EqualTo("Some Skin"));
        }

        [Test]
        public void EveryInstalledSkinIsListedUnderItsOwnName()
        {
            install("first-archive", "Rafis");
            install("second-archive", "Aristia");

            var listed = SkinLibrary.Scan(skinsRoot);

            Assert.That(listed.Select(s => s.Name), Is.EqualTo(new[] { "Aristia", "Rafis" }), "listed alphabetically by name");
            Assert.That(listed.Select(s => s.Folder), Is.EqualTo(new[] { "second-archive", "first-archive" }), "each keeps its own folder as its identity");
        }

        // Two skins may perfectly well declare the same name — they are different skins, and the
        // dropdown has to offer two rows a user can tell apart.
        [Test]
        public void SkinsSharingANameAreDisambiguatedInTheLabel()
        {
            install("aristia-2016", "Aristia");
            install("aristia-2019", "Aristia");
            install("solo", "Rafis");

            var listed = SkinLibrary.Scan(skinsRoot);

            Assert.That(listed.Select(s => s.Label), Is.EqualTo(new[] { "Aristia", "Aristia (2)", "Rafis" }));
            Assert.That(listed.Select(s => s.Name), Is.EqualTo(new[] { "Aristia", "Aristia", "Rafis" }), "the declared name itself is untouched");
            Assert.That(listed.Select(s => s.Folder).Distinct().Count(), Is.EqualTo(3), "and they stay separately selectable");
        }

        [Test]
        public void AnEmptyOrAbsentSkinsDirectoryListsNothing()
        {
            Assert.That(SkinLibrary.Scan(skinsRoot), Is.Empty, "no skins directory at all");

            Directory.CreateDirectory(skinsRoot);
            Assert.That(SkinLibrary.Scan(skinsRoot), Is.Empty, "created but empty");
        }

        // A staging folder is an import in flight or the wreckage of a failed one. Listing it would
        // offer the user a half-extracted skin.
        [Test]
        public void HalfExtractedStagingFoldersAreNotListed()
        {
            install("Real Skin", "Real Skin");
            install($"import-abc123{SkinArchive.STAGING_SUFFIX}", "Half A Skin");

            Assert.That(SkinLibrary.Scan(skinsRoot).Select(s => s.Name), Is.EqualTo(new[] { "Real Skin" }));
        }

        // Importing accumulates: a second .osk joins the first rather than replacing it, which is
        // the whole difference between a library and the single slot this used to be.
        [Test]
        public void ImportingASecondSkinAddsToTheLibraryRatherThanReplacingTheFirst()
        {
            SkinArchive.Extract(makeOsk("Aristia"), skinsRoot, "Aristia");
            SkinArchive.Extract(makeOsk("Rafis"), skinsRoot, "Rafis");

            Assert.That(SkinLibrary.Scan(skinsRoot).Select(s => s.Name), Is.EqualTo(new[] { "Aristia", "Rafis" }));
        }

        // ...but re-importing the SAME archive updates that one entry in place. The folder is named
        // after the archive, so the second extraction lands on the first one's folder.
        [Test]
        public void ReimportingTheSameArchiveUpdatesItsEntryInsteadOfAddingOne()
        {
            SkinArchive.Extract(makeOsk("Aristia", declaredName: "Aristia"), skinsRoot, "Aristia");
            SkinArchive.Extract(makeOsk("Aristia", declaredName: "Aristia Reborn"), skinsRoot, "Aristia");

            var listed = SkinLibrary.Scan(skinsRoot);

            Assert.That(listed.Count, Is.EqualTo(1), "one skin, imported twice");
            Assert.That(listed[0].Folder, Is.EqualTo("Aristia"), "the identity the selection persists is unchanged");
            Assert.That(listed[0].Name, Is.EqualTo("Aristia Reborn"), "and it now goes by the updated skin.ini name");
        }

        /// <param name="fileName">Both the archive's file name and, by default, the declared name.</param>
        private string makeOsk(string fileName, string? declaredName = null)
        {
            string build = Path.Combine(tmp, "build-" + Path.GetRandomFileName());
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "skin.ini"), $"[General]\nName: {declaredName ?? fileName}\nVersion: 2.5\n");
            File.WriteAllBytes(Path.Combine(build, "cursor.png"), new byte[] { 0x89, 0x50 });

            string osk = Path.Combine(tmp, Path.GetRandomFileName() + ".osk");
            ZipFile.CreateFromDirectory(build, osk);
            return osk;
        }
    }
}
