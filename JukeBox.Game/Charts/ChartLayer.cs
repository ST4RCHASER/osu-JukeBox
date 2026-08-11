#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Lines;
using osu.Framework.Graphics.Shapes;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.Charts;

/// <summary>
/// Autoplay-style display of a difficulty's hit objects (circles + approach circles, slider
/// bodies, spinners) in the standard 512×384 osu-pixel playfield. Every object's animation is
/// compiled ONCE at load into framework transforms (same architecture as
/// <see cref="Storyboard.TransformStoryboardLayer"/>): zero per-frame evaluation, and the internal
/// <see cref="LifetimeManagementContainer"/> skips objects outside their lifetime window entirely.
/// Seeking works in both directions (<c>RemoveCompletedTransforms</c> is false and dead objects
/// stay children).
/// </summary>
public partial class ChartLayer : CompositeDrawable
{
    public override bool RemoveCompletedTransforms => false;

    /// <summary>Objects currently inside their lifetime window. Exposed for tests.</summary>
    internal int AliveObjectCount => objects?.AliveObjects.Count() ?? 0;

    /// <summary>Total object drawables constructed. Exposed for tests.</summary>
    internal int TotalObjectCount => objects?.AllObjects.Count ?? 0;

    /// <summary>How long a hit-circle pop/fade lingers after its hit time.</summary>
    private const double hit_fade_duration = 150;

    private readonly ChartBeatmap beatmap;

    private ObjectContainer? objects;

    public ChartLayer(ChartBeatmap beatmap)
    {
        this.beatmap = beatmap;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Size = new Vector2(512, 384);

        AddInternal(objects = new ObjectContainer { RelativeSizeAxes = Axes.Both });

        float radius = Math.Max(4, beatmap.CircleRadius);
        double preempt = beatmap.PreemptMs;
        double fadeIn = beatmap.FadeInMs;

        int comboIndex = 0;
        bool first = true;

        // Later objects must render UNDER earlier ones (osu! stacking rule), so depth increases
        // with index. LifetimeManagementContainer respects Depth for draw order.
        float depth = 0;

        foreach (var obj in beatmap.HitObjects)
        {
            if ((obj.NewCombo || first) && obj.Kind != HitObjectKind.Spinner)
                comboIndex++;
            first = false;

            var colour = Theme.ComboColours[comboIndex % Theme.ComboColours.Length];

            Drawable drawable = obj.Kind switch
            {
                HitObjectKind.Circle => new DrawableChartCircle(obj, radius, preempt, fadeIn, colour),
                HitObjectKind.Slider => new DrawableChartSlider(obj, radius, preempt, fadeIn, colour),
                _ => new DrawableChartSpinner(obj, preempt),
            };

            drawable.Depth = depth++;
            objects.AddObject(drawable);
        }
    }

    private partial class ObjectContainer : LifetimeManagementContainer
    {
        public IEnumerable<Drawable> AliveObjects => AliveInternalChildren;
        public IReadOnlyList<Drawable> AllObjects => InternalChildren;

        public void AddObject(Drawable drawable) => AddInternal(drawable);
    }

