#nullable enable

// Adapted from osu!lazer (ppy/osu, MIT licence):
// osu.Game/Storyboards/Drawables/DrawableStoryboardAnimation.cs.
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Animations;
using osuTK;
using ReOsuStoryboardPlayer.Core.Base;

using Anchor = osu.Framework.Graphics.Anchor;

namespace JukeBox.Game.Storyboard;

/// <summary>
/// The animation ("Animation,...") counterpart of <see cref="DrawableStoryboardSprite"/>: frame
/// stepping is delegated to the framework's <see cref="TextureAnimation"/> (fed from Core's
/// FrameBaseImagePath/FrameCount/FrameDelay/LoopType fields) instead of Core's per-frame
/// ImageFilePath mutation; command playback is compiled transforms, same as the sprite.
/// </summary>
internal partial class DrawableStoryboardAnimation : TextureAnimation, IStoryboardDrawable
{
    /// <summary>
    /// Upper bound on frames read from an animation declaration. Real storyboard animations are
    /// at most a few hundred frames; a hostile FrameCount (e.g. 2 billion) must not turn the
    /// frame-loading loop into a hang or grow the texture memo unboundedly.
    /// </summary>
    internal const int MaxFrames = 10_000;

    private readonly StoryboardAnimation anim;
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

    public override bool RemoveWhenNotAlive => false;

    public override bool RemoveCompletedTransforms => false;

    /// <summary>
    /// Same Core flip-OR semantics as <see cref="DrawableStoryboardSprite.DrawScale"/>.
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

    public override bool IsPresent
        => !float.IsNaN(DrawPosition.X) && !float.IsNaN(DrawPosition.Y) && base.IsPresent;

    public DrawableStoryboardAnimation(StoryboardAnimation anim, TransformStoryboardLayer owner)
    {
        this.anim = anim;
        this.owner = owner;

        Name = anim.FrameBaseImagePath;
        Anchor = Anchor.TopLeft;
        Origin = Anchor.Custom;
        Depth = -anim.Z;

        LifetimeStart = anim.FrameStartTime;
        // Same hostile-overflow clamp as DrawableStoryboardSprite.
        LifetimeEnd = Math.Max(anim.FrameStartTime, anim.FrameEndTime);

        Position = new Vector2(anim.Postion.X, anim.Postion.Y);
        Rotation = MathHelper.RadiansToDegrees(anim.Rotate);
        Colour = new Colour4(anim.Color.X, anim.Color.Y, anim.Color.Z, 255);
        Alpha = anim.Color.W / 255f;
        vectorScale = new Vector2(anim.Scale.X, anim.Scale.Y);
        flipH = anim.IsHorizonFlip;
        flipV = anim.IsVerticalFlip;
        Blending = anim.IsAdditive ? BlendingParameters.Additive : BlendingParameters.Inherit;

        Loop = anim.LoopType == LoopType.LoopForever;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        bool sized = false;

        // Core builds frame paths as FrameBaseImagePath + index + FrameFileExtension
        // (StoryboardAnimation.Update); reuse the layer's fallback+memo texture lookup per frame.
        // Missing frames are still added (as null-texture frames, like lazer) so the surviving
        // frames keep their original indices and timing. The layer only constructs this drawable
        // when at least one frame texture resolves, so FrameCount here is never zero.
        int frameCount = Math.Min(anim.FrameCount, MaxFrames);

        for (int i = 0; i < frameCount; i++)
        {
            var tex = owner.GetTexture(anim.FrameBaseImagePath + i + anim.FrameFileExtension);

            // null-forgiving: Animation<Texture> declares a non-nullable frame content parameter
            // but tolerates null at draw time (Sprite.Texture is nullable); lazer relies on the
            // same behaviour for missing frames.
            AddFrame(tex!, anim.FrameDelay);

            if (tex != null && !sized)
            {
                // Frames are near-universally uniform in size; use the first found frame for the
                // custom origin conversion (same Y-flip mapping as DrawableStoryboardSprite).
                Size = new Vector2(tex.Width, tex.Height);
                OriginPosition = new Vector2(
                    (0.5f + (float)anim.OriginOffset.X) * tex.Width,
                    (0.5f - (float)anim.OriginOffset.Y) * tex.Height);
                sized = true;
            }
        }

        StoryboardTransforms.ApplyTransforms(this, anim);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Sync frame 0 to the object's FrameStartTime in storyboard (= clock) time, matching
        // Core's frame index formula (time - FrameStartTime) / FrameDelay. Lazer does the
        // equivalent against its gameplay clock in its DrawableStoryboardAnimation.
        PlaybackPosition = Clock.CurrentTime - anim.FrameStartTime;
    }

    protected override void Update()
    {
        base.Update();

        if (Alpha > 1)
            Alpha %= 1;
    }
}
