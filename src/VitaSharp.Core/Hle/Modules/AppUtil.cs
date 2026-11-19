using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceAppUtil: system parameters, safe memory and save data slots (files under savedata0:).</summary>
public sealed partial class AppUtil : HleModule
{
    internal const int ERR_PARAMETER = unchecked((int)0x80100600), ERR_NOT_INITIALIZED = unchecked((int)0x80100601),
        ERR_NOT_MOUNTED = unchecked((int)0x80100604), ERR_APPEVENT_PARSE_INVALID_DATA = unchecked((int)0x80100620),
        ERR_SLOT_NOT_FOUND = unchecked((int)0x80100641);
    const int ERRNO_ENOENT = unchecked((int)0x80010002), ERR_SAFE_MEMORY_IO = unchecked((int)0x80800014);
    const int SafeMemorySize = 64 * 1024;
    /// <summary>SceAppUtilSaveDataSlotParam: status, title[64], subTitle[128], detail[512], iconPath[64], userParam,
    /// sizeKiB, modifiedTime (SceDateTime), reserved[48].</summary>
    internal const int SlotParamSize = 4 + 64 + 128 + 512 + 64 + 4 + 4 + 16 + 48;
    const int SlotUserParamOffset = 4 + 64 + 128 + 512 + 64, SlotSizeOffset = SlotUserParamOffset + 4, SlotTimeOffset = SlotSizeOffset + 4;

    string? Host(string vitaPath) => Emu.Vfs.Resolve(vitaPath);

    internal string SavePath(string rel)
    {
        rel = rel.Replace('\\', '/');
        int colon = rel.IndexOf(':');
        if (colon >= 0) rel = rel[(colon + 1)..];
        var p = Host("savedata0:" + rel.TrimStart('/')) ?? Path.Combine(Path.GetTempPath(), "vitasharp-save", rel);
        return p;
    }

    internal string SlotPath(uint slot) => SavePath($"SlotParam_{slot}.bin");

    public bool SlotExists(uint slot) => File.Exists(SlotPath(slot));

    /// <summary>Copies the stored SceAppUtilSaveDataSlotParam into guest memory.</summary>
    public void ReadSlotParam(uint slot, uint dst)
    {
        var p = SlotPath(slot);
        if (!File.Exists(p)) return;
        var data = File.ReadAllBytes(p);
        data.AsSpan(0, Math.Min(data.Length, SlotParamSize)).CopyTo(GuestMemory.Span(dst, SlotParamSize));
    }

    byte[]? LoadSlot(uint slot)
    {
        var p = SlotPath(slot);
        if (!File.Exists(p)) return null;
        var data = File.ReadAllBytes(p);
        if (data.Length < SlotParamSize) Array.Resize(ref data, SlotParamSize);
        return data;
    }

    const int ERR_BUSY = unchecked((int)0x80100603);
    bool _initialized;

    /// <summary>SDK version the main module was built with (SceProcessParam +0xC, as sceKernelGetMainModuleSdkVersion).</summary>
    uint MainModuleSdkVersion()
    {
        uint pp = Emu.Linker.ProcessParam;
        if (pp == 0 || GuestMemory.R32(pp + 4) != 0x32505350 /* "PSP2" */ || GuestMemory.R32(pp + 8) == 0) return 0;
        return GuestMemory.R32(pp + 0xC);
    }

    /// <summary>
    /// FW 3.60 libSceAppUtil: a second Init is BUSY for titles built with SDK 1.50 or later; both pointers are
    /// required and every reserved byte must be zero (initParam->workBufSize included: SceAppUtilInitParam +0 and
    /// +4..0x3F, SceAppUtilBootParam +8..0x27), else PARAMETER. attr and appVersion are copied from the AppMgr boot
    /// param words that FW 3.60 always returns zeroed, so both are 0 on hardware too.
    /// </summary>
    [Hle]
    int sceAppUtilInit(uint initParam, uint bootParam)
    {
        if (_initialized && MainModuleSdkVersion() >= 0x01500000) return ERR_BUSY;
        if (initParam == 0 || bootParam == 0 || GuestMemory.R32(initParam) != 0) return ERR_PARAMETER;
        if (GuestMemory.ReadSpan(initParam + 4, 0x3C).IndexOfAnyExcept((byte)0) >= 0) return ERR_PARAMETER;
        if (GuestMemory.ReadSpan(bootParam + 8, 0x20).IndexOfAnyExcept((byte)0) >= 0) return ERR_PARAMETER;
        GuestMemory.W32(bootParam, 0);     // attr
        GuestMemory.W32(bootParam + 4, 0); // appVersion
        Directory.CreateDirectory(SavePath(""));
        _initialized = true;
        return 0;
    }

