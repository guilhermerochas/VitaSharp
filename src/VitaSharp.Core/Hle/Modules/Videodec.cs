using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceVideodec / SceAvcdec: H.264 decoding of access units into game buffers (YUV420 planar, YUV420 packed or
/// RGBA/BGRA with colour-space conversion). Layouts: SceAvcdecCtrl {handle, frameBuf{p,size}} 12 bytes,
/// SceAvcdecAu {pts, dts, es{p,size}} 24 bytes, SceAvcdecArrayPicture {numOfOutput, numOfElm, pPicture} and
/// SceAvcdecPicture {size, frame (0x40 bytes, pPicture[2] at +0x38), info (pts at +0x58)} 100 bytes.
/// </summary>
public sealed partial class Videodec : HleModule
{
    const int ERR_INVALID_PARAM = unchecked((int)0x80620002), ERR_INVALID_POINTER = unchecked((int)0x80620009);
    const uint TYPE_HW_AVCDEC = 0x1001;
    const uint PIXEL_RGBA8888 = 0x0, PIXEL_BGRA8888 = 0x8, PIXEL_YUV420_RASTER = 0x10, PIXEL_YUV420_PACKED_RASTER = 0x20;

    sealed class Decoder
    {
        public IVideoDecoder? Codec;
        public uint MaxWidth, MaxHeight;
        public DecodedPicture? Last;
        /// <summary>A picture came out since the decoder was created or flushed (see Output's repeatIfLate).</summary>
        public bool Started;
        /// <summary>Pictures of the decoder's own handed out since it was created or flushed (repeats not counted).</summary>
        public int Delivered;
        /// <summary>The stream reorders its pictures (an SPS states max_num_reorder_frames above 0, or B slices): the Vita
        /// answers the first calls of such a stream with no picture.</summary>
        public bool StreamReorders;
        /// <summary>Scratch list of an asynchronous decoder's ready pictures (Output).</summary>
        public readonly List<DecodedPicture> Ready = [];
        /// <summary>The last access unit's pts, and whether the stream sent one out of order (B-frames).</summary>
        public long AuPts = long.MinValue;
        public bool AuReordered;
        /// <summary>Statistics, logged when the decoder is deleted: calls, pictures repeated, calls with no picture,
        /// the slowest call (Stopwatch ticks).</summary>
        public long Calls, Repeats, Empty, MaxCallTicks, Dropped, CarriedOver, CallTicks, FirstCall, LastCall;
        /// <summary>The stream's last SPS and PPS (Annex B), kept for a decoder recreated by a save state load: players send
        /// them once at the start of the movie, and a new decoder without them cannot decode the units that follow.</summary>
        public byte[]? ParamSets;
        public bool ResendParamSets;
        /// <summary>The access units since the last IDR picture (one growing buffer, reused), so a save state can rebuild
        /// the decoder's reference pictures: a new decoder fed only the units after the load has no references for them
        /// until the next IDR, and a movie loaded between two IDRs froze for seconds (Ninja Gaiden Sigma 2 Plus's intro,
        /// ~3.5 s). <see cref="ReplayValid"/> is false while the GOP is longer than <see cref="MaxReplayBytes"/>.</summary>
        public byte[] Replay = [];
        public int ReplayBytes;
        public readonly List<(int Offset, int Length, long Pts)> ReplayUnits = [];
        public bool ReplayValid;
        /// <summary>The pts of the last picture taken from the codec; after a replay, the pictures up to
        /// <see cref="SkipThroughPts"/> (the last one taken before the save) are ones the game already had and are
        /// dropped. By pts, not by count: with B-frames the decoder holds pictures back, so a count of the units or of the
        /// pictures out since the IDR is off by the reorder delay (and dropped new pictures stalled the game's player).</summary>
        public long LastTakenPts = long.MinValue, SkipThroughPts = long.MinValue;

        public void Remember(ReadOnlySpan<byte> unit, long pts)
        {
            if (StartsIdr(unit)) { ReplayBytes = 0; ReplayUnits.Clear(); ReplayValid = true; }
            if (!ReplayValid) return;
            if (ReplayBytes + unit.Length > MaxReplayBytes) { ReplayValid = false; ReplayUnits.Clear(); ReplayBytes = 0; return; }
            if (Replay.Length < ReplayBytes + unit.Length) Array.Resize(ref Replay, Math.Max(ReplayBytes + unit.Length, Math.Max(256 << 10, Replay.Length * 2)));
            unit.CopyTo(Replay.AsSpan(ReplayBytes));
            ReplayUnits.Add((ReplayBytes, unit.Length, pts));
            ReplayBytes += unit.Length;
        }

        public void Forget() { ReplayBytes = 0; ReplayUnits.Clear(); ReplayValid = false; SkipThroughPts = long.MinValue; }

        /// <summary>A picture out of the codec, or null: the pictures a replay produces again are dropped.</summary>
        public DecodedPicture? Take()
        {
            while (Codec?.Take() is { } p)
            {
                if (SkipThroughPts != long.MinValue)
                {
                    if (p.Pts <= SkipThroughPts) { Codec.Recycle(p); continue; }
                    SkipThroughPts = long.MinValue;
                }
                LastTakenPts = p.Pts;
                return p;
            }
            return null;
        }
    }

    /// <summary>Upper bound on the access units kept for a save state's replay (per decoder).</summary>
    const int MaxReplayBytes = 32 << 20;

    /// <summary>
    /// An asynchronous decoder (MediaCodec) hands its pictures out one or more calls after their access unit, or repeats the
    /// previous one (Output). The Vita's decoder answers each call with the picture of the unit it was given, and movie
    /// players compare that pts with their clock: a lagging or repeated old pts made F1 2011's player skip its picture
    /// draw now and then (one-frame black flashes in the movies on a phone). So each picture an asynchronous decoder hands
    /// out carries the pts of the unit of that call, as long as the units come in pts order (no B-frames).
    /// VITASHARP_AVCDEC_PTS=picture keeps the decoded picture's own pts.
    /// </summary>
    /// <summary>Pictures of an asynchronous decoder that may wait for the next calls (beyond the call's capacity).</summary>
    const int MaxBacklog = 4;

    static readonly bool StampUnitPts = Environment.GetEnvironmentVariable(EnvVars.AvcdecPts) != "picture";

    /// <summary>VITASHARP_AVCDEC_FILL_REPEAT=1: repeat the first picture while an asynchronous decoder fills up (the
    /// earlier behaviour); default: those calls give no picture, as on the Vita.</summary>
    static readonly bool s_fillRepeat = Environment.GetEnvironmentVariable(EnvVars.AvcdecFillRepeat) == "1";

    /// <summary>VITASHARP_AVCDEC_CARRYOVER=1: a new or flushed asynchronous decoder answers its first calls with the last
    /// picture of the previous decoder or pass (the earlier behaviour); default: no picture, as on the Vita, which never
    /// hands out a picture of another stream (the old frame at movie starts on the phone).</summary>
    static readonly bool s_carryOver = Environment.GetEnvironmentVariable(EnvVars.AvcdecCarryOver) == "1";

    readonly Dictionary<uint, Decoder> _decoders = new();

    /// <summary>
    /// The last picture of the last deleted asynchronous decoder. A new MediaCodec decoder needs several calls (about 0.4 s on
    /// the MediaTek one) before its first picture; the Vita's answers the first call. Games that recreate the decoder to
    /// loop a movie (Uncharted's title) showed a stale picture buffer then, a one-frame flash of an older scene: until the
    /// new decoder's first picture, calls answer with this one; a decoder flushed to loop a movie (Uncharted's title keeps
    /// its decoder) answers with its own last picture until the first one of the new pass.
    /// </summary>
    DecodedPicture? _carryOver;
    uint _nextHandle = 1;

    [Hle("sceVideodecInitLibraryInternal")]
    [Hle("_sceVideodecInitLibraryInternal")]
    [Hle("sceVideodecInitLibraryNongameapp")]
    [Hle("_sceVideodecInitLibraryNongameapp")]
    [Hle("_sceVideodecInitLibrary")]
    [Hle] int sceVideodecInitLibrary(uint codecType, uint initInfo) => codecType == TYPE_HW_AVCDEC ? 0 : ERR_INVALID_PARAM;
    [Hle("sceVideodecInitLibraryWithUnmapMemInternal")]
    [Hle("_sceVideodecInitLibraryWithUnmapMemInternal")]
    [Hle("sceVideodecInitLibraryWithUnmapMemNongameapp")]
    [Hle("_sceVideodecInitLibraryWithUnmapMemNongameapp")]
    [Hle("_sceVideodecInitLibraryWithUnmapMem")]
    [Hle] int sceVideodecInitLibraryWithUnmapMem(uint codecType, uint initInfo, uint memInfo) => codecType == TYPE_HW_AVCDEC ? 0 : ERR_INVALID_PARAM;
    [Hle("_sceVideodecTermLibrary")]
    [Hle] int sceVideodecTermLibrary(uint codecType) => 0;

    /// <summary>Memory the codec library needs for its own state; the decoding happens host-side.</summary>
    [Hle("sceVideodecQueryMemSizeInternal")]
    [Hle("_sceVideodecQueryMemSizeInternal")]
    [Hle("sceVideodecQueryMemSizeNongameapp")]
    [Hle("_sceVideodecQueryMemSizeNongameapp")]
    [Hle("_sceVideodecQueryMemSize")]
    [Hle]
    int sceVideodecQueryMemSize(uint codecType, uint initInfo, uint memInfo)
    {
        if (codecType != TYPE_HW_AVCDEC) return ERR_INVALID_PARAM;
        if (memInfo == 0) return ERR_INVALID_POINTER;
        GuestMemory.W32(memInfo, 0x40000);
        return 0;
    }

    static bool ValidQuery(uint query, out uint w, out uint h, out uint refs)
    {
        w = GuestMemory.R32(query); h = GuestMemory.R32(query + 4); refs = GuestMemory.R32(query + 8);
        return w <= 1920 && h <= 1088 && w * h <= 1920 * 1088 && refs <= 16;
    }

    [Hle("sceAvcdecQueryDecoderMemSizeInternal")]
    [Hle("_sceAvcdecQueryDecoderMemSizeInternal")]
    [Hle("sceAvcdecQueryDecoderMemSizeNongameapp")]
    [Hle("_sceAvcdecQueryDecoderMemSizeNongameapp")]
    [Hle("_sceAvcdecQueryDecoderMemSize")]
    [Hle]
    int sceAvcdecQueryDecoderMemSize(uint codecType, uint query, uint info)
    {
        if (codecType != TYPE_HW_AVCDEC) return ERR_INVALID_PARAM;
        if (query == 0 || info == 0) return ERR_INVALID_POINTER;
        if (!ValidQuery(query, out uint w, out uint h, out uint refs)) return ERR_INVALID_PARAM;
        uint frame = w * h * 3 / 2;
        GuestMemory.W32(info, (frame * Math.Max(refs, 1u) + 0x3FFFF) & ~0x3FFFFu);
        return 0;
    }

    [Hle("sceAvcdecCreateDecoderInternal")]
    [Hle("_sceAvcdecCreateDecoderInternal")]
    [Hle("sceAvcdecCreateDecoderNongameapp")]
    [Hle("_sceAvcdecCreateDecoderNongameapp")]
    [Hle("_sceAvcdecCreateDecoder")]
    [Hle]
    int sceAvcdecCreateDecoder(uint codecType, uint ctrl, uint query)
    {
        if (codecType != TYPE_HW_AVCDEC) return ERR_INVALID_PARAM;
        if (ctrl == 0 || query == 0) return ERR_INVALID_POINTER;
        if (!ValidQuery(query, out uint w, out uint h, out _)) return ERR_INVALID_PARAM;
        var d = new Decoder { Codec = VideoDecoders.CreateH264(new VideoDecoderOptions((int)w, (int)h)), MaxWidth = w, MaxHeight = h };
        if (d.Codec == null) Log.WarnOnce("avcdec-noffmpeg", "sceAvcdec: FFmpeg is unavailable, videos will be black");
        lock (_decoders)
        {
            uint handle = _nextHandle++;
            _decoders[handle] = d;
            GuestMemory.W32(ctrl, handle);
        }
        return 0;
    }

    Decoder? Find(uint ctrl, out int error)
    {
        error = 0;
        if (ctrl == 0) { error = ERR_INVALID_POINTER; return null; }
        lock (_decoders)
            if (_decoders.TryGetValue(GuestMemory.R32(ctrl), out var d)) return d;
        error = ERR_INVALID_PARAM;
        return null;
    }

    [Hle("_sceAvcdecDecode")]
    [Hle]
    int sceAvcdecDecode(uint ctrl, uint au, uint array)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        if (au == 0 || array == 0) return ERR_INVALID_POINTER;
        long pts = ((long)GuestMemory.R32(au) << 32) | GuestMemory.R32(au + 4);
        uint es = GuestMemory.R32(au + 16), size = GuestMemory.R32(au + 20);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long? stamp = null;
        if (d.Codec != null && es != 0 && size != 0)
        {
            var unit = GuestMemory.Span(es, (int)size);
            if (ParameterSets(unit) is { } ps) d.ParamSets = ps;
            if (H264ReorderLimit.StatedReorderFrames(unit) > 0 || H264ReorderLimit.IsBSlice(unit))
            {
                d.StreamReorders = true;
            }

            else if (d.ResendParamSets && d.ParamSets != null) d.Codec.Send(d.ParamSets, pts);
            d.ResendParamSets = false;
            d.Remember(unit, pts);
            d.Codec.Send(unit, pts);
            if (d.AuPts != long.MinValue && pts < d.AuPts) d.AuReordered = true;
            d.AuPts = pts;
            if (StampUnitPts && d.Codec.Asynchronous && !d.AuReordered) stamp = pts;
        }
        int r = Output(d, array, repeatIfLate: true, stamp);
        d.Calls++;
        d.MaxCallTicks = Math.Max(d.MaxCallTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        d.CallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (d.FirstCall == 0) d.FirstCall = t0;
        d.LastCall = t0;
        return r;
    }

    /// <summary>True when the first slice of an Annex B access unit is an IDR slice (NAL type 5): only the NAL headers
    /// up to that slice are looked at, not the slice data.</summary>
    internal static bool StartsIdr(ReadOnlySpan<byte> au)
    {
        int i = 0;
        while (true)
        {
            int start = au[i..].IndexOf(StartCode);
            if (start < 0) return false;
            int hdr = i + start + 3;
            if (hdr >= au.Length) return false;
            int type = au[hdr] & 0x1F;
            if (type == 5) return true;
            if (type == 1) return false;
            i = hdr;
        }
    }

    static ReadOnlySpan<byte> StartCode => [0, 0, 1];

    /// <summary>The SPS and PPS NAL units before the first slice of an Annex B access unit (with start codes), or null.</summary>
    internal static byte[]? ParameterSets(ReadOnlySpan<byte> au)
    {
        List<byte>? ps = null;
        int i = 0;
        while (true)
        {
            int start = au[i..].IndexOf(new byte[] { 0, 0, 1 });
            if (start < 0) break;
            int hdr = i + start + 3;
            if (hdr >= au.Length) break;
            int type = au[hdr] & 0x1F;
            if (type is 1 or 5) break; // a slice: the parameter sets come before it
            int next = au[hdr..].IndexOf(new byte[] { 0, 0, 1 });
            int end = next < 0 ? au.Length : hdr + next;
            if (next >= 0 && end > hdr && au[end - 1] == 0) end--; // the next start code's leading zero
            if (type is 7 or 8) { ps ??= []; ps.AddRange(new byte[] { 0, 0, 0, 1 }); ps.AddRange(au[hdr..end].ToArray()); }
            if (next < 0) break;
            i = hdr + next;
        }
        return ps?.ToArray();
    }

    /// <summary>Writes every ready picture into the caller's picture array. <paramref name="repeatIfLate"/>: an
    /// asynchronous decoder (<see cref="IVideoDecoder.Asynchronous"/>) whose picture is not out yet, after the stream's
    /// first picture, hands out the previous picture again.</summary>
    int Output(Decoder d, uint array, bool repeatIfLate = false, long? stampPts = null)
    {
        uint capacity = GuestMemory.R32(array + 4), list = GuestMemory.R32(array + 8);
        if (capacity != 1) stampPts = null; // several pictures per call keep their own pts
        uint n = 0;
        bool Emit(DecodedPicture pic)
        {
            uint picture = GuestMemory.R32(list + n * 4);
            if (picture == 0) return false;
            WritePicture(picture, pic, stampPts);
            if (d.Last != null && d.Last != pic) d.Codec!.Recycle(d.Last); // its planes were already written out
            d.Last = pic;
            d.Started = true;
            d.Delivered++;
            n++;
            return true;
        }
        if (repeatIfLate && d.Codec is { Asynchronous: true } codec)
        {
            // pictures of an asynchronous decoder come out late and in bursts (the MediaTek decoder hands two out every
            // other call): they wait in a short queue and go out one per call in order. Showing only the newest of a burst
            // and repeating it on the next call showed every other picture of F1 2011's movies twice. The queue is capped
            // (oldest dropped) so the delay cannot grow.
            while (d.Take() is { } p) d.Ready.Add(p);
            int keep = (int)capacity + MaxBacklog;
            while (d.Ready.Count > keep) { codec.Recycle(d.Ready[0]); d.Ready.RemoveAt(0); d.Dropped++; }
            while (n < capacity && d.Ready.Count > 0)
            {
                var p = d.Ready[0];
                d.Ready.RemoveAt(0);
                if (!Emit(p)) { codec.Recycle(p); break; }
            }
        }
        else
        {
            while (n < capacity && d.Ready.Count > 0) // pictures still queued from earlier calls first
            {
                var q = d.Ready[0];
                d.Ready.RemoveAt(0);
                if (!Emit(q)) { d.Codec?.Recycle(q); break; }
            }
            while (n < capacity && d.Take() is { } pic)
                if (!Emit(pic)) break;
        }
        // a late picture is answered with the previous one (an empty buffer would show), except while the decoder fills
        // up at the start of a stream that reorders: the Vita answers those calls with no picture (a stream with B-frames
        // gets its first one after its reorder depth), and repeating the first picture there showed as flicker at every
        // movie start of F1 2011 on the phone (its movies state a reorder depth of 2)
        bool repeatAllowed = d.Delivered >= 2 || !d.StreamReorders || s_fillRepeat;
        if (n == 0 && repeatIfLate && repeatAllowed && d.Started && d.Last != null && d.Codec is { Asynchronous: true } && capacity > 0 && GuestMemory.R32(list) is var first && first != 0)
        {
            WritePicture(first, d.Last, stampPts);
            d.Repeats++;
            n = 1;
        }
        if (n == 0 && repeatIfLate && s_carryOver && !d.Started && (d.Last ?? _carryOver) is { } carried && d.Codec is { Asynchronous: true } && capacity > 0
            && GuestMemory.R32(list) is var firstPic && firstPic != 0)
        {
            WritePicture(firstPic, carried, stampPts);
            d.CarriedOver++;
            n = 1;
        }
        if (n == 0 && repeatIfLate) d.Empty++;
        GuestMemory.W32(array, n);
        return 0;
    }

    [Hle("_sceAvcdecDecodeStop")]
    [Hle]
    int sceAvcdecDecodeStop(uint ctrl, uint array)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        d.Codec?.Flush();
        d.Forget();
        d.Started = false;
        d.Delivered = 0;
        d.AuPts = long.MinValue; d.AuReordered = false;
        return array == 0 ? 0 : Output(d, array);
    }

    [Hle("_sceAvcdecDecodeFlush")]
    [Hle]
    int sceAvcdecDecodeFlush(uint ctrl)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        foreach (var q in d.Ready) d.Codec?.Recycle(q);
        d.Ready.Clear();
        d.Codec?.Reset();
        d.Forget();
        d.Started = false;
        d.Delivered = 0;
        d.AuPts = long.MinValue; d.AuReordered = false;
        return 0;
    }

    [Hle("_sceAvcdecDecodeAvailableSize")]
    [Hle]
    int sceAvcdecDecodeAvailableSize(uint ctrl)
    {
        var d = Find(ctrl, out int err);
        return d == null ? err : (int)(d.MaxWidth * d.MaxHeight * 3 / 2);
    }

    [Hle("_sceAvcdecDeleteDecoder")]
    [Hle]
    int sceAvcdecDeleteDecoder(uint ctrl)
    {
        if (ctrl == 0) return ERR_INVALID_POINTER;
        Decoder? d;
        lock (_decoders)
            if (!_decoders.Remove(GuestMemory.R32(ctrl), out d)) return ERR_INVALID_PARAM;
        if (d.Calls > 0)
            Log.Info($"sceAvcdec: decoder {d.Codec?.Name ?? "none"} deleted after {d.Calls} decode calls, {d.Repeats} pictures repeated, {d.Dropped} dropped, {d.Empty} calls without a picture ({d.CarriedOver} answered with the previous decoder's last picture), " +
                     $"{d.CallTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, d.Calls):F1} ms per call over {(d.LastCall - d.FirstCall) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms, " +
                     $"slowest call {d.MaxCallTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms{(d.AuReordered ? ", units out of pts order" : "")}");
        if (d.Codec is { Asynchronous: true } && d.Last != null) _carryOver = d.Last; // kept out of the closed decoder's pool
        d.Codec?.Dispose();
        return 0;
    }

    static void WritePicture(uint picture, DecodedPicture p, long? stampPts = null)
    {
        uint frame = picture + 4;
        uint pixelType = GuestMemory.R32(frame);
        uint pitch = GuestMemory.R32(frame + 4), bufW = GuestMemory.R32(frame + 8), bufH = GuestMemory.R32(frame + 12);
        uint dst = GuestMemory.R32(frame + 0x38);
        if (pitch == 0) pitch = bufW;
        Log.DebugOnce($"avcdec-pic-{pixelType}-{pitch}-{bufH}-{p.Width}x{p.Height}", $"sceAvcdec picture: pixelType 0x{pixelType:X} pitch {pitch} buffer {bufW}x{bufH} decoded {p.Width}x{p.Height}");
        int w = (int)Math.Min((uint)p.Width, pitch), h = (int)Math.Min((uint)p.Height, bufH == 0 ? (uint)p.Height : bufH);
        GuestMemory.W32(frame + 16, (uint)p.Width);   // horizontalSize
        GuestMemory.W32(frame + 20, (uint)p.Height);  // verticalSize
        for (uint o = 24; o < 40; o += 4) GuestMemory.W32(frame + o, 0); // no cropping
        // info.pts (upper, lower)
        long outPts = stampPts ?? p.Pts;
        GuestMemory.W32(picture + 0x5C, (uint)(outPts >> 32));
        GuestMemory.W32(picture + 0x60, (uint)outPts);
        if (dst == 0) return;
        int cw = (p.Width + 1) / 2;
        switch (pixelType & 0x38)
        {
            case PIXEL_YUV420_RASTER:
            {
                // planar: Y (pitch x height), then U and V (pitch/2 x height/2)
                uint uOff = pitch * bufH, vOff = uOff + pitch / 2 * (bufH / 2);
                for (int y = 0; y < h; y++) FastCopy.Copy(p.Y.AsSpan(y * p.Width, w), GuestMemory.Span(dst + (uint)y * pitch, w));
                for (int y = 0; y < h / 2; y++)
                {
                    FastCopy.Copy(p.U.AsSpan(y * cw, w / 2), GuestMemory.Span(dst + uOff + (uint)y * (pitch / 2), w / 2));
                    FastCopy.Copy(p.V.AsSpan(y * cw, w / 2), GuestMemory.Span(dst + vOff + (uint)y * (pitch / 2), w / 2));
                }
                break;
            }
            case PIXEL_YUV420_PACKED_RASTER:
            {
                uint uvOff = pitch * bufH;
                for (int y = 0; y < h; y++) FastCopy.Copy(p.Y.AsSpan(y * p.Width, w), GuestMemory.Span(dst + (uint)y * pitch, w));
                for (int y = 0; y < h / 2; y++)
                {
                    var row = GuestMemory.Span(dst + uvOff + (uint)y * pitch, w);
                    // YVU420P2: V before U (games sample it as SCE_GXM_TEXTURE_FORMAT_YVU420P2_CSC1)
                    for (int x = 0; x < w / 2; x++) { row[2 * x] = p.V[y * cw + x]; row[2 * x + 1] = p.U[y * cw + x]; }
                }
                break;
            }
            default:
            {
                bool bgra = (pixelType & 0x38) == PIXEL_BGRA8888;
                byte alpha = GuestMemory.Read<byte>(frame + 0x28);
                byte csc = GuestMemory.Read<byte>(frame + 0x29);
                bool bt709 = csc is 2 or 4 || (csc == 0 && p.Bt709);
                bool full = csc is 3 or 4 || (csc == 0 && p.FullRange);
                ToRgb(p, dst, pitch, w, h, bgra, alpha, bt709, full);
                break;
            }
        }
        PadToMacroblocks(dst, pixelType & 0x38, pitch, bufH, w, h);
        // kept for textures sampled after the next picture overwrote it (Gpu/DecoderOutputs.cs)
        if (VitaSharp.Core.Gpu.DecoderOutputs.Enabled && bufH != 0)
        {
            uint bytes = (pixelType & 0x38) is PIXEL_YUV420_RASTER or PIXEL_YUV420_PACKED_RASTER ? pitch * bufH * 3 / 2 : pitch * 4 * bufH;
            VitaSharp.Core.Gpu.DecoderOutputs.Record(dst, (uint)p.Width, (uint)p.Height, GuestMemory.ReadSpan(dst, (int)bytes), pitch, bufH);
        }
    }

    /// <summary>
    /// The hardware decoder writes whole macroblock rows (a 960x540 movie fills 544 rows), FFmpeg returns the picture
    /// cropped: the rows up to the next multiple of 16 (within the buffer) repeat the last row, as the encoder's
    /// padding does. Games that sample the whole 16-aligned buffer showed the unwritten rows (Shinobido 2: a green line
    /// under the movie, YUV 0,0,0).
    /// </summary>
    static void PadToMacroblocks(uint dst, uint layout, uint pitch, uint bufH, int w, int h)
    {
        int padded = Math.Min((h + 15) & ~15, (int)bufH);
        if (h <= 0 || padded <= h) return;
        void Repeat(uint plane, uint rowBytes, int lastRow, int from, int to, int bytes)
        {
            var last = GuestMemory.Span(plane + (uint)lastRow * rowBytes, bytes).ToArray();
            for (int y = from; y < to; y++) last.CopyTo(GuestMemory.Span(plane + (uint)y * rowBytes, bytes));
        }
        switch (layout)
        {
            case PIXEL_YUV420_RASTER:
            {
                uint uOff = pitch * bufH, vOff = uOff + pitch / 2 * (bufH / 2);
                Repeat(dst, pitch, h - 1, h, padded, w);
                Repeat(dst + uOff, pitch / 2, h / 2 - 1, h / 2, padded / 2, w / 2);
                Repeat(dst + vOff, pitch / 2, h / 2 - 1, h / 2, padded / 2, w / 2);
                break;
            }
            case PIXEL_YUV420_PACKED_RASTER:
                Repeat(dst, pitch, h - 1, h, padded, w);
                Repeat(dst + pitch * bufH, pitch, h / 2 - 1, h / 2, padded / 2, w);
                break;
            default:
                Repeat(dst, pitch * 4, h - 1, h, padded, w * 4);
                break;
        }
    }

    static void ToRgb(DecodedPicture p, uint dst, uint pitch, int w, int h, bool bgra, byte alpha, bool bt709, bool full)
    {
        // YCbCr -> RGB (fixed point, 16.16)
        float kr = bt709 ? 0.2126f : 0.299f, kb = bt709 ? 0.0722f : 0.114f, kg = 1 - kr - kb;
        float yScale = full ? 1f : 255f / 219f, cScale = full ? 1f : 255f / 224f;
        int yo = full ? 0 : 16;
        int crR = (int)(2 * (1 - kr) * cScale * 65536), cbB = (int)(2 * (1 - kb) * cScale * 65536);
        int cbG = (int)(2 * (1 - kb) * kb / kg * cScale * 65536), crG = (int)(2 * (1 - kr) * kr / kg * cScale * 65536);
        int ys = (int)(yScale * 65536);
        int cw = (p.Width + 1) / 2;
        for (int y = 0; y < h; y++)
        {
            var row = GuestMemory.Span(dst + (uint)y * pitch * 4, w * 4);
            for (int x = 0; x < w; x++)
            {
                int Y = (p.Y[y * p.Width + x] - yo) * ys;
                int cb = p.U[(y / 2) * cw + x / 2] - 128, cr = p.V[(y / 2) * cw + x / 2] - 128;
                int r = (Y + crR * cr) >> 16, g = (Y - cbG * cb - crG * cr) >> 16, b = (Y + cbB * cb) >> 16;
                byte R = (byte)Math.Clamp(r, 0, 255), G = (byte)Math.Clamp(g, 0, 255), B = (byte)Math.Clamp(b, 0, 255);
                int o = x * 4;
                if (bgra) { row[o] = B; row[o + 1] = G; row[o + 2] = R; }
                else { row[o] = R; row[o + 1] = G; row[o + 2] = B; }
                row[o + 3] = alpha;
            }
        }
    }
}

