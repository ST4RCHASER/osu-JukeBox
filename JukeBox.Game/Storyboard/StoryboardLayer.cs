#nullable enable

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
using ReOsuStoryboardPlayer.Core.Kernel;

// ReOsuStoryboardPlayer.Core.Base also declares an `Anchor` enum (storyboard origin values,
// unrelated to osu!framework's). Both namespaces are `using`d in this file for other types
// (StoryboardObject/Layer/StoryboardBackgroundObject vs. Sprite/Drawable), so the framework's
// Anchor needs an explicit alias to resolve unambiguously.
using Anchor = osu.Framework.Graphics.Anchor;

namespace JukeBox.Game.Storyboard;

/// <summary>
/// Maps a Core-driven storyboard timeline onto pooled osu!framework Sprites. One instance owns
/// one storyboard's worth of state: the Core StoryboardUpdater (evaluated every Update() against
/// this Drawable's Clock) plus a Sprite per StoryboardObject, created lazily and reused for the
/// object's lifetime.
/// </summary>
public partial class StoryboardLayer : CompositeDrawable
{
    /// <summary>
    /// Count of StoryboardObjects Core currently reports as visible (non-zero alpha and within
    /// their active time window). Exposed for tests; not a count of realised Sprites.
    /// </summary>
    public int VisibleSpriteCount { get; private set; }

    /// <summary>
    /// Test-only access to the realised Sprite pool (JukeBox.Game.Tests has InternalsVisibleTo),
    /// to assert per-sprite transform state (e.g. OriginPosition) that
    /// <see cref="VisibleSpriteCount"/> alone can't cover.
    /// </summary>
    internal Sprite? FirstSprite => sprites.Values.FirstOrDefault()?.Sprite;

    private readonly CachedBeatmapSet set;

    private StoryboardUpdater updater = null!;
    private TextureStore textures = null!;

    // StoryboardObject.Equals is a deep, O(command-count²) structural comparison (not reference
    // equality) — every Dictionary/HashSet operation keyed on it pays that cost on every lookup,
    // even a hit against the exact same instance. Update() therefore keeps this to the *one*
    // unavoidable StoryboardObject-keyed dictionary (the sprite pool itself) and folds the
    // per-frame "was this object touched this frame" bookkeeping into the pooled entry instead of
    // a second/third dictionary — see LastActiveFrame below.
    private readonly Dictionary<StoryboardObject, SpriteEntry> sprites = new();

    // getTexture()'s 3-way fallback chain (raw path / .png / "-0" suffix) does string allocations
    // and up to 3 TextureStore lookups; memoizing by the raw (pre-normalized) ImageFilePath turns
    // every repeat call — which is most of them, since the same handful of distinct paths recur
    // every frame — into a single dictionary hit. Misses are cached too (as null) so a
    // permanently-missing texture doesn't re-pay the fallback chain every frame either.
    private readonly Dictionary<string, Texture?> textureCache = new();

    private int frameIndex;
    private const int sprite_sweep_interval_frames = 300;
    private const int sprite_idle_frames_before_removal = 300;

    public StoryboardLayer(CachedBeatmapSet set)
    {
        this.set = set;
    }

