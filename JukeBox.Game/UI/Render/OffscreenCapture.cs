#nullable enable

using System;
using System.Reflection;
using System.Threading.Tasks;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using osuTK;
using osuTK.Graphics;
using SixLabors.ImageSharp;
using RectangleF = osu.Framework.Graphics.Primitives.RectangleF;
using SixLabors.ImageSharp.PixelFormats;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// A container whose children are drawn into an OFF-SCREEN frame buffer of an exact pixel size and
/// never to the window, and whose last drawn frame can be read back as an image. This is the
/// isolated capture the offline render needs: the requested resolution regardless of the window,
/// and only the render scene's own pixels — no sidebar, no menu bar, no on-screen player.
///
/// <para>
/// How it draws: the draw node binds the buffer, points the viewport/scissor/projection at it so the
/// children land zero-based at 1 unit = 1 pixel, draws them exactly as a <c>BufferedContainer</c>
/// would, then unbinds and draws NOTHING to the back-buffer. The container therefore sizes itself so
/// its screen-space rectangle is exactly <see cref="Width"/>×<see cref="Height"/> pixels (see
/// <see cref="Update"/> — the game runs under a DrawSizePreservingFillContainer, so a unit is not a
/// pixel by default), and it lifts the framework's off-screen culling for its subtree (see
/// <see cref="ComputeMaskingBounds"/>) so a render larger than the window still draws in full.
/// </para>
///
/// <para>
/// Reading back: <see cref="CaptureAsync"/> waits for the frame that follows the caller's clock
/// step to be drawn and then extracts the buffer on the draw thread. The framework keeps the
/// extraction on its renderer behind an internal interface member, reached here by reflection and
/// resolved once — the render fails with a clear message rather than silently if it is ever absent.
/// </para>
/// </summary>
public partial class OffscreenCapture : Container
{
    /// <summary>The buffer's pixel width — the render's requested width.</summary>
    public int Width { get; }

    /// <summary>The buffer's pixel height — the render's requested height.</summary>
    public int Height { get; }

    private readonly Shared shared = new Shared();

    public OffscreenCapture(int width, int height)
    {
        Width = width;
        Height = height;

        Size = new Vector2(width, height);
        AlwaysPresent = true;
    }

