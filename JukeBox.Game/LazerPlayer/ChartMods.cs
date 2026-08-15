#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using JukeBox.Game.Online;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Utils;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// The gameplay mods the Chart tab offers, identified by osu!'s own acronyms — which is also how
/// they are persisted (see <see cref="JukeBoxSetting.ChartMods"/>) and how they are resolved into
/// real ruleset mod instances, so a mod is never re-implemented here, only named.
/// </summary>
public enum ChartMod
{
    [Description("Easy")]
    Easy,

    [Description("Half Time")]
    HalfTime,

    [Description("Hard Rock")]
    HardRock,

    [Description("Hidden")]
    Hidden,

    [Description("Double Time")]
    DoubleTime,

    [Description("Nightcore")]
    Nightcore,

    [Description("Flashlight")]
    Flashlight,
}

public static class ChartModExtensions
{
    /// <summary>The osu! acronym naming this mod in every ruleset that offers it.</summary>
    public static string Acronym(this ChartMod mod) => mod switch
    {
        ChartMod.Easy => "EZ",
        ChartMod.HalfTime => "HT",
        ChartMod.HardRock => "HR",
        ChartMod.Hidden => "HD",
        ChartMod.DoubleTime => "DT",
        ChartMod.Nightcore => "NC",
        ChartMod.Flashlight => "FL",
        _ => throw new ArgumentOutOfRangeException(nameof(mod), mod, null),
    };

    public static string Label(this ChartMod mod)
        => typeof(ChartMod).GetField(mod.ToString())?
                           .GetCustomAttributes(typeof(DescriptionAttribute), false)
                           .OfType<DescriptionAttribute>()
                           .FirstOrDefault()?.Description
           ?? mod.ToString();
}

/// <summary>
/// Game-lifetime service owning the user's chart-mod selection: which of
/// <see cref="ChartMod"/> are on, what that means for the rendered chart, and what it means for
/// playback speed.
///
/// <list type="bullet">
/// <item><b>Selection</b> — one <see cref="BindableBool"/> per mod, persisted as acronyms in
/// <see cref="JukeBoxSetting.ChartMods"/>. Incompatible pairs are resolved by asking lazer
/// (<see cref="ModUtils.CheckCompatibleSet(IEnumerable{Mod})"/>) rather than by a table kept here:
/// enabling one member of an exclusive family (DT/NC/HT are all <c>ModRateAdjust</c>; EZ/HR) simply
/// switches the others off.</item>
/// <item><b>Gameplay</b> — <see cref="CreateFor"/> materialises the selection as FRESH instances of
/// the given ruleset's own mods. Mods are stateful (see <see cref="ReplayMods.ForGameplay"/>), so
/// every DrawableRuleset build must get its own.</item>
/// <item><b>Rate</b> — DT/NC/HT move the TRACK in osu!, not the chart, and this app is arranged the
/// same way (see <see cref="PlaybackController.ChartModTempo"/>). The split between pitch-preserving
/// tempo and pitch-shifting frequency is read back out of the mods themselves via
/// <see cref="ReplayMods.TrackAdjustmentsFor"/>, so DT stays pitch-preserving and NC does not.</item>
/// </list>
///
/// <para>
/// A dropped replay overrides all of it: the replay's own mods are what was played, so while one is
/// driving playback (<see cref="ReplayActive"/>) the selection is neither rendered
/// (<c>LazerChartLayer</c> takes the replay's mods) nor allowed to touch the rate, and the Chart tab
/// greys its toggles out and shows the replay's mods instead.
/// </para>
/// </summary>
public partial class ChartModSelection : osu.Framework.Graphics.Component
{
    private static readonly ChartMod[] all_mods = Enum.GetValues<ChartMod>();

    private readonly Dictionary<ChartMod, BindableBool> enabled =
        all_mods.ToDictionary(m => m, _ => new BindableBool());

    private readonly Bindable<string> persisted = new Bindable<string>(string.Empty);

