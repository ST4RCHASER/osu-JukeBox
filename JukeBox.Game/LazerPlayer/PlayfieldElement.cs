#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using osu.Framework.Graphics;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko;
using osu.Game.Skinning;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// One individually hideable piece of the rendered playfield, across all four rulesets.
///
/// <para>
/// The entries mirror what a skinner actually replaces — the element list of osu!'s own skinning
/// reference (https://osu.ppy.sh/community/forums/topics/51694), grouped the same way: standard's
/// cursor/hitcircle/slider/spinner/hitburst families, taiko's bar, drum, notes and pippidon,
/// catch's fruits/droplets/catcher, mania's stage, keys and notes. Each maps onto the lazer
/// <see cref="ISkinComponentLookup"/>(s) that the corresponding drawable actually asks the skin
/// chain for, so hiding one is done by lazer's own skin lookup (see
/// <see cref="PlayfieldElementFilter"/>) rather than by reaching into the hosted DrawableRuleset's
/// drawable tree — which would have to fight object pooling on every note.
/// </para>
///
/// <para>
/// Names are persisted verbatim (see <see cref="Configuration.JukeBoxSetting.HiddenPlayfieldElements"/>),
/// so members may be added or reordered but not renamed without dropping a user's choice.
/// </para>
/// </summary>
public enum PlayfieldElement
{
    // ---- every ruleset ----

    [Description("Judgements (hit scores)")]
    Judgements,

    // ---- osu! ----

    [Description("Cursor")]
    OsuCursor,

    [Description("Cursor trail")]
    OsuCursorTrail,

    [Description("Cursor ripples")]
    OsuCursorRipples,

    [Description("Cursor particles")]
    OsuCursorParticles,

    [Description("Hit circles")]
    OsuHitCircles,

    [Description("Approach circles")]
    OsuApproachCircles,

    [Description("Combo numbers")]
    OsuComboNumbers,

    [Description("Follow points")]
    OsuFollowPoints,

    [Description("Slider body")]
    OsuSliderBody,

    [Description("Slider ball")]
    OsuSliderBall,

    [Description("Slider follow ring")]
    OsuSliderFollowRing,

    [Description("Slider ticks")]
    OsuSliderTicks,

    [Description("Reverse arrows")]
    OsuReverseArrows,

    [Description("Spinner")]
    OsuSpinner,

    // ---- osu!taiko ----

    [Description("Notes")]
    TaikoNotes,

    [Description("Drum rolls")]
    TaikoDrumRolls,

    [Description("Swells (spinners)")]
    TaikoSwells,

    [Description("Hit target")]
    TaikoHitTarget,

    [Description("Input drum")]
    TaikoInputDrum,

    [Description("Lane background")]
    TaikoLaneBackground,

    [Description("Bar lines")]
    TaikoBarLines,

    [Description("Hit explosions")]
    TaikoHitExplosions,

    [Description("Kiai glow")]
    TaikoKiaiGlow,

    [Description("Mascot (pippidon)")]
    TaikoMascot,

    [Description("Scroller")]
    TaikoScroller,

    // ---- osu!catch ----

    [Description("Fruits")]
    CatchFruits,

    [Description("Bananas")]
    CatchBananas,

    [Description("Droplets")]
    CatchDroplets,

    [Description("Catcher")]
    CatchCatcher,

    [Description("Combo counter")]
    CatchComboCounter,

    [Description("Hit explosions")]
    CatchHitExplosions,

    // ---- osu!mania ----

    [Description("Notes")]
    ManiaNotes,

    [Description("Hold notes")]
    ManiaHoldNotes,

    [Description("Stage background")]
    ManiaStageBackground,

    [Description("Stage foreground")]
    ManiaStageForeground,

    [Description("Column background")]
    ManiaColumnBackground,

    [Description("Key area")]
    ManiaKeyArea,

    [Description("Judgement line")]
    ManiaJudgementLine,

    [Description("Bar lines")]
    ManiaBarLines,

    [Description("Hit explosions")]
    ManiaHitExplosions,
}

