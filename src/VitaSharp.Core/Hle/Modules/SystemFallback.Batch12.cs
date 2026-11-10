using VitaSharp.Core.Memory;
using static VitaSharp.Core.Hle.ShellSvcIpc;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// libScePromoterUtil 3.74: the package operations of the promoter, each one a request to the shell's service
/// (0x13xxxx) followed, for the synchronous ones, by polling the promoter state (0x130003, a sleep of 0.2 or 1 s
/// while it is busy) and reading the result (0x130004). Without the shell's service the first request fails as
/// not available (ShellSvcIpc), like the library without libshellsvc.
/// </summary>
public sealed partial class SystemFallback
{
    const int PROMOTER_BUSY = unchecked((int)0x80AF0021);
    ThreadMgr? _threads;
    ThreadMgr T => _threads ??= Emu.Hle.Modules.OfType<ThreadMgr>().First();

    /// <summary>The promoter state (0 idle) or the request's error.</summary>
    int PromoterStateNow(out uint state)
    {
        Span<uint> cell = stackalloc uint[1];
        int r = Invoke(Emu, 0x130003, [], [Cell()], cell, PROMOTER_INVALID);
        state = cell[0];
        return r;
    }

    /// <summary>Waits until the promoter is idle, then answers its result (0x130004), like the library's loops.</summary>
    int PromoterWait(uint sleepUs, uint resultOut = 0, uint resultSize = 0)
    {
        while (true)
        {
            int r = PromoterStateNow(out uint state);
            if (r < 0) return r;
            if (state == 0) break;
            T.sceKernelDelayThread(sleepUs);
        }
        Span<uint> cell = stackalloc uint[1];
        int rr = resultOut == 0
            ? Invoke(Emu, 0x130004, [], [Cell()], cell, PROMOTER_INVALID)
            : Invoke(Emu, 0x130005, [], [Cell(), Buf(resultOut, resultSize)], cell, PROMOTER_INVALID);
        return rr < 0 ? rr : (int)cell[0];
    }

    /// <summary>(const char *titleId): deletes an installed package.</summary>
    [Hle("scePromoterUtilityDeletePkg", Library = "ScePromoterUtil")]
    int PromoterDeletePkg(uint titleId)
    {
        if (titleId == 0) return PROMOTER_INVALID;
        int r = Promoter(0x130009, [Str(titleId)], []);
        return r < 0 ? r : PromoterWait(200_000);
    }

    /// <summary>(const char *titleId, SceUInt64 *size): the installed size of a title.</summary>
    [Hle("scePromoterUtilityGetSize", Library = "ScePromoterUtil")]
    int PromoterGetSize(uint titleId, uint size)
    {
        if (titleId == 0 || size == 0) return PROMOTER_INVALID;
        int r = Promoter(0x13000B, [Str(titleId)], [Buf(size, 8)]);
        return r < 0 ? r : PromoterWait(200_000, size, 8);
    }

    /// <summary>(const ScePromoterUtilityImportParam *param 0xB0): promotes an imported content and waits.</summary>
    [Hle("scePromoterUtilityPromoteImport", Library = "ScePromoterUtil")]
    int PromoterPromoteImport(uint param)
    {
        int r = param == 0 ? PROMOTER_INVALID : Promoter(0x13000D, [Buf(param, 0xB0)], []);
        return r < 0 ? r : PromoterWait(1_000_000);
    }

    /// <summary>The 0x120 request of a package promotion: the path (0x7E), then flags 1 at +0x80 and +0x83, the
    /// caller's flag at +0x82 and 3 at +0x84; refused while the promoter is busy.</summary>
    int PromotePkgRequest(uint path, byte flag)
    {
        if (path == 0) return PROMOTER_INVALID;
        int r = PromoterStateNow(out uint state);
        if (r < 0) return r;
        if (state != 0) return PROMOTER_BUSY;
        lock (_promoterLock)
        {
            if (_promoterReq == 0) _promoterReq = Mem.Alloc(0x120, "promoter request (HLE)");
            uint req = _promoterReq;
            GuestMemory.Fill(req, 0, 0x120);
            GuestMemory.WriteCString(req, GuestMemory.ReadCString(path, 0x7E), 0x7E);
            GuestMemory.Write<byte>(req + 0x80, 1);
            GuestMemory.Write(req + 0x82, flag);
            GuestMemory.Write<byte>(req + 0x83, 1);
            GuestMemory.W32(req + 0x84, 3);
            return Promoter(0x130001, [Buf(req, 0x120)], []);
        }
    }

    uint _promoterReq;
    readonly object _promoterLock = new();

    /// <summary>(const char *path, SceBool flag): starts promoting a package (asynchronous).</summary>
    [Hle("scePromoterUtilityPromotePkg", Library = "ScePromoterUtil")]
    int PromoterPromotePkg(uint path, int flag) => PromotePkgRequest(path, (byte)flag);

    /// <summary>(const char *path, SceBool flag): promotes a package and waits for the result.</summary>
    [Hle("scePromoterUtilityPromotePkgWithRif", Library = "ScePromoterUtil")]
    int PromoterPromotePkgWithRif(uint path, int flag)
    {
        int r = PromotePkgRequest(path, (byte)flag);
        return r < 0 ? r : PromoterWait(1_000_000);
    }

    /// <summary>(const char *titleId, SceUInt64 arg): removes a title's saved data (0x13001E { arg }, titleId).</summary>
    [Hle("scePromoterUtilityRemoveSavedata", Library = "ScePromoterUtil")]
    int PromoterRemoveSavedata(uint titleId, uint lo, uint hi)
    {
        if (titleId == 0) return PROMOTER_INVALID;
        int r = Promoter(0x13001E, [Val(lo, hi), Str(titleId)], []);
        return r < 0 ? r : PromoterWait(1_000_000);
    }

    /// <summary>(const ScePromoterUtilityLAUpdate *update): the title id (+0) and the LiveArea path (+0xC).</summary>
    [Hle("scePromoterUtilityUpdateLiveArea", Library = "ScePromoterUtil")]
    int PromoterUpdateLiveArea(uint update)
    {
        if (update == 0) return PROMOTER_INVALID;
        int r = Promoter(0x13000E, [Str(update), Str(update + 0xC)], []);
        return r < 0 ? r : PromoterWait(1_000_000);
    }

    /// <summary>(const char *titleId): refreshes a title's upgradable state.</summary>
    [Hle("scePromoterUtilityUpdateUpgradableStatus", Library = "ScePromoterUtil")]
    int PromoterUpdateUpgradableStatus(uint titleId)
    {
        if (titleId == 0) return PROMOTER_INVALID;
        int r = Promoter(0x13000C, [Str(titleId)], []);
        return r < 0 ? r : PromoterWait(1_000_000);
    }

    // ------------------------------------------------------------------ SceTeleportServer (libSceTeleportServer)

    /// <summary>(const SceTeleportServerInitialInfo *info, int arg): the Remote Play server side, which the module
    /// refuses with 0x8010840A on a unit with model capability 10 (the teleport client's one) before looking at its
    /// arguments; the emulated unit has it (sceKernelCheckModelCapability), so this is the answer here.</summary>
    [Hle("sceTeleportServerSendInitialInfo", Library = "SceTeleportServer")]
    int TeleportServerSendInitialInfo(uint info, uint arg) => unchecked((int)0x8010840A);
}
