using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Depth surfaces sampled as textures (soft particles, fog, depth of field, SSAO): the GPU depth buffer is copied
/// into a sampleable image of the same format and read through its depth aspect, which yields the normalised depth
/// an X8U24 / F32 texture fetch returns on the Vita.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    sealed class DepthTex
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public uint W, H;
        public long Version = -1;
    }

    readonly Dictionary<DepthTarget, DepthTex> _depthTextures = new();

    DepthTarget? DepthTextureSource(uint addr, GxmTextureDesc d)
    {
        var b = GxmFormats.BaseOf(d.Format);
        if (b is not (TexBase.X8U24 or TexBase.F32 or TexBase.F32M or TexBase.U16)) return null;
        DepthTarget? best = null;
        foreach (var dt in _depthTargets.Values)
            if (dt.Address == addr && (best == null || dt.LastScene > best.LastScene)) best = dt;
        return best != null && MultisampledDepthRead(best, false) ? null : best;
    }

    (ImageView, ImageLayout) DepthTexture(DepthTarget dt, uint w, uint h)
    {
        w = Math.Min(w, dt.Width);
        h = Math.Min(h, dt.Height);
        if (!_depthTextures.TryGetValue(dt, out var t) || t.W != w || t.H != h)
        {
            if (t != null)
            {
                var old = t;
                F.Deferred.Add(() => { _vk.DestroyImageView(_dev, old.View, null); _vk.DestroyImage(_dev, old.Image, null); _imagePool.Free(old.Mem); });
            }
            t = new DepthTex { W = w, H = h };
            (t.Image, t.View, t.Mem) = CreateImage(w, h, _core.DepthFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit);
            _vk.DestroyImageView(_dev, t.View, null);
            // depth-only view: sampling returns depth in .r (replicated to all channels like an X8U24 fetch)
            t.View = CreateView(t.Image, _core.DepthFormat, ImageAspectFlags.DepthBit, 1, 1, false,
                new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R));
            _depthTextures[dt] = t;
            BeginFrameIfNeeded();
            EndRendering();
            Barrier(t.Image, ImageLayout.Undefined, ImageLayout.DepthStencilReadOnlyOptimal, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        }
        if (t.Version == dt.Version) return (t.View, ImageLayout.DepthStencilReadOnlyOptimal);
        t.Version = dt.Version;

        EndRendering();
        const ImageAspectFlags ds = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        Barrier(dt.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.TransferSrcOptimal, ds,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.TransferBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.TransferReadBit);
        Barrier(t.Image, ImageLayout.DepthStencilReadOnlyOptimal, ImageLayout.TransferDstOptimal, ds,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ds, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ds, 0, 0, 1),
            Extent = new Extent3D(w, h, 1),
        };
        _vk.CmdCopyImage(Cmd, dt.Image, ImageLayout.TransferSrcOptimal, t.Image, ImageLayout.TransferDstOptimal, 1, in copy);
        Barrier(dt.Image, ImageLayout.TransferSrcOptimal, ImageLayout.DepthStencilAttachmentOptimal, ds,
            PipelineStageFlags.TransferBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.TransferReadBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        Barrier(t.Image, ImageLayout.TransferDstOptimal, ImageLayout.DepthStencilReadOnlyOptimal, ds,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return (t.View, ImageLayout.DepthStencilReadOnlyOptimal);
    }

    // ------------------------------------------------------------------ stencil planes sampled as U8 textures

    sealed class StencilTex
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public uint W, H, Swizzle;
        public long Version = -1;
    }

    readonly Dictionary<DepthTarget, StencilTex> _stencilTextures = new();

    /// <summary>
    /// A separate S8 stencil plane (DF32_S8, DF32M_S8, S8) read as a U8 texture: games use the stencil they rendered
    /// as a mask in post-processing (Gravity Rush's tone-mapping composite reads it to tell the characters from the
    /// background). The plane only exists on the GPU, so the texture is built from the stencil aspect.
    /// </summary>
    DepthTarget? StencilTextureSource(uint addr, GxmTextureDesc d)
    {
        if (GxmFormats.BaseOf(d.Format) != TexBase.U8 || addr is < GuestMemory.CdramStart or >= GuestMemory.CdramEnd) return null;
        DepthTarget? best = null;
        foreach (var dt in _depthTargets.Values)
            if (dt.StencilAddress == addr && (best == null || dt.LastScene > best.LastScene)) best = dt;
        return best != null && MultisampledDepthRead(best, true) ? null : best;
    }

    (ImageView, ImageLayout) StencilTexture(DepthTarget dt, GxmTextureDesc d)
    {
        uint w = Math.Min(d.Width, Math.Min(dt.Width, dt.StencilStrideBytes)), h = Math.Min(d.Height, dt.Height);
        uint swz = d.Format & GxmFormats.TextureSwizzleMask;
        if (!_stencilTextures.TryGetValue(dt, out var t) || t.W != w || t.H != h || t.Swizzle != swz)
        {
            if (t != null)
            {
                var old = t;
                F.Deferred.Add(() => { _vk.DestroyImageView(_dev, old.View, null); _vk.DestroyImage(_dev, old.Image, null); _imagePool.Free(old.Mem); });
            }
            t = new StencilTex { W = w, H = h, Swizzle = swz };
            (t.Image, t.View, t.Mem) = CreateImage(w, h, Format.R8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                ImageAspectFlags.ColorBit, swizzle: Formats.TextureSwizzle(d.Format));
            _stencilTextures[dt] = t;
            BeginFrameIfNeeded();
            EndRendering();
            Barrier(t.Image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        }
        if (t.Version == dt.Version) return (t.View, ImageLayout.ShaderReadOnlyOptimal);
        t.Version = dt.Version;
        // stencil aspect -> CDRAM mirror (guest layout, one byte per pixel) -> R8 image
        EnsureMirror();
        _mirroredDepth.Remove((dt, true));
        if (!MirrorDepthTarget(dt, true)) return (t.View, ImageLayout.ShaderReadOnlyOptimal);
        EndRendering();
        Barrier(t.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        var region = new BufferImageCopy
        {
            BufferOffset = dt.StencilAddress - GuestMemory.CdramStart,
            BufferRowLength = dt.StencilStrideBytes,
            BufferImageHeight = dt.Height,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(w, h, 1),
        };
        _vk.CmdCopyBufferToImage(Cmd, _mirror, t.Image, ImageLayout.TransferDstOptimal, 1, in region);
        Barrier(t.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferReadBit, AccessFlags.ShaderReadBit);
        return (t.View, ImageLayout.ShaderReadOnlyOptimal);
    }
}
