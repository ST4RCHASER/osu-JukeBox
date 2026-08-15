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
using osu.Game.Rulesets.Mods;

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
/// <item><b>Mods</b> — applied to the autoplay chart via <see cref="ChartModSelection"/>, grouped
/// into lazer's own <see cref="ModType"/> categories and narrowed to the mods the playing ruleset
/// actually offers: Easy / Half Time / Hard Rock / Hidden / Double Time / Nightcore / Flashlight
/// everywhere, plus osu!mania's key counts, Co-op (Dual Stages) and Fade In there, plus Mirror and
/// Random wherever lazer has them. Locked and greyed while a dropped replay is driving playback,
/// which shows the replay's own mods instead — the same treatment (and the same underlying "is a
/// replay playing" test) the difficulty switcher already uses.</item>
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
    /// A wrapper per mod row, carrying the "does this ruleset even have this mod?" visibility —
    /// kept OFF the checkbox itself, whose own <see cref="Drawable.Alpha"/> is the replay lock's
    /// dimming. Two independent alphas that multiply, so a locked row that this ruleset doesn't
    /// offer stays gone rather than reappearing at 0.55.
    /// </summary>
    private readonly Dictionary<ChartMod, Container> modRowHosts = new Dictionary<ChartMod, Container>();

    /// <summary>One per <see cref="ModType"/> category actually built, hidden entirely when the
    /// playing ruleset offers none of its mods.</summary>
    private readonly Dictionary<ModType, Drawable> modCategories = new Dictionary<ModType, Drawable>();

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

    /// <summary>Explains the permanently-inapplicable conversion mods; shown whenever any of those
    /// rows is on screen. Null when no such category was built.</summary>
    private OsuSpriteText? convertsOnlyNote;

    private readonly Bindable<CachedBeatmapSet?> currentSet = new Bindable<CachedBeatmapSet?>();
    private readonly Bindable<string?> selectedOsuFile = new Bindable<string?>();
    private readonly IBindable<bool> replayActive = new Bindable<bool>();
    private readonly IBindable<IReadOnlyList<string>> replayModAcronyms = new Bindable<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the tab's controls,
    /// so tests can drive/assert them without depending on this panel's internal layout.</summary>
    internal SettingsCheckbox RenderChartCheckbox => renderChartCheckbox;

    internal SettingsCheckbox PlayHitSoundsCheckbox => playHitSoundsCheckbox;

    internal SettingsCheckbox ModCheckbox(ChartMod mod) => modCheckboxes[mod];

    /// <summary>Test-only: whether a mod's row is offered for what is currently playing.</summary>
    internal bool ModOffered(ChartMod mod) => modRowHosts[mod].Alpha > 0;

    /// <summary>Test-only: whether a mod category's block is on screen at all.</summary>
    internal bool ModCategoryVisible(ModType type) => modCategories[type].Alpha > 0;

    internal SettingsCheckbox ElementCheckbox(PlayfieldElement element) => elementCheckboxes[element];

    /// <summary>Test-only: whether a ruleset's element group is currently on screen.</summary>
    internal bool ElementGroupVisible(int rulesetId) => elementGroups[rulesetId].Alpha > 0;

    /// <summary>Test-only: the "mods come from the replay" line, empty when no replay is playing.</summary>
    internal string ReplayModsNote => replayModsNote.Text.ToString();

    /// <summary>
    /// Test-only: whether the mod rows are in their locked (replay) presentation — EVERY mod's
    /// toggle refuses to move, and every row actually on screen is dimmed. The dimming is only
    /// asserted for offered rows because osu!framework does not update a subtree that isn't present,
    /// so a row this ruleset doesn't offer never runs its fade — which is invisible either way, and
    /// self-corrects the moment the row is shown.
    /// </summary>
    internal bool ModsLocked
        => modUi.Values.All(b => b.Disabled)
           && modCheckboxes.Where(pair => ModOffered(pair.Key)).All(pair => pair.Value.Alpha < 1);

    /// <summary>Test-only: whether a row is marked inapplicable (greyed and refusing input) because
    /// its mod can only act on a converted beatmap.</summary>
    internal bool ModInert(ChartMod mod) => modUi[mod].Disabled && ChartModCatalog.AppliesOnlyToConverts(mod);

    /// <summary>Test-only: whether the "only applies to converted beatmaps" line is showing.</summary>
    internal bool ConvertsOnlyNoteVisible => convertsOnlyNote?.Alpha > 0;

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

        var blocks = new List<Drawable> { replayModsNote };

        // Grouped by lazer's own ModType rather than by a layout invented here — which lands on
        // Difficulty reduction / Difficulty increase / Conversion, the same three rows stable's
        // mania mod screen uses (its "Special" column is lazer's Conversion). Twenty toggles as one
        // flat list would be unreadable; these are the game's own categories.
        foreach (var (type, mods) in ChartModCatalog.Categories)
        {
            var rows = new List<Drawable>();

            // The rows that can only ever act on a converted beatmap get a line saying so, right
            // above them — see ChartModCatalog.AppliesOnlyToConverts.
            if (mods.Any(ChartModCatalog.AppliesOnlyToConverts))
            {
                rows.Add(convertsOnlyNote = new OsuSpriteText
                {
                    Alpha = 0,
                    Colour = Theme.TextTertiary,
                    Font = OsuFont.GetFont(size: 12),
                    Text = "Key counts and Co-op only apply to converted beatmaps — this map is already in its own mode.",
                    Margin = new MarginPadding
                    {
                        Left = SettingsPanel.CONTENT_PADDING.Left,
                        Right = SettingsPanel.CONTENT_PADDING.Right,
                        Bottom = 6,
                    },
                });
            }

            foreach (var mod in mods)
            {
                modUi[mod] = new BindableBool();
                modCheckboxes[mod] = new SettingsCheckbox { LabelText = mod.Label() };

                // See modRowHosts: availability lives on this wrapper so it can't fight the
                // replay lock's dimming of the checkbox inside it.
                rows.Add(modRowHosts[mod] = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Child = modCheckboxes[mod],
                });
            }

            var block = new Container
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Child = new LazerSubsection(ChartModCatalog.CategoryName(type))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Children = rows,
                },
            };

            modCategories[type] = block;
            blocks.Add(block);
        }

        return new LazerSection("Mods", FontAwesome.Solid.SlidersH) { Children = blocks };
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
        currentSet.BindValueChanged(_ => updateForCurrentRuleset());
        selectedOsuFile.BindValueChanged(_ => updateForCurrentRuleset());
        updateForCurrentRuleset();
    }

    /// <summary>Both halves of the tab narrow to what the chart on screen can actually use.</summary>
    private void updateForCurrentRuleset()
    {
        int mode = currentMode();

        updateVisibleModRows(mode);
        updateVisibleElementGroups(mode);
    }

    /// <summary>
    /// Only the mods the playing ruleset actually offers are shown, asked of lazer rather than
    /// declared (see <see cref="ChartModCatalog.OfferedBy"/>) — so osu!mania's key counts, Dual
    /// Stages and Fade In appear only there, while Mirror and Random appear wherever lazer really
    /// has them, which is more rulesets than mania alone. A category whose every row is hidden goes
    /// with them, header and all.
    ///
    /// <para>
    /// A hidden row keeps its live bindable and its value: an unplayable selection is left alone
    /// rather than cleared, so switching a mania set's difficulty away and back doesn't cost the
    /// user their 7K. It cannot leak into another ruleset's chart either way —
    /// <see cref="ChartModSelection.CreateFor"/> only ever materialises what that ruleset offers.
    /// </para>
    /// </summary>
    private void updateVisibleModRows(int mode)
    {
        if (chartMods == null)
            return;

        foreach (var (mod, host) in modRowHosts)
            host.Alpha = ChartModCatalog.OfferedBy(mod, mode) ? 1 : 0;

        foreach (var (type, block) in modCategories)
            block.Alpha = ChartModCatalog.Categories.First(c => c.Type == type).Mods.Any(m => ChartModCatalog.OfferedBy(m, mode)) ? 1 : 0;

        if (convertsOnlyNote != null)
        {
            convertsOnlyNote.Alpha = ChartModCatalog.Categories
                                                    .SelectMany(c => c.Mods)
                                                    .Any(m => ChartModCatalog.AppliesOnlyToConverts(m) && ChartModCatalog.OfferedBy(m, mode))
                ? 1
                : 0;
        }

        updateModRowStates();
    }

    /// <summary>
    /// A mod row refuses to move for either of two reasons, and both dim it the same way: a replay
    /// is driving playback (so the mods are the replay's, not the user's), or the mod can only act
    /// on a converted beatmap and this app never renders one (see
    /// <see cref="ChartModCatalog.AppliesOnlyToConverts"/>). Kept in one place so the two can't
    /// fight over the same bindable.
    /// </summary>
    private void updateModRowStates()
    {
        bool replayLocked = replayActive.Value;

        foreach (var (mod, ui) in modUi)
        {
            bool inert = ChartModCatalog.AppliesOnlyToConverts(mod);

            ui.Disabled = replayLocked || inert;
            modCheckboxes[mod].FadeTo(replayLocked || inert ? locked_alpha : 1, Theme.HoverFadeDuration, Easing.OutQuint);
        }
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

        updateModRowStates();

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
    private void updateVisibleElementGroups(int mode)
    {
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
