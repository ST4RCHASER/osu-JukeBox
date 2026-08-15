#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
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
public partial class SettingsOverlay : FocusedOverlayContainer
{
    private const float panel_width = 360;

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

    // Only ever assigned (and used) in the floating branch of load() — a docked instance has no
    // card to pop, and its PopIn/PopOut are both guarded no-ops (see their own comments below).
    private Container? panelCard;

    // Likewise floating-only: the docked presentation deliberately paints no ground of its own (see
    // load()), so this stays null there.
    private Box? cardBackground;

    private OsuScrollContainer scroll = null!;

    // ---- our settings ----
    private SkinSettingsDropdown skinDropdown = null!;
    private SettingsCheckbox showStoryboardVideoCheckbox = null!;
    private SettingsSlider<double> backgroundDimRow = null!;
    private SettingsSlider<double> backgroundBlurRow = null!;
    private SettingsSlider<double> uiScaleRow = null!;
    private SettingsSlider<double> playfieldZoomRow = null!;
    private SettingsEnumDropdown<MirrorSource> mirrorDropdown = null!;
    private SettingsEnumDropdown<SearchApi> searchApiDropdown = null!;
    private SettingsTextBox clientIdTextBox = null!;
    private SettingsPasswordTextBox clientSecretTextBox = null!;

    // Wraps the three official-API-only rows so "Search API = Mirror" hides the whole block in one
    // write. Hidden by Alpha (not removed) because these rows carry live config bindables that must
    // keep their values across toggles; a zero-Alpha child is not IsPresent, so the surrounding
    // FillFlowContainer stops laying it out entirely rather than leaving a gap.
    private Container officialCredentials = null!;
    private SettingsCheckbox detachPlayerCheckbox = null!;
    private SettingsCheckbox playOnMainCheckbox = null!;

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

    /// <summary>Test seam (JukeBox.Game.Tests has InternalsVisibleTo): replaces
    /// <see cref="osu.Framework.Platform.GameHost.OpenUrlExternally"/> so tests can assert the
    /// opened URL without actually opening a browser. Mirrors
    /// <see cref="FullscreenListingOverlay.OpenUrl"/>.</summary>
    internal Action<string>? OpenUrl;

    private void openUrl(string url)
    {
        if (OpenUrl != null)
            OpenUrl(url);
        else
            host.OpenUrlExternally(url);
    }

    internal SettingsSlider<double> BackgroundDimSlider => backgroundDimRow;
    internal SettingsSlider<double> PlayfieldZoomSlider => playfieldZoomRow;
    internal SettingsDropdown<JukeBoxSkin> SkinDropdown => skinDropdown;
    internal SettingsDropdown<string> AudioDeviceDropdown => audioDeviceDropdown;
    internal SettingsSlider<double> MasterVolumeSlider => masterVolumeRow;
    internal SettingsCheckbox DetachPlayerCheckbox => detachPlayerCheckbox;
    internal SettingsCheckbox PlayOnMainCheckbox => playOnMainCheckbox;

    /// <summary>Test-only: the panel's own background surface — non-null only for the floating
    /// card, which is a card in its own right over a scrim. The docked presentation paints none, so
    /// its sections sit on the hosting column's single surface (see load()).</summary>
    internal Box? CardBackground => cardBackground;

    /// <summary>Test-only: scrolls a control into view (instantly, so the very next test step's
    /// mouse coordinates are already final) so real mouse input can reach it.</summary>
    internal void ScrollControlIntoView(Drawable control) => scroll.ScrollIntoView(control, animated: false);

