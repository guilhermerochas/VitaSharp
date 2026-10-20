using VitaSharp.Core.Hle.Audio.Atrac9;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Audio;

/// <summary>Decodes whole ATRAC9 superframes; <see cref="Reset"/> drops the inter-frame (MDCT overlap) state.</summary>
public interface IAt9SuperframeDecoder
{
    void Reset();
    void Decode(byte[] superframe, short[][] pcm);
}

/// <summary>The managed ATRAC9 decoder behind <see cref="IAt9SuperframeDecoder"/>.</summary>
public sealed class ManagedAt9Decoder(byte[] config) : IAt9SuperframeDecoder
{
    Atrac9Decoder _d = Create(config);
    static Atrac9Decoder Create(byte[] config) { var d = new Atrac9Decoder(); d.Initialize(config); return d; }
    public void Reset() => _d = Create(config);
    public void Decode(byte[] superframe, short[][] pcm) => _d.Decode(superframe, pcm);
}

/// <summary>
/// One SceAtrac handle: playback position, loop bookkeeping and the guest buffer, either holding the whole file
/// ("all data on memory") or used as a ring the game refills through GetStreamInfo/AddStreamData.
/// <para>Coordinates: "full" samples count from the first decoded sample (encoder delay included), superframe k covers
/// full samples [k*SfSamples, (k+1)*SfSamples). Output samples (the API's) are full samples minus the encoder delay.
/// The ring is addressed by stream position (bytes ever written, the header included), mapped to buffer offset
/// position % BufferSize. Superframes enter the ring in play order: when looping, the reader is sent back to the loop
/// start block right after the loop end block, so the decoder consumes the ring strictly sequentially.</para>
/// </summary>
public sealed class AtracStream
{
    public const int ERR_DATA_SHORTAGE_IN_BUFFER = unchecked((int)0x80630010), ERR_ALL_DATA_WAS_DECODED = unchecked((int)0x80630011),
        ERR_ADDED_DATA_IS_TOO_BIG = unchecked((int)0x80630013), ERR_INVALID_SAMPLE = unchecked((int)0x80630015),
        ERR_INVALID_LOOP_STATUS = unchecked((int)0x80630017), ERR_DECODER = unchecked((int)0x807F0000);
    public const uint STATUS_ALL_DATA_WAS_DECODED = 1, STATUS_ALL_DATA_IS_ON_MEMORY = 2, STATUS_NONLOOP_PART_IS_ON_MEMORY = 4,
        STATUS_LOOP_PART_IS_ON_MEMORY = 8;

    public readonly At9Info Info;
    public readonly Atrac9Config Cfg;
    readonly IAt9SuperframeDecoder _decoder;
    public readonly uint Buffer, BufferSize;
    public readonly bool AllOnMemory;
    public readonly int SfBytes, SfSamples, Blocks, Channels;

    // ring (stream mode)
    long _write, _consume;
    long _streamToFile;   // file offset = stream position + this, while _linear
    bool _linear = true;  // no reader jump since the last reset: stream and file positions differ by a constant
    int _readerLoops;
    int _nextStreamBlock; // superframe index of the next superframe in the ring

    // playback
    long _pos;
    int _cachedBlock = -1, _lastDecoded = -1;
    readonly short[][] _pcm;
    readonly byte[] _staging;

    public uint ReadPos { get; private set; }
    public int LoopNum { get; private set; }
    public int OutputSamples { get; set; }
    public bool Done { get; private set; }
    public int InternalError { get; private set; }

    public AtracStream(At9Info info, Atrac9Config cfg, IAt9SuperframeDecoder decoder, uint buffer, uint readSize, uint bufferSize)
    {
        Info = info;
        Cfg = cfg;
        _decoder = decoder;
        Buffer = buffer;
        BufferSize = bufferSize;
        SfBytes = cfg.SuperframeBytes;
        SfSamples = cfg.SuperframeSamples;
        Channels = cfg.ChannelCount;
        Blocks = (int)(info.DataSize / (uint)SfBytes);
        AllOnMemory = readSize >= info.FileSize || readSize >= info.DataOffset + info.DataSize;
        OutputSamples = cfg.FrameSamples;
        _pcm = new short[Channels][];
        for (int c = 0; c < Channels; c++) _pcm[c] = new short[SfSamples];
        _staging = new byte[SfBytes];
        _write = readSize;
        _consume = info.DataOffset;
        ReadPos = readSize;
        _pos = info.EncoderDelay;
        if (EndFull < _pos) Done = true;
    }

    // ---- derived positions

