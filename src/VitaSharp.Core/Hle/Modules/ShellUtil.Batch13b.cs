using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceShellUtil / SceShellUtilLaunchApp and the event-flag half of SceShellSvc (libshellsvc.suprx 3.74, read in the
/// module). SceShellUtil is the client of the shell's "shell util" service: sceShellUtilInitEvents sets up the
/// client (a lightweight mutex, a condition and, unless bit 0 of the flags is set, the client thread that polls
/// every 17 ms); every request (Lock, Unlock and 20 others, each with its kind 1..0x18) takes one of four request
/// slots { +0 request, +4 kind, +8 parameter (the lock bits of Lock/Unlock), +0xC next }, is sent to the shell
/// asynchronously and is queued; the poll (NID_D71D8245, also what the client thread runs) takes the first finished
/// request off the queue and calls the registered handler(result, kind, parameter, user data). Locking 0x800 (the
/// second PS-button lock) also registers the shell events 0x35..0x39, which reach the handler as kind 0x15. The text
/// clipboard (NID_C4810C56/1B186905/D0DDEDBC) is a shared block of up to 0x1FFE UTF-16 characters.
/// A process that runs libshellsvc uses the module's own functions; these serve one that does not, where there is
/// no shell: the requests are taken and finish at once with result 0 (the lock bits are kept in
/// <see cref="LockedBits"/>), the clipboard lives in the HLE and the shell's event flags are never raised. The
/// requests that wait for the shell to act (launching an application, the synchronous queries) answer what they
/// answer with no shell service and are marked default.
/// </summary>
public sealed class ShellUtil : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    const string Lib = "SceShellUtil";
    const int ERR_CLIPBOARD = unchecked((int)0x80101800), ERR_NOT_INIT = unchecked((int)0x80101801),
        ERR_ALREADY = unchecked((int)0x80101802), ERR_BUSY = unchecked((int)0x80101803), ERR_INVALID = unchecked((int)0x80101804),
        ERR_TOO_LONG = unchecked((int)0x80101810);
    /// <summary>libshellsvc's answer when the shell's IPC server cannot be reached.</summary>
    const int SVC_UNAVAILABLE = unchecked((int)0x801009FF);
    const int SVC_BAD_EVENT = unchecked((int)0x80100910), SVC_BAD_EVENT2 = unchecked((int)0x80100970),
        SVC_HANDLER_SET = unchecked((int)0x80100901), SVC_TIMEOUT = unchecked((int)0x80100904);

    readonly object _lock = new();
    bool _init, _thread;
    uint _handler, _userData;
    int _ps2Locks;
    /// <summary>The request queue (kind, parameter), at most four in flight.</summary>
    readonly List<(uint Kind, uint Param)> _queue = [];

    /// <summary>The lock bits the process holds (SCE_SHELL_UTIL_LOCK_TYPE_*: 1 PS button, 2 quick menu, 4 power-off
    /// menu, 0x10 USB connection, 0x400 music player, 0x800 the second PS-button lock...).</summary>
    public uint LockedBits { get; private set; }

    static uint CallGuest(uint fn, params ReadOnlySpan<uint> args) => GuestThread.Current!.CallGuest(fn, args);

    // ------------------------------------------------------------------ the client

    /// <summary>(flags): bit 0 set = no client thread (the program calls the poll itself).</summary>
    [Hle("sceShellUtilInitEvents", Library = Lib)]
    int InitEvents(uint flags)
    {
        lock (_lock)
        {
            if (_init) return ERR_ALREADY;
            _init = true;
            _thread = (flags & 1) == 0;
            _queue.Clear();
            return 0;
        }
    }

    /// <summary>Ends the client: the thread stops, the handler, the queue and the 0x800 lock count are cleared.</summary>
    [Hle("NID_5C5373B9", Nid = 0x5C5373B9, Library = Lib)]
    void TermEvents()
    {
        lock (_lock)
        {
            if (!_init) return;
            _init = false; _thread = false;
            _handler = 0; _userData = 0; _ps2Locks = 0;
            _queue.Clear();
        }
    }

    [Hle("sceShellUtilRegisterEventHandler", Library = Lib)]
    int RegisterEventHandler(uint handler, uint userData)
    {
        lock (_lock)
        {
            if (!_init) return ERR_NOT_INIT;
            _handler = handler; _userData = userData;
            return 0;
        }
    }

    /// <summary>The poll: the first finished request leaves the queue and goes to the handler (all of them finish at
    /// once without a shell).</summary>
    [Hle("NID_D71D8245", Nid = 0xD71D8245, Library = Lib)]
    int Poll()
    {
        uint handler, ud;
        (uint Kind, uint Param) done;
        lock (_lock)
        {
            if (!_init) return ERR_NOT_INIT;
            if (_queue.Count == 0) return 0;
            done = _queue[0];
            _queue.RemoveAt(0);
            handler = _handler; ud = _userData;
        }
        if (handler != 0) CallGuest(handler, 0, done.Kind, done.Param, ud);
        return 0;
    }

    /// <summary>Queues a request of <paramref name="kind"/>; with the client thread the handler hears of it right
    /// away (the thread would on its next pass).</summary>
    int Request(uint kind, uint param = 0, Action? onAccepted = null)
    {
        lock (_lock)
        {
            if (!_init) return ERR_NOT_INIT;
            if (_queue.Count >= 4) return ERR_BUSY;
            onAccepted?.Invoke();
            _queue.Add((kind, param));
            if (!_thread) return 0;
        }
        while (Poll() == 0)
            lock (_lock) if (_queue.Count == 0) break;
        return 0;
    }

    [Hle("sceShellUtilLock", Library = Lib)]
    int Lock(uint type) => Request(1, type, () =>
    {
        LockedBits |= type;
        if ((type & 0x800) != 0) _ps2Locks++;
    });

    [Hle("sceShellUtilUnlock", Library = Lib)]
    int Unlock(uint type) => Request(2, type, () =>
    {
        LockedBits &= ~type;
        if ((type & 0x800) != 0 && _ps2Locks != 0) _ps2Locks--;
    });

    /// <summary>(type): the unlock that also drops every 0x800 lock.</summary>
    [Hle("NID_7A2CC043", Nid = 0x7A2CC043, Library = Lib)]
    int UnlockAll(uint type) => Request(3, type, () =>
    {
        LockedBits &= ~type;
        if ((type & 0x800) != 0) _ps2Locks = 0;
    });

    [Hle("NID_040997D6", Nid = 0x040997D6, Library = Lib)] int Request04(uint v) => Request(4);
    [Hle("NID_96BBF91B", Nid = 0x96BBF91B, Library = Lib)] int Request07(uint v) => Request(7);
    [Hle("NID_8B69AD27", Nid = 0x8B69AD27, Library = Lib)] int Request08(uint v) => Request(8);
    [Hle("NID_B65B60CA", Nid = 0xB65B60CA, Library = Lib)] int Request09(uint v) => Request(9);
    [Hle("NID_3219A1C0", Nid = 0x3219A1C0, Library = Lib)] int Request0A(uint v) => Request(0xA);
    [Hle("NID_9B0EE918", Nid = 0x9B0EE918, Library = Lib)] int Request0B() => Request(0xB);
    [Hle("NID_6430C45E", Nid = 0x6430C45E, Library = Lib)] int Request0C() => Request(0xC);
    [Hle("NID_29DB95D1", Nid = 0x29DB95D1, Library = Lib)] int Request0D(uint v) => Request(0xD);
    [Hle("NID_0B066FAD", Nid = 0x0B066FAD, Library = Lib)] int Request0E() => Request(0xE);
    [Hle("NID_01141995", Nid = 0x01141995, Library = Lib)] int Request10(uint v) => Request(0x10);
    [Hle("NID_0E7EAD89", Nid = 0x0E7EAD89, Library = Lib)] int Request12(uint v) => Request(0x12);
    [Hle("NID_F75C684D", Nid = 0xF75C684D, Library = Lib)] int Request13(uint v) => Request(0x13);
    [Hle("NID_8CAAB23B", Nid = 0x8CAAB23B, Library = Lib)] int Request14() => Request(0x14);
    [Hle("NID_71C21A8C", Nid = 0x71C21A8C, Library = Lib)] int Request16() => Request(0x16);
    [Hle("NID_FB612CA8", Nid = 0xFB612CA8, Library = Lib)] int Request17() => Request(0x17);
    [Hle("NID_9A83AC60", Nid = 0x9A83AC60, Library = Lib)] int Request18(uint a, uint b) => Request(0x18);

    /// <summary>(const SceWChar16 *a, const SceWChar16 *b): two texts of up to 8 characters (the rest is cut).</summary>
    [Hle("NID_40DFAC6B", Nid = 0x40DFAC6B, Library = Lib)]
    int Request05(uint a, uint b) => Request(5);

    /// <summary>(const char *text): up to 31 characters.</summary>
    [Hle("NID_03201F4B", Nid = 0x03201F4B, Library = Lib)] int Request06(uint text) => Request(6);
    [Hle("NID_E701BA71", Nid = 0xE701BA71, Library = Lib)] int Request11(uint text) => Request(0x11);

    // ------------------------------------------------------------------ requests that wait for the shell

    /// <summary>The synchronous queries (0xC000B/C/D, 0xC0017): no shell service to answer.</summary>
    [Hle("NID_8F2F143D", Nid = 0x8F2F143D, Library = Lib, Default = true)] int Sync0B(uint v) => SVC_UNAVAILABLE;
    [Hle("NID_636544FB", Nid = 0x636544FB, Library = Lib, Default = true)] int Sync0C(uint v) => SVC_UNAVAILABLE;
    [Hle("NID_BB54D049", Nid = 0xBB54D049, Library = Lib, Default = true)] int Sync0D(uint a, uint b, uint c) => SVC_UNAVAILABLE;
    [Hle("NID_6F730976", Nid = 0x6F730976, Library = Lib, Default = true)] int Sync17() => SVC_UNAVAILABLE;

    /// <summary>(const SceShellUtilLaunchAppParam *param = { const char *cmd (up to 959 characters) }): the shell
    /// launches the application of the command; without it nothing can start one.</summary>
    [Hle("sceShellUtilRequestLaunchApp", Library = Lib, Default = true)]
    [Hle("sceShellUtilLaunchAppRequestLaunchApp", Library = "SceShellUtilLaunchApp", Default = true)]
    int RequestLaunchApp(uint param) => param == 0 ? ERR_INVALID : SVC_UNAVAILABLE;

    /// <summary>The other waiting requests (0xC0015, and 0xC0019/0xC001A with a text of up to 255 characters).</summary>
    [Hle("NID_4A234ABB", Nid = 0x4A234ABB, Library = Lib, Default = true)] int Wait15() => SVC_UNAVAILABLE;
    [Hle("NID_48C7E8F8", Nid = 0x48C7E8F8, Library = Lib, Default = true)] int Wait19(uint text) => SVC_UNAVAILABLE;
    [Hle("NID_B67A0C68", Nid = 0xB67A0C68, Library = Lib, Default = true)] int Wait1A(uint text) => SVC_UNAVAILABLE;

    // ------------------------------------------------------------------ impose (quick menu) events

    uint _imposeCallback, _imposeArg;
    bool _imposeThread;

    /// <summary>(callback(0, kind, on), arg): the receiver of the quick-menu events.</summary>
    [Hle("NID_CE35B2B8", Nid = 0xCE35B2B8, Library = Lib)]
    int SetImposeCallback(uint callback, uint arg) { _imposeCallback = callback; _imposeArg = arg; return 0; }

    /// <summary>Starts the thread that waits on the shell's "SceShellUtilImposeEvent" flag (none without a shell, so
    /// no event arrives); 0x80101802 while it runs.</summary>
    [Hle("NID_EC5881A5", Nid = 0xEC5881A5, Library = Lib)]
    int StartImposeEvents()
    {
        if (_imposeThread) return ERR_ALREADY;
        _imposeCallback = 0; _imposeArg = 0;
        _imposeThread = true;
        return 0;
    }

    [Hle("NID_F979148A", Nid = 0xF979148A, Library = Lib)] void StopImposeEvents() => _imposeThread = false;

    // ------------------------------------------------------------------ text clipboard

    const int ClipboardMax = 0x1FFE;
    readonly object _clipLock = new();
    char[] _clipboard = [];

    /// <summary>(const SceWChar16 *text, SceSize length): replaces the clipboard (at most 0x1FFD characters).</summary>
    [Hle("NID_C4810C56", Nid = 0xC4810C56, Library = Lib)]
    int SetClipboard(uint text, uint length)
    {
        if (length >= ClipboardMax) return ERR_TOO_LONG;
        var c = new char[length];
        for (uint i = 0; i < length; i++) c[i] = (char)GuestMemory.Read<ushort>(text + i * 2);
        lock (_clipLock) _clipboard = c;
        return 0;
    }

    /// <summary>(SceWChar16 *buf, SceSize max, SceSize *length): copies up to <paramref name="max"/> characters and a
    /// terminating 0 (the buffer holds max + 1).</summary>
    [Hle("NID_1B186905", Nid = 0x1B186905, Library = Lib)]
    int GetClipboard(uint buf, uint max, uint lengthOut)
    {
        lock (_clipLock)
        {
            uint n = Math.Min(max, (uint)_clipboard.Length);
            for (uint i = 0; i < n; i++) GuestMemory.Write(buf + i * 2, (ushort)_clipboard[i]);
            GuestMemory.Write(buf + n * 2, (ushort)0);
            if (lengthOut != 0) GuestMemory.W32(lengthOut, n);
        }
        return 0;
    }

    [Hle("NID_D0DDEDBC", Nid = 0xD0DDEDBC, Library = Lib, ReturnsValue = true)]
    uint ClipboardLength() { lock (_clipLock) return (uint)_clipboard.Length; }

    /// <summary>The clipboard text (host side).</summary>
    public string Clipboard { get { lock (_clipLock) return new string(_clipboard); } }

    // ------------------------------------------------------------------ SceShellSvc: the shell's event flags
    // An event (&lt; 0x80) is bit ev % 32 of word ev / 32 of the shell's flags (the handler flags) and of word
    // ev / 32 + 4 (the state flags). Without the shell nobody raises them.

    readonly uint[] _eventFlags = new uint[8];
    readonly (uint Fn, uint Arg)[] _eventHandlers = new (uint, uint)[0x80];

    /// <summary>(event, fn(event, arg), arg): registers the handler sceShellSvc_FB03FBC9 runs for a raised event.</summary>
    [Hle("NID_D943CE15", Nid = 0xD943CE15, Library = "SceShellSvc")]
    int RegisterEvent(int ev, uint fn, uint arg)
    {
        if ((uint)ev >= 0x80) return SVC_BAD_EVENT;
        if (_eventHandlers[ev].Fn != 0) return SVC_HANDLER_SET;
        _eventFlags[ev / 32] &= ~(1u << (ev % 32));
        _eventHandlers[ev] = (fn, arg);
        return 0;
    }

    /// <summary>(event): lowers its handler flag.</summary>
    [Hle("NID_1FAB9A1E", Nid = 0x1FAB9A1E, Library = "SceShellSvc")]
    int ClearEvent(int ev)
    {
        if ((uint)ev >= 0x80) return SVC_BAD_EVENT;
        _eventFlags[ev / 32] &= ~(1u << (ev % 32));
        return 0;
    }

    /// <summary>(event): lowers its state flag.</summary>
    [Hle("NID_B452A177", Nid = 0xB452A177, Library = "SceShellSvc")]
    int ClearState(int ev)
    {
        if ((uint)ev >= 0x80) return SVC_BAD_EVENT2;
        _eventFlags[ev / 32 + 4] &= ~(1u << (ev % 32));
        return 0;
    }

    /// <summary>(event, bool *raised): whether its state flag is up.</summary>
    [Hle("NID_BD4161B0", Nid = 0xBD4161B0, Library = "SceShellSvc")]
    int PollState(int ev, uint raised)
    {
        if ((uint)ev >= 0x80) return SVC_BAD_EVENT2;
        GuestMemory.Write(raised, (byte)((_eventFlags[ev / 32 + 4] >> (ev % 32)) & 1));
        return 0;
    }

    /// <summary>(event, SceUInt32 *timeout): waits for the state flag; nobody raises it without the shell, so the wait
    /// ends as timed out (0x80100904).</summary>
    [Hle("NID_8D4F9F2E", Nid = 0x8D4F9F2E, Library = "SceShellSvc", Default = true)]
    int WaitState(int ev, uint timeout)
    {
        if ((uint)ev >= 0x80) return SVC_BAD_EVENT2;
        return ((_eventFlags[ev / 32 + 4] >> (ev % 32)) & 1) != 0 ? 0 : SVC_TIMEOUT;
    }

    /// <summary>Connects to the shell's IPC server ("SceShellSvc"); always answers 0.</summary>
    [Hle("NID_E91AEAD4", Nid = 0xE91AEAD4, Library = "SceShellSvc")] int Connect() => 0;

    /// <summary>Raises an event's flags (tests; the shell does it on the Vita).</summary>
    internal void RaiseEvent(int ev, bool state)
    {
        _eventFlags[ev / 32 + (state ? 4 : 0)] |= 1u << (ev % 32);
    }

    /// <summary>IPMI::Client::Config::estimateClientMemorySize(): the client's work memory for a configuration:
    /// 0x94 + 0x28 per entry of +0x20 and of +0x30 + 8 per entry of +0x2C.</summary>
    [Hle("_ZN4IPMI6Client6Config24estimateClientMemorySizeEv", Library = "SceIpmi", ReturnsValue = true)]
    uint IpmiClientMemorySize(uint config) =>
        GuestMemory.R32(config + 0x20) * 0x28 + GuestMemory.R32(config + 0x2C) * 8 + GuestMemory.R32(config + 0x30) * 0x28 + 0x94;
}
