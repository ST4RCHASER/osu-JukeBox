#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings;

namespace JukeBox.Game.UI;

/// <summary>
/// The right column's middle tab, "Chart" — everything about the gameplay chart lazer renders over
/// the visuals, in one scrollable column presented with lazer's own settings components (the same
/// <see cref="LazerSection"/>/<see cref="SettingsCheckbox"/> language as the Settings tab beside
/// it):
///
/// <list type="bullet">
/// <item><b>Chart</b> — "Render chart" and "Play hit sounds", MOVED here from Settings → Gameplay
/// (same config keys, so existing values carry over and each setting still has exactly one
/// control).</item>
/// <item><b>Mods</b> — Easy / Half Time / Hard Rock / Hidden / Double Time / Nightcore /
/// Flashlight, applied to the autoplay chart via <see cref="ChartModSelection"/>. Locked and greyed
/// while a dropped replay is driving playback, which shows the replay's own mods instead — the same
/// treatment (and the same underlying "is a replay playing" test) the difficulty switcher already
/// uses.</item>
/// <item><b>Playfield elements</b> — one checkbox per <see cref="PlayfieldElement"/>, grouped by
/// ruleset with only the group(s) that apply to what is playing on screen.</item>
/// </list>
///
/// <para>
/// Mod changes rebuild the chart layer (they change beatmap conversion); element changes apply to
/// the chart already on screen without one. Both are persisted — see
/// <see cref="JukeBoxSetting.ChartMods"/> and <see cref="JukeBoxSetting.HiddenPlayfieldElements"/>.
/// </para>
/// </summary>
public partial class ChartPanel : CompositeDrawable
{
    /// <summary>The dimming a locked (replay-driven) control gets, matching
    /// <see cref="DifficultySwitcher"/>'s own locked state so the two read as one rule.</summary>
    private const float locked_alpha = 0.55f;

