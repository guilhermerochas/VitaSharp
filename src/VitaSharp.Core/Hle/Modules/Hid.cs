using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceHid: the keyboards and mice paired over Bluetooth (or plugged into a PS TV). FW 3.74's SceHid (os0:kd/bootimage,
/// read in the module, study only) keeps up to 8 devices of each kind; Enumerate copies the handles of the connected
/// ones (a NULL list is 0x803B0001, at most 8), Read and Peek check the handle (below 1: 0x803B0002; a NULL report list
/// or a negative count: 0x803B0001; a handle that is no device of the kind: 0x803B0003), take at most 16 reports (a
/// keyboard report is 0x18 bytes, a mouse report 0x10) and return how many they copied, at once: a device's ring of
/// reports with a position per reader. The host's keyboard and mouse are the paired devices when the emulator option
/// says so (<see cref="HostServices.HidKeyboard"/>, <see cref="HostServices.HidMouse"/>); without it nothing is paired
/// and Enumerate finds nothing, as before.
/// </summary>
public sealed partial class Hid : HleModule
{
    private const int ERR_INVALID_POINTER = unchecked((int)0x803B0001), ERR_INVALID_HANDLE = unchecked((int)0x803B0002),
        ERR_NOT_DEVICE = unchecked((int)0x803B0003);

    /// <summary>The handle of the (single) paired keyboard and mouse: the module numbers its devices from 1.</summary>
    internal const int KeyboardHandle = 1, MouseHandle = 1;

    /// <summary>Devices of one kind the module keeps (Enumerate's limit), reports one call copies at most.</summary>
    private const int MaxDevices = 8, MaxReports = 16;

    internal const uint KeyboardReportSize = 0x18, MouseReportSize = 0x10;

    /// <summary>This process's position in the keyboard's and the mouse's reports (the module keeps one per reader).</summary>
    [SnapshotMissing] // states of builds before it: nothing read
    private long _keyboardPosition, _mousePosition;

    /// <summary>The program's keyboard intercept (sceHidKeyboardSetIntercept: 0 or 1).</summary>
    [SnapshotMissing]
    private int _keyboardIntercept;

    /// <summary>The callbacks registered for keyboard and mouse changes (enumeration hints) and new reports (read
    /// hints), by kind.</summary>
    [SnapshotMissing]
    private readonly HashSet<int> _keyboardEnumHints = [], _keyboardReadHints = [], _mouseEnumHints = [], _mouseReadHints = [];

    /// <summary>The host devices this module listens to (the read hints follow the device paired now).</summary>
    [SnapshotIgnore]
    private HostServices? _hintHost;

    [SnapshotIgnore]
    private HidKeyboard? _hintKeyboard;

    [SnapshotIgnore]
    private HidMouse? _hintMouse;

    private HidKeyboard? Keyboard => Emu?.Host.HidKeyboard;

    private HidMouse? Mouse => Emu?.Host.HidMouse;

    [Hle]
    int sceHidKeyboardEnumerate(uint handles, int count) => Enumerate(handles, count, Keyboard != null ? KeyboardHandle : 0);

    [Hle]
    int sceHidMouseEnumerate(uint handles, int count) => Enumerate(handles, count, Mouse != null ? MouseHandle : 0);

    private static int Enumerate(uint handles, int count, int handle)
    {
        if (handles == 0)
        {
            return ERR_INVALID_POINTER;
        }

        if (Math.Min(count, MaxDevices) < 1 || handle == 0)
        {
            return 0;
        }

        GuestMemory.W32(handles, (uint)handle);
        return 1;
    }

    [Hle]
    int sceHidKeyboardRead(int handle, uint reports, int count) => ReadKeyboard(handle, reports, count, consume: true);

    /// <summary>Like Read, without taking the reports: the next Read gets them again.</summary>
    [Hle]
    int sceHidKeyboardPeek(int handle, uint reports, int count) => ReadKeyboard(handle, reports, count, consume: false);

    private int ReadKeyboard(int handle, uint reports, int count, bool consume)
    {
        if (CheckRead(handle, reports, count) is var error and not 0)
        {
            return error;
        }

        if (Keyboard is not { } keyboard || handle != KeyboardHandle)
        {
            return ERR_NOT_DEVICE;
        }

        Span<HidKeyboardReport> buffer = stackalloc HidKeyboardReport[MaxReports];
        int n = keyboard.Read(ref _keyboardPosition, buffer[..Math.Min(count, MaxReports)], consume);
        for (int i = 0; i < n; i++)
        {
            uint p = reports + (uint)i * KeyboardReportSize;
            HidKeyboardReport r = buffer[i];
            GuestMemory.Fill(p, 0, KeyboardReportSize);
            GuestMemory.Write(p + 1, r.Modifiers);                // modifiers[0]: Ctrl/Shift/Alt/GUI, left and right
            for (int k = 0; k < 6; k++)
            {
                GuestMemory.Write(p + 3 + (uint)k, r.Key(k));      // keycodes[6]: USB HID usages
            }

            GuestMemory.Write(p + 0x10, r.TimestampUs);
        }

        return n;
    }

