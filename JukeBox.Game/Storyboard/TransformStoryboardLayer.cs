#nullable enable

// Architecture adapted from osu!lazer (ppy/osu, MIT licence):
// osu.Game/Storyboards/Drawables/DrawableStoryboard{,Layer}.cs.
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osuTK;
using ReOsuStoryboardPlayer.Core.Base;

namespace JukeBox.Game.Storyboard;

/// <summary>
/// Renders a storyboard the way osu!lazer does: every object's command timelines are compiled
/// ONCE at load into osu!framework transforms on a per-object drawable, so playback needs zero
/// per-frame command evaluation — the framework lazily evaluates pre-compiled transforms per
/// drawable, and the internal <see cref="LifetimeManagementContainer"/> skips drawables outside
/// their [FrameStartTime, FrameEndTime) window entirely (no per-frame scan of dead objects).
/// Replaces the previous StoryboardLayer, which re-evaluated every active Core object's commands
/// on the update thread each frame (~214ms/frame on extreme particle maps).
/// </summary>
public partial class TransformStoryboardLayer : CompositeDrawable
{
    /// <summary>
    /// Count of storyboard drawables currently inside their lifetime window AND visibly present
    /// (non-zero alpha etc.). Exposed for tests.
    /// </summary>
    public int VisibleSpriteCount => elements?.AliveElements.Count(d => d.IsPresent) ?? 0;

    /// <summary>
    /// Test-only access to the first storyboard sprite (JukeBox.Game.Tests has
    /// InternalsVisibleTo), to assert per-sprite state (e.g. OriginPosition) that
    /// <see cref="VisibleSpriteCount"/> alone can't cover.
    /// </summary>
    internal Sprite? FirstSprite => elements?.AllElements.OfType<Sprite>().FirstOrDefault();

    // Mandatory for seek/rewind; propagated to all storyboard drawables by the framework on
    // AddInternal (each drawable also overrides it itself, belt-and-braces).
    public override bool RemoveCompletedTransforms => false;

    private readonly CachedBeatmapSet set;
    private readonly string? osuFile;

    private TextureStore textures = null!;
    private ElementContainer? elements;

    // getTexture()'s 3-way fallback chain (raw path / .png / "-0" suffix) does string allocations
    // and up to 3 TextureStore lookups; memoizing by the raw (pre-normalized) path collapses
    // repeat lookups (animations share frame paths, particles share the same handful of images).
    // Misses are cached too (as null) so a missing texture doesn't re-pay the chain.
    private readonly Dictionary<string, Texture?> textureCache = new();

    /// <param name="set">The beatmap set to render the storyboard of.</param>
    /// <param name="osuFile">The specific difficulty's .osu file whose storyboard events should
    /// merge with the .osb (defaults to <see cref="CachedBeatmapSet.PreferredOsuFile"/>).</param>
    public TransformStoryboardLayer(CachedBeatmapSet set, string? osuFile = null)
    {
        this.set = set;
        this.osuFile = osuFile ?? set.PreferredOsuFile;
    }

