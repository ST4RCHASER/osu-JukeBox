#nullable enable

// Adapted from osu!lazer (ppy/osu, MIT licence):
// osu.Game/Storyboards/Drawables/DrawableStoryboardSprite.cs.
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osuTK;
using ReOsuStoryboardPlayer.Core.Base;

// ReOsuStoryboardPlayer.Core.Base also declares an `Anchor` enum (storyboard origin values,
// unrelated to osu!framework's); alias the framework's for unambiguous use.
using Anchor = osu.Framework.Graphics.Anchor;

namespace JukeBox.Game.Storyboard;

/// <summary>
/// A single storyboard sprite whose entire timeline was compiled into framework transforms at
/// load — it costs nothing per frame beyond the framework's lazy transform evaluation, and
/// nothing at all while outside its lifetime window (parent LifetimeManagementContainer skips it).
/// </summary>
internal partial class DrawableStoryboardSprite : Sprite, IStoryboardDrawable
{
    private readonly StoryboardObject obj;
    private readonly TransformStoryboardLayer owner;

    private bool flipH;

    public bool FlipH
    {
        get => flipH;
        set
        {
            if (flipH == value)
                return;

            flipH = value;
            Invalidate(Invalidation.MiscGeometry);
        }
    }

    private bool flipV;

    public bool FlipV
    {
        get => flipV;
        set
        {
            if (flipV == value)
                return;

            flipV = value;
            Invalidate(Invalidation.MiscGeometry);
        }
    }

    private Vector2 vectorScale = Vector2.One;

    public Vector2 VectorScale
    {
        get => vectorScale;
        set
        {
            if (vectorScale == value)
                return;

            vectorScale = value;
            Invalidate(Invalidation.MiscGeometry);
        }
    }

    // Dead drawables must stay children (not be removed) so a seek backwards can revive them.
    public override bool RemoveWhenNotAlive => false;

    // Mandatory for seek/rewind: transforms must be re-evaluable at any past time.
    public override bool RemoveCompletedTransforms => false;

    /// <summary>
    /// Core's renderer treats a negative scale axis and the explicit P-flip flag as an OR, not a
    /// sign multiply: both set still renders flipped once (not double-flipped back). Kept over
    /// lazer's XOR so rendering matches the previous Core-driven renderer. The flip happens
    /// around OriginPosition (Origin is Custom), same as the old negative-Sprite.Scale approach.
    /// </summary>
    protected override Vector2 DrawScale
    {
        get
        {
            var baseScale = base.DrawScale;
            float x = Math.Abs(vectorScale.X) * (flipH || vectorScale.X < 0 ? -1 : 1);
            float y = Math.Abs(vectorScale.Y) * (flipV || vectorScale.Y < 0 ? -1 : 1);
            return new Vector2(baseScale.X * x, baseScale.Y * y);
        }
    }

    // Guard against NaN positions from degenerate commands (lazer does the same) — a NaN draw
    // position would poison the draw matrix.
    public override bool IsPresent
        => !float.IsNaN(DrawPosition.X) && !float.IsNaN(DrawPosition.Y) && base.IsPresent;

    /// <summary>
    /// Construction-time state comes from the object's post-parse reset values (the layer calls
    /// <c>ResetTransform()</c> before constructing us): Core's parser folds the declaration
    /// line's base position etc. into <c>BaseTransformResetAction</c>, replicating lazer's
    /// "apply initial values then transforms" pattern.
    /// </summary>
    public DrawableStoryboardSprite(StoryboardObject obj, TransformStoryboardLayer owner)
    {
        this.obj = obj;
        this.owner = owner;

        Name = obj.ImageFilePath;
        Anchor = Anchor.TopLeft;
        Origin = Anchor.Custom;

        // Framework depth: higher = further back. Core Z: higher = painted later (front).
        Depth = -obj.Z;

        LifetimeStart = obj.FrameStartTime;
        // Clamp: a hostile loop count can int-overflow Core's FrameEndTime computation
        // (StartTime + CostTime * LoopCount) into the negative; a lifetime that ends before it
        // starts simply never becomes alive (matching the old renderer, whose updater also never
        // admitted such objects).
        LifetimeEnd = Math.Max(obj.FrameStartTime, obj.FrameEndTime);

        Position = new Vector2(obj.Postion.X, obj.Postion.Y);
        Rotation = MathHelper.RadiansToDegrees(obj.Rotate);
        Colour = new Colour4(obj.Color.X, obj.Color.Y, obj.Color.Z, 255);
        Alpha = obj.Color.W / 255f;
        vectorScale = new Vector2(obj.Scale.X, obj.Scale.Y);
        flipH = obj.IsHorizonFlip;
        flipV = obj.IsVerticalFlip;
        Blending = obj.IsAdditive ? BlendingParameters.Additive : BlendingParameters.Inherit;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        var tex = owner.GetTexture(obj.ImageFilePath);

        if (tex != null)
        {
            Texture = tex;
            Size = new Vector2(tex.Width, tex.Height);

            // Core's OriginOffset is a normalized (-0.5..0.5) offset from sprite centre in a Y-up
            // frame (AnchorConvert: TopLeft -> (-0.5, +0.5)) — the opposite vertical sense from
            // framework's Y-down OriginPosition (0,0 == texture top-left). Flip Y only.
            OriginPosition = new Vector2(
                (0.5f + (float)obj.OriginOffset.X) * tex.Width,
                (0.5f - (float)obj.OriginOffset.Y) * tex.Height);
        }

        StoryboardTransforms.ApplyTransforms(this, obj);
    }

    protected override void Update()
    {
        base.Update();

        // stable's byte alpha overflowed above 1.0, a quirk storyboarders exploit for flicker
        // effects; lazer reproduces it (see DrawableStoryboardSprite there), and Core's
        // (byte)(value*255) cast wrapped similarly.
        if (Alpha > 1)
            Alpha %= 1;
    }
}
