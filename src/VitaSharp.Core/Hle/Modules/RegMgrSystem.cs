using VitaSharp.Core.Firmware;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceRegMgr (by category and name), SceRegMgrForSDK (sceRegMgrUtility*, by SceRegMgrSystemParam id) and
/// SceRegMgrService: the system software's access to the registry (<see cref="Registry"/>, the firmware's own key
/// list and defaults, persisted under vd0:registry). The keys games read through SceRegMgrForGame/SceAppUtil keep
/// following the emulator's options (language, enter button, date format) until the system software writes them.
/// Registry callbacks: RegMgrSystem.Callbacks.cs (a change notifies the started ones that watch the key).
/// </summary>
public sealed partial class RegMgrSystem : HleModule
{
    /// <summary>SCE_REGMGR_ERROR_* (inferred from the SceRegMgrForGame errors: 0x800D0004 invalid key/argument).</summary>
    const int ERR_INVALID = unchecked((int)0x800D0004);

    Registry Reg => Registry.For(Emu.Options.FsRoot);

    public override void Initialize()
    {
        // the keys SceAppUtil/SceRegMgrForGame answer from the emulator's options start from the same values here
        foreach (uint id in EmulatorOptionKeys)
            if (RegMgr.Table.TryGetValue(id, out var k))
                Reg.Define(k.Path, Registry.KeyType.Int, 4, () => (int)k.Default());
    }

    /// <summary>Button assignment, language, date/time format, time zone, summer time.</summary>
    static readonly uint[] EmulatorOptionKeys = [0x00229142, 0x00037502, 0x00088776, 0x00668503, 0x00100591, 0x00760538, 0x007F9315];

    static string? Str(uint p) => p == 0 ? null : GuestMemory.ReadCString(p, 0x100);

    int GetInt(string? category, string? name, Ptr<int> value, string fn)
    {
        if (category == null || name == null || value.IsNull) return ERR_INVALID;
        if (!TryReadInt(category, name, out int v))
        {
            Log.WarnOnce($"reg-{category}{name}", $"{fn}({category}, {name}): no such registry key");
            return ERR_INVALID;
        }
        value.Value = v;
        return 0;
    }

    /// <summary>An integer key as this process reads it: the registry's value, except the language of an application
    /// started with the emulator's per-game system language (<see cref="AppUtil.LanguageOverride"/>).</summary>
    bool TryReadInt(string category, string name, out int value)
    {
        if (!Reg.TryGetInt(category, name, out value))
        {
            return false;
        }

        // comparing the path is safe: it is the firmware's fixed key of the system language
        if (Registry.IsPath(category, name, LanguagePath) && AppUtil.LanguageOverride(Emu) is int language)
        {
            value = language;
        }

        return true;
    }

    const string LanguagePath = "/CONFIG/SYSTEM/language";

    int GetBytes(string? category, string? name, uint buf, int size, bool str, string fn)
    {
        if (category == null || name == null || buf == 0 || size < 0) return ERR_INVALID;
        if (Reg.Get(category, name) is not { } v)
        {
            Log.WarnOnce($"reg-{category}{name}", $"{fn}({category}, {name}): no such registry key");
            return ERR_INVALID;
        }
        var dst = GuestMemory.Span(buf, size);
        dst.Clear();
        int n = Math.Min(v.Length, size);
        if (str)
        {
            int nul = Array.IndexOf(v, (byte)0);
            n = Math.Min(nul < 0 ? v.Length : nul, Math.Max(size - 1, 0));
        }
        v.AsSpan(0, n).CopyTo(dst);
        return 0;
    }

    int SetBytes(string? category, string? name, uint buf, int size, string fn)
    {
        if (category == null || name == null || (buf == 0 && size > 0) || size < 0) return ERR_INVALID;
        if (!Reg.Set(category, name, size > 0 ? GuestMemory.Span(buf, size) : [])) return Unknown(fn, category, name);
        Log.Debug($"{fn}({category}{name})");
        return 0;
    }

