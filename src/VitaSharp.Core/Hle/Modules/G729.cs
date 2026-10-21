using FFmpeg.AutoGen;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceG729 (module SceLibG729, internal sysmodule 0x80000004): ITU-T G.729 voice codec, 10 ms frames of 80 S16
/// samples at 8 kHz ↔ 10 bytes. No header and no Vita3K module exist: prototypes are inferred from the Sony
/// context-based voice libraries (GetContextSize → app allocates → Init(ctx) → Core per frame → Reset). The names
/// are not in nids.txt, so every export carries its NID. Decoding uses FFmpeg's g729 decoder (silence without
/// FFmpeg); there is no G.729 encoder, so encoding emits a zero 10-byte frame. Error codes are unknown:
/// <see cref="SceError.INVALID_ARGUMENT"/> is used for null/unknown contexts.
/// </summary>
public sealed unsafe class G729 : HleModule
{
    internal const int ERR_INVALID_ARGUMENT = SceError.INVALID_ARGUMENT;
    internal const int ContextSize = 0x1000, FrameSamples = 80, FrameBytes = 10;

    sealed class DecState { public FrameDecoder? Codec; public bool Tried; }
    sealed class EncState { public uint Vad; }

    readonly object _lock = new();
    readonly Dictionary<uint, DecState> _dec = new();
    readonly Dictionary<uint, EncState> _enc = new();
    readonly short[] _pcm = new short[FrameSamples * 4];

    /// <summary>Inferred: SceInt32 (void) returning the context size. r0 is logged to tell it apart from a
    /// (SceUInt32 *pSize) variant; nothing is written through it.</summary>
    [Hle(Nid = 0x57DB3ADD)]
    int sceG729DecodeGetContextSize(uint r0)
    {
        Log.WarnOnce("g729-decsize", $"sceG729DecodeGetContextSize(r0=0x{r0:X8}): returning 0x{ContextSize:X} (prototype inferred)");
        return ContextSize;
    }

    [Hle(Nid = 0xFB053ABE)]
    int sceG729EncodeGetContextSize(uint r0)
    {
        Log.WarnOnce("g729-encsize", $"sceG729EncodeGetContextSize(r0=0x{r0:X8}): returning 0x{ContextSize:X} (prototype inferred)");
        return ContextSize;
    }

    [Hle(Nid = 0x7A74B98F)]
    int sceG729DecodeInit(uint ctx)
    {
        if (ctx == 0) return ERR_INVALID_ARGUMENT;
        lock (_lock)
        {
            if (_dec.Remove(ctx, out var old)) old.Codec?.Dispose();
            _dec[ctx] = new DecState();
        }
        return 0;
    }

    /// <summary>Inferred: (ctx, const void *in /*10 bytes*/, SceInt16 *out /*80 samples*/, ...). Frame-loss flags (if
    /// any) are not known: the frame is always decoded.</summary>
    [Hle(Nid = 0x918BE529)]
    int sceG729DecodeCore(uint ctx, uint input, uint output)
    {
        if (ctx == 0 || input == 0 || output == 0) return ERR_INVALID_ARGUMENT;
        lock (_lock)
        {
            if (!_dec.TryGetValue(ctx, out var d)) return ERR_INVALID_ARGUMENT;
            if (!d.Tried)
            {
                d.Tried = true;
                d.Codec = FrameDecoder.Create(AVCodecID.AV_CODEC_ID_G729, 8000, 1);
                if (d.Codec == null) Log.WarnOnce("g729-noffmpeg", "sceG729: FFmpeg g729 decoder unavailable, frames decode to silence");
            }
            int n = d.Codec?.Decode(GuestMemory.Ptr(input), FrameBytes, _pcm, 1) ?? 0;
            var dst = GuestMemory.Span<short>(output, FrameSamples);
            dst.Clear();
            _pcm.AsSpan(0, Math.Min(n, FrameSamples)).CopyTo(dst);
        }
        return 0;
    }

    [Hle(Nid = 0x60C5F97E)]
    int sceG729DecodeReset(uint ctx)
    {
        lock (_lock)
        {
            if (ctx == 0 || !_dec.TryGetValue(ctx, out var d)) return ERR_INVALID_ARGUMENT;
            d.Codec?.Flush();
        }
        return 0;
    }

    /// <summary>Inferred: (ctx, int enableVad?) — the second word is kept for logging only.</summary>
    [Hle(Nid = 0xAB9DA641)]
    int sceG729EncodeInit(uint ctx, uint vad)
    {
        if (ctx == 0) return ERR_INVALID_ARGUMENT;
        lock (_lock) _enc[ctx] = new EncState { Vad = vad };
        return 0;
    }

    /// <summary>Inferred: (ctx, const SceInt16 *in /*80 samples*/, void *out /*10 bytes*/). No G.729 encoder: a zero
    /// 10-byte frame is written (the bit pattern of a real silence frame is to be confirmed).</summary>
    [Hle(Nid = 0xFF57E664)]
    int sceG729EncodeCore(uint ctx, uint input, uint output)
    {
        if (ctx == 0 || input == 0 || output == 0) return ERR_INVALID_ARGUMENT;
        lock (_lock)
            if (!_enc.ContainsKey(ctx)) return ERR_INVALID_ARGUMENT;
        Log.WarnOnce("g729-enc", "sceG729: no G.729 encoder, emitting zero frames");
        GuestMemory.Fill(output, 0, FrameBytes);
        return 0;
    }

    [Hle(Nid = 0xA5B87504)]
    int sceG729EncodeReset(uint ctx)
    {
        lock (_lock)
            return ctx != 0 && _enc.ContainsKey(ctx) ? 0 : ERR_INVALID_ARGUMENT;
    }
}
