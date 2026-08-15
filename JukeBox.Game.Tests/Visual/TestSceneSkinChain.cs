#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Skinning;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The ORDER of the chart's skin chain, which decides which skin answers a lookup and has been
    /// the cause of two separate visual bugs this session. Asserted against the composed chain the
    /// layer actually built (<see cref="LazerChartLayer.SkinChain"/>) rather than against a
    /// re-derivation of the intended order, which would keep passing while the real chain drifted.
    /// </summary>
    [TestFixture]
    public partial class TestSceneSkinChain : JukeBoxTestScene
    {
        private JukeBoxConfigManager config = null!;
        private SkinSelection skinSelection = null!;
        private Container layerHost = null!;

        private readonly ManualClock manual = new ManualClock();

        private string dir = null!;
        private LazerChartLayer layer = null!;

        // Own config (ini in temp storage) and skin service bound to it, so choosing skins here
        // never reaches the developer's real settings.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-skin-chain-test", Path.GetRandomFileName())));
            skinSelection = new SkinSelection();

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            deps.Cache(skinSelection);
            return deps;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Add(skinSelection);
            Add(layerHost = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset", () =>
            {
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
                layerHost.Clear();
            });
        }

        /// <summary>
        /// Classic is the final legacy-name fallback under every non-legacy selection: a beatmap or
        /// skin asking for a legacy element the user's skin has no answer for must land on the
        /// classic look, never on Argon's or Triangles' completely different one.
        /// </summary>
        [TestCase(JukeBoxSkin.Argon)]
        [TestCase(JukeBoxSkin.ArgonPro)]
        [TestCase(JukeBoxSkin.Triangles)]
        public void ANonLegacySelectionFallsBackToClassic(JukeBoxSkin skin)
        {
            selectSkin(skin);
            buildLayer();

            // The beatmap's own folder skin is always the first link — it is built for every
            // beatmap and simply answers nothing when the folder holds no skin files — so the
            // user's selection leads the SKINS, not the chain.
            AddAssert("the selection leads the user skins", () => indexOf(expectedType(skin)) == indexOf(typeof(BeatmapFolderSkin)) + 1);

            AddAssert("with the classic legacy skin behind it", () => indexOf(typeof(DefaultLegacySkin)) >= 0);

            AddAssert("and classic comes AFTER the selection, not in front of it",
                () => indexOf(typeof(DefaultLegacySkin)) > indexOf(expectedType(skin)));
        }

        /// <summary>Selecting Classic itself must not stack a second copy of it — it is already the
        /// selection, so there is nothing to fall back to.</summary>
        [Test]
        public void SelectingClassicDoesNotAppendADuplicate()
        {
            selectSkin(JukeBoxSkin.Classic);
            buildLayer();

            AddAssert("exactly one classic skin in the chain",
                () => chain().Count(s => s is DefaultLegacySkin) == 1);

            AddAssert("and it is the selection itself, leading the user skins",
                () => indexOf(typeof(DefaultLegacySkin)) == indexOf(typeof(BeatmapFolderSkin)) + 1);
        }

        /// <summary>
        /// The ordering that caused the mania geometry bug: the beatmap's own skin sits ABOVE the
        /// user's, so a beatmap that ships an element overrides the user's skin for it — and, just
        /// as importantly, a beatmap that ships nothing for a lookup falls through to the user's
        /// skin rather than the other way round.
        /// </summary>
        [Test]
        public void TheBeatmapSkinSitsAboveTheUserSkin()
        {
            selectSkin(JukeBoxSkin.Argon);
            buildLayer(withBeatmapSkin: true);

            AddAssert("the beatmap folder skin is in the chain", () => chain().Any(s => s is BeatmapFolderSkin));

            AddAssert("and it is ahead of the user's selection", () =>
            {
                int beatmapSkin = Array.FindIndex(chain(), s => s is BeatmapFolderSkin);
                int userSkin = Array.FindIndex(chain(), s => s.GetType() == expectedType(JukeBoxSkin.Argon));

                return beatmapSkin >= 0 && userSkin >= 0 && beatmapSkin < userSkin;
            });
        }

        /// <summary>
        /// A beatmap providing only a PARTIAL selection of legacy elements gets classic inserted at
        /// the same priority as itself, so whatever it doesn't cover reads as the classic look
        /// rather than as the user's non-legacy skin — lazer's own BeatmapSkinProvidingContainer
        /// semantics. Observable as classic appearing ahead of the user's selection, which is not
        /// where the plain legacy-fallback copy sits.
        /// </summary>
        [Test]
        public void APartialLegacyBeatmapSkinGetsClassicAtItsOwnPriority()
        {
            selectSkin(JukeBoxSkin.Argon);
            buildLayer(withBeatmapSkin: true, legacyBeatmapSkin: true);

            AddAssert("classic is inserted ahead of the user's selection", () =>
            {
                int classic = Array.FindIndex(chain(), s => s is DefaultLegacySkin);
                int userSkin = Array.FindIndex(chain(), s => s.GetType() == expectedType(JukeBoxSkin.Argon));

                return classic >= 0 && userSkin >= 0 && classic < userSkin;
            });

            AddAssert("and still right behind the beatmap's own skin", () =>
            {
                int beatmapSkin = Array.FindIndex(chain(), s => s is BeatmapFolderSkin);
                int classic = Array.FindIndex(chain(), s => s is DefaultLegacySkin);

                return beatmapSkin >= 0 && classic == beatmapSkin + 1;
            });
        }

        /// <summary>
        /// A skin change rebuilds the chain rather than mutating it — the chain is composed once per
        /// layer — so the new selection must come out at the front of a freshly built one.
        /// </summary>
        [Test]
        public void SwitchingSkinMidSongRebuildsTheChainInTheRightOrder()
        {
            selectSkin(JukeBoxSkin.Argon);
            buildLayer();

            AddAssert("argon leads the user skins",
                () => indexOf(expectedType(JukeBoxSkin.Argon)) == indexOf(typeof(BeatmapFolderSkin)) + 1);

            AddStep("switch to Triangles", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Triangles));
            AddUntilStep("selection resolved", () => skinSelection.Effective.Value == JukeBoxSkin.Triangles);

            AddStep("rebuild the layer", () =>
            {
                layerHost.Clear();
                buildLayerNow();
            });

            AddUntilStep("rebuilt", () => layer.IsLoaded && layer.SkinChain.Count > 0);

            AddAssert("triangles leads the user skins now",
                () => indexOf(expectedType(JukeBoxSkin.Triangles)) == indexOf(typeof(BeatmapFolderSkin)) + 1);
            AddAssert("with classic still behind it",
                () => indexOf(typeof(DefaultLegacySkin)) > indexOf(expectedType(JukeBoxSkin.Triangles)));
            AddAssert("and no argon left over", () => indexOf(expectedType(JukeBoxSkin.Argon)) < 0);
        }

        /// <summary>The ruleset's own bundled resources are the last resort, behind every skin.</summary>
        [Test]
        public void TheRulesetsOwnResourcesAreLast()
        {
            selectSkin(JukeBoxSkin.Argon);
            buildLayer();

            AddAssert("a resource-store skin is at the very back",
                () => chain()[^1] is ResourceStoreBackedSkin);
        }

        private static Type expectedType(JukeBoxSkin skin) => skin switch
        {
            JukeBoxSkin.Argon => typeof(ArgonSkin),
            JukeBoxSkin.ArgonPro => typeof(ArgonProSkin),
            JukeBoxSkin.Triangles => typeof(TrianglesSkin),
            _ => typeof(DefaultLegacySkin),
        };

        private ISkin[] chain() => layer.UnwrappedSkinChain.ToArray();

        /// <summary>Where a skin type sits in the composed chain, or -1 when it isn't in it.</summary>
        private int indexOf(Type skinType) => Array.FindIndex(chain(), s => s.GetType() == skinType);

        private void selectSkin(JukeBoxSkin skin)
        {
            AddStep($"select {skin}", () => config.SetValue(JukeBoxSetting.Skin, skin));
            AddUntilStep("selection resolved", () => skinSelection.Effective.Value == skin);
        }

        private void buildLayer(bool withBeatmapSkin = false, bool legacyBeatmapSkin = false)
        {
            AddStep("build the chart layer", () => buildLayerNow(withBeatmapSkin, legacyBeatmapSkin));
            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.SkinChain.Count > 0);
            AddStep("report the chain", () => osu.Framework.Logging.Logger.Log(
                $"[skin-chain] {string.Join(" -> ", chain().Select(s => s.GetType().Name))}"));
        }

        private void buildLayerNow(bool withBeatmapSkin = false, bool legacyBeatmapSkin = false)
        {
            manual.CurrentTime = 0;

            string mapDir = Path.Combine(dir, withBeatmapSkin ? (legacyBeatmapSkin ? "legacy-skin" : "plain-skin") : "no-skin");
            Directory.CreateDirectory(mapDir);

            string osu = Path.Combine(mapDir, "chain [0].osu");
            File.WriteAllText(osu,
                "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n"
                + "[Metadata]\nTitle:test\nArtist:test\nCreator:test\nVersion:test\n\n"
                + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
                + "[TimingPoints]\n0,500,4,1,0,100,1,0\n\n"
                + "[HitObjects]\n64,192,1000,1,0\n192,192,1500,1,0\n");

            if (withBeatmapSkin)
            {
                // A skin.ini alone makes the folder a beatmap skin; a legacy ELEMENT is what makes it
                // claim to provide legacy resources, which is the partial-legacy case.
                File.WriteAllText(Path.Combine(mapDir, "skin.ini"), "[General]\nName: beatmap\nVersion: 2.5\n");

                if (legacyBeatmapSkin)
                    File.WriteAllBytes(Path.Combine(mapDir, "hitcircle.png"), solidPng());
            }

            layerHost.Child = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Clock = new FramedClock(manual),
                Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu),
            };
        }

        /// <summary>The smallest valid PNG — enough for a skin to count as providing an element.</summary>
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    }
}
