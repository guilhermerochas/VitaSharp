using Silk.NET.Vulkan;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Textures that read the surface of the scene being rendered. The Vita renders a scene in on-chip tile memory and
/// stores it when the scene ends, so a texture pointing at the current colour surface reads its memory: the content
/// from before the scene, never pixels drawn earlier in the same scene. Killzone Mercenary clears its environment
/// map target and then blends last frame's map (sampled from the same surface) into it; reading the live image gave
/// the cleared white instead. Surfaces seen doing this get a copy taken at the start of each of their scenes, which
/// is what such textures sample.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private sealed class SceneStartCopy
    {
        public RenderTarget Shadow = null!;
        public ImageMemoryPool.Allocation Mem;
        public bool Valid;
        /// <summary>Views in other formats (sRGB, raw integer) for the MSAA scene-start load.</summary>
        public readonly Dictionary<Format, ImageView> Views = new();
    }

    private readonly Dictionary<RenderTarget, SceneStartCopy> _sceneStartCopies = new();

    /// <summary>Pixel rectangle the current draw can write (region clip, else the whole scene).</summary>
    private int _drawMinX, _drawMinY, _drawMaxX = int.MaxValue, _drawMaxY = int.MaxValue;

    private void SetDrawArea(in Core.Gpu.DrawState st)
    {
        if (st.RegionClipMode == 2)
        {
            _drawMinX = st.ClipMinX;
            _drawMinY = st.ClipMinY;
            _drawMaxX = st.ClipMaxX;
            _drawMaxY = st.ClipMaxY;
        }
        else
        {
            _drawMinX = _drawMinY = 0;
            _drawMaxX = _drawMaxY = int.MaxValue;
        }
        // the draw is rendered through a Vulkan viewport of the GXM viewport's rectangle, which clips it to that
        // rectangle: post-processing passes chained through regions of one surface (Croixleur Sigma's bloom blurs one
        // half of a 1024x512 target into the other with a full-surface region clip) do not render where they sample
        if (st.ViewportEnabled && float.IsFinite(st.VpOffX) && float.IsFinite(st.VpOffY) && float.IsFinite(st.VpScaleX) && float.IsFinite(st.VpScaleY))
        {
            float x0 = st.VpOffX - MathF.Abs(st.VpScaleX), x1 = st.VpOffX + MathF.Abs(st.VpScaleX);
            float y0 = st.VpOffY - MathF.Abs(st.VpScaleY), y1 = st.VpOffY + MathF.Abs(st.VpScaleY);
            if (x1 > x0 && y1 > y0 && x1 < int.MaxValue && y1 < int.MaxValue)
            {
                _drawMinX = Math.Max(_drawMinX, (int)MathF.Floor(x0));
                _drawMinY = Math.Max(_drawMinY, (int)MathF.Floor(y0));
                _drawMaxX = Math.Min(_drawMaxX, (int)MathF.Ceiling(x1) - 1);
                _drawMaxY = Math.Min(_drawMaxY, (int)MathF.Ceiling(y1) - 1);
            }
        }
    }

    /// <summary>
    /// Whether a texture region of the current surface overlaps the pixels the draw renders. Other parts of the
    /// surface may already have been stored by tiles that finished (post-processing atlases chain passes through
    /// different regions of one scene), so only the overlapping case reads the scene-start content.
    /// </summary>
    private bool SamplesOwnTiles(uint x0, uint y0, uint w, uint h) =>
        (int)x0 <= _drawMaxX && (int)(x0 + w - 1) >= _drawMinX && (int)y0 <= _drawMaxY && (int)(y0 + h - 1) >= _drawMinY;

    /// <summary>The scene-start copy of the current surface (created the first time a draw samples it).</summary>
    private RenderTarget SceneStartSource(RenderTarget rt)
    {
        if (_sceneStartCopies.TryGetValue(rt, out SceneStartCopy? sceneStartCopy))
        {
            return sceneStartCopy.Valid ? sceneStartCopy.Shadow : rt;
        }

        Log.WarnOnce($"selfsample-{rt.Address:X8}", $"[gpu] render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} is sampled while it is rendered: textures read its content from the start of the scene");
        CreateSceneStartCopy(rt);
        // usable from the next scene of this surface on
        return rt;
    }

    /// <summary>Multisampled scenes always keep a scene-start copy: their first rendering loads the samples from it.</summary>
    private void EnsureSceneStartCopy(RenderTarget rt)
    {
        if (!_sceneStartCopies.ContainsKey(rt))
        {
            CreateSceneStartCopy(rt);
        }
    }

    /// <summary>The scene-start copy viewed as <paramref name="format"/> (same texel size as the surface).</summary>
    private ImageView SceneStartCopyView(RenderTarget rt, Format format)
    {
        SceneStartCopy sceneStartCopy = _sceneStartCopies[rt];
        if (format == sceneStartCopy.Shadow.Format)
        {
            return sceneStartCopy.Shadow.View;
        }

        if (sceneStartCopy.Views.TryGetValue(format, out ImageView v))
        {
            return v;
        }

        return sceneStartCopy.Views[format] = CreateView(sceneStartCopy.Shadow.Image, format, ImageAspectFlags.ColorBit, 1, 1, false, default);
    }

    private void DestroySceneStartCopyLater(RenderTarget rt, bool now = false)
    {
        if (!_sceneStartCopies.Remove(rt, out SceneStartCopy? sceneStartCopy))
        {
            return;
        }

        Release(() =>
        {
            foreach (ImageView v in sceneStartCopy.Views.Values)
            {
                _vk.DestroyImageView(_dev, v, null);
            }

            _vk.DestroyImageView(_dev, sceneStartCopy.Shadow.View, null);
            _vk.DestroyImage(_dev, sceneStartCopy.Shadow.Image, null);
            _imagePool.Free(sceneStartCopy.Mem);
        }, now);
    }

    private void CreateSceneStartCopy(RenderTarget rt)
    {
        (Image img, ImageView view, ImageMemoryPool.Allocation mem) = CreateImage(MemCat.Region, rt.ImgW, rt.ImgH, rt.Format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.ColorBit, mutable: true);
        var shadow = new RenderTarget
        {
            Address = rt.Address,
            Width = rt.Width,
            Height = rt.Height,
            ColorFormat = rt.ColorFormat,
            Stride = rt.Stride,
            Format = rt.Format,
            Image = img,
            View = view,
            Version = 0,
            Scale = rt.Scale,
        };
        _sceneStartCopies[rt] = new SceneStartCopy { Shadow = shadow, Mem = mem };
    }

    /// <summary>Called when a scene begins: refreshes the copy of a surface that its own draws sample.</summary>
    private void CaptureSceneStart(RenderTarget rt)
    {
        if (!_sceneStartCopies.TryGetValue(rt, out SceneStartCopy? sceneStartCopy))
        {
            return;
        }

        RenderTarget renderTarget = sceneStartCopy.Shadow;
        EndRendering();
        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        Barrier(renderTarget.Image, sceneStartCopy.Valid ? ImageLayout.General : ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(rt.ImgW, rt.ImgH, 1),
        };
        DevStampOp();
        _vk.CmdCopyImage(Cmd, rt.Image, ImageLayout.General, renderTarget.Image, ImageLayout.TransferDstOptimal, 1, in copy);
        Barrier(renderTarget.Image, ImageLayout.TransferDstOptimal, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        renderTarget.Version++;
        renderTarget.Dirty = false;
        sceneStartCopy.Valid = true;
    }
}
