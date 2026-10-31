using System.Collections.Concurrent;

namespace VitaSharp.Core.Util;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>
/// An extra destination for log lines (the Android front-end's logcat). <see cref="Write"/> is called under the log
/// lock, in order, for every line that passes <see cref="Log.Level"/>; it must be fast and must not throw.
/// </summary>
public interface ILogSink
{
    void Write(LogLevel level, string thread, string message);
}

/// <summary>Lightweight thread-safe logger writing to the console, an optional file and an optional sink.</summary>
public static class Log
{
    public static LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>Extra destination (Android: logcat). Null on the desktop.</summary>
    public static ILogSink? Sink { get; set; }

    /// <summary>False stops the console copy (Android, where the sink already reaches logcat and stdout would
    /// duplicate every line under another tag).</summary>
    public static bool ConsoleEnabled { get; set; } = true;
    static StreamWriter? _file;
    static readonly object Lock = new();
    static readonly ConcurrentDictionary<string, int> Once = new();

    /// <summary>Path of the open log file (null when logging to the console only).</summary>
    public static string? FilePath { get; private set; }

    /// <summary>
    /// Debug mode: warnings are deduplicated by their text with numbers masked; the first three of a kind are written,
    /// then only the 10th, 100th, ... occurrence with its count (<see cref="WarningSummary"/> lists them all).
    /// </summary>
    public static bool DedupeWarnings { get; set; }

    /// <summary>Above this many characters written, Info/Debug lines are dropped (warnings and errors still go out),
    /// so a runaway log cannot fill the disk.</summary>
    public static long MaxFileChars { get; set; } = 256L << 20;
    static long _written;
    static bool _capNoted;
    static readonly ConcurrentDictionary<string, int> WarnCounts = new();

    public static void OpenFile(string path)
    {
        lock (Lock)
        {
            _file?.Dispose();
            FilePath = Path.GetFullPath(path);
            _written = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // buffered: a flush per line costs a syscall on every message; warnings/errors flush immediately and a
            // timer flushes the rest so a crash loses at most a quarter second of log. FileShare.ReadWrite: diagnostic
            // captures copy the file while it is open
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), System.Text.Encoding.UTF8, 1 << 16);
            _flushTimer ??= new Timer(_ => Flush(), null, 250, 250);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    static Timer? _flushTimer;

    public static void Flush()
    {
        lock (Lock) { try { _file?.Flush(); } catch (ObjectDisposedException) { } }
    }

    public static void Write(LogLevel level, string msg) => Write(level, msg, toSink: true);

    /// <summary>An Info line for the log file (and the console) only, kept out of <see cref="Sink"/> (Android: logcat):
    /// periodic statistics that only the file needs.</summary>
    public static void InfoFileOnly(string m) => Write(LogLevel.Info, m, toSink: false);