    int Unknown(string fn, string category, string name)
    {
        Log.WarnOnce($"reg-{category}{name}", $"{fn}({category}, {name}): no such registry key");
        return ERR_INVALID;
    }

    // ------------------------------------------------------------------ SceRegMgr

    [Hle] int sceRegMgrGetKeyInt(uint category, uint name, Ptr<int> value) => GetInt(Str(category), Str(name), value, nameof(sceRegMgrGetKeyInt));
    [Hle] int sceRegMgrGetKeyStr(uint category, uint name, uint buf, int size) => GetBytes(Str(category), Str(name), buf, size, true, nameof(sceRegMgrGetKeyStr));
    [Hle] int sceRegMgrGetKeyBin(uint category, uint name, uint buf, int size) => GetBytes(Str(category), Str(name), buf, size, false, nameof(sceRegMgrGetKeyBin));

    [Hle]
    int sceRegMgrSetKeyInt(uint category, uint name, int value)
    {
        string? c = Str(category), n = Str(name);
        if (c == null || n == null) return ERR_INVALID;
        return Reg.SetInt(c, n, value) ? 0 : Unknown(nameof(sceRegMgrSetKeyInt), c, n);
    }

    [Hle] int sceRegMgrSetKeyStr(uint category, uint name, uint buf, int size) => SetBytes(Str(category), Str(name), buf, size, nameof(sceRegMgrSetKeyStr));
    [Hle] int sceRegMgrSetKeyBin(uint category, uint name, uint buf, int size) => SetBytes(Str(category), Str(name), buf, size, nameof(sceRegMgrSetKeyBin));

    /// <summary>The size every entry of a key list states at +0 (FW 3.74 SceRegistryMgr: anything else is 0x800D001C).</summary>
    const int KeyEntrySize = 0x10;

    const int ERR_ENTRY_SIZE = unchecked((int)0x800D001C);

    /// <summary>A key list holds 1 to 32 entries.</summary>
    const int MaxKeyEntries = 32;

    /// <summary>
    /// Checks a key list of sceRegMgrGetKeys/SetKeys as the FW 3.74 syscall does before it touches the registry
    /// (SceRegistryMgr 0x8100C090): a category and a list (0x800D0004 otherwise), 1 to 32 entries, each one 0x10 bytes
    /// { +0 0x10, +4 const char *name (not NULL), +8 u16 type (0 int, 1 string, 2 binary), +0xA u16 size, +0xC the
    /// integer itself or, for a string or binary key, a pointer to its bytes (not NULL) }.
    /// </summary>
    static int CheckKeyList(string? category, uint keys, int count)
    {
        if (category == null || keys == 0 || (uint)(count - 1) >= MaxKeyEntries)
        {
            return ERR_INVALID;
        }

        for (int i = 0; i < count; i++)
        {
            uint entry = keys + (uint)(i * KeyEntrySize);
            if (GuestMemory.R32(entry) != KeyEntrySize)
            {
                return ERR_ENTRY_SIZE;
            }

            ushort type = GuestMemory.Read<ushort>(entry + 8);
            bool bytes = type is (ushort)Registry.KeyType.Str or (ushort)Registry.KeyType.Bin;
            if (GuestMemory.R32(entry + 4) == 0 || (type != (ushort)Registry.KeyType.Int && !bytes)
                || (bytes && GuestMemory.R32(entry + 0xC) == 0))
            {
                return ERR_INVALID;
            }
        }

        return 0;
    }

    /// <summary>(const char *category, SceRegMgrKeysInfo *keys, int count): reads several keys of a category at once
    /// (list layout: <see cref="CheckKeyList"/>). An integer key's value goes to +0xC; a string or binary key's bytes
    /// go to the buffer +0xC points at, at most +0xA bytes.</summary>
    [Hle]
    int sceRegMgrGetKeys(uint category, uint keys, int count)
    {
        string? c = Str(category);
        int check = CheckKeyList(c, keys, count);
        if (check < 0)
        {
            return check;
        }

        for (int i = 0; i < count; i++)
        {
            uint entry = keys + (uint)(i * KeyEntrySize);
            string? name = Str(GuestMemory.R32(entry + 4));
            ushort type = GuestMemory.Read<ushort>(entry + 8);
            int result;
            if (type == (ushort)Registry.KeyType.Int)
            {
                result = GetInt(c, name, new Ptr<int>(entry + 0xC), nameof(sceRegMgrGetKeys));
            }
            else
            {
                result = GetBytes(c, name, GuestMemory.R32(entry + 0xC), GuestMemory.Read<ushort>(entry + 0xA),
                    type == (ushort)Registry.KeyType.Str, nameof(sceRegMgrGetKeys));
            }

            if (result < 0)
            {
                return result;
            }
        }

        return 0;
    }

