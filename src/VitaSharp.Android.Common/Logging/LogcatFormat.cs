using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Logging;

/// <summary>android_LogPriority (android/log.h).</summary>
public enum LogcatPriority
{
    Verbose = 2, Debug = 3, Info = 4, Warn = 5, Error = 6, Fatal = 7,
}

/// <summary>One logcat entry: priority, tag and text.</summary>
public readonly record struct LogcatEntry(LogcatPriority Priority, string Tag, string Text);

/// <summary>
/// How VitaSharp's log lines become logcat entries (the sink itself, a P/Invoke of __android_log_write, lives in the app):
/// <list type="bullet">
/// <item>Priority follows the level: Trace = VERBOSE, Debug = DEBUG, Info = INFO, Warn = WARN, Error = ERROR.</item>
/// <item>Tag <see cref="Tag"/> ("VitaSharp"); what the guest prints (the "[guest...]" lines of sceClibPrintf,
/// sceIoWrite on the TTY, sceKernelPrintf...) goes to <see cref="GuestTag"/> ("VitaSharp-Guest") without the
/// "[guest]" / "[guest stdout]" prefix, so <c>adb logcat -s VitaSharp-Guest</c> shows the game's own output.</item>
/// <item>Text: "[thread] message" (logcat adds time, pid and tid itself, so the file log's time and level are left out).
/// </item>
/// <item>liblog truncates an entry at about 4 KB (LOGGER_ENTRY_MAX_PAYLOAD 4068 bytes, tag included): longer
/// messages and multi-line ones (exception stacks) are split at line breaks, then into chunks of at most
/// <see cref="MaxEntryBytes"/> UTF-8 bytes on character boundaries.</item>
/// </list>
/// </summary>
public static class LogcatFormat
{
    public const string Tag = "VitaSharp", GuestTag = "VitaSharp-Guest";

    /// <summary>Payload bytes per entry, below liblog's limit with room for the tag and the thread prefix.</summary>
    public const int MaxEntryBytes = 4000;

    /// <summary>
    /// The lowest level forwarded to logcat while a game runs (from its first frame on): warnings and errors only, unless
    /// <c>VITASHARP_ANDROID_LOGCAT</c> (<paramref name="setting"/>) is <c>info</c>, or <c>debug</c>/<c>all</c> (then what
    /// <see cref="Log.Level"/> lets through). Every logcat entry is a system call into logd and an entry in a buffer the
    /// whole system shares; the session's log file still gets every line.
    /// </summary>
    public static LogLevel GameplayLevel(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        "info" => LogLevel.Info,
        "debug" or "all" or "trace" => LogLevel.Trace,
        _ => LogLevel.Warn,
    };

    public static LogcatPriority Priority(LogLevel level) => level switch
    {
        LogLevel.Trace => LogcatPriority.Verbose,
        LogLevel.Debug => LogcatPriority.Debug,
        LogLevel.Info => LogcatPriority.Info,
        LogLevel.Warn => LogcatPriority.Warn,
        _ => LogcatPriority.Error,
    };

    /// <summary>The tag and text of a message: guest output to <see cref="GuestTag"/>, the rest to <see cref="Tag"/>.</summary>
    public static (string Tag, string Text) Route(string message)
    {
        if (!message.StartsWith("[guest", StringComparison.Ordinal)) return (Tag, message);
        // "[guest] x" and "[guest stdout] x" are the plain output; "[guest stderr]", "[guest fd3]" keep their marker
        if (message.StartsWith("[guest] ", StringComparison.Ordinal)) return (GuestTag, message["[guest] ".Length..]);
        if (message.StartsWith("[guest stdout] ", StringComparison.Ordinal)) return (GuestTag, message["[guest stdout] ".Length..]);
        if (message.StartsWith("[guest ", StringComparison.Ordinal) || message.StartsWith("[guest]", StringComparison.Ordinal)) return (GuestTag, message);
        return (Tag, message); // "[guestfoo]": not guest output
    }

    /// <summary>The logcat entries for one log line, in order.</summary>
    public static IEnumerable<LogcatEntry> Format(LogLevel level, string thread, string message)
    {
        var prio = Priority(level);
        var (tag, text) = Route(message);
        string prefix = tag == GuestTag || thread.Length == 0 ? "" : $"[{thread}] ";
        if (text.Length == 0) { yield return new(prio, tag, prefix); yield break; }
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            foreach (var chunk in Chunks(l, MaxEntryBytes - Encoding.UTF8.GetByteCount(prefix)))
                yield return new(prio, tag, prefix + chunk);
        }
    }

    /// <summary>Splits <paramref name="s"/> into pieces of at most <paramref name="maxBytes"/> UTF-8 bytes without
    /// cutting a surrogate pair.</summary>
    public static IEnumerable<string> Chunks(string s, int maxBytes)
    {
        maxBytes = Math.Max(maxBytes, 16);
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes) { yield return s; yield break; }
        int start = 0;
        while (start < s.Length)
        {
            int bytes = 0, i = start;
            while (i < s.Length)
            {
                int n = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                int b = n == 2 ? 4 : s[i] < 0x80 ? 1 : s[i] < 0x800 ? 2 : 3;
                if (bytes + b > maxBytes) break;
                bytes += b;
                i += n;
            }
            yield return s[start..i];
            start = i;
        }
    }
}
