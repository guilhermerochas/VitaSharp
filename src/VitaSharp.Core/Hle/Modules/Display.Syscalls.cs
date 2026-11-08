using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceDisplay syscalls behind the SceDriverUser wrappers (3.74 driver_us): the internal frame-buffer calls
/// move their fourth argument (sync) into an opt {sync, pParam->size} on the caller's stack.</summary>
public sealed partial class Display
{
    [Hle]
    int _sceDisplayGetFrameBufInternal(int head, int index, uint pParam, uint opt)
        => sceDisplayGetFrameBufInternal(head, index, pParam, opt == 0 ? 0 : GuestMemory.R32(opt));

    [Hle]
    int _sceDisplaySetFrameBufInternal(int head, int index, uint pParam, uint opt)
        => sceDisplaySetFrameBufInternal(head, index, pParam, opt == 0 ? 0 : GuestMemory.R32(opt));
}
