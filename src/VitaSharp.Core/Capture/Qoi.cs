namespace VitaSharp.Core.Capture;

/// <summary>
/// QOI ("Quite OK Image", qoiformat.org, RGB variant): a lossless image codec that encodes ~100-300 MB/s with a few
/// lines of code. The session capture stores every presented frame with it (480x272 game frames are 60-250 KB),
/// cheap enough to run on a background thread at 60 fps. Standard files: any QOI viewer opens them.
/// </summary>
public static class Qoi
{
    const byte OpIndex = 0x00, OpDiff = 0x40, OpLuma = 0x80, OpRun = 0xC0, OpRgb = 0xFE, OpRgba = 0xFF;

    static int Hash(byte r, byte g, byte b, byte a) => (r * 3 + g * 5 + b * 7 + a * 11) & 63;

    /// <summary>Worst-case encoded size of a <paramref name="w"/> x <paramref name="h"/> image.</summary>
    public static int MaxSize(int w, int h) => 14 + w * h * 4 + 8;

    /// <summary>Encodes RGBA pixels (alpha ignored, written as 3 channels) into <paramref name="dst"/> (at least
    /// <see cref="MaxSize"/> bytes) and returns the encoded length.</summary>
    public static int Encode(ReadOnlySpan<byte> rgba, int w, int h, Span<byte> dst)
    {
        "qoif"u8.CopyTo(dst);
        dst[4] = (byte)(w >> 24); dst[5] = (byte)(w >> 16); dst[6] = (byte)(w >> 8); dst[7] = (byte)w;
        dst[8] = (byte)(h >> 24); dst[9] = (byte)(h >> 16); dst[10] = (byte)(h >> 8); dst[11] = (byte)h;
        dst[12] = 3; // RGB
        dst[13] = 0; // sRGB
        int p = 14;
        Span<uint> index = stackalloc uint[64];
        index.Clear();
        byte pr = 0, pg = 0, pb = 0;
        const byte pa = 255;
        int run = 0, n = w * h;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            byte r = rgba[o], g = rgba[o + 1], b = rgba[o + 2];
            if (r == pr && g == pg && b == pb)
            {
                run++;
                if (run == 62 || i == n - 1) { dst[p++] = (byte)(OpRun | (run - 1)); run = 0; }
                continue;
            }
            if (run > 0) { dst[p++] = (byte)(OpRun | (run - 1)); run = 0; }
            int h6 = Hash(r, g, b, pa);
            uint packed = (uint)(r | g << 8 | b << 16 | pa << 24);
            if (index[h6] == packed) dst[p++] = (byte)(OpIndex | h6);
            else
            {
                index[h6] = packed;
                int dr = (sbyte)(byte)(r - pr), dg = (sbyte)(byte)(g - pg), db = (sbyte)(byte)(b - pb);
                int drg = dr - dg, dbg = db - dg;
                if (dr is >= -2 and <= 1 && dg is >= -2 and <= 1 && db is >= -2 and <= 1)
                    dst[p++] = (byte)(OpDiff | (dr + 2) << 4 | (dg + 2) << 2 | (db + 2));
                else if (dg is >= -32 and <= 31 && drg is >= -8 and <= 7 && dbg is >= -8 and <= 7)
                {
                    dst[p++] = (byte)(OpLuma | (dg + 32));
                    dst[p++] = (byte)((drg + 8) << 4 | (dbg + 8));
                }
                else { dst[p++] = OpRgb; dst[p++] = r; dst[p++] = g; dst[p++] = b; }
            }
            pr = r; pg = g; pb = b;
        }
        for (int i = 0; i < 7; i++) dst[p++] = 0;
        dst[p++] = 1;
        return p;
    }

    public static byte[] Encode(ReadOnlySpan<byte> rgba, int w, int h)
    {
        var buf = new byte[MaxSize(w, h)];
        int n = Encode(rgba, w, h, buf);
        Array.Resize(ref buf, n);
        return buf;
    }

    /// <summary>Decodes a QOI image into RGBA pixels (alpha 255 for RGB files).</summary>
    public static (byte[] Rgba, int W, int H) Decode(ReadOnlySpan<byte> src)
    {
        if (src.Length < 22 || !src[..4].SequenceEqual("qoif"u8)) throw new InvalidDataException("not a QOI image");
        int w = src[4] << 24 | src[5] << 16 | src[6] << 8 | src[7], h = src[8] << 24 | src[9] << 16 | src[10] << 8 | src[11];
        if (w <= 0 || h <= 0 || (long)w * h > 1 << 26) throw new InvalidDataException($"QOI size {w}x{h}");
        var dst = new byte[w * h * 4];
        Span<uint> index = stackalloc uint[64];
        index.Clear();
        byte r = 0, g = 0, b = 0, a = 255;
        int p = 14, run = 0, end = src.Length - 8;
        for (int o = 0; o < dst.Length; o += 4)
        {
            if (run > 0) run--;
            else if (p < end)
            {
                byte op = src[p++];
                if (op == OpRgb) { r = src[p++]; g = src[p++]; b = src[p++]; }
                else if (op == OpRgba) { r = src[p++]; g = src[p++]; b = src[p++]; a = src[p++]; }
                else switch (op & 0xC0)
                {
                    case OpIndex: { uint v = index[op & 63]; r = (byte)v; g = (byte)(v >> 8); b = (byte)(v >> 16); a = (byte)(v >> 24); break; }
                    case OpDiff: r += (byte)(((op >> 4) & 3) - 2); g += (byte)(((op >> 2) & 3) - 2); b += (byte)((op & 3) - 2); break;
                    case OpLuma:
                    {
                        int dg = (op & 63) - 32;
                        byte b2 = src[p++];
                        r += (byte)(dg - 8 + ((b2 >> 4) & 15)); g += (byte)dg; b += (byte)(dg - 8 + (b2 & 15));
                        break;
                    }
                    default: run = op & 63; break; // OpRun
                }
                index[Hash(r, g, b, a)] = (uint)(r | g << 8 | b << 16 | a << 24);
            }
            dst[o] = r; dst[o + 1] = g; dst[o + 2] = b; dst[o + 3] = a;
        }
        return (dst, w, h);
    }
}
