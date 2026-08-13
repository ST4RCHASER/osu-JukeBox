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
/// Autoplay osu!mania chart: vertical lanes (key count = CircleSize) centred in the 512×384
/// playfield, notes scrolling down over a fixed visible window to a judgement line near the
/// bottom, hold notes as elongated bodies that shrink while "held". Same compiled-transforms
/// architecture as <see cref="ChartLayer"/>: zero per-frame evaluation, lifetime-managed,
/// bidirectional seeking.
/// </summary>
public partial class ManiaChartLayer : CompositeDrawable, IChartRenderer
{
    public override bool RemoveCompletedTransforms => false;

    /// <summary>Time a note is visible before its hit moment (spawn → judgement line).</summary>
    public const double scroll_window_ms = 700;

    internal const float judgement_line_y = 340;
    internal const float spawn_y = -30;

    private const double hit_fade_duration = 150;

    /// <summary>Hit-object drawables currently inside their lifetime window. Exposed for tests.</summary>
    public int AliveObjectCount => notes?.AliveElements.Count() ?? 0;

    /// <summary>Total note drawables constructed. Exposed for tests.</summary>
    public int TotalObjectCount => notes?.AllElements.Count ?? 0;

    /// <summary>Key (lane) count in use. Exposed for tests.</summary>
    internal int KeyCount { get; private set; }

    private readonly ChartBeatmap beatmap;

    private ElementContainer? notes;

    public ManiaChartLayer(ChartBeatmap beatmap)
    {
        this.beatmap = beatmap;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Size = new Vector2(512, 384);

        KeyCount = ModeChartComputations.ManiaKeyCount(beatmap.CircleSize);

        float laneWidth = Math.Min(48, 512f / KeyCount);
        float totalWidth = laneWidth * KeyCount;
        float left = (512 - totalWidth) / 2;

        // Static stage: column backdrops + judgement line (not lifetime-managed).
        for (int lane = 0; lane < KeyCount; lane++)
        {
            AddInternal(new Box
            {
                Position = new Vector2(left + lane * laneWidth, 0),
                Size = new Vector2(laneWidth - 1, 384),
                Colour = Color4.Black,
                Alpha = lane % 2 == 0 ? 0.55f : 0.4f,
            });
        }

        AddInternal(new Box
        {
            Position = new Vector2(left, judgement_line_y),
            Size = new Vector2(totalWidth, 3),
            Colour = Theme.Accent,
        });

        AddInternal(notes = new ElementContainer { RelativeSizeAxes = Axes.Both });

        foreach (var obj in beatmap.HitObjects)
        {
            // Mania maps contain circles (notes) and holds; anything else (shouldn't appear, but
            // converted maps can be odd) renders as a plain note at its start time.
            if (obj.Kind == HitObjectKind.Spinner)
                continue;

            int lane = ModeChartComputations.ManiaLane(obj.X, KeyCount);
            float laneCentre = left + lane * laneWidth + laneWidth / 2;
            var colour = laneColour(lane);

            notes.AddElement(obj.Kind == HitObjectKind.Hold
                ? new DrawableManiaHold(obj, laneCentre, laneWidth, colour)
                : new DrawableManiaNote(obj, laneCentre, laneWidth, colour));
        }
    }

    private Color4 laneColour(int lane)
    {
        if (ModeChartComputations.IsSpecialLane(lane, KeyCount))
            return Theme.Accent;

        return lane % 2 == 0 ? Color4.White : Theme.ComboColours[1]; // white / sky, osu!mania-like
    }

    private partial class ElementContainer : LifetimeManagementContainer
    {
        public IEnumerable<Drawable> AliveElements => AliveInternalChildren;
        public IReadOnlyList<Drawable> AllElements => InternalChildren;

        public void AddElement(Drawable drawable) => AddInternal(drawable);
    }

    internal partial class DrawableManiaNote : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private readonly ChartHitObject obj;

        public DrawableManiaNote(ChartHitObject obj, float laneCentre, float laneWidth, Color4 colour)
        {
            this.obj = obj;

            Origin = Anchor.BottomCentre;
            Position = new Vector2(laneCentre, spawn_y);
            Size = new Vector2(laneWidth - 4, 14);
            Alpha = 0;
            Masking = true;
            CornerRadius = 4;

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
                this.MoveToY(judgement_line_y, scroll_window_ms);
            }

            using (BeginAbsoluteSequence(obj.Time))
                this.ScaleTo(1.25f, hit_fade_duration, Easing.OutQuint).FadeOut(hit_fade_duration);
        }
    }

    internal partial class DrawableManiaHold : CompositeDrawable
    {
        public override bool RemoveWhenNotAlive => false;
        public override bool RemoveCompletedTransforms => false;

        private readonly ChartHitObject obj;
        private readonly float bodyHeight;

        public DrawableManiaHold(ChartHitObject obj, float laneCentre, float laneWidth, Color4 colour)
        {
            this.obj = obj;

            double duration = Math.Max(1, obj.EndTime - obj.Time);

            // Body length so its ends hit the line exactly one scroll-speed apart in time
            // (capped so absurd hold durations don't build kilometre-long boxes).
            float pxPerMs = (judgement_line_y - spawn_y) / (float)scroll_window_ms;
            bodyHeight = Math.Min(2000, (float)duration * pxPerMs) + 14;

            Origin = Anchor.BottomCentre;
            Position = new Vector2(laneCentre, spawn_y);
            Size = new Vector2(laneWidth - 4, bodyHeight);
            Alpha = 0;
            Masking = true;
            CornerRadius = 4;

            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = 0.55f },
                new Box // brighter head cap at the bottom (leading) edge
                {
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.BottomCentre,
                    RelativeSizeAxes = Axes.X,
                    Height = 14,
                    Colour = colour,
                },
            };

            LifetimeStart = obj.Time - scroll_window_ms;
            LifetimeEnd = obj.EndTime + hit_fade_duration + 50;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            using (BeginAbsoluteSequence(obj.Time - scroll_window_ms))
            {
                this.FadeTo(1, 80);
                this.MoveToY(judgement_line_y, scroll_window_ms); // bottom edge reaches the line at Time
            }

            // Held (autoplay = full hold): bottom edge pinned to the line, body shrinking away
            // until the tail arrives.
            using (BeginAbsoluteSequence(obj.Time))
                this.ResizeHeightTo(14, Math.Max(1, obj.EndTime - obj.Time));

            using (BeginAbsoluteSequence(obj.EndTime))
                this.ScaleTo(1.25f, hit_fade_duration, Easing.OutQuint).FadeOut(hit_fade_duration);
        }
    }
}