/// <summary>
/// What each <see cref="PlayfieldElement"/> means: which ruleset it belongs to, how it is labelled,
/// and which lazer skin lookups hiding it must intercept.
/// </summary>
public static class PlayfieldElementCatalog
{
    /// <summary><see cref="Entry.RulesetId"/> for elements every ruleset draws.</summary>
    public const int all_rulesets = -1;

    public sealed class Entry
    {
        public required PlayfieldElement Element { get; init; }

        /// <summary>The group this element is listed under: an online ruleset id, or
        /// <see cref="all_rulesets"/> for the shared block at the top.</summary>
        public required int RulesetId { get; init; }

        /// <summary>
        /// Whether this element means anything for a chart of the given ruleset. Defaults to its
        /// own group (a shared entry applying everywhere), which is right for all but the entries
        /// whose component only SOME rulesets actually draw — osu!catch, for one, has no hit-score
        /// popups at all, so a judgements toggle there would be a control that does nothing.
        /// </summary>
        public Func<int, bool>? AppliesToRuleset { get; init; }

        public bool AppliesTo(int rulesetId)
            => AppliesToRuleset?.Invoke(rulesetId) ?? (RulesetId == all_rulesets || RulesetId == rulesetId);

        /// <summary>Human label for the toggle, from the enum member's <see cref="DescriptionAttribute"/>.</summary>
        public required string Label { get; init; }

        /// <summary>Whether this element owns <paramref name="lookup"/> — i.e. whether suppressing
        /// the element should make this lookup resolve to nothing.</summary>
        public required Func<ISkinComponentLookup, bool> Matches { get; init; }

        /// <summary>
        /// What the filter hands back for a suppressed lookup. An empty drawable for almost
        /// everything — any skin may answer any component with any drawable, which is the contract
        /// <see cref="osu.Game.Skinning.SkinnableDrawable"/> consumers are written against.
        ///
        /// <para>
        /// A handful of consumers break that contract and CAST the answer to the type of their own
        /// default implementation, so for those the replacement has to be an invisible instance of
        /// that type instead. <see cref="PlayfieldElement.OsuSliderBody"/> is the case in point:
        /// <c>DrawableSlider</c> casts to <c>PlaySliderBody</c> to drive the path's progress and
        /// accent colour, so an empty container aborts the game outright. Anything of this shape
        /// is caught by TestScenePlayfieldElements, which hides every element of a ruleset at once
        /// and plays a chart through.
        /// </para>
        /// </summary>
        public Func<Drawable> CreateHidden { get; init; } = Drawable.Empty;
    }

    private static Entry osu(PlayfieldElement element, params OsuSkinComponents[] components)
        => entry(element, 0, lookup => lookup is SkinComponentLookup<OsuSkinComponents> l && components.Contains(l.Component));

    private static Entry with(Entry entry, Func<Drawable> createHidden) => new Entry
    {
        Element = entry.Element,
        RulesetId = entry.RulesetId,
        Label = entry.Label,
        Matches = entry.Matches,
        AppliesToRuleset = entry.AppliesToRuleset,
        CreateHidden = createHidden,
    };

    private static Entry taiko(PlayfieldElement element, params TaikoSkinComponents[] components)
        => entry(element, 1, lookup => lookup is SkinComponentLookup<TaikoSkinComponents> l && components.Contains(l.Component));

    private static Entry katch(PlayfieldElement element, params CatchSkinComponents[] components)
        => entry(element, 2, lookup => lookup is SkinComponentLookup<CatchSkinComponents> l && components.Contains(l.Component));

    private static Entry mania(PlayfieldElement element, params ManiaSkinComponents[] components)
        => entry(element, 3, lookup => lookup is SkinComponentLookup<ManiaSkinComponents> l && components.Contains(l.Component));

    private static Entry entry(PlayfieldElement element, int rulesetId, Func<ISkinComponentLookup, bool> matches)
        => new Entry
        {
            Element = element,
            RulesetId = rulesetId,
            Label = describe(element),
            Matches = matches,
        };

