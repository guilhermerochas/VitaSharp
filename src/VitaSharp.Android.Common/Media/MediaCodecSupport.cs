using VitaSharp.Core.Media;

namespace VitaSharp.Android.Media;

/// <summary>
/// The platform-neutral parts of the Android MediaCodec decoders (src/VitaSharp.Android/Media), unit-tested on the
/// desktop: copying a decoder's output image (any YUV 4:2:0 layout MediaCodec describes with row and pixel strides:
/// planar I420/YV12, semi-planar NV12/NV21, padded vendor layouts) into the planar <see cref="DecodedPicture"/> the
/// upload path takes, inside the crop rectangle.
/// </summary>
public static class YuvImageCopy
{
    /// <summary>One plane of an android.media.Image: its bytes from the plane's first sample, row stride and pixel
    /// stride (1 = planar, 2 = interleaved chroma).</summary>
    public readonly ref struct Plane(ReadOnlySpan<byte> data, int rowStride, int pixelStride)
    {
        public readonly ReadOnlySpan<byte> Data = data;
        public readonly int RowStride = rowStride, PixelStride = pixelStride;
    }

    /// <summary>
    /// Copies the crop rectangle (<paramref name="cropX"/>, <paramref name="cropY"/>, <paramref name="width"/> x
    /// <paramref name="height"/>, in luma samples) of a YUV_420_888 image into <paramref name="picture"/>, reusing
    /// its planes when the size matches. Odd crop offsets are rounded down for chroma, as the 4:2:0 sampling does.
    /// </summary>
    public static void Copy(Plane y, Plane u, Plane v, int cropX, int cropY, int width, int height, DecodedPicture picture)
    {
        int cw = (width + 1) / 2, ch = (height + 1) / 2;
        if (picture.Y.Length != width * height) picture.Y = new byte[width * height];
        if (picture.U.Length != cw * ch) { picture.U = new byte[cw * ch]; picture.V = new byte[cw * ch]; }
        picture.Width = width;
        picture.Height = height;
        CopyPlane(y, cropX, cropY, width, height, picture.Y);
        CopyPlane(u, cropX / 2, cropY / 2, cw, ch, picture.U);
        CopyPlane(v, cropX / 2, cropY / 2, cw, ch, picture.V);
    }

    static void CopyPlane(Plane p, int x0, int y0, int w, int h, Span<byte> dst)
    {
        for (int row = 0; row < h; row++)
        {
            int start = (y0 + row) * p.RowStride + x0 * p.PixelStride;
            var d = dst.Slice(row * w, w);
            if (p.PixelStride == 1)
            {
                p.Data.Slice(start, w).CopyTo(d);
                continue;
            }
            var s = p.Data[start..];
            for (int x = 0; x < w; x++) d[x] = s[x * p.PixelStride];
        }
    }

    /// <summary>
    /// The planes of a raw output buffer in one of the two standard layouts (COLOR_FormatYUV420Planar = 19,
    /// COLOR_FormatYUV420SemiPlanar = 21), for decoders that give no Image: Y with <paramref name="stride"/>, chroma
    /// after <paramref name="sliceHeight"/> rows. Returns false for any other colour format.
    /// </summary>
    public static bool CopyRaw(ReadOnlySpan<byte> buffer, int colorFormat, int stride, int sliceHeight,
        int cropX, int cropY, int width, int height, DecodedPicture picture)
    {
        int chromaStart = stride * sliceHeight;
        switch (colorFormat)
        {
            case ColorFormatYuv420SemiPlanar:
            {
                var uv = buffer[chromaStart..];
                Copy(new Plane(buffer, stride, 1), new Plane(uv, stride, 2), new Plane(uv[1..], stride, 2), cropX, cropY, width, height, picture);
                return true;
            }
            case ColorFormatYuv420Planar:
            {
                int cs = stride / 2, vStart = chromaStart + cs * ((sliceHeight + 1) / 2);
                Copy(new Plane(buffer, stride, 1), new Plane(buffer[chromaStart..], cs, 1), new Plane(buffer[vStart..], cs, 1),
                    cropX, cropY, width, height, picture);
                return true;
            }
            default:
                return false;
        }
    }

    public const int ColorFormatYuv420Planar = 19, ColorFormatYuv420SemiPlanar = 21, ColorFormatYuv420Flexible = 0x7F420888;
}

