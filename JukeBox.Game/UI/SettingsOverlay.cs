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
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Configuration;
using osu.Game.Rulesets.Mania.UI;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.Taiko.Configuration;
using osu.Game.Rulesets.UI;
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
/// Every control binds a REAL config bindable, from one of four config sources:
/// <see cref="JukeBoxConfigManager"/> (ours), <see cref="FrameworkConfigManager"/> (host-cached,
/// always present), the lazer-side <see cref="OsuConfigManager"/> and the per-ruleset config
/// managers via <see cref="IRulesetConfigCache"/> (both cached by JukeBoxGameBase). The lazer-side
/// sections are simply omitted when those dependencies aren't cached (bare framework test scenes)
/// rather than rendering dead controls.
/// </summary>
public partial class SettingsOverlay : FocusedOverlayContainer
{
    private const float panel_width = 360;

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

    [Resolved(canBeNull: true)]
    private IRulesetConfigCache? rulesetConfigs { get; set; }

    // Only ever assigned (and used) in the floating branch of load() — a docked instance has no
    // card to pop, and its PopIn/PopOut are both guarded no-ops (see their own comments below).
    private Container? panelCard;

    private OsuScrollContainer scroll = null!;

    // ---- our settings ----
    private SettingsEnumDropdown<JukeBoxSkin> skinDropdown = null!;
    private SettingsCheckbox renderChartCheckbox = null!;
    private SettingsCheckbox playHitSoundsCheckbox = null!;
    private SettingsCheckbox showStoryboardVideoCheckbox = null!;
    private SettingsSlider<double> backgroundDimRow = null!;
    private SettingsSlider<double> backgroundBlurRow = null!;
    private SettingsSlider<double> uiScaleRow = null!;
    private SettingsSlider<double> playfieldZoomRow = null!;
    private SettingsEnumDropdown<MirrorSource> mirrorDropdown = null!;
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
    private SettingsCheckbox hitLightingCheckbox = null!;
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

    // ---- replay analysis (osu! ruleset config; only built with the ruleset config cache) ----
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

    // ---- ruleset settings; only built when the ruleset config cache is present ----
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

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to controls, to drive/assert
    /// them without depending on this panel's internal layout.
    /// </summary>
    internal SettingsDropdown<FpsDisplayMode> FpsDisplayDropdown => fpsDisplayDropdown;
    internal SettingsCheckbox HardwareAccelerationCheckbox => hardwareAccelerationCheckbox;

