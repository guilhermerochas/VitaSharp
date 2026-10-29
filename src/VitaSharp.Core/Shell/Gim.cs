using System.Buffers.Binary;

namespace VitaSharp.Core.Shell;

/// <summary>A decoded image: straight (not premultiplied) RGBA8888, rows top to bottom.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Decoder for GIM images ("MIG.00.1PSP"), the texture format of the system RCOs (texture/gim). A GIM is a tree of
/// blocks {u16 type, u16 0, u32 size with children, u32 next, u32 data offset}: 2 = file, 3 = picture, 4 = image,
/// 5 = palette, 0xFF = info. Image and palette blocks carry a header {header size, reference, format, pixel order,
/// width, height, bpp, pitch align, height align, ..., pixels start, pixels end} followed by the pixels. Formats:
/// 0 RGBA5650, 1 RGBA5551, 2 RGBA4444, 3 RGBA8888, 4/5/6/7 indexed 4/8/16/32-bit (with a palette block), 8/9/10
/// DXT1/3/5. Pixel order 1 is the PSP "faster" layout: 16-byte x 8-row tiles.
/// </summary>
public static class Gim
{
    public static bool IsGim(ReadOnlySpan<byte> d) => d.Length >= 16 && d[0] == 'M' && d[1] == 'I' && d[2] == 'G' && d[3] == '.';

    sealed class Section
    {
        public int Format, Order, Width, Height, Bpp, PitchAlign, HeightAlign;
        public int PixelOffset, PixelEnd;
    }

    public static RgbaImage Decode(byte[] data)
    {
        if (!IsGim(data)) throw new InvalidDataException("not a GIM image");
        Section? image = null, palette = null;
        Walk(data, 16, data.Length, ref image, ref palette, 0);
        if (image == null) throw new InvalidDataException("GIM has no image block");
        return DecodeImage(data, image, palette);
    }

