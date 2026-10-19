using System.Collections.Concurrent;
using System.Numerics;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceGxm: GPU command recording. Draws become <see cref="GpuCommand"/>s executed by the GPU worker.</summary>
public sealed class Gxm : HleModule
{
    const int ERR_UNINITIALIZED = unchecked((int)0x805B0000), ERR_ALREADY_INITIALIZED = unchecked((int)0x805B0001),
        ERR_INVALID_VALUE = unchecked((int)0x805B0003), ERR_INVALID_POINTER = unchecked((int)0x805B0004),
        ERR_INVALID_ALIGNMENT = unchecked((int)0x805B0005), ERR_NOT_WITHIN_SCENE = unchecked((int)0x805B0006),
        ERR_WITHIN_SCENE = unchecked((int)0x805B0007), ERR_NULL_PROGRAM = unchecked((int)0x805B0008),
        ERR_UNSUPPORTED = unchecked((int)0x805B0009), ERR_RESERVE_FAILED = unchecked((int)0x805B000B),
        // Vita3K gxm/types.h only (absent from vita-headers)
        ERR_WITHIN_COMMAND_LIST = unchecked((int)0x805B002C), ERR_NOT_WITHIN_COMMAND_LIST = unchecked((int)0x805B002D);

    const uint TexLinearStrided = 0xC0000000, TexSwizzled = 0, TexCube = 0x40000000, TexLinear = 0x60000000, TexTiled = 0x80000000,
        TexSwizzledArbitrary = 0xA0000000, TexCubeArbitrary = 0xE0000000;

    readonly ConcurrentDictionary<uint, GxmContext> _contexts = new();
    readonly ConcurrentDictionary<uint, GxmRenderTarget> _renderTargets = new();
    readonly ConcurrentDictionary<uint, GxmShaderPatcher> _patchers = new();
    readonly ConcurrentDictionary<uint, GxmVertexProgram> _vertexPrograms = new();
    readonly ConcurrentDictionary<uint, GxmFragmentProgram> _fragmentPrograms = new();
    readonly ConcurrentDictionary<uint, GxmSyncObject> _syncObjects = new();
    readonly ConcurrentDictionary<uint, (uint Base, uint Size)> _mapped = new();

    bool _initialized;
    uint _notificationRegion, _parameterBufferSize;
    uint _displayCallback, _displayDataSize;
    BlockingCollection<(uint Data, long GpuSeq)>? _displayQueue;
    int _displayPending;
    readonly object _displayLock = new();
    GuestThread? _displayThread;

    GpuQueue Gpu => Emu.Gpu;

    uint AllocObj(uint size, string name)
    {
        uint a = Emu.Memory.Alloc(Math.Max(size, 16), name, MemoryRegion.User, 16);
        GuestMemory.Fill(a, 0, Math.Max(size, 16));
        return a;
    }

    // =========================================================== init / memory mapping

    [Hle]
    int sceGxmInitialize(uint pParams)
    {
        if (pParams == 0) return ERR_INVALID_POINTER;
        if (_initialized) return ERR_ALREADY_INITIALIZED;
        uint pendingMax = GuestMemory.R32(pParams + 4);
        _parameterBufferSize = GuestMemory.R32(pParams + 16);
        _displayCallback = GuestMemory.R32(pParams + 8);
        _displayDataSize = GuestMemory.R32(pParams + 12);
        _notificationRegion = Emu.Memory.Alloc(1 << 20, "SceGxmNotificationRegion", MemoryRegion.User);
        GuestMemory.Fill(_notificationRegion, 0, 1 << 20);
        int capacity = (int)Math.Max(Math.Min(pendingMax, 3u) - 1, 1u);
        _displayQueue = new BlockingCollection<(uint, long)>(capacity);
        var t = K.CreateThread("SceGxmDisplayQueue", 0, 64, 0x4000, 0, 0);
        t.HostBody = DisplayQueueThread;
        _displayThread = t;
        t.Start(0, 0);
        _initialized = true;
        Log.Info($"sceGxmInitialize: displayQueue pending={pendingMax} callback=0x{_displayCallback:X8} dataSize={_displayDataSize}");
        return 0;
    }

    void DisplayQueueThread(GuestThread self)
    {
        while (true)
        {
            (uint data, long seq) entry;
            using (Scheduler.Block()) // blocked on the queue: do not hold an emulated core
            {
                try { entry = _displayQueue!.Take(); }
                catch (InvalidOperationException) { break; } // queue completed
            }
            var (data, seq) = entry;
            Gpu.WaitFor(seq);
            if (!Emu.Running && Emu.MainModule != null && K.MainThread?.Status == ThreadStatus.Dormant) break;
            try
            {
                if (_displayCallback != 0) self.CallGuest(_displayCallback, data);
            }
            catch (GuestThreadExitException) { break; }
            catch (Exception e) { Log.Error($"display queue callback failed: {e.Message}"); }
            finally
            {
                Emu.Memory.Free(data);
                lock (_displayLock)
                {
                    _displayPending--;
                    Monitor.PulseAll(_displayLock);
                }
            }
        }
    }

    [Hle]
    int sceGxmTerminate()
    {
        _displayQueue?.CompleteAdding();
        _initialized = false;
        return 0;
    }

    [Hle] uint sceGxmGetNotificationRegion() => _notificationRegion;

    [Hle]
    int sceGxmMapMemory(uint baseAddr, uint size, uint attribs)
    {
        if (baseAddr == 0) return ERR_INVALID_POINTER;
        _mapped[baseAddr] = (baseAddr, size);
        return 0;
    }

    [Hle] int sceGxmUnmapMemory(uint baseAddr) => _mapped.TryRemove(baseAddr, out _) ? 0 : ERR_INVALID_VALUE;

    [Hle]
    int sceGxmMapVertexUsseMemory(uint baseAddr, uint size, Ptr<uint> offset)
    {
        if (baseAddr == 0 || offset.Address == 0) return ERR_INVALID_POINTER;
        offset.Value = baseAddr;
        return 0;
    }

    [Hle]
    int sceGxmMapFragmentUsseMemory(uint baseAddr, uint size, Ptr<uint> offset)
    {
        if (baseAddr == 0 || offset.Address == 0) return ERR_INVALID_POINTER;
        offset.Value = baseAddr;
        return 0;
    }

    [Hle] int sceGxmUnmapVertexUsseMemory(uint baseAddr) => 0;
    [Hle] int sceGxmUnmapFragmentUsseMemory(uint baseAddr) => 0;
    [Hle] int sceGxmPadHeartbeat(uint displaySurface, uint displaySync) => 0;
    /// <summary>SCE_GXM_WCLAMP_MODE_ENABLED = 0x8000. Stored in the draw state; the clamp itself is not applied by the
    /// renderer yet (Vulkan clipping already handles W &lt;= 0).</summary>
    [Hle] void sceGxmSetWClampEnable(uint ctx, uint enable) { if (Ctx(ctx) is { } c) c.State.WClampEnabled = enable != 0; }
    [Hle] void sceGxmSetWClampValue(uint ctx, float value) { if (Ctx(ctx) is { } c) c.State.WClampValue = value; }
    [Hle] int sceGxmIsDebugVersion() => 0;
    [Hle] int sceGxmSetValidationEnable(uint ctx, uint enable) => 0;
    /// <summary>Inferred (not in vita-headers): toggles a warning class of the debug libgxm; a no-op in release.</summary>
    [Hle] int sceGxmSetWarningEnabled(uint warning, uint enable) => 0;

    /// <summary>Parameter buffer threshold: VitaSharp has no parameter buffer (never partial-renders), so the size given to
    /// sceGxmInitialize is reported (actual hardware fraction unknown).</summary>
    [Hle]
    int sceGxmGetParameterBufferThreshold(Ptr<uint> size)
    {
        if (size.IsNull) return ERR_INVALID_POINTER;
        size.Value = _parameterBufferSize;
        return 0;
    }

    /// <summary>Inferred: an internal libgxm/GLES driver event wait. Logged once so a game importing it reveals its
    /// arguments; returns immediately.</summary>
    [Hle]
    int sceGxmWaitEvent(uint a0, uint a1, uint a2, uint a3)
    {
        Log.WarnOnce("gxm-waitevent", $"sceGxmWaitEvent(0x{a0:X8}, 0x{a1:X8}, 0x{a2:X8}, 0x{a3:X8})");
        return 0;
    }

    // ---- Razor user markers: debug grouping of draws, no visual effect

    [Hle]
    int sceGxmPushUserMarker(uint ctx, uint tag)
    {
        if (Ctx(ctx) is not { } c) return ERR_INVALID_POINTER;
        c.MarkerDepth++;
        return 0;
    }

    [Hle]
    int sceGxmPopUserMarker(uint ctx)
    {
        if (Ctx(ctx) is not { } c) return ERR_INVALID_POINTER;
        if (c.MarkerDepth > 0) c.MarkerDepth--;
        return 0;
    }

    [Hle] int sceGxmSetUserMarker(uint ctx, uint tag) => Ctx(ctx) == null ? ERR_INVALID_POINTER : 0;

    // =========================================================== contexts / render targets / sync

    [Hle]
    int sceGxmCreateContext(uint p, Ptr<uint> outContext)
    {
        if (p == 0 || outContext.Address == 0) return ERR_INVALID_POINTER;
        uint h = AllocObj(64, "SceGxmContext");
        var c = new GxmContext
        {
            Handle = h,
            VdmRing = GuestMemory.R32(p + 8), VdmRingSize = GuestMemory.R32(p + 12),
            VertexRing = GuestMemory.R32(p + 16), VertexRingSize = GuestMemory.R32(p + 20),
            FragmentRing = GuestMemory.R32(p + 24), FragmentRingSize = GuestMemory.R32(p + 28),
        };
        _contexts[h] = c;
        outContext.Value = h;
        return 0;
    }

    [Hle] int sceGxmDestroyContext(uint ctx) { Gpu.WaitIdle(); _contexts.TryRemove(ctx, out _); return 0; }

    /// <summary>SceGxmContextType: IMMEDIATE 0, DEFERRED 1 (Vita3K gxm/types.h).</summary>
    [Hle]
    int sceGxmGetContextType(uint ctx, Ptr<uint> type)
    {
        if (Ctx(ctx) is not { } c || type.IsNull) return ERR_INVALID_POINTER;
        type.Value = c.Deferred ? 1u : 0u;
        return 0;
    }

    [Hle]
    int sceGxmGetRenderTargetMemSize(uint p, Ptr<uint> size)
    {
        if (p == 0 || size.Address == 0) return ERR_INVALID_POINTER;
        size.Value = 0x10000;
        return 0;
    }

    [Hle]
    int sceGxmCreateRenderTarget(uint p, Ptr<uint> outRt)
    {
        if (p == 0 || outRt.Address == 0) return ERR_INVALID_POINTER;
        uint h = AllocObj(32, "SceGxmRenderTarget");
        var rt = new GxmRenderTarget
        {
            Handle = h,
            Width = GuestMemory.Read<ushort>(p + 4), Height = GuestMemory.Read<ushort>(p + 6),
            ScenesPerFrame = GuestMemory.Read<ushort>(p + 8), MultisampleMode = GuestMemory.Read<ushort>(p + 10),
            DriverMemBlock = (int)GuestMemory.R32(p + 16),
        };
        _renderTargets[h] = rt;
        outRt.Value = h;
        return 0;
    }

    [Hle] int sceGxmDestroyRenderTarget(uint rt) { _renderTargets.TryRemove(rt, out _); return 0; }

    [Hle]
    int sceGxmRenderTargetGetDriverMemBlock(uint rt, Ptr<int> block)
    {
        if (!_renderTargets.TryGetValue(rt, out var r) || block.Address == 0) return ERR_INVALID_POINTER;
        block.Value = r.DriverMemBlock;
        return 0;
    }

    [Hle]
    int sceGxmSyncObjectCreate(Ptr<uint> outSync)
    {
        if (outSync.Address == 0) return ERR_INVALID_POINTER;
        uint h = AllocObj(16, "SceGxmSyncObject");
        _syncObjects[h] = new GxmSyncObject { Handle = h };
        outSync.Value = h;
        return 0;
    }

    [Hle] int sceGxmSyncObjectDestroy(uint sync) { _syncObjects.TryRemove(sync, out _); return 0; }

    GxmContext? Ctx(uint h) => _contexts.TryGetValue(h, out var c) ? c : null;

    // =========================================================== surfaces

    [Hle]
    int sceGxmColorSurfaceInit(uint surface, uint colorFormat, uint surfaceType, uint scaleMode, uint outputRegisterSize,
        uint width, uint height, uint strideInPixels, uint data)
    {
        if (surface == 0 || data == 0) return ERR_INVALID_POINTER;
        if (width == 0 || width > 4096 || height == 0 || height > 4096) return ERR_INVALID_VALUE;
        if ((strideInPixels & 1) != 0) return ERR_INVALID_ALIGNMENT;
        if (strideInPixels < width || (data & 3) != 0) return ERR_INVALID_VALUE;
        GuestMemory.Fill(surface, 0, 48);
        GuestMemory.W32(surface, scaleMode == 1 ? 2u : 0u);
        GuestMemory.W32(surface + 4, width);
        GuestMemory.W32(surface + 8, height);
        GuestMemory.W32(surface + 12, strideInPixels);
        GuestMemory.W32(surface + 16, data);
        GuestMemory.W32(surface + 20, colorFormat);
        GuestMemory.W32(surface + 24, surfaceType);
        GuestMemory.W32(surface + 28, outputRegisterSize & 1);
        WriteClip(surface, 0, 0, width - 1, height - 1);
        uint texType = surfaceType switch { 0x08000000 => TexSwizzled, 0x04000000 => TexTiled, _ => TexLinear };
        InitTexture(surface + 32, data, ColorToTextureFormat(colorFormat), width, height, 1, texType);
        return 0;
    }

    static uint ColorToTextureFormat(uint colorFormat)
    {
        // base formats differ; the common ones map 1:1 on the swizzle field
        uint baseFmt = colorFormat & 0xF1800000;
        uint swz = (colorFormat >> 20) & 7;
        uint texBase = baseFmt switch
        {
            0x00000000 => 0x0C000000, 0x10000000 => 0x98000000, 0x30000000 => 0x05000000, 0x40000000 => 0x04000000,
            0x50000000 => 0x02000000, 0x60000000 => 0x03000000, 0xF0000000 => 0x0B000000, 0x00800000 => 0x11000000,
            0x10800000 => 0x12000000, 0x20800000 => 0x0A000000, 0x30800000 => 0x10000000, 0x40800000 => 0x09000000,
            0x50800000 => 0x0F000000, 0x60800000 => 0x0E000000, 0x80800000 => 0x00000000, 0x90800000 => 0x01000000,
            0xA0800000 => 0x06000000, 0xB0800000 => 0x07000000, 0xC0800000 => 0x08000000, 0xD0800000 => 0x14000000,
            0xE0800000 => 0x0D000000, 0x01000000 => 0x1B000000, 0x11000000 => 0x1E000000, 0x21000000 => 0x1A000000,
            0x31000000 => 0x19000000, 0x41000000 => 0x9A000000, _ => 0x0C000000,
        };
        return texBase | (swz << 12);
    }

