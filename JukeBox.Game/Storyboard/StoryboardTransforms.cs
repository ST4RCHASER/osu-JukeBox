#nullable enable

// Command→transform compilation adapted from osu!lazer (ppy/osu, MIT licence):
// osu.Game/Storyboards/StoryboardSprite.cs (ApplyTransforms) and
// osu.Game/Storyboards/Commands/* (per-command transform generation, StoryboardLoopingGroup).
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Transforms;
using osuTK;
using ReOsuStoryboardPlayer.Core.Base;
using ReOsuStoryboardPlayer.Core.Commands;
using ReOsuStoryboardPlayer.Core.Commands.Group;

namespace JukeBox.Game.Storyboard;

/// <summary>
/// The two storyboard-specific transformable properties that plain <see cref="Drawable"/>s lack:
/// osu! "P" flip states and "V" vector scale. Mirrors lazer's IFlippable/IVectorScalable.
/// </summary>
public interface IStoryboardDrawable : IDrawable
{
    bool FlipH { get; set; }
    bool FlipV { get; set; }
    Vector2 VectorScale { get; set; }
}

/// <summary>
/// Compiles a Core <see cref="StoryboardObject"/>'s parsed command timelines ONCE into
/// osu!framework transforms on a drawable, so playback needs no per-frame command evaluation at
/// all — the framework lazily evaluates the pre-compiled transforms against the drawable's clock
/// (which also makes seek/rewind free, given RemoveCompletedTransforms == false).
/// </summary>
public static class StoryboardTransforms
{
    /// <summary>
    /// A command occurrence to compile: a plain timeline command plays once at its own StartTime;
    /// a loop sub-command plays at loopStart + relative StartTime and repeats via the framework's
    /// transform-loop support (lazer's StoryboardLoopingGroup pattern — O(1) storage per
    /// sub-command no matter how large the iteration count, so a hostile "L,0,2000000000" costs
    /// nothing beyond a single set of transforms).
    /// </summary>
    private readonly record struct Entry(Command Command, double StartTime, double LoopPause, int LoopIterations);