/// <summary>
/// The access units since the last IDR picture, kept while a hardware decoder works so that, when it fails, a
/// software decoder can continue from that IDR without a gap; the pictures the failed decoder already output are
/// skipped when the replay produces them again (the same scheme as FFmpegVideoDecoder's hwaccel fallback).
/// </summary>
public sealed class DecoderReplay
{
    public const long MaxBytes = 16 << 20;
    readonly List<(byte[] Data, long Pts)> _units = new();
    long _bytes;
    bool _replayable = true;
    int _producedSinceIdr;

    /// <summary>Records an access unit before it goes to the decoder.</summary>
    public void Add(ReadOnlySpan<byte> accessUnit, long pts)
    {
        if (H264AnnexB.ContainsIdr(accessUnit)) Clear(replayable: true);
        if (_bytes + accessUnit.Length <= MaxBytes) { _units.Add((accessUnit.ToArray(), pts)); _bytes += accessUnit.Length; }
        else _replayable = false; // too long since the last IDR: a fallback restarts at the next one
    }

    /// <summary>A picture came out of the decoder.</summary>
    public void Produced() => _producedSinceIdr++;

    /// <summary>The access units to send to the new decoder and how many of its first pictures to drop.</summary>
    public (IReadOnlyList<(byte[] Data, long Pts)> Units, int Skip) TakeReplay()
    {
        var units = _replayable ? _units.ToArray() : [];
        int skip = units.Length > 0 ? _producedSinceIdr : 0;
        Clear(replayable: _replayable);
        return (units, skip);
    }

    /// <summary>Flush/reset: nothing before this point is replayed.</summary>
    public void Clear(bool replayable = true)
    {
        _units.Clear();
        _bytes = 0;
        _producedSinceIdr = 0;
        _replayable = replayable;
    }

    public int Count => _units.Count;
}

/// <summary>Annex B H.264 helpers.</summary>
public static class H264AnnexB
{
    /// <summary>True when the data starts with a start code (00 00 01 or 00 00 00 01).</summary>
    public static bool StartsWithStartCode(ReadOnlySpan<byte> d) =>
        d.Length >= 3 && d[0] == 0 && d[1] == 0 && (d[2] == 1 || (d.Length >= 4 && d[2] == 0 && d[3] == 1));

    /// <summary>
    /// One access unit for the decoder: <paramref name="header"/> (SPS/PPS, already Annex B) in front of key frames,
    /// then the sample, converted from 4-byte length-prefixed NAL units (MP4 "avcC" samples) when it does not start with
    /// a start code. Returns the bytes written to <paramref name="buffer"/> (grown as needed).
    /// </summary>
    public static int BuildAccessUnit(ReadOnlySpan<byte> sample, ReadOnlySpan<byte> header, ref byte[] buffer)
    {
        int need = header.Length + sample.Length + 64;
        if (buffer.Length < need) buffer = new byte[need * 2];
        header.CopyTo(buffer);
        int o = header.Length;
        if (StartsWithStartCode(sample) || sample.Length < 5)
        {
            sample.CopyTo(buffer.AsSpan(o));
            return o + sample.Length;
        }
        for (int i = 0; i + 4 <= sample.Length;)
        {
            int len = (sample[i] << 24) | (sample[i + 1] << 16) | (sample[i + 2] << 8) | sample[i + 3];
            i += 4;
            if (len <= 0 || len > sample.Length - i) break;
            if (buffer.Length < o + 4 + len) Array.Resize(ref buffer, (o + 4 + len) * 2);
            buffer[o] = 0; buffer[o + 1] = 0; buffer[o + 2] = 0; buffer[o + 3] = 1;
            sample.Slice(i, len).CopyTo(buffer.AsSpan(o + 4));
            o += 4 + len;
            i += len;
        }
        return o;
    }

    /// <summary>True when the access unit has an IDR slice (NAL unit type 5).</summary>
    public static bool ContainsIdr(ReadOnlySpan<byte> au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0) continue;
            int nal;
            if (au[i + 2] == 1) nal = i + 3;
            else if (au[i + 2] == 0 && au[i + 3] == 1 && i + 4 < au.Length) nal = i + 4;
            else continue;
            if (nal < au.Length && (au[nal] & 0x1F) == 5) return true;
            i = nal - 1;
        }
        return false;
    }
}
