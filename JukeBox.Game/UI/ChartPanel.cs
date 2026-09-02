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
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Configuration;
using osu.Game.Rulesets.Mania.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.Taiko.Configuration;
using osu.Game.Rulesets.UI;

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
/// <item><b>Playfield elements</b> — grouped by ruleset, with EVERY ruleset's group listed at all
/// times (user request): a stable, complete inventory rather than a view of the current song, so
/// the panel never reshuffles when the track changes, and a row picked for a ruleset that isn't
/// playing takes effect when a map of that ruleset does. Each group holds that ruleset's element
/// toggles (one per <see cref="PlayfieldElement"/>) first and then its own gameplay settings —
/// snaking, hit animations, mania's scroll speed and direction, osu!'s replay-analysis overlays —
/// which MOVED here from Settings and keep their real lazer per-ruleset config bindables. Those are
/// rows of their ruleset's group rather than a category of their own (user request).</item>
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

    [Resolved(canBeNull: true)]
    private IRulesetConfigCache? rulesetConfigs { get; set; }

    [Resolved(canBeNull: true)]
    private ChartConversion? conversion { get; set; }

    // ---- per-ruleset settings, moved here from Settings; only built with the ruleset config cache ----
    private SettingsCheckbox snakingInCheckbox = null!;
    private SettingsCheckbox snakingOutCheckbox = null!;
    private SettingsCheckbox osuHitAnimationsCheckbox = null!;
    private SettingsCheckbox cursorTrailCheckbox = null!;
    private SettingsCheckbox cursorRipplesCheckbox = null!;
    private SettingsEnumDropdown<PlayfieldBorderStyle> playfieldBorderDropdown = null!;
    private SettingsCheckbox taikoHitAnimationsCheckbox = null!;
    private SettingsEnumDropdown<ManiaScrollingDirection> maniaDirectionDropdown = null!;
    private SettingsSlider<double> maniaScrollSpeedRow = null!;
    private SettingsCheckbox maniaTimingColourCheckbox = null!;

    // ---- replay analysis (osu! ruleset config), moved here from Settings ----
    private SettingsCheckbox clickMarkersCheckbox = null!;
    private SettingsCheckbox frameMarkersCheckbox = null!;
    private SettingsCheckbox cursorPathCheckbox = null!;
    private SettingsCheckbox hideCursorCheckbox = null!;
    private SettingsSlider<int> analysisLengthRow = null!;

    // Ranged local for the display-length slider, synced two-way with the config value: the config
    // bindable is declared rangeless upstream, and BindTo would copy that (unusable) range onto
    // whatever binds it.
    private readonly BindableInt analysisDisplayLength = new BindableInt(800) { MinValue = 200, MaxValue = 2000, Precision = 100 };
    private Bindable<int>? analysisLengthConfig;

    private SettingsCheckbox renderChartCheckbox = null!;
    private SettingsCheckbox playHitSoundsCheckbox = null!;

    private SettingsSlider<double> chartOpacityRow = null!;

    /// <summary>
    /// Slider-facing adapter for <see cref="JukeBoxSetting.ChartOpacity"/>. The "only while the
    /// chart is rendered" grey-out lives on THIS bindable's Disabled, never on the config bindable
    /// itself — same reason as every other adapter here: a disabled config bindable makes any
    /// programmatic write throw, and this one is written by the settings mirror.
    /// </summary>
    private readonly BindableDouble chartOpacityUi = new BindableDouble(1)
    {
        MinValue = 0,
        MaxValue = 1,
        Precision = 0.01,
    };

    private Bindable<double>? chartOpacityConfig;

    /// <summary>"Convert to", and the line explaining why it is greyed when the map on screen has
    /// no conversion available.</summary>
    private SettingsEnumDropdown<ChartConversionTarget>? convertDropdown;

    private OsuTextFlowContainer? convertNote;

    /// <summary>
    /// Dropdown-facing adapter for the conversion target. The "nothing to convert" grey-out lives on
    /// THIS bindable's Disabled, never on the service's own — that one is the config bindable, and a
    /// disabled config bindable makes every programmatic SetValue throw. Same shape as the mod rows.
    /// </summary>
    private readonly Bindable<ChartConversionTarget> convertUi = new Bindable<ChartConversionTarget>();

    private readonly IBindable<bool> sourceConvertible = new BindableBool();

    private readonly IBindable<bool> isConverting = new BindableBool();

    private readonly IBindable<int> effectiveRulesetId = new Bindable<int>();
    private SettingsCheckbox? hitLightingCheckbox;

    private readonly Dictionary<ChartMod, SettingsCheckbox> modCheckboxes = new Dictionary<ChartMod, SettingsCheckbox>();

    /// <summary>
    /// A wrapper per mod row, carrying the "does this ruleset even have this mod?" visibility —
    /// kept OFF the checkbox itself, whose own <see cref="Drawable.Alpha"/> is the replay lock's
    /// dimming. Two independent alphas that multiply, so a locked row that this ruleset doesn't
    /// offer stays gone rather than reappearing at 0.55.
    /// </summary>
    private readonly Dictionary<ChartMod, Container> modRowHosts = new Dictionary<ChartMod, Container>();

    /// <summary>The collapsed key-count control (checkbox + 1-9 value) and its own availability
    /// wrapper, standing in for the nine <c>ManiaModKeyN</c> rows.</summary>
    private SettingsCheckbox keyOverrideCheckbox = null!;

    private SettingsSlider<int> keyCountRow = null!;

    private Container keyOverrideHost = null!;

    private readonly BindableBool keyOverrideUi = new BindableBool();

    private readonly BindableInt keyCountUi = new BindableInt(4)
    {
        MinValue = ChartModCatalog.min_key_count,
        MaxValue = ChartModCatalog.max_key_count,
        Precision = 1,
    };

    /// <summary>Guards the selection→controls direction from being echoed straight back.</summary>
    private bool applyingKeySelection;

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

    private OsuTextFlowContainer replayModsNote = null!;

    /// <summary>What <see cref="replayModsNote"/> currently says. Kept alongside it because
    /// <see cref="TextFlowContainer.Text"/> is write-only.</summary>
    private string replayModsNoteText = string.Empty;

    /// <summary>Explains the permanently-inapplicable conversion mods; shown whenever any of those
    /// rows is on screen. Null when no such category was built.</summary>
    private OsuTextFlowContainer? convertsOnlyNote;

    private readonly Bindable<CachedBeatmapSet?> currentSet = new Bindable<CachedBeatmapSet?>();
    private readonly Bindable<string?> selectedOsuFile = new Bindable<string?>();
    private readonly IBindable<bool> replayActive = new Bindable<bool>();
    private readonly IBindable<IReadOnlyList<string>> replayModAcronyms = new Bindable<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the tab's controls,
    /// so tests can drive/assert them without depending on this panel's internal layout.</summary>
    internal SettingsCheckbox RenderChartCheckbox => renderChartCheckbox;

    internal SettingsCheckbox PlayHitSoundsCheckbox => playHitSoundsCheckbox;

    /// <summary>Test-only access to the chart-opacity row and its dependent-row state.</summary>
    internal SettingsSlider<double> ChartOpacitySlider => chartOpacityRow;

    /// <summary>Test-only: whether the opacity row refuses input because nothing is being
    /// rendered for it to apply to.</summary>
    internal bool ChartOpacityInert => chartOpacityUi.Disabled;

    internal SettingsCheckbox ModCheckbox(ChartMod mod) => modCheckboxes[mod];

    /// <summary>Test-only: whether a mod's row is offered for what is currently playing. Key counts
    /// have no row of their own any more — see <see cref="KeyOverrideOffered"/>.</summary>
    internal bool ModOffered(ChartMod mod) => modRowHosts[mod].Alpha > 0;

    /// <summary>Test-only access to the "Convert to" control and its explanation.</summary>
    internal SettingsDropdown<ChartConversionTarget> ConvertDropdown => convertDropdown!;

    internal bool ConvertNoteVisible => convertNote?.Alpha > 0;

    internal bool ConvertInert => convertUi.Disabled;

    /// <summary>Test-only access to the collapsed key-count control.</summary>
    internal SettingsCheckbox KeyOverrideCheckbox => keyOverrideCheckbox;

    internal SettingsSlider<int> KeyCountSlider => keyCountRow;

    internal bool KeyOverrideOffered => keyOverrideHost.Alpha > 0;

    /// <summary>Test-only: whether the key-count control refuses input because it can only act on a
    /// converted beatmap.</summary>
    internal bool KeyOverrideInert => keyOverrideUi.Disabled && keyCountUi.Disabled;

    /// <summary>Test-only: whether a mod category's block is on screen at all.</summary>
    internal bool ModCategoryVisible(ModType type) => modCategories[type].Alpha > 0;

    /// <summary>Test-only: the mania scroll-speed slider, which moved here from Settings.</summary>
    internal SettingsSlider<double> ManiaScrollSpeedSlider => maniaScrollSpeedRow;

    internal SettingsCheckbox ElementCheckbox(PlayfieldElement element) => elementCheckboxes[element];

    /// <summary>Test-only: whether a ruleset's element group is currently on screen.</summary>
    internal bool ElementGroupVisible(int rulesetId) => elementGroups[rulesetId].Alpha > 0;

    /// <summary>Test-only: a ruleset's element group, so tests can assert what is parented INSIDE
    /// it rather than merely present somewhere in the tab.</summary>
    internal Drawable ElementGroup(int rulesetId) => elementGroups[rulesetId];

    /// <summary>Test-only: the "mods come from the replay" line, empty when no replay is playing.</summary>
    internal string ReplayModsNote => replayModsNoteText;

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
                    // Indented under the checkbox it depends on, the same dependent-row shape the
                    // key-count value uses: with nothing being rendered there is nothing for an
                    // opacity to apply to, so the row greys out and refuses input alongside it.
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding { Left = 24 },
                        Child = chartOpacityRow = new SettingsSlider<double>
                        {
                            LabelText = "Chart opacity",
                            DisplayAsPercentage = true,
                            KeyboardStep = 0.01f,
                        },
                    },
                    playHitSoundsCheckbox = new SettingsCheckbox { LabelText = "Play hit sounds" },
                },
            },
        };

        if (conversion != null)
        {
            // Playing a map as another mode is a property of the chart itself rather than of any
            // one ruleset, so it belongs with "render it at all" at the top.
            ((LazerSection)sections[^1]).Add(convertDropdown = new SettingsEnumDropdown<ChartConversionTarget> { LabelText = "Convert to" });
            ((LazerSection)sections[^1]).Add(convertNote = note("Only osu! maps can be played as another mode — this one is already in a mode of its own."));
        }

        // Deliberate order, widest scope first: what to draw at all (Chart), then what is being
        // played (Mods), then everything about how the playfield itself draws — which pieces show
        // and how each ruleset renders them, all inside the one Playfield elements section.
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

    /// <summary>
    /// Ruleset config managers are realm-backed and only exist once <c>LazerRulesetConfigCache</c>
    /// has loaded on the update thread (its GetConfigFor throws before that, by design) — retry
    /// next frame until it's ready. The bound bindables are the REAL per-ruleset config values, so
    /// the DrawableRuleset pieces that bind them (snaking, cursor trail, mania scroll speed and
    /// direction) react live on the chart already on screen; the rest apply on the next chart
    /// (re)build. Unchanged from when this lived in SettingsOverlay.
    /// </summary>
    private void bindRulesetConfigs()
    {
        if (rulesetConfigs is Drawable { IsLoaded: false })
        {
            Schedule(bindRulesetConfigs);
            return;
        }

        if (rulesetConfigs!.GetConfigFor(new OsuRuleset()) is OsuRulesetConfigManager osuRulesetConfig)
        {
            snakingInCheckbox.Current = osuRulesetConfig.GetBindable<bool>(OsuRulesetSetting.SnakingInSliders);
            snakingOutCheckbox.Current = osuRulesetConfig.GetBindable<bool>(OsuRulesetSetting.SnakingOutSliders);
            osuHitAnimationsCheckbox.Current = osuRulesetConfig.GetBindable<bool>(OsuRulesetSetting.HitAnimations);
            cursorTrailCheckbox.Current = osuRulesetConfig.GetBindable<bool>(OsuRulesetSetting.ShowCursorTrail);
            cursorRipplesCheckbox.Current = osuRulesetConfig.GetBindable<bool>(OsuRulesetSetting.ShowCursorRipples);
            playfieldBorderDropdown.Current = osuRulesetConfig.GetBindable<PlayfieldBorderStyle>(OsuRulesetSetting.PlayfieldBorderStyle);

            clickMarkersCheckbox.Current = osuRulesetConfig.GetBindable<bool>(OsuRulesetSetting.ReplayClickMarkersEnabled);
            frameMarkersCheckbox.Current = osuRulesetConfig.GetBindable<bool>(OsuRulesetSetting.ReplayFrameMarkersEnabled);
            cursorPathCheckbox.Current = osuRulesetConfig.GetBindable<bool>(OsuRulesetSetting.ReplayCursorPathEnabled);
            hideCursorCheckbox.Current = osuRulesetConfig.GetBindable<bool>(OsuRulesetSetting.ReplayCursorHideEnabled);

            // Two-way sync (see analysisDisplayLength's remarks) instead of a direct bind.
            analysisLengthConfig = osuRulesetConfig.GetBindable<int>(OsuRulesetSetting.ReplayAnalysisDisplayLength);
            analysisDisplayLength.Value = analysisLengthConfig.Value;
            analysisDisplayLength.BindValueChanged(e => analysisLengthConfig!.Value = e.NewValue);
            analysisLengthConfig.BindValueChanged(e => analysisDisplayLength.Value = e.NewValue);
            analysisLengthRow.Current = analysisDisplayLength;
        }

        if (rulesetConfigs.GetConfigFor(new TaikoRuleset()) is TaikoRulesetConfigManager taikoRulesetConfig)
            taikoHitAnimationsCheckbox.Current = taikoRulesetConfig.GetBindable<bool>(TaikoRulesetSetting.HitAnimations);

        if (rulesetConfigs.GetConfigFor(new ManiaRuleset()) is ManiaRulesetConfigManager maniaRulesetConfig)
        {
            maniaDirectionDropdown.Current = maniaRulesetConfig.GetBindable<ManiaScrollingDirection>(ManiaRulesetSetting.ScrollDirection);
            maniaScrollSpeedRow.Current = maniaRulesetConfig.GetBindable<double>(ManiaRulesetSetting.ScrollSpeed);
            maniaTimingColourCheckbox.Current = maniaRulesetConfig.GetBindable<bool>(ManiaRulesetSetting.TimingBasedNoteColouring);
        }
    }

    /// <summary>
    /// An explanatory line under a section header, in the same shape (and with the same content
    /// padding) as the one SettingsOverlay puts under its OAuth rows. A flow container rather than
    /// a plain sprite because these sentences are longer than the 340px column: a single-line
    /// <see cref="OsuSpriteText"/> simply ran off the panel's right edge.
    /// </summary>
    private static OsuTextFlowContainer note(string text) => new OsuTextFlowContainer(t => t.Font = OsuFont.GetFont(size: 12))
    {
        Alpha = 0,
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Colour = Theme.TextTertiary,
        Text = text,
        Padding = new MarginPadding
        {
            Left = SettingsPanel.CONTENT_PADDING.Left,
            Right = SettingsPanel.CONTENT_PADDING.Right,
            Bottom = 6,
        },
    };

    private Drawable createModsSection()
    {
        // Alpha 0 (not removed) keeps a note out of the flow entirely while it has nothing to say —
        // a zero-alpha child isn't IsPresent, so the surrounding flow leaves no gap.
        replayModsNote = note(string.Empty);

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
                rows.Add(convertsOnlyNote = note("The key count and Co-op only apply to beatmaps converted from another mode — this map is already in its own."));
            }

            // The nine key-count mods collapse into one checkbox plus a 1-9 value (user request):
            // they are mutually exclusive by nature, so nine rows were nine ways of saying one
            // number. Built where the first of them would have appeared, so the category's order is
            // otherwise unchanged.
            if (mods.Any(m => ChartModCatalog.KeyCountOf(m) != null))
                rows.Add(keyOverrideHost = createKeyCountControl());

            foreach (var mod in mods)
            {
                if (ChartModCatalog.KeyCountOf(mod) != null)
                    continue;

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

    /// <summary>
    /// The nine <c>ManiaModKeyN</c> rows as one control: a checkbox that says whether the key count
    /// is being overridden at all, and a bounded 1-9 value that says to what. Ticked plus N selects
    /// <c>ManiaModKeyN</c> and nothing else; unticked selects none. Nothing about how the mod is
    /// resolved changed — <see cref="ChartModSelection"/> still holds one <see cref="ChartMod"/> per
    /// key count and still materialises it by acronym from the ruleset's own mods.
    ///
    /// <para>
    /// The value is a lazer <see cref="SettingsSlider{T}"/> over a bounded
    /// <see cref="BindableInt"/>, which is what lazer itself uses for a bounded integer setting —
    /// out-of-range is unreachable by construction rather than clamped after the fact, so there is
    /// no typed value to silently lose. The count is mirrored into the row's own label because a
    /// slider otherwise only shows its value in a hover tooltip, and a key count you have to hover
    /// to read is not much of an answer to "which one is selected".
    /// </para>
    /// </summary>
    private Container createKeyCountControl()
    {
        keyOverrideCheckbox = new SettingsCheckbox { LabelText = "Override key count" };

        keyCountRow = new SettingsSlider<int>
        {
            LabelText = keyCountLabel(keyCountUi.Value),
            KeyboardStep = 1,
        };

        return new Container
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Child = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Children = new Drawable[]
                {
                    keyOverrideCheckbox,
                    // Indented under its checkbox, the same dependent-row shape SettingsOverlay
                    // uses for "Play on main window too".
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding { Left = 24 },
                        Child = keyCountRow,
                    },
                },
            },
        };
    }

    private static string keyCountLabel(int keys) => $"Keys: {keys}";

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

            // Then how that ruleset draws what's left — the rows that used to be a "Rulesets"
            // section of their own. Visibility first, behaviour second: "is this drawn at all" is
            // the coarser question and answering it can make the finer one moot (there is no point
            // tuning slider snaking with slider bodies hidden), and it keeps the section's original
            // list unbroken at the top of every group.
            if (rulesetConfigs != null)
                rows.AddRange(rulesetBehaviourRows(rulesetId));

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

    /// <summary>
    /// The per-ruleset gameplay rows that used to be their own "Rulesets" section, now living in
    /// the group for the ruleset they belong to (user request: no separate category). Nothing about
    /// their bindings changed — they are still lazer's own per-ruleset config values, wired in
    /// <see cref="bindRulesetConfigs"/>, revert arrows and all. This is a re-parent in the UI only.
    ///
    /// <para>
    /// osu!'s replay-analysis overlays keep their own heading as a nested subsection rather than
    /// being mixed into the list: they draw on top of the playfield rather than being part of it,
    /// and five more unlabelled rows would have made the longest group unreadable.
    /// </para>
    /// </summary>
    private IEnumerable<Drawable> rulesetBehaviourRows(int rulesetId)
    {
        switch (rulesetId)
        {
            case 0:
                return new Drawable[]
                {
                    snakingInCheckbox = new SettingsCheckbox { LabelText = "Snaking in sliders" },
                    snakingOutCheckbox = new SettingsCheckbox { LabelText = "Snaking out sliders" },
                    osuHitAnimationsCheckbox = new SettingsCheckbox { LabelText = "Hit animations" },
                    cursorTrailCheckbox = new SettingsCheckbox { LabelText = "Cursor trail" },
                    cursorRipplesCheckbox = new SettingsCheckbox { LabelText = "Cursor ripples" },
                    playfieldBorderDropdown = new SettingsEnumDropdown<PlayfieldBorderStyle> { LabelText = "Playfield border style" },
                    // Labelled "(osu!)" even though it is nested inside the osu! group: lazer's
                    // subsection heading is one size, so a nested one is indistinguishable from a
                    // sibling and read as a fifth ruleset in the real window. The suffix — which is
                    // also what this block was called in Settings — carries the parentage that the
                    // visual hierarchy cannot.
                    new LazerSubsection("Analysis (osu!)")
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Children = new Drawable[]
                        {
                            clickMarkersCheckbox = new SettingsCheckbox { LabelText = "Show click markers" },
                            frameMarkersCheckbox = new SettingsCheckbox { LabelText = "Show frame markers" },
                            cursorPathCheckbox = new SettingsCheckbox { LabelText = "Show cursor path" },
                            hideCursorCheckbox = new SettingsCheckbox { LabelText = "Hide gameplay cursor" },
                            analysisLengthRow = new SettingsSlider<int> { LabelText = "Display length", KeyboardStep = 100 },
                        },
                    },
                };

            case 1:
                return new Drawable[]
                {
                    taikoHitAnimationsCheckbox = new SettingsCheckbox { LabelText = "Hit animations" },
                };

            case 3:
                return new Drawable[]
                {
                    maniaDirectionDropdown = new SettingsEnumDropdown<ManiaScrollingDirection> { LabelText = "Scrolling direction" },
                    maniaScrollSpeedRow = new SettingsSlider<double> { LabelText = "Scroll speed", KeyboardStep = 0.5f },
                    maniaTimingColourCheckbox = new SettingsCheckbox { LabelText = "Timing-based note colouring" },
                };

            // osu!catch has no per-ruleset gameplay settings of its own in lazer, and the shared
            // group is elements-only.
            default:
                return Array.Empty<Drawable>();
        }
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        renderChartCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RenderChart);
        playHitSoundsCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.PlayHitSounds);

        bindChartOpacity();

        if (hitLightingCheckbox != null && lazerConfig != null)
            hitLightingCheckbox.Current = lazerConfig.GetBindable<bool>(OsuSetting.HitLighting);

        if (rulesetConfigs != null)
            bindRulesetConfigs();

        if (chartMods != null)
            bindMods(chartMods);

        if (elements != null)
        {
            foreach (var entry in PlayfieldElementCatalog.All)
                elementCheckboxes[entry.Element].Current = elements.Shown(entry.Element);
        }

        if (conversion != null && convertDropdown != null)
        {
            convertDropdown.Current = convertUi;

            // Two-way sync: service → UI lifts the disable for the mirroring write, since the
            // disable exists to stop USER edits rather than programmatic ones.
            conversion.Target.BindValueChanged(e =>
            {
                bool wasDisabled = convertUi.Disabled;

                convertUi.Disabled = false;
                convertUi.Value = e.NewValue;
                convertUi.Disabled = wasDisabled;
            }, true);

            convertUi.BindValueChanged(e => conversion.Target.Value = e.NewValue);

            sourceConvertible.BindTo(conversion.SourceConvertible);
            isConverting.BindTo(conversion.IsConverting);
            effectiveRulesetId.BindTo(conversion.EffectiveRulesetId);

            sourceConvertible.BindValueChanged(_ => updateConversionState(), true);
            isConverting.BindValueChanged(_ => updateForCurrentRuleset(), true);
            effectiveRulesetId.BindValueChanged(_ => updateForCurrentRuleset());
        }

        currentSet.BindTo(playback.Current);
        selectedOsuFile.BindTo(playback.SelectedOsuFile);
        currentSet.BindValueChanged(_ => updateForCurrentRuleset());
        selectedOsuFile.BindValueChanged(_ => updateForCurrentRuleset());
        updateForCurrentRuleset();
    }

    /// <summary>
    /// Two-way sync between the opacity slider and its config value, plus the dependent-row state:
    /// the row follows "Render chart" in both directions — greyed and inert the moment rendering is
    /// switched off, live again the moment it is switched back on.
    /// </summary>
    private void bindChartOpacity()
    {
        chartOpacityConfig = config.GetBindable<double>(JukeBoxSetting.ChartOpacity);
        chartOpacityRow.Current = chartOpacityUi;

        // Config → UI lifts the disable for the mirroring write, since the disable exists to stop
        // USER edits rather than programmatic ones (see chartOpacityUi's remarks).
        chartOpacityConfig.BindValueChanged(e =>
        {
            bool wasDisabled = chartOpacityUi.Disabled;

            chartOpacityUi.Disabled = false;
            chartOpacityUi.Value = e.NewValue;
            chartOpacityUi.Disabled = wasDisabled;
        }, true);

        chartOpacityUi.BindValueChanged(e => chartOpacityConfig.Value = e.NewValue);

        renderChartCheckbox.Current.BindValueChanged(e => updateChartOpacityState(e.NewValue), true);
    }

    private void updateChartOpacityState(bool rendering)
    {
        chartOpacityUi.Disabled = !rendering;
        chartOpacityRow.FadeTo(rendering ? 1 : locked_alpha, Theme.HoverFadeDuration, Easing.OutQuint);
    }

    /// <summary>
    /// A map with nothing to convert to gets a greyed control and a line saying why, the same
    /// treatment the converts-only mod rows get. Convertibility is lazer's answer about the actual
    /// decoded beatmap (see ChartConversion), published by whoever built the visuals.
    /// </summary>
    private void updateConversionState()
    {
        if (convertDropdown == null || convertNote == null)
            return;

        bool live = sourceConvertible.Value;

        convertUi.Disabled = !live;
        convertDropdown.FadeTo(live ? 1 : locked_alpha, Theme.HoverFadeDuration, Easing.OutQuint);
        convertNote.Alpha = live ? 0 : 1;
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

        // The collapsed control stands in for the nine key-count mods, so it is offered exactly
        // where they are.
        keyOverrideHost.Alpha = ChartModCatalog.KeyCountMods.Any(m => ChartModCatalog.OfferedBy(m, mode)) ? 1 : 0;

        foreach (var (type, block) in modCategories)
            block.Alpha = ChartModCatalog.Categories.First(c => c.Type == type).Mods.Any(m => ChartModCatalog.OfferedBy(m, mode)) ? 1 : 0;

        if (convertsOnlyNote != null)
        {
            // Explaining why these rows can't bite is only true while they can't: a conversion is
            // exactly the state in which they do, so the note goes when one is in force.
            convertsOnlyNote.Alpha = !isConverting.Value
                                     && ChartModCatalog.Categories
                                                       .SelectMany(c => c.Mods)
                                                       .Any(m => ChartModCatalog.AppliesOnlyToConverts(m) && ChartModCatalog.OfferedBy(m, mode))
                ? 1
                : 0;
        }

        updateModRowStates();
        updateConversionState();
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
            bool inert = !isConverting.Value && ChartModCatalog.AppliesOnlyToConverts(mod);

            ui.Disabled = replayLocked || inert;
            modCheckboxes[mod].FadeTo(replayLocked || inert ? locked_alpha : 1, Theme.HoverFadeDuration, Easing.OutQuint);
        }

        // Same two reasons for the collapsed key-count control, and the value follows its checkbox:
        // there is nothing to pick a count FOR while the override is off.
        // The key counts and Co-op reach the beatmap only through the CONVERTER, so they bite
        // exactly when a conversion is in force — which is now a real, user-reachable state rather
        // than something that could never happen.
        bool keysInert = !isConverting.Value && ChartModCatalog.KeyCountMods.All(ChartModCatalog.AppliesOnlyToConverts);

        keyOverrideUi.Disabled = replayLocked || keysInert;
        keyCountUi.Disabled = replayLocked || keysInert || !keyOverrideUi.Value;

        keyOverrideCheckbox.FadeTo(replayLocked || keysInert ? locked_alpha : 1, Theme.HoverFadeDuration, Easing.OutQuint);
        keyCountRow.FadeTo(keyCountUi.Disabled ? locked_alpha : 1, Theme.HoverFadeDuration, Easing.OutQuint);
    }

    private void bindMods(ChartModSelection selection)
    {
        foreach (var mod in Enum.GetValues<ChartMod>())
        {
            // The key counts have no row of their own — they are driven by the collapsed control
            // bound below.
            if (ChartModCatalog.KeyCountOf(mod) != null)
                continue;

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

        bindKeyCountControl(selection);

        replayActive.BindTo(selection.ReplayActive);
        replayModAcronyms.BindTo(selection.ReplayModAcronyms);

        replayActive.BindValueChanged(_ => updateReplayLock(), true);
        replayModAcronyms.BindValueChanged(_ => updateReplayLock(), true);
    }

    /// <summary>
    /// Two-way sync between the collapsed control and the nine key-count mods the selection
    /// actually holds. Both directions lift the disable before writing, for the same reason every
    /// other adapter here does: the lock exists to stop USER edits, and a disabled bindable throws
    /// on any write at all.
    /// </summary>
    private void bindKeyCountControl(ChartModSelection selection)
    {
        keyOverrideCheckbox.Current = keyOverrideUi;
        keyCountRow.Current = keyCountUi;

        foreach (var mod in ChartModCatalog.KeyCountMods)
            selection.Enabled(mod).BindValueChanged(_ => syncKeyControlsFrom(selection));

        syncKeyControlsFrom(selection);

        keyOverrideUi.BindValueChanged(_ =>
        {
            applyKeySelection(selection);

            // The value is a dependent row: it enables and disables with its checkbox, so the
            // shared state pass has to run again right here rather than waiting for the next
            // ruleset or replay change.
            updateModRowStates();
        });

        keyCountUi.BindValueChanged(e =>
        {
            keyCountRow.LabelText = keyCountLabel(e.NewValue);
            applyKeySelection(selection);
        });
    }

    /// <summary>Selection → control: which key count is on, if any.</summary>
    private void syncKeyControlsFrom(ChartModSelection selection)
    {
        if (applyingKeySelection)
            return;

        var selected = ChartModCatalog.KeyCountMods
                                      .Where(m => selection.Enabled(m).Value)
                                      .Select(m => (ChartMod?)m)
                                      .FirstOrDefault();

        applyingKeySelection = true;

        try
        {
            write(keyOverrideUi, selected != null);

            // An unticked control keeps the last count it showed rather than snapping back to a
            // default, so unticking and re-ticking returns what the user had.
            if (selected != null && ChartModCatalog.KeyCountOf(selected.Value) is int keys)
                write(keyCountUi, keys);
        }
        finally
        {
            applyingKeySelection = false;
        }
    }

    /// <summary>Control → selection: exactly one key-count mod on, or none.</summary>
    private void applyKeySelection(ChartModSelection selection)
    {
        if (applyingKeySelection)
            return;

        applyingKeySelection = true;

        try
        {
            var wanted = keyOverrideUi.Value ? ChartModCatalog.KeyCountMod(keyCountUi.Value) : null;

            foreach (var mod in ChartModCatalog.KeyCountMods)
                selection.Enabled(mod).Value = wanted == mod;
        }
        finally
        {
            applyingKeySelection = false;
        }
    }

    private static void write<T>(Bindable<T> bindable, T value)
    {
        bool wasDisabled = bindable.Disabled;

        bindable.Disabled = false;
        bindable.Value = value;
        bindable.Disabled = wasDisabled;
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

        replayModsNoteText = !locked
            ? string.Empty
            : acronyms.Count > 0
                ? $"Replay is playing — its mods are in force: {string.Join(" ", acronyms)}"
                : "Replay is playing — it was a no-mod play";

        replayModsNote.Text = replayModsNoteText;
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
        // Every ruleset's group is listed at all times, whatever is playing (user request): the
        // list is a stable, complete inventory of what the player can hide, not a view of the
        // current song. A toggle picked for a ruleset that isn't on screen stays fully interactive
        // and persists, and takes effect the moment a map of that ruleset plays — the filter is
        // driven by PlayfieldElementVisibility, which knows nothing about what is playing.
        foreach (var group in elementGroups.Values)
            group.Alpha = 1;

        // A ruleset's own rows are always shown with its group. The only row that still comes and
        // goes is in the SHARED block, and not as scoping: osu!catch draws no hit-score popups at
        // all (see PlayfieldElementCatalog.Entry.AppliesToRuleset), so while a catch map is on
        // screen there is genuinely nothing for a judgements toggle to hide.
        foreach (var entry in PlayfieldElementCatalog.All)
        {
            bool sharedRowThatDoesNotApply = entry.RulesetId == PlayfieldElementCatalog.all_rulesets && !entry.AppliesTo(mode);

            elementCheckboxes[entry.Element].Alpha = sharedRowThatDoesNotApply ? 0 : 1;
        }
    }

    private int currentMode()
    {
        // While a conversion is in force the chart on screen IS the target ruleset, so that is what
        // the mod rows and element groups are for.
        if (isConverting.Value)
            return effectiveRulesetId.Value;

        var set = currentSet.Value;

        if (set == null || set.Difficulties.Count == 0)
            return 0;

        string? path = selectedOsuFile.Value ?? set.PreferredOsuFile;

        return set.Difficulties.FirstOrDefault(d => d.Path == path)?.Mode ?? 0;
    }
}
