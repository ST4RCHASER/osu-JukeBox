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
///
/// <para>
/// The acronym below is the ONLY thing written down about a mod. Everything else — its display
/// name, which category it belongs to, which rulesets offer it, what it does to the track, and what
/// it may not be worn with — is read back off lazer's own mod objects (see
/// <see cref="ChartModCatalog"/>), so this list can never drift out of step with the game.
/// </para>
/// </summary>
public enum ChartMod
{
    Easy,
    HalfTime,
    HardRock,
    Hidden,
    DoubleTime,
    Nightcore,
    Flashlight,

    /// <summary>osu!mania's Fade In — the notes appear late instead of vanishing early, which is
    /// why lazer makes it incompatible with Hidden rather than a variant of it.</summary>
    FadeIn,

    Key1,
    Key2,
    Key3,
    Key4,
    Key5,
    Key6,
    Key7,
    Key8,
    Key9,

    /// <summary>osu!mania's Dual Stages — stable calls this "Co-op", which is the name the UI
    /// uses; lazer's own <c>Mod.Name</c> for it is "Dual Stages".</summary>
    DualStages,

    Mirror,
    Random,
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
        ChartMod.FadeIn => "FI",
        ChartMod.Key1 => "1K",
        ChartMod.Key2 => "2K",
        ChartMod.Key3 => "3K",
        ChartMod.Key4 => "4K",
        ChartMod.Key5 => "5K",
        ChartMod.Key6 => "6K",
        ChartMod.Key7 => "7K",
        ChartMod.Key8 => "8K",
        ChartMod.Key9 => "9K",
        ChartMod.DualStages => "DS",
        ChartMod.Mirror => "MR",
        ChartMod.Random => "RD",
        _ => throw new ArgumentOutOfRangeException(nameof(mod), mod, null),
    };

    public static string Label(this ChartMod mod) => ChartModCatalog.LabelFor(mod);
}

/// <summary>
/// Everything about a <see cref="ChartMod"/> that lazer already knows, read off real mod instances
/// once at startup rather than restated here: its name, its <see cref="ModType"/> (which is how the
/// Chart tab groups the rows), which rulesets actually offer it, and whether two of them may be
/// worn together.
/// </summary>
public static class ChartModCatalog
{
    private static readonly ChartMod[] all_mods = Enum.GetValues<ChartMod>();

    /// <summary>Online ruleset ids, in the order the app uses everywhere else.</summary>
    private static readonly int[] ruleset_ids = { 0, 1, 2, 3 };

    /// <summary>
    /// Every ruleset's own mods, by acronym. Reference instances only: they are used to ASK lazer
    /// questions (name, type, exclusions, track adjustments) and are never handed to a
    /// DrawableRuleset, which always gets fresh ones from <see cref="ChartModSelection.CreateFor"/>.
    /// </summary>
    private static readonly Dictionary<int, Dictionary<string, Mod>> prototypes_by_ruleset =
        ruleset_ids.ToDictionary(id => id, id => LazerChartLayer.CreateRuleset(id)
                                                                .CreateAllMods()
                                                                .GroupBy(m => m.Acronym)
                                                                .ToDictionary(g => g.Key, g => g.First()));

    /// <summary>
    /// The rulesets that actually offer each mod — asked of <c>CreateAllMods()</c> rather than
    /// declared here. Worth reading before assuming: the key mods, Dual Stages and Fade In really
    /// are osu!mania-only, but <b>Mirror is also an osu! and osu!catch mod and Random is also an
    /// osu! and osu!taiko mod</b>, so scoping those two to mania would hide working toggles.
    /// </summary>
    private static readonly Dictionary<ChartMod, int[]> rulesets_offering =
        all_mods.ToDictionary(m => m, m => ruleset_ids.Where(id => prototypes_by_ruleset[id].ContainsKey(m.Acronym())).ToArray());

