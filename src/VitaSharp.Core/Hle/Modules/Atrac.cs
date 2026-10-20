using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Hle.Audio.Atrac9;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceAtrac (libatrac): .at9 playback on top of the ATRAC9 decoder, with the whole file in memory or streamed through a
/// game-owned ring buffer, loops and seek. HLE fallback: sceSysmoduleLoadModule(SCE_SYSMODULE_ATRAC) loads the real
/// libatrac.suprx (game-shipped or vs0:sys/external) by LLE when present, which rebinds these imports.
/// Structs (psp2/atrac.h): SceAtracDecoderGroup 0xC, SceAtracContentInfo 0x34, SceAtracStreamInfo 0x10.
/// Handles are (SCE_ATRAC_TYPE_AT9 &lt;&lt; 16) | slot.
/// </summary>
public sealed class Atrac : HleModule
{
    internal const int ERR_INVALID_POINTER = unchecked((int)0x80630000), ERR_INVALID_SIZE = unchecked((int)0x80630001),
        ERR_INVALID_WORD_LENGTH = unchecked((int)0x80630002), ERR_INVALID_TYPE = unchecked((int)0x80630003),
        ERR_INVALID_TOTAL_CH = unchecked((int)0x80630004), ERR_INVALID_ALIGNMENT = unchecked((int)0x80630005),
        ERR_ALREADY_CREATED = unchecked((int)0x80630006), ERR_NOT_CREATED = unchecked((int)0x80630007),
        ERR_SHORTAGE_OF_CH = unchecked((int)0x80630008), ERR_UNSUPPORTED_DATA = unchecked((int)0x80630009),
        ERR_INVALID_DATA = unchecked((int)0x8063000A), ERR_READ_SIZE_IS_TOO_SMALL = unchecked((int)0x8063000B),
        ERR_INVALID_HANDLE = unchecked((int)0x8063000C), ERR_READ_SIZE_OVER_BUFFER = unchecked((int)0x8063000D),
        ERR_MAIN_BUFFER_SIZE_IS_TOO_SMALL = unchecked((int)0x8063000E), ERR_INVALID_MAX_OUTPUT_SAMPLES = unchecked((int)0x80630012),
        ERR_NO_NEED_SUB_BUFFER = unchecked((int)0x80630016), ERR_REMAIN_VALID_HANDLE = unchecked((int)0x80630018),
        ERR_INVALID_LOOP_NUM = unchecked((int)0x80630030);
    const uint TYPE_AT9 = 0x2003, ALIGN = 0x100, WORD_LENGTH_16 = 16, MAX_TOTAL_CH = 16, GROUP_SIZE = 0xC,
        CONTENT_INFO_SIZE = 0x34, STREAM_INFO_SIZE = 0x10, MAX_OUTPUT_SAMPLES = 2048, MAX_OUTPUT_FRAMES = 8;
    const uint AUDIODEC_TYPE_AT9 = 0x1003;

    readonly object _lock = new();
    bool _created;
    uint _totalCh, _usedCh;
    readonly Dictionary<int, AtracStream> _handles = new();

    /// <summary>Superframe decoder per acquired handle (tests substitute a deterministic one).</summary>
    internal Func<At9Info, IAt9SuperframeDecoder> DecoderFactory = info => new ManagedAt9Decoder(info.Config);

    Audiodec? AudiodecModule => Emu?.Hle.Modules.OfType<Audiodec>().FirstOrDefault();

    static int CheckGroup(uint group)
    {
        if (group == 0) return ERR_INVALID_POINTER;
        if (GuestMemory.R32(group) != GROUP_SIZE) return ERR_INVALID_SIZE;
        if (GuestMemory.R32(group + 4) != WORD_LENGTH_16) return ERR_INVALID_WORD_LENGTH;
        return 0;
    }

    static int CheckTypeAndCh(uint atracType, uint group)
    {
        if (atracType != TYPE_AT9) return ERR_INVALID_TYPE;
        uint ch = GuestMemory.R32(group + 8);
        return ch is 0 or > MAX_TOTAL_CH ? ERR_INVALID_TOTAL_CH : 0;
    }

    [Hle]
    int sceAtracQueryDecoderGroupMemSize(uint atracType, uint pDecoderGroup)
    {
        int r = CheckGroup(pDecoderGroup);
        if (r == 0) r = CheckTypeAndCh(atracType, pDecoderGroup);
        // the HLE does not use the work memory; Vita3K's estimate (totalCh << 10) is already 0x100 aligned
        return r != 0 ? r : (int)(GuestMemory.R32(pDecoderGroup + 8) << 10);
    }