    [Hle] int sceGxmColorSurfaceInitDisabled(uint surface) { GuestMemory.Fill(surface, 0, 48); GuestMemory.W32(surface, 1); return 0; }

    // Colour surface clip (VitaSharp layout): w0 bits 8-19 xMax, bits 20-31 yMax; +0x1C (outputRegisterSize in bit 0)
    // bits 8-19 xMin, bits 20-31 yMin. Kept in the guest struct because games copy surfaces by value. The renderer does
    // not apply it yet (it would intersect the scissor with it).
    static void WriteClip(uint surface, uint xMin, uint yMin, uint xMax, uint yMax)
    {
        uint w0 = GuestMemory.R32(surface) & 0xFFu, w7 = GuestMemory.R32(surface + 0x1C) & 0xFFu;
        GuestMemory.W32(surface, w0 | ((xMax & 0xFFF) << 8) | ((yMax & 0xFFF) << 20));
        GuestMemory.W32(surface + 0x1C, w7 | ((xMin & 0xFFF) << 8) | ((yMin & 0xFFF) << 20));
    }

    [Hle]
    void sceGxmColorSurfaceSetClip(uint surface, uint xMin, uint yMin, uint xMax, uint yMax)
    {
        if (surface == 0) return;
        uint w = GuestMemory.R32(surface + 4), h = GuestMemory.R32(surface + 8);
        if (w > 0) { xMin = Math.Min(xMin, w - 1); xMax = Math.Min(xMax, w - 1); }
        if (h > 0) { yMin = Math.Min(yMin, h - 1); yMax = Math.Min(yMax, h - 1); }
        WriteClip(surface, xMin, yMin, xMax, yMax);
    }

    [Hle]
    void sceGxmColorSurfaceGetClip(uint surface, Ptr<uint> xMin, Ptr<uint> yMin, Ptr<uint> xMax, Ptr<uint> yMax)
    {
        if (surface == 0) return;
        uint w0 = GuestMemory.R32(surface), w7 = GuestMemory.R32(surface + 0x1C);
        xMin.TrySet((w7 >> 8) & 0xFFF); yMin.TrySet((w7 >> 20) & 0xFFF);
        xMax.TrySet((w0 >> 8) & 0xFFF); yMax.TrySet((w0 >> 20) & 0xFFF);
    }

    [Hle]
    int sceGxmColorSurfaceSetGammaMode(uint surface, uint mode)
    {
        uint w = GuestMemory.R32(surface) & ~0xCu;
        GuestMemory.W32(surface, w | (((mode >> 12) & 3) << 2));
        return 0;
    }

    [Hle] uint sceGxmColorSurfaceGetGammaMode(uint surface) => ((GuestMemory.R32(surface) >> 2) & 3) << 12;
    [Hle] uint sceGxmColorSurfaceGetData(uint surface) => GuestMemory.R32(surface + 16);
    [Hle] int sceGxmColorSurfaceSetData(uint surface, uint data) { GuestMemory.W32(surface + 16, data); GuestMemory.W32(surface + 40, data); return 0; }
    [Hle] uint sceGxmColorSurfaceGetFormat(uint surface) => GuestMemory.R32(surface + 20);
    [Hle] int sceGxmColorSurfaceSetFormat(uint surface, uint f) { GuestMemory.W32(surface + 20, f); return 0; }
    [Hle] uint sceGxmColorSurfaceGetType(uint surface) => GuestMemory.R32(surface + 24);
    [Hle] uint sceGxmColorSurfaceGetStrideInPixels(uint surface) => GuestMemory.R32(surface + 12);
    [Hle] bool sceGxmColorSurfaceIsEnabled(uint surface) => (GuestMemory.R32(surface) & 1) == 0;
    [Hle] uint sceGxmColorSurfaceGetScaleMode(uint surface) => (GuestMemory.R32(surface) >> 1) & 1;
    [Hle] void sceGxmColorSurfaceSetScaleMode(uint surface, uint mode) { uint w = GuestMemory.R32(surface) & ~2u; GuestMemory.W32(surface, w | (mode != 0 ? 2u : 0)); }
    /// <summary>Dither mode (SCE_GXM_COLOR_SURFACE_DITHER_ENABLED = 8) kept in w0 bit 4 (bits 2-3 are the gamma mode).
    /// Vulkan has no fixed-function dither, so it has no visual effect.</summary>
    [Hle] uint sceGxmColorSurfaceGetDitherMode(uint surface) => surface != 0 && ((GuestMemory.R32(surface) >> 4) & 1) != 0 ? 8u : 0u;

    [Hle]
    int sceGxmColorSurfaceSetDitherMode(uint surface, uint mode)
    {
        if (surface == 0) return ERR_INVALID_POINTER;
        uint w = GuestMemory.R32(surface) & ~0x10u;
        GuestMemory.W32(surface, w | (mode != 0 ? 0x10u : 0));
        return 0;
    }

    static ColorSurfaceInfo? ReadColorSurface(uint s)
    {
        if (s == 0) return null;
        uint w0 = GuestMemory.R32(s);
        if ((w0 & 1) != 0) return null;
        return new ColorSurfaceInfo(GuestMemory.R32(s + 16), GuestMemory.R32(s + 4), GuestMemory.R32(s + 8), GuestMemory.R32(s + 12),
            GuestMemory.R32(s + 20), GuestMemory.R32(s + 24), (w0 & 2) != 0, (w0 >> 2) & 3);
    }

    [Hle]
    int sceGxmDepthStencilSurfaceInit(uint surface, uint format, uint surfaceType, uint strideInSamples, uint depthData, uint stencilData)
    {
        if (surface == 0) return ERR_INVALID_POINTER;
        if (strideInSamples == 0 || (strideInSamples % 32) != 0) return ERR_INVALID_VALUE;
        uint taf = ((surfaceType >> 12) & 0x11) | 0x100 | ((format >> 12) & 0x7EEE);
        uint w0 = 1u | (((strideInSamples >> 5) - 1) & 0xFF) << 3 | (taf << 12);
        GuestMemory.W32(surface, w0);
        GuestMemory.W32(surface + 4, depthData);
        GuestMemory.W32(surface + 8, stencilData);
        GuestMemory.Write(surface + 12, 1.0f);
        GuestMemory.W32(surface + 16, (1u << 8) | (1u << 9));
        return 0;
    }

    [Hle]
    int sceGxmDepthStencilSurfaceInitDisabled(uint surface)
    {
        GuestMemory.Fill(surface, 0, 20);
        GuestMemory.Write(surface + 12, 1.0f);
        GuestMemory.W32(surface + 16, (1u << 8) | (1u << 9));
        return 0;
    }

    [Hle] uint sceGxmDepthStencilSurfaceGetForceLoadMode(uint s) => (GuestMemory.R32(s) & 2) != 0 ? 2u : 0u;
    [Hle] uint sceGxmDepthStencilSurfaceGetForceStoreMode(uint s) => (GuestMemory.R32(s) & 4) != 0 ? 4u : 0u;
    [Hle] void sceGxmDepthStencilSurfaceSetForceLoadMode(uint s, uint mode) { uint w = GuestMemory.R32(s) & ~2u; GuestMemory.W32(s, w | (mode != 0 ? 2u : 0)); }
    [Hle] void sceGxmDepthStencilSurfaceSetForceStoreMode(uint s, uint mode) { uint w = GuestMemory.R32(s) & ~4u; GuestMemory.W32(s, w | (mode != 0 ? 4u : 0)); }
    [Hle] float sceGxmDepthStencilSurfaceGetBackgroundDepth(uint s) => GuestMemory.Read<float>(s + 12);
    [Hle] void sceGxmDepthStencilSurfaceSetBackgroundDepth(uint s, float depth) => GuestMemory.Write(s + 12, depth);
    [Hle] uint sceGxmDepthStencilSurfaceGetBackgroundStencil(uint s) => GuestMemory.R32(s + 16) & 0xFF;
    [Hle] void sceGxmDepthStencilSurfaceSetBackgroundStencil(uint s, uint v) { uint w = GuestMemory.R32(s + 16) & ~0xFFu; GuestMemory.W32(s + 16, w | (v & 0xFF)); }
    [Hle] bool sceGxmDepthStencilSurfaceGetBackgroundMask(uint s) => (GuestMemory.R32(s + 16) & 0x100) != 0;
    [Hle] void sceGxmDepthStencilSurfaceSetBackgroundMask(uint s, bool m) { uint w = GuestMemory.R32(s + 16) & ~0x100u; GuestMemory.W32(s + 16, w | (m ? 0x100u : 0)); }
    [Hle] bool sceGxmDepthStencilSurfaceIsEnabled(uint s) => ((GuestMemory.R32(s) >> 12) & 0x7EEE) != 0;
    [Hle] uint sceGxmDepthStencilSurfaceGetFormat(uint s) => ((GuestMemory.R32(s) >> 12) & 0x7EEE) << 12;
    [Hle] uint sceGxmDepthStencilSurfaceGetStrideInSamples(uint s) => (((GuestMemory.R32(s) >> 3) & 0xFF) + 1) << 5;

    static DepthSurfaceInfo? ReadDepthSurface(uint s)
    {
        if (s == 0) return null;
        uint w0 = GuestMemory.R32(s);
        uint taf = w0 >> 12;
        uint format = (taf & 0x7EEE) << 12;
        if (format == 0) return null;
        uint w4 = GuestMemory.R32(s + 16);
        return new DepthSurfaceInfo(GuestMemory.R32(s + 4), GuestMemory.R32(s + 8), (((w0 >> 3) & 0xFF) + 1) << 5, format, (taf & 0x11) << 12,
            GuestMemory.Read<float>(s + 12), (byte)(w4 & 0xFF), (w0 & 2) != 0, (w0 & 4) != 0);
    }

    // =========================================================== scenes

    void SetDefaultRegionClipAndViewport(GxmContext c, uint xMax, uint yMax)
    {
        ref var s = ref c.State;
        s.VpOffX = 0.5f * (1.0f + xMax);
        s.VpOffY = 0.5f * (1.0f + yMax);
        s.VpOffZ = 0.5f;
        s.VpScaleX = 0.5f * (1.0f + xMax);
        s.VpScaleY = -0.5f * (1.0f + yMax);
        s.VpScaleZ = 0.5f;
        s.RegionClipMode = 2;
        s.ClipMinX = 0; s.ClipMinY = 0; s.ClipMaxX = (int)xMax; s.ClipMaxY = (int)yMax;
    }

    [Hle]
    int sceGxmBeginScene(uint ctx, uint flags, uint renderTarget, uint validRegion, uint vertexSync, uint fragmentSync, uint colorSurface, uint depthStencil)
        => BeginScene(ctx, renderTarget, validRegion, fragmentSync, colorSurface, depthStencil);

    [Hle]
    int sceGxmBeginSceneEx(uint ctx, uint flags, uint renderTarget, uint validRegion, uint vertexSync, uint fragmentSync, uint colorSurface,
        uint loadDepthStencil, uint storeDepthStencil)
        => BeginScene(ctx, renderTarget, validRegion, fragmentSync, colorSurface, storeDepthStencil != 0 ? storeDepthStencil : loadDepthStencil);

    int BeginScene(uint ctx, uint renderTarget, uint validRegion, uint fragmentSync, uint colorSurface, uint depthStencil)
    {
        var c = Ctx(ctx);
        if (c == null || !_renderTargets.TryGetValue(renderTarget, out var rt)) return ERR_INVALID_POINTER;
        if (c.Deferred) return ERR_INVALID_VALUE;
        if (c.InScene) return ERR_WITHIN_SCENE;
        c.InScene = true;
        c.RenderTarget = rt;
        c.FragmentSyncObject = fragmentSync;
        uint xMax = validRegion != 0 ? GuestMemory.R32(validRegion) : rt.Width - 1u;
        uint yMax = validRegion != 0 ? GuestMemory.R32(validRegion + 4) : rt.Height - 1u;
        SetDefaultRegionClipAndViewport(c, xMax, yMax);
        Gpu.Submit(new BeginSceneCommand
        {
            Width = rt.Width, Height = rt.Height,
            Color = ReadColorSurface(colorSurface),
            Depth = ReadDepthSurface(depthStencil),
        });
        return 0;
    }

    static (uint, uint)? ReadNotification(uint p) => p == 0 ? null : (GuestMemory.R32(p), GuestMemory.R32(p + 4));

    [Hle]
    int sceGxmEndScene(uint ctx, uint vertexNotification, uint fragmentNotification)
    {
        var c = Ctx(ctx);
        if (c == null) return ERR_INVALID_POINTER;
        if (c.Deferred) return ERR_INVALID_VALUE;
        if (!c.InScene) return ERR_NOT_WITHIN_SCENE;
        c.InScene = false;
        var list = new List<(uint, uint)>();
        if (ReadNotification(vertexNotification) is { } vn && vn.Item1 != 0) list.Add(vn);
        if (ReadNotification(fragmentNotification) is { } fn && fn.Item1 != 0) list.Add(fn);
        long seq = Gpu.Submit(new EndSceneCommand { Notifications = list.ToArray() });
        // keep the guest at most a few scenes ahead of the GPU worker
        Gpu.WaitFor(seq - 4096);
        if (Gpu.Pending > 20000) Gpu.WaitFor(seq - 2000);
        return 0;
    }

    [Hle]
    int sceGxmMidSceneFlush(uint ctx, uint flags, uint vertexSync, uint vertexNotification)
    {
        if (Ctx(ctx) is { Deferred: true }) return ERR_INVALID_VALUE;
        Gpu.Submit(new MidSceneFlushCommand());
        if (ReadNotification(vertexNotification) is { } n && n.Item1 != 0) Gpu.Notify(n.Item1, n.Item2);
        return 0;
    }

    /// <summary>Vertex barrier inside a scene (Vita3K: a mid-scene flush without notification). The GPU queue already runs
    /// in order, so only the validation remains.</summary>
    [Hle]
    int sceGxmVertexFence(uint ctx)
    {
        if (Ctx(ctx) is not { } c) return ERR_INVALID_POINTER;
        if (c.Deferred) return ERR_INVALID_VALUE;
        return c.InScene ? 0 : ERR_NOT_WITHIN_SCENE;
    }

    [Hle] int sceGxmFinish(uint ctx) { GpuFinish(); return 0; }
    [Hle] int sceGxmTransferFinish() { GpuFinish(); return 0; }

