using System.Buffers.Binary;
using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Hle.Audio.Atrac9;

namespace VitaSharp.Core.Shell;

/// <summary>Decoded PCM: interleaved 16-bit samples.</summary>
public sealed record PcmSound(int SampleRate, int Channels, short[] Samples)
{
    public double Seconds => Samples.Length / (double)Math.Max(1, Channels) / Math.Max(1, SampleRate);
}

/// <summary>
/// VAG ("VAGp") sound effects, as stored in the system RCOs (sound/vag): a 0x30-byte big-endian header (version, data
/// size at 0x0C, sample rate at 0x10, name) followed by 16-byte SPU ADPCM frames {shift | filter &lt;&lt; 4, flags,
/// 28 4-bit samples}. Flags bit 0 marks the last frame.
/// </summary>
public static class Vag
{
    static readonly int[,] Filters = { { 0, 0 }, { 60, 0 }, { 115, -52 }, { 98, -55 }, { 122, -60 } };

    public static bool IsVag(ReadOnlySpan<byte> d) => d.Length >= 0x30 && d[0] == 'V' && d[1] == 'A' && d[2] == 'G' && d[3] == 'p';

    /// <summary>Mono samples of one VAG channel and its sample rate (the header's, else <paramref name="fallbackRate"/>).</summary>
    public static (short[] Samples, int Rate) DecodeMono(ReadOnlySpan<byte> d, int fallbackRate = 48000)
    {
        if (!IsVag(d)) throw new InvalidDataException("not a VAG sound");
        int rate = (int)BinaryPrimitives.ReadUInt32BigEndian(d[0x10..]);
        if (rate is <= 0 or > 192000) rate = fallbackRate;
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(d[0x0C..]);
        int end = size > 0 ? Math.Min(d.Length, 0x30 + size) : d.Length;
        var outS = new List<short>((end - 0x30) / 16 * 28);
        double h1 = 0, h2 = 0;
        for (int p = 0x30; p + 16 <= end; p += 16)
        {
            int shift = d[p] & 0xF, filter = Math.Min(d[p] >> 4, 4), flags = d[p + 1];
            if (flags == 7) break; // end-of-stream marker frame
            for (int i = 0; i < 28; i++)
            {
                int b = d[p + 2 + i / 2];
                int nib = (i & 1) == 0 ? b & 0xF : b >> 4;
                int s = (short)(nib << 12) >> shift;
                double v = s + (h1 * Filters[filter, 0] + h2 * Filters[filter, 1]) / 64.0;
                h2 = h1; h1 = v;
                outS.Add((short)Math.Clamp((int)Math.Round(v), short.MinValue, short.MaxValue));
            }
            if ((flags & 1) != 0) break;
        }
        return (outS.ToArray(), rate);
    }

    /// <summary>A mono or stereo (left + right files) VAG sound as PCM.</summary>
    public static PcmSound Decode(byte[] left, byte[]? right = null)
    {
        var (l, rate) = DecodeMono(left);
        if (right == null) return new PcmSound(rate, 1, l);
        var (r, _) = DecodeMono(right);
        int n = Math.Max(l.Length, r.Length);
        var st = new short[n * 2];
        for (int i = 0; i < n; i++)
        {
            st[i * 2] = i < l.Length ? l[i] : (short)0;
            st[i * 2 + 1] = i < r.Length ? r[i] : (short)0;
        }
        return new PcmSound(rate, 2, st);
    }
}

/// <summary>
/// Streams an .at9 file (the system BGM in pd0:data/systembgm, theme BGMs) as PCM, superframe by superframe, with the
/// managed ATRAC9 decoder. <see cref="Read"/> returns false at the end; <see cref="Rewind"/> starts over (the shell
/// loops its BGM).
/// </summary>
public sealed class At9Reader
{
    readonly byte[] _file;
    readonly At9Info _info;
    Atrac9Decoder _decoder = new();
    readonly short[][] _pcm;
    readonly byte[] _superframe;
    int _block;
    long _emitted;
    int _skip;

    public int SampleRate => _info.SampleRate;
    public int Channels => _info.Channels;
    public int TotalSamples => _info.TotalSamples;

    public At9Reader(byte[] file)
    {
        if (At9Container.Parse(file, out _info) != At9ParseResult.Ok) throw new InvalidDataException("not an ATRAC9 file");
        _file = file;
        _decoder.Initialize(_info.Config);
        var cfg = _decoder.Config;
        _pcm = new short[cfg.ChannelCount][];
        for (int i = 0; i < _pcm.Length; i++) _pcm[i] = new short[cfg.SuperframeSamples];
        _superframe = new byte[cfg.SuperframeBytes];
        _skip = _info.EncoderDelay;
    }

    public static At9Reader Open(string path) => new(File.ReadAllBytes(path));

    public void Rewind()
    {
        _decoder = new Atrac9Decoder();
        _decoder.Initialize(_info.Config);
        _block = 0; _emitted = 0; _skip = _info.EncoderDelay;
    }

    /// <summary>Decodes the next superframe into interleaved samples (count in <paramref name="frames"/>).</summary>
    public bool Read(List<short> into, out int frames)
    {
        frames = 0;
        var cfg = _decoder.Config;
        while (true)
        {
            long off = _info.DataOffset + (long)_block * cfg.SuperframeBytes;
            if (off + cfg.SuperframeBytes > Math.Min(_file.Length, (long)_info.DataOffset + _info.DataSize)) return false;
            if (_info.TotalSamples > 0 && _emitted >= _info.TotalSamples) return false;
            Array.Copy(_file, off, _superframe, 0, cfg.SuperframeBytes);
            _block++;
            _decoder.Decode(_superframe, _pcm);
            int n = cfg.SuperframeSamples, start = Math.Min(_skip, n);
            _skip -= start;
            int count = n - start;
            if (_info.TotalSamples > 0) count = (int)Math.Min(count, _info.TotalSamples - _emitted);
            if (count <= 0) continue;
            for (int i = start; i < start + count; i++)
                for (int c = 0; c < _pcm.Length; c++) into.Add(_pcm[c][i]);
            _emitted += count;
            frames = count;
            return true;
        }
    }
}
