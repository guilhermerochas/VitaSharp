using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceAppUtil: system parameters, safe memory and save data slots (files under savedata0:).</summary>
public sealed class AppUtil : HleModule
{
    const int ERR_PARAMETER = unchecked((int)0x80100600), ERR_SLOT_NOT_FOUND = unchecked((int)0x80100641);
    const int SafeMemorySize = 64 * 1024;
    const int SlotParamSize = 4 + 64 + 128 + 512 + 64 + 4 + 4 + 16 + 48;

    string? Host(string vitaPath) => Emu.Vfs.Resolve(vitaPath);

    string SavePath(string rel)
    {
        rel = rel.Replace('\\', '/');
        int colon = rel.IndexOf(':');
        if (colon >= 0) rel = rel[(colon + 1)..];
        var p = Host("savedata0:" + rel.TrimStart('/')) ?? Path.Combine(Path.GetTempPath(), "vitasharp-save", rel);
        return p;
    }

    string SlotPath(uint slot) => SavePath($"SlotParam_{slot}.bin");

    public bool SlotExists(uint slot) => File.Exists(SlotPath(slot));

    /// <summary>Copies the stored SceAppUtilSaveDataSlotParam into guest memory.</summary>
    public void ReadSlotParam(uint slot, uint dst)
    {
        var p = SlotPath(slot);
        if (!File.Exists(p)) return;
        var data = File.ReadAllBytes(p);
        data.AsSpan(0, Math.Min(data.Length, SlotParamSize)).CopyTo(GuestMemory.Span(dst, SlotParamSize));
    }

    [Hle]
    int sceAppUtilInit(uint initParam, uint bootParam)
    {
        if (bootParam != 0) GuestMemory.Fill(bootParam, 0, 40); // SceAppUtilBootParam: attr, appVersion, reserved[32]
        Directory.CreateDirectory(SavePath(""));
        return 0;
    }

    [Hle] int sceAppUtilShutdown() => 0;

