#nullable enable

using System;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
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
/// </summary>
public partial class SettingsOverlay : FocusedOverlayContainer
{
    private const float panel_width = 360;
    private const float fade_duration = 200;

    /// <summary>See the class summary.</summary>
    private readonly bool docked;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    private BasicCheckbox showFpsCheckbox = null!;
    private BasicCheckbox renderChartCheckbox = null!;
    private BasicCheckbox playHitSoundsCheckbox = null!;
    private BasicSliderBar<double> backgroundDimSlider = null!;
    private SpriteText backgroundDimLabel = null!;
    private BasicDropdown<MirrorSource> mirrorDropdown = null!;

    // Field, not a local: config bindables use a weak-reference chain back to the master value —
    // an unrooted local would be collected, silently dropping the % label binding.
    private Bindable<double> backgroundDim = null!;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the "Show FPS" checkbox,
    /// to drive/assert it without depending on this panel's internal layout.
    /// </summary>
    internal BasicCheckbox ShowFpsCheckbox => showFpsCheckbox;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the "Beatmap mirror"
    /// dropdown, to drive/assert it without depending on this panel's internal layout.
    /// </summary>
    internal BasicDropdown<MirrorSource> MirrorDropdown => mirrorDropdown;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the "Render
    /// chart" checkbox.</summary>
    internal BasicCheckbox RenderChartCheckbox => renderChartCheckbox;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the "Play hit
    /// sounds" checkbox.</summary>
    internal BasicCheckbox PlayHitSoundsCheckbox => playHitSoundsCheckbox;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the background
    /// dim slider.</summary>
    internal BasicSliderBar<double> BackgroundDimSlider => backgroundDimSlider;

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
                new BasicScrollContainer
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
                new Container
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Width = panel_width,
                    AutoSizeAxes = Axes.Y,
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
                        createBody(),
                    },
                },
            };
    }

    /// <summary>
    /// The actual settings content, shared by both presentations (see the class summary). Always
    /// <see cref="Axes.X"/>-relative: the floating modal's fixed <see cref="panel_width"/> already
    /// constrains the outer card, and the docked scroll container constrains the tab body.
    /// </summary>
    private Drawable createBody()
    {
        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding(Theme.PanelPadding),
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, Theme.SectionSpacing),
            Children = new Drawable[]
            {
                new SpriteText
                {
                    Font = FontUsage.Default.With(size: Theme.HeaderTextSize),
                    Colour = Theme.TextPrimary,
                    Text = "Settings",
                },
                showFpsCheckbox = new BasicCheckbox { LabelText = "Show FPS" },
                renderChartCheckbox = new BasicCheckbox { LabelText = "Render chart" },
                playHitSoundsCheckbox = new BasicCheckbox { LabelText = "Play hit sounds" },
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
                            Text = "Background dim",
                        },
                        backgroundDimLabel = new SpriteText
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                            Colour = Theme.TextPrimary,
                        },
                    }
                },
                backgroundDimSlider = new BasicSliderBar<double>
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 8,
                    CornerRadius = 4,
                    Masking = true,
                    BackgroundColour = Theme.ElevatedSurface,
                    SelectionColour = Theme.Accent,
                    FocusColour = Theme.Accent,
                },
                new SpriteText
                {
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextSecondary,
                    Text = "Beatmap mirror",
                },
                mirrorDropdown = new BasicDropdown<MirrorSource>
                {
                    RelativeSizeAxes = Axes.X,
                    Items = Enum.GetValues<MirrorSource>(),
                },
            }
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        showFpsCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.ShowFps);
        renderChartCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RenderChart);
        playHitSoundsCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.PlayHitSounds);
        mirrorDropdown.Current = config.GetBindable<MirrorSource>(JukeBoxSetting.PreferredMirror);

        backgroundDim = config.GetBindable<double>(JukeBoxSetting.BackgroundDim);
        backgroundDimSlider.Current = backgroundDim;
        backgroundDim.BindValueChanged(e => backgroundDimLabel.Text = $"{e.NewValue:P0}", true);

        // Docked instances are the three-column layout's "Settings" tab body: shown once here and
        // never hidden again (see the class summary) — the owning tab strip toggles the tab body's
        // Alpha instead of this overlay's own visibility state.
        if (docked)
        {
            Show();
        }
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
            this.FadeIn(fade_duration, Easing.OutQuint);
    }

    protected override void PopOut()
    {
        if (!docked)
            this.FadeOut(fade_duration, Easing.OutQuint);
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
}
