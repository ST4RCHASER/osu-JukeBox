#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Platform;
using osu.Game.Audio;
using osu.Game.IO;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Skinning;
using osuTK.Graphics;
using SixLabors.ImageSharp;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The beatmap folder acting as a legacy skin. lazer's realm-backed <c>LegacyBeatmapSkin</c> is
    /// not merely a <see cref="LegacySkin"/> pointed at a beatmap — it turns off or redirects a
    /// specific set of <see cref="LegacySkin"/> behaviours that are right for a USER skin and wrong
    /// for a beatmap. <see cref="BeatmapFolderSkin"/> is our standalone equivalent and has to carry
    /// the same set; each test here pins one of them by its effect, not by reading the flag back.
    /// </summary>
    [TestFixture]
    public partial class TestSceneBeatmapFolderSkin : JukeBoxTestScene
    {
        [Resolved]
        private IStorageResourceProvider resources { get; set; } = null!;

        [Resolved]
        private GameHost host { get; set; } = null!;

        private string dir = null!;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

        // NOTE: deliberately NOT deleting `dir` — see TestSceneImportedManiaSkin for why. TestScene
        // runs queued AddStep bodies from a base-class teardown hook that fires AFTER this class's
        // own [TearDown], so a synchronous delete here races steps that have not run yet.

        /// <param name="colours">Optional [Colours] section lines, e.g. "Combo1 : 255,0,0".</param>
        private BeatmapFolderSkin createSkin(params string[] colours)
        {
            var osu = new StringBuilder("osu file format v14\n\n[General]\nAudioFilename: audio.mp3\n\n[Metadata]\nVersion:x\n");

            if (colours.Length > 0)
            {
                osu.Append("\n[Colours]\n");
                foreach (string line in colours)
                    osu.Append(line).Append('\n');
            }

            string path = Path.Combine(dir, "map.osu");
            File.WriteAllText(path, osu.ToString());
            return new BeatmapFolderSkin(path, resources, host);
        }

        // Stable's per-hitobject "custom sample set" index 2+ becomes a hitsound Suffix, and the
        // numbered file lives in the BEATMAP folder. LegacySkin's default (UseCustomSampleBanks =
        // false) filters every suffixed candidate name out of the lookup, so the request for sample
        // set 2 was answered with the unnumbered file instead.
        [Test]
        public void ACustomSampleBankResolvesToTheBeatmapsNumberedFileNotItsPlainOne()
        {
            BeatmapFolderSkin skin = null!;
            string? resolved = null;

            AddStep("write both a plain and a numbered sample", () =>
            {
                writeSilentWav(Path.Combine(dir, "soft-hitnormal.wav"));
                writeSilentWav(Path.Combine(dir, "soft-hitnormal2.wav"));
                skin = createSkin();
            });

            AddStep("ask for sample set 2", () => resolved = skin.GetSample(
                new HitSampleInfo(HitSampleInfo.HIT_NORMAL, HitSampleInfo.BANK_SOFT, suffix: "2", useBeatmapSamples: true))?.Name);

            AddAssert("a sample resolved", () => resolved != null);
            AddAssert("and it is the numbered one", () => resolved!.Contains("soft-hitnormal2", StringComparison.Ordinal));
        }

        // The other half of the same behaviour: sample set 0/1 carry no suffix and must still find
        // the plain file, so the fix above must not have inverted the filter.
        [Test]
        public void AnUnnumberedSampleStillResolvesToThePlainFile()
        {
            BeatmapFolderSkin skin = null!;
            string? resolved = null;

            AddStep("write a plain sample", () =>
            {
                writeSilentWav(Path.Combine(dir, "soft-hitnormal.wav"));
                skin = createSkin();
            });

            AddStep("ask with no suffix", () => resolved = skin.GetSample(
                new HitSampleInfo(HitSampleInfo.HIT_NORMAL, HitSampleInfo.BANK_SOFT, useBeatmapSamples: true))?.Name);

            AddAssert("the plain file resolved", () => resolved != null && resolved.Contains("soft-hitnormal", StringComparison.Ordinal));
        }

        // UseBeatmapSamples is stable's "custom sample set index >= 1" — the map explicitly asking
        // for its own hitsounds. False means it wants the USER's skin sounds, and this skin outranks
        // the user's, so it has to decline rather than serve a same-named file that merely happens to
        // sit in the beatmap folder. Mappers routinely ship a full hitsound set alongside objects
        // that don't use it.
        [Test]
        public void ASampleTheMapDidNotAskForIsDeclinedSoTheUserSkinIsReached()
        {
            BeatmapFolderSkin skin = null!;

            AddStep("write a sample the map does not ask for", () =>
            {
                writeSilentWav(Path.Combine(dir, "soft-hitnormal.wav"));
                skin = createSkin();
            });

            AddAssert("the beatmap skin declines it", () => skin.GetSample(
                new HitSampleInfo(HitSampleInfo.HIT_NORMAL, HitSampleInfo.BANK_SOFT, useBeatmapSamples: false)) == null);

            // The same file, once the map does ask, still resolves — the gate is on the request, not
            // on the folder's contents.
            AddAssert("but serves it once the map asks", () => skin.GetSample(
                new HitSampleInfo(HitSampleInfo.HIT_NORMAL, HitSampleInfo.BANK_SOFT, useBeatmapSamples: true)) != null);
        }

        // The gate above is only correct if real beatmap data actually opens it. The hitsounds the
        // app plays are not hand-built HitSampleInfos — they come out of lazer's own beatmap decoder,
        // which produces LegacyHitSampleInfo with UseBeatmapSamples set from the hitobject's custom
        // sample-set index. This decodes a real .osu to confirm the two ends agree: index 0 (the
        // map wants skin sounds) stays closed, index 2 (the map wants its own) opens and carries the
        // suffix. Without this, a mistake in that mapping would silence beatmap hitsounds app-wide
        // while every other test here still passed.
        [Test]
        public void RealDecodedHitsoundsDriveTheGateBothWays()
        {
            BeatmapFolderSkin skin = null!;
            IList<HitSampleInfo>? plain = null;
            IList<HitSampleInfo>? custom = null;

            AddStep("decode a map with one plain and one custom-sample-set object", () =>
            {
                // BOTH files have to exist. If only the numbered one did, the plain object would
                // resolve to nothing whether or not the gate is doing its job, and the assertion
                // below would pass for the wrong reason.
                writeSilentWav(Path.Combine(dir, "soft-hitnormal.wav"));
                writeSilentWav(Path.Combine(dir, "soft-hitnormal2.wav"));

                string path = Path.Combine(dir, "samples.osu");
                File.WriteAllText(path, string.Join("\n",
                    "osu file format v14",
                    "",
                    "[General]",
                    "AudioFilename: audio.mp3",
                    "Mode: 0",
                    "",
                    "[TimingPoints]",
                    // Sample set 2 (soft), custom sample bank 0 — objects use the SKIN's sounds.
                    "0,500,4,2,0,100,1,0",
                    // Same, but custom sample bank 2 — objects use the BEATMAP's numbered sounds.
                    "1000,-100,4,2,2,100,0,0",
                    "",
                    "[HitObjects]",
                    "64,192,0,1,0",
                    "64,192,1000,1,0",
                    ""));

                using var stream = File.OpenRead(path);
                using var reader = new LineBufferedReader(stream);
                var beatmap = osu.Game.Beatmaps.Formats.Decoder.GetDecoder<osu.Game.Beatmaps.Beatmap>(reader).Decode(reader);

                plain = beatmap.HitObjects[0].Samples;
                custom = beatmap.HitObjects[1].Samples;

                skin = createSkin();
            });

            AddAssert("the plain object asks for skin sounds", () => plain!.All(s => !s.UseBeatmapSamples));
            AddAssert("so this skin declines them", () => plain!.All(s => skin.GetSample(s) == null));

            AddAssert("the custom-bank object asks for the beatmap's own", () => custom!.Any(s => s.UseBeatmapSamples && s.Suffix == "2"));
            AddAssert("and gets the numbered file", () => custom!
                .Where(s => s.Name == HitSampleInfo.HIT_NORMAL)
                .All(s => skin.GetSample(s)?.Name.Contains("soft-hitnormal2", StringComparison.Ordinal) == true));
        }

        // Storyboard Sample events arrive as StoryboardSampleInfo (an ISampleInfo that is NOT a
        // HitSampleInfo). They must stay unaffected by the gate above: the beatmap folder is the only
        // place a storyboard's audio ever lives, so declining would silence storyboards outright.
        [Test]
        public void StoryboardSamplesAreNotGatedByTheHitsoundOptOut()
        {
            BeatmapFolderSkin skin = null!;

            AddStep("write a storyboard sample", () =>
            {
                writeSilentWav(Path.Combine(dir, "key.wav"));
                skin = createSkin();
            });

            AddAssert("it resolves from the beatmap folder",
                () => skin.GetSample(new osu.Game.Storyboards.StoryboardSampleInfo(
                    osu.Game.Storyboards.StoryboardElementSource.Beatmap, "key.wav", 0, 100)) != null);
        }

        // "@2x" is a SKIN convention, not a beatmap one: to a beatmap folder, "foo@2x.png" is a file
        // literally named that, not a high-resolution variant of "foo". LegacySkin's default probes
        // for the sibling anyway and, on a hit, answers the lookup at half size — which also stops
        // the request falling through to the user's skin, where stable would have found it.
        [Test]
        public void AnAt2xFileIsNotTreatedAsAHighResolutionVariant()
        {
            BeatmapFolderSkin skin = null!;

            AddStep("write only an @2x file", () =>
            {
                File.WriteAllBytes(Path.Combine(dir, "hitcircle@2x.png"), solidPng(128, 128));
                skin = createSkin();
            });

            // The load-bearing half: declining is what lets the user's skin answer instead.
            AddAssert("the beatmap skin declines the un-suffixed lookup", () => skin.GetTexture("hitcircle") == null);

            // ...and the file is still reachable by the name it actually has.
            AddAssert("the file is still reachable by its real name", () => skin.GetTexture("hitcircle@2x") != null);
        }

        // With both sizes present the DISPLAYED size is 128/2 = 64 either way, so displayed size
        // cannot tell the two behaviours apart (an earlier test in TestSceneBeatmapVisuals asserts
        // exactly that and passes under both). The raw texture is what discriminates: lazer's beatmap
        // skin serves the 64px asset untouched, never the 128px one at ScaleAdjust 2.
        [Test]
        public void WithBothSizesPresentThePlainFileIsServedUnscaled()
        {
            BeatmapFolderSkin skin = null!;

            AddStep("write a 64px file and a 128px @2x sibling", () =>
            {
                File.WriteAllBytes(Path.Combine(dir, "hitcircle.png"), solidPng(64, 64));
                File.WriteAllBytes(Path.Combine(dir, "hitcircle@2x.png"), solidPng(128, 128));
                skin = createSkin();
            });

            AddAssert("the 64px asset is what came back", () => skin.GetTexture("hitcircle")?.Width == 64);
            AddAssert("at no scale adjustment", () => skin.GetTexture("hitcircle")?.ScaleAdjust == 1);
        }

        // Combo colours are indexed twice over: ComboIndex counts combos, ComboIndexWithOffsets also
        // applies the mapper's per-object colour SKIP. Lookups arrive carrying the former, and
        // ComboIndexWithOffsets' own documentation says it is what a beatmap skin must use. Without
        // the substitution every combo after the first skip wears the wrong colour.
        [Test]
        public void ComboColoursFollowTheMappersSkipsNotThePlainComboCount()
        {
            BeatmapFolderSkin skin = null!;
            Color4? colour = null;

            AddStep("create a skin with three combo colours", () => skin = createSkin(
                "Combo1 : 255,0,0",
                "Combo2 : 0,255,0",
                "Combo3 : 0,0,255"));

            AddStep("look up a combo that skipped a colour", () =>
            {
                // Second combo (index 1) with a one-colour skip: offsets index lands on 2.
                var combo = new HitCircle { ComboIndex = 1, ComboIndexWithOffsets = 2 };
                colour = skin.GetConfig<SkinComboColourLookup, Color4>(new SkinComboColourLookup(combo.ComboIndex, combo))?.Value;
            });

            AddAssert("the skipped-to colour is used", () => colour == new Color4(0, 0, 255, 255));
            AddAssert("not the one the plain index points at", () => colour != new Color4(0, 255, 0, 255));
        }

        // LegacySkin answers a HUD lookup with a whole default legacy component set rather than
        // declining, and this skin outranks the user's — so a beatmap with no legacy score font would
        // substitute that default HUD for the user skin's own on every map.
        //
        // Parity coverage only: JukeBox renders a DrawableRuleset with no HUD, so nothing in the app
        // issues this lookup today. The test drives it directly rather than through the app, and
        // therefore does NOT demonstrate a user-visible effect — it exists so that adding a HUD later
        // cannot quietly reintroduce the divergence.
        [Test]
        public void AFontlessBeatmapSkinSuppliesNoHudComponents()
        {
            BeatmapFolderSkin skin = null!;

            AddStep("create a skin with no score font", () => skin = createSkin());

            AddAssert("it declines the HUD lookup", () => skin.GetDrawableComponent(
                new GlobalSkinnableContainerLookup(GlobalSkinnableContainers.MainHUDComponents)) == null);
        }

        // See TestSceneFileDrop.writeSilentWav: BASS plays WAV directly, so a 44-byte RIFF header
        // followed by silence is enough for the sample store to return a real sample.
        private static void writeSilentWav(string path)
        {
            const int sample_rate = 44100;
            const short channels = 1;
            const short bits_per_sample = 16;
            const int data_size = sample_rate * channels * (bits_per_sample / 8) / 10;

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + data_size);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sample_rate);
            writer.Write(sample_rate * channels * (bits_per_sample / 8));
            writer.Write((short)(channels * (bits_per_sample / 8)));
            writer.Write(bits_per_sample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(data_size);
            writer.Write(new byte[data_size]);
        }

        private static byte[] solidPng(int width, int height)
        {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height,
                new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 128, 0, 255));
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }
    }
}