/// <summary>SceCodecEngine: memory the hardware codecs use inside "unmapped" memblocks owned by the game.</summary>
public sealed partial class CodecEngine : HleModule
{
    const int ERR_INVALID_VALUE = unchecked((int)0x80600009);

    sealed partial class Block
    {
        public uint Base, Size;
        public readonly SortedList<uint, uint> Free = new();
        public readonly Dictionary<uint, uint> Used = new();
    }

    readonly Dictionary<int, Block> _blocks = new();
    int _nextId = 0x10;

    [Hle]
    int sceCodecEngineOpenUnmapMemBlock(uint memBlock, uint size)
    {
        if (memBlock == 0 || size == 0) return ERR_INVALID_VALUE;
        var b = new Block { Base = memBlock, Size = size };
        b.Free.Add(memBlock, size);
        lock (_blocks) { _blocks[_nextId] = b; return _nextId++; }
    }

    [Hle] int sceCodecEngineCloseUnmapMemBlock(int uid) { lock (_blocks) return _blocks.Remove(uid) ? 0 : ERR_INVALID_VALUE; }

    // Two argument-less calls the Camera (NPXS10004) makes when video recording starts (9A5E7B2F) and stops
    // (9AD7E66F), ignoring the results: probably the codec engine's performance monitor start/stop (unnamed; inferred
    // from the call sites). There is no codec hardware to monitor, so both succeed.
    [Hle("sceCodecEngineUser_9A5E7B2F", Nid = 0x9A5E7B2F, Library = "SceCodecEngineUser")]
    int CodecEngineMonitorStart() => 0;

