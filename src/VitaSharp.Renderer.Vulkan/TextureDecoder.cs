using System.Buffers;
using System.Numerics;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Decoded texels: an exact-length view of a buffer that is usually rented from <see cref="ArrayPool{T}.Shared"/>.
/// Dispose returns it to the pool, so the data must be copied out (upload ring, inspection copy) before that.
/// </summary>
public readonly struct Texels : IDisposable
{
    public readonly byte[] Array;
    public readonly int Length;
    readonly bool _pooled;

    Texels(byte[] array, int length, bool pooled) { Array = array; Length = length; _pooled = pooled; }

    public Span<byte> Span => Array.AsSpan(0, Length);

    /// <summary>A zero-filled pooled buffer of <paramref name="length"/> bytes (same contents as a new array).</summary>
    public static Texels Rent(long length)
    {
        int n = checked((int)length);
        var a = ArrayPool<byte>.Shared.Rent(n);
        System.Array.Clear(a, 0, n);
        return new Texels(a, n, true);
    }

    /// <summary>Wraps a buffer that does not come from the pool (Dispose leaves it alone).</summary>
    public static Texels Wrap(byte[] array) => new(array, array.Length, false);

    /// <summary>An exact-size array copy (callers that keep the data).</summary>
    public byte[] ToArray() => Span.ToArray();

    public void Dispose() { if (_pooled) ArrayPool<byte>.Shared.Return(Array); }
}

/// <summary>Turns guest texture memory (any GXM layout) into tightly packed rows ready for vkCmdCopyBufferToImage.</summary>
public static class TextureDecoder
{
    /// <summary>Decodes one level. The result is pooled: dispose it once its bytes were copied.</summary>
    public static Texels Decode(GxmTextureDesc d, TexFormatInfo info, uint addr, uint w, uint h, uint rowPitch, out uint outW, out uint outH)
    {
        outW = w;
        outH = h;
        uint type = d.TextureType;
        bool swizzled = type is GxmTextureDesc.TypeSwizzled or GxmTextureDesc.TypeCube or GxmTextureDesc.TypeSwizzledArbitrary or GxmTextureDesc.TypeCubeArbitrary;
        bool tiled = type == GxmTextureDesc.TypeTiled;

        if (info.Compressed)
        {
            if (info.Convert == TexConvert.Pvrtc)
            {
                var bf = GxmFormats.BaseOf(d.Format);
                bool two = bf is TexBase.PVRT2BPP or TexBase.PVRTII2BPP, ii = bf is TexBase.PVRTII2BPP or TexBase.PVRTII4BPP;
                uint nx = BitOperations.RoundUpToPowerOf2(Math.Max((w + (two ? 7u : 3u)) / (two ? 8u : 4u), 2)), ny = BitOperations.RoundUpToPowerOf2(Math.Max((h + 3) / 4, 2));
                return Pvrtc.Decode(GuestMemory.ReadSpan(addr, (int)(nx * ny * 8)), w, h, two, ii);
            }
            if (info.Convert == TexConvert.AstcFromPvrtc)
            {
                // GPUs without BCn but with ASTC: PVRTC decoded, then encoded as ASTC 4x4 (AstcTranscoder)
                var bf = GxmFormats.BaseOf(d.Format);
                bool two = bf is TexBase.PVRT2BPP or TexBase.PVRTII2BPP, ii = bf is TexBase.PVRTII2BPP or TexBase.PVRTII4BPP;
                uint nx = BitOperations.RoundUpToPowerOf2(Math.Max((w + (two ? 7u : 3u)) / (two ? 8u : 4u), 2)), ny = BitOperations.RoundUpToPowerOf2(Math.Max((h + 3) / 4, 2));
                return AstcTranscoder.FromPvrtc(bf, GuestMemory.ReadSpan(addr, (int)(nx * ny * 8)), w, h, two, ii);
            }
            uint bw = (w + 3) / 4, bh = (h + 3) / 4;
            int bb = info.BlockBytes;
            var outBuf = Texels.Rent(bw * bh * bb);
            var o = outBuf.Span;
            if (!swizzled)
            {
                for (uint y = 0; y < bh; y++)
                    GuestMemory.ReadSpan(addr + y * rowPitch, (int)(bw * bb)).CopyTo(o[(int)(y * bw * bb)..]);
            }
            else
            {
                uint pw = BitOperations.RoundUpToPowerOf2(bw), ph = BitOperations.RoundUpToPowerOf2(bh);
                for (uint y = 0; y < bh; y++)
                    for (uint x = 0; x < bw; x++)
                    {
                        uint idx = TextureLayout.Morton(x, y, pw, ph);
                        GuestMemory.ReadSpan(addr + idx * (uint)bb, bb).CopyTo(o.Slice((int)((y * bw + x) * bb), bb));
                    }
            }
            if (info.Convert == TexConvert.BcSoftware)
            {
                using (outBuf) return BcSoftware.Decode(GxmFormats.BaseOf(d.Format), outBuf.Span, w, h);
            }
            if (info.Convert == TexConvert.AstcFromBc)
            {
                var bf = GxmFormats.BaseOf(d.Format);
                using (outBuf) return AstcTranscoder.FromBc(bf, outBuf.Span, w, h, AstcTranscoder.ChannelsFor(bf, false) ?? AstcChannels.Rgba);
            }
            return outBuf;
        }

        int bits = info.BitsPerPixel;
        switch (info.Convert)
        {
            case TexConvert.Yuv420P2:
            case TexConvert.Yuv420P3:
                return Yuv420(addr, w, h, rowPitch == 0 ? w : (uint)Math.Max(rowPitch, w), info.Convert == TexConvert.Yuv420P3, d.Swizzle);
            case TexConvert.Yuv422:
                return Yuv422(addr, w, h, rowPitch);
        }

        if (bits < 8)
        {
            // P4: 2 texels per byte
            using var raw = Gather(addr, w, h, rowPitch, 1, swizzled, tiled, halfBytes: true);
            return ExpandPalette(raw.Span, d.PaletteAddress, w * h, true);
        }

        int bpp = bits / 8;
        var linear = Gather(addr, w, h, rowPitch, bpp, swizzled, tiled, halfBytes: false);
        if (info.Convert is TexConvert.None)
        {
            if (IsU5U5U5U1(d.Format)) U5U5U5U1ToU1U5U5U5(linear.Span[..(int)(w * h * 2)]);
            return linear;
        }
        using (linear)
        {
            var src = linear.Span;
            return info.Convert switch
            {
                TexConvert.Palette8 => ExpandPalette(src, d.PaletteAddress, w * h, false),
                TexConvert.Rgb8ToRgba8 => Rgb8ToRgba8(src, w * h, 0xFF),
                TexConvert.Srgb8ToRgba8 => Rgb8ToRgba8(src, w * h, GxmFormats.BaseOf(d.Format) == TexBase.S8S8S8 ? (byte)0x7F : (byte)0xFF), // SNorm alpha = +1.0
                TexConvert.U8U3U3U2 => U8U3U3U2(src, w * h),
                TexConvert.U2F10F10F10 => U2F10F10F10(src, w * h),
                TexConvert.S5S5U6 => S5S5U6(src, w * h),
                TexConvert.X8U24 => X8U24(src, w * h),
                _ => Unhandled(info, src),
            };
        }
    }