    static void Walk(byte[] d, int start, int end, ref Section? image, ref Section? palette, int depth)
    {
        if (depth > 8) return;
        int p = start;
        while (p + 16 <= end)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p));
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
            int dataOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 12));
            if (size < 16 || p + size > end) size = end - p;
            switch (type)
            {
                case 2 or 3:
                    Walk(d, p + Math.Max(dataOff, 16), p + size, ref image, ref palette, depth + 1);
                    break;
                case 4 when image == null:
                    image = ReadSection(d, p + dataOff, p + size);
                    break;
                case 5 when palette == null:
                    palette = ReadSection(d, p + dataOff, p + size);
                    break;
            }
            p += size;
        }
    }

    static Section ReadSection(byte[] d, int h, int blockEnd)
    {
        if (h + 0x30 > d.Length) throw new InvalidDataException("GIM section header lies outside the file");
        int U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(h + o));
        int I32(int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(h + o));
        var s = new Section
        {
            Format = U16(4), Order = U16(6), Width = U16(8), Height = U16(10), Bpp = U16(12),
            PitchAlign = Math.Max(U16(14), 1), HeightAlign = Math.Max(U16(16), 1),
            PixelOffset = h + I32(0x1C), PixelEnd = h + I32(0x20),
        };
        if (s.PixelOffset < h || s.PixelEnd > blockEnd || s.PixelEnd > d.Length || s.PixelEnd < s.PixelOffset)
            throw new InvalidDataException("GIM pixels lie outside their block");
        return s;
    }

    static int BitsOf(int format) => format switch
    {
        0 or 1 or 2 => 16,
        3 => 32,
        4 => 4,
        5 => 8,
        6 => 16,
        7 => 32,
        8 => 4,
        9 or 10 => 8,
        _ => throw new InvalidDataException($"GIM pixel format {format} is not supported"),
    };

    static RgbaImage DecodeImage(byte[] d, Section s, Section? pal)
    {
        int w = s.Width, h = s.Height;
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) throw new InvalidDataException($"GIM size {w}x{h} is not valid");
        var outPx = new byte[w * h * 4];
        if (s.Format is 8 or 9 or 10)
        {
            DecodeDxt(d.AsSpan(s.PixelOffset, s.PixelEnd - s.PixelOffset), s.Format, w, h, outPx);
            return new RgbaImage(w, h, outPx);
        }
        int bpp = BitsOf(s.Format);
        int pitch = Align((w * bpp + 7) / 8, s.PitchAlign);
        int rows = Align(h, s.HeightAlign);
        var src = d.AsSpan(s.PixelOffset, s.PixelEnd - s.PixelOffset);
        byte[] lin = new byte[pitch * rows];
        if (s.Order == 1) Unswizzle(src, lin, pitch, rows);
        else src[..Math.Min(src.Length, lin.Length)].CopyTo(lin);

        uint[]? palette = null;
        if (s.Format is >= 4 and <= 7)
        {
            if (pal == null) throw new InvalidDataException("indexed GIM without a palette");
            int n = pal.Width * Math.Max(pal.Height, 1);
            palette = new uint[n];
            var ps = d.AsSpan(pal.PixelOffset, pal.PixelEnd - pal.PixelOffset);
            int pb = BitsOf(pal.Format) / 8;
            for (int i = 0; i < n && (i + 1) * pb <= ps.Length; i++) palette[i] = Color(pal.Format, ps[(i * pb)..]);
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int row = y * pitch;
                uint c;
                switch (s.Format)
                {
                    case 4:
                    {
                        int b = lin[row + x / 2];
                        int idx = (x & 1) == 0 ? b & 0xF : b >> 4;
                        c = idx < palette!.Length ? palette[idx] : 0;
                        break;
                    }
                    case 5: c = Index(palette!, lin[row + x]); break;
                    case 6: c = Index(palette!, BinaryPrimitives.ReadUInt16LittleEndian(lin.AsSpan(row + x * 2))); break;
                    case 7: c = Index(palette!, (int)BinaryPrimitives.ReadUInt32LittleEndian(lin.AsSpan(row + x * 4))); break;
                    default: c = Color(s.Format, lin.AsSpan(row + x * (bpp / 8))); break;
                }
                int o = (y * w + x) * 4;
                outPx[o] = (byte)c; outPx[o + 1] = (byte)(c >> 8); outPx[o + 2] = (byte)(c >> 16); outPx[o + 3] = (byte)(c >> 24);
            }
        }
        return new RgbaImage(w, h, outPx);
    }

    static uint Index(uint[] palette, int i) => (uint)i < (uint)palette.Length ? palette[i] : 0;

    static int Align(int v, int a) => (v + a - 1) / a * a;

    /// <summary>PSP/Vita "faster" pixel order: the image is stored as 16-byte wide, 8-row tall tiles, row major.</summary>
    static void Unswizzle(ReadOnlySpan<byte> src, byte[] dst, int pitch, int rows)
    {
        int tilesPerRow = Math.Max(pitch / 16, 1);
        for (int y = 0; y < rows; y++)
            for (int xb = 0; xb < pitch; xb++)
            {
                int tile = (y / 8) * tilesPerRow + xb / 16;
                int s = tile * 128 + (y % 8) * 16 + xb % 16;
                if (s < src.Length) dst[y * pitch + xb] = src[s];
            }
    }

    /// <summary>A direct-colour pixel as 0xAABBGGRR.</summary>
    static uint Color(int format, ReadOnlySpan<byte> p)
    {
        switch (format)
        {
            case 0:
            {
                int v = p[0] | p[1] << 8;
                return Pack(Expand(v & 31, 5), Expand((v >> 5) & 63, 6), Expand((v >> 11) & 31, 5), 255);
            }
            case 1:
            {
                int v = p[0] | p[1] << 8;
                return Pack(Expand(v & 31, 5), Expand((v >> 5) & 31, 5), Expand((v >> 10) & 31, 5), (v & 0x8000) != 0 ? 255 : 0);
            }
            case 2:
            {
                int v = p[0] | p[1] << 8;
                return Pack((v & 15) * 17, ((v >> 4) & 15) * 17, ((v >> 8) & 15) * 17, ((v >> 12) & 15) * 17);
            }
            default:
                return (uint)(p[0] | p[1] << 8 | p[2] << 16 | p[3] << 24);
        }
    }

    static int Expand(int v, int bits) => (v << (8 - bits)) | (v >> (2 * bits - 8));
    static uint Pack(int r, int g, int b, int a) => (uint)(r | g << 8 | b << 16 | a << 24);

    /// <summary>DXT1/3/5 blocks in raster block order. The PSP family stores each block's colour part as
    /// {u32 indices, u16 c0, u16 c1} (indices first) and DXT3/5 alpha after the colour part.</summary>
    static void DecodeDxt(ReadOnlySpan<byte> src, int format, int w, int h, byte[] dst)
    {
        int blockBytes = format == 8 ? 8 : 16;
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<uint> colors = stackalloc uint[4];
        Span<int> alpha = stackalloc int[16];
        Span<int> lut = stackalloc int[8];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int o = (by * bw + bx) * blockBytes;
                if (o + blockBytes > src.Length) return;
                var blk = src.Slice(o, blockBytes);
                var col = blk[..8];
                uint idx = BinaryPrimitives.ReadUInt32LittleEndian(col);
                int c0 = BinaryPrimitives.ReadUInt16LittleEndian(col[4..]), c1 = BinaryPrimitives.ReadUInt16LittleEndian(col[6..]);
                colors[0] = Color(0, col[4..]); colors[1] = Color(0, col[6..]);
                Lerp(colors, c0 > c1 || format != 8);
                alpha.Fill(255);
                if (format == 9)
                {
                    ulong a = BinaryPrimitives.ReadUInt64LittleEndian(blk[8..]);
                    for (int i = 0; i < 16; i++) alpha[i] = (int)((a >> (i * 4)) & 15) * 17;
                }
                else if (format == 10)
                {
                    // {u16 alpha indices low.., a0, a1} in the PSP order: 6 index bytes then a0, a1
                    var ab = blk[8..];
                    int a0 = ab[6], a1 = ab[7];
                    ulong bits = 0;
                    for (int i = 0; i < 6; i++) bits |= (ulong)ab[i] << (8 * i);
                    lut[0] = a0; lut[1] = a1;
                    if (a0 > a1) for (int i = 1; i < 7; i++) lut[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                    else { for (int i = 1; i < 5; i++) lut[i + 1] = ((5 - i) * a0 + i * a1) / 5; lut[6] = 0; lut[7] = 255; }
                    for (int i = 0; i < 16; i++) alpha[i] = lut[(int)((bits >> (i * 3)) & 7)];
                }
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        int i = py * 4 + px;
                        uint c = colors[(int)((idx >> (i * 2)) & 3)];
                        int a = format == 8 ? (int)(c >> 24) : alpha[i];
                        int d = (y * w + x) * 4;
                        dst[d] = (byte)c; dst[d + 1] = (byte)(c >> 8); dst[d + 2] = (byte)(c >> 16); dst[d + 3] = (byte)a;
                    }
            }
    }

    static void Lerp(Span<uint> c, bool fourColor)
    {
        int Ch(uint v, int s) => (int)((v >> s) & 255);
        if (fourColor)
        {
            c[2] = Pack((2 * Ch(c[0], 0) + Ch(c[1], 0)) / 3, (2 * Ch(c[0], 8) + Ch(c[1], 8)) / 3, (2 * Ch(c[0], 16) + Ch(c[1], 16)) / 3, 255);
            c[3] = Pack((Ch(c[0], 0) + 2 * Ch(c[1], 0)) / 3, (Ch(c[0], 8) + 2 * Ch(c[1], 8)) / 3, (Ch(c[0], 16) + 2 * Ch(c[1], 16)) / 3, 255);
        }
        else
        {
            c[2] = Pack((Ch(c[0], 0) + Ch(c[1], 0)) / 2, (Ch(c[0], 8) + Ch(c[1], 8)) / 2, (Ch(c[0], 16) + Ch(c[1], 16)) / 2, 255);
            c[3] = 0;
        }
    }
}
