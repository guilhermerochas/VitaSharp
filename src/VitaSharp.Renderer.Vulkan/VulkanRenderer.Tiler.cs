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
/// Desktop GPUs keep beginning renderings directly in the primary command buffer (no behaviour change). The load and
/// store ops of every scene rendering are counted for the PERF gpu line.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Single-sampled renderings are deferred into secondaries so their store ops can be DONT_CARE.</summary>
    bool TiledRenderings => _core.Caps.Has(GpuQuirk.TileBased);

    struct TilePass
    {
        public ImageView Color, Depth;
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
        $"load/store ops{(TiledRenderings ? $" (tile-based, {_tileDeferred} deferred renderings)" : "")}: colour load {_colorLoads[0]}/clear {_colorLoads[1]}/dont-care {_colorLoads[2]}, store {_colorStores[0]}/dont-care {_colorStores[1]}; " +
        $"depth load {_depthLoads[0]}/clear {_depthLoads[1]}/dont-care {_depthLoads[2]}, store {_depthStores[0]}/dont-care {_depthStores[1]}";

    /// <summary>Opens a single-sampled scene rendering in a secondary command buffer (tile-based GPUs).</summary>
    void BeginTiledRendering(ImageView colorView, Format colorFormat, AttachmentLoadOp depthLoad, ClearValue depthClear)
    {
        _tilePass = new TilePass
        {
            Color = colorView, ColorFormat = colorFormat, HasColor = _rt != null, Depth = _depth!.View, DepthLoad = depthLoad, DepthClear = depthClear,
            Area = new Extent2D(_sceneW * SceneScale, _sceneH * SceneScale),
        };
        var cb = AllocSecondary();
        var fmt = colorFormat;
        var rii = new CommandBufferInheritanceRenderingInfo
        {
            SType = StructureType.CommandBufferInheritanceRenderingInfo,
            ColorAttachmentCount = _tilePass.HasColor ? 1u : 0u,
            PColorAttachmentFormats = &fmt,
            DepthAttachmentFormat = _core.DepthFormat,
            StencilAttachmentFormat = _core.DepthFormat,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var ii = new CommandBufferInheritanceInfo { SType = StructureType.CommandBufferInheritanceInfo, PNext = &rii };
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
        var p = _tilePass;
        bool keepDepth = !final || _scene?.Depth is { ForceStore: true } || _depth is { EverRead: true };
        var color = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = p.Color, ImageLayout = ImageLayout.General,
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
            ColorAttachmentCount = p.HasColor ? 1u : 0u,
            PColorAttachments = &color,
            PDepthAttachment = &depth,
            PStencilAttachment = &depth,
        };
        CountOps(p.HasColor, AttachmentLoadOp.Load, AttachmentStoreOp.Store, true, p.DepthLoad, depthStore);
        var cmd = F.Cmd;
        _vk.CmdBeginRendering(cmd, in ri);
        _vk.CmdExecuteCommands(cmd, 1, in cb);
        _vk.CmdEndRendering(cmd);
    }
}
