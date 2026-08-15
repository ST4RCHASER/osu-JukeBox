#nullable enable

using System.IO;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Game.Audio;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Skinning;
using osuTK.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// The beatmap folder as a legacy skin: beatmap-provided skin textures, custom hitsound/keysound
/// samples, and the beatmap file's own [Colours] section as skin configuration — the standalone
/// equivalent of lazer's realm-backed <see cref="LegacyBeatmapSkin"/>. Shared by the gameplay
/// chart layer (hitsounds, skin elements) and the storyboard layer (storyboard Sample events).
/// </summary>
internal class BeatmapFolderSkin : LegacySkin
{
    /// <param name="configFile">Absolute path of a .osu (or .osb) file inside the beatmap folder;
    /// its directory becomes the skin root and the file itself is parsed as skin configuration
    /// (LegacySkin's parser accepts .osu content — same trick as lazer's LegacyBeatmapSkin).</param>
    /// <param name="resources">Game-level resource provider.</param>
    /// <param name="host">Host for storage access.</param>
    public BeatmapFolderSkin(string configFile, IStorageResourceProvider resources, GameHost host)
        : base(
            new SkinInfo { Name = Path.GetFileName(configFile) },
            resources,
            // See SkinFolderResourceStore: beatmap folders reference their files with the same
            // stable-era conventions skins do (a .osu's own event and skin references are written
            // by the same editor), so they need the same normalisation.
            new SkinFolderResourceStore(new NativeStorage(Path.GetDirectoryName(configFile)!, host)),
            Path.GetFileName(configFile))
    {
        // This class mirrors every behavioural override LegacyBeatmapSkin applies; the members below
        // are the full set (audited against osu.Game 2026.730.0). Two of its members are deliberately
        // NOT carried, for reasons that are not a matter of choice:
        //
        //  - AllowDefaultComboColoursFallback = false, set on Configuration in its constructor. The
        //    field is `internal` to osu.Game and so unreachable from here, which is the one known
        //    remaining deviation: a beatmap with no [Colours] section falls back to the classic
        //    default combo colours rather than to the active skin's own palette.
        //  - BeatmapSetResources, an accessor that downcasts its fallback store to
        //    RealmBackedResourceStore. Not applicable — ours is a folder, not a realm-backed set
        //    (which is the whole reason LegacyBeatmapSkin can't simply be used directly).
        //
        // Its createSkinInfo (Name from the beatmap, Creator from the mapper) is cosmetic and
        // approximated above by naming the skin after the file.
    }

    /// <summary>
    /// Mirrors <c>LegacyBeatmapSkin</c>'s own opt-out. A beatmap folder is parsed as skin
    /// configuration from its .osu file (see the constructor), and a .osu never carries a
    /// <c>[Mania]</c> section — but <see cref="LegacySkin"/> does not DECLINE a mania lookup it has
    /// no section for. Asked about a key count it doesn't know, it manufactures a default
    /// configuration and answers from it, authoritatively.
    ///
    /// <para>
    /// This skin sits at higher priority than the user's own (see LazerChartLayer's chain), so
    /// without this the beatmap answered every mania geometry lookup with lazer's defaults and the
    /// user's skin was never asked: a 4K stage rendered at the default 48-unit column width instead
    /// of the skin's 102.4, roughly a third of its intended width. IMAGE lookups fell through
    /// (a default configuration has no image entries), which is why the result was a default-sized
    /// stage wearing the user skin's notes — the notes then far wider than the columns holding them.
    /// </para>
    /// </summary>
    protected override bool AllowManiaConfigLookups => false;

    /// <summary>
    /// Mirrors <c>LegacyBeatmapSkin</c>. "Custom sample banks" are stable's per-hitobject sample-set
    /// index: index 2+ is written into a hitsound's <c>Suffix</c>, so the map asking for sample set 3
    /// wants <c>soft-hitnormal3.wav</c> specifically. <see cref="LegacySkin"/> defaults this off and
    /// then actively FILTERS OUT every candidate filename ending in that suffix — the correct choice
    /// for a user skin (which must not answer a request for the beatmap's own numbered sample with
    /// its unnumbered one) and exactly wrong for the beatmap folder, which is where those numbered
    /// files live. Without this, a map's custom sample banks silently resolved to its plain
    /// <c>soft-hitnormal.wav</c>, and where the map shipped only the numbered file this skin declined
    /// and the user's skin sound played instead.
    /// </summary>
    protected override bool UseCustomSampleBanks => true;

    /// <summary>
    /// Mirrors <c>LegacyBeatmapSkin</c>. <see cref="LegacySkin"/> otherwise probes for an
    /// <c>@2x</c>-suffixed sibling of every texture and, on a hit, halves the displayed size via
    /// <c>ScaleAdjust</c>. Stable has no such convention for beatmap folders — only for skins — so a
    /// map shipping <c>foo@2x.png</c> means a file literally named that, not a high-resolution
    /// variant of <c>foo</c>. Answering the lookup anyway both draws the wrong asset at half size and
    /// (the more damaging half) stops the request falling through to the user's skin, which is where
    /// stable would have found it.
    /// </summary>
    protected override bool AllowHighResolutionSprites => false;