    /// <summary>Waits until every submitted GPU operation completed and its results are in guest memory.</summary>
    void GpuFinish() => Gpu.WaitFor(Gpu.Submit(new FinishCommand()));

    [Hle]
    int sceGxmNotificationWait(uint notification)
    {
        if (notification == 0) return ERR_INVALID_POINTER;
        uint addr = GuestMemory.R32(notification), value = GuestMemory.R32(notification + 4);
        if (GuestMemory.R32(addr) != value)
        {
            using var blocked = Scheduler.Block();
            lock (Gpu.NotificationLock)
            {
                while (GuestMemory.R32(addr) != value && Emu.Running)
                    Monitor.Wait(Gpu.NotificationLock, 50);
            }
        }
        return 0;
    }

    [Hle]
    int sceGxmDisplayQueueAddEntry(uint oldBuffer, uint newBuffer, uint callbackData)
    {
        if (oldBuffer == 0 || newBuffer == 0) return ERR_INVALID_POINTER;
        if (_displayQueue == null) return ERR_UNINITIALIZED;
        uint size = Math.Max(_displayDataSize, 4);
        uint copy = Emu.Memory.Alloc(size, "gxm display data", MemoryRegion.User, 16);
        if (callbackData != 0) GuestMemory.Copy(copy, callbackData, _displayDataSize);
        lock (_displayLock) _displayPending++;
        // the queue has pendingCount-1 slots: when the render thread runs ahead it blocks here until the display thread
        // flips, and that wait must not hold an emulated core (it starved every lower-priority thread, e.g. God of
        // War's Bink audio thread, making the video audio port restart every 100 ms)
        using (Scheduler.Block()) _displayQueue.Add((copy, Gpu.Submitted));
        return 0;
    }

    [Hle]
    int sceGxmDisplayQueueFinish()
    {
        using var blocked = Scheduler.Block();
        lock (_displayLock)
            while (_displayPending > 0 && Emu.Running) Monitor.Wait(_displayLock, 50);
        return 0;
    }

    // =========================================================== state setters

    [Hle] void sceGxmSetVertexProgram(uint ctx, uint vp) { if (Ctx(ctx) is { } c) c.VertexProgram = _vertexPrograms.GetValueOrDefault(vp); }
    [Hle] void sceGxmSetFragmentProgram(uint ctx, uint fp) { if (Ctx(ctx) is { } c) c.FragmentProgram = _fragmentPrograms.GetValueOrDefault(fp); }

    [Hle]
    int sceGxmSetVertexStream(uint ctx, uint index, uint data)
    {
        var c = Ctx(ctx);
        if (c == null || data == 0) return ERR_INVALID_POINTER;
        if (index >= 16) return ERR_INVALID_VALUE;
        c.Streams[index] = data;
        return 0;
    }

    [Hle]
    int sceGxmSetFragmentTexture(uint ctx, uint index, uint texture)
    {
        var c = Ctx(ctx);
        if (c == null || texture == 0) return ERR_INVALID_POINTER;
        if (index >= 16) return ERR_INVALID_VALUE;
        c.FragmentTextures[index] = GxmTextureDesc.Read(texture);
        return 0;
    }

    [Hle]
    int sceGxmSetVertexTexture(uint ctx, uint index, uint texture)
    {
        var c = Ctx(ctx);
        if (c == null || texture == 0) return ERR_INVALID_POINTER;
        if (index >= 16) return ERR_INVALID_VALUE;
        c.VertexTextures[index] = GxmTextureDesc.Read(texture);
        return 0;
    }

    [Hle]
    int sceGxmSetVertexUniformBuffer(uint ctx, uint index, uint data)
    {
        var c = Ctx(ctx);
        if (c == null || data == 0) return ERR_INVALID_POINTER;
        if (index >= 14) return ERR_INVALID_VALUE;
        c.VertexUniformBuffers[index] = data;
        return 0;
    }

    [Hle]
    int sceGxmSetFragmentUniformBuffer(uint ctx, uint index, uint data)
    {
        var c = Ctx(ctx);
        if (c == null || data == 0) return ERR_INVALID_POINTER;
        if (index >= 14) return ERR_INVALID_VALUE;
        c.FragmentUniformBuffers[index] = data;
        return 0;
    }

    [Hle] int sceGxmSetVertexDefaultUniformBuffer(uint ctx, uint data) { if (Ctx(ctx) is { } c) c.VertexUniformBuffers[14] = data; return 0; }
    [Hle] int sceGxmSetFragmentDefaultUniformBuffer(uint ctx, uint data) { if (Ctx(ctx) is { } c) c.FragmentUniformBuffers[14] = data; return 0; }

    [Hle]
    int sceGxmReserveVertexDefaultUniformBuffer(uint ctx, Ptr<uint> outBuf)
    {
        var c = Ctx(ctx);
        if (c == null || outBuf.Address == 0) return ERR_INVALID_POINTER;
        uint size = (c.VertexProgram?.Info.Program.DefaultUniformBufferCount ?? 0) * 4;
        if (size == 0) { outBuf.Value = 0; c.VertexUniformBuffers[14] = 0; return 0; }
        c.VertexRingUsed = (c.VertexRingUsed + 3) & ~3u;
        if (c.VertexRingUsed + size > c.VertexRingSize)
        {
            if (c.Deferred && c.VertexCallback != 0)
            {
                uint mem = CallMemoryCallback(c, c.VertexCallback, Math.Max(size, RingCallbackRequest), out uint got);
                if (mem == 0) return ERR_RESERVE_FAILED;
                c.VertexRing = mem; c.VertexRingSize = got;
            }
            c.VertexRingUsed = 0;
        }
        uint p = c.VertexRing + c.VertexRingUsed;
        c.VertexDefaultReserved = true;
        c.VertexUniformBuffers[14] = p;
        outBuf.Value = p;
        return 0;
    }

    [Hle]
    int sceGxmReserveFragmentDefaultUniformBuffer(uint ctx, Ptr<uint> outBuf)
    {
        var c = Ctx(ctx);
        if (c == null || outBuf.Address == 0) return ERR_INVALID_POINTER;
        uint size = (c.FragmentProgram?.Info?.Program.DefaultUniformBufferCount ?? 0) * 4;
        if (size == 0) { outBuf.Value = 0; c.FragmentUniformBuffers[14] = 0; return 0; }
        c.FragmentRingUsed = (c.FragmentRingUsed + 3) & ~3u;
        if (c.FragmentRingUsed + size > c.FragmentRingSize)
        {
            if (c.Deferred && c.FragmentCallback != 0)
            {
                uint mem = CallMemoryCallback(c, c.FragmentCallback, Math.Max(size, RingCallbackRequest), out uint got);
                if (mem == 0) return ERR_RESERVE_FAILED;
                c.FragmentRing = mem; c.FragmentRingSize = got;
            }
            c.FragmentRingUsed = 0;
        }
        uint p = c.FragmentRing + c.FragmentRingUsed;
        c.FragmentDefaultReserved = true;
        c.FragmentUniformBuffers[14] = p;
        outBuf.Value = p;
        return 0;
    }

    [Hle] void sceGxmSetCullMode(uint ctx, uint mode) { if (Ctx(ctx) is { } c) c.State.CullMode = (byte)mode; }
    [Hle] void sceGxmSetTwoSidedEnable(uint ctx, uint mode) { if (Ctx(ctx) is { } c) c.State.TwoSided = mode != 0; }

    [Hle] void sceGxmSetFrontDepthFunc(uint ctx, uint f) { if (Ctx(ctx) is { } c) c.State.Front.DepthFunc = (byte)(f >> 22); }
    [Hle] void sceGxmSetBackDepthFunc(uint ctx, uint f) { if (Ctx(ctx) is { } c) c.State.Back.DepthFunc = (byte)(f >> 22); }
    [Hle] void sceGxmSetFrontDepthWriteEnable(uint ctx, uint e) { if (Ctx(ctx) is { } c) c.State.Front.DepthWrite = e == 0; }
    [Hle] void sceGxmSetBackDepthWriteEnable(uint ctx, uint e) { if (Ctx(ctx) is { } c) c.State.Back.DepthWrite = e == 0; }
    [Hle] void sceGxmSetFrontFragmentProgramEnable(uint ctx, uint e) { if (Ctx(ctx) is { } c) c.State.Front.FragmentEnabled = e == 0; }
    [Hle] void sceGxmSetBackFragmentProgramEnable(uint ctx, uint e) { if (Ctx(ctx) is { } c) c.State.Back.FragmentEnabled = e == 0; }
    [Hle] void sceGxmSetFrontPolygonMode(uint ctx, uint m) { if (Ctx(ctx) is { } c) c.State.Front.PolygonMode = (byte)(m >> 15); }
    [Hle] void sceGxmSetBackPolygonMode(uint ctx, uint m) { if (Ctx(ctx) is { } c) c.State.Back.PolygonMode = (byte)(m >> 15); }
    /// <summary>SCE_GXM_WBUFFER_ENABLED = 0x4000 (not 1).</summary>
    [Hle]
    void sceGxmSetWBufferEnable(uint ctx, uint enable)
    {
        if (Ctx(ctx) is not { } c) return;
        c.State.WBuffer = enable != 0;
        if (enable != 0) Log.WarnOnce("gxm-wbuffer", "sceGxmSetWBufferEnable: W-buffering approximated with Z/W depth");
    }
    /// <summary>SCE_GXM_LINE_FILL_LAST_PIXEL_ENABLED = 0x80000.</summary>
    [Hle] void sceGxmSetFrontLineFillLastPixelEnable(uint ctx, uint e) { if (Ctx(ctx) is { } c) c.State.Front.LineFillLastPixel = e != 0; }
    [Hle] void sceGxmSetBackLineFillLastPixelEnable(uint ctx, uint e) { if (Ctx(ctx) is { } c) c.State.Back.LineFillLastPixel = e != 0; }

    /// <summary>YUV->RGB matrix of the colour space converter <paramref name="index"/> (0/1): BT601 0, BT709 1,
    /// BT601 full range 2, BT709 full range 3. Stored per context; the texture decoder still uses its fixed matrices.</summary>
    [Hle]
    int sceGxmSetYuvProfile(uint ctx, uint index, uint profile)
    {
        if (Ctx(ctx) is not { } c) return ERR_INVALID_POINTER;
        if (index > 1 || profile > 3) return ERR_INVALID_VALUE;
        c.YuvProfile[index] = (byte)profile;
        return 0;
    }
    [Hle] void sceGxmSetFrontPointLineWidth(uint ctx, uint w) { if (Ctx(ctx) is { } c) c.State.Front.PointLineWidth = (byte)w; }
    [Hle] void sceGxmSetBackPointLineWidth(uint ctx, uint w) { if (Ctx(ctx) is { } c) c.State.Back.PointLineWidth = (byte)w; }
    [Hle] void sceGxmSetFrontDepthBias(uint ctx, int factor, int units) { if (Ctx(ctx) is { } c) { c.State.Front.DepthBiasFactor = factor; c.State.Front.DepthBiasUnits = units; } }
    [Hle] void sceGxmSetBackDepthBias(uint ctx, int factor, int units) { if (Ctx(ctx) is { } c) { c.State.Back.DepthBiasFactor = factor; c.State.Back.DepthBiasUnits = units; } }

    static void SetStencil(ref StencilState s, uint func, uint sf, uint df, uint dp, uint compareMask, uint writeMask)
    {
        s.Func = (byte)(func >> 25);
        s.StencilFail = (byte)(sf & 7);
        s.DepthFail = (byte)(df & 7);
        s.DepthPass = (byte)(dp & 7);
        s.CompareMask = (byte)compareMask;
        s.WriteMask = (byte)writeMask;
    }

    [Hle]
    void sceGxmSetFrontStencilFunc(uint ctx, uint func, uint stencilFail, uint depthFail, uint depthPass, uint compareMask, uint writeMask)
    {
        if (Ctx(ctx) is not { } c) return;
        SetStencil(ref c.State.Front.Stencil, func, stencilFail, depthFail, depthPass, compareMask, writeMask);
        if (!c.State.TwoSided) SetStencil(ref c.State.Back.Stencil, func, stencilFail, depthFail, depthPass, compareMask, writeMask);
    }

    [Hle]
    void sceGxmSetBackStencilFunc(uint ctx, uint func, uint stencilFail, uint depthFail, uint depthPass, uint compareMask, uint writeMask)
    {
        if (Ctx(ctx) is { } c) SetStencil(ref c.State.Back.Stencil, func, stencilFail, depthFail, depthPass, compareMask, writeMask);
    }

    [Hle] void sceGxmSetFrontStencilRef(uint ctx, uint r) { if (Ctx(ctx) is { } c) { c.State.Front.Stencil.Ref = (byte)r; if (!c.State.TwoSided) c.State.Back.Stencil.Ref = (byte)r; } }
    [Hle] void sceGxmSetBackStencilRef(uint ctx, uint r) { if (Ctx(ctx) is { } c) c.State.Back.Stencil.Ref = (byte)r; }

    [Hle]
    void sceGxmSetViewport(uint ctx, float xOffset, float xScale, float yOffset, float yScale, float zOffset, float zScale)
    {
        if (Ctx(ctx) is not { } c) return;
        ref var s = ref c.State;
        s.VpOffX = xOffset; s.VpScaleX = xScale; s.VpOffY = yOffset; s.VpScaleY = yScale; s.VpOffZ = zOffset; s.VpScaleZ = zScale;
        c.ViewportSet = true;
    }

    [Hle] void sceGxmSetViewportEnable(uint ctx, uint enable) { if (Ctx(ctx) is { } c) { c.State.ViewportEnabled = enable == 0; c.ViewportSet = true; } }

    [Hle]
    void sceGxmSetRegionClip(uint ctx, uint mode, uint xMin, uint yMin, uint xMax, uint yMax)
    {
        if (Ctx(ctx) is not { } c) return;
        ref var s = ref c.State;
        s.RegionClipMode = (byte)(mode >> 30);
        s.ClipMinX = (int)xMin; s.ClipMinY = (int)yMin; s.ClipMaxX = (int)xMax; s.ClipMaxY = (int)yMax;
        c.RegionClipSet = true;
    }

