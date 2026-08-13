#nullable enable

using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Allocation;
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
            // change must land on a different concrete entry.
            for (int i = 0; i < 5; i++)
            {
                JukeBoxSkin before = default;
                AddStep("song change", () =>
                {
                    before = skins.Effective.Value;
                    skins.OnSongChanged();
                });
                AddAssert("re-rolled to a different concrete skin", () =>
                    skins.Effective.Value != JukeBoxSkin.Random && skins.Effective.Value != before);
            }

            AddStep("restore Argon", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon));
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
