namespace VitaSharp.Core.Media;

/// <summary>
/// Planar YCbCr &lt;-&gt; 8-bit RGBA conversions. JFIF (full-range BT.601, what JPEG stores) by default; the BT.601 option
/// reads limited-range (16-235 / 16-240) input. Chroma is upsampled by replication and downsampled by box averaging.
/// RGBA means bytes R, G, B, A in memory (the Vita's A8B8G8R8 little-endian framebuffer layout), BGRA swaps R and B.
/// </summary>
public static class YuvConvert
{
    const int One = 1 << 16, Half = 1 << 15;

    // JFIF full range
    const int JCrR = 91881, JCbG = 22554, JCrG = 46802, JCbB = 116130;
    // BT.601 limited range
    const int LY = 76309, LCrR = 104597, LCbG = 25675, LCrG = 53279, LCbB = 132201;

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>Converts <paramref name="width"/> x <paramref name="height"/> pixels. Chroma sample (x / hdiv, y / vdiv)
    /// covers each pixel. <paramref name="dstStride"/> is in pixels.</summary>
    public static void ToRgba(ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int cStride,
        int width, int height, int hdiv, int vdiv, Span<byte> dst, int dstStride, bool bgra = false, bool bt601 = false)
    {
        int ri = bgra ? 2 : 0, bi = bgra ? 0 : 2;
        for (int row = 0; row < height; row++)
        {
            var yr = y.Slice(row * yStride);
            int crow = row / vdiv * cStride;
            var o = dst.Slice(row * dstStride * 4, width * 4);
            for (int x = 0; x < width; x++)
            {
                int u = cb[crow + x / hdiv] - 128, v = cr[crow + x / hdiv] - 128;
                int l, r, g, b;
                if (bt601)
                {
                    l = (yr[x] - 16) * LY + Half;
                    r = (l + LCrR * v) >> 16;
                    g = (l - LCbG * u - LCrG * v) >> 16;
                    b = (l + LCbB * u) >> 16;
                }
                else
                {
                    l = yr[x] * One + Half;
                    r = (l + JCrR * v) >> 16;
                    g = (l - JCbG * u - JCrG * v) >> 16;
                    b = (l + JCbB * u) >> 16;
                }
                o[x * 4 + ri] = Clamp(r);
                o[x * 4 + 1] = Clamp(g);
                o[x * 4 + bi] = Clamp(b);
                o[x * 4 + 3] = 0xFF;
            }
        }
    }

    /// <summary>Luma-only image to opaque grey RGBA.</summary>
    public static void GrayToRgba(ReadOnlySpan<byte> y, int yStride, int width, int height, Span<byte> dst, int dstStride, bool bt601 = false)
    {
        for (int row = 0; row < height; row++)
        {
            var yr = y.Slice(row * yStride);
            var o = dst.Slice(row * dstStride * 4, width * 4);
            for (int x = 0; x < width; x++)
            {
                byte l = bt601 ? Clamp(((yr[x] - 16) * LY + Half) >> 16) : yr[x];
                o[x * 4] = o[x * 4 + 1] = o[x * 4 + 2] = l;
                o[x * 4 + 3] = 0xFF;
            }
        }
    }

    /// <summary>RGBA (stride in pixels) to planar JFIF YCbCr. The chroma planes are ceil(w / hdiv) x ceil(h / vdiv),
    /// each sample the average of the pixels it covers.</summary>
    public static void FromRgba(ReadOnlySpan<byte> src, int srcStride, int width, int height, bool bgra, int hdiv, int vdiv,
        Span<byte> y, int yStride, Span<byte> cb, Span<byte> cr, int cStride)
    {
        int ri = bgra ? 2 : 0, bi = bgra ? 0 : 2;
        for (int row = 0; row < height; row++)
        {
            var s = src.Slice(row * srcStride * 4);
            var o = y.Slice(row * yStride, width);
            for (int x = 0; x < width; x++)
                o[x] = (byte)((19595 * s[x * 4 + ri] + 38470 * s[x * 4 + 1] + 7471 * s[x * 4 + bi] + Half) >> 16);
        }
        int cw = (width + hdiv - 1) / hdiv, ch = (height + vdiv - 1) / vdiv;
        for (int cy = 0; cy < ch; cy++)
            for (int cx = 0; cx < cw; cx++)
            {
                long rs = 0, gs = 0, bs = 0;
                int n = 0;
                for (int dy = 0; dy < vdiv; dy++)
                {
                    int py = cy * vdiv + dy;
                    if (py >= height) break;
                    for (int dx = 0; dx < hdiv; dx++)
                    {
                        int px = cx * hdiv + dx;
                        if (px >= width) break;
                        int i = (py * srcStride + px) * 4;
                        rs += src[i + ri]; gs += src[i + 1]; bs += src[i + bi];
                        n++;
                    }
                }
                long bias = (128L * One + Half) * n, div = (long)One * n;
                cb[cy * cStride + cx] = Clamp((int)((-11059 * rs - 21709 * gs + 32768 * bs + bias) / div));
                cr[cy * cStride + cx] = Clamp((int)((32768 * rs - 27439 * gs - 5329 * bs + bias) / div));
            }
    }
}