    /// <summary>A reference instance from whichever ruleset offers this mod first, for the
    /// questions whose answer doesn't depend on the ruleset (name, type, track adjustments).</summary>
    private static Mod prototype(ChartMod mod)
        => prototypes_by_ruleset[rulesets_offering[mod].First()][mod.Acronym()];

    public static bool OfferedBy(ChartMod mod, int rulesetId) => rulesets_offering[mod].Contains(rulesetId);

    /// <summary>
    /// Whether this mod can only change a beatmap that is being CONVERTED into its ruleset, and so
    /// does nothing to a beatmap already native to it. True for osu!mania's key counts and Co-op:
    /// they reach the conversion through <see cref="IApplicableToBeatmapConverter"/> alone, and
    /// <c>ManiaBeatmapConverter</c> only honours a requested column count when the map isn't
    /// already mania. Mirror and Random, which rewrite the converted beatmap itself
    /// (<see cref="IApplicableToBeatmap"/>), are unaffected by this and work on any map.
    ///
    /// <para>
    /// This is osu!'s own rule, not ours — stable will not apply a key mod to a native mania map
    /// either. It matters here because this app always renders a beatmap in the ruleset its .osu
    /// declares (see <c>LazerChartLayer.CreateRuleset</c>), so a convert never happens and these
    /// mods can never take effect. The Chart tab says so rather than offering a toggle that
    /// silently does nothing.
    /// </para>
    /// </summary>
    public static bool AppliesOnlyToConverts(ChartMod mod)
    {
        var type = prototype(mod).GetType();

        return typeof(IApplicableToBeatmapConverter).IsAssignableFrom(type)
               && !typeof(IApplicableToBeatmap).IsAssignableFrom(type);
    }

    /// <summary>Which of lazer's own mod categories this row belongs under.</summary>
    public static ModType TypeOf(ChartMod mod) => prototype(mod).Type;

    /// <summary>
    /// The row's label: lazer's own <see cref="Mod.Name"/> plus the acronym, so the tab reads
    /// exactly like the game. <see cref="ChartMod.DualStages"/> is the one deliberate departure —
    /// lazer names it "Dual Stages" where every mania player (and stable's own mod screen) calls it
    /// Co-op, so the label says both.
    /// </summary>
    public static string LabelFor(ChartMod mod)
        => mod == ChartMod.DualStages
            ? $"Co-op / {prototype(mod).Name} ({mod.Acronym()})"
            : $"{prototype(mod).Name} ({mod.Acronym()})";

    /// <summary>
    /// Whether lazer allows these two together <b>in the given ruleset</b>. Per-ruleset because the
    /// rules genuinely differ and a global answer is wrong in both directions: osu! is happy with
    /// Hidden and Flashlight together (HDFL is an ordinary osu! play) while osu!mania forbids it,
    /// since <c>ManiaModHidden</c> lists <c>ModFlashlight</c> among its exclusions and
    /// <c>OsuModHidden</c> does not. Two mods that ruleset doesn't both offer cannot conflict in it.
    /// </summary>
    public static bool Compatible(ChartMod a, ChartMod b, int rulesetId)
    {
        if (!prototypes_by_ruleset.TryGetValue(rulesetId, out var available))
            return true;

        if (!available.TryGetValue(a.Acronym(), out var first) || !available.TryGetValue(b.Acronym(), out var second))
            return true;

        return ModUtils.CheckCompatibleSet(new[] { first, second });
    }

    /// <summary>
    /// Narrows <paramref name="mods"/> to a set <paramref name="ruleset"/> actually accepts, keeping
    /// earlier entries when a later one clashes. The Chart tab already resolves conflicts as the
    /// user clicks, but it does so against the ruleset then on screen and the selection outlives
    /// any one song — so a pair that is legal in osu! and illegal in osu!mania (Hidden with
    /// Flashlight) would otherwise reach a mania chart. This is the last word before a mod is built.
    /// </summary>
    public static IReadOnlyList<Mod> Compatible(IEnumerable<Mod> mods, Ruleset ruleset)
    {
        var kept = new List<Mod>();

        foreach (var mod in mods)
        {
            if (ModUtils.CheckCompatibleSet(kept.Append(mod)))
                kept.Add(mod);
            else
                Logger.Log($"[chart mods] {mod.Acronym} dropped for {ruleset.ShortName} — incompatible with the rest of the selection there");
        }

        return kept;
    }