    /// <summary>(const char *category, const SceRegMgrKeysInfo *keys, int count): writes several keys of a category
    /// at once (list layout: <see cref="CheckKeyList"/>): an integer from +0xC, a string or binary key from the +0xA
    /// bytes +0xC points at. The list is checked whole before anything is written. libmtp keeps the hosts it knows this
    /// way (/CONFIG/MTP/NET*/NN: host_id, host_name, last_connected_time, mac_address); every value used to be stored
    /// as the integer at +0xC, so those keys held the guest addresses of their bytes.</summary>
    [Hle]
    int sceRegMgrSetKeys(uint category, uint keys, int count)
    {
        string? c = Str(category);
        int check = CheckKeyList(c, keys, count);
        if (check < 0)
        {
            return check;
        }

        for (int i = 0; i < count; i++)
        {
            uint entry = keys + (uint)(i * KeyEntrySize);
            string name = Str(GuestMemory.R32(entry + 4))!;
            ushort type = GuestMemory.Read<ushort>(entry + 8);
            int result;
            if (type == (ushort)Registry.KeyType.Int)
            {
                result = Reg.SetInt(c!, name, (int)GuestMemory.R32(entry + 0xC)) ? 0 : Unknown(nameof(sceRegMgrSetKeys), c!, name);
            }
            else
            {
                result = SetBytes(c, name, GuestMemory.R32(entry + 0xC), GuestMemory.Read<ushort>(entry + 0xA), nameof(sceRegMgrSetKeys));
            }

            if (result < 0)
            {
                return result;
            }
        }

        return 0;
    }

    /// <summary>Same entries as sceRegMgrGetKeys; fills each type (0 int, 1 string, 2 binary).</summary>
    [Hle]
    int sceRegMgrGetKeysInfo(uint category, uint keys, int count)
    {
        string? c = Str(category);
        if (c == null || keys == 0) return ERR_INVALID;
        for (int i = 0; i < count; i++)
        {
            uint k = keys + (uint)i * 16;
            var key = Str(GuestMemory.R32(k + 4)) is { } n ? Reg.Find(c, n) : null;
            GuestMemory.W32(k + 8, (uint)(key?.Type ?? 0));
        }
        return 0;
    }

    [Hle] int sceRegMgrIsBlueScreen() => 0;

    // the registry callbacks (Register/Add/Start/Stop/Unregister): RegMgrSystem.Callbacks.cs

    /// <summary>What the callback stubs of older builds recorded, unused now: kept so the save states of those builds
    /// (positional layouts) still read field by field.</summary>
    readonly HashSet<(int Cb, string Category)> _callbacks = new();

    [Hle] int sceRegMgrRegisterDrvErrCallback(int callback) => 0;
    [Hle] int sceRegMgrUnregisterDrvErrCallback(int callback) => 0;

    // ------------------------------------------------------------------ SceRegMgrForSDK (by system parameter id)

    string? PathOf(uint id, string fn)
    {
        if (RegMgr.Table.TryGetValue(id, out var k)) return k.Path;
        Log.WarnOnce($"regid-{id:X}", $"{fn}(0x{id:X8}): unknown registry id");
        return null;
    }

    static (string Category, string Name) Split(string path)
    {
        int slash = path.LastIndexOf('/') + 1;
        return (path[..slash], path[slash..]);
    }

