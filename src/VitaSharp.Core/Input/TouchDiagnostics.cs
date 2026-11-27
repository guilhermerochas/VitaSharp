using System.Globalization;
using System.Text;

namespace VitaSharp.Core.Input;

/// <summary>
/// Counters of the touch path, for the log and the "Gravar tudo" session.txt: what the front-end received (its raw
/// touch events by kind, the historical points, the most fingers at once) and what the touch driver handed the guest
/// (reads, buffers, samples with contacts, new contacts, and a hash of every delivered report: id and panel
/// position). Counts and a hash only: no positions are logged and nothing personal.
/// </summary>
public static class TouchDiagnostics
{
    /// <summary>The kinds of raw host touch events (Android's MotionEvent actions).</summary>
    public enum RawKind
    {
        Down, PointerDown, Move, PointerUp, Up, Cancel,
    }

    private static readonly long[] s_raw = new long[6];
    private static long s_history, s_maxPointers;
    private static readonly long[] s_reads = new long[2], s_buffers = new long[2], s_samples = new long[2],
        s_withContacts = new long[2], s_presses = new long[2], s_maxReports = new long[2];
    private static readonly ulong[] s_hash = [HashSeed, HashSeed];

    /// <summary>The start of the report hash (FNV-1a 64).</summary>
    public const ulong HashSeed = 14695981039346656037UL;

    /// <summary>One raw host event with <paramref name="pointers"/> fingers and <paramref name="historical"/> batched
    /// points (MotionEvent.HistorySize).</summary>
    public static void Raw(RawKind kind, int pointers, int historical)
    {
        Interlocked.Increment(ref s_raw[(int)kind]);
        Interlocked.Add(ref s_history, historical);
        long max;
        while (pointers > (max = Interlocked.Read(ref s_maxPointers)) && Interlocked.CompareExchange(ref s_maxPointers, pointers, max) != max)
        {
        }
    }

    /// <summary>A read (sceTouchPeek/Read) of a panel that handed over <paramref name="buffers"/> samples.</summary>
    public static void Peeked(int port, int buffers)
    {
        Interlocked.Increment(ref s_reads[port]);
        Interlocked.Add(ref s_buffers[port], buffers);
    }

    /// <summary>The reads (sceTouchPeek/Read) of panel <paramref name="port"/> so far (0 front, 1 rear).</summary>
    public static long Reads(int port) => Interlocked.Read(ref s_reads[port]);

    /// <summary>A sample the driver took (<paramref name="reports"/> contacts, <paramref name="presses"/> of them new)
    /// and its part of the report hash.</summary>
    public static void Sampled(int port, int reports, int presses, ulong hash)
    {
        Interlocked.Increment(ref s_samples[port]);
        if (reports > 0)
        {
            Interlocked.Increment(ref s_withContacts[port]);
        }

        Interlocked.Add(ref s_presses[port], presses);
        if (reports > Interlocked.Read(ref s_maxReports[port]))
        {
            Interlocked.Exchange(ref s_maxReports[port], reports);
        }

        lock (s_hash)
        {
            s_hash[port] = Mix(s_hash[port], hash);
        }
    }

    /// <summary>One FNV-1a step over the bytes of <paramref name="value"/>.</summary>
    public static ulong Mix(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (value >> (i * 8)) & 0xFF;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Back to zero (a new session; tests).</summary>
    public static void Reset()
    {
        Array.Clear(s_raw);
        s_history = 0;
        s_maxPointers = 0;
        Array.Clear(s_reads);
        Array.Clear(s_buffers);
        Array.Clear(s_samples);
        Array.Clear(s_withContacts);
        Array.Clear(s_presses);
        Array.Clear(s_maxReports);
        lock (s_hash)
        {
            s_hash[0] = s_hash[1] = HashSeed;
        }
    }

    /// <summary>One line: "touch: host down=… | front reads=… | back …".</summary>
    public static string Describe()
    {
        var sb = new StringBuilder("touch: host");
        for (int i = 0; i < s_raw.Length; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {((RawKind)i).ToString().ToLowerInvariant()}={Interlocked.Read(ref s_raw[i])}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" historical={Interlocked.Read(ref s_history)} max-pointers={Interlocked.Read(ref s_maxPointers)}");
        for (int port = 0; port < 2; port++)
        {
            ulong hash;
            lock (s_hash)
            {
                hash = s_hash[port];
            }

            sb.Append(CultureInfo.InvariantCulture,
                $" | {(port == 0 ? "front" : "back")} reads={Interlocked.Read(ref s_reads[port])} buffers={Interlocked.Read(ref s_buffers[port])} samples={Interlocked.Read(ref s_samples[port])} with-contacts={Interlocked.Read(ref s_withContacts[port])} presses={Interlocked.Read(ref s_presses[port])} max-reports={Interlocked.Read(ref s_maxReports[port])} hash={hash:X16}");
        }

        return sb.ToString();
    }
}
