using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

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
    /// _vshKernelSearchModuleByName(name, int buf[2]): the uid of a loaded module (user or kernel, such as a kernel
    /// plugin of the plugin configuration) by name, else SCE_KERNEL_ERROR_MODULEMGR_NO_MOD.
    /// </summary>
    [Hle]
    int _vshKernelSearchModuleByName(uint namePtr, uint buf)
    {
        if (namePtr == 0) return SceError.ILLEGAL_ADDR;
        string name = GuestMemory.ReadCString(namePtr, 0x100);
        var m = K.Modules.Values.OrderBy(x => x.Uid).FirstOrDefault(x => x.Name == name);
        if (m != null)
        {
            return m.Uid;
        }

        return SceError.MODULEMGR_NO_MOD;
    }

}
