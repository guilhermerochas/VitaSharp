using System.Buffers;
using System.Diagnostics.CodeAnalysis;
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
    private readonly bool _pooled;

    private Texels(byte[] array, int length, bool pooled)
    {
        Array = array;
        Length = length;
        _pooled = pooled;
    }

    public Span<byte> Span => Array.AsSpan(0, Length);

    /// <summary>A zero-filled pooled buffer of <paramref name="length"/> bytes (same contents as a new array).</summary>
    public static Texels Rent(long length)
    {
        int n = checked((int)length);
        byte[] a = ArrayPool<byte>.Shared.Rent(n);
        System.Array.Clear(a, 0, n);
        return new Texels(a, n, true);
    }

    /// <summary>Wraps a buffer that does not come from the pool (Dispose leaves it alone).</summary>
    public static Texels Wrap(byte[] array) => new(array, array.Length, false);

    /// <summary>An exact-size array copy (callers that keep the data).</summary>
    public byte[] ToArray() => Span.ToArray();

    public void Dispose()
    {
        if (_pooled)
        {
            ArrayPool<byte>.Shared.Return(Array);
        }
    }
}

/// <summary>Turns guest texture memory (any GXM layout) into tightly packed rows ready for vkCmdCopyBufferToImage.</summary>
public static class TextureDecoder
{
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static uint t_srcAddr;
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static byte[]? t_srcData;

    /// <summary>Until disposed, reads of [addr, addr + data.Length) on this thread come from <paramref name="data"/>
    /// instead of guest memory (an earlier decoder picture, Gpu/DecoderOutputs.cs).</summary>
    public static SourceScope UseSource(uint addr, byte[] data)
    {
        t_srcAddr = addr;
        t_srcData = data;
        return default;
    }

    public readonly struct SourceScope : IDisposable
    {
        public void Dispose() => t_srcData = null;
    }

    private static ReadOnlySpan<byte> Mem(uint addr, int length)
    {
        if (t_srcData is { } d && addr - t_srcAddr <= (uint)d.Length && (ulong)(addr - t_srcAddr) + (uint)length <= (uint)d.Length)
        {
            return d.AsSpan((int)(addr - t_srcAddr), length);
        }

        return GuestMemory.ReadSpan(addr, length);
    }

    /// <summary>
    /// A swizzled image (or block grid) inside a larger power-of-two swizzle, read as one span and reordered through
    /// <see cref="TextureLayout.MortonToLinear"/> instead of one read per element. False (nothing written) under
    /// VITASHARP_TEX_LAYOUT=legacy, or when an earlier decoder picture covers only part of the range (the per-element
    /// reads take each element from wherever it lies).
    /// </summary>
    private static bool MortonGather(Span<byte> dst, uint addr, uint w, uint h, uint pw, uint ph, int bytesPerElement)
    {
        if (TextureLayout.Legacy)
        {
            return false;
        }

        long bytes = TextureLayout.MortonSourceBytes(w, h, pw, ph, bytesPerElement);
        if (bytes > int.MaxValue)
        {
            return false;
        }

        if (t_srcData is { } d)
        {
            ulong lo = addr, hi = addr + (ulong)bytes, dLo = t_srcAddr, dHi = t_srcAddr + (ulong)d.Length;
            bool inside = lo >= dLo && hi <= dHi, apart = hi <= dLo || lo >= dHi;
            if (!inside && !apart)
            {
                return false;
            }
        }
        return TextureLayout.MortonToLinear(dst, Mem(addr, (int)bytes), w, h, pw, ph, bytesPerElement);
    }

    private static byte Mem8(uint addr) => t_srcData is { } d && addr - t_srcAddr < (uint)d.Length ? d[addr - t_srcAddr] : GuestMemory.Read<byte>(addr);