    // The exact DI lazer's SettingsPanel provides its subtree (same scheme SettingsOverlay and
    // PlaybackPanel cache): every settings control below resolves this for its purple palette.
    [Cached]
    private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved(canBeNull: true)]
    private ChartModSelection? chartMods { get; set; }

    [Resolved(canBeNull: true)]
    private PlayfieldElementVisibility? elements { get; set; }

    [Resolved(canBeNull: true)]
    private OsuConfigManager? lazerConfig { get; set; }

    private SettingsCheckbox renderChartCheckbox = null!;
    private SettingsCheckbox playHitSoundsCheckbox = null!;
    private SettingsCheckbox? hitLightingCheckbox;

    private readonly Dictionary<ChartMod, SettingsCheckbox> modCheckboxes = new Dictionary<ChartMod, SettingsCheckbox>();

    /// <summary>
    /// Checkbox-facing adapters for the mod rows. The replay lock lives on THESE bindables'
    /// <see cref="Bindable{T}.Disabled"/>, never on the selection's own — a disabled bindable throws
    /// on any write, including the programmatic ones the selection itself makes when resolving
    /// incompatibilities. Same shape as SettingsOverlay's "Play on main window too" adapter.
    /// </summary>
    private readonly Dictionary<ChartMod, BindableBool> modUi = new Dictionary<ChartMod, BindableBool>();

    private readonly Dictionary<PlayfieldElement, SettingsCheckbox> elementCheckboxes = new Dictionary<PlayfieldElement, SettingsCheckbox>();

    /// <summary>One per ruleset id (plus the ruleset-agnostic block), shown/hidden by
    /// <see cref="updateVisibleElementGroups"/>.</summary>
    private readonly Dictionary<int, Drawable> elementGroups = new Dictionary<int, Drawable>();

    private OsuSpriteText replayModsNote = null!;

    private readonly Bindable<CachedBeatmapSet?> currentSet = new Bindable<CachedBeatmapSet?>();
    private readonly Bindable<string?> selectedOsuFile = new Bindable<string?>();
    private readonly IBindable<bool> replayActive = new Bindable<bool>();
    private readonly IBindable<IReadOnlyList<string>> replayModAcronyms = new Bindable<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the tab's controls,
    /// so tests can drive/assert them without depending on this panel's internal layout.</summary>
    internal SettingsCheckbox RenderChartCheckbox => renderChartCheckbox;

    internal SettingsCheckbox PlayHitSoundsCheckbox => playHitSoundsCheckbox;

    internal SettingsCheckbox ModCheckbox(ChartMod mod) => modCheckboxes[mod];

    internal SettingsCheckbox ElementCheckbox(PlayfieldElement element) => elementCheckboxes[element];

    /// <summary>Test-only: whether a ruleset's element group is currently on screen.</summary>
    internal bool ElementGroupVisible(int rulesetId) => elementGroups[rulesetId].Alpha > 0;

    /// <summary>Test-only: the "mods come from the replay" line, empty when no replay is playing.</summary>
    internal string ReplayModsNote => replayModsNote.Text.ToString();

    /// <summary>Test-only: whether the mod rows are in their locked (replay) presentation.</summary>
    internal bool ModsLocked => modUi.Values.All(b => b.Disabled) && modCheckboxes.Values.All(c => c.Alpha < 1);

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        // Tooltip host for the whole tab, same wrapper SettingsOverlay and PlaybackPanel use.
        InternalChild = new OsuTooltipContainer(null!)
        {
            RelativeSizeAxes = Axes.Both,
            Child = new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                ScrollbarVisible = true,
                Child = createBody(),
            },
        };
    }

    private Drawable createBody()
    {
        var sections = new List<Drawable>
        {
            new OsuSpriteText
            {
                Font = OsuFont.TorusAlternate.With(size: 32),
                Text = "Chart",
                Margin = new MarginPadding { Left = SettingsPanel.CONTENT_PADDING.Left, Top = 18, Bottom = 4 },
            },
            new LazerSection("Chart", FontAwesome.Regular.DotCircle)
            {
                Children = new Drawable[]
                {
                    renderChartCheckbox = new SettingsCheckbox { LabelText = "Render chart" },
                    playHitSoundsCheckbox = new SettingsCheckbox { LabelText = "Play hit sounds" },
                },
            },
        };

        if (chartMods != null)
            sections.Add(createModsSection());

        if (elements != null)
            sections.Add(createElementsSection());

        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding { Bottom = 20 },
            Direction = FillDirection.Vertical,
            Children = sections,
        };
    }

    private Drawable createModsSection()
    {
        foreach (var mod in Enum.GetValues<ChartMod>())
        {
            modUi[mod] = new BindableBool();
            modCheckboxes[mod] = new SettingsCheckbox { LabelText = $"{mod.Label()} ({mod.Acronym()})" };
        }

        // The rows go straight into the section rather than into a wrapper flow of their own:
        // lazer's SettingsSection lays its own children out with the row spacing every other
        // settings row in the app has, and nesting a plain FillFlowContainer inside it swallowed
        // that — the mod rows came out visibly tighter than the ones above and below them.
        // Locking therefore dims each row (see updateReplayLock) instead of one wrapper.
        replayModsNote = new OsuSpriteText
        {
            // Alpha 0 (not removed) keeps it out of the flow entirely while no replay plays — a
            // zero-alpha child isn't IsPresent, so the surrounding flow leaves no gap.
            Alpha = 0,
            Colour = Theme.TextTertiary,
            Font = OsuFont.GetFont(size: 12),
            Margin = new MarginPadding
            {
                Left = SettingsPanel.CONTENT_PADDING.Left,
                Right = SettingsPanel.CONTENT_PADDING.Right,
                Bottom = 6,
            },
        };

        return new LazerSection("Mods", FontAwesome.Solid.SlidersH)
        {
            Children = new Drawable[] { replayModsNote }
                       .Concat(Enum.GetValues<ChartMod>().Select(m => (Drawable)modCheckboxes[m]))
                       .ToArray(),
        };
    }

    private Drawable createElementsSection()
    {
        var groups = new List<Drawable>();

        foreach (int rulesetId in new[] { PlayfieldElementCatalog.all_rulesets, 0, 1, 2, 3 })
        {
            var rows = new List<Drawable>();

            // "Hit lighting" is a real lazer gameplay setting rather than a skin lookup, so it
            // rides in the ruleset-agnostic group alongside the lookup-driven toggles rather than
            // staying behind in Settings — it is one of the playfield elements being switched on
            // and off, and splitting it across two tabs would only make it harder to find.
            if (rulesetId == PlayfieldElementCatalog.all_rulesets && lazerConfig != null)
                rows.Add(hitLightingCheckbox = new SettingsCheckbox { LabelText = "Hit lighting" });

            foreach (var entry in PlayfieldElementCatalog.All.Where(e => e.RulesetId == rulesetId))
            {
                var checkbox = new SettingsCheckbox { LabelText = entry.Label };
                elementCheckboxes[entry.Element] = checkbox;
                rows.Add(checkbox);
            }

            var group = new Container
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Child = new LazerSubsection(PlayfieldElementCatalog.RulesetName(rulesetId))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Children = rows,
                },
            };

            elementGroups[rulesetId] = group;
            groups.Add(group);
        }

        return new LazerSection("Playfield elements", FontAwesome.Regular.Eye) { Children = groups };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        renderChartCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RenderChart);
        playHitSoundsCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.PlayHitSounds);

        if (hitLightingCheckbox != null && lazerConfig != null)
            hitLightingCheckbox.Current = lazerConfig.GetBindable<bool>(OsuSetting.HitLighting);

        if (chartMods != null)
            bindMods(chartMods);

        if (elements != null)
        {
            foreach (var entry in PlayfieldElementCatalog.All)
                elementCheckboxes[entry.Element].Current = elements.Shown(entry.Element);
        }

        currentSet.BindTo(playback.Current);
        selectedOsuFile.BindTo(playback.SelectedOsuFile);
        currentSet.BindValueChanged(_ => updateVisibleElementGroups());
        selectedOsuFile.BindValueChanged(_ => updateVisibleElementGroups());
        updateVisibleElementGroups();
    }

    private void bindMods(ChartModSelection selection)
    {
        foreach (var mod in Enum.GetValues<ChartMod>())
        {
            var captured = mod;
            var ui = modUi[mod];
            var source = selection.Enabled(mod);

            modCheckboxes[mod].Current = ui;

            // Two-way adapter (see modUi's remarks): selection → UI lifts the disable for the
            // mirroring write, since the disable exists to stop USER edits, not programmatic ones.
            source.BindValueChanged(e =>
            {
                bool wasDisabled = ui.Disabled;
                ui.Disabled = false;
                ui.Value = e.NewValue;
                ui.Disabled = wasDisabled;
            }, true);

            ui.BindValueChanged(e => selection.Enabled(captured).Value = e.NewValue);
        }

        replayActive.BindTo(selection.ReplayActive);
        replayModAcronyms.BindTo(selection.ReplayModAcronyms);

        replayActive.BindValueChanged(_ => updateReplayLock(), true);
        replayModAcronyms.BindValueChanged(_ => updateReplayLock(), true);
    }

    /// <summary>
    /// A replay carries the mods it was played with, so while one drives playback the selection is
    /// inert: the rows lock and dim (matching <see cref="DifficultySwitcher"/>'s locked state) and a
    /// line above them names the mods actually in force. The user's own selection is left untouched
    /// underneath and comes straight back when the replay stops playing.
    /// </summary>
    private void updateReplayLock()
    {
        bool locked = replayActive.Value;

        foreach (var ui in modUi.Values)
            ui.Disabled = locked;

        foreach (var row in modCheckboxes.Values)
            row.FadeTo(locked ? locked_alpha : 1, Theme.HoverFadeDuration, Easing.OutQuint);

        var acronyms = replayModAcronyms.Value;

        replayModsNote.Text = !locked
            ? string.Empty
            : acronyms.Count > 0
                ? $"Replay is playing — its mods are in force: {string.Join(" ", acronyms)}"
                : "Replay is playing — it was a no-mod play";

        replayModsNote.Alpha = locked ? 1 : 0;
    }

    /// <summary>
    /// Only what the ruleset actually on screen can draw is shown — a taiko map has no slider ball
    /// to hide, and an osu!catch one has no hit-score popups even though every other mode does, so
    /// the shared group's individual rows are filtered too rather than just its whole block. Alpha 0
    /// rather than removal, so the hidden rows keep their live config bindables across mode changes
    /// and the flow leaves no gap for them. Falls back to osu! when nothing is playing yet.
    /// </summary>
    private void updateVisibleElementGroups()
    {
        int mode = currentMode();

        foreach (var entry in PlayfieldElementCatalog.All)
            elementCheckboxes[entry.Element].Alpha = entry.AppliesTo(mode) ? 1 : 0;

        foreach (var (rulesetId, group) in elementGroups)
        {
            bool anyRowApplies = PlayfieldElementCatalog.All.Any(e => e.RulesetId == rulesetId && e.AppliesTo(mode))
                                 // Hit lighting is a lazer gameplay setting rather than a catalogued
                                 // element, and it applies to every ruleset — so its group stays.
                                 || (rulesetId == PlayfieldElementCatalog.all_rulesets && hitLightingCheckbox != null);

            group.Alpha = anyRowApplies ? 1 : 0;
        }
    }

    private int currentMode()
    {
        var set = currentSet.Value;

        if (set == null || set.Difficulties.Count == 0)
            return 0;

        string? path = selectedOsuFile.Value ?? set.PreferredOsuFile;

        return set.Difficulties.FirstOrDefault(d => d.Path == path)?.Mode ?? 0;
    }
}