    [Hle]
    int sceAppUtilSystemParamGetInt(uint id, Ptr<int> value)
    {
        if (value.Address == 0) return ERR_PARAMETER;
        value.Value = id switch
        {
            1 => 1,  // language: English (US)
            2 => 1,  // enter button: cross
            4 => 0,  // date format YYYYMMDD
            5 => 0,  // 12 hour clock
            6 => (int)TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes,
            7 => TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now) ? 1 : 0,
            _ => 0,
        };
        return id is >= 1 and <= 7 ? 0 : ERR_PARAMETER;
    }

    [Hle]
    int sceAppUtilSystemParamGetString(uint id, uint buf, uint size)
    {
        if (buf == 0 || size == 0) return ERR_PARAMETER;
        GuestMemory.WriteCString(buf, id == 3 ? "VitaSharp" : "", (int)size);
        return 0;
    }

    [Hle]
    int sceAppUtilAppParamGetInt(uint id, Ptr<int> value)
    {
        if (value.Address == 0) return ERR_PARAMETER;
        value.Value = 0;
        return 0;
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
    [Hle] int sceAppUtilSaveDataUmount(uint mountPoint) => 0;

    [Hle]
    int sceAppUtilSaveDataSlotCreate(uint slot, uint param, uint mountPoint)
    {
        var p = SlotPath(slot);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, param != 0 ? GuestMemory.Span(param, SlotParamSize).ToArray() : new byte[SlotParamSize]);
        return 0;
    }

    [Hle]
    int sceAppUtilSaveDataSlotGetParam(uint slot, uint param, uint mountPoint)
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

    [Hle] int sceAppUtilSaveDataSlotSetParam(uint slot, uint param, uint mountPoint) => sceAppUtilSaveDataSlotCreate(slot, param, mountPoint);

    [Hle]
    int sceAppUtilSaveDataSlotDelete(uint slot, uint mountPoint)
    {
        var p = SlotPath(slot);
        if (File.Exists(p)) File.Delete(p);
        return 0;
    }

    [Hle]
    int sceAppUtilSaveDataDataSave(uint slot, uint files, uint fileNum, uint mountPoint, Ptr<uint> requiredKiB)
    {
        requiredKiB.TrySet(0);
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
        if (slot != 0)
        {
            uint id = GuestMemory.R32(slot), param = GuestMemory.R32(slot + 4);
            var p = SlotPath(id);
            byte[] data = param != 0 ? GuestMemory.Span(param, SlotParamSize).ToArray()
                : File.Exists(p) ? File.ReadAllBytes(p) : new byte[SlotParamSize];
            if (data.Length < SlotParamSize) Array.Resize(ref data, SlotParamSize);
            var now = DateTime.Now;
            int t = 4 + 64 + 128 + 512 + 64 + 8;
            BitConverter.TryWriteBytes(data.AsSpan(t), (ushort)now.Year);
            BitConverter.TryWriteBytes(data.AsSpan(t + 2), (ushort)now.Month);
            BitConverter.TryWriteBytes(data.AsSpan(t + 4), (ushort)now.Day);
            BitConverter.TryWriteBytes(data.AsSpan(t + 6), (ushort)now.Hour);
            BitConverter.TryWriteBytes(data.AsSpan(t + 8), (ushort)now.Minute);
            BitConverter.TryWriteBytes(data.AsSpan(t + 10), (ushort)now.Second);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
        }
        return 0;
    }

    [Hle]
    int sceAppUtilSaveDataDataRemove(uint slot, uint files, uint fileNum, uint mountPoint)
    {
        if (slot != 0 && GuestMemory.R32(slot) >= 256) return ERR_PARAMETER; // SCE_APPUTIL_SAVEDATA_SLOT_MAX
        for (uint i = 0; i < fileNum; i++)
        {
            string path = SavePath(GuestMemory.ReadCString(GuestMemory.R32(files + i * 0x2C)));
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        if (slot != 0 && fileNum > 0 && GuestMemory.R32(files + 4) == 0)
        {
            var p = SlotPath(GuestMemory.R32(slot));
            if (File.Exists(p)) File.Delete(p);
        }
        return 0;
    }

    [Hle]
    int sceAppUtilSaveDataGetQuota(Ptr<uint> quota, Ptr<uint> used, uint mountPoint)
    {
        quota.TrySet(1024 * 1024);
        long bytes = 0;
        var root = SavePath("");
        if (Directory.Exists(root)) bytes = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        used.TrySet((uint)(bytes / 1024));
        return 0;
    }

    [Hle] int sceAppUtilReceiveAppEvent(uint eventParam) { if (eventParam != 0) GuestMemory.Fill(eventParam, 0, 4); return 0; }
    [Hle] int sceAppUtilAppEventParseLiveArea(uint e, uint buf) => ERR_PARAMETER;
    [Hle] int sceAppUtilAppEventParseNearGift(uint e, uint buf) => ERR_PARAMETER;
    [Hle] int sceAppUtilAppEventParseNpBasicJoinablePresence(uint e, uint buf) => ERR_PARAMETER;
    [Hle] int sceAppUtilAppEventParseNpInviteMessage(uint e, uint buf) => ERR_PARAMETER;
    [Hle] int sceAppUtilPhotoMount() => 0;
    [Hle] int sceAppUtilPhotoUmount() => 0;
    [Hle] int sceAppUtilMusicMount() => 0;
    [Hle] int sceAppUtilMusicUmount() => 0;
    [Hle] int sceAppUtilLaunchWebBrowser(uint param) => 0;

    [Hle]
    int sceAppUtilDrmOpen(uint dirName, uint mountPoint)
    {
        if (dirName == 0) return ERR_PARAMETER;
        return Directory.Exists(Host("addcont0:" + GuestMemory.ReadCString(dirName)) ?? "") ? 0 : unchecked((int)0x80100A00);
    }

    [Hle] int sceAppUtilDrmClose(uint dirName, uint mountPoint) => 0;
}