    /// <summary>Bumped whenever the selection changes — consumers rebuild the chart layer on it
    /// (mods change beatmap CONVERSION, so they cannot be applied to a live DrawableRuleset).</summary>
    public IBindable<int> Revision => revision;

    private readonly Bindable<int> revision = new Bindable<int>();

    /// <summary>Whether a dropped replay is driving playback, in which case the selection is inert.
    /// Read off the now-playing set, the object a replay actually travels on — the same source
    /// <c>DifficultySwitcher.ReplayLocked</c> reads, so the two lock together.</summary>
    public IBindable<bool> ReplayActive => replayActive;

    private readonly BindableBool replayActive = new BindableBool();

    /// <summary>The replay's own mods while one is playing, for the Chart tab to show in place of
    /// the (locked) toggles. Empty otherwise.</summary>
    public IBindable<IReadOnlyList<string>> ReplayModAcronyms => replayModAcronyms;

    private readonly Bindable<IReadOnlyList<string>> replayModAcronyms =
        new Bindable<IReadOnlyList<string>>(Array.Empty<string>());

    private readonly Bindable<BeatmapSetInfo?> nowPlaying = new Bindable<BeatmapSetInfo?>();

    /// <summary>Guards the config→bindables direction from being echoed straight back.</summary>
    private bool applying;

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    [Resolved(canBeNull: true)]
    private PlaybackController? playback { get; set; }

    [Resolved(canBeNull: true)]
    private Jukebox? jukebox { get; set; }

    public BindableBool Enabled(ChartMod mod) => enabled[mod];

    /// <summary>The selected mods, in <see cref="ChartMod"/> order.</summary>
    public IReadOnlyList<ChartMod> Selected => all_mods.Where(m => enabled[m].Value).ToArray();

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (config != null)
            config.BindWith(JukeBoxSetting.ChartMods, persisted);

        persisted.BindValueChanged(e => applyFromConfig(e.NewValue), true);

        foreach (var (mod, bindable) in enabled)
        {
            var captured = mod;
            bindable.BindValueChanged(e => onToggled(captured, e.NewValue));
        }

        if (jukebox != null)
        {
            nowPlaying.BindTo(jukebox.NowPlaying);
            nowPlaying.BindValueChanged(e =>
            {
                replayActive.Value = e.NewValue?.Replay != null;
                replayModAcronyms.Value = e.NewValue?.Replay?.ModAcronyms ?? Array.Empty<string>();
                updateRate();
            }, true);
        }

