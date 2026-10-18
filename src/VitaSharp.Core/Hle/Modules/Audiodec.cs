using FFmpeg.AutoGen;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using At9Decoder = VitaSharp.Core.Hle.Audio.Atrac9.Atrac9Decoder;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceAudiodec: frame-by-frame decoding of ATRAC9 (built-in decoder), MP3 and AAC (FFmpeg) into S16 PCM.
/// SceAudiodecCtrl (0x28 bytes): size, handle, pEs, inputEsSize, maxEsSize, pPcm, outputPcmSize, maxPcmSize,
/// wordLength, pInfo. SceAudiodecInfo starts with its size followed by the codec-specific fields.
/// </summary>
public sealed unsafe class Audiodec : HleModule
{
    const int ERR_API_FAIL = unchecked((int)0x807F0000), ERR_INVALID_TYPE = unchecked((int)0x807F0001),
        ERR_NOT_INITIALIZED = unchecked((int)0x807F0005), ERR_INVALID_PTR = unchecked((int)0x807F0008),
        ERR_INVALID_HANDLE = unchecked((int)0x807F0009), ERR_NOT_HANDLE_IN_USE = unchecked((int)0x807F000A),
        ERR_INVALID_SIZE = unchecked((int)0x807F000D), ERR_AT9_INVALID_CONFIG = unchecked((int)0x807F2000),
        ERR_MP3_INVALID_VERSION = unchecked((int)0x807F2801);
    const uint TYPE_AT9 = 0x1003, TYPE_MP3 = 0x1004, TYPE_AAC = 0x1005, TYPE_CELP = 0x1006;
    const uint CtrlSize = 0x28;

    sealed class Decoder
    {
        public uint Type;
        public int Channels;
        // ATRAC9
        public At9Decoder? At9;
        public short[][] At9Pcm = [];
        public int FrameIndex;
        public byte[] Staging = [];
        // MP3 / AAC
        public FrameDecoder? Codec;
        public bool Adts;
        public short[] Pcm = [];
    }

    readonly HashSet<uint> _libraries = new();
    readonly Dictionary<uint, Decoder> _decoders = new();
    uint _nextHandle = 1;

    [Hle]
    int sceAudiodecInitLibrary(uint codecType, uint initParam)
    {
        if (codecType is < TYPE_AT9 or > TYPE_CELP) return ERR_INVALID_TYPE;
        if (initParam == 0) return ERR_INVALID_PTR;
        lock (_libraries) _libraries.Add(codecType);
        return 0;
    }

    [Hle]
    int sceAudiodecTermLibrary(uint codecType)
    {
        if (codecType is < TYPE_AT9 or > TYPE_CELP) return ERR_INVALID_TYPE;
        lock (_libraries) return _libraries.Remove(codecType) ? 0 : ERR_NOT_INITIALIZED;
    }

    [Hle] int sceAudiodecCreateDecoder(uint ctrl, uint codecType) => Create(ctrl, codecType, true);
    // external decoders live in memory the application provides: no sceAudiodecInitLibrary needed
    [Hle] int sceAudiodecCreateDecoderExternal(uint ctrl, uint codecType, uint context, uint size) => Create(ctrl, codecType, false);
    [Hle] int sceAudiodecGetContextSize(uint ctrl, uint codecType) => 0x10000;