    [Hle]
    int sceAtracCreateDecoderGroup(uint atracType, uint pDecoderGroup, uint pvWorkMem, int initAudiodecFlag)
    {
        int r = CheckGroup(pDecoderGroup);
        if (r != 0) return r;
        if (pvWorkMem == 0) return ERR_INVALID_POINTER;
        if ((pvWorkMem & (ALIGN - 1)) != 0) return ERR_INVALID_ALIGNMENT;
        r = CheckTypeAndCh(atracType, pDecoderGroup);
        if (r != 0) return r;
        lock (_lock)
        {
            if (_created) return ERR_ALREADY_CREATED;
            if (initAudiodecFlag != 0)
            {
                r = AudiodecModule?.InitLibraryInternal(AUDIODEC_TYPE_AT9) ?? 0;
                if (r < 0) return r;
            }
            _created = true;
            _totalCh = GuestMemory.R32(pDecoderGroup + 8);
            _usedCh = 0;
        }
        return 0;
    }

    [Hle]
    int sceAtracDeleteDecoderGroup(uint atracType, int termAudiodecFlag)
    {
        if (atracType != TYPE_AT9) return ERR_INVALID_TYPE;
        lock (_lock)
        {
            if (!_created) return ERR_NOT_CREATED;
            if (_handles.Count != 0) return ERR_REMAIN_VALID_HANDLE;
            if (termAudiodecFlag != 0)
            {
                int r = AudiodecModule?.TermLibraryInternal(AUDIODEC_TYPE_AT9) ?? 0;
                if (r < 0) return r;
            }
            _created = false;
            _totalCh = _usedCh = 0;
        }
        return 0;
    }

    [Hle]
    int sceAtracGetDecoderGroupInfo(uint atracType, uint pCreatedDecoder, uint pAvailableDecoder)
    {
        if (pCreatedDecoder == 0 || pAvailableDecoder == 0) return ERR_INVALID_POINTER;
        // Vita3K reports a wrong size as INVALID_POINTER too
        if (GuestMemory.R32(pCreatedDecoder) != GROUP_SIZE || GuestMemory.R32(pAvailableDecoder) != GROUP_SIZE) return ERR_INVALID_POINTER;
        if (atracType != TYPE_AT9) return ERR_INVALID_TYPE;
        lock (_lock)
        {
            if (!_created) return ERR_NOT_CREATED;
            GuestMemory.W32(pCreatedDecoder + 4, WORD_LENGTH_16);
            GuestMemory.W32(pCreatedDecoder + 8, _totalCh);
            GuestMemory.W32(pAvailableDecoder + 4, WORD_LENGTH_16);
            GuestMemory.W32(pAvailableDecoder + 8, _totalCh - _usedCh);
        }
        return 0;
    }

