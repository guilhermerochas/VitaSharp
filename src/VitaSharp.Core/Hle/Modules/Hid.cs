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

    /// <summary>The callbacks registered for device changes (enumeration hints) and new reports (read hints).</summary>
    [SnapshotMissing]
    private readonly HashSet<int> _hintCallbacks = [];

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

    /// <summary>(SceUID callback, ...): a callback notified when devices of the kind come or go (the enumeration hint)
    /// or have new reports (the read hint). The paired devices are there from the start: kept for the unregister.</summary>
    [Hle("sceHidKeyboardRegisterEnumHintCallback")]
    [Hle("sceHidKeyboardRegisterReadHintCallback")]
    [Hle("sceHidMouseRegisterEnumHintCallback")]
    [Hle("sceHidMouseRegisterReadHintCallback")]
    int sceHidRegisterHintCallback(int callback)
    {
        lock (_hintCallbacks)
        {
            _hintCallbacks.Add(callback);
        }

        return 0;
    }

    [Hle("sceHidKeyboardUnregisterEnumHintCallback")]
    [Hle("sceHidKeyboardUnregisterReadHintCallback")]
    [Hle("sceHidMouseUnregisterEnumHintCallback")]
    [Hle("sceHidMouseUnregisterReadHintCallback")]
    int sceHidUnregisterHintCallback(int callback)
    {
        lock (_hintCallbacks)
        {
            _hintCallbacks.Remove(callback);
        }

        return 0;
    }

    /// <summary>The guest time a host event gets (microseconds of the guest clock).</summary>
    public static ulong NowUs => (ulong)(GuestClock.Seconds * 1_000_000);
}