    [Hle] void sceGxmSetDefaultRegionClipAndViewport(uint ctx, uint xMax, uint yMax) { if (Ctx(ctx) is { } c) { SetDefaultRegionClipAndViewport(c, xMax, yMax); c.ViewportSet = c.RegionClipSet = true; } }
    // visibility tests: the GPU counts the samples of a draw that pass the depth/stencil tests into a word of the
    // visibility buffer (index * 4 within the per-core stride); games use them for occlusion culling and streaming
    const uint VisibilityOpIncrement = 0, VisibilityAlignment = 16;
    [Hle] void sceGxmSetFrontVisibilityTestEnable(uint ctx, uint e) { if (Ctx(ctx) is { } c) c.VisFrontEnable = e != 0; }
    [Hle] void sceGxmSetBackVisibilityTestEnable(uint ctx, uint e) { if (Ctx(ctx) is { } c) c.VisBackEnable = e != 0; }
    [Hle] void sceGxmSetFrontVisibilityTestIndex(uint ctx, uint i) { if (Ctx(ctx) is { } c) c.VisFrontIndex = i; }
    [Hle] void sceGxmSetBackVisibilityTestIndex(uint ctx, uint i) { if (Ctx(ctx) is { } c) c.VisBackIndex = i; }
    [Hle] void sceGxmSetFrontVisibilityTestOp(uint ctx, uint op) { if (Ctx(ctx) is { } c) c.VisFrontIncrement = op == VisibilityOpIncrement; }
    [Hle] void sceGxmSetBackVisibilityTestOp(uint ctx, uint op) { if (Ctx(ctx) is { } c) c.VisBackIncrement = op == VisibilityOpIncrement; }

    [Hle]
    int sceGxmSetVisibilityBuffer(uint ctx, uint bufferBase, uint stride)
    {
        if (Ctx(ctx) is not { } c) return ERR_INVALID_POINTER;
        if ((bufferBase & (VisibilityAlignment - 1)) != 0) return ERR_INVALID_ALIGNMENT;
        c.VisBuffer = bufferBase;
        c.VisStride = stride;
        return 0;
    }

    // =========================================================== draws

    static GuestMemory.Block? _ubBlock;

    /// <summary>Bytes from <paramref name="addr"/> to the end of its memory block (uint.MaxValue when unknown).</summary>
    static uint BlockBytesLeft(uint addr)
    {
        var b = _ubBlock;
        if (b == null || addr < b.Address || addr - b.Address >= b.Size)
        {
            b = GuestMemory.Instance?.FindBlock(addr);
            if (b == null) return uint.MaxValue;
            _ubBlock = b;
        }
        return b.Address + b.Size - addr;
    }

    /// <summary>Gathers the draw's uniform buffers into the command's reusable buffer; returns the byte count.</summary>
    static int CopyUniforms(GxmProgramInfo info, uint[] buffers, ref byte[] data)
    {
        if (info.UniformBytes == 0) return 0;
        if (data.Length < info.UniformBytes) data = new byte[Math.Max(info.UniformBytes, data.Length * 2)];
        else data.AsSpan(0, info.UniformBytes).Clear();
        foreach (var slot in info.UniformSlots)
        {
            if (slot.Mapped) continue; // read in place by the renderer
            uint idx = Math.Min(slot.GuestIndex, 14);
            uint addr = buffers[idx];
            if (addr == 0) continue;
            int size = slot.ByteSize;
            // large "unknown size" buffers: don't run past the next buffer
            foreach (var other in buffers)
                if (other > addr && other - addr < size) size = (int)(other - addr);
            // ...nor past the end of the allocation (unbounded buffers may sit at the end of a block)
            if (size >= ProgramInput.MaxUbInFloat * 4) size = (int)Math.Min((uint)size, BlockBytesLeft(addr));
            GuestMemory.Span(addr, size).CopyTo(data.AsSpan(slot.ByteOffset, size));
        }
        return info.UniformBytes;
    }

    int Draw(GxmContext c, GxmVertexProgram? vp, GxmFragmentProgram? fp, uint[] vUbs, uint[] fUbs, GxmTextureDesc[] vTex, GxmTextureDesc[] fTex,
        uint[] streams, uint primType, uint indexType, uint indexData, uint indexCount, uint instanceCount)
    {
        if (c.Deferred) { if (!c.InCommandList) return ERR_NOT_WITHIN_COMMAND_LIST; }
        else if (!c.InScene) return ERR_NOT_WITHIN_SCENE;
        if (vp == null || fp == null) return ERR_NULL_PROGRAM;
        if (indexData == 0) return ERR_INVALID_POINTER;
        // visibility test (front state; the back state applies when only it is enabled)
        uint visAddress = 0;
        bool visIncrement = false;
        bool front = c.VisFrontEnable, back = c.VisBackEnable;
        if (c.VisBuffer != 0 && (front || back))
        {
            uint index = front ? c.VisFrontIndex : c.VisBackIndex;
            if (index < Math.Max(c.VisStride / 4, 1))
            {
                visAddress = c.VisBuffer + index * 4;
                visIncrement = front ? c.VisFrontIncrement : c.VisBackIncrement;
            }
        }
        if (c.Deferred)
        {
            int r = RecordDraw(c, vp, fp, vUbs, fUbs, vTex, fTex, streams, primType, indexType, indexData, indexCount, instanceCount, visAddress, visIncrement);
            if (r != 0) return r;
        }
        else SubmitDraw(in c.State, vp, fp, vUbs, fUbs, vTex, fTex, streams, primType, indexType, indexData, indexCount, instanceCount, visAddress, visIncrement);

        if (c.VertexDefaultReserved)
        {
            c.VertexRingUsed += vp.Info.Program.DefaultUniformBufferCount * 4;
            c.VertexDefaultReserved = false;
        }
        if (c.FragmentDefaultReserved)
        {
            c.FragmentRingUsed += (fp.Info?.Program.DefaultUniformBufferCount ?? 0) * 4;
            c.FragmentDefaultReserved = false;
        }
        return 0;
    }

    void SubmitDraw(in DrawState state, GxmVertexProgram vp, GxmFragmentProgram fp, uint[] vUbs, uint[] fUbs, GxmTextureDesc[] vTex,
        GxmTextureDesc[] fTex, uint[] streams, uint primType, uint indexType, uint indexData, uint indexCount, uint instanceCount,
        uint visAddress, bool visIncrement)
    {
        var cmd = Gpu.RentDraw();
        cmd.VertexProgram = vp;
        cmd.FragmentProgram = fp;
        cmd.State = state;
        cmd.VertexUniformsLength = CopyUniforms(vp.Info, vUbs, ref cmd.VertexUniforms);
        cmd.FragmentUniformsLength = fp.Info != null ? CopyUniforms(fp.Info, fUbs, ref cmd.FragmentUniforms) : 0;
        vUbs.AsSpan(0, Math.Min(vUbs.Length, 15)).CopyTo(cmd.VertexUniformAddrs);
        fUbs.AsSpan(0, Math.Min(fUbs.Length, 15)).CopyTo(cmd.FragmentUniformAddrs);
        cmd.PrimitiveType = (byte)(primType >> 26);
        cmd.Index32 = indexType != 0;
        cmd.IndexAddress = indexData;
        cmd.IndexCount = indexCount;
        cmd.InstanceCount = Math.Max(instanceCount, 1);
        if (visAddress != 0)
        {
            cmd.VisAddress = visAddress;
            cmd.VisIncrement = visIncrement;
        }
        vTex.CopyTo(cmd.VertexTextures, 0);
        fTex.CopyTo(cmd.FragmentTextures, 0);
        streams.CopyTo(cmd.Streams, 0);
        Gpu.Submit(cmd);
    }

    // =========================================================== deferred contexts / command lists
    // A deferred context records draw "recipes" (state, programs, textures, streams, uniform buffer addresses) between
    // sceGxmBeginCommandList and sceGxmEndCommandList; sceGxmExecuteCommandList replays them inside the scene of an
    // immediate context, reading the uniform data at that point. VDM memory is accounted only so callbacks and list
    // lifetimes look like the hardware's (bytes per recorded draw unconfirmed).

    const uint VdmCostPerDraw = 64, VdmCallbackRequest = 1024, RingCallbackRequest = 4096, DeferredMinBufferSize = 1024;
    const uint CommandListMagic = 0x4C435356; // "VSCL"
    const int MaxCommandLists = 8192;
    readonly Dictionary<uint, RecordedCommandList> _commandLists = new();
    uint _nextCommandListId = 1;

    /// <summary>Calls a deferred-context memory callback: void *cb(void *data, SceSize requested, SceSize *size).</summary>
    static uint CallMemoryCallback(GxmContext c, uint cb, uint requested, out uint size)
    {
        size = 0;
        if (cb == 0 || GuestThread.Current is not { } t) return 0;
        GuestMemory.W32(c.SizeScratch, 0);
        uint mem = (uint)t.CallGuest(cb, c.CallbackData, requested, c.SizeScratch);
        size = GuestMemory.R32(c.SizeScratch);
        return mem;
    }

    /// <summary>Accounts VDM space for a recorded draw, asking the VDM callback for a new block when the current one is
    /// full (the list then spans from its start to the end of the old block plus the new one).</summary>
    int ConsumeVdm(GxmContext c, uint bytes)
    {
        if (c.VdmUsed + bytes <= c.VdmBufferSize) { c.VdmUsed += bytes; return 0; }
        if (c.VdmCallback != 0)
        {
            uint mem = CallMemoryCallback(c, c.VdmCallback, Math.Max(VdmCallbackRequest, bytes), out uint size);
            if (mem == 0) return ERR_RESERVE_FAILED;
            c.VdmBuffer = mem; c.VdmBufferSize = size; c.VdmUsed = Math.Min(bytes, size);
            return 0;
        }
        c.VdmUsed = Math.Min(bytes, c.VdmBufferSize); // no callback: the ring wraps
        return 0;
    }

    int RecordDraw(GxmContext c, GxmVertexProgram vp, GxmFragmentProgram fp, uint[] vUbs, uint[] fUbs, GxmTextureDesc[] vTex,
        GxmTextureDesc[] fTex, uint[] streams, uint primType, uint indexType, uint indexData, uint indexCount, uint instanceCount,
        uint visAddress, bool visIncrement)
    {
        int r = ConsumeVdm(c, VdmCostPerDraw);
        if (r != 0) return r;
        var d = new DrawRecipe
        {
            Vp = vp, Fp = fp, State = c.State,
            PrimType = primType, IndexType = indexType, IndexData = indexData, IndexCount = indexCount, InstanceCount = instanceCount,
            VisAddress = visAddress, VisIncrement = visIncrement, ViewportSet = c.ViewportSet, RegionClipSet = c.RegionClipSet,
        };
        vUbs.AsSpan(0, Math.Min(vUbs.Length, 15)).CopyTo(d.VUbs);
        fUbs.AsSpan(0, Math.Min(fUbs.Length, 15)).CopyTo(d.FUbs);
        vTex.CopyTo(d.VTex, 0);
        fTex.CopyTo(d.FTex, 0);
        streams.CopyTo(d.Streams, 0);
        c.Recording!.Add(d);
        return 0;
    }

    [Hle]
    int sceGxmCreateDeferredContext(uint p, Ptr<uint> outContext)
    {
        if (p == 0 || outContext.IsNull) return ERR_INVALID_POINTER;
        uint hostMem = GuestMemory.R32(p);
        uint h = hostMem != 0 ? (hostMem + 7) & ~7u : AllocObj(64, "SceGxmDeferredContext");
        var c = new GxmContext
        {
            Handle = h, Deferred = true,
            VdmCallback = GuestMemory.R32(p + 0x08), VertexCallback = GuestMemory.R32(p + 0x0C),
            FragmentCallback = GuestMemory.R32(p + 0x10), CallbackData = GuestMemory.R32(p + 0x14),
            VdmBuffer = GuestMemory.R32(p + 0x18), VdmBufferSize = GuestMemory.R32(p + 0x1C),
            VertexRing = GuestMemory.R32(p + 0x20), VertexRingSize = GuestMemory.R32(p + 0x24),
            FragmentRing = GuestMemory.R32(p + 0x28), FragmentRingSize = GuestMemory.R32(p + 0x2C),
            SizeScratch = Emu.Memory.Alloc(16, "SceGxmDeferredContext size", MemoryRegion.User, 16),
        };
        _contexts[h] = c;
        outContext.Value = h;
        return 0;
    }

    GxmContext? DeferredCtx(uint ctx, out int error)
    {
        var c = Ctx(ctx);
        error = c == null ? ERR_INVALID_POINTER : !c.Deferred ? ERR_INVALID_VALUE : 0;
        return error == 0 ? c : null;
    }

    [Hle]
    int sceGxmDestroyDeferredContext(uint ctx)
    {
        if (DeferredCtx(ctx, out int err) is not { } c) return err;
        if (c.InCommandList) return ERR_WITHIN_COMMAND_LIST;
        _contexts.TryRemove(ctx, out _);
        lock (_commandLists)
            foreach (var id in _commandLists.Where(kv => kv.Value.Owner == ctx).Select(kv => kv.Key).ToList()) _commandLists.Remove(id);
        if (c.SizeScratch != 0) Emu.Memory.Free(c.SizeScratch);
        return 0;
    }

    [Hle]
    int sceGxmBeginCommandList(uint ctx)
    {
        if (DeferredCtx(ctx, out int err) is not { } c) return err;
        if (c.InCommandList) return ERR_WITHIN_COMMAND_LIST;
        c.VertexRingUsed = c.FragmentRingUsed = 0;
        if (c.VertexRing == 0 && c.VertexCallback != 0)
        {
            uint mem = CallMemoryCallback(c, c.VertexCallback, RingCallbackRequest, out uint size);
            if (mem == 0) return ERR_RESERVE_FAILED;
            c.VertexRing = mem; c.VertexRingSize = size;
        }
        if (c.FragmentRing == 0 && c.FragmentCallback != 0)
        {
            uint mem = CallMemoryCallback(c, c.FragmentCallback, RingCallbackRequest, out uint size);
            if (mem == 0) return ERR_RESERVE_FAILED;
            c.FragmentRing = mem; c.FragmentRingSize = size;
        }
        if ((c.VdmBuffer == 0 || c.VdmUsed >= c.VdmBufferSize) && c.VdmCallback != 0)
        {
            uint mem = CallMemoryCallback(c, c.VdmCallback, VdmCallbackRequest, out uint size);
            if (mem == 0) return ERR_RESERVE_FAILED;
            c.VdmBuffer = mem; c.VdmBufferSize = size; c.VdmUsed = 0;
        }
        c.InCommandList = true;
        c.ViewportSet = c.RegionClipSet = false;
        c.VdmListStart = c.VdmBuffer + c.VdmUsed;
        c.Recording = new List<DrawRecipe>();
        return 0;
    }

