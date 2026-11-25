using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Kernel services the system software (shell, initial setup, system apps) calls that games do not: the user-facing
/// error code text, callback registrations for devices a PCH-1000 with nothing attached never reports (Bluetooth,
/// USB host, HID keyboard, BD remote), process termination callbacks, the region string. Each follows the FW 3.74
/// kernel module named in its comment (os0:kd/bootimage.skprx, os0:kd/*.skprx, os0:us/driver_us.suprx).
/// </summary>
public sealed partial class SystemServicesVsh : HleModule
{
    // ------------------------------------------------------------------ SceErrorUser (SceError, bootimage)

    ErrorTable? _errors;
    bool _errorsLoaded;

    /// <summary>
    /// (char buf[0x10], int error): the code the system shows for an error ("C1-6775-5"). SceError looks the error up in
    /// os0:kd/error_table.bin: its position in the code list plus the table's base (2000) is the number, the prefix comes
    /// from the facility ((error &gt;&gt; 16) &amp; 0xFFF: 0x200..0x2FF "NS", else the table's ranges, else its default
    /// prefix), and the last digit is the sum of the number's digits mod 10. Unknown codes read "E-%08x".
    /// </summary>
    [Hle]
    int sceErrorGetExternalString(uint buf, int error)
    {
        if (buf == 0) return unchecked((int)0x80010016);
        if (!_errorsLoaded)
        {
            _errorsLoaded = true;
            _errors = Emu.Vfs.Resolve(Io.FirmwareLayout.KernelModules.Guest("error_table.bin")) is { } p && File.Exists(p) ? ErrorTable.Parse(File.ReadAllBytes(p)) : null;
        }
        GuestMemory.WriteCString(buf, ErrorTable.Format(_errors, (uint)error), 0x10);
        return 0;
    }

    /// <summary>(int format): how the error history records errors; nothing is recorded here.</summary>
    [Hle] int sceErrorHistorySetDefaultFormat(int format) => 0;

    /// <summary>(const char *message, const char *titleId, int, const void *info): an application adds the error it
    /// just showed to the system's error history (the Browser's "Cannot display the page.", Calendar's network error).
    /// The history is not kept; the entry goes to the log.</summary>
    [Hle]
    int sceErrorHistoryPostError(uint message, uint titleId, int a2, uint info)
    {
        Log.Info($"error history: {(titleId != 0 ? GuestMemory.ReadCString(titleId, 16) : "?")}: {(message != 0 ? GuestMemory.ReadCString(message, 256) : "")}");
        return 0;
    }

    // ------------------------------------------------------------------ SceThreadmgr / SceProcessmgr

    /// <summary>The low 32 bits of the system time in microseconds (the kernel-named import the shell uses for its boot
    /// time stamp).</summary>
    [Hle] uint ksceKernelGetSystemTimeLow() => (uint)K.ProcessTimeUs;

    /// <summary>(SceUID callback): called when another process ends (libshellsvc's IPC server cleans up its clients).
    /// The IPC objects of an ended process are released by VitaSharp itself.</summary>
    [Hle] int sceKernelRegisterProcessTerminationCallback(int callback) => 0;
    [Hle] int sceKernelUnregisterProcessTerminationCallback(int callback) => 0;

    // ------------------------------------------------------------------ devices nothing is attached to

