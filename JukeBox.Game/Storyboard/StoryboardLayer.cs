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
    internal Sprite? FirstSprite => sprites.Values.FirstOrDefault();

    private readonly CachedBeatmapSet set;

    private StoryboardUpdater updater = null!;
    private TextureStore textures = null!;
    private readonly Dictionary<StoryboardObject, Sprite> sprites = new();

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

        var objects = StoryboardLoader.Load(set.OsbFile, set.PreferredOsuFile);

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
        // Core lowercases paths and normalizes separators to '\' when parsing; TextureStore keys
        // off the real (forward-slash) relative path, so undo that before every lookup.
        string p = imagePath.Replace('\\', '/');
        return textures.Get(p) ?? textures.Get(Path.ChangeExtension(p, "png")) ?? textures.Get(p + "-0");
    }

    protected override void Update()
    {
        base.Update();

        updater.Update((float)Clock.CurrentTime);

        var visible = updater.UpdatingStoryboardObjects;
        var visibleSet = new HashSet<StoryboardObject>(visible);

        // Hide sprites for objects Core no longer considers active or visible; they stay pooled
        // in `sprites` in case the object becomes active again (e.g. on a seek backwards).
        foreach (var (obj, sprite) in sprites)
        {
            if (!obj.IsVisible || !visibleSet.Contains(obj))
                sprite.Alpha = 0;
        }

        float depth = 0;
        int visibleCount = 0;

        foreach (var obj in visible) // already Z-sorted by the updater
        {
            if (!obj.IsVisible)
                continue;

            visibleCount++;

            if (!sprites.TryGetValue(obj, out var sprite))
            {
                sprite = new Sprite { Anchor = Anchor.TopLeft, Origin = Anchor.Custom };
                sprites[obj] = sprite;
                AddInternal(sprite);
            }

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

            ChangeInternalChildDepth(sprite, depth); // maintain Z paint order
            depth -= 1;
        }

        VisibleSpriteCount = visibleCount;
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        textures?.Dispose();
    }
}