    private static string describe(PlayfieldElement element)
        => typeof(PlayfieldElement).GetField(element.ToString())?
                                   .GetCustomAttributes(typeof(DescriptionAttribute), false)
                                   .OfType<DescriptionAttribute>()
                                   .FirstOrDefault()?.Description
           ?? element.ToString();

    /// <summary>
    /// Every element, in the order the Chart tab lists them. Ruleset-agnostic entries first, then
    /// one block per ruleset in online-id order.
    /// </summary>
    public static readonly IReadOnlyList<Entry> All = new[]
    {
        // osu!, taiko and mania all route their hit-score popups through the same generic HitResult
        // lookup (DrawableJudgement), so one entry covers the three. osu!catch is deliberately
        // excluded: it shows no judgement popups at all (fruits simply explode), so the toggle
        // would be dead there.
        new Entry
        {
            Element = PlayfieldElement.Judgements,
            RulesetId = all_rulesets,
            Label = describe(PlayfieldElement.Judgements),
            Matches = lookup => lookup is SkinComponentLookup<HitResult>,
            AppliesToRuleset = rulesetId => rulesetId != 2,
        },

        // OsuCursorContainer casts this one (see Entry.CreateHidden).
        with(osu(PlayfieldElement.OsuCursor, OsuSkinComponents.Cursor), () => new HiddenCursor()),
        osu(PlayfieldElement.OsuCursorTrail, OsuSkinComponents.CursorTrail),
        osu(PlayfieldElement.OsuCursorRipples, OsuSkinComponents.CursorRipple),
        osu(PlayfieldElement.OsuCursorParticles, OsuSkinComponents.CursorParticles, OsuSkinComponents.CursorSmoke),
        osu(PlayfieldElement.OsuHitCircles, OsuSkinComponents.HitCircle, OsuSkinComponents.SliderHeadHitCircle, OsuSkinComponents.SliderTailHitCircle),
        osu(PlayfieldElement.OsuApproachCircles, OsuSkinComponents.ApproachCircle),
        osu(PlayfieldElement.OsuComboNumbers, OsuSkinComponents.HitCircleText),
        osu(PlayfieldElement.OsuFollowPoints, OsuSkinComponents.FollowPoint),
        // DrawableSlider casts this one (see Entry.CreateHidden), so it gets an invisible real
        // slider body rather than an empty drawable — same visible result, no crash.
        with(osu(PlayfieldElement.OsuSliderBody, OsuSkinComponents.SliderBody), () => new HiddenSliderBody()),
        osu(PlayfieldElement.OsuSliderBall, OsuSkinComponents.SliderBall),
        osu(PlayfieldElement.OsuSliderFollowRing, OsuSkinComponents.SliderFollowCircle),
        osu(PlayfieldElement.OsuSliderTicks, OsuSkinComponents.SliderScorePoint),
        osu(PlayfieldElement.OsuReverseArrows, OsuSkinComponents.ReverseArrow),
        osu(PlayfieldElement.OsuSpinner, OsuSkinComponents.SpinnerBody),

        taiko(PlayfieldElement.TaikoNotes, TaikoSkinComponents.CentreHit, TaikoSkinComponents.RimHit),
        taiko(PlayfieldElement.TaikoDrumRolls, TaikoSkinComponents.DrumRollHead, TaikoSkinComponents.DrumRollBody, TaikoSkinComponents.DrumRollTick),
        taiko(PlayfieldElement.TaikoSwells, TaikoSkinComponents.Swell),
        taiko(PlayfieldElement.TaikoHitTarget, TaikoSkinComponents.HitTarget),
        taiko(PlayfieldElement.TaikoInputDrum, TaikoSkinComponents.InputDrum),
        taiko(PlayfieldElement.TaikoLaneBackground, TaikoSkinComponents.PlayfieldBackgroundLeft, TaikoSkinComponents.PlayfieldBackgroundRight),
        taiko(PlayfieldElement.TaikoBarLines, TaikoSkinComponents.BarLine),
        taiko(PlayfieldElement.TaikoHitExplosions, TaikoSkinComponents.TaikoExplosionMiss, TaikoSkinComponents.TaikoExplosionOk, TaikoSkinComponents.TaikoExplosionGreat, TaikoSkinComponents.TaikoExplosionKiai),
        taiko(PlayfieldElement.TaikoKiaiGlow, TaikoSkinComponents.KiaiGlow),
        taiko(PlayfieldElement.TaikoMascot, TaikoSkinComponents.Mascot),
        taiko(PlayfieldElement.TaikoScroller, TaikoSkinComponents.Scroller),

        katch(PlayfieldElement.CatchFruits, CatchSkinComponents.Fruit),
        katch(PlayfieldElement.CatchBananas, CatchSkinComponents.Banana),
        katch(PlayfieldElement.CatchDroplets, CatchSkinComponents.Droplet),
        katch(PlayfieldElement.CatchCatcher, CatchSkinComponents.Catcher),
        katch(PlayfieldElement.CatchComboCounter, CatchSkinComponents.CatchComboCounter),
        katch(PlayfieldElement.CatchHitExplosions, CatchSkinComponents.HitExplosion),

        mania(PlayfieldElement.ManiaNotes, ManiaSkinComponents.Note),
        mania(PlayfieldElement.ManiaHoldNotes, ManiaSkinComponents.HoldNoteHead, ManiaSkinComponents.HoldNoteTail, ManiaSkinComponents.HoldNoteBody),
        mania(PlayfieldElement.ManiaStageBackground, ManiaSkinComponents.StageBackground),
        mania(PlayfieldElement.ManiaStageForeground, ManiaSkinComponents.StageForeground),
        mania(PlayfieldElement.ManiaColumnBackground, ManiaSkinComponents.ColumnBackground),
        mania(PlayfieldElement.ManiaKeyArea, ManiaSkinComponents.KeyArea),
        mania(PlayfieldElement.ManiaJudgementLine, ManiaSkinComponents.HitTarget),
        mania(PlayfieldElement.ManiaBarLines, ManiaSkinComponents.BarLine),
        mania(PlayfieldElement.ManiaHitExplosions, ManiaSkinComponents.HitExplosion),
    };