    /// <summary>U1U5U5U5 with a RGBA/BGRA/RGB1/BGR1 swizzle keeps its 1-bit field in bit 0 (U5U5U5U1).</summary>
    public static bool IsU5U5U5U1(uint format) =>
        GxmFormats.BaseOf(format) == TexBase.U1U5U5U5 && (GxmFormats.SwizzleOf(format) >> 12) is 2 or 3 or 6 or 7;

    /// <summary>Moves the 1-bit field from bit 0 to bit 15 so the texels fit A1R5G5B5 (the 5-bit fields keep their order).</summary>
    static void U5U5U5U1ToU1U5U5U5(Span<byte> texels)
    {
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(texels);
        for (int i = 0; i < px.Length; i++) px[i] = (ushort)((px[i] >> 1) | (px[i] << 15));
    }

    static Texels Unhandled(TexFormatInfo info, ReadOnlySpan<byte> data)
    {
        Log.WarnOnce("texconv" + info.Convert, $"[gpu] texture conversion {info.Convert} is not implemented: texels passed through raw");
        var copy = Texels.Rent(data.Length);
        data.CopyTo(copy.Span);
        return copy;
    }

    /// <summary>Reads texels into row-major order. For P4 (halfBytes) the result has one byte per texel.</summary>
    static Texels Gather(uint addr, uint w, uint h, uint rowPitch, int bpp, bool swizzled, bool tiled, bool halfBytes)
    {
        var result = Texels.Rent((long)w * h * bpp);
        var dst = result.Span;
        if (halfBytes)
        {
            for (uint y = 0; y < h; y++)
                for (uint x = 0; x < w; x++)
                {
                    uint idx = swizzled ? TextureLayout.Morton(x, y, BitOperations.RoundUpToPowerOf2(w), BitOperations.RoundUpToPowerOf2(h))
                        : tiled ? TextureLayout.Tiled(x, y, w) : y * rowPitch * 2 + x;
                    byte b = GuestMemory.Read<byte>(addr + idx / 2);
                    dst[(int)(y * w + x)] = (byte)((idx & 1) != 0 ? b >> 4 : b & 0xF);
                }
            return result;
        }
        if (swizzled)
        {
            uint pw = BitOperations.RoundUpToPowerOf2(w), ph = BitOperations.RoundUpToPowerOf2(h);
            if (pw == w && ph == h)
            {
                TextureLayout.SwizzledToLinear(dst, GuestMemory.ReadSpan(addr, (int)(w * h * bpp)), w, h, bpp);
                return result;
            }
            for (uint y = 0; y < h; y++)
                for (uint x = 0; x < w; x++)
                    GuestMemory.ReadSpan(addr + TextureLayout.Morton(x, y, pw, ph) * (uint)bpp, bpp).CopyTo(dst.Slice((int)((y * w + x) * bpp), bpp));
            return result;
        }
        if (tiled)
        {
            uint tw = (w + 31) & ~31u, th = (h + 31) & ~31u;
            TextureLayout.TiledToLinear(dst, GuestMemory.ReadSpan(addr, (int)(tw * th * bpp)), w, h, bpp);
            return result;
        }
        int row = (int)(w * bpp);
        for (uint y = 0; y < h; y++)
            GuestMemory.ReadSpan(addr + y * rowPitch, row).CopyTo(dst.Slice((int)(y * row), row));
        return result;
    }

