#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using JukeBox.Game.Configuration;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Configuration;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Configuration;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Configuration;
using osu.Game.Rulesets.Mania.UI;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.Taiko.Configuration;
using osu.Game.Rulesets.UI;

namespace JukeBox.Game.Detach;

/// <summary>
/// The single mechanism by which the detached viewer window honours the main window's settings.
///
/// <para>
/// Every setting that changes what the player RENDERS lives in one of three config managers —
/// ours (<see cref="JukeBoxConfigManager"/>), lazer's game-wide one
/// (<see cref="OsuConfigManager"/>), or a per-ruleset one behind
/// <see cref="IRulesetConfigCache"/> — and every consumer downstream already binds those
/// bindables and reacts live. So rather than plumbing each setting through the sync protocol by
/// hand, this holds ONE registry of the settings that matter: the main process
/// <see cref="Capture"/>s them into the snapshot, the viewer process <see cref="Apply"/>s them
/// back into its own (separate-storage) config managers, and everything downstream updates live
/// for free. Adding a setting to <see cref="register"/> is the whole cost of syncing it.
/// </para>
///
/// <para>
/// Values cross the wire as strings because the three managers hold nine different value types
/// between them and a string dictionary is the one shape that survives the source-generated JSON
/// serializer without a context entry per type. Unknown keys are ignored on both sides, so a
/// viewer binary from a different build never chokes on a key it doesn't have.
/// </para>
///
/// <para>
/// Each entry holds its bindable in a FIELD, deliberately: <c>ConfigManager.GetBindable</c> hands
/// back a bound COPY that the manager references only weakly, so an entry that fetched a bindable
/// and dropped the reference would read correctly once and then freeze as soon as a GC ran — the
/// exact "I changed it and nothing happened" failure this class exists to prevent.
/// </para>
/// </summary>
public partial class SettingsMirror : Component
{
    [Resolved]
    private JukeBoxConfigManager jukeboxConfig { get; set; } = null!;

    [Resolved]
    private OsuConfigManager lazerConfig { get; set; } = null!;

    [Resolved]
    private IRulesetConfigCache rulesetConfigs { get; set; } = null!;

    private readonly List<Entry> entries = new List<Entry>();

    /// <summary>Test-only: every key currently registered.</summary>
    internal IEnumerable<string> Keys
    {
        get
        {
            foreach (var entry in entries)
                yield return entry.Key;
        }
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        register();
    }

    /// <summary>
    /// The registry. One line per synced setting; the key is arbitrary but must match on both
    /// sides, so it is namespaced by which manager owns the setting.
    /// </summary>
    private void register()
    {
        // Ruleset config managers are realm-backed and only exist once LazerRulesetConfigCache has
        // loaded on the update thread (its GetConfigFor throws before that, by design) — retry next
        // frame until it's ready, rather than registering a half-populated table.
        if (rulesetConfigs is Drawable { IsLoaded: false })
        {
            Schedule(register);
            return;
        }

        // ---- our own settings ----
        //
        // Skin is deliberately absent: the main process must send its RESOLVED skin (Random rolls
        // per song, and two independent rolls would show two different skins), so it travels as its
        // own snapshot field. CustomSkinPath likewise — it names a folder under the MAIN process's
        // storage, which does not exist under the viewer's.
        add<bool>(JukeBoxSetting.RenderChart);
        add<bool>(JukeBoxSetting.PlayHitSounds);
        add<double>(JukeBoxSetting.ChartOpacity);
        add<bool>(JukeBoxSetting.RemoveChartMask);
        add<bool>(JukeBoxSetting.RemoveStoryboardMask);
        add<double>(JukeBoxSetting.BackgroundDim);
        add<double>(JukeBoxSetting.BackgroundBlur);
        add<bool>(JukeBoxSetting.ShowStoryboardVideo);
        add<double>(JukeBoxSetting.PlayfieldZoom);
        add<double>(JukeBoxSetting.GlobalAudioOffset);
        add<string>(JukeBoxSetting.ChartMods);
        add<string>(JukeBoxSetting.HiddenPlayfieldElements);
        add<LazerPlayer.ChartConversionTarget>(JukeBoxSetting.ConvertToRuleset);
        add<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode);

        // ---- lazer's game-wide gameplay settings ----
        //
        // OsuSetting.VolumeInactive is deliberately absent: "duck while the window is unfocused" is
        // a property of the window the user is looking at, and mirroring it would duck the viewer
        // whenever the MAIN window lost focus — including when the user clicked the viewer.
        add<bool>(OsuSetting.HitLighting);
        add<bool>(OsuSetting.BeatmapSkins);
        add<bool>(OsuSetting.BeatmapColours);
        add<bool>(OsuSetting.BeatmapHitsounds);
        add<float>(OsuSetting.ComboColourNormalisationAmount);
        add<float>(OsuSetting.PositionalHitsoundsLevel);