    /// <summary>The track adjustments a selection asks for, split into pitch-preserving tempo and
    /// pitch-shifting frequency — see <see cref="ReplayMods.TrackAdjustmentsFor"/>.</summary>
    public static (double Tempo, double Frequency) TrackAdjustmentsFor(IEnumerable<ChartMod> mods)
        => ReplayMods.TrackAdjustmentsFor(mods.Select(prototype));

    /// <summary>The categories the Chart tab shows, in lazer's own order, each with the mods that
    /// belong to it (empty categories are simply never built).</summary>
    public static IEnumerable<(ModType Type, IReadOnlyList<ChartMod> Mods)> Categories
        => all_mods.GroupBy(TypeOf)
                   .OrderBy(g => g.Key)
                   .Select(g => (g.Key, (IReadOnlyList<ChartMod>)g.ToArray()));

    /// <summary>Human heading for a category — lazer's enum names are PascalCase run-ons.</summary>
    public static string CategoryName(ModType type) => type switch
    {
        ModType.DifficultyReduction => "Difficulty reduction",
        ModType.DifficultyIncrease => "Difficulty increase",
        ModType.Conversion => "Conversion",
        _ => type.ToString(),
    };
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
    /// Whether these two mods may be worn together on the chart currently on screen. Per-ruleset,
    /// because the rules really do differ between them — see
    /// <see cref="ChartModCatalog.Compatible(ChartMod, ChartMod, int)"/>.
    /// </summary>
    public bool Compatible(ChartMod a, ChartMod b) => ChartModCatalog.Compatible(a, b, CurrentRulesetId);

    /// <summary>
    /// The online id of the ruleset the selected difficulty belongs to — what the mod rules and the
    /// Chart tab's row visibility are both judged against. Falls back to osu! before anything is
    /// playing, matching the tab's own fallback.
    /// </summary>
    public int CurrentRulesetId
    {
        get
        {
            var set = playback?.Current.Value;

            if (set == null || set.Difficulties.Count == 0)
                return 0;

            string? path = playback!.SelectedOsuFile.Value ?? set.PreferredOsuFile;

            return set.Difficulties.FirstOrDefault(d => d.Path == path)?.Mode ?? 0;
        }
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

        var resolved = Selected.Select(m => available.FirstOrDefault(a => a.Acronym == m.Acronym()))
                               .Where(m => m != null)
                               .Select(m => m!);

        // Resolving by acronym is already what keeps a 7K selection out of an osu! chart — osu!
        // simply has no "7K". The compatibility pass on top is for the subtler case of a pair that
        // is legal where it was picked and illegal where it lands (see the overload's remarks).
        return ChartModCatalog.Compatible(resolved, ruleset);
    }

    /// <summary>
    /// Pushes the selection's speed change onto playback. Rate is ruleset-independent (DT is 1.5×
    /// everywhere), so it is read off the catalogue's reference instances — and it applies whether
    /// or not the chart is being rendered, because a rate mod is a change to the song, not to the
    /// drawing of it. The conversion mods (key counts, Dual Stages, Mirror, Random) touch the track
    /// not at all, and contribute nothing here for exactly that reason.
    /// </summary>
    private void updateRate()
    {
        if (playback == null)
            return;

        double tempo = 1;
        double frequency = 1;

        if (!replayActive.Value)
            (tempo, frequency) = ChartModCatalog.TrackAdjustmentsFor(Selected);

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