    [Hle]
    int sceAppUtilShutdown()
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        _initialized = false;
        return 0;
    }

    [Hle]
    internal int sceAppUtilSystemParamGetInt(uint id, Ptr<int> value)
    {
        if (value.Address == 0) return ERR_PARAMETER;
        value.Value = ProcessSystemParamInt(id, Emu);
        return id is >= 1 and <= 7 ? 0 : ERR_PARAMETER;
    }

    /// <summary>Integer system parameters (SCE_SYSTEM_PARAM_ID_*), shared with SceRegMgr so both agree: what the
    /// system software wrote to the system registry (the initial setup, Settings) when <paramref name="reg"/> has
    /// it, else the default a fresh system starts with (the host's language and time zone).</summary>
    internal static int SystemParamInt(uint id, Firmware.Registry? reg = null)
    {
        if (reg != null && SystemParamKey(id) is { } path && reg.TryGetWrittenInt(path, out int v))
            return id == 6 ? Firmware.SystemTimeZone.OffsetMinutes(v) ?? DefaultSystemParamInt(6) : v;
        return DefaultSystemParamInt(id);
    }

    /// <summary>An integer system parameter as the process of <paramref name="emu"/> reads it: the system's
    /// (<see cref="SystemParamInt"/>), except the language of an application started with the emulator's per-game
    /// system language (<see cref="LanguageOverride"/>).</summary>
    internal static int ProcessSystemParamInt(uint id, Emulator? emu)
    {
        if (id == 1 && LanguageOverride(emu) is int language)
        {
            return language;
        }

        return SystemParamInt(id, SystemRegistry(emu));
    }

    /// <summary>The per-game system language (<see cref="EmulatorOptions.SystemLanguage"/>) the process of
    /// <paramref name="emu"/> reads instead of /CONFIG/SYSTEM/language, or null when it reads the system's: no option,
    /// an invalid one, or the system software (a program from vs0:), which always runs in the system's language.</summary>
    internal static int? LanguageOverride(Emulator? emu)
    {
        if (emu == null || emu.IsSystemProgram || emu.Options.SystemLanguage is not int language
            || !Firmware.SystemLanguage.IsValid(language))
        {
            return null;
        }

        return language;
    }

    /// <summary>The system registry of the emulator's file-system root (null without an emulator: unit tests).</summary>
    internal static Firmware.Registry? SystemRegistry(Emulator? emu) => emu == null ? null : Firmware.Registry.For(emu.Options.FsRoot);

    /// <summary>The system registry key behind an integer system parameter.</summary>
    internal static string? SystemParamKey(uint id) => id switch
    {
        1 => "/CONFIG/SYSTEM/language",
        2 => "/CONFIG/SYSTEM/button_assign", // 0 circle, 1 cross (SceSystemParamEnterButtonAssign)
        4 => "/CONFIG/DATE/date_format",
        5 => "/CONFIG/DATE/time_format",
        6 => "/CONFIG/DATE/time_zone", // a firmware time zone id
        7 => "/CONFIG/DATE/summer_time",
        _ => null,
    };

    static int DefaultSystemParamInt(uint id) => id switch
    {
        1 => Firmware.SystemLanguage.Host, // language: the host's UI language, else English (US)
        2 => 1,  // enter button: cross
        4 => 0,  // date format YYYYMMDD
        5 => 0,  // 12 hour clock
        6 => (int)TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes,
        7 => TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now) ? 1 : 0,
        _ => 0,
    };

    /// <summary>SCE_SYSTEM_PARAM_ID_USERNAME (3).</summary>
    internal const string UserName = "VitaSharp";

    [Hle]
    internal int sceAppUtilSystemParamGetString(uint id, uint buf, uint size)
    {
        if (buf == 0 || size == 0) return ERR_PARAMETER;
        GuestMemory.WriteCString(buf, id == 3 ? UserName : "", (int)size);
        return 0;
    }

    /// <summary>Only SCE_APPUTIL_APPPARAM_ID_SKU_FLAG (0) exists: the license SKU, read like Vita3K from the .rif
    /// (big-endian int at 0xFC) or, without a license, 1 when the app has retail LiveArea data.</summary>
    [Hle]
    int sceAppUtilAppParamGetInt(uint id, Ptr<int> value)
    {
        if (id != 0) return ERR_PARAMETER;
        if (value.Address == 0) return ERR_NOT_INITIALIZED; // Vita3K
        value.Value = SkuFlag();
        return 0;
    }

    int SkuFlag()
    {
        try
        {
            string? rif = Io.FirmwareLayout.ApplicationLicenseFiles(Emu.Options.FsRoot, Emu.TitleId).FirstOrDefault();
            if (rif != null)
            {
                var data = File.ReadAllBytes(rif);
                if (data.Length >= 0x100) return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0xFC));
            }
            return Directory.Exists(VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(Emu.AppPath, "sce_sys/retail/livearea")) ? 1 : 0;
        }
        catch (IOException) { return 0; }
    }

    // The process's safe memory, read from its file once (the file is this process's alone while it runs).
    byte[]? _safeMem;
    string? _safeMemPath;
    readonly object _safeMemLock = new();

    /// <summary>The file the kernel bound the process's safe memory to with sceAppMgrInitSafeMemoryById (a PSM
    /// title's pm.dat); null: the application's own, savedata0:sce_sys/safemem.dat.</summary>
    [SnapshotMissing] // states of builds before it: the safe memory was always the application's own
    string? _safeMemoryFile;

    byte[] SafeMemory(out string path)
    {
        path = _safeMemoryFile ?? SavePath("sce_sys/safemem.dat");
        if (_safeMem != null && _safeMemPath == path)
        {
            return _safeMem;
        }

        var data = new byte[SafeMemorySize];
        if (File.Exists(path))
        {
            File.ReadAllBytes(path).AsSpan(0, Math.Min(SafeMemorySize, (int)new FileInfo(path).Length)).CopyTo(data);
        }

        _safeMemPath = path;
        return _safeMem = data;
    }

    /// <summary>
    /// The kernel's binding of the process's safe memory to <paramref name="file"/> (FW 3.74 appmgr, what
    /// sceAppMgrInitSafeMemoryById does after its checks): a file of the area's size is read into the area; otherwise
    /// (missing, another size) the file is created from the area as it is. A short read or write is 0x80800014. Later
    /// saves and loads use that file.
    /// </summary>
    internal int BindSafeMemoryFile(string file)
    {
        lock (_safeMemLock)
        {
            byte[] area = SafeMemory(out _);
            try
            {
                if (File.Exists(file) && new FileInfo(file).Length == SafeMemorySize)
                {
                    using FileStream stream = File.OpenRead(file);
                    stream.ReadExactly(area);
                }
                else
                {
                    File.WriteAllBytes(file, area);
                }
            }
            catch (IOException)
            {
                return ERR_SAFE_MEMORY_IO;
            }

            _safeMemoryFile = file;
            _safeMemPath = file;
            _safeMem = area;
        }

        return 0;
    }

    /// <summary>apputil.suprx 3.74: 0x80100601 before sceAppUtilInit; the kernel's 0x80801000 (outside the area) and
    /// 0x80801001 (NULL buffer) become SCE_APPUTIL_ERROR_PARAMETER.</summary>
    [Hle]
    internal int sceAppUtilLoadSafeMemory(uint buf, uint size, long offset)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        int r = LoadSafeMemoryKernel(buf, size, offset);
        return r == unchecked((int)0x80801000) || r == unchecked((int)0x80801001) ? ERR_PARAMETER : r;
    }

    /// <summary>The kernel side (SceAppMgr 0xFAF3DAAA, what sceAppMgrLoadSafeMemory returns): a copy out of the
    /// process's safe memory area, so an area never written reads as zeros and the result is the size copied either
    /// way; NULL buffer 0x80801001, outside the area 0x80801000.</summary>
    internal int LoadSafeMemoryKernel(uint buf, uint size, long offset)
    {
        if (buf == 0) return unchecked((int)0x80801001);
        if (offset < 0 || offset + size > SafeMemorySize) return unchecked((int)0x80801000);
        lock (_safeMemLock)
        {
            var data = SafeMemory(out var p);
            if (!File.Exists(p)) { GuestMemory.Fill(buf, 0, size); return (int)size; }
            data.AsSpan((int)offset, (int)size).CopyTo(GuestMemory.Span(buf, (int)size));
        }
        return (int)size;
    }

    /// <summary>apputil.suprx 3.74: 0x80100601 before sceAppUtilInit; the kernel's 0x80801000 (outside the area) and
    /// 0x80801001 (NULL buffer) become SCE_APPUTIL_ERROR_PARAMETER.</summary>
    [Hle]
    internal int sceAppUtilSaveSafeMemory(uint buf, uint size, long offset)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        int r = SaveSafeMemoryKernel(buf, size, offset);
        return r == unchecked((int)0x80801000) || r == unchecked((int)0x80801001) ? ERR_PARAMETER : r;
    }

    /// <summary>The kernel side (SceAppMgr 0xD366AA44, what sceAppMgrSaveSafeMemory returns): NULL buffer 0x80801001,
    /// outside the area 0x80801000, else the size written. Writes the file only when the content changes: Friends
    /// (NPXS10006) clears its 64 KiB 4 bytes at a time at its first start (16384 calls), and a file rewrite per call
    /// (~11 ms each) kept it on a black screen for minutes.</summary>
    internal int SaveSafeMemoryKernel(uint buf, uint size, long offset)
    {
        if (buf == 0)
        {
            return unchecked((int)0x80801001);
        }

        if (offset < 0 || offset + size > SafeMemorySize)
        {
            return unchecked((int)0x80801000);
        }

        lock (_safeMemLock)
        {
            var data = SafeMemory(out var p);
            var src = GuestMemory.Span(buf, (int)size);
            var dst = data.AsSpan((int)offset, (int)size);
            if (src.SequenceEqual(dst) && (File.Exists(p) || !src.ContainsAnyExcept((byte)0))) return (int)size;
            src.CopyTo(dst);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
        }
        return (int)size;
    }

    // SDK 1.x API: the application mounts its savedata explicitly; savedata0: is always mounted here
    [Hle]
    int sceAppUtilSaveDataMount(uint titleIdPtr, uint passcodePtr)
    {
        // mounts another title's save data (title id + passcode) for import; only a title with save data here succeeds
        string titleId = titleIdPtr != 0 ? GuestMemory.ReadCString(titleIdPtr, 12) : "";
        string dir = Io.FirmwareLayout.SaveData.Host(Emu.Options.FsRoot, titleId);
        bool exists = titleId.Length > 0 && Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        Log.Info($"sceAppUtilSaveDataMount({titleId}): {(exists ? "mounted" : "no save data")}");
        if (!exists) return ERR_SLOT_NOT_FOUND;
        Emu.Vfs.Mount("savedata1", dir);
        return 0;
    }

    /// <summary>Unmounts the other title's save data mounted by sceAppUtilSaveDataMount (savedata1:).</summary>
    [Hle] int sceAppUtilSaveDataUmount(uint mountPoint) => Emu.Vfs.Unmount("savedata1") ? 0 : ERR_NOT_MOUNTED;

    [Hle]
    internal int sceAppUtilSaveDataSlotCreate(uint slot, uint param, uint mountPoint)
    {
        var p = SlotPath(slot);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, param != 0 ? GuestMemory.Span(param, SlotParamSize).ToArray() : new byte[SlotParamSize]);
        return 0;
    }

    [Hle]
    internal int sceAppUtilSaveDataSlotGetParam(uint slot, uint param, uint mountPoint)
    {
        var p = SlotPath(slot);
        if (!File.Exists(p)) return ERR_SLOT_NOT_FOUND;
        if (param != 0)
        {
            var data = File.ReadAllBytes(p);
            GuestMemory.Fill(param, 0, SlotParamSize);
            data.AsSpan(0, Math.Min(data.Length, SlotParamSize)).CopyTo(GuestMemory.Span(param, SlotParamSize));
            GuestMemory.W32(param, 0);
        }
        return 0;
    }

    /// <summary>Updates the param of an existing slot only.</summary>
    [Hle]
    internal int sceAppUtilSaveDataSlotSetParam(uint slot, uint param, uint mountPoint)
    {
        if (param == 0) return ERR_PARAMETER;
        if (!SlotExists(slot)) return ERR_SLOT_NOT_FOUND;
        return sceAppUtilSaveDataSlotCreate(slot, param, mountPoint);
    }

    [Hle]
    internal int sceAppUtilSaveDataSlotDelete(uint slot, uint mountPoint)
    {
        var p = SlotPath(slot);
        if (File.Exists(p)) File.Delete(p);
        return 0;
    }

    /// <summary>SceAppUtilSaveDataFile: dataPath* (+0), buf* (+4), bufSize (+8), pad (+0xC), offset (s64, +0x10),
    /// mode (+0x18: FILE 0, FILE_TRUNCATE 1, DIRECTORY 2), reserved[36] (+0x1C).</summary>
    internal const uint SaveFileSize = 0x40;

    /// <summary>
    /// apputil.suprx 3.74 (0x81000BB2, list check 0x81001AEE/0x810016F8) checks everything before the kernel writes:
    /// NOT_INITIALIZED before sceAppUtilInit; PARAMETER for a slot id of 256 or more, a mount point other than
    /// savedata0:, a NULL requiredSizeKiB, a NULL list, 0 or more than 5 files, and per file a path breaking the rules
    /// of <see cref="SaveDataPathValid"/> (relative to the mount point: a "savedata0:" prefix is refused), components
    /// over 63 bytes, a mode above DIRECTORY, a directory with a buffer, size or offset, a file with a size but no
    /// buffer, and pad or reserved bytes set.
    /// </summary>
    [Hle]
    int sceAppUtilSaveDataDataSave(uint slot, uint files, uint fileNum, uint mountPoint, Ptr<uint> requiredKiB)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        if (!SlotArgumentValid(slot) || (mountPoint != 0 && GuestMemory.ReadCString(mountPoint, 16) != "savedata0:"))
        {
            return ERR_PARAMETER;
        }

        if (files == 0 || fileNum == 0 || fileNum > MaxRemoveItems || requiredKiB.IsNull)
        {
            return ERR_PARAMETER;
        }

        for (uint i = 0; i < fileNum; i++)
        {
            if (!SaveFileValid(files + i * SaveFileSize))
            {
                return ERR_PARAMETER;
            }
        }

        requiredKiB.TrySet(0);
        SaveFiles(files, fileNum);
        if (slot != 0)
        {
            UpdateSlotAfterSave(GuestMemory.R32(slot), GuestMemory.R32(slot + 4));
        }

        return 0;
    }

    static bool SaveFileValid(uint file)
    {
        if (!SaveDataPathValid(GuestMemory.R32(file), maxSeparators: 3, lastComponentSce: false, maxComponent: 63))
        {
            return false;
        }

        uint buffer = GuestMemory.R32(file + 4);
        uint size = GuestMemory.R32(file + 8);
        long offset = GuestMemory.Read<long>(file + 0x10);
        uint mode = GuestMemory.R32(file + 0x18);
        if (mode > 2)
        {
            return false;
        }

        if (mode == 2 ? buffer != 0 || size != 0 || offset != 0 : buffer == 0 && size != 0)
        {
            return false;
        }

        return GuestMemory.R32(file + 0xC) == 0 && !GuestMemory.Span(file + 0x1C, 36).ContainsAnyExcept((byte)0);
    }

    /// <summary>Stores a slot's param as given.</summary>
    void WriteSlotParam(uint slot, byte[] param)
    {
        string path = SlotPath(slot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, param);
    }

    /// <summary>Writes SceAppUtilSaveDataFile entries (0x40 bytes: dataPath*, buf*, bufSize, pad, offset (s64), mode).</summary>
    internal void SaveFiles(uint files, uint fileNum)
    {
        for (uint i = 0; i < fileNum; i++)
        {
            uint f = files + i * 64;
            string path = SavePath(GuestMemory.ReadCString(GuestMemory.R32(f)));
            uint buf = GuestMemory.R32(f + 4), size = GuestMemory.R32(f + 8);
            long offset = GuestMemory.Read<long>(f + 16);
            int mode = (int)GuestMemory.R32(f + 24);
            try
            {
                if (mode == 2) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write);
                if (buf != 0 && size > 0)
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    fs.Write(GuestMemory.Span(buf, (int)size));
                }
                if (mode == 1) fs.SetLength(offset + size);
            }
            catch (Exception e) { Log.Warn($"save data write {path} failed: {e.Message}"); }
        }
    }

    /// <summary>Stores the slot param (given, or the existing one) with the current modification time.</summary>
    internal void UpdateSlotAfterSave(uint id, uint param)
    {
        var p = SlotPath(id);
        byte[] data = param != 0 ? GuestMemory.Span(param, SlotParamSize).ToArray()
            : File.Exists(p) ? File.ReadAllBytes(p) : new byte[SlotParamSize];
        if (data.Length < SlotParamSize) Array.Resize(ref data, SlotParamSize);
        var now = DateTime.Now;
        int t = SlotTimeOffset;
        BitConverter.TryWriteBytes(data.AsSpan(t), (ushort)now.Year);
        BitConverter.TryWriteBytes(data.AsSpan(t + 2), (ushort)now.Month);
        BitConverter.TryWriteBytes(data.AsSpan(t + 4), (ushort)now.Day);
        BitConverter.TryWriteBytes(data.AsSpan(t + 6), (ushort)now.Hour);
        BitConverter.TryWriteBytes(data.AsSpan(t + 8), (ushort)now.Minute);
        BitConverter.TryWriteBytes(data.AsSpan(t + 10), (ushort)now.Second);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
    }

    /// <summary>SceAppUtilSaveDataRemoveItem: dataPath* (+0), mode (+4: DEFAULT 0, NO_SLOT 1), reserved[36] (+8).</summary>
    internal const uint RemoveItemSize = 0x2C;

    /// <summary>The longest remove list apputil and the kernel accept (FW 3.74: apputil refuses more, SceAppMgr stops
    /// at 5).</summary>
    const uint MaxRemoveItems = 5;

    /// <summary>What SceAppMgr answers for a remove item it refuses (FW 3.74 SceAppMgr 0x8102AF54).</summary>
    internal const int ERR_APPMGR_INVALID = unchecked((int)0x80800001);

    const int ERRNO_EBUSY = unchecked((int)0x80010010), ERRNO_EACCES = unchecked((int)0x8001000D);

    /// <summary>
    /// The path rules of a remove item, as both apputil (0x81001BEC) and SceAppMgr (0x8102AF54) check them in FW 3.74:
    /// shorter than 200 bytes, only letters, digits, '-', '_', '.', '/' and '\', no "..", no first component starting
    /// with "sce_" (the system's own files, sce_sys), mode DEFAULT or NO_SLOT and the reserved bytes zero. apputil also
    /// refuses more than 3 separators and a last component starting with "sce_". An empty path passes both: it names
    /// savedata0: itself.
    /// </summary>
    static bool RemoveItemValid(uint item, bool appUtilRules)
    {
        if (!SaveDataPathValid(GuestMemory.R32(item), appUtilRules ? 3 : int.MaxValue, lastComponentSce: appUtilRules, maxComponent: int.MaxValue))
        {
            return false;
        }

        if (GuestMemory.R32(item + 4) > 1)
        {
            return false;
        }

        return !GuestMemory.Span(item + 8, 36).ContainsAnyExcept((byte)0);
    }

    /// <summary>The path rules shared by the save and remove lists (see <see cref="RemoveItemValid"/>): at most
    /// <paramref name="maxSeparators"/> separators, an "sce_" last component refused when
    /// <paramref name="lastComponentSce"/> (apputil's remove check), components of at most <paramref name="maxComponent"/>
    /// bytes (apputil's save check 0x810016F8: 63).</summary>
    static bool SaveDataPathValid(uint pathAddress, int maxSeparators, bool lastComponentSce, int maxComponent)
    {
        if (pathAddress == 0)
        {
            return false;
        }

        string path = GuestMemory.ReadCString(pathAddress, 200);
        if (path.Length >= 200)
        {
            return false;
        }

        int separators = 0;
        int lastSeparator = -1;
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            bool separator = c == '/' || c == '\\';
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.' && !separator)
            {
                return false;
            }

            if (c == '.' && i + 1 < path.Length && path[i + 1] == '.')
            {
                return false;
            }

            if (separator)
            {
                if (i - lastSeparator - 1 > maxComponent)
                {
                    return false;
                }

                separators++;
                lastSeparator = i;
            }
        }

        if (path.Length - lastSeparator - 1 > maxComponent)
        {
            return false;
        }

        string firstComponent = path.TrimStart('/', '\\');
        if (firstComponent.StartsWith("sce_", StringComparison.OrdinalIgnoreCase))
        {
            return false; // a fixed name of the firmware's save data layout (sce_sys)
        }

        if (separators > maxSeparators)
        {
            return false;
        }

        return !lastComponentSce || !path.AsSpan(lastSeparator + 1).StartsWith("sce_", StringComparison.Ordinal);
    }

    /// <summary>
    /// The kernel side of a save data removal (SceAppMgr sceAppMgrSaveDataDataRemove, FW 3.74 0x81029E3C): up to 5
    /// items of <see cref="RemoveItemSize"/> bytes, each checked (<see cref="ERR_APPMGR_INVALID"/>, the items before it
    /// already removed) and removed with everything under it; a path that does not exist is fine. A host error (a file
    /// another program holds, no permission) becomes the I/O error instead of an exception that would stop the guest.
    /// Then the slot <paramref name="slotId"/> (-1: none), when it exists: removed when every item is in mode DEFAULT;
    /// kept when one is NO_SLOT, and then given <paramref name="slotParam"/> if there is one (stamped with the current
    /// time when <paramref name="stampTime"/>, which apputil does before the call).
    /// </summary>
    internal int RemoveItems(uint files, uint fileNum, int slotId, uint slotParam, bool stampTime)
    {
        bool slotExists = slotId >= 0 && SlotExists((uint)slotId);
        bool keepSlot = false;
        for (uint i = 0; i < fileNum && i < MaxRemoveItems; i++)
        {
            uint item = files + i * RemoveItemSize;
            if (!RemoveItemValid(item, appUtilRules: false))
            {
                return ERR_APPMGR_INVALID;
            }

            if (GuestMemory.R32(item + 4) == 1)
            {
                keepSlot = true;
            }

            string rel = GuestMemory.ReadCString(GuestMemory.R32(item), 200);
            string host = SavePath(rel);
            int r = RemoveTree(host, IsSameDirectory(host, SavePath("")));
            if (r < 0)
            {
                return r;
            }
        }

        if (!slotExists)
        {
            return 0;
        }

        if (!keepSlot)
        {
            return RemoveTree(SlotPath((uint)slotId), isRoot: false);
        }

        if (slotParam != 0)
        {
            if (stampTime)
            {
                UpdateSlotAfterSave((uint)slotId, slotParam);
            }
            else
            {
                WriteSlotParam((uint)slotId, GuestMemory.Span(slotParam, SlotParamSize).ToArray());
            }
        }

        return 0;
    }

    static bool IsSameDirectory(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Removes a file, or a directory with everything under it. The save data root itself (an empty path)
    /// stays, emptied, and keeps the system's own data: sce_sys and the slot params this emulator keeps there
    /// (SlotParam_*.bin, a fixed name of our own layout).</summary>
    static int RemoveTree(string host, bool isRoot)
    {
        try
        {
            if (File.Exists(host))
            {
                File.Delete(host);
                return 0;
            }

            if (!Directory.Exists(host))
            {
                return 0;
            }

            if (!isRoot)
            {
                Directory.Delete(host, true);
                return 0;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(host))
            {
                string name = Path.GetFileName(entry);
                if (name.StartsWith("sce_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SlotParam_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, true);
                }
                else
                {
                    File.Delete(entry);
                }
            }

            return 0;
        }
        catch (UnauthorizedAccessException e)
        {
            Log.Warn($"save data remove {host} failed: {e.Message}");
            return ERRNO_EACCES;
        }
        catch (IOException e)
        {
            Log.Warn($"save data remove {host} failed: {e.Message}");
            return ERRNO_EBUSY;
        }
    }

    /// <summary>
    /// apputil.suprx 3.74 (0x81000D2C): NOT_INITIALIZED before sceAppUtilInit; PARAMETER for a NULL list, 0 or more than
    /// 5 items, an item breaking the path rules (<see cref="RemoveItemValid"/>), a mount point other than savedata0:,
    /// or a slot with a new param while item 0 also removes the slot (mode DEFAULT). Then the kernel removes the items
    /// and handles the slot (<see cref="RemoveItems"/>).
    /// </summary>
    [Hle]
    int sceAppUtilSaveDataDataRemove(uint slot, uint files, uint fileNum, uint mountPoint)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        if (files == 0 || fileNum == 0 || fileNum > MaxRemoveItems)
        {
            return ERR_PARAMETER;
        }

        for (uint i = 0; i < fileNum; i++)
        {
            if (!RemoveItemValid(files + i * RemoveItemSize, appUtilRules: true))
            {
                return ERR_PARAMETER;
            }
        }

        if (mountPoint != 0 && GuestMemory.ReadCString(mountPoint, 16) != "savedata0:")
        {
            return ERR_PARAMETER;
        }

        if (slot != 0 && GuestMemory.R32(slot + 4) != 0 && GuestMemory.R32(files + 4) == 0)
        {
            return ERR_PARAMETER;
        }

        if (!SlotArgumentValid(slot))
        {
            return ERR_PARAMETER;
        }

        return slot == 0 ? RemoveItems(files, fileNum, -1, 0, stampTime: true)
            : RemoveItems(files, fileNum, (int)GuestMemory.R32(slot), GuestMemory.R32(slot + 4), stampTime: true);
    }

    /// <summary>apputil 3.74 (0x810016D8): a slot argument is NULL or names a slot 0..255 (SCE_APPUTIL_SAVEDATA_SLOT_MAX).</summary>
    static bool SlotArgumentValid(uint slot) => slot == 0 || GuestMemory.R32(slot) < 256;

    /// <summary>Quota = SAVEDATA_MAX_SIZE of param.sfo (KiB); used = savedata0: size in KiB, clamped to the quota.</summary>
    [Hle]
    internal int sceAppUtilSaveDataGetQuota(Ptr<uint> quota, Ptr<uint> used, uint mountPoint)
    {
        if (quota.IsNull && used.IsNull) return ERR_PARAMETER;
        uint q = SaveDataQuotaKiB();
        quota.TrySet(q);
        long bytes = 0;
        var root = SavePath("");
        if (Directory.Exists(root)) bytes = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        uint u = (uint)(bytes / 1024);
        if (q > 0 && u > q) u = q;
        used.TrySet(u);
        return 0;
    }

    uint SaveDataQuotaKiB()
    {
        var sfo = Emu.ParamSfo;
        if (sfo != null && sfo.Values.TryGetValue("SAVEDATA_MAX_SIZE", out var v))
        {
            if (v is uint u) return u;
            if (v is string s && uint.TryParse(s, out var p)) return p;
        }
        return 1024; // no SFO value: 1 MiB
    }

    // ------------------------------------------------------------------ slot search

    /// <summary>SceAppUtilSaveDataSlotSearchCond (0x34): type (EXIST 0 / EMPTY 1), from, range, key (SLOT_ID 0,
    /// USER_PARAM 1, SIZE_KIB 2, MODIFIED_TIME 3), order (ASCENT 0 / DESCENT 1). Result (0x28): hitNum, slotList*.
    /// SceAppUtilSaveDataSlot (0x10): id, status, userParam, emptyParam*. The list goes to workBuf->buf (compact, sorted).</summary>
    [Hle]
    int sceAppUtilSaveDataSlotSearch(uint workBuf, uint cond, uint result, uint mountPoint)
    {
        if (cond == 0 || result == 0) return ERR_PARAMETER;
        uint type = GuestMemory.R32(cond), from = GuestMemory.R32(cond + 4), range = GuestMemory.R32(cond + 8);
        uint key = GuestMemory.R32(cond + 12), order = GuestMemory.R32(cond + 16);
        var hits = new List<(uint Id, uint Status, uint UserParam, uint Size, long Time)>();
        for (uint id = from; id < from + range && id < 256; id++)
        {
            var data = LoadSlot(id);
            if (type == 0 && data != null)
            {
                var d = data.AsSpan();
                long time = 0;
                for (int k = 0; k < 7; k++) time = time * 100_000 + BitConverter.ToUInt16(d[(SlotTimeOffset + k * 2)..]);
                hits.Add((id, BitConverter.ToUInt32(d), BitConverter.ToUInt32(d[SlotUserParamOffset..]), BitConverter.ToUInt32(d[SlotSizeOffset..]), time));
            }
            else if (type == 1 && data == null) hits.Add((id, 0, 0, 0, 0));
        }
        Comparison<(uint Id, uint Status, uint UserParam, uint Size, long Time)> cmp = key switch
        {
            1 => (a, b) => a.UserParam.CompareTo(b.UserParam),
            2 => (a, b) => a.Size.CompareTo(b.Size),
            3 => (a, b) => a.Time.CompareTo(b.Time),
            _ => (a, b) => a.Id.CompareTo(b.Id),
        };
        hits.Sort((a, b) => { int c = cmp(a, b); if (c == 0) c = a.Id.CompareTo(b.Id); return order == 1 ? -c : c; });
        uint list = workBuf != 0 ? GuestMemory.R32(workBuf) : GuestMemory.R32(result + 4);
        if (list != 0)
        {
            int cap = workBuf != 0 ? (int)(GuestMemory.R32(workBuf + 4) / 16) : hits.Count;
            for (int i = 0; i < Math.Min(cap, hits.Count); i++)
            {
                uint e = list + (uint)i * 16;
                GuestMemory.W32(e, hits[i].Id);
                GuestMemory.W32(e + 4, hits[i].Status);
                GuestMemory.W32(e + 8, hits[i].UserParam);
                GuestMemory.W32(e + 12, 0);
            }
        }
        GuestMemory.W32(result, (uint)hits.Count);
        GuestMemory.W32(result + 4, list);
        return 0;
    }

    // ------------------------------------------------------------------ app events (none: the app is never launched by one)

    [Hle] int sceAppUtilReceiveAppEvent(uint eventParam) { if (eventParam != 0) GuestMemory.Fill(eventParam, 0, 4); return 0; }

    /// <summary>Every sceAppUtilAppEventParse*: PARAMETER for NULL, otherwise the event is never of the right type.</summary>
    static int ParseEvent(uint e) => e == 0 ? ERR_PARAMETER : ERR_APPEVENT_PARSE_INVALID_DATA;

    [Hle] int sceAppUtilAppEventParseLiveArea(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseNearGift(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseNpBasicJoinablePresence(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseNpInviteMessage(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseNpAppDataMessage(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseGameCustomData(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseIncomingDialog(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseNpActivity(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseScreenShotNotification(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseSessionInvitation(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseTeleport(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseTriggerUtil(uint e, uint buf) => ParseEvent(e);
    [Hle] int sceAppUtilAppEventParseWebBrowser(uint e, uint buf) => ParseEvent(e);

    // ------------------------------------------------------------------ photo / music / store / browser

    bool _photoMounted, _musicMounted;

    /// <summary>photo0: is the user's gallery (ux0:picture), as Vita3K maps it.</summary>
    internal int MountPhoto()
    {
        Emu.Vfs.Mount("photo0", Io.FirmwareLayout.Picture.Host(Emu.Options.FsRoot));
        _photoMounted = true;
        return 0;
    }

    internal int UmountPhoto()
    {
        if (!_photoMounted) return ERR_NOT_MOUNTED;
        _photoMounted = false;
        return 0;
    }

    // ------------------------------------------------------------------ cache0: (apputil.suprx FW 3.74)

    bool _cacheMounted;

    /// <summary>cache0: is the title's work directory ux0:cache/&lt;TITLEID&gt; (sceAppMgrWorkDirMountById 205 with the
    /// application's title id). Before sceAppUtilInit 0x80100601.</summary>
    [Hle]
    int sceAppUtilCacheMount()
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        string dir = Io.FirmwareLayout.ApplicationCache.Host(Emu.Options.FsRoot, Emu.TitleId);
        Directory.CreateDirectory(dir);
        Emu.Vfs.Mount("cache0", dir);
        _cacheMounted = true;
        return 0;
    }

    /// <summary>sceAppMgrUmount("cache0:"); not mounted 0x80100604.</summary>
    [Hle]
    int sceAppUtilCacheUmount()
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (!_cacheMounted) return ERR_NOT_MOUNTED;
        Emu.Vfs.Unmount("cache0");
        _cacheMounted = false;
        return 0;
    }

    /// <summary>(SceSize *maxSize, SceSize *freeSize): sceAppMgrGetDevInfo("cache0:"); not mounted 0x80100604.</summary>
    [Hle]
    int sceAppUtilCacheGetDevInfo(Ptr<ulong> maxSize, Ptr<ulong> freeSize)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (!_cacheMounted || Emu.Vfs.Resolve("cache0:") is not { } host) return ERR_NOT_MOUNTED;
        (ulong total, ulong free) = DeviceCapacity.Of(host);
        maxSize.TrySet(total);
        freeSize.TrySet(free);
        return 0;
    }

    [Hle] int sceAppUtilPhotoMount() => MountPhoto();
    [Hle] int sceAppUtilPhotoUmount() => UmountPhoto();

    [Hle]
    int sceAppUtilMusicMount()
    {
        Emu.Vfs.Mount("music0", Io.FirmwareLayout.Music.Host(Emu.Options.FsRoot));
        _musicMounted = true;
        return 0;
    }

    [Hle]
    int sceAppUtilMusicUmount()
    {
        if (!_musicMounted) return ERR_NOT_MOUNTED;
        _musicMounted = false;
        return 0;
    }

    /// <summary>SceAppUtilStoreBrowseParam (8): type, const char *id. There is no PS Store: the user "opens and closes"
    /// it without buying anything.</summary>
    [Hle]
    int sceAppUtilStoreBrowse(uint param)
    {
        if (param == 0 || GuestMemory.R32(param + 4) == 0) return ERR_PARAMETER;
        Log.Info($"sceAppUtilStoreBrowse type {GuestMemory.R32(param)} id {GuestMemory.ReadCString(GuestMemory.R32(param + 4), 256)}");
        return 0;
    }

    [Hle] int sceAppUtilLaunchWebBrowser(uint param) => 0;
    [Hle] int sceAppUtilAddCookieWebBrowser(uint param) => param == 0 ? ERR_PARAMETER : 0; // no browser: cookies are dropped
    [Hle] int sceAppUtilResetCookieWebBrowser() => 0;

    /// <summary>SceAppUtilBgdlStatus (0x2C): no background download pending.</summary>
    [Hle]
    int sceAppUtilBgdlGetStatus(uint stat)
    {
        if (stat == 0) return ERR_PARAMETER;
        GuestMemory.Fill(stat, 0, 0x2C);
        return 0;
    }

    // ------------------------------------------------------------------ PSP save data import (no PSP saves here)

    [Hle]
    int sceAppUtilPspSaveDataGetDirNameList(uint cond, uint dirNames, Ptr<uint> num)
    {
        if (num.IsNull) return ERR_PARAMETER;
        num.Value = 0;
        return 0;
    }

    [Hle] int sceAppUtilPspSaveDataLoad(uint dirName, uint buf, uint size) => ERR_SLOT_NOT_FOUND;

    // ------------------------------------------------------------------ DLC

    bool AddcontExists(uint dirName)
    {
        var host = Host("addcont0:" + GuestMemory.ReadCString(dirName, 17));
        return host != null && Directory.Exists(host) && Directory.EnumerateFileSystemEntries(host).Any();
    }

    [Hle]
    int sceAppUtilDrmOpen(uint dirName, uint mountPoint)
    {
        if (dirName == 0) return ERR_PARAMETER;
        return Directory.Exists(Host("addcont0:" + GuestMemory.ReadCString(dirName)) ?? "") ? 0 : unchecked((int)0x80100A00);
    }

    [Hle] int sceAppUtilDrmClose(uint dirName, uint mountPoint) => 0;

    uint _addcontMounted;

    /// <summary>SDK 3.x form of DrmOpen: mounts one DLC (SceAppUtilDrmAddcontId, 20 bytes: data[17]).</summary>
    [Hle]
    int sceAppUtilAddcontMount(uint dataType, uint addcontId)
    {
        if (addcontId == 0) return ERR_PARAMETER;
        if (!AddcontExists(addcontId)) return ERRNO_ENOENT; // like Vita3K's DrmOpen
        _addcontMounted = addcontId;
        return 0;
    }

    [Hle]
    int sceAppUtilAddcontUmount()
    {
        if (_addcontMounted == 0) return ERR_NOT_MOUNTED;
        _addcontMounted = 0;
        return 0;
    }
}
