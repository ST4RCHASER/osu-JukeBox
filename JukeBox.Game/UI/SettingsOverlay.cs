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
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Graphics.Video;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Configuration;
using osu.Game.Rulesets.Mania.UI;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.Taiko.Configuration;
using osu.Game.Rulesets.UI;
using osuTK;
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

    private BasicScrollContainer scroll = null!;

    // ---- our settings ----
    private BasicDropdown<JukeBoxSkin> skinDropdown = null!;
    private BasicCheckbox showFpsCheckbox = null!;
    private BasicCheckbox renderChartCheckbox = null!;
    private BasicCheckbox playHitSoundsCheckbox = null!;
    private BasicCheckbox showStoryboardVideoCheckbox = null!;
    private SliderRow<double> backgroundDimRow = null!;
    private SliderRow<double> backgroundBlurRow = null!;
    private SliderRow<double> uiScaleRow = null!;
    private BasicDropdown<MirrorSource> mirrorDropdown = null!;

    // ---- framework settings ----
    private DeviceDropdown audioDeviceDropdown = null!;
    private SliderRow<double> masterVolumeRow = null!;
    private SliderRow<double> effectVolumeRow = null!;
    private SliderRow<double> musicVolumeRow = null!;
    private BasicDropdown<RendererType> rendererDropdown = null!;
    private BasicDropdown<FrameSync> frameLimiterDropdown = null!;
    private BasicDropdown<ExecutionMode> threadingDropdown = null!;
    private BasicDropdown<HardwareVideoDecoder> hardwareVideoDropdown = null!;
    private BasicDropdown<WindowMode>? screenModeDropdown;
    private BasicDropdown<Display>? displayDropdown;
    private readonly Bindable<Display> currentDisplay = new Bindable<Display>();

    // ---- lazer (OsuConfigManager) settings; only built when lazerConfig is present ----
    private BasicCheckbox hitLightingCheckbox = null!;
    private BasicCheckbox beatmapSkinsCheckbox = null!;
    private BasicCheckbox beatmapColoursCheckbox = null!;
    private BasicCheckbox beatmapHitsoundsCheckbox = null!;
    private SliderRow<float> comboNormalisationRow = null!;
    private SliderRow<double> inactiveVolumeRow = null!;
    private SliderRow<float> positionalHitsoundsRow = null!;

    // ---- ruleset settings; only built when the ruleset config cache is present ----
    private BasicCheckbox snakingInCheckbox = null!;
    private BasicCheckbox snakingOutCheckbox = null!;
    private BasicCheckbox osuHitAnimationsCheckbox = null!;
    private BasicCheckbox cursorTrailCheckbox = null!;
    private BasicCheckbox cursorRipplesCheckbox = null!;
    private BasicDropdown<PlayfieldBorderStyle> playfieldBorderDropdown = null!;
    private BasicCheckbox taikoHitAnimationsCheckbox = null!;
    private BasicDropdown<ManiaScrollingDirection> maniaDirectionDropdown = null!;
    private SliderRow<double> maniaScrollSpeedRow = null!;
    private BasicCheckbox maniaTimingColourCheckbox = null!;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to controls, to drive/assert
    /// them without depending on this panel's internal layout.
    /// </summary>
    internal BasicCheckbox ShowFpsCheckbox => showFpsCheckbox;

    internal BasicDropdown<MirrorSource> MirrorDropdown => mirrorDropdown;
    internal BasicCheckbox RenderChartCheckbox => renderChartCheckbox;
    internal BasicCheckbox PlayHitSoundsCheckbox => playHitSoundsCheckbox;
    internal BasicSliderBar<double> BackgroundDimSlider => backgroundDimRow.Slider;
    internal BasicDropdown<JukeBoxSkin> SkinDropdown => skinDropdown;
    internal BasicSliderBar<double>? ManiaScrollSpeedSlider => rulesetConfigs != null ? maniaScrollSpeedRow.Slider : null;
    internal DeviceDropdown AudioDeviceDropdown => audioDeviceDropdown;
    internal BasicSliderBar<double> MasterVolumeSlider => masterVolumeRow.Slider;

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

        InternalChildren = docked
            ? new Drawable[]
            {
                // No scrim, no floating card, no fixed width — this is inline tab-body content
                // inside the three-column layout's right panel, which already supplies the
                // surrounding panel surface/padding.
                scroll = new BasicScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
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
                            Colour = Theme.PanelSurface,
                        },
                        scroll = new BasicScrollContainer
                        {
                            RelativeSizeAxes = Axes.Both,
                            Child = createBody(),
                        },
                    },
                },
            };
    }

    /// <summary>
    /// The actual settings content, shared by both presentations (see the class summary). Always
    /// <see cref="Axes.X"/>-relative: the floating modal's fixed <see cref="panel_width"/> already
    /// constrains the outer card, and both presentations scroll vertically.
    /// </summary>
    private Drawable createBody()
    {
        var sections = new List<Drawable>
        {
            new SpriteText
            {
                Font = FontUsage.Default.With(size: Theme.HeaderTextSize),
                Colour = Theme.TextPrimary,
                Text = "Settings",
            },
            section("Skin",
                labelled("Gameplay skin", skinDropdown = new BasicDropdown<JukeBoxSkin>
                {
                    RelativeSizeAxes = Axes.X,
                    Items = Enum.GetValues<JukeBoxSkin>(),
                })),
        };

        // Per-ruleset settings need the ruleset config cache (realm-backed); a bare test scene
        // without it gets no dead controls.
        if (rulesetConfigs != null)
        {
            sections.Add(section("osu!",
                snakingInCheckbox = new BasicCheckbox { LabelText = "Snaking in sliders" },
                snakingOutCheckbox = new BasicCheckbox { LabelText = "Snaking out sliders" },
                osuHitAnimationsCheckbox = new BasicCheckbox { LabelText = "Hit animations" },
                cursorTrailCheckbox = new BasicCheckbox { LabelText = "Cursor trail" },
                cursorRipplesCheckbox = new BasicCheckbox { LabelText = "Cursor ripples" },
                labelled("Playfield border style", playfieldBorderDropdown = new BasicDropdown<PlayfieldBorderStyle>
                {
                    RelativeSizeAxes = Axes.X,
                    Items = Enum.GetValues<PlayfieldBorderStyle>(),
                })));

            sections.Add(section("osu!taiko",
                taikoHitAnimationsCheckbox = new BasicCheckbox { LabelText = "Hit animations" }));

            sections.Add(section("osu!mania",
                labelled("Scrolling direction", maniaDirectionDropdown = new BasicDropdown<ManiaScrollingDirection>
                {
                    RelativeSizeAxes = Axes.X,
                    Items = Enum.GetValues<ManiaScrollingDirection>(),
                }),
                maniaScrollSpeedRow = new SliderRow<double>("Scroll speed", v => $"{v:0.0}"),
                maniaTimingColourCheckbox = new BasicCheckbox { LabelText = "Timing-based note colouring" }));
        }

        var gameplayRows = new List<Drawable>();

        if (lazerConfig != null)
            gameplayRows.Add(hitLightingCheckbox = new BasicCheckbox { LabelText = "Hit lighting" });

        gameplayRows.Add(backgroundDimRow = new SliderRow<double>("Background dim", v => $"{v:P0}"));
        gameplayRows.Add(backgroundBlurRow = new SliderRow<double>("Background blur", v => $"{v:P0}"));
        gameplayRows.Add(renderChartCheckbox = new BasicCheckbox { LabelText = "Render chart" });
        gameplayRows.Add(playHitSoundsCheckbox = new BasicCheckbox { LabelText = "Play hit sounds" });
        sections.Add(section("Gameplay", gameplayRows.ToArray()));

        var beatmapRows = new List<Drawable>();

        if (lazerConfig != null)
        {
            beatmapRows.Add(beatmapSkinsCheckbox = new BasicCheckbox { LabelText = "Beatmap skins" });
            beatmapRows.Add(beatmapColoursCheckbox = new BasicCheckbox { LabelText = "Beatmap colours" });
            beatmapRows.Add(beatmapHitsoundsCheckbox = new BasicCheckbox { LabelText = "Beatmap hitsounds" });
        }

        beatmapRows.Add(showStoryboardVideoCheckbox = new BasicCheckbox { LabelText = "Storyboard / video" });

        if (lazerConfig != null)
            beatmapRows.Add(comboNormalisationRow = new SliderRow<float>("Combo colour normalisation", v => $"{v:P0}"));

        sections.Add(section("Beatmap", beatmapRows.ToArray()));

        var audioRows = new List<Drawable>();
        audioRows.Add(labelled("Output device", audioDeviceDropdown = new DeviceDropdown { RelativeSizeAxes = Axes.X }));
        audioRows.Add(masterVolumeRow = new SliderRow<double>("Master", v => $"{v:P0}"));

        if (lazerConfig != null)
            audioRows.Add(inactiveVolumeRow = new SliderRow<double>("Master (window inactive)", v => $"{v:P0}"));

        audioRows.Add(effectVolumeRow = new SliderRow<double>("Effect", v => $"{v:P0}"));
        audioRows.Add(musicVolumeRow = new SliderRow<double>("Music", v => $"{v:P0}"));

        if (lazerConfig != null)
            audioRows.Add(positionalHitsoundsRow = new SliderRow<float>("Hitsound stereo separation", v => $"{v:P0}"));

        sections.Add(section("Audio", audioRows.ToArray()));

        var graphicsRows = new List<Drawable>();

        // A headless host has no window — the window-bound rows simply don't exist there.
        if (host.Window != null)
        {
            graphicsRows.Add(labelled("Screen mode", screenModeDropdown = new BasicDropdown<WindowMode>
            {
                RelativeSizeAxes = Axes.X,
                Items = host.Window.SupportedWindowModes,
            }));

            graphicsRows.Add(labelled("Display", displayDropdown = new BasicDropdown<Display>
            {
                RelativeSizeAxes = Axes.X,
                Items = host.Window.Displays,
            }));
        }

        graphicsRows.Add(uiScaleRow = new SliderRow<double>("UI scaling", v => $"{v:0.00}x"));
        graphicsRows.Add(labelled("Renderer (requires restart)", rendererDropdown = new BasicDropdown<RendererType>
        {
            RelativeSizeAxes = Axes.X,
            Items = host.GetPreferredRenderersForCurrentPlatform(),
        }));
        graphicsRows.Add(labelled("Frame limiter", frameLimiterDropdown = new BasicDropdown<FrameSync>
        {
            RelativeSizeAxes = Axes.X,
            Items = Enum.GetValues<FrameSync>(),
        }));
        graphicsRows.Add(labelled("Threading mode", threadingDropdown = new BasicDropdown<ExecutionMode>
        {
            RelativeSizeAxes = Axes.X,
            Items = Enum.GetValues<ExecutionMode>(),
        }));
        graphicsRows.Add(showFpsCheckbox = new BasicCheckbox { LabelText = "Show FPS" });
        graphicsRows.Add(labelled("Video hardware acceleration", hardwareVideoDropdown = new BasicDropdown<HardwareVideoDecoder>
        {
            RelativeSizeAxes = Axes.X,
            Items = new[] { HardwareVideoDecoder.None, HardwareVideoDecoder.Any },
        }));
        sections.Add(section("Graphics", graphicsRows.ToArray()));

        sections.Add(section("Online",
            labelled("Beatmap mirror", mirrorDropdown = new BasicDropdown<MirrorSource>
            {
                RelativeSizeAxes = Axes.X,
                Items = Enum.GetValues<MirrorSource>(),
            })));

        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding(Theme.PanelPadding),
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, Theme.SectionSpacing),
            Children = sections,
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // ---- ours ----
        skinDropdown.Current = config.GetBindable<JukeBoxSkin>(JukeBoxSetting.Skin);
        showFpsCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.ShowFps);
        renderChartCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RenderChart);
        playHitSoundsCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.PlayHitSounds);
        showStoryboardVideoCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.ShowStoryboardVideo);
        backgroundDimRow.Slider.Current = config.GetBindable<double>(JukeBoxSetting.BackgroundDim);
        backgroundBlurRow.Slider.Current = config.GetBindable<double>(JukeBoxSetting.BackgroundBlur);
        uiScaleRow.Slider.Current = config.GetBindable<double>(JukeBoxSetting.UiScale);
        mirrorDropdown.Current = config.GetBindable<MirrorSource>(JukeBoxSetting.PreferredMirror);

        // ---- framework (all apply live; renderer takes effect on restart) ----
        audioDeviceDropdown.Current = frameworkConfig.GetBindable<string>(FrameworkSetting.AudioDevice);
        masterVolumeRow.Slider.Current = frameworkConfig.GetBindable<double>(FrameworkSetting.VolumeUniversal);
        effectVolumeRow.Slider.Current = frameworkConfig.GetBindable<double>(FrameworkSetting.VolumeEffect);
        musicVolumeRow.Slider.Current = frameworkConfig.GetBindable<double>(FrameworkSetting.VolumeMusic);
        rendererDropdown.Current = frameworkConfig.GetBindable<RendererType>(FrameworkSetting.Renderer);
        frameLimiterDropdown.Current = frameworkConfig.GetBindable<FrameSync>(FrameworkSetting.FrameSync);
        threadingDropdown.Current = frameworkConfig.GetBindable<ExecutionMode>(FrameworkSetting.ExecutionMode);
        hardwareVideoDropdown.Current = frameworkConfig.GetBindable<HardwareVideoDecoder>(FrameworkSetting.HardwareVideoDecoder);

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
            comboNormalisationRow.Slider.Current = lazerConfig.GetBindable<float>(OsuSetting.ComboColourNormalisationAmount);
            inactiveVolumeRow.Slider.Current = lazerConfig.GetBindable<double>(OsuSetting.VolumeInactive);
            positionalHitsoundsRow.Slider.Current = lazerConfig.GetBindable<float>(OsuSetting.PositionalHitsoundsLevel);
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
        }

        if (rulesetConfigs.GetConfigFor(new TaikoRuleset()) is TaikoRulesetConfigManager taikoRulesetConfig)
            taikoHitAnimationsCheckbox.Current = taikoRulesetConfig.GetBindable<bool>(TaikoRulesetSetting.HitAnimations);

        if (rulesetConfigs.GetConfigFor(new ManiaRuleset()) is ManiaRulesetConfigManager maniaRulesetConfig)
        {
            maniaDirectionDropdown.Current = maniaRulesetConfig.GetBindable<ManiaScrollingDirection>(ManiaRulesetSetting.ScrollDirection);
            maniaScrollSpeedRow.Slider.Current = maniaRulesetConfig.GetBindable<double>(ManiaRulesetSetting.ScrollSpeed);
            maniaTimingColourCheckbox.Current = maniaRulesetConfig.GetBindable<bool>(ManiaRulesetSetting.TimingBasedNoteColouring);
        }
    }

    private void onAudioDeviceChanged(string deviceName) => Schedule(updateAudioDevices);

    private void onDisplaysChanged(IEnumerable<Display> displays) => Schedule(() =>
    {
        if (displayDropdown != null)
            displayDropdown.Items = displays;
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

    private static Drawable section(string title, params Drawable[] rows)
    {
        var flow = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, Theme.RowSpacing),
        };

        flow.Add(new SpriteText
        {
            Font = FontUsage.Default.With(size: 16),
            Colour = Theme.Accent,
            Text = title,
        });

        foreach (var row in rows)
            flow.Add(row);

        return flow;
    }

    private static Drawable labelled(string label, Drawable control) => new FillFlowContainer
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Vertical,
        Spacing = new Vector2(0, 4),
        Children = new[]
        {
            new SpriteText
            {
                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                Colour = Theme.TextSecondary,
                Text = label,
            },
            control,
        },
    };

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
    /// A slider with its label on the left and the live formatted value on the right — the shared
    /// row shape for every slider in the panel.
    /// </summary>
    private partial class SliderRow<T> : FillFlowContainer
        where T : struct, System.Numerics.INumber<T>, System.Numerics.IMinMaxValue<T>
    {
        public readonly BasicSliderBar<T> Slider;

        private readonly SpriteText valueLabel;
        private readonly Func<T, string> format;

        public SliderRow(string label, Func<T, string> format)
        {
            this.format = format;

            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Vertical;
            Spacing = new Vector2(0, 4);

            Children = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Children = new Drawable[]
                    {
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                            Colour = Theme.TextSecondary,
                            Text = label,
                        },
                        valueLabel = new SpriteText
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                            Colour = Theme.TextPrimary,
                        },
                    },
                },
                Slider = new BasicSliderBar<T>
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 8,
                    CornerRadius = 4,
                    Masking = true,
                    BackgroundColour = Theme.ElevatedSurface,
                    SelectionColour = Theme.Accent,
                    FocusColour = Theme.Accent,
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Bound on the slider's own Current (a stable BindableWithCurrent) so the label keeps
            // tracking across any later Current reassignment.
            Slider.Current.BindValueChanged(e => valueLabel.Text = format(e.NewValue), true);
        }
    }

    /// <summary>
    /// Audio output device dropdown: device names are raw BASS strings, with the empty string
    /// meaning "let the system decide" (AudioManager's documented convention).
    /// </summary>
    internal partial class DeviceDropdown : BasicDropdown<string>
    {
        protected override LocalisableString GenerateItemText(string item)
            => string.IsNullOrEmpty(item) ? "System default" : item;
    }
}
