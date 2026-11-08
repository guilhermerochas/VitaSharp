using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// Syscall forms of functions the HLE already had under their user name (the 3.74 driver_us/avcodec_us/libnet wrappers
// pass the arguments unchanged), and small services of np_common, the error history and the application list.

public sealed partial class AppMgr
{
    [Hle] int _sceAppMgrDrmOpen(uint param) => sceAppMgrDrmOpen(param);
    [Hle] int _sceAppMgrDrmClose(uint param) => sceAppMgrDrmClose(param);
    [Hle] int _sceAppMgrSaveDataSlotInit(uint param, uint opt) => sceAppMgrSaveDataSlotInit();
    [Hle] int _sceAppMgrSaveDataSlotFileOpen(uint param, uint result) => sceAppMgrSaveDataSlotFileOpen();
    [Hle] int _sceAppMgrSaveDataSlotFileClose(uint param) => sceAppMgrSaveDataSlotFileClose();
    [Hle] int _sceAppMgrUpdateSaveDataParam(uint path) => sceAppMgrUpdateSaveDataParam(path);
    [Hle] int _sceAppMgrSetBgmProxyApp(int appId) => sceAppMgrSetBgmProxyApp(appId);
}

public sealed partial class AppMgrShell
{
    [Hle] int _sceAppMgrSetShellScreenOrientation(int a, int b) => sceAppMgrSetShellScreenOrientation(a, b);
}

public sealed partial class SystemMisc
{
    /// <summary>(SceUID *appIds, int count[, opt: size = count*4 &lt;= 0x50]): the application ids of the running
    /// applications other than the shell (only the shell asks); returns how many were written.</summary>
    [Hle("_sceAppMgrGetRunningAppIdListForShell")]
    int GetRunningAppIdListForShell(uint list, int count, uint opt)
    {
        if ((uint)count * 4 > 0x50) return unchecked((int)0x8002710B);
        if (list == 0) return unchecked((int)0x80802016);
        int n = 0;
        foreach (var p in Emu.Vita.Processes)
        {
            if (p.IsShell || n >= count) continue;
            GuestMemory.W32(list + (uint)n++ * 4, (uint)p.AppId);
        }
        return n;
    }
}

public sealed partial class SystemServicesVsh
{
    /// <summary>(SceErrorHistoryEntries *out 0x1B0): the error history Settings shows. PostError only logs, nothing is
    /// recorded: an empty history.</summary>
    [Hle("sceErrorHistoryGetError")]
    [Hle("_sceErrorHistoryGetError")]
    int ErrorHistoryGetError(uint buf)
    {
        if (buf == 0) return unchecked((int)0x80020006);
        GuestMemory.Fill(buf, 0, 0x1B0);
        return 0;
    }

    /// <summary>Clears the error history (already empty).</summary>
    [Hle("sceErrorHistoryClearError")]
    [Hle("_sceErrorHistoryClearError")]
    int ErrorHistoryClearError() => 0;

    /// <summary>(const SceErrorHistorySequenceInfo *info 0x20): the sequence numbers the shell keeps for the history.</summary>
    [Hle("sceErrorHistoryUpdateSequenceInfo")]
    [Hle("_sceErrorHistoryUpdateSequenceInfo")]
    int ErrorHistoryUpdateSequenceInfo(uint info) => info == 0 ? unchecked((int)0x80020006) : 0;
}

public sealed partial class Np
{
    /// <summary>(char buf[8]): np_common.suprx 3.74: the buffer cleared, then snprintf(buf, 8, "%02d.%02d") of the
    /// version sceKernelGetSystemSwVersion reports ("03.74"); snprintf's result back.</summary>
    [Hle]
    int sceNpCommonGetSystemSwVersion(uint buf)
    {
        uint v = SystemMisc.SystemSwVersion;
        uint major = ((v >> 28) & 0x96) + ((v >> 24) & 0xF), minor = ((v >> 20) & 0xF) * 10 + ((v >> 16) & 0xF);
        string s = $"{major:D2}.{minor:D2}";
        GuestMemory.Fill(buf, 0, 8);
        GuestMemory.WriteCString(buf, s, 8);
        return s.Length;
    }

    /// <summary>(const void *src, char *dst, SceSize len): standard Base64 with '=' padding, no terminator; returns the
    /// characters written.</summary>
    [Hle]
    int sceNpCommonBase64Encode(uint src, uint dst, uint len)
    {
        var s = Convert.ToBase64String(GuestMemory.ReadSpan(src, (int)len));
        for (int i = 0; i < s.Length; i++) GuestMemory.Write(dst + (uint)i, (byte)s[i]);
        return s.Length;
    }
}

public sealed partial class Net
{
    /// <summary>libnet's internal inet_pton (the same parser as sceNetInetPton, a flag word added).</summary>
    [Hle] int sceNetInternalInetPton(int af, string src, uint dst) => sceNetInetPton(af, src, dst);
}

public sealed partial class Videodec
{
    // the user forms of the system applications' entries (avcodec_us: the arguments go through unchanged)
    [Hle("sceAvcdecDecodeAuNongameapp")] int DecodeAuNongameapp(uint ctrl, uint au, uint ready) => DecodeAu(ctrl, au, ready);
    [Hle("sceAvcdecDecodeGetPictureNongameapp")] int DecodeGetPictureNongameapp(uint ctrl, uint array, uint more) => DecodeGetPicture(ctrl, array, more);
    [Hle("sceAvcdecSetLowDelayModeNongameapp")] int SetLowDelayModeNongameapp(uint ctrl, int mode) => SetLowDelayMode(ctrl, mode);
}
