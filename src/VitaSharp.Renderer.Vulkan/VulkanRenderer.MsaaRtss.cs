using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Native MSAA scenes rendered to the single-sampled surface (VITASHARP_MSAA_RTSS=1, VK_EXT_multisampled_render_to_single_sampled,
/// render-pass path with deferred renderings: the Mali-G57). A GXM MSAA downscale scene loads its surface into the
/// samples of the tile memory, renders with 4 samples and writes the averaged pixels back when it stores: what the
/// extension does with a one-sample colour attachment (LOAD fills every sample with the pixel, STORE resolves). The
/// default path instead keeps a multisampled colour image, copies the surface into a scene-start copy and draws it into
/// the samples at the scene's first rendering, then resolves in the render pass (Killzone Mercenary gameplay: an 8 MB
/// copy, a full-screen draw and a 4 MB resolve per frame on the main 960x544 RGBA16F scene). Depth/stencil keeps its
/// multisampled image (the same sample count as the rasterization).
/// <para>The samples exist only inside a rendering, so a scene that needs them after its rendering ended (a mid-scene
/// break, programmable blending, the tile word, raw outputs) continues with what was stored, every sample loaded from
/// the resolved pixel, and the surface uses the default path from then on (<see cref="_rtssBlocked"/>).</para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>The current MSAA scene renders to its single-sampled surface.</summary>
    private bool _msRtss;
    /// <summary>Surfaces whose MSAA scenes needed their samples outside a rendering: the default path.</summary>
    private readonly HashSet<RenderTarget> _rtssBlocked = new();
    private readonly Dictionary<RenderTarget, Dictionary<(ulong Pass, ulong Color, ulong Depth), Framebuffer>> _rtssFramebuffers = new();
    private long _statRtssScenes, _statRtssRenderings, _statRtssDemoted;

    /// <summary>
    /// Whether an MSAA scene renders to its single-sampled surface: the switch and the extension are on, the scene
    /// renderings are render passes recorded into secondaries and begun when they end (their store op is chosen then),
    /// and the surface never needed its samples outside a rendering. Pure, unit-tested (MsaaRtssTests).
    /// </summary>
    internal static bool UseRtss(bool extensionEnabled, bool renderPasses, bool direct, bool surfaceBlocked) =>
        extensionEnabled && renderPasses && !direct && !surfaceBlocked;

    private bool RtssPossible => UseRtss(_core.MultisampledRenderToSingleSampled, UseRenderPasses, MsaaDirect, false);

    /// <summary>Scenes rendered to the single-sampled surface (tests, PERF gpu).</summary>
    public long RtssScenes => _statRtssScenes;

    /// <summary>
    /// One subpass: attachment 0 = the surface (one sample, LOAD fills every sample, STORE resolves), 1 = the
    /// multisampled depth/stencil; rasterization with the scene's sample count.
    /// </summary>
    private RenderPass CreateRtssRenderPass(MsaaPassKey k)
    {
        AttachmentDescription2* att = stackalloc AttachmentDescription2[2];
        att[0] = new AttachmentDescription2
        {
            SType = StructureType.AttachmentDescription2,
            Format = k.Format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = k.ColorLoad,
            StoreOp = k.ColorStore,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.General,
            FinalLayout = ImageLayout.General,
        };
        att[1] = new AttachmentDescription2
        {
            SType = StructureType.AttachmentDescription2,
            Format = _core.DepthFormat,
            Samples = k.Samples,
            LoadOp = k.DepthLoad,
            StoreOp = k.DepthStore,
            StencilLoadOp = k.DepthLoad,
            StencilStoreOp = k.DepthStore,
            InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var color = new AttachmentReference2
        {
            SType = StructureType.AttachmentReference2,
            Attachment = 0,
            Layout = ImageLayout.General,
            AspectMask = ImageAspectFlags.ColorBit,
        };
        var depth = new AttachmentReference2
        {
            SType = StructureType.AttachmentReference2,
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal,
            AspectMask = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        };
        var rtss = new MultisampledRenderToSingleSampledInfoEXT
        {
            SType = StructureType.MultisampledRenderToSingleSampledInfoExt,
            MultisampledRenderToSingleSampledEnable = true,
            RasterizationSamples = k.Samples,
        };
        var sub = new SubpassDescription2
        {
            SType = StructureType.SubpassDescription2,
            PNext = &rtss,
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &color,
            PDepthStencilAttachment = &depth,
        };
        var ci = new RenderPassCreateInfo2
        {
            SType = StructureType.RenderPassCreateInfo2,
            AttachmentCount = 2,
            PAttachments = att,
            SubpassCount = 1,
            PSubpasses = &sub,
        };
        _vk.CreateRenderPass2(_dev, in ci, null, out RenderPass renderPass).Ok("MSAA render-to-single-sampled render pass");
        return renderPass;
    }

    /// <summary>The render-to-single-sampled form of <see cref="BeginMsaaRendering"/>: every rendering loads the surface
    /// into the samples (the scene's first one included: no scene-start copy, no load draw).</summary>
    private void BeginRtssRendering()
    {
        RenderTarget rt = _renderTarget!;
        Format format = _rtAttachFormat;
        DepthSurfaceInfo? depthSurfaceInfo = _scene?.Depth;
        _msPass = new MsaaPass
        {
            Image = null!,
            DepthImage = _depth!.Image,
            Format = format,
            Color = _rtGamma ? rt.SrgbView : rt.View,
            Raw = false,
            ColorLoad = AttachmentLoadOp.Load,
            Depth = _depth.View,
            DepthLoad = _depthNeedsClear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
            DepthClear = new ClearValue(depthStencil: new ClearDepthStencilValue(Math.Clamp(depthSurfaceInfo?.BackgroundDepth ?? 1f, 0f, 1f), depthSurfaceInfo?.BackgroundStencil ?? 0)),
            Area = SceneArea,
        };
        _depthNeedsClear = false;
        _msNeedsLoad = false;
        CommandBuffer cb = AllocSecondary();
        var ii = new CommandBufferInheritanceInfo { SType = StructureType.CommandBufferInheritanceInfo, RenderPass = MsaaCompatPass(format, _msSamples, false, true), Subpass = 0 };
        var bi = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit,
            PInheritanceInfo = &ii,
        };
        _vk.BeginCommandBuffer(cb, in bi).Ok("vkBeginCommandBuffer(render-to-single-sampled secondary)");
        _msCmd = cb;
        _boundPipeline = default;
        _rendering = true;
        _statMsPasses++;
        _statRtssRenderings++;
        rt.Dirty = true;
        rt.Version++;
        _depth.Version++;
    }

    /// <summary>Begins the render pass of the ended rendering in the primary: the surface is stored (resolved) whether
    /// or not the scene ends; depth/stencil as in the default path.</summary>
    private void EndRtssRendering(bool final)
    {
        CommandBuffer cb = _msCmd;
        _vk.EndCommandBuffer(cb).Ok("vkEndCommandBuffer(render-to-single-sampled secondary)");
        _msCmd = default;
        _boundPipeline = default;
        MsaaPass msaaPass = _msPass;
        RenderTarget rt = _renderTarget!;
        bool keepDepth = !final || _scene?.Depth is { ForceStore: true };
        if (final)
        {
            _statMsDiscards++;
        }
        else
        {
            BlockRtss(rt, "a mid-scene break");
        }

        _msUnresolved = false;
        _statMsResolves++;
        ResolveTargetBarrier();
        Barrier(msaaPass.DepthImage, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        AttachmentStoreOp depthStore = keepDepth ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare;
        CountOps(true, AttachmentLoadOp.Load, AttachmentStoreOp.Store, true, msaaPass.DepthLoad, depthStore, msaaPass.Area, msaaPass.Format, (int)_msSamples, colorSamples: 1);
        RenderPass pass = MsaaRenderPass(new MsaaPassKey(msaaPass.Format, _msSamples, AttachmentLoadOp.Load, AttachmentStoreOp.Store,
            msaaPass.DepthLoad, depthStore, false, Rtss: true));
        Framebuffer fb = RtssFramebuffer(rt, pass, msaaPass.Color, msaaPass.Depth);
        ClearValue* clears = stackalloc ClearValue[2] { default, msaaPass.DepthClear };
        var rbi = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = fb,
            RenderArea = new Rect2D(new Offset2D(0, 0), msaaPass.Area),
            ClearValueCount = 2,
            PClearValues = clears,
        };
        CommandBuffer cmd = F.Cmd;
        _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.SecondaryCommandBuffers);
        _vk.CmdExecuteCommands(cmd, 1, in cb);
        _vk.CmdEndRenderPass(cmd);
    }

    /// <summary>Framebuffers of a surface's render-to-single-sampled passes; destroyed with the surface.</summary>
    private Framebuffer RtssFramebuffer(RenderTarget rt, RenderPass pass, ImageView color, ImageView depth)
    {
        if (!_rtssFramebuffers.TryGetValue(rt, out Dictionary<(ulong Pass, ulong Color, ulong Depth), Framebuffer>? fbs))
        {
            fbs = new Dictionary<(ulong Pass, ulong Color, ulong Depth), Framebuffer>();
            _rtssFramebuffers[rt] = fbs;
        }

        (ulong, ulong, ulong) key = (pass.Handle, color.Handle, depth.Handle);
        if (fbs.TryGetValue(key, out Framebuffer fb))
        {
            return fb;
        }

        ImageView* views = stackalloc ImageView[2] { color, depth };
        var ci = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = pass,
            AttachmentCount = 2,
            PAttachments = views,
            Width = rt.ImgW,
            Height = rt.ImgH,
            Layers = 1,
        };
        _vk.CreateFramebuffer(_dev, in ci, null, out fb).Ok("MSAA render-to-single-sampled framebuffer");
        return fbs[key] = fb;
    }

    private void DestroyRtssLater(RenderTarget rt, bool now)
    {
        _rtssBlocked.Remove(rt);
        if (!_rtssFramebuffers.Remove(rt, out Dictionary<(ulong Pass, ulong Color, ulong Depth), Framebuffer>? fbs))
        {
            return;
        }

        Release(() =>
        {
            foreach (Framebuffer fb in fbs.Values)
            {
                _vk.DestroyFramebuffer(_dev, fb, null);
            }
        }, now);
    }

    private void DestroyRtss()
    {
        foreach (Dictionary<(ulong Pass, ulong Color, ulong Depth), Framebuffer> fbs in _rtssFramebuffers.Values)
        {
            foreach (Framebuffer fb in fbs.Values)
            {
                _vk.DestroyFramebuffer(_dev, fb, null);
            }
        }

        _rtssFramebuffers.Clear();
        _rtssBlocked.Clear();
    }

    private void BlockRtss(RenderTarget rt, string reason)
    {
        if (_rtssBlocked.Add(rt))
        {
            Log.Info($"[gpu] render target 0x{rt.Address:X8} {rt.Width}x{rt.Height}: {reason} in an MSAA scene rendered to the single-sampled surface; its MSAA scenes use the multisampled image from now on");
        }
    }

    /// <summary>
    /// A draw of a render-to-single-sampled scene needs the samples outside a rendering (programmable blending, the
    /// tile word, raw outputs): the open rendering stores the surface, and the scene continues on the multisampled
    /// image, its samples loaded from the surface as stored so far (through the scene-start copy, refreshed now).
    /// </summary>
    private void DemoteRtss(string reason)
    {
        RenderTarget rt = _renderTarget!;
        BlockRtss(rt, reason);
        _statRtssDemoted++;
        EndRendering();
        _msRtss = false;
        EnsureSceneStartCopy(rt);
        CaptureSceneStart(rt);
        _msNeedsLoad = true;
    }

    private string RtssReport() => _core.MultisampledRenderToSingleSampled
        ? $"; rendered to the single-sampled surface: {_statRtssScenes} scenes, {_statRtssRenderings} renderings, {_statRtssDemoted} continued on the multisampled image, {_rtssBlocked.Count} surfaces on the multisampled image"
        : "";
}
