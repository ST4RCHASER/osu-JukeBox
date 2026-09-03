#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using osu.Game.Overlays.BeatmapListing;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Video;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// Settings panel. Two presentations, chosen by the constructor:
///
/// <list type="bullet">
/// <item>Floating (default, <c>docked: false</c>) — a centred modal, dimmed behind by a
/// full-screen scrim, opened on demand (e.g. from a corner gear button). Escape or toggling it
/// again closes it (<see cref="VisibilityContainer.ToggleVisibility"/>, inherited).</item>
/// <item>Docked (<c>docked: true</c>) — the three-column layout's right panel embeds this same
/// content inline as its "Settings" tab body: no scrim, no floating card chrome, shown once at
/// load and never hidden again (tab switching toggles the tab body's own Alpha instead).</item>
/// </list>
///
/// Presented with osu!lazer's REAL settings components (<see cref="SettingsCheckbox"/>,
/// <see cref="SettingsSlider{T}"/>, <see cref="SettingsDropdown{T}"/> inside
/// <see cref="SettingsSection"/>/<see cref="SettingsSubsection"/>), which carry lazer's pill
/// toggles, rounded tooltip-valued sliders, dropdown styling and the built-in hover
/// revert-to-default arrow. Those components resolve an <see cref="OverlayColourProvider"/> —
/// cached for this subtree with the same purple scheme lazer's own SettingsPanel caches.
///
/// Every control binds a REAL config bindable, from one of three config sources:
/// <see cref="JukeBoxConfigManager"/> (ours), <see cref="FrameworkConfigManager"/> (host-cached,
/// always present) and the lazer-side <see cref="OsuConfigManager"/> (cached by JukeBoxGameBase).
/// The lazer-side sections are simply omitted when that dependency isn't cached (bare framework
/// test scenes) rather than rendering dead controls. The per-ruleset config managers are no longer
/// among them: the sections that bound those moved to <see cref="ChartPanel"/>.
/// </summary>
public partial class SettingsOverlay : FocusedOverlayContainer, ITabSearch
{
    private const float panel_width = 360;

    /// <summary>
    /// Half of lazer's <c>SettingsButton.Margin.Vertical = -5</c>. It is NEGATIVE and a
    /// <see cref="FillFlowContainer"/> steps by LayoutSize, so a button silently subtracts this
    /// much from whatever gap the drawable above it asked for. Anything spacing itself against a
    /// settings button has to add it back.
    /// </summary>
    private const float settings_button_top_margin = 5;

    /// <summary>Where a user creates the OAuth application the official search backend needs — the
    /// account page's OAuth section, deep-linked so it opens already scrolled to it.</summary>
    internal const string oauth_application_url = "https://osu.ppy.sh/home/account/edit#oauth";

    /// <summary>Fraction of the game height the floating card may occupy (content scrolls inside).</summary>
    private const float floating_height = 0.85f;

    /// <summary>See the class summary.</summary>
    private readonly bool docked;

    // The exact DI lazer's SettingsPanel provides its subtree (same scheme): every lazer settings
    // control below resolves this for the purple pill/slider/dropdown palette.
    [Cached]
    private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private FrameworkConfigManager frameworkConfig { get; set; } = null!;

    [Resolved]
    private GameHost host { get; set; } = null!;

    [Resolved]
    private AudioManager audio { get; set; } = null!;

    [Resolved(canBeNull: true)]
    private OsuConfigManager? lazerConfig { get; set; }

    /// <summary>The signed-in osu! account. Null in bare scenes that cache no account, where the
    /// section shows its disconnected state and the button reports the missing service.</summary>
    [Resolved(canBeNull: true)]
    private OsuAccount? account { get; set; }

    /// <summary>The shared per-layer storyboard visibility. Nullable like the rest: a bare test
    /// scene that caches no services still builds the panel, with the layer rows simply inert.</summary>
    [Resolved(canBeNull: true)]
    private StoryboardLayerVisibility? storyboardLayers { get; set; }

    /// <summary>
    /// The imported-skin listing behind the gameplay-skin dropdown. Optional for the same reason
    /// <see cref="lazerConfig"/> is: settings test scenes cache a config manager and little else,
    /// and the dropdown degrades to just the bundled skins without one.
    /// </summary>
    [Resolved(canBeNull: true)]
    private SkinLibrary? skinLibrary { get; set; }

    // Only ever assigned (and used) in the floating branch of load() — a docked instance has no
    // card to pop, and its PopIn/PopOut are both guarded no-ops (see their own comments below).
    private Container? panelCard;

    // Likewise floating-only: the docked presentation deliberately paints no ground of its own (see
    // load()), so this stays null there.
    private Box? cardBackground;


    /// <summary>
    /// The build stamp at the very bottom of the body — see where it's built in
    /// <see cref="createSections"/>. Deliberately NOT a searchable row: it is a fact about the
    /// app rather than a setting, so it survives every filter instead of vanishing and leaving
    /// the panel looking broken mid-search.
    /// </summary>
    private OsuSpriteText versionText = null!;

    /// <summary>Test hook: what the footer is actually displaying.</summary>
    internal string VersionText => versionText.Text.ToString();

    /// <summary>Test hook: the footer drawable, for asserting where it sits.</summary>
    internal Drawable VersionDrawable => versionText;

    // ---- our settings ----
    private SkinSettingsDropdown skinDropdown = null!;
    private MaintenanceSection maintenanceSection = null!;
    private SettingsCheckbox showStoryboardCheckbox = null!;
    private SettingsCheckbox showVideoCheckbox = null!;

    /// <summary>One checkbox per storyboard layer, and the block they live in — dimmed and inert
    /// while the master "Storyboard" toggle is off, the same dependent-row treatment the Chart
    /// tab's opacity slider gets under "Render chart".</summary>
    private readonly Dictionary<StoryboardLayerKind, SettingsCheckbox> layerCheckboxes =
        new Dictionary<StoryboardLayerKind, SettingsCheckbox>();

    /// <summary>
    /// Checkbox-facing adapters for the layer rows. As everywhere else here, the grey-out lives on
    /// THESE bindables' Disabled and never on the service's own — those are written programmatically
    /// (config load, the settings mirror), and a disabled bindable throws on any write at all.
    /// </summary>
    private readonly Dictionary<StoryboardLayerKind, BindableBool> layerUi =
        new Dictionary<StoryboardLayerKind, BindableBool>();

    private Container storyboardLayerBlock = null!;
    private SettingsSlider<double> backgroundDimRow = null!;
    private SettingsSlider<double> backgroundBlurRow = null!;
    private SettingsSlider<double> uiScaleRow = null!;
    private SettingsSlider<double> playfieldZoomRow = null!;
    private SettingsCheckbox removeChartMaskCheckbox = null!;
    private SettingsCheckbox removeStoryboardMaskCheckbox = null!;
    private SettingsEnumDropdown<MirrorSource> mirrorDropdown = null!;
    private SettingsEnumDropdown<SearchApi> searchApiDropdown = null!;
    private SettingsTextBox clientIdTextBox = null!;
    private SettingsPasswordTextBox clientSecretTextBox = null!;

    // ---- Account (osu! sign-in) ----
    private SettingsButton accountButton = null!;
    private TruncatingSpriteText accountStatus = null!;
    private OsuTextFlowContainer accountHint = null!;

    // Wraps the three official-API-only rows so "Search API = Mirror" hides the whole block in one
    // write. Hidden by Alpha (not removed) because these rows carry live config bindables that must
    // keep their values across toggles; a zero-Alpha child is not IsPresent, so the surrounding
    // FillFlowContainer stops laying it out entirely rather than leaving a gap.
    private Container officialCredentials = null!;
    private SettingsCheckbox detachPlayerCheckbox = null!;
    private SettingsCheckbox playOnMainCheckbox = null!;
    private SettingsCheckbox discordPresenceCheckbox = null!;

    // ---- Radio ----
    private SettingsCheckbox radioOnEmptyQueueCheckbox = null!;
    private SettingsCheckbox radioOnStartCheckbox = null!;
    private SettingsEnumDropdown<RadioRuleset> radioModeDropdown = null!;
    private SettingsDropdown<SearchCategory> radioCategoryDropdown = null!;
    private SettingsEnumDropdown<SearchGenre> radioGenreDropdown = null!;
    private SettingsEnumDropdown<SearchLanguage> radioLanguageDropdown = null!;
    private SettingsCheckbox radioHasVideoCheckbox = null!;
    private SettingsCheckbox radioHasStoryboardCheckbox = null!;
    private SettingsCheckbox radioFeaturedArtistsCheckbox = null!;
    private SettingsSlider<double> radioMinStarsRow = null!;
    private SettingsSlider<double> radioMaxStarsRow = null!;

