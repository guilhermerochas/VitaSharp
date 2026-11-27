using System.Collections.Concurrent;

namespace VitaSharp.Core.Io;

/// <summary>
/// The path calls of IoFileMgr (open, dopen, getstat, chstat, remove, mkdir, rmdir, rename) that returned an error,
/// since the boot: function, guest path, error code, how many times and the guest frame of the first one. A file a
/// game cannot open on one host only (a data folder copied with another spelling, a missing bank, a path the host
/// rejects) shows up here, so an F8 capture made at any time (io_failures.txt) names it without a trace from the boot.
/// Only failures are recorded, so the cost of a successful call is one compare. Guest paths only: no host path (the
/// user's folders) is kept. Bounded: after <see cref="MaxEntries"/> distinct entries new ones are only counted.
/// </summary>
public sealed class IoFailureLog
{
    public const int MaxEntries = 1024;

    private sealed class Entry
    {
        public int Count;
        public long FirstFrame;
        public long Order;
    }

    private readonly ConcurrentDictionary<(string Function, string Path, int Error), Entry> _entries = new();
    private long _total;
    private long _dropped;
    private long _order;

    /// <summary>All failed calls, counted ones included.</summary>
    public long Total => Interlocked.Read(ref _total);

    /// <summary>Records <paramref name="result"/> when it is an error (negative) and returns it unchanged.</summary>
    public int Note(string function, string path, int result)
    {
        if (result >= 0)
        {
            return result;
        }

        Interlocked.Increment(ref _total);
        (string, string, int) key = (function, path, result);
        if (_entries.TryGetValue(key, out Entry? known))
        {
            Interlocked.Increment(ref known.Count);
            return result;
        }

        if (_entries.Count >= MaxEntries)
        {
            Interlocked.Increment(ref _dropped);
            return result;
        }

        Entry fresh = new()
        {
            Count = 1,
            FirstFrame = Hle.Modules.Display.FramesPresented,
            Order = Interlocked.Increment(ref _order),
        };
        if (!_entries.TryAdd(key, fresh) && _entries.TryGetValue(key, out known))
        {
            Interlocked.Increment(ref known.Count);
        }

        return result;
    }

    /// <summary>The io_failures.txt lines: a header, then one line per distinct failure in the order they first happened.</summary>
    public IEnumerable<string> Lines()
    {
        KeyValuePair<(string Function, string Path, int Error), Entry>[] all = _entries.ToArray();
        yield return $"# failed IoFileMgr path calls since the boot: {Total} calls, {all.Length} distinct, {Interlocked.Read(ref _dropped)} beyond the first {MaxEntries} distinct (only counted)";
        yield return "# function\terror\tcount\tfirstFrame\tguestPath";
        foreach (KeyValuePair<(string Function, string Path, int Error), Entry> kv in all.OrderBy(k => k.Value.Order))
        {
            yield return $"{kv.Key.Function}\t0x{(uint)kv.Key.Error:X8}\t{kv.Value.Count}\t{kv.Value.FirstFrame}\t{kv.Key.Path}";
        }
    }
}
