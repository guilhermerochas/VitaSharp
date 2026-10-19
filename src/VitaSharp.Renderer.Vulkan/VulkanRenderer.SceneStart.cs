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
    sealed class SceneStartCopy
    {
        public RenderTarget Shadow = null!;
        public ImageMemoryPool.Allocation Mem;
        public bool Valid;
    }

    readonly Dictionary<RenderTarget, SceneStartCopy> _sceneStartCopies = new();

    /// <summary>Pixel rectangle the current draw can write (region clip, else the whole scene).</summary>
    int _drawMinX, _drawMinY, _drawMaxX = int.MaxValue, _drawMaxY = int.MaxValue;

    void SetDrawArea(in Core.Gpu.DrawState st)
    {
        if (st.RegionClipMode == 2) { _drawMinX = st.ClipMinX; _drawMinY = st.ClipMinY; _drawMaxX = st.ClipMaxX; _drawMaxY = st.ClipMaxY; }
        else { _drawMinX = _drawMinY = 0; _drawMaxX = _drawMaxY = int.MaxValue; }
    }

    /// <summary>
    /// Whether a texture region of the current surface overlaps the pixels the draw renders. Other parts of the
    /// surface may already have been stored by tiles that finished (post-processing atlases chain passes through
    /// different regions of one scene), so only the overlapping case reads the scene-start content.
    /// </summary>
    bool SamplesOwnTiles(uint x0, uint y0, uint w, uint h) =>
        (int)x0 <= _drawMaxX && (int)(x0 + w - 1) >= _drawMinX && (int)y0 <= _drawMaxY && (int)(y0 + h - 1) >= _drawMinY;

    /// <summary>The scene-start copy of the current surface (created the first time a draw samples it).</summary>
    RenderTarget SceneStartSource(RenderTarget rt)
    {
        if (_sceneStartCopies.TryGetValue(rt, out var c)) return c.Valid ? c.Shadow : rt;
        Log.WarnOnce($"selfsample-{rt.Address:X8}", $"[gpu] render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} is sampled while it is rendered: textures read its content from the start of the scene");
        var (img, view, mem) = CreateImage(rt.Width, rt.Height, rt.Format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.ColorBit, mutable: true);
        var shadow = new RenderTarget
        {
            Address = rt.Address, Width = rt.Width, Height = rt.Height, ColorFormat = rt.ColorFormat, Stride = rt.Stride, Format = rt.Format,
            Image = img, View = view, Version = 0,
        };
        _sceneStartCopies[rt] = new SceneStartCopy { Shadow = shadow, Mem = mem };
        // usable from the next scene of this surface on
        return rt;
    }

    /// <summary>Called when a scene begins: refreshes the copy of a surface that its own draws sample.</summary>
    void CaptureSceneStart(RenderTarget rt)
    {
        if (!_sceneStartCopies.TryGetValue(rt, out var c)) return;
        var s = c.Shadow;
        EndRendering();
        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        Barrier(s.Image, c.Valid ? ImageLayout.General : ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(rt.Width, rt.Height, 1),
        };
        _vk.CmdCopyImage(Cmd, rt.Image, ImageLayout.General, s.Image, ImageLayout.TransferDstOptimal, 1, in copy);
        Barrier(s.Image, ImageLayout.TransferDstOptimal, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        s.Version++;
        s.Dirty = false;
        c.Valid = true;
    }
}
