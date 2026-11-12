using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceAppUtilBook (apputil.suprx 3.74): the e-book library's mount. sceAppUtilBookMount(char mountPoint[16]) needs
/// sceAppUtilInit (0x80100601), a buffer (0x80100600), and mounts application data 112 through SceAppMgr
/// (sceAppMgrAppDataMount, ux0:book); the mount point is kept and copied out, and a second mount answers 0x80100603
/// with the kept one. sceAppUtilBookUmount unmounts it (0x80100604 when nothing is mounted).
/// </summary>
public sealed partial class AppUtil
{
    const int ERR_ALREADY_MOUNTED = unchecked((int)0x80100603);
    const int BookAppDataId = 112;
    string? _bookMount;

    AppMgr? _appMgr;
    AppMgr Am => _appMgr ??= Emu.Hle.Modules.OfType<AppMgr>().First();

    [Hle("sceAppUtilBookMount")]
    int sceAppUtilBookMount(uint mountPoint)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (mountPoint == 0) return ERR_PARAMETER;
        if (_bookMount != null)
        {
            GuestMemory.Fill(mountPoint, 0, 16);
            GuestMemory.WriteCString(mountPoint, _bookMount, 16);
            return ERR_ALREADY_MOUNTED;
        }
        Directory.CreateDirectory(Path.Combine(Emu.Options.FsRoot, "ux0", "book"));
        int r = Am.sceAppMgrAppDataMount(BookAppDataId, mountPoint);
        if (r < 0) return r;
        _bookMount = GuestMemory.ReadCString(mountPoint, 16);
        return 0;
    }

    [Hle("sceAppUtilBookUmount")]
    int sceAppUtilBookUmount()
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (_bookMount == null) return ERR_NOT_MOUNTED;
        uint cell = Emu.Memory.Alloc(16, "apputil book umount");
        GuestMemory.Fill(cell, 0, 16);
        GuestMemory.WriteCString(cell, _bookMount, 16);
        int r = Am.sceAppMgrUmount(cell);
        Emu.Memory.Free(cell);
        if (r < 0) return ERR_NOT_MOUNTED;
        _bookMount = null;
        return 0;
    }
}

/// <summary>
/// SceAppUtilAddcontForce: sceAppUtilAddcontForceAddcontMount(const char titleId[16], const char addcontId[32])
/// mounts another title's additional content at addcont1: (apputil.suprx 3.74): NULL arguments 0x80100600; the title
/// ID must be four capitals and five digits with the rest of its 16 bytes zero, the content ID only letters, digits,
/// '-' and '_' (else 0x80100600); then SceAppMgr's add-content mount (ux0:addcont/&lt;title&gt;/&lt;content&gt;):
/// already mounted 0x80100603, no such content 0x80100606.
/// </summary>
public sealed partial class AppUtil
{
    const int ERR_ADDCONT_NOT_FOUND = unchecked((int)0x80100606);
    string? _forceAddcont;

    [Hle("sceAppUtilAddcontForceAddcontMount")]
    int sceAppUtilAddcontForceAddcontMount(uint titleId, uint addcontId)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (titleId == 0 || addcontId == 0) return ERR_PARAMETER;
        var t = GuestMemory.ReadSpan(titleId, 16);
        for (int i = 0; i < 4; i++) if (t[i] is < (byte)'A' or > (byte)'Z') return ERR_PARAMETER;
        for (int i = 4; i < 9; i++) if (t[i] is < (byte)'0' or > (byte)'9') return ERR_PARAMETER;
        if (t[9] != 0) return ERR_PARAMETER;
        for (int i = 10; i < 16; i++) if (t[i] != 0) return ERR_PARAMETER;
        var a = GuestMemory.ReadSpan(addcontId, 32);
        int len = a.IndexOf((byte)0);
        if (len < 0) len = 32;
        foreach (byte b in a[..len])
            if (!(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_')) return ERR_PARAMETER;
        if (_forceAddcont != null) return ERR_ALREADY_MOUNTED;
        string title = System.Text.Encoding.ASCII.GetString(t[..9]), content = System.Text.Encoding.ASCII.GetString(a[..len]);
        string dir = Io.FirmwareLayout.AddCont.Host(Emu.Options.FsRoot, title, content);
        if (!Directory.Exists(dir)) return ERR_ADDCONT_NOT_FOUND;
        Emu.Vfs.Mount("addcont1", dir);
        _forceAddcont = dir;
        return 0;
    }
}

/// <summary>
/// SceAppUtilExt (apputil_ext.suprx 3.74): sceAppUtilExtVideoMount() mounts application data 109 (the video folder,
/// ux0:video) through SceAppMgr and keeps the mount point (a second mount is SceAppMgr's "already mounted", 0x80100603);
/// sceAppUtilExtVideoUmount() unmounts it, 0x80100604 when nothing is mounted.
/// </summary>
public sealed partial class AppUtil
{
    const int VideoAppDataId = 109;
    string? _videoMount;

    [Hle("sceAppUtilExtVideoMount")]
    int sceAppUtilExtVideoMount()
    {
        if (_videoMount != null) return ERR_ALREADY_MOUNTED;
        Directory.CreateDirectory(Io.FirmwareLayout.Video.Host(Emu.Options.FsRoot));
        uint cell = Emu.Memory.Alloc(16, "apputil ext video mount");
        try
        {
            int r = Am.sceAppMgrAppDataMount(VideoAppDataId, cell);
            if (r < 0) return r;
            _videoMount = GuestMemory.ReadCString(cell, 16);
            return 0;
        }
        finally { Emu.Memory.Free(cell); }
    }

    [Hle("sceAppUtilExtVideoUmount")]
    int sceAppUtilExtVideoUmount()
    {
        if (_videoMount == null) return ERR_NOT_MOUNTED;
        uint cell = Emu.Memory.Alloc(16, "apputil ext video umount");
        try
        {
            GuestMemory.Fill(cell, 0, 16);
            GuestMemory.WriteCString(cell, _videoMount, 16);
            if (Am.sceAppMgrUmount(cell) < 0) return ERR_NOT_MOUNTED;
            _videoMount = null;
            return 0;
        }
        finally { Emu.Memory.Free(cell); }
    }
}
