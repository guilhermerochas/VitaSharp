using Silk.NET.Vulkan;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Programmable blending of small draws in one-sample scenes. On hardware every fragment of a draw that reads the
/// framebuffer colour sees what earlier primitives of the same draw wrote. One-sample scenes give such shaders a copy
/// of the surface taken before the draw, so overlapping primitives of one draw lose each other's output: LittleBigPlanet
/// draws a text string as one draw of SDF glyph quads whose padding overlaps the neighbouring glyphs, and every glyph
/// blended the background from before the draw over the right side of the previous one ("of" became "cf", "STORY"
/// became "STCRY"). Multisampled scenes already render in render passes whose colour attachment is also the input
/// attachment and draw such draws piece by piece (VulkanRenderer.MsaaNative.cs); here a small draw of a
/// one-sample scene leaves the dynamic rendering and renders in a one-subpass render pass of its own on the surface,
/// read as a single-sampled input attachment, with the same pieces and barriers (Core/Gpu/FetchSplit.cs: one
/// triangle per piece in lists, two in strips).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    long _statSingleFetchDraws;
    DescriptorSet _singleInputSet;
    ImageView _singleInputSetView;
    long _singleInputSetFrame = -1;

    /// <summary>The current scene can render a programmable-blending draw in a one-sample render pass: plain
    /// one-sample colour surface (no supersampling, tile-word attachment or tile-based rendering).</summary>
    bool SingleFetchPossible() =>
        _rt != null && _depth != null && !_ms && !_ss && !_msaaNoDownscale && !_tw1Active && !TiledRenderings;

    /// <summary>The surface view the scene renders with (sRGB with surface gamma).</summary>
    ImageView SingleFetchView() => _rtGamma ? _rt!.SrgbView : _rt!.View;

    DescriptorSet BuildSingleInputFetchSet()
    {
        var view = SingleFetchView();
        if (_singleInputSet.Handle != 0 && _singleInputSetView.Handle == view.Handle && _singleInputSetFrame == _frameSerial) return _singleInputSet;
        var set = AllocSet(_fbLayout);
        (_singleInputSet, _singleInputSetView, _singleInputSetFrame) = (set, view, _frameSerial);
        var info = stackalloc DescriptorImageInfo[3]
        {
            new(_dummySampler, _dummyView, ImageLayout.ShaderReadOnlyOptimal),
            new(_dummySampler, DummyMsView(), ImageLayout.General),
            new(default, view, ImageLayout.General),
        };
        var w = stackalloc WriteDescriptorSet[3];
        for (int i = 0; i < 3; i++)
            w[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i, DescriptorCount = 1,
                DescriptorType = i == 2 ? DescriptorType.InputAttachment : DescriptorType.CombinedImageSampler, PImageInfo = &info[i],
            };
        _vk.UpdateDescriptorSets(_dev, 3, w, 0, null);
        return set;
    }

    /// <summary>Ends the scene's dynamic rendering and begins a render pass on the surface and depth/stencil (both
    /// loaded and stored) for one draw; returns the command buffer to record it into.</summary>
    CommandBuffer BeginSingleFetchPass(Format format)
    {
        EndRendering();
        // a pending depth clear (no rendering yet in this scene) happens in an empty rendering first
        if (_depthNeedsClear) { BeginRendering(); EndRendering(); }
        // the scene-break bookkeeping (VulkanRenderer.SceneBreaks.cs) sees this pass as a rendering of its own
        _renderingSerial++;
        _statRenderings++;
        var rt = _rt!;
        var depth = _depth!;
        // the earlier renderings' writes come before this pass's loads, input attachment reads and writes
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit | AccessFlags.ShaderReadBit,
            AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.InputAttachmentReadBit);
        Barrier(depth.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        var pass = MsaaRenderPass(new MsaaPassKey(format, SampleCountFlags.Count1Bit, AttachmentLoadOp.Load, AttachmentStoreOp.Store,
            AttachmentLoadOp.Load, AttachmentStoreOp.Store, false));
        var views = stackalloc ImageView[2] { SingleFetchView(), depth.View };
        var fci = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = pass,
            AttachmentCount = 2,
            PAttachments = views,
            Width = _sceneW, Height = _sceneH, Layers = 1,
        };
        _vk.CreateFramebuffer(_dev, in fci, null, out var fb).Ok("single-fetch framebuffer");
        // the views may die with their surface: the framebuffer lives only as long as this frame's commands
        DestroyAfterGpu(() => _vk.DestroyFramebuffer(_dev, fb, null));
        var rbi = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = fb,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(_sceneW, _sceneH)),
        };
        CountOps(true, AttachmentLoadOp.Load, AttachmentStoreOp.Store, true, AttachmentLoadOp.Load, AttachmentStoreOp.Store);
        // the primary, after any ended renderings still waiting to be begun there (VulkanRenderer.SceneBreaks.cs)
        var cmd = Cmd;
        _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.Inline);
        _boundPipeline = default;
        rt.Dirty = true; rt.Version++; rt.LastTouchedFrame = FrameCount;
        depth.Version++;
        if (_statSingleFetchDraws++ == 0)
            Log.Info($"[gpu] programmable blending in one-sample scenes: small draws render in a render pass that reads the surface as an input attachment (render target 0x{rt.Address:X8})");
        return cmd;
    }

    void EndSingleFetchPass()
    {
        var cmd = F.Cmd;
        _vk.CmdEndRenderPass(cmd);
        _boundPipeline = default;
        // the next rendering (or a copy / sampling of the surface) comes after this pass's writes
        Barrier(_rt!.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit,
            AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.InputAttachmentReadBit | AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        Barrier(_depth!.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
    }
}