    /// <summary>The radio filter rows paired with the capability each needs, so the whole block's
    /// visibility is one loop over the active backend's answer — see
    /// <see cref="updateRadioFilterAvailability"/>.</summary>
    private (Drawable Row, SearchFilters Needs)[] radioFilterRows = System.Array.Empty<(Drawable, SearchFilters)>();

    /// <summary>Shown in place of the filter rows when the reachable source can express none of
    /// them, so an empty Radio section reads as deliberate rather than broken.</summary>
    private OsuTextFlowContainer radioNoFiltersHint = null!;

    // Checkbox-facing adapter for playOnMainCheckbox (same shape as hardwareAccelerationEnabled
    // below): the dependent-row grey-out lives on THIS bindable's Disabled, never on the config
    // bindable itself — a disabled config bindable would make any programmatic SetValue throw.
    private readonly Bindable<bool> playOnMainUi = new Bindable<bool>();
    private Bindable<bool> playOnMainConfig = null!;

    // ---- framework settings ----
    private DeviceSettingsDropdown audioDeviceDropdown = null!;
    private SettingsSlider<double> masterVolumeRow = null!;
    private SettingsSlider<double> effectVolumeRow = null!;
    private SettingsSlider<double> musicVolumeRow = null!;
    private SettingsEnumDropdown<RendererType> rendererDropdown = null!;
    private SettingsEnumDropdown<FrameSync> frameLimiterDropdown = null!;
    private SettingsEnumDropdown<ExecutionMode> threadingDropdown = null!;
    private SettingsEnumDropdown<FpsDisplayMode> fpsDisplayDropdown = null!;
    private SettingsCheckbox hardwareAccelerationCheckbox = null!;
    private SettingsEnumDropdown<WindowMode>? screenModeDropdown;
    private DisplaySettingsDropdown? displayDropdown;
    private readonly Bindable<Display> currentDisplay = new Bindable<Display>();

    // Adapter bindable for hardwareAccelerationCheckbox: the framework setting is a [Flags] enum
    // (HardwareVideoDecoder) with many platform-specific values, but the checkbox only ever writes
    // None/Any (see LoadComplete) while reading back "checked" for ANY non-None value — same
    // two-way-sync shape as analysisDisplayLength below, just bool<->enum instead of ranged int.
    private readonly BindableBool hardwareAccelerationEnabled = new BindableBool();
    private Bindable<HardwareVideoDecoder> hardwareVideoDecoderConfig = null!;

    // ---- lazer (OsuConfigManager) settings; only built when lazerConfig is present ----
    private SettingsCheckbox beatmapSkinsCheckbox = null!;
    private SettingsCheckbox beatmapColoursCheckbox = null!;
    private SettingsCheckbox beatmapHitsoundsCheckbox = null!;
    private SettingsSlider<float> comboNormalisationRow = null!;
    private SettingsSlider<double> inactiveVolumeRow = null!;
    private SettingsSlider<float> positionalHitsoundsRow = null!;

    // Global (device-level) audio calibration, sitting with the output device and volume rows it
    // belongs to. The playback-scoped rows that used to live here — speed, transport, and the
    // per-beatmap offset — moved to the right column's Playback tab (see PlaybackPanel).
    private SettingsSlider<double> globalOffsetRow = null!;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to controls, to drive/assert
    /// them without depending on this panel's internal layout.
    /// </summary>
    internal SettingsDropdown<FpsDisplayMode> FpsDisplayDropdown => fpsDisplayDropdown;
    internal SettingsCheckbox HardwareAccelerationCheckbox => hardwareAccelerationCheckbox;

    internal SettingsDropdown<MirrorSource> MirrorDropdown => mirrorDropdown;
    internal SettingsDropdown<SearchApi> SearchApiDropdown => searchApiDropdown;
    internal SettingsItem<string> ClientIdTextBox => clientIdTextBox;
    internal SettingsItem<string> ClientSecretTextBox => clientSecretTextBox;

    /// <summary>Test-only: the block of official-API credential rows, present only while the
    /// official search backend is selected.</summary>
    internal Container OfficialCredentials => officialCredentials;

    /// <summary>Test-only: the Account section's button and status line, so the connected/
    /// disconnected presentation can be asserted without depending on layout.</summary>
    internal SettingsButton AccountButton => accountButton;

    internal string AccountStatusText => accountStatusText;

    internal string AccountHintText => accountHintText;

    // TextFlowContainer is write-only for Text, so the strings are kept here too — the tests need
    // to read back what the section is actually telling the user.
    private string accountStatusText = string.Empty;
    private string accountHintText = string.Empty;

    /// <summary>Test seam (JukeBox.Game.Tests has InternalsVisibleTo): replaces
    /// <see cref="osu.Framework.Platform.GameHost.OpenUrlExternally"/> so tests can assert the
    /// opened URL without actually opening a browser. Mirrors
    /// <see cref="FullscreenListingOverlay.OpenUrl"/>.</summary>
    internal Action<string>? OpenUrl;

    /// <summary>
    /// The five storyboard layers as their own indented block. osu! storyboards always carry all
    /// five (an empty storyboard still reports them), so this is a complete, stable inventory
    /// rather than a view of the current map — the same choice the Chart tab's element list makes.
    /// </summary>
    private Container createStoryboardLayerBlock()
    {
        var rows = new List<Drawable>();

        foreach (var layer in StoryboardLayerVisibility.All)
        {
            var checkbox = new SettingsCheckbox { LabelText = layer.ToString() };

            layerCheckboxes[layer] = checkbox;
            // The UI bindable's DEFAULT must mirror the service's (Fail ships hidden), or the
            // revert-to-default arrow shows on the shipped state and "revert" turns Fail ON.
            layerUi[layer] = new BindableBool(!StoryboardLayerVisibility.HiddenByDefault.Contains(layer));
            rows.Add(checkbox);
        }

        rows.Add(new OsuTextFlowContainer(t => t.Font = OsuFont.GetFont(size: 12))
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding
            {
                Left = SettingsPanel.CONTENT_PADDING.Left,
                Right = SettingsPanel.CONTENT_PADDING.Right,
                Top = 4,
                Bottom = 8,
            },
            Colour = Theme.TextTertiary,
            // Said rather than hidden: the row is part of the storyboard's real shape, and a
            // missing one would only raise the question of where it went.
            Text = "Fail normally draws only while failing, which never happens here — switching it "
                   + "on forces that layer visible anyway.",
        });

