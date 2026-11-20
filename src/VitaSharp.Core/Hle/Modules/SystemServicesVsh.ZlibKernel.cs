using System.IO.Compression;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceZlibForDriver: the zlib 1.2.5 that SceSblPostSsMgr exports to the kernel (FW 3.74, decompiled, study only: the
/// version string "1.2.5", z_stream of 0x38 bytes, every function under two NIDs). The update manager inflates the
/// update's pieces with it and the coredump module deflates its dumps.
/// <para>
/// The z_stream is the guest's (next_in +0, avail_in +4, total_in +8, next_out +0xC, avail_out +0x10, total_out +0x14,
/// msg +0x18, state +0x1C, zalloc +0x20, zfree +0x24, opaque +0x28, data_type +0x2C, adler +0x30); the state word holds
/// a kernel token for the stream's state kept here. Inflating is a resumable RFC 1950/1951/1952 decoder
/// (<see cref="ZlibStreamInflater"/>): input taken in pieces, output given as the caller has room, the bytes past the
/// end handed back. Deflating compresses the input between two flush points as one piece (the host's deflate, which
/// is zlib's own algorithm; the bytes may differ from the firmware's build but decode the same), with the zlib or gzip
/// wrapper written here.
/// </para>
/// </summary>
public sealed partial class SystemServicesVsh
{
    const int ZOk = 0, ZStreamEnd = 1, ZNeedDict = 2, ZStreamError = -2, ZDataError = -3, ZBufError = -5, ZVersionError = -6;
    const int ZFinish = 4;
    const uint ZStreamSize = 0x38;
    const uint ZNextIn = 0x00, ZAvailIn = 0x04, ZTotalIn = 0x08, ZNextOut = 0x0C, ZAvailOut = 0x10, ZTotalOut = 0x14;
    const uint ZMsg = 0x18, ZState = 0x1C, ZZfree = 0x24, ZOpaque = 0x28, ZAdler = 0x30;

    /// <summary>The state of one stream (by its token).</summary>
    sealed class ZStream
    {
        public bool Deflating;
        public ZlibStreamInflater? Inflater;
        public int InflateWindowBits;
        public int Level;
        public int WindowBits;
        public int MemLevel;
        public ZlibStreamInflater.Wrapper Wrapper;
        public List<byte> PendingInput = [];
        public byte[] Output = [];
        public int Delivered;
        public bool HeaderWritten;
        public bool Finished;
        public uint Checksum;
        public uint InputSize;

        public ZStream Clone()
        {
            var copy = (ZStream)MemberwiseClone();
            copy.Inflater = Inflater?.Clone();
            copy.PendingInput = [.. PendingInput];
            copy.Output = (byte[])Output.Clone();
            return copy;
        }
    }

    /// <summary>The streams by their state token. A host-side codec state: a save state taken mid-stream does not
    /// carry it (the stream reports Z_STREAM_ERROR afterwards, as one whose state was lost).</summary>
    [SnapshotIgnore]
    readonly Dictionary<uint, ZStream> _zlibStreams = new();

    /// <summary>The zlib strings in kernel memory (0 until first asked): "1.2.5" and z_errmsg.</summary>
    [SnapshotMissing] // states of builds before it: made again on first request
    uint _zlibStrings;

    static readonly string[] ZlibMessages =
    [
        "need dictionary", "stream end", "", "file error", "stream error", "data error", "insufficient memory",
        "buffer error", "incompatible version",
    ];

    /// <summary>The address of "1.2.5" (+0) and of each z_errmsg string (+0x10 + index * 0x18).</summary>
    uint ZlibString(int index)
    {
        lock (_zlibStreams)
        {
            if (_zlibStrings == 0)
            {
                uint block;
                using (ProcessScope.Enter(KernelSpace.KernelPid))
                {
                    block = Mem.Alloc(0x10 + (uint)ZlibMessages.Length * 0x18, "kernel:zlib", MemoryRegion.Kernel, 8);
                }

                GuestMemory.WriteCString(block, "1.2.5", 0x10);
                for (int i = 0; i < ZlibMessages.Length; i++)
                {
                    GuestMemory.WriteCString(block + 0x10 + (uint)i * 0x18, ZlibMessages[i], 0x18);
                }

                _zlibStrings = block;
            }

            return index < 0 ? _zlibStrings : _zlibStrings + 0x10 + (uint)index * 0x18;
        }
    }

    ZStream? StreamOf(uint stream)
    {
        if (stream == 0)
        {
            return null;
        }

        uint token = GuestMemory.R32(stream + ZState);
        lock (_zlibStreams)
        {
            return token != 0 && _zlibStreams.TryGetValue(token, out ZStream? state) ? state : null;
        }
    }

    /// <summary>The version check of the init calls: "1..." and a 0x38-byte z_stream.</summary>
    static bool ZlibVersionOk(uint version, uint size) => version != 0 && GuestMemory.Read<byte>(version) == (byte)'1' && size == ZStreamSize;

    int AttachStream(uint stream, ZStream state)
    {
        uint token;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            token = Mem.Alloc(8, "kernel:zstream", MemoryRegion.Kernel, 8);
        }

        lock (_zlibStreams)
        {
            _zlibStreams[token] = state;
        }

        GuestMemory.W32(stream + ZState, token);
        GuestMemory.W32(stream + ZMsg, 0);
        GuestMemory.W32(stream + ZTotalIn, 0);
        GuestMemory.W32(stream + ZTotalOut, 0);
        GuestMemory.W32(stream + ZAdler, state.Wrapper == ZlibStreamInflater.Wrapper.Gzip ? 0u : 1u);
        return ZOk;
    }

    int DetachStream(uint stream)
    {
        uint token = GuestMemory.R32(stream + ZState);
        bool known;
        lock (_zlibStreams)
        {
            known = _zlibStreams.Remove(token);
        }

        if (!known)
        {
            return ZStreamError;
        }

        Mem.Free(token);
        GuestMemory.W32(stream + ZState, 0);
        return ZOk;
    }

    void ZlibError(uint stream, int code)
    {
        GuestMemory.W32(stream + ZMsg, ZlibString(2 - code));
    }

    // ------------------------------------------------------------------ deflate

    /// <summary>deflateInit2_ checks: level -1..9 (-1: 6), method 8, windowBits 8..15 (zlib), -8..-15 (raw), 24..31
    /// (gzip), memLevel 1..9, strategy 0..4.</summary>
    int DeflateInit(uint stream, int level, int method, int windowBits, int memLevel, int strategy, uint version, uint size)
    {
        if (!ZlibVersionOk(version, size))
        {
            return ZVersionError;
        }

        if (stream == 0)
        {
            return ZStreamError;
        }

        if (level == -1)
        {
            level = 6;
        }

        ZlibStreamInflater.Wrapper wrapper = ZlibStreamInflater.Wrapper.Zlib;
        if (windowBits < 0)
        {
            wrapper = ZlibStreamInflater.Wrapper.Raw;
            windowBits = -windowBits;
        }
        else if (windowBits > 15)
        {
            wrapper = ZlibStreamInflater.Wrapper.Gzip;
            windowBits -= 16;
        }

        if (memLevel < 1 || memLevel > 9 || method != 8 || windowBits < 8 || windowBits > 15 || level < 0 || level > 9 || strategy < 0 || strategy > 4)
        {
            return ZStreamError;
        }

        var state = new ZStream
        {
            Deflating = true,
            Level = level,
            WindowBits = windowBits == 8 ? 9 : windowBits,
            MemLevel = memLevel,
            Wrapper = wrapper,
            Checksum = wrapper == ZlibStreamInflater.Wrapper.Gzip ? 0u : 1u,
        };
        return AttachStream(stream, state);
    }

    /// <summary>deflateInit_ (0x25F28DA7, 0x520CAA7F; the variant 0x5A0078D6, 0xAD23EEBB)(strm, level, version,
    /// stream_size): deflateInit2_ with method 8, windowBits 15, memLevel 8, strategy 0.</summary>
    [Hle(Nid = 0x25F28DA7, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x520CAA7F, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x5A0078D6, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xAD23EEBB, Library = "SceZlibForDriver")]
    int zlibDeflateInit(uint stream, int level, uint version, uint size) => DeflateInit(stream, level, 8, 15, 8, 0, version, size);

    /// <summary>deflateInit2_ (0xE6EB524C, 0xF2D8FC1A, 0x20A122F8, 0xBE5CE88A)(strm, level, method, windowBits,
    /// memLevel, strategy, version, stream_size).</summary>
    [Hle(Nid = 0xE6EB524C, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xF2D8FC1A, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x20A122F8, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xBE5CE88A, Library = "SceZlibForDriver")]
    void zlibDeflateInit2(HleContext context)
    {
        context.Return(DeflateInit(context.Gpr(0), (int)context.Gpr(1), (int)context.Gpr(2), (int)context.Gpr(3), (int)context.Gpr(4), (int)context.Gpr(5), context.Gpr(6), context.Gpr(7)));
    }

    /// <summary>The zlib (CMF, FLG by level) or gzip (no name, no time, OS 3) header of a deflated stream.</summary>
    static byte[] DeflateHeader(ZStream state)
    {
        if (state.Wrapper == ZlibStreamInflater.Wrapper.Gzip)
        {
            return [0x1F, 0x8B, 8, 0, 0, 0, 0, 0, (byte)(state.Level == 9 ? 2 : state.Level < 2 ? 4 : 0), 3];
        }

        if (state.Wrapper == ZlibStreamInflater.Wrapper.Raw)
        {
            return [];
        }

        int cmf = 8 | ((state.WindowBits - 8) << 4);
        int levelFlags = state.Level < 2 ? 0 : state.Level < 6 ? 1 : state.Level == 6 ? 2 : 3;
        int flags = levelFlags << 6;
        flags += 31 - ((cmf << 8) | flags) % 31;
        return [(byte)cmf, (byte)flags];
    }

    static CompressionLevel HostLevel(int level) => level switch
    {
        0 => CompressionLevel.NoCompression,
        < 4 => CompressionLevel.Fastest,
        9 => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    /// <summary>Deflates the input since the last flush point as one piece: a sync-flushed piece, or the final one.</summary>
    static byte[] DeflatePiece(ZStream state, bool final)
    {
        using var output = new MemoryStream();
        long flushed;
        using (var deflater = new DeflateStream(output, HostLevel(state.Level), leaveOpen: true))
        {
            deflater.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(state.PendingInput));
            deflater.Flush();
            flushed = output.Length;
        }

        byte[] bytes = output.ToArray();
        return final ? bytes : bytes[..(int)flushed];
    }

    void AppendOutput(ZStream state, ReadOnlySpan<byte> bytes)
    {
        int pending = state.Output.Length - state.Delivered;
        var merged = new byte[pending + bytes.Length];
        state.Output.AsSpan(state.Delivered).CopyTo(merged);
        bytes.CopyTo(merged.AsSpan(pending));
        state.Output = merged;
        state.Delivered = 0;
    }

    /// <summary>Gives the pending output to the stream's next_out; the bytes given.</summary>
    static uint DeliverOutput(uint stream, ZStream state)
    {
        uint room = GuestMemory.R32(stream + ZAvailOut);
        int count = (int)Math.Min(room, (uint)(state.Output.Length - state.Delivered));
        if (count == 0)
        {
            return 0;
        }

        uint next = GuestMemory.R32(stream + ZNextOut);
        state.Output.AsSpan(state.Delivered, count).CopyTo(GuestMemory.Span(next, count));
        state.Delivered += count;
        GuestMemory.W32(stream + ZNextOut, next + (uint)count);
        GuestMemory.W32(stream + ZAvailOut, room - (uint)count);
        GuestMemory.W32(stream + ZTotalOut, GuestMemory.R32(stream + ZTotalOut) + (uint)count);
        return (uint)count;
    }

    /// <summary>deflate (0x5B718E55, 0xE859D60F)(strm, flush): takes all the input, compresses it at a flush
    /// (Z_PARTIAL/SYNC/FULL_FLUSH, Z_BLOCK) or at Z_FINISH (the wrapper's trailer after it), and gives what fits;
    /// Z_STREAM_END once everything after Z_FINISH was given, Z_BUF_ERROR when no progress was possible.</summary>
    [Hle(Nid = 0x5B718E55, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xE859D60F, Library = "SceZlibForDriver")]
    int zlibDeflate(uint stream, int flush)
    {
        if (StreamOf(stream) is not { Deflating: true } state || flush < 0 || flush > 5)
        {
            return ZStreamError;
        }

        if (GuestMemory.R32(stream + ZNextOut) == 0 || (GuestMemory.R32(stream + ZNextIn) == 0 && GuestMemory.R32(stream + ZAvailIn) != 0)
            || (state.Finished && flush != ZFinish))
        {
            ZlibError(stream, ZStreamError);
            return ZStreamError;
        }

        if (GuestMemory.R32(stream + ZAvailOut) == 0)
        {
            ZlibError(stream, ZBufError);
            return ZBufError;
        }

        uint available = GuestMemory.R32(stream + ZAvailIn);
        uint next = GuestMemory.R32(stream + ZNextIn);
        ReadOnlySpan<byte> input = GuestMemory.ReadSpan(next, (int)available);
        if (!state.Finished && available > 0)
        {
            state.PendingInput.AddRange(input);
            state.Checksum = state.Wrapper == ZlibStreamInflater.Wrapper.Gzip
                ? ZlibStreamInflater.Crc32(state.Checksum, input)
                : ZlibStreamInflater.Adler32(state.Checksum, input);
            state.InputSize += available;
            GuestMemory.W32(stream + ZNextIn, next + available);
            GuestMemory.W32(stream + ZAvailIn, 0);
            GuestMemory.W32(stream + ZTotalIn, GuestMemory.R32(stream + ZTotalIn) + available);
            GuestMemory.W32(stream + ZAdler, state.Checksum);
        }

        if (!state.HeaderWritten)
        {
            AppendOutput(state, DeflateHeader(state));
            state.HeaderWritten = true;
        }

        if (!state.Finished && flush != 0)
        {
            AppendOutput(state, DeflatePiece(state, flush == ZFinish));
            state.PendingInput.Clear();
            if (flush == ZFinish)
            {
                AppendOutput(state, DeflateTrailer(state));
                state.Finished = true;
            }
        }

        uint given = DeliverOutput(stream, state);
        if (state.Finished && state.Delivered == state.Output.Length)
        {
            return ZStreamEnd;
        }

        return given == 0 && available == 0 && flush == 0 ? ZBufError : ZOk;
    }

    static byte[] DeflateTrailer(ZStream state)
    {
        uint checksum = state.Checksum;
        return state.Wrapper switch
        {
            ZlibStreamInflater.Wrapper.Gzip =>
            [
                (byte)checksum, (byte)(checksum >> 8), (byte)(checksum >> 16), (byte)(checksum >> 24),
                (byte)state.InputSize, (byte)(state.InputSize >> 8), (byte)(state.InputSize >> 16), (byte)(state.InputSize >> 24),
            ],
            ZlibStreamInflater.Wrapper.Zlib => [(byte)(checksum >> 24), (byte)(checksum >> 16), (byte)(checksum >> 8), (byte)checksum],
            _ => [],
        };
    }

    /// <summary>deflateEnd (0x5492B3F2, 0xA5D70E95)(strm): frees the stream; Z_DATA_ERROR when it ended before its
    /// Z_FINISH was given out (as zlib reports a stream freed mid-way).</summary>
    [Hle(Nid = 0x5492B3F2, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xA5D70E95, Library = "SceZlibForDriver")]
    int zlibDeflateEnd(uint stream)
    {
        if (StreamOf(stream) is not { Deflating: true } state)
        {
            return ZStreamError;
        }

        bool busy = state.HeaderWritten && !(state.Finished && state.Delivered == state.Output.Length);
        DetachStream(stream);
        return busy ? ZDataError : ZOk;
    }

    /// <summary>deflateReset (0x211D25F5, 0x68CFEA45)(strm): a fresh stream with the same parameters.</summary>
    [Hle(Nid = 0x211D25F5, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x68CFEA45, Library = "SceZlibForDriver")]
    int zlibDeflateReset(uint stream)
    {
        if (StreamOf(stream) is not { Deflating: true } state)
        {
            return ZStreamError;
        }

        state.PendingInput.Clear();
        state.Output = [];
        state.Delivered = 0;
        state.HeaderWritten = false;
        state.Finished = false;
        state.InputSize = 0;
        state.Checksum = state.Wrapper == ZlibStreamInflater.Wrapper.Gzip ? 0u : 1u;
        GuestMemory.W32(stream + ZTotalIn, 0);
        GuestMemory.W32(stream + ZTotalOut, 0);
        GuestMemory.W32(stream + ZMsg, 0);
        GuestMemory.W32(stream + ZAdler, state.Checksum);
        return ZOk;
    }

    /// <summary>deflateSetDictionary (0x4EE6C080, 0x93168F72)(strm, dictionary, length): accepted before the first
    /// deflate (Z_STREAM_ERROR after it or for a gzip stream); the host deflate does not take a preset dictionary,
    /// so the output simply does not refer to it (any inflater, with or without the dictionary, decodes it).</summary>
    [Hle(Nid = 0x4EE6C080, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x93168F72, Library = "SceZlibForDriver")]
    int zlibDeflateSetDictionary(uint stream, uint dictionary, uint length)
    {
        if (StreamOf(stream) is not { Deflating: true } state || dictionary == 0 || state.HeaderWritten
            || state.Wrapper == ZlibStreamInflater.Wrapper.Gzip)
        {
            return ZStreamError;
        }

        GuestMemory.W32(stream + ZAdler, ZlibStreamInflater.Adler32(1, GuestMemory.ReadSpan(dictionary, (int)length)));
        return ZOk;
    }

    /// <summary>deflatePrime (0x0FA805A3, 0x1C344E27)(strm, bits, value): bits to put before the next deflate output.
    /// The host deflate starts its pieces on a byte, so the bits cannot be merged; the call is accepted (0) as the
    /// firmware's, without them.</summary>
    [Hle(Nid = 0x0FA805A3, Library = "SceZlibForDriver", Default = true)]
    [Hle(Nid = 0x1C344E27, Library = "SceZlibForDriver", Default = true)]
    int zlibDeflatePrime(uint stream, int bits, int value) => StreamOf(stream) is { Deflating: true } ? ZOk : ZStreamError;

    /// <summary>deflateTune (0x938F34FA, 0xBC022D38)(strm, good_length, max_lazy, nice_length, max_chain): the match
    /// search parameters (the host deflate keeps its own); 0.</summary>
    [Hle(Nid = 0x938F34FA, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xBC022D38, Library = "SceZlibForDriver")]
    int zlibDeflateTune(uint stream) => StreamOf(stream) is { Deflating: true } ? ZOk : ZStreamError;

    /// <summary>deflateBound (0x05F712FE, 0x67A085C4)(strm, sourceLen): the most a single deflate(Z_FINISH) of
    /// sourceLen bytes writes: the tight bound for windowBits 15 and memLevel 8, else the conservative one, plus the
    /// wrapper (zlib 6, gzip 18, raw 0; without a stream 6).</summary>
    [Hle(Nid = 0x05F712FE, Library = "SceZlibForDriver", ReturnsValue = true)]
    [Hle(Nid = 0x67A085C4, Library = "SceZlibForDriver", ReturnsValue = true)]
    uint zlibDeflateBound(uint stream, uint sourceLength)
    {
        uint conservative = sourceLength + ((sourceLength + 7) >> 3) + ((sourceLength + 63) >> 6) + 5;
        if (StreamOf(stream) is not { Deflating: true } state)
        {
            return conservative + 6;
        }

        uint wrapper = state.Wrapper switch
        {
            ZlibStreamInflater.Wrapper.Zlib => 6u,
            ZlibStreamInflater.Wrapper.Gzip => 18u,
            _ => 0u,
        };
        if (state.WindowBits != 15 || state.MemLevel != 8)
        {
            return conservative + wrapper;
        }

        return sourceLength + (sourceLength >> 12) + (sourceLength >> 14) + (sourceLength >> 25) + 13 - 6 + wrapper;
    }

    /// <summary>deflateCopy (0x1E135CC1, 0x3252D28C)(dest, source): dest becomes a copy of source (the z_stream and
    /// the state).</summary>
    [Hle(Nid = 0x1E135CC1, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x3252D28C, Library = "SceZlibForDriver")]
    int zlibDeflateCopy(uint destination, uint source) => CopyStream(destination, source, deflating: true);

    int CopyStream(uint destination, uint source, bool deflating)
    {
        if (destination == 0 || StreamOf(source) is not { } state || state.Deflating != deflating)
        {
            return ZStreamError;
        }

        GuestMemory.Copy(destination, source, ZStreamSize);
        return AttachStreamKeeping(destination, state.Clone());
    }

    /// <summary>Attaches a state without touching the stream's counters (a copy keeps the source's).</summary>
    int AttachStreamKeeping(uint stream, ZStream state)
    {
        uint totalIn = GuestMemory.R32(stream + ZTotalIn);
        uint totalOut = GuestMemory.R32(stream + ZTotalOut);
        uint adler = GuestMemory.R32(stream + ZAdler);
        uint message = GuestMemory.R32(stream + ZMsg);
        AttachStream(stream, state);
        GuestMemory.W32(stream + ZTotalIn, totalIn);
        GuestMemory.W32(stream + ZTotalOut, totalOut);
        GuestMemory.W32(stream + ZAdler, adler);
        GuestMemory.W32(stream + ZMsg, message);
        return ZOk;
    }

    // ------------------------------------------------------------------ inflate

    /// <summary>inflateInit2_ checks: windowBits 8..15 (zlib), -8..-15 (raw), 24..31 (gzip), 40..47 (either), 0 (the
    /// header's).</summary>
    int InflateInit(uint stream, int windowBits, uint version, uint size)
    {
        if (!ZlibVersionOk(version, size))
        {
            return ZVersionError;
        }

        if (stream == 0)
        {
            return ZStreamError;
        }

        ZlibStreamInflater.Wrapper wrapper;
        int bits = windowBits;
        if (windowBits < 0)
        {
            wrapper = ZlibStreamInflater.Wrapper.Raw;
            bits = -windowBits;
        }
        else if (windowBits >= 32)
        {
            wrapper = ZlibStreamInflater.Wrapper.Auto;
            bits = windowBits - 32;
        }
        else if (windowBits >= 16)
        {
            wrapper = ZlibStreamInflater.Wrapper.Gzip;
            bits = windowBits - 16;
        }
        else
        {
            wrapper = ZlibStreamInflater.Wrapper.Zlib;
        }

        if (bits != 0 && (bits < 8 || bits > 15))
        {
            return ZStreamError;
        }

        var state = new ZStream { Wrapper = wrapper, InflateWindowBits = windowBits, Inflater = new ZlibStreamInflater(wrapper) };
        return AttachStream(stream, state);
    }

    /// <summary>inflateInit_ (0x723495A5, 0x81D0667B; the variant 0x44DA19D2, 0xB03E109B)(strm, version,
    /// stream_size): inflateInit2_ with windowBits 15.</summary>
    [Hle(Nid = 0x723495A5, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x81D0667B, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x44DA19D2, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xB03E109B, Library = "SceZlibForDriver")]
    int zlibInflateInit(uint stream, uint version, uint size) => InflateInit(stream, 15, version, size);

    /// <summary>inflateInit2_ (0x0BDDF66A, 0x21A03034, 0xA1E7E8B3, 0xAC2F8437)(strm, windowBits, version,
    /// stream_size).</summary>
    [Hle(Nid = 0x0BDDF66A, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x21A03034, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xA1E7E8B3, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xAC2F8437, Library = "SceZlibForDriver")]
    int zlibInflateInit2(uint stream, int windowBits, uint version, uint size) => InflateInit(stream, windowBits, version, size);

    /// <summary>
    /// inflate (0xD4A85178, 0xE4F34A68)(strm, flush): takes all the input, decodes as far as it goes and gives what
    /// fits; at the end of the stream the bytes after it are handed back (next_in/avail_in) and Z_STREAM_END comes once
    /// the output is all given. Z_NEED_DICT (adler = the dictionary's id) for a zlib header with a preset dictionary,
    /// Z_DATA_ERROR (msg set) for bad data, Z_BUF_ERROR when no progress was possible.
    /// </summary>
    [Hle(Nid = 0xD4A85178, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xE4F34A68, Library = "SceZlibForDriver")]
    int zlibInflate(uint stream, int flush)
    {
        if (StreamOf(stream) is not { Deflating: false, Inflater: { } inflater } state)
        {
            return ZStreamError;
        }

        if (GuestMemory.R32(stream + ZNextOut) == 0 || (GuestMemory.R32(stream + ZNextIn) == 0 && GuestMemory.R32(stream + ZAvailIn) != 0))
        {
            return ZStreamError;
        }

        uint available = GuestMemory.R32(stream + ZAvailIn);
        uint next = GuestMemory.R32(stream + ZNextIn);
        if (!inflater.Ended && available > 0)
        {
            inflater.AddInput(GuestMemory.ReadSpan(next, (int)available));
        }
        else
        {
            available = 0;
        }

        ZlibStreamInflater.Status status = inflater.Ended ? ZlibStreamInflater.Status.Done : inflater.Run();
        uint handedBack = status == ZlibStreamInflater.Status.Done ? (uint)Math.Min(inflater.UnusedInput, (int)available) : 0;
        uint used = available - handedBack;
        GuestMemory.W32(stream + ZNextIn, next + used);
        GuestMemory.W32(stream + ZAvailIn, GuestMemory.R32(stream + ZAvailIn) - used);
        GuestMemory.W32(stream + ZTotalIn, GuestMemory.R32(stream + ZTotalIn) + used);

        uint room = GuestMemory.R32(stream + ZAvailOut);
        uint nextOut = GuestMemory.R32(stream + ZNextOut);
        int given = inflater.TakeOutput(GuestMemory.Span(nextOut, (int)room));
        GuestMemory.W32(stream + ZNextOut, nextOut + (uint)given);
        GuestMemory.W32(stream + ZAvailOut, room - (uint)given);
        GuestMemory.W32(stream + ZTotalOut, GuestMemory.R32(stream + ZTotalOut) + (uint)given);
        GuestMemory.W32(stream + ZAdler, inflater.Checksum);

        switch (status)
        {
            case ZlibStreamInflater.Status.BadData:
                ZlibError(stream, ZDataError);
                return ZDataError;
            case ZlibStreamInflater.Status.NeedDictionary:
                GuestMemory.W32(stream + ZAdler, inflater.DictionaryId);
                return ZNeedDict;
            case ZlibStreamInflater.Status.Done:
                return inflater.Pending == 0 ? ZStreamEnd : ZOk;
        }

        return used == 0 && given == 0 ? ZBufError : ZOk;
    }

    /// <summary>inflateEnd (0x134E91EA, 0x9030BAE4)(strm): frees the stream.</summary>
    [Hle(Nid = 0x134E91EA, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x9030BAE4, Library = "SceZlibForDriver")]
    int zlibInflateEnd(uint stream) => StreamOf(stream) is { Deflating: false } ? DetachStream(stream) : ZStreamError;

    /// <summary>inflateReset (0x408311E8, 0xEEC6D267)(strm): a fresh stream with the same windowBits.</summary>
    [Hle(Nid = 0x408311E8, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xEEC6D267, Library = "SceZlibForDriver")]
    int zlibInflateReset(uint stream)
    {
        if (StreamOf(stream) is not { Deflating: false } state)
        {
            return ZStreamError;
        }

        state.Inflater = new ZlibStreamInflater(state.Wrapper);
        GuestMemory.W32(stream + ZTotalIn, 0);
        GuestMemory.W32(stream + ZTotalOut, 0);
        GuestMemory.W32(stream + ZMsg, 0);
        GuestMemory.W32(stream + ZAdler, 1);
        return ZOk;
    }

    /// <summary>inflateSetDictionary (0x00561385, 0x7B16DBD6)(strm, dictionary, length): the window starts with the
    /// dictionary; for a zlib stream only after Z_NEED_DICT and when its adler32 matches (Z_DATA_ERROR).</summary>
    [Hle(Nid = 0x00561385, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x7B16DBD6, Library = "SceZlibForDriver")]
    int zlibInflateSetDictionary(uint stream, uint dictionary, uint length)
    {
        if (StreamOf(stream) is not { Deflating: false, Inflater: { } inflater } || dictionary == 0)
        {
            return ZStreamError;
        }

        return inflater.SetDictionary(GuestMemory.ReadSpan(dictionary, (int)length)) ? ZOk : ZDataError;
    }

    /// <summary>inflateSync (0x35E0108C, 0xE2DF5A8B)(strm): skips to the next full flush point (00 00 FF FF) in the
    /// input; Z_OK there, Z_DATA_ERROR when the input has none, Z_BUF_ERROR without input.</summary>
    [Hle(Nid = 0x35E0108C, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xE2DF5A8B, Library = "SceZlibForDriver")]
    int zlibInflateSync(uint stream)
    {
        if (StreamOf(stream) is not { Deflating: false, Inflater: { } inflater })
        {
            return ZStreamError;
        }

        uint available = GuestMemory.R32(stream + ZAvailIn);
        if (available == 0)
        {
            return ZBufError;
        }

        uint next = GuestMemory.R32(stream + ZNextIn);
        inflater.AddInput(GuestMemory.ReadSpan(next, (int)available));
        bool found = inflater.Sync();
        uint left = (uint)Math.Min(inflater.UnusedInput, (int)available);
        GuestMemory.W32(stream + ZNextIn, next + available - left);
        GuestMemory.W32(stream + ZAvailIn, left);
        GuestMemory.W32(stream + ZTotalIn, GuestMemory.R32(stream + ZTotalIn) + available - left);
        return found ? ZOk : ZDataError;
    }

    /// <summary>inflateSyncPoint (0x4CB63BCD, 0x834CC4A2)(strm): 1 when the decoder is between two blocks.</summary>
    [Hle(Nid = 0x4CB63BCD, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x834CC4A2, Library = "SceZlibForDriver")]
    int zlibInflateSyncPoint(uint stream)
    {
        if (StreamOf(stream) is not { Deflating: false, Inflater: { } inflater })
        {
            return ZStreamError;
        }

        return inflater.AtBlockBoundary ? 1 : 0;
    }

    /// <summary>inflateCopy (0x4C27A382, 0x89B30588)(dest, source).</summary>
    [Hle(Nid = 0x4C27A382, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x89B30588, Library = "SceZlibForDriver")]
    int zlibInflateCopy(uint destination, uint source) => CopyStream(destination, source, deflating: false);

    /// <summary>
    /// inflateBack (0x5377643A, 0x7C40CC39)(strm, in, in_desc, out, out_desc): inflates raw deflate data the guest's
    /// callbacks supply: in(in_desc, &amp;buffer) gives the next input (0 = none left), out(out_desc, buffer, length)
    /// takes the output (nonzero stops). Z_STREAM_END at the end (the unused input left in next_in/avail_in), Z_BUF_ERROR
    /// when in() or out() stopped it, Z_DATA_ERROR for bad data. The stream needs an inflateBackInit_ state (one made
    /// by inflateInit2_ here) and next_in/avail_in may already hold input.
    /// </summary>
    [Hle(Nid = 0x5377643A, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x7C40CC39, Library = "SceZlibForDriver")]
    void zlibInflateBack(HleContext context)
    {
        uint stream = context.Gpr(0);
        uint input = context.Gpr(1);
        uint inputDescriptor = context.Gpr(2);
        uint output = context.Gpr(3);
        uint outputDescriptor = context.Gpr(4);
        if (StreamOf(stream) is not { Deflating: false } state)
        {
            context.Return(ZStreamError);
            return;
        }

        var inflater = new ZlibStreamInflater(ZlibStreamInflater.Wrapper.Raw);
        state.Inflater = inflater;
        uint bufferPointer;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            bufferPointer = Mem.Alloc(0x10000 + 8, "kernel:inflateBack", MemoryRegion.Kernel, 8);
        }

        try
        {
            uint available = GuestMemory.R32(stream + ZAvailIn);
            uint next = GuestMemory.R32(stream + ZNextIn);
            while (true)
            {
                if (available == 0)
                {
                    available = context.Thread.CallGuest(input, [inputDescriptor, bufferPointer]);
                    next = GuestMemory.R32(bufferPointer);
                    if (available == 0 || next == 0)
                    {
                        GuestMemory.W32(stream + ZNextIn, 0);
                        GuestMemory.W32(stream + ZAvailIn, 0);
                        context.Return(ZBufError);
                        return;
                    }
                }

                inflater.AddInput(GuestMemory.ReadSpan(next, (int)available));
                ZlibStreamInflater.Status status = inflater.Run();
                uint left = status == ZlibStreamInflater.Status.Done ? (uint)Math.Min(inflater.UnusedInput, (int)available) : 0;
                next += available - left;
                available = left;
                while (inflater.Pending > 0)
                {
                    uint chunk = bufferPointer + 8;
                    int count = inflater.TakeOutput(GuestMemory.Span(chunk, 0x10000));
                    if (context.Thread.CallGuest(output, [outputDescriptor, chunk, (uint)count]) != 0)
                    {
                        GuestMemory.W32(stream + ZNextIn, next);
                        GuestMemory.W32(stream + ZAvailIn, available);
                        context.Return(ZBufError);
                        return;
                    }
                }

                if (status == ZlibStreamInflater.Status.BadData)
                {
                    ZlibError(stream, ZDataError);
                    context.Return(ZDataError);
                    return;
                }

                if (status == ZlibStreamInflater.Status.Done)
                {
                    GuestMemory.W32(stream + ZNextIn, next);
                    GuestMemory.W32(stream + ZAvailIn, available);
                    context.Return(ZStreamEnd);
                    return;
                }
            }
        }
        finally
        {
            Mem.Free(bufferPointer);
        }
    }

    /// <summary>inflateBackEnd (0x86FF6C8B, 0x904AA7AE)(strm): frees the stream (a stream without zfree: Z_STREAM_ERROR,
    /// as the firmware calls it).</summary>
    [Hle(Nid = 0x86FF6C8B, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x904AA7AE, Library = "SceZlibForDriver")]
    int zlibInflateBackEnd(uint stream)
    {
        if (StreamOf(stream) is null || GuestMemory.R32(stream + ZZfree) == 0)
        {
            return ZStreamError;
        }

        return DetachStream(stream);
    }

    // ------------------------------------------------------------------ one-shot calls and utilities

    /// <summary>compress2 (0x82167CD9, 0xE94663DD)(dest, uLongf *destLen, source, sourceLen, level): a zlib stream of
    /// the source; Z_BUF_ERROR when it does not fit in *destLen, Z_STREAM_ERROR for a bad level.</summary>
    [Hle(Nid = 0x82167CD9, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xE94663DD, Library = "SceZlibForDriver")]
    void zlibCompress2(HleContext context)
    {
        context.Return(Compress(context.Gpr(0), context.Gpr(1), context.Gpr(2), context.Gpr(3), (int)context.Gpr(4)));
    }

    /// <summary>compress (0x6ED5B677, 0x7993ADAB)(dest, destLen, source, sourceLen): compress2 at level 6.</summary>
    [Hle(Nid = 0x6ED5B677, Library = "SceZlibForDriver")]
    [Hle(Nid = 0x7993ADAB, Library = "SceZlibForDriver")]
    int zlibCompress(uint destination, uint destinationLength, uint source, uint sourceLength) => Compress(destination, destinationLength, source, sourceLength, 6);

    int Compress(uint destination, uint destinationLength, uint source, uint sourceLength, int level)
    {
        if (level == -1)
        {
            level = 6;
        }

        if (level < 0 || level > 9)
        {
            return ZStreamError;
        }

        var state = new ZStream { Deflating = true, Level = level, WindowBits = 15, MemLevel = 8, Wrapper = ZlibStreamInflater.Wrapper.Zlib, Checksum = 1 };
        ReadOnlySpan<byte> input = GuestMemory.ReadSpan(source, (int)sourceLength);
        state.PendingInput.AddRange(input);
        state.Checksum = ZlibStreamInflater.Adler32(1, input);
        byte[] header = DeflateHeader(state);
        byte[] body = DeflatePiece(state, final: true);
        byte[] trailer = DeflateTrailer(state);
        uint total = (uint)(header.Length + body.Length + trailer.Length);
        if (total > GuestMemory.R32(destinationLength))
        {
            return ZBufError;
        }

        Span<byte> target = GuestMemory.Span(destination, (int)total);
        header.CopyTo(target);
        body.CopyTo(target[header.Length..]);
        trailer.CopyTo(target[(header.Length + body.Length)..]);
        GuestMemory.W32(destinationLength, total);
        return ZOk;
    }

    /// <summary>compressBound (0x7048F14C, 0xE323828B)(sourceLen): the most compress writes.</summary>
    [Hle(Nid = 0x7048F14C, Library = "SceZlibForDriver", ReturnsValue = true)]
    [Hle(Nid = 0xE323828B, Library = "SceZlibForDriver", ReturnsValue = true)]
    uint zlibCompressBound(uint sourceLength) => sourceLength + (sourceLength >> 12) + (sourceLength >> 14) + (sourceLength >> 25) + 13;

    /// <summary>uncompress (0x89A13883, 0xD9BDC778)(dest, uLongf *destLen, source, sourceLen): inflates a zlib stream;
    /// *destLen the bytes written. Z_BUF_ERROR when the output does not fit, Z_DATA_ERROR for bad or truncated data
    /// (or one that needs a dictionary).</summary>
    [Hle(Nid = 0x89A13883, Library = "SceZlibForDriver")]
    [Hle(Nid = 0xD9BDC778, Library = "SceZlibForDriver")]
    int zlibUncompress(uint destination, uint destinationLength, uint source, uint sourceLength)
    {
        var inflater = new ZlibStreamInflater(ZlibStreamInflater.Wrapper.Zlib);
        inflater.AddInput(GuestMemory.ReadSpan(source, (int)sourceLength));
        ZlibStreamInflater.Status status = inflater.Run();
        uint capacity = GuestMemory.R32(destinationLength);
        int written = inflater.TakeOutput(GuestMemory.Span(destination, (int)capacity));
        GuestMemory.W32(destinationLength, (uint)written);
        return status switch
        {
            ZlibStreamInflater.Status.Done when inflater.Pending == 0 => ZOk,
            ZlibStreamInflater.Status.Done => ZBufError,
            ZlibStreamInflater.Status.NeedInput when inflater.Pending > 0 => ZBufError,
            _ => ZDataError,
        };
    }

    /// <summary>zlibVersion (0x517BC5F7)(): "1.2.5".</summary>
    [Hle(Nid = 0x517BC5F7, Library = "SceZlibForDriver", ReturnsValue = true)]
    uint zlibVersion() => ZlibString(-1);

    /// <summary>zError (0x3B4466F4, 0x3F33F55F)(err): the message of a zlib result (z_errmsg[2 - err]).</summary>
    [Hle(Nid = 0x3B4466F4, Library = "SceZlibForDriver", ReturnsValue = true)]
    [Hle(Nid = 0x3F33F55F, Library = "SceZlibForDriver", ReturnsValue = true)]
    uint zlibError(int error)
    {
        int index = 2 - error;
        return index >= 0 && index < ZlibMessages.Length ? ZlibString(index) : ZlibString(2);
    }

    /// <summary>adler32 (0x7E823337, 0x98619620)(adler, buffer, length): 1 for a NULL buffer.</summary>
    [Hle(Nid = 0x7E823337, Library = "SceZlibForDriver", ReturnsValue = true)]
    [Hle(Nid = 0x98619620, Library = "SceZlibForDriver", ReturnsValue = true)]
    uint zlibAdler32(uint adler, uint buffer, uint length) => buffer == 0 ? 1 : ZlibStreamInflater.Adler32(adler, GuestMemory.ReadSpan(buffer, (int)length));

    /// <summary>crc32 (0x3370B9AD, 0xE0CE06C0)(crc, buffer, length): 0 for a NULL buffer.</summary>
    [Hle(Nid = 0x3370B9AD, Library = "SceZlibForDriver", ReturnsValue = true)]
    [Hle(Nid = 0xE0CE06C0, Library = "SceZlibForDriver", ReturnsValue = true)]
    uint zlibCrc32(uint crc, uint buffer, uint length) => buffer == 0 ? 0 : ZlibStreamInflater.Crc32(crc, GuestMemory.ReadSpan(buffer, (int)length));
}
