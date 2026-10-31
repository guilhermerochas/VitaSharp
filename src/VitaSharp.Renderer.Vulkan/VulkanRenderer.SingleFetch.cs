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
/// Every one-sample image a scene renders into works this way: the surface, the 2x sample image of a supersampled
/// scene (VITASHARP_MSAA=ss, or a sample count the GPU lacks) and the 2x surface of MSAA without downscale, also on
/// tile-based GPUs. Without it (copy taken before the whole draw) Soul Sacrifice's fire and smoke, strips of particle
/// quads in one draw, lost almost all their smoke in supersampled scenes: each pixel kept only the last, mostly
/// transparent, quad blended over the colour from before the draw.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    long _statSingleFetchDraws;
    DescriptorSet _singleInputSet;
    ImageView _singleInputSetView;
    long _singleInputSetFrame = -1;

    /// <summary>The current scene can render a programmable-blending draw in a one-sample render pass: a one-sample
    /// colour image (surface or supersample image) without the tile-word attachment.</summary>
    bool SingleFetchPossible() => _rt != null && _depth != null && !_ms && !_tw1Active;

    /// <summary>The image view the scene renders with: the supersample image or the surface (sRGB with surface gamma).</summary>
    ImageView SingleFetchView() => _ss ? SupersampleView(_rt!) : _rtGamma ? _rt!.SrgbView : _rt!.View;

    /// <summary>The image the scene renders into (see <see cref="SingleFetchView"/>).</summary>
    Image SingleFetchImage() => _ss ? GetSupersampleImage(_rt!).Image : _rt!.Image;

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
        // (a supersampled scene's samples are filtered into the surface when rendering pauses for something else)
        EndRendering(resolve: !_ss);
        // a pending depth clear (no rendering yet in this scene) happens in an empty rendering first
        if (_depthNeedsClear) { BeginRendering(); EndRendering(resolve: !_ss); }
        // the scene-break bookkeeping (VulkanRenderer.SceneBreaks.cs) sees this pass as a rendering of its own
        _renderingSerial++;
        _statRenderings++;
        var rt = _rt!;
        var depth = _depth!;
        var image = SingleFetchImage();
        uint w = _sceneW * SceneScale, h = _sceneH * SceneScale;
        // the earlier renderings' writes come before this pass's loads, input attachment reads and writes
        Barrier(image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
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
            Width = w, Height = h, Layers = 1,
        };
        _vk.CreateFramebuffer(_dev, in fci, null, out var fb).Ok("single-fetch framebuffer");
        // the views may die with their surface: the framebuffer lives only as long as this frame's commands
        DestroyAfterGpu(() => _vk.DestroyFramebuffer(_dev, fb, null));
        var rbi = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = fb,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h)),
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
        if (_ss && _singleFetchSsLogged.Add(rt.Address))
            Log.Info($"[gpu] programmable blending in supersampled scene 0x{rt.Address:X8}: small draws render in a render pass that reads the {w}x{h} sample image as an input attachment");
        _fetchPassOpen = true; _fetchPassDraws = 0;
        (_fetchPassRt, _fetchPassDepth, _fetchPassFormat, _fetchPassScene, _fetchPassImage) = (rt, depth, format, _sceneSerial, image);
        return cmd;
    }

    readonly HashSet<uint> _singleFetchSsLogged = new();

    /// <summary>
    /// The fetch pass stays open after its draw when the GPU orders input attachment reads with the rasterization
    /// (VK_EXT/ARM_rasterization_order_attachment_access): the next programmable-blending draw of the same scene renders
    /// in it too, instead of a store and a load of the whole surface and depth/stencil per draw (Uncharted's title had
    /// about 18 such draws a frame: 45 renderings per frame instead of 8, most of its 28 ms of GPU time on a Mali-G57).
    /// Any other recording ends it first (<see cref="Cmd"/>). Off by default (VITASHARP_FETCH_PASS_MERGE=1 turns it on): on
    /// the Mali-G57 it brought Uncharted's title from 25 to 36 fps, but about one frame in seven came out 15% darker
    /// (screenrecord + signalstats), a cause not found yet.
    /// </summary>
    bool _fetchPassOpen;
    RenderTarget? _fetchPassRt;
    DepthTarget? _fetchPassDepth;
    Format _fetchPassFormat;
    long _fetchPassScene;
    /// <summary>The image the open pass renders into: a pass ended lazily (after the scene moved on) still makes its
    /// own surface and depth visible, not the current scene's.</summary>
    Image _fetchPassImage;
    long _statFetchPassMerged;
    static readonly bool FetchPassMerge = Environment.GetEnvironmentVariable(Core.Util.EnvVars.FetchPassMerge) == "1";

    /// <summary>The next draw renders in the open fetch pass: the bookkeeping <see cref="BeginSingleFetchPass"/> does.</summary>
    void ContinueSingleFetchPass()
    {
        _statFetchPassMerged++; _fetchPassDraws++;
        var rt = _fetchPassRt!;
        rt.Dirty = true; rt.Version++; rt.LastTouchedFrame = FrameCount;
        _fetchPassDepth!.Version++;
    }

    /// <summary>The open fetch pass can take the next programmable-blending draw (same surface, depth and format).</summary>
    bool FetchPassContinues(Format format) =>
        _fetchPassOpen && _fetchPassScene == _sceneSerial && _fetchPassRt == _rt && _fetchPassDepth == _depth && _fetchPassFormat == format && !_hoisting && _fetchPassDraws < XFetchMax;
    int _fetchPassDraws;
    static readonly int XFetchMax = int.TryParse(Environment.GetEnvironmentVariable("VITASHARP_X_FETCH_MAX"), out var xm) ? xm : int.MaxValue;

    void EndSingleFetchPass()
    {
        _fetchPassOpen = false;
        var cmd = F.Cmd;
        _vk.CmdEndRenderPass(cmd);
        _boundPipeline = default;
        // the samples of a supersampled scene now hold rendering that is not filtered into the surface yet
        if (_ss) _ssUnresolved = true;
        // the next rendering (or a copy / sampling of the surface) comes after this pass's writes
        Barrier(_fetchPassImage, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit,
            AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.InputAttachmentReadBit | AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        Barrier(_fetchPassDepth!.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
    }
}
