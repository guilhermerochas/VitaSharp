using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The shell's overlay plane (<see cref="PresentCommand.Overlay"/>): while an application that shows its own frames is
/// in front, the frames the shell shows (its on-screen keyboard, notifications) are a second display plane blended over
/// the application's frame with their alpha. The presented frame is copied into a composite image and the shell's
/// frame drawn over it with alpha blending; the composite is what is shown and dumped.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    RenderTarget? _overlayComposite;
    readonly Dictionary<Format, (RenderPass Pass, Pipeline Pipeline)> _overlayPipelines = new();

    /// <summary><paramref name="src"/> with the shell frame <paramref name="overlay"/> blended over it, or src itself
    /// when that frame is not a rendered surface of the same size.</summary>
    RenderTarget ComposeShellOverlay(RenderTarget src, DisplayFrame overlay)
    {
        var o = FindRenderTargetByAddress(overlay.Address);
        if (o == null || o == src || src.Format is not (Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm))
        {
            Log.WarnOnce($"overlay{overlay.Address:X}", $"[gpu] shell overlay 0x{overlay.Address:X8}: {(o == null ? "not a rendered surface" : "not composable")}, not shown");
            return src;
        }
        SyncRenderTargetForRead(o);
        o.LastUsedFrame = o.LastTouchedFrame = FrameCount;
        o = PresentSource(o);
        if (o.ImgW != src.ImgW || o.ImgH != src.ImgH || o.Format is not (Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm))
        {
            Log.WarnOnce($"overlaysize{overlay.Address:X}", $"[gpu] shell overlay 0x{overlay.Address:X8} {o.ImgW}x{o.ImgH} over {src.ImgW}x{src.ImgH}: not shown");
            return src;
        }

        var c = _overlayComposite;
        if (c == null || c.ImgW != src.ImgW || c.ImgH != src.ImgH || c.Format != src.Format || c.Scale != src.Scale || c.Width != src.Width)
        {
            if (c != null)
            {
                DestroyLater(c);
            }

            var (img, view, mem) = CreateImage(MemCat.RenderTarget, src.ImgW, src.ImgH, src.Format,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
            c = _overlayComposite = new RenderTarget { Width = src.Width, Height = src.Height, Stride = src.Stride, Image = img, View = view, Mem = mem, Format = src.Format, Scale = src.Scale };
        }
        c.Address = src.Address;
        c.ColorFormat = src.ColorFormat;
        c.LastUsedFrame = c.LastTouchedFrame = FrameCount;

        EndRendering();
        var cmd = Cmd;
        // the application's frame -> the composite
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Barrier(c.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        var sub = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
        var copy = new ImageCopy(sub, default, sub, default, new Extent3D(src.ImgW, src.ImgH, 1));
        _vk.CmdCopyImage(cmd, src.Image, ImageLayout.General, c.Image, ImageLayout.General, 1, in copy);
        Barrier(c.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferWriteBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        Barrier(o.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);

        // the shell's frame over it, blended with its alpha
        var (pass, pipeline) = OverlayPipeline(c.Format);
        var area = new Extent2D(c.ImgW, c.ImgH);
        var rbi = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = TransientFramebuffer(pass, area, c.View),
            RenderArea = new Rect2D(new Offset2D(0, 0), area),
        };
        var set = AllocSet(EnsureLoadLayout());
        var info = new DescriptorImageInfo(_dummySampler, o.View, ImageLayout.General);
        var wr = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info };
        _vk.UpdateDescriptorSets(_dev, 1, &wr, 0, null);
        _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.Inline);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _msLoadLayout, 0, 1, &set, 0, null);
        var vp = new Viewport(0, 0, c.ImgW, c.ImgH, 0, 1);
        var sc = new Rect2D(new Offset2D(0, 0), area);
        _vk.CmdSetViewport(cmd, 0, 1, in vp);
        _vk.CmdSetScissor(cmd, 0, 1, in sc);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        _vk.CmdEndRenderPass(cmd);
        _boundPipeline = default;
        c.Version++;
        Barrier(c.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        Barrier(o.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit);
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit);
        return c;
    }

    (RenderPass, Pipeline) OverlayPipeline(Format format)
    {
        if (_overlayPipelines.TryGetValue(format, out var p))
        {
            return p;
        }

        var att = new AttachmentDescription(0, format, SampleCountFlags.Count1Bit, AttachmentLoadOp.Load, AttachmentStoreOp.Store,
            AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.General, ImageLayout.General);
        var colorRef = new AttachmentReference(0, ImageLayout.General);
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef };
        var rci = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &att, SubpassCount = 1, PSubpasses = &subpass };
        _vk.CreateRenderPass(_dev, in rci, null, out var pass).Ok("shell overlay render pass");

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
        var blend = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.Zero,
            DstAlphaBlendFactor = BlendFactor.One,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend };
        var dyn = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dsi = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };
        var gci = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            RenderPass = pass,
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
        _vk.CreateGraphicsPipelines(_dev, _vkPipelineCache, 1, in gci, null, out var pipeline).Ok("shell overlay pipeline");
        return _overlayPipelines[format] = (pass, pipeline);
    }
}