        return storyboardLayerBlock = new Container
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding { Left = 24 },
            Child = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Children = rows,
            },
        };
    }

    /// <summary>
    /// Two-way sync between each layer row and the shared visibility service, plus the dependent
    /// state: with the storyboard itself switched off there is nothing for a layer choice to act
    /// on, so the whole block greys out and refuses input — and comes straight back when it isn't.
    /// </summary>
    private void bindStoryboardLayers()
    {
        foreach (var layer in StoryboardLayerVisibility.All)
        {
            var captured = layer;
            var ui = layerUi[layer];
            var source = storyboardLayers!.Shown(layer);

            layerCheckboxes[layer].Current = ui;

            // Service → UI lifts the disable for the mirroring write, since the disable exists to
            // stop USER edits rather than programmatic ones.
            source.BindValueChanged(e =>
            {
                bool wasDisabled = ui.Disabled;

                ui.Disabled = false;
                ui.Value = e.NewValue;
                ui.Disabled = wasDisabled;
            }, true);

            ui.BindValueChanged(e => storyboardLayers!.Shown(captured).Value = e.NewValue);
        }

        showStoryboardCheckbox.Current.BindValueChanged(e => updateStoryboardLayerState(e.NewValue), true);
    }

    private void updateStoryboardLayerState(bool storyboardShown)
    {
        foreach (var ui in layerUi.Values)
            ui.Disabled = !storyboardShown;

        storyboardLayerBlock.FadeTo(storyboardShown ? 1 : dependent_row_locked_alpha, Theme.HoverFadeDuration, Easing.OutQuint);
    }

    /// <summary>The dimming a dependent row gets while the toggle it depends on is off, matching
    /// the Chart tab's own locked-row alpha so the two read as one rule.</summary>
    private const float dependent_row_locked_alpha = 0.55f;

    private void openUrl(string url)
    {
        if (OpenUrl != null)
            OpenUrl(url);
        else
            host.OpenUrlExternally(url);
    }

    internal SettingsSlider<double> BackgroundDimSlider => backgroundDimRow;
    internal SettingsSlider<double> PlayfieldZoomSlider => playfieldZoomRow;
    internal SettingsCheckbox ShowStoryboardCheckbox => showStoryboardCheckbox;
    internal SettingsCheckbox ShowVideoCheckbox => showVideoCheckbox;

    /// <summary>Test-only: a storyboard layer's own row, and whether the block is currently
    /// accepting input at all.</summary>
    internal SettingsCheckbox LayerCheckbox(StoryboardLayerKind layer) => layerCheckboxes[layer];

    internal bool StoryboardLayersInert => layerUi.Values.All(b => b.Disabled);

    internal SettingsCheckbox RemoveChartMaskCheckbox => removeChartMaskCheckbox;
    internal SettingsCheckbox RemoveStoryboardMaskCheckbox => removeStoryboardMaskCheckbox;
    internal SettingsDropdown<SkinChoice> SkinDropdown => skinDropdown;
    internal MaintenanceSection MaintenanceSection => maintenanceSection;
    internal SettingsDropdown<string> AudioDeviceDropdown => audioDeviceDropdown;
    internal SettingsSlider<double> MasterVolumeSlider => masterVolumeRow;
    internal SettingsCheckbox DetachPlayerCheckbox => detachPlayerCheckbox;
    internal SettingsCheckbox PlayOnMainCheckbox => playOnMainCheckbox;
    internal SettingsCheckbox DiscordPresenceCheckbox => discordPresenceCheckbox;

    // Test-only access to the Radio section, to drive the toggles and assert per-backend row
    // visibility without depending on this panel's layout.
    internal SettingsCheckbox RadioOnEmptyQueueCheckbox => radioOnEmptyQueueCheckbox;
    internal SettingsCheckbox RadioOnStartCheckbox => radioOnStartCheckbox;
    internal SettingsDropdown<RadioRuleset> RadioModeDropdown => radioModeDropdown;
    internal SettingsDropdown<SearchCategory> RadioCategoryDropdown => radioCategoryDropdown;
    internal SettingsDropdown<SearchGenre> RadioGenreDropdown => radioGenreDropdown;
    internal SettingsDropdown<SearchLanguage> RadioLanguageDropdown => radioLanguageDropdown;
    internal SettingsCheckbox RadioHasVideoCheckbox => radioHasVideoCheckbox;
    internal SettingsCheckbox RadioHasStoryboardCheckbox => radioHasStoryboardCheckbox;
    internal SettingsCheckbox RadioFeaturedArtistsCheckbox => radioFeaturedArtistsCheckbox;
    internal SettingsSlider<double> RadioMinStarsSlider => radioMinStarsRow;
    internal SettingsSlider<double> RadioMaxStarsSlider => radioMaxStarsRow;
    internal Drawable RadioNoFiltersHint => radioNoFiltersHint;

    /// <summary>Test-only: the panel's own background surface — non-null only for the floating
    /// card, which is a card in its own right over a scrim. The docked presentation paints none, so
    /// its sections sit on the hosting column's single surface (see load()).</summary>
    internal Box? CardBackground => cardBackground;

    /// <summary>Test-only: scrolls a control into view (instantly, so the very next test step's
    /// mouse coordinates are already final) so real mouse input can reach it.</summary>
    internal void ScrollControlIntoView(Drawable control) => body.Scroll.ScrollIntoView(control, animated: false);

    /// <param name="docked">See the class summary.</param>
    /// <param name="searchEngine">The app's one search engine, purely so the Radio section's filter
    /// rows can follow <see cref="BeatmapSearchEngine.AvailableFilters"/> — the SAME capability
    /// signal the beatmap listing's own rows follow, so a filter dimension appears and disappears
    /// in both places together. Passed in rather than resolved because the engine is created and
    /// owned by <see cref="Screens.MainScreen"/> and never cached in DI; null in bare test scenes
    /// and in the floating presentation, where the rows then simply all show.</param>
    public SettingsOverlay(bool docked = false, BeatmapSearchEngine? searchEngine = null)
    {
        this.docked = docked;
        this.searchEngine = searchEngine;
    }

    private readonly BeatmapSearchEngine? searchEngine;

    /// <summary>
    /// This panel's view of the radio's station. A SECOND <see cref="RadioFilters"/> over the same
    /// config manager rather than the one cached in DI, which is fine and deliberate: both are made
    /// of <c>ConfigManager.GetBindable</c> copies of the same keys, and those stay in sync with each
    /// other automatically — so editing a control here moves the radio's own value with it. Held in
    /// a field because those copies are referenced only weakly by the manager.
    /// </summary>
    private RadioFilters radioFilters = null!;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        // Tooltip host for the whole panel: lazer's RoundedSliderBar surfaces its value through a
        // tooltip, which only renders inside a TooltipContainer ancestor.
        InternalChild = new OsuTooltipContainer(null)
        {
            RelativeSizeAxes = Axes.Both,
            Children = docked
                ? new Drawable[]
                {
                    // No scrim, no floating card, no fixed width AND no backdrop of its own — this
                    // is inline tab-body content inside the three-column layout's right panel, which
                    // already paints an opaque Theme.PanelSurface behind the whole tab. Painting a
                    // second (lighter) ground here stacked a card on that card, and lazer's own
                    // SettingsSection separators then read as the edges of per-section sub-cards
                    // rather than as dividers. Sections sit directly on the column's one surface, in
                    // lazer's own arrangement: one ground, sections told apart by their separators
                    // and header spacing. (The floating presentation below is a real card in its own
                    // right, over a scrim, so it keeps lazer's Background4 ground.) The same
                    // reasoning QueuePanel's docked branch already follows.
                    body = new TabSearchBody(createSections(), "search settings"),
                }
                : new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Theme.ModalScrim,
                    },
                    panelCard = new Container
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Width = panel_width,
                        RelativeSizeAxes = Axes.Y,
                        Height = floating_height,
                        Masking = true,
                        CornerRadius = Theme.CornerRadius,
                        EdgeEffect = Theme.PanelShadow,
                        Children = new Drawable[]
                        {
                            cardBackground = new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = colourProvider.Background4,
                            },
                            body = new TabSearchBody(createSections(), "search settings"),
                        },
                    },
                },
        };
    }

    private TabSearchBody body = null!;

    /// <summary>Test seam: this tab's live filter term.</summary>
    internal string SearchTerm => body.SearchTerm;

    /// <summary>Test seam: whether this tab's search box holds keyboard focus.</summary>
    internal bool SearchHasFocus => body.SearchHasFocus;

    public void BeginSearch(char first) => body.BeginSearch(first);

    public void ClearSearch() => body.ClearSearch();

    /// <summary>
    /// The actual settings content, shared by both presentations (see the class summary). Always
    /// <see cref="Axes.X"/>-relative: the floating modal's fixed <see cref="panel_width"/> already
    /// constrains the outer card, and both presentations scroll vertically. Layout is lazer's:
    /// big TorusAlternate section headers (via <see cref="SettingsSection"/>), bold subsection
    /// headers (via <see cref="SettingsSubsection"/>), items carrying their own content padding.
    /// </summary>
    private List<Drawable> createSections()
    {
        var sections = new List<Drawable>
        {
            new OsuSpriteText
            {
                Font = OsuFont.TorusAlternate.With(size: 32),
                Text = "Settings",
                Margin = new MarginPadding { Left = SettingsPanel.CONTENT_PADDING.Left, Top = 18, Bottom = 4 },
            },
            new LazerSection("Skin", FontAwesome.Solid.PaintBrush)
            {
                Children = new Drawable[]
                {
                    skinDropdown = new SkinSettingsDropdown { LabelText = "Gameplay skin" },
                },
            },
        };

        // The "Rulesets" section (per-ruleset gameplay settings) and its "Analysis (osu!)"
        // subsection used to sit here; both moved wholesale to the right column's Chart tab (see
        // ChartPanel), which is where everything about the rendered chart now lives. Moved, not
        // copied: they bind the very same lazer per-ruleset config bindables there, so existing
        // values carry over and each setting still has exactly one control.

        var gameplayRows = new List<Drawable>();

        gameplayRows.Add(backgroundDimRow = new SettingsSlider<double> { LabelText = "Background dim", DisplayAsPercentage = true, KeyboardStep = 0.01f });
        gameplayRows.Add(backgroundBlurRow = new SettingsSlider<double> { LabelText = "Background blur", DisplayAsPercentage = true, KeyboardStep = 0.01f });
        gameplayRows.Add(playfieldZoomRow = new SettingsSlider<double> { LabelText = "Playfield zoom", DisplayAsPercentage = true, KeyboardStep = 0.01f });

        // The two mask releases, sitting with the zoom they interact with: zooming in is the other
        // way to push content at the box's edges, and these decide whether it stops there. Named
        // for what they remove rather than what they show, which is how the user asked for them.
        gameplayRows.Add(removeChartMaskCheckbox = new SettingsCheckbox { LabelText = "Remove playfield/chart mask" });
        gameplayRows.Add(removeStoryboardMaskCheckbox = new SettingsCheckbox { LabelText = "Remove storyboard mask" });

        // The multi-replay controls (Multiple replays / Knockout / Rank players by / Re-order) used
        // to live here, but they belong with the players they act on — they now sit in the Players
        // section of the Playback tab (see PlayersPanel), one source of truth alongside the
        // per-player colour and mods.

        sections.Add(new LazerSection("Gameplay", FontAwesome.Regular.DotCircle) { Children = gameplayRows });

        var beatmapRows = new List<Drawable>();

        if (lazerConfig != null)
        {
            beatmapRows.Add(beatmapSkinsCheckbox = new SettingsCheckbox { LabelText = "Beatmap skins" });
            beatmapRows.Add(beatmapColoursCheckbox = new SettingsCheckbox { LabelText = "Beatmap colours" });
            beatmapRows.Add(beatmapHitsoundsCheckbox = new SettingsCheckbox { LabelText = "Beatmap hitsounds" });
        }

        // Two independent rows where there was one combined "Storyboard / video" (user request):
        // the storyboard's video is a layer of its own in lazer's model, so the two really are
        // separable, and a busy storyboard over a map you want the video of is exactly the case the
        // combined toggle could not express. The per-layer block sits under the storyboard row it
        // depends on.
        beatmapRows.Add(showStoryboardCheckbox = new SettingsCheckbox { LabelText = "Storyboard" });
        beatmapRows.Add(createStoryboardLayerBlock());
        beatmapRows.Add(showVideoCheckbox = new SettingsCheckbox { LabelText = "Video" });

        if (lazerConfig != null)
            beatmapRows.Add(comboNormalisationRow = new SettingsSlider<float> { LabelText = "Combo colour normalisation", DisplayAsPercentage = true, KeyboardStep = 0.01f });

        sections.Add(new LazerSection("Beatmap", FontAwesome.Solid.Music) { Children = beatmapRows });

        var audioRows = new List<Drawable>();
        audioRows.Add(audioDeviceDropdown = new DeviceSettingsDropdown { LabelText = "Output device" });
        audioRows.Add(masterVolumeRow = new SettingsSlider<double> { LabelText = "Master", DisplayAsPercentage = true, KeyboardStep = 0.01f });

        if (lazerConfig != null)
            audioRows.Add(inactiveVolumeRow = new SettingsSlider<double> { LabelText = "Master (window inactive)", DisplayAsPercentage = true, KeyboardStep = 0.01f });

        audioRows.Add(effectVolumeRow = new SettingsSlider<double> { LabelText = "Effect", DisplayAsPercentage = true, KeyboardStep = 0.01f });
        audioRows.Add(musicVolumeRow = new SettingsSlider<double> { LabelText = "Music", DisplayAsPercentage = true, KeyboardStep = 0.01f });

        audioRows.Add(globalOffsetRow = new SettingsSlider<double> { LabelText = "Audio offset (global)", KeyboardStep = 1 });

        if (lazerConfig != null)
            audioRows.Add(positionalHitsoundsRow = new SettingsSlider<float> { LabelText = "Hitsound stereo separation", DisplayAsPercentage = true, KeyboardStep = 0.01f });

        sections.Add(new LazerSection("Audio", FontAwesome.Solid.VolumeUp) { Children = audioRows });

        var graphicsRows = new List<Drawable>();

        // A headless host has no window — the window-bound rows simply don't exist there.
        if (host.Window != null)
        {
            graphicsRows.Add(screenModeDropdown = new SettingsEnumDropdown<WindowMode>
            {
                LabelText = "Screen mode",
                Items = host.Window.SupportedWindowModes,
            });

            graphicsRows.Add(displayDropdown = new DisplaySettingsDropdown
            {
                LabelText = "Display",
                Items = host.Window.Displays,
            });
        }

        graphicsRows.Add(uiScaleRow = new SettingsSlider<double> { LabelText = "UI scaling", KeyboardStep = 0.05f });
        graphicsRows.Add(rendererDropdown = new SettingsEnumDropdown<RendererType>
        {
            LabelText = "Renderer (requires restart)",
            Items = host.GetPreferredRenderersForCurrentPlatform(),
        });
        graphicsRows.Add(frameLimiterDropdown = new SettingsEnumDropdown<FrameSync> { LabelText = "Frame limiter" });
        graphicsRows.Add(threadingDropdown = new SettingsEnumDropdown<ExecutionMode> { LabelText = "Threading mode" });
        graphicsRows.Add(fpsDisplayDropdown = new SettingsEnumDropdown<FpsDisplayMode> { LabelText = "FPS counter" });
        graphicsRows.Add(detachPlayerCheckbox = new SettingsCheckbox { LabelText = "Detach player window" });
        // Dependent row, lazer-style: indented under its parent and disabled (greyed, not
        // hidden — hiding would make the layout jump) whenever the parent is off. The disable
        // is applied to the checkbox's Current (the config bindable) in LoadComplete.
        graphicsRows.Add(new Container
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding { Left = 24 },
            Child = playOnMainCheckbox = new SettingsCheckbox { LabelText = "Play on main window too" },
        });
        graphicsRows.Add(new LazerSubsection("Video Playback")
        {
            Children = new Drawable[]
            {
                hardwareAccelerationCheckbox = new SettingsCheckbox { LabelText = "Use hardware acceleration" },
            },
        });
        sections.Add(new LazerSection("Graphics", FontAwesome.Solid.Laptop) { Children = graphicsRows });

        sections.Add(createRadioSection());

        sections.Add(new LazerSection("Online", FontAwesome.Solid.GlobeAsia)
        {
            Children = new Drawable[]
            {
                mirrorDropdown = new SettingsEnumDropdown<MirrorSource> { LabelText = "Beatmap mirror" },
                searchApiDropdown = new SettingsEnumDropdown<SearchApi> { LabelText = "Search API" },
                officialCredentials = new Container
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
                            clientIdTextBox = new SettingsTextBox { LabelText = "Client ID" },
                            clientSecretTextBox = new SettingsPasswordTextBox { LabelText = "Client secret" },
                            new OsuTextFlowContainer(t => t.Font = OsuFont.GetFont(size: 12))
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Padding = new MarginPadding
                                {
                                    // CONTENT_MARGINS (20), not CONTENT_PADDING (12/22). Every
                                    // settings ROW — labels and buttons alike — puts its content at
                                    // 20, so a paragraph at 12 hangs eight pixels left of the
                                    // button beneath it and of every label around it, which is
                                    // what made this block read as falling out of the panel.
                                    Horizontal = SettingsPanel.CONTENT_MARGINS,
                                    Top = 8,

                                    // The button below carries Margin.Vertical = -5, and a flow
                                    // steps by LayoutSize, so it takes 5 straight back off this
                                    // gap: a Bottom of 8 rendered as 3, which is why the paragraph
                                    // sat on top of the button. Add the 5 back.
                                    Bottom = Theme.SectionSpacing + settings_button_top_margin,
                                },
                                Colour = Theme.TextTertiary,
                                Text = "Create an OAuth application on your osu! account page and paste its id and "
                                       + "secret here. For beatmap search alone the callback URL can be left blank; to "
                                       + "sign in under Account (which is what spectating needs) it must be set — see "
                                       + "that section for the exact URL.",
                            },
                            new SettingsButton
                            {
                                Text = "Open osu! OAuth settings",
                                Action = () => openUrl(oauth_application_url),
                            },
                        },
                    },
                },
                discordPresenceCheckbox = new SettingsCheckbox { LabelText = "Discord Rich Presence" },
            },
        });

        sections.Add(createAccountSection());

        // Last section, and deliberately so: these are one-shot destructive actions rather than
        // settings, and putting them anywhere above would mean scrolling past a pair of delete
        // buttons to reach something you actually wanted to change.
        sections.Add(maintenanceSection = new MaintenanceSection());

        // Quiet build stamp, last thing in the scrolling body — lazer puts its own version in the
        // same place. Deliberately NOT a settings row: no label column, no separator, centred and
        // muted so it reads as a footnote about the app rather than something to change. It lives
        // inside the same flow as the sections (rather than pinned to the panel) so it scrolls into
        // view at the end and can't overlap the last section.
        sections.Add(versionText = new OsuSpriteText
        {
            Anchor = Anchor.TopCentre,
            Origin = Anchor.TopCentre,
            Font = OsuFont.GetFont(size: 12),
            Colour = Theme.TextTertiary,
            Margin = new MarginPadding { Top = 12, Bottom = 4 },
            Text = AppVersion.DisplayString,
        });

        return sections;
    }

    /// <summary>
    /// Joins the skin dropdown to the two config keys that between them hold the choice:
    /// <see cref="JukeBoxSetting.Skin"/>, plus <see cref="JukeBoxSetting.CustomSkinPath"/> naming
    /// which import is meant when the former is <see cref="JukeBoxSkin.Custom"/>. Both directions
    /// are wired, because either key can move without the dropdown — importing a .osk selects it,
    /// and the detached viewer writes the skin it was told to show.
    ///
    /// <para>
    /// Picking a BUNDLED skin deliberately leaves CustomSkinPath alone, so switching to Argon and
    /// back to the library returns to the import that was selected rather than to no import at
    /// all. And an imported pick writes the folder first: Skin=Custom against a stale folder would
    /// briefly build the previously-selected skin, the same ordering the importer follows.
    /// </para>
    /// </summary>
    private void bindSkinDropdown()
    {
        var selected = config.GetBindable<JukeBoxSkin>(JukeBoxSetting.Skin);
        var selectedFolder = config.GetBindable<string>(JukeBoxSetting.CustomSkinPath);

        // Null in test scenes that cache a config manager but no skin library; the dropdown then
        // simply lists the bundled skins. BindTo through the interface because that is the
        // overload IBindableList exposes — BindableList's own public one wants a concrete list.
        if (skinLibrary != null)
        {
            IBindableList<ImportedSkin> library = skinDropdown.Library;
            library.BindTo(skinLibrary.Skins);
        }

        bool syncing = false;

        void pullFromConfig()
        {
            if (syncing)
                return;

            syncing = true;
            skinDropdown.Current.Value = selected.Value == JukeBoxSkin.Custom
                ? SkinChoice.Imported(selectedFolder.Value)
                : SkinChoice.Bundled(selected.Value);
            syncing = false;
        }

        selected.BindValueChanged(_ => pullFromConfig());
        selectedFolder.BindValueChanged(_ => pullFromConfig());
        pullFromConfig();

        skinDropdown.Current.BindValueChanged(e =>
        {
            if (syncing)
                return;

            syncing = true;

            if (e.NewValue.IsImported)
                selectedFolder.Value = e.NewValue.Folder;

            selected.Value = e.NewValue.Builtin;
            syncing = false;
        });
    }

    /// <summary>
    /// Signing in to osu! as yourself — what the spectate feature needs in order to download the
    /// replays it shows.
    ///
    /// <para>
    /// The sign-in happens on osu!'s OWN login page in the browser (OAuth authorization code); this
    /// app never sees the password. It uses the same client id/secret from the Online section
    /// above, which means the user's OAuth application must have its Redirect URI set to exactly
    /// <see cref="OsuOAuth.RedirectUri"/> — printed here rather than merely documented, because a
    /// blank or mismatched one is the single most likely way this fails, with an error message that
    /// explains nothing on its own.
    /// </para>
    /// </summary>
    private Drawable createAccountSection()
    {
        accountHintText = "Opens osu! in your browser to sign in — osu!JukeBox never sees your password. "
                          + $"Your OAuth application's Redirect URI must be exactly {OsuOAuth.RedirectUri}. "
                          + "The connection is only used to look up players and download their replays for spectating.";

        return new LazerSection("Account", FontAwesome.Solid.User)
        {
            Children = new Drawable[]
            {
                // A single OsuSpriteText, not descriptionText's OsuTextFlowContainer: this label's
                // Text is swapped at runtime during sign-in, and a text-flow rebuilds its per-word
                // child sprites on every change. Those word sprites are tracked by the settings
                // SearchContainer (TabSearchBody), whose child bookkeeping throws
                // KeyNotFoundException when they churn under it — a hard crash on the sign-in path.
                // A single sprite changes its string without adding/removing children, so nothing
                // churns; Truncate keeps a long status inside the panel instead of overflowing.
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding
                    {
                        Left = SettingsPanel.CONTENT_PADDING.Left,
                        Right = SettingsPanel.CONTENT_PADDING.Right,
                        Top = 8,
                        Bottom = 8,
                    },
                    Child = accountStatus = new TruncatingSpriteText
                    {
                        RelativeSizeAxes = Axes.X,
                        Font = OsuFont.GetFont(size: 12),
                        Colour = Theme.TextTertiary,
                    },
                },
                accountButton = new SettingsButton { Text = "Connect osu! account" },
                accountHint = descriptionText(accountHintText),
            },
        };
    }

    /// <summary>The small muted paragraph style the credential block already uses, factored out so
    /// the Account section reads identically rather than approximately.</summary>
    private static OsuTextFlowContainer descriptionText(string text) => new OsuTextFlowContainer(t => t.Font = OsuFont.GetFont(size: 12))
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Padding = new MarginPadding
        {
            Left = SettingsPanel.CONTENT_PADDING.Left,
            Right = SettingsPanel.CONTENT_PADDING.Right,
            Top = 8,
            Bottom = 8,
        },
        Colour = Theme.TextTertiary,
        Text = text,
    };

    /// <summary>
    /// The radio: whether it plays at all, and what it picks from.
    ///
    /// <para>
    /// The filter rows are the beatmap listing's filter block said in lazer's SETTINGS idiom rather
    /// than osu-web's. The listing renders each dimension as a row of horizontal text tab items,
    /// which needs the width of a fullscreen page; this column is 340px, so each dimension becomes
    /// one labelled dropdown (or checkbox, or slider) instead — the same seven dimensions, in the
    /// same order as the screenshot, expressed in the controls the rest of this panel already uses.
    /// </para>
    ///
    /// <para>
    /// Which rows exist follows the ACTIVE BACKEND, exactly as the listing's do and from the same
    /// signal — see <see cref="updateRadioFilterAvailability"/>. Offering the radio a filter the
    /// backend will ignore would be worse here than in the listing: the listing at least shows the
    /// broader results it got, while a radio silently picks one of them and plays it.
    /// </para>
    /// </summary>
    private Drawable createRadioSection()
    {
        var rows = new List<Drawable>();

        rows.Add(radioOnEmptyQueueCheckbox = new SettingsCheckbox { LabelText = "Auto-play random song on empty queue" });
        rows.Add(radioOnStartCheckbox = new SettingsCheckbox { LabelText = "Auto-play random song on start" });
        rows.Add(new LazerSubsection("Random conditions")
        {
            Children = new Drawable[]
            {
                radioModeDropdown = new SettingsEnumDropdown<RadioRuleset> { LabelText = "Mode" },
                radioCategoryDropdown = new SettingsDropdown<SearchCategory>
                {
                    LabelText = "Categories",
                    // Favourites and Mine need a signed-in account this app doesn't have —
                    // omitted rather than shown dead, matching the listing's own Categories row.
                    Items = Enum.GetValues<SearchCategory>()
                                .Where(c => c != SearchCategory.Favourites && c != SearchCategory.Mine),
                },
                radioGenreDropdown = new SettingsEnumDropdown<SearchGenre> { LabelText = "Genre" },
                radioLanguageDropdown = new SettingsEnumDropdown<SearchLanguage> { LabelText = "Language" },
                radioHasVideoCheckbox = new SettingsCheckbox { LabelText = "Has video" },
                radioHasStoryboardCheckbox = new SettingsCheckbox { LabelText = "Has storyboard" },
                radioFeaturedArtistsCheckbox = new SettingsCheckbox { LabelText = "Featured Artists" },
                radioMinStarsRow = new SettingsSlider<double> { LabelText = "Minimum stars", KeyboardStep = 0.1f },
                radioMaxStarsRow = new SettingsSlider<double> { LabelText = "Maximum stars", KeyboardStep = 0.1f },
                radioNoFiltersHint = new OsuTextFlowContainer(t => t.Font = OsuFont.GetFont(size: 12))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding
                    {
                        Left = SettingsPanel.CONTENT_PADDING.Left,
                        Right = SettingsPanel.CONTENT_PADDING.Right,
                        Top = 8,
                        Bottom = 8,
                    },
                    Colour = Theme.TextTertiary,
                    Alpha = 0,
                Text = "The beatmap source that can be reached right now can't narrow a random "
                       + "pick at all, so there is nothing to set here.",
                },
            },
        });

        // Each row's own capability, so hiding is one table rather than a wall of assignments that
        // has to be kept in step with the rows above by eye.
        radioFilterRows = new[]
        {
            ((Drawable)radioModeDropdown, SearchFilters.Mode),
            (radioCategoryDropdown, SearchFilters.Status),
            (radioGenreDropdown, SearchFilters.Genre),
            (radioLanguageDropdown, SearchFilters.Language),
            (radioHasVideoCheckbox, SearchFilters.Extra),
            (radioHasStoryboardCheckbox, SearchFilters.Extra),
            (radioFeaturedArtistsCheckbox, SearchFilters.FeaturedArtists),
            (radioMinStarsRow, SearchFilters.Stars),
            (radioMaxStarsRow, SearchFilters.Stars),
        };

        return new LazerSection("Radio", FontAwesome.Solid.BroadcastTower) { Children = rows };
    }

    /// <summary>
    /// Shows exactly the radio filter rows the active backend can answer, hiding the rest — the
    /// same rule, from the same <see cref="BeatmapSearchEngine.AvailableFilters"/> signal, that the
    /// listing's own filter block follows. Alpha rather than removal, because each row carries a
    /// live config binding whose VALUE has to survive a backend going away and coming back; a
    /// zero-Alpha child is not IsPresent, so the section's flow closes over it.
    /// </summary>
    /// <summary>
    /// Takes a row that is currently hidden out of the search, and puts it back when it returns.
    ///
    /// <para>
    /// Every lazer settings row is an <see cref="IConditionalFilterable"/> whose
    /// <c>CanBeShown</c> the filter consults before descending — and it hands back the very
    /// <see cref="BindableBool"/> the row owns, so the cast is how a caller writes it.
    /// </para>
    /// </summary>
    private static void setSearchable(Drawable row, bool searchable)
    {
        if (row is IConditionalFilterable { CanBeShown: BindableBool flag })
            flag.Value = searchable;
    }

    private void updateRadioFilterAvailability(SearchFilters available)
    {
        foreach ((var row, var needs) in radioFilterRows)
        {
            bool shown = (available & needs) != 0;
            row.Alpha = shown ? 1 : 0;

            // …and tell the search the row is not on offer. Alpha alone would not: a filter walks
            // the drawable tree without looking at it, so a hidden row would still MATCH and hold
            // its whole subsection open around nothing. CanBeShown is lazer's own escape hatch for
            // exactly this (see IConditionalFilterable).
            setSearchable(row, shown);
        }

        // Every dimension gone leaves the subsection as a bare header, which reads as a bug.
        bool none = radioFilterRows.All(r => (available & r.Needs) == 0);

        radioNoFiltersHint.Alpha = none ? 1 : 0;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // ---- ours ----
        bindSkinDropdown();
        fpsDisplayDropdown.Current = config.GetBindable<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode);
        showStoryboardCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.ShowStoryboard);
        showVideoCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.ShowVideo);

        if (storyboardLayers != null)
            bindStoryboardLayers();
        backgroundDimRow.Current = config.GetBindable<double>(JukeBoxSetting.BackgroundDim);
        backgroundBlurRow.Current = config.GetBindable<double>(JukeBoxSetting.BackgroundBlur);
        playfieldZoomRow.Current = config.GetBindable<double>(JukeBoxSetting.PlayfieldZoom);
        removeChartMaskCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RemoveChartMask);
        removeStoryboardMaskCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RemoveStoryboardMask);
        uiScaleRow.Current = config.GetBindable<double>(JukeBoxSetting.UiScale);
        globalOffsetRow.Current = config.GetBindable<double>(JukeBoxSetting.GlobalAudioOffset);
        mirrorDropdown.Current = config.GetBindable<MirrorSource>(JukeBoxSetting.PreferredMirror);

        searchApiDropdown.Current = config.GetBindable<SearchApi>(JukeBoxSetting.SearchApi);
        clientIdTextBox.Current = config.GetBindable<string>(JukeBoxSetting.OsuClientId);
        clientSecretTextBox.Current = config.GetBindable<string>(JukeBoxSetting.OsuClientSecret);
        discordPresenceCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.DiscordRichPresence);

        // Credentials only mean anything to the official backend — with the mirrors selected the
        // block is gone rather than greyed, since there is nothing about it to explain in that mode.
        searchApiDropdown.Current.BindValueChanged(e =>
        {
            bool official = e.NewValue == SearchApi.Official;
            officialCredentials.Alpha = official ? 1 : 0;

            // Same reason as the radio rows: a search walks the tree without consulting Alpha, so
            // credentials hidden behind "Mirror" would still answer a query for "client".
            setSearchable(clientIdTextBox, official);
            setSearchable(clientSecretTextBox, official);
        }, true);

        // ---- Radio ----
        radioOnEmptyQueueCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RadioOnEmptyQueue);
        radioOnStartCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RadioOnStart);

        // See the field: these are config-bindable copies, so they and the radio's own copies of
        // the same keys move together without either side subscribing to the other.
        radioFilters = new RadioFilters(config);

        radioModeDropdown.Current = radioFilters.Mode;
        radioCategoryDropdown.Current = radioFilters.Category;
        radioGenreDropdown.Current = radioFilters.Genre;
        radioLanguageDropdown.Current = radioFilters.Language;
        radioHasVideoCheckbox.Current = radioFilters.HasVideo;
        radioHasStoryboardCheckbox.Current = radioFilters.HasStoryboard;
        radioFeaturedArtistsCheckbox.Current = radioFilters.FeaturedArtists;
        radioMinStarsRow.Current = radioFilters.MinStars;
        radioMaxStarsRow.Current = radioFilters.MaxStars;

        // With no engine to ask (bare scenes, the floating presentation) every row shows: an
        // unknown capability is not a reason to hide a setting the user may have deliberately set.
        if (searchEngine != null)
            searchEngine.AvailableFilters.BindValueChanged(e => updateRadioFilterAvailability(e.NewValue), true);
        else
            updateRadioFilterAvailability(SearchFilters.All);

        bindAccountSection();

        detachPlayerCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.DetachPlayer);

        // "Play on main window too" only means something while the player IS detached; while it
        // isn't, the row greys out with its remembered value intact (MainScreen gates on both,
        // so a stale true never shows through by itself). Two-way adapter sync: config → UI
        // lifts the disable for the mirroring write (the disable exists to stop USER edits,
        // not programmatic ones), UI → config is only reachable while enabled anyway.
        playOnMainConfig = config.GetBindable<bool>(JukeBoxSetting.DetachPlayOnMain);
        playOnMainCheckbox.Current = playOnMainUi;
        playOnMainConfig.BindValueChanged(e =>
        {
            bool wasDisabled = playOnMainUi.Disabled;
            playOnMainUi.Disabled = false;
            playOnMainUi.Value = e.NewValue;
            playOnMainUi.Disabled = wasDisabled;
        }, true);
        playOnMainUi.BindValueChanged(e => playOnMainConfig.Value = e.NewValue);
        detachPlayerCheckbox.Current.BindValueChanged(e => playOnMainUi.Disabled = !e.NewValue, true);

        // ---- framework (all apply live; renderer takes effect on restart) ----
        audioDeviceDropdown.Current = frameworkConfig.GetBindable<string>(FrameworkSetting.AudioDevice);
        masterVolumeRow.Current = frameworkConfig.GetBindable<double>(FrameworkSetting.VolumeUniversal);
        effectVolumeRow.Current = frameworkConfig.GetBindable<double>(FrameworkSetting.VolumeEffect);
        musicVolumeRow.Current = frameworkConfig.GetBindable<double>(FrameworkSetting.VolumeMusic);
        rendererDropdown.Current = frameworkConfig.GetBindable<RendererType>(FrameworkSetting.Renderer);
        frameLimiterDropdown.Current = frameworkConfig.GetBindable<FrameSync>(FrameworkSetting.FrameSync);
        threadingDropdown.Current = frameworkConfig.GetBindable<ExecutionMode>(FrameworkSetting.ExecutionMode);

        // Checkbox <-> flags-enum adapter (see the field comment above): initialise from whatever
        // the framework setting currently holds (so an externally-set specific decoder, e.g. just
        // NVDEC, still shows checked), then sync both directions.
        hardwareVideoDecoderConfig = frameworkConfig.GetBindable<HardwareVideoDecoder>(FrameworkSetting.HardwareVideoDecoder);
        hardwareAccelerationEnabled.Value = hardwareVideoDecoderConfig.Value != HardwareVideoDecoder.None;
        hardwareAccelerationEnabled.BindValueChanged(e => hardwareVideoDecoderConfig.Value = e.NewValue ? HardwareVideoDecoder.Any : HardwareVideoDecoder.None);
        hardwareVideoDecoderConfig.BindValueChanged(e => hardwareAccelerationEnabled.Value = e.NewValue != HardwareVideoDecoder.None);
        hardwareAccelerationCheckbox.Current = hardwareAccelerationEnabled;

        if (screenModeDropdown != null)
            screenModeDropdown.Current = frameworkConfig.GetBindable<WindowMode>(FrameworkSetting.WindowMode);

        if (displayDropdown != null && host.Window != null)
        {
            // The window owns display selection (and persists FrameworkSetting.LastDisplayDevice
            // itself) — bind through its bindable rather than the raw config value.
            currentDisplay.BindTo(host.Window.CurrentDisplayBindable);
            displayDropdown.Current = currentDisplay;
            host.Window.DisplaysChanged += onDisplaysChanged;
        }

        updateAudioDevices();
        audio.OnNewDevice += onAudioDeviceChanged;
        audio.OnLostDevice += onAudioDeviceChanged;

        // ---- lazer (OsuConfigManager) ----
        if (lazerConfig != null)
        {
            beatmapSkinsCheckbox.Current = lazerConfig.GetBindable<bool>(OsuSetting.BeatmapSkins);
            beatmapColoursCheckbox.Current = lazerConfig.GetBindable<bool>(OsuSetting.BeatmapColours);
            beatmapHitsoundsCheckbox.Current = lazerConfig.GetBindable<bool>(OsuSetting.BeatmapHitsounds);
            comboNormalisationRow.Current = lazerConfig.GetBindable<float>(OsuSetting.ComboColourNormalisationAmount);
            inactiveVolumeRow.Current = lazerConfig.GetBindable<double>(OsuSetting.VolumeInactive);
            positionalHitsoundsRow.Current = lazerConfig.GetBindable<float>(OsuSetting.PositionalHitsoundsLevel);
        }

        // Docked instances are the three-column layout's "Settings" tab body: shown once here and
        // never hidden again (see the class summary) — the owning tab strip toggles the tab body's
        // Alpha instead of this overlay's own visibility state.
        if (docked)
        {
            Show();
        }
    }

    /// <summary>
    /// Makes the Account section live: the button connects or disconnects depending on state, and
    /// the status line names whoever is signed in.
    /// </summary>
    private void bindAccountSection()
    {
        if (account == null)
        {
            setAccountStatus("Sign-in isn't available in this window.");
            accountButton.Enabled.Value = false;
            return;
        }

        account.Username.BindValueChanged(_ => updateAccountSection(), true);
        account.IsConnected.BindValueChanged(_ => updateAccountSection(), true);

        accountButton.Action = () =>
        {
            if (account.IsConnected.Value)
            {
                account.Disconnect();
                return;
            }

            connectAccount();
        };
    }

    private void updateAccountSection()
    {
        if (account == null)
            return;

        bool connected = account.IsConnected.Value;

        accountButton.Text = connected ? "Disconnect osu! account" : "Connect osu! account";

        setAccountStatus(connected
            ? $"Signed in as {(account.Username.Value.Length > 0 ? account.Username.Value : "your osu! account")}."
            : "Not signed in.");
    }

    /// <summary>
    /// Runs the sign-in and reports the outcome in the section's own status line.
    ///
    /// <para>
    /// Deliberately fire-and-forget with everything caught: the flow waits on a browser the user
    /// may simply close, and an unobserved faulted task there would take the update thread down
    /// (the same abort path a stray exception on any framework thread takes).
    /// </para>
    /// </summary>
    private async void connectAccount()
    {
        accountButton.Enabled.Value = false;
        setAccountStatus($"Waiting for osu! in your browser… (callback: {OsuOAuth.RedirectUri})");

        try
        {
            await account!.ConnectAsync(openUrl).ConfigureAwait(false);
        }
        catch (OsuOAuthException e)
        {
            // Already written for a person — see OsuOAuth.DescribeTokenError, which is where the
            // redirect-URI case gets its actionable sentence.
            Schedule(() => setAccountStatus(e.Message));
        }
        catch (Exception e)
        {
            Logger.Log($"osu! sign-in failed: {e.GetBaseException().Message}", level: LogLevel.Important);
            Schedule(() => setAccountStatus("Sign-in failed. See the log for details."));
        }
        finally
        {
            Schedule(() =>
            {
                accountButton.Enabled.Value = true;
                updateAccountSection();
            });
        }
    }

    private void setAccountStatus(string text)
    {
        accountStatusText = text;
        accountStatus.Text = text;
    }

    private void onAudioDeviceChanged(string deviceName) => Schedule(updateAudioDevices);

    /// <summary>
    /// <see cref="IWindow.DisplaysChanged"/> fires even when only the current display's
    /// <see cref="Display.Bounds"/> changed (e.g. a transient resolution/DPI blip while dragging
    /// the window across monitors) — not just when the actual set of displays changed. Blindly
    /// reassigning <c>displayDropdown.Items</c> on every such event makes the dropdown rebuild
    /// its internal item map keyed by <see cref="Display.Equals(Display?)"/> (which compares
    /// <see cref="Display.Bounds"/>); the previously-selected <see cref="Display"/> then fails that
    /// lookup and the dropdown resets <c>Current</c> to its first item — which round-trips through
    /// <see cref="currentDisplay"/>'s two-way bind into <see cref="GameHost.Window"/>'s
    /// <see cref="IWindow.CurrentDisplayBindable"/> and drags the real window back to that
    /// (usually primary) display, fighting the user's manual drag. Comparing first — ignoring
    /// <see cref="Display.Bounds"/>/<see cref="Display.UsableBounds"/> — and only reassigning
    /// <c>Items</c> on a genuine change avoids that reset. Mirrors lazer's own
    /// <c>LayoutSettings.DisplayListComparer</c> guard.
    /// </summary>
    private void onDisplaysChanged(IEnumerable<Display> displays) => Schedule(() =>
    {
        if (displayDropdown == null)
            return;

        var newDisplays = displays as IReadOnlyCollection<Display> ?? displays.ToList();

        if (!displayDropdown.Items.SequenceEqual(newDisplays, DisplayListComparer.Default))
            displayDropdown.Items = newDisplays;
    });

    /// <summary>
    /// The device list is "System default" (an empty device name, per AudioManager's contract)
    /// plus every currently-enabled output device; refreshed live on device hotplug.
    /// </summary>
    private void updateAudioDevices()
        => audioDeviceDropdown.Items = new[] { string.Empty }.Concat(audio.AudioDeviceNames).Distinct();

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);

        if (audio.IsNotNull())
        {
            audio.OnNewDevice -= onAudioDeviceChanged;
            audio.OnLostDevice -= onAudioDeviceChanged;
        }

        if (host.IsNotNull() && host.Window != null)
            host.Window.DisplaysChanged -= onDisplaysChanged;
    }

    // Docked: PopIn/PopOut deliberately do NOT touch Alpha at all — a docked instance's Alpha is
    // owned entirely and exclusively by the tab strip (MainScreen.selectTab), never by this
    // overlay's own Show()/Hide()/State machinery. Show() (called once, at load, purely so State
    // reads Visible for bookkeeping/tests) still triggers PopIn() same as always, so it must be a
    // genuine no-op here — a docked instance's own load-time Show() call and the owning tab
    // strip's Alpha write aren't ordering-guaranteed relative to each other (e.g. when nested
    // inside a GridContainer cell, which loads its content lazily), so if PopIn wrote Alpha too,
    // whichever of the two ran second would silently win and could leave the wrong tab showing.
    protected override void PopIn()
    {
        if (!docked)
        {
            this.FadeIn(Theme.DurationNormal, Theme.EaseEnter);
            panelCard!.ScaleTo(Theme.PopScale).Then().ScaleTo(1f, Theme.DurationNormal, Theme.EaseEnter);
        }
    }

    protected override void PopOut()
    {
        if (!docked)
        {
            this.FadeOut(Theme.DurationFast, Theme.EaseExit);
            panelCard!.ScaleTo(Theme.PopScale, Theme.DurationFast, Theme.EaseExit);
        }
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        // Docked: no overlay to close — Escape falls through (e.g. to MainScreen's own handling).
        if (!docked && !e.Repeat && e.Key == Key.Escape)
        {
            Hide();
            return true;
        }

        return base.OnKeyDown(e);
    }

    /// <summary>
    /// The gameplay-skin dropdown. Unlike every other dropdown here it is NOT a
    /// <see cref="SettingsEnumDropdown{T}"/>, because its rows are not an enum: the bundled skins
    /// are, but each imported skin is its own row too, listed under the name it declares for
    /// itself the way osu! lists a skin library. A row is therefore a <see cref="SkinChoice"/> —
    /// a bundled skin, or Custom paired with the specific folder it means.
    /// </summary>
    internal partial class SkinSettingsDropdown : SettingsDropdown<SkinChoice>
    {
        /// <summary>The bundled rows, in the order they are listed. Imported skins follow them.</summary>
        private static readonly JukeBoxSkin[] bundled =
        {
            JukeBoxSkin.Argon,
            JukeBoxSkin.ArgonPro,
            JukeBoxSkin.Triangles,
            JukeBoxSkin.Classic,
            JukeBoxSkin.Random,
        };

        /// <summary>
        /// The imported skins to list, already ordered and labelled by <see cref="SkinLibrary"/>.
        /// Left empty in test scenes that cache a config manager but no skin library, which then
        /// simply get the bundled rows.
        /// </summary>
        public readonly BindableList<ImportedSkin> Library = new BindableList<ImportedSkin>();

        /// <summary>
        /// Folder name to dropdown label, rebuilt alongside the item list. The control below reads
        /// it to render each imported row; a folder is all a <see cref="SkinChoice"/> carries, and
        /// the label is a property of the library listing rather than of the choice itself.
        /// </summary>
        private readonly Dictionary<string, string> importedLabels = new Dictionary<string, string>();

        /// <summary>Guards the item rebuild against the Current change its own assignment can cause.</summary>
        private bool rebuilding;

        protected override OsuDropdown<SkinChoice> CreateDropdown() => new SkinDropdownControl(importedLabels);

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Library.BindCollectionChanged((_, _) => rebuildItems(), true);

            // A selection can arrive naming a skin the library does not (yet) list — the config
            // value is read at startup before anything has scanned, and a folder can be deleted
            // from disk by hand. rebuildItems keeps such a row present rather than letting the
            // control fall back onto some other value and overwrite the user's choice.
            Current.BindValueChanged(_ => rebuildItems(), true);
        }

        private void rebuildItems()
        {
            if (rebuilding)
                return;

            rebuilding = true;

            try
            {
                importedLabels.Clear();

                var items = bundled.Select(SkinChoice.Bundled).ToList();

                foreach (var skin in Library)
                {
                    importedLabels[skin.Folder] = skin.Label;
                    items.Add(SkinChoice.Imported(skin.Folder));
                }

                // The selected import is missing from the library — deleted from app storage by
                // hand, or simply not scanned yet. Keep the row so the selection stays visible and
                // stays put; dropping it would leave Current pointing at nothing and the control
                // would write some other value back over a choice the user never changed.
                if (Current.Value.IsImported && !items.Contains(Current.Value))
                {
                    importedLabels[Current.Value.Folder] = Current.Value.Folder;
                    items.Add(SkinChoice.Imported(Current.Value.Folder));
                }

                Items = items;
            }
            finally
            {
                rebuilding = false;
            }
        }

        private partial class SkinDropdownControl : DropdownControl
        {
            private readonly IReadOnlyDictionary<string, string> importedLabels;

            public SkinDropdownControl(IReadOnlyDictionary<string, string> importedLabels)
            {
                this.importedLabels = importedLabels;
            }

            protected override LocalisableString GenerateItemText(SkinChoice item)
            {
                if (!item.IsImported)
                    return item.Builtin.GetDescription();

                // Falling back to the raw folder covers the window between an item being added and
                // its label being known; the folder name is a usable name in its own right.
                return importedLabels.TryGetValue(item.Folder, out string? label) ? label : item.Folder;
            }
        }
    }

    /// <summary>
    /// A <see cref="SettingsTextBox"/> whose control masks what it holds. lazer ships no settings
    /// row of this shape (its only password box lives in the login form), but it does ship the
    /// masked <see cref="OsuPasswordTextBox"/> — so this is that control dropped into
    /// <see cref="SettingsItem{T}"/>'s label/revert-arrow chrome, matching every other row here.
    /// </summary>
    internal partial class SettingsPasswordTextBox : SettingsItem<string>
    {
        protected override Drawable CreateControl() => new OsuPasswordTextBox
        {
            Margin = new MarginPadding { Top = 5 },
            RelativeSizeAxes = Axes.X,
            CommitOnFocusLost = true,
        };
    }

    /// <summary>
    /// Audio output device dropdown: device names are raw BASS strings, with the empty string
    /// meaning "let the system decide" (AudioManager's documented convention). Same shape as
    /// lazer's AudioDevicesSettings dropdown.
    /// </summary>
    internal partial class DeviceSettingsDropdown : SettingsDropdown<string>
    {
        protected override OsuDropdown<string> CreateDropdown() => new DeviceDropdownControl();

        private partial class DeviceDropdownControl : DropdownControl
        {
            protected override LocalisableString GenerateItemText(string item)
                => string.IsNullOrEmpty(item) ? "System default" : base.GenerateItemText(item);
        }
    }

    /// <summary>Display picker labelled "index: name", the same shape as lazer's DisplayDropdown.</summary>
    internal partial class DisplaySettingsDropdown : SettingsDropdown<Display>
    {
        protected override OsuDropdown<Display> CreateDropdown() => new DisplayDropdownControl();

        private partial class DisplayDropdownControl : DropdownControl
        {
            protected override LocalisableString GenerateItemText(Display item)
                => $"{item.Index}: {item.Name}";
        }
    }

    /// <summary>
    /// Compares <see cref="Display"/>s while disregarding <see cref="Display.Bounds"/> and
    /// <see cref="Display.UsableBounds"/> — see <see cref="onDisplaysChanged"/> for why those must
    /// be ignored. Equivalent to (and named after) osu.Game's own internal
    /// <c>LayoutSettings.DisplayListComparer</c>.
    /// </summary>
    internal sealed class DisplayListComparer : IEqualityComparer<Display>
    {
        public static readonly DisplayListComparer Default = new DisplayListComparer();

        public bool Equals(Display? x, Display? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;

            return x.Index == y.Index && x.Name == y.Name && x.DisplayModes.SequenceEqual(y.DisplayModes);
        }

        public int GetHashCode(Display obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Index);
            hash.Add(obj.Name);
            hash.Add(obj.DisplayModes.Length);

            foreach (var mode in obj.DisplayModes)
                hash.Add(mode);

            return hash.ToHashCode();
        }
    }
}

/// <summary>
/// One selectable row of the gameplay-skin dropdown: either a bundled skin (or
/// <see cref="JukeBoxSkin.Random"/>), or a specific imported skin.
///
/// <para>
/// Two values rather than one because that is what the setting actually is. Config stores the
/// choice across two keys — <see cref="JukeBoxSetting.Skin"/> and, when that is
/// <see cref="JukeBoxSkin.Custom"/>, the <see cref="JukeBoxSetting.CustomSkinPath"/> folder naming
/// WHICH import is meant — and a dropdown row has to carry both halves or it cannot tell one
/// imported skin from another. The folder, not the display name, is the identity: names come from
/// each skin's own skin.ini and two skins may well declare the same one.
/// </para>
/// </summary>
internal readonly record struct SkinChoice(JukeBoxSkin Builtin, string Folder)
{
    public static SkinChoice Bundled(JukeBoxSkin skin) => new SkinChoice(skin, string.Empty);

    public static SkinChoice Imported(string folder) => new SkinChoice(JukeBoxSkin.Custom, folder);

    public bool IsImported => Builtin == JukeBoxSkin.Custom;
}
