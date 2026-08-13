#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.Charts;

/// <summary>
/// Autoplay osu!catch chart: fruits fall from the top at their hit-object X, over the AR preempt
/// window, onto a catcher plate that autoplays along the bottom (compiled MoveTo keyframes from
/// the sorted catch targets). Slider bodies become droplet streams, spinners become banana
/// showers (both capped). Compiled transforms + lifetime management throughout.
/// </summary>
public partial class CatchChartLayer : CompositeDrawable, IChartRenderer
{
    public override bool RemoveCompletedTransforms => false;

    internal const float catch_y = 340;
    internal const float spawn_y = -20;
    private const double hit_fade_duration = 120;

    private static readonly Color4 banana_yellow = new Color4(0xF5, 0xD8, 0x42, 0xFF);

    public int AliveObjectCount => drops?.AliveElements.Count() ?? 0;
    public int TotalObjectCount => drops?.AllElements.Count ?? 0;

    /// <summary>Test-only: the autoplay catcher plate.</summary>
    internal Container Plate { get; private set; } = null!;

    private readonly ChartBeatmap beatmap;

    private ElementContainer? drops;

    public CatchChartLayer(ChartBeatmap beatmap)
    {
        this.beatmap = beatmap;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Size = new Vector2(512, 384);

        AddInternal(drops = new ElementContainer { RelativeSizeAxes = Axes.Both });

        double preempt = beatmap.PreemptMs;
        var combos = ChartComputations.AssignCombos(beatmap.HitObjects, Theme.ComboColours.Length);

        for (int i = 0; i < beatmap.HitObjects.Count; i++)
        {
            var obj = beatmap.HitObjects[i];
            var colour = Theme.ComboColours[combos[i].ColourIndex % Theme.ComboColours.Length];

            switch (obj.Kind)
            {
                case HitObjectKind.Slider:
                    var droplets = ModeChartComputations.SliderDroplets(obj);

                    // Head and tail are full fruits; in-between samples are small droplets.
                    for (int d = 0; d < droplets.Count; d++)
                    {
                        bool edge = d == 0 || d == droplets.Count - 1;
                        drops.AddElement(new DrawableFallingCatch(droplets[d], edge ? 14 : 7, colour, preempt));
                    }

                    break;

                case HitObjectKind.Spinner:
                    foreach (var banana in ModeChartComputations.Bananas(obj))
                        drops.AddElement(new DrawableFallingCatch(banana, 9, banana_yellow, preempt));
                    break;

                default: // circles (fruits); stray holds degrade to a fruit at their start
                    drops.AddElement(new DrawableFallingCatch(new CatchDropSpec(obj.Time, obj.X), 14, colour, preempt));
                    break;
            }
        }

        // The plate — attached BEFORE its keyframe transforms are compiled (clock-less transforms
        // are applied instantly and discarded; see the slider-ball post-mortem).
        AddInternal(Plate = new Container
        {
            Origin = Anchor.Centre,
            Position = new Vector2(256, catch_y + 12),
            Size = new Vector2(70, 12),
            Masking = true,
            CornerRadius = 6,
            Child = new Box { RelativeSizeAxes = Axes.Both, Colour = Theme.Accent },
        });

        var keyframes = ModeChartComputations.PlateKeyframes(ModeChartComputations.CatchTargets(beatmap));

        if (keyframes.Count > 0)
        {
            using (Plate.BeginAbsoluteSequence(keyframes[0].Time - beatmap.PreemptMs))
            {
                // Autoplay: glide linearly so the plate is under each target exactly on time.
                var sequence = Plate.MoveToX(keyframes[0].X, beatmap.PreemptMs);
                double previousTime = keyframes[0].Time;

                for (int i = 1; i < keyframes.Count; i++)
                {
                    sequence = sequence.Then().MoveToX(keyframes[i].X, keyframes[i].Time - previousTime);
                    previousTime = keyframes[i].Time;
                }
            }
        }
    }

    private partial class ElementContainer : LifetimeManagementContainer
    {
        public IEnumerable<Drawable> AliveElements => AliveInternalChildren;
        public IReadOnlyList<Drawable> AllElements => InternalChildren;

        public void AddElement(Drawable drawable) => AddInternal(drawable);
    }

    /// <summary>A fruit, droplet or banana: falls from the top to the plate line at its time.</summary>
    internal partial class DrawableFallingCatch : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private readonly CatchDropSpec spec;
        private readonly double preempt;

        public DrawableFallingCatch(CatchDropSpec spec, float radius, Color4 colour, double preempt)
        {
            this.spec = spec;
            this.preempt = preempt;

            Origin = Anchor.Centre;
            Position = new Vector2(Math.Clamp(spec.X, 0, 512), spawn_y);
            Size = new Vector2(radius * 2);
            Alpha = 0;

            InternalChild = new Circle { RelativeSizeAxes = Axes.Both, Colour = colour };

            LifetimeStart = spec.Time - preempt;
            LifetimeEnd = spec.Time + hit_fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            using (BeginAbsoluteSequence(spec.Time - preempt))
            {
                this.FadeTo(1, 100);
                this.MoveToY(catch_y, preempt);
            }

            using (BeginAbsoluteSequence(spec.Time))
                this.FadeOut(hit_fade_duration); // caught
        }
    }
}
