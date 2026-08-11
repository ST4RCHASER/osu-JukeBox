#nullable enable

using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// The bottom bar's signature element: a thin accent-pink progress line with a soft glow that
/// thickens (3px -> 6px) and reveals a seek-handle circle on hover (or while being dragged), built
/// on <see cref="SliderBar{T}"/> so it keeps the exact same drag/commit semantics
/// <see cref="NowPlayingBar"/> already relies on (<see cref="SliderBar{T}.Current"/>,
/// <see cref="Drawable.IsDragged"/>, <see cref="SliderBar{T}.TransferValueOnCommit"/>).
/// </summary>
internal partial class ProgressSliderBar : SliderBar<double>
{
    private const float idle_thickness = 3;
    private const float hover_thickness = 6;
    private const float handle_size = 12;

    // Taller than the visual line itself so there's a comfortable hit/drag target even while idle
    // and thin.
    private const float hit_area_height = 16;

    private Container barContainer = null!;
    private Container fillContainer = null!;
    private Circle handle = null!;

    // Transforms (ResizeHeightTo/FadeTo) must only run after LoadComplete — see IconButton's
    // `ready` field for the same guard and reasoning.
    private bool ready;

    [BackgroundDependencyLoader]
    private void load()
    {
        Height = hit_area_height;

        InternalChildren = new Drawable[]
        {
            barContainer = new Container
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                RelativeSizeAxes = Axes.X,
                Height = idle_thickness,
                Masking = true,
                CornerRadius = idle_thickness / 2,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Theme.ElevatedSurface,
                    },
                    fillContainer = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Width = 0,
                        Masking = true,
                        CornerRadius = idle_thickness / 2,
                        EdgeEffect = new EdgeEffectParameters
                        {
                            Type = EdgeEffectType.Glow,
                            Colour = Theme.Accent.Opacity(0.55f),
                            Radius = 8,
                        },
                        Child = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Theme.Accent,
                        },
                    },
                },
            },
            handle = new Circle
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.Centre,
                RelativePositionAxes = Axes.X,
                Size = new Vector2(handle_size),
                Colour = Theme.Accent,
                Alpha = 0,
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        ready = true;
    }

    protected override bool OnHover(HoverEvent e)
    {
        setExpanded(true);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        // Keep the expanded state while an active drag continues past the pointer leaving the
        // hit area — collapsing mid-drag would be a jarring visual jump for no functional reason.
        if (!IsDragged)
            setExpanded(false);
        base.OnHoverLost(e);
    }

    protected override void OnDragEnd(DragEndEvent e)
    {
        base.OnDragEnd(e);
        if (!IsHovered)
            setExpanded(false);
    }

    private void setExpanded(bool expanded)
    {
        float thickness = expanded ? hover_thickness : idle_thickness;

        if (ready)
        {
            barContainer.ResizeHeightTo(thickness, Theme.HoverFadeDuration, Easing.OutQuint);
            handle.FadeTo(expanded ? 1 : 0, Theme.HoverFadeDuration);
        }
        else
        {
            barContainer.Height = thickness;
            handle.Alpha = expanded ? 1 : 0;
        }

        barContainer.CornerRadius = thickness / 2;
        fillContainer.CornerRadius = thickness / 2;
    }

    protected override void UpdateValue(float value)
    {
        fillContainer.Width = value;
        handle.X = value;
    }
}