    [Hle]
    int sceGxmEndCommandList(uint ctx, uint list)
    {
        if (list == 0) return ERR_INVALID_POINTER;
        if (DeferredCtx(ctx, out int err) is not { } c) return err;
        if (!c.InCommandList) return ERR_NOT_WITHIN_COMMAND_LIST;
        uint start = c.VdmListStart, end = c.VdmBuffer + c.VdmUsed;
        if (end < start || end - start > c.VdmBufferSize) start = c.VdmBuffer; // the list moved to a new VDM block
        uint id;
        lock (_commandLists)
        {
            // the VDM memory of older lists of this context that this one overwrote was reused: they are gone
            if (end > start)
                foreach (var old in _commandLists.Values.Where(l => l.Owner == ctx && l.VdmStart < end && start < l.VdmEnd).Select(l => l.Id).ToList())
                    _commandLists.Remove(old);
            if (_commandLists.Count >= MaxCommandLists)
            {
                Log.WarnOnce("gxm-cmdlists", "sceGxmEndCommandList: too many live command lists, dropping the oldest");
                _commandLists.Remove(_commandLists.Keys.Min());
            }
            id = _nextCommandListId++;
            _commandLists[id] = new RecordedCommandList { Id = id, Owner = ctx, Draws = c.Recording!.ToArray(), VdmStart = start, VdmEnd = end };
        }
        GuestMemory.Fill(list, 0, 32);
        GuestMemory.W32(list, CommandListMagic);
        GuestMemory.W32(list + 4, id);
        GuestMemory.W32(list + 8, ctx);
        c.InCommandList = false;
        c.Recording = null;
        return 0;
    }

    [Hle]
    int sceGxmExecuteCommandList(uint ctx, uint list)
    {
        var c = Ctx(ctx);
        if (c == null || list == 0) return ERR_INVALID_POINTER;
        if (c.Deferred) return ERR_INVALID_VALUE;
        if (!c.InScene) return ERR_NOT_WITHIN_SCENE;
        if (GuestMemory.R32(list) != CommandListMagic) return ERR_INVALID_POINTER;
        RecordedCommandList? rec;
        lock (_commandLists) _commandLists.TryGetValue(GuestMemory.R32(list + 4), out rec);
        if (rec == null) return ERR_INVALID_POINTER;
        foreach (var d in rec.Draws)
        {
            // viewport and region clip come from the executing scene unless the deferred context set them itself
            var st = d.State;
            ref readonly var im = ref c.State;
            if (!d.ViewportSet)
            {
                st.ViewportEnabled = im.ViewportEnabled;
                st.VpOffX = im.VpOffX; st.VpOffY = im.VpOffY; st.VpOffZ = im.VpOffZ;
                st.VpScaleX = im.VpScaleX; st.VpScaleY = im.VpScaleY; st.VpScaleZ = im.VpScaleZ;
            }
            if (!d.RegionClipSet)
            {
                st.RegionClipMode = im.RegionClipMode;
                st.ClipMinX = im.ClipMinX; st.ClipMinY = im.ClipMinY; st.ClipMaxX = im.ClipMaxX; st.ClipMaxY = im.ClipMaxY;
            }
            SubmitDraw(in st, d.Vp, d.Fp, d.VUbs, d.FUbs, d.VTex, d.FTex, d.Streams, d.PrimType, d.IndexType, d.IndexData, d.IndexCount,
                d.InstanceCount, d.VisAddress, d.VisIncrement);
        }
        if (Gpu.Pending > 20000) Gpu.WaitFor(Gpu.Submitted - 2000);
        return 0;
    }

    [Hle]
    int sceGxmGetDeferredContextVdmBuffer(uint ctx, Ptr<uint> mem)
    {
        if (mem.IsNull) return ERR_INVALID_POINTER;
        if (DeferredCtx(ctx, out int err) is not { } c) return err;
        if (c.InCommandList) return ERR_WITHIN_COMMAND_LIST;
        mem.Value = c.VdmBuffer;
        return 0;
    }

    [Hle]
    int sceGxmSetDeferredContextVdmBuffer(uint ctx, uint mem, uint size)
    {
        if (DeferredCtx(ctx, out int err) is not { } c) return err;
        if (c.InCommandList) return ERR_WITHIN_COMMAND_LIST;
        c.VdmBuffer = mem; c.VdmBufferSize = size; c.VdmUsed = 0;
        return 0;
    }

    [Hle]
    int sceGxmGetDeferredContextVertexBuffer(uint ctx, Ptr<uint> mem)
    {
        if (mem.IsNull) return ERR_INVALID_POINTER;
        if (DeferredCtx(ctx, out int err) is not { } c) return err;
        if (c.InCommandList) return ERR_WITHIN_COMMAND_LIST;
        mem.Value = c.VertexRing;
        return 0;
    }

    [Hle]
    int sceGxmSetDeferredContextVertexBuffer(uint ctx, uint mem, uint size)
    {
        if (DeferredCtx(ctx, out int err) is not { } c) return err;
        if (c.InCommandList) return ERR_WITHIN_COMMAND_LIST;
        if ((size != 0 && size < DeferredMinBufferSize) || (mem != 0 && size == 0)) return ERR_INVALID_VALUE;
        c.VertexRing = mem; c.VertexRingSize = size; c.VertexRingUsed = 0;
        return 0;
    }

    [Hle]
    int sceGxmGetDeferredContextFragmentBuffer(uint ctx, Ptr<uint> mem)
    {
        if (mem.IsNull) return ERR_INVALID_POINTER;
        if (DeferredCtx(ctx, out int err) is not { } c) return err;
        if (c.InCommandList) return ERR_WITHIN_COMMAND_LIST;
        mem.Value = c.FragmentRing;
        return 0;
    }

    [Hle]
    int sceGxmSetDeferredContextFragmentBuffer(uint ctx, uint mem, uint size)
    {
        if (DeferredCtx(ctx, out int err) is not { } c) return err;
        if (c.InCommandList) return ERR_WITHIN_COMMAND_LIST;
        if ((size != 0 && size < DeferredMinBufferSize) || (mem != 0 && size == 0)) return ERR_INVALID_VALUE;
        c.FragmentRing = mem; c.FragmentRingSize = size; c.FragmentRingUsed = 0;
        return 0;
    }

    [Hle]
    int sceGxmDraw(uint ctx, uint primType, uint indexType, uint indexData, uint indexCount)
    {
        var c = Ctx(ctx);
        if (c == null) return ERR_INVALID_POINTER;
        return Draw(c, c.VertexProgram, c.FragmentProgram, c.VertexUniformBuffers, c.FragmentUniformBuffers, c.VertexTextures, c.FragmentTextures,
            c.Streams, primType, indexType, indexData, indexCount, 1);
    }

    [Hle]
    int sceGxmDrawInstanced(uint ctx, uint primType, uint indexType, uint indexData, uint indexCount, uint indexWrap)
    {
        var c = Ctx(ctx);
        if (c == null) return ERR_INVALID_POINTER;
        if (indexWrap == 0 || indexCount % indexWrap != 0) return ERR_INVALID_VALUE;
        return Draw(c, c.VertexProgram, c.FragmentProgram, c.VertexUniformBuffers, c.FragmentUniformBuffers, c.VertexTextures, c.FragmentTextures,
            c.Streams, primType, indexType, indexData, indexWrap, indexCount / indexWrap);
    }

    // ---- precomputed state (layouts are ours; the guest only allocates them)
    // draw: program@0 type@4 streams@8 streamCount@12 indexFormat@16 indexData@20 vertexCount@24 instanceCount@28
    // vertex/fragment state: program@0 textures@4 textureCount@8(u16) bufferCount@10(u16) uniformBuffers@12

    static int TextureCount(GxmProgramInfo? info) => info == null ? 0 : 32 - BitOperations.LeadingZeroCount(info.TexturesUsed);
    static int BufferCount(GxmProgramInfo? info) => info == null || info.UniformSlots.Count == 0 ? 0 : (int)info.UniformSlots.Max(s => Math.Min(s.GuestIndex, 14)) + 1;

    static uint StateSize(int buffers, int textures) { uint s = (uint)(buffers * 4 + textures * 16); return s > 0 ? s : 8; }

    [Hle]
    uint sceGxmGetPrecomputedDrawSize(uint vp)
    {
        if (!_vertexPrograms.TryGetValue(vp, out var v)) return 0;
        int max = v.Attributes.Length == 0 ? -1 : v.Attributes.Max(a => (int)a.StreamIndex);
        return (uint)((max + 1) * 4);
    }

    [Hle] uint sceGxmGetPrecomputedVertexStateSize(uint vp) => _vertexPrograms.TryGetValue(vp, out var v) ? StateSize(BufferCount(v.Info), TextureCount(v.Info)) : 8;
    [Hle] uint sceGxmGetPrecomputedFragmentStateSize(uint fp) => _fragmentPrograms.TryGetValue(fp, out var f) ? StateSize(BufferCount(f.Info), TextureCount(f.Info)) : 8;

    [Hle]
    int sceGxmPrecomputedDrawInit(uint state, uint vp, uint extra)
    {
        if (state == 0 || vp == 0 || extra == 0) return ERR_INVALID_POINTER;
        if ((extra & 0xF) != 0) return ERR_INVALID_ALIGNMENT;
        GuestMemory.Fill(state, 0, 32);
        GuestMemory.W32(state, vp);
        GuestMemory.W32(state + 8, extra);
        int max = _vertexPrograms.TryGetValue(vp, out var v) && v.Attributes.Length > 0 ? v.Attributes.Max(a => a.StreamIndex) : 0;
        GuestMemory.Write(state + 12, (ushort)(max + 1));
        GuestMemory.W32(state + 28, 1);
        return 0;
    }

    [Hle]
    int sceGxmPrecomputedDrawSetAllVertexStreams(uint state, uint streams)
    {
        if (state == 0 || streams == 0) return ERR_INVALID_POINTER;
        uint dst = GuestMemory.R32(state + 8);
        uint n = GuestMemory.Read<ushort>(state + 12);
        GuestMemory.Copy(dst, streams, n * 4);
        return 0;
    }

    [Hle]
    int sceGxmPrecomputedDrawSetVertexStream(uint state, uint index, uint data)
    {
        if (state == 0 || data == 0) return ERR_INVALID_POINTER;
        if (index >= 16) return ERR_INVALID_VALUE;
        GuestMemory.W32(GuestMemory.R32(state + 8) + index * 4, data);
        return 0;
    }

    [Hle]
    int sceGxmPrecomputedDrawSetParams(uint state, uint primType, uint indexType, uint indexData, uint indexCount)
    {
        if (state == 0 || indexData == 0) return ERR_INVALID_POINTER;
        GuestMemory.W32(state + 4, primType);
        GuestMemory.W32(state + 16, indexType);
        GuestMemory.W32(state + 20, indexData);
        GuestMemory.W32(state + 24, indexCount);
        GuestMemory.W32(state + 28, 1);
        return 0;
    }

    [Hle]
    int sceGxmPrecomputedDrawSetParamsInstanced(uint state, uint primType, uint indexType, uint indexData, uint indexCount, uint indexWrap)
    {
        if (state == 0 || indexData == 0) return ERR_INVALID_POINTER;
        if (indexWrap == 0) return ERR_INVALID_VALUE;
        GuestMemory.W32(state + 4, primType);
        GuestMemory.W32(state + 16, indexType);
        GuestMemory.W32(state + 20, indexData);
        GuestMemory.W32(state + 24, indexWrap);
        GuestMemory.W32(state + 28, indexCount / indexWrap);
        return 0;
    }

    int PrecomputedStateInit(uint state, uint program, uint extra, GxmProgramInfo? info)
    {
        if (state == 0 || program == 0 || extra == 0) return ERR_INVALID_POINTER;
        if ((extra & 0xF) != 0) return ERR_INVALID_ALIGNMENT;
        int tc = TextureCount(info), bc = BufferCount(info);
        GuestMemory.W32(state, program);
        GuestMemory.W32(state + 4, extra);
        GuestMemory.Write(state + 8, (ushort)tc);
        GuestMemory.Write(state + 10, (ushort)bc);
        GuestMemory.W32(state + 12, extra + (uint)tc * 16);
        GuestMemory.Fill(extra, 0, (uint)(tc * 16 + bc * 4));
        return 0;
    }

    [Hle] int sceGxmPrecomputedVertexStateInit(uint state, uint vp, uint extra) => PrecomputedStateInit(state, vp, extra, _vertexPrograms.GetValueOrDefault(vp)?.Info);
    [Hle] int sceGxmPrecomputedFragmentStateInit(uint state, uint fp, uint extra) => PrecomputedStateInit(state, fp, extra, _fragmentPrograms.GetValueOrDefault(fp)?.Info);

    static int PrecomputedSetTexture(uint state, uint index, uint texture)
    {
        if (state == 0 || texture == 0) return ERR_INVALID_POINTER;
        if (index >= GuestMemory.Read<ushort>(state + 8)) return ERR_INVALID_VALUE;
        GuestMemory.Copy(GuestMemory.R32(state + 4) + index * 16, texture, 16);
        return 0;
    }

    static int PrecomputedSetAllTextures(uint state, uint textures)
    {
        if (state == 0 || textures == 0) return ERR_INVALID_POINTER;
        GuestMemory.Copy(GuestMemory.R32(state + 4), textures, GuestMemory.Read<ushort>(state + 8) * 16u);
        return 0;
    }

    static int PrecomputedSetUniformBuffer(uint state, uint index, uint data)
    {
        if (state == 0 || data == 0) return ERR_INVALID_POINTER;
        if (index >= GuestMemory.Read<ushort>(state + 10)) return ERR_INVALID_VALUE;
        GuestMemory.W32(GuestMemory.R32(state + 12) + index * 4, data);
        return 0;
    }

    /// <summary>sceGxmPrecomputed*StateSetAllUniformBuffers(state, const void *const *bufferDataArray): the 14 non-default buffers.</summary>
    static int PrecomputedSetAllUniformBuffers(uint state, uint array)
    {
        if (state == 0 || array == 0) return ERR_INVALID_POINTER;
        uint n = Math.Min(GuestMemory.Read<ushort>(state + 10), 14u), dst = GuestMemory.R32(state + 12);
        for (uint i = 0; i < n; i++) GuestMemory.W32(dst + i * 4, GuestMemory.R32(array + i * 4));
        return 0;
    }

    [Hle] int sceGxmPrecomputedVertexStateSetAllUniformBuffers(uint s, uint a) => PrecomputedSetAllUniformBuffers(s, a);
    [Hle] int sceGxmPrecomputedFragmentStateSetAllUniformBuffers(uint s, uint a) => PrecomputedSetAllUniformBuffers(s, a);
    /// <summary>No type check: the raw field + 1, even for linear strided textures where those bits belong to the stride.</summary>
    [Hle] uint sceGxmTextureGetMipmapCountUnsafe(uint tex) => (((GuestMemory.R32(tex) >> 17) & 0xF) + 1) & 0xF;

    static int PrecomputedSetDefaultUniformBuffer(uint state, uint data)
    {
        if (state == 0 || data == 0) return ERR_INVALID_POINTER;
        uint n = GuestMemory.Read<ushort>(state + 10);
        if (n < 15) return 0;
        GuestMemory.W32(GuestMemory.R32(state + 12) + 14 * 4, data);
        return 0;
    }

