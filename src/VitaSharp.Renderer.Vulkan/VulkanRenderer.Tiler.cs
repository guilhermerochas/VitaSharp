using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Tile-based GPUs (<see cref="GpuQuirk.TileBased"/>: Adreno, Mali, PowerVR, Apple): what a rendering loads into
/// the tiles and stores back is chosen per scene, so tile memory is not written out when the Vita would not have
/// written it either.
/// <list type="bullet">
/// <item>Single-sampled renderings are recorded into a secondary command buffer and begun in the primary only when
/// they end (as multisampled ones always are, VulkanRenderer.MsaaNative.cs), so the store ops are chosen knowing
/// whether the scene goes on. Depth/stencil: CLEAR when the scene does not force-load it, else LOAD; at the end of
/// the scene DONT_CARE unless SCE_GXM_DEPTH_STENCIL_FORCE_STORE is set (GXM stores depth/stencil only when forced)
/// or the surface was ever read back as a texture / mapped memory (kept conservatively).</item>
/// <item>Colour always LOADs and STOREs: the colour surface is guest memory and the Vita loads it into the tiles at
/// scene start. Multisampled scenes start from DONT_CARE samples (the scene-start copy is drawn into them), resolve
/// inside the render pass and discard their samples at the scene end.</item>
/// </list>
/// Desktop GPUs defer their renderings the same way (so texture uploads and copies can be recorded before them,
/// VulkanRenderer.SceneBreaks.cs) but always store depth/stencil (VITASHARP_SCENE_BREAKS=strict: begun directly, as
/// before). The load and store ops of every scene rendering are counted for the PERF gpu line.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Tile-based GPU: depth/stencil store ops of scene renderings can be DONT_CARE at the scene end.</summary>
    bool TiledRenderings => _core.Caps.Has(GpuQuirk.TileBased);

    struct TilePass
    {
        public ImageView Color, Depth;
        /// <summary>The scene's tile-word attachment (colour attachment 1), default = none.</summary>
        public ImageView Word;
        public Format ColorFormat;
        public AttachmentLoadOp DepthLoad;
        public ClearValue DepthClear;
        public Extent2D Area;
        public bool HasColor;
    }

    CommandBuffer _tileCmd;
    TilePass _tilePass;

    // counters (scene renderings): [0] load, [1] clear, [2] dont care / [0] store, [1] dont care
    readonly long[] _colorLoads = new long[3], _depthLoads = new long[3], _colorStores = new long[2], _depthStores = new long[2];
    long _tileDeferred;

    static int LoadIndex(AttachmentLoadOp op) => op switch { AttachmentLoadOp.Load => 0, AttachmentLoadOp.Clear => 1, _ => 2 };

    void CountOps(bool color, AttachmentLoadOp colorLoad, AttachmentStoreOp colorStore, bool depth, AttachmentLoadOp depthLoad, AttachmentStoreOp depthStore)
    {
        if (color)
        {
            _colorLoads[LoadIndex(colorLoad)]++;
            _colorStores[colorStore == AttachmentStoreOp.Store ? 0 : 1]++;
        }
        if (depth)
        {
            _depthLoads[LoadIndex(depthLoad)]++;
            _depthStores[depthStore == AttachmentStoreOp.Store ? 0 : 1]++;
        }
    }

    /// <summary>"load/store: colour L/C/D a/b/c S/D d/e, depth ..." for the PERF gpu line.</summary>
    string LoadStoreReport() =>
        $"load/store ops{(TiledRenderings ? " (tile-based" : " (desktop")}, {_tileDeferred} deferred single-sampled renderings): colour load {_colorLoads[0]}/clear {_colorLoads[1]}/dont-care {_colorLoads[2]}, store {_colorStores[0]}/dont-care {_colorStores[1]}; " +
        $"depth load {_depthLoads[0]}/clear {_depthLoads[1]}/dont-care {_depthLoads[2]}, store {_depthStores[0]}/dont-care {_depthStores[1]}; {SceneBreakReport()}";

    /// <summary>Opens a single-sampled scene rendering in a secondary command buffer.</summary>
    /// <param name="wordView">the scene's tile-word attachment (colour attachment 1), default = none</param>
    void BeginTiledRendering(ImageView colorView, ImageView wordView, Format colorFormat, AttachmentLoadOp depthLoad, ClearValue depthClear)
    {
        _tilePass = new TilePass
        {
            Color = colorView, Word = wordView, ColorFormat = colorFormat, HasColor = _rt != null, Depth = _depth!.View, DepthLoad = depthLoad, DepthClear = depthClear,
            Area = new Extent2D(_sceneW * SceneScale, _sceneH * SceneScale),
        };
        // merge: the renderings ended since the primary was last touched continue on the same attachments
        if (_chain.Count > 0 && !(_chainScene == _sceneSerial && SameAttachments(in _chainPass, in _tilePass))) FlushChain(false);
        if (_chain.Count > 0) _statMerged++;
        else { _chainStartSerial = _renderingSerial; ResetDrawn(); }
        var cb = AllocSecondary();
        var fmt = stackalloc Format[2] { colorFormat, TileWord1Format };
        var rii = new CommandBufferInheritanceRenderingInfo
        {
            SType = StructureType.CommandBufferInheritanceRenderingInfo,
            ColorAttachmentCount = _tilePass.HasColor ? wordView.Handle != 0 ? 2u : 1u : 0u,
            PColorAttachmentFormats = fmt,
            DepthAttachmentFormat = _core.DepthFormat,
            StencilAttachmentFormat = _core.DepthFormat,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        // render-pass path (VulkanRenderer.RenderPasses.cs): the secondary inherits the compatible pass instead
        var ii = UseRenderPasses
            ? new CommandBufferInheritanceInfo { SType = StructureType.CommandBufferInheritanceInfo, RenderPass = SingleCompatPass(_tilePass.HasColor ? colorFormat : Format.Undefined, wordView.Handle != 0), Subpass = 0 }
            : new CommandBufferInheritanceInfo { SType = StructureType.CommandBufferInheritanceInfo, PNext = &rii };
        var bi = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit,
            PInheritanceInfo = &ii,
        };
        _vk.BeginCommandBuffer(cb, in bi).Ok("vkBeginCommandBuffer(tiled secondary)");
        _tileCmd = cb;
        _boundPipeline = default;
        _tileDeferred++;
    }

    /// <param name="final">the scene ends here: depth/stencil are discarded unless the scene forces the store</param>
    void EndTiledRendering(bool final)
    {
        var cb = _tileCmd;
        _vk.EndCommandBuffer(cb).Ok("vkEndCommandBuffer(tiled secondary)");
        _tileCmd = default;
        _boundPipeline = default;
        if (_chain.Count == 0) { _chainPass = _tilePass; _chainScene = _sceneSerial; }
        _chain.Add(cb);
        // not begun yet: the next rendering may continue it (merge) unless something is recorded into the primary first
        if (final || StrictSceneBreaks) FlushChain(final);
    }

    /// <summary>Ended renderings not yet begun in the primary (same attachments; the first one's load ops).</summary>
    readonly List<CommandBuffer> _chain = new();
    TilePass _chainPass;
    long _chainScene, _chainStartSerial, _statMerged;

    static bool SameAttachments(in TilePass a, in TilePass b) =>
        a.Color.Handle == b.Color.Handle && a.Word.Handle == b.Word.Handle && a.Depth.Handle == b.Depth.Handle && a.ColorFormat == b.ColorFormat
        && a.HasColor == b.HasColor && a.Area.Width == b.Area.Width && a.Area.Height == b.Area.Height;

    /// <summary>Begins the pending renderings in the primary as one rendering executing their secondaries.</summary>
    /// <param name="final">the scene ends with them: depth/stencil are discarded unless the scene forces the store</param>
    void FlushChain(bool final)
    {
        if (_chain.Count == 0) return;
        var p = _chainPass;
        // desktop GPUs store depth/stencil as before (no tile memory to save)
        bool keepDepth = !TiledRenderings || !final || _scene?.Depth is { ForceStore: true } || _depth is { EverRead: true };
        var colors = stackalloc RenderingAttachmentInfo[2];
        colors[0] = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = p.Color, ImageLayout = ImageLayout.General,
            LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
        };
        colors[1] = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = p.Word, ImageLayout = ImageLayout.General,
            LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
        };
        var depthStore = keepDepth ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare;
        var depth = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = p.Depth, ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = p.DepthLoad, StoreOp = depthStore, ClearValue = p.DepthClear,
        };
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            Flags = RenderingFlags.ContentsSecondaryCommandBuffersBit,
            RenderArea = new Rect2D(new Offset2D(0, 0), p.Area),
            LayerCount = 1,
            ColorAttachmentCount = p.HasColor ? p.Word.Handle != 0 ? 2u : 1u : 0u,
            PColorAttachments = colors,
            PDepthAttachment = &depth,
            PStencilAttachment = &depth,
        };
        CountOps(p.HasColor, AttachmentLoadOp.Load, AttachmentStoreOp.Store, true, p.DepthLoad, depthStore);
        var cmd = F.Cmd;
        var cbs = stackalloc CommandBuffer[_chain.Count];
        for (int i = 0; i < _chain.Count; i++) cbs[i] = _chain[i];
        uint n = (uint)_chain.Count;
        _chain.Clear();
        if (UseRenderPasses)
        {
            BeginSingleRenderPass(cmd, in p, depthStore);
            _vk.CmdExecuteCommands(cmd, n, cbs);
            _vk.CmdEndRenderPass(cmd);
        }
        else
        {
            _vk.CmdBeginRendering(cmd, in ri);
            _vk.CmdExecuteCommands(cmd, n, cbs);
            _vk.CmdEndRendering(cmd);
        }
        if (Poison && depthStore == AttachmentStoreOp.DontCare && _depth != null) PoisonDepth(cmd, _depth.Image);
    }

    /// <summary>
    /// VITASHARP_GPU_POISON=1 (debugging, mainly for replay-gpu on a desktop GPU): what Vulkan leaves undefined gets a
    /// loud value instead of whatever the driver keeps. A desktop GPU usually keeps the old content, so a scene that
    /// wrongly relies on it looks right there and only flickers on a tiler (Mali, Adreno) that really drops it. New render
    /// targets the guest never wrote start magenta, and a depth/stencil buffer discarded at a scene's end (DONT_CARE
    /// store) is cleared to depth 0 / stencil 0xFF: a later scene that loads it draws nothing where it depended on it.
    /// </summary>
    static readonly bool Poison = Environment.GetEnvironmentVariable(Core.Util.EnvVars.GpuPoison) == "1";

    void PoisonDepth(CommandBuffer cmd, Image image)
    {
        const ImageAspectFlags aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        var all = PipelineStageFlags.AllCommandsBit;
        var rw = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
        Barrier(image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.TransferDstOptimal, aspect, all, PipelineStageFlags.TransferBit, rw, AccessFlags.TransferWriteBit);
        var clear = new ClearDepthStencilValue(0f, 0xFF);
        var range = new ImageSubresourceRange(aspect, 0, 1, 0, 1);
        _vk.CmdClearDepthStencilImage(cmd, image, ImageLayout.TransferDstOptimal, in clear, 1, in range);
        Barrier(image, ImageLayout.TransferDstOptimal, ImageLayout.DepthStencilAttachmentOptimal, aspect, PipelineStageFlags.TransferBit, all, AccessFlags.TransferWriteBit, rw);
    }
}
