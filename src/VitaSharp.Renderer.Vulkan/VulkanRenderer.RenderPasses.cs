using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The Vulkan 1.1 path (<see cref="GpuQuirk.RenderPasses"/>): devices without dynamic rendering (Adreno 6xx on old
/// drivers, Vulkan 1.1/1.2 devices) and drivers whose dynamic rendering is suspect render every single-sampled scene
/// rendering, the empty MSAA resolve rendering and the depth unpack pass in a VkRenderPass with a VkFramebuffer.
/// Multisampled scenes always used render passes (VulkanRenderer.MsaaNative.cs); single-sampled ones get the same
/// treatment here:
/// <list type="bullet">
/// <item>scene renderings are always deferred into secondaries (VulkanRenderer.Tiler.cs), which inherit the
/// compatible pass of their attachment formats; the pass with the real load/store ops and a framebuffer of the
/// rendering's views is begun when the rendering ends;</item>
/// <item>one subpass: colour (attachment 0, GENERAL), the scene's tile word (1, GENERAL) when it has one, depth/stencil
/// last; colour and tile word always LOAD/STORE, depth/stencil with the rendering's ops (the same ops as dynamic
/// rendering, so tile-based devices keep their per-scene DONT_CARE stores);</item>
/// <item>framebuffers are created for one rendering and destroyed once the frame retires (a few dozen per frame;
/// no cache to invalidate when render targets and their views are destroyed);</item>
/// <item>pipelines of single-sampled scenes are created against the compatible pass instead of
/// VkPipelineRenderingCreateInfo.</item>
/// </list>
/// The device is then created without the Vulkan 1.3 features (and, on a 1.1 device or the adreno-vk11 profile,
/// without any 1.2/1.3 feature struct); extended dynamic state is baked into the pipelines when it is missing.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Renderings are render passes instead of dynamic rendering.</summary>
    bool UseRenderPasses => !_core.UsesDynamicRendering;

    /// <param name="Color">colour attachment format, Undefined = depth/stencil only</param>
    readonly record struct SinglePassKey(Format Color, bool Word, AttachmentLoadOp DepthLoad, AttachmentStoreOp DepthStore, AttachmentStoreOp ColorStore = AttachmentStoreOp.Store);

    readonly Dictionary<SinglePassKey, RenderPass> _singlePasses = new();
    long _statRenderPasses, _statFramebuffers;

    /// <summary>The pass pipelines and secondaries of single-sampled renderings are created against.</summary>
    RenderPass SingleCompatPass(Format color, bool word) =>
        SinglePass(new SinglePassKey(color, word && color != Format.Undefined, AttachmentLoadOp.Load, AttachmentStoreOp.Store));

    RenderPass SinglePass(SinglePassKey k)
    {
        lock (_singlePasses)
        {
            if (_singlePasses.TryGetValue(k, out var rp)) return rp;
            var att = stackalloc AttachmentDescription[3];
            uint n = 0;
            bool color = k.Color != Format.Undefined;
            if (color)
            {
                att[n++] = new AttachmentDescription(0, k.Color, SampleCountFlags.Count1Bit, AttachmentLoadOp.Load, k.ColorStore,
                    AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.General, ImageLayout.General);
                if (k.Word)
                    att[n++] = new AttachmentDescription(0, TileWord1Format, SampleCountFlags.Count1Bit, AttachmentLoadOp.Load, AttachmentStoreOp.Store,
                        AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.General, ImageLayout.General);
            }
            uint depthIndex = n;
            att[n++] = new AttachmentDescription(0, _core.DepthFormat, SampleCountFlags.Count1Bit, k.DepthLoad, k.DepthStore, k.DepthLoad, k.DepthStore,
                ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilAttachmentOptimal);
            var colors = stackalloc AttachmentReference[2] { new(0, ImageLayout.General), new(1, ImageLayout.General) };
            var depth = new AttachmentReference(depthIndex, ImageLayout.DepthStencilAttachmentOptimal);
            var sub = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = depthIndex, PColorAttachments = colors,
                PDepthStencilAttachment = &depth,
            };
            var ci = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = n, PAttachments = att, SubpassCount = 1, PSubpasses = &sub };
            _vk.CreateRenderPass(_dev, in ci, null, out rp).Ok("render pass");
            _statRenderPasses++;
            return _singlePasses[k] = rp;
        }
    }

    /// <summary>A framebuffer for one rendering, destroyed once the frame that uses it retires.</summary>
    Framebuffer TransientFramebuffer(RenderPass pass, Extent2D area, ImageView a0, ImageView a1 = default, ImageView a2 = default)
    {
        var views = stackalloc ImageView[3] { a0, a1, a2 };
        uint n = a2.Handle != 0 ? 3u : a1.Handle != 0 ? 2u : 1u;
        var ci = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo, RenderPass = pass, AttachmentCount = n, PAttachments = views,
            Width = Math.Max(area.Width, 1), Height = Math.Max(area.Height, 1), Layers = 1,
        };
        _vk.CreateFramebuffer(_dev, in ci, null, out var fb).Ok("vkCreateFramebuffer");
        _statFramebuffers++;
        DestroyAfterGpu(() => _vk.DestroyFramebuffer(_dev, fb, null));
        return fb;
    }

    /// <summary>Begins a scene rendering as a render pass in the primary (its draws are in secondaries).</summary>
    void BeginSingleRenderPass(CommandBuffer cmd, in TilePass p, AttachmentStoreOp depthStore)
    {
        var colorFormat = p.HasColor ? p.ColorFormat : Format.Undefined;
        bool word = p.HasColor && p.Word.Handle != 0;
        var pass = SinglePass(new SinglePassKey(colorFormat, word, p.DepthLoad, depthStore));
        var fb = !p.HasColor ? TransientFramebuffer(pass, p.Area, p.Depth)
            : word ? TransientFramebuffer(pass, p.Area, p.Color, p.Word, p.Depth)
            : TransientFramebuffer(pass, p.Area, p.Color, p.Depth);
        // clear values are indexed by attachment: depth/stencil is the last one
        var clears = stackalloc ClearValue[3] { p.DepthClear, p.DepthClear, p.DepthClear };
        var rbi = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo, RenderPass = pass, Framebuffer = fb,
            RenderArea = new Rect2D(new Offset2D(0, 0), p.Area), ClearValueCount = 3, PClearValues = clears,
        };
        _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.SecondaryCommandBuffers);
    }

    /// <summary>Depth-only pass with the depth unpack's ops (DONT_CARE load, STORE).</summary>
    RenderPass DepthUnpackPass() => SinglePass(new SinglePassKey(Format.Undefined, false, AttachmentLoadOp.DontCare, AttachmentStoreOp.Store));

    string RenderPassReport() => UseRenderPasses ? $"render passes (no dynamic rendering): {_singlePasses.Count} single-sampled passes, {_statFramebuffers} framebuffers" : "dynamic rendering";
}