    /// <summary>
    /// Lazer's "apply initial values then transforms" pattern (StoryboardSprite.ApplyTransforms):
    /// commands are applied in chronological order (keeps the framework's per-property transform
    /// list append-only, avoiding O(n²) inserts), and the first command of each property also
    /// snaps the property to its start value, because osu!'s command semantics clamp: before a
    /// property's first command, the property shows that command's start value (Core replicates
    /// this via CommandTimeline.PickCommand returning the first command for t &lt; its window).
    /// </summary>
    public static void ApplyTransforms<T>(T drawable, StoryboardObject obj)
        where T : Drawable, IStoryboardDrawable
    {
        var entries = new List<Entry>();

        foreach (var pair in obj.CommandMap)
        {
            switch (pair.Key)
            {
                // Trigger timelines are unsupported (skipped, same as the previous renderer) —
                // counted/logged by the layer.
                case Event.Trigger:
                    continue;

                // Loops: Core (with unrolling off) keeps the LoopCommand in CommandMap[Loop] with
                // its sub-timelines normalized at parse to iteration-relative times (min start 0;
                // CostTime = one iteration's length; the loop's own StartTime absorbed the
                // offset). Each sub-command becomes ONE set of transforms starting at its
                // first-iteration absolute time, repeated with a framework transform-loop whose
                // period is CostTime (pause = CostTime - command duration), lazer-style.
                case Event.Loop:
                    foreach (var loop in pair.Value.OfType<LoopCommand>())
                    {
                        int iterations = Math.Max(1, loop.LoopCount);

                        foreach (var subTimeline in loop.SubCommands.Values)
                        {
                            foreach (var sub in subTimeline)
                                entries.Add(new Entry(sub, (double)loop.StartTime + sub.StartTime,
                                    Math.Max(0, loop.CostTime - Duration(sub)), iterations));
                        }
                    }

                    continue;

                default:
                    // Note: with unrolling off, Core also inserts internal LoopSubTimelineCommand
                    // wrappers into the per-event timelines; they extend Command directly (not the
                    // concrete Value/State command types), so the compile switch below ignores
                    // them — the loop's real sub-commands are compiled from CommandMap[Loop].
                    foreach (var cmd in pair.Value)
                        entries.Add(new Entry(cmd, cmd.StartTime, 0, 1));
                    continue;
            }
        }

        var appliedProperties = new HashSet<string>();

        foreach (var entry in entries.OrderBy(e => e.StartTime)) // stable: ties keep event order
        {
            var command = entry.Command;

            switch (command)
            {
                // M — Core keeps it a single Vector command; decompose to X/Y here (like lazer's
                // decoder does at parse) so M and MX/MY commands share the same transform target
                // members ("X"/"Y") instead of fighting a separate "Position" transform.
                case MoveCommand m:
                    if (appliedProperties.Add(nameof(drawable.X)))
                        drawable.X = m.StartValue.X;
                    if (appliedProperties.Add(nameof(drawable.Y)))
                        drawable.Y = m.StartValue.Y;

                    using (drawable.BeginAbsoluteSequence(entry.StartTime))
                    {
                        ApplyLoop(entry, drawable.MoveToX(m.StartValue.X).Then().MoveToX(m.EndValue.X, Duration(m), Ease(m)));
                        ApplyLoop(entry, drawable.MoveToY(m.StartValue.Y).Then().MoveToY(m.EndValue.Y, Duration(m), Ease(m)));
                    }

                    break;

                case MoveXCommand mx:
                    if (appliedProperties.Add(nameof(drawable.X)))
                        drawable.X = mx.StartValue;

                    using (drawable.BeginAbsoluteSequence(entry.StartTime))
                        ApplyLoop(entry, drawable.MoveToX(mx.StartValue).Then().MoveToX(mx.EndValue, Duration(mx), Ease(mx)));
                    break;

                case MoveYCommand my:
                    if (appliedProperties.Add(nameof(drawable.Y)))
                        drawable.Y = my.StartValue;

                    using (drawable.BeginAbsoluteSequence(entry.StartTime))
                        ApplyLoop(entry, drawable.MoveToY(my.StartValue).Then().MoveToY(my.EndValue, Duration(my), Ease(my)));
                    break;

                case FadeCommand f:
                    if (appliedProperties.Add(nameof(drawable.Alpha)))
                        drawable.Alpha = f.StartValue;

                    using (drawable.BeginAbsoluteSequence(entry.StartTime))
                        ApplyLoop(entry, drawable.FadeTo(f.StartValue).Then().FadeTo(f.EndValue, Duration(f), Ease(f)));
                    break;

                // S and V both target the single VectorScale property, mirroring Core where both
                // write the one StoryboardObject.Scale field. (Lazer instead multiplies a uniform
                // Scale with VectorScale; Core's semantics — last writer wins — are kept here so
                // rendering matches the previous Core-driven renderer.)
                case ScaleCommand s:
                    if (appliedProperties.Add(nameof(IStoryboardDrawable.VectorScale)))
                        drawable.VectorScale = new Vector2(s.StartValue);

                    using (drawable.BeginAbsoluteSequence(entry.StartTime))
                    {
                        ApplyLoop(entry, drawable.TransformTo(nameof(IStoryboardDrawable.VectorScale), new Vector2(s.StartValue)).Then()
                                                 .TransformTo(nameof(IStoryboardDrawable.VectorScale), new Vector2(s.EndValue), Duration(s), Ease(s)));
                    }

                    break;

                case VectorScaleCommand v:
                    if (appliedProperties.Add(nameof(IStoryboardDrawable.VectorScale)))
                        drawable.VectorScale = new Vector2(v.StartValue.X, v.StartValue.Y);

                    using (drawable.BeginAbsoluteSequence(entry.StartTime))
                    {
                        ApplyLoop(entry, drawable.TransformTo(nameof(IStoryboardDrawable.VectorScale), new Vector2(v.StartValue.X, v.StartValue.Y)).Then()
                                                 .TransformTo(nameof(IStoryboardDrawable.VectorScale), new Vector2(v.EndValue.X, v.EndValue.Y), Duration(v), Ease(v)));
                    }

                    break;

                case RotateCommand r:
                    // Core stores radians; framework Rotation is degrees.
                    if (appliedProperties.Add(nameof(drawable.Rotation)))
                        drawable.Rotation = MathHelper.RadiansToDegrees(r.StartValue);

                    using (drawable.BeginAbsoluteSequence(entry.StartTime))
                    {
                        ApplyLoop(entry, drawable.RotateTo(MathHelper.RadiansToDegrees(r.StartValue)).Then()
                                                 .RotateTo(MathHelper.RadiansToDegrees(r.EndValue), Duration(r), Ease(r)));
                    }

                    break;

                case ColorCommand c:
                    // C only animates RGB; alpha lives exclusively in the F/Alpha channel
                    // (Core's ColorCommand likewise never touches Color.W).
                    if (appliedProperties.Add(nameof(drawable.Colour)))
                        drawable.Colour = ToColour4(c.StartValue);

                    using (drawable.BeginAbsoluteSequence(entry.StartTime))
                    {
                        ApplyLoop(entry, drawable.TransformTo(nameof(drawable.Colour), (ColourInfo)ToColour4(c.StartValue)).Then()
                                                 .TransformTo(nameof(drawable.Colour), (ColourInfo)ToColour4(c.EndValue), Duration(c), Ease(c)));
                    }

                    break;

                // P commands (state flips / additive): lazer's pattern — the state turns on at
                // StartTime and reverts at EndTime, except a zero-duration command which is
                // permanent from StartTime on. Initial value is only applied for zero-duration
                // commands (a windowed state must not leak before its window).
                case HorizonFlipCommand:
                    if (appliedProperties.Add(nameof(IStoryboardDrawable.FlipH)) && isPermanent(command))
                        drawable.FlipH = true;

                    applyStateTransforms(drawable, entry, nameof(IStoryboardDrawable.FlipH), true, isPermanent(command));
                    break;

                case VerticalFlipCommand:
                    if (appliedProperties.Add(nameof(IStoryboardDrawable.FlipV)) && isPermanent(command))
                        drawable.FlipV = true;

                    applyStateTransforms(drawable, entry, nameof(IStoryboardDrawable.FlipV), true, isPermanent(command));
                    break;

                case AdditiveBlendCommand:
                    if (appliedProperties.Add(nameof(drawable.Blending)) && isPermanent(command))
                        drawable.Blending = BlendingParameters.Additive;

                    applyStateTransforms(drawable, entry, nameof(drawable.Blending),
                        BlendingParameters.Additive,
                        isPermanent(command) ? BlendingParameters.Additive : BlendingParameters.Inherit);
                    break;
            }
        }

        static bool isPermanent(Command command) => command.StartTime == command.EndTime;

        static void applyStateTransforms<TValue>(T drawable, Entry entry, string property, TValue startValue, TValue endValue)
        {
            using (drawable.BeginAbsoluteSequence(entry.StartTime))
            {
                ApplyLoop(entry, drawable.TransformTo(property, startValue)
                                         .Delay(Duration(entry.Command))
                                         .TransformTo(property, endValue));
            }
        }
    }