    private static readonly Dictionary<PlayfieldElement, Entry> by_element = All.ToDictionary(e => e.Element);

    public static Entry For(PlayfieldElement element) => by_element[element];

    /// <summary>
    /// The elements a chart of <paramref name="rulesetId"/> can actually draw: the ruleset's own,
    /// plus the ruleset-agnostic ones. Listed agnostic-first, matching <see cref="All"/>'s order.
    /// </summary>
    public static IEnumerable<Entry> ForRuleset(int rulesetId) => All.Where(e => e.AppliesTo(rulesetId));

    /// <summary>
    /// A real <c>PlaySliderBody</c> that simply never draws — what the filter hands back for a
    /// hidden <see cref="PlayfieldElement.OsuSliderBody"/>, because <c>DrawableSlider</c> casts the
    /// skin's answer to that type to drive the path's progress and accent colour (see
    /// <see cref="Entry.CreateHidden"/>). Its own <see cref="Drawable.Alpha"/> is what hides it:
    /// <c>DrawableSlider</c> fades the <c>SkinnableDrawable</c> WRAPPER in and out over the object's
    /// lifetime, never this drawable, so the zero here is never written over.
    /// </summary>
    private partial class HiddenSliderBody : osu.Game.Rulesets.Osu.Skinning.Default.PlaySliderBody
    {
        public HiddenSliderBody()
        {
            Alpha = 0;
        }
    }

    /// <summary>The same trick for the osu! cursor, whose container casts the skin's answer to
    /// <c>SkinnableCursor</c> to drive its expand/contract on click.</summary>
    private partial class HiddenCursor : osu.Game.Rulesets.Osu.UI.Cursor.SkinnableCursor
    {
        public HiddenCursor()
        {
            Alpha = 0;
        }
    }

    /// <summary>The display name of a ruleset group header, for the Chart tab's subsections.</summary>
    public static string RulesetName(int rulesetId) => rulesetId switch
    {
        all_rulesets => "All modes",
        1 => "osu!taiko",
        2 => "osu!catch",
        3 => "osu!mania",
        _ => "osu!",
    };
}
