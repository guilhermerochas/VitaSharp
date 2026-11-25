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

    /// <summary>FW 3.74 SceHid: consumer controls follow the keyboard's checks; with nothing paired Enumerate finds none
    /// and Read knows no handle; the hint callbacks are kept. Before, the six were missing imports.</summary>
    [Fact]
    public void ConsumerControlsAnswerAsTheFirmware()
    {
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

    private const ushort PlayPause = 0xCD, VolumeUp = 0xE9, VolumeDown = 0xEA, Mute = 0xE2, NextTrack = 0xB5;

    /// <summary>
    /// FW 3.74 SceHid keeps each application collection of a Bluetooth device as a device of its own (FUN_81000524:
    /// up to eight per device): a keyboard's media keys (its consumer collection) are a consumer-control device next
    /// to the keyboard. Its reports (0x10 bytes: four 16-bit consumer usages held, then the time) carry at most four
    /// usages, in the order they were found, and drop the consumer page's reserved usages (FUN_81002218,
    /// FUN_810016d0); each report once per reader, the read hint on each. Before, no consumer control was paired.
    /// </summary>
    [Fact]
    public void APairedKeyboardsMediaKeysAreItsConsumerControl()
    {
        ThreadMgr threads = _harness.Module<ThreadMgr>();
        _harness.OnGuest(_ =>
        {
            int enumHint = Call(threads, "sceKernelCreateCallback", "hid-cc-enum", 0u, SyntheticEntry, 0u);
            int readHint = Call(threads, "sceKernelCreateCallback", "hid-cc-read", 0u, SyntheticEntry, 0u);
            Assert.Equal(0, Call(Module, "sceHidConsumerControlRegisterEnumHintCallback", enumHint));
            Assert.Equal(0, Call(Module, "sceHidConsumerControlRegisterReadHintCallback", readHint));

            var keyboard = new HidKeyboard();
            _harness.Emu.Host.HidKeyboard = keyboard;     // the keyboard (and its media keys) connects
            Assert.Equal(1, Call(threads, "sceKernelGetCallbackCount", enumHint));
            uint handles = _harness.Alloc(32);
            Assert.Equal(1, Call(Module, "sceHidConsumerControlEnumerate", handles, 8));
            int handle = (int)GuestMemory.R32(handles);
            Assert.Equal(Hid.ConsumerControlHandle, handle);
            uint reports = _harness.Alloc(Hid.ConsumerControlReportSize * 16);

            keyboard.ConsumerControl.Key(PlayPause, true, 1000);
            keyboard.ConsumerControl.Key(VolumeUp, true, 2000);
            keyboard.ConsumerControl.Key(0x10, true, 2500);       // a reserved usage: dropped
            keyboard.ConsumerControl.Key(0x29D, true, 2600);      // past the defined ones: dropped
            keyboard.ConsumerControl.Key(PlayPause, false, 3000);
            keyboard.ConsumerControl.Key(PlayPause, false, 3500); // released already: no report
            keyboard.Key(UsageA, true, 3600);                     // a key of the keyboard is no consumer report
            Assert.Equal(3, Call(threads, "sceKernelGetCallbackCount", readHint));
            Assert.Equal(3, Call(Module, "sceHidConsumerControlRead", handle, reports, 16));
            Assert.Equal((PlayPause, (ushort)0), (GuestMemory.Read<ushort>(reports), GuestMemory.Read<ushort>(reports + 2)));
            Assert.Equal(1000ul, GuestMemory.Read<ulong>(reports + 8));
            uint second = reports + Hid.ConsumerControlReportSize;
            Assert.Equal((PlayPause, VolumeUp, (ushort)0), (GuestMemory.Read<ushort>(second), GuestMemory.Read<ushort>(second + 2), GuestMemory.Read<ushort>(second + 4)));
            uint third = reports + 2 * Hid.ConsumerControlReportSize;
            Assert.Equal((VolumeUp, (ushort)0), (GuestMemory.Read<ushort>(third), GuestMemory.Read<ushort>(third + 2)));
            Assert.Equal(3000ul, GuestMemory.Read<ulong>(third + 8));
            Assert.Equal(0, Call(Module, "sceHidConsumerControlRead", handle, reports, 16));

            // five held: the report keeps the first four
            keyboard.ConsumerControl.Key(VolumeDown, true, 4000);
            keyboard.ConsumerControl.Key(Mute, true, 5000);
            keyboard.ConsumerControl.Key(NextTrack, true, 6000);
            keyboard.ConsumerControl.Key(PlayPause, true, 7000);
            Assert.Equal(4, Call(Module, "sceHidConsumerControlRead", handle, reports, 16));
            uint last = reports + 3 * Hid.ConsumerControlReportSize;
            Assert.Equal((VolumeUp, VolumeDown, Mute, NextTrack),
                (GuestMemory.Read<ushort>(last), GuestMemory.Read<ushort>(last + 2), GuestMemory.Read<ushort>(last + 4), GuestMemory.Read<ushort>(last + 6)));

            _harness.Emu.Host.HidKeyboard = null;         // the keyboard disconnects: its media keys go with it
            Assert.Equal(2, Call(threads, "sceKernelGetCallbackCount", enumHint));
            Assert.Equal(0, Call(Module, "sceHidConsumerControlEnumerate", handles, 8));
            Assert.Equal(NotDevice, Call(Module, "sceHidConsumerControlRead", handle, reports, 16));
        });
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
        Assert.Equal(0xCD, HidUsages.ConsumerFromAndroidKeyCode(85));  // KEYCODE_MEDIA_PLAY_PAUSE: Play/Pause
        Assert.Equal(0xE9, HidUsages.ConsumerFromAndroidKeyCode(24));  // KEYCODE_VOLUME_UP: Volume Increment
        Assert.Equal(0, HidUsages.ConsumerFromAndroidKeyCode(29));     // KEYCODE_A: no media key
        for (int keyCode = 0; keyCode < 300; keyCode++)
        {
            ushort usage = HidUsages.ConsumerFromAndroidKeyCode(keyCode);
            Assert.True(usage == 0 || HidConsumerControl.IsKept(usage), $"key code {keyCode} maps to a usage SceHid drops");
        }
    }

    /// <summary>GLFW's scancode of the same key differs by platform (GLFW 3.4): Wayland passes the evdev code
    /// (KEY_PLAYPAUSE 164), X11 the X keycode (172), Windows the set 1 code (0x122); each platform's code gives the
    /// same usage, and the other platform's code for that key gives none of it.</summary>
    [Fact]
    public void MediaKeyScancodesFollowGlfwsPlatform()
    {
        Assert.Equal(ScancodeKind.Evdev, HidUsages.ScancodeKindOfGlfwPlatform(0x00060003));
        Assert.Equal(ScancodeKind.X11Keycode, HidUsages.ScancodeKindOfGlfwPlatform(0x00060004));
        Assert.Equal(ScancodeKind.WindowsSet1, HidUsages.ScancodeKindOfGlfwPlatform(0x00060001));
        Assert.Equal(ScancodeKind.None, HidUsages.ScancodeKindOfGlfwPlatform(0x00060002));
        Assert.Equal(PlayPause, HidUsages.ConsumerFromScancode(164, ScancodeKind.Evdev));
        Assert.Equal(PlayPause, HidUsages.ConsumerFromScancode(172, ScancodeKind.X11Keycode));
        Assert.Equal(PlayPause, HidUsages.ConsumerFromScancode(0x122, ScancodeKind.WindowsSet1));
        Assert.Equal(Mute, HidUsages.ConsumerFromScancode(113, ScancodeKind.Evdev));          // KEY_MUTE
        Assert.Equal(VolumeDown, HidUsages.ConsumerFromScancode(114, ScancodeKind.Evdev));    // KEY_VOLUMEDOWN
        Assert.Equal(VolumeUp, HidUsages.ConsumerFromScancode(115, ScancodeKind.Evdev));      // KEY_VOLUMEUP
        Assert.Equal(NextTrack, HidUsages.ConsumerFromScancode(163, ScancodeKind.Evdev));     // KEY_NEXTSONG
        // on Wayland 172 is KEY_HOMEPAGE, 121 KEY_KPCOMMA: not the X11 keys of those numbers
        Assert.NotEqual(PlayPause, HidUsages.ConsumerFromScancode(172, ScancodeKind.Evdev));
        Assert.NotEqual(Mute, HidUsages.ConsumerFromScancode(121, ScancodeKind.Evdev));
        Assert.Equal(0, HidUsages.ConsumerFromScancode(0x122, ScancodeKind.None));
    }

    /// <summary>The PC's media keys, which GLFW reports with no key token: by the Windows scancode (set 1 with the
    /// extended bit) and the X11 keycode (evdev + 8); every one maps to a usage SceHid keeps, and the scancodes of
    /// ordinary keys (A: 0x1E on Windows, 38 on X11) map to none.</summary>
    [Fact]
    public void PcMediaKeysHaveTheirConsumerUsages()
    {
        Assert.Equal(PlayPause, HidUsages.ConsumerFromWindowsScancode(0x122));
        Assert.Equal(VolumeUp, HidUsages.ConsumerFromWindowsScancode(0x130));
        Assert.Equal(NextTrack, HidUsages.ConsumerFromWindowsScancode(0x119));
        Assert.Equal(0, HidUsages.ConsumerFromWindowsScancode(0x1E));
        Assert.Equal(0, HidUsages.ConsumerFromWindowsScancode(0x22)); // G: the same code without the extended bit
        Assert.Equal(PlayPause, HidUsages.ConsumerFromX11Keycode(172));
        Assert.Equal(Mute, HidUsages.ConsumerFromX11Keycode(121));
        Assert.Equal(VolumeDown, HidUsages.ConsumerFromX11Keycode(122));
        Assert.Equal(0, HidUsages.ConsumerFromX11Keycode(38));
        for (int code = 0; code < 0x200; code++)
        {
            ushort windows = HidUsages.ConsumerFromWindowsScancode(code), x11 = HidUsages.ConsumerFromX11Keycode(code);
            Assert.True(windows == 0 || HidConsumerControl.IsKept(windows), $"Windows scancode 0x{code:X} maps to a usage SceHid drops");
            Assert.True(x11 == 0 || HidConsumerControl.IsKept(x11), $"X11 keycode {code} maps to a usage SceHid drops");
        }
    }

    /// <summary>macOS media keys: NSSystemDefined events of subtype 8, the key in data1 bits 16..31 (NX_KEYTYPE_*) and
    /// its state in bits 8..15 (0x0A down, 0x0B up); another subtype, state or key is none.</summary>
    [Fact]
    public void MacMediaKeyEventsHaveTheirConsumerUsages()
    {
        static long Data1(int key, int state) => ((long)key << 16) | ((long)state << 8);

        Assert.Equal(PlayPause, HidUsages.ConsumerFromMacSystemEvent(8, Data1(16, 0x0A), out bool down));
        Assert.True(down);
        Assert.Equal(PlayPause, HidUsages.ConsumerFromMacSystemEvent(8, Data1(16, 0x0B), out down));
        Assert.False(down);
        Assert.Equal(VolumeUp, HidUsages.ConsumerFromMacSystemEvent(8, Data1(0, 0x0A), out _));
        Assert.Equal(VolumeDown, HidUsages.ConsumerFromMacSystemEvent(8, Data1(1, 0x0A), out _));
        Assert.Equal(Mute, HidUsages.ConsumerFromMacSystemEvent(8, Data1(7, 0x0A), out _));
        Assert.Equal(NextTrack, HidUsages.ConsumerFromMacSystemEvent(8, Data1(17, 0x0A), out _));
        Assert.Equal(0, HidUsages.ConsumerFromMacSystemEvent(7, Data1(16, 0x0A), out _));   // another subtype
        Assert.Equal(0, HidUsages.ConsumerFromMacSystemEvent(8, Data1(16, 0x01), out _));   // not a key state
        Assert.Equal(0, HidUsages.ConsumerFromMacSystemEvent(8, Data1(2, 0x0A), out _));    // brightness: no consumer key here
        for (int key = 0; key < 32; key++)
        {
            ushort usage = HidUsages.ConsumerFromMacSystemEvent(8, Data1(key, 0x0A), out _);
            Assert.True(usage == 0 || HidConsumerControl.IsKept(usage), $"NX key {key} maps to a usage SceHid drops");
        }
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
