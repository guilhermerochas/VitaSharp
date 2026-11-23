using System.Globalization;
using System.Text;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Capture;

/// <summary>
/// The HLE calls of a "Gravar tudo" recording (hle_calls.tsv, docs/notes/session-capture.md): every call any guest
/// thread made into the emulator's HLE while recording, with its four argument registers, the return address, the
/// result and the guest time, so a problem in the system's side (a wrong return, an error the game does not expect,
/// the call that came before a hang) can be read from the capture without the game. Each thread fills its own
/// fixed-size chunks (one allocation per <see cref="ChunkSize"/> calls, nothing per call); past
/// <see cref="MaxCalls"/> calls only the per-function counts go on. The re-run records its own, and the comparison
/// names the first call where the two differ.
/// </summary>
public static class HleCallTrace
{
    /// <summary>Calls per chunk of a thread's buffer.</summary>
    public const int ChunkSize = 4096;

    /// <summary>Calls kept per recording (about 40 bytes each in memory): 15 s of a busy game.</summary>
    public const int MaxCalls = 1_000_000;

    /// <summary>The file a recording writes.</summary>
    public const string FileName = "hle_calls.tsv";

    /// <summary>One call.</summary>
    public struct Call
    {
        public long GuestUs;
        public HleFunction Function;
        public uint A0, A1, A2, A3, Lr, Ret;
        public bool Returned;
    }

    /// <summary>A thread's calls of the recording.</summary>
    public sealed class ThreadBuffer(GuestThread thread, int generation)
    {
        public readonly GuestThread Thread = thread;
        public readonly int Generation = generation;
        /// <summary>The chunks, made as the thread needs them (a fixed table: the stop reads it while a late call may
        /// still add one).</summary>
        public readonly Call[]?[] Chunks = new Call[]?[MaxCalls / ChunkSize + 1];
        public int Count;
    }

    private static volatile bool s_active;
    private static int s_generation;
    private static int s_total;
    private static long s_dropped;
    private static readonly List<ThreadBuffer> s_buffers = [];
    private static readonly object s_lock = new();

    /// <summary>A recording is collecting calls (one static read per HLE call when it is not).</summary>
    public static bool Active => s_active;

    /// <summary>Starts collecting (a new recording): earlier calls are dropped.</summary>
    public static void Begin()
    {
        lock (s_lock)
        {
            s_buffers.Clear();
            s_total = 0;
            s_dropped = 0;
            s_generation++;
            s_active = true;
        }
    }

    /// <summary>Stops collecting; the calls stay until <see cref="Write"/> or the next <see cref="Begin"/>.</summary>
    public static void End() => s_active = false;

    /// <summary>The call starts: returns its slot in the thread's buffer (to complete with <see cref="Returned"/>) or
    /// -1 when it is not kept.</summary>
    public static int Enter(GuestThread thread, ref ThreadBuffer? buffer, HleFunction function, uint a0, uint a1, uint a2, uint a3, uint lr)
    {
        int generation = Volatile.Read(ref s_generation);
        if (buffer == null || buffer.Generation != generation)
        {
            buffer = new ThreadBuffer(thread, generation);
            lock (s_lock)
            {
                s_buffers.Add(buffer);
            }
        }

        if (Interlocked.Increment(ref s_total) > MaxCalls)
        {
            Interlocked.Increment(ref s_dropped);
            return -1;
        }

        int index = buffer.Count;
        int chunk = index / ChunkSize;
        if (chunk >= buffer.Chunks.Length)
        {
            return -1;
        }

        Call[] calls = buffer.Chunks[chunk] ??= new Call[ChunkSize];
        ref Call call = ref calls[index % ChunkSize];
        call.GuestUs = GuestClock.Microseconds;
        call.Function = function;
        call.A0 = a0;
        call.A1 = a1;
        call.A2 = a2;
        call.A3 = a3;
        call.Lr = lr;
        call.Ret = 0;
        call.Returned = false;
        Volatile.Write(ref buffer.Count, index + 1);
        return index;
    }