    int Create(uint ctrl, uint codecType, bool needsLibrary)
    {
        if (ctrl == 0) return ERR_INVALID_PTR;
        if (GuestMemory.R32(ctrl) != CtrlSize) return ERR_INVALID_SIZE;
        if (codecType is < TYPE_AT9 or > TYPE_CELP) return ERR_INVALID_TYPE;
        if (needsLibrary) lock (_libraries) if (!_libraries.Contains(codecType)) return ERR_NOT_INITIALIZED;
        uint info = GuestMemory.R32(ctrl + 36);
        if (info == 0) return ERR_INVALID_PTR;
        var d = new Decoder { Type = codecType };
        uint esMax, pcmMax;
        switch (codecType)
        {
            case TYPE_AT9:
            {
                uint config = GuestMemory.R32(info + 4);
                try
                {
                    d.At9 = new At9Decoder();
                    d.At9.Initialize(BitConverter.GetBytes(config));
                }
                catch (Exception)
                {
                    return ERR_AT9_INVALID_CONFIG;
                }
                var c = d.At9.Config;
                d.Channels = c.ChannelCount;
                d.At9Pcm = new short[c.ChannelCount][];
                for (int i = 0; i < c.ChannelCount; i++) d.At9Pcm[i] = new short[c.FrameSamples];
                d.Staging = new byte[c.SuperframeBytes + 16];
                // info: size, configData, ch, bitRate, samplingRate, superFrameSize, framesInSuperFrame
                GuestMemory.W32(info + 8, (uint)c.ChannelCount);
                GuestMemory.W32(info + 12, (uint)(c.SuperframeBytes * 8L * c.SampleRate / c.SuperframeSamples / 1000));
                GuestMemory.W32(info + 16, (uint)c.SampleRate);
                GuestMemory.W32(info + 20, (uint)c.SuperframeBytes);
                GuestMemory.W32(info + 24, (uint)c.FramesPerSuperframe);
                esMax = (uint)Math.Min(c.SuperframeBytes, 1024);
                pcmMax = (uint)(c.FrameSamples * c.ChannelCount * 2);
                break;
            }
            case TYPE_MP3:
            {
                // info: size, ch, version (0 = 2.5, 2 = 2, 3 = 1)
                int ch = (int)GuestMemory.R32(info + 4);
                uint version = GuestMemory.R32(info + 8);
                if (version is not (0 or 2 or 3)) return ERR_MP3_INVALID_VERSION;
                d.Channels = Math.Clamp(ch, 1, 2);
                d.Codec = FrameDecoder.Create(AVCodecID.AV_CODEC_ID_MP3, 0, d.Channels);
                esMax = 1441;
                pcmMax = (uint)(d.Channels * (version == 3 ? 2304 : 1152));
                break;
            }
            case TYPE_AAC:
            {
                // info: size, isAdts, ch, samplingRate, isSbr
                d.Adts = GuestMemory.R32(info + 4) != 0;
                d.Channels = Math.Clamp((int)GuestMemory.R32(info + 8), 1, 2);
                int rate = (int)GuestMemory.R32(info + 12);
                bool sbr = GuestMemory.R32(info + 16) != 0;
                d.Codec = FrameDecoder.Create(AVCodecID.AV_CODEC_ID_AAC, rate, d.Channels, d.Adts ? null : FrameDecoder.AacConfig(sbr ? rate / 2 : rate, d.Channels));
                esMax = d.Adts ? 1536u + 0x100 : 1536u;
                pcmMax = (uint)(d.Channels * 2048 * (sbr ? 2 : 1));
                break;
            }
            default:
                Log.WarnOnce("audiodec" + codecType, $"sceAudiodec: codec 0x{codecType:X} (CELP) is not supported");
                return ERR_INVALID_TYPE;
        }
        if (d.Codec == null && d.At9 == null)
            Log.WarnOnce("audiodec-noffmpeg", "sceAudiodec: FFmpeg is unavailable, MP3/AAC frames decode to silence");
        d.Pcm = new short[pcmMax / 2 + 4096];
        lock (_decoders)
        {
            uint h = _nextHandle++;
            _decoders[h] = d;
            GuestMemory.W32(ctrl + 4, h);
        }
        GuestMemory.W32(ctrl + 16, esMax);
        GuestMemory.W32(ctrl + 28, pcmMax);
        return 0;
    }

    Decoder? Find(uint ctrl, out int error)
    {
        error = 0;
        if (ctrl == 0) { error = ERR_INVALID_PTR; return null; }
        if (GuestMemory.R32(ctrl) != CtrlSize) { error = ERR_INVALID_SIZE; return null; }
        uint h = GuestMemory.R32(ctrl + 4);
        if (h == 0) { error = ERR_INVALID_HANDLE; return null; }
        lock (_decoders)
        {
            if (_decoders.TryGetValue(h, out var d)) return d;
        }
        error = ERR_NOT_HANDLE_IN_USE;
        return null;
    }

