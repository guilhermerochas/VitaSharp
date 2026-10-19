namespace VitaSharp.Core.Media;

/// <summary>Pixel layouts host capture APIs commonly deliver (DirectShow/V4L2/Media Foundation/Android).</summary>
public enum CameraHostFormat
{
    /// <summary>Packed 4:2:2, Y0 U Y1 V.</summary>
    Yuyv,
    /// <summary>Packed 4:2:2, U Y0 V Y1.</summary>
    Uyvy,
    /// <summary>Y plane followed by interleaved U/V at half resolution.</summary>
    Nv12,
    /// <summary>Y plane followed by interleaved V/U at half resolution (Android default).</summary>
    Nv21,
    /// <summary>B, G, R bytes; rows padded to 4 bytes (DIB layout).</summary>
    Bgr24,
    /// <summary>B, G, R, X bytes.</summary>
    Bgra32,
    /// <summary>R, G, B, A bytes.</summary>
    Rgba32,
}

/// <summary>Vita SceCameraFormat values.</summary>
public static class VitaCameraFormat
{
    public const int Invalid = 0, Yuv422Planar = 1, Yuv422Packed = 2, Yuv420Planar = 3, Argb = 4, Abgr = 5, Raw8 = 6;
    public static bool IsPlanar(int f) => f is Yuv422Planar or Yuv420Planar;
}

/// <summary>
/// Image conversions for the camera: host capture format to RGBA, RGBA resampling (centre crop to the target
/// aspect, bilinear), and RGBA to the Vita camera output formats. All pure functions over spans.
/// </summary>
public static class CameraImage
{
    // ------------------------------------------------------------------ host -> RGBA

    /// <summary>Bytes per row of a tightly packed host image (DIB rows for <see cref="CameraHostFormat.Bgr24"/>).</summary>
    public static int HostStride(CameraHostFormat f, int width) => f switch
    {
        CameraHostFormat.Yuyv or CameraHostFormat.Uyvy => width * 2,
        CameraHostFormat.Nv12 or CameraHostFormat.Nv21 => width,
        CameraHostFormat.Bgr24 => (width * 3 + 3) & ~3,
        _ => width * 4,
    };

    /// <summary>Minimum source size in bytes for a host image.</summary>
    public static int HostSize(CameraHostFormat f, int width, int height) => f switch
    {
        CameraHostFormat.Nv12 or CameraHostFormat.Nv21 => width * height + width * ((height + 1) / 2),
        _ => HostStride(f, width) * height,
    };

    /// <summary>Converts a host frame to RGBA (top-down). <paramref name="bottomUp"/> flips row order (DIB RGB).
    /// Host YUV is taken as BT.601 limited range, as webcams deliver.</summary>
    public static void DecodeToRgba(ReadOnlySpan<byte> src, CameraHostFormat f, int width, int height, bool bottomUp, Span<byte> dst)
    {
        if (src.Length < HostSize(f, width, height)) throw new ArgumentException("source too small", nameof(src));
        if (dst.Length < width * height * 4) throw new ArgumentException("destination too small", nameof(dst));
        int stride = HostStride(f, width);
        for (int y = 0; y < height; y++)
        {
            int sy = bottomUp ? height - 1 - y : y;
            var o = dst.Slice(y * width * 4, width * 4);
            switch (f)
            {
                case CameraHostFormat.Yuyv:
                case CameraHostFormat.Uyvy:
                {
                    var row = src.Slice(sy * stride, stride);
                    bool uyvy = f == CameraHostFormat.Uyvy;
                    for (int x = 0; x + 1 < width; x += 2)
                    {
                        int b = x * 2;
                        int y0 = uyvy ? row[b + 1] : row[b], u = uyvy ? row[b] : row[b + 1];
                        int y1 = uyvy ? row[b + 3] : row[b + 2], v = uyvy ? row[b + 2] : row[b + 3];
                        YuvToRgb(y0, u, v, o.Slice(x * 4, 4));
                        YuvToRgb(y1, u, v, o.Slice(x * 4 + 4, 4));
                    }
                    if ((width & 1) != 0 && width > 1) // odd width (not produced by real devices): repeat the last pixel
                        o.Slice((width - 2) * 4, 4).CopyTo(o.Slice((width - 1) * 4, 4));
                    break;
                }
                case CameraHostFormat.Nv12:
                case CameraHostFormat.Nv21:
                {
                    var yRow = src.Slice(sy * width, width);
                    var uvRow = src.Slice(width * height + (sy / 2) * width);
                    bool nv21 = f == CameraHostFormat.Nv21;
                    for (int x = 0; x < width; x++)
                    {
                        int c = x & ~1;
                        int u = uvRow[nv21 ? c + 1 : c], v = uvRow[nv21 ? c : c + 1];
                        YuvToRgb(yRow[x], u, v, o.Slice(x * 4, 4));
                    }
                    break;
                }
                case CameraHostFormat.Bgr24:
                {
                    var row = src.Slice(sy * stride, width * 3);
                    for (int x = 0; x < width; x++)
                    {
                        o[x * 4] = row[x * 3 + 2]; o[x * 4 + 1] = row[x * 3 + 1]; o[x * 4 + 2] = row[x * 3]; o[x * 4 + 3] = 255;
                    }
                    break;
                }
                case CameraHostFormat.Bgra32:
                {
                    var row = src.Slice(sy * stride, width * 4);
                    for (int x = 0; x < width; x++)
                    {
                        o[x * 4] = row[x * 4 + 2]; o[x * 4 + 1] = row[x * 4 + 1]; o[x * 4 + 2] = row[x * 4]; o[x * 4 + 3] = 255;
                    }
                    break;
                }
                default:
                    src.Slice(sy * stride, width * 4).CopyTo(o);
                    break;
            }
        }
    }