    /// <summary>
    /// Mirrors <c>LegacyBeatmapSkin</c>. Combo colours are indexed twice over: <c>ComboIndex</c>
    /// counts combos, while <c>ComboIndexWithOffsets</c> additionally applies each object's
    /// <c>ComboOffset</c> — the colour-skip a mapper encodes in the new-combo bits. Lookups arrive
    /// carrying the former; <see cref="IHasComboInformation.ComboIndexWithOffsets"/>'s own
    /// documentation says it "should be used instead of ComboIndex only when retrieving combo colours
    /// from the beatmap's skin", which is precisely this class. Without the substitution a beatmap
    /// with deliberate colour skips gets its palette walked in plain order, so every combo after the
    /// first skip wears the wrong colour.
    /// </summary>
    protected override IBindable<Color4>? GetComboColour(IHasComboColours source, int comboIndex, IHasComboInformation combo)
        => base.GetComboColour(source, combo.ComboIndexWithOffsets, combo);

    /// <summary>
    /// Mirrors <c>LegacyBeatmapSkin</c>. <see cref="HitSampleInfo.UseBeatmapSamples"/> is stable's
    /// "custom sample set index >= 1" — the map explicitly asking for ITS OWN hitsound files. When it
    /// is false the map wants the user's skin sounds, and this skin (which outranks the user's) must
    /// decline rather than serve a same-named file that merely happens to sit in the beatmap folder.
    /// Mappers routinely ship a full hitsound set alongside objects that don't use it, so without this
    /// the beatmap's samples played over the user's skin on essentially every object.
    ///
    /// <para>
    /// Only <see cref="HitSampleInfo"/> is gated. Storyboard <c>Sample</c> events arrive as
    /// <c>StoryboardSampleInfo</c>, which implements <see cref="ISampleInfo"/> without deriving from
    /// <see cref="HitSampleInfo"/>, and must keep resolving from the beatmap folder — that folder is
    /// the only place a storyboard's audio ever lives.
    /// </para>
    /// </summary>
    public override ISample? GetSample(ISampleInfo sampleInfo)
    {
        if (sampleInfo is HitSampleInfo { UseBeatmapSamples: false })
            return null;

        return base.GetSample(sampleInfo);
    }

    /// <summary>
    /// Mirrors <c>LegacyBeatmapSkin</c>: a beatmap skin lacking the legacy score font must not supply
    /// gameplay HUD components, because <see cref="LegacySkin"/> answers a HUD lookup with a whole
    /// default legacy component set (combo counter, spectator list, leaderboard) rather than
    /// declining — and this skin outranks the user's, so it would substitute that default set for the
    /// user skin's own HUD on every beatmap.
    ///
    /// <para>
    /// Carried for parity only: JukeBox renders a <c>DrawableRuleset</c> with no HUD at all, so
    /// nothing in the app currently issues this lookup. It is mirrored so that adding one later
    /// cannot quietly reintroduce the divergence.
    /// </para>
    /// </summary>
    public override Drawable? GetDrawableComponent(ISkinComponentLookup lookup)
    {
        if (lookup is GlobalSkinnableContainerLookup { Lookup: GlobalSkinnableContainers.MainHUDComponents } && !this.HasFont(LegacyFont.Score))
            return null;

        return base.GetDrawableComponent(lookup);
    }

    /// <summary>
    /// Mirrors <c>LegacyBeatmapSkin.GetConfig</c>'s exact override for
    /// <see cref="SkinConfiguration.LegacySetting.Version"/>: always returns null, "ignoring
    /// beatmap-level versioning completely". Without this, the .osu-file-as-skin-config parse this
    /// class relies on (see the constructor doc) always reports the legacy decoder's DEFAULT
    /// version (1.0 — beatmaps essentially never declare a real one), never null — and, unlike a
    /// genuinely-versionless lookup, a concrete (if defaulted) value here means the beatmap skin's
    /// own answer wins outright rather than falling through to the user's actual selected skin.
    /// Ruleset code that branches on this (e.g. TaikoLegacySkinTransformer's LegacyHalfDrum, whose
    /// "&gt;=2.1" check picks between two different position calibrations for the drum hit flash)
    /// then always takes the old, sub-2.1 branch for anything resolved through a beatmap skin —
    /// regardless of what the user's actual skin (or the beatmap's own real skin.ini, which this
    /// class doesn't separately parse at all — see the constructor doc, it's the .osu file itself)
    /// would have reported — producing wrong, version-1.0-calibrated positioning even under an
    /// otherwise-modern skin.
    /// </summary>
    public override IBindable<TValue>? GetConfig<TLookup, TValue>(TLookup lookup)
    {
        if (lookup is SkinConfiguration.LegacySetting s && s == SkinConfiguration.LegacySetting.Version)
            return null;

        return base.GetConfig<TLookup, TValue>(lookup);
    }
}
