using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>libgxm internals of HLE batch 13, read in libgxm_es4.suprx 3.74.</summary>
public sealed partial class Gxm
{
    const int ERR_REPLAY_OUT_OF_MEMORY = unchecked((int)0x805B000B);

    /// <summary>SceGxmInternalForReplay 0xEFA222AF (VitaSDK name sceGxmGetReplayRenderTargetMemSize): (arena, size,
    /// void **out) takes size bytes, 4-byte aligned, from a replay arena { +8 next free, +0xC end, +0x14 refill
    /// function }: when the space left is too small the refill function runs once on the arena's cursor (+8) and the
    /// cursor is aligned again; still too small = 0x805B000B with *out NULL.</summary>
    [Hle("sceGxmGetReplayRenderTargetMemSize")]
    int GxmReplayArenaAlloc(uint arena, uint size, uint outp)
    {
        if (outp != 0) GuestMemory.W32(outp, 0);
        uint cur = (GuestMemory.R32(arena + 8) + 3) & ~3u;
        if (GuestMemory.R32(arena + 0xC) < cur + size)
        {
            uint refill = GuestMemory.R32(arena + 0x14);
            if (refill != 0) GuestThread.Current?.CallGuest(refill, arena + 8);
            cur = (GuestMemory.R32(arena + 8) + 3) & ~3u;
            if (GuestMemory.R32(arena + 0xC) < cur + size) cur = 0;
        }
        if (cur == 0) return ERR_REPLAY_OUT_OF_MEMORY;
        GuestMemory.W32(arena + 8, cur + size);
        if (outp != 0) GuestMemory.W32(outp, cur);
        return 0;
    }
}
