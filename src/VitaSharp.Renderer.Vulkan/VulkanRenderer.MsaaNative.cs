using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// GXM MSAA on downscale surfaces (SCE_GXM_MULTISAMPLE_2X/4X render target, SCE_GXM_COLOR_SURFACE_SCALE_MSAA_DOWNSCALE
/// colour surface) emulated with real Vulkan multisampling: the scene renders into a 2- or 4-sample colour image
/// (and a multisampled depth/stencil image) and resolves into the 1x surface with an in-pass resolve attachment
/// (average; SAMPLE_ZERO through integer views in scenes with raw integer outputs, which must keep their bits). On tile-based GPUs
/// this stays on chip; on desktop it costs the samples instead of the 4x fill of the old 2x2 supersampling, which
/// remains available as VITASHARP_MSAA=ss (and is the fallback for a format/sample count the device cannot
/// multisample). VITASHARP_MSAA=off renders such scenes with one sample, as Vita3K does.
/// <para>
/// Hardware behaviour reproduced:
/// <list type="bullet">
/// <item>scene start: the tile loads the surface into every sample. The first rendering of the scene starts with
/// undefined samples (DONT_CARE) and draws a full-screen copy of the scene-start copy of the surface (the copy, not
/// the surface: the surface is this rendering's resolve target);</item>
/// <item>the renderer has to interrupt a scene for copies, texture syncs and integer-view switches. Each rendering is
/// recorded into a secondary command buffer and only begun in the primary when it ends, so its store operations are
/// chosen knowing what follows: STORE when the scene continues (the next rendering loads the samples), DONT_CARE when
/// the scene ends here. Depth/stencil are kept past the scene only with SCE_GXM_DEPTH_STENCIL_FORCE_STORE (GXM
/// stores depth/stencil only when forced);</item>
/// <item>everything that reads the surface (textures, mid-scene flushes, writeback, the CDRAM mirror, the display)
/// ends the rendering first, which resolves it. Renderings interrupted only for a programmable-blending copy or an
/// integer-view switch skip the resolve (the next one resolves; a pending resolve runs as an empty rendering when
/// something reads the surface first);</item>
/// <item>programmable blending reads a multisampled copy of the samples per sample (sample-rate shading through
/// gl_SampleID), so each sample blends with its own value; without sampleRateShading they read sample 0.</item>
/// </list>
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    enum MsaaEmulation { Native, Supersample, Off }

    static readonly MsaaEmulation MsaaSetting = (Environment.GetEnvironmentVariable("VITASHARP_MSAA") ?? "").Trim().ToLowerInvariant() switch
    {
        "ss" or "supersample" or "supersampling" => MsaaEmulation.Supersample,
        "off" or "none" or "1x" or "0" => MsaaEmulation.Off,
        _ => MsaaEmulation.Native,
    };

    /// <summary>
    /// Programmable blending in multisampled scenes reads the colour attachment as an input attachment (render pass
    /// with a subpass self-dependency, a by-region barrier before each such draw), unless VITASHARP_MSAA_FETCH=copy:
    /// then, like raw integer renderings always, the samples are copied for every such draw.
    /// </summary>
    static readonly bool MsaaInputFetch = !string.Equals(Environment.GetEnvironmentVariable("VITASHARP_MSAA_FETCH")?.Trim(), "copy", StringComparison.OrdinalIgnoreCase);

    sealed class MsaaImage
    {
        public Image Image;
        public ImageMemoryPool.Allocation Mem;
        public SampleCountFlags Samples;
        public uint Width, Height;
        public readonly Dictionary<Format, ImageView> Views = new();
        /// <summary>Framebuffers per (colour view, depth view, resolve view); they live as long as the image (the
        /// surface views die with it, depth targets are never destroyed before shutdown).</summary>
        public readonly Dictionary<(ulong, ulong, ulong), Framebuffer> Framebuffers = new();
    }

    /// <summary>Attachments of the open multisampled rendering; begun in the primary command buffer when it ends.</summary>
    struct MsaaPass
    {
        public MsaaImage Image;
        public Image DepthImage;
        public Format Format;
        public ImageView Color, Depth;
        public bool Raw;
        public AttachmentLoadOp ColorLoad, DepthLoad;
        public ClearValue DepthClear;
        public Extent2D Area;
    }

    // =================================================================== render passes

    readonly record struct MsaaPassKey(Format Format, SampleCountFlags Samples, AttachmentLoadOp ColorLoad, AttachmentStoreOp ColorStore,
        AttachmentLoadOp DepthLoad, AttachmentStoreOp DepthStore, bool Resolve);

    /// <summary>Render passes of multisampled renderings (shared with the pipeline prewarm thread, hence the lock).</summary>
    readonly Dictionary<MsaaPassKey, RenderPass> _msRenderPasses = new();

    /// <summary>The render pass multisampled pipelines and secondary command buffers are created against: one
    /// subpass, so any pass of the same format/sample count is compatible whatever its load/store ops and resolve.</summary>
    RenderPass MsaaCompatPass(Format format, SampleCountFlags samples) =>
        MsaaRenderPass(new MsaaPassKey(format, samples, AttachmentLoadOp.Load, AttachmentStoreOp.Store, AttachmentLoadOp.Load, AttachmentStoreOp.Store, false));

    /// <summary>
    /// One subpass: attachment 0 = the samples (colour AND input attachment, GENERAL layout, self-dependency for
    /// programmable blending), 1 = multisampled depth/stencil, 2 = optional resolve into the surface.
    /// </summary>
    RenderPass MsaaRenderPass(MsaaPassKey k)
    {
        lock (_msRenderPasses)
        {
            if (_msRenderPasses.TryGetValue(k, out var rp)) return rp;
            var att = stackalloc AttachmentDescription[3];
            att[0] = new AttachmentDescription(0, k.Format, k.Samples, k.ColorLoad, k.ColorStore, AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare,
                ImageLayout.General, ImageLayout.General);
            att[1] = new AttachmentDescription(0, _core.DepthFormat, k.Samples, k.DepthLoad, k.DepthStore, k.DepthLoad, k.DepthStore,
                ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilAttachmentOptimal);
            att[2] = new AttachmentDescription(0, k.Format, SampleCountFlags.Count1Bit, AttachmentLoadOp.DontCare, AttachmentStoreOp.Store,
                AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.General, ImageLayout.General);
            var color = new AttachmentReference(0, ImageLayout.General);
            var depth = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
            var resolve = new AttachmentReference(2, ImageLayout.General);
            var sub = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                InputAttachmentCount = 1, PInputAttachments = &color,
                ColorAttachmentCount = 1, PColorAttachments = &color,
                PResolveAttachments = k.Resolve ? &resolve : null,
                PDepthStencilAttachment = &depth,
            };
            // programmable blending: earlier fragments' colour writes become visible to input attachment reads of
            // later draws (by region: each fragment reads only its own pixel)
            var self = new SubpassDependency(0, 0, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit,
                AccessFlags.ColorAttachmentWriteBit, AccessFlags.InputAttachmentReadBit, DependencyFlags.ByRegionBit);
            var ci = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = k.Resolve ? 3u : 2u, PAttachments = att,
                SubpassCount = 1, PSubpasses = &sub,
                DependencyCount = 1, PDependencies = &self,
            };
            _vk.CreateRenderPass(_dev, in ci, null, out rp).Ok("MSAA render pass");
            return _msRenderPasses[k] = rp;
        }
    }

    Framebuffer MsaaFramebuffer(MsaaImage m, RenderPass pass, ImageView color, ImageView depth, ImageView resolve)
    {
        var key = (color.Handle, depth.Handle, resolve.Handle);
        if (m.Framebuffers.TryGetValue(key, out var fb)) return fb;
        var views = stackalloc ImageView[3] { color, depth, resolve };
        var ci = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = pass,
            AttachmentCount = resolve.Handle != 0 ? 3u : 2u,
            PAttachments = views,
            Width = m.Width, Height = m.Height, Layers = 1,
        };
        _vk.CreateFramebuffer(_dev, in ci, null, out fb).Ok("MSAA framebuffer");
        return m.Framebuffers[key] = fb;
    }

    /// <summary>The samples hold rendering not resolved into the surface yet (renderings interrupted only to copy the
    /// samples for programmable blending, or to switch to/from an integer view, skip the resolve).</summary>
    bool _msUnresolved;
    /// <summary>The scene wrote raw integer bits: its resolves go through the integer views with SAMPLE_ZERO, since
    /// averaging (in any float view) would canonicalise NaN payloads and flush denormals in the packed data.</summary>
    bool _msSceneRaw;
    long _statMsResolves;

    readonly Dictionary<RenderTarget, MsaaImage> _msImages = new();
    /// <summary>The current scene renders with real multisampling.</summary>
    bool _ms;
    SampleCountFlags _msSamples = SampleCountFlags.Count1Bit;
    /// <summary>The next rendering is the scene's first: its samples are loaded from the surface.</summary>
    bool _msNeedsLoad;
    CommandBuffer _msCmd;
    MsaaPass _msPass;
    readonly HashSet<(uint, SampleCountFlags)> _msLogged = new();
    long _statMsScenes, _statMsPasses, _statMsDiscards, _statMsFetchCopies;

    /// <summary>Rasterization samples of the current scene's pipelines.</summary>
    SampleCountFlags SceneSamples => _ms ? _msSamples : SampleCountFlags.Count1Bit;

    /// <summary>SceGxmMultisampleMode (1 = 2X, 2 = 4X) to a sample count.</summary>
    static SampleCountFlags GxmSamples(uint mode) => mode == 1 ? SampleCountFlags.Count2Bit : SampleCountFlags.Count4Bit;

    readonly Dictionary<(Format, SampleCountFlags), bool> _msFormatOk = new();

    bool SupportsNativeMsaa(Format f, SampleCountFlags s)
    {
        if (_msFormatOk.TryGetValue((f, s), out var ok)) return ok;
        var lim = _core.Properties.Limits;
        var why = new List<string>();
        if ((lim.FramebufferColorSampleCounts & s) == 0) why.Add("framebufferColorSampleCounts");
        if ((lim.FramebufferDepthSampleCounts & s) == 0 || (lim.FramebufferStencilSampleCounts & s) == 0) why.Add("framebufferDepth/StencilSampleCounts");
        if ((lim.SampledImageColorSampleCounts & s) == 0) why.Add("sampledImageColorSampleCounts");
        if ((_core.ImageSampleCounts(f, MsaaImageUsage) & s) == 0) why.Add($"{f} attachment");
        if ((_core.ImageSampleCounts(f, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, 0) & s) == 0) why.Add($"{f} sampled");
        if ((_core.ImageSampleCounts(_core.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit, 0) & s) == 0) why.Add($"{_core.DepthFormat} attachment");
        ok = why.Count == 0;
        if (!ok) Log.Warn($"[gpu] {s} multisampling of {f} unsupported on {_core.DeviceName} ({string.Join(", ", why)}): falling back to supersampling");
        return _msFormatOk[(f, s)] = ok;
    }

    MsaaImage GetMsaaImage(RenderTarget rt, SampleCountFlags samples)
    {
        if (_msImages.TryGetValue(rt, out var m))
        {
            if (m.Samples == samples) return m;
            DestroyMsaaLater(rt);
        }
        m = new MsaaImage { Samples = samples, Width = rt.Width, Height = rt.Height };
        ImageView view;
        (m.Image, view, m.Mem) = CreateImage(rt.Width, rt.Height, rt.Format, MsaaImageUsage, ImageAspectFlags.ColorBit, mutable: true, samples: samples);
        m.Views[rt.Format] = view;
        _msImages[rt] = m;
        Barrier(m.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.ColorAttachmentOutputBit, 0, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        return m;
    }

    ImageView MsaaView(MsaaImage m, Format format)
    {
        if (m.Views.TryGetValue(format, out var v)) return v;
        return m.Views[format] = CreateView(m.Image, format, ImageAspectFlags.ColorBit, 1, 1, false, default);
    }

    void DestroyMsaaLater(RenderTarget rt)
    {
        if (!_msImages.Remove(rt, out var m)) return;
        F.Deferred.Add(() => DestroyMsaaImage(m));
    }

    void DestroyMsaaImage(MsaaImage m)
    {
        foreach (var fb in m.Framebuffers.Values) _vk.DestroyFramebuffer(_dev, fb, null);
        foreach (var v in m.Views.Values) _vk.DestroyImageView(_dev, v, null);
        _vk.DestroyImage(_dev, m.Image, null);
        _imagePool.Free(m.Mem);
    }

    const ImageUsageFlags MsaaImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.InputAttachmentBit | ImageUsageFlags.TransferSrcBit;

    /// <summary>Scene start: decides how an MSAA scene is emulated (real multisampling, supersampling or one sample).</summary>
    void ChooseMsaaPath(BeginSceneCommand b)
    {
        _ms = _ss = _msaaNoDownscale = false;
        if (_rt == null || b.Multisample == 0) return;
        if (!b.Color!.Downscale)
        {
            // the surface stores the samples (see VulkanRenderer.Msaa.cs)
            _msaaNoDownscale = MsaaSetting != MsaaEmulation.Off;
            if (_msaaNoDownscale && _msaaNoDownscaleLogged.Add(_rt.Address)) Log.Info($"[gpu] render target 0x{_rt.Address:X8} {b.Width}x{b.Height} MSAA {(b.Multisample == 2 ? "4X" : "2X")} without downscale: rendering at 2x into the {_rt.Width}x{_rt.Height} sample surface");
            return;
        }
        var samples = GxmSamples(b.Multisample);
        if (MsaaSetting == MsaaEmulation.Native && SupportsNativeMsaa(_rt.Format, samples))
        {
            _ms = true;
            _msSamples = samples;
            _msNeedsLoad = true;
            _msUnresolved = _msSceneRaw = false;
            _statMsScenes++;
            EnsureSceneStartCopy(_rt);
            if (_msLogged.Add((_rt.Address, samples)))
                Log.Info($"[gpu] render target 0x{_rt.Address:X8} {_rt.Width}x{_rt.Height} fmt 0x{_rt.ColorFormat:X8}: MSAA downscale scene, rendering with {(samples == SampleCountFlags.Count4Bit ? 4 : 2)} samples and resolving into the surface");
        }
        else if (MsaaSetting != MsaaEmulation.Off && SupportsSupersample(_rt.Format)) _ss = true;
    }

    // =================================================================== renderings

    CommandBuffer AllocSecondary()
    {
        var f = F;
        if (f.SecondaryIndex < f.Secondaries.Count) return f.Secondaries[f.SecondaryIndex++];
        var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = f.Pool, Level = CommandBufferLevel.Secondary, CommandBufferCount = 1 };
        _vk.AllocateCommandBuffers(_dev, in ai, out CommandBuffer cb).Ok("vkAllocateCommandBuffers(secondary)");
        f.Secondaries.Add(cb);
        f.SecondaryIndex++;
        return cb;
    }

    void BeginMsaaRendering()
    {
        var rt = _rt!;
        var m = GetMsaaImage(rt, _msSamples);
        var format = _renderRaw ? _rawFormat : _rtAttachFormat;
        var bg = _scene?.Depth;
        _msPass = new MsaaPass
        {
            Image = m,
            DepthImage = _depth!.Image,
            Format = format,
            Color = MsaaView(m, format),
            Raw = _renderRaw,
            // the scene's first rendering starts from undefined samples and draws the scene-start content into them
            ColorLoad = _msNeedsLoad ? AttachmentLoadOp.DontCare : AttachmentLoadOp.Load,
            Depth = _depth.View,
            DepthLoad = _depthNeedsClear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
            DepthClear = new ClearValue(depthStencil: new ClearDepthStencilValue(Math.Clamp(bg?.BackgroundDepth ?? 1f, 0f, 1f), bg?.BackgroundStencil ?? 0)),
            Area = new Extent2D(_sceneW, _sceneH),
        };
        _depthNeedsClear = false;
        if (_renderRaw) _msSceneRaw = true;

        // the draws go into a secondary command buffer that inherits a render pass compatible with the one begun
        // when the rendering ends (load/store ops and the resolve attachment do not affect compatibility)
        var cb = AllocSecondary();
        var ii = new CommandBufferInheritanceInfo { SType = StructureType.CommandBufferInheritanceInfo, RenderPass = MsaaCompatPass(format, _msSamples), Subpass = 0 };
        var bi = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit,
            PInheritanceInfo = &ii,
        };
        _vk.BeginCommandBuffer(cb, in bi).Ok("vkBeginCommandBuffer(secondary)");
        _msCmd = cb;
        _boundPipeline = default;
        _rendering = true;
        _statMsPasses++;
        rt.Dirty = true; rt.Version++;
        _depth.Version++;
        if (_msNeedsLoad)
        {
            _msNeedsLoad = false;
            RecordMsaaLoad(format);
        }
    }

    /// <param name="final">the scene ends with this rendering: its samples (and transient depth) are not needed again</param>
    /// <param name="resolve">false: the scene goes on and nothing reads the surface before its next rendering, which
    /// resolves (programmable-blending copies, integer-view switches)</param>
    void EndMsaaRendering(bool final, bool resolve)
    {
        var cb = _msCmd;
        _vk.EndCommandBuffer(cb).Ok("vkEndCommandBuffer(secondary)");
        _msCmd = default;
        _boundPipeline = default;
        var p = _msPass;
        bool keepDepth = !final || _scene?.Depth is { ForceStore: true };
        if (final) _statMsDiscards++;
        resolve |= final;
        // float renderings of scenes without raw outputs resolve in the render pass (average); scenes with raw
        // integer outputs resolve afterwards through the integer views (SAMPLE_ZERO, exact bits)
        bool inline = resolve && !_msSceneRaw;
        bool after = resolve && !inline;
        _msUnresolved = !resolve;
        // the previous rendering's sample and depth writes come before this one's loads (render pass instances are
        // not ordered against each other without a dependency)
        Barrier(p.Image.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.InputAttachmentReadBit);
        Barrier(p.DepthImage, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        ImageView resolveView = default;
        if (inline)
        {
            ResolveTargetBarrier();
            resolveView = _rtGamma ? _rt!.SrgbView : _rt!.View;
            _statMsResolves++;
        }
        var pass = MsaaRenderPass(new MsaaPassKey(p.Format, _msSamples, p.ColorLoad,
            final && !after ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
            p.DepthLoad, keepDepth ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare, inline));
        var fb = MsaaFramebuffer(p.Image, pass, p.Color, p.Depth, resolveView);
        var clears = stackalloc ClearValue[3] { default, p.DepthClear, default };
        var rbi = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = fb,
            RenderArea = new Rect2D(new Offset2D(0, 0), p.Area),
            ClearValueCount = 2,
            PClearValues = clears,
        };
        var cmd = F.Cmd;
        _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.SecondaryCommandBuffers);
        _vk.CmdExecuteCommands(cmd, 1, in cb);
        _vk.CmdEndRenderPass(cmd);
        if (after) ResolveMsaa(final);
    }

    /// <summary>Resolve settings of a rendering: average into the surface view the scene renders with, or (scenes with
    /// raw integer outputs) sample 0 through the integer views, which keeps the bits exact.</summary>
    void SetResolve(ref RenderingAttachmentInfo color, bool raw)
    {
        color.ResolveMode = raw ? ResolveModeFlags.SampleZeroBit : ResolveModeFlags.AverageBit;
        color.ResolveImageView = raw ? RawAttachmentView(_rt!) : _rtGamma ? _rt!.SrgbView : _rt!.View;
        color.ResolveImageLayout = ImageLayout.General;
        _statMsResolves++;
    }

    /// <summary>The resolve overwrites the surface: earlier reads of it (textures, copies, writeback) must be done.</summary>
    void ResolveTargetBarrier() =>
        Barrier(_rt!.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.ColorAttachmentOutputBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit,
            AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);

    /// <summary>
    /// Resolves the samples into the surface outside a rendering of draws: an empty rendering whose only work is
    /// the resolve (the samples are kept unless the scene ends here).
    /// </summary>
    void ResolveMsaa(bool final)
    {
        _msUnresolved = false;
        var rt = _rt!;
        var m = GetMsaaImage(rt, _msSamples);
        var color = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = MsaaView(m, _msSceneRaw ? _rawFormat : _rtAttachFormat),
            ImageLayout = ImageLayout.General,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = final ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
        };
        SetResolve(ref color, _msSceneRaw);
        Barrier(m.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        ResolveTargetBarrier();
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(_sceneW, _sceneH)),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &color,
        };
        _vk.CmdBeginRendering(F.Cmd, in ri);
        _vk.CmdEndRendering(F.Cmd);
    }


    // =================================================================== scene-start load

    DescriptorSetLayout _msLoadSetLayout;
    PipelineLayout _msLoadLayout;
    ShaderModule _msLoadVs, _msLoadFsFloat, _msLoadFsUint;
    readonly Dictionary<(Format, SampleCountFlags), Pipeline> _msLoadPipelines = new();

    static bool IsUintFormat(Format f) => f is Format.R8G8B8A8Uint or Format.R16G16B16A16Uint or Format.R16G16Uint or Format.R32G32Uint or Format.R32Uint;

    const string LoadVs = """
#version 450
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
""";
    const string LoadFsFloat = """
#version 450
layout(set = 0, binding = 0) uniform sampler2D src;
layout(location = 0) out vec4 o;
void main() { o = texelFetch(src, ivec2(gl_FragCoord.xy), 0); }
""";
    const string LoadFsUint = """
#version 450
layout(set = 0, binding = 0) uniform usampler2D src;
layout(location = 0) out uvec4 o;
void main() { o = texelFetch(src, ivec2(gl_FragCoord.xy), 0); }
""";

    ShaderModule CompileBuiltin(string glsl, bool vertex)
    {
        var spv = _compiler.Compile(glsl, vertex, out var err) ?? throw new InvalidOperationException($"built-in shader: {err}");
        return CreateShaderModule(spv);
    }

    Pipeline MsaaLoadPipeline(Format format, SampleCountFlags samples)
    {
        if (_msLoadPipelines.TryGetValue((format, samples), out var p)) return p;
        if (_msLoadLayout.Handle == 0)
        {
            var b = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
            var ci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &b };
            _vk.CreateDescriptorSetLayout(_dev, in ci, null, out _msLoadSetLayout).Ok("load set layout");
            var sl = _msLoadSetLayout;
            var pli = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &sl };
            _vk.CreatePipelineLayout(_dev, in pli, null, out _msLoadLayout).Ok("load pipeline layout");
            _msLoadVs = CompileBuiltin(LoadVs, true);
            _msLoadFsFloat = CompileBuiltin(LoadFsFloat, false);
            _msLoadFsUint = CompileBuiltin(LoadFsUint, false);
        }
        var entry = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _msLoadVs, PName = entry };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = IsUintFormat(format) ? _msLoadFsUint : _msLoadFsFloat, PName = entry };
        var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var vp = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rs = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, LineWidth = 1 };
        var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = samples };
        var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
        var att = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &att };
        var dyn = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dsi = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };
        var gci = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo, RenderPass = MsaaCompatPass(format, samples), Subpass = 0, StageCount = 2, PStages = stages,
            PVertexInputState = &vi, PInputAssemblyState = &ia, PViewportState = &vp, PRasterizationState = &rs, PMultisampleState = &ms,
            PDepthStencilState = &ds, PColorBlendState = &cb, PDynamicState = &dsi, Layout = _msLoadLayout,
        };
        _vk.CreateGraphicsPipelines(_dev, _vkPipelineCache, 1, in gci, null, out p).Ok("MSAA load pipeline");
        return _msLoadPipelines[(format, samples)] = p;
    }

    /// <summary>
    /// First draw of a scene's first rendering: every sample takes the surface's pixel, read from the scene-start
    /// copy through a view of the attachment's format (raw integer views copy the bits exactly).
    /// </summary>
    void RecordMsaaLoad(Format format)
    {
        var src = SceneStartCopyView(_rt!, format);
        var cmd = _msCmd;
        var pipeline = MsaaLoadPipeline(format, _msSamples);
        var set = AllocSet(_msLoadSetLayout);
        var info = new DescriptorImageInfo(_dummySampler, src, ImageLayout.General);
        var w = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info };
        _vk.UpdateDescriptorSets(_dev, 1, &w, 0, null);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _msLoadLayout, 0, 1, &set, 0, null);
        var vp = new Viewport(0, 0, _sceneW, _sceneH, 0, 1);
        var sc = new Rect2D(new Offset2D(0, 0), new Extent2D(_sceneW, _sceneH));
        _vk.CmdSetViewport(cmd, 0, 1, in vp);
        _vk.CmdSetScissor(cmd, 0, 1, in sc);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        _boundPipeline = default;
    }

    // =================================================================== programmable blending

    /// <summary>Multisampled copies of the samples for framebuffer fetch, per size/format/sample count.</summary>
    readonly Dictionary<(uint W, uint H, Format F, SampleCountFlags S), (Image Image, ImageView View, ImageMemoryPool.Allocation Mem)> _msFetchCopies = new();
    Image _dummyMsImage;
    ImageView _dummyMsView;
    ImageMemoryPool.Allocation _dummyMsMem;

    /// <summary>1x1 four-sample image bound to the multisampled framebuffer-fetch binding when the scene has none.</summary>
    ImageView DummyMsView()
    {
        if (_dummyMsView.Handle != 0) return _dummyMsView;
        (_dummyMsImage, _dummyMsView, _dummyMsMem) = CreateImage(1, 1, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit,
            ImageAspectFlags.ColorBit, samples: SampleCountFlags.Count4Bit);
        EndRendering();
        Barrier(_dummyMsImage, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        return _dummyMsView;
    }

    long _statMsInputFetches;
    /// <summary>Serial of the frame recording (bumped when a frame slot's descriptor pools are reset).</summary>
    long _frameSerial;
    DescriptorSet _msInputSet;
    ImageView _msInputSetView;
    long _msInputSetFrame = -1;

    /// <summary>
    /// Programmable blending in a multisampled render pass: the shader reads the colour attachment itself as an input
    /// attachment (set 3 binding 2), per sample. No copy and no interruption of the rendering.
    /// </summary>
    DescriptorSet BuildMsaaInputFetchSet()
    {
        var m = GetMsaaImage(_rt!, _msSamples);
        var view = MsaaView(m, _rtAttachFormat);
        // the set only names the attachment view: reused by every such draw of the frame (lighting passes issue many)
        if (_msInputSet.Handle != 0 && _msInputSetView.Handle == view.Handle && _msInputSetFrame == _frameSerial) return _msInputSet;
        var set = AllocSet(_fbLayout);
        (_msInputSet, _msInputSetView, _msInputSetFrame) = (set, view, _frameSerial);
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
        if (_statMsInputFetches++ == 0)
            Log.Info($"[gpu] programmable blending in multisampled scenes reads the colour attachment as an input attachment{(_core.SampleRateShading ? " per sample" : " (sample 0: no sampleRateShading)")}");
        return set;
    }

    /// <summary>Before a programmable-blending draw in a multisampled render pass: the colour writes of earlier draws
    /// become visible to its input attachment reads (the render pass's by-region self-dependency).</summary>
    void InputFetchBarrier(CommandBuffer cmd)
    {
        var mb = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ColorAttachmentWriteBit, DstAccessMask = AccessFlags.InputAttachmentReadBit };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit, DependencyFlags.ByRegionBit, 1, in mb, 0, null, 0, null);
    }

    /// <summary>Programmable blending in a multisampled scene: the shaders read a copy of the samples, per sample.</summary>
    DescriptorSet BuildMsaaFetchSet()
    {
        var rt = _rt!;
        EndRendering(resolve: false); // stores the samples (the scene goes on); the next rendering resolves
        var m = GetMsaaImage(rt, _msSamples);
        var key = (rt.Width, rt.Height, _rtAttachFormat, _msSamples);
        if (!_msFetchCopies.TryGetValue(key, out var copy))
        {
            copy = CreateImage(rt.Width, rt.Height, _rtAttachFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit, samples: _msSamples);
            _msFetchCopies[key] = copy;
            Barrier(copy.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
            Log.Info($"[gpu] programmable blending in multisampled scene 0x{rt.Address:X8}: shaders read the {_msSamples} samples{(_core.SampleRateShading ? " per sample" : " (sample 0: no sampleRateShading)")}");
        }
        _statMsFetchCopies++;
        if (FetchRegion(1, rt.Width, rt.Height) is not (var offset, var extent))
            return BuildFetchSet(_dummyView, ImageLayout.ShaderReadOnlyOptimal, copy.View, ImageLayout.General);
        Barrier(m.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        Barrier(copy.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = offset, DstOffset = offset, Extent = extent,
        };
        _vk.CmdCopyImage(Cmd, m.Image, ImageLayout.General, copy.Image, ImageLayout.General, 1, in region);
        Barrier(copy.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        // the copy must finish before the scene renders into the samples again
        Barrier(m.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        return BuildFetchSet(_dummyView, ImageLayout.ShaderReadOnlyOptimal, copy.View, ImageLayout.General);
    }

    long _statFetchPixels, _statFetchFullPixels;

    /// <summary>
    /// Part of the framebuffer a programmable-blending draw can read, in pixels of the copied image
    /// (<paramref name="scale"/> = image pixels per render-target pixel): shaders fetch only at their own fragment, so
    /// only the draw's region clip within the scene area needs copying. Null when the draw covers no pixel.
    /// </summary>
    (Offset3D, Extent3D)? FetchRegion(uint scale, uint imageW, uint imageH)
    {
        long x0 = Math.Clamp(_drawMinX, 0, _sceneW), y0 = Math.Clamp(_drawMinY, 0, _sceneH);
        long x1 = Math.Clamp((long)_drawMaxX + 1, x0, _sceneW), y1 = Math.Clamp((long)_drawMaxY + 1, y0, _sceneH);
        x0 = Math.Min(x0 * scale, imageW); y0 = Math.Min(y0 * scale, imageH);
        x1 = Math.Min(x1 * scale, imageW); y1 = Math.Min(y1 * scale, imageH);
        _statFetchFullPixels += (long)imageW * imageH;
        if (x1 <= x0 || y1 <= y0) return null;
        _statFetchPixels += (x1 - x0) * (y1 - y0);
        return (new Offset3D((int)x0, (int)y0, 0), new Extent3D((uint)(x1 - x0), (uint)(y1 - y0), 1));
    }

    /// <summary>
    /// A depth texture or a mapped (CDRAM mirror) read hits the depth/stencil surface of a multisampled scene. Its
    /// samples are not stored to memory unless the scene forces the store (none of the tested games do: their MSAA
    /// depth is transient, as Vita3K treats it too), so the read sees guest memory, like on hardware. A forced store
    /// would need the samples unpacked into the surface's sample layout, which is not implemented yet: warned once.
    /// </summary>
    bool MultisampledDepthRead(DepthTarget dt, bool stencil)
    {
        if (dt.Samples == SampleCountFlags.Count1Bit) return false;
        Log.WarnOnce($"msdepth-read-{dt.Address:X8}-{stencil}", $"[gpu] {(stencil ? "stencil" : "depth")} surface 0x{(stencil ? dt.StencilAddress : dt.Address):X8} of a multisampled scene is read: " +
            "the samples are not stored (read from guest memory)" + (dt.ForceStored ? "; the scene FORCES THE STORE, which is not implemented for real MSAA (VITASHARP_MSAA=ss renders it supersampled)" : ""));
        return true;
    }

    string MsaaReport() => (MsaaSetting switch
    {
        MsaaEmulation.Supersample => "supersampled (VITASHARP_MSAA=ss)",
        MsaaEmulation.Off => "off (VITASHARP_MSAA=off)",
        _ => $"native: {_statMsScenes} scenes, {_statMsPasses} renderings ({_statMsDiscards} discarding their samples at scene end), {_statMsResolves} resolves, {_statMsInputFetches} input-attachment fetches, {_statMsFetchCopies} multisampled framebuffer-fetch copies",
    }) + $"; framebuffer-fetch copies moved {_statFetchPixels / 1e6:F1} M of {_statFetchFullPixels / 1e6:F1} M pixels";

    void DestroyMsaa()
    {
        foreach (var m in _msImages.Values) DestroyMsaaImage(m);
        _msImages.Clear();
        foreach (var rp in _msRenderPasses.Values) _vk.DestroyRenderPass(_dev, rp, null);
        _msRenderPasses.Clear();
        foreach (var c in _msFetchCopies.Values) { _vk.DestroyImageView(_dev, c.View, null); _vk.DestroyImage(_dev, c.Image, null); }
        _msFetchCopies.Clear();
        if (_dummyMsView.Handle != 0) { _vk.DestroyImageView(_dev, _dummyMsView, null); _vk.DestroyImage(_dev, _dummyMsImage, null); }
        foreach (var p in _msLoadPipelines.Values) _vk.DestroyPipeline(_dev, p, null);
        _msLoadPipelines.Clear();
        if (_msLoadLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_dev, _msLoadLayout, null);
            _vk.DestroyDescriptorSetLayout(_dev, _msLoadSetLayout, null);
            _vk.DestroyShaderModule(_dev, _msLoadVs, null);
            _vk.DestroyShaderModule(_dev, _msLoadFsFloat, null);
            _vk.DestroyShaderModule(_dev, _msLoadFsUint, null);
        }
    }
}
