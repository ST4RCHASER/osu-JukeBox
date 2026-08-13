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
using osu.Framework.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.Charts;

/// <summary>
/// Autoplay-style display of a difficulty's hit objects in the standard 512×384 osu-pixel
/// playfield, modelled on lazer's default-skin visuals: numbered hitcircles with approach circles,
/// bordered slider tracks with travelling ball + follow circle, reverse arrows and ticks, faint
/// follow-point trails between combo neighbours, detailed spinners, and osu!'s stack-shift so
/// jump stacks read correctly. Every animation is compiled ONCE at load into framework transforms
/// (same architecture as <see cref="Storyboard.TransformStoryboardLayer"/>): zero per-frame
/// evaluation, and the internal <see cref="LifetimeManagementContainer"/>s skip drawables outside
/// their lifetime window entirely. Seeking works in both directions
/// (<c>RemoveCompletedTransforms</c> is false and dead objects stay children).
/// </summary>
public partial class ChartLayer : CompositeDrawable, IChartRenderer
{
    public override bool RemoveCompletedTransforms => false;

    /// <summary>Hit objects currently inside their lifetime window. Exposed for tests.</summary>
    internal int AliveObjectCount => objects?.AliveElements.Count() ?? 0;

    /// <summary>Total hit-object drawables constructed. Exposed for tests.</summary>
    internal int TotalObjectCount => objects?.AllElements.Count ?? 0;

    int IChartRenderer.AliveObjectCount => AliveObjectCount;
    int IChartRenderer.TotalObjectCount => TotalObjectCount;

    /// <summary>Total follow-point dot drawables constructed. Exposed for tests.</summary>
    internal int FollowPointCount => followPoints?.AllElements.Count ?? 0;

    /// <summary>How long a hit pop/fade lingers after its hit time.</summary>
    private const double hit_fade_duration = 150;

    private readonly ChartBeatmap beatmap;

    private ElementContainer? objects;
    private ElementContainer? followPoints;

    public ChartLayer(ChartBeatmap beatmap)
    {
        this.beatmap = beatmap;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Size = new Vector2(512, 384);

        // Follow points sit UNDER the hit objects, in their own lifetime container so the
        // object-count test accessors (and the objects' draw order) stay untouched.
        AddInternal(followPoints = new ElementContainer { RelativeSizeAxes = Axes.Both });
        AddInternal(objects = new ElementContainer { RelativeSizeAxes = Axes.Both });

        float radius = Math.Max(4, beatmap.CircleRadius);
        double preempt = beatmap.PreemptMs;
        double fadeIn = beatmap.FadeInMs;

        ChartComputations.ApplyStacking(beatmap);
        var combos = ChartComputations.AssignCombos(beatmap.HitObjects, Theme.ComboColours.Length);

        // Later objects must render UNDER earlier ones (osu! stacking rule), so depth increases
        // with index. LifetimeManagementContainer respects Depth for draw order.
        float depth = 0;

        for (int i = 0; i < beatmap.HitObjects.Count; i++)
        {
            var obj = beatmap.HitObjects[i];
            var colour = Theme.ComboColours[combos[i].ColourIndex % Theme.ComboColours.Length];

            // Mania holds have no osu!std representation — only relevant if a hostile file mixes
            // type bits; a real std map never contains them.
            if (obj.Kind == HitObjectKind.Hold)
                continue;

            Drawable drawable = obj.Kind switch
            {
                HitObjectKind.Circle => new DrawableChartCircle(obj, radius, preempt, fadeIn, colour, combos[i].NumberInCombo),
                HitObjectKind.Slider => new DrawableChartSlider(obj, beatmap, radius, preempt, fadeIn, colour, combos[i].NumberInCombo),
                _ => new DrawableChartSpinner(obj, preempt),
            };

            drawable.Depth = depth++;
            objects.AddElement(drawable);
        }

        foreach (var spec in ChartComputations.FollowPoints(beatmap, radius))
            followPoints.AddElement(new DrawableFollowPoint(spec));
    }

