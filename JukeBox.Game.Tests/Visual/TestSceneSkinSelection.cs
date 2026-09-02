#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Platform;
using osu.Game.IO;
using osu.Game.Skinning;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The skin-selection service: concrete choices pass straight through to
    /// <see cref="SkinSelection.Effective"/>, Random resolves to a concrete skin (never Random
    /// itself) and re-rolls on song change, and every choice maps to the right constructible
    /// lazer skin. Uses the runner game's own cached service — the same instance the real app's
    /// chart layer consumes.
    /// </summary>
    [TestFixture]
    public partial class TestSceneSkinSelection : JukeBoxTestScene
    {
        [Resolved]
        private SkinSelection skins { get; set; } = null!;

        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        [Resolved]
        private IStorageResourceProvider resources { get; set; } = null!;

        [Test]
        public void ConcreteChoicePassesThrough()
        {
            AddStep("choose Triangles", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Triangles));
            AddAssert("effective is Triangles", () => skins.Effective.Value == JukeBoxSkin.Triangles);

            AddStep("choose Classic", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Classic));
            AddAssert("effective is Classic", () => skins.Effective.Value == JukeBoxSkin.Classic);

            AddStep("restore Argon", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon));
        }

        [Test]
        public void RandomResolvesConcreteAndRerollsPerSong()
        {
            AddStep("choose Random", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Random));
            AddAssert("effective is concrete", () => skins.Effective.Value != JukeBoxSkin.Random);

            // The re-roll deliberately never repeats the current skin, so every simulated song
            // change must land on a different entry. Compared as a (skin, folder) PAIR, since two
            // different imported skins are both Custom and only the folder tells them apart.
            for (int i = 0; i < 5; i++)
            {
                (JukeBoxSkin skin, string folder) before = default;
                AddStep("song change", () =>
                {
                    before = (skins.Effective.Value, skins.EffectiveCustomFolder.Value);
                    skins.OnSongChanged();
                });
                AddAssert("re-rolled to a different concrete skin", () =>
                    skins.Effective.Value != JukeBoxSkin.Random
                    && (skins.Effective.Value, skins.EffectiveCustomFolder.Value) != before);
            }

            AddStep("restore Argon", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon));
        }

        /// <summary>
        /// Random draws from the whole library — the bundled skins AND every import — rather than
        /// only the four that shipped. Statistical, but not flaky: with four imports the pool is
        /// half bundled and half imported, so 40 rolls missing either side entirely is a ~1-in-10^12
        /// event.
        /// </summary>
        [Test]
        public void RandomAlsoRollsImportedSkins()
        {
            AddStep("install four skins", () =>
            {
                foreach (string folder in installed)
                {
                    string directory = Path.Combine(skinsRoot, folder);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "skin.ini"), $"[General]\nName: {folder}\nVersion: 2.5\n");
                }

                installedAny = true;
            });

            var rolled = new List<(JukeBoxSkin skin, string folder)>();

            AddStep("choose Random and roll 40 songs", () =>
            {
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Random);
                rolled.Add((skins.Effective.Value, skins.EffectiveCustomFolder.Value));

                for (int i = 0; i < 40; i++)
                {
                    skins.OnSongChanged();
                    rolled.Add((skins.Effective.Value, skins.EffectiveCustomFolder.Value));
                }
            });

            AddAssert("imported skins came up", () => rolled.Any(r => r.skin == JukeBoxSkin.Custom));
            AddAssert("bundled skins came up too", () => rolled.Any(r => r.skin != JukeBoxSkin.Custom));
            AddAssert("every imported roll names an installed skin", () => rolled
                .Where(r => r.skin == JukeBoxSkin.Custom)
                .All(r => installed.Contains(r.folder)));
            AddAssert("and a bundled roll never carries a folder", () => rolled
                .Where(r => r.skin != JukeBoxSkin.Custom)
                .All(r => r.folder.Length == 0));

            AddStep("restore Argon", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon));
        }

        /// <summary>
        /// A skin folder vanishing under a running process — which is exactly what the Maintenance
        /// section does to the detached viewer, since both processes read the same skins directory.
        /// It must degrade, not throw: the viewer re-resolves on its next song and shows a bundled
        /// skin rather than falling over.
        /// </summary>
        [Test]
        public void ASelectedSkinDeletedFromUnderUsDegradesInsteadOfThrowing()
        {
            AddStep("install and select a skin", () =>
            {
                string directory = Path.Combine(skinsRoot, installed[0]);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "skin.ini"), "[General]\nName: Doomed\nVersion: 2.5\n");
                installedAny = true;

                config.SetValue(JukeBoxSetting.CustomSkinPath, installed[0]);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Custom);
            });

            AddAssert("it resolves while it exists", () => skins.CustomSkinDirectory != null);

            AddStep("delete it from under the app", () => Directory.Delete(Path.Combine(skinsRoot, installed[0]), true));

            AddAssert("the directory now resolves to nothing", () => skins.CustomSkinDirectory == null);
            AddAssert("and building a skin falls back rather than throwing", () =>
            {
                using var skin = skins.CreateEffectiveSkin(resources);
                return skin is ArgonSkin;
            });

            AddStep("restore Argon", () =>
            {
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
                config.SetValue(JukeBoxSetting.CustomSkinPath, string.Empty);
            });
        }

        private static readonly string[] installed = { "roll-a", "roll-b", "roll-c", "roll-d" };

        [Resolved]
        private GameHost host { get; set; } = null!;

        private string skinsRoot => host.Storage.GetFullPath(SkinLibrary.STORAGE_DIRECTORY);

        private bool installedAny;

        // The skins directory is the runner game's real storage, shared with every other test in
        // this assembly — so the installs above are torn down whatever the test did, rather than
        // left to widen some other fixture's random pool. Skipped when nothing was installed:
        // TearDown also runs for TestConstructor, which never loads the scene and so never gets a
        // host to ask for the storage path.
        [TearDown]
        public void RemoveInstalledSkins()
        {
            if (!installedAny)
                return;

            installedAny = false;

            foreach (string folder in installed)
            {
                string directory = Path.Combine(skinsRoot, folder);

                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        [Test]
        public void EveryChoiceConstructsItsSkin()
        {
            assertSkinType<ArgonSkin>(JukeBoxSkin.Argon);
            assertSkinType<ArgonProSkin>(JukeBoxSkin.ArgonPro);
            assertSkinType<TrianglesSkin>(JukeBoxSkin.Triangles);
            assertSkinType<DefaultLegacySkin>(JukeBoxSkin.Classic);
        }

        private void assertSkinType<T>(JukeBoxSkin choice) where T : Skin
        {
            AddAssert($"{choice} builds {typeof(T).Name}", () =>
            {
                using var skin = SkinSelection.CreateSkin(choice, resources);
                // ArgonProSkin derives from ArgonSkin — exact type equality keeps the mapping honest.
                return skin.GetType() == typeof(T);
            });
        }
    }
}
