using Silk.NET.Vulkan;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The display shows guest memory. U8U8U8U8 colour surfaces with the RGBA or BGRA swizzle render into images that hold
/// the program's output in logical order (<see cref="Formats.LogicalOrderSwizzle"/>), so presenting such an image as
/// it is would show other colours than the hardware: a full-screen pass copies it through a view in memory byte order
/// (<see cref="Formats.SurfaceMemoryOrder"/>) into an image that is presented (and dumped) instead. Gal Gunvolt
/// composes its frame into an RGBA framebuffer and writes (w, z, y, x) for the display to read R, G, B, A.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    sealed class MemoryOrderCopy
    {
        public RenderTarget Target = null!;
        public ImageView SourceView;
        public Image SourceImage;
        public uint ColorFormat;
    }

    readonly Dictionary<RenderTarget, MemoryOrderCopy> _memoryOrderCopies = new();
    RenderPass _memoryOrderPass;
    Pipeline _memoryOrderPipeline;

    /// <summary><paramref name="src"/> in guest memory byte order: itself unless it is a logical-order surface.</summary>
    RenderTarget PresentSource(RenderTarget src)
    {
        if (src.Format != Format.R8G8B8A8Unorm || Formats.LogicalOrderSwizzle(src.ColorFormat) == 0)
        {
            return src;
        }

        if (!_memoryOrderCopies.TryGetValue(src, out var c) || c.Target.Width != src.Width || c.Target.Height != src.Height || c.Target.Scale != src.Scale
            || c.SourceImage.Handle != src.Image.Handle || c.ColorFormat != src.ColorFormat)
        {
            if (c != null)
            {
                ReleaseMemoryOrderCopy(c);
            }

            var (img, view, mem) = CreateImage(MemCat.RenderTarget, src.ImgW, src.ImgH, Format.R8G8B8A8Unorm,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
            c = new MemoryOrderCopy
            {
                Target = new RenderTarget { Address = src.Address, Width = src.Width, Height = src.Height, Stride = src.Stride, Image = img, View = view, Mem = mem, Format = Format.R8G8B8A8Unorm, Scale = src.Scale },
                SourceView = CreateView(src.Image, Format.R8G8B8A8Unorm, ImageAspectFlags.ColorBit, 1, 1, false, Formats.SurfaceMemoryOrder(src.ColorFormat)),
                SourceImage = src.Image,
                ColorFormat = src.ColorFormat,
            };
            _memoryOrderCopies[src] = c;
        }
        RecordMemoryOrderCopy(src, c);
        c.Target.LastUsedFrame = c.Target.LastTouchedFrame = FrameCount;
        return c.Target;
    }

    void ReleaseMemoryOrderCopy(MemoryOrderCopy c)
    {
        var (img, view, mem, srcView) = (c.Target.Image, c.Target.View, c.Target.Mem, c.SourceView);
        DestroyAfterGpu(() =>
        {
            _vk.DestroyImageView(_dev, srcView, null);
            _vk.DestroyImageView(_dev, view, null);
            _vk.DestroyImage(_dev, img, null);
            _imagePool.Free(mem);
        });
    }

    /// <summary>Drops the copies of surfaces that no longer exist (called when a target is released).</summary>
    void ForgetMemoryOrderCopy(RenderTarget rt)
    {
        if (_memoryOrderCopies.Remove(rt, out var c))
        {
            ReleaseMemoryOrderCopy(c);
        }
    }

    void RecordMemoryOrderCopy(RenderTarget src, MemoryOrderCopy c)
    {
        EndRendering();
        var cmd = Cmd;
        var dst = c.Target;
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        Barrier(dst.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit, 0, AccessFlags.ColorAttachmentWriteBit);
        var pass = MemoryOrderPass();
        var area = new Extent2D(dst.ImgW, dst.ImgH);
        var rbi = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = TransientFramebuffer(pass, area, dst.View),
            RenderArea = new Rect2D(new Offset2D(0, 0), area),
        };
        var set = AllocSet(EnsureLoadLayout());
        var info = new DescriptorImageInfo(_dummySampler, c.SourceView, ImageLayout.General);
        var wr = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info };
        _vk.UpdateDescriptorSets(_dev, 1, &wr, 0, null);
        _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.Inline);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, MemoryOrderPipeline());
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _msLoadLayout, 0, 1, &set, 0, null);
        var vp = new Viewport(0, 0, dst.ImgW, dst.ImgH, 0, 1);
        var sc = new Rect2D(new Offset2D(0, 0), area);
        _vk.CmdSetViewport(cmd, 0, 1, in vp);
        _vk.CmdSetScissor(cmd, 0, 1, in sc);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        _vk.CmdEndRenderPass(cmd);
        _boundPipeline = default;
        dst.Version++; // (its native-size copy follows it: VulkanRenderer.ResScale.cs)
        // the copy is read by the swapchain blit / frame readback; the source is rendered again after this read
        Barrier(dst.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit);
    }

    RenderPass MemoryOrderPass()
    {
        if (_memoryOrderPass.Handle != 0)
        {
            return _memoryOrderPass;
        }

        var att = new AttachmentDescription(0, Format.R8G8B8A8Unorm, SampleCountFlags.Count1Bit, AttachmentLoadOp.DontCare, AttachmentStoreOp.Store,
            AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.General, ImageLayout.General);
        var colorRef = new AttachmentReference(0, ImageLayout.General);
        var sub = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef };
        var ci = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &att, SubpassCount = 1, PSubpasses = &sub };
        _vk.CreateRenderPass(_dev, in ci, null, out _memoryOrderPass).Ok("memory order render pass");
        return _memoryOrderPass;
    }

    Pipeline MemoryOrderPipeline()
    {
        if (_memoryOrderPipeline.Handle != 0)
        {
            return _memoryOrderPipeline;
        }

        EnsureLoadLayout();
        var entry = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _msLoadVs, PName = entry };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = _msLoadFsFloat, PName = entry };
        var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var vps = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rs = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, LineWidth = 1 };
        var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var dss = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
        var blend = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend };
        var dyn = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dsi = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };
        var gci = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            RenderPass = MemoryOrderPass(),
            Subpass = 0,
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
        _vk.CreateGraphicsPipelines(_dev, _vkPipelineCache, 1, in gci, null, out _memoryOrderPipeline).Ok("memory order pipeline");
        return _memoryOrderPipeline;
    }
}
