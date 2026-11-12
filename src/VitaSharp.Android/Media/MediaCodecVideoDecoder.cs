using Android.Media;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Media;

namespace VitaSharp.Android.Media;

/// <summary>
/// Built into the app, not run on a device yet. The Android video decoder backend: H.264 through MediaCodec (the .NET
/// for Android bindings of android.media.MediaCodec), surface-less, into the planar YUV <see cref="DecodedPicture"/>
/// that SceAvcdec/SceVideodec and the SceAvPlayer media player hand to the existing upload path. It is the default
/// backend on Android (EmulationSession installs it); FFmpeg is not part of the app.
/// <list type="bullet">
/// <item>Decoders: the device's hardware H.264 decoders first, then Android's own software decoder
///   (c2.android.avc.decoder / OMX.google.h264.decoder), which every device has. When a decoder fails while running,
///   the next one continues from the last IDR picture (<see cref="DecoderReplay"/>, the same scheme as the desktop's
///   FFmpeg hwaccel fallback).</item>
/// <item>Output: COLOR_FormatYUV420Flexible read through MediaCodec.getOutputImage (YUV_420_888 planes with their row
///   and pixel strides, so vendor layouts work), inside the crop rectangle; decoders that give no image fall back to
///   the raw buffer in the two standard layouts.</item>
/// <item>Low delay: KEY_LOW_LATENCY (API 30) where the decoder supports it and realtime priority, so pictures come out
///   without a reorder delay the guest does not expect (the Vita's decoder is low delay); colour range and standard
///   from the output format.</item>
/// </list>
/// </summary>
public sealed class MediaCodecVideoBackend : IVideoDecoderBackend
{
    public string Name => "mediacodec";

    public IVideoDecoder? CreateH264(VideoDecoderOptions options) =>
        MediaCodecVideoDecoder.Create(options.MaxWidth > 0 ? options.MaxWidth : 960, options.MaxHeight > 0 ? options.MaxHeight : 544);
}

/// <summary>A synchronous MediaCodec H.264 decoder (see <see cref="MediaCodecVideoBackend"/>).</summary>
public sealed unsafe class MediaCodecVideoDecoder : IVideoDecoder
{
    const string Mime = "video/avc";
    const int InfoTryAgainLater = -1, InfoOutputFormatChanged = -2, InfoOutputBuffersChanged = -3;
    const long InputTimeoutUs = 10_000;
    // an access unit waits at most this long for an input buffer while the output is drained
    const int InputAttempts = 20;

    /// <summary>Process-wide counters (PERF, tests on a device): pictures decoded, decoder fallbacks.</summary>
    public static long Pictures, Fallbacks;

