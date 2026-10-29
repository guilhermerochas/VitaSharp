using VitaSharp.Core.Firmware;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceRegMgr (by category and name), SceRegMgrForSDK (sceRegMgrUtility*, by SceRegMgrSystemParam id) and
/// SceRegMgrService: the system software's access to the registry (<see cref="Registry"/>, the firmware's own key
/// list and defaults, persisted under vd0:registry). The keys games read through SceRegMgrForGame/SceAppUtil keep
/// following the emulator's options (language, enter button, date format) until the system software writes them.
/// Registry callbacks are accepted and recorded; changes do not notify them yet.
/// </summary>
public sealed class RegMgrSystem : HleModule
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
        if (!Reg.TryGetInt(category, name, out int v))
        {
            Log.WarnOnce($"reg-{category}{name}", $"{fn}({category}, {name}): no such registry key");
            return ERR_INVALID;
        }
        value.Value = v;
        return 0;
    }

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

    /// <summary>(category, SceRegMgrKeys *keys, count): keys[i] = { size, const char *name, type, int value } (16
    /// bytes, Vita3K); fills each value.</summary>
    [Hle]
    int sceRegMgrGetKeys(uint category, uint keys, int count)
    {
        string? c = Str(category);
        if (c == null || keys == 0) return ERR_INVALID;
        for (int i = 0; i < count; i++)
        {
            uint k = keys + (uint)i * 16;
            string? n = Str(GuestMemory.R32(k + 4));
            GuestMemory.W32(k + 12, n != null && Reg.TryGetInt(c, n, out int v) ? (uint)v : 0);
        }
        return 0;
    }

    [Hle]
    int sceRegMgrSetKeys(uint category, uint keys, int count)
    {
        string? c = Str(category);
        if (c == null || keys == 0) return ERR_INVALID;
        for (int i = 0; i < count; i++)
        {
            uint k = keys + (uint)i * 16;
            if (Str(GuestMemory.R32(k + 4)) is { } n) Reg.SetInt(c, n, (int)GuestMemory.R32(k + 12));
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

    readonly HashSet<(int Cb, string Category)> _callbacks = new();

    [Hle] int sceRegMgrRegisterCallback(int callback) => 0;
    [Hle] int sceRegMgrUnregisterCallback(int callback) { lock (_callbacks) _callbacks.RemoveWhere(c => c.Cb == callback); return 0; }

    [Hle]
    int sceRegMgrAddRegistryCallback(int callback, uint category)
    {
        if (Str(category) is { } c) lock (_callbacks) _callbacks.Add((callback, c));
        return 0;
    }

    [Hle] int sceRegMgrStartCallback(int callback) => 0;
    [Hle] int sceRegMgrStopCallback(int callback) => 0;
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
    int sceRegMgrUtilityGetInt(uint id, Ptr<int> value)
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

    /// <summary>(int *region): the unit's sales region. No prototype is known; the out pointer form matches how the
    /// shell calls it. VitaSharp emulates a North American unit (SCE_REGION 1? inferred), consistent with the
    /// "us" country of the NP keys.</summary>
    [Hle]
    int sceRegMgrSrvGetRegion(Ptr<int> region)
    {
        if (!region.IsNull) region.Value = 1;
        return 0;
    }
}
