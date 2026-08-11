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
using osuTK.Graphics;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// Right-anchored, dismissable settings panel — styled like <see cref="QueuePanel"/> (dark box,
/// same width) but a plain <see cref="FocusedOverlayContainer"/> rather than a permanently-present
/// slide-in drawer, since it's opened on demand from the corner gear button and doesn't need to
/// stay docked in either <see cref="Screens.MainScreen"/> layout. Escape or clicking the gear
/// button again closes it (<see cref="VisibilityContainer.ToggleVisibility"/>, inherited).
/// </summary>
public partial class SettingsOverlay : FocusedOverlayContainer
{
    private const float panel_width = 320;
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
        RelativeSizeAxes = Axes.Y;
        Width = panel_width;
        Anchor = Anchor.TopRight;
        Origin = Anchor.TopRight;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = new Color4(28, 28, 28, 255),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(8),
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 8),
                    Children = new Drawable[]
                    {
                        new SpriteText { Font = FontUsage.Default.With(size: 18), Text = "Settings" },
                        showFpsCheckbox = new BasicCheckbox { LabelText = "Show FPS" },
                        new SpriteText { Text = "Beatmap mirror" },
                        mirrorDropdown = new BasicDropdown<MirrorSource>
                        {
                            RelativeSizeAxes = Axes.X,
                            Items = Enum.GetValues<MirrorSource>(),
                        },
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
