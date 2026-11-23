using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// The PlayStation Mobile side of appmgr (FW 3.74 SceAppMgr in bootimage.skprx, read for study): a PSM runtime process
// names the PSM title it runs with sceAppMgrInitSafeMemoryById, which keeps the title in the process's appmgr info
// (the field the kernel later formats as "ux0:/psm/%s/RW/System") and moves the process's safe memory to that title's
// pm.dat; SceAppUtilPsm (apputil.suprx) wraps the call for the runtime's libpsm.
public sealed partial class AppMgr
{
    const int ERR_NOT_ALLOWED = unchecked((int)0x80800009), ERR_ALREADY_SET = unchecked((int)0x8080004A),
        ERR_SAFE_MEMORY_FOLDER = unchecked((int)0x80800049), ERR_NULL_NAME = unchecked((int)0x80801001);
    const int SafeMemoryNameMax = 0x40;

    [SnapshotMissing] // states of builds before it: no PSM runtime named a title
    string? _psmTitleId;

    /// <summary>The PSM title this process runs, as sceAppMgrInitSafeMemoryById stored it in the process's appmgr info
    /// (null until the runtime names it). The one place the emulator keeps it: everything that needs the PSM title of
    /// the running process reads it here.</summary>
    public string? PsmTitleId => _psmTitleId;

    /// <summary>A PSM title ID as appmgr checks it for the PSM runtime and for the PSM data mounts (106/107): four
    /// capital letters and five digits ("NPOA00239").</summary>
    internal static bool IsPsmTitleId(string id) =>
        id.Length == 9 && id.AsSpan(0, 4).IndexOfAnyExceptInRange('A', 'Z') < 0 && id.AsSpan(4).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>The name a runtime a PSM title carries itself gives: 1 to 63 of [0-9A-Za-z_-].</summary>
    static bool IsOwnRuntimeName(string name) =>
        name.Length is > 0 and < SafeMemoryNameMax && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>
    /// driver_us: <c>int sceAppMgrInitSafeMemoryById(const char *name)</c>, the syscall with an 8-byte opt. Only a PSM
    /// runtime may call it (else 0x80800009), once (then 0x8080004A). The PSM runtime gives the PSM title ID (NULL or
    /// any other form 0x80800001): its safe memory becomes ux0:psm/&lt;id&gt;/RW/System/pm.dat; a runtime a title carries
    /// itself gives a name (NULL 0x80801001, a bad one 0x80800001) and gets ux0:cache/&lt;title id&gt;/&lt;name&gt;/System/pm.dat.
    /// The folder must exist (the installer makes RW/System), else 0x80800049. The kernel keeps the name in the
    /// process's info and binds the area to the file: an existing pm.dat of the area's size is read into it, otherwise
    /// the file is written from the area (AppUtil.BindSafeMemoryFile).
    /// </summary>
    [Hle] internal int sceAppMgrInitSafeMemoryById(uint name) => _sceAppMgrInitSafeMemoryByIdThroughEntry(name, 0);

    [Hle]
    internal int _sceAppMgrInitSafeMemoryById(uint name, uint opt)
    {
        string? text = null;
        if (name != 0)
        {
            text = GuestMemory.ReadCString(name, SafeMemoryNameMax);
            if (text.Length >= SafeMemoryNameMax)
            {
                return ERR_TOO_LONG;
            }
        }

        PsmRuntimeKind kind = Emu.PsmRuntime;
        if (kind == PsmRuntimeKind.None)
        {
            return ERR_NOT_ALLOWED;
        }

        if (text == null)
        {
            return kind == PsmRuntimeKind.Mono ? ERR_NULL : ERR_NULL_NAME;
        }

        if (_psmTitleId != null)
        {
            return ERR_ALREADY_SET;
        }

        bool valid = kind == PsmRuntimeKind.Mono ? IsPsmTitleId(text) : IsOwnRuntimeName(text);
        if (!valid)
        {
            return ERR_NULL;
        }

        string folder = kind == PsmRuntimeKind.Mono
            ? Io.FirmwareLayout.PsmTitles.Host(Emu.Options.FsRoot, text, Io.FirmwareLayout.PsmWritable, "System")
            : Io.FirmwareLayout.ApplicationCache.Host(Emu.Options.FsRoot, Emu.TitleId, text, "System");
        if (!Directory.Exists(folder))
        {
            return ERR_SAFE_MEMORY_FOLDER;
        }

        int r = AppUtil.BindSafeMemoryFile(Path.Combine(folder, "pm.dat"));
        if (r < 0)
        {
            return r;
        }

        _psmTitleId = text;
        return 0;
    }

    /// <summary>A SceAppMgrUser NID the PSM runtime 1.00 (SDK 2.00) imports and no FW 3.74 module exports (driver_us has
    /// no such entry; vita-headers has no name for it). The module loader leaves such an import as the importer's own
    /// stub, "mvn r0, #0; bx lr": the call returns -1 and writes nothing. That is why the 1.00 runtime cannot start a
    /// title on FW 3.74 (its sceAppMgrAppDataMountById(106) then gets an empty title ID and fails), and the shell asks
    /// for the runtime update first.</summary>
    [Hle("NID_9D6D8058", Nid = 0x9D6D8058, Library = "SceAppMgrUser", Default = true, ReturnsValue = true)]
    uint NotExported9D6D8058() => 0xFFFFFFFF;
}