    [Hle]
    int sceHidMouseRead(int handle, uint reports, int count)
    {
        if (CheckRead(handle, reports, count) is var error and not 0)
        {
            return error;
        }

        if (Mouse is not { } mouse || handle != MouseHandle)
        {
            return ERR_NOT_DEVICE;
        }

        Span<HidMouseReport> buffer = stackalloc HidMouseReport[MaxReports];
        int n = mouse.Read(ref _mousePosition, buffer[..Math.Min(count, MaxReports)], consume: true);
        for (int i = 0; i < n; i++)
        {
            uint p = reports + (uint)i * MouseReportSize;
            HidMouseReport r = buffer[i];
            GuestMemory.Fill(p, 0, MouseReportSize);
            GuestMemory.Write(p, r.Buttons);
            GuestMemory.Write(p + 2, r.DeltaX);
            GuestMemory.Write(p + 4, r.DeltaY);
            GuestMemory.Write(p + 6, r.Wheel);
            GuestMemory.Write(p + 7, r.Tilt);
            GuestMemory.Write(p + 8, r.TimestampUs);
        }

        return n;
    }

    /// <summary>The argument checks Read and Peek make first, in the module's order (0 = fine).</summary>
    private static int CheckRead(int handle, uint reports, int count)
    {
        if (handle < 1)
        {
            return ERR_INVALID_HANDLE;
        }

        if (reports == 0 || count < 0)
        {
            return ERR_INVALID_POINTER;
        }

        return 0;
    }

    /// <summary>(handle): drops the keyboard's reports this program has not read.</summary>
    [Hle]
    int sceHidKeyboardClear(int handle)
    {
        if (handle < 1)
        {
            return ERR_INVALID_HANDLE;
        }

        if (Keyboard is not { } keyboard || handle != KeyboardHandle)
        {
            return ERR_NOT_DEVICE;
        }

        keyboard.Clear(ref _keyboardPosition);
        return 0;
    }

    /// <summary>(int intercept 0/1): whether this program takes the keyboard from the system (the IME); 2 and up are
    /// refused.</summary>
    [Hle]
    int sceHidKeyboardSetIntercept(uint intercept)
    {
        if (intercept >= 2)
        {
            return ERR_INVALID_POINTER;
        }

        _keyboardIntercept = (int)intercept;
        return 0;
    }

    [Hle]
    int sceHidKeyboardGetIntercept(uint intercept)
    {
        if (intercept == 0)
        {
            return ERR_INVALID_POINTER;
        }

        GuestMemory.W32(intercept, _keyboardIntercept == 1 ? 1u : 0u);
        return 0;
    }

    // (SceUID callback): FW 3.74 SceHid keeps a list per kind and hint and notifies its callbacks when a device of the
    // kind connects or disconnects (enumeration) or has a new report (read); the program then enumerates or reads.
    [Hle] int sceHidKeyboardRegisterEnumHintCallback(int callback) => RegisterHint(_keyboardEnumHints, callback);

    [Hle] int sceHidKeyboardRegisterReadHintCallback(int callback) => RegisterHint(_keyboardReadHints, callback);

    [Hle] int sceHidMouseRegisterEnumHintCallback(int callback) => RegisterHint(_mouseEnumHints, callback);

    [Hle] int sceHidMouseRegisterReadHintCallback(int callback) => RegisterHint(_mouseReadHints, callback);

    [Hle] int sceHidKeyboardUnregisterEnumHintCallback(int callback) => UnregisterHint(_keyboardEnumHints, callback);

    [Hle] int sceHidKeyboardUnregisterReadHintCallback(int callback) => UnregisterHint(_keyboardReadHints, callback);

    [Hle] int sceHidMouseUnregisterEnumHintCallback(int callback) => UnregisterHint(_mouseEnumHints, callback);

    [Hle] int sceHidMouseUnregisterReadHintCallback(int callback) => UnregisterHint(_mouseReadHints, callback);

    private int RegisterHint(HashSet<int> hints, int callback)
    {
        lock (hints)
        {
            hints.Add(callback);
        }

        ListenToHost();
        return 0;
    }