    internal SettingsDropdown<MirrorSource> MirrorDropdown => mirrorDropdown;
    internal SettingsCheckbox RenderChartCheckbox => renderChartCheckbox;
    internal SettingsCheckbox PlayHitSoundsCheckbox => playHitSoundsCheckbox;
    internal SettingsSlider<double> BackgroundDimSlider => backgroundDimRow;
    internal SettingsSlider<double> PlayfieldZoomSlider => playfieldZoomRow;
    internal SettingsDropdown<JukeBoxSkin> SkinDropdown => skinDropdown;
    internal SettingsSlider<double>? ManiaScrollSpeedSlider => rulesetConfigs != null ? maniaScrollSpeedRow : null;
    internal SettingsDropdown<string> AudioDeviceDropdown => audioDeviceDropdown;
    internal SettingsSlider<double> MasterVolumeSlider => masterVolumeRow;
    internal SettingsCheckbox DetachPlayerCheckbox => detachPlayerCheckbox;
    internal SettingsCheckbox PlayOnMainCheckbox => playOnMainCheckbox;

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
                    // No scrim, no floating card, no fixed width — this is inline tab-body content
                    // inside the three-column layout's right panel. The backdrop matches lazer's
                    // settings panel ground (Background4 of the shared purple scheme) so the lazer
                    // controls sit on their intended surface.
                    new Box
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
                            new Box
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
            new Section("Skin", FontAwesome.Solid.PaintBrush)
            {
                Children = new Drawable[]
                {
                    skinDropdown = new SettingsEnumDropdown<JukeBoxSkin> { LabelText = "Gameplay skin" },
                },
            },
        };

        // Per-ruleset settings need the ruleset config cache (realm-backed); a bare test scene
        // without it gets no dead controls. One big "Rulesets" section, lazer-style, with one
        // subsection per ruleset (same rows, same bindables as before).
        if (rulesetConfigs != null)
        {
            sections.Add(new Section("Rulesets", FontAwesome.Solid.Gamepad)
            {
                Children = new Drawable[]
                {
                    new Subsection("osu!")
                    {
                        Children = new Drawable[]
                        {
                            snakingInCheckbox = new SettingsCheckbox { LabelText = "Snaking in sliders" },
                            snakingOutCheckbox = new SettingsCheckbox { LabelText = "Snaking out sliders" },
                            osuHitAnimationsCheckbox = new SettingsCheckbox { LabelText = "Hit animations" },
                            cursorTrailCheckbox = new SettingsCheckbox { LabelText = "Cursor trail" },
                            cursorRipplesCheckbox = new SettingsCheckbox { LabelText = "Cursor ripples" },
                            playfieldBorderDropdown = new SettingsEnumDropdown<PlayfieldBorderStyle> { LabelText = "Playfield border style" },
                        },
                    },
                    new Subsection("osu!taiko")
                    {
                        Children = new Drawable[]
                        {
                            taikoHitAnimationsCheckbox = new SettingsCheckbox { LabelText = "Hit animations" },
                        },
                    },
                    new Subsection("osu!mania")
                    {
                        Children = new Drawable[]
                        {
                            maniaDirectionDropdown = new SettingsEnumDropdown<ManiaScrollingDirection> { LabelText = "Scrolling direction" },
                            maniaScrollSpeedRow = new SettingsSlider<double> { LabelText = "Scroll speed", KeyboardStep = 0.5f },
                            maniaTimingColourCheckbox = new SettingsCheckbox { LabelText = "Timing-based note colouring" },
                        },
                    },
                    // Replay-analysis overlays: our autoplay chart IS replay-driven, and
                    // LazerChartLayer attaches lazer's ReplayAnalysisOverlay for osu! charts,
                    // bound to these keys.
                    new Subsection("Analysis (osu!)")
                    {
                        Children = new Drawable[]
                        {
                            clickMarkersCheckbox = new SettingsCheckbox { LabelText = "Show click markers" },
                            frameMarkersCheckbox = new SettingsCheckbox { LabelText = "Show frame markers" },
                            cursorPathCheckbox = new SettingsCheckbox { LabelText = "Show cursor path" },
                            hideCursorCheckbox = new SettingsCheckbox { LabelText = "Hide gameplay cursor" },
                            analysisLengthRow = new SettingsSlider<int> { LabelText = "Display length", KeyboardStep = 100 },
                        },
                    },
                },
            });
        }

        var gameplayRows = new List<Drawable>();

        if (lazerConfig != null)
            gameplayRows.Add(hitLightingCheckbox = new SettingsCheckbox { LabelText = "Hit lighting" });

        gameplayRows.Add(backgroundDimRow = new SettingsSlider<double> { LabelText = "Background dim", DisplayAsPercentage = true, KeyboardStep = 0.01f });
        gameplayRows.Add(backgroundBlurRow = new SettingsSlider<double> { LabelText = "Background blur", DisplayAsPercentage = true, KeyboardStep = 0.01f });
        gameplayRows.Add(renderChartCheckbox = new SettingsCheckbox { LabelText = "Render chart" });
        gameplayRows.Add(playHitSoundsCheckbox = new SettingsCheckbox { LabelText = "Play hit sounds" });
        gameplayRows.Add(playfieldZoomRow = new SettingsSlider<double> { LabelText = "Playfield zoom", DisplayAsPercentage = true, KeyboardStep = 0.01f });
        sections.Add(new Section("Gameplay", FontAwesome.Regular.DotCircle) { Children = gameplayRows });

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

        sections.Add(new Section("Beatmap", FontAwesome.Solid.Music) { Children = beatmapRows });

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

        sections.Add(new Section("Audio", FontAwesome.Solid.VolumeUp) { Children = audioRows });

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
        graphicsRows.Add(new Subsection("Video Playback")
        {
            Children = new Drawable[]
            {
                hardwareAccelerationCheckbox = new SettingsCheckbox { LabelText = "Use hardware acceleration" },
            },
        });
        sections.Add(new Section("Graphics", FontAwesome.Solid.Laptop) { Children = graphicsRows });

        sections.Add(new Section("Online", FontAwesome.Solid.GlobeAsia)
        {
            Children = new Drawable[]
            {
                mirrorDropdown = new SettingsEnumDropdown<MirrorSource> { LabelText = "Beatmap mirror" },
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
        fpsDisplayDropdown.Current = config.GetBindable<FpsDisplayMode>(JukeBoxSetting.FpsDisplayMode);
        renderChartCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RenderChart);
        playHitSoundsCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.PlayHitSounds);
        showStoryboardVideoCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.ShowStoryboardVideo);
        backgroundDimRow.Current = config.GetBindable<double>(JukeBoxSetting.BackgroundDim);
        backgroundBlurRow.Current = config.GetBindable<double>(JukeBoxSetting.BackgroundBlur);
        playfieldZoomRow.Current = config.GetBindable<double>(JukeBoxSetting.PlayfieldZoom);
        uiScaleRow.Current = config.GetBindable<double>(JukeBoxSetting.UiScale);
        globalOffsetRow.Current = config.GetBindable<double>(JukeBoxSetting.GlobalAudioOffset);
        mirrorDropdown.Current = config.GetBindable<MirrorSource>(JukeBoxSetting.PreferredMirror);
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
            hitLightingCheckbox.Current = lazerConfig.GetBindable<bool>(OsuSetting.HitLighting);
            beatmapSkinsCheckbox.Current = lazerConfig.GetBindable<bool>(OsuSetting.BeatmapSkins);
            beatmapColoursCheckbox.Current = lazerConfig.GetBindable<bool>(OsuSetting.BeatmapColours);
            beatmapHitsoundsCheckbox.Current = lazerConfig.GetBindable<bool>(OsuSetting.BeatmapHitsounds);
            comboNormalisationRow.Current = lazerConfig.GetBindable<float>(OsuSetting.ComboColourNormalisationAmount);
            inactiveVolumeRow.Current = lazerConfig.GetBindable<double>(OsuSetting.VolumeInactive);
            positionalHitsoundsRow.Current = lazerConfig.GetBindable<float>(OsuSetting.PositionalHitsoundsLevel);
        }

        // ---- rulesets ----
        if (rulesetConfigs != null)
            bindRulesetConfigs();

        // Docked instances are the three-column layout's "Settings" tab body: shown once here and
        // never hidden again (see the class summary) — the owning tab strip toggles the tab body's
        // Alpha instead of this overlay's own visibility state.
        if (docked)
        {
            Show();
        }
    }

    /// <summary>
    /// Ruleset config managers are realm-backed and only exist once
    /// <c>LazerRulesetConfigCache</c> has loaded on the update thread (its GetConfigFor throws
    /// before that, by design) — retry next frame until it's ready. The bound bindables are the
    /// REAL per-ruleset config values, so the DrawableRuleset pieces that bind them (snaking,
    /// cursor trail, mania scroll) react live; the rest apply on the next chart (re)build.
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

            // Two-way sync (see analysisDisplayLength remarks) instead of a direct bind.
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
    /// A concrete lazer <see cref="SettingsSection"/> (big TorusAlternate header, separator,
    /// content padding). Standalone-safe: with no SettingsPanel in DI it self-selects, so the
    /// section-dimming/scroll-to-section machinery is inert.
    /// </summary>
    private partial class Section : SettingsSection
    {
        private readonly LocalisableString header;
        private readonly IconUsage icon;

        public Section(LocalisableString header, IconUsage icon)
        {
            this.header = header;
            this.icon = icon;
        }

        public override LocalisableString Header => header;

        public override Drawable CreateIcon() => new SpriteIcon { Icon = icon };
    }

    /// <summary>A concrete lazer <see cref="SettingsSubsection"/> (bold subsection header).</summary>
    private partial class Subsection : SettingsSubsection
    {
        private readonly LocalisableString header;

        public Subsection(LocalisableString header)
        {
            this.header = header;
        }

        protected override LocalisableString Header => header;
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
