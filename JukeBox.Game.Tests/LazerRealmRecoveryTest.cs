#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using osu.Framework.Platform;

namespace JukeBox.Game.Tests
{
    /// <summary>
    /// The lazer-side realm database is throwaway (default key bindings + regenerable ruleset
    /// configs), so a corrupt/unopenable file must never hard-crash startup (lazer's own known
    /// failure mode, ppy/osu#16441) — it gets deleted and recreated instead.
    /// </summary>
    [TestFixture]
    public class LazerRealmRecoveryTest
    {
        private string dir = null!;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }

        [Test]
        public void OpensCleanStorage()
        {
            using var realm = JukeBoxGameBase.CreateLazerRealmWithRecovery(new NativeStorage(dir), null);

            Assert.That(realm, Is.Not.Null);
            realm.Run(r => Assert.That(r.All<osu.Game.Input.Bindings.RealmKeyBinding>().Count(), Is.GreaterThanOrEqualTo(0)));
        }

        [Test]
        public void RecoversFromCorruptRealmFile()
        {
            // Plant garbage where the realm file lives — opening this must throw internally and
            // trigger the delete-and-retry path rather than propagating.
            File.WriteAllBytes(Path.Combine(dir, "client.realm"),
                Enumerable.Repeat(Encoding.ASCII.GetBytes("this is not a realm file "), 200).SelectMany(b => b).ToArray());

            using var realm = JukeBoxGameBase.CreateLazerRealmWithRecovery(new NativeStorage(dir), null);

            Assert.That(realm, Is.Not.Null);

            // The recovered database is functional...
            realm.Run(r => Assert.That(r.All<osu.Game.Input.Bindings.RealmKeyBinding>().Count(), Is.GreaterThanOrEqualTo(0)));

            // ...and really is a fresh realm file, not the planted garbage.
            byte[] head = new byte[24];
            using (var fs = File.OpenRead(Path.Combine(dir, "client.realm")))
                fs.ReadExactly(head);
            Assert.That(Encoding.ASCII.GetString(head), Does.Not.StartWith("this is not a realm file"));
        }

        [Test]
        public void SecondFailurePropagates()
        {
            // A storage that can't be written at all: point at a path whose parent is a FILE, so
            // both the initial open and the post-delete retry fail.
            string blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "file, not a directory");

            Assert.Throws(Is.InstanceOf<Exception>(),
                () => JukeBoxGameBase.CreateLazerRealmWithRecovery(new NativeStorage(Path.Combine(blocker, "sub")), null).Dispose());
        }
    }
}