    [Hle] int sceGxmPrecomputedVertexStateSetTexture(uint s, uint i, uint t) => PrecomputedSetTexture(s, i, t);
    [Hle] int sceGxmPrecomputedFragmentStateSetTexture(uint s, uint i, uint t) => PrecomputedSetTexture(s, i, t);
    [Hle] int sceGxmPrecomputedVertexStateSetAllTextures(uint s, uint t) => PrecomputedSetAllTextures(s, t);
    [Hle] int sceGxmPrecomputedFragmentStateSetAllTextures(uint s, uint t) => PrecomputedSetAllTextures(s, t);
    [Hle] int sceGxmPrecomputedVertexStateSetUniformBuffer(uint s, uint i, uint d) => PrecomputedSetUniformBuffer(s, i, d);
    [Hle] int sceGxmPrecomputedFragmentStateSetUniformBuffer(uint s, uint i, uint d) => PrecomputedSetUniformBuffer(s, i, d);
    [Hle] int sceGxmPrecomputedVertexStateSetDefaultUniformBuffer(uint s, uint d) => PrecomputedSetDefaultUniformBuffer(s, d);
    [Hle] int sceGxmPrecomputedFragmentStateSetDefaultUniformBuffer(uint s, uint d) => PrecomputedSetDefaultUniformBuffer(s, d);
    [Hle] uint sceGxmPrecomputedVertexStateGetDefaultUniformBuffer(uint s) => GuestMemory.Read<ushort>(s + 10) >= 15 ? GuestMemory.R32(GuestMemory.R32(s + 12) + 56) : 0;
    [Hle] uint sceGxmPrecomputedFragmentStateGetDefaultUniformBuffer(uint s) => GuestMemory.Read<ushort>(s + 10) >= 15 ? GuestMemory.R32(GuestMemory.R32(s + 12) + 56) : 0;

    [Hle] void sceGxmSetPrecomputedVertexState(uint ctx, uint state) { if (Ctx(ctx) is { } c) c.PrecomputedVertexState = state; }
    [Hle] void sceGxmSetPrecomputedFragmentState(uint ctx, uint state) { if (Ctx(ctx) is { } c) c.PrecomputedFragmentState = state; }

    static void ReadStateArrays(uint state, uint[] ubs, GxmTextureDesc[] tex)
    {
        uint t = GuestMemory.R32(state + 4), tc = GuestMemory.Read<ushort>(state + 8);
        uint b = GuestMemory.R32(state + 12), bc = GuestMemory.Read<ushort>(state + 10);
        for (uint i = 0; i < Math.Min(tc, 16u); i++) tex[i] = GxmTextureDesc.Read(t + i * 16);
        for (uint i = 0; i < Math.Min(bc, 15u); i++) ubs[i] = GuestMemory.R32(b + i * 4);
    }

    [Hle]
    int sceGxmDrawPrecomputed(uint ctx, uint draw)
    {
        var c = Ctx(ctx);
        if (c == null || draw == 0) return ERR_INVALID_POINTER;
        if (!c.Deferred && !c.InScene) return ERR_NOT_WITHIN_SCENE;
        var vp = c.VertexProgram; var fp = c.FragmentProgram;
        var vUbs = (uint[])c.VertexUniformBuffers.Clone(); var fUbs = (uint[])c.FragmentUniformBuffers.Clone();
        var vTex = (GxmTextureDesc[])c.VertexTextures.Clone(); var fTex = (GxmTextureDesc[])c.FragmentTextures.Clone();
        if (c.PrecomputedVertexState != 0)
        {
            vp = _vertexPrograms.GetValueOrDefault(GuestMemory.R32(c.PrecomputedVertexState)) ?? vp;
            ReadStateArrays(c.PrecomputedVertexState, vUbs, vTex);
        }
        if (c.PrecomputedFragmentState != 0)
        {
            fp = _fragmentPrograms.GetValueOrDefault(GuestMemory.R32(c.PrecomputedFragmentState)) ?? fp;
            ReadStateArrays(c.PrecomputedFragmentState, fUbs, fTex);
        }
        var dvp = _vertexPrograms.GetValueOrDefault(GuestMemory.R32(draw));
        if (dvp != null) vp = dvp;
        var streams = c.StreamScratch; // per context, copied into the pooled draw command
        Array.Clear(streams);
        uint sp = GuestMemory.R32(draw + 8), sc = Math.Min(GuestMemory.Read<ushort>(draw + 12), (ushort)16);
        for (uint i = 0; i < sc; i++) streams[i] = GuestMemory.R32(sp + i * 4);
        return Draw(c, vp, fp, vUbs, fUbs, vTex, fTex, streams, GuestMemory.R32(draw + 4), GuestMemory.R32(draw + 16),
            GuestMemory.R32(draw + 20), GuestMemory.R32(draw + 24), GuestMemory.R32(draw + 28));
    }

    // =========================================================== programs

    static GxpParameter? ParamAt(uint program, uint param)
    {
        var info = GxmProgramInfo.Get(program);
        return info.Program.Parameters.FirstOrDefault(p => program + p.Offset == param);
    }

    [Hle] int sceGxmProgramCheck(uint program) => program == 0 ? ERR_INVALID_POINTER : GuestMemory.R32(program) == 0x00505847 ? 0 : ERR_INVALID_VALUE;
    [Hle] uint sceGxmProgramGetSize(uint program) => GuestMemory.R32(program + 8);
    [Hle] uint sceGxmProgramGetType(uint program) => GuestMemory.R32(program + 0x14) & 1;
    [Hle] bool sceGxmProgramIsNativeColorUsed(uint program) => (GuestMemory.R32(program + 0x14) & GxpProgram.FlagNativeColorUsed) != 0;
    [Hle] bool sceGxmProgramIsDiscardUsed(uint program) => (GuestMemory.R32(program + 0x14) & GxpProgram.FlagDiscardUsed) != 0;
    [Hle] bool sceGxmProgramIsDepthReplaceUsed(uint program) => (GuestMemory.R32(program + 0x14) & GxpProgram.FlagDepthUsed) != 0;
    [Hle] bool sceGxmProgramIsSpriteCoordUsed(uint program) => (GuestMemory.R32(program + 0x14) & GxpProgram.FlagSpriteCoordUsed) != 0;
    [Hle] bool sceGxmProgramIsFragColorUsed(uint program) => (GuestMemory.R32(program + 0x14) & GxpProgram.FlagFragColorUsed) != 0;
    [Hle] uint sceGxmProgramGetDefaultUniformBufferSize(uint program) => GuestMemory.R32(program + 0x64) * 4;
    [Hle] uint sceGxmProgramGetParameterCount(uint program) => GuestMemory.R32(program + 0x24);

    /// <summary>True when both GXP blobs are identical (same size at +8 and same bytes).</summary>
    [Hle]
    bool sceGxmProgramIsEquivalent(uint a, uint b)
    {
        if (a == 0 || b == 0) return false;
        if (a == b) return true;
        uint size = GuestMemory.R32(a + 8);
        if (size != GuestMemory.R32(b + 8) || size > 16 * 1024 * 1024) return false;
        return GuestMemory.Span(a, (int)size).SequenceEqual(GuestMemory.Span(b, (int)size));
    }

    /// <summary>SceGxmFragmentProgramInputs mask of the varyings a fragment program reads: POSITION 1, FOG 2, COLOR0 4,
    /// COLOR1 8, TEXCOORD0..9 0x10..0x2000, SPRITECOORD 0x4000. Each fragment attribute descriptor names up to two
    /// resources, one in the low nibble of its info word and one in bits 12-15 (0xF = unused).</summary>
    internal static uint FragmentProgramInputs(GxpProgram p)
    {
        if (!p.IsFragment) return 0;
        uint mask = 0;
        foreach (var d in p.FragmentAttributeDescriptors())
        {
            uint info = d.AttributeInfo;
            uint lo = info & 0xF;
            if (lo != 0xF)
            {
                if ((info & 0x400) != 0) mask |= 1u << 14;
                else if (lo <= 9) mask |= 1u << (int)(4 + lo);
            }
            uint hi = (info >> 12) & 0xF;
            if (hi != 0xF)
            {
                if ((info & 0x40000000) != 0) mask |= 1u << 14;
                else if (hi <= 9) mask |= 1u << (int)(4 + hi);
                else mask |= hi switch { 0xA => 1u << 2, 0xB => 1u << 3, 0xC => 1u << 1, 0xD => 1u, _ => 0u };
            }
        }
        return mask;
    }

    [Hle] uint sceGxmProgramGetFragmentProgramInputs(uint program) => program == 0 ? 0 : FragmentProgramInputs(GxmProgramInfo.Get(program).Program);

    /// <summary>SceGxmPassType of a patched fragment program: MASK_UPDATE 0x06000000, DEPTH_REPLACE 0x0A000000, DISCARD
    /// 0x04000000, TRANSLUCENT 0x02000000 (blending on), OPAQUE 0. The precedence among them is not confirmed (most
    /// restrictive first).</summary>
    [Hle]
    uint sceGxmFragmentProgramGetPassType(uint fp)
    {
        if (!_fragmentPrograms.TryGetValue(fp, out var f)) return 0;
        if (f.IsMaskUpdate) return 0x06000000;
        var p = f.Info?.Program;
        if (p != null && (p.Flags & GxpProgram.FlagDepthUsed) != 0) return 0x0A000000;
        if (p != null && (p.Flags & GxpProgram.FlagDiscardUsed) != 0) return 0x04000000;
        if (f.Blend is { } b && !(b.ColorFunc == 0 && b.AlphaFunc == 0) && !(b.ColorSrc == 1 && b.ColorDst == 0 && b.AlphaSrc == 1 && b.AlphaDst == 0))
            return 0x02000000;
        return 0;
    }

    [Hle]
    uint sceGxmProgramGetParameter(uint program, uint index)
    {
        var ps = GxmProgramInfo.Get(program).Program.Parameters;
        return index < ps.Count ? program + ps[(int)index].Offset : 0;
    }

    [Hle]
    uint sceGxmProgramFindParameterByName(uint program, string name)
    {
        if (program == 0) return 0;
        foreach (var p in GxmProgramInfo.Get(program).Program.Parameters)
            if (p.RawName == name) return program + p.Offset;
        return 0;
    }

    [Hle]
    uint sceGxmProgramFindParameterBySemantic(uint program, uint semantic, uint index)
    {
        if (program == 0) return 0;
        foreach (var p in GxmProgramInfo.Get(program).Program.Parameters)
            if (p.Category == ParamCategory.Attribute && p.Semantic == semantic && (p.SemanticIndex & 0xF) == index) return program + p.Offset;
        return 0;
    }

    [Hle]
    uint sceGxmProgramParameterGetIndex(uint program, uint param)
    {
        uint pbase = program + 0x28 + GuestMemory.R32(program + 0x28);
        return (param - pbase) / 16;
    }

    [Hle] uint sceGxmProgramParameterGetName(uint param) => param == 0 ? 0 : (uint)(param + (int)GuestMemory.R32(param));
    [Hle] uint sceGxmProgramParameterGetCategory(uint param) => GuestMemory.Read<byte>(param + 4) & 0xFu;
    [Hle] uint sceGxmProgramParameterGetType(uint param) => (uint)GuestMemory.Read<byte>(param + 4) >> 4;
    [Hle] uint sceGxmProgramParameterGetComponentCount(uint param) => GuestMemory.Read<byte>(param + 5) & 0xFu;
    [Hle] uint sceGxmProgramParameterGetContainerIndex(uint param) => (uint)GuestMemory.Read<byte>(param + 5) >> 4;
    [Hle] uint sceGxmProgramParameterGetSemantic(uint param) => (GuestMemory.Read<byte>(param + 4) & 0xF) == 0 ? GuestMemory.Read<byte>(param + 6) : 0u;
    [Hle] uint sceGxmProgramParameterGetSemanticIndex(uint param) => GuestMemory.Read<byte>(param + 7) & 0xFu;
    [Hle] uint sceGxmProgramParameterGetArraySize(uint param) => GuestMemory.R32(param + 8);
    [Hle] uint sceGxmProgramParameterGetResourceIndex(uint param) => GuestMemory.R32(param + 12);
    [Hle] bool sceGxmProgramParameterIsSamplerCube(uint param) => ((GuestMemory.Read<byte>(param + 7) >> 4) & 1) != 0;

    [Hle]
    bool sceGxmProgramParameterIsRegFormat(uint program, uint param)
    {
        if ((GuestMemory.Read<byte>(param + 4) & 0xF) != 0) return false;
        var prog = GxmProgramInfo.Get(program).Program;
        return ((prog.UntypedPaRegs >> (int)GuestMemory.R32(param + 12)) & 1) != 0;
    }

    [Hle]
    uint sceGxmProgramGetVertexProgramOutputs(uint program) => (uint)GxmProgramInfo.Get(program).Program.VertexOutputs(out _);

    [Hle]
    int sceGxmProgramGetOutputRegisterFormat(uint program, Ptr<uint> type, Ptr<uint> componentCount)
    {
        var p = GxmProgramInfo.Get(program).Program;
        if (!p.IsFragment) return ERR_INVALID_VALUE;
        type.TrySet((uint)p.OutputParamType);
        componentCount.TrySet(p.OutputCompCount);
        return 0;
    }

    [Hle] uint sceGxmVertexProgramGetProgram(uint vp) => _vertexPrograms.TryGetValue(vp, out var v) ? v.Info.Address : 0;
    [Hle] uint sceGxmFragmentProgramGetProgram(uint fp) => _fragmentPrograms.TryGetValue(fp, out var f) ? f.Info?.Address ?? 0 : 0;
    [Hle] bool sceGxmFragmentProgramIsEnabled(uint fp) => _fragmentPrograms.TryGetValue(fp, out var f) && f.Info != null && !f.IsMaskUpdate;