    /// <summary>The call at <paramref name="slot"/> returned <paramref name="ret"/> (r0).</summary>
    public static void Returned(ThreadBuffer buffer, int slot, uint ret)
    {
        ref Call call = ref buffer.Chunks[slot / ChunkSize]![slot % ChunkSize];
        call.Ret = ret;
        call.Returned = true;
    }

    /// <summary>The recording's calls, by guest time (a thread's calls keep their order).</summary>
    public static List<(string Thread, Call Call)> Snapshot()
    {
        var all = new List<(string Thread, Call Call)>();
        lock (s_lock)
        {
            foreach (ThreadBuffer buffer in s_buffers)
            {
                int count = Volatile.Read(ref buffer.Count);
                for (int i = 0; i < count; i++)
                {
                    if (buffer.Chunks[i / ChunkSize] is { } chunk)
                    {
                        all.Add((buffer.Thread.Name, chunk[i % ChunkSize]));
                    }
                }
            }
        }

        // stable: the calls of one thread at the same microsecond stay in order
        return all.Select((x, i) => (x, i)).OrderBy(p => p.x.Call.GuestUs).ThenBy(p => p.i).Select(p => p.x).ToList();
    }

    /// <summary>Writes hle_calls.tsv (and returns the summary lines for hle_errors.txt): one line per call; a call
    /// without a result had not returned at the stop (the thread waits in it).</summary>
    public static List<string> Write(string path)
    {
        List<(string Thread, Call Call)> calls = Snapshot();
        var counts = new Dictionary<string, (int Calls, int Errors)>(StringComparer.Ordinal);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.WriteLine("# guestUs\tthread\tfunction\tlibrary\tr0\tr1\tr2\tr3\tresult\tlr");
            foreach ((string thread, Call call) in calls)
            {
                HleFunction f = call.Function;
                bool error = call.Returned && !f.ReturnsValue && call.Ret is >= 0x80000000 and < 0x81000000;
                (int c, int e) = counts.GetValueOrDefault(f.Name);
                counts[f.Name] = (c + 1, e + (error ? 1 : 0));
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{call.GuestUs}\t{thread}\t{f.Name}\t{f.Library}\t0x{call.A0:X}\t0x{call.A1:X}\t0x{call.A2:X}\t0x{call.A3:X}\t{(call.Returned ? $"0x{call.Ret:X8}" : "-")}\t0x{call.Lr:X8}"));
            }
        }

        var summary = new List<string>
        {
            FormattableString.Invariant($"HLE calls: {calls.Count} recorded in {FileName}{(Interlocked.Read(ref s_dropped) > 0 ? $" ({Interlocked.Read(ref s_dropped)} more after the first {MaxCalls} were only counted)" : "")}, {counts.Count} functions"),
        };
        foreach (KeyValuePair<string, (int Calls, int Errors)> kv in counts.OrderByDescending(k => k.Value.Calls).Take(40))
        {
            summary.Add(FormattableString.Invariant($"  {kv.Key}: {kv.Value.Calls} calls{(kv.Value.Errors > 0 ? $", {kv.Value.Errors} error returns" : "")}"));
        }

