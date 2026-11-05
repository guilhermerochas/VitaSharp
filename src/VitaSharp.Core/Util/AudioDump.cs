namespace VitaSharp.Core.Util;

/// <summary>
/// Test aid (<c>--audio-dump=&lt;dir&gt;</c>): writes every grain a guest submits to an audio port, after port volume,
/// into one 16-bit stereo WAV per opened port. The files hold the exact content the game produced, independent of
/// host timing (no device, no underruns), so they can be analysed for clipping, pitch and clicks.
/// </summary>
public static class AudioDump
{
    static string? _dir;
    static int _seq;
    static long _enabledNs;
    static readonly List<(Writer W, string File, long OpenedNs)> Opened = new();

    public static bool Enabled => _dir != null;

    /// <summary>Also write each NGS source voice to its own WAV (the session capture; VITASHARP_NGS_TRACE does it too).</summary>
    public static bool VoiceDumps;

    /// <summary>Changes when dumping starts or stops: open ports compare it at every grain and reopen their writer.</summary>
    public static volatile int Generation;

    public static void Enable(string dir)
    {
        Directory.CreateDirectory(dir);
        lock (Opened) Opened.Clear();
        _enabledNs = VitaSharp.Core.Capture.CaptureClock.NowNs;
        _dir = dir;
        Generation++;
    }

    /// <summary>Stops dumping: every WAV is completed and closed now (a port writing later drops its grain and opens
    /// no new file). Returns the files with the time each was opened, in ms after <see cref="Enable"/>.</summary>
    public static List<(string File, double StartMs)> Disable()
    {
        _dir = null;
        Generation++;
        var list = new List<(string, double)>();
        lock (Opened)
        {
            foreach (var (w, file, ns) in Opened)
            {
                w.Dispose();
                list.Add((file, (ns - _enabledNs) / 1e6));
            }
            Opened.Clear();
        }
        return list;
    }


    public sealed class Writer : IDisposable
    {
        readonly FileStream _fs;
        readonly int _rate;
        long _bytes;
        bool _closed;

        internal Writer(string path, int rate)
        {
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _rate = rate;
            _fs.Write(new byte[44]);
            Header();
        }

        public void Write(ReadOnlySpan<short> stereo)
        {
            lock (_fs)
            {
                if (_closed) return;
                _fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(stereo));
                _bytes += stereo.Length * 2;
                // keep the header valid while the emulator is still running (runs may end by timeout)
                if ((_bytes & 0x3FFFF) < stereo.Length * 2) Header();
            }
        }

        void Header()
        {
            long at = _fs.Position;
            Span<byte> h = stackalloc byte[44];
            "RIFF"u8.CopyTo(h);
            BitConverter.TryWriteBytes(h[4..], (int)Math.Min(int.MaxValue, 36 + _bytes));
            "WAVEfmt "u8.CopyTo(h[8..]);
            BitConverter.TryWriteBytes(h[16..], 16);
            BitConverter.TryWriteBytes(h[20..], (short)1);
            BitConverter.TryWriteBytes(h[22..], (short)2);
            BitConverter.TryWriteBytes(h[24..], _rate);
            BitConverter.TryWriteBytes(h[28..], _rate * 4);
            BitConverter.TryWriteBytes(h[32..], (short)4);
            BitConverter.TryWriteBytes(h[34..], (short)16);
            "data"u8.CopyTo(h[36..]);
            BitConverter.TryWriteBytes(h[40..], (int)Math.Min(int.MaxValue, _bytes));
            _fs.Position = 0;
            _fs.Write(h);
            _fs.Position = at;
            _fs.Flush();
        }

        public void Dispose()
        {
            lock (_fs)
            {
                if (_closed) return;
                _closed = true;
                Header();
                _fs.Dispose();
            }
        }
    }

    /// <summary>A raw byte stream next to the WAVs (e.g. the ATRAC9 superframes an NGS voice decoded), or null when off.</summary>
    public static FileStream? OpenRaw(string name)
    {
        if (_dir == null) return null;
        string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_'));
        try { return new FileStream(Path.Combine(_dir, safe), FileMode.Create, FileAccess.Write, FileShare.Read); }
        catch (Exception e) { Log.Warn($"Audio dump: {e.Message}"); return null; }
    }

    /// <summary>A writer for a newly opened port, or null when dumping is off.</summary>
    public static Writer? Open(string name, int rate)
    {
        if (_dir == null) return null;
        int n = Interlocked.Increment(ref _seq);
        string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        try
        {
            string file = Path.Combine(_dir, $"{n:D2}_{safe}_{rate}.wav");
            var w = new Writer(file, rate);
            lock (Opened) Opened.Add((w, file, VitaSharp.Core.Capture.CaptureClock.NowNs));
            return w;
        }
        catch (Exception e) { Log.Warn($"Audio dump: {e.Message}"); return null; }
    }
}
