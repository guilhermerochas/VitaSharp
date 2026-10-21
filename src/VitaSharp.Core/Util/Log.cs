using System.Collections.Concurrent;

namespace VitaSharp.Core.Util;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>Lightweight thread-safe logger writing to the console and an optional file.</summary>
public static class Log
{
    public static LogLevel Level { get; set; } = LogLevel.Info;
    static StreamWriter? _file;
    static readonly object Lock = new();
    static readonly ConcurrentDictionary<string, int> Once = new();

    public static void OpenFile(string path)
    {
        lock (Lock)
        {
            _file?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // buffered: a flush per line costs a syscall on every message; warnings/errors flush immediately and a
            // timer flushes the rest so a crash loses at most a quarter second of log
            _file = new StreamWriter(path, false, System.Text.Encoding.UTF8, 1 << 16);
            _flushTimer ??= new Timer(_ => Flush(), null, 250, 250);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    static Timer? _flushTimer;

    public static void Flush()
    {
        lock (Lock) { try { _file?.Flush(); } catch (ObjectDisposedException) { } }
    }

    public static void Write(LogLevel level, string msg)
    {
        if (level < Level) return;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level,-5}] [{Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString()}] {msg}";
        lock (Lock)
        {
            if (level >= LogLevel.Warn) Console.ForegroundColor = level == LogLevel.Error ? ConsoleColor.Red : ConsoleColor.Yellow;
            Console.WriteLine(line);
            if (level >= LogLevel.Warn) Console.ResetColor();
            _file?.WriteLine(line);
            if (level >= LogLevel.Warn) _file?.Flush();
        }
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
