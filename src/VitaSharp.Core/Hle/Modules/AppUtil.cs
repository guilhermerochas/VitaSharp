using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceAppUtil: system parameters, safe memory and save data slots (files under savedata0:).</summary>
public sealed class AppUtil : HleModule
{
    internal const int ERR_PARAMETER = unchecked((int)0x80100600), ERR_NOT_INITIALIZED = unchecked((int)0x80100601),
        ERR_NOT_MOUNTED = unchecked((int)0x80100604), ERR_APPEVENT_PARSE_INVALID_DATA = unchecked((int)0x80100620),
        ERR_SLOT_NOT_FOUND = unchecked((int)0x80100641);
    const int ERRNO_ENOENT = unchecked((int)0x80010002);
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
        value.Value = SystemParamInt(id);
        return id is >= 1 and <= 7 ? 0 : ERR_PARAMETER;
    }

    /// <summary>Integer system parameters (SCE_SYSTEM_PARAM_ID_*), shared with SceRegMgr so both agree.</summary>
    internal static int SystemParamInt(uint id) => id switch
    {
        1 => 1,  // language: English (US)
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
            string dir = Path.Combine(Emu.Options.FsRoot, "ux0", "license", Emu.TitleId);
            var rif = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.rif").FirstOrDefault() : null;
            if (rif != null)
            {
                var data = File.ReadAllBytes(rif);
                if (data.Length >= 0x100) return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0xFC));
            }
            return Directory.Exists(VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(Emu.AppPath, "sce_sys/retail/livearea")) ? 1 : 0;
        }
        catch (IOException) { return 0; }
    }

    byte[] LoadSafeMemory()
    {
        var p = SavePath("sce_sys/safemem.dat");
        var data = new byte[SafeMemorySize];
        if (File.Exists(p)) File.ReadAllBytes(p).AsSpan(0, Math.Min(SafeMemorySize, (int)new FileInfo(p).Length)).CopyTo(data);
        return data;
    }

    [Hle]
    int sceAppUtilLoadSafeMemory(uint buf, uint size, long offset)
    {
        if (buf == 0 || offset + size > SafeMemorySize) return ERR_PARAMETER;
        var p = SavePath("sce_sys/safemem.dat");
        if (!File.Exists(p)) { GuestMemory.Fill(buf, 0, size); return 0; }
        LoadSafeMemory().AsSpan((int)offset, (int)size).CopyTo(GuestMemory.Span(buf, (int)size));
        return (int)size;
    }

    [Hle]
    int sceAppUtilSaveSafeMemory(uint buf, uint size, long offset)
    {
        if (buf == 0 || offset + size > SafeMemorySize) return ERR_PARAMETER;
        var data = LoadSafeMemory();
        GuestMemory.Span(buf, (int)size).CopyTo(data.AsSpan((int)offset));
        var p = SavePath("sce_sys/safemem.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return (int)size;
    }

    // SDK 1.x API: the application mounts its savedata explicitly; savedata0: is always mounted here
    [Hle]
    int sceAppUtilSaveDataMount(uint titleIdPtr, uint passcodePtr)
    {
        // mounts another title's save data (title id + passcode) for import; only a title with save data here succeeds
        string titleId = titleIdPtr != 0 ? GuestMemory.ReadCString(titleIdPtr, 12) : "";
        string dir = Path.Combine(Emu.Options.FsRoot, "ux0", "user", "00", "savedata", titleId);
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

    [Hle]
    int sceAppUtilSaveDataDataSave(uint slot, uint files, uint fileNum, uint mountPoint, Ptr<uint> requiredKiB)
    {
        requiredKiB.TrySet(0);
        SaveFiles(files, fileNum);
        if (slot != 0) UpdateSlotAfterSave(GuestMemory.R32(slot), GuestMemory.R32(slot + 4));
        return 0;
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

    /// <summary>Deletes the files/directories of a remove list whose items are <paramref name="stride"/> bytes apart
    /// (dataPath* first).</summary>
    internal void RemoveFiles(uint files, uint fileNum, uint stride)
    {
        for (uint i = 0; i < fileNum; i++)
        {
            string path = SavePath(GuestMemory.ReadCString(GuestMemory.R32(files + i * stride)));
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Hle]
    int sceAppUtilSaveDataDataRemove(uint slot, uint files, uint fileNum, uint mountPoint)
    {
        if (slot != 0 && GuestMemory.R32(slot) >= 256) return ERR_PARAMETER; // SCE_APPUTIL_SAVEDATA_SLOT_MAX
        RemoveFiles(files, fileNum, 0x2C); // SceAppUtilSaveDataRemoveItem: dataPath*, mode, reserved[36]
        if (slot != 0 && fileNum > 0 && GuestMemory.R32(files + 4) == 0)
        {
            var p = SlotPath(GuestMemory.R32(slot));
            if (File.Exists(p)) File.Delete(p);
        }
        return 0;
    }

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
        Emu.Vfs.Mount("photo0", Path.Combine(Emu.Options.FsRoot, "ux0", "picture"));
        _photoMounted = true;
        return 0;
    }

    internal int UmountPhoto()
    {
        if (!_photoMounted) return ERR_NOT_MOUNTED;
        _photoMounted = false;
        return 0;
    }

    [Hle] int sceAppUtilPhotoMount() => MountPhoto();
    [Hle] int sceAppUtilPhotoUmount() => UmountPhoto();

    [Hle]
    int sceAppUtilMusicMount()
    {
        Emu.Vfs.Mount("music0", Path.Combine(Emu.Options.FsRoot, "ux0", "music"));
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