    long EndFull => Math.Min((long)Info.EncoderDelay + Info.TotalSamples, (long)Blocks * SfSamples) - 1;
    bool HasLoop => Info.HasLoop;
    long LoopStartFull => Info.LoopStart + (long)Info.EncoderDelay;
    long LoopEndFull => Math.Min(Info.LoopEnd + (long)Info.EncoderDelay, EndFull);
    int LoopStartBlock => (int)(LoopStartFull / SfSamples);
    int LoopEndBlock => (int)(LoopEndFull / SfSamples);
    /// <summary>File offset just past the superframe holding the loop end.</summary>
    public uint LoopBoundary => Info.DataOffset + (uint)(LoopEndBlock + 1) * (uint)SfBytes;
    /// <summary>File offset of the superframe holding the loop start.</summary>
    public uint LoopRestart => Info.DataOffset + (uint)LoopStartBlock * (uint)SfBytes;
    uint DataEnd => Info.DataOffset + Info.DataSize;
    long Filled => _write - _consume;

    public int EndSample => Info.TotalSamples - 1;
    public long NextOutputSample => _pos - Info.EncoderDelay;

    // ---- streaming

    bool ReaderLooping => HasLoop && _readerLoops != 0 && ReadPos < LoopBoundary;

    /// <summary>GetStreamInfo: guest address to write to, file offset to read from and contiguous writable bytes.</summary>
    public (uint writePtr, uint readPos, uint writable) StreamInfo()
    {
        if (AllOnMemory) return (Buffer, ReadPos, 0);
        long free = BufferSize - Filled;
        long contig = BufferSize - _write % BufferSize;
        long remain = (long)(ReaderLooping ? LoopBoundary : DataEnd) - ReadPos;
        long w = Math.Max(0, Math.Min(free, Math.Min(contig, remain)));
        return (Buffer + (uint)(_write % BufferSize), ReadPos, (uint)w);
    }

    public uint VacantSize => AllOnMemory ? 0 : (uint)Math.Max(0, BufferSize - Filled);

    public int AddStreamData(uint size)
    {
        if (size > StreamInfo().writable) return ERR_ADDED_DATA_IS_TOO_BIG;
        if (size == 0) return 0;
        bool looping = ReaderLooping;
        _write += size;
        ReadPos += size;
        if (looping && ReadPos == LoopBoundary) ReaderJump();
        return 0;
    }

    void ReaderJump()
    {
        ReadPos = LoopRestart;
        _linear = false;
        if (_readerLoops > 0) _readerLoops--;
    }

    public int SetLoopNum(int loopNum)
    {
        if (loopNum != 0 && !HasLoop) return ERR_INVALID_LOOP_STATUS;
        LoopNum = loopNum;
        if (AllOnMemory || !HasLoop) return 0;
        _readerLoops = loopNum;
        // the reader may already have read past the loop end in a straight line: drop those bytes (the decoder has not
        // reached them) and send it back to the loop start
        if (loopNum != 0 && _linear)
        {
            long boundaryStream = LoopBoundary - _streamToFile;
            if (_write >= boundaryStream && _consume <= boundaryStream)
            {
                _write = boundaryStream;
                ReaderJump();
            }
        }
        return 0;
    }

    // ---- decoding

    /// <summary>Decodes up to <see cref="OutputSamples"/> samples per channel as interleaved S16 into
    /// <paramref name="output"/>. Stops early at the loop point and at the end of the data.</summary>
    public int Decode(Span<short> output, out int samples)
    {
        samples = 0;
        if (Done) return ERR_ALL_DATA_WAS_DECODED;
        int want = Math.Min(OutputSamples, output.Length / Channels);
        while (samples < want)
        {
            bool looping = HasLoop && LoopNum != 0 && _pos <= LoopEndFull;
            long segEnd = looping ? LoopEndFull : EndFull;
            int block = (int)(_pos / SfSamples);
            if (_cachedBlock != block)
            {
                int r = DecodeBlock(block);
                if (r == ERR_DATA_SHORTAGE_IN_BUFFER && samples > 0) break;
                if (r != 0) return r;
            }
            long blockStart = (long)block * SfSamples;
            int n = (int)Math.Min(want - samples, Math.Min(segEnd, blockStart + SfSamples - 1) - _pos + 1);
            int src = (int)(_pos - blockStart);
            for (int i = 0; i < n; i++)
                for (int c = 0; c < Channels; c++)
                    output[(samples + i) * Channels + c] = _pcm[c][src + i];
            samples += n;
            _pos += n;
            if (_pos > segEnd)
            {
                if (looping)
                {
                    _pos = LoopStartFull;
                    if (LoopNum > 0) LoopNum--;
                    if (!AllOnMemory) _nextStreamBlock = LoopStartBlock;
                    _cachedBlock = -1;
                }
                else Done = true;
                break;
            }
        }
        return 0;
    }