    [Hle]
    int sceGxmSetUniformDataF(uint buffer, uint param, uint componentOffset, uint componentCount, uint source)
    {
        if (buffer == 0 || param == 0 || source == 0) return ERR_INVALID_POINTER;
        byte b4 = GuestMemory.Read<byte>(param + 4), b5 = GuestMemory.Read<byte>(param + 5);
        if ((b4 & 0xF) != 1) return ERR_INVALID_VALUE;
        var type = (ParamType)(b4 >> 4);
        int compCountParam = b5 & 0xF;
        uint arraySize = GuestMemory.R32(param + 8), resIndex = GuestMemory.R32(param + 12);
        int compSize = type switch { ParamType.F32 or ParamType.U32 or ParamType.S32 => 4, ParamType.F16 or ParamType.U16 or ParamType.S16 => 2, _ => 1 };
        bool isFloat = type is ParamType.F32 or ParamType.F16;

        void WriteComp(uint dst, float v)
        {
            switch (type)
            {
                case ParamType.F32: GuestMemory.Write(dst, v); break;
                case ParamType.F16: GuestMemory.Write(dst, BitConverter.HalfToUInt16Bits((Half)v)); break;
                case ParamType.U32: GuestMemory.Write(dst, (uint)v); break;
                case ParamType.S32: GuestMemory.Write(dst, (int)v); break;
                case ParamType.U16: GuestMemory.Write(dst, (ushort)v); break;
                case ParamType.S16: GuestMemory.Write(dst, (short)v); break;
                case ParamType.U8: GuestMemory.Write(dst, (byte)v); break;
                case ParamType.S8: GuestMemory.Write(dst, (sbyte)v); break;
                default: GuestMemory.Write(dst, v); break;
            }
        }

        if (arraySize == 1 || compCountParam == 1)
        {
            if (compCountParam == 1) compSize = 4;
            uint dst = buffer + resIndex * 4 + componentOffset * (uint)compSize;
            for (uint i = 0; i < componentCount; i++) WriteComp(dst + i * (uint)compSize, GuestMemory.Read<float>(source + i * 4));
            return 0;
        }
        int size = compCountParam * compSize;
        int align = isFloat ? ((size & 7) != 0 ? 8 - (size & 7) : 0) : ((size & 3) != 0 ? 4 - (size & 3) : 0);
        int vecStart = (int)componentOffset / compCountParam;
        int cursor = (int)componentOffset % compCountParam;
        uint d = buffer + resIndex * 4 + (uint)(vecStart * (size + align) + cursor * compSize);
        int remainInElem = compCountParam - cursor;
        int left = (int)componentCount;
        uint s = source;
        while (left > 0)
        {
            int n = Math.Min(remainInElem, left);
            for (int i = 0; i < n; i++) WriteComp(d + (uint)(i * compSize), GuestMemory.Read<float>(s + (uint)i * 4));
            d += (uint)(compSize * remainInElem + align);
            s += (uint)n * 4;
            left -= n;
            remainInElem = compCountParam;
        }
        return 0;
    }

    // =========================================================== shader patcher

    [Hle]
    int sceGxmShaderPatcherCreate(uint p, Ptr<uint> outPatcher)
    {
        if (p == 0 || outPatcher.Address == 0) return ERR_INVALID_POINTER;
        uint h = AllocObj(64, "SceGxmShaderPatcher");
        var sp = new GxmShaderPatcher
        {
            Handle = h,
            UserData = GuestMemory.R32(p), HostAlloc = GuestMemory.R32(p + 4), HostFree = GuestMemory.R32(p + 8),
            BufferAlloc = GuestMemory.R32(p + 12), BufferFree = GuestMemory.R32(p + 16), BufferMem = GuestMemory.R32(p + 20), BufferMemSize = GuestMemory.R32(p + 24),
            VertexUsseAlloc = GuestMemory.R32(p + 28), VertexUsseFree = GuestMemory.R32(p + 32), VertexUsseMem = GuestMemory.R32(p + 36), VertexUsseMemSize = GuestMemory.R32(p + 40),
            FragmentUsseAlloc = GuestMemory.R32(p + 48), FragmentUsseFree = GuestMemory.R32(p + 52), FragmentUsseMem = GuestMemory.R32(p + 56), FragmentUsseMemSize = GuestMemory.R32(p + 60),
        };
        _patchers[h] = sp;
        outPatcher.Value = h;
        return 0;
    }

    [Hle] int sceGxmShaderPatcherDestroy(uint patcher) { _patchers.TryRemove(patcher, out _); return 0; }
    [Hle] uint sceGxmShaderPatcherGetUserData(uint patcher) => _patchers.TryGetValue(patcher, out var p) ? p.UserData : 0;
    [Hle] int sceGxmShaderPatcherSetUserData(uint patcher, uint data) { if (_patchers.TryGetValue(patcher, out var p)) p.UserData = data; return 0; }

    [Hle]
    int sceGxmShaderPatcherRegisterProgram(uint patcher, uint program, Ptr<uint> outId)
    {
        if (!_patchers.TryGetValue(patcher, out var sp) || program == 0 || outId.Address == 0) return ERR_INVALID_POINTER;
        uint id = AllocObj(16, "SceGxmRegisteredProgram");
        GuestMemory.W32(id, program);
        lock (sp) sp.Registered[id] = program;
        GxmProgramInfo.Get(program);
        outId.Value = id;
        return 0;
    }

    [Hle]
    int sceGxmShaderPatcherUnregisterProgram(uint patcher, uint id)
    {
        if (!_patchers.TryGetValue(patcher, out var sp)) return ERR_INVALID_POINTER;
        lock (sp) sp.Registered.Remove(id);
        return 0;
    }

    [Hle] int sceGxmShaderPatcherForceUnregisterProgram(uint patcher, uint id) => sceGxmShaderPatcherUnregisterProgram(patcher, id);
    [Hle] uint sceGxmShaderPatcherGetProgramFromId(uint id) => id == 0 ? 0 : GuestMemory.R32(id);

    [Hle]
    int sceGxmShaderPatcherCreateVertexProgram(uint patcher, uint programId, uint attributes, uint attributeCount, uint streams, uint streamCount, Ptr<uint> outVp)
    {
        if (!_patchers.TryGetValue(patcher, out var sp) || programId == 0 || outVp.Address == 0) return ERR_INVALID_POINTER;
        uint program = GuestMemory.R32(programId);
        var attrs = new GxmVertexAttribute[attributeCount];
        var hash = new System.IO.Hashing.XxHash64();
        for (uint i = 0; i < attributeCount; i++)
        {
            uint a = attributes + i * 8;
            attrs[i] = new GxmVertexAttribute(GuestMemory.Read<ushort>(a), GuestMemory.Read<ushort>(a + 2), GuestMemory.Read<byte>(a + 4),
                GuestMemory.Read<byte>(a + 5), GuestMemory.Read<ushort>(a + 6));
        }
        var strs = new GxmVertexStream[streamCount];
        for (uint i = 0; i < streamCount; i++)
            strs[i] = new GxmVertexStream(GuestMemory.Read<ushort>(streams + i * 4), GuestMemory.Read<ushort>(streams + i * 4 + 2));
        if (attributeCount > 0) hash.Append(GuestMemory.Span(attributes, (int)attributeCount * 8));
        if (streamCount > 0) hash.Append(GuestMemory.Span(streams, (int)streamCount * 4));
        var key = (program, hash.GetCurrentHashAsUInt64());
        lock (sp)
        {
            if (sp.VertexCache.TryGetValue(key, out var cached) && _vertexPrograms.ContainsKey(cached.Handle))
            {
                cached.RefCount++;
                outVp.Value = cached.Handle;
                return 0;
            }
            uint h = AllocObj(16, "SceGxmVertexProgram");
            GuestMemory.W32(h + 4, program);
            var vp = new GxmVertexProgram { Handle = h, Info = GxmProgramInfo.Get(program), Attributes = attrs, Streams = strs };
            _vertexPrograms[h] = vp;
            sp.VertexCache[key] = vp;
            outVp.Value = h;
        }
        return 0;
    }

    [Hle]
    int sceGxmShaderPatcherCreateFragmentProgram(uint patcher, uint programId, uint outputFormat, uint multisampleMode, uint blendInfo, uint vertexProgram, Ptr<uint> outFp)
    {
        if (!_patchers.TryGetValue(patcher, out var sp) || programId == 0 || outFp.Address == 0) return ERR_INVALID_POINTER;
        uint program = GuestMemory.R32(programId);
        var blend = GxmBlendInfo.Read(blendInfo);
        ulong blendKey = blendInfo == 0 ? ulong.MaxValue : GuestMemory.R32(blendInfo);
        var key = (program, outputFormat, multisampleMode, blendKey, vertexProgram);
        lock (sp)
        {
            if (sp.FragmentCache.TryGetValue(key, out var cached) && _fragmentPrograms.ContainsKey(cached.Handle))
            {
                cached.RefCount++;
                outFp.Value = cached.Handle;
                return 0;
            }
            uint h = AllocObj(16, "SceGxmFragmentProgram");
            GuestMemory.W32(h + 4, program);
            var fp = new GxmFragmentProgram
            {
                Handle = h, Info = GxmProgramInfo.Get(program), Blend = blend, OutputFormat = outputFormat, MultisampleMode = multisampleMode,
            };
            _fragmentPrograms[h] = fp;
            sp.FragmentCache[key] = fp;
            outFp.Value = h;
        }
        return 0;
    }

    [Hle]
    int sceGxmShaderPatcherCreateMaskUpdateFragmentProgram(uint patcher, Ptr<uint> outFp)
    {
        if (outFp.Address == 0) return ERR_INVALID_POINTER;
        uint h = AllocObj(16, "SceGxmFragmentProgram(mask)");
        _fragmentPrograms[h] = new GxmFragmentProgram { Handle = h, Info = null!, IsMaskUpdate = true };
        outFp.Value = h;
        return 0;
    }

    [Hle]
    int sceGxmShaderPatcherReleaseVertexProgram(uint patcher, uint vp)
    {
        if (_vertexPrograms.TryGetValue(vp, out var v) && --v.RefCount <= 0) _vertexPrograms.TryRemove(vp, out _);
        return 0;
    }

    [Hle]
    int sceGxmShaderPatcherReleaseFragmentProgram(uint patcher, uint fp)
    {
        if (_fragmentPrograms.TryGetValue(fp, out var f) && --f.RefCount <= 0) _fragmentPrograms.TryRemove(fp, out _);
        return 0;
    }

    [Hle] int sceGxmShaderPatcherAddRefVertexProgram(uint patcher, uint vp) { if (_vertexPrograms.TryGetValue(vp, out var v)) v.RefCount++; return 0; }
    [Hle] int sceGxmShaderPatcherAddRefFragmentProgram(uint patcher, uint fp) { if (_fragmentPrograms.TryGetValue(fp, out var f)) f.RefCount++; return 0; }

    [Hle]
    int sceGxmShaderPatcherGetVertexProgramRefCount(uint patcher, uint vp, Ptr<uint> count)
    {
        count.TrySet(_vertexPrograms.TryGetValue(vp, out var v) ? (uint)v.RefCount : 0);
        return 0;
    }

    [Hle]
    int sceGxmShaderPatcherGetFragmentProgramRefCount(uint patcher, uint fp, Ptr<uint> count)
    {
        count.TrySet(_fragmentPrograms.TryGetValue(fp, out var f) ? (uint)f.RefCount : 0);
        return 0;
    }

    [Hle] uint sceGxmShaderPatcherGetHostMemAllocated(uint patcher) => 0;
    [Hle] uint sceGxmShaderPatcherGetBufferMemAllocated(uint patcher) => 0;
    [Hle] uint sceGxmShaderPatcherGetVertexUsseMemAllocated(uint patcher) => 0;
    [Hle] uint sceGxmShaderPatcherGetFragmentUsseMemAllocated(uint patcher) => 0;

    // =========================================================== textures

    static void InitTexture(uint tex, uint data, uint format, uint width, uint height, uint mipCount, uint type)
    {
        uint mip = Math.Min(15u, mipCount - 1) & 0xF;
        uint w0 = (2u << 3) | (2u << 6) | (mip << 17) | (31u << 21) | (((format >> 31) & 1) << 31); // clamp/clamp, lod bias 31
        uint w1;
        if (type is TexSwizzled or TexCube)
            w1 = (uint)(BitOperations.Log2(Math.Max(height, 1)) & 0xF) | ((uint)(BitOperations.Log2(Math.Max(width, 1)) & 0xF) << 16);
        else
            w1 = ((height - 1) & 0xFFF) | (((width - 1) & 0xFFF) << 12);
        w1 |= ((format >> 24) & 0x1F) << 24 | (type >> 29) << 29;
        uint w2 = data & ~3u;
        uint w3 = (((format >> 12) & 7) << 28) | (1u << 31);
        GuestMemory.W32(tex, w0);
        GuestMemory.W32(tex + 4, w1);
        GuestMemory.W32(tex + 8, w2);
        GuestMemory.W32(tex + 12, w3);
    }

    static int TexInit(uint tex, uint data, uint format, uint width, uint height, uint mipCount, uint type, bool pow2)
    {
        if (tex == 0) return ERR_INVALID_POINTER;
        if (width > 4096 || height > 4096 || mipCount > 13) return ERR_INVALID_VALUE;
        if (pow2 && (width == 0 || height == 0 || !BitOperations.IsPow2(width) || !BitOperations.IsPow2(height))) return ERR_INVALID_ALIGNMENT;
        InitTexture(tex, data, format, width, height, Math.Max(mipCount, 1), type);
        return 0;
    }

    [Hle] int sceGxmTextureInitSwizzled(uint t, uint d, uint f, uint w, uint h, uint m) => TexInit(t, d, f, w, h, m, TexSwizzled, true);
    [Hle] int sceGxmTextureInitSwizzledArbitrary(uint t, uint d, uint f, uint w, uint h, uint m) => TexInit(t, d, f, w, h, m, TexSwizzledArbitrary, false);
    [Hle] int sceGxmTextureInitLinear(uint t, uint d, uint f, uint w, uint h, uint m) => TexInit(t, d, f, w, h, m, TexLinear, false);
    [Hle] int sceGxmTextureInitTiled(uint t, uint d, uint f, uint w, uint h, uint m) => TexInit(t, d, f, w, h, m, TexTiled, false);
    [Hle] int sceGxmTextureInitCube(uint t, uint d, uint f, uint w, uint h, uint m) => TexInit(t, d, f, w, h, m, TexCube, true);
    [Hle] int sceGxmTextureInitCubeArbitrary(uint t, uint d, uint f, uint w, uint h, uint m) => TexInit(t, d, f, w, h, m, TexCubeArbitrary, false);

    [Hle]
    int sceGxmTextureInitLinearStrided(uint tex, uint data, uint format, uint width, uint height, uint byteStride)
    {
        if (tex == 0) return ERR_INVALID_POINTER;
        if (width > 4096 || height > 4096) return ERR_INVALID_VALUE;
        if ((byteStride & 3) != 0) return ERR_INVALID_ALIGNMENT;
        if (byteStride < 4 || byteStride > 131072) return ERR_INVALID_VALUE;
        uint w0 = EncodeStride((2u << 3) | (2u << 6) | (((format >> 31) & 1) << 31), byteStride);
        uint w1 = ((height - 1) & 0xFFF) | (((width - 1) & 0xFFF) << 12) | (((format >> 24) & 0x1F) << 24) | ((TexLinearStrided >> 29) << 29);
        GuestMemory.W32(tex, w0);
        GuestMemory.W32(tex + 4, w1);
        GuestMemory.W32(tex + 8, data & ~3u);
        GuestMemory.W32(tex + 12, (((format >> 12) & 7) << 28) | (1u << 31));
        return 0;
    }

