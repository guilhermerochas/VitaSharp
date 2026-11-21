using VitaSharp.Core.Kernel;

namespace VitaSharp.Core.Hle.Modules;

// SceVshBridge syscalls of the module manager and system version (homebrew such as the so_loader ports prints the
// firmware version and checks that the kernel plugin it needs is loaded before anything else).
public sealed partial class VshBridge
{
    /// <summary>_vshSblGetSystemSwVersion(SceKernelSystemSwVersion *): ksceKernelGetSystemSwVersion, the same
    /// structure as sceKernelGetSystemSwVersion.</summary>
    [Hle]
    int _vshSblGetSystemSwVersion(uint data) => SystemMisc.WriteSystemSwVersion(data);

    /// <summary>
    /// _vshKernelSearchModuleByName(name, int buf[2]): ksceKernelSearchModuleByName for the shell, the UID of the kernel
    /// module of that name (a kernel plugin of the plugin configuration, such as the one an so_loader port checks
    /// for), on the kernel process's list; 0x8002D082 when none.
    /// </summary>
    [Hle]
    int _vshKernelSearchModuleByName(uint namePtr, uint buf)
    {
        if (namePtr == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        return ModulemgrKernel.SearchKernelModuleByName(Emu, namePtr);
    }

}
