using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The SceVshBridge syscalls homebrew uses (the so_loader ports print the firmware version and check that the kubridge
// kernel plugin is loaded before anything else).
public sealed partial class VshBridge
{
    /// <summary>_vshSblGetSystemSwVersion(SceKernelSystemSwVersion *): ksceKernelGetSystemSwVersion, the same
    /// structure as sceKernelGetSystemSwVersion.</summary>
    [Hle]
    int _vshSblGetSystemSwVersion(uint data) => SystemMisc.WriteSystemSwVersion(data);

    /// <summary>
    /// _vshKernelSearchModuleByName(name, int buf[2]): the uid of a loaded module (user or kernel) by name, else
    /// SCE_KERNEL_ERROR_MODULEMGR_NO_MOD. The kernel plugins whose user API the HLE provides (kubridge) count as loaded,
    /// as they would be on a unit set up for the homebrew that asks.
    /// </summary>
    [Hle]
    int _vshKernelSearchModuleByName(uint namePtr, uint buf)
    {
        if (namePtr == 0) return SceError.ILLEGAL_ADDR;
        string name = GuestMemory.ReadCString(namePtr, 0x100);
        var m = K.Modules.Values.OrderBy(x => x.Uid).FirstOrDefault(x => x.Name == name);
        if (m != null) return m.Uid;
        if (name == "kubridge") return HleKernelModuleUid;
        return SceError.MODULEMGR_NO_MOD;
    }

    /// <summary>The uid reported for a kernel module emulated at HLE level (an odd value, as kernel UIDs are).</summary>
    const int HleKernelModuleUid = 0x7F100001;
}