    static bool IsStrided(uint tex) => (GuestMemory.R32(tex + 4) >> 29) == (TexLinearStrided >> 29);

    /// <summary>Linear strided byte stride, as s = stride/4 - 1 spread over word 0: bit 9, bits 10-11, 17-20 and 21-26.</summary>
    const uint StrideBitsMask = (1u << 9) | (3u << 10) | (0xFu << 17) | (0x3Fu << 21);

    static uint EncodeStride(uint w0, uint byteStride)
    {
        uint s = (byteStride >> 2) - 1;
        return (w0 & ~StrideBitsMask) | ((s & 1) << 9) | (((s >> 1) & 3) << 10) | (((s >> 3) & 0xF) << 17) | (((s >> 7) & 0x3F) << 21);
    }

    [Hle]
    int sceGxmTextureSetStride(uint tex, uint byteStride)
    {
        if (tex == 0) return ERR_INVALID_POINTER;
        if (!IsStrided(tex)) return ERR_UNSUPPORTED;
        if ((byteStride & 3) != 0) return ERR_INVALID_ALIGNMENT;
        if (byteStride < 4 || byteStride > 131072) return ERR_INVALID_VALUE;
        GuestMemory.W32(tex, EncodeStride(GuestMemory.R32(tex), byteStride));
        return 0;
    }

    static int SetBits(uint addr, int shift, int bits, uint value)
    {
        if (addr == 0) return ERR_INVALID_POINTER;
        uint mask = ((1u << bits) - 1) << shift;
        uint w = GuestMemory.R32(addr);
        GuestMemory.W32(addr, (w & ~mask) | ((value << shift) & mask));
        return 0;
    }

    [Hle] uint sceGxmTextureGetData(uint tex) => GuestMemory.R32(tex + 8) & ~3u;
    [Hle] int sceGxmTextureSetData(uint tex, uint data) => SetBits(tex + 8, 2, 30, data >> 2);
    [Hle] int sceGxmTextureSetMagFilter(uint tex, uint f) => SetBits(tex, 12, 2, f);
    [Hle] int sceGxmTextureSetMinFilter(uint tex, uint f) => IsStrided(tex) ? 0 : SetBits(tex, 10, 2, f);
    [Hle] int sceGxmTextureSetMipFilter(uint tex, uint f) => IsStrided(tex) ? 0 : SetBits(tex, 9, 1, f != 0 ? 1u : 0);
    [Hle] int sceGxmTextureSetUAddrMode(uint tex, uint m) => SetBits(tex, 6, 3, m);
    [Hle] int sceGxmTextureSetVAddrMode(uint tex, uint m) => SetBits(tex, 3, 3, m);
    [Hle] int sceGxmTextureSetUAddrModeSafe(uint tex, uint m) => tex != 0 && IsStrided(tex) ? 0 : SetBits(tex, 6, 3, m);
    [Hle] int sceGxmTextureSetVAddrModeSafe(uint tex, uint m) => tex != 0 && IsStrided(tex) ? 0 : SetBits(tex, 3, 3, m);
    /// <summary>Linear strided textures do not support address modes: they read as CLAMP (2).</summary>
    [Hle] uint sceGxmTextureGetUAddrModeSafe(uint tex) => IsStrided(tex) ? 2u : (GuestMemory.R32(tex) >> 6) & 7;
    [Hle] uint sceGxmTextureGetVAddrModeSafe(uint tex) => IsStrided(tex) ? 2u : (GuestMemory.R32(tex) >> 3) & 7;
    [Hle] int sceGxmTextureSetLodBias(uint tex, uint bias) => IsStrided(tex) ? 0 : SetBits(tex, 21, 6, bias);
    [Hle] int sceGxmTextureSetLodMin(uint tex, uint lodMin) { if (IsStrided(tex)) return 0; int r = SetBits(tex + 8, 0, 2, lodMin & 3); return r != 0 ? r : SetBits(tex + 12, 26, 2, lodMin >> 2); }
    [Hle] uint sceGxmTextureGetLodMin(uint tex) => IsStrided(tex) ? 0 : GxmTextureDesc.Read(tex).LodMin;
    [Hle] int sceGxmTextureSetGammaMode(uint tex, uint mode) => SetBits(tex, 27, 2, mode >> 27);
    [Hle] uint sceGxmTextureGetGammaMode(uint tex) => ((GuestMemory.R32(tex) >> 27) & 3) << 27;
    [Hle] int sceGxmTextureSetPalette(uint tex, uint data) => SetBits(tex + 12, 0, 26, data >> 6);
    [Hle] int sceGxmTextureSetNormalizeMode(uint tex, uint mode) => SetBits(tex + 12, 31, 1, mode >> 31);
    [Hle] uint sceGxmTextureGetNormalizeMode(uint tex) => GuestMemory.R32(tex + 12) & 0x80000000;
    [Hle] uint sceGxmTextureGetPalette(uint tex) => (GuestMemory.R32(tex + 12) & 0x03FFFFFF) << 6;
    [Hle] uint sceGxmTextureGetFormat(uint tex) => GxmTextureDesc.Read(tex).Format;
    [Hle] uint sceGxmTextureGetType(uint tex) => GuestMemory.R32(tex + 4) & 0xE0000000;
    [Hle] uint sceGxmTextureGetWidth(uint tex) => GxmTextureDesc.Read(tex).Width;
    [Hle] uint sceGxmTextureGetHeight(uint tex) => GxmTextureDesc.Read(tex).Height;
    [Hle] uint sceGxmTextureGetMipmapCount(uint tex) => GxmTextureDesc.Read(tex).MipCount;
    [Hle] uint sceGxmTextureGetStride(uint tex) => IsStrided(tex) ? GxmTextureDesc.Read(tex).StrideBytes : 0;
    [Hle] uint sceGxmTextureGetMagFilter(uint tex) => (GuestMemory.R32(tex) >> 12) & 3;
    [Hle] uint sceGxmTextureGetMinFilter(uint tex) => IsStrided(tex) ? 0 : (GuestMemory.R32(tex) >> 10) & 3;
    [Hle] uint sceGxmTextureGetMipFilter(uint tex) => IsStrided(tex) ? 0 : (GuestMemory.R32(tex) >> 9) & 1;
    [Hle] uint sceGxmTextureGetUAddrMode(uint tex) => (GuestMemory.R32(tex) >> 6) & 7;
    [Hle] uint sceGxmTextureGetVAddrMode(uint tex) => (GuestMemory.R32(tex) >> 3) & 7;
    [Hle] uint sceGxmTextureGetLodBias(uint tex) => IsStrided(tex) ? 0 : (GuestMemory.R32(tex) >> 21) & 0x3F;
    [Hle] int sceGxmTextureValidate(uint tex) => tex == 0 ? ERR_INVALID_POINTER : 0;

    [Hle]
    int sceGxmTextureSetFormat(uint tex, uint format)
    {
        if (tex == 0) return ERR_INVALID_POINTER;
        SetBits(tex, 31, 1, format >> 31);
        SetBits(tex + 4, 24, 5, format >> 24);
        SetBits(tex + 12, 28, 3, format >> 12);
        return 0;
    }

    [Hle]
    int sceGxmTextureSetWidth(uint tex, uint width)
    {
        if (tex == 0) return ERR_INVALID_POINTER;
        uint type = GuestMemory.R32(tex + 4) & 0xE0000000;
        return type is TexSwizzled or TexCube ? SetBits(tex + 4, 16, 4, (uint)BitOperations.Log2(Math.Max(width, 1))) : SetBits(tex + 4, 12, 12, width - 1);
    }

    [Hle]
    int sceGxmTextureSetHeight(uint tex, uint height)
    {
        if (tex == 0) return ERR_INVALID_POINTER;
        uint type = GuestMemory.R32(tex + 4) & 0xE0000000;
        return type is TexSwizzled or TexCube ? SetBits(tex + 4, 0, 4, (uint)BitOperations.Log2(Math.Max(height, 1))) : SetBits(tex + 4, 0, 12, height - 1);
    }

    [Hle] int sceGxmTextureSetMipmapCount(uint tex, uint count) => IsStrided(tex) ? 0 : SetBits(tex, 17, 4, Math.Min(15u, count - 1));

    // =========================================================== transfers (CPU, in GPU order)

    static int TransferBpp(uint format) => format switch
    {
        0x00000000 => 1,
        0x00010000 or 0x00020000 or 0x00030000 or 0x00040000 or 0x000F0000 => 2,
        0x00050000 => 3,
        0x00070000 or 0x00080000 or 0x00090000 or 0x000A0000 => 2,
        0x00120000 => 8,
        0x00130000 => 16,
        _ => 4,
    };

    static uint TransferOffset(uint x, uint y, uint strideBytes, int bpp, uint type, uint w, uint h) => type switch
    {
        0x00400000 => TextureLayout.Tiled(x, y, strideBytes / (uint)bpp) * (uint)bpp,
        0x00800000 => TextureLayout.Morton(x, y, w, h) * (uint)bpp,
        _ => y * strideBytes + x * (uint)bpp,
    };

    [Hle]
    int sceGxmTransferCopy(uint width, uint height, uint keyValue, uint keyMask, uint keyMode, uint srcFormat, uint srcType, uint srcAddr,
        uint srcX, uint srcY, int srcStride, uint dstFormat, uint dstType, uint dstAddr, uint dstX, uint dstY, int dstStride,
        uint syncObject, uint syncFlags, uint notification)
    {
        if (srcAddr == 0 || dstAddr == 0) return ERR_INVALID_POINTER;
        int bpp = TransferBpp(srcFormat);
        var notif = ReadNotification(notification);
        Gpu.Submit(new CallbackCommand
        {
            WriteAddress = dstAddr, WriteSize = (uint)Math.Abs(dstStride) * (dstY + height),
            Action = () =>
            {
                for (uint y = 0; y < height; y++)
                    for (uint x = 0; x < width; x++)
                    {
                        uint so = TransferOffset(srcX + x, srcY + y, (uint)Math.Abs(srcStride), bpp, srcType, width, height);
                        uint d = TransferOffset(dstX + x, dstY + y, (uint)Math.Abs(dstStride), bpp, dstType, width, height);
                        if (keyMode != 0 && bpp == 4)
                        {
                            uint v = GuestMemory.R32(srcAddr + so);
                            bool match = (v & keyMask) == keyValue;
                            if ((keyMode == 1 && !match) || (keyMode == 2 && match)) continue;
                        }
                        GuestMemory.Copy(dstAddr + d, srcAddr + so, (uint)bpp);
                    }
                if (notif is { } n && n.Item1 != 0) lock (Gpu.NotificationLock) { GuestMemory.W32(n.Item1, n.Item2); Monitor.PulseAll(Gpu.NotificationLock); }
            },
        });
        return 0;
    }

    [Hle]
    int sceGxmTransferFill(uint color, uint dstFormat, uint dstAddr, uint dstX, uint dstY, uint width, uint height, int dstStride,
        uint syncObject, uint syncFlags, uint notification)
    {
        if (dstAddr == 0) return ERR_INVALID_POINTER;
        int bpp = TransferBpp(dstFormat);
        var notif = ReadNotification(notification);
        Gpu.Submit(new CallbackCommand
        {
            WriteAddress = dstAddr, WriteSize = (uint)Math.Abs(dstStride) * (dstY + height),
            Action = () =>
            {
                for (uint y = 0; y < height; y++)
                {
                    uint row = dstAddr + (dstY + y) * (uint)Math.Abs(dstStride) + dstX * (uint)bpp;
                    for (uint x = 0; x < width; x++)
                    {
                        uint a = row + x * (uint)bpp;
                        switch (bpp)
                        {
                            case 1: GuestMemory.Write(a, (byte)color); break;
                            case 2: GuestMemory.Write(a, (ushort)color); break;
                            case 3: GuestMemory.Write(a, (ushort)color); GuestMemory.Write(a + 2, (byte)(color >> 16)); break;
                            default: GuestMemory.W32(a, color); break;
                        }
                    }
                }
                if (notif is { } n && n.Item1 != 0) lock (Gpu.NotificationLock) { GuestMemory.W32(n.Item1, n.Item2); Monitor.PulseAll(Gpu.NotificationLock); }
            },
        });
        return 0;
    }

    [Hle]
    int sceGxmTransferDownscale(uint srcFormat, uint srcAddr, uint srcX, uint srcY, uint srcWidth, uint srcHeight, int srcStride,
        uint dstFormat, uint dstAddr, uint dstX, uint dstY, int dstStride, uint syncObject, uint syncFlags, uint notification)
    {
        if (srcAddr == 0 || dstAddr == 0) return ERR_INVALID_POINTER;
        int bpp = TransferBpp(srcFormat);
        var notif = ReadNotification(notification);
        Log.DebugOnce($"downscale-{srcAddr:X8}-{dstAddr:X8}", $"[gxm] TransferDownscale 0x{srcAddr:X8} ({srcX},{srcY} {srcWidth}x{srcHeight} stride {srcStride}) -> 0x{dstAddr:X8} ({dstX},{dstY} stride {dstStride}) fmt 0x{srcFormat:X8}");
        Gpu.Submit(new CallbackCommand
        {
            WriteAddress = dstAddr, WriteSize = (uint)Math.Abs(dstStride) * (dstY + srcHeight / 2),
            Action = () =>
            {
                uint ss = (uint)Math.Abs(srcStride), ds = (uint)Math.Abs(dstStride);
                for (uint y = 0; y < srcHeight / 2; y++)
                    for (uint x = 0; x < srcWidth / 2; x++)
                    {
                        uint s0 = srcAddr + (srcY + 2 * y) * ss + (srcX + 2 * x) * (uint)bpp;
                        uint d = dstAddr + (dstY + y) * ds + (dstX + x) * (uint)bpp;
                        if (bpp == 4)
                        {
                            uint a = GuestMemory.R32(s0), b = GuestMemory.R32(s0 + 4), c = GuestMemory.R32(s0 + ss), e = GuestMemory.R32(s0 + ss + 4);
                            uint r = 0;
                            for (int k = 0; k < 32; k += 8)
                                r |= ((((a >> k) & 0xFF) + ((b >> k) & 0xFF) + ((c >> k) & 0xFF) + ((e >> k) & 0xFF) + 2) / 4) << k;
                            GuestMemory.W32(d, r);
                        }
                        else GuestMemory.Copy(d, s0, (uint)bpp);
                    }
                if (notif is { } n && n.Item1 != 0) lock (Gpu.NotificationLock) { GuestMemory.W32(n.Item1, n.Item2); Monitor.PulseAll(Gpu.NotificationLock); }
            },
        });
        return 0;
    }
}
