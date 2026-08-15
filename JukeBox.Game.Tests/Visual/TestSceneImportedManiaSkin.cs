#nullable enable

using System.IO;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Platform;
using osu.Game.IO;
using osu.Game.Skinning;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// A user-imported .osk answering mania's own skin lookups. The fixture reproduces the shape of
    /// the skin in the user's report (StepOsu!Mania): a per-key-count <c>[Mania]</c> block for 4K,
    /// with every note and key image referenced by a Windows path whose capitalisation does not
    /// match the folder — <c>Arrownote\left</c> against <c>arrownote/left.png</c>.
    /// </summary>
    [TestFixture]
    public partial class TestSceneImportedManiaSkin : JukeBoxTestScene
    {
        [Resolved]
        private IStorageResourceProvider resources { get; set; } = null!;

        [Resolved]
        private GameHost host { get; set; } = null!;

        private string dir = null!;
        private ImportedLegacySkin skin = null!;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(dir, "arrownote"));
            Directory.CreateDirectory(Path.Combine(dir, "arrows"));

            foreach (string note in new[] { "left", "up", "down", "right", "holdbody", "holdcap" })
                File.WriteAllText(Path.Combine(dir, "arrownote", note + ".png"), note);

            foreach (string key in new[] { "key_left", "key_leftD", "key_up", "key_upD" })
                File.WriteAllText(Path.Combine(dir, "arrows", key + ".png"), key);

            // Written with CRLF and backslashes, exactly as osu! stable produces it.
            File.WriteAllText(Path.Combine(dir, "skin.ini"), string.Join("\r\n",
                "[General]",
                "Name: fixture",
                "Version: 2.1",
                "",
                "[Mania]",
                "Keys: 4",
                "ColumnStart: 300",
                "HitPosition: 466",
                "ScorePosition: 265",
                "ColumnWidth: 64,64,64,64",
                "NoteImage0: Arrownote\\left",
                "NoteImage0L: Arrownote\\holdbody",
                "NoteImage0T: Arrownote\\holdcap",
                "NoteImage1: Arrownote\\up",
                "NoteImage2: Arrownote\\down",
                "NoteImage3: Arrownote\\right",
                "KeyImage0: arrows\\key_left",
                "KeyImage0D: arrows\\key_leftD",
                ""));
        }

        // NOTE: deliberately NOT deleting `dir` — see TestScenePlaybackController for why. TestScene
        // runs queued AddStep bodies from a base-class teardown hook that fires AFTER this class's
        // own [TearDown], so a synchronous delete here races the fixture out from under steps that
        // have not run yet.

        private void createSkin() => AddStep("create skin", () => skin = new ImportedLegacySkin(dir, resources, host));

        private string? config(LegacyManiaSkinConfigurationLookups lookup, int? column = null)
            => skin.GetConfig<LegacyManiaSkinConfigurationLookup, string>(
                new LegacyManiaSkinConfigurationLookup(4, lookup, column))?.Value;

        private float? number(LegacyManiaSkinConfigurationLookups lookup, int? column = null)
            => skin.GetConfig<LegacyManiaSkinConfigurationLookup, float>(
                new LegacyManiaSkinConfigurationLookup(4, lookup, column))?.Value;

        // The per-key-count section has to be found and used — mania config is keyed by Keys:, and
        // a 4K map must read the 4K block rather than defaults.
        [Test]
        public void ManiaLookupsResolveToTheSkinsOwnValuesNotDefaults()
        {
            createSkin();

            // Compared against the values AFTER lazer's own stable-coordinate conversion — a hit
            // position is stored as (480 - y) * 1.6 and a score position as y * 1.6. What the
            // assertion is really pinning is that these came from the skin rather than the
            // defaults (a default hit position is 124.8, nowhere near this).
            AddAssert("hit position is the skin's 466", () => number(LegacyManiaSkinConfigurationLookups.HitPosition) == (480 - 466) * 1.6f);
            AddAssert("and not the default", () => number(LegacyManiaSkinConfigurationLookups.HitPosition) != LegacyManiaSkinConfiguration.DEFAULT_HIT_POSITION);
            AddAssert("score position is the skin's 265", () => number(LegacyManiaSkinConfigurationLookups.ScorePosition) == 265 * 1.6f);
            // Column widths get the same 1.6 scale on the way in.
            AddAssert("column width is the skin's 64", () => number(LegacyManiaSkinConfigurationLookups.ColumnWidth, 0) == 64 * 1.6f);
            AddAssert("and not the default", () => number(LegacyManiaSkinConfigurationLookups.ColumnWidth, 0) != LegacyManiaSkinConfiguration.DEFAULT_COLUMN_SIZE);
        }

        // The reported bug: every note image is referenced as "Arrownote\left" while the file is
        // "arrownote/left.png". The configuration carries the author's spelling — that part is
        // lazer's behaviour too — so what matters is that the name still resolves to a real file.
        [Test]
        public void EveryPerColumnNoteAndKeyImageResolvesToARealFile()
        {
            createSkin();

            AddAssert("note images are the skin's", () =>
                config(LegacyManiaSkinConfigurationLookups.NoteImage, 0) == @"Arrownote\left"
                && config(LegacyManiaSkinConfigurationLookups.NoteImage, 3) == @"Arrownote\right");

            AddAssert("key images are the skin's", () =>
                config(LegacyManiaSkinConfigurationLookups.KeyImage, 0) == @"arrows\key_left"
                && config(LegacyManiaSkinConfigurationLookups.KeyImageDown, 0) == @"arrows\key_leftD");

            // The part that was broken: those names have to reach actual bytes. Checked through the
            // same store the skin's textures are loaded from, since a texture needs a renderer.
            AddAssert("and each of them reaches a real file", () =>
            {
                using var store = new SkinFolderResourceStore(new NativeStorage(dir));
                store.AddExtension("png");

                return store.Get(@"Arrownote\left") != null
                       && store.Get(@"Arrownote\up") != null
                       && store.Get(@"Arrownote\down") != null
                       && store.Get(@"Arrownote\right") != null
                       && store.Get(@"Arrownote\holdbody") != null
                       && store.Get(@"Arrownote\holdcap") != null
                       && store.Get(@"arrows\key_left") != null
                       && store.Get(@"arrows\key_leftD") != null;
            });
        }

        // A legacy skin declaring "Version: 2.1" must report it: ruleset code branches on this to
        // choose between pre- and post-2.1 calibrations, and a skin that reports the decoder's 1.0
        // default silently gets the old behaviour everywhere.
        [Test]
        public void TheSkinsDeclaredVersionIsReported()
        {
            createSkin();

            AddAssert("version is 2.1", () => skin.GetConfig<SkinConfiguration.LegacySetting, decimal>(
                SkinConfiguration.LegacySetting.Version)?.Value == 2.1m);
        }
    }
}
