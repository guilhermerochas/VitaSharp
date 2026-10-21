using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Small SceShell-backed utilities with no shell behind them: SceNotificationUtil (logged), SceIncomingDialog (nobody
/// answers: the dialog times out), SceClipboard (in-memory text) and SceGameUpdate (no update available).
/// </summary>
public sealed class SystemUtil : HleModule
{
    readonly object _lock = new();

    // ------------------------------------------------------------------ SceNotificationUtil

    internal const int NOTIFICATION_ERROR_INTERNAL = unchecked((int)0x80106300);
    /// <summary>The text buffer is 0x410 bytes of UTF-16.</summary>
    const int NotificationMaxChars = 0x208;

    /// <summary>Last notification text (for a front-end toast / tests).</summary>
    public string? LastNotification { get; private set; }

    [Hle]
    int sceNotificationUtilSendNotification(uint text)
    {
        if (text == 0) return NOTIFICATION_ERROR_INTERNAL;
        var s = CommonDialog.ReadUtf16(text, NotificationMaxChars);
        lock (_lock) LastNotification = s;
        Log.Info($"Notification: {s}");
        return 0;
    }

    [Hle] int sceNotificationUtilCleanHistory() { lock (_lock) LastNotification = null; return 0; }

    // ------------------------------------------------------------------ SceIncomingDialog

    internal const int INCOMINGDIALOG_ERROR_INVALID_ARG = unchecked((int)0x80106201);
    internal enum IncomingStatus { NotRunning = 0, Accepted = 1, Running = 2, Rejected = 3, Closed = 4, Busy = 5, Timeout = 6 }

    bool _incomingInit;
    IncomingStatus _incomingStatus;

    [Hle]
    int sceIncomingDialogInitialize(int initType)
    {
        if (initType != 1) return INCOMINGDIALOG_ERROR_INVALID_ARG;
        lock (_lock) { _incomingInit = true; _incomingStatus = IncomingStatus.NotRunning; }
        return 0;
    }

    /// <summary>SceIncomingDialogParam (0x25C): +0x84 titleid[0x10], +0x98 dialogTimer, +0x15A dialogText (UTF-16[0x80]).
    /// Nobody can accept the call: the dialog expires at once.</summary>
    [Hle]
    int sceIncomingDialogOpen(uint param)
    {
        lock (_lock)
        {
            if (param == 0 || !_incomingInit) return INCOMINGDIALOG_ERROR_INVALID_ARG;
            string text = CommonDialog.ReadUtf16(param + 0x15A, 0x80);
            string title = GuestMemory.ReadCString(param + 0x84, 0x10);
            Log.Info($"sceIncomingDialogOpen: \"{text}\" (title {title}, {GuestMemory.R32(param + 0x98)} s): timed out");
            _incomingStatus = IncomingStatus.Timeout;
        }
        return 0;
    }

    [Hle] int sceIncomingDialogGetStatus() { lock (_lock) return (int)_incomingStatus; }
    [Hle] int sceIncomingDialogSwitchToDialog() => 0;
    [Hle] int sceIncomingDialogClose() { lock (_lock) _incomingStatus = IncomingStatus.Closed; return 0; }

    [Hle]
    int sceIncomingDialogFinish()
    {
        lock (_lock) { _incomingInit = false; _incomingStatus = IncomingStatus.NotRunning; }
        return 0;
    }

    // ------------------------------------------------------------------ SceClipboard

    /// <summary>Clipboard text (UTF-8 bytes). There is no host clipboard hook; the text lives in the process.</summary>
    byte[] _clipboard = [];

    /// <summary>Inferred prototype: (const char *text, SceSize length). Stores up to length bytes or the first NUL.</summary>
    [Hle]
    int sceClipboardSetText(uint text, uint length)
    {
        if (text == 0) return SceError.INVALID_ARGUMENT;
        var src = GuestMemory.Span(text, (int)Math.Min(length, 0x10000u));
        int n = src.IndexOf((byte)0);
        var data = (n < 0 ? src : src[..n]).ToArray();
        lock (_lock) _clipboard = data;
        Log.Debug($"sceClipboardSetText({data.Length} bytes)");
        return 0;
    }

    /// <summary>Inferred prototype: (char *text, SceSize length). Copies up to length - 1 bytes plus a NUL.</summary>
    [Hle]
    int sceClipboardGetText(uint text, uint length)
    {
        if (text == 0 || length == 0) return SceError.INVALID_ARGUMENT;
        byte[] data;
        lock (_lock) data = _clipboard;
        int n = (int)Math.Min((uint)data.Length, length - 1);
        data.AsSpan(0, n).CopyTo(GuestMemory.Span(text, n));
        GuestMemory.Span(text + (uint)n, 1)[0] = 0;
        return 0;
    }

    internal string ClipboardText { get { lock (_lock) return Encoding.UTF8.GetString(_clipboard); } }

    // ------------------------------------------------------------------ SceGameUpdate (inferred prototypes)

    bool _gameUpdateInit;

    [Hle] int sceGameUpdateInit(uint param) { _gameUpdateInit = true; return 0; }

    /// <summary>No network / PSN: the check finds nothing. Nothing is written until the result layout is known.</summary>
    [Hle]
    int sceGameUpdateRun(uint param)
    {
        Log.Info($"sceGameUpdateRun(0x{param:X8}): no update available{(_gameUpdateInit ? "" : " (not initialized)")}");
        return 0;
    }

    [Hle] int sceGameUpdateAbort() => 0;
    [Hle] int sceGameUpdateTerm() { _gameUpdateInit = false; return 0; }
}