    static void Write(LogLevel level, string msg, bool toSink)
    {
        if (level < Level) return;
        if (level == LogLevel.Warn && DedupeWarnings)
        {
            int n = WarnCounts.AddOrUpdate(MaskNumbers(msg), 1, (_, c) => c + 1);
            if (n > 3 && n is not (10 or 100 or 1000 or 10000 or 100000 or 1000000)) return;
            if (n > 3) msg += $" (repeated {n} times)";
        }
        string thread = Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString();
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level,-5}] [{thread}] {msg}";
        lock (Lock)
        {
            if (ConsoleEnabled)
            {
                if (level >= LogLevel.Warn) Console.ForegroundColor = level == LogLevel.Error ? ConsoleColor.Red : ConsoleColor.Yellow;
                Console.WriteLine(line);
                if (level >= LogLevel.Warn) Console.ResetColor();
            }
            if (toSink) Sink?.Write(level, thread, msg);
            if (_file != null)
            {
                _written += line.Length + 2;
                if (_written <= MaxFileChars || level >= LogLevel.Warn) _file.WriteLine(line);
                else if (!_capNoted) { _capNoted = true; _file.WriteLine($"[log] {MaxFileChars >> 20} M characters written: info/debug lines are dropped from now on"); }
                if (level >= LogLevel.Warn) _file.Flush();
            }
        }
    }

    /// <summary>Warnings with their numbers masked (# = any number), most frequent first (debug mode).</summary>
    public static IEnumerable<string> WarningSummary(int max = 60) =>
        WarnCounts.OrderByDescending(k => k.Value).Take(max).Select(k => $"[dbg] WARN x{k.Value}: {k.Key}");

    /// <summary>Message text with numbers (decimal, or hexadecimal runs containing a digit) replaced by '#': the dedup
    /// key of a warning.</summary>
    static string MaskNumbers(string m)
    {
        var sb = new System.Text.StringBuilder(m.Length);
        int i = 0;
        while (i < m.Length)
        {
            if (char.IsAsciiHexDigit(m[i]) && (i == 0 || !char.IsAsciiLetterOrDigit(m[i - 1]) || m[i - 1] == 'x' && i >= 2 && char.IsAsciiDigit(m[i - 2])))
            {
                int j = i;
                bool digit = false;
                while (j < m.Length && char.IsAsciiHexDigit(m[j])) { digit |= char.IsAsciiDigit(m[j]); j++; }
                if (digit && (j >= m.Length || !char.IsAsciiLetter(m[j]) || m[j] == 'x'))
                {
                    sb.Append('#');
                    i = j;
                    continue;
                }
                sb.Append(m, i, j - i);
                i = j;
                continue;
            }
            sb.Append(m[i++]);
        }
        return sb.ToString();
    }

    public static void Trace(string m) { if (Level <= LogLevel.Trace) Write(LogLevel.Trace, m); }
    public static void Debug(string m) { if (Level <= LogLevel.Debug) Write(LogLevel.Debug, m); }
    /// <summary>Interpolated messages are only formatted when the level is enabled (no allocation otherwise).</summary>
    public static void Trace(ref TraceLogHandler m) { if (Level <= LogLevel.Trace) Write(LogLevel.Trace, m.ToStringAndClear()); }
    public static void Debug(ref DebugLogHandler m) { if (Level <= LogLevel.Debug) Write(LogLevel.Debug, m.ToStringAndClear()); }
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    /// <summary>Logs a message only the first time a given key is seen.</summary>
    public static void WarnOnce(string key, string m)
    {
        if (Once.TryAdd(key, 0)) Warn(m);
    }

    /// <summary><see cref="WarnOnce(string, string)"/> for interpolated keys: a key seen before costs no allocation and the
    /// message is only formatted the first time.</summary>
    public static void WarnOnce(ref OnceKeyHandler key, [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument("key")] ref OnceMessageHandler m)
    {
        if (m.Enabled) Warn(m.ToStringAndClear());
    }

    static readonly ConcurrentDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> OnceBySpan = Once.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>True the first time <paramref name="key"/> is seen (shared with the string-keyed WarnOnce).</summary>
    internal static bool TryAddOnce(ReadOnlySpan<char> key) => !OnceBySpan.ContainsKey(key) && OnceBySpan.TryAdd(key, 0);

    /// <summary>Debug-level message logged only the first time a given key is seen.</summary>
    public static void DebugOnce(string key, string m)
    {
        if (Level <= LogLevel.Debug && Once.TryAdd(key, 0)) Debug(m);
    }

    /// <summary><see cref="DebugOnce(string, string)"/> whose key and message are only formatted when debug logging is on.</summary>
    public static void DebugOnce(ref DebugLogHandler key, ref DebugLogHandler m)
    {
        if (Level <= LogLevel.Debug && Once.TryAdd(key.ToStringAndClear(), 0)) Debug(m.ToStringAndClear());
    }

    public static void DebugOnce(string key, ref DebugLogHandler m)
    {
        if (Level <= LogLevel.Debug && Once.TryAdd(key, 0)) Debug(m.ToStringAndClear());
    }
}