    public SettingsOverlay(bool docked = false)
    {
        this.docked = docked;
    }

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
                    scroll = new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        ScrollbarVisible = true,
                        Child = createBody(),
                    },
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
                            scroll = new OsuScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                ScrollbarVisible = true,
                                Child = createBody(),
                            },
                        },
                    },
                },
        };
    }

    /// <summary>
    /// The actual settings content, shared by both presentations (see the class summary). Always
    /// <see cref="Axes.X"/>-relative: the floating modal's fixed <see cref="panel_width"/> already
    /// constrains the outer card, and both presentations scroll vertically. Layout is lazer's:
    /// big TorusAlternate section headers (via <see cref="SettingsSection"/>), bold subsection
    /// headers (via <see cref="SettingsSubsection"/>), items carrying their own content padding.
    /// </summary>
    private Drawable createBody()
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
        sections.Add(new LazerSection("Gameplay", FontAwesome.Regular.DotCircle) { Children = gameplayRows });

        var beatmapRows = new List<Drawable>();

        if (lazerConfig != null)
        {
            beatmapRows.Add(beatmapSkinsCheckbox = new SettingsCheckbox { LabelText = "Beatmap skins" });
            beatmapRows.Add(beatmapColoursCheckbox = new SettingsCheckbox { LabelText = "Beatmap colours" });
            beatmapRows.Add(beatmapHitsoundsCheckbox = new SettingsCheckbox { LabelText = "Beatmap hitsounds" });
        }

        beatmapRows.Add(showStoryboardVideoCheckbox = new SettingsCheckbox { LabelText = "Storyboard / video" });

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
                                    Left = SettingsPanel.CONTENT_PADDING.Left,
                                    Right = SettingsPanel.CONTENT_PADDING.Right,
                                    Top = 8,
                                    Bottom = 8,
                                },
                                Colour = Theme.TextTertiary,
                                Text = "Create an OAuth application on your osu! account page and paste its id and "
                                       + "secret here. The callback URL can be left blank. Nothing about your account "
                                       + "is read — the credentials only unlock osu!'s own beatmap search.",
                            },
                            new SettingsButton
                            {
                                Text = "Open osu! OAuth settings",
                                Action = () => openUrl(oauth_application_url),
                            },
                        },
                    },
                },
            },
        });

        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding { Bottom = 20 },
            Direction = FillDirection.Vertical,
            Children = sections,
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // ---- ours ----
        skinDropdown.Current = config.GetBindable<JukeBoxSkin>(JukeBoxSetting.Skin);

        // Names the imported .osk in the Custom entry ("Custom: Aristia" rather than a bare
        // "Custom (imported)"). Bound to the config value rather than SkinSelection so this works
        // in test scenes that cache a config manager but no skin service.
        skinDropdown.CustomSkinName.BindTo(config.GetBindable<string>(JukeBoxSetting.CustomSkinPath));
        fpsDisplayDropdown.Current = config.GetBindable<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode);
        showStoryboardVideoCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.ShowStoryboardVideo);
        backgroundDimRow.Current = config.GetBindable<double>(JukeBoxSetting.BackgroundDim);
        backgroundBlurRow.Current = config.GetBindable<double>(JukeBoxSetting.BackgroundBlur);
        playfieldZoomRow.Current = config.GetBindable<double>(JukeBoxSetting.PlayfieldZoom);
        uiScaleRow.Current = config.GetBindable<double>(JukeBoxSetting.UiScale);
        globalOffsetRow.Current = config.GetBindable<double>(JukeBoxSetting.GlobalAudioOffset);
        mirrorDropdown.Current = config.GetBindable<MirrorSource>(JukeBoxSetting.PreferredMirror);

        searchApiDropdown.Current = config.GetBindable<SearchApi>(JukeBoxSetting.SearchApi);
        clientIdTextBox.Current = config.GetBindable<string>(JukeBoxSetting.OsuClientId);
        clientSecretTextBox.Current = config.GetBindable<string>(JukeBoxSetting.OsuClientSecret);

        // Credentials only mean anything to the official backend — with the mirrors selected the
        // block is gone rather than greyed, since there is nothing about it to explain in that mode.
        searchApiDropdown.Current.BindValueChanged(e => officialCredentials.Alpha = e.NewValue == SearchApi.Official ? 1 : 0, true);

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
    /// The gameplay-skin dropdown, identical to a plain <see cref="SettingsEnumDropdown{T}"/>
    /// except that <see cref="JukeBoxSkin.Custom"/> is labelled with the imported skin's own name
    /// ("Custom: Aristia") once there is one — the enum's <c>[Description]</c> alone can only say
    /// something generic, and a user with a skin imported wants to see WHICH one this entry means.
    /// </summary>
    internal partial class SkinSettingsDropdown : SettingsEnumDropdown<JukeBoxSkin>
    {
        public readonly Bindable<string> CustomSkinName = new Bindable<string>(string.Empty);

        protected override OsuDropdown<JukeBoxSkin> CreateDropdown() => new SkinDropdownControl(CustomSkinName);

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Item text is generated when the item list is assigned, so a name arriving later
            // (a .osk dropped mid-session) needs the list re-assigned to take effect. Dropdown
            // keeps its Current across the reassignment, since the same values are still present.
            CustomSkinName.BindValueChanged(_ => Items = Items.ToArray());
        }

        private partial class SkinDropdownControl : DropdownControl
        {
            private readonly IBindable<string> customSkinName;

            public SkinDropdownControl(IBindable<string> customSkinName)
            {
                this.customSkinName = customSkinName;
            }

            protected override LocalisableString GenerateItemText(JukeBoxSkin item)
                => item == JukeBoxSkin.Custom && customSkinName.Value.Length > 0
                    ? $"Custom: {customSkinName.Value}"
                    : base.GenerateItemText(item);
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