    [BackgroundDependencyLoader]
    private void load(GameHost host)
    {
        Size = new Vector2(set.Widescreen ? 854 : 640, 480);

        // Must exist before the background-scale lookup below — Core's StoryboardBackgroundObject
        // needs the real texture height to compute its cover-scale before the updater is built.
        textures = new TextureStore(host.Renderer,
            host.CreateTextureLoaderStore(new StorageBackedResourceStore(new NativeStorage(set.Directory, host))),
            useAtlas: false, scaleAdjust: 1);

        List<StoryboardObject> objects;

        try
        {
            objects = StoryboardLoader.Load(set.OsbFile, set.PreferredOsuFile);
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

        foreach (var bg in objects.OfType<StoryboardBackgroundObject>())
        {
            var tex = getTexture(bg.ImageFilePath);
            if (tex != null)
                bg.AdjustScale(tex.Height);
        }

        updater = new StoryboardUpdater(objects);
    }

    private Texture? getTexture(string imagePath)
    {
        if (textureCache.TryGetValue(imagePath, out var cached))
            return cached;

        // Core lowercases paths and normalizes separators to '\' when parsing; TextureStore keys
        // off the real (forward-slash) relative path, so undo that before every lookup.
        string p = imagePath.Replace('\\', '/');
        var tex = textures.Get(p) ?? textures.Get(Path.ChangeExtension(p, "png")) ?? textures.Get(p + "-0");
        textureCache[imagePath] = tex;
        return tex;
    }

    protected override void Update()
    {
        base.Update();

        updater.Update((float)Clock.CurrentTime);
        frameIndex++;

        var visible = updater.UpdatingStoryboardObjects;

        float depth = 0;
        int visibleCount = 0;

        foreach (var obj in visible) // already Z-sorted by the updater
        {
            if (!obj.IsVisible)
                continue;

            visibleCount++;

            if (!sprites.TryGetValue(obj, out var entry))
            {
                var newSprite = new Sprite { Anchor = Anchor.TopLeft, Origin = Anchor.Custom };
                entry = new SpriteEntry(newSprite);
                sprites[obj] = entry;
                AddInternal(newSprite);
            }

            entry.LastActiveFrame = frameIndex;
            var sprite = entry.Sprite;

            var tex = getTexture(obj.ImageFilePath);
            if (tex == null)
            {
                sprite.Alpha = 0;
                continue;
            }

            if (sprite.Texture != tex)
            {
                sprite.Texture = tex;
                sprite.Size = new Vector2(tex.Width, tex.Height);
            }

            // Core's OriginOffset is a normalized (-0.5..0.5) offset from sprite centre, but in
            // a Y-up frame (AnchorConvert: TopLeft -> (-0.5, +0.5)) — the opposite vertical sense
            // from framework's Y-down OriginPosition (0,0 == texture top-left). Flip Y only.
            sprite.OriginPosition = new Vector2(
                (0.5f + (float)obj.OriginOffset.X) * tex.Width,
                (0.5f - (float)obj.OriginOffset.Y) * tex.Height);

            // Core's Postion is already Y-down 640x480 (or widescreen 854x480 with objects offset
            // +107 in X) osu!-pixel space, matching this Drawable's own coordinate space directly.
            sprite.Position = new Vector2(obj.Postion.X + (set.Widescreen ? 107 : 0), obj.Postion.Y);
            sprite.Rotation = MathHelper.RadiansToDegrees(obj.Rotate);

            // Core's renderer treats a negative Scale axis and the explicit flip flag as an OR,
            // not a sign multiply: both true still renders flipped (not double-flipped back).
            bool flipX = obj.IsHorizonFlip || obj.Scale.X < 0;
            bool flipY = obj.IsVerticalFlip || obj.Scale.Y < 0;
            sprite.Scale = new Vector2(
                Math.Abs(obj.Scale.X) * (flipX ? -1 : 1),
                Math.Abs(obj.Scale.Y) * (flipY ? -1 : 1));

            sprite.Colour = new Colour4(obj.Color.X, obj.Color.Y, obj.Color.Z, 255);
            sprite.Alpha = obj.Color.W / 255f;
            sprite.Blending = obj.IsAdditive ? BlendingParameters.Additive : BlendingParameters.Inherit;

            // ChangeInternalChildDepth is a remove+re-insert into a sorted child list. The
            // framework itself already no-ops when the depth is unchanged, but skipping the call
            // entirely still avoids its own per-call overhead (e.g. EnsureChildMutationAllowed);
            // paint order rarely changes frame-to-frame (only when objects enter/leave the visible
            // set ahead of this one), so most frames skip every single one of these calls.
            if (sprite.Depth != depth)
                ChangeInternalChildDepth(sprite, depth); // maintain Z paint order
            depth -= 1;
        }

        // Hide sprites for objects Core no longer considers active or visible this frame; they
        // stay pooled in `sprites` in case the object becomes active again (e.g. on a seek
        // backwards). This reuses the LastActiveFrame stamp set above instead of a second
        // membership check keyed on StoryboardObject (see the class-level comment on `sprites`).
        foreach (var entry in sprites.Values)
        {
            if (entry.LastActiveFrame != frameIndex)
                entry.Sprite.Alpha = 0;
        }

        VisibleSpriteCount = visibleCount;

        if (frameIndex % sprite_sweep_interval_frames == 0)
            sweepIdleSprites();
    }

    /// <summary>
    /// Removes pooled sprites for objects that haven't been rendered in a while, so a storyboard
    /// with many short-lived particle objects doesn't grow an ever-larger set of InternalChildren
    /// (the framework iterates all of them every frame, whether visible or not). Safe to remove
    /// eagerly: if the object becomes visible again later it's realised fresh, same as an object
    /// seen for the first time.
    /// </summary>
    private void sweepIdleSprites()
    {
        List<StoryboardObject>? idle = null;

        foreach (var (obj, entry) in sprites)
        {
            if (frameIndex - entry.LastActiveFrame < sprite_idle_frames_before_removal)
                continue;

            RemoveInternal(entry.Sprite, true);
            (idle ??= new List<StoryboardObject>()).Add(obj);
        }

        if (idle == null)
            return;

        foreach (var obj in idle)
            sprites.Remove(obj);
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        textures?.Dispose();
    }

    // Reference type (not a struct) so the frame stamp can be mutated in place on a value already
    // fetched from `sprites` — avoids a second dictionary write keyed on StoryboardObject per
    // object per frame.
    private sealed class SpriteEntry(Sprite sprite)
    {
        public readonly Sprite Sprite = sprite;
        public int LastActiveFrame;
    }
}
