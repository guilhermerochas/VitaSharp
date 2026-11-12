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
    private sealed class DepthTex
    {
        public Image Image;
        public ImageView View;
        public ImageView AttachView;  // depth/stencil attachment view (unpacked multisampled depth only)
        public ImageMemoryPool.Allocation Mem;
        public uint W, H;
        public long Version = -1;
        public long LastUsedFrame;
    }

    private readonly Dictionary<DepthTarget, DepthTex> _depthTextures = new();

    private DepthTarget? DepthTextureSource(uint addr, GxmTextureDesc gxmTextureDesc)
    {
        TexBase texBase = GxmFormats.BaseOf(gxmTextureDesc.Format);
        if (texBase is not (TexBase.X8U24 or TexBase.F32 or TexBase.F32M or TexBase.U16))
        {
            return null;
        }

        return StoredDepthSource(addr, stencil: false, unpack: true);
    }

    /// <summary>
    /// The depth target whose content a read of the depth (or stencil) surface at <paramref name="addr"/> sees: the
    /// latest scene that rendered it, unless that scene was multisampled and did not store its samples. Such a scene
    /// leaves memory as the last scene that stored it left it, so the latest target that holds stored content is used
    /// (Metal Gear Solid 3 HD renders its 720x448 world into a one-sample 768x480 scene that stores the depth, then
    /// draws effects in a 4X downscaled scene that loads that depth and does not store it, and its final composite
    /// reads the depth as an X8U24 texture: taken from the 4X scene it fell back to guest memory, never written, and
    /// the fog/colour pass painted red and blue wedges over the plane interior). Only when no such target exists is
    /// the multisampled read reported and guest memory used (<see cref="MultisampledDepthRead"/>).
    /// </summary>
    private DepthTarget? StoredDepthSource(uint addr, bool stencil, bool unpack)
    {
        DepthTarget? best = null, stored = null;
        foreach (DepthTarget dt in _depthTargets.Values)
        {
            if ((stencil ? dt.StencilAddress : dt.Address) != addr)
            {
                continue;
            }

            if (best == null || dt.LastScene > best.LastScene)
            {
                best = dt;
            }

            bool samplesStored = dt.Samples == SampleCountFlags.Count1Bit || (unpack && !stencil && dt.ForceStored);
            if (samplesStored && (stored == null || dt.LastScene > stored.LastScene))
            {
                stored = dt;
            }
        }
        if (best == null)
        {
            return null;
        }

        if (stored != null && stored != best && !(best.Samples != SampleCountFlags.Count1Bit && best.ForceStored))
        {
            best.EverRead = true;
            return stored;
        }
        return MultisampledDepthRead(best, stencil, unpack) ? null : best;
    }

    private (ImageView, ImageLayout) DepthTexture(DepthTarget dt, uint w, uint h)
    {
        dt.LastTouchedFrame = FrameCount; // the copy below may allocate: the source must survive a reclaim
        if (dt.Samples != SampleCountFlags.Count1Bit)
        {
            return UnpackedDepthTexture(dt, w, h);
        }

        if (DepthFeedbackView(dt, w, h) is { } inPlace)
        {
            return (inPlace, ImageLayout.DepthReadOnlyStencilAttachmentOptimal);
        }

        if (dt.Scale != 1f)
        {
            w = ScaleDim(w, dt.Scale);
            h = ScaleDim(h, dt.Scale);
        } // image pixels (VulkanRenderer.ResScale.cs)
        w = Math.Min(w, dt.Width);
        h = Math.Min(h, dt.Height);
        if (!_depthTextures.TryGetValue(dt, out DepthTex? depthTex) || depthTex.W != w || depthTex.H != h)
        {
            if (depthTex != null)
            {
                DepthTex old = depthTex;
                DestroyAfterGpu(() => DestroyDepthTex(old));
            }
            depthTex = new DepthTex { W = w, H = h };
            // DEPTH_STENCIL_READ_ONLY_OPTIMAL (the layout it is sampled in) needs the attachment usage (validation on the Mali-G57)
            (depthTex.Image, depthTex.View, depthTex.Mem) = CreateImage(MemCat.Region, w, h, _core.DepthFormat,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.DepthStencilAttachmentBit,
                ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit);
            _vk.DestroyImageView(_dev, depthTex.View, null);
            // depth-only view: sampling returns depth in .r (replicated to all channels like an X8U24 fetch)
            depthTex.View = CreateView(depthTex.Image, _core.DepthFormat, ImageAspectFlags.DepthBit, 1, 1, false,
                new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R));
            _depthTextures[dt] = depthTex;
            BeginFrameIfNeeded();
            using OutsideScope init = BeginOutside(depthTex.View);
            Barrier(depthTex.Image, ImageLayout.Undefined, ImageLayout.DepthStencilReadOnlyOptimal, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        }
        depthTex.LastUsedFrame = dt.LastTouchedFrame = FrameCount;
        if (depthTex.Version == dt.Version)
        {
            return (depthTex.View, ImageLayout.DepthStencilReadOnlyOptimal);
        }

        depthTex.Version = dt.Version;

        // another scene's depth is copied before the open rendering; the scene's own depth ends it
        using OutsideScope outside = BeginOutside(depthTex.View, readsDepth: dt == _depth);
        const ImageAspectFlags DepthStencilAspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        Barrier(dt.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.TransferSrcOptimal, DepthStencilAspect,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.TransferBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.TransferReadBit);
        Barrier(depthTex.Image, ImageLayout.DepthStencilReadOnlyOptimal, ImageLayout.TransferDstOptimal, DepthStencilAspect,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(DepthStencilAspect, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(DepthStencilAspect, 0, 0, 1),
            Extent = new Extent3D(w, h, 1),
        };
        DevStampOp();
        _vk.CmdCopyImage(Cmd, dt.Image, ImageLayout.TransferSrcOptimal, depthTex.Image, ImageLayout.TransferDstOptimal, 1, in copy);
        Barrier(dt.Image, ImageLayout.TransferSrcOptimal, ImageLayout.DepthStencilAttachmentOptimal, DepthStencilAspect,
            PipelineStageFlags.TransferBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.TransferReadBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        Barrier(depthTex.Image, ImageLayout.TransferDstOptimal, ImageLayout.DepthStencilReadOnlyOptimal, DepthStencilAspect,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return (depthTex.View, ImageLayout.DepthStencilReadOnlyOptimal);
    }

    // ------------------------------------------------------------------ forced-stored 4X depth read as a texture

    /// <summary>
    /// A 4X MSAA scene that forces its depth store writes every sample to memory: the depth surface is sample-sized
    /// (2x2 samples per pixel) and later passes read it as such (LittleBigPlanet's depth of field samples the 720x408
    /// scene's depth as a 1440x816 U16 texture). With real MSAA the samples live in a multisampled depth image, so they
    /// are unpacked by a full-screen pass into a single-sample depth image twice the size, sample (x&amp;1, y&amp;1) of
    /// pixel (x/2, y/2) at texel (x, y) (Vulkan's standard 4x positions: 0 top left, 1 top right, 2 bottom left,
    /// 3 bottom right), which is then sampled like any depth texture. A 2X scene stores its two samples one above the
    /// other (Ninja Gaiden Sigma Plus's depth of field reads the 960x544 scene's depth as a 960x1088 F32 texture): texel
    /// (x, y) is the upper (y even) or lower sample of pixel (x, y/2). Without that the read saw guest memory, the blur
    /// factor came out "far" everywhere and the cutscenes were blurred over the subject in focus.
    /// </summary>
    private void DestroyDepthTex(DepthTex depthTex)
    {
        _vk.DestroyImageView(_dev, depthTex.View, null);
        if (depthTex.AttachView.Handle != 0)
        {
            _vk.DestroyImageView(_dev, depthTex.AttachView, null);
        }

        _vk.DestroyImage(_dev, depthTex.Image, null);
        _imagePool.Free(depthTex.Mem);
    }

    private (ImageView, ImageLayout) UnpackedDepthTexture(DepthTarget dt, uint w, uint h)
    {
        // above native the multisampled image is scaled too: every sample of it is unpacked, at the scaled size, like
        // the single-sampled depth texture (VulkanRenderer.ResScale.cs). Unpacked at the guest size, texel (x, y) read
        // pixel (x/2, y/2) of the scaled image, the top-left quarter of the scene, and LittleBigPlanet's depth of field
        // blurred the whole planet in its pod menus at 2x
        if (dt.Scale != 1f)
        {
            w = ScaleDim(w, dt.Scale);
            h = ScaleDim(h, dt.Scale);
        }
        int layout = UnpackLayout(dt);
        w = Math.Min(w, layout == 0 ? dt.Width * 2 : dt.Width);
        h = Math.Min(h, dt.Height * 2);
        const ImageAspectFlags DepthStencilAspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        if (!_depthTextures.TryGetValue(dt, out DepthTex? depthTex) || depthTex.W != w || depthTex.H != h)
        {
            if (depthTex != null)
            {
                DepthTex old = depthTex;
                DestroyAfterGpu(() => DestroyDepthTex(old));
            }
            depthTex = new DepthTex { W = w, H = h };
            (depthTex.Image, depthTex.AttachView, depthTex.Mem) = CreateImage(MemCat.Region, w, h, _core.DepthFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.DepthStencilAttachmentBit, DepthStencilAspect);
            depthTex.View = CreateView(depthTex.Image, _core.DepthFormat, ImageAspectFlags.DepthBit, 1, 1, false,
                new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R));
            _depthTextures[dt] = depthTex;
            BeginFrameIfNeeded();
            EndRendering();
            Barrier(depthTex.Image, ImageLayout.Undefined, ImageLayout.DepthStencilReadOnlyOptimal, DepthStencilAspect,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
            Log.Info($"[gpu] depth surface 0x{dt.Address:X8} of a {(layout == 0 ? "4X" : "2X")} multisampled scene (forced store) is read as a {w}x{h} texture: the samples are unpacked");
        }
        depthTex.LastUsedFrame = FrameCount;
        if (depthTex.Version == dt.Version)
        {
            return (depthTex.View, ImageLayout.DepthStencilReadOnlyOptimal);
        }

        depthTex.Version = dt.Version;

        EndRendering();
        dt.SampledView = dt.SampledView.Handle != 0 ? dt.SampledView : CreateView(dt.Image, _core.DepthFormat, ImageAspectFlags.DepthBit, 1, 1, false, default);
        Barrier(dt.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilReadOnlyOptimal, DepthStencilAspect,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.ShaderReadBit);
        Barrier(depthTex.Image, ImageLayout.DepthStencilReadOnlyOptimal, ImageLayout.DepthStencilAttachmentOptimal, DepthStencilAspect,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.ShaderReadBit, AccessFlags.DepthStencilAttachmentWriteBit);

        CommandBuffer cmd = Cmd;
        DescriptorSet set = AllocSet(_msLoadSetLayout.Handle != 0 ? _msLoadSetLayout : EnsureLoadLayout());
        var info = new DescriptorImageInfo(_dummySampler, dt.SampledView, ImageLayout.DepthStencilReadOnlyOptimal);
        var wr = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info };
        _vk.UpdateDescriptorSets(_dev, 1, &wr, 0, null);
        var att = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depthTex.AttachView,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
        };
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h)),
            LayerCount = 1,
            PDepthAttachment = &att,
            PStencilAttachment = &att,
        };
        if (UseRenderPasses)
        {
            // Vulkan 1.1 path (VulkanRenderer.RenderPasses.cs)
            RenderPass pass = DepthUnpackPass();
            var rbi = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = pass,
                Framebuffer = TransientFramebuffer(pass, new Extent2D(w, h), depthTex.AttachView),
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h)),
            };
            _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.Inline);
        }
        else
        {
            _vk.CmdBeginRendering(cmd, in ri);
        }

        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, DepthUnpackPipeline(layout));
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _msLoadLayout, 0, 1, &set, 0, null);
        var vp = new Viewport(0, 0, w, h, 0, 1);
        var sc = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h));
        _vk.CmdSetViewport(cmd, 0, 1, in vp);
        _vk.CmdSetScissor(cmd, 0, 1, in sc);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        if (UseRenderPasses)
        {
            _vk.CmdEndRenderPass(cmd);
        }
        else
        {
            _vk.CmdEndRendering(cmd);
        }

        _boundPipeline = default;

        Barrier(dt.Image, ImageLayout.DepthStencilReadOnlyOptimal, ImageLayout.DepthStencilAttachmentOptimal, DepthStencilAspect,
            PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.ShaderReadBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        Barrier(depthTex.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilReadOnlyOptimal, DepthStencilAspect,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.ShaderReadBit);
        return (depthTex.View, ImageLayout.DepthStencilReadOnlyOptimal);
    }

    /// <summary>
    /// How the samples of <paramref name="dt"/> sit in the guest's sample-sized surface: 0 = 4X (2x2 per pixel),
    /// 1 = 2X in a 2-sample image, 2 = 2X in a 4-sample image (Msaa2xAs4x).
    /// </summary>
    private static int UnpackLayout(DepthTarget dt) => dt.TwoXAsFour ? 2 : dt.Samples == SampleCountFlags.Count2Bit ? 1 : 0;

    // 4X: Vulkan's standard 4x positions (0 top left, 1 top right, 2 bottom left, 3 bottom right). 2X: Vulkan's 2x
    // positions put sample 1 at (0.25, 0.25) above sample 0 at (0.75, 0.75); in a 4-sample image the 2X scene's upper
    // and lower samples are 0 (0.375, 0.125) and 3 (0.625, 0.875)
    private static readonly string[] s_depthUnpackFetch =
    [
        "texelFetch(src, p >> 1, (p.y & 1) * 2 + (p.x & 1))",
        "texelFetch(src, ivec2(p.x, p.y >> 1), 1 - (p.y & 1))",
        "texelFetch(src, ivec2(p.x, p.y >> 1), (p.y & 1) * 3)",
    ];

    /// <summary>The unpack pass's fragment shader for a <see cref="UnpackLayout"/> layout.</summary>
    public static string DepthUnpackFs(int layout) => $$"""
#version 450
layout(set = 0, binding = 0) uniform sampler2DMS src;
void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    gl_FragDepth = {{s_depthUnpackFetch[layout]}}.r;
}
""";

    private readonly Pipeline[] _depthUnpackPipelines = new Pipeline[3];
    private readonly ShaderModule[] _depthUnpackFs = new ShaderModule[3];

    private Pipeline DepthUnpackPipeline(int layout)
    {
        if (_depthUnpackPipelines[layout].Handle != 0)
        {
            return _depthUnpackPipelines[layout];
        }

        EnsureLoadLayout();
        ShaderModule fs = _depthUnpackFs[layout] = CompileBuiltin(DepthUnpackFs(layout), false);
        byte* entry = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _msLoadVs, PName = entry };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fs, PName = entry };
        var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var vps = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rs = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, LineWidth = 1 };
        var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var dss = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.Always };
        var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo };
        DynamicState* dyn = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dsi = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };
        var rendering = new PipelineRenderingCreateInfo
        {
            SType = StructureType.PipelineRenderingCreateInfo,
            DepthAttachmentFormat = _core.DepthFormat,
            StencilAttachmentFormat = _core.DepthFormat,
        };
        var gci = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            PNext = UseRenderPasses ? null : &rendering,
            RenderPass = UseRenderPasses ? DepthUnpackPass() : default,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vi,
            PInputAssemblyState = &ia,
            PViewportState = &vps,
            PRasterizationState = &rs,
            PMultisampleState = &ms,
            PDepthStencilState = &dss,
            PColorBlendState = &cb,
            PDynamicState = &dsi,
            Layout = _msLoadLayout,
        };
        _vk.CreateGraphicsPipelines(_dev, _vkPipelineCache, 1, in gci, null, out _depthUnpackPipelines[layout]).Ok("depth unpack pipeline");
        return _depthUnpackPipelines[layout];
    }

    // ------------------------------------------------------------------ stencil planes sampled as U8 textures

    private sealed class StencilTex
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public uint W, H, Swizzle;
        public long Version = -1;
        public long LastUsedFrame;
    }

    private readonly Dictionary<DepthTarget, StencilTex> _stencilTextures = new();

    /// <summary>
    /// A separate S8 stencil plane (DF32_S8, DF32M_S8, S8) read as a U8 texture: games use the stencil they rendered
    /// as a mask in post-processing (Gravity Rush's tone-mapping composite reads it to tell the characters from the
    /// background). The plane only exists on the GPU, so the texture is built from the stencil aspect.
    /// </summary>
    private DepthTarget? StencilTextureSource(uint addr, GxmTextureDesc gxmTextureDesc)
    {
        if (GxmFormats.BaseOf(gxmTextureDesc.Format) != TexBase.U8 || addr is < GuestMemory.CdramStart or >= GuestMemory.CdramEnd)
        {
            return null;
        }

        return StoredDepthSource(addr, stencil: true, unpack: false);
    }

    private (ImageView, ImageLayout) StencilTexture(DepthTarget dt, GxmTextureDesc gxmTextureDesc)
    {
        dt.LastTouchedFrame = FrameCount;
        uint w = Math.Min(gxmTextureDesc.Width, Math.Min(dt.Width, dt.StencilStrideBytes)), h = Math.Min(gxmTextureDesc.Height, dt.Height);
        if (dt.Scale != 1f)
        {
            w = Math.Min(ScaleDim(gxmTextureDesc.Width, dt.Scale), dt.Width);
            h = Math.Min(ScaleDim(gxmTextureDesc.Height, dt.Scale), dt.Height);
        } // VulkanRenderer.ResScale.cs
        uint swz = gxmTextureDesc.Format & GxmFormats.TextureSwizzleMask;
        if (!_stencilTextures.TryGetValue(dt, out StencilTex? stencilTex) || stencilTex.W != w || stencilTex.H != h || stencilTex.Swizzle != swz)
        {
            if (stencilTex != null)
            {
                StencilTex old = stencilTex;
                DestroyAfterGpu(() =>
                {
                    _vk.DestroyImageView(_dev, old.View, null);
                    _vk.DestroyImage(_dev, old.Image, null);
                    _imagePool.Free(old.Mem);
                });
            }
            stencilTex = new StencilTex { W = w, H = h, Swizzle = swz };
            (stencilTex.Image, stencilTex.View, stencilTex.Mem) = CreateImage(MemCat.Region, w, h, Format.R8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                ImageAspectFlags.ColorBit, swizzle: Formats.TextureSwizzle(gxmTextureDesc.Format));
            _stencilTextures[dt] = stencilTex;
            BeginFrameIfNeeded();
            EndRendering();
            Barrier(stencilTex.Image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        }
        stencilTex.LastUsedFrame = dt.LastTouchedFrame = FrameCount;
        if (stencilTex.Version == dt.Version)
        {
            return (stencilTex.View, ImageLayout.ShaderReadOnlyOptimal);
        }

        stencilTex.Version = dt.Version;
        // above native the scaled plane is larger than the guest's stencil surface: it goes through a buffer of its own
        if (dt.Scale > 1f)
        {
            CopyScaledStencil(dt, stencilTex, w, h);
            return (stencilTex.View, ImageLayout.ShaderReadOnlyOptimal);
        }
        // stencil aspect -> CDRAM mirror (guest layout, one byte per pixel) -> R8 image
        EnsureMirror();
        _mirroredDepth.Remove((dt, true));
        if (!MirrorDepthTarget(dt, true))
        {
            return (stencilTex.View, ImageLayout.ShaderReadOnlyOptimal);
        }

        EndRendering();
        Barrier(stencilTex.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        var region = new BufferImageCopy
        {
            BufferOffset = dt.StencilAddress - GuestMemory.CdramStart,
            BufferRowLength = dt.Scale != 1f ? dt.Width : dt.StencilStrideBytes,
            BufferImageHeight = dt.Height,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(w, h, 1),
        };
        _vk.CmdCopyBufferToImage(Cmd, _mirror, stencilTex.Image, ImageLayout.TransferDstOptimal, 1, in region);
        Barrier(stencilTex.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferReadBit, AccessFlags.ShaderReadBit);
        return (stencilTex.View, ImageLayout.ShaderReadOnlyOptimal);
    }

    private Silk.NET.Vulkan.Buffer _stencilScratch;
    private DeviceMemory _stencilScratchMem;
    private ulong _stencilScratchBytes;

    /// <summary>
    /// Stencil texture of a target above native resolution (VulkanRenderer.ResScale.cs): the stencil aspect is copied
    /// at the internal resolution through a scratch buffer into the R8 image, so the texture keeps the scaled detail and
    /// nothing is written past the guest's stencil surface in the CDRAM mirror.
    /// </summary>
    private void CopyScaledStencil(DepthTarget dt, StencilTex stencilTex, uint w, uint h)
    {
        ulong bytes = (ulong)w * h;
        if (_stencilScratchBytes < bytes)
        {
            if (_stencilScratch.Handle != 0)
            {
                (Silk.NET.Vulkan.Buffer ob, DeviceMemory om) = (_stencilScratch, _stencilScratchMem);
                DestroyAfterGpu(() => _core.DestroyBuffer(ob, om));
            }
            (_stencilScratch, _stencilScratchMem) = _core.CreateDeviceBuffer(bytes, BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit, MemCat.Region);
            _stencilScratchBytes = bytes;
        }
        EndRendering();
        const ImageAspectFlags DepthStencilAspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        Barrier(dt.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.TransferSrcOptimal, DepthStencilAspect,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.TransferBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.TransferReadBit);
        BufferBarrier(_stencilScratch, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            BufferRowLength = w,
            BufferImageHeight = h,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.StencilBit, 0, 0, 1),
            ImageExtent = new Extent3D(w, h, 1),
        };
        _vk.CmdCopyImageToBuffer(Cmd, dt.Image, ImageLayout.TransferSrcOptimal, _stencilScratch, 1, in region);
        Barrier(dt.Image, ImageLayout.TransferSrcOptimal, ImageLayout.DepthStencilAttachmentOptimal, DepthStencilAspect,
            PipelineStageFlags.TransferBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.TransferReadBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        BufferBarrier(_stencilScratch, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Barrier(stencilTex.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        region.ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
        _vk.CmdCopyBufferToImage(Cmd, _stencilScratch, stencilTex.Image, ImageLayout.TransferDstOptimal, 1, in region);
        Barrier(stencilTex.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
    }

    private void DestroyStencilScratch()
    {
        if (_stencilScratch.Handle != 0)
        {
            _core.DestroyBuffer(_stencilScratch, _stencilScratchMem);
        }

        _stencilScratch = default;
        _stencilScratchBytes = 0;
    }
}
