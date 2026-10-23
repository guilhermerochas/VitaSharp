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

    public static bool Enabled => _dir != null;

    public static void Enable(string dir)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
    }

    public sealed class Writer : IDisposable
    {
        readonly FileStream _fs;
        readonly int _rate;
        long _bytes;

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
            lock (_fs) { Header(); _fs.Dispose(); }
        }
    }

    /// <summary>A writer for a newly opened port, or null when dumping is off.</summary>
    public static Writer? Open(string name, int rate)
    {
        if (_dir == null) return null;
        int n = Interlocked.Increment(ref _seq);
        string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        try { return new Writer(Path.Combine(_dir, $"{n:D2}_{safe}_{rate}.wav"), rate); }
        catch (Exception e) { Log.Warn($"Audio dump: {e.Message}"); return null; }
    }
}