    static void YuvToRgb(int y, int u, int v, Span<byte> o)
    {
        int c = (y - 16) * 298, d = u - 128, e = v - 128;
        o[0] = Clamp((c + 409 * e + 128) >> 8);
        o[1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
        o[2] = Clamp((c + 516 * d + 128) >> 8);
        o[3] = 255;
    }

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // ------------------------------------------------------------------ resampling

    /// <summary>Scales an RGBA image to <paramref name="dw"/>×<paramref name="dh"/>, cropping the centre of the
    /// source to the target aspect ratio (no stretching), with bilinear filtering. <paramref name="mirror"/> flips
    /// horizontally, <paramref name="flip"/> vertically (SCE_CAMERA_REVERSE_*).</summary>
    public static void Resample(ReadOnlySpan<byte> src, int sw, int sh, Span<byte> dst, int dw, int dh, bool mirror = false, bool flip = false)
    {
        if (dst.Length < dw * dh * 4) throw new ArgumentException("destination too small", nameof(dst));
        // crop window in source pixels with the destination's aspect ratio
        double cw = sw, ch = sh;
        if ((long)sw * dh > (long)sh * dw) cw = (double)sh * dw / dh; else ch = (double)sw * dh / dw;
        double x0 = (sw - cw) / 2, y0 = (sh - ch) / 2;
        double sx = cw / dw, sy = ch / dh;
        if (sw == dw && sh == dh && !mirror && !flip) { src[..(dw * dh * 4)].CopyTo(dst); return; }
        Span<int> xi = dw <= 4096 ? stackalloc int[dw] : new int[dw];
        Span<int> xf = dw <= 4096 ? stackalloc int[dw] : new int[dw];
        for (int x = 0; x < dw; x++)
        {
            double fx = x0 + (x + 0.5) * sx - 0.5;
            if (fx < 0) fx = 0;
            int ix = Math.Min((int)fx, sw - 1);
            xi[x] = ix;
            xf[x] = ix + 1 < sw ? (int)((fx - ix) * 256) : 0;
        }
        for (int y = 0; y < dh; y++)
        {
            double fy = y0 + (y + 0.5) * sy - 0.5;
            if (fy < 0) fy = 0;
            int iy = Math.Min((int)fy, sh - 1);
            int wy = iy + 1 < sh ? (int)((fy - iy) * 256) : 0;
            var r0 = src.Slice(iy * sw * 4, sw * 4);
            var r1 = src.Slice(Math.Min(iy + 1, sh - 1) * sw * 4, sw * 4);
            int oy = flip ? dh - 1 - y : y;
            var o = dst.Slice(oy * dw * 4, dw * 4);
            for (int x = 0; x < dw; x++)
            {
                int a = xi[x] * 4, b = Math.Min(xi[x] + 1, sw - 1) * 4, wx = xf[x];
                int ox = (mirror ? dw - 1 - x : x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    int top = r0[a + c] * (256 - wx) + r0[b + c] * wx;
                    int bot = r1[a + c] * (256 - wx) + r1[b + c] * wx;
                    o[ox + c] = (byte)((top * (256 - wy) + bot * wy + 32768) >> 16);
                }
            }
        }
    }

    // ------------------------------------------------------------------ RGBA -> Vita formats

    /// <summary>Bytes the Vita writes into each plane for a format, size and line pitch (in pixels).</summary>
    public static (int I, int U, int V) PlaneSizes(int format, int width, int height, int stride)
    {
        return format switch
        {
            VitaCameraFormat.Yuv422Planar => (stride * height, stride / 2 * height, stride / 2 * height),
            VitaCameraFormat.Yuv420Planar => (stride * height, stride / 2 * (height / 2), stride / 2 * (height / 2)),
            VitaCameraFormat.Yuv422Packed => (stride * 2 * height, 0, 0),
            VitaCameraFormat.Argb or VitaCameraFormat.Abgr => (stride * 4 * height, 0, 0),
            VitaCameraFormat.Raw8 => (stride * height, 0, 0),
            _ => (0, 0, 0),
        };
    }

    /// <summary>Converts an RGBA image to a Vita camera format. <paramref name="stride"/> is the line pitch in pixels
    /// (≥ width). Rows that do not fit in a plane are skipped (never writes past a span). <paramref name="fullRange"/>
    /// selects SCE_CAMERA_DATA_RANGE_FULL (0..255) instead of BT.601 limited range (16..235/240).</summary>
    public static void Encode(ReadOnlySpan<byte> rgba, int width, int height, int format, bool fullRange, int stride,
        Span<byte> iPlane, Span<byte> uPlane, Span<byte> vPlane)
    {
        if (stride < width) stride = width;
        switch (format)
        {
            case VitaCameraFormat.Argb:
            case VitaCameraFormat.Abgr:
            {
                bool argb = format == VitaCameraFormat.Argb; // u32 0xAARRGGBB little endian = B, G, R, A bytes
                for (int y = 0; y < height; y++)
                {
                    int off = y * stride * 4;
                    if (off + width * 4 > iPlane.Length) break;
                    var s = rgba.Slice(y * width * 4, width * 4);
                    var d = iPlane.Slice(off, width * 4);
                    if (!argb) { s.CopyTo(d); continue; }
                    for (int x = 0; x < width * 4; x += 4)
                    {
                        d[x] = s[x + 2]; d[x + 1] = s[x + 1]; d[x + 2] = s[x]; d[x + 3] = s[x + 3];
                    }
                }
                break;
            }
            case VitaCameraFormat.Raw8:
                for (int y = 0; y < height; y++)
                {
                    int off = y * stride;
                    if (off + width > iPlane.Length) break;
                    var s = rgba.Slice(y * width * 4, width * 4);
                    for (int x = 0; x < width; x++) iPlane[off + x] = Luma(s[x * 4], s[x * 4 + 1], s[x * 4 + 2], fullRange);
                }
                break;
            case VitaCameraFormat.Yuv422Packed:
                for (int y = 0; y < height; y++)
                {
                    int off = y * stride * 2;
                    if (off + width * 2 > iPlane.Length) break;
                    var s = rgba.Slice(y * width * 4, width * 4);
                    for (int x = 0; x + 1 < width; x += 2)
                    {
                        int p = x * 4;
                        int r = (s[p] + s[p + 4] + 1) >> 1, g = (s[p + 1] + s[p + 5] + 1) >> 1, b = (s[p + 2] + s[p + 6] + 1) >> 1;
                        int o = off + x * 2;
                        iPlane[o] = Luma(s[p], s[p + 1], s[p + 2], fullRange);
                        iPlane[o + 1] = ChromaU(r, g, b, fullRange);
                        iPlane[o + 2] = Luma(s[p + 4], s[p + 5], s[p + 6], fullRange);
                        iPlane[o + 3] = ChromaV(r, g, b, fullRange);
                    }
                }
                break;
            case VitaCameraFormat.Yuv422Planar:
            case VitaCameraFormat.Yuv420Planar:
            {
                bool is420 = format == VitaCameraFormat.Yuv420Planar;
                for (int y = 0; y < height; y++)
                {
                    int off = y * stride;
                    if (off + width > iPlane.Length) break;
                    var s = rgba.Slice(y * width * 4, width * 4);
                    for (int x = 0; x < width; x++) iPlane[off + x] = Luma(s[x * 4], s[x * 4 + 1], s[x * 4 + 2], fullRange);
                }
                int cw = width / 2, cs = stride / 2, chH = is420 ? height / 2 : height;
                for (int cy = 0; cy < chH; cy++)
                {
                    int off = cy * cs;
                    bool uOk = off + cw <= uPlane.Length, vOk = off + cw <= vPlane.Length;
                    if (!uOk && !vOk) break;
                    int y0 = is420 ? cy * 2 : cy, y1 = is420 ? Math.Min(cy * 2 + 1, height - 1) : cy;
                    var s0 = rgba.Slice(y0 * width * 4, width * 4);
                    var s1 = rgba.Slice(y1 * width * 4, width * 4);
                    for (int cx = 0; cx < cw; cx++)
                    {
                        int p = cx * 8;
                        int r = (s0[p] + s0[p + 4] + s1[p] + s1[p + 4] + 2) >> 2;
                        int g = (s0[p + 1] + s0[p + 5] + s1[p + 1] + s1[p + 5] + 2) >> 2;
                        int b = (s0[p + 2] + s0[p + 6] + s1[p + 2] + s1[p + 6] + 2) >> 2;
                        if (uOk) uPlane[off + cx] = ChromaU(r, g, b, fullRange);
                        if (vOk) vPlane[off + cx] = ChromaV(r, g, b, fullRange);
                    }
                }
                break;
            }
        }
    }

    // BT.601 (JFIF coefficients for full range, studio swing for limited range)
    public static byte Luma(int r, int g, int b, bool full) =>
        full ? (byte)((77 * r + 150 * g + 29 * b + 128) >> 8) : (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

    public static byte ChromaU(int r, int g, int b, bool full) =>
        full ? Clamp(((-43 * r - 85 * g + 128 * b + 128) >> 8) + 128) : Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);

    public static byte ChromaV(int r, int g, int b, bool full) =>
        full ? Clamp(((128 * r - 107 * g - 21 * b + 128) >> 8) + 128) : Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
}
