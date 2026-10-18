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
    public bool VSync = true;
    /// <summary>Debug: log every draw of this presented frame number (-1 = off).</summary>
    public long TraceDrawsFrame = -1;
    /// <summary>When set, every dumped frame also adds a 240x136 thumbnail here (contact sheets for tests).</summary>
    public List<(long Frame, byte[] Rgba)>? Thumbnails;
    /// <summary>Automated tests: decode every uploaded texture on the CPU and warn when it looks like garbage.</summary>
    public bool InspectTextures;
}

/// <summary>GXM command executor on Vulkan 1.3 (dynamic rendering + extended dynamic state).</summary>
public sealed unsafe partial class VulkanRenderer : IGpuBackend
{
    const int FramesInFlight = 2;
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
        /// <summary>Render targets copied out this frame, written into guest memory once the frame completes.</summary>
        public readonly List<RtWriteback> Writebacks = new();
        /// <summary>Scene notifications signalled once this frame (and its writebacks) completed.</summary>
        public readonly List<(uint Address, uint Value)> Notifications = new();
        public bool Retired;
        /// <summary>Occlusion queries of this frame's visibility tests.</summary>
        public VisibilityFrame? Visibility;
    }

    readonly Frame[] _frames = new Frame[FramesInFlight];
    int _frameIndex;
    Frame F => _frames[_frameIndex];
    CommandBuffer Cmd => F.Cmd;
    public long FrameCount { get; private set; }

    public VulkanRenderer(VulkanRendererOptions options)
    {
        _opt = options;
        _core = new VkCore(options.Surface, options.Validation);
        _vk = _core.Vk;
        _dev = _core.Device;
        _imagePool = new ImageMemoryPool(_core);
        _compiler = new ShaderCompiler(options.ShaderCacheDir);
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
        RetireFrame(f);
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
        _vk.ResetCommandPool(_dev, f.Pool, 0);
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(f.Cmd, in bi);
        f.Recording = true;
        ResetVisibility(f);
        _boundPipeline = default;
    }

    void SubmitFrame(bool present, uint swapIndex)
    {
        var f = F;
        if (!f.Recording) return;
        EndRendering();
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
        _vk.QueueSubmit(_core.Queue, 1, in si, f.Fence).Ok("vkQueueSubmit");
        f.Recording = false;
        f.Submitted = true;
        if (present) PresentSwapchain(swapIndex, done);
        _frameIndex = (_frameIndex + 1) % FramesInFlight;
        FrameCount++;
    }

    /// <summary>
    /// Waits for a submitted frame and publishes its results: dumps, render-target writebacks into guest memory,
    /// visibility results and the scene notifications that were deferred until the GPU really finished.
    /// </summary>
    void RetireFrame(Frame f)
    {
        if (!f.Submitted || f.Retired) return;
        _vk.WaitForFences(_dev, 1, in f.Fence, true, ulong.MaxValue);
        f.Retired = true;
        if (f.Readback is { } rb) WriteDump(f, rb);
        CompleteWritebacks(f);
        CompleteVisibility(f);
        foreach (var (addr, value) in f.Notifications) GpuQueue.SignalNotification(addr, value);
        f.Notifications.Clear();
    }

    bool HasPendingResults(Frame f) => f.Writebacks.Count > 0 || f.Notifications.Count > 0;

    /// <summary>sceGxmFinish: submit what is recorded and retire every frame in flight.</summary>
    void Finish()
    {
        if (F.Recording) { SubmitFrame(false, 0); _drawsSinceSubmit = 0; }
        foreach (var f in _frames) RetireFrame(f);
    }

    public void Idle()
    {
        // keep latency low when the guest stops producing work (loading screens etc.)
        bool resultsRecorded = F.Recording && HasPendingResults(F);
        if (F.Recording && (!_inScene || resultsRecorded) && (_drawsSinceSubmit > 0 || resultsRecorded)) { SubmitFrame(false, 0); _drawsSinceSubmit = 0; }
        // the guest may be waiting for results of a submitted frame: publish them now that nothing else is queued
        foreach (var f in _frames)
            if (f.Submitted && !f.Retired && HasPendingResults(f)) RetireFrame(f);
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
    }

    readonly Dictionary<(uint, uint, uint, uint), RenderTarget> _renderTargets = new();
    readonly Dictionary<(uint, uint, uint), DepthTarget> _depthTargets = new();

    (Image, ImageView, ImageMemoryPool.Allocation) CreateImage(uint w, uint h, Format format, ImageUsageFlags usage, ImageAspectFlags aspect,
        uint mips = 1, uint layers = 1, bool mutable = false, bool cube = false, ComponentMapping? swizzle = null)
    {
        var ici = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(Math.Max(w, 1), Math.Max(h, 1), 1),
            MipLevels = mips,
            ArrayLayers = layers,
            Samples = SampleCountFlags.Count1Bit,
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
        foreach (var (k, old) in _renderTargets.Where(kv => kv.Key.Item1 == c.Data).ToList())
        {
            _renderTargets.Remove(k);
            DestroyLater(old);
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
        return rt;
    }

    void DestroyLater(RenderTarget rt)
    {
        var f = F;
        f.Deferred.Add(() =>
        {
            _vk.DestroyImageView(_dev, rt.View, null);
            if (rt.SampledView.Handle != 0) _vk.DestroyImageView(_dev, rt.SampledView, null);
            if (rt.SrgbView.Handle != 0) _vk.DestroyImageView(_dev, rt.SrgbView, null);
            _vk.DestroyImage(_dev, rt.Image, null);
            _imagePool.Free(rt.Mem);
        });
    }

    DepthTarget GetDepthTarget(DepthSurfaceInfo? d, uint w, uint h)
    {
        uint addr = d?.DepthData ?? 0;
        var key = (addr != 0 ? addr : d?.StencilData ?? 0, w, h);
        if (_depthTargets.TryGetValue(key, out var dt)) return dt;
        var (img, view, mem) = CreateImage(w, h, _core.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit);
        dt = new DepthTarget
        {
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
            var (buf, off) = UploadSpan(GuestMemory.Span(rt.Address, (int)(rowBytes * rt.Height)), 16);
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
        BeginFrameIfNeeded();
        EndRendering();
        _scene = b;
        _inScene = true;
        _rt = b.Color != null ? GetRenderTarget(b.Color) : null;
        // surface gamma: shader output is linear and gets sRGB-encoded on write
        var srgb = _rt != null ? Formats.Srgb(_rt.Format) : Format.Undefined;
        _rtGamma = _rt != null && b.Color!.GammaMode != 0 && srgb != _rt.Format && _core.SupportsColorAttachment(srgb);
        _rtAttachFormat = _rt == null ? Format.Undefined : _rtGamma ? srgb : _rt.Format;
        if (_rtGamma && _rt!.SrgbView.Handle == 0) _rt.SrgbView = CreateView(_rt.Image, srgb, ImageAspectFlags.ColorBit, 1, 1, false, default);
        if (b.Color is { } cs) Log.DebugOnce($"scene{cs.Data:X}{cs.Width}{cs.ColorFormat:X}", $"[gpu] scene target 0x{cs.Data:X8} {cs.Width}x{cs.Height} stride {cs.StrideInPixels} fmt 0x{cs.ColorFormat:X8} type 0x{cs.SurfaceType:X} gamma {cs.GammaMode}");
        if (b.Depth is { } ds) Log.DebugOnce($"scened{ds.DepthData:X}", $"[gpu] scene depth 0x{ds.DepthData:X8} stencil 0x{ds.StencilData:X8} stride {ds.StrideInSamples} fmt 0x{ds.Format:X8} type 0x{ds.Type:X8} ({b.Width}x{b.Height})");
        else Log.WarnOnce("scene-nocolor", "[gpu] scene without colour surface");
        _sceneW = _rt != null ? Math.Min(b.Width, _rt.Width) : b.Width;
        _sceneH = _rt != null ? Math.Min(b.Height, _rt.Height) : b.Height;
        _depth = GetDepthTarget(b.Depth, Math.Max(b.Width, _rt?.Width ?? 0), Math.Max(b.Height, _rt?.Height ?? 0));
        _sceneSerial++;
        if (_rt != null) _rt.LastScene = _sceneSerial;
        _depth.LastScene = _sceneSerial;
        _depth.LastUsedFrame = FrameCount;
        _depthNeedsClear = b.Depth == null || !b.Depth.ForceLoad;
    }

    void EndScene(EndSceneCommand e)
    {
        EndRendering();
        bool writeback = _rt != null && ScheduleWriteback(_rt);
        // a scene whose pixels the CPU can read back signals its notifications only once they are in memory
        if (writeback && e.Notifications.Length > 0) { F.Notifications.AddRange(e.Notifications); e.NotificationsDeferred = true; }
        _inScene = false;
        _scene = null;
    }

    void BeginRendering()
    {
        if (_rendering || !_inScene) return;
        var color = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _rt == null ? default : _rtGamma ? _rt.SrgbView : _rt.View,
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
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(_sceneW, _sceneH)),
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

    void EndRendering()
    {
        if (!_rendering) return;
        _vk.CmdEndRendering(Cmd);
        _rendering = false;
    }

    /// <summary>Makes colour writes to <paramref name="rt"/> visible to shader reads / transfers.</summary>
    void SyncRenderTargetForRead(RenderTarget rt)
    {
        if (!rt.Dirty) return;
        EndRendering();
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

        _presentCount++;
        bool dump = _opt.DumpFramesDir != null && _presentCount % Math.Max(1, _opt.DumpEvery) == 0;
        if (dump) RecordReadback(src, frame.Width, frame.Height);
        else F.Readback = null;

        if (_opt.TraceDrawsFrame == _presentCount && _opt.ShaderCacheDir != null)
        {
            SubmitFrame(false, 0);
            DumpAllTargets(Path.Combine(_opt.ShaderCacheDir, "trace", "targets"));
            return;
        }
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

    /// <summary>
    /// The display flip happens once the frame's rendering is complete on the hardware, so anything the CPU reads
    /// after the flip (luminance buffers, picking results, notifications) must already be in memory.
    /// </summary>
    void RetireFlippedFrame()
    {
        var f = _frames[(_frameIndex + FramesInFlight - 1) % FramesInFlight];
        if (HasPendingResults(f)) RetireFrame(f);
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
        _readbackIsBgra = src.Format == Format.B8G8R8A8Unorm;
    }

    bool _readbackIsBgra;

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
        try
        {
            Directory.CreateDirectory(_opt.DumpFramesDir!);
            var pixels = new ReadOnlySpan<byte>((void*)rb.Ptr, (int)(rb.W * rb.H * 4)).ToArray();
            if (_readbackIsBgra)
                for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // the display ignores alpha
            Png.Write(Path.Combine(_opt.DumpFramesDir!, $"frame_{f.DumpIndex:D6}.png"), pixels, (int)rb.W, (int)rb.H);
            if (_opt.Thumbnails is { } thumbs)
                lock (thumbs) thumbs.Add((f.DumpIndex, Thumbnail(pixels, (int)rb.W, (int)rb.H, 240, 136)));
        }
        catch (Exception e) { Log.WarnOnce("dumpframe", $"frame dump failed: {e.Message}"); }
        f.Readback = null;
    }

    /// <summary>Debug: synchronously reads back every render target into PNGs.</summary>
    void DumpAllTargets(string dir)
    {
        _vk.DeviceWaitIdle(_dev);
        Directory.CreateDirectory(dir);
        var stats = new System.Text.StringBuilder();
        var pci = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _core.QueueFamily };
        _vk.CreateCommandPool(_dev, in pci, null, out var pool);
        foreach (var rt in _renderTargets.Values)
        {
            bool half = rt.Format == Format.R16G16B16A16Sfloat;
            var (buf, mem, ptr) = _core.CreateHostBuffer(rt.Width * rt.Height * (half ? 8u : 4u), BufferUsageFlags.TransferDstBit);
            var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            _vk.AllocateCommandBuffers(_dev, in ai, out var cmd);
            var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _vk.BeginCommandBuffer(cmd, in bi);
            var region = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D(rt.Width, rt.Height, 1) };
            if (half || Formats.ColorBytesPerPixel(rt.ColorFormat) == 4 || rt.Format is Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm)
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
            else px = new ReadOnlySpan<byte>((void*)ptr, (int)(rt.Width * rt.Height * 4)).ToArray();
            if (rt.Format == Format.B8G8R8A8Unorm) for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
            var opaque = (byte[])px.Clone();
            for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
            Png.Write(Path.Combine(dir, $"rt_{rt.Address:X8}_{rt.Width}x{rt.Height}.png"), opaque, (int)rt.Width, (int)rt.Height);
            _core.DestroyBuffer(buf, mem);
        }
        File.WriteAllText(Path.Combine(dir, "targets.txt"), stats.ToString());
        _vk.DestroyCommandPool(_dev, pool, null);
        Log.Info($"[gpu] dumped {_renderTargets.Count} render targets to {dir}");
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
        _vk.DeviceWaitIdle(_dev);
        DestroyMirror();
        foreach (var fr in _frames) RetireFrame(fr);
        foreach (var fr in _frames) DestroyVisibility(fr);
        DestroyWritebacks();
        foreach (var f in _frames)
        {
            if (f.Submitted && f.Readback is { } rb) WriteDump(f, rb);
            _vk.DestroyCommandPool(_dev, f.Pool, null);
            _vk.DestroyFence(_dev, f.Fence, null);
            _vk.DestroySemaphore(_dev, f.Acquire, null);
            _vk.DestroySemaphore(_dev, f.RenderDone, null);
            foreach (var p in f.DescPools) _vk.DestroyDescriptorPool(_dev, p, null);
            _core.DestroyBuffer(f.Ring, f.RingMem);
            foreach (var (b, m) in f.Overflow) _core.DestroyBuffer(b, m);
            foreach (var a in f.Deferred) a();
        }
        DestroyPipelines();
        DestroyTextures();
        foreach (var rt in _renderTargets.Values)
        {
            _vk.DestroyImageView(_dev, rt.View, null);
            if (rt.SampledView.Handle != 0) _vk.DestroyImageView(_dev, rt.SampledView, null);
            if (rt.SrgbView.Handle != 0) _vk.DestroyImageView(_dev, rt.SrgbView, null);
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
