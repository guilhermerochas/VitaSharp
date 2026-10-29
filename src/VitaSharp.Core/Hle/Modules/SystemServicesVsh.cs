using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Kernel services the system software (shell, initial setup, system apps) calls that games do not: the user-facing
/// error code text, callback registrations for devices a PCH-1000 with nothing attached never reports (Bluetooth,
/// USB host, HID keyboard, BD remote), process termination callbacks, the region string. Each follows the FW 3.74
/// kernel module named in its comment (os0:kd/bootimage.skprx, os0:kd/*.skprx, os0:us/driver_us.suprx).
/// </summary>
public sealed class SystemServicesVsh : HleModule
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
            _errors = Emu.Vfs.Resolve("os0:kd/error_table.bin") is { } p && File.Exists(p) ? ErrorTable.Parse(File.ReadAllBytes(p)) : null;
        }
        GuestMemory.WriteCString(buf, ErrorTable.Format(_errors, (uint)error), 0x10);
        return 0;
    }

    /// <summary>(int format): how the error history records errors; nothing is recorded here.</summary>
    [Hle] int sceErrorHistorySetDefaultFormat(int format) => 0;

    // ------------------------------------------------------------------ SceThreadmgr / SceProcessmgr

    /// <summary>The low 32 bits of the system time in microseconds (the kernel-named import the shell uses for its boot
    /// time stamp).</summary>
    [Hle] uint ksceKernelGetSystemTimeLow() => (uint)K.ProcessTimeUs;

    /// <summary>(SceUID callback, SceUID event): ties a callback to a kernel event object (the shell's RTC "cron"
    /// callback). The emulated RTC never raises that event, so the callback never runs.</summary>
    [Hle] int sceKernelRegisterCallbackToEvent(int callback, int ev) => 0;

    /// <summary>(SceUID callback): called when another process ends (libshellsvc's IPC server cleans up its clients).
    /// The IPC objects of an ended process are released by VitaSharp itself.</summary>
    [Hle] int sceKernelRegisterProcessTerminationCallback(int callback) => 0;
    [Hle] int sceKernelUnregisterProcessTerminationCallback(int callback) => 0;

    // ------------------------------------------------------------------ devices nothing is attached to

    /// <summary>(SceUID cb, int, int, int): Bluetooth events. No device ever pairs or connects.</summary>
    [Hle] int sceBtRegisterCallback(int cb, int a, int b, int c) => 0;
    /// <summary>The shell retries while this returns 0x802F1501 (stack not up); the emulated stack is up.</summary>
    [Hle] int sceBtGetConfiguration() => 0;
    /// <summary>(SceUID cb, int): USB host attach/detach events; nothing is ever plugged in.</summary>
    [Hle] int sceUsbdRegisterCallback(int cb, int a) => 0;
    /// <summary>(SceUID cb): HID keyboard enumeration / input hints; no keyboard is attached.</summary>
    [Hle] int sceHidKeyboardRegisterEnumHintCallback(int cb) => 0;
    [Hle] int sceHidKeyboardRegisterReadHintCallback(int cb) => 0;
    /// <summary>(SceUID cb): BD remote control events (PS TV); the handheld has none.</summary>
    [Hle] int sceCtrlRegisterBdRMCCallback(int cb) => 0;
    /// <summary>(const void *info): the accessibility button assignment from /CONFIG/ACCESSIBILITY/button_assign (or
    /// NULL for none). The host's own input mapping already decides which button is which.</summary>
    [Hle] int sceCtrlSetButtonRemappingInfo(uint info) => 0;

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