    /// <summary>The shell retries while this returns 0x802F1501 (stack not up); the emulated stack is up.</summary>
    [Hle(Default = true)] int sceBtGetConfiguration() => 0;
    /// <summary>(int config): Settings switches Bluetooth on or off; the emulated stack is up, so it is accepted.</summary>
    [Hle(Default = true)] int sceBtSetConfiguration(int config) => 0;
    /// <summary>
    /// (uint mac0, uint mac1, SceBtRegisteredInfo *info, int count): the registered (paired) devices. FW 3.74 SceBt
    /// (0x81000554) walks its device list, takes the registered ones (all for a zero address, else the one of that
    /// address), writes a 0x100-byte record of each (+0 the address, +8 the device word, +0xC the class of device,
    /// +0x10 bit 0 connected and the HID flags, +0x14 vendor/product/version for a HID device, +0x1A its kind, +0x20
    /// the name) and returns how many it found. The paired keyboard and mouse (the host's, the emulator option) are its
    /// devices; with none the stack reports no device, as before.
    /// </summary>
    [Hle]
    int sceBtGetRegisteredInfo(uint mac0, uint mac1, uint info, int count)
    {
        var devices = PairedBluetoothDevices();
        if (devices.Count == 0)
        {
            return unchecked((int)0x802F0109);
        }

        int found = 0;
        foreach (PairedBluetoothDevice device in devices)
        {
            if (found >= count)
            {
                break;
            }

            if ((mac0 != 0 || mac1 != 0) && (device.Mac0 != mac0 || device.Mac1 != mac1))
            {
                continue;
            }

            if (info != 0)
            {
                uint record = info + (uint)found * BtRegisteredInfoSize;
                GuestMemory.Fill(record, 0, BtRegisteredInfoSize);
                GuestMemory.W32(record, device.Mac0);
                GuestMemory.W32(record + 4, device.Mac1);
                GuestMemory.W32(record + 0xC, device.ClassOfDevice);
                GuestMemory.W32(record + 0x10, 1u | 0x100u);           // connected, a HID device
                GuestMemory.Write(record + 0x1A, (byte)2);              // a HID device
                GuestMemory.WriteCString(record + 0x20, device.Name, 0x79);
            }

            found++;
        }

        return found;
    }

    /// <summary>The size of one SceBt registered device record.</summary>
    const uint BtRegisteredInfoSize = 0x100;

    /// <summary>A device paired with the Vita's Bluetooth: its address (two words, as SceBt passes it), class of device
    /// and name.</summary>
    internal readonly record struct PairedBluetoothDevice(uint Mac0, uint Mac1, uint ClassOfDevice, string Name);

    /// <summary>The Bluetooth address of the paired keyboard and mouse (a locally administered address, the same on
    /// every run).</summary>
    internal const uint PairedKeyboardMac0 = 0x0F1E2D02, PairedMouseMac0 = 0x0F1E2D03, PairedMac1 = 0x0000A55A;

    /// <summary>The devices paired now: the host's keyboard and mouse when the emulator option pairs them (class of
    /// device: peripheral, keyboard 0x2540 / pointing device 0x2580).</summary>
    internal List<PairedBluetoothDevice> PairedBluetoothDevices()
    {
        var devices = new List<PairedBluetoothDevice>(2);
        if (Emu?.Host.HidKeyboard != null)
        {
            devices.Add(new PairedBluetoothDevice(PairedKeyboardMac0, PairedMac1, 0x2540, "Keyboard"));
        }

        if (Emu?.Host.HidMouse != null)
        {
            devices.Add(new PairedBluetoothDevice(PairedMouseMac0, PairedMac1, 0x2580, "Mouse"));
        }

        return devices;
    }

    // SceWlan (bootimage SceWlanBt): the Wi-Fi configuration word; no Wi-Fi chip, the word is kept.
    int _wlanConfig;
    [Hle] int sceWlanGetConfiguration() => _wlanConfig;
    [Hle] int sceWlanSetConfiguration(int config) { _wlanConfig = config; return 0; }

    /// <summary>(int type 0..1, int enable): routes keyboard input to the caller (Sign Up's text fields); no keyboard.</summary>
    [Hle] int sceHidKeyboardSetIntercept(int type, int enable) => (uint)type > 1 ? unchecked((int)0x80010016) : 0;
    [Hle] int sceHidKeyboardUnregisterEnumHintCallback(int cb) => 0;
    [Hle] int sceHidKeyboardUnregisterReadHintCallback(int cb) => 0;

    /// <summary>(int what, int flags): the WebKit process stops further module loads of the given kinds (its sandbox).
    /// Modules are loaded only on request here: accepted.</summary>
    [Hle] int sceKernelInhibitLoadingModule(int what, int flags) => 0;

