using System.Runtime.InteropServices;
using VitaSharp.Android.Logging;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Services;

/// <summary>
/// VitaSharp's log in logcat: every line <see cref="Log"/> writes goes to __android_log_write (liblog) under the tag
/// "VitaSharp" (guest output: "VitaSharp-Guest") with the matching priority; the formatting is
/// <see cref="LogcatFormat"/> (unit-tested in VitaSharp.Tests). <c>adb logcat -s VitaSharp VitaSharp-Guest</c> shows both.
/// The console copy is switched off (stdout would reach logcat a second time under another tag); the file log in
/// &lt;data&gt;/logs stays (SessionLog).
/// </summary>
public sealed partial class LogcatSink : ILogSink
{
    public static void Install()
    {
        Log.Sink = new LogcatSink();
        Log.ConsoleEnabled = false;
    }

    public void Write(LogLevel level, string thread, string message)
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
