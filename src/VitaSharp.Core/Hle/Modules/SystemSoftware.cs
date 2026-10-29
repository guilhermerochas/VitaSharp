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
}
