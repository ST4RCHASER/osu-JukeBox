#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.Charts;

/// <summary>
/// Autoplay osu!taiko chart: one horizontal track with a hit target on the left; notes scroll
/// right→left arriving at the target exactly on their time. Don (red) / kat (blue, whistle|clap),
/// big finisher notes drawn larger, sliders as scrolling drumroll capsules, spinners as a
/// "denden" disc held at the target. Compiled transforms + lifetime management, as everywhere.
/// </summary>
public partial class TaikoChartLayer : CompositeDrawable, IChartRenderer
{
    public override bool RemoveCompletedTransforms => false;

    /// <summary>Time a note is visible before reaching the target.</summary>
    public const double scroll_window_ms = 800;

    internal const float target_x = 72;
    internal const float track_y = 192;
    private const float spawn_x = 562;
    private const float small_radius = 22;
    private const float big_radius = 32;
    private const double hit_fade_duration = 150;

    /// <summary>Capsule width cap — hostile drumroll durations must not build absurd quads.</summary>
    private const float max_capsule_width = 2000;

    private static readonly Color4 don_red = new Color4(0xEB, 0x45, 0x3B, 0xFF);
    private static readonly Color4 kat_blue = new Color4(0x43, 0x8D, 0xF5, 0xFF);
    private static readonly Color4 roll_yellow = new Color4(0xEB, 0xC5, 0x3B, 0xFF);

    public int AliveObjectCount => elements?.AliveElements.Count() ?? 0;
    public int TotalObjectCount => elements?.AllElements.Count ?? 0;

    private readonly ChartBeatmap beatmap;

    private ElementContainer? elements;

    public TaikoChartLayer(ChartBeatmap beatmap)
    {
        this.beatmap = beatmap;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Size = new Vector2(512, 384);

        // Static stage: track strip + target ring.
        AddInternal(new Box
        {
            Position = new Vector2(0, track_y - 45),
            Size = new Vector2(512, 90),
            Colour = Color4.Black,
            Alpha = 0.55f,
        });

        AddInternal(new CircularContainer
        {
            Origin = Anchor.Centre,
            Position = new Vector2(target_x, track_y),
            Size = new Vector2(big_radius * 2 + 8),
            Masking = true,
            BorderThickness = 4,
            BorderColour = Color4.White,
            Child = new Box { RelativeSizeAxes = Axes.Both, Alpha = 0, AlwaysPresent = true },
        });

        AddInternal(elements = new ElementContainer { RelativeSizeAxes = Axes.Both });

        foreach (var obj in beatmap.HitObjects)
        {
            switch (obj.Kind)
            {
                case HitObjectKind.Slider:
                    elements.AddElement(new DrawableDrumroll(obj));
                    break;

                case HitObjectKind.Spinner:
                    elements.AddElement(new DrawableDenden(obj));
                    break;

                default: // circles (and any stray holds) are plain hits
                    elements.AddElement(new DrawableTaikoHit(obj));
                    break;
            }
        }
    }

    private partial class ElementContainer : LifetimeManagementContainer
    {
        public IEnumerable<Drawable> AliveElements => AliveInternalChildren;
        public IReadOnlyList<Drawable> AllElements => InternalChildren;

        public void AddElement(Drawable drawable) => AddInternal(drawable);
    }

    internal partial class DrawableTaikoHit : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private readonly ChartHitObject obj;

        public DrawableTaikoHit(ChartHitObject obj)
        {
            this.obj = obj;

            float radius = ModeChartComputations.IsBig(obj.HitSound) ? big_radius : small_radius;
            var colour = ModeChartComputations.IsKat(obj.HitSound) ? kat_blue : don_red;

            Origin = Anchor.Centre;
            Position = new Vector2(spawn_x, track_y);
            Size = new Vector2(radius * 2);
            Alpha = 0;
            Masking = true;
            CornerRadius = radius;
            BorderThickness = Math.Max(3, radius * 0.14f);
            BorderColour = Color4.White;

            InternalChild = new Box { RelativeSizeAxes = Axes.Both, Colour = colour };

            LifetimeStart = obj.Time - scroll_window_ms;
            LifetimeEnd = obj.Time + hit_fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            using (BeginAbsoluteSequence(obj.Time - scroll_window_ms))
            {
                this.FadeTo(1, 80);
                this.MoveToX(target_x, scroll_window_ms);
            }

            using (BeginAbsoluteSequence(obj.Time))
                this.ScaleTo(1.3f, hit_fade_duration, Easing.OutQuint).FadeOut(hit_fade_duration);
        }
    }

    internal partial class DrawableDrumroll : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private readonly ChartHitObject obj;
        private readonly float bodyWidth;

        public DrawableDrumroll(ChartHitObject obj)
        {
            this.obj = obj;

            const float px_per_ms = (spawn_x - target_x) / (float)scroll_window_ms;
            double duration = Math.Max(1, obj.EndTime - obj.Time);
            bodyWidth = Math.Min(max_capsule_width, (float)duration * px_per_ms) + small_radius * 2;

            Origin = Anchor.CentreLeft; // leading (left) edge is the travel reference
            Position = new Vector2(spawn_x - small_radius, track_y);
            Size = new Vector2(bodyWidth, small_radius * 2);
            Alpha = 0;
            Masking = true;
            CornerRadius = small_radius;
            BorderThickness = 3;
            BorderColour = Color4.White;

            InternalChild = new Box { RelativeSizeAxes = Axes.Both, Colour = roll_yellow, Alpha = 0.9f };

            LifetimeStart = obj.Time - scroll_window_ms;
            LifetimeEnd = obj.EndTime + hit_fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            using (BeginAbsoluteSequence(obj.Time - scroll_window_ms))
            {
                this.FadeTo(1, 80);
                this.MoveToX(target_x - small_radius, scroll_window_ms); // head reaches the target at Time
            }

            // Keep scrolling until the tail has passed through the target, then fade.
            using (BeginAbsoluteSequence(obj.Time))
                this.MoveToX(target_x - small_radius - (bodyWidth - small_radius * 2), Math.Max(1, obj.EndTime - obj.Time));

            using (BeginAbsoluteSequence(obj.EndTime))
                this.FadeOut(hit_fade_duration);
        }
    }

    internal partial class DrawableDenden : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private readonly ChartHitObject obj;

        public DrawableDenden(ChartHitObject obj)
        {
            this.obj = obj;

            Origin = Anchor.Centre;
            Position = new Vector2(target_x, track_y);
            Size = new Vector2(big_radius * 2.4f);
            Alpha = 0;
            Masking = true;
            CornerRadius = big_radius * 1.2f;
            BorderThickness = 4;
            BorderColour = roll_yellow;

            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = don_red, Alpha = 0.35f },
                new Circle
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new Vector2(big_radius),
                    Colour = roll_yellow,
                },
            };

            LifetimeStart = obj.Time - 200;
            LifetimeEnd = obj.EndTime + hit_fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            using (BeginAbsoluteSequence(obj.Time - 200))
                this.FadeTo(1, 200);

            using (BeginAbsoluteSequence(obj.EndTime))
                this.ScaleTo(1.3f, hit_fade_duration, Easing.OutQuint).FadeOut(hit_fade_duration);
        }
    }
}