        return summary;
    }

    /// <summary>
    /// The first difference between two recordings' calls (an original and its re-run), thread by thread (threads
    /// keep their names): the functions called in order, then their results. Null when the threads both have made
    /// the same calls over the part both recorded.
    /// </summary>
    public static string? FirstDifference(string originalFile, string rerunFile)
    {
        Dictionary<string, List<(long Us, string Function, string Result)>> a = Read(originalFile), b = Read(rerunFile);
        var differences = new List<(long Us, string Text)>();
        foreach ((string thread, List<(long Us, string Function, string Result)> calls) in a)
        {
            if (!b.TryGetValue(thread, out List<(long Us, string Function, string Result)>? other))
            {
                continue;
            }

            if (calls.Count == 0 || other.Count == 0)
            {
                continue;
            }

            // the capture's trace starts before its state is saved (the game runs until a safe point), the re-run's at
            // the state's moment: the capture's calls from before the re-run's first one (the state restores the guest
            // clock) are not compared. A pre-roll state is older than the capture: the re-run's first calls then come
            // before the capture's and are skipped by the alignment.
            int skip = 0;
            while (skip < calls.Count - 1 && calls[skip].Us < other[0].Us)
            {
                skip++;
            }

            int offset = Align(calls, skip, other);
            if (offset < 0)
            {
                differences.Add((calls[skip].Us, $"thread '{thread}': the capture's first calls ({calls[skip].Function}...) are not among the re-run's first {AlignWindow}"));
                continue;
            }

            int n = Math.Min(calls.Count - skip, other.Count - offset);
            for (int k = 0; k < n; k++)
            {
                int i = skip + k;
                (long Us, string Function, string Result) mine = calls[i], theirs = other[k + offset];
                if (mine.Function != theirs.Function)
                {
                    differences.Add((mine.Us, $"thread '{thread}' call {i + 1}: {mine.Function} in the capture, {theirs.Function} in the re-run"));
                    break;
                }

                if (mine.Result != theirs.Result && mine.Result != "-" && theirs.Result != "-")
                {
                    differences.Add((mine.Us, $"thread '{thread}' call {i + 1}: {mine.Function} returned {mine.Result} in the capture, {theirs.Result} in the re-run"));
                    break;
                }
            }
        }

        return differences.Count == 0 ? null : differences.OrderBy(d => d.Us).First().Text + FormattableString.Invariant($" (guest time {differences.Min(d => d.Us) / 1e6:F3} s)");
    }

    /// <summary>How many of the re-run's first calls of a thread may come before the capture's first one.</summary>
    private const int AlignWindow = 256;

    /// <summary>The index of the re-run's call where the capture's calls (from <paramref name="from"/>) start: of the
    /// re-run's calls from which the next ones (up to 8) are to the same functions, the closest in guest time (a game
    /// repeats the same calls every frame: the first match can be another frame's), else the first call to the
    /// capture's first function (the difference is then within the first calls); -1 when there is none in the first
    /// <see cref="AlignWindow"/>.</summary>
    private static int Align(List<(long Us, string Function, string Result)> capture, int from, List<(long Us, string Function, string Result)> rerun)
    {
        int firstFunction = -1;
        for (int start = 0; start < Math.Min(AlignWindow, rerun.Count); start++)
        {
            if (firstFunction < 0 && rerun[start].Function == capture[from].Function)
            {
                firstFunction = start;
            }
        }

        int best = -1;
        long bestDistance = long.MaxValue;
        for (int start = 0; start < Math.Min(AlignWindow, rerun.Count); start++)
        {
            int length = Math.Min(8, Math.Min(capture.Count - from, rerun.Count - start));
            bool same = true;
            for (int i = 0; i < length && same; i++)
            {
                same = capture[from + i].Function == rerun[start + i].Function;
            }

            long distance = Math.Abs(rerun[start].Us - capture[from].Us);
            if (same && distance < bestDistance)
            {
                best = start;
                bestDistance = distance;
            }
        }

        return best >= 0 ? best : firstFunction;
    }

    private static Dictionary<string, List<(long Us, string Function, string Result)>> Read(string file)
    {
        var threads = new Dictionary<string, List<(long, string, string)>>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(file))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            string[] f = line.Split('\t');
            if (f.Length < 10 || !long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
            {
                continue;
            }

            if (!threads.TryGetValue(f[1], out List<(long, string, string)>? list))
            {
                threads[f[1]] = list = [];
            }

            list.Add((us, f[2], f[8]));
        }

        return threads;
    }
}