    static Texels ExpandPalette(ReadOnlySpan<byte> indices, uint palette, uint count, bool p4)
    {
        var result = Texels.Rent(count * 4L);
        if (palette == 0) return result;
        var dst = result.Span;
        var pal = GuestMemory.ReadSpan<uint>(palette, p4 ? 16 : 256);
        for (int i = 0; i < count; i++) BitConverter.TryWriteBytes(dst[(i * 4)..], pal[indices[i] & (p4 ? 0xF : 0xFF)]);
        return result;
    }

    static Texels Rgb8ToRgba8(ReadOnlySpan<byte> src, uint count, byte alpha)
    {
        var result = Texels.Rent(count * 4L);
        var dst = result.Span;
        for (int i = 0; i < count; i++)
        {
            dst[i * 4] = src[i * 3];
            dst[i * 4 + 1] = src[i * 3 + 1];
            dst[i * 4 + 2] = src[i * 3 + 2];
            dst[i * 4 + 3] = alpha;
        }
        return result;
    }

    static Texels U8U3U3U2(ReadOnlySpan<byte> src, uint count)
    {
        var result = Texels.Rent(count * 4L);
        var dst = result.Span;
        for (int i = 0; i < count; i++)
        {
            ushort v = BitConverter.ToUInt16(src[(i * 2)..]);
            int b = v & 3, g = (v >> 2) & 7, r = (v >> 5) & 7, a = v >> 8;
            dst[i * 4] = (byte)(r * 255 / 7);
            dst[i * 4 + 1] = (byte)(g * 255 / 7);
            dst[i * 4 + 2] = (byte)(b * 255 / 3);
            dst[i * 4 + 3] = (byte)a;
        }
        return result;
    }

    /// <summary>X8U24 depth: low 24 bits, normalised to 0..1 (the fetch result on the Vita).</summary>
    static Texels X8U24(ReadOnlySpan<byte> src, uint count)
    {
        var result = Texels.Rent(count * 4L);
        var dst = result.Span;
        for (int i = 0; i < count; i++)
            BitConverter.TryWriteBytes(dst[(i * 4)..], (BitConverter.ToUInt32(src[(i * 4)..]) & 0xFFFFFF) / 16777215f);
        return result;
    }

    static Texels S5S5U6(ReadOnlySpan<byte> src, uint count)
    {
        var result = Texels.Rent(count * 4L);
        var dst = result.Span;
        for (int i = 0; i < count; i++)
        {
            ushort v = BitConverter.ToUInt16(src[(i * 2)..]);
            int r = (v & 0x1F), g = (v >> 5) & 0x1F, b = v >> 10;
            dst[i * 4] = (byte)(sbyte)((r << 27) >> 27 << 3);
            dst[i * 4 + 1] = (byte)(sbyte)((g << 27) >> 27 << 3);
            dst[i * 4 + 2] = (byte)(b * 127 / 63);
            dst[i * 4 + 3] = 127;
        }
        return result;
    }

    static ushort F10ToHalf(uint v)
    {
        // 5-bit exponent, 5-bit mantissa, no sign: same exponent bias as half
        uint e = (v >> 5) & 0x1F, m = v & 0x1F;
        return (ushort)((e << 10) | (m << 5));
    }