    /// <summary>An approach-circle ring: thin-bordered circle scaling 4→1 over the preempt window.</summary>
    private static CircularContainer makeApproachRing(float radius, Color4 colour) => new CircularContainer
    {
        Anchor = Anchor.Centre,
        Origin = Anchor.Centre,
        Size = new Vector2(radius * 2),
        Masking = true,
        BorderThickness = 3,
        BorderColour = colour,
        Scale = new Vector2(4),
        Child = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Alpha = 0,
            AlwaysPresent = true, // border only renders while a child is present
        },
    };

    /// <summary>A filled hit-circle disc with a white rim.</summary>
    private static CircularContainer makeDisc(float radius, Color4 colour) => new CircularContainer
    {
        Anchor = Anchor.Centre,
        Origin = Anchor.Centre,
        Size = new Vector2(radius * 2),
        Masking = true,
        BorderThickness = Math.Max(3, radius * 0.12f),
        BorderColour = Color4.White,
        Child = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = colour,
        },
    };

    internal partial class DrawableChartCircle : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private readonly ChartHitObject obj;
        private readonly float radius;
        private readonly double preempt;
        private readonly double fadeIn;
        private readonly Color4 colour;

        public DrawableChartCircle(ChartHitObject obj, float radius, double preempt, double fadeIn, Color4 colour)
        {
            this.obj = obj;
            this.radius = radius;
            this.preempt = preempt;
            this.fadeIn = fadeIn;
            this.colour = colour;

            Origin = Anchor.Centre;
            Position = new Vector2(obj.X, obj.Y);
            Size = new Vector2(radius * 2);
            Alpha = 0;

            LifetimeStart = obj.Time - preempt;
            LifetimeEnd = obj.Time + hit_fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            CircularContainer approach;

            InternalChildren = new Drawable[]
            {
                makeDisc(radius, colour),
                approach = makeApproachRing(radius, colour),
            };

            using (BeginAbsoluteSequence(obj.Time - preempt))
            {
                this.FadeTo(1, fadeIn);
                approach.ScaleTo(1, preempt);
            }

            using (BeginAbsoluteSequence(obj.Time))
            {
                approach.FadeOut();
                this.ScaleTo(1.4f, hit_fade_duration, Easing.OutQuint).FadeOut(hit_fade_duration);
            }
        }
    }

    internal partial class DrawableChartSlider : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private readonly ChartHitObject obj;
        private readonly float radius;
        private readonly double preempt;
        private readonly double fadeIn;
        private readonly Color4 colour;

        public DrawableChartSlider(ChartHitObject obj, float radius, double preempt, double fadeIn, Color4 colour)
        {
            this.obj = obj;
            this.radius = radius;
            this.preempt = preempt;
            this.fadeIn = fadeIn;
            this.colour = colour;

            // Children are positioned in coordinates relative to the slider head, which sits at
            // this container's Position within the playfield.
            Position = new Vector2(obj.X, obj.Y);
            Alpha = 0;

            LifetimeStart = obj.Time - preempt;
            LifetimeEnd = obj.EndTime + hit_fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            var head = new Vector2(obj.X, obj.Y);
            List<Vector2> vertices = SliderCurve.Sample(obj.CurveType, obj.ControlPoints, obj.PixelLength)
                                                .Select(v => v - head)
                                                .ToList();
            if (vertices.Count == 0)
                vertices.Add(Vector2.Zero);

            var body = new SmoothPath
            {
                PathRadius = radius,
                Colour = colour.Darken(0.35f),
                Alpha = 0.85f,
                Vertices = vertices,
            };

            // A Path's drawable quad spans its vertex bounding box (plus radius); shift it so the
            // vertex at the head lands on this container's origin.
            body.Position = -body.PositionInBoundingBox(Vector2.Zero);

            CircularContainer approach;

            InternalChildren = new Drawable[]
            {
                body,
                new CircularContainer // tail marker
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                    Position = vertices[^1],
                    Size = new Vector2(radius * 2),
                    Masking = true,
                    BorderThickness = 3,
                    BorderColour = Color4.White,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = 0.6f },
                },
                makeDisc(radius, colour),
                approach = makeApproachRing(radius, colour),
            };

            using (BeginAbsoluteSequence(obj.Time - preempt))
            {
                this.FadeTo(1, fadeIn);
                approach.ScaleTo(1, preempt);
            }

            using (BeginAbsoluteSequence(obj.Time))
                approach.FadeOut();

            using (BeginAbsoluteSequence(obj.EndTime))
                this.FadeOut(hit_fade_duration);
        }
    }

    internal partial class DrawableChartSpinner : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private const float spinner_size = 260;

        private readonly ChartHitObject obj;
        private readonly double preempt;

        public DrawableChartSpinner(ChartHitObject obj, double preempt)
        {
            this.obj = obj;
            this.preempt = preempt;

            Origin = Anchor.Centre;
            Position = new Vector2(256, 192);
            Size = new Vector2(spinner_size);
            Alpha = 0;

            LifetimeStart = obj.Time - preempt;
            LifetimeEnd = obj.EndTime + hit_fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Container rotor;

            InternalChildren = new Drawable[]
            {
                new CircularContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    BorderThickness = 6,
                    BorderColour = Color4.White,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Alpha = 0, AlwaysPresent = true },
                },
                rotor = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Child = new Box // "spin" marker: a radial bar from centre to rim
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.BottomCentre,
                        Size = new Vector2(5, spinner_size / 2f - 8),
                        Colour = Theme.Accent,
                    },
                },
            };

            double duration = Math.Max(1, obj.EndTime - obj.Time);

            using (BeginAbsoluteSequence(obj.Time - preempt))
                this.FadeTo(1, fadeIn_for(preempt));

            using (BeginAbsoluteSequence(obj.Time))
            {
                // One full revolution per 500ms of spinner duration — purely decorative.
                rotor.RotateTo(0).RotateTo((float)(360 * duration / 500), duration);
            }

            using (BeginAbsoluteSequence(obj.EndTime))
                this.FadeOut(hit_fade_duration);
        }

        private static double fadeIn_for(double preempt) => 400 * preempt / 1200;
    }
}
