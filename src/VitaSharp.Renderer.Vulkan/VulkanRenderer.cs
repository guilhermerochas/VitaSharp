using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

public sealed class VulkanRendererOptions
{
    public IPresentationSurface? Surface;
    public bool Validation;
    public string? ShaderCacheDir;
    /// <summary>Headless debugging: write every Nth presented frame as PNG into this folder.</summary>
    public string? DumpFramesDir;
    public int DumpEvery = 60;
    /// <summary>Frames before this one are not dumped (--dump-from).</summary>
    public long DumpFrom;
    public bool VSync = true;
    /// <summary>Debug: log every draw of this presented frame number (-1 = off).</summary>
    public long TraceDrawsFrame = -1;
    /// <summary>When set, every dumped frame also adds a 240x136 thumbnail here (contact sheets for tests).</summary>
    public List<(long Frame, byte[] Rgba)>? Thumbnails;
    /// <summary>Automated tests: decode every uploaded texture on the CPU and warn when it looks like garbage.</summary>
    public bool InspectTextures;
    /// <summary>shaderc optimisation level: "zero" (default: fast compiles, less stutter when new shaders appear),
    /// "size" or "perf". VITASHARP_SHADERC_OPT overrides it.</summary>
    public string? ShaderOptimization;
    /// <summary>Translate/compile new shaders and build their pipelines on worker threads as soon as the guest submits
    /// the draws (VITASHARP_ASYNC_SHADERS=0|1 overrides it).</summary>
    public bool AsyncShaders = true;
    /// <summary>Also build predicted pipelines in the background (needs <see cref="AsyncShaders"/>;
    /// VITASHARP_ASYNC_PIPELINES=0|1 overrides it).</summary>
    public bool AsyncPipelines = true;
    /// <summary>Shader worker threads, 0 = automatic (half the logical processors, 1-6). VITASHARP_SHADER_WORKERS overrides it.</summary>
    public int ShaderWorkers;
    /// <summary>Presents counted before this renderer's first one (GPU dump replay: frames keep the recorded numbers).</summary>
    public long FirstPresent;
}

/// <summary>GXM command executor on Vulkan 1.3 (dynamic rendering + extended dynamic state).</summary>
public sealed unsafe partial class VulkanRenderer : IGpuBackend
{
    /// <summary>
    /// Command buffers in flight. Sync points submit early (see DeferSignal), so a frame is often split into 2-3
    /// submissions; with only two slots, recording the next one regularly waited for the GPU (Slot fence waits).
    /// </summary>
    const int FramesInFlight = 3;
    const ulong RingSize = 64ul << 20;

    readonly VkCore _core;
    readonly Vk _vk;
    readonly Device _dev;
    readonly VulkanRendererOptions _opt;
    readonly ImageMemoryPool _imagePool;
    readonly ShaderCompiler _compiler;

    sealed class Frame
    {
        public CommandPool Pool;
        public CommandBuffer Cmd;
        public Fence Fence;
        public Silk.NET.Vulkan.Semaphore Acquire, RenderDone;
        public readonly List<DescriptorPool> DescPools = new();
        public int DescPoolIndex;
        public Buffer Ring;
        public DeviceMemory RingMem;
        public byte* RingPtr;
        public ulong RingOffset;
        public readonly List<(Buffer, DeviceMemory)> Overflow = new();
        public readonly List<Action> Deferred = new();
        public bool Recording;
        public bool Submitted;
        public (Buffer Buf, DeviceMemory Mem, nint Ptr, uint W, uint H)? Readback;
        public int DumpIndex;
        /// <summary>Readback holds a frame to dump (the buffer itself stays allocated and is reused by later dumps).</summary>
        public bool DumpPending;
        /// <summary>Debug mode: replay ring slot this frame copies the presented image into, -1 = none (VulkanRenderer.Replay.cs).</summary>
        public int ReplaySlot = -1;
        /// <summary>Debug mode / VITASHARP_DEBUG_NAN: NaN/Inf scan results and draw hits of this frame (VulkanRenderer.NanCheck.cs).</summary>
        public NanFrame? Nan;
        /// <summary>Render targets copied out this frame, written into guest memory once the frame completes.</summary>
        public readonly List<RtWriteback> Writebacks = new();
        /// <summary>Notifications and other sync points (in submission order) signalled once this frame completed and its results are published.</summary>
        public readonly List<Action> Signals = new();
        public bool Retired;
        /// <summary>Submission order (frames retire oldest first so results are published in GPU order).</summary>
        public long SubmitSeq;
        /// <summary>Occlusion queries of this frame's visibility tests.</summary>
        public VisibilityFrame? Visibility;
        /// <summary>Secondary command buffers of multisampled renderings (reused every time the frame slot is recorded).</summary>
        public readonly List<CommandBuffer> Secondaries = new();
        public int SecondaryIndex;
        /// <summary>First of this frame's two timestamp queries (-1 = none), and whether they were written.</summary>
        public int TimeQuery = -1;
        public bool TimePending;
    }

    readonly Frame[] _frames = new Frame[FramesInFlight];
    int _frameIndex;
    Frame F => _frames[_frameIndex];
    /// <summary>Where commands go: the open multisampled rendering's secondary command buffer, else the frame's.</summary>
    CommandBuffer Cmd => _msCmd.Handle != 0 ? _msCmd : F.Cmd;
    public long FrameCount { get; private set; }

    public VulkanRenderer(VulkanRendererOptions options)
    {
        _opt = options;
        _presentCount = Math.Max(0, options.FirstPresent);
        _core = new VkCore(options.Surface, options.Validation);
        _vk = _core.Vk;
        _dev = _core.Device;
        _imagePool = new ImageMemoryPool(_core);
        // VITASHARP_DEBUG_NAN=draws: the instrumented SPIR-V stays out of the on-disk cache
        _compiler = new ShaderCompiler(Core.Gxm.Shader.ShaderGenerator.NanDraws ? null : options.ShaderCacheDir, ShaderCompiler.ResolveOptLevel(options.ShaderOptimization));
        ConfigureAsyncShaders(options);
        InitTrace();
        // programmable blending in multisampled scenes reads per sample only where sample-rate shading exists
        Core.Gxm.Shader.ShaderGenerator.PerSampleFramebufferFetch = _core.SampleRateShading;
        for (int i = 0; i < FramesInFlight; i++) _frames[i] = CreateFrame();
        CreateLayouts();
        CreateDummies();
        if (options.Surface != null) CreateSwapchain();
    }

