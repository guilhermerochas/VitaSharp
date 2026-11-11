using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace VitaSharp.Core.Capture;

/// <summary>WAV helpers of the session capture: reading the dumped ports, the mix, simple checks.</summary>
public static class CaptureAudio
{
    public const int MixRate = 48000;

    /// <summary>Reads a 16-bit PCM WAV (mono or stereo) as stereo samples. The data size is taken from the file
    /// length when the header was not completed.</summary>
    public static (short[] Stereo, int Rate) ReadWav(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 44 || b[0] != 'R' || b[8] != 'W') throw new InvalidDataException($"{Path.GetFileName(path)}: not a WAV file");
        int ch = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(22)), rate = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24));
        int bits = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(34));
        if (bits != 16 || ch is < 1 or > 2) throw new InvalidDataException($"{Path.GetFileName(path)}: {bits}-bit {ch}-channel WAV not supported");
        int data = 36;
        while (data + 8 <= b.Length && !(b[data] == 'd' && b[data + 1] == 'a' && b[data + 2] == 't' && b[data + 3] == 'a'))
            data += 8 + BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(data + 4));
        int start = data + 8, bytes = Math.Max(0, b.Length - start) & ~(2 * ch - 1);
        var mono = new short[bytes / 2];
        Buffer.BlockCopy(b, start, mono, 0, bytes);
        if (ch == 2) return (mono, rate);
        var st = new short[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) st[2 * i] = st[2 * i + 1] = mono[i];
        return (st, rate);
    }

    public static void WriteWav(string path, ReadOnlySpan<short> stereo, int rate)
    {
        using var fs = File.Create(path);
        Span<byte> h = stackalloc byte[44];
        int bytes = stereo.Length * 2;
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 36 + bytes);
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], rate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], 4);
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], bytes);
        fs.Write(h);
        fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(stereo));
    }

    /// <summary>
    /// The audio ports mixed as the device would play them: each port's WAV placed at the time it opened, resampled
    /// to 48 kHz, summed and clipped. The NGS voice files are inside the ports' output already, so they are left out.
    /// </summary>
    public static void Mix(IReadOnlyList<(string File, double StartMs)> files, string output, int maxSeconds = 900)
    {
        var ports = files.Where(f => !Path.GetFileName(f.File).Contains("ngs_voice", StringComparison.Ordinal)).ToList();
        if (ports.Count == 0) return;
        var tracks = new List<(short[] S, int Rate, long Offset)>();
        long frames = 0;
        foreach (var (file, startMs) in ports)
        {
            if (!File.Exists(file)) continue;
            var (s, rate) = ReadWav(file);
            if (s.Length == 0 || rate <= 0) continue;
            long off = (long)Math.Max(0, startMs * MixRate / 1000);
            tracks.Add((s, rate, off));
            frames = Math.Max(frames, off + (long)(s.Length / 2 * (double)MixRate / rate));
        }
        frames = Math.Min(frames, (long)maxSeconds * MixRate);
        if (frames <= 0) return;
        var acc = new float[frames * 2];
        foreach (var (s, rate, off) in tracks)
        {
            long n = s.Length / 2;
            double step = rate / (double)MixRate;
            for (long o = off; o < frames; o++)
            {
                double pos = (o - off) * step;
                long i = (long)pos;
                if (i + 1 >= n) break;
                float t = (float)(pos - i);
                acc[2 * o] += s[2 * i] + (s[2 * i + 2] - s[2 * i]) * t;
                acc[2 * o + 1] += s[2 * i + 1] + (s[2 * i + 3] - s[2 * i + 1]) * t;
            }
        }
        var outS = new short[acc.Length];
        for (int i = 0; i < acc.Length; i++) outS[i] = (short)Math.Clamp(acc[i], short.MinValue, short.MaxValue);
        WriteWav(output, outS, MixRate);
    }

    /// <summary>One file's checks: level, clipping, silent stretches inside sound, sample jumps (clicks).</summary>
    public sealed record Check(string File, int Rate, double Seconds, double PeakDb, double RmsDb, double ClippedPct, int Gaps, double LongestGapMs, int Clicks, List<double> GapTimesS, List<double> ClickTimesS, double SoundSeconds = 0);

    public static Check Analyze(string path)
    {
        var (s, rate) = ReadWav(path);
        long n = s.Length / 2;
        double sum = 0;
        int peak = 0, clipped = 0, clicks = 0, gaps = 0;
        double longest = 0;
        var gapTimes = new List<double>();
        var clickTimes = new List<double>();
        // silent stretches between sound: 50 ms or more of exact zeros after sound and before more sound
        long zeroRun = 0, lastSound = -1;
        int minGap = rate / 20;
        // time with audible sound: 20 ms windows above -60 dBFS RMS
        int win = Math.Max(1, rate / 50), soundWindows = 0;
        double winSum = 0;
        for (long i = 0; i < n; i++)
        {
            int l = s[2 * i], r = s[2 * i + 1];
            winSum += (double)l * l + (double)r * r;
            if ((i + 1) % win == 0)
            {
                if (winSum / (2.0 * win) > 32.768 * 32.768) soundWindows++; // RMS above -60 dBFS (32.8 of 32768)
                winSum = 0;
            }
            int a = Math.Max(Math.Abs(l), Math.Abs(r));
            peak = Math.Max(peak, a);
            if (a >= 32767) clipped++;
            sum += (double)l * l + (double)r * r;
            if (l == 0 && r == 0) { zeroRun++; continue; }
            if (zeroRun >= minGap && lastSound >= 0)
            {
                gaps++;
                double ms = zeroRun * 1000.0 / rate;
                longest = Math.Max(longest, ms);
                if (gapTimes.Count < 50) gapTimes.Add((i - zeroRun) / (double)rate);
            }
            // a jump of more than half the range between two samples, with quiet neighbours: a click
            if (i > 1)
            {
                int d = Math.Abs(l - s[2 * i - 2]), dPrev = Math.Abs(s[2 * i - 2] - s[2 * i - 4]);
                if (d > 16384 && dPrev < 2048) { clicks++; if (clickTimes.Count < 50) clickTimes.Add(i / (double)rate); }
            }
            zeroRun = 0;
            lastSound = i;
        }
        double rms = Math.Sqrt(sum / Math.Max(1, 2 * n));
        static double Db(double v) => v <= 0 ? -120 : 20 * Math.Log10(v / 32768.0);
        return new Check(Path.GetFileName(path), rate, n / (double)rate, Db(peak), Db(rms), 100.0 * clipped / Math.Max(1, n), gaps, longest, clicks, gapTimes, clickTimes, soundWindows * (double)win / rate);
    }
}

/// <summary>A small PNG writer (RGBA8, one IDAT, zlib) for the analyzer's pictures.</summary>
public static class CapturePng
{
    public static void Write(string path, ReadOnlySpan<byte> rgba, int w, int h)
    {
        var raw = new byte[(w * 4 + 1) * h];
        for (int y = 0; y < h; y++)
        {
            raw[y * (w * 4 + 1)] = 0;
            rgba.Slice(y * w * 4, w * 4).CopyTo(raw.AsSpan(y * (w * 4 + 1) + 1));
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw);
        using var fs = File.Create(path);
        fs.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR"u8, ihdr);
        Chunk(fs, "IDAT"u8, z.GetBuffer().AsSpan(0, (int)z.Length));
        Chunk(fs, "IEND"u8, []);
    }

    static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var crc = new Crc32();
        crc.Append(type);
        crc.Append(data);
        s.Write(type);
        s.Write(data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc.GetCurrentHashAsUInt32());
        s.Write(c);
    }
}