    private static int UnregisterHint(HashSet<int> hints, int callback)
    {
        lock (hints)
        {
            hints.Remove(callback);
        }

        return 0;
    }

    /// <summary>Listens to the host's device changes (once) and to the reports of the devices paired now.</summary>
    private void ListenToHost()
    {
        if (Emu?.Host is not { } host)
        {
            return;
        }

        lock (_keyboardEnumHints)
        {
            if (_hintHost == null)
            {
                _hintHost = host;
                host.HidDevicesChanged += OnDevicesChanged;
            }
        }

        FollowDevices();
    }

    /// <summary>A keyboard or mouse connected or disconnected: the enumeration hints of its kind, and the read hints
    /// follow the device paired now.</summary>
    private void OnDevicesChanged()
    {
        bool keyboardChanged = !ReferenceEquals(_hintKeyboard, Keyboard);
        bool mouseChanged = !ReferenceEquals(_hintMouse, Mouse);
        FollowDevices();
        if (keyboardChanged)
        {
            NotifyHints(_keyboardEnumHints);
        }

        if (mouseChanged)
        {
            NotifyHints(_mouseEnumHints);
        }
    }

    private void FollowDevices()
    {
        lock (_keyboardEnumHints)
        {
            if (!ReferenceEquals(_hintKeyboard, Keyboard))
            {
                if (_hintKeyboard != null)
                {
                    _hintKeyboard.ReportAdded -= OnKeyboardReport;
                }

                _hintKeyboard = Keyboard;
                if (_hintKeyboard != null)
                {
                    _hintKeyboard.ReportAdded += OnKeyboardReport;
                }
            }

            if (!ReferenceEquals(_hintMouse, Mouse))
            {
                if (_hintMouse != null)
                {
                    _hintMouse.ReportAdded -= OnMouseReport;
                }

                _hintMouse = Mouse;
                if (_hintMouse != null)
                {
                    _hintMouse.ReportAdded += OnMouseReport;
                }
            }
        }
    }

    private void OnKeyboardReport() => NotifyHints(_keyboardReadHints);

    private void OnMouseReport() => NotifyHints(_mouseReadHints);

    /// <summary>Notifies the callbacks of one hint list (each runs at its thread's next callback check).</summary>
    private void NotifyHints(HashSet<int> hints)
    {
        Span<int> callbacks = stackalloc int[16];
        int n = 0;
        lock (hints)
        {
            foreach (int callback in hints)
            {
                if (n < callbacks.Length)
                {
                    callbacks[n++] = callback;
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            Emu.Kernel.Objects.Get<Kernel.Callback>(callbacks[i])?.Notify(0);
        }
    }

    // ------------------------------------------------------------------ consumer controls
    // FW 3.74 SceHid keeps consumer-control devices (media keys of a Bluetooth keyboard or remote) like the keyboards:
    // Enumerate (NULL list 0x803B0001, at most 8) copies the handles of the connected ones, Read checks the handle
    // (below 1: 0x803B0002; NULL reports or a negative count: 0x803B0001; no device of that handle: 0x803B0003) and
    // copies at most 16 reports of 0x10 bytes, and the hint callbacks go into lists of their own. No consumer-control
    // device is ever paired here (the host's keyboard is paired as a keyboard only), so Enumerate finds none and Read
    // knows no handle. (The controller calls, 0x803B0004 in 3.74, are in FirmwareConstantReturns.cs.)

    /// <summary>The callbacks registered for consumer-control changes and reports (never notified: no device).</summary>
    [SnapshotMissing]
    private readonly HashSet<int> _consumerEnumHints = [], _consumerReadHints = [];

    [Hle]
    int sceHidConsumerControlEnumerate(uint handles, int count) => Enumerate(handles, count, 0);

    [Hle]
    int sceHidConsumerControlRead(int handle, uint reports, int count)
    {
        if (CheckRead(handle, reports, count) is var error and not 0)
        {
            return error;
        }

        return ERR_NOT_DEVICE;
    }

    [Hle] int sceHidConsumerControlRegisterEnumHintCallback(int callback) => RegisterHint(_consumerEnumHints, callback);

    [Hle] int sceHidConsumerControlRegisterReadHintCallback(int callback) => RegisterHint(_consumerReadHints, callback);

    [Hle] int sceHidConsumerControlUnregisterEnumHintCallback(int callback) => UnregisterHint(_consumerEnumHints, callback);

    [Hle] int sceHidConsumerControlUnregisterReadHintCallback(int callback) => UnregisterHint(_consumerReadHints, callback);

    /// <summary>The guest time a host event gets (microseconds of the guest clock).</summary>
    public static ulong NowUs => (ulong)(GuestClock.Seconds * 1_000_000);
}