    public string DeviceName => _core.DeviceName;

    Frame CreateFrame()
    {
        var f = new Frame();
        var pci = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _core.QueueFamily, Flags = CommandPoolCreateFlags.TransientBit };
        _vk.CreateCommandPool(_dev, in pci, null, out f.Pool).Ok("vkCreateCommandPool");
        var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = f.Pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        _vk.AllocateCommandBuffers(_dev, in ai, out f.Cmd).Ok("vkAllocateCommandBuffers");
        var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        _vk.CreateFence(_dev, in fci, null, out f.Fence).Ok("vkCreateFence");
        var sci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        _vk.CreateSemaphore(_dev, in sci, null, out f.Acquire);
        _vk.CreateSemaphore(_dev, in sci, null, out f.RenderDone);
        var (buf, mem, ptr) = _core.CreateHostBuffer(RingSize, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit
            | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit);
        f.Ring = buf; f.RingMem = mem; f.RingPtr = (byte*)ptr;
        return f;
    }

    // =================================================================== frame lifecycle

    void BeginFrameIfNeeded()
    {
        var f = F;
        if (f.Recording) return;
        RetireFrame(f, RetireCause.Slot);
        f.Submitted = false;
        f.Retired = false;
        _vk.ResetFences(_dev, 1, in f.Fence);
        foreach (var a in f.Deferred) a();
        f.Deferred.Clear();
        foreach (var (b, m) in f.Overflow) _core.DestroyBuffer(b, m);
        f.Overflow.Clear();
        f.RingOffset = 0;
        foreach (var p in f.DescPools) _vk.ResetDescriptorPool(_dev, p, 0);
        f.DescPoolIndex = 0;
        f.SecondaryIndex = 0;
        _frameSerial++; // the frame's descriptor pools were reset: per-frame cached sets are gone
        _vk.ResetCommandPool(_dev, f.Pool, 0);
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(f.Cmd, in bi);
        f.Recording = true;
        ResetVisibility(f);
        BeginGpuTime(f);
        _boundPipeline = default;
    }

    void SubmitFrame(bool present, uint swapIndex)
    {
        var f = F;
        if (!f.Recording) return;
        EndRendering();
        if (f.Nan is { Used: true }) NanBeforeSubmit(f);
        EndGpuTime(f);
        _vk.EndCommandBuffer(f.Cmd);
        var cmd = f.Cmd;
        var waitStage = PipelineStageFlags.TransferBit;
        var acquire = f.Acquire;
        var done = f.RenderDone;
        var si = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            WaitSemaphoreCount = present ? 1u : 0u,
            PWaitSemaphores = &acquire,
            PWaitDstStageMask = &waitStage,
            SignalSemaphoreCount = present ? 1u : 0u,
            PSignalSemaphores = &done,
        };
        var submitResult = _vk.QueueSubmit(_core.Queue, 1, in si, f.Fence);
        if (submitResult == Result.ErrorDeviceLost) { _deviceLost = true; ReportDeviceLost("vkQueueSubmit"); } // stops background shader/pipeline work
        submitResult.Ok("vkQueueSubmit");
        f.Recording = false;
        f.Submitted = true;
        f.SubmitSeq = ++_submitSeq;
        if (present) PresentSwapchain(swapIndex, done);
        _frameIndex = (_frameIndex + 1) % FramesInFlight;
        FrameCount++;
    }

    /// <summary>
    /// Waits for a submitted frame and publishes its results: dumps, render-target writebacks into guest memory,
    /// visibility results and the scene notifications that were deferred until the GPU really finished.
    /// </summary>
    void RetireFrame(Frame f, RetireCause cause)
    {
        if (!f.Submitted || f.Retired) return;
        // the GPU completes work in submission order: publish older frames' results first
        foreach (var o in _frames)
            if (o != f && o.Submitted && !o.Retired && o.SubmitSeq < f.SubmitSeq) RetireFrame(o, RetireCause.Older);
        WaitFrameFence(f, cause);
        f.Retired = true;
        CompleteGpuTime(f);
        if (_longWait is { } lw) { Log.Info($"[sync] waited {lw.Ms:F1} ms ({lw.Cause}) for a frame with {_lastGpuMs:F1} ms of GPU work, present {_presentCount}"); _longWait = null; }
        if (f.Readback is { } rb) WriteDump(f, rb);
        if (f.ReplaySlot >= 0) CompleteReplayFrame(f);
        if (f.Nan is { Used: true }) CompleteNanFrame(f);
        using (FrameLedger.Time(FrameLedger.Cat.Writeback)) CompleteWritebacks(f);
        CompleteVisibility(f);
        foreach (var s in f.Signals) s();
        f.Signals.Clear();
    }

    bool HasPendingResults(Frame f) => f.Writebacks.Count > 0 || f.Signals.Count > 0 || f.Visibility is { Used: > 0 };

    long _submitSeq;

    /// <summary>
    /// True when GPU results recorded so far (render-target writebacks, visibility test results, deferred
    /// notifications) are not yet in guest memory. A notification means that all earlier GPU work completed, so a
    /// scene ending now must not signal before those results are published.
    /// </summary>
    bool ResultsPending()
    {
        foreach (var f in _frames)
            if ((f.Recording || (f.Submitted && !f.Retired)) && HasPendingResults(f)) return true;
        return false;
    }

    /// <summary>Sync points (notifications, display queue entries) wait for every result recorded before them.</summary>
    public bool DeferSignal(Action signal)
    {
        if (!ResultsPending()) return false;
        Frame? target = F.Recording ? F : null;
        if (target == null)
            foreach (var f in _frames)
                if (f.Submitted && !f.Retired && (target == null || f.SubmitSeq > target.SubmitSeq)) target = f;
        if (target == null) return false;
        target.Signals.Add(signal);
        // the guest will wait on this sync point (display queue entry, notification): start the GPU on everything
        // recorded so far now, instead of once the guest has run out of work and the queue drained
        if (target == F && !_inScene) { SubmitFrame(false, 0); _drawsSinceSubmit = 0; }
        return true;
    }

    /// <summary>sceGxmFinish: submit what is recorded and retire every frame in flight.</summary>
    void Finish()
    {
        if (F.Recording) { SubmitFrame(false, 0); _drawsSinceSubmit = 0; }
        foreach (var f in _frames) RetireFrame(f, RetireCause.Finish);
    }

    int _drawsSinceSubmit;

    // =================================================================== upload ring

    (Buffer Buffer, ulong Offset, nint Ptr) Upload(ulong size, ulong align = 16)
    {
        var f = F;
        ulong off = (f.RingOffset + align - 1) & ~(align - 1);
        if (off + size <= RingSize)
        {
            f.RingOffset = off + size;
            return (f.Ring, off, (nint)(f.RingPtr + off));
        }
        var (b, m, p) = _core.CreateHostBuffer(Math.Max(size, 1), BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit
            | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit);
        f.Overflow.Add((b, m));
        return (b, 0, p);
    }

    (Buffer Buffer, ulong Offset) UploadSpan(ReadOnlySpan<byte> data, ulong align = 16)
    {
        var (b, off, p) = Upload((ulong)data.Length, align);
        data.CopyTo(new Span<byte>((void*)p, data.Length));
        return (b, off);
    }

    // =================================================================== command dispatch

    public void Execute(GpuCommand command)
    {
        switch (command)
        {
            case BeginSceneCommand b: BeginScene(b); break;
            case DrawCommand d: Draw(d); break;
            case EndSceneCommand e: EndScene(e); break;
            case FinishCommand: Finish(); break;
            case MidSceneFlushCommand:
                if (TracingScenes) TraceLine($"[scene] mid-scene flush rt=0x{_rt?.Address ?? 0:X8} (next draw id={_drawSerial + 1})");
                if (_inScene && _rt != null) CaptureSceneStart(_rt);
                break;
            case PresentCommand p: Present(p.Frame); break;
            case CallbackCommand cb:
                if (cb.WriteSize > 0) InvalidateTextures(cb.WriteAddress, cb.WriteSize);
                break;
        }
    }

    // =================================================================== render targets

    sealed class RenderTarget
    {
        public uint Address, Width, Height, ColorFormat, Stride;
        public Format Format;
        public Image Image;
        public ImageView View;
        public ImageView SampledView;
        public ComponentMapping SampledSwizzle;
        public bool SampledGamma;
        public ImageView SrgbView;    // attachment view for scenes with surface gamma
        public ImageView RawView;     // integer attachment view for raw-bit fragment outputs (see RawAttachmentView)
        public ImageMemoryPool.Allocation Mem;
        public bool Dirty;            // written since the last read barrier
        public long Version;          // bumped whenever the contents change
        public long LastScene;        // serial of the last scene that rendered into it
        public long LastUsedFrame;
    }

    sealed class DepthTarget
    {
        public uint Address, Width, Height;
        public uint StrideBytes;
        public uint StencilAddress, StencilStrideBytes; // separate S8 stencil plane (formats S8, DF32_S8, DF32M_S8)
        public long LastScene;
        public long Version;          // bumped whenever depth may have been written
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public long LastUsedFrame;
        /// <summary>Multisampled depth of a scene rendered with real MSAA (one sample per GXM sample).</summary>
        public SampleCountFlags Samples = SampleCountFlags.Count1Bit;
        /// <summary>A scene stored it with SCE_GXM_DEPTH_STENCIL_FORCE_STORE.</summary>
        public bool ForceStored;
    }

    readonly Dictionary<(uint, uint, uint, uint), RenderTarget> _renderTargets = new();
    readonly List<RenderTarget> _traceReplaced = new();
    readonly Dictionary<(uint, uint, uint, SampleCountFlags), DepthTarget> _depthTargets = new();

    (Image, ImageView, ImageMemoryPool.Allocation) CreateImage(uint w, uint h, Format format, ImageUsageFlags usage, ImageAspectFlags aspect,
        uint mips = 1, uint layers = 1, bool mutable = false, bool cube = false, ComponentMapping? swizzle = null, SampleCountFlags samples = SampleCountFlags.Count1Bit)
    {
        var ici = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(Math.Max(w, 1), Math.Max(h, 1), 1),
            MipLevels = mips,
            ArrayLayers = layers,
            Samples = samples,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            Flags = (mutable ? ImageCreateFlags.CreateMutableFormatBit : 0) | (cube ? ImageCreateFlags.CreateCubeCompatibleBit : 0),
        };
        _vk.CreateImage(_dev, in ici, null, out var image).Ok("vkCreateImage");
        _vk.GetImageMemoryRequirements(_dev, image, out var req);
        var alloc = _imagePool.Allocate(req);
        _vk.BindImageMemory(_dev, image, alloc.Memory, alloc.Offset).Ok("vkBindImageMemory");
        var view = CreateView(image, format, aspect, mips, layers, cube, swizzle ?? default);
        return (image, view, alloc);
    }

    ImageView CreateView(Image image, Format format, ImageAspectFlags aspect, uint mips, uint layers, bool cube, ComponentMapping swizzle)
    {
        var vci = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = cube ? ImageViewType.TypeCube : ImageViewType.Type2D,
            Format = format,
            Components = swizzle,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, mips, 0, layers),
        };
        _vk.CreateImageView(_dev, in vci, null, out var view).Ok("vkCreateImageView");
        return view;
    }

    static Format SurfaceFormat(uint colorFormat)
    {
        var f = Formats.ColorFormat(colorFormat);
        if (f == Format.R8G8B8A8Unorm && (colorFormat & 0xF1800000) == 0 && ((colorFormat >> 20) & 3) == 1) f = Format.B8G8R8A8Unorm;
        return f;
    }

    RenderTarget GetRenderTarget(ColorSurfaceInfo c)
    {
        var key = (c.Data, c.Width, c.Height, c.ColorFormat);
        if (_renderTargets.TryGetValue(key, out var rt)) return rt;
        // a different surface at the same address replaces the old one
        // (no lambda over c: its closure was allocated on every call, including cache hits)
        var replaced = new List<KeyValuePair<(uint, uint, uint, uint), RenderTarget>>();
        foreach (var kv in _renderTargets) if (kv.Key.Item1 == c.Data) replaced.Add(kv);
        foreach (var (k, old) in replaced)
        {
            _renderTargets.Remove(k);
            if (TraceVis) Log.Info($"[vis] render target 0x{c.Data:X8} {old.Width}x{old.Height} fmt {old.ColorFormat:X8} replaced by {c.Width}x{c.Height} fmt {c.ColorFormat:X8} present={_presentCount}");
            if (_traceDumpFrame == _presentCount + 1 && _traceDir != null) _traceReplaced.Add(old); // keep it for the target dump (taken when present #TraceDrawsFrame is reached)
            else DestroyLater(old);
            _mirroredRt.Remove(old);
        }
        var format = SurfaceFormat(c.ColorFormat);
        if (!_core.SupportsColorAttachment(format)) format = Format.R8G8B8A8Unorm;
        var (img, view, mem) = CreateImage(c.Width, c.Height, format,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit, mutable: true);
        rt = new RenderTarget { Address = c.Data, Width = c.Width, Height = c.Height, ColorFormat = c.ColorFormat, Stride = c.StrideInPixels, Format = format, Image = img, View = view, Mem = mem };
        // start from the guest memory content (games often fill the surface with the CPU first)
        UploadSurfaceFromGuest(rt);
        _renderTargets[key] = rt;
        _fNewTargets++;
        if (DebugMode.Enabled) DbgRenderTarget(rt, format, replaced);
        return rt;
    }

    void DestroyLater(RenderTarget rt)
    {
        DestroySupersampleLater(rt);
        DestroyMsaaLater(rt);
        DestroySceneStartCopyLater(rt);
        var f = F;
        f.Deferred.Add(() =>
        {
            _vk.DestroyImageView(_dev, rt.View, null);
            if (rt.SampledView.Handle != 0) _vk.DestroyImageView(_dev, rt.SampledView, null);
            if (rt.SrgbView.Handle != 0) _vk.DestroyImageView(_dev, rt.SrgbView, null);
            if (rt.RawView.Handle != 0) _vk.DestroyImageView(_dev, rt.RawView, null);
            _vk.DestroyImage(_dev, rt.Image, null);
            _imagePool.Free(rt.Mem);
        });
    }

    DepthTarget GetDepthTarget(DepthSurfaceInfo? d, uint w, uint h, SampleCountFlags samples = SampleCountFlags.Count1Bit)
    {
        uint addr = d?.DepthData ?? 0;
        var key = (addr != 0 ? addr : d?.StencilData ?? 0, w, h, samples);
        if (_depthTargets.TryGetValue(key, out var dt)) return dt;
        var (img, view, mem) = CreateImage(w, h, _core.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, samples: samples);
        dt = new DepthTarget
        {
            Samples = samples,
            Address = addr, Width = w, Height = h, StrideBytes = (d?.StrideInSamples ?? w) * 4,
            StencilAddress = d?.StencilData ?? 0, StencilStrideBytes = d?.StrideInSamples ?? w,
            Image = img, View = view, Mem = mem,
        };
        BeginFrameIfNeeded();
        Barrier(img, ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            0, AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit);
        _depthTargets[key] = dt;
        return dt;
    }

    void UploadSurfaceFromGuest(RenderTarget rt)
    {
        rt.Version++;
        BeginFrameIfNeeded();
        EndRendering();
        int bpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
        Barrier(rt.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        if (bpp == 4 && rt.Address != 0)
        {
            uint rowBytes = rt.Stride * 4;
            var (buf, off) = UploadSpan(GuestMemory.ReadSpan(rt.Address, (int)(rowBytes * rt.Height)), 16);
            var region = new BufferImageCopy
            {
                BufferOffset = off,
                BufferRowLength = rt.Stride,
                BufferImageHeight = rt.Height,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(rt.Width, rt.Height, 1),
            };
            _vk.CmdCopyBufferToImage(Cmd, buf, rt.Image, ImageLayout.General, 1, in region);
        }
        else
        {
            var clear = new ClearColorValue(0, 0, 0, 1);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            _vk.CmdClearColorImage(Cmd, rt.Image, ImageLayout.General, in clear, 1, in range);
        }
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit);
    }

    void Barrier(Image image, ImageLayout from, ImageLayout to, ImageAspectFlags aspect, PipelineStageFlags srcStage, PipelineStageFlags dstStage,
        AccessFlags srcAccess, AccessFlags dstAccess, uint mips = Vk.RemainingMipLevels, uint layers = Vk.RemainingArrayLayers)
    {
        var b = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, mips, 0, layers),
        };
        _vk.CmdPipelineBarrier(Cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, in b);
    }

    // =================================================================== scenes

    bool _inScene, _rendering, _depthNeedsClear;
    long _sceneSerial;
    BeginSceneCommand? _scene;
    RenderTarget? _rt;
    Format _rtAttachFormat;   // format the scene renders with (the sRGB view when the surface has gamma)
    bool _rtGamma;
    DepthTarget? _depth;
    uint _sceneW, _sceneH;

    void BeginScene(BeginSceneCommand b)
    {
        PollRetire(); // publish finished frames' results without waiting (cheap fence status checks)
        BeginFrameIfNeeded();
        EndRendering();
        _scene = b;
        _inScene = true;
        _rt = b.Color != null ? GetRenderTarget(b.Color) : null;
        if (TracingScenes || TraceVis) TraceLine($"[scene] begin present={_presentCount} rt=0x{_rt?.Address ?? 0:X8} {b.Width}x{b.Height} msaa={b.Multisample} downscale={b.Color?.Downscale} ds=0x{b.Depth?.DepthData ?? 0:X8} load={b.Depth?.ForceLoad} bgDepth={b.Depth?.BackgroundDepth} (next draw id={_drawSerial + 1})");
        // MSAA: real multisampling (VulkanRenderer.MsaaNative.cs), else 2x supersampling (VulkanRenderer.Msaa.cs);
        // MSAA without downscale renders at 2x into the sample-sized surface
        ChooseMsaaPath(b);
        _fScenes++;
        if (DebugMode.Enabled) DbgScene(b);
        if (_drawRing != null) DrawRingScene(b);
        if (_rt != null) CaptureSceneStart(_rt);
        if (_ss) LoadSupersample(_rt!);
        // surface gamma: shader output is linear and gets sRGB-encoded on write
        var srgb = _rt != null ? Formats.Srgb(_rt.Format) : Format.Undefined;
        _rtGamma = _rt != null && b.Color!.GammaMode != 0 && srgb != _rt.Format && _core.SupportsColorAttachment(srgb);
        _rtAttachFormat = _rt == null ? Format.Undefined : _rtGamma ? srgb : _rt.Format;
        if (_rtGamma && _rt!.SrgbView.Handle == 0) _rt.SrgbView = CreateView(_rt.Image, srgb, ImageAspectFlags.ColorBit, 1, 1, false, default);
        if (b.Color is { } cs) Log.DebugOnce($"scene{cs.Data:X}{cs.Width}{cs.ColorFormat:X}", $"[gpu] scene target 0x{cs.Data:X8} {cs.Width}x{cs.Height} stride {cs.StrideInPixels} fmt 0x{cs.ColorFormat:X8} type 0x{cs.SurfaceType:X} gamma {cs.GammaMode}");
        if (b.Depth is { } ds) Log.DebugOnce($"scened{ds.DepthData:X}", $"[gpu] scene depth 0x{ds.DepthData:X8} stencil 0x{ds.StencilData:X8} stride {ds.StrideInSamples} fmt 0x{ds.Format:X8} type 0x{ds.Type:X8} ({b.Width}x{b.Height})");
        else Log.WarnOnce("scene-nocolor", "[gpu] scene without colour surface");
        // the scene's size in render-target pixels; the render area is SceneScale times that
        uint surfW = _rt?.Width ?? b.Width, surfH = _rt?.Height ?? b.Height;
        if (_msaaNoDownscale) { surfW = Math.Max(surfW / 2, 1); surfH = Math.Max(surfH / 2, 1); }
        _sceneW = Math.Min(b.Width, surfW);
        _sceneH = Math.Min(b.Height, surfH);
        _depth = _msaaNoDownscale
            ? GetDepthTarget(b.Depth, Math.Max(b.Width * 2, _rt!.Width), Math.Max(b.Height * 2, _rt.Height))
            : GetDepthTarget(b.Depth, Math.Max(b.Width, _rt?.Width ?? 0) * SsScale, Math.Max(b.Height, _rt?.Height ?? 0) * SsScale, SceneSamples);
        _sceneSerial++;
        if (_rt != null) _rt.LastScene = _sceneSerial;
        _depth.LastScene = _sceneSerial;
        _depth.LastUsedFrame = FrameCount;
        if (b.Depth is { ForceStore: true }) _depth.ForceStored = true;
        _depthNeedsClear = b.Depth == null || !b.Depth.ForceLoad;
    }

    void EndScene(EndSceneCommand e)
    {
        EndRendering(final: true);
        if (DebugMode.Enabled && _rt != null) NanScanScene(_rt);
        bool writeback = _rt != null && ScheduleWriteback(_rt);
        // a scene whose pixels the CPU can read back signals its notifications only once they are in memory; the
        // same holds for every result of earlier work (visibility tests of this or a previous scene, writebacks):
        // games read occlusion results once the frame's last notification arrived
        if (e.Notifications.Length > 0 && (writeback || ResultsPending())) { foreach (var (addr, value) in e.Notifications) F.Signals.Add(() => GpuQueue.SignalNotification(addr, value)); e.NotificationsDeferred = true; }
        if (TraceVis) Log.Info($"[vis] endscene rt=0x{_rt?.Address ?? 0:X8} present={_presentCount} drawsSoFar={_drawSerial} gpuframe={FrameCount} queries={F.Visibility?.Used ?? 0} notif={string.Join(",", e.Notifications.Select(n => $"{n.Item1:X}={n.Item2:X}"))} deferred={e.NotificationsDeferred}");
        _inScene = false;
        _scene = null;
        _ss = false;
        _ms = false;
        _msaaNoDownscale = false;
    }

    /// <summary>The current rendering writes through the surface's integer view (raw-bit fragment outputs).</summary>
    bool _renderRaw;

    /// <summary>
    /// Integer format with the same texel layout as a colour surface's host format, used to write raw register bits
    /// (<see cref="GeneratedShader.RawColorBits"/>) exactly: through a float attachment the host GPU converts the
    /// values, canonicalising NaNs and flushing denormals, which corrupts packed data. Undefined when there is none
    /// for raw components of <paramref name="bits"/> bits.
    /// </summary>
    static Format RawAttachmentFormat(Format f, int bits) => (f, bits) switch
    {
        (Format.R16G16B16A16Sfloat, 16) => Format.R16G16B16A16Uint,
        (Format.R16G16Sfloat, 16) => Format.R16G16Uint,
        (Format.R32G32Sfloat, 32) => Format.R32G32Uint,
        (Format.R32Sfloat, 32) => Format.R32Uint,
        (Format.R8G8B8A8Unorm, 8) => Format.R8G8B8A8Uint,
        _ => Format.Undefined,
    };

    /// <summary>Integer view of the render target (or of its supersampled image) for raw-bit outputs.</summary>
    ImageView RawAttachmentView(RenderTarget rt)
    {
        var format = _rawFormat;
        if (_ss)
        {
            var ss = GetSupersampleImage(rt);
            if (ss.RawView.Handle == 0) ss.RawView = CreateView(ss.Image, format, ImageAspectFlags.ColorBit, 1, 1, false, default);
            return ss.RawView;
        }
        if (rt.RawView.Handle == 0) rt.RawView = CreateView(rt.Image, format, ImageAspectFlags.ColorBit, 1, 1, false, default);
        return rt.RawView;
    }

    Format _rawFormat;

    void BeginRendering()
    {
        if (_rendering || !_inScene) return;
        if (_ms) { BeginMsaaRendering(); return; }
        var color = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _rt == null ? default : _renderRaw ? RawAttachmentView(_rt) : _ss ? SupersampleView(_rt) : _rtGamma ? _rt.SrgbView : _rt.View,
            ImageLayout = ImageLayout.General,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var bg = _scene?.Depth;
        var depth = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _depth!.View,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = _depthNeedsClear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(Math.Clamp(bg?.BackgroundDepth ?? 1f, 0f, 1f), bg?.BackgroundStencil ?? 0)),
        };
        _depthNeedsClear = false;
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(_sceneW * SceneScale, _sceneH * SceneScale)),
            LayerCount = 1,
            ColorAttachmentCount = _rt != null ? 1u : 0u,
            PColorAttachments = &color,
            PDepthAttachment = &depth,
            PStencilAttachment = &depth,
        };
        _vk.CmdBeginRendering(Cmd, in ri);
        _rendering = true;
        if (_rt != null) { _rt.Dirty = true; _rt.Version++; }
        if (_depth != null) _depth.Version++;
    }

    /// <param name="resolve">false keeps a supersampled scene's samples unfiltered (the next pause filters them)</param>
    /// <param name="final">the scene ends: a multisampled rendering discards its samples instead of storing them</param>
    void EndRendering(bool resolve = true, bool final = false)
    {
        if (_rendering && _msCmd.Handle != 0)
        {
            _rendering = false;
            EndMsaaRendering(final, resolve);
        }
        else if (_ms && _msUnresolved && (resolve || final)) ResolveMsaa(final);
        if (_rendering)
        {
            _vk.CmdEndRendering(Cmd);
            _rendering = false;
            if (_ss && _rt != null) _ssUnresolved = true;
        }
        if (resolve && _ssUnresolved)
        {
            _ssUnresolved = false;
            if (_ss && _rt != null) ResolveSupersample(_rt);
        }
    }

    /// <summary>Makes colour writes to <paramref name="rt"/> visible to shader reads / transfers.</summary>
    /// <summary>
    /// Ends the rendering for work outside it that reads <paramref name="reads"/> (or no surface): a multisampled
    /// scene resolves its samples only when the work reads its own surface; otherwise the resolve waits for the next
    /// rendering (texture uploads and reads of other surfaces break multisampled scenes often).
    /// </summary>
    void PauseRendering(RenderTarget? reads = null) => EndRendering(resolve: !_ms || (reads != null && reads == _rt));

    void SyncRenderTargetForRead(RenderTarget rt)
    {
        if (!rt.Dirty) return;
        PauseRendering(rt);
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        rt.Dirty = false;
        if (rt == _rt) rt.Dirty = true; // will be written again when rendering resumes
    }

    // =================================================================== present

    RenderTarget? FindRenderTargetByAddress(uint address)
    {
        RenderTarget? best = null;
        foreach (var rt in _renderTargets.Values)
            if (rt.Address == address && (best == null || rt.LastUsedFrame > best.LastUsedFrame)) best = rt;
        return best;
    }

    RenderTarget? _cpuFramebuffer;
    long _presentCount;
    public long PresentCount => _presentCount;

    void Present(DisplayFrame frame)
    {
        BeginFrameIfNeeded();
        EndRendering();
        var src = FindRenderTargetByAddress(frame.Address);
        Log.WarnOnce($"present{frame.Address:X}", $"[gpu] present 0x{frame.Address:X8} {frame.Width}x{frame.Height} pitch {frame.Pitch} fmt {frame.Format}: {(src != null ? "rendered surface" : "CPU framebuffer")}");
        if (src == null)
        {
            // the guest drew the framebuffer with the CPU
            var c = new ColorSurfaceInfo(frame.Address, frame.Width, frame.Height, frame.Pitch, 0, 0, false, 0);
            if (_cpuFramebuffer == null || _cpuFramebuffer.Width != frame.Width || _cpuFramebuffer.Height != frame.Height)
            {
                if (_cpuFramebuffer != null) DestroyLater(_cpuFramebuffer);
                var (img, view, mem) = CreateImage(frame.Width, frame.Height, Format.R8G8B8A8Unorm,
                    ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
                _cpuFramebuffer = new RenderTarget { Address = frame.Address, Width = frame.Width, Height = frame.Height, Stride = frame.Pitch, Image = img, View = view, Mem = mem, Format = Format.R8G8B8A8Unorm };
            }
            _cpuFramebuffer.Address = frame.Address;
            _cpuFramebuffer.Stride = frame.Pitch;
            UploadSurfaceFromGuest(_cpuFramebuffer);
            src = _cpuFramebuffer;
        }
        SyncRenderTargetForRead(src);
        src.LastUsedFrame = FrameCount;

        RecordFrameSummary(frame);
        DrawRingPresent(frame);
        _presentCount++;
        _drawSerial = 0;
        EndFrameStallStats();
        bool dump = _opt.DumpFramesDir != null && _presentCount % Math.Max(1, _opt.DumpEvery) == 0 && _presentCount >= _opt.DumpFrom;
        if (dump) RecordReadback(src, frame.Width, frame.Height);
        else F.DumpPending = false;
        if (DebugMode.Enabled) DebugPresent(src, frame);
        else if (_replay != null) ReleaseReplay();

        // diagnostic capture (F10 / debug-mode anomaly): the frame that ends here is presented normally, then its
        // targets are dumped; a pending request arms the trace for the frame that starts now
        if (_diagActive is { } capture && _traceDumpFrame == _presentCount)
        {
            PresentOut(src, frame);
            CompleteCapture(capture, src, frame);
            return;
        }
        if (_diagRequest is { } pending && _diagActive == null && pending.ReadyToArm(_presentCount)) ArmCapture(pending);

        if (_traceDumpFrame == _presentCount && _traceDir != null)
        {
            SubmitFrame(false, 0);
            DumpAllTargets(Path.Combine(_traceDir, "targets"));
            return;
        }
        if (_traceSnaps.Count > 0 && _opt.ShaderCacheDir != null)
        {
            SubmitFrame(false, 0);
            _vk.DeviceWaitIdle(_dev);
            WriteTraceSnapshots(Path.Combine(_opt.ShaderCacheDir, "trace", "targets"));
            return;
        }
        PresentOut(src, frame);
    }

    /// <summary>Shows <paramref name="src"/> on the swapchain (or just submits the frame when there is none).</summary>
    void PresentOut(RenderTarget src, DisplayFrame frame)
    {
        if (_swapchain.Handle != 0)
        {
            if (AcquireSwapchain(out uint index))
            {
                BlitToSwapchain(src, frame.Width, frame.Height, index);
                SubmitFrame(true, index);
                RetireFlippedFrame();
                return;
            }
        }
        SubmitFrame(false, 0);
        RetireFlippedFrame();
    }

    void RecordReadback(RenderTarget src, uint w, uint h)
    {
        w = Math.Min(w, src.Width);
        h = Math.Min(h, src.Height);
        var f = F;
        if (f.Readback is not { } rb || rb.W != w || rb.H != h)
        {
            if (f.Readback is { } old) f.Deferred.Add(() => _core.DestroyBuffer(old.Buf, old.Mem));
            var (b, m, p) = _core.CreateHostBuffer(w * h * 4, BufferUsageFlags.TransferDstBit);
            f.Readback = (b, m, p, w, h);
        }
        var r = f.Readback!.Value;
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(w, h, 1),
        };
        _vk.CmdCopyImageToBuffer(Cmd, src.Image, ImageLayout.General, r.Buf, 1, in region);
        f.DumpIndex = (int)_presentCount;
        f.DumpPending = true;
        _readbackIsBgra = src.Format == Format.B8G8R8A8Unorm;
    }

    bool _readbackIsBgra;
    byte[]? _dumpPixels;

    /// <summary>Box-filtered downscale of an RGBA image.</summary>
    static byte[] Thumbnail(byte[] src, int w, int h, int tw, int th)
    {
        var dst = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int x0 = x * w / tw, x1 = Math.Max(x0 + 1, (x + 1) * w / tw), y0 = y * h / th, y1 = Math.Max(y0 + 1, (y + 1) * h / th);
                int r = 0, g = 0, b = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int o = (sy * w + sx) * 4;
                        r += src[o]; g += src[o + 1]; b += src[o + 2]; n++;
                    }
                int d = (y * tw + x) * 4;
                dst[d] = (byte)(r / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(b / n); dst[d + 3] = 255;
            }
        return dst;
    }

    void WriteDump(Frame f, (Buffer Buf, DeviceMemory Mem, nint Ptr, uint W, uint H) rb)
    {
        if (!f.DumpPending) return;
        try
        {
            Directory.CreateDirectory(_opt.DumpFramesDir!);
            // one reused CPU copy (dumps run on the GPU worker only): a fresh 2 MB array per dump went to the large object heap
            int bytes = (int)(rb.W * rb.H * 4);
            if (_dumpPixels?.Length != bytes) _dumpPixels = new byte[bytes];
            var pixels = _dumpPixels;
            new ReadOnlySpan<byte>((void*)rb.Ptr, bytes).CopyTo(pixels);
            if (_readbackIsBgra)
                for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // the display ignores alpha
            Png.Write(Path.Combine(_opt.DumpFramesDir!, $"frame_{f.DumpIndex:D6}.png"), pixels, (int)rb.W, (int)rb.H);
            if (_opt.Thumbnails is { } thumbs)
                lock (thumbs) thumbs.Add((f.DumpIndex, Thumbnail(pixels, (int)rb.W, (int)rb.H, 240, 136)));
        }
        catch (Exception e) { Log.WarnOnce("dumpframe", $"frame dump failed: {e.Message}"); }
        f.DumpPending = false;
    }

    /// <summary>Debug: synchronously reads back every render target into PNGs. <paramref name="shot"/>: also writes the
    /// presented region of that target (which may be the CPU framebuffer, not a render target) as a screenshot.</summary>
    int DumpAllTargets(string dir, (RenderTarget Src, uint W, uint H, string Path)? shot = null)
    {
        _vk.DeviceWaitIdle(_dev);
        Directory.CreateDirectory(dir);
        var stats = new System.Text.StringBuilder();
        var pci = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _core.QueueFamily };
        _vk.CreateCommandPool(_dev, in pci, null, out var pool);
        var replaced = _traceReplaced.ToList();
        _traceReplaced.Clear();
        var all = _renderTargets.Values.Concat(replaced).ToList();
        if (shot is { } s0 && !all.Contains(s0.Src)) all.Add(s0.Src);
        foreach (var rt in all)
        {
            bool half = rt.Format == Format.R16G16B16A16Sfloat;
            bool raw64 = rt.Format == Format.R32G32Sfloat; // dumped as its first 32-bit word per pixel (packed data)
            var (buf, mem, ptr) = _core.CreateHostBuffer(rt.Width * rt.Height * (half || raw64 ? 8u : 4u), BufferUsageFlags.TransferDstBit);
            var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            _vk.AllocateCommandBuffers(_dev, in ai, out var cmd);
            var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _vk.BeginCommandBuffer(cmd, in bi);
            var region = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D(rt.Width, rt.Height, 1) };
            if (half || raw64 || Formats.ColorBytesPerPixel(rt.ColorFormat) == 4 || rt.Format is Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm)
                _vk.CmdCopyImageToBuffer(cmd, rt.Image, ImageLayout.General, buf, 1, in region);
            _vk.EndCommandBuffer(cmd);
            var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
            _vk.QueueSubmit(_core.Queue, 1, in si, default);
            _vk.QueueWaitIdle(_core.Queue);
            stats.AppendLine(TargetStats(rt, ptr));
            byte[] px;
            if (half)
            {
                // HDR target: clamp to 0..1 for viewing
                var h = new ReadOnlySpan<Half>((void*)ptr, (int)(rt.Width * rt.Height * 4));
                px = new byte[h.Length];
                for (int i = 0; i < h.Length; i++) px[i] = (byte)Math.Clamp((int)((float)h[i] * 255f + 0.5f), 0, 255);
            }
            else if (raw64)
            {
                var wds = new ReadOnlySpan<uint>((void*)ptr, (int)(rt.Width * rt.Height * 2));
                px = new byte[rt.Width * rt.Height * 4];
                for (int i = 0; i < px.Length / 4; i++) BitConverter.TryWriteBytes(px.AsSpan(i * 4), wds[i * 2]);
            }
            else if (rt.Format == Format.B10G11R11UfloatPack32)
            {
                // packed unsigned floats (R 11 bits, G 11 bits, B 10 bits): decode for viewing, clamped to 0..1
                var w = new ReadOnlySpan<uint>((void*)ptr, (int)(rt.Width * rt.Height));
                px = new byte[w.Length * 4];
                for (int i = 0; i < w.Length; i++)
                {
                    uint v = w[i];
                    px[i * 4] = ToByte(UFloat(v & 0x7FF, 6));
                    px[i * 4 + 1] = ToByte(UFloat((v >> 11) & 0x7FF, 6));
                    px[i * 4 + 2] = ToByte(UFloat((v >> 22) & 0x3FF, 5));
                    px[i * 4 + 3] = 255;
                }
            }
            else px = new ReadOnlySpan<byte>((void*)ptr, (int)(rt.Width * rt.Height * 4)).ToArray();
            if (rt.Format == Format.B8G8R8A8Unorm) for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
            var opaque = (byte[])px.Clone();
            for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
            if (shot is { } s && s.Src == rt) WriteScreenshot(s.Path, opaque, rt.Width, Math.Min(s.W, rt.Width), Math.Min(s.H, rt.Height));
            Png.Write(Path.Combine(dir, replaced.Contains(rt) ? $"rt_{rt.Address:X8}_{rt.Width}x{rt.Height}_{rt.ColorFormat:X8}.png" : $"rt_{rt.Address:X8}_{rt.Width}x{rt.Height}.png"), opaque, (int)rt.Width, (int)rt.Height);
            _core.DestroyBuffer(buf, mem);
        }
        File.WriteAllText(Path.Combine(dir, "targets.txt"), stats.ToString());
        foreach (var old in replaced) DestroyLater(old);

        // unsigned small float (5-bit exponent, 6- or 5-bit mantissa), as in B10G11R11_UFLOAT
        static float UFloat(uint bits, int mantissaBits)
        {
            uint e = bits >> mantissaBits, m = bits & ((1u << mantissaBits) - 1);
            if (e == 0) return m / (float)(1 << mantissaBits) * MathF.Pow(2, -14);
            if (e == 31) return m == 0 ? float.PositiveInfinity : float.NaN;
            return (1 + m / (float)(1 << mantissaBits)) * MathF.Pow(2, (int)e - 15);
        }
        _vk.DestroyCommandPool(_dev, pool, null);
        Log.Info($"[gpu] dumped {all.Count} render targets to {dir}");
        return all.Count;
    }

    static void WriteScreenshot(string path, byte[] rgba, uint stride, uint w, uint h)
    {
        var px = new byte[w * h * 4];
        for (uint y = 0; y < h; y++) System.Buffer.BlockCopy(rgba, (int)(y * stride * 4), px, (int)(y * w * 4), (int)(w * 4));
        Png.Write(path, px, (int)w, (int)h);
    }

    /// <summary>Numeric summary of a target (per-channel min/max/mean, NaN/Inf count) and, for surfaces the CPU reads
    /// back, the mean of the guest copy so the writeback can be verified against the GPU image.</summary>
    string TargetStats(RenderTarget rt, nint ptr)
    {
        int n = (int)(rt.Width * rt.Height);
        bool half = rt.Format == Format.R16G16B16A16Sfloat;
        Span<float> min = stackalloc float[4], max = stackalloc float[4];
        Span<double> sum = stackalloc double[4];
        min.Fill(float.MaxValue); max.Fill(float.MinValue);
        int bad = 0;
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 4; c++)
            {
                float v = half ? (float)((Half*)ptr)[i * 4 + c] : ((byte*)ptr)[i * 4 + c] / 255f;
                if (float.IsNaN(v) || float.IsInfinity(v)) { bad++; continue; }
                if (v < min[c]) min[c] = v;
                if (v > max[c]) max[c] = v;
                sum[c] += v;
            }
        var sb = new System.Text.StringBuilder();
        sb.Append($"rt 0x{rt.Address:X8} {rt.Width}x{rt.Height} fmt 0x{rt.ColorFormat:X8} {rt.Format} scene {rt.LastScene}:");
        for (int c = 0; c < 4; c++) sb.Append($" {"rgba"[c]}[{min[c]:G4}..{max[c]:G4} mean {sum[c] / n:G4}]");
        if (bad > 0) sb.Append($" NaN/Inf {bad}");
        if ((rt.ColorFormat & 0xF1800000) == 0x41000000 && rt.Address != 0)
        {
            double gs = 0; int gn = 0;
            for (uint y = 0; y < rt.Height; y++)
                for (uint x = 0; x < rt.Width; x++)
                {
                    uint v = GuestMemory.R32(rt.Address + (y * rt.Stride + x) * 4);
                    gs += (float)BitConverter.UInt16BitsToHalf((ushort)(((v & 0x3FF) << 5)));
                    gn++;
                }
            sb.Append($" guest r mean {gs / Math.Max(1, gn):G4}");
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        StopShaderWorkers();
        CloseTitleCaches();
        _vk.DeviceWaitIdle(_dev);
        DestroyMirror();
        foreach (var fr in _frames) RetireFrame(fr, RetireCause.Dispose);
        foreach (var fr in _frames) DestroyVisibility(fr);
        DestroyWritebacks();
        DestroyReplay();
        DestroyNan();
        foreach (var f in _frames)
        {
            if (f.Submitted && f.Readback is { } rb) WriteDump(f, rb);
            if (f.Readback is { } rbuf) _core.DestroyBuffer(rbuf.Buf, rbuf.Mem);
            _vk.DestroyCommandPool(_dev, f.Pool, null);
            _vk.DestroyFence(_dev, f.Fence, null);
            _vk.DestroySemaphore(_dev, f.Acquire, null);
            _vk.DestroySemaphore(_dev, f.RenderDone, null);
            foreach (var p in f.DescPools) _vk.DestroyDescriptorPool(_dev, p, null);
            _core.DestroyBuffer(f.Ring, f.RingMem);
            foreach (var (b, m) in f.Overflow) _core.DestroyBuffer(b, m);
            foreach (var a in f.Deferred) a();
        }
        DestroyPersistentSets();
        DestroyMsaa();
        DestroyGpuTime();
        DestroyPipelines();
        DestroyTextures();
        foreach (var rt in _renderTargets.Values)
        {
            _vk.DestroyImageView(_dev, rt.View, null);
            if (rt.SampledView.Handle != 0) _vk.DestroyImageView(_dev, rt.SampledView, null);
            if (rt.SrgbView.Handle != 0) _vk.DestroyImageView(_dev, rt.SrgbView, null);
            if (rt.RawView.Handle != 0) _vk.DestroyImageView(_dev, rt.RawView, null);
            _vk.DestroyImage(_dev, rt.Image, null);
        }
        foreach (var d in _depthTargets.Values)
        {
            _vk.DestroyImageView(_dev, d.View, null);
            _vk.DestroyImage(_dev, d.Image, null);
        }
        DestroySwapchain();
        _imagePool.Dispose();
        _compiler.Dispose();
        _core.Dispose();
    }
}
