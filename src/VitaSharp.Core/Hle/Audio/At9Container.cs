using System.Buffers.Binary;

namespace VitaSharp.Core.Hle.Audio;

/// <summary>Outcome of <see cref="At9Container.Parse"/>.</summary>
public enum At9ParseResult
{
    Ok,
    /// <summary>The bytes end before the <c>data</c> chunk header: the caller must supply more of the file.</summary>
    TooSmall,
    /// <summary>Not RIFF/WAVE, or a mandatory chunk (<c>fmt </c>, <c>fact</c>, <c>data</c>) is missing or malformed.</summary>
    Invalid,
    /// <summary>A well-formed WAVE whose codec is not ATRAC9 (or with more channels than libatrac decodes).</summary>
    Unsupported,
}

/// <summary>
/// The fields libatrac needs from an <c>.at9</c> file: RIFF/WAVE with a WAVEFORMATEXTENSIBLE <c>fmt </c> chunk (subformat
/// ATRAC9), a <c>fact</c> chunk (total samples + encoder delay), an optional <c>smpl</c> chunk (first loop) and the
/// <c>data</c> chunk holding contiguous superframes.
/// </summary>
public sealed class At9Info
{
    public int Channels, SampleRate;
    /// <summary>fmt blockAlign: bytes per superframe.</summary>
    public int BlockAlign;
    /// <summary>fmt samplesPerBlock: samples per superframe.</summary>
    public int SamplesPerBlock;
    public uint ChannelMask, VersionInfo;
    /// <summary>The 4-byte ATRAC9 configData handed to the decoder.</summary>
    public byte[] Config = new byte[4];
    /// <summary>fact: playable samples (encoder delay excluded).</summary>
    public int TotalSamples;
    /// <summary>fact: samples the decoder produces before the first playable one.</summary>
    public int EncoderDelay;
    /// <summary>smpl loop points in output coordinates (encoder delay removed, end inclusive); -1 without a loop.</summary>
    public int LoopStart = -1, LoopEnd = -1;
    /// <summary>File offset of the first superframe and the data chunk size (clamped to the RIFF size).</summary>
    public uint DataOffset, DataSize;
    /// <summary>8 + RIFF chunk size.</summary>
    public uint FileSize;
    public bool HasLoop => LoopStart >= 0 && LoopEnd >= LoopStart;
}

/// <summary>Parser for the <c>.at9</c> RIFF container used by SceAtrac.</summary>
public static class At9Container
{
    const uint RIFF = 0x46464952, WAVE = 0x45564157, FMT = 0x20746D66, FACT = 0x74636166, SMPL = 0x6C706D73, DATA = 0x61746164;
    const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    /// <summary>KSDATAFORMAT_SUBTYPE_ATRAC9 {47E142D2-36BA-4D8D-88FC-61654F8C836C} in its on-disk (mixed-endian) byte order.</summary>
    public static readonly byte[] Atrac9Guid =
        [0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D, 0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C];

    /// <summary>
    /// Parses the header at the start of <paramref name="file"/> (only the first bytes of the file are required: parsing
    /// stops at the <c>data</c> chunk header). smpl loop points are stored in the file in decoder coordinates (they include
    /// the encoder delay, like AT3 on the PSP) and are returned with the delay removed.
    /// </summary>
    public static At9ParseResult Parse(ReadOnlySpan<byte> file, out At9Info info)
    {
        info = new At9Info();
        if (file.Length < 12) return At9ParseResult.TooSmall;
        if (U32(file, 0) != RIFF || U32(file, 8) != WAVE) return At9ParseResult.Invalid;
        info.FileSize = 8 + U32(file, 4);
        bool fmt = false, fact = false;
        int smplStart = -1, smplEnd = -1;
        int p = 12;
        while (true)
        {
            if (p + 8 > file.Length) return At9ParseResult.TooSmall;
            uint id = U32(file, p), size = U32(file, p + 4);
            int body = p + 8;
            if (id == DATA)
            {
                if (!fmt || !fact) return At9ParseResult.Invalid;
                info.DataOffset = (uint)body;
                info.DataSize = info.FileSize > info.DataOffset ? Math.Min(size, info.FileSize - info.DataOffset) : size;
                break;
            }
            if (size > int.MaxValue - body) return At9ParseResult.Invalid;
            // every chunk but data must be fully present to be read
            if (body + (int)size > file.Length && id is FMT or FACT or SMPL) return At9ParseResult.TooSmall;
            switch (id)
            {
                case FMT:
                {
                    if (size < 0x10) return At9ParseResult.Invalid;
                    var f = file.Slice(body, (int)size);
                    if (U16(f, 0) != WAVE_FORMAT_EXTENSIBLE) return At9ParseResult.Unsupported;
                    if (size < 0x30) return At9ParseResult.Invalid;
                    if (!f.Slice(0x18, 16).SequenceEqual(Atrac9Guid)) return At9ParseResult.Unsupported;
                    info.Channels = U16(f, 2);
                    info.SampleRate = (int)U32(f, 4);
                    info.BlockAlign = U16(f, 0x0C);
                    info.SamplesPerBlock = U16(f, 0x12);
                    info.ChannelMask = U32(f, 0x14);
                    info.VersionInfo = U32(f, 0x28);
                    f.Slice(0x2C, 4).CopyTo(info.Config);
                    if (info.Channels is < 1 or > 2) return At9ParseResult.Unsupported;
                    if (info.BlockAlign == 0) return At9ParseResult.Invalid;
                    fmt = true;
                    break;
                }
                case FACT:
                    if (size < 8) return At9ParseResult.Invalid;
                    info.TotalSamples = (int)U32(file, body);
                    info.EncoderDelay = (int)U32(file, body + 4);
                    fact = true;
                    break;
                case SMPL:
                    if (size >= 0x24 + 24 && U32(file, body + 0x1C) > 0)
                    {
                        smplStart = (int)U32(file, body + 0x24 + 8);
                        smplEnd = (int)U32(file, body + 0x24 + 12);
                    }
                    break;
            }
            p = body + (int)size + (int)(size & 1); // chunks are word aligned
        }
        if (info.TotalSamples < 0 || info.EncoderDelay < 0) return At9ParseResult.Invalid;
        if (smplStart >= 0 && smplEnd >= smplStart)
        {
            info.LoopStart = Math.Max(0, smplStart - info.EncoderDelay);
            info.LoopEnd = Math.Min(info.TotalSamples - 1, smplEnd - info.EncoderDelay);
            if (info.LoopEnd < info.LoopStart) info.LoopStart = info.LoopEnd = -1;
        }
        return At9ParseResult.Ok;
    }

    static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
}
