#nullable enable

using System;
using System.ComponentModel;
using JukeBox.Game.Configuration;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Which ruleset the chart should be RENDERED as, regardless of the one its .osu declares — osu!'s
/// own "play a standard map in another mode" conversion.
/// </summary>
public enum ChartConversionTarget
{
    [Description("Off (play in the map's own mode)")]
    Off,

    [Description("osu!taiko")]
    Taiko,

    [Description("osu!catch")]
    Catch,

    [Description("osu!mania")]
    Mania,
}

/// <summary>
/// Game-lifetime service owning the "Convert to" choice and, with it, the single answer to "which
/// ruleset is actually on screen".
///
/// <para>
/// The conversion itself is entirely lazer's: <see cref="WorkingBeatmap.GetPlayableBeatmap(IRulesetInfo, System.Collections.Generic.IReadOnlyList{osu.Game.Rulesets.Mods.Mod})"/>
/// runs the TARGET ruleset's own <see cref="IBeatmapConverter"/>, which is the same path osu! takes
/// when you pick a different mode at song select. Nothing here converts anything; it only decides
/// which ruleset to ask.
/// </para>
///
/// <para>
/// Whether a given beatmap can be converted at all is likewise asked of the ruleset
/// (<see cref="IBeatmapConverter.CanConvert"/>) rather than assumed from its mode: in practice that
/// means osu!standard maps convert to the other three and a map native to taiko/catch/mania
/// converts to nothing, but that is lazer's answer, read at the time, not a rule written here.
/// </para>
///
/// <para>
/// The choice is GLOBAL rather than per-beatmap: it describes how the user wants to listen — "show
/// me everything as taiko" — not a property of any one map, and a per-map memory would leave the
/// setting apparently doing nothing every time the song changed. It follows the same "applies when
/// a map it fits comes along" shape as the per-ruleset element toggles.
/// </para>
/// </summary>
public partial class ChartConversion : osu.Framework.Graphics.Component
{
    /// <summary>The user's choice, persisted in <see cref="JukeBoxSetting.ConvertToRuleset"/>.</summary>
    public readonly Bindable<ChartConversionTarget> Target = new Bindable<ChartConversionTarget>();

    /// <summary>
    /// Bumped whenever the rendered ruleset could have changed. Consumers rebuild the chart layer on
    /// it — a conversion changes the beatmap the ruleset is built from, so it cannot be applied to a
    /// DrawableRuleset already on screen, exactly like the mods that change conversion.
    /// </summary>
    public IBindable<int> Revision => revision;

    private readonly Bindable<int> revision = new Bindable<int>();

    /// <summary>
    /// Whether the difficulty on screen can be converted to anything at all — published by whoever
    /// last built the visuals, which is the only place a decoded beatmap exists to ask about. False
    /// before anything has played.
    /// </summary>
    public IBindable<bool> SourceConvertible => sourceConvertible;

    private readonly BindableBool sourceConvertible = new BindableBool();

    /// <summary>
    /// The online id of the ruleset actually being rendered: the target while a conversion is in
    /// force, the beatmap's own otherwise. This is what the Chart tab keys its mod rows off, and
    /// what <see cref="ChartModSelection"/> judges mod compatibility against.
    /// </summary>
    public IBindable<int> EffectiveRulesetId => effectiveRulesetId;

    private readonly Bindable<int> effectiveRulesetId = new Bindable<int>();

    /// <summary>Whether what is on screen is a CONVERT rather than a native chart — the one state in
    /// which osu!mania's key counts and Dual Stages actually do anything.</summary>
    public IBindable<bool> IsConverting => isConverting;

    private readonly BindableBool isConverting = new BindableBool();

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        config?.BindWith(JukeBoxSetting.ConvertToRuleset, Target);

