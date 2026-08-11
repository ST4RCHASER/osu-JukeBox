#nullable enable

using System;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
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
/// Centred modal settings panel, dimmed behind by a full-screen scrim — opened on demand from the
/// corner gear button. Escape or clicking the gear button again closes it
/// (<see cref="VisibilityContainer.ToggleVisibility"/>, inherited).
/// </summary>
public partial class SettingsOverlay : FocusedOverlayContainer
{
    private const float panel_width = 360;
    private const float fade_duration = 200;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    private BasicCheckbox showFpsCheckbox = null!;
    private BasicDropdown<MirrorSource> mirrorDropdown = null!;

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

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
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
                    new FillFlowContainer
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
                    }
                }
            }
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        showFpsCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.ShowFps);
        mirrorDropdown.Current = config.GetBindable<MirrorSource>(JukeBoxSetting.PreferredMirror);
    }

    protected override void PopIn() => this.FadeIn(fade_duration, Easing.OutQuint);

    protected override void PopOut() => this.FadeOut(fade_duration, Easing.OutQuint);

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!e.Repeat && e.Key == Key.Escape)
        {
            Hide();
            return true;
        }

        return base.OnKeyDown(e);
    }
}
