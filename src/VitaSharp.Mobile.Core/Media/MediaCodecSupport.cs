using VitaSharp.Core.Media;

namespace VitaSharp.Mobile.Core.Media;

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
        if (picture.Y.Length != width * height)
        {
            picture.Y = new byte[width * height];
        }

        if (picture.U.Length != cw * ch)
        {
            picture.U = new byte[cw * ch];
            picture.V = new byte[cw * ch];
        }
        picture.Width = width;
        picture.Height = height;
        CopyPlane(y, cropX, cropY, width, height, picture.Y);
        CopyPlane(u, cropX / 2, cropY / 2, cw, ch, picture.U);
        CopyPlane(v, cropX / 2, cropY / 2, cw, ch, picture.V);
    }

    // Every movie picture goes through here on the decoding thread (F1 2011's movies spent 5 ms per 960x544 picture
    // with Span.CopyTo rows, Mono's slow block copy, and a bounds-checked byte loop for interleaved chroma): rows go to
    // FastCopy (libc memmove on Mono), interleaved chroma through pointers.
    private static unsafe void CopyPlane(Plane p, int x0, int y0, int w, int h, Span<byte> dst)
    {
        if (h <= 0 || w <= 0)
        {
            return;
        }
        // the last row reads only up to its last sample: check the whole range once
        int lastStart = (y0 + h - 1) * p.RowStride + x0 * p.PixelStride;
        if (lastStart + (w - 1) * p.PixelStride >= p.Data.Length || dst.Length < w * h)
        {
            throw new ArgumentOutOfRangeException(nameof(p), "plane smaller than the crop rectangle");
        }

        fixed (byte* src0 = p.Data)
        fixed (byte* dst0 = dst)
        {
            for (int row = 0; row < h; row++)
            {
                byte* s = src0 + (y0 + row) * p.RowStride + x0 * p.PixelStride;
                byte* d = dst0 + row * w;
                if (p.PixelStride == 1)
                {
                    VitaSharp.Core.Util.FastCopy.Copy(new ReadOnlySpan<byte>(s, w), new Span<byte>(d, w));
                    continue;
                }
                int stride = p.PixelStride, x = 0;
                if (stride == 2)
                {
                    // 8 samples from 16 bytes at a time (the even bytes)
                    for (; x + 8 <= w && (x + 8) * 2 - 1 <= (w - 1) * 2; x += 8)
                    {
                        ulong a = *(ulong*)(s + x * 2), b = *(ulong*)(s + x * 2 + 8);
                        ulong ea = (a & 0xFF) | ((a >> 8) & 0xFF00) | ((a >> 16) & 0xFF0000) | ((a >> 24) & 0xFF000000);
                        ulong eb = (b & 0xFF) | ((b >> 8) & 0xFF00) | ((b >> 16) & 0xFF0000) | ((b >> 24) & 0xFF000000);
                        *(ulong*)(d + x) = ea | (eb << 32);
                    }
                }
                for (; x < w; x++)
                {
                    d[x] = s[x * stride];
                }
            }
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
                ReadOnlySpan<byte> uv = buffer[chromaStart..];
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
    private readonly List<(byte[] Data, long Pts)> _units = new();
    private long _bytes;
    private bool _replayable = true;
    private int _producedSinceIdr;

    /// <summary>Records an access unit before it goes to the decoder.</summary>
    public void Add(ReadOnlySpan<byte> accessUnit, long pts)
    {
        if (H264AnnexB.ContainsIdr(accessUnit))
        {
            Clear(replayable: true);
        }

        if (_bytes + accessUnit.Length <= MaxBytes)
        {
            _units.Add((accessUnit.ToArray(), pts));
            _bytes += accessUnit.Length;
        }
        else
        {
            _replayable = false; // too long since the last IDR: a fallback restarts at the next one
        }
    }

    /// <summary>A picture came out of the decoder.</summary>
    public void Produced() => _producedSinceIdr++;

    /// <summary>The access units to send to the new decoder and how many of its first pictures to drop.</summary>
    public (IReadOnlyList<(byte[] Data, long Pts)> Units, int Skip) TakeReplay()
    {
        (byte[] Data, long Pts)[] units = _replayable ? _units.ToArray() : [];
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
        if (buffer.Length < need)
        {
            buffer = new byte[need * 2];
        }

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
            if (len <= 0 || len > sample.Length - i)
            {
                break;
            }

            if (buffer.Length < o + 4 + len)
            {
                Array.Resize(ref buffer, (o + 4 + len) * 2);
            }

            buffer[o] = 0;
            buffer[o + 1] = 0;
            buffer[o + 2] = 0;
            buffer[o + 3] = 1;
            sample.Slice(i, len).CopyTo(buffer.AsSpan(o + 4));
            o += 4 + len;
            i += len;
        }
        return o;
    }

    /// <summary>True when the access unit has an IDR slice (NAL unit type 5).</summary>
    public static bool ContainsIdr(ReadOnlySpan<byte> au) => ContainsNal(au, 5, 5);

    /// <summary>True when the access unit has a slice (NAL unit type 1 or 5), that is, it makes a picture; a unit with
    /// only SPS/PPS/SEI makes none.</summary>
    public static bool ContainsSlice(ReadOnlySpan<byte> au) => ContainsNal(au, 1, 5);

    /// <summary>True when a NAL unit of type <paramref name="firstType"/> or <paramref name="secondType"/> is in the
    /// access unit.</summary>
    private static bool ContainsNal(ReadOnlySpan<byte> au, int firstType, int secondType)
    {
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0)
            {
                continue;
            }

            int nal;
            if (au[i + 2] == 1)
            {
                nal = i + 3;
            }
            else if (au[i + 2] == 0 && au[i + 3] == 1 && i + 4 < au.Length)
            {
                nal = i + 4;
            }
            else
            {
                continue;
            }

            int nalType = nal < au.Length ? au[nal] & 0x1F : -1;
            if (nalType == firstType || nalType == secondType)
            {
                return true;
            }

            i = nal - 1;
        }
        return false;
    }
}