        updateRate();
    }

    private void onToggled(ChartMod mod, bool value)
    {
        if (!applying && value)
            resolveConflicts(mod);

        revision.Value++;

        if (!applying)
        {
            writeToConfig();

            string acronyms = string.Join(" ", Selected.Select(m => m.Acronym()));
            Logger.Log($"[chart mods] {(acronyms.Length > 0 ? acronyms : "(none)")}");
        }

        updateRate();
    }

    /// <summary>
    /// Switches off anything lazer says cannot be worn alongside <paramref name="justEnabled"/>.
    /// The rules come from the mods themselves — <see cref="Mod.IncompatibleMods"/> as evaluated by
    /// <see cref="ModUtils.CheckCompatibleSet(IEnumerable{Mod})"/> — so DT/NC/HT (all
    /// <c>ModRateAdjust</c>) and EZ/HR fall out of it without a table here to drift out of date, and
    /// so would any exclusion a future osu! release introduces.
    /// </summary>
    private void resolveConflicts(ChartMod justEnabled)
    {
        applying = true;

        try
        {
            foreach (var other in all_mods)
            {
                if (other == justEnabled || !enabled[other].Value)
                    continue;

                if (!Compatible(justEnabled, other))
                {
                    enabled[other].Value = false;
                    Logger.Log($"[chart mods] {other.Acronym()} switched off — incompatible with {justEnabled.Acronym()}");
                }
            }
        }
        finally
        {
            applying = false;
        }
    }

    /// <summary>
    /// Reference instances used only to ASK lazer questions about the mods (their exclusions, their
    /// track adjustments) — never handed to a ruleset, which always gets fresh ones from
    /// <see cref="CreateFor"/>. osu!'s are the reference because every ruleset's rate and difficulty
    /// mods inherit their exclusion rules from the same base types.
    /// </summary>
    private static readonly Dictionary<ChartMod, Mod> prototypes = buildPrototypes();

    private static Dictionary<ChartMod, Mod> buildPrototypes()
    {
        var available = new OsuRuleset().CreateAllMods().ToArray();

        return all_mods.Select(m => (mod: m, instance: available.FirstOrDefault(a => a.Acronym == m.Acronym())))
                       .Where(p => p.instance != null)
                       .ToDictionary(p => p.mod, p => p.instance!);
    }

    /// <summary>Whether lazer allows these two mods together.</summary>
    public static bool Compatible(ChartMod a, ChartMod b)
    {
        if (!prototypes.TryGetValue(a, out var first) || !prototypes.TryGetValue(b, out var second))
            return true;

        return ModUtils.CheckCompatibleSet(new[] { first, second });
    }

    /// <summary>
    /// Fresh instances of <paramref name="ruleset"/>'s own mods for the current selection — never
    /// cached and never shared: <c>ModWithVisibilityAdjustment</c> (HD and friends) binds config
    /// bindables when a DrawableRuleset loads, and binding an already-bound bindable throws.
    /// A mod the ruleset does not offer is simply skipped.
    /// </summary>
    public IReadOnlyList<Mod> CreateFor(Ruleset ruleset)
    {
        var available = ruleset.CreateAllMods().ToArray();

        return Selected.Select(m => available.FirstOrDefault(a => a.Acronym == m.Acronym()))
                       .Where(m => m != null)
                       .Select(m => m!)
                       .ToArray();
    }

    /// <summary>
    /// Pushes the selection's speed change onto playback. Rate is ruleset-independent (DT is 1.5×
    /// everywhere), so it is read off osu!'s instances — and it applies whether or not the chart is
    /// being rendered, because a rate mod is a change to the song, not to the drawing of it.
    /// </summary>
    private void updateRate()
    {
        if (playback == null)
            return;

        double tempo = 1;
        double frequency = 1;

        if (!replayActive.Value)
        {
            // Read off the reference instances rather than fresh ones: nothing here is applied to a
            // ruleset, it is only being asked what each mod does to a track.
            (tempo, frequency) = ReplayMods.TrackAdjustmentsFor(
                Selected.Where(prototypes.ContainsKey).Select(m => prototypes[m]));
        }

        playback.ChartModTempo.Value = tempo;
        playback.ChartModFrequency.Value = frequency;
    }

    /// <summary>Acronyms that don't resolve are dropped silently — a config written by a newer
    /// build (or hand-edited) must not stop the rest of the list from applying.</summary>
    private void applyFromConfig(string value)
    {
        var wanted = new HashSet<ChartMod>();

        foreach (string acronym in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = all_mods.Where(m => string.Equals(m.Acronym(), acronym, StringComparison.OrdinalIgnoreCase))
                                .Select(m => (ChartMod?)m)
                                .FirstOrDefault();

            // A persisted pair that can't coexist (hand-edited config) resolves in list order:
            // the first one wins, exactly as if the user had clicked them in that order.
            if (match is ChartMod mod && wanted.All(w => Compatible(mod, w)))
                wanted.Add(mod);
        }

        applying = true;

        try
        {
            foreach (var (mod, bindable) in enabled)
                bindable.Value = wanted.Contains(mod);
        }
        finally
        {
            applying = false;
        }

        updateRate();
    }

    private void writeToConfig()
        => persisted.Value = string.Join(',', Selected.Select(m => m.Acronym()));
}