    [BackgroundDependencyLoader]
    private void load(GameHost host)
    {
        Size = new Vector2(set.Widescreen ? 854 : 640, 480);

        textures = new TextureStore(host.Renderer,
            host.CreateTextureLoaderStore(new StorageBackedResourceStore(new NativeStorage(set.Directory, host))),
            useAtlas: false, scaleAdjust: 1);

        List<StoryboardObject> objects;

        try
        {
            objects = StoryboardLoader.Load(set.OsbFile, osuFile);
        }
        catch (Exception ex)
        {
            // Core's parser is strict (e.g. int.Parse/Enum.Parse throw outright on a malformed
            // line) and storyboards are downloaded from arbitrary third-party mirrors — a single
            // corrupt .osb/.osu must not take the whole game down. Fall back to an empty
            // storyboard (renders nothing; audio keeps playing) rather than rethrow.
            Logger.Error(ex, $"Failed to load storyboard for set {set.SetId}; falling back to no storyboard");
            objects = new List<StoryboardObject>();
        }

        // The .osu Background event's object only knows how to cover the 480-tall field once it
        // knows its texture height; Core injects the cover-scale command via AdjustScale and
        // keeps un-adjusted background objects permanently hidden (so: no texture, no drawable).
        foreach (var bg in objects.OfType<StoryboardBackgroundObject>())
        {
            var tex = GetTexture(bg.ImageFilePath);
            if (tex != null)
                bg.AdjustScale(tex.Height);
        }

        // From Core's StoryboardUpdater ctor: if a normal storyboard object reuses the background
        // image, the standalone background object is dropped entirely; otherwise it renders
        // behind everything (Z = -1 → framework Depth = +1).
        var background = objects.FirstOrDefault(o => o is StoryboardBackgroundObject);

        if (background != null)
        {
            if (objects.Any(o => o.ImageFilePath == background.ImageFilePath && o is not StoryboardBackgroundObject))
                objects.RemoveAll(o => o is StoryboardBackgroundObject);
            else
                background.Z = -1;
        }

        // Trigger ("T") commands are unsupported, same as the previous renderer — an object's
        // plain command timelines still play.
        int triggerObjects = objects.Count(o => o.ContainTrigger);
        if (triggerObjects > 0)
            Logger.Log($"Storyboard set {set.SetId}: skipping trigger commands on {triggerObjects} object(s) (unsupported)");

        AddInternal(elements = new ElementContainer
        {
            Size = new Vector2(640, 480),
            // Widescreen storyboard space is 854x480 with the playfield-space origin shifted
            // +107px right; offsetting the whole element container preserves raw storyboard
            // coordinates inside the compiled transforms.
            X = set.Widescreen ? 107 : 0,
        });

        foreach (var obj in objects)
        {
            // No commands at all (FrameStartTime never computed) → nothing can ever show;
            // Core's updater likewise never surfaced such objects sensibly. Trigger-only
            // objects stay hidden too (Core hides them until triggered, and triggers are
            // unsupported).
            if (obj.FrameStartTime == int.MinValue || obj.CommandMap.All(p => p.Key is Event.Loop or Event.Trigger))
                continue;

            // Apply the parse-time reset chain (base position from the declaration line, colour/
            // scale/rotation defaults) so the drawable ctor can read the object's initial state
            // from its plain fields — lazer's "initial values, then transforms" pattern.
            obj.ResetTransform();

            switch (obj)
            {
                case StoryboardAnimation anim:
                    // Same missing-texture rule as sprites: if not a single frame resolves, the
                    // animation can never show anything — and a zero-frame TextureAnimation is an
                    // unguarded framework exception, so gate before construction. Lookups are
                    // memoized and the drawable re-uses them, so the probe costs nothing extra.
                    if (hasAnyFrame(anim))
                        elements.AddElement(new DrawableStoryboardAnimation(anim, this));
                    break;

                default:
                    // Objects whose texture can't be resolved never become visible in Core's
                    // renderer either (it blanked their sprite) — skip the drawable entirely.
                    if (GetTexture(obj.ImageFilePath) != null)
                        elements.AddElement(new DrawableStoryboardSprite(obj, this));
                    break;
            }
        }
    }

    /// <summary>
    /// Whether at least one of the animation's frame textures resolves (bounded by
    /// <see cref="DrawableStoryboardAnimation.MaxFrames"/> so a hostile FrameCount can't turn
    /// this probe into a hang).
    /// </summary>
    private bool hasAnyFrame(StoryboardAnimation anim)
    {
        int frameCount = Math.Min(anim.FrameCount, DrawableStoryboardAnimation.MaxFrames);

        for (int i = 0; i < frameCount; i++)
        {
            if (GetTexture(anim.FrameBaseImagePath + i + anim.FrameFileExtension) != null)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Texture lookup with Core-compatible path fallbacks, memoized. Core lowercases paths and
    /// normalizes separators to '\' when parsing; TextureStore keys off the real (forward-slash)
    /// relative path, so undo that before every lookup.
    /// </summary>
    internal Texture? GetTexture(string imagePath)
    {
        if (textureCache.TryGetValue(imagePath, out var cached))
            return cached;

        string p = imagePath.Replace('\\', '/');
        var tex = textures.Get(p) ?? textures.Get(Path.ChangeExtension(p, "png")) ?? textures.Get(p + "-0");
        textureCache[imagePath] = tex;
        return tex;
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        textures?.Dispose();
    }

    /// <summary>
    /// LifetimeManagementContainer tracks children's lifetime windows in an interval structure and
    /// only updates/draws currently-alive ones — dead or not-yet-alive drawables cost nothing per
    /// frame (the key to lazer-level performance on particle storms with short-lived objects).
    /// </summary>
    private partial class ElementContainer : LifetimeManagementContainer
    {
        public IEnumerable<Drawable> AliveElements => AliveInternalChildren;
        public IReadOnlyList<Drawable> AllElements => InternalChildren;

        public void AddElement(Drawable drawable) => AddInternal(drawable);
    }
}