/// <summary>
/// Makes an asynchronous MediaCodec decoder answer the way the Vita's sceAvcdecDecode does: the call that hands over an
/// access unit returns the picture that unit completes (sceAvcdecDecode is synchronous on the console; Vita3K and the
/// desktop's FFmpeg path send the unit and receive its picture in the same call). MediaCodec decodes on its own threads,
/// so a unit's picture comes out some milliseconds after it was queued (12.8 ms per picture with c2.android.avc.decoder
/// on the Redmi Note 14). Waiting only 5 ms, and not at all for the first picture of a stream, made most calls answer
/// with an earlier unit's picture, a repeated one, or none; the HLE's stand-ins for a missing picture (the previous
/// picture again, the last picture of the previous movie pass) showed as black and old frames in F1 2011's and
/// Uncharted's movies on the phone.
/// <para>After each unit, <see cref="Await"/> drains the decoder until every picture-making unit has its picture out,
/// apart from the pictures the decoder holds on purpose (reordering: a stream with B-frames, or a decoder that outputs
/// from a full DPB), within <see cref="PictureDeadlineUs"/> (<see cref="FirstPictureDeadlineUs"/> for the first
/// picture after a start or flush, which includes the decoder's start-up). A missed deadline counts as one more held
/// picture, so later calls stop waiting for it (no stall per call); when the decoder gives a held picture out early
/// again, the count goes back down (<see cref="PictureOut"/>).</para>
/// <para>MediaCodecVideoDecoder uses it with <see cref="PipelinedAfterStart"/>: only a stream's first picture is
/// waited for (and drained out of a decoder that holds it, PrimeFirstPicture), the later ones come out about a call
/// after their unit, so the decoding overlaps the game's frame as before (waiting on every call cost 7% fps in F1
/// 2011's intro on the phone). VITASHARP_MEDIACODEC_SYNC=all waits on every call.</para>
/// </summary>
public sealed class SynchronousOutput
{
    /// <summary>Longest wait for a unit's picture: three times a software decode of a Vita-sized picture, so a slow
    /// picture is not taken for a held one, and still within a 30 fps movie frame.</summary>
    public const long PictureDeadlineUs = 40_000;

