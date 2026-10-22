using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
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
}