    int DecodeBlock(int block)
    {
        try
        {
            if (AllOnMemory)
            {
                if (block != _lastDecoded + 1)
                {
                    // non-sequential (seek or loop): restart the decoder and prime its overlap with the previous superframe
                    _decoder.Reset();
                    if (block > 0) DecodeAt(block - 1);
                }
                DecodeAt(block);
            }
            else
            {
                if (_nextStreamBlock > block) _nextStreamBlock = block;
                while (_nextStreamBlock <= block)
                {
                    if (Filled < SfBytes) return ERR_DATA_SHORTAGE_IN_BUFFER;
                    ReadRing(_staging);
                    _consume += SfBytes;
                    _decoder.Decode(_staging, _pcm);
                    _nextStreamBlock++;
                }
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            InternalError = ERR_DECODER;
            Log.WarnOnce("atrac-decode", $"sceAtracDecode: ATRAC9 superframe error: {e.Message}");
            return ERR_DECODER;
        }
        _cachedBlock = block;
        _lastDecoded = block;
        return 0;
    }

    void DecodeAt(int block)
    {
        GuestMemory.Span(Info.DataOffset + Buffer + (uint)block * (uint)SfBytes, SfBytes).CopyTo(_staging);
        _decoder.Decode(_staging, _pcm);
        _lastDecoded = block;
    }

    void ReadRing(Span<byte> dst)
    {
        long off = _consume % BufferSize;
        int first = (int)Math.Min(dst.Length, BufferSize - off);
        GuestMemory.Span(Buffer + (uint)off, first).CopyTo(dst);
        if (first < dst.Length) GuestMemory.Span(Buffer, dst.Length - first).CopyTo(dst[first..]);
    }

    /// <summary>Seek: the next output sample becomes <paramref name="sample"/>. In stream mode the ring is emptied and
    /// the reader restarts one superframe early so the decoder can prime its overlap.</summary>
    public int ResetPosition(uint sample)
    {
        if (sample > (uint)Math.Max(0, EndSample)) return ERR_INVALID_SAMPLE;
        _pos = sample + (long)Info.EncoderDelay;
        Done = _pos > EndFull;
        _cachedBlock = -1;
        _lastDecoded = -2;
        if (!AllOnMemory)
        {
            int block = Math.Max(0, (int)(_pos / SfSamples) - 1);
            _decoder.Reset();
            _write = _consume = 0;
            ReadPos = Info.DataOffset + (uint)block * (uint)SfBytes;
            _streamToFile = ReadPos;
            _linear = true;
            _nextStreamBlock = block;
            _readerLoops = LoopNum;
            if (HasLoop && _readerLoops != 0 && ReadPos >= LoopBoundary) _readerLoops = 0; // seek past the loop
        }
        return 0;
    }

    // ---- getters

    public uint Status
    {
        get
        {
            uint s = Done ? STATUS_ALL_DATA_WAS_DECODED : 0;
            if (AllOnMemory) s |= STATUS_ALL_DATA_IS_ON_MEMORY | STATUS_NONLOOP_PART_IS_ON_MEMORY | (HasLoop ? STATUS_LOOP_PART_IS_ON_MEMORY : 0);
            else if (ReadPos >= DataEnd) s |= STATUS_NONLOOP_PART_IS_ON_MEMORY;
            return s;
        }
    }

    /// <summary>1 (RESETABLE_PART) while the loop end has not been played yet, else 0.</summary>
    public uint LoopStatus => HasLoop && _pos <= LoopEndFull && !Done ? 1u : 0u;

    /// <summary>Samples still to be output, loops included; -1 with an infinite loop ahead.</summary>
    public long RemainSamples
    {
        get
        {
            if (Done) return 0;
            bool beforeLoopEnd = HasLoop && _pos <= LoopEndFull;
            if (beforeLoopEnd && LoopNum < 0) return -1;
            long rem = EndFull - _pos + 1;
            if (beforeLoopEnd && LoopNum > 0) rem += LoopNum * (LoopEndFull - LoopStartFull + 1);
            return Math.Max(0, rem);
        }
    }

    /// <summary>Samples decodable from the data already in the buffer.</summary>
    public long OutputableSamples
    {
        get
        {
            long remain = RemainSamples;
            if (AllOnMemory || Done) return remain;
            int cur = (int)(_pos / SfSamples);
            long lastBlock = (_cachedBlock == cur ? cur : _nextStreamBlock - 1) + Filled / SfBytes;
            long avail = Math.Max(0, (lastBlock + 1) * SfSamples - _pos);
            return remain < 0 ? avail : Math.Min(avail, remain);
        }
    }

    public int BitRateKbps => (int)(SfBytes * 8L * Cfg.SampleRate / SfSamples / 1000);
}