    readonly int _maxWidth, _maxHeight;
    readonly string[] _candidates;
    int _next;
    MediaCodec? _codec;
    string _codecName = "";
    bool _hardware;
    readonly MediaCodec.BufferInfo _info = new();
    readonly Queue<DecodedPicture> _ready = new();
    readonly Stack<DecodedPicture> _free = new();
    readonly DecoderReplay _replay = new();
    int _skip;
    // SceAvcdec hands a picture out in the call that decoded it; MediaCodec decodes on its own threads, so each Send
    // waits for the picture of its unit (SynchronousOutput); IVideoDecoder.Asynchronous: a missed deadline can still
    // leave a call without its picture
    readonly SynchronousOutput _sync = new() { FirstDeadlineUs = s_firstDeadlineUs, PipelinedAfterStart = !s_waitEveryPicture };
    // VITASHARP_MEDIACODEC_SYNC=all: every access unit waits for its picture (default: only a stream's first one)
    static readonly bool s_waitEveryPicture = System.Environment.GetEnvironmentVariable(EnvVars.MediaCodecSync) == "all";
    // VITASHARP_MEDIACODEC_FIRST_US: the longest wait for a stream's first picture (A/B runs on a device)
    static readonly long s_firstDeadlineUs = long.TryParse(System.Environment.GetEnvironmentVariable(EnvVars.MediaCodecFirstUs), out long firstUs) && firstUs > 0
        ? firstUs : SynchronousOutput.FirstPictureDeadlineUs;
    // VITASHARP_MEDIACODEC_WARMUP=1: a new decoder decodes one synthetic picture of the largest size first, so the
    // codec's threads and output buffers exist before the game's first unit (A/B runs on a device)
    static readonly bool s_warmUp = System.Environment.GetEnvironmentVariable(EnvVars.MediaCodecWarmUp) == "1";
    // VITASHARP_MEDIACODEC_SYNC=0: the old timing (a 5 ms grace once a picture came out), for A/B runs on a device
    static readonly bool s_legacyOutputTiming = System.Environment.GetEnvironmentVariable(EnvVars.MediaCodecSync) == "0";
    const long LegacyOutputGraceUs = 5_000;
    // SPSs without a reorder limit get max_num_reorder_frames = 0 (H264ReorderLimit: Android's software decoder holds a
    // whole DPB back otherwise, 8 pictures for a Vita movie) until a B slice shows the stream reorders; the pictures of
    // a stream restarted then that were already handed out (pts up to this) are dropped
    bool _streamReorders, _reorderLimitLogged, _reorderLimitGiven;
    // the reorder depth the stream's SPS states (0 also when it states none and gets the limit): only a stream that does
    // not reorder gets its first picture drained out early (PrimeFirstPicture); the Vita gives a reordering stream's
    // first picture only after its reorder depth. VITASHARP_MEDIACODEC_PRIME=all drains every stream's first picture
    int _statedReorderFrames = -1;
    static readonly bool s_primeEveryStream = System.Environment.GetEnvironmentVariable(EnvVars.MediaCodecPrime) == "all";
    long _lastOutputPts = long.MinValue, _skipThroughPts = long.MinValue;
    // output format (raw-buffer path and crop)
    int _width, _height, _stride, _sliceHeight, _colorFormat, _cropX, _cropY;
    bool _fullRange, _bt709;

    public string Name => $"mediacodec-{_codecName}";
    public bool IsHardware => _hardware;
    public bool Asynchronous => true;

    MediaCodecVideoDecoder(string[] candidates, int maxWidth, int maxHeight)
    {
        _candidates = candidates;
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
    }

    public static MediaCodecVideoDecoder? Create(int maxWidth, int maxHeight)
    {
        // Android's own decoder first for pictures up to the Vita's screen (every Vita movie): it gives a picture back in
        // the call that decoded it, as the Vita does; the MediaTek hardware decoder holds several in flight, and the
        // players of F1 2011 and Uncharted pace themselves by the pictures' timestamps: F1's 5 s movie took 6.25 s
        // (one audio frame in four came out as silence), 5.6 s with the software decoder. VITASHARP_MEDIACODEC_SOFTWARE=0
        // puts the hardware decoders first again, =1 always the software one.
        string? sw = System.Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.MediaCodecSoftware);
        var names = H264Decoders(sw == "1" || (sw != "0" && (long)maxWidth * maxHeight <= 960 * 544));
        if (names.Length == 0)
        {
            Log.Warn("MediaCodec: no H.264 decoder on this device");
            return null;
        }
        var d = new MediaCodecVideoDecoder(names, maxWidth, maxHeight);
        if (!d.OpenNext())
            return null;
        if (s_warmUp)
        {
            d.WarmUp();
        }

