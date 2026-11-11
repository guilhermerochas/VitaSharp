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
        if (_singleInputSet.Handle != 0 && _singleInputSetView.Handle == view.Handle && _singleInputSetFrame == _frameSerial)
        {
            return _singleInputSet;
        }

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
        {
            w[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = i == 2 ? DescriptorType.InputAttachment : DescriptorType.CombinedImageSampler,
                PImageInfo = &info[i],
            };
        }

        _vk.UpdateDescriptorSets(_dev, 3, w, 0, null);
        return set;
    }

    /// <summary>Ends the scene's dynamic rendering and begins a render pass on the surface and depth/stencil (both
    /// loaded and stored) for one draw; returns the command buffer to record it into.</summary>
    CommandBuffer BeginSingleFetchPass(Format format, bool depthReadOnly = false)
    {
        // (a supersampled scene's samples are filtered into the surface when rendering pauses for something else)
        EndRendering(resolve: !_ss);
        // a pending depth clear (no rendering yet in this scene) happens in an empty rendering first
        if (_depthNeedsClear)
        {
            BeginRendering();
            EndRendering(resolve: !_ss);
        }
        // the scene-break bookkeeping (VulkanRenderer.SceneBreaks.cs) sees this pass as a rendering of its own
        _renderingSerial++;
        _statRenderings++;
        var rt = _rt!;
        var depth = _depth!;
        var image = SingleFetchImage();
        var area = SceneArea;
        uint w = area.Width, h = area.Height;
        // the earlier renderings' writes come before this pass's loads, input attachment reads and writes
        Barrier(image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit | AccessFlags.ShaderReadBit,
            AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.InputAttachmentReadBit);
        Barrier(depth.Image, ImageLayout.DepthStencilAttachmentOptimal, depthReadOnly ? ImageLayout.DepthReadOnlyStencilAttachmentOptimal : ImageLayout.DepthStencilAttachmentOptimal,
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit | (depthReadOnly ? PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit : 0),
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit | (depthReadOnly ? AccessFlags.ShaderReadBit : 0));
        var pass = MsaaRenderPass(new MsaaPassKey(format, SampleCountFlags.Count1Bit, AttachmentLoadOp.Load, AttachmentStoreOp.Store,
            AttachmentLoadOp.Load, AttachmentStoreOp.Store, false, DepthReadOnly: depthReadOnly));
        var views = stackalloc ImageView[2] { SingleFetchView(), depth.View };
        var fci = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = pass,
            AttachmentCount = 2,
            PAttachments = views,
            Width = w,
            Height = h,
            Layers = 1,
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
        DevStampOp(cmd, depthReadOnly ? "fetch-pass-depthro" : "fetch-pass");
        _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.Inline);
        _boundPipeline = default;
        rt.Dirty = true;
        rt.Version++;
        rt.LastTouchedFrame = FrameCount;
        depth.Version++;
        if (_statSingleFetchDraws++ == 0)
        {
            Log.Info($"[gpu] programmable blending in one-sample scenes: small draws render in a render pass that reads the surface as an input attachment (render target 0x{rt.Address:X8})");
        }

        if (_ss && _singleFetchSsLogged.Add(rt.Address))
        {
            Log.Info($"[gpu] programmable blending in supersampled scene 0x{rt.Address:X8}: small draws render in a render pass that reads the {w}x{h} sample image as an input attachment");
        }

        _fetchPassOpen = true;
        _fetchPassDepthRo = depthReadOnly;
        (_fetchPassRt, _fetchPassDepth, _fetchPassFormat, _fetchPassScene, _fetchPassImage) = (rt, depth, format, _sceneSerial, image);
        return cmd;
    }

    readonly HashSet<uint> _singleFetchSsLogged = new();

    /// <summary>
    /// On tile-based GPUs (VITASHARP_FETCH_PASS_MERGE=0/1 overrides) the fetch pass stays open after its draw: the next programmable-blending draw
    /// of the same scene renders in it too, instead of a store and a load of the whole surface and depth/stencil per
    /// draw (Uncharted's title had about 18 such draws a frame: 45 renderings per frame instead of 8, most of its 28 ms
    /// of GPU time on a Mali-G57). Rasterization-order attachment access (VK_EXT/ARM_rasterization_order_attachment_access)
    /// orders the draws' reads; without it a by-region barrier goes between them (so a desktop GPU runs this path too).
    /// Any other recording ends it first (<see cref="Cmd"/>). On the Mali-G57 it brings Uncharted's title from 27 to
    /// 31-35 fps. The title's dark frames once blamed on it (frames without the lighting layer, more of them at higher
    /// frame rates) were <see cref="OverwrittenSinceRendered"/> taking game writes to a page the lighting buffer shares
    /// as writes to the buffer: 0 since that was fixed, with the merge on (in-game flicker check on the phone).
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
    static readonly string? FetchPassMergeSetting = Environment.GetEnvironmentVariable(Core.Util.EnvVars.FetchPassMerge)?.Trim();
    bool FetchPassMerge => FetchPassMergeSetting switch { "1" => true, "0" => false, _ => TiledRenderings };


    /// <summary>The next draw renders in the open fetch pass: the bookkeeping <see cref="BeginSingleFetchPass"/> does.</summary>
    void ContinueSingleFetchPass()
    {
        _statFetchPassMerged++;
        var rt = _fetchPassRt!;
        rt.Dirty = true;
        rt.Version++;
        rt.LastTouchedFrame = FrameCount;
        _fetchPassDepth!.Version++;
    }

    /// <summary>The open fetch pass can take the next programmable-blending draw (same surface, depth and format).</summary>
    bool FetchPassContinues(Format format) =>
        _fetchPassOpen && _fetchPassScene == _sceneSerial && _fetchPassRt == _rt && _fetchPassDepth == _depth && _fetchPassFormat == format && !_hoisting;

    /// <summary>The open fetch pass takes a draw that writes depth (<paramref name="writesDepth"/>) or samples the
    /// scene's depth in place (<paramref name="feedback"/>): a read-only depth pass takes no depth writes, a writable one
    /// no in-place depth reads.</summary>
    bool FetchPassContinues(Format format, bool writesDepth, bool feedback) =>
        FetchPassContinues(format) && !(writesDepth && _fetchPassDepthRo) && !(feedback && !_fetchPassDepthRo);

    /// <summary>The open fetch pass's depth/stencil is in DEPTH_READ_ONLY_STENCIL_ATTACHMENT_OPTIMAL: its draws may
    /// sample the depth while they test against it and write stencil.</summary>
    bool _fetchPassDepthRo;

    static readonly string? FetchLargeSetting = Environment.GetEnvironmentVariable(EnvVars.FetchLarge)?.Trim();
    static readonly string? FetchPassJoinSetting = Environment.GetEnvironmentVariable(EnvVars.FetchPassJoin)?.Trim();
    static readonly string? DepthFeedbackSetting = Environment.GetEnvironmentVariable(EnvVars.DepthFeedback)?.Trim();
    static bool Switch(string? setting, bool dflt) => setting switch { "1" => true, "0" => false, _ => dflt };

    /// <summary>
    /// With rasterization-order attachment access every fragment of a draw reads what earlier fragments of the same
    /// draw wrote, as on hardware, so a programmable-blending draw of any size renders in the fetch pass, whole. Without
    /// it, large draws read a copy of the surface taken before the draw: a mid-scene break and a full-surface copy each
    /// (Uncharted's gameplay lighting pass had two per frame).
    /// </summary>
    bool FetchLarge => _core.RasterizationOrderColor && Switch(FetchLargeSetting, TiledRenderings);

    /// <summary>
    /// Draws that do not read the colour keep rendering in an open fetch pass of their scene (pipelines made for its
    /// render pass) instead of ending it, so a scene that mixes programmable-blending and ordinary draws stays one pass:
    /// Uncharted's lighting pass alternates stencil-marking draws, light volumes and ordinary geometry.
    /// </summary>
    bool FetchPassJoin => Switch(FetchPassJoinSetting, TiledRenderings);

    /// <summary>
    /// Draws that sample the depth buffer they test against (deferred light volumes reading the scene depth) read the
    /// depth image itself in a fetch pass whose depth is read-only (DEPTH_READ_ONLY_STENCIL_ATTACHMENT_OPTIMAL: no depth
    /// writes, stencil still written), instead of a copy of the depth that must end the pass and is taken again for
    /// every draw (Uncharted: six depth copies and pass restarts per frame). The image holds exactly what the copy would.
    /// </summary>
    bool DepthFeedback => Switch(DepthFeedbackSetting, TiledRenderings);

    /// <summary>Set before a draw's textures are resolved: the draw can render in a fetch pass with read-only depth
    /// (one-sample scene at 1x, no depth write), so a texture of the scene's own depth may be the depth image itself.</summary>
    bool _feedbackAllowed;
    /// <summary>A texture of the current draw is the scene's depth image, read in place.</summary>
    bool _feedbackUsed;
    long _statDepthFeedback;

    /// <summary>The depth image of <paramref name="dt"/> sampled in place (depth aspect, depth in every channel), or
    /// null when this draw cannot (see <see cref="_feedbackAllowed"/>).</summary>
    ImageView? DepthFeedbackView(DepthTarget dt, uint w, uint h)
    {
        if (!_feedbackAllowed || dt != _depth || dt.Samples != SampleCountFlags.Count1Bit || !dt.Sampleable || w != dt.Width || h != dt.Height)
        {
            return null;
        }

        if (dt.FeedbackView.Handle == 0)
        {
            dt.FeedbackView = CreateView(dt.Image, _core.DepthFormat, ImageAspectFlags.DepthBit, 1, 1, false,
                new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R));
        }

        if (_statDepthFeedback++ == 0)
        {
            Log.Info($"[gpu] depth 0x{dt.Address:X8} {dt.Width}x{dt.Height} sampled by draws that test against it: read in place in a read-only depth fetch pass");
        }

        _feedbackUsed = true;
        return dt.FeedbackView;
    }

    void EndSingleFetchPass()
    {
        _fetchPassOpen = false;
        var cmd = F.Cmd;
        _vk.CmdEndRenderPass(cmd);
        _boundPipeline = default;
        var depthLayout = _fetchPassDepthRo ? ImageLayout.DepthReadOnlyStencilAttachmentOptimal : ImageLayout.DepthStencilAttachmentOptimal;
        _fetchPassDepthRo = false;
        // the samples of a supersampled scene now hold rendering that is not filtered into the surface yet
        if (_ss)
        {
            _ssUnresolved = true;
        }
        // the next rendering (or a copy / sampling of the surface) comes after this pass's writes
        Barrier(_fetchPassImage, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit,
            AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.InputAttachmentReadBit | AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        Barrier(_fetchPassDepth!.Image, depthLayout, ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            PipelineStageFlags.LateFragmentTestsBit | (depthLayout != ImageLayout.DepthStencilAttachmentOptimal ? PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit : 0),
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
    }
}