    [Hle]
    int sceAtracSetDataAndAcquireHandle(uint pucBuffer, uint uiReadSize, uint uiBufferSize)
    {
        if (pucBuffer == 0) return ERR_INVALID_POINTER;
        if ((pucBuffer & (ALIGN - 1)) != 0 || (uiBufferSize & (ALIGN - 1)) != 0) return ERR_INVALID_ALIGNMENT;
        if (uiReadSize > uiBufferSize) return ERR_READ_SIZE_OVER_BUFFER;
        lock (_lock)
        {
            if (!_created) return ERR_NOT_CREATED;
            var res = At9Container.Parse(GuestMemory.Span(pucBuffer, (int)uiReadSize), out var info);
            switch (res)
            {
                case At9ParseResult.TooSmall: return ERR_READ_SIZE_IS_TOO_SMALL;
                case At9ParseResult.Invalid: return ERR_INVALID_DATA;
                case At9ParseResult.Unsupported: return ERR_UNSUPPORTED_DATA;
            }
            Atrac9Config cfg;
            try { cfg = new Atrac9Config(info.Config); }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException)
            {
                return ERR_UNSUPPORTED_DATA;
            }
            if (cfg.ChannelCount != info.Channels) return ERR_INVALID_DATA;
            if (info.BlockAlign != cfg.SuperframeBytes)
                Log.WarnOnce("atrac-blockalign", $"sceAtrac: fmt blockAlign {info.BlockAlign} != superframe size {cfg.SuperframeBytes}; using the config");
            bool allOnMemory = uiReadSize >= info.FileSize || uiReadSize >= info.DataOffset + info.DataSize;
            if (!allOnMemory && uiBufferSize < info.DataOffset + (uint)cfg.SuperframeBytes) return ERR_MAIN_BUFFER_SIZE_IS_TOO_SMALL;
            if (_totalCh - _usedCh < (uint)info.Channels) return ERR_SHORTAGE_OF_CH;
            int slot = 0;
            while (_handles.ContainsKey(slot)) slot++;
            var s = new AtracStream(info, cfg, DecoderFactory(info), pucBuffer, uiReadSize, uiBufferSize);
            _handles[slot] = s;
            _usedCh += (uint)info.Channels;
            Log.Debug($"sceAtrac: handle {slot}: {info.Channels}ch {info.SampleRate} Hz, {info.TotalSamples} samples, loop {info.LoopStart}..{info.LoopEnd}, {(allOnMemory ? "in memory" : "streaming")}");
            return (int)(TYPE_AT9 << 16) | slot;
        }
    }

    AtracStream? Find(int handle)
    {
        if ((handle >> 16) != TYPE_AT9) return null;
        return _handles.GetValueOrDefault(handle & 0xFFFF);
    }

    [Hle]
    int sceAtracReleaseHandle(int atracHandle)
    {
        lock (_lock)
        {
            var s = Find(atracHandle);
            if (s == null) return ERR_INVALID_HANDLE;
            _handles.Remove(atracHandle & 0xFFFF);
            _usedCh -= (uint)s.Channels;
        }
        return 0;
    }

    [Hle]
    int sceAtracDecode(int atracHandle, uint pOutputBuffer, Ptr<uint> pOutputSamples, Ptr<uint> pDecoderStatus)
    {
        if (pOutputBuffer == 0 || pOutputSamples.IsNull || pDecoderStatus.IsNull) return ERR_INVALID_POINTER;
        if ((pOutputBuffer & 1) != 0) return ERR_INVALID_ALIGNMENT;
        lock (_lock)
        {
            var s = Find(atracHandle);
            if (s == null) return ERR_INVALID_HANDLE;
            int r = s.Decode(GuestMemory.Span<short>(pOutputBuffer, s.OutputSamples * s.Channels), out int samples);
            pOutputSamples.Value = (uint)samples;
            pDecoderStatus.Value = s.Status;
            return r;
        }
    }

    [Hle]
    int sceAtracGetStreamInfo(int atracHandle, uint pStreamInfo)
    {
        if (pStreamInfo == 0) return ERR_INVALID_POINTER;
        if (GuestMemory.R32(pStreamInfo) != STREAM_INFO_SIZE) return ERR_INVALID_SIZE;
        lock (_lock)
        {
            var s = Find(atracHandle);
            if (s == null) return ERR_INVALID_HANDLE;
            var (writePtr, readPos, writable) = s.StreamInfo();
            GuestMemory.W32(pStreamInfo + 4, writePtr);
            GuestMemory.W32(pStreamInfo + 8, readPos);
            GuestMemory.W32(pStreamInfo + 12, writable);
        }
        return 0;
    }

    [Hle]
    int sceAtracAddStreamData(int atracHandle, uint addSize)
    {
        lock (_lock) return Find(atracHandle)?.AddStreamData(addSize) ?? ERR_INVALID_HANDLE;
    }

    /// <summary>The HLE ring receives the superframes in play order (the reader is sent back to the loop start), so a
    /// separate sub-buffer for the loop part is never needed.</summary>
    [Hle]
    int sceAtracIsSubBufferNeeded(int atracHandle)
    {
        lock (_lock) return Find(atracHandle) == null ? ERR_INVALID_HANDLE : 0;
    }

    [Hle]
    int sceAtracGetSubBufferInfo(int atracHandle, uint pReadPosition, uint pMinSubBufferSize, uint pDataSize)
    {
        if (pReadPosition == 0 || pMinSubBufferSize == 0 || pDataSize == 0) return ERR_INVALID_POINTER;
        lock (_lock) return Find(atracHandle) == null ? ERR_INVALID_HANDLE : ERR_NO_NEED_SUB_BUFFER;
    }

    [Hle]
    int sceAtracSetSubBuffer(int atracHandle, uint pSubBuffer, uint subBufferSize)
    {
        if ((pSubBuffer & (ALIGN - 1)) != 0 || (subBufferSize & (ALIGN - 1)) != 0) return ERR_INVALID_ALIGNMENT;
        if (pSubBuffer == 0) return ERR_INVALID_POINTER;
        lock (_lock) return Find(atracHandle) == null ? ERR_INVALID_HANDLE : ERR_NO_NEED_SUB_BUFFER;
    }

    [Hle]
    int sceAtracSetLoopNum(int atracHandle, int loopNum)
    {
        if (loopNum < -1) return ERR_INVALID_LOOP_NUM;
        lock (_lock) return Find(atracHandle)?.SetLoopNum(loopNum) ?? ERR_INVALID_HANDLE;
    }

    [Hle]
    int sceAtracSetOutputSamples(int atracHandle, uint outputSamples)
    {
        lock (_lock)
        {
            var s = Find(atracHandle);
            if (s == null) return ERR_INVALID_HANDLE;
            uint frame = (uint)s.Cfg.FrameSamples;
            if (outputSamples == 0 || outputSamples > MAX_OUTPUT_SAMPLES || outputSamples % frame != 0 || outputSamples > frame * MAX_OUTPUT_FRAMES)
                return ERR_INVALID_MAX_OUTPUT_SAMPLES;
            s.OutputSamples = (int)outputSamples;
        }
        return 0;
    }

    [Hle]
    int sceAtracResetNextOutputPosition(int atracHandle, uint resetSample)
    {
        lock (_lock) return Find(atracHandle)?.ResetPosition(resetSample) ?? ERR_INVALID_HANDLE;
    }

    [Hle]
    int sceAtracGetContentInfo(int atracHandle, uint pContentInfo)
    {
        if (pContentInfo == 0) return ERR_INVALID_POINTER;
        if (GuestMemory.R32(pContentInfo) != CONTENT_INFO_SIZE) return ERR_INVALID_SIZE;
        lock (_lock)
        {
            var s = Find(atracHandle);
            if (s == null) return ERR_INVALID_HANDLE;
            var i = s.Info;
            uint p = pContentInfo;
            GuestMemory.W32(p + 0x04, TYPE_AT9);
            GuestMemory.W32(p + 0x08, (uint)s.Channels);
            GuestMemory.W32(p + 0x0C, (uint)s.Cfg.SampleRate);
            GuestMemory.W32(p + 0x10, (uint)s.EndSample);
            GuestMemory.W32(p + 0x14, (uint)(i.HasLoop ? i.LoopStart : -1));
            GuestMemory.W32(p + 0x18, (uint)(i.HasLoop ? i.LoopEnd : -1));
            GuestMemory.W32(p + 0x1C, (uint)s.BitRateKbps);
            GuestMemory.W32(p + 0x20, (uint)s.SfBytes);
            GuestMemory.W32(p + 0x24, (uint)s.SfSamples);
            GuestMemory.W32(p + 0x28, (uint)s.Cfg.FrameSamples);
            GuestMemory.W32(p + 0x2C, i.HasLoop ? s.LoopRestart : 0);
            GuestMemory.W32(p + 0x30, i.HasLoop ? s.LoopBoundary - s.LoopRestart : 0);
        }
        return 0;
    }

    // ---- simple getters: the pointer is checked before the handle (Vita3K order)

    int Get(int handle, uint ptr, Action<AtracStream> write)
    {
        if (ptr == 0) return ERR_INVALID_POINTER;
        lock (_lock)
        {
            var s = Find(handle);
            if (s == null) return ERR_INVALID_HANDLE;
            write(s);
        }
        return 0;
    }

    [Hle]
    int sceAtracGetLoopInfo(int atracHandle, Ptr<int> pLoopNum, Ptr<uint> pLoopStatus)
    {
        if (pLoopNum.IsNull || pLoopStatus.IsNull) return ERR_INVALID_POINTER;
        return Get(atracHandle, pLoopNum, s => { pLoopNum.Value = s.LoopNum; pLoopStatus.Value = s.LoopStatus; });
    }

    [Hle] int sceAtracGetOutputSamples(int atracHandle, Ptr<uint> p) => Get(atracHandle, p, s => p.Value = (uint)s.OutputSamples);
    [Hle] int sceAtracGetNextOutputPosition(int atracHandle, Ptr<uint> p) => Get(atracHandle, p, s => p.Value = (uint)s.NextOutputSample);
    [Hle] int sceAtracGetRemainSamples(int atracHandle, Ptr<long> p) => Get(atracHandle, p, s => p.Value = s.RemainSamples);
    [Hle] int sceAtracGetOutputableSamples(int atracHandle, Ptr<long> p) => Get(atracHandle, p, s => p.Value = s.OutputableSamples);
    [Hle] int sceAtracGetDecoderStatus(int atracHandle, Ptr<uint> p) => Get(atracHandle, p, s => p.Value = s.Status);
    [Hle] int sceAtracGetVacantSize(int atracHandle, Ptr<uint> p) => Get(atracHandle, p, s => p.Value = s.VacantSize);
    [Hle] int sceAtracGetInternalError(int atracHandle, Ptr<int> p) => Get(atracHandle, p, s => p.Value = s.InternalError);
}