    /// <summary>Default longest wait for the first picture after a start or flush (the decoder's threads start up);
    /// a decoder that holds its first pictures longer is drained (MediaCodecVideoDecoder.PrimeFirstPicture).</summary>
    public const long FirstPictureDeadlineUs = 250_000;

    /// <summary>After the first picture, in <see cref="PipelinedAfterStart"/> mode, the longest wait for a call without a
    /// new picture (the timing MediaCodecVideoDecoder always had).</summary>
    public const long PipelinedGraceUs = 5_000;

    /// <summary>
    /// The first picture of a stream is waited for (the call that starts the stream answers with it, as on the Vita),
    /// the later ones are not: a call takes the pictures that are done and waits at most <see cref="PipelinedGraceUs"/>
    /// when none is, so the decoder works while the game does (the picture of a call comes out about one call later).
    /// Waiting for every picture made the game thread wait the decoder's ~15 ms per picture on the phone: F1 2011's
    /// intro went from 20.6 to 19.1 fps. False: every call waits for its own picture.
    /// </summary>
    public bool PipelinedAfterStart { get; init; }

    /// <summary>Longest wait for the first picture after a start or flush (<see cref="FirstPictureDeadlineUs"/> unless
    /// set).</summary>
    public long FirstDeadlineUs { get; init; } = FirstPictureDeadlineUs;

    /// <summary>Longest wait for a later picture (<see cref="PictureDeadlineUs"/> unless set; tests that run a decoder
    /// on real threads set a long one, so a slow machine is not taken for a decoder that holds pictures).</summary>
    public long DeadlineUs { get; init; } = PictureDeadlineUs;

    /// <summary>The most pictures a decoder is taken to hold back (the largest H.264 DPB); past it, every call waits again.</summary>
    public const int MaxHeldPictures = 16;

    /// <summary>One drain poll waits at most this long before the deadline is checked again.</summary>
    private const long PollUs = 10_000;

    private long _units;
    private long _pictures;
    private bool _started;
    private bool _missedBeforeFirstPicture;

    /// <summary>Pictures the decoder keeps back on purpose (learned from missed deadlines, lowered when it gives
    /// one out early).</summary>
    public int HeldPictures { get; private set; }

    /// <summary>Statistics: deadlines missed.</summary>
    public long MissedDeadlines { get; private set; }

    /// <summary>Statistics: time spent waiting, in microseconds.</summary>
    public long WaitedUs { get; private set; }

    /// <summary>A picture came out since the last start or flush.</summary>
    public bool Started => _started;

    /// <summary>Picture-making units queued whose picture is not out yet.</summary>
    public long Outstanding => _units - _pictures;

    /// <summary>True while a queued unit's picture is still due.</summary>
    public bool PictureDue => Outstanding > HeldPictures;

    /// <summary>A unit went to the decoder; <paramref name="makesPicture"/>: it has a slice
    /// (<see cref="H264AnnexB.ContainsSlice"/>).</summary>
    public void UnitQueued(bool makesPicture)
    {
        if (makesPicture)
        {
            _units++;
        }
    }

    /// <summary>The stream's first picture was obtained another way (a drain): later calls do not wait for a first
    /// picture.</summary>
    public void MarkStarted() => _started = true;

    /// <summary>A picture came out of the decoder.</summary>
    public void PictureOut()
    {
        _pictures++;
        _started = true;
        long outstanding = Math.Max(0, Outstanding);
        if (outstanding < HeldPictures)
        {
            HeldPictures = (int)outstanding;
        }
    }

