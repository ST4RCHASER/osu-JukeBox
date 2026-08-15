#nullable enable

using System.IO;
using System.Text;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Framework.IO.Stores;

namespace JukeBox.Game.Tests.LazerPlayer
{
    // A legacy skin.ini names its files the way osu! stable wrote them: Windows path separators and
    // whatever capitalisation the author typed, neither of which need match what is on disk. The
    // StepOsu!Mania skin in the user's report asks for "Arrownote\left" while shipping
    // "arrownote/left.png" — on a case-sensitive or non-Windows filesystem that resolves to nothing,
    // and mania then draws no notes at all.
    //
    // lazer never hits this because its skins are realm-backed and it looks files up through a
    // lowercased, separator-standardised map (RealmBackedResourceStore). Folder-backed skins need
    // the same treatment.
    [TestFixture]
    public class SkinFolderResourceStoreTest
    {
        private string tmp = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(tmp, "arrownote"));
            File.WriteAllText(Path.Combine(tmp, "arrownote", "left.png"), "left");
            File.WriteAllText(Path.Combine(tmp, "mania-note1.png"), "plain");
            Directory.CreateDirectory(Path.Combine(tmp, "Sub Dir"));
            File.WriteAllText(Path.Combine(tmp, "Sub Dir", "Mixed Case.png"), "mixed");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
        }

        private SkinFolderResourceStore store() => new SkinFolderResourceStore(new NativeStorage(tmp));

        private static string? text(byte[]? bytes) => bytes == null ? null : Encoding.UTF8.GetString(bytes);

        // The exact lookup the reported skin performs.
        [Test]
        public void ResolvesAWindowsPathWithMismatchedCase()
        {
            using var s = store();
            Assert.That(text(s.Get(@"Arrownote\left.png")), Is.EqualTo("left"));
        }

        // A skin.ini value carries no extension ("Arrownote\left"); the ".png" is appended above
        // this store, by whichever store is searching extensions (the texture store, in the real
        // pipeline). Normalisation therefore has to compose with that search rather than replace
        // it — which is why it hooks GetFilenames instead of Get.
        [Test]
        public void NormalisationComposesWithExtensionSearch()
        {
            using var s = store();
            s.AddExtension("png");

            Assert.That(text(s.Get(@"Arrownote\left")), Is.EqualTo("left"));
        }

        [Test]
        public void ResolvesForwardSlashesAndExactCaseToo()
        {
            using var s = store();
            Assert.That(text(s.Get("arrownote/left.png")), Is.EqualTo("left"));
            Assert.That(text(s.Get("mania-note1.png")), Is.EqualTo("plain"));
            Assert.That(text(s.Get(@"sub dir\mixed case.png")), Is.EqualTo("mixed"));
        }

        // A name that genuinely isn't there must still miss, or every fallback in the skin chain
        // would stop at this store.
        [Test]
        public void StillMissesWhatIsNotThere()
        {
            using var s = store();
            Assert.That(s.Get("no-such-file.png"), Is.Null);
            Assert.That(s.Get(@"Arrownote\right.png"), Is.Null);
        }

        // Streams go through the same path — the texture loader uses GetStream, not Get.
        [Test]
        public void StreamsResolveTheSameWay()
        {
            using var s = store();
            using var stream = s.GetStream(@"Arrownote\left.png");

            Assert.That(stream, Is.Not.Null);

            using var reader = new StreamReader(stream!);
            Assert.That(reader.ReadToEnd(), Is.EqualTo("left"));
        }
    }
}