    [Hle]
    internal int sceRegMgrUtilityGetInt(uint id, Ptr<int> value)
    {
        if (PathOf(id, nameof(sceRegMgrUtilityGetInt)) is not { } p) return ERR_INVALID;
        var (c, n) = Split(p);
        return GetInt(c, n, value, nameof(sceRegMgrUtilityGetInt));
    }

    [Hle]
    int sceRegMgrUtilityGetStr(uint id, uint buf, int size)
    {
        if (PathOf(id, nameof(sceRegMgrUtilityGetStr)) is not { } p) return ERR_INVALID;
        var (c, n) = Split(p);
        return GetBytes(c, n, buf, size, true, nameof(sceRegMgrUtilityGetStr));
    }

    [Hle]
    int sceRegMgrUtilityGetBin(uint id, uint buf, int size)
    {
        if (PathOf(id, nameof(sceRegMgrUtilityGetBin)) is not { } p) return ERR_INVALID;
        var (c, n) = Split(p);
        return GetBytes(c, n, buf, size, false, nameof(sceRegMgrUtilityGetBin));
    }

    [Hle]
    int sceRegMgrUtilitySetInt(uint id, int value)
    {
        if (PathOf(id, nameof(sceRegMgrUtilitySetInt)) is not { } p) return ERR_INVALID;
        var (c, n) = Split(p);
        return Reg.SetInt(c, n, value) ? 0 : Unknown(nameof(sceRegMgrUtilitySetInt), c, n);
    }

    [Hle]
    int sceRegMgrUtilitySetStr(uint id, uint buf, int size)
    {
        if (PathOf(id, nameof(sceRegMgrUtilitySetStr)) is not { } p) return ERR_INVALID;
        var (c, n) = Split(p);
        return SetBytes(c, n, buf, size, nameof(sceRegMgrUtilitySetStr));
    }

    [Hle]
    int sceRegMgrUtilitySetBin(uint id, uint buf, int size)
    {
        if (PathOf(id, nameof(sceRegMgrUtilitySetBin)) is not { } p) return ERR_INVALID;
        var (c, n) = Split(p);
        return SetBytes(c, n, buf, size, nameof(sceRegMgrUtilitySetBin));
    }

    // ------------------------------------------------------------------ SceRegMgrService

    /// <summary>(void): the unit's sales region as the index of the registry manager's region table (the one
    /// sceRegMgrSrvGetRegionStr names: 1 J1, 2 UC2, ... 13 CN9), returned directly: every caller (the shell, libmtp,
    /// the Browser) uses the result as that index and passes no pointer. A North American PCH-1001: 2 (UC2), as
    /// GetRegionStr. It used to write 1 through r0 and return 0: the Browser's r0 was a string object of its own, so
    /// it was corrupted, and the region lookup failed; the Browser then freed a bad pointer and asserted.</summary>
    [Hle]
    int sceRegMgrSrvGetRegion() => 2;

    /// <summary>(int region): the product code (SceSblAimgr pscode +2: 0x103 J1, 0x104 US ... 0x10D CN) of a region-table
    /// index, returned directly, as SceRegistryMgr (FW 3.74): 0 is the unit's own code (a North American unit: 0x104,
    /// as _vshSblAimgrGetPscode); 11 is the unit's code only on a test/tool/DEX unit (0x100-0x102); 12 and anything
    /// past 13 are out of range (0x800D0018). Only system programs may ask (ksceSblACMgrIsSystemProgram; others 0x80010058).</summary>
    [Hle]
    int sceRegMgrSrvCnvRegionPsCode(int region)
    {
        if (!Emu.Access.IsSystemProgram)
        {
            return ErrNotSystemProgram;
        }

        const int OwnCode = 0x104, OutOfRange = unchecked((int)0x800D0018);
        return region switch
        {
            0 => OwnCode,
            1 => 0x103, 2 => 0x104, 3 => 0x109, 4 => 0x107, 5 => 0x105, 6 => 0x106,
            7 => 0x10A, 8 => 0x10B, 9 => 0x10C, 10 => 0x108, 13 => 0x10D,
            _ => OutOfRange, // 11 included: a retail unit
        };
    }
}