    /// <summary>
    /// Wraps a compiled command sequence in a framework transform-loop when it came from an "L"
    /// loop (lazer's StoryboardLoopingCommand.ApplyTransforms): the pause tops the sequence up to
    /// one full iteration period, and the framework replays it <paramref name="entry"/>.LoopIterations
    /// times by evaluation (Transform.LoopCount) — no per-iteration allocation.
    /// </summary>
    private static void ApplyLoop<T>(Entry entry, TransformSequence<T> sequence)
        where T : Drawable, IStoryboardDrawable
    {
        if (entry.LoopIterations > 1)
            sequence.Loop(entry.LoopPause, entry.LoopIterations);
    }

    private static double Duration(Command c) => Math.Max(0, (double)c.EndTime - c.StartTime);

    /// <summary>
    /// Core's EasingTypes is a verbatim copy of osu!framework's Easing (same source, same
    /// ordering — Core's Easing.cs even carries the framework licence header), so an int cast is
    /// exact; unknown values (malformed files) fall back to linear.
    /// </summary>
    private static Easing Ease(ValueCommand c)
        => Enum.IsDefined(typeof(Easing), (int)c.Easing) ? (Easing)(int)c.Easing : Easing.None;

    private static Colour4 ToColour4(ReOsuStoryboardPlayer.Core.PrimitiveValue.ByteVec4 c)
        => new Colour4(c.X, c.Y, c.Z, 255);
}