    /// <summary>(SceUID cb, int): USB host attach/detach events; nothing is ever plugged in.</summary>
    [Hle] int sceUsbdRegisterCallback(int cb, int a) => 0;
    /// <summary>(SceUID cb): HID keyboard enumeration / input hints; no keyboard is attached.</summary>
    [Hle] int sceHidKeyboardRegisterEnumHintCallback(int cb) => 0;
    [Hle] int sceHidKeyboardRegisterReadHintCallback(int cb) => 0;
    /// <summary>(SceUID cb): BD remote control events (PS TV); the handheld has none.</summary>
    [Hle(Cost = HleCost.KernelEntry)] int sceCtrlRegisterBdRMCCallback(int cb) => 0;
    /// <summary>(const void *info): the accessibility button assignment from /CONFIG/ACCESSIBILITY/button_assign (or
    /// NULL for none). The host's own input mapping already decides which button is which.</summary>
    [Hle(Cost = HleCost.KernelEntry)] int sceCtrlSetButtonRemappingInfo(uint info) => 0;

    // ------------------------------------------------------------------ SceRegMgrService (os0:kd registry manager)

    /// <summary>(char *buf, int size): the unit's region code from the registry manager's table (index 1..13: J1, UC2,
    /// AU3, CEK, CEL, KR2, E12, TW1, RU3, MX2, TOL, BR2, CN9); returns the index. A North American PCH-1001: "UC2" (2).</summary>
    [Hle]
    int sceRegMgrSrvGetRegionStr(uint buf, int size)
    {
        if (buf == 0 || size <= 3) return unchecked((int)0x800D0004);
        GuestMemory.WriteCString(buf, "UC2", size);
        return 2;
    }
}

/// <summary>os0:kd/error_table.bin: default prefix (4), u16 range count, u16 code count, the ranges (u16 first facility,
/// u16 facility count, prefix[4]), u16 base number, u16 code count, then the u32 error codes.</summary>
public sealed class ErrorTable
{
    public string DefaultPrefix = "C0";
    public readonly List<(int First, int Count, string Prefix)> Ranges = new();
    public int Base;
    public uint[] Codes = [];

    static string Str(ReadOnlySpan<byte> b) => System.Text.Encoding.ASCII.GetString(b).TrimEnd('\0');

    public static ErrorTable? Parse(byte[] d)
    {
        if (d.Length < 8) return null;
        var t = new ErrorTable { DefaultPrefix = Str(d.AsSpan(0, 4)) };
        int ranges = BitConverter.ToUInt16(d, 4), p = 8;
        for (int i = 0; i < ranges && p + 8 <= d.Length; i++, p += 8)
            t.Ranges.Add((BitConverter.ToUInt16(d, p), BitConverter.ToUInt16(d, p + 2), Str(d.AsSpan(p + 4, 4))));
        if (p + 4 > d.Length) return null;
        t.Base = BitConverter.ToUInt16(d, p);
        int count = Math.Min(BitConverter.ToUInt16(d, p + 2), (d.Length - p - 4) / 4);
        t.Codes = new uint[count];
        for (int i = 0; i < count; i++) t.Codes[i] = BitConverter.ToUInt32(d, p + 4 + i * 4);
        return t;
    }

    public static string Format(ErrorTable? t, uint error)
    {
        int idx = t == null ? -1 : Array.IndexOf(t.Codes, error);
        if (idx < 0) return $"E-{error:x8}";
        int facility = (int)((error >> 16) & 0xFFF);
        string prefix = facility - 0x200 is >= 0 and <= 0xFF ? "NS"
            : t!.Ranges.FirstOrDefault(r => facility >= r.First && facility < r.First + r.Count).Prefix ?? t.DefaultPrefix;
        int number = t!.Base + idx, sum = 0;
        for (int n = number; n != 0; n /= 10) sum += n % 10;
        return $"{prefix}-{number}-{sum % 10}";
    }
}