    /// <summary>
    /// Drains until no picture is due or the deadline passes. <paramref name="poll"/> takes the decoder's finished
    /// pictures out, waiting at most the given microseconds for one, and reports each with <see cref="PictureOut"/>;
    /// <paramref name="nowUs"/> is a monotonic clock in microseconds.
    /// </summary>
    public void Await(Action<long> poll, Func<long> nowUs)
    {
        if (!PictureDue)
        {
            return;
        }

        if (PipelinedAfterStart && _started)
        {
            long before = _pictures;
            poll(0);
            if (_pictures == before && PictureDue)
            {
                poll(PipelinedGraceUs);
            }

            return;
        }

        if (_missedBeforeFirstPicture && !_started)
        {
            // the first picture did not come within its deadline: the decoder fills its pipeline (a DPB it holds back)
            // before it gives anything out; waiting again on every call until then would stall each of them
            poll(0);
            if (!_started)
            {
                HeldPictures = (int)Math.Min(Outstanding, MaxHeldPictures);
            }

            return;
        }

        long start = nowUs();
        long deadline = start + (_started ? DeadlineUs : FirstDeadlineUs);
        while (PictureDue)
        {
            long left = deadline - nowUs();
            if (left <= 0)
            {
                MissedDeadlines++;
                _missedBeforeFirstPicture |= !_started;
                if (HeldPictures < MaxHeldPictures)
                {
                    HeldPictures++;
                }

                break;
            }

            poll(Math.Min(left, PollUs));
        }
        WaitedUs += nowUs() - start;
    }

    /// <summary>Start, flush or reset: nothing is in flight. <see cref="HeldPictures"/> stays (same decoder).</summary>
    public void Restart()
    {
        _units = 0;
        _pictures = 0;
        _started = false;
        _missedBeforeFirstPicture = false;
    }
}

/// <summary>The ADTS header of an AAC frame (ISO/IEC 13818-7 / 14496-3): its length and the AudioSpecificConfig it
/// stands for, so ADTS frames can go to a decoder as raw frames.</summary>
public static class AdtsHeader
{
    private static readonly int[] s_sampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>
    /// Parses the header at the start of <paramref name="frame"/>: <paramref name="headerLength"/> 7 bytes, or 9 with
    /// the CRC; <paramref name="audioSpecificConfig"/> the 2-byte config (object type = profile + 1, sampling frequency
    /// index, channel configuration). False when the frame does not start with a valid ADTS header.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, out int headerLength, out byte[] audioSpecificConfig, out int sampleRate, out int channels)
    {
        headerLength = 0;
        audioSpecificConfig = [];
        sampleRate = 0;
        channels = 0;
        if (frame.Length < 7 || frame[0] != 0xFF || (frame[1] & 0xF6) != 0xF0)
        {
            return false;
        }

        bool protectionAbsent = (frame[1] & 1) == 1;
        int objectType = ((frame[2] >> 6) & 3) + 1;
        int frequencyIndex = (frame[2] >> 2) & 0xF;
        int channelConfiguration = ((frame[2] & 1) << 2) | (frame[3] >> 6);
        if (frequencyIndex >= s_sampleRates.Length)
        {
            return false;
        }

        headerLength = protectionAbsent ? 7 : 9;
        int frameLength = ((frame[3] & 3) << 11) | (frame[4] << 3) | (frame[5] >> 5);
        if (frameLength < headerLength || frame.Length < headerLength)
        {
            return false;
        }

        sampleRate = s_sampleRates[frequencyIndex];
        channels = Math.Max(1, channelConfiguration == 7 ? 8 : channelConfiguration);
        int config = (objectType << 11) | (frequencyIndex << 7) | (channelConfiguration << 3);
        audioSpecificConfig = [(byte)(config >> 8), (byte)config];
        return true;
    }
}
