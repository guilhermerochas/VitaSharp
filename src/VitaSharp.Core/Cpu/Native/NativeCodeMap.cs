using System.Buffers;
using System.Buffers.Text;
using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Profiling aid (<see cref="EnvVars.NativeMap"/>=file): one line per piece of generated code,
/// <c>host-address size guest-pc arm|thumb hex-bytes</c>, so the samples a CPU profiler (simpleperf on a phone, perf on
/// Linux) puts in the anonymous code arena can be attributed to guest blocks and disassembled on another machine.
/// The lines are formatted into one reused buffer and written through a buffered file (<see cref="NativeCodeMapWriter"/>):
/// no allocation per block (a string per code byte and a flush per line made FIFA 13 allocate 4.9 MB per frame on the
/// phone, with the GC pausing 25% of the time, and the game was gone before the profiler started).
/// </summary>
public static unsafe class NativeCodeMap
{
    private static readonly NativeCodeMapWriter? s_writer = Open();

    public static bool Enabled => s_writer != null;

    private static NativeCodeMapWriter? Open()
    {
        if (Environment.GetEnvironmentVariable(EnvVars.NativeMap) is not { Length: > 0 } path)
        {
            return null;
        }

        try
        {
            var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            Log.Info($"Native code map: {path}");
            return new NativeCodeMapWriter(file, flushPeriodMs: 1000);
        }
        catch (Exception e)
        {
            Log.Warn($"Native code map {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>Records code at <paramref name="code"/> (<paramref name="size"/> bytes) for <paramref name="guestPc"/>
    /// (0 for runtime stubs, <paramref name="name"/> then says which).</summary>
    public static void Add(nint code, int size, uint guestPc, bool thumb, string? name = null)
    {
        s_writer?.Add(new ReadOnlySpan<byte>((void*)code, size), (ulong)code, guestPc, thumb, name);
    }
}

/// <summary>
/// Writes native code map lines to a stream without allocating per line: each line is formatted into a reused byte
/// buffer (grown only for a larger block) and written to the stream, which buffers it. A timer flushes the stream
/// every <c>flushPeriodMs</c> (0: never; <see cref="Flush"/> does it), so a process that is killed (the profiling
/// scripts stop the app before pulling the map) loses at most that much of the map.
/// </summary>
public sealed class NativeCodeMapWriter : IDisposable
{
    private static ReadOnlySpan<byte> HexDigits => "0123456789ABCDEF"u8;

    private readonly object _lock = new();
    private readonly Stream _stream;
    private readonly Timer? _flushTimer;
    private byte[] _line = new byte[4096];
    private bool _dirty;

    public NativeCodeMapWriter(Stream stream, int flushPeriodMs)
    {
        _stream = stream;
        if (flushPeriodMs > 0)
        {
            _flushTimer = new Timer(_ => Flush(), null, flushPeriodMs, flushPeriodMs);
        }
    }

    /// <summary>One line: host address, size, guest pc, <paramref name="name"/> or arm/thumb, the code's bytes in hex.</summary>
    public void Add(ReadOnlySpan<byte> code, ulong hostAddress, uint guestPc, bool thumb, string? name = null)
    {
        string kind = name ?? (thumb ? "thumb" : "arm");
        lock (_lock)
        {
            int need = 64 + kind.Length + code.Length * 2;
            if (_line.Length < need)
            {
                _line = new byte[Math.Max(need, _line.Length * 2)];
            }

            Span<byte> line = _line;
            int n = 0;
            Utf8Formatter.TryFormat(hostAddress, line, out int written, new StandardFormat('X'));
            n += written;
            line[n++] = (byte)' ';
            Utf8Formatter.TryFormat(code.Length, line[n..], out written);
            n += written;
            line[n++] = (byte)' ';
            Utf8Formatter.TryFormat(guestPc, line[n..], out written, new StandardFormat('X', 8));
            n += written;
            line[n++] = (byte)' ';
            n += Encoding.ASCII.GetBytes(kind, line[n..]);
            line[n++] = (byte)' ';
            foreach (byte b in code)
            {
                line[n++] = HexDigits[b >> 4];
                line[n++] = HexDigits[b & 0xF];
            }

            line[n++] = (byte)'\n';
            _stream.Write(line[..n]);
            _dirty = true;
        }
    }

    /// <summary>Writes the buffered lines to the underlying file.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            try
            {
                _stream.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Log.Warn($"Native code map: {e.Message}");
            }
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        Flush();
        _stream.Dispose();
    }
}
