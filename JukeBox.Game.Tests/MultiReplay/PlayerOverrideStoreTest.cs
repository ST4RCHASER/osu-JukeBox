#nullable enable

using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osuTK.Graphics;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The per-player override store: one player's mods, colour and skin, keyed by the replay, kept
    /// strictly apart from every other player's. The whole reason this type exists is that the shared
    /// Chart-tab selection put one player's mods on everyone — so the property under test is that a
    /// write to one replay is invisible to the next.
    /// </summary>
    [TestFixture]
    public class PlayerOverrideStoreTest
    {
        private static ReplayAttachment replay(string name) => new ReplayAttachment { PlayerName = name };

        [Test]
        public void AnUntouchedPlayerHasNoOverrideAndFallsBackToTheGivenDefaults()
        {
            var store = new PlayerOverrideStore();
            var a = replay("a");

            // Peek must not conjure one — the render path calls it every frame and an untouched
            // player must allocate nothing and read as "no override".
            Assert.That(store.Peek(a), Is.Null);

            var recorded = new Mod[] { new OsuModHidden() };
            Assert.That(store.EffectiveMods(a, recorded), Is.SameAs(recorded));
            Assert.That(store.EffectiveCursorColour(a, Color4.Red), Is.EqualTo(Color4.Red));
        }

        [Test]
        public void SettingOnePlayersModsLeavesEveryOtherPlayerOnTheirRecordedMods()
        {
            var store = new PlayerOverrideStore();
            var a = replay("a");
            var b = replay("b");

            var recordedA = new Mod[] { new OsuModHidden() };
            var recordedB = new Mod[] { new OsuModHardRock() };

            store.SetMods(a, new Mod[] { new OsuModDoubleTime() });

            // a takes the override; b — never written — still reads its own recorded set, not a's.
            Assert.That(store.EffectiveMods(a, recordedA).Select(m => m.Acronym), Is.EqualTo(new[] { "DT" }));
            Assert.That(store.EffectiveMods(b, recordedB), Is.SameAs(recordedB));
        }

        [Test]
        public void EachOverrideAxisIsIndependentAndClearableBackToTheFallback()
        {
            var store = new PlayerOverrideStore();
            var a = replay("a");

            store.SetCursorColour(a, Color4.Lime);
            store.SetMods(a, new Mod[] { new OsuModHardRock() });

            Assert.That(store.EffectiveCursorColour(a, Color4.Red), Is.EqualTo(Color4.Lime));
            Assert.That(store.EffectiveMods(a, new Mod[] { new OsuModHidden() }).Select(m => m.Acronym), Is.EqualTo(new[] { "HR" }));

            // Clearing the colour must NOT wipe the mods — the axes are separate.
            store.SetCursorColour(a, null);
            Assert.That(store.EffectiveCursorColour(a, Color4.Red), Is.EqualTo(Color4.Red));
            Assert.That(store.EffectiveMods(a, new Mod[] { new OsuModHidden() }).Select(m => m.Acronym), Is.EqualTo(new[] { "HR" }));
        }

        [Test]
        public void ChangingAnOverrideAnnouncesTheReplayAndTheKind()
        {
            var store = new PlayerOverrideStore();
            var a = replay("a");

            var seen = new List<(ReplayAttachment, PlayerOverrideKind)>();
            store.Changed += (r, k) => seen.Add((r, k));

            store.SetCursorColour(a, Color4.Lime);
            store.SetMods(a, new Mod[] { new OsuModHardRock() });
            store.SetSkin(a, "argon");

            Assert.That(seen, Is.EqualTo(new[]
            {
                (a, PlayerOverrideKind.Colour),
                (a, PlayerOverrideKind.Mods),
                (a, PlayerOverrideKind.Skin),
            }));
        }
    }
}