        return d;
    }

    /// <summary>Decodes one synthetic picture of the decoder's largest size and drops it (see s_warmUp).</summary>
    void WarmUp()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int width = Math.Max(16, _maxWidth / 16 * 16);
        int height = Math.Max(16, _maxHeight / 16 * 16);
        Send(SyntheticH264.Stream(1, width, height)[0].AccessUnit, 0);
        Reset();
        _sends = 0;
        _sendTicks = _queueTicks = _waitTicks = _copyTicks = 0;
        Log.Info($"MediaCodec: {_codecName} warmed up with a {width}x{height} picture in {(System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms");
    }

    /// <summary>The device's H.264 decoders, hardware ones first (vendor names before Android's own c2.android/OMX.google).</summary>
    static string[] H264Decoders(bool softwareFirst)
    {
        var list = new List<(string Name, bool Software)>();
        try
        {
            foreach (var info in new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos() ?? [])
            {
                if (info.IsEncoder || info.Name is not { } name)
                    continue;
                if (!(info.GetSupportedTypes() ?? []).Any(t => string.Equals(t, Mime, StringComparison.OrdinalIgnoreCase)))
                    continue;
                bool software = OperatingSystem.IsAndroidVersionAtLeast(29) ? info.IsSoftwareOnly
                    : name.StartsWith("OMX.google.", StringComparison.Ordinal) || name.StartsWith("c2.android.", StringComparison.Ordinal);
                list.Add((name, software));
            }
        }
        catch (Java.Lang.Exception e)
        {
            Log.Warn($"MediaCodec: listing decoders failed: {e.Message}");
        }
        return list.OrderBy(c => c.Software != softwareFirst).Select(c => c.Name).ToArray();
    }

    bool OpenNext()
    {
        while (_next < _candidates.Length)
        {
            string name = _candidates[_next++];
            MediaCodec? codec = null;
            try
            {
                codec = MediaCodec.CreateByCodecName(name);
                var fmt = MediaFormat.CreateVideoFormat(Mime, _maxWidth, _maxHeight);
                fmt.SetInteger(MediaFormat.KeyColorFormat, YuvImageCopy.ColorFormatYuv420Flexible);
                // room for an uncompressed (I_PCM) picture: the warm-up picture
                fmt.SetInteger(MediaFormat.KeyMaxInputSize, Math.Max(_maxWidth * _maxHeight * 3 / 2 + 64 * 1024, 256 * 1024));
                fmt.SetInteger("priority", 0); // realtime
                bool lowLatency = false;
                if (OperatingSystem.IsAndroidVersionAtLeast(30)
                    && codec.CodecInfo.GetCapabilitiesForType(Mime) is { } caps && caps.IsFeatureSupported("low-latency")) // FEATURE_LowLatency
                {
                    fmt.SetInteger(MediaFormat.KeyLowLatency, 1);
                    lowLatency = true;
                }
                // SPS/PPS arrive in band with each IDR (Annex B), so no csd-0/csd-1 is set here
                codec.Configure(fmt, null, null, MediaCodecConfigFlags.None);
                codec.Start();
                _codec = codec;
                _codecName = name;
                _hardware = OperatingSystem.IsAndroidVersionAtLeast(29) ? codec.CodecInfo.IsHardwareAccelerated
                    : !(name.StartsWith("OMX.google.", StringComparison.Ordinal) || name.StartsWith("c2.android.", StringComparison.Ordinal));
                _width = _stride = _maxWidth;
                _height = _sliceHeight = _maxHeight;
                _cropX = _cropY = 0;
                _colorFormat = YuvImageCopy.ColorFormatYuv420Flexible;
                Log.Info($"MediaCodec: H.264 with {name} ({(_hardware ? "hardware" : "software")}{(lowLatency ? ", low latency" : "")})");
                return true;
            }
            catch (Java.Lang.Exception e)
            {
                Log.Warn($"MediaCodec: {name} could not be configured: {e.Message}");
                try
                {
                    codec?.Release();
                }
                catch (Java.Lang.Exception) { }
            }
        }
        return false;
    }

    void ReleaseCodec()
    {
        var c = _codec;
        _codec = null;
        if (c == null)
            return;
        try
        {
            c.Stop();
        }
        catch (Java.Lang.Exception) { }
        try
        {
            c.Release();
        }
        catch (Java.Lang.Exception) { }
        c.Dispose();
    }

    /// <summary>The decoder failed: the next one continues from the last IDR picture.</summary>
    void FallBack(string why)
    {
        Interlocked.Increment(ref Fallbacks);
        string failed = _codecName;
        ReleaseCodec();
        if (!OpenNext())
        {
            Log.Warn($"MediaCodec: {failed} failed ({why}) and no other H.264 decoder is left");
            return;
        }
        Log.Warn($"MediaCodec: {failed} failed ({why}); continuing with {_codecName} from the last IDR picture");
        var (units, skip) = _replay.TakeReplay();
        _skip = skip;
        _sync.Restart();
        foreach (var (data, pts) in units)
            Send(data, pts);
    }

    public void Send(ReadOnlySpan<byte> accessUnit, long pts)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            SendCore(accessUnit, pts);
        }
        finally
        {
            _sendTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _sends++;
        }
    }

    long _sendTicks, _queueTicks, _waitTicks, _copyTicks, _sends;

    void SendCore(ReadOnlySpan<byte> accessUnit, long pts)
    {
        if (_codec == null)
            return;
        if (!s_legacyOutputTiming && !_streamReorders && H264ReorderLimit.IsBSlice(accessUnit))
        {
            // F1 2011's movies have B frames and a reorder limit of their own: nothing to undo there
            _streamReorders = true;
            if (_reorderLimitGiven)
            {
                RestartWithoutReorderLimit();
                if (_codec == null)
                {
                    return;
                }
            }
        }
        _replay.Add(accessUnit, pts);
        try
        {
            // pictures finished since the last call first: a picture counted as held that came out after all
            // lowers SynchronousOutput.HeldPictures before this unit's wait
            Drain();
            long queueStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ReadOnlySpan<byte> unitToQueue = accessUnit;
            if (H264ReorderLimit.ContainsSps(accessUnit))
            {
                _statedReorderFrames = H264ReorderLimit.StatedReorderFrames(accessUnit) ?? 0;
            }

            if (!s_legacyOutputTiming && !_streamReorders && H264ReorderLimit.ContainsSps(accessUnit))
            {
                byte[] limited = H264ReorderLimit.Apply(accessUnit.ToArray(), 0);
                _reorderLimitGiven |= limited.Length != accessUnit.Length;
                if (!_reorderLimitLogged && limited.Length != accessUnit.Length)
                {
                    _reorderLimitLogged = true;
                    Log.Info($"MediaCodec: {_codecName}: the stream's SPS has no reorder limit: max_num_reorder_frames = 0 given to the decoder");
                }
                unitToQueue = limited;
            }
            bool queued = Queue(unitToQueue, pts, 0);
            _queueTicks += System.Diagnostics.Stopwatch.GetTimestamp() - queueStart;
            _sync.UnitQueued(queued && H264AnnexB.ContainsSlice(accessUnit));
            // the Vita answers this call with this unit's picture: wait for it (a software decode of a Vita-sized
            // picture takes ~13 ms), except for the pictures the decoder holds back on purpose
            long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (s_legacyOutputTiming)
            {
                Drain();
                if (_codec != null && _ready.Count == 0 && _sync.Outstanding > 0 && _sync.Started)
                {
                    Drain(LegacyOutputGraceUs);
                }
            }
            else if (queued && !_sync.Started && H264AnnexB.ContainsIdr(accessUnit)
                && (s_primeEveryStream || (_statedReorderFrames == 0 && !_streamReorders)))
            {
                // a stream's first picture: answered in this call whatever the decoder holds back
                PrimeFirstPicture(unitToQueue, pts);
            }
            else if (!_sync.Started && !s_waitEveryPicture && (_statedReorderFrames > 0 || _streamReorders))
            {
                // a reordering stream's first pictures come after its reorder depth: nothing to wait for yet
                Drain();
            }
            else
            {
                _sync.Await(PollOutput, MonotonicMicroseconds);
            }
            _waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - waitStart;
        }
        catch (Java.Lang.Exception e)
        {
            FallBack(e.Message ?? e.GetType().Name);
        }
    }

    /// <summary>
    /// A B slice: the stream reorders, so the reorder limit given to its SPS was wrong. The decoder restarts from the last
    /// IDR picture with the stream's own SPS (the decoder then holds the pictures it must reorder), and the pictures it
    /// gives out again up to the last one handed out are dropped.
    /// </summary>
    void RestartWithoutReorderLimit()
    {
        _streamReorders = true;
        _reorderLimitGiven = false;
        Log.Info($"MediaCodec: {_codecName}: the stream has B slices; decoding again from the last IDR picture with its own reorder depth");
        try
        {
            _codec!.Flush();
        }
        catch (Java.Lang.Exception e)
        {
            FallBack(e.Message ?? e.GetType().Name);
            return;
        }
        (IReadOnlyList<(byte[] Data, long Pts)> units, _) = _replay.TakeReplay();
        _sync.Restart();
        _skipThroughPts = _lastOutputPts;
        foreach ((byte[] data, long unitPts) in units)
        {
            SendCore(data, unitPts);
        }
    }

    /// <summary>
    /// A stream's first IDR unit: Android's software decoder (libavc) holds the first pictures
    /// until more units arrive (2 for a 64x64 stream, 6 to 8 calls for F1 2011's 960x544 movies), with or without a
    /// reorder limit, where the Vita's decoder answers the call that starts a stream with its picture; without one the
    /// game showed an empty buffer or, from the HLE, the previous movie's last picture (black and old frames at every
    /// movie start on the phone). An end-of-stream buffer makes the decoder give out what it holds; a flush then takes
    /// it back to decoding, and the same unit goes in again as the reference for what follows (its second picture is
    /// dropped by pts). The calls after it repeat this picture (SceAvcdec) until the decoder's own pictures come.
    /// </summary>
    void PrimeFirstPicture(ReadOnlySpan<byte> unit, long pts)
    {
        long start = MonotonicMicroseconds();
        MediaCodec codec = _codec!;
        if (!Queue([], 0, MediaCodecBufferFlags.EndOfStream))
        {
            return;
        }

        while (MonotonicMicroseconds() - start < PrimeTimeoutUs)
        {
            if (Drain(10_000))
            {
                break;
            }
        }
        codec.Flush();
        _sync.Restart();
        if (_ready.Count == 0)
        {
            Log.WarnOnce("mediacodec-prime", $"MediaCodec: {_codecName}: no picture even at the end of the stream; the first calls stay without one");
            Queue(unit, pts, 0);
            _sync.UnitQueued(true);
            return;
        }

        _sync.MarkStarted();
        _skipThroughPts = _lastOutputPts;
        if (Queue(unit, pts, 0))
        {
            _sync.UnitQueued(true);
        }

        _primes++;
        if (_primes == 1)
        {
            Log.Info($"MediaCodec: {_codecName}: first picture given out by an end-of-stream drain in {(MonotonicMicroseconds() - start) / 1000.0:F1} ms");
        }
    }

    /// <summary>Longest wait for the pictures an end-of-stream drain gives out (PrimeFirstPicture).</summary>
    const long PrimeTimeoutUs = 1_000_000;
    long _primes;

    static long MonotonicMicroseconds() => System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;

    void PollOutput(long timeoutUs)
    {
        if (_codec != null)
        {
            Drain(timeoutUs);
        }
    }

    /// <summary>Queues one input buffer; false when no input buffer came free (the unit is dropped).</summary>
    bool Queue(ReadOnlySpan<byte> data, long pts, MediaCodecBufferFlags flags)
    {
        var c = _codec!;
        for (int attempt = 0; attempt < InputAttempts; attempt++)
        {
            int idx = c.DequeueInputBuffer(InputTimeoutUs);
            if (idx < 0)
            {
                Drain();
                continue;
            } // every input buffer is held: make room by taking pictures out
            using var buf = c.GetInputBuffer(idx)!;
            int n = Math.Min(data.Length, buf.Capacity());
            if (n < data.Length)
                Log.WarnOnce("mediacodec-in-size", $"MediaCodec: access unit of {data.Length} bytes truncated to {n}");
            data[..n].CopyTo(new Span<byte>((void*)buf.GetDirectBufferAddress(), n));
            c.QueueInputBuffer(idx, 0, n, Math.Max(0, pts), flags);
            return true;
        }
        Log.WarnOnce("mediacodec-in", "MediaCodec: no input buffer available, access unit dropped");
        return false;
    }

    /// <summary>Takes every finished picture out; true once the end-of-stream buffer came out.</summary>
    bool Drain(long timeoutUs = 0)
    {
        var c = _codec!;
        while (true)
        {
            int idx = c.DequeueOutputBuffer(_info, timeoutUs);
            if (idx >= 0)
                timeoutUs = 0; // waited for one buffer; the rest only if already there
            if (idx == InfoOutputFormatChanged)
            {
                ReadOutputFormat(c.OutputFormat);
                continue;
            }
            if (idx == InfoOutputBuffersChanged)
                continue;
            if (idx < 0)
                return false; // InfoTryAgainLater
            bool eos = (_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
            if (_info.Size > 0)
            {
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var p = Output(c, idx);
                _copyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
                if (p != null)
                {
                    Interlocked.Increment(ref Pictures);
                    _sync.PictureOut();
                    _replay.Produced();
                    if (_skip > 0)
                    {
                        _skip--;
                        Recycle(p);
                    }
                    else if (_skipThroughPts != long.MinValue && p.Pts <= _skipThroughPts)
                    {
                        Recycle(p);
                    }
                    else
                    {
                        _skipThroughPts = long.MinValue;
                        _lastOutputPts = p.Pts;
                        _ready.Enqueue(p);
                    }
                }
            }
            c.ReleaseOutputBuffer(idx, false);
            if (eos)
                return true;
        }
    }

    void ReadOutputFormat(MediaFormat f)
    {
        int Get(string k, int d) => f.ContainsKey(k) ? f.GetInteger(k) : d;
        _width = Get(MediaFormat.KeyWidth, _width);
        _height = Get(MediaFormat.KeyHeight, _height);
        _stride = Math.Max(_width, Get(MediaFormat.KeyStride, _width));
        _sliceHeight = Math.Max(_height, Get(MediaFormat.KeySliceHeight, _height));
        _colorFormat = Get(MediaFormat.KeyColorFormat, _colorFormat);
        // crop-left/top/right/bottom (inclusive), present when the coded size is larger than the picture
        if (f.ContainsKey("crop-left") && f.ContainsKey("crop-right") && f.ContainsKey("crop-top") && f.ContainsKey("crop-bottom"))
        {
            _cropX = f.GetInteger("crop-left");
            _cropY = f.GetInteger("crop-top");
            _width = f.GetInteger("crop-right") - _cropX + 1;
            _height = f.GetInteger("crop-bottom") - _cropY + 1;
        }
        _fullRange = Get(MediaFormat.KeyColorRange, 2) == 1;                 // COLOR_RANGE_FULL = 1, LIMITED = 2
        _bt709 = Get(MediaFormat.KeyColorStandard, 0) == 1;                   // COLOR_STANDARD_BT709 = 1
        Log.Info($"MediaCodec: {_codecName} output {_width}x{_height} (stride {_stride}, slice height {_sliceHeight}, crop {_cropX},{_cropY}), " +
                 $"colour format 0x{_colorFormat:X}, {(_fullRange ? "full" : "limited")} range{(_bt709 ? ", BT.709" : "")}");
    }

    DecodedPicture Rent()
    {
        var p = _free.Count > 0 ? _free.Pop() : new DecodedPicture();
        p.Pts = _info.PresentationTimeUs;
        p.FullRange = _fullRange;
        p.Bt709 = _bt709;
        return p;
    }

    DecodedPicture? Output(MediaCodec c, int idx)
    {
        // YUV_420_888 image: works for every layout the decoder describes with strides
        using (var image = c.GetOutputImage(idx))
        {
            if (image != null && image.GetPlanes() is { Length: 3 } planes)
            {
                var crop = image.CropRect;
                int cx = crop?.Left ?? 0, cy = crop?.Top ?? 0;
                int w = crop is { } r && r.Width() > 0 ? r.Width() : image.Width;
                int h = crop is { } r2 && r2.Height() > 0 ? r2.Height() : image.Height;
                var p = Rent();
                YuvImageCopy.Copy(Plane(planes[0]), Plane(planes[1]), Plane(planes[2]), cx, cy, w, h, p);
                foreach (var pl in planes)
                    pl.Dispose();
                return p;
            }
        }
        // no image (decoders without flexible YUV): the raw buffer in a standard layout
        using var buf = c.GetOutputBuffer(idx);
        if (buf == null)
            return null;
        var data = new ReadOnlySpan<byte>((byte*)buf.GetDirectBufferAddress() + _info.Offset, _info.Size);
        var pic = Rent();
        if (YuvImageCopy.CopyRaw(data, _colorFormat, _stride, _sliceHeight, _cropX, _cropY, _width, _height, pic))
            return pic;
        Log.WarnOnce($"mediacodec-cf{_colorFormat}", $"MediaCodec: {_codecName} gives no image and an unknown colour format 0x{_colorFormat:X}: pictures dropped");
        _free.Push(pic);
        return null;
    }

    static YuvImageCopy.Plane Plane(global::Android.Media.Image.Plane p)
    {
        var b = p.Buffer!;
        // the plane's buffer starts at its first sample; its limit covers the last one
        var span = new ReadOnlySpan<byte>((byte*)b.GetDirectBufferAddress() + b.Position(), b.Remaining());
        return new YuvImageCopy.Plane(span, p.RowStride, p.PixelStride);
    }

    public void Flush()
    {
        if (_codec == null)
            return;
        try
        {
            Queue([], 0, MediaCodecBufferFlags.EndOfStream);
            // pictures held for reordering come out before the end-of-stream buffer
            for (int i = 0; i < 50; i++)
                if (Drain(10_000))
                    break;
            _codec?.Flush(); // accepts a new stream afterwards
        }
        catch (Java.Lang.Exception e)
        {
            FallBack(e.Message ?? e.GetType().Name);
        }
        _replay.Clear();
        _sync.Restart();
    }

    public void Reset()
    {
        try
        {
            _codec?.Flush();
        }
        catch (Java.Lang.Exception e)
        {
            FallBack(e.Message ?? e.GetType().Name);
        }
        while (_ready.Count > 0)
            Recycle(_ready.Dequeue());
        _replay.Clear();
        _sync.Restart();
        _skip = 0;
        _skipThroughPts = long.MinValue;
        _lastOutputPts = long.MinValue;
    }

    public DecodedPicture? Take() => _ready.Count > 0 ? _ready.Dequeue() : null;

    public void Recycle(DecodedPicture picture)
    {
        if (_free.Count < 4)
            _free.Push(picture);
    }

    public void Dispose()
    {
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency, n = Math.Max(1, _sends);
        if (_sends > 0)
        {
            Log.Info($"MediaCodec: {_codecName}: {_sends} units, per unit {_sendTicks * f / n:F1} ms in Send (queue {_queueTicks * f / n:F1}, output waits {_waitTicks * f / n:F1}, picture copies {_copyTicks * f / n:F1}), " +
                     $"{_sync.MissedDeadlines} picture deadlines missed, {_sync.HeldPictures} pictures held by the decoder, {_primes} first pictures drained out");
        }
        ReleaseCodec();
        _info.Dispose();
    }
}