    [Hle("sceCodecEngineUser_9AD7E66F", Nid = 0x9AD7E66F, Library = "SceCodecEngineUser")]
    int CodecEngineMonitorStop() => 0;

    /// <summary>Returns the address, or 0 when the block cannot hold it: callers test the result against 0 (the camera,
    /// NPXS10004, stores anything else as an address and frees it later).</summary>
    [Hle]
    uint sceCodecEngineAllocMemoryFromUnmapMemBlock(int uid, uint size, uint alignment)
    {
        lock (_blocks)
        {
            if (!_blocks.TryGetValue(uid, out var b)) return 0;
            if (size == 0 || size > b.Size) return 0;
            size = GuestMemory.AlignUp(Math.Max(size, 1), 256);
            // The alignment is a request for the codec's physical pages, not a guarantee about the virtual address
            // inside the game's block: Gravity Rush's AvPlayer opens a 1 MiB block, takes 256 KiB from it and then
            // asks for 512 KiB aligned to 1 MiB, which only fits if the alignment is relaxed (the game treats the
            // error code as an address; Vita3K returns fake addresses and never fails). Try the requested
            // alignment first, then smaller powers of two.
            for (uint align = Math.Max(alignment == 0 ? 256u : System.Numerics.BitOperations.RoundUpToPowerOf2(alignment), 256u); align >= 256; align >>= 1)
                if (TryAlloc(b, size, align) is uint p) return p;
            return 0;
        }
    }

