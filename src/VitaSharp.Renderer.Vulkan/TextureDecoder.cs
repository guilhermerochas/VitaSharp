using System.Numerics;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>Turns guest texture memory (any GXM layout) into tightly packed rows ready for vkCmdCopyBufferToImage.</summary>
public static class TextureDecoder
{
    public static byte[] Decode(GxmTextureDesc d, TexFormatInfo info, uint addr, uint w, uint h, uint rowPitch, out uint outW, out uint outH)
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
                return Pvrtc.Decode(GuestMemory.Span(addr, (int)(nx * ny * 8)), w, h, two, ii);
            }
            uint bw = (w + 3) / 4, bh = (h + 3) / 4;
            int bb = info.BlockBytes;
            var outBuf = new byte[bw * bh * bb];
            if (!swizzled)
            {
                for (uint y = 0; y < bh; y++)
                    GuestMemory.Span(addr + y * rowPitch, (int)(bw * bb)).CopyTo(outBuf.AsSpan((int)(y * bw * bb)));
            }
            else
            {
                uint pw = BitOperations.RoundUpToPowerOf2(bw), ph = BitOperations.RoundUpToPowerOf2(bh);
                for (uint y = 0; y < bh; y++)
                    for (uint x = 0; x < bw; x++)
                    {
                        uint idx = TextureLayout.Morton(x, y, pw, ph);
                        GuestMemory.Span(addr + idx * (uint)bb, bb).CopyTo(outBuf.AsSpan((int)((y * bw + x) * bb), bb));
                    }
            }
            if (info.Convert == TexConvert.BcSoftware) return BcDecode.Decode(GxmFormats.BaseOf(d.Format), outBuf, w, h);
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
            var raw = Gather(addr, w, h, rowPitch, 1, swizzled, tiled, halfBytes: true);
            return ExpandPalette(raw, d.PaletteAddress, w * h, true);
        }

        int bpp = bits / 8;
        var linear = Gather(addr, w, h, rowPitch, bpp, swizzled, tiled, halfBytes: false);
        return info.Convert switch
        {
            TexConvert.None => linear,
            TexConvert.Palette8 => ExpandPalette(linear, d.PaletteAddress, w * h, false),
            TexConvert.Rgb8ToRgba8 => Rgb8ToRgba8(linear, w * h, 0xFF),
            TexConvert.Srgb8ToRgba8 => Rgb8ToRgba8(linear, w * h, GxmFormats.BaseOf(d.Format) == TexBase.S8S8S8 ? (byte)0x7F : (byte)0xFF), // SNorm alpha = +1.0
            TexConvert.U8U3U3U2 => U8U3U3U2(linear, w * h),
            TexConvert.U2F10F10F10 => U2F10F10F10(linear, w * h),
            TexConvert.S5S5U6 => S5S5U6(linear, w * h),
            TexConvert.X8U24 => X8U24(linear, w * h),
            _ => Unhandled(info, linear),
        };
    }

    static byte[] Unhandled(TexFormatInfo info, byte[] data)
    {
        Log.WarnOnce("texconv" + info.Convert, $"[gpu] texture conversion {info.Convert} is not implemented: texels passed through raw");
        return data;
    }

    static byte[] Solid(uint w, uint h, uint rgba)
    {
        var b = new byte[w * h * 4];
        for (int i = 0; i < b.Length; i += 4) BitConverter.TryWriteBytes(b.AsSpan(i), rgba);
        return b;
    }

    /// <summary>Reads texels into row-major order. For P4 (halfBytes) the result has one byte per texel.</summary>
    static byte[] Gather(uint addr, uint w, uint h, uint rowPitch, int bpp, bool swizzled, bool tiled, bool halfBytes)
    {
        var dst = new byte[w * h * bpp];
        if (halfBytes)
        {
            for (uint y = 0; y < h; y++)
                for (uint x = 0; x < w; x++)
                {
                    uint idx = swizzled ? TextureLayout.Morton(x, y, BitOperations.RoundUpToPowerOf2(w), BitOperations.RoundUpToPowerOf2(h))
                        : tiled ? TextureLayout.Tiled(x, y, w) : y * rowPitch * 2 + x;
                    byte b = GuestMemory.Read<byte>(addr + idx / 2);
                    dst[y * w + x] = (byte)((idx & 1) != 0 ? b >> 4 : b & 0xF);
                }
            return dst;
        }
        if (swizzled)
        {
            uint pw = BitOperations.RoundUpToPowerOf2(w), ph = BitOperations.RoundUpToPowerOf2(h);
            if (pw == w && ph == h)
            {
                TextureLayout.SwizzledToLinear(dst, GuestMemory.Span(addr, (int)(w * h * bpp)), w, h, bpp);
                return dst;
            }
            for (uint y = 0; y < h; y++)
                for (uint x = 0; x < w; x++)
                    GuestMemory.Span(addr + TextureLayout.Morton(x, y, pw, ph) * (uint)bpp, bpp).CopyTo(dst.AsSpan((int)((y * w + x) * bpp), bpp));
            return dst;
        }
        if (tiled)
        {
            uint tw = (w + 31) & ~31u, th = (h + 31) & ~31u;
            TextureLayout.TiledToLinear(dst, GuestMemory.Span(addr, (int)(tw * th * bpp)), w, h, bpp);
            return dst;
        }
        int row = (int)(w * bpp);
        for (uint y = 0; y < h; y++)
            GuestMemory.Span(addr + y * rowPitch, row).CopyTo(dst.AsSpan((int)(y * row), row));
        return dst;
    }

    static byte[] ExpandPalette(byte[] indices, uint palette, uint count, bool p4)
    {
        var dst = new byte[count * 4];
        if (palette == 0) return dst;
        var pal = GuestMemory.Span<uint>(palette, p4 ? 16 : 256);
        for (int i = 0; i < count; i++) BitConverter.TryWriteBytes(dst.AsSpan(i * 4), pal[indices[i] & (p4 ? 0xF : 0xFF)]);
        return dst;
    }

    static byte[] Rgb8ToRgba8(byte[] src, uint count, byte alpha)
    {
        var dst = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            dst[i * 4] = src[i * 3];
            dst[i * 4 + 1] = src[i * 3 + 1];
            dst[i * 4 + 2] = src[i * 3 + 2];
            dst[i * 4 + 3] = alpha;
        }
        return dst;
    }

    static byte[] U8U3U3U2(byte[] src, uint count)
    {
        var dst = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            ushort v = BitConverter.ToUInt16(src, i * 2);
            int b = v & 3, g = (v >> 2) & 7, r = (v >> 5) & 7, a = v >> 8;
            dst[i * 4] = (byte)(r * 255 / 7);
            dst[i * 4 + 1] = (byte)(g * 255 / 7);
            dst[i * 4 + 2] = (byte)(b * 255 / 3);
            dst[i * 4 + 3] = (byte)a;
        }
        return dst;
    }

    /// <summary>X8U24 depth: low 24 bits, normalised to 0..1 (the fetch result on the Vita).</summary>
    static byte[] X8U24(byte[] src, uint count)
    {
        var dst = new byte[count * 4];
        for (int i = 0; i < count; i++)
            BitConverter.TryWriteBytes(dst.AsSpan(i * 4), (BitConverter.ToUInt32(src, i * 4) & 0xFFFFFF) / 16777215f);
        return dst;
    }

    static byte[] S5S5U6(byte[] src, uint count)
    {
        var dst = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            ushort v = BitConverter.ToUInt16(src, i * 2);
            int r = (v & 0x1F), g = (v >> 5) & 0x1F, b = v >> 10;
            dst[i * 4] = (byte)(sbyte)((r << 27) >> 27 << 3);
            dst[i * 4 + 1] = (byte)(sbyte)((g << 27) >> 27 << 3);
            dst[i * 4 + 2] = (byte)(b * 127 / 63);
            dst[i * 4 + 3] = 127;
        }
        return dst;
    }

    static ushort F10ToHalf(uint v)
    {
        // 5-bit exponent, 5-bit mantissa, no sign: same exponent bias as half
        uint e = (v >> 5) & 0x1F, m = v & 0x1F;
        return (ushort)((e << 10) | (m << 5));
    }

    static byte[] U2F10F10F10(byte[] src, uint count)
    {
        var dst = new byte[count * 8];
        for (int i = 0; i < count; i++)
        {
            uint v = BitConverter.ToUInt32(src, i * 4);
            BitConverter.TryWriteBytes(dst.AsSpan(i * 8), F10ToHalf(v & 0x3FF));
            BitConverter.TryWriteBytes(dst.AsSpan(i * 8 + 2), F10ToHalf((v >> 10) & 0x3FF));
            BitConverter.TryWriteBytes(dst.AsSpan(i * 8 + 4), F10ToHalf((v >> 20) & 0x3FF));
            BitConverter.TryWriteBytes(dst.AsSpan(i * 8 + 6), BitConverter.HalfToUInt16Bits((Half)((v >> 30) / 3f)));
        }
        return dst;
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
    static byte[] Yuv420(uint addr, uint w, uint h, uint pitch, bool threePlane, uint swizzle)
    {
        bool yvu = (swizzle & 1) != 0, full = (swizzle & 2) != 0;
        var dst = new byte[w * h * 4];
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
                YuvToRgb(Y, U, V, dst.AsSpan((int)((y * w + x) * 4), 4), full);
            }
        return dst;
    }

    static byte[] Yuv422(uint addr, uint w, uint h, uint pitch)
    {
        var dst = new byte[w * h * 4];
        if (pitch == 0) pitch = w * 2;
        for (uint y = 0; y < h; y++)
            for (uint x = 0; x < w; x += 2)
            {
                uint p = addr + y * pitch + x * 2;
                int y0 = GuestMemory.Read<byte>(p), u = GuestMemory.Read<byte>(p + 1), y1 = GuestMemory.Read<byte>(p + 2), v = GuestMemory.Read<byte>(p + 3);
                YuvToRgb(y0, u, v, dst.AsSpan((int)((y * w + x) * 4), 4));
                if (x + 1 < w) YuvToRgb(y1, u, v, dst.AsSpan((int)((y * w + x + 1) * 4), 4));
            }
        return dst;
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
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(data, Crc32(t)) ^ 0xFFFFFFFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    public static void Write(string path, byte[] rgba, int w, int h)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0);
                z.Write(rgba, y * w * 4, w * 4);
            }
        }
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", []);
    }
}