    // Nothing here should ever take the user's mouse: the scene is drawn for capture, not to be
    // interacted with, and it sits over the whole app while a render runs.
    public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) => false;

    protected override void Update()
    {
        base.Update();

        // Counter the UI scale so ONE unit of this container is ONE pixel on screen — the frame
        // buffer is sized in pixels and the children are drawn 1:1 into it — SHRUNK to fit inside
        // the window when the buffer is bigger than it. The shrink matters: parts of a drawable
        // that render through their own nested frame buffer (a slider body's path — its buffered
        // draw node clips itself to the ROOT drawable's bounds) simply never draw outside the
        // window's rectangle, so a subtree laid out past the window loses those parts (the broken
        // giant slider-body blobs). Kept within the root, everything draws; the draw node's
        // projection stretches the subtree's rectangle over the full buffer, and the path's
        // distance-field body scales up cleanly.
        if (Parent != null)
        {
            var unit = Parent.ToScreenSpace(Vector2.One) - Parent.ToScreenSpace(Vector2.Zero);

            if (unit.X > 0 && unit.Y > 0)
                Scale = new Vector2(1 / unit.X, 1 / unit.Y) * rootFitScale();
        }
    }

    /// <summary>The factor (≤ 1) shrinking this subtree's on-screen footprint until it fits inside
    /// the root drawable's bounds — 1 whenever the buffer already fits (the draw is then exactly
    /// 1 unit : 1 pixel, as before).</summary>
    private float rootFitScale()
    {
        IDrawable root = this;

        while (root.Parent != null)
            root = root.Parent;

        return FitScale(root.ScreenSpaceDrawQuad.AABBFloat, ScreenSpaceDrawQuad.TopLeft, new Vector2(Width, Height));
    }

    /// <summary>
    /// How much a buffer-sized rectangle at <paramref name="topLeft"/> must shrink to fit inside
    /// <paramref name="rootBounds"/>: 1 when it already fits (never upscaled), the limiting axis
    /// ratio otherwise. A degenerate position outside the bounds yields 1 — there is nothing
    /// sensible to fit to. Pure and internal for the tests.
    /// </summary>
    internal static float FitScale(RectangleF rootBounds, Vector2 topLeft, Vector2 bufferSize)
    {
        float availableX = rootBounds.Right - topLeft.X;
        float availableY = rootBounds.Bottom - topLeft.Y;

        if (availableX <= 0 || availableY <= 0 || bufferSize.X <= 0 || bufferSize.Y <= 0)
            return 1;

        return Math.Min(1, Math.Min(availableX / bufferSize.X, availableY / bufferSize.Y));
    }

    /// <summary>
    /// Everything under this container is drawn into the buffer, never clipped to the window — a
    /// 4K render on a 1440p window must still draw its far edge. The framework culls a drawable
    /// whose screen rectangle falls outside its parent chain's masking bounds; making this
    /// subtree's bounds effectively infinite is what stops that.
    /// </summary>
    public override RectangleF ComputeMaskingBounds() => new RectangleF(float.MinValue / 2, float.MinValue / 2, float.MaxValue, float.MaxValue);

    /// <summary>
    /// Reads back the frame drawn from the caller's most recent update. The buffer holds whatever
    /// the draw thread last rendered into it, so this waits one draw frame (for the node generated
    /// after the caller's step to be drawn) and extracts on the next.
    /// </summary>
    public Task<Image<Rgba32>> CaptureAsync(GameHost host)
    {
        var completion = new TaskCompletionSource<Image<Rgba32>>();

        host.DrawThread.Scheduler.Add(() => host.DrawThread.Scheduler.Add(() =>
        {
            try
            {
                if (shared.FrameBuffer == null || shared.Renderer == null)
                    throw new InvalidOperationException("The render scene has not been drawn yet.");

                completion.SetResult(Shared.Extract(shared.Renderer, shared.FrameBuffer));
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        }));

        return completion.Task;
    }

    protected override DrawNode CreateDrawNode() => new CaptureDrawNode(this);

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        shared.Dispose();
    }

    /// <summary>The buffer and the renderer that owns it, shared between the drawable (which reads
    /// back) and its draw nodes (which draw). Created on first draw, on the draw thread.</summary>
    private sealed class Shared : IDisposable
    {
        public IFrameBuffer? FrameBuffer;
        public IRenderer? Renderer;

        private static readonly MethodInfo? extract_method = typeof(IRenderer).GetMethod("ExtractFrameBufferData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public static Image<Rgba32> Extract(IRenderer renderer, IFrameBuffer frameBuffer)
        {
            if (extract_method == null)
                throw new NotSupportedException("This osu!framework build exposes no frame buffer readback; the offline render cannot capture frames.");

            return (Image<Rgba32>)extract_method.Invoke(renderer, new object[] { frameBuffer })!;
        }

        public void Dispose()
        {
            if (FrameBuffer != null && Renderer != null)
                Renderer.ScheduleDisposal(b => b.Dispose(), FrameBuffer);

            FrameBuffer = null;
        }
    }

    private sealed class CaptureDrawNode : CompositeDrawableDrawNode
    {
        private readonly Shared shared;
        private readonly Vector2 bufferSize;

        private RectangleF drawRect;

        public CaptureDrawNode(OffscreenCapture source)
            : base(source)
        {
            shared = source.shared;
            bufferSize = new Vector2(source.Width, source.Height);
        }

        public override void ApplyState()
        {
            base.ApplyState();
            drawRect = Source.ScreenSpaceDrawQuad.AABBFloat;
        }

        protected override void Draw(IRenderer renderer)
        {
            shared.Renderer = renderer;
            var buffer = shared.FrameBuffer ??= renderer.CreateFrameBuffer();

            // Sizing allocates the texture on first use.
            buffer.Size = bufferSize;

            int width = (int)bufferSize.X;
            int height = (int)bufferSize.Y;

            // The children are drawn as if zero-based at the buffer's top-left: masking off (it is
            // re-applied by the children that mask), the viewport and scissor matched to the buffer,
            // and an orthographic projection mapping the container's screen rectangle onto it — the
            // rectangle is the buffer's size 1:1 when it fits the window, or a shrunk-to-fit version
            // of it otherwise (see Update), and the projection stretches either over the buffer.
            var maskingRect = new RectangleI((int)Math.Floor(drawRect.X), (int)Math.Floor(drawRect.Y), (int)Math.Ceiling(drawRect.Width) + 1, (int)Math.Ceiling(drawRect.Height) + 1);

            renderer.PushMaskingInfo(new MaskingInfo
            {
                ScreenSpaceAABB = maskingRect,
                MaskingRect = drawRect,
                ToMaskingSpace = Matrix3.Identity,
                BlendRange = 1,
                AlphaExponent = 1,
            }, true);

            renderer.PushViewport(new RectangleI(0, 0, width, height));
            renderer.PushScissor(new RectangleI(0, 0, width, height));
            renderer.PushScissorOffset(maskingRect.Location);

            buffer.Bind();
            renderer.PushOrtho(drawRect);
            renderer.Clear(new ClearInfo(Color4.Black));

            // The children, into the bound buffer. Nothing is drawn to the back-buffer afterwards —
            // the scene is for capture only.
            base.Draw(renderer);

            renderer.PopProjectionMatrix();
            buffer.Unbind();

            renderer.PopScissorOffset();
            renderer.PopScissor();
            renderer.PopViewport();
            renderer.PopMaskingInfo();
        }
    }
}
