using System.Runtime.InteropServices;
using VitaSharp.Android.Logging;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Services;

/// <summary>
/// VitaSharp's log in logcat: every line <see cref="Log"/> writes goes to __android_log_write (liblog) under the tag
/// "VitaSharp" (guest output: "VitaSharp-Guest") with the matching priority; the formatting is
/// <see cref="LogcatFormat"/> (unit-tested in VitaSharp.Tests). <c>adb logcat -s VitaSharp VitaSharp-Guest</c> shows both.
/// The console copy is switched off (stdout would reach logcat a second time under another tag); the file log in
/// &lt;data&gt;/logs stays (SessionLog). Once a game shows its first frame (<see cref="EnterGameplay"/>) only warnings and
/// errors reach logcat (<see cref="LogcatFormat.GameplayLevel"/>; <c>VITASHARP_ANDROID_LOGCAT=info</c> keeps the info
/// lines), and <see cref="Notice"/> lines always do.
/// </summary>
public sealed partial class LogcatSink : ILogSink
{
    static volatile int _minLevel = (int)LogLevel.Trace;

    public static void Install()
    {
        Log.Sink = new LogcatSink();
        Log.ConsoleEnabled = false;
    }

    /// <summary>The game is running: from now on logcat only gets the gameplay level (the log file is unchanged).</summary>
    public static void EnterGameplay()
    {
        var level = LogcatFormat.GameplayLevel(Environment.GetEnvironmentVariable(EnvVars.AndroidLogcat));
        if (_minLevel == (int)level) return;
        Notice($"logcat: {(level >= LogLevel.Warn ? "warnings and errors only" : $"{level} and above")} while the game runs " +
               $"(the log file keeps every line; {EnvVars.AndroidLogcat}=info|debug to change)");
        _minLevel = (int)level;
    }

    /// <summary>An info line that reaches logcat even during gameplay (one-off reports such as the CPU placement).</summary>
    public static void Notice(string message)
    {
        Log.Info(message);
        if (_minLevel > (int)LogLevel.Info && Log.Level <= LogLevel.Info)
            Forward(LogLevel.Info, Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString(), message);
    }

    public void Write(LogLevel level, string thread, string message)
    {
        if ((int)level < _minLevel) return;
        Forward(level, thread, message);
    }

    static void Forward(LogLevel level, string thread, string message)
    {
        try
        {
            foreach (var e in LogcatFormat.Format(level, thread, message))
                __android_log_write((int)e.Priority, e.Tag, e.Text);
        }
        catch (Exception) { } // never let logging take the emulator down
    }

    [LibraryImport("log", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int __android_log_write(int prio, string tag, string text);
}