    /// <summary>Decodes one level. The result is pooled: dispose it once its bytes were copied. <paramref name="yuvProfiles"/>:
    /// the matrices of the scene's colour space converters, for a YUV format.</summary>
    public static Texels Decode(GxmTextureDesc gxmTextureDesc, TexFormatInfo info, uint addr, uint w, uint h, uint rowPitch,
        GxmYuvProfiles yuvProfiles, out uint outW, out uint outH)
    {
        outW = w;
        outH = h;
        uint type = gxmTextureDesc.TextureType;
        bool swizzled = type is GxmTextureDesc.TypeSwizzled or GxmTextureDesc.TypeCube or GxmTextureDesc.TypeSwizzledArbitrary or GxmTextureDesc.TypeCubeArbitrary;
        bool tiled = type == GxmTextureDesc.TypeTiled;

        if (info.Compressed)
        {
            if (info.Convert == TexConvert.Pvrtc)
            {
                TexBase bf = GxmFormats.BaseOf(gxmTextureDesc.Format);
                bool two = bf is TexBase.PVRT2BPP or TexBase.PVRTII2BPP, ii = bf is TexBase.PVRTII2BPP or TexBase.PVRTII4BPP;
                uint nx = BitOperations.RoundUpToPowerOf2(Math.Max((w + (two ? 7u : 3u)) / (two ? 8u : 4u), 2)), ny = BitOperations.RoundUpToPowerOf2(Math.Max((h + 3) / 4, 2));
                return Pvrtc.Decode(Mem(addr, (int)(nx * ny * 8)), w, h, two, ii);
            }
            if (info.Convert == TexConvert.AstcFromPvrtc)
            {
                // GPUs without BCn but with ASTC: PVRTC decoded, then encoded as ASTC 4x4 (AstcTranscoder)
                TexBase bf = GxmFormats.BaseOf(gxmTextureDesc.Format);
                bool two = bf is TexBase.PVRT2BPP or TexBase.PVRTII2BPP, ii = bf is TexBase.PVRTII2BPP or TexBase.PVRTII4BPP;
                uint nx = BitOperations.RoundUpToPowerOf2(Math.Max((w + (two ? 7u : 3u)) / (two ? 8u : 4u), 2)), ny = BitOperations.RoundUpToPowerOf2(Math.Max((h + 3) / 4, 2));
                return AstcTranscoder.FromPvrtc(bf, Mem(addr, (int)(nx * ny * 8)), w, h, two, ii);
            }
            uint bw = (w + 3) / 4, bh = (h + 3) / 4;
            int bb = info.BlockBytes;
            var outBuf = Texels.Rent(bw * bh * bb);
            Span<byte> o = outBuf.Span;
            if (!swizzled)
            {
                for (uint y = 0; y < bh; y++)
                {
                    Mem(addr + y * rowPitch, (int)(bw * bb)).CopyTo(o[(int)(y * bw * bb)..]);
                }
            }
            else
            {
                uint pw = BitOperations.RoundUpToPowerOf2(bw), ph = BitOperations.RoundUpToPowerOf2(bh);
                if (!MortonGather(o, addr, bw, bh, pw, ph, bb))
                {
                    for (uint y = 0; y < bh; y++)
                    {
                        for (uint x = 0; x < bw; x++)
                        {
                            uint idx = TextureLayout.Morton(x, y, pw, ph);
                            Mem(addr + idx * (uint)bb, bb).CopyTo(o.Slice((int)((y * bw + x) * bb), bb));
                        }
                    }
                }
            }
            if (info.Convert == TexConvert.Etc1)
            {
                using (outBuf)
                {
                    return Etc1.Decode(outBuf.Span, w, h);
                }
            }

            if (info.Convert == TexConvert.BcSoftware)
            {
                using (outBuf)
                {
                    return BcSoftware.Decode(GxmFormats.BaseOf(gxmTextureDesc.Format), outBuf.Span, w, h);
                }
            }
            if (info.Convert == TexConvert.AstcFromBc)
            {
                TexBase bf = GxmFormats.BaseOf(gxmTextureDesc.Format);
                using (outBuf)
                {
                    return AstcTranscoder.FromBc(bf, outBuf.Span, w, h, AstcTranscoder.ChannelsFor(bf, false) ?? AstcChannels.Rgba);
                }
            }
            return outBuf;
        }

        int bits = info.BitsPerPixel;
        switch (info.Convert)
        {
            case TexConvert.Yuv420P2:
            case TexConvert.Yuv420P3:
                return Yuv420(addr, w, h, rowPitch == 0 ? w : (uint)Math.Max(rowPitch, w), info.Convert == TexConvert.Yuv420P3,
                    (gxmTextureDesc.Swizzle & 1) != 0, yuvProfiles.ProfileOf(gxmTextureDesc.Format));
            case TexConvert.Yuv422:
                return Yuv422(addr, w, h, rowPitch, gxmTextureDesc.Swizzle & 3, yuvProfiles.ProfileOf(gxmTextureDesc.Format));
        }

        if (bits < 8)
        {
            // P4: 2 texels per byte
            using Texels raw = Gather(addr, w, h, rowPitch, 1, swizzled, tiled, halfBytes: true);
            return ExpandPalette(raw.Span, gxmTextureDesc.PaletteAddress, w * h, true);
        }

        int bpp = bits / 8;
        Texels linear = Gather(addr, w, h, rowPitch, bpp, swizzled, tiled, halfBytes: false);
        if (info.Convert is TexConvert.None)
        {
            if (IsU5U5U5U1(gxmTextureDesc.Format))
            {
                U5U5U5U1ToU1U5U5U5(linear.Span[..(int)(w * h * 2)]);
            }

            return linear;
        }
        using (linear)
        {
            Span<byte> src = linear.Span;
            return info.Convert switch
            {
                TexConvert.Palette8 => ExpandPalette(src, gxmTextureDesc.PaletteAddress, w * h, false),
                TexConvert.Rgb8ToRgba8 => Rgb8ToRgba8(src, w * h, 0xFF),
                TexConvert.Srgb8ToRgba8 => Rgb8ToRgba8(src, w * h, GxmFormats.BaseOf(gxmTextureDesc.Format) == TexBase.S8S8S8 ? (byte)0x7F : (byte)0xFF), // SNorm alpha = +1.0
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
    private static void U5U5U5U1ToU1U5U5U5(Span<byte> texels)
    {
        Span<ushort> px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(texels);
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = (ushort)((px[i] >> 1) | (px[i] << 15));
        }
    }

    private static Texels Unhandled(TexFormatInfo info, ReadOnlySpan<byte> data)
    {
        Log.WarnOnce("texconv" + info.Convert, $"[gpu] texture conversion {info.Convert} is not implemented: texels passed through raw");
        var copy = Texels.Rent(data.Length);
        data.CopyTo(copy.Span);
        return copy;
    }

    /// <summary>Reads texels into row-major order. For P4 (halfBytes) the result has one byte per texel.</summary>
    private static Texels Gather(uint addr, uint w, uint h, uint rowPitch, int bpp, bool swizzled, bool tiled, bool halfBytes)
    {
        var result = Texels.Rent((long)w * h * bpp);
        Span<byte> dst = result.Span;
        if (halfBytes)
        {
            for (uint y = 0; y < h; y++)
            {
                for (uint x = 0; x < w; x++)
                {
                    uint idx = swizzled ? TextureLayout.Morton(x, y, BitOperations.RoundUpToPowerOf2(w), BitOperations.RoundUpToPowerOf2(h))
                        : tiled ? TextureLayout.Tiled(x, y, w) : y * rowPitch * 2 + x;
                    byte b = Mem8(addr + idx / 2);
                    dst[(int)(y * w + x)] = (byte)((idx & 1) != 0 ? b >> 4 : b & 0xF);
                }
            }

            return result;
        }
        if (swizzled)
        {
            uint pw = BitOperations.RoundUpToPowerOf2(w), ph = BitOperations.RoundUpToPowerOf2(h);
            if (pw == w && ph == h)
            {
                TextureLayout.SwizzledToLinear(dst, Mem(addr, (int)(w * h * bpp)), w, h, bpp);
                return result;
            }
            if (MortonGather(dst, addr, w, h, pw, ph, bpp))
            {
                return result;
            }

            for (uint y = 0; y < h; y++)
            {
                for (uint x = 0; x < w; x++)
                {
                    Mem(addr + TextureLayout.Morton(x, y, pw, ph) * (uint)bpp, bpp).CopyTo(dst.Slice((int)((y * w + x) * bpp), bpp));
                }
            }

            return result;
        }
        if (tiled)
        {
            uint tw = (w + 31) & ~31u, th = (h + 31) & ~31u;
            TextureLayout.TiledToLinear(dst, Mem(addr, (int)(tw * th * bpp)), w, h, bpp);
            return result;
        }
        int row = (int)(w * bpp);
        for (uint y = 0; y < h; y++)
        {
            Mem(addr + y * rowPitch, row).CopyTo(dst.Slice((int)(y * row), row));
        }

        return result;
    }

    private static Texels ExpandPalette(ReadOnlySpan<byte> indices, uint palette, uint count, bool p4)
    {
        var result = Texels.Rent(count * 4L);
        if (palette == 0)
        {
            return result;
        }

        Span<byte> dst = result.Span;
        ReadOnlySpan<uint> pal = GuestMemory.ReadSpan<uint>(palette, p4 ? 16 : 256);
        for (int i = 0; i < count; i++)
        {
            BitConverter.TryWriteBytes(dst[(i * 4)..], pal[indices[i] & (p4 ? 0xF : 0xFF)]);
        }

        return result;
    }

    private static Texels Rgb8ToRgba8(ReadOnlySpan<byte> src, uint count, byte alpha)
    {
        var result = Texels.Rent(count * 4L);
        Span<byte> dst = result.Span;
        for (int i = 0; i < count; i++)
        {
            dst[i * 4] = src[i * 3];
            dst[i * 4 + 1] = src[i * 3 + 1];
            dst[i * 4 + 2] = src[i * 3 + 2];
            dst[i * 4 + 3] = alpha;
        }
        return result;
    }

    private static Texels U8U3U3U2(ReadOnlySpan<byte> src, uint count)
    {
        var result = Texels.Rent(count * 4L);
        Span<byte> dst = result.Span;
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
    private static Texels X8U24(ReadOnlySpan<byte> src, uint count)
    {
        var result = Texels.Rent(count * 4L);
        Span<byte> dst = result.Span;
        for (int i = 0; i < count; i++)
        {
            BitConverter.TryWriteBytes(dst[(i * 4)..], (BitConverter.ToUInt32(src[(i * 4)..]) & 0xFFFFFF) / 16777215f);
        }

        return result;
    }

    private static Texels S5S5U6(ReadOnlySpan<byte> src, uint count)
    {
        var result = Texels.Rent(count * 4L);
        Span<byte> dst = result.Span;
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

    private static ushort F10ToHalf(uint v)
    {
        // 5-bit exponent, 5-bit mantissa, no sign: same exponent bias as half
        uint e = (v >> 5) & 0x1F, m = v & 0x1F;
        return (ushort)((e << 10) | (m << 5));
    }

    private static Texels U2F10F10F10(ReadOnlySpan<byte> src, uint count)
    {
        var result = Texels.Rent(count * 8L);
        Span<byte> dst = result.Span;
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

    /// <summary>One texel through the conversion of <paramref name="tables"/> (<see cref="YuvTables"/>).</summary>
    private static void YuvToRgb(int y, int u, int v, Span<byte> o, YuvTables tables)
    {
        int c = tables.Luma[y];
        o[0] = ClampByte(c + tables.RFromV[v]);
        o[1] = ClampByte(c + tables.GFromU[u] + tables.GFromV[v]);
        o[2] = ClampByte(c + tables.BFromU[u]);
        o[3] = 255;
    }

    /// <summary>YUV420P2 / YUV420P3 to RGBA. <paramref name="yvu"/>: V before U (the YVU420 swizzles);
    /// <paramref name="yuvProfile"/>: the SceGxmYuvProfile of the texture's converter (<see cref="GxmYuvProfiles.ProfileOf"/>).</summary>
    public static Texels Yuv420(uint addr, uint w, uint h, uint pitch, bool threePlane, bool yvu, int yuvProfile)
    {
        return Yuv420Fast(addr, w, h, pitch, threePlane, yvu, yuvProfile) ?? Yuv420Texel(addr, w, h, pitch, threePlane, yvu, yuvProfile);
    }

    /// <summary>The per-channel terms of each SceGxmYuvProfile's conversion (<see cref="GxmYuv.Coefficients"/>) by
    /// sample value, the rounding half (128) in the luma term.</summary>
    private sealed class YuvTables
    {
        public readonly int[] Luma = new int[256], RFromV = new int[256], GFromU = new int[256], GFromV = new int[256], BFromU = new int[256];

        private YuvTables(int profile)
        {
            (int luma, int lumaOffset, int rFromV, int gFromU, int gFromV, int bFromU) = GxmYuv.Coefficients(profile);
            for (int value = 0; value < 256; value++)
            {
                Luma[value] = luma * (value - lumaOffset) + 128;
                RFromV[value] = rFromV * (value - 128);
                GFromU[value] = -gFromU * (value - 128);
                GFromV[value] = -gFromV * (value - 128);
                BFromU[value] = bFromU * (value - 128);
            }
        }

        private static readonly YuvTables[] s_profiles = [new(0), new(1), new(2), new(3)];

        public static YuvTables Of(int profile) => s_profiles[profile & 3];
    }

    private static byte ClampByte(int scaled) => (byte)Math.Clamp(scaled >> 8, 0, 255);

    /// <summary>
    /// <see cref="Yuv420Texel"/> by rows over one read of the planes, with the conversion's terms from tables: the same
    /// bytes. A video texture is converted every frame (F1 2011's 94 s intro movie, a 960x544 YUV420P2 texture per
    /// frame, cost the Android GPU worker ~38 ms per frame with a guest memory read per sample, and the movie showed 17
    /// pictures a second where the Vita shows ~25). Null when the planes cannot be read as one span (a decoder picture
    /// covering only part of them): the per-texel path reads each sample from wherever it lies.
    /// </summary>
    private static Texels? Yuv420Fast(uint addr, uint w, uint h, uint pitch, bool threePlane, bool yvu, int yuvProfile)
    {
        if (w == 0 || h == 0)
        {
            return null;
        }

        ulong ySize = (ulong)pitch * h;
        uint chromaPitch = threePlane ? pitch / 2 : pitch;
        ulong chromaPlane = (ulong)(pitch / 2) * (h / 2);
        // the last byte the per-texel path reads: the last chroma sample of the last row
        ulong lastChroma = threePlane
            ? ySize + chromaPlane + (ulong)((h - 1) / 2) * chromaPitch + (w - 1) / 2
            : ySize + (ulong)((h - 1) / 2) * pitch + ((w - 1) & ~1u) + 1;
        ulong length = Math.Max(lastChroma + 1, ySize);
        if (length > int.MaxValue || addr + length > 0x1_0000_0000UL)
        {
            return null;
        }

        if (t_srcData is { } d)
        {
            ulong lo = addr, hi = addr + length, dLo = t_srcAddr, dHi = t_srcAddr + (ulong)d.Length;
            if (!(lo >= dLo && hi <= dHi) && !(hi <= dLo || lo >= dHi))
            {
                return null;
            }
        }

        ReadOnlySpan<byte> src = Mem(addr, (int)length);
        var tables = YuvTables.Of(yuvProfile);
        int[] luma = tables.Luma, rFromV = tables.RFromV, gFromU = tables.GFromU, gFromV = tables.GFromV, bFromU = tables.BFromU;
        var result = Texels.Rent(w * h * 4L);
        Span<byte> dst = result.Span;
        for (uint y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> lumaRow = src.Slice((int)(y * pitch), (int)w);
            int chromaRow = (int)(ySize + (y / 2) * chromaPitch);
            int uBase = threePlane ? chromaRow + (yvu ? (int)chromaPlane : 0) : chromaRow + (yvu ? 1 : 0);
            int vBase = threePlane ? chromaRow + (yvu ? 0 : (int)chromaPlane) : chromaRow + (yvu ? 0 : 1);
            Span<byte> outRow = dst.Slice((int)(y * w * 4), (int)(w * 4));
            for (int x = 0; x < (int)w; x++)
            {
                int chroma = threePlane ? x / 2 : x & ~1;
                int u = src[uBase + chroma], v = src[vBase + chroma];
                int c = luma[lumaRow[x]];
                int o = x * 4;
                outRow[o] = ClampByte(c + rFromV[v]);
                outRow[o + 1] = ClampByte(c + gFromU[u] + gFromV[v]);
                outRow[o + 2] = ClampByte(c + bFromU[u]);
                outRow[o + 3] = 255;
            }
        }

        return result;
    }

    /// <summary>The per-texel conversion (one guest memory read per sample): the reference of <see cref="Yuv420Fast"/>,
    /// and the path for planes a decoder picture covers only in part.</summary>
    public static Texels Yuv420Texel(uint addr, uint w, uint h, uint pitch, bool threePlane, bool yvu, int yuvProfile)
    {
        var tables = YuvTables.Of(yuvProfile);
        var result = Texels.Rent(w * h * 4L);
        Span<byte> dst = result.Span;
        uint ySize = pitch * h;
        for (uint y = 0; y < h; y++)
        {
            for (uint x = 0; x < w; x++)
            {
                int Y = Mem8(addr + y * pitch + x);
                int U, V;
                if (threePlane)
                {
                    uint cp = pitch / 2, cs = cp * (h / 2);
                    U = Mem8(addr + ySize + (yvu ? cs : 0) + (y / 2) * cp + x / 2);
                    V = Mem8(addr + ySize + (yvu ? 0 : cs) + (y / 2) * cp + x / 2);
                }
                else
                {
                    uint c = addr + ySize + (y / 2) * pitch + (x & ~1u);
                    U = Mem8(c + (yvu ? 1u : 0u));
                    V = Mem8(c + (yvu ? 0u : 1u));
                }
                YuvToRgb(Y, U, V, dst.Slice((int)((y * w + x) * 4), 4), tables);
            }
        }

        return result;
    }

    /// <summary>YUV422 to RGBA: each 4-byte group holds two texels' luma and their shared U and V, in the order of
    /// <paramref name="order"/> (SceGxmTextureSwizzleYUV422Mode bits 0-1: 0 YUYV, 1 YVYU, 2 UYVY, 3 VYUY);
    /// <paramref name="yuvProfile"/>: the SceGxmYuvProfile of the texture's converter.</summary>
    public static Texels Yuv422(uint addr, uint w, uint h, uint pitch, uint order, int yuvProfile)
    {
        var tables = YuvTables.Of(yuvProfile);
        // byte of the group holding Y0, Y1, U and V
        bool lumaFirst = (order & 2) == 0, vFirst = (order & 1) != 0;
        uint y0At = lumaFirst ? 0u : 1u;
        uint chromaAt = lumaFirst ? 1u : 0u;
        uint uAt = vFirst ? chromaAt + 2 : chromaAt, vAt = vFirst ? chromaAt : chromaAt + 2;
        var result = Texels.Rent(w * h * 4L);
        Span<byte> dst = result.Span;
        if (pitch == 0)
        {
            pitch = w * 2;
        }

        for (uint y = 0; y < h; y++)
        {
            for (uint x = 0; x < w; x += 2)
            {
                uint p = addr + y * pitch + x * 2;
                int y0 = Mem8(p + y0At), y1 = Mem8(p + y0At + 2), u = Mem8(p + uAt), v = Mem8(p + vAt);
                YuvToRgb(y0, u, v, dst.Slice((int)((y * w + x) * 4), 4), tables);
                if (x + 1 < w)
                {
                    YuvToRgb(y1, u, v, dst.Slice((int)((y * w + x + 1) * 4), 4), tables);
                }
            }
        }

        return result;
    }
}

/// <summary>Minimal PNG writer (RGBA8) for frame dumps.</summary>
public static class Png
{
    private static readonly uint[] s_crc = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0xFFFFFFFF)
    {
        foreach (byte b in data)
        {
            crc = s_crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static void Chunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        stream.Write(len);
        Span<byte> t = stackalloc byte[4];
        System.Text.Encoding.ASCII.GetBytes(type, t);
        stream.Write(t);
        stream.Write(data);
        uint crc = Crc32(data, Crc32(t)) ^ 0xFFFFFFFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        stream.Write(len);
    }

    /// <summary>Compressed image data buffer reused across writes on the same thread (frame dumps write one PNG every few frames).</summary>
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static MemoryStream? t_idat;

    public static void Write(string path, ReadOnlySpan<byte> rgba, int w, int h)
    {
        using FileStream fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        ihdr.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        MemoryStream ms = t_idat ??= new MemoryStream();
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
