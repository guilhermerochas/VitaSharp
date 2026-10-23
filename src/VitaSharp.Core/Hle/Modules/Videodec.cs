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
public sealed class Videodec : HleModule
{
    const int ERR_INVALID_PARAM = unchecked((int)0x80620002), ERR_INVALID_POINTER = unchecked((int)0x80620009);
    const uint TYPE_HW_AVCDEC = 0x1001;
    const uint PIXEL_RGBA8888 = 0x0, PIXEL_BGRA8888 = 0x8, PIXEL_YUV420_RASTER = 0x10, PIXEL_YUV420_PACKED_RASTER = 0x20;

    sealed class Decoder
    {
        public IVideoDecoder? Codec;
        public uint MaxWidth, MaxHeight;
        public DecodedPicture? Last;
    }

    readonly Dictionary<uint, Decoder> _decoders = new();
    uint _nextHandle = 1;

    [Hle] int sceVideodecInitLibrary(uint codecType, uint initInfo) => codecType == TYPE_HW_AVCDEC ? 0 : ERR_INVALID_PARAM;
    [Hle] int sceVideodecInitLibraryWithUnmapMem(uint codecType, uint initInfo, uint memInfo) => codecType == TYPE_HW_AVCDEC ? 0 : ERR_INVALID_PARAM;
    [Hle] int sceVideodecTermLibrary(uint codecType) => 0;

    /// <summary>Memory the codec library needs for its own state; the decoding happens host-side.</summary>
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

    [Hle]
    int sceAvcdecDecode(uint ctrl, uint au, uint array)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        if (au == 0 || array == 0) return ERR_INVALID_POINTER;
        long pts = ((long)GuestMemory.R32(au) << 32) | GuestMemory.R32(au + 4);
        uint es = GuestMemory.R32(au + 16), size = GuestMemory.R32(au + 20);
        if (d.Codec != null && es != 0 && size != 0) d.Codec.Send(GuestMemory.Span(es, (int)size), pts);
        return Output(d, array);
    }

    /// <summary>Writes every ready picture into the caller's picture array.</summary>
    int Output(Decoder d, uint array)
    {
        uint capacity = GuestMemory.R32(array + 4), list = GuestMemory.R32(array + 8);
        uint n = 0;
        while (n < capacity && d.Codec?.Take() is { } pic)
        {
            uint picture = GuestMemory.R32(list + n * 4);
            if (picture == 0) break;
            WritePicture(picture, pic);
            if (d.Last != null && d.Last != pic) d.Codec!.Recycle(d.Last); // its planes were already written out
            d.Last = pic;
            n++;
        }
        GuestMemory.W32(array, n);
        return 0;
    }

    [Hle]
    int sceAvcdecDecodeStop(uint ctrl, uint array)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        d.Codec?.Flush();
        return array == 0 ? 0 : Output(d, array);
    }

    [Hle]
    int sceAvcdecDecodeFlush(uint ctrl)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        d.Codec?.Reset();
        return 0;
    }

    [Hle]
    int sceAvcdecDecodeAvailableSize(uint ctrl)
    {
        var d = Find(ctrl, out int err);
        return d == null ? err : (int)(d.MaxWidth * d.MaxHeight * 3 / 2);
    }

    [Hle]
    int sceAvcdecDeleteDecoder(uint ctrl)
    {
        if (ctrl == 0) return ERR_INVALID_POINTER;
        Decoder? d;
        lock (_decoders)
            if (!_decoders.Remove(GuestMemory.R32(ctrl), out d)) return ERR_INVALID_PARAM;
        d.Codec?.Dispose();
        return 0;
    }

    static void WritePicture(uint picture, DecodedPicture p)
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
        GuestMemory.W32(picture + 0x5C, (uint)(p.Pts >> 32));
        GuestMemory.W32(picture + 0x60, (uint)p.Pts);
        if (dst == 0) return;
        int cw = (p.Width + 1) / 2;
        switch (pixelType & 0x38)
        {
            case PIXEL_YUV420_RASTER:
            {
                // planar: Y (pitch x height), then U and V (pitch/2 x height/2)
                uint uOff = pitch * bufH, vOff = uOff + pitch / 2 * (bufH / 2);
                for (int y = 0; y < h; y++) p.Y.AsSpan(y * p.Width, w).CopyTo(GuestMemory.Span(dst + (uint)y * pitch, w));
                for (int y = 0; y < h / 2; y++)
                {
                    p.U.AsSpan(y * cw, w / 2).CopyTo(GuestMemory.Span(dst + uOff + (uint)y * (pitch / 2), w / 2));
                    p.V.AsSpan(y * cw, w / 2).CopyTo(GuestMemory.Span(dst + vOff + (uint)y * (pitch / 2), w / 2));
                }
                break;
            }
            case PIXEL_YUV420_PACKED_RASTER:
            {
                uint uvOff = pitch * bufH;
                for (int y = 0; y < h; y++) p.Y.AsSpan(y * p.Width, w).CopyTo(GuestMemory.Span(dst + (uint)y * pitch, w));
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
public sealed class CodecEngine : HleModule
{
    const int ERR_INVALID_VALUE = unchecked((int)0x80600009);

    sealed class Block
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

    [Hle]
    uint sceCodecEngineAllocMemoryFromUnmapMemBlock(int uid, uint size, uint alignment)
    {
        lock (_blocks)
        {
            if (!_blocks.TryGetValue(uid, out var b)) return unchecked((uint)ERR_INVALID_VALUE);
            size = GuestMemory.AlignUp(Math.Max(size, 1), 256);
            // The alignment is a request for the codec's physical pages, not a guarantee about the virtual address
            // inside the game's block: Gravity Rush's AvPlayer opens a 1 MiB block, takes 256 KiB from it and then
            // asks for 512 KiB aligned to 1 MiB, which only fits if the alignment is relaxed (the game treats the
            // error code as an address; Vita3K returns fake addresses and never fails). Try the requested
            // alignment first, then smaller powers of two.
            for (uint align = Math.Max(alignment == 0 ? 256u : System.Numerics.BitOperations.RoundUpToPowerOf2(alignment), 256u); align >= 256; align >>= 1)
                if (TryAlloc(b, size, align) is uint p) return p;
            return unchecked((uint)ERR_INVALID_VALUE);
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
            if (!_blocks.TryGetValue(uid, out var b) || !b.Used.Remove(addr, out uint size)) return ERR_INVALID_VALUE;
            b.Free.Add(addr, size);
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
