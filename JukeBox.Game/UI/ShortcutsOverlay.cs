#nullable enable

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// The "Show all shortcut keys" modal opened from Help — a centred card styled exactly like
/// <see cref="MapIdOverlay"/> (dim scrim + rounded panel-surface card, Escape or OK to close),
/// listing every shortcut as a clean two-column key→action table.
///
/// <para>
/// The rows are INJECTED (<see cref="ShortcutsOverlay(IReadOnlyList{ValueTuple{string, string}})"/>)
/// rather than gathered here: the authoritative list of what the app binds lives with the input
/// handlers (<see cref="Input.PlaybackShortcuts"/> and the menu bar itself), and duplicating it in
/// the viewer would let the two drift. This class only knows how to lay a list out.
/// </para>
/// </summary>
public partial class ShortcutsOverlay : FocusedOverlayContainer
{
    private const float panel_width = 460;

    private readonly IReadOnlyList<(string Keys, string Action)> shortcuts;

    private Container panelCard = null!;
    private TextButton okButton = null!;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the OK button.</summary>
    internal TextButton OkButton => okButton;

    public ShortcutsOverlay(IReadOnlyList<(string Keys, string Action)> shortcuts)
    {
        this.shortcuts = shortcuts;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        var table = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 6),
        };

        foreach (var (keys, action) in shortcuts)
            table.Add(new ShortcutRow(keys, action));

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
                                Text = "Keyboard shortcuts",
                            },
                            table,
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Child = okButton = new TextButton("OK")
                                {
                                    Anchor = Anchor.CentreRight,
                                    Origin = Anchor.CentreRight,
                                    Size = new Vector2(88, 34),
                                    IdleColour = Theme.AccentDim,
                                    HoverColour = Theme.Accent,
                                    Action = Hide,
                                },
                            },
                        },
                    },
                },
            },
        };
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
            // Escape is OK: same path as the button, so the two can't drift.
            okButton.TriggerClick();
            return true;
        }

        return base.OnKeyDown(e);
    }

    // Clicking the scrim outside the card closes too, matching the other modals.
    protected override bool OnClick(ClickEvent e)
    {
        Hide();
        return true;
    }

    /// <summary>One key→action line: the key on the left as a chip, the action's description filling
    /// the rest of the row on the right.</summary>
    private partial class ShortcutRow : CompositeDrawable
    {
        private readonly string keys;
        private readonly string action;

        public ShortcutRow(string keys, string action)
        {
            this.keys = keys;
            this.action = action;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            InternalChild = new GridContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                ColumnDimensions = new[]
                {
                    new Dimension(GridSizeMode.Absolute, 140),
                    new Dimension(),
                },
                RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
                Content = new[]
                {
                    new Drawable[]
                    {
                        new ShortcutChip(keys)
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                        },
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = action,
                            Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                            Colour = Theme.TextSecondary,
                        },
                    },
                },
            };
        }
    }
}