    private partial class ElementContainer : LifetimeManagementContainer
    {
        public IEnumerable<Drawable> AliveElements => AliveInternalChildren;
        public IReadOnlyList<Drawable> AllElements => InternalChildren;

        public void AddElement(Drawable drawable) => AddInternal(drawable);
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

    /// <summary>A filled hit-circle disc (combo colour) with a white rim ~9% of the radius thick.</summary>
    private static CircularContainer makeDisc(float radius, Color4 colour) => new CircularContainer
    {
        Anchor = Anchor.Centre,
        Origin = Anchor.Centre,
        Size = new Vector2(radius * 2),
        Masking = true,
        BorderThickness = Math.Max(3, radius * 0.09f),
        BorderColour = Color4.White,
        Child = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = colour,
        },
    };

    /// <summary>The combo number, centred in the disc — font ≈ 40% of the circle's radius scale.</summary>
    private static SpriteText makeNumber(float radius, int number) => new SpriteText
    {
        Anchor = Anchor.Centre,
        Origin = Anchor.Centre,
        Font = FontUsage.Default.With(size: radius * 0.8f),
        Colour = Color4.White,
        Text = number.ToString(),
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
        private readonly int number;

        public DrawableChartCircle(ChartHitObject obj, float radius, double preempt, double fadeIn, Color4 colour, int number)
        {
            this.obj = obj;
            this.radius = radius;
            this.preempt = preempt;
            this.fadeIn = fadeIn;
            this.colour = colour;
            this.number = number;

            Origin = Anchor.Centre;
            Position = ChartComputations.StackedPosition(obj, radius);
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
                makeNumber(radius, number),
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

                // Explode: ring, fill and number pop together (they're all children of this).
                this.ScaleTo(1.4f, hit_fade_duration, Easing.OutQuint).FadeOut(hit_fade_duration);
            }
        }
    }

    internal partial class DrawableChartSlider : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        /// <summary>Test-only: number of reverse-arrow drawables constructed.</summary>
        internal int ReverseArrowCount { get; private set; }

        /// <summary>Test-only: number of tick drawables constructed.</summary>
        internal int TickCount { get; private set; }

        /// <summary>Test-only: the travelling ball + follow circle container.</summary>
        internal Container BallContainer { get; private set; } = null!;

        private readonly ChartHitObject obj;
        private readonly ChartBeatmap beatmap;
        private readonly float radius;
        private readonly double preempt;
        private readonly double fadeIn;
        private readonly Color4 colour;
        private readonly int number;

        public DrawableChartSlider(ChartHitObject obj, ChartBeatmap beatmap, float radius, double preempt, double fadeIn, Color4 colour, int number)
        {
            this.obj = obj;
            this.beatmap = beatmap;
            this.radius = radius;
            this.preempt = preempt;
            this.fadeIn = fadeIn;
            this.colour = colour;
            this.number = number;

            // Children are positioned in coordinates relative to the slider head, which sits at
            // this container's Position within the playfield.
            Position = ChartComputations.StackedPosition(obj, radius);
            Alpha = 0;

            LifetimeStart = obj.Time - preempt;
            LifetimeEnd = obj.EndTime + hit_fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // One log per absurd slider — every renderer input (ball keyframes, arrows, ticks)
            // derives from the same clamped span count inside ChartComputations.
            if (obj.Slides > ChartComputations.MaxRenderedSlides)
            {
                osu.Framework.Logging.Logger.Log(
                    $"ChartLayer: slider at t={obj.Time} has {obj.Slides} slides; rendering only the first {ChartComputations.MaxRenderedSlides}");
            }

            var head = new Vector2(obj.X, obj.Y);
            List<Vector2> vertices = SliderCurve.Sample(obj.CurveType, obj.ControlPoints, obj.PixelLength)
                                                .Select(v => v - head)
                                                .ToList();
            if (vertices.Count == 0)
                vertices.Add(Vector2.Zero);

            // Track look, lazer-style: a lighter outline path underneath and a darker
            // semi-transparent fill path (smaller radius) layered on top of it.
            var bodyBorder = new SmoothPath
            {
                PathRadius = radius,
                Colour = Color4.White,
                Alpha = 0.75f,
                Vertices = vertices,
            };
            bodyBorder.Position = -bodyBorder.PositionInBoundingBox(Vector2.Zero);

            var bodyFill = new SmoothPath
            {
                PathRadius = radius * 0.86f,
                Colour = colour.Darken(0.65f),
                Alpha = 0.85f,
                Vertices = vertices,
            };
            bodyFill.Position = -bodyFill.PositionInBoundingBox(Vector2.Zero);

            var children = new List<Drawable>
            {
                bodyBorder,
                bodyFill,
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
            };

            // ---- ticks (pop as the ball passes them, per span) ----------------------------

            // NOTE (root cause of the invisible-ball bug, applies to ticks/arrows/ball alike):
            // transforms added to a drawable that has no clock yet — i.e. constructed here but
            // not yet attached via InternalChildren — are applied INSTANTLY to their end state
            // and never registered (osu.framework's clock-less AddTransform behaviour). So all
            // per-child animation specs are collected first and only compiled into transforms
            // AFTER the children are attached (attachment during load() loads them with a clock).
            var tickAnimations = new List<(Circle Dot, SliderTickSpec Spec)>();
            var arrowAnimations = new List<(SpriteIcon Chevron, ReverseArrowSpec Spec)>();

            foreach (var tick in ChartComputations.SliderTicks(obj, beatmap, vertices, preempt))
            {
                var dot = new Circle
                {
                    Origin = Anchor.Centre,
                    Position = tick.Position,
                    Size = new Vector2(Math.Max(3, radius * 0.3f)),
                    Colour = Color4.White,
                    Alpha = 0,
                };

                children.Add(dot);
                tickAnimations.Add((dot, tick));
                TickCount++;
            }

            // ---- reverse arrows -----------------------------------------------------------

            foreach (var arrow in ChartComputations.ReverseArrows(obj, vertices, preempt))
            {
                var chevron = new SpriteIcon
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                    Position = arrow.Position,
                    Size = new Vector2(radius * 1.1f),
                    Icon = FontAwesome.Solid.ChevronRight,
                    Colour = Color4.White,
                    Rotation = arrow.RotationDegrees,
                    Alpha = 0,
                };

                children.Add(chevron);
                arrowAnimations.Add((chevron, arrow));
                ReverseArrowCount++;
            }

            // ---- ball + follow circle -----------------------------------------------------

            var ball = new Container
            {
                Origin = Anchor.Centre,
                Size = new Vector2(radius * 2),
                Alpha = 0,
                Children = new Drawable[]
                {
                    new CircularContainer // follow circle ring (~2.4× radius) around the ball
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(radius * 2 * 2.4f),
                        Masking = true,
                        BorderThickness = 4,
                        BorderColour = Color4.White,
                        Alpha = 0.55f,
                        Child = new Box { RelativeSizeAxes = Axes.Both, Alpha = 0, AlwaysPresent = true },
                    },
                    new Circle // the ball itself
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(radius * 1.8f),
                        Colour = colour.Lighten(0.2f),
                    },
                },
            };

            BallContainer = ball;

            var keyframes = ChartComputations.BallKeyframes(obj, vertices);

            if (keyframes.Count > 0)
                ball.Position = keyframes[0].Position;

            // ---- head circle (explodes at hit time; body stays until the tail) -------------

            var headPiece = new Container
            {
                Origin = Anchor.Centre,
                Size = new Vector2(radius * 2),
                Children = new Drawable[]
                {
                    makeDisc(radius, colour),
                    makeNumber(radius, number),
                },
            };

            var approach = makeApproachRing(radius, colour);

            children.Add(headPiece);
            children.Add(approach);

            // Ball on top of the head piece (the head is already exploding when the ball sets
            // off, and the travelling ball must never disappear under it).
            children.Add(ball);

            // Attaching loads every child against this drawable's clock; ONLY NOW is it safe to
            // compile their transforms (see the note above the tick loop).
            InternalChildren = children.ToArray();

            foreach (var (dot, spec) in tickAnimations)
            {
                using (dot.BeginAbsoluteSequence(spec.AppearTime))
                    dot.FadeTo(0.9f, fadeIn);

                using (dot.BeginAbsoluteSequence(spec.Time))
                    dot.ScaleTo(1.6f, 120, Easing.OutQuint).FadeOut(120);
            }

            foreach (var (chevron, spec) in arrowAnimations)
            {
                using (chevron.BeginAbsoluteSequence(spec.AppearTime))
                    chevron.FadeTo(1, fadeIn);

                using (chevron.BeginAbsoluteSequence(spec.Time))
                    chevron.ScaleTo(1.3f, hit_fade_duration, Easing.OutQuint).FadeOut(hit_fade_duration);
            }

            if (keyframes.Count > 0)
            {
                using (ball.BeginAbsoluteSequence(obj.Time))
                    ball.FadeIn();

                using (ball.BeginAbsoluteSequence(keyframes[0].Time))
                {
                    // Piecewise travel as one chained MoveTo sequence — compiled once, evaluated
                    // lazily by the framework; ping-pong across repeats comes from the keyframes.
                    var sequence = ball.MoveTo(keyframes[0].Position);
                    double previousTime = keyframes[0].Time;

                    for (int i = 1; i < keyframes.Count; i++)
                    {
                        sequence = sequence.Then().MoveTo(keyframes[i].Position, keyframes[i].Time - previousTime);
                        previousTime = keyframes[i].Time;
                    }
                }

                using (ball.BeginAbsoluteSequence(obj.EndTime))
                    ball.FadeOut(hit_fade_duration);
            }

            using (BeginAbsoluteSequence(obj.Time - preempt))
            {
                this.FadeTo(1, fadeIn);
                approach.ScaleTo(1, preempt);
            }

            using (BeginAbsoluteSequence(obj.Time))
            {
                approach.FadeOut();
                headPiece.ScaleTo(1.4f, hit_fade_duration, Easing.OutQuint).FadeOut(hit_fade_duration);
            }

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
            CircularContainer approach;

            InternalChildren = new Drawable[]
            {
                new CircularContainer // outer ring
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    BorderThickness = 6,
                    BorderColour = Color4.White,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Alpha = 0, AlwaysPresent = true },
                },
                approach = new CircularContainer // approach: shrinks over the spin duration
                {
                    RelativeSizeAxes = Axes.Both,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Masking = true,
                    BorderThickness = 3,
                    BorderColour = Color4.White,
                    Alpha = 0.5f,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Alpha = 0, AlwaysPresent = true },
                },
                rotor = new Container // rotating middle
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
                new Circle // centre dot
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new Vector2(12),
                    Colour = Color4.White,
                },
            };

            double duration = Math.Max(1, obj.EndTime - obj.Time);

            using (BeginAbsoluteSequence(obj.Time - preempt))
                this.FadeTo(1, fadeIn_for(preempt));

            using (BeginAbsoluteSequence(obj.Time))
            {
                // One full revolution per 500ms of spinner duration — purely decorative.
                rotor.RotateTo(0).RotateTo((float)(360 * duration / 500), duration);
                approach.ScaleTo(1).ScaleTo(0.12f, duration);
            }

            using (BeginAbsoluteSequence(obj.EndTime))
                this.FadeOut(hit_fade_duration);
        }

        private static double fadeIn_for(double preempt) => 400 * preempt / 1200;
    }

    internal partial class DrawableFollowPoint : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private const double fade_duration = 150;

        private readonly FollowPointSpec spec;

        public DrawableFollowPoint(FollowPointSpec spec)
        {
            this.spec = spec;

            Origin = Anchor.Centre;
            Position = spec.Position;
            Rotation = spec.RotationDegrees;
            Size = new Vector2(8, 3);
            Alpha = 0;

            LifetimeStart = spec.AppearTime;
            LifetimeEnd = spec.DisappearTime + fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChild = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Color4.White,
            };

            // Kept faint — a hint of the reading path, not a feature in itself.
            using (BeginAbsoluteSequence(spec.AppearTime))
                this.FadeTo(0.3f, fade_duration);

            using (BeginAbsoluteSequence(spec.DisappearTime))
                this.FadeOut(fade_duration);
        }
    }
}
