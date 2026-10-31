using System.Buffers.Binary;

namespace VitaSharp.PerfTools;

/// <summary>
/// <c>audio-check &lt;dir|file.wav&gt;... [--grain 512] [--click 12000]</c>: per guest audio WAV (VITASHARP_AUDIO_DUMP on
/// Android, --audio-dump on the desktop), what goes wrong when the producer is late: silent gaps inside the sound
/// (runs of zero samples of at least a grain, not counting the leading/trailing silence), repeated grains (a grain
/// equal to the one before it, non-silent), clicks (a jump between neighbouring samples above the threshold) and
/// the duration. Run it on the phone's and the desktop's dumps of the same scene and compare the lines.
/// </summary>
sealed class AudioCheckCommand : IPerfCommand
{
    public string Name => "audio-check";
    public string Summary => "silent gaps, repeated grains and clicks in guest audio WAV dumps";

    public void PrintUsage()
    {
        Console.WriteLine("vitasharp-perf audio-check <dir|file.wav>... [--grain 512] [--click 12000]");
        Console.WriteLine("  16-bit PCM WAVs (VITASHARP_AUDIO_DUMP / --audio-dump); one line per file");
    }

    public int Run(PerfArgs a)
    {
        if (a.Positional.Count == 0) throw new PerfArgsException("audio-check: a WAV file or a folder of WAVs is required");
        int grain = (int)a.Double("--grain", 512);
        int click = (int)a.Double("--click", 12000);
        var files = a.Positional.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.wav", SearchOption.AllDirectories).Order() : (IEnumerable<string>)[p]).ToList();
        foreach (var f in files)
        {
            var (pcm, ch, rate) = ReadWav(f);
            if (ch == 0) { Console.WriteLine($"{Path.GetFileName(f)}: not a 16-bit PCM WAV"); continue; }
            int frames = pcm.Length / ch;
            // first and last non-zero frame: the silence around the sound is not a gap
            int first = 0, last = frames - 1;
            while (first < frames && Silent(pcm, first, ch)) first++;
            while (last > first && Silent(pcm, last, ch)) last--;
            int gaps = 0, gapFrames = 0, run = 0;
            for (int i = first; i <= last; i++)
            {
                if (Silent(pcm, i, ch)) { run++; continue; }
                if (run >= grain) { gaps++; gapFrames += run; }
                run = 0;
            }
            int repeats = 0;
            for (int g = first + grain; g + grain <= last; g += grain)
                if (!AllSilent(pcm, g, grain, ch) && pcm.AsSpan(g * ch, grain * ch).SequenceEqual(pcm.AsSpan((g - grain) * ch, grain * ch))) repeats++;
            int clicks = 0;
            for (int i = Math.Max(first, 1); i <= last; i++)
                for (int c = 0; c < ch; c++)
                    if (Math.Abs(pcm[i * ch + c] - pcm[(i - 1) * ch + c]) > click) { clicks++; break; }
            Console.WriteLine($"{Path.GetFileName(f)}: {frames / (double)rate:F2} s ({ch} ch, {rate} Hz), sound {(last - first + 1) / (double)rate:F2} s; " +
                              $"gaps {gaps} ({gapFrames / (double)rate * 1000:F0} ms), repeated grains {repeats}, clicks {clicks}");
        }
        return 0;
    }

    static bool Silent(short[] pcm, int frame, int ch)
    {
        for (int c = 0; c < ch; c++) if (pcm[frame * ch + c] != 0) return false;
        return true;
    }

    static bool AllSilent(short[] pcm, int start, int n, int ch)
    {
        for (int i = start; i < start + n; i++) if (!Silent(pcm, i, ch)) return false;
        return true;
    }

    /// <summary>16-bit PCM samples, channels and rate of a RIFF WAV (0 channels when it is something else).</summary>
    static (short[] Pcm, int Channels, int Rate) ReadWav(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x46464952) return ([], 0, 0);
        int p = 12, ch = 0, rate = 0, bits = 0;
        while (p + 8 <= d.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
            int len = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4)), (uint)(d.Length - p - 8));
            if (id == 0x20746D66) // "fmt "
            {
                ch = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 10));
                rate = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 22));
            }
            else if (id == 0x61746164 && bits == 16 && ch > 0) // "data"
            {
                var pcm = new short[len / 2];
                Buffer.BlockCopy(d, p + 8, pcm, 0, pcm.Length * 2);
                return (pcm, ch, rate);
            }
            p += 8 + len + (len & 1);
        }
        return ([], 0, 0);
    }
}
