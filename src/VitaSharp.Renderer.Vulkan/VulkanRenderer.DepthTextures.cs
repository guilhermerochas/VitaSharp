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
        public ImageView AttachView;  // depth/stencil attachment view (unpacked multisampled depth only)
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
        return best != null && MultisampledDepthRead(best, false, unpack: true) ? null : best;
    }

    (ImageView, ImageLayout) DepthTexture(DepthTarget dt, uint w, uint h)
    {
        if (dt.Samples != SampleCountFlags.Count1Bit) return UnpackedDepthTexture(dt, w, h);
        w = Math.Min(w, dt.Width);
        h = Math.Min(h, dt.Height);
        if (!_depthTextures.TryGetValue(dt, out var t) || t.W != w || t.H != h)
        {
            if (t != null)
            {
                var old = t;
                F.Deferred.Add(() => { _vk.DestroyImageView(_dev, old.View, null); if (old.AttachView.Handle != 0) _vk.DestroyImageView(_dev, old.AttachView, null); _vk.DestroyImage(_dev, old.Image, null); _imagePool.Free(old.Mem); });
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

    // ------------------------------------------------------------------ forced-stored 4X depth read as a texture

    /// <summary>
    /// A 4X MSAA scene that forces its depth store writes every sample to memory: the depth surface is sample-sized
    /// (2x2 samples per pixel) and later passes read it as such (LittleBigPlanet's depth of field samples the 720x408
    /// scene's depth as a 1440x816 U16 texture). With real MSAA the samples live in a multisampled depth image, so they
    /// are unpacked by a full-screen pass into a single-sample depth image twice the size, sample (x&amp;1, y&amp;1) of
    /// pixel (x/2, y/2) at texel (x, y) (Vulkan's standard 4x positions: 0 top left, 1 top right, 2 bottom left,
    /// 3 bottom right), which is then sampled like any depth texture.
    /// </summary>
    (ImageView, ImageLayout) UnpackedDepthTexture(DepthTarget dt, uint w, uint h)
    {
        w = Math.Min(w, dt.Width * 2);
        h = Math.Min(h, dt.Height * 2);
        const ImageAspectFlags ds = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        if (!_depthTextures.TryGetValue(dt, out var t) || t.W != w || t.H != h)
        {
            if (t != null)
            {
                var old = t;
                F.Deferred.Add(() => { _vk.DestroyImageView(_dev, old.View, null); _vk.DestroyImageView(_dev, old.AttachView, null); _vk.DestroyImage(_dev, old.Image, null); _imagePool.Free(old.Mem); });
            }
            t = new DepthTex { W = w, H = h };
            (t.Image, t.AttachView, t.Mem) = CreateImage(w, h, _core.DepthFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.DepthStencilAttachmentBit, ds);
            t.View = CreateView(t.Image, _core.DepthFormat, ImageAspectFlags.DepthBit, 1, 1, false,
                new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R));
            _depthTextures[dt] = t;
            BeginFrameIfNeeded();
            EndRendering();
            Barrier(t.Image, ImageLayout.Undefined, ImageLayout.DepthStencilReadOnlyOptimal, ds,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
            Log.Info($"[gpu] depth surface 0x{dt.Address:X8} of a 4X multisampled scene (forced store) is read as a {w}x{h} texture: the samples are unpacked");
        }
        if (t.Version == dt.Version) return (t.View, ImageLayout.DepthStencilReadOnlyOptimal);
        t.Version = dt.Version;

        EndRendering();
        dt.SampledView = dt.SampledView.Handle != 0 ? dt.SampledView : CreateView(dt.Image, _core.DepthFormat, ImageAspectFlags.DepthBit, 1, 1, false, default);
        Barrier(dt.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilReadOnlyOptimal, ds,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.ShaderReadBit);
        Barrier(t.Image, ImageLayout.DepthStencilReadOnlyOptimal, ImageLayout.DepthStencilAttachmentOptimal, ds,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.ShaderReadBit, AccessFlags.DepthStencilAttachmentWriteBit);

        var cmd = Cmd;
        var set = AllocSet(_msLoadSetLayout.Handle != 0 ? _msLoadSetLayout : EnsureLoadLayout());
        var info = new DescriptorImageInfo(_dummySampler, dt.SampledView, ImageLayout.DepthStencilReadOnlyOptimal);
        var wr = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info };
        _vk.UpdateDescriptorSets(_dev, 1, &wr, 0, null);
        var att = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = t.AttachView, ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.DontCare, StoreOp = AttachmentStoreOp.Store,
        };
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo, RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h)), LayerCount = 1,
            PDepthAttachment = &att, PStencilAttachment = &att,
        };
        _vk.CmdBeginRendering(cmd, in ri);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, DepthUnpackPipeline());
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _msLoadLayout, 0, 1, &set, 0, null);
        var vp = new Viewport(0, 0, w, h, 0, 1);
        var sc = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h));
        _vk.CmdSetViewport(cmd, 0, 1, in vp);
        _vk.CmdSetScissor(cmd, 0, 1, in sc);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        _vk.CmdEndRendering(cmd);
        _boundPipeline = default;

        Barrier(dt.Image, ImageLayout.DepthStencilReadOnlyOptimal, ImageLayout.DepthStencilAttachmentOptimal, ds,
            PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.ShaderReadBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        Barrier(t.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilReadOnlyOptimal, ds,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.ShaderReadBit);
        return (t.View, ImageLayout.DepthStencilReadOnlyOptimal);
    }

    const string DepthUnpackFs = """
#version 450
layout(set = 0, binding = 0) uniform sampler2DMS src;
void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    gl_FragDepth = texelFetch(src, p >> 1, (p.y & 1) * 2 + (p.x & 1)).r;
}
""";

    Pipeline _depthUnpackPipeline;
    ShaderModule _depthUnpackFs;

    Pipeline DepthUnpackPipeline()
    {
        if (_depthUnpackPipeline.Handle != 0) return _depthUnpackPipeline;
        EnsureLoadLayout();
        _depthUnpackFs = CompileBuiltin(DepthUnpackFs, false);
        var entry = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _msLoadVs, PName = entry };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = _depthUnpackFs, PName = entry };
        var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var vps = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rs = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, LineWidth = 1 };
        var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var dss = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.Always };
        var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo };
        var dyn = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dsi = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };
        var rendering = new PipelineRenderingCreateInfo
        {
            SType = StructureType.PipelineRenderingCreateInfo, DepthAttachmentFormat = _core.DepthFormat, StencilAttachmentFormat = _core.DepthFormat,
        };
        var gci = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo, PNext = &rendering, StageCount = 2, PStages = stages,
            PVertexInputState = &vi, PInputAssemblyState = &ia, PViewportState = &vps, PRasterizationState = &rs, PMultisampleState = &ms,
            PDepthStencilState = &dss, PColorBlendState = &cb, PDynamicState = &dsi, Layout = _msLoadLayout,
        };
        _vk.CreateGraphicsPipelines(_dev, _vkPipelineCache, 1, in gci, null, out _depthUnpackPipeline).Ok("depth unpack pipeline");
        return _depthUnpackPipeline;
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