        // ---- per-ruleset settings (the Chart tab's ruleset rows) ----
        //
        // catch is absent because it exposes none of its own in the Chart tab.
        if (configFor(new OsuRuleset()) is OsuRulesetConfigManager osuConfig)
        {
            add<OsuRulesetSetting, bool>(osuConfig, OsuRulesetSetting.SnakingInSliders);
            add<OsuRulesetSetting, bool>(osuConfig, OsuRulesetSetting.SnakingOutSliders);
            add<OsuRulesetSetting, bool>(osuConfig, OsuRulesetSetting.HitAnimations);
            add<OsuRulesetSetting, bool>(osuConfig, OsuRulesetSetting.ShowCursorTrail);
            add<OsuRulesetSetting, bool>(osuConfig, OsuRulesetSetting.ShowCursorRipples);
            add<OsuRulesetSetting, PlayfieldBorderStyle>(osuConfig, OsuRulesetSetting.PlayfieldBorderStyle);
            add<OsuRulesetSetting, bool>(osuConfig, OsuRulesetSetting.ReplayClickMarkersEnabled);
            add<OsuRulesetSetting, bool>(osuConfig, OsuRulesetSetting.ReplayFrameMarkersEnabled);
            add<OsuRulesetSetting, bool>(osuConfig, OsuRulesetSetting.ReplayCursorPathEnabled);
            add<OsuRulesetSetting, bool>(osuConfig, OsuRulesetSetting.ReplayCursorHideEnabled);
            add<OsuRulesetSetting, int>(osuConfig, OsuRulesetSetting.ReplayAnalysisDisplayLength);
        }

        if (configFor(new TaikoRuleset()) is TaikoRulesetConfigManager taikoConfig)
            add<TaikoRulesetSetting, bool>(taikoConfig, TaikoRulesetSetting.HitAnimations);

        if (configFor(new ManiaRuleset()) is ManiaRulesetConfigManager maniaConfig)
        {
            add<ManiaRulesetSetting, ManiaScrollingDirection>(maniaConfig, ManiaRulesetSetting.ScrollDirection);
            add<ManiaRulesetSetting, double>(maniaConfig, ManiaRulesetSetting.ScrollSpeed);
            add<ManiaRulesetSetting, bool>(maniaConfig, ManiaRulesetSetting.TimingBasedNoteColouring);
        }
    }

    private IRulesetConfigManager? configFor(Ruleset ruleset) => rulesetConfigs.GetConfigFor(ruleset);

    private void add<T>(JukeBoxSetting setting)
        where T : notnull
        => entries.Add(new Entry<T>($"jukebox:{setting}", jukeboxConfig.GetBindable<T>(setting)));

    private void add<T>(OsuSetting setting)
        where T : notnull
        => entries.Add(new Entry<T>($"lazer:{setting}", lazerConfig.GetBindable<T>(setting)));

    private void add<TLookup, T>(RulesetConfigManager<TLookup> config, TLookup setting)
        where TLookup : struct, Enum
        where T : notnull
        => entries.Add(new Entry<T>($"ruleset:{typeof(TLookup).Name}.{setting}", config.GetBindable<T>(setting)));

    /// <summary>
    /// Every registered setting's current value, keyed for <see cref="Apply"/> on the other side.
    /// Called on the main process's update thread once per sync tick.
    /// </summary>
    public Dictionary<string, string> Capture()
    {
        var captured = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);

        foreach (var entry in entries)
            captured[entry.Key] = entry.Read();

        return captured;
    }

    /// <summary>
    /// Writes a captured set back into this process's config managers. Keys we don't know are
    /// skipped (a viewer built from different source), as are values that don't parse — a torn
    /// setting must not take the rest of the snapshot down with it. Writing a bindable its own
    /// current value is a no-op, so re-applying an unchanged snapshot at the heartbeat rate costs
    /// nothing downstream.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, string>? values)
    {
        if (values == null)
            return;

        foreach (var entry in entries)
        {
            if (values.TryGetValue(entry.Key, out string? raw))
                entry.Write(raw);
        }
    }

    private abstract class Entry
    {
        public abstract string Key { get; }
        public abstract string Read();
        public abstract void Write(string raw);
    }

    private sealed class Entry<T> : Entry
        where T : notnull
    {
        // Strong reference — see this class's remarks on GetBindable's weakly-held copy.
        private readonly Bindable<T> bindable;

        public override string Key { get; }

        public Entry(string key, Bindable<T> bindable)
        {
            Key = key;
            this.bindable = bindable;
        }

        public override string Read() => encode(bindable.Value);

        public override void Write(string raw)
        {
            if (tryDecode(raw, out var value))
                bindable.Value = value;
        }

        private static string encode(T value) => value switch
        {
            bool b => b ? "1" : "0",
            int i => i.ToString(CultureInfo.InvariantCulture),
            // Round-trippable: the default double format loses the last digit or two, which would
            // make an offset or scroll speed drift by a hair every time it crossed the wire.
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            string s => s,
            Enum e => e.ToString(),
            _ => throw new NotSupportedException($"{typeof(T)} has no sync encoding — add one to {nameof(SettingsMirror)}."),
        };

        private static bool tryDecode(string raw, out T value)
        {
            var type = typeof(T);
            object? parsed = null;

            if (type == typeof(bool))
                parsed = raw == "1";
            else if (type == typeof(string))
                parsed = raw;
            else if (type == typeof(int))
            {
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                    parsed = i;
            }
            else if (type == typeof(double))
            {
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    parsed = d;
            }
            else if (type == typeof(float))
            {
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    parsed = f;
            }
            else if (type.IsEnum)
            {
                if (Enum.TryParse(type, raw, out object? e))
                    parsed = e;
            }
            else
                throw new NotSupportedException($"{typeof(T)} has no sync decoding — add one to {nameof(SettingsMirror)}.");

            if (parsed == null)
            {
                value = default!;
                return false;
            }

            value = (T)parsed;
            return true;
        }
    }
}
