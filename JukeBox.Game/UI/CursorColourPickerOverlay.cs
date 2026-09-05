#nullable enable

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// The cursor-colour picker as a centred MODAL — an HSV+hex <see cref="osu.Game.Graphics.UserInterfaceV2.OsuColourPicker"/>
/// over a dim scrim, with Apply and Cancel. Opened from the rainbow swatch in the Players panel
/// (<see cref="PlayersPanel"/>) rather than living inline in the panel, where the picker's saturation
/// square crowded the sidebar. Apply hands the chosen colour back through the callback
/// <see cref="Open"/> was given — the panel both applies it to the current player and remembers it as
/// a new swatch — and Cancel closes with no change.
/// </summary>
public partial class CursorColourPickerOverlay : FocusedOverlayContainer
{
    private const float panel_width = 320;

    private Container panelCard = null!;
    private osu.Game.Graphics.UserInterfaceV2.OsuColourPicker picker = null!;
    private TextButton applyButton = null!;
    private TextButton cancelButton = null!;

    private Action<Color4>? onApply;

    /// <summary>Test hook: the embedded picker.</summary>
    internal osu.Game.Graphics.UserInterfaceV2.OsuColourPicker Picker => picker;

    /// <summary>Test hook: the Apply / Cancel buttons.</summary>
    internal TextButton ApplyButton => applyButton;

    internal TextButton CancelButton => cancelButton;

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
            panelCard = new Container
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
                                Text = "Pick a cursor colour",
                            },
                            // Every direct child of a vertical flow must share one anchor (the
                            // framework throws otherwise — and did, the first time this opened), so
                            // the centred picker and the right-aligned buttons each sit inside a
                            // full-width strip and are anchored within THAT.
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Child = picker = new osu.Game.Graphics.UserInterfaceV2.OsuColourPicker
                                {
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                },
                            },
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Child = new FillFlowContainer
                                {
                                    Anchor = Anchor.TopRight,
                                    Origin = Anchor.TopRight,
                                    AutoSizeAxes = Axes.Both,
                                    Direction = FillDirection.Horizontal,
                                    Spacing = new Vector2(Theme.RowSpacing, 0),
                                    Children = new Drawable[]
                                    {
                                        cancelButton = new TextButton("Cancel")
                                        {
                                            Size = new Vector2(88, 34),
                                            Action = Hide,
                                        },
                                        applyButton = new TextButton("Apply")
                                        {
                                            Size = new Vector2(88, 34),
                                            IdleColour = Theme.AccentDim,
                                            HoverColour = Theme.Accent,
                                            Action = apply,
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    /// <summary>Opens the picker seeded at <paramref name="start"/>; <paramref name="applied"/> is
    /// called with the chosen colour if the user presses Apply.</summary>
    public void Open(Color4 start, Action<Color4> applied)
    {
        onApply = applied;
        picker.Current.Value = start;
        Show();
    }

    private void apply()
    {
        var picked = picker.Current.Value;
        onApply?.Invoke(new Color4(picked.R, picked.G, picked.B, 1f));
        Hide();
    }

    protected override void PopIn()
    {
        this.FadeIn(Theme.DurationNormal, Theme.EaseEnter);
        panelCard.ScaleTo(Theme.PopScale).Then().ScaleTo(1f, Theme.DurationNormal, Theme.EaseEnter);
    }

    protected override void PopOut()
    {
        this.FadeOut(Theme.DurationFast, Theme.EaseExit);
        panelCard.ScaleTo(Theme.PopScale, Theme.DurationFast, Theme.EaseExit);
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!e.Repeat && e.Key == Key.Escape)
        {
            cancelButton.TriggerClick();
            return true;
        }

        return base.OnKeyDown(e);
    }

    // A click on the scrim (outside the card) cancels, matching the app's other modals.
    protected override bool OnClick(ClickEvent e)
    {
        if (!panelCard.ReceivePositionalInputAt(e.ScreenSpaceMousePosition))
            Hide();
        return true;
    }
}