    [Hle] int sceAudiodecDecode(uint ctrl) => DecodeFrames(ctrl, 1);
    [Hle] int sceAudiodecDecodeNFrames(uint ctrl, uint frames) => DecodeFrames(ctrl, Math.Max(frames, 1));

    int DecodeFrames(uint ctrl, uint frames)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        uint es = GuestMemory.R32(ctrl + 8), pcm = GuestMemory.R32(ctrl + 20);
        uint esMax = GuestMemory.R32(ctrl + 16);
        if (es == 0 || pcm == 0) return ERR_INVALID_PTR;
        uint esUsed = 0, pcmBytes = 0;
        for (uint f = 0; f < frames; f++)
        {
            int consumed, samples;
            if (d.At9 != null)
            {
                int avail = (int)Math.Min((uint)d.Staging.Length - 16, esMax);
                GuestMemory.Span(es + esUsed, avail).CopyTo(d.Staging);
                try { consumed = d.At9.DecodeFrame(d.Staging, d.FrameIndex, d.At9Pcm); }
                catch (Exception e) when (e is IndexOutOfRangeException or InvalidDataException or ArgumentException)
                {
                    Log.WarnOnce("audiodec-at9", $"sceAudiodec ATRAC9 frame error: {e.Message}");
                    return ERR_API_FAIL;
                }
                d.FrameIndex = (d.FrameIndex + 1) % d.At9.Config.FramesPerSuperframe;
                samples = d.At9.Config.FrameSamples;
                for (int i = 0; i < samples; i++)
                    for (int c = 0; c < d.Channels; c++)
                        d.Pcm[i * d.Channels + c] = d.At9Pcm[c][i];
            }
            else
            {
                var head = GuestMemory.Span(es + esUsed, 8);
                consumed = d.Type == TYPE_MP3 ? FrameDecoder.Mp3FrameLength(head)
                         : d.Adts ? FrameDecoder.AdtsFrameLength(head) : (int)esMax;
                if (consumed <= 0) return ERR_API_FAIL;
                consumed = (int)Math.Min((uint)consumed, esMax);
                samples = d.Codec?.Decode(GuestMemory.Ptr(es + esUsed), consumed, d.Pcm, d.Channels) ?? (d.Type == TYPE_MP3 ? 1152 : 1024);
                if (d.Codec == null) Array.Clear(d.Pcm, 0, samples * d.Channels);
            }
            int bytes = samples * d.Channels * 2;
            uint pcmCap = GuestMemory.R32(ctrl + 28); // maxPcmSize reported at creation: never write past the guest buffer
            if (pcmCap != 0 && bytes > pcmCap) bytes = (int)pcmCap / (d.Channels * 2) * (d.Channels * 2);
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(d.Pcm.AsSpan(0, bytes / 2)).CopyTo(GuestMemory.Span(pcm + pcmBytes, bytes));
            esUsed += (uint)consumed;
            pcmBytes += (uint)bytes;
        }
        GuestMemory.W32(ctrl + 12, esUsed);
        GuestMemory.W32(ctrl + 24, pcmBytes);
        return 0;
    }

    [Hle]
    int sceAudiodecClearContext(uint ctrl)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        d.FrameIndex = 0;
        d.Codec?.Flush();
        return 0;
    }

    [Hle]
    int sceAudiodecDeleteDecoder(uint ctrl)
    {
        if (ctrl == 0) return ERR_INVALID_PTR;
        if (GuestMemory.R32(ctrl) != CtrlSize) return ERR_INVALID_SIZE;
        Decoder? d;
        lock (_decoders)
        {
            if (!_decoders.Remove(GuestMemory.R32(ctrl + 4), out d)) return ERR_NOT_HANDLE_IN_USE;
        }
        d.Codec?.Dispose();
        return 0;
    }

    [Hle] int sceAudiodecDeleteDecoderExternal(uint ctrl, uint context) => sceAudiodecDeleteDecoder(ctrl);

    [Hle]
    int sceAudiodecGetInternalError(uint ctrl, Ptr<int> error)
    {
        var d = Find(ctrl, out int err);
        if (d == null) return err;
        error.TrySet(0);
        return 0;
    }
}
