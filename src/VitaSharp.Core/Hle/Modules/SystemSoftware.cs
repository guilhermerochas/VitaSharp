using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Kernel services that only the system software calls (libpaf, libshellsvc, the system apps and the shell):
/// process/module queries, SceVshBridge, the hardware feature checks. They answer what a retail PS Vita
/// (PCH-1000, no debug features) answers; anything the system software asks that is not known yet is logged once.
/// </summary>
public sealed class SystemSoftware : HleModule
{
    // ------------------------------------------------------------------ SceLibKernel / SceProcessmgr

    /// <summary>SDK version the main module was built with (SceProcessParam +0xC).</summary>
    [Hle]
    uint sceKernelGetMainModuleSdkVersion()
    {
        uint pp = Emu.Linker.ProcessParam;
        if (pp == 0 || GuestMemory.R32(pp + 4) != 0x32505350 /* "PSP2" */) return SystemMisc.SystemSwVersion;
        return GuestMemory.R32(pp + 0xC);
    }

    /// <summary>The extra debug TTY's file descriptor: a retail unit has none (negative error, as without a debugger).</summary>
    [Hle] int sceKernelGetExtraTty() => unchecked((int)0x80020001);

    /// <summary>How many common dialogs the process may hold open at once; accepted.</summary>
    [Hle] int sceKernelCDialogSetLeaseLimit(int limit) => 0;

    // ------------------------------------------------------------------ SceSblSsUpdateMgr

    /// <summary>
    /// Versions of the firmware packages (spkg) installed in the unit's other processors, by package type. The shell
    /// checks at boot that they match what its system software expects and otherwise shows "A serious error has
    /// occurred" (0x80AF0002); the values are the ones the 3.74 shell compares against (read from its code).
    /// Unknown types report the system software version.
    /// </summary>
    static readonly Dictionary<int, uint> SpkgVersions = new() { [0x17] = 0x01000010 };

    /// <summary>(int type, SceSblUsSpkgInfo *info): info +0 size, +4 version.</summary>
    [Hle]
    int sceSblUsGetSpkgInfo(int type, uint info)
    {
        if (info == 0) return unchecked((int)0x80182005); // the shell's own "invalid argument" for this call
        GuestMemory.W32(info + 4, SpkgVersions.GetValueOrDefault(type, SystemMisc.SystemSwVersion));
        return 0;
    }
}
