#nullable enable

using System;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// The playback panel's signature element: a thin accent-pink progress line with a soft glow that
/// thickens (3px -> 6px) and reveals a seek-handle circle on hover (or while being dragged), built
/// on <see cref="SliderBar{T}"/> so it keeps the exact same drag/commit semantics
/// <see cref="NowPlayingPanel"/> already relies on (<see cref="SliderBar{T}.Current"/>,
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

    /// <summary>Exposed so <see cref="NowPlayingPanel"/> can size the block holding this bar and
    /// its elapsed/total time labels without duplicating the magic number.</summary>
    internal const float HitAreaHeight = hit_area_height;

    private Container barContainer = null!;
    private Container fillContainer = null!;
    private Circle handle = null!;

    // The YouTube-style BUFFER fill: a lighter/greyer segment behind the pink played portion, its
    // width the fraction of the replays' timelines recorded so far. Shown only while a preload runs.
    private Box buffer = null!;
    private static readonly Color4 buffer_colour = new Color4(0.72f, 0.72f, 0.78f, 0.45f);

    /// <summary>The multi-replay preload's progress, published by the combine layer. Null in a bare
    /// test host (no combine), in which case the buffer segment simply never shows.</summary>
    [Resolved(canBeNull: true)]
    private PreloadProgressTracker? preloadTracker { get; set; }

    private readonly Bindable<double> bufferProgress = new Bindable<double>(1);
    private readonly BindableBool bufferActive = new BindableBool();

    /// <summary>Test hook: the buffered fraction the grey segment currently shows, 0 to 1.</summary>
    internal float BufferFraction => buffer.Width;

    /// <summary>Test hook: whether the buffer segment is currently visible (a preload is running).</summary>
    internal bool BufferShowing => buffer.Alpha > 0.5f;

    // Transforms (ResizeHeightTo/FadeTo) must only run after LoadComplete — see IconButton's
    // `ready` field for the same guard and reasoning.
    private bool ready;

    /// <summary>Test-only access to the visual track (JukeBox.Game.Tests has
    /// InternalsVisibleTo), to assert its rendered quad stays within the containing card's
    /// bounds (rather than this whole hit-area Drawable's own quad, which is intentionally wider
    /// than the visual line to keep a comfortable drag target).</summary>
    internal Drawable VisualBar => barContainer;

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
                    // Behind the pink played portion, above the track: the buffered region.
                    buffer = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Width = 0,
                        Alpha = 0,
                        Colour = buffer_colour,
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

        if (preloadTracker != null)
        {
            bufferProgress.BindTo(preloadTracker.Progress);
            bufferActive.BindTo(preloadTracker.Active);
        }

        // The tracker is written from the update thread (Report) but also cleared during teardown on
        // the disposal thread; mutate the drawable directly on the update thread, marshal onto it
        // otherwise — the buffer-bar teardown crash pattern from round 8.
        bufferProgress.BindValueChanged(e => onUpdateThread(() => buffer.Width = (float)Math.Clamp(e.NewValue, 0, 1)), true);
        bufferActive.BindValueChanged(e => onUpdateThread(() => buffer.FadeTo(e.NewValue ? 1 : 0, 200, Easing.OutQuint)), true);
    }

    /// <summary>Runs a drawable mutation on the update thread: straight through when already on it
    /// (the live path), scheduled when not (the tracker's off-thread teardown Clear()).</summary>
    private void onUpdateThread(Action mutate)
    {
        if (ThreadSafety.IsUpdateThread)
            mutate();
        else
            Schedule(mutate);
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