        Target.BindValueChanged(e =>
        {
            Logger.Log($"[chart conversion] target: {e.NewValue}");
            revision.Value++;
        });
    }

    /// <summary>The online ruleset id a target names, or null for <see cref="ChartConversionTarget.Off"/>.</summary>
    public static int? RulesetIdFor(ChartConversionTarget target) => target switch
    {
        ChartConversionTarget.Taiko => 1,
        ChartConversionTarget.Catch => 2,
        ChartConversionTarget.Mania => 3,
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="working"/> can be played as <paramref name="target"/>. Two conditions,
    /// and they are different questions:
    ///
    /// <list type="number">
    /// <item>The SOURCE must be osu!standard. In osu!, a "convert" is by definition an osu! beatmap
    /// interpreted by another mode — a map authored for taiko, catch or mania is not converted to
    /// anything, and its stored coordinates would be meaningless to another ruleset if it were.
    /// The osu! ruleset is asked for its own id rather than the number being written here.</item>
    /// <item>The TARGET's own converter must accept it
    /// (<see cref="IBeatmapConverter.CanConvert"/>).</item>
    /// </list>
    ///
    /// <para>
    /// Both are needed, and the first cannot be folded into the second: <c>CanConvert</c> is a check
    /// on the SHAPE of the decoded hit objects (do they carry the positions/durations this converter
    /// reads), and a legacy beatmap of any mode decodes to objects of that shape — measured, it
    /// answers true for taiko, catch and mania sources as readily as for osu! ones. It is the wrong
    /// question to ask on its own, and asking only it would offer nonsense conversions of maps whose
    /// x-coordinates mean nothing.
    /// </para>
    ///
    /// <para>
    /// A beatmap is always "convertible" to its own ruleset (there is nothing to convert), and a
    /// converter that throws while inspecting a malformed beatmap is treated as a no rather than
    /// taking the chart down with it.
    /// </para>
    /// </summary>
    public static bool CanConvert(WorkingBeatmap working, Ruleset target)
    {
        int sourceId = working.BeatmapInfo.Ruleset.OnlineID;

        if (sourceId == target.RulesetInfo.OnlineID)
            return true;

        if (sourceId != new osu.Game.Rulesets.Osu.OsuRuleset().RulesetInfo.OnlineID)
            return false;

        try
        {
            return target.CreateBeatmapConverter(working.Beatmap).CanConvert();
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Failed to ask {target.ShortName} whether it can convert this beatmap — treating it as unconvertible");
            return false;
        }
    }

    /// <summary>Whether this beatmap can be converted to ANY of the targets on offer.</summary>
    public static bool ConvertibleToAnything(WorkingBeatmap working)
    {
        foreach (var target in Enum.GetValues<ChartConversionTarget>())
        {
            if (RulesetIdFor(target) is int id
                && id != working.BeatmapInfo.Ruleset.OnlineID
                && CanConvert(working, LazerChartLayer.CreateRuleset(id)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ruleset to build <paramref name="working"/> with: the chosen target when one is set and
    /// the beatmap can actually be converted to it, the beatmap's own otherwise. A conversion that
    /// the target refuses degrades to the native ruleset rather than failing the chart.
    ///
    /// <para>
    /// <paramref name="allowConversion"/> is false for replay playback: a replay's frames belong to
    /// the ruleset it was played on, so rendering it as another mode would be showing input that
    /// never happened.
    /// </para>
    /// </summary>
    public Ruleset EffectiveRulesetFor(WorkingBeatmap working, bool allowConversion = true)
    {
        var native = LazerChartLayer.CreateRuleset(working.BeatmapInfo.Ruleset.OnlineID);

        if (!allowConversion || RulesetIdFor(Target.Value) is not int targetId || targetId == native.RulesetInfo.OnlineID)
            return native;

        var target = LazerChartLayer.CreateRuleset(targetId);

        if (CanConvert(working, target))
            return target;

        Logger.Log($"[chart conversion] {native.ShortName} beatmap cannot be converted to {target.ShortName} — rendering it natively");
        return native;
    }

    /// <summary>
    /// Publishes what is actually on screen. Called by whoever built the visuals for a difficulty,
    /// since that is where a decoded beatmap exists; the Chart tab reads the result rather than
    /// decoding anything itself.
    /// </summary>
    public void Publish(WorkingBeatmap? working, bool allowConversion = true)
    {
        if (working == null)
        {
            sourceConvertible.Value = false;
            isConverting.Value = false;
            return;
        }

        var effective = EffectiveRulesetFor(working, allowConversion);

        sourceConvertible.Value = allowConversion && ConvertibleToAnything(working);
        effectiveRulesetId.Value = effective.RulesetInfo.OnlineID;
        isConverting.Value = effective.RulesetInfo.OnlineID != working.BeatmapInfo.Ruleset.OnlineID;
    }
}
