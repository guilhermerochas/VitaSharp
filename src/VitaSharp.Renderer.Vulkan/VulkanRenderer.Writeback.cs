using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Util;
using VitaSharp.Core.Memory;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Render-target writeback: on the Vita, rendered pixels land in memory and games read them back with the CPU
/// (average luminance for auto exposure, picking, screenshots). Small targets are copied out after every scene and
/// written into guest memory when the frame's GPU work has completed (one or two frames later).
/// Surfaces that are rendered in a wider host format than their guest layout (U2F10F10F10, U8U8U8) are converted
/// back to the guest layout on the CPU so the game reads exactly what the hardware would have written.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Targets up to this many pixels are written back (luminance / histogram / small effect buffers).</summary>
    static readonly uint WritebackMaxPixels = uint.TryParse(Environment.GetEnvironmentVariable(EnvVars.WritebackMax), out var wb) ? wb : 512 * 512;

    enum WritebackConvert : byte { None, SwapRb, Half4ToU2F10F10F10, Rgba8ToRgb8 }

    sealed class RtWriteback
    {
        public Buffer Buffer;
        public DeviceMemory Memory;
        public nint Ptr;
        public ulong Size;
        /// <summary>False: non-coherent cached memory, invalidated before the CPU reads it.</summary>
        public bool Coherent = true;
        public uint Address, Width, Rows, HostStride, GuestStride, GuestBpp;
        public WritebackConvert Convert;
        /// <summary>The address space of the process whose scene rendered the target (the frame retires later, maybe
        /// while another process's command runs).</summary>
        public AddressSpace? Space;
    }

    readonly Dictionary<ulong, Stack<RtWriteback>> _writebackPool = new();
    readonly HashSet<(uint, uint, uint)> _tracedWritebacks = new();

    /// <summary>How a host pixel row is turned into the guest layout; null when no conversion is known.</summary>
    static WritebackConvert? WritebackConversion(uint colorFormat, Format hostFormat, int hostBpp, int guestBpp)
    {
        if (hostFormat == Format.B8G8R8A8Unorm) return WritebackConvert.SwapRb;
        if (hostBpp == guestBpp) return WritebackConvert.None;
        return (colorFormat & 0xF1800000) switch
        {
            0x41000000 when hostFormat == Format.R16G16B16A16Sfloat => WritebackConvert.Half4ToU2F10F10F10,
            0x10000000 when hostFormat == Format.R8G8B8A8Unorm => WritebackConvert.Rgba8ToRgb8,
            _ => null,
        };
    }

    /// <summary>
    /// Targets above <see cref="WritebackMaxPixels"/> are not copied out whole, but the small rectangles their draws
    /// rendered are (draws whose viewport within the region clip covers at most this many pixels). GPU reductions end
    /// in such a draw: Gravity Rush renders its average-luminance block (32x16) into a corner of a 768x384
    /// post-processing atlas and reads it with the CPU for auto exposure; without it the game read zeros and its tone
    /// mapping washed every frame out to near white.
    /// </summary>
    static readonly uint WritebackRegionMaxPixels = uint.TryParse(Environment.GetEnvironmentVariable(EnvVars.WritebackRegionMax), out var wr) ? wr : 64 * 64;
    const int MaxWritebackRegions = 16;

    /// <summary>Small pixel rectangles drawn in the current scene of a target too large to write back whole.</summary>
    readonly List<(uint X, uint Y, uint W, uint H)> _sceneRegions = new();

    /// <summary>Records the rectangle a draw can write (viewport within the region clip) when it is small.</summary>
    void NoteWritebackRegion(in Core.Gpu.DrawState st)
    {
        var rt = _rt;
        // (a scene rendered without MSAA downscale has 2x2 surface pixels per scene pixel: not mapped here)
        if (rt == null || _msaaNoDownscale || rt.Width * rt.Height <= WritebackMaxPixels || WritebackRegionMaxPixels == 0 || _sceneRegions.Count >= MaxWritebackRegions) return;
        int w = (int)Math.Min(_sceneW, rt.Width), h = (int)Math.Min(_sceneH, rt.Height);
        int x0 = 0, y0 = 0, x1 = w, y1 = h;
        if (st.RegionClipMode == 2) { x0 = st.ClipMinX; y0 = st.ClipMinY; x1 = st.ClipMaxX + 1; y1 = st.ClipMaxY + 1; }
        if (st.ViewportEnabled)
        {
            float hx = Math.Abs(st.VpScaleX), hy = Math.Abs(st.VpScaleY);
            x0 = Math.Max(x0, (int)MathF.Floor(st.VpOffX - hx)); x1 = Math.Min(x1, (int)MathF.Ceiling(st.VpOffX + hx));
            y0 = Math.Max(y0, (int)MathF.Floor(st.VpOffY - hy)); y1 = Math.Min(y1, (int)MathF.Ceiling(st.VpOffY + hy));
        }
        x0 = Math.Clamp(x0, 0, w); x1 = Math.Clamp(x1, x0, w);
        y0 = Math.Clamp(y0, 0, h); y1 = Math.Clamp(y1, y0, h);
        uint rw = (uint)(x1 - x0), rh = (uint)(y1 - y0);
        if (rw == 0 || rh == 0 || rw * rh > WritebackRegionMaxPixels) return;
        foreach (var r in _sceneRegions)
            if (x0 >= r.X && y0 >= r.Y && x1 <= r.X + r.W && y1 <= r.Y + r.H) return;
        _sceneRegions.Add(((uint)x0, (uint)y0, rw, rh));
    }

    bool ScheduleWriteback(RenderTarget rt)
    {
        if (rt.Address == 0) return false;
        bool whole = rt.Width * rt.Height <= WritebackMaxPixels;
        if (!whole && _sceneRegions.Count == 0) return false;
        int guestBpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
        int hostBpp = HostBytesPerPixel(rt.Format);
        var conv = WritebackConversion(rt.ColorFormat, rt.Format, hostBpp, guestBpp);
        if (conv == null)
        {
            Log.WarnOnce($"writeback-fmt-{rt.ColorFormat:X8}", $"[gpu] render target 0x{rt.Address:X8} ({rt.Format}) is not written back: no conversion to guest format 0x{rt.ColorFormat:X8}");
            return false;
        }
        uint width = Math.Min(rt.Width, rt.Stride);

        rt.Dirty = true;
        rt.WrittenBack = true;
        SyncRenderTargetForRead(rt); // (also marks the target used by this frame: eviction keeps it)
        // a whole lossless copy that UploadSurfaceFromGuest can load back: the target may be evicted and recreated
        // (region writebacks leave the rest of the surface only on the GPU)
        if (whole && conv == WritebackConvert.None && guestBpp == 4 && hostBpp == 4 && width == rt.Width) rt.InMemoryVersion = rt.Version;
        if (whole) { CopyOutRect(rt, 0, 0, width, rt.Height, hostBpp, guestBpp, conv.Value); rt.MemVersion = rt.Version; }
        else
            foreach (var r in _sceneRegions)
            {
                uint w = Math.Min(r.W, width - Math.Min(r.X, width)), h = Math.Min(r.H, rt.Height - Math.Min(r.Y, rt.Height));
                if (w > 0 && h > 0) CopyOutRect(rt, r.X, r.Y, w, h, hostBpp, guestBpp, conv.Value);
            }
        if (TraceSync && _tracedWritebacks.Add((rt.Address, rt.Width, rt.Height)))
            Log.Info($"[sync] writeback target 0x{rt.Address:X8} {rt.Width}x{rt.Height} stride {rt.Stride} fmt 0x{rt.ColorFormat:X8} ({rt.Format}, {conv}) present {_presentCount}" +
                (whole ? "" : $", regions {string.Join(" ", _sceneRegions.Select(r => $"({r.X},{r.Y} {r.W}x{r.H})"))}"));
        return true;
    }

    /// <summary>
    /// GPU transfers (sceGxmTransferCopy/Downscale) run on the CPU over guest memory. When their source is a rendered
    /// surface whose pixels are only on the GPU (too large for the per-scene writeback), the surface is copied out and
    /// the GPU work so far is waited for first. WRC 4 renders each car's livery into a 1024x1024 surface at load time
    /// and builds the car texture's mip chain from it with TransferCopy + TransferDownscale; reading the never
    /// written-back memory made every car black.
    /// </summary>
    public void BeforeCallback(CallbackCommand cb)
    {
        if (cb.ReadSize == 0) return;
        ulong lo = cb.ReadAddress, hi = lo + cb.ReadSize;
        bool any = false, swept = false;
        // a snapshot: the writeback buffer allocation may reclaim memory, and eviction removes targets from the map
        _xferTargets.Clear();
        _xferTargets.AddRange(_renderTargets.Values);
        foreach (var rt in _xferTargets)
        {
            if (rt.Address == 0 || rt.Version == rt.MemVersion) continue;
            ulong end = rt.Address + (ulong)rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height;
            if (lo >= end || hi <= rt.Address) continue;
            // evicted by a reclaim during an earlier writeback of this loop (its image is gone)
            if (!_renderTargets.TryGetValue((rt.Address, rt.Width, rt.Height, rt.ColorFormat), out var live) || live != rt) continue;
            // memory written after the surface was last rendered (an earlier transfer into a stale surface's memory,
            // data loaded there) is newer than the surface: the writeback would overwrite it
            if (!swept) { _sweptFrame = -1; swept = true; } // fold the stores made since this frame's sweep
            if (OverwrittenSinceRendered(rt, cb.ReadAddress, cb.ReadSize)) continue;
            // a surface still being rendered (transfers come between scenes; never seen inside one)
            if (_inScene) { Log.WarnOnce($"xfer-inscene-{rt.Address:X8}", $"[gpu] transfer reads render target 0x{rt.Address:X8} while a scene is open: not written back"); continue; }
            int guestBpp = Formats.ColorBytesPerPixel(rt.ColorFormat), hostBpp = HostBytesPerPixel(rt.Format);
            if (WritebackConversion(rt.ColorFormat, rt.Format, hostBpp, guestBpp) is not { } conv)
            {
                Log.WarnOnce($"writeback-fmt-{rt.ColorFormat:X8}", $"[gpu] render target 0x{rt.Address:X8} ({rt.Format}) is not written back: no conversion to guest format 0x{rt.ColorFormat:X8}");
                continue;
            }
            BeginFrameIfNeeded();
            rt.Dirty = true;
            rt.WrittenBack = true;
            SyncRenderTargetForRead(rt);
            uint width = Math.Min(rt.Width, rt.Stride);
            if (conv == WritebackConvert.None && guestBpp == 4 && hostBpp == 4 && width == rt.Width) rt.InMemoryVersion = rt.Version; // evictable, as in ScheduleWriteback
            CopyOutRect(rt, 0, 0, width, rt.Height, hostBpp, guestBpp, conv);
            rt.MemVersion = rt.Version;
            if (_xferWritebacks++ < 20 || TraceSync)
                Log.Info($"[gpu] transfer reads render target 0x{rt.Address:X8} {rt.Width}x{rt.Height}: written back first (present {_presentCount})");
            any = true;
        }
        _xferTargets.Clear();
        if (any) Finish(); // submits and retires every frame: the rows are in guest memory when it returns
    }

    readonly List<RenderTarget> _xferTargets = new();

    int _xferWritebacks;

    /// <summary>Copies one pixel rectangle of the target into a host buffer (into guest memory once the frame completed).</summary>
    void CopyOutRect(RenderTarget rt, uint x, uint y, uint w, uint h, int hostBpp, int guestBpp, WritebackConvert conv)
    {
        ulong size = (ulong)w * (uint)hostBpp * h;
        if (!_writebackPool.TryGetValue(size, out var pool)) _writebackPool[size] = pool = new();
        _writebackPoolUsed[size] = FrameCount;
        var wb = pool.Count > 0 ? pool.Pop() : null;
        if (wb == null)
        {
            wb = new RtWriteback { Size = size };
            (wb.Buffer, wb.Memory, wb.Ptr) = _core.CreateReadbackBuffer(size, BufferUsageFlags.TransferDstBit, MemCat.Readback, out wb.Coherent);
        }
        wb.GuestStride = rt.Stride * (uint)guestBpp;
        wb.Address = rt.Address + y * wb.GuestStride + x * (uint)guestBpp;
        wb.Width = w;
        wb.Rows = h;
        wb.HostStride = w * (uint)hostBpp;
        wb.GuestBpp = (uint)guestBpp;
        wb.Convert = conv;
        wb.Space = AddressSpace.Current;
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = w,
            BufferImageHeight = h,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D((int)x, (int)y, 0),
            ImageExtent = new Extent3D(w, h, 1),
        };
        _vk.CmdCopyImageToBuffer(Cmd, rt.Image, ImageLayout.General, wb.Buffer, 1, in region);
        F.Writebacks.Add(wb);
    }

    /// <summary>Runs after the frame's fence signalled: copies each target's rows into guest memory.</summary>
    void CompleteWritebacks(Frame f)
    {
        if (f.Writebacks.Count == 0) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var wb in f.Writebacks)
        {
            using var space = AddressSpace.Use(wb.Space);
            if (!wb.Coherent) _core.InvalidateMapped(wb.Memory);
            var src = new ReadOnlySpan<byte>((void*)wb.Ptr, (int)wb.Size);
            for (uint y = 0; y < wb.Rows; y++)
            {
                var row = src.Slice((int)(y * wb.HostStride));
                switch (wb.Convert)
                {
                    case WritebackConvert.None:
                    {
                        var dst = GuestMemory.Span(wb.Address + y * wb.GuestStride, (int)(wb.Width * wb.GuestBpp));
                        row[..dst.Length].CopyTo(dst);
                        break;
                    }
                    case WritebackConvert.SwapRb:
                    {
                        var dst = GuestMemory.Span(wb.Address + y * wb.GuestStride, (int)wb.Width * 4);
                        row[..dst.Length].CopyTo(dst);
                        for (int i = 0; i + 3 < dst.Length; i += 4) (dst[i], dst[i + 2]) = (dst[i + 2], dst[i]);
                        break;
                    }
                    case WritebackConvert.Half4ToU2F10F10F10:
                    {
                        var dst = GuestMemory.Span(wb.Address + y * wb.GuestStride, (int)wb.Width * 4);
                        var h = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(row[..(int)(wb.Width * 8)]);
                        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst);
                        var lut = F10Lut;
                        for (int i = 0; i < d.Length; i++)
                        {
                            uint r = lut[h[i * 4]] & 0x3FFu, g = lut[h[i * 4 + 1]] & 0x3FFu, b = lut[h[i * 4 + 2]] & 0x3FFu, a2 = (uint)lut[h[i * 4 + 3]] >> 10;
                            d[i] = r | (g << 10) | (b << 20) | (a2 << 30);
                        }
                        break;
                    }
                    case WritebackConvert.Rgba8ToRgb8:
                    {
                        var dst = GuestMemory.Span(wb.Address + y * wb.GuestStride, (int)wb.Width * 3);
                        for (int i = 0, o = 0; o + 2 < dst.Length; i += 4, o += 3) { dst[o] = row[i]; dst[o + 1] = row[i + 1]; dst[o + 2] = row[i + 2]; }
                        break;
                    }
                }
            }
            _writebackPool[wb.Size].Push(wb);
            _wbCount++;
            _wbBytes += wb.Rows * wb.Width * wb.GuestBpp;
        }
        // the rows just written mark their pages: sweep again before the next texture check, so a texture over that
        // memory is re-read in this frame already (as when every texture was hashed every frame)
        _sweptFrame = -1;
        f.Writebacks.Clear();
        _wbTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>
    /// Half bits to the guest U2F10F10F10 channels, one lookup per channel (luminance targets are converted every
    /// frame): bits 0-9 hold the unsigned 10-bit float, bits 10-11 the half read as a 2-bit alpha.
    /// </summary>
    static readonly ushort[] F10Lut = BuildF10Lut();

    static ushort[] BuildF10Lut()
    {
        var t = new ushort[65536];
        for (int i = 0; i < t.Length; i++)
        {
            float a = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            uint a2 = (uint)Math.Clamp((int)MathF.Round(a * 3f), 0, 3); // NaN rounds to int.MinValue: 0
            t[i] = (ushort)(HalfToF10((ushort)i) | a2 << 10);
        }
        return t;
    }

    /// <summary>Half (1.5.10) to the surface's unsigned 10-bit float (5.5): negatives clamp to zero, round to nearest.</summary>
    static uint HalfToF10(ushort half)
    {
        if ((half & 0x8000) != 0) return 0;
        uint exp = (uint)(half >> 10) & 0x1F, man = (uint)half & 0x3FF;
        if (exp == 0x1F) return 0x3FF; // inf/nan -> max
        uint m5 = man >> 5;
        if ((man & 0x10) != 0 && (m5 != 0x1F || exp != 0x1E)) { m5++; if (m5 == 0x20) { m5 = 0; exp++; } }
        if (exp >= 0x1F) return 0x3FF;
        return (exp << 5) | m5;
    }

    /// <summary>GPU frame each writeback buffer size was last used in (region writebacks come in many sizes).</summary>
    readonly Dictionary<ulong, long> _writebackPoolUsed = new();
    readonly List<ulong> _writebackIdle = new();

    /// <summary>
    /// Destroys pooled (idle: their frames completed) writeback buffers of sizes unused for <paramref name="minAge"/>
    /// GPU frames. Region writebacks of oversized targets use a buffer per rectangle size, so without this every size
    /// ever seen kept its buffers. Returns the bytes freed.
    /// </summary>
    ulong TrimWritebackPool(long minAge)
    {
        ulong freed = 0;
        _writebackIdle.Clear();
        foreach (var (size, pool) in _writebackPool)
            if (FrameCount - _writebackPoolUsed.GetValueOrDefault(size) >= minAge) _writebackIdle.Add(size);
        foreach (var size in _writebackIdle)
        {
            var pool = _writebackPool[size];
            while (pool.Count > 0) { var wb = pool.Pop(); _core.DestroyBuffer(wb.Buffer, wb.Memory); freed += wb.Size; }
        }
        return freed;
    }

    void DestroyWritebacks()
    {
        foreach (var pool in _writebackPool.Values)
            foreach (var wb in pool) _core.DestroyBuffer(wb.Buffer, wb.Memory);
        _writebackPool.Clear();
    }
}
