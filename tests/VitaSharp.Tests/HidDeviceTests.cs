using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceHid's keyboards and mice (psp2/hid.h; FW 3.74 SceHid): with the host keyboard and mouse paired (the emulator
/// option) Enumerate lists one device of each kind and Read returns the reports a scripted keyboard and mouse made,
/// oldest first, each once per reader, Peek without taking them; the module's argument checks and codes; without the
/// option nothing is paired, as before.
/// </summary>
[Collection("guest")]
public sealed class HidDeviceTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "hid");

    public void Dispose() => _harness.Dispose();

    private Hid Module => _harness.Module<Hid>();

    private const int InvalidPointer = unchecked((int)0x803B0001), InvalidHandle = unchecked((int)0x803B0002), NotDevice = unchecked((int)0x803B0003);

    private const byte UsageA = 0x04, UsageB = 0x05, LeftShift = 0xE1;

    [Fact]
    public void WithoutTheOptionNothingIsPaired()
    {
        uint handles = _harness.Alloc(32);
        Assert.Equal(0, Call(Module, "sceHidKeyboardEnumerate", handles, 8));
        Assert.Equal(0, Call(Module, "sceHidMouseEnumerate", handles, 8));
        Assert.Equal(NotDevice, Call(Module, "sceHidKeyboardRead", 1, _harness.Alloc(0x18 * 4), 4));
        Assert.Equal(InvalidPointer, Call(Module, "sceHidKeyboardEnumerate", 0u, 8));
    }

    [Fact]
    public void APairedKeyboardReportsTheKeysPressed()
    {
        var keyboard = new HidKeyboard();
        _harness.Emu.Host.HidKeyboard = keyboard;
        uint handles = _harness.Alloc(32);
        Assert.Equal(1, Call(Module, "sceHidKeyboardEnumerate", handles, 8));
        int handle = (int)GuestMemory.R32(handles);
        Assert.Equal(Hid.KeyboardHandle, handle);
        uint reports = _harness.Alloc(Hid.KeyboardReportSize * 16);

        // Shift+A, then B, then everything released
        keyboard.Key(LeftShift, true, 1000);
        keyboard.Key(UsageA, true, 2000);
        keyboard.Key(UsageB, true, 3000);
        keyboard.Key(UsageA, false, 4000);
        keyboard.Key(UsageA, false, 4500); // released already: no report
        Assert.Equal(4, Call(Module, "sceHidKeyboardPeek", handle, reports, 16));
        Assert.Equal(4, Call(Module, "sceHidKeyboardRead", handle, reports, 16));
        Assert.Equal(0x02, GuestMemory.Read<byte>(reports + 1));                       // left Shift
        Assert.Equal(0, GuestMemory.Read<byte>(reports + 3));
        uint second = reports + Hid.KeyboardReportSize;
        Assert.Equal((0x02, UsageA, (byte)0), (GuestMemory.Read<byte>(second + 1), GuestMemory.Read<byte>(second + 3), GuestMemory.Read<byte>(second + 4)));
        Assert.Equal(2000ul, GuestMemory.Read<ulong>(second + 0x10));
        uint third = reports + 2 * Hid.KeyboardReportSize;
        Assert.Equal((UsageA, UsageB), (GuestMemory.Read<byte>(third + 3), GuestMemory.Read<byte>(third + 4)));
        uint fourth = reports + 3 * Hid.KeyboardReportSize;
        Assert.Equal((UsageB, (byte)0), (GuestMemory.Read<byte>(fourth + 3), GuestMemory.Read<byte>(fourth + 4)));

        // each report once: nothing new now; a count of 2 takes the oldest two, the next read the rest
        Assert.Equal(0, Call(Module, "sceHidKeyboardRead", handle, reports, 16));
        keyboard.Key(LeftShift, false, 5000);
        keyboard.Key(UsageB, false, 6000);
        keyboard.Key(UsageA, true, 7000);
        Assert.Equal(2, Call(Module, "sceHidKeyboardRead", handle, reports, 2));
        Assert.Equal(1, Call(Module, "sceHidKeyboardRead", handle, reports, 16));
        Assert.Equal(UsageA, GuestMemory.Read<byte>(reports + 3));

        // Clear drops what was not read
        keyboard.Key(UsageA, false, 8000);
        Assert.Equal(0, Call(Module, "sceHidKeyboardClear", handle));
        Assert.Equal(0, Call(Module, "sceHidKeyboardRead", handle, reports, 16));
    }

    [Fact]
    public void APairedMouseReportsMovesButtonsAndTheWheel()
    {
        var mouse = new HidMouse();
        _harness.Emu.Host.HidMouse = mouse;
        uint handles = _harness.Alloc(32);
        Assert.Equal(1, Call(Module, "sceHidMouseEnumerate", handles, 8));
        int handle = (int)GuestMemory.R32(handles);
        uint reports = _harness.Alloc(Hid.MouseReportSize * 16);

        mouse.Move(5, -3, 0, 100);
        mouse.Button(0, true, 200);
        mouse.Move(0, 0, 1, 300);
        mouse.Button(0, true, 400); // held already: no report
        Assert.Equal(3, Call(Module, "sceHidMouseRead", handle, reports, 16));
        Assert.Equal((0, (short)5, (short)-3), (GuestMemory.Read<byte>(reports), GuestMemory.Read<short>(reports + 2), GuestMemory.Read<short>(reports + 4)));
        Assert.Equal((byte)1, GuestMemory.Read<byte>(reports + Hid.MouseReportSize));
        Assert.Equal(((byte)1, (sbyte)1), (GuestMemory.Read<byte>(reports + 2 * Hid.MouseReportSize), GuestMemory.Read<sbyte>(reports + 2 * Hid.MouseReportSize + 6)));
        Assert.Equal(300ul, GuestMemory.Read<ulong>(reports + 2 * Hid.MouseReportSize + 8));
        Assert.Equal(0, Call(Module, "sceHidMouseRead", handle, reports, 16));
    }

    /// <summary>FW 3.74 SceHid notifies the enumeration hints of a kind when a device of it connects or disconnects,
    /// and its read hints when the device has a new report; an unregistered callback is not notified.</summary>
    [Fact]
    public void HintCallbacksHearConnectionsAndReports()
    {
        ThreadMgr threads = _harness.Module<ThreadMgr>();
        _harness.OnGuest(_ =>
        {
            int enumHint = Call(threads, "sceKernelCreateCallback", "hid-enum", 0u, SyntheticEntry, 0u);
            int readHint = Call(threads, "sceKernelCreateCallback", "hid-read", 0u, SyntheticEntry, 0u);
            int mouseHint = Call(threads, "sceKernelCreateCallback", "hid-mouse", 0u, SyntheticEntry, 0u);
            Assert.Equal(0, Call(Module, "sceHidKeyboardRegisterEnumHintCallback", enumHint));
            Assert.Equal(0, Call(Module, "sceHidKeyboardRegisterReadHintCallback", readHint));
            Assert.Equal(0, Call(Module, "sceHidMouseRegisterEnumHintCallback", mouseHint));

            var keyboard = new HidKeyboard();
            _harness.Emu.Host.HidKeyboard = keyboard;     // the keyboard connects
            Assert.Equal(1, Call(threads, "sceKernelGetCallbackCount", enumHint));
            Assert.Equal(0, Call(threads, "sceKernelGetCallbackCount", mouseHint)); // a keyboard is no mouse

            keyboard.Key(UsageA, true, 10);
            keyboard.Key(UsageA, false, 20);
            Assert.Equal(2, Call(threads, "sceKernelGetCallbackCount", readHint));

            Assert.Equal(0, Call(Module, "sceHidKeyboardUnregisterReadHintCallback", readHint));
            keyboard.Key(UsageB, true, 30);
            Assert.Equal(2, Call(threads, "sceKernelGetCallbackCount", readHint));

            _harness.Emu.Host.HidKeyboard = null;         // the keyboard disconnects
            Assert.Equal(2, Call(threads, "sceKernelGetCallbackCount", enumHint));
            _harness.Emu.Host.HidMouse = new HidMouse();  // a mouse connects
            Assert.Equal(1, Call(threads, "sceKernelGetCallbackCount", mouseHint));
        });
    }

    /// <summary>FW 3.74 SceBt lists the registered devices (0x100-byte records: address, class of device, connected
    /// flag, name) and their connection state: the paired keyboard and mouse are there for the shell's Bluetooth
    /// settings; with nothing paired, "no device" as before.</summary>
    [Fact]
    public void BluetoothListsThePairedDevices()
    {
        SystemServicesVsh bt = _harness.Module<SystemServicesVsh>();
        uint info = _harness.Alloc(0x100 * 4);
        Assert.Equal(unchecked((int)0x802F0109), Call(bt, "sceBtGetRegisteredInfo", 0u, 0u, info, 4));

        _harness.Emu.Host.HidKeyboard = new HidKeyboard();
        _harness.Emu.Host.HidMouse = new HidMouse();
        Assert.Equal(2, Call(bt, "sceBtGetRegisteredInfo", 0u, 0u, info, 4));
        Assert.Equal("Keyboard", GuestMemory.ReadCString(info + 0x20, 0x79));
        Assert.Equal(0x2540u, GuestMemory.R32(info + 0xC));
        Assert.Equal(1u, GuestMemory.R32(info + 0x10) & 1);
        Assert.Equal("Mouse", GuestMemory.ReadCString(info + 0x100 + 0x20, 0x79));
        uint mouseMac0 = GuestMemory.R32(info + 0x100), mac1 = GuestMemory.R32(info + 0x104);

        // one address: that device only; a count of 1: the first
        Assert.Equal(1, Call(bt, "sceBtGetRegisteredInfo", mouseMac0, mac1, info, 4));
        Assert.Equal("Mouse", GuestMemory.ReadCString(info + 0x20, 0x79));
        Assert.Equal(1, Call(bt, "sceBtGetRegisteredInfo", 0u, 0u, info, 1));
        Assert.Equal(2, Call(bt, "sceBtGetConnectingInfo", mouseMac0, mac1));
        Assert.Equal(unchecked((int)0x802F0109), Call(bt, "sceBtGetConnectingInfo", 0x12345678u, mac1));
    }

    /// <summary>FW 3.74 SceHid: consumer controls follow the keyboard's checks (none is paired: Enumerate finds none,
    /// Read knows no handle) and keep their hint callbacks. Before, the six were missing imports.</summary>
    [Fact]
    public void ConsumerControlsAnswerAsTheFirmware()
    {
        _harness.Emu.Host.HidKeyboard = new HidKeyboard();   // a paired keyboard is no consumer control
        uint handles = _harness.Alloc(32);
        uint reports = _harness.Alloc(0x10 * 16);
        Assert.Equal(InvalidPointer, Call(Module, "sceHidConsumerControlEnumerate", 0u, 8));
        Assert.Equal(0, Call(Module, "sceHidConsumerControlEnumerate", handles, 8));
        Assert.Equal(InvalidHandle, Call(Module, "sceHidConsumerControlRead", 0, reports, 4));
        Assert.Equal(InvalidPointer, Call(Module, "sceHidConsumerControlRead", 1, 0u, 4));
        Assert.Equal(InvalidPointer, Call(Module, "sceHidConsumerControlRead", 1, reports, -1));
        Assert.Equal(NotDevice, Call(Module, "sceHidConsumerControlRead", 1, reports, 4));
        Assert.Equal(0, Call(Module, "sceHidConsumerControlRegisterEnumHintCallback", 5));
        Assert.Equal(0, Call(Module, "sceHidConsumerControlRegisterReadHintCallback", 5));
        Assert.Equal(0, Call(Module, "sceHidConsumerControlUnregisterEnumHintCallback", 5));
        Assert.Equal(0, Call(Module, "sceHidConsumerControlUnregisterReadHintCallback", 5));
    }

    /// <summary>A guest address for the callbacks' entry (they are counted, not run).</summary>
    private const uint SyntheticEntry = 0x81000001;

    /// <summary>The host keys' USB HID usages: the desktop's by their Silk.NET names, Android's by their key codes.</summary>
    [Fact]
    public void HostKeysHaveTheirUsbHidUsages()
    {
        Assert.Equal(0x04, HidUsages.FromKeyName("A"));
        Assert.Equal(0x1D, HidUsages.FromKeyName("Z"));
        Assert.Equal(0x1E, HidUsages.FromKeyName("Number1"));
        Assert.Equal(0x27, HidUsages.FromKeyName("Number0"));
        Assert.Equal(0x28, HidUsages.FromKeyName("Enter"));
        Assert.Equal(0x45, HidUsages.FromKeyName("F12"));
        Assert.Equal(0xE1, HidUsages.FromKeyName("ShiftLeft"));
        Assert.Equal(0, HidUsages.FromKeyName("Unknown"));
        Assert.Equal(0x04, HidUsages.FromAndroidKeyCode(29));   // KEYCODE_A
        Assert.Equal(0x27, HidUsages.FromAndroidKeyCode(7));    // KEYCODE_0
        Assert.Equal(0x2A, HidUsages.FromAndroidKeyCode(67));   // KEYCODE_DEL = backspace
        Assert.Equal(0xE0, HidUsages.FromAndroidKeyCode(113));  // KEYCODE_CTRL_LEFT
        Assert.Equal(0, HidUsages.FromAndroidKeyCode(4));       // KEYCODE_BACK: no key of a keyboard
    }

    /// <summary>The module's checks: a handle below 1, a NULL report list or a negative count, a handle of no device;
    /// at most 16 reports per call; the intercept switch.</summary>
    [Fact]
    public void TheArgumentChecksAreTheModules()
    {
        var keyboard = new HidKeyboard();
        _harness.Emu.Host.HidKeyboard = keyboard;
        uint reports = _harness.Alloc(Hid.KeyboardReportSize * 20);
        Assert.Equal(InvalidHandle, Call(Module, "sceHidKeyboardRead", 0, reports, 4));
        Assert.Equal(InvalidPointer, Call(Module, "sceHidKeyboardRead", 1, 0u, 4));
        Assert.Equal(InvalidPointer, Call(Module, "sceHidKeyboardRead", 1, reports, -1));
        Assert.Equal(NotDevice, Call(Module, "sceHidKeyboardRead", 2, reports, 4));
        for (int i = 0; i < 20; i++)
        {
            keyboard.Key((byte)(UsageA + i), i % 2 == 0, (ulong)i);
            keyboard.Key((byte)(UsageA + i), false, (ulong)i);
        }

        Assert.Equal(16, Call(Module, "sceHidKeyboardRead", 1, reports, 20));
        uint flag = _harness.Alloc(4);
        Assert.Equal(InvalidPointer, Call(Module, "sceHidKeyboardGetIntercept", 0u));
        Assert.Equal(0, Call(Module, "sceHidKeyboardSetIntercept", 1u));
        Assert.Equal(0, Call(Module, "sceHidKeyboardGetIntercept", flag));
        Assert.Equal(1u, GuestMemory.R32(flag));
        Assert.Equal(InvalidPointer, Call(Module, "sceHidKeyboardSetIntercept", 2u));
    }
}
