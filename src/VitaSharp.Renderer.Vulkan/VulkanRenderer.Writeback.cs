using Silk.NET.Vulkan;
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
    static readonly uint WritebackMaxPixels = uint.TryParse(Environment.GetEnvironmentVariable("VITASHARP_WRITEBACK_MAX"), out var wb) ? wb : 512 * 512;

    enum WritebackConvert : byte { None, SwapRb, Half4ToU2F10F10F10, Rgba8ToRgb8 }

    sealed class RtWriteback
    {
        public Buffer Buffer;
        public DeviceMemory Memory;
        public nint Ptr;
        public ulong Size;
        public uint Address, Width, Rows, HostStride, GuestStride, GuestBpp;
        public WritebackConvert Convert;
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

    bool ScheduleWriteback(RenderTarget rt)
    {
        if (rt.Width * rt.Height > WritebackMaxPixels || rt.Address == 0) return false;
        int guestBpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
        int hostBpp = HostBytesPerPixel(rt.Format);
        var conv = WritebackConversion(rt.ColorFormat, rt.Format, hostBpp, guestBpp);
        if (conv == null)
        {
            Log.WarnOnce($"writeback-fmt-{rt.ColorFormat:X8}", $"[gpu] render target 0x{rt.Address:X8} ({rt.Format}) is not written back: no conversion to guest format 0x{rt.ColorFormat:X8}");
            return false;
        }
        uint width = Math.Min(rt.Width, rt.Stride);
        ulong size = (ulong)rt.Stride * (uint)hostBpp * rt.Height;
        if (!_writebackPool.TryGetValue(size, out var pool)) _writebackPool[size] = pool = new();
        var wb = pool.Count > 0 ? pool.Pop() : null;
        if (wb == null)
        {
            wb = new RtWriteback { Size = size };
            (wb.Buffer, wb.Memory, wb.Ptr) = _core.CreateHostBuffer(size, BufferUsageFlags.TransferDstBit);
        }
        wb.Address = rt.Address;
        wb.Width = width;
        wb.Rows = rt.Height;
        wb.HostStride = rt.Stride * (uint)hostBpp;
        wb.GuestStride = rt.Stride * (uint)guestBpp;
        wb.GuestBpp = (uint)guestBpp;
        wb.Convert = conv.Value;

        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = rt.Stride,
            BufferImageHeight = rt.Height,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(width, rt.Height, 1),
        };
        _vk.CmdCopyImageToBuffer(Cmd, rt.Image, ImageLayout.General, wb.Buffer, 1, in region);
        F.Writebacks.Add(wb);
        if (TraceSync && _tracedWritebacks.Add((rt.Address, rt.Width, rt.Height)))
            Log.Info($"[sync] writeback target 0x{rt.Address:X8} {rt.Width}x{rt.Height} stride {rt.Stride} fmt 0x{rt.ColorFormat:X8} ({rt.Format}, {conv}) present {_presentCount}");
        return true;
    }

    /// <summary>Runs after the frame's fence signalled: copies each target's rows into guest memory.</summary>
    void CompleteWritebacks(Frame f)
    {
        if (f.Writebacks.Count == 0) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var wb in f.Writebacks)
        {
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
            _wbBytes += wb.Rows * wb.GuestStride;
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

    void DestroyWritebacks()
    {
        foreach (var pool in _writebackPool.Values)
            foreach (var wb in pool) _core.DestroyBuffer(wb.Buffer, wb.Memory);
        _writebackPool.Clear();
    }
}
