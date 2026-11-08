using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceModulemgr syscalls behind the SceLibKernel wrappers (3.74 libkernel): the start/stop calls pass
/// opt {flags, option, status*} as the fourth argument instead of three more arguments.
/// </summary>
public sealed partial class SystemMisc
{
    static uint ModOpt(uint opt, int index) => opt == 0 ? 0 : GuestMemory.R32(opt + (uint)index * 4);

    [Hle] int _sceKernelLoadModule(string path, uint flags, uint opt) => sceKernelLoadModule(path, flags, opt);

    [Hle]
    int _sceKernelLoadStartModule(string path, uint args, uint argp, uint opt)
        => sceKernelLoadStartModule(path, args, argp, ModOpt(opt, 0), ModOpt(opt, 1), new Ptr<int>(ModOpt(opt, 2)));

    [Hle]
    int _sceKernelStartModule(int uid, uint args, uint argp, uint opt)
        => sceKernelStartModule(uid, args, argp, ModOpt(opt, 0), ModOpt(opt, 1), new Ptr<int>(ModOpt(opt, 2)));

    [Hle]
    int _sceKernelStopModule(int uid, uint args, uint argp, uint opt)
        => sceKernelStopModule(uid, args, argp, ModOpt(opt, 0), ModOpt(opt, 1), new Ptr<int>(ModOpt(opt, 2)));

    [Hle]
    int _sceKernelStopUnloadModule(int uid, uint args, uint argp, uint opt)
        => sceKernelStopUnloadModule(uid, args, argp, ModOpt(opt, 0), ModOpt(opt, 1), new Ptr<int>(ModOpt(opt, 2)));
}