    static uint? TryAlloc(Block b, uint size, uint align)
    {
        for (int i = 0; i < b.Free.Count; i++)
        {
            uint start = b.Free.Keys[i], len = b.Free.Values[i], p = GuestMemory.AlignUp(start, align);
            if ((ulong)p - start + size > len) continue;
            b.Free.RemoveAt(i);
            if (p > start) b.Free.Add(start, p - start);
            if (start + len > p + size) b.Free.Add(p + size, start + len - p - size);
            b.Used[p] = size;
            return p;
        }
        return null;
    }

    [Hle]
    int sceCodecEngineFreeMemoryFromUnmapMemBlock(int uid, uint addr)
    {
        lock (_blocks)
        {
            // a block that was never handed out (or is freed twice) is refused, never a host exception
            if (!_blocks.TryGetValue(uid, out var b) || !b.Used.Remove(addr, out uint size)) return ERR_INVALID_VALUE;
            if (!b.Free.TryAdd(addr, size)) return ERR_INVALID_VALUE;
            // merge adjacent free ranges
            for (int i = 0; i + 1 < b.Free.Count;)
            {
                if (b.Free.Keys[i] + b.Free.Values[i] == b.Free.Keys[i + 1])
                {
                    uint merged = b.Free.Values[i] + b.Free.Values[i + 1];
                    uint k = b.Free.Keys[i];
                    b.Free.RemoveAt(i + 1);
                    b.Free[k] = merged;
                }
                else i++;
            }
        }
        return 0;
    }
}
