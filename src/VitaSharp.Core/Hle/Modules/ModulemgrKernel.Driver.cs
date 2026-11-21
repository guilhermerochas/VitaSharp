using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The SceModulemgrForDriver entries left (modulemgr 3.74, read in the module, study only): the kernel modules looked up
// by name and by address (both on the kernel process's list: a name compared over 0x1A characters, not found
// 0x8002D082), and the shared-module forms of the *ForPid loads, which need a process (pid 0: 0x8002D017) and, to stop,
// no flags (0x8002000A). The emulated loader keeps no module text shared between processes, so a shared load is
// the process's own load.
public sealed partial class ModulemgrKernel
{
    /// <summary>The length the module manager compares a module name over.</summary>
    const int ModuleNameCompare = 0x1A;

    /// <summary>ksceKernelSearchModuleByName(const char *name): the UID of the kernel module of that name;
    /// 0x8002D082 when none.</summary>
    [Hle(Nid = 0xBBE1771C, Library = "SceModulemgrForDriver")]
    int ksceKernelSearchModuleByName(uint name) => SearchKernelModuleByName(Emu, name);

    /// <summary>The UID of the kernel module named by the string at <paramref name="name"/> (compared over 0x1A
    /// characters) on the kernel process's list, which every caller's system shares; 0x8002D082 when none. Also what
    /// SceVshBridge's _vshKernelSearchModuleByName answers.</summary>
    internal static int SearchKernelModuleByName(Emulator caller, uint name)
    {
        string wanted = GuestMemory.ReadCString(name, ModuleNameCompare);
        if (KernelSpace.ModulesOf(caller, KernelSpace.KernelPid) is { } modules)
        {
            foreach (LoadedModule module in modules)
            {
                string own = module.Name.Length > ModuleNameCompare ? module.Name[..ModuleNameCompare] : module.Name;
                if (string.Equals(own, wanted, StringComparison.Ordinal))
                {
                    return module.Uid;
                }
            }
        }

        return SceError.LIBRARYDB_NO_MOD;
    }

    /// <summary>ksceKernelGetModuleInfoByAddr(const void *addr, SceKernelModuleInfo *info): the record of the kernel
    /// module whose segments hold the address; 0x8002D082 when none.</summary>
    [Hle(Nid = 0x1D9E0F7E, Library = "SceModulemgrForDriver")]
    int ksceKernelGetModuleInfoByAddr(uint address, uint info)
    {
        if (ModulesOf(KernelSpace.KernelPid) is { } modules)
        {
            foreach (LoadedModule module in modules)
            {
                foreach (SegmentInfo segment in module.Segments.Values)
                {
                    if (address >= segment.Address && address - segment.Address < segment.Size)
                    {
                        return info == 0 ? SceError.ILLEGAL_ADDR : SystemMisc.WriteModuleInfo(module, info);
                    }
                }
            }
        }

        return SceError.LIBRARYDB_NO_MOD;
    }

    /// <summary>ksceKernelLoadStartSharedModuleForPid(pid, path, args, argp, flags, opt, int *status): a module for a
    /// process (pid 0: 0x8002D017), loaded and started as ksceKernelLoadStartModuleForPid.</summary>
    [Hle(Nid = 0xE2ADEF8D, Library = "SceModulemgrForDriver")]
    int ksceKernelLoadStartSharedModuleForPid(int pid, string path, uint args, uint argp, int flags, uint opt, uint status)
    {
        if (pid == 0)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        return ksceKernelLoadStartModuleForPid(pid, path, args, argp, flags, opt, status);
    }

    /// <summary>ksceKernelStopUnloadSharedModuleForPid(pid, modid, args, argp, flags, opt, int *status): a process's
    /// module stopped and unloaded (pid 0: 0x8002D017; flags: 0x8002000A).</summary>
    [Hle(Nid = 0x02D3D0C1, Library = "SceModulemgrForDriver")]
    int ksceKernelStopUnloadSharedModuleForPid(int pid, int modid, uint args, uint argp, int flags, uint opt, uint status)
    {
        if (pid == 0)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        if (flags != 0)
        {
            return SceError.INVALID_FLAGS;
        }

        return ksceKernelStopUnloadModuleForPid(pid, modid, args, argp, flags, opt, status);
    }
}