    static Texels U2F10F10F10(ReadOnlySpan<byte> src, uint count)
    {
        var result = Texels.Rent(count * 8L);
        var dst = result.Span;
        for (int i = 0; i < count; i++)
        {
            uint v = BitConverter.ToUInt32(src[(i * 4)..]);
            BitConverter.TryWriteBytes(dst[(i * 8)..], F10ToHalf(v & 0x3FF));
            BitConverter.TryWriteBytes(dst[(i * 8 + 2)..], F10ToHalf((v >> 10) & 0x3FF));
            BitConverter.TryWriteBytes(dst[(i * 8 + 4)..], F10ToHalf((v >> 20) & 0x3FF));
            BitConverter.TryWriteBytes(dst[(i * 8 + 6)..], BitConverter.HalfToUInt16Bits((Half)((v >> 30) / 3f)));
        }
        return result;
    }

    /// <summary>BT.601: CSC0 is studio range (Y 16..235), CSC1 full range (the hardware's two colour-space modes).</summary>
    static void YuvToRgb(int y, int u, int v, Span<byte> o, bool fullRange = false)
    {
        int d = u - 128, e = v - 128;
        if (fullRange)
        {
            o[0] = (byte)Math.Clamp((256 * y + 359 * e + 128) >> 8, 0, 255);
            o[1] = (byte)Math.Clamp((256 * y - 88 * d - 183 * e + 128) >> 8, 0, 255);
            o[2] = (byte)Math.Clamp((256 * y + 454 * d + 128) >> 8, 0, 255);
        }
        else
        {
            int c = y - 16;
            o[0] = (byte)Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
            o[1] = (byte)Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
            o[2] = (byte)Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);
        }
        o[3] = 255;
    }

    /// <summary>swizzle: bit 0 = YVU order (V before U), bit 1 = CSC1 (full range).</summary>
    static Texels Yuv420(uint addr, uint w, uint h, uint pitch, bool threePlane, uint swizzle)
    {
        bool yvu = (swizzle & 1) != 0, full = (swizzle & 2) != 0;
        var result = Texels.Rent(w * h * 4L);
        var dst = result.Span;
        uint ySize = pitch * h;
        for (uint y = 0; y < h; y++)
            for (uint x = 0; x < w; x++)
            {
                int Y = GuestMemory.Read<byte>(addr + y * pitch + x);
                int U, V;
                if (threePlane)
                {
                    uint cp = pitch / 2, cs = cp * (h / 2);
                    U = GuestMemory.Read<byte>(addr + ySize + (yvu ? cs : 0) + (y / 2) * cp + x / 2);
                    V = GuestMemory.Read<byte>(addr + ySize + (yvu ? 0 : cs) + (y / 2) * cp + x / 2);
                }
                else
                {
                    uint c = addr + ySize + (y / 2) * pitch + (x & ~1u);
                    U = GuestMemory.Read<byte>(c + (yvu ? 1u : 0u));
                    V = GuestMemory.Read<byte>(c + (yvu ? 0u : 1u));
                }
                YuvToRgb(Y, U, V, dst.Slice((int)((y * w + x) * 4), 4), full);
            }
        return result;
    }

    static Texels Yuv422(uint addr, uint w, uint h, uint pitch)
    {
        var result = Texels.Rent(w * h * 4L);
        var dst = result.Span;
        if (pitch == 0) pitch = w * 2;
        for (uint y = 0; y < h; y++)
            for (uint x = 0; x < w; x += 2)
            {
                uint p = addr + y * pitch + x * 2;
                int y0 = GuestMemory.Read<byte>(p), u = GuestMemory.Read<byte>(p + 1), y1 = GuestMemory.Read<byte>(p + 2), v = GuestMemory.Read<byte>(p + 3);
                YuvToRgb(y0, u, v, dst.Slice((int)((y * w + x) * 4), 4));
                if (x + 1 < w) YuvToRgb(y1, u, v, dst.Slice((int)((y * w + x + 1) * 4), 4));
            }
        return result;
    }
}

/// <summary>Minimal PNG writer (RGBA8) for frame dumps.</summary>
public static class Png
{
    static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0xFFFFFFFF)
    {
        foreach (var b in data) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        Span<byte> t = stackalloc byte[4];
        System.Text.Encoding.ASCII.GetBytes(type, t);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(data, Crc32(t)) ^ 0xFFFFFFFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    /// <summary>Compressed image data buffer reused across writes on the same thread (frame dumps write one PNG every few frames).</summary>
    [ThreadStatic] static MemoryStream? _idat;

    public static void Write(string path, ReadOnlySpan<byte> rgba, int w, int h)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        ihdr.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        var ms = _idat ??= new MemoryStream();
        ms.SetLength(0);
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0);
                z.Write(rgba.Slice(y * w * 4, w * 4));
            }
        }
        Chunk(fs, "IDAT", ms.GetBuffer().AsSpan(0, (int)ms.Length));
        Chunk(fs, "IEND", []);
    }
}
