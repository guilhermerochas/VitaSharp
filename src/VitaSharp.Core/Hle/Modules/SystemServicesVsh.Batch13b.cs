using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The libkernel 3.74 forms of the SceSblGcAuthMgr services that were still missing: each one fills a size word on
/// its stack and calls the matching _sceSblGcAuthMgr syscall, so it answers what the syscall answers without the
/// secure module (the default group, as in batch 11), and libkernel's NID_70867F93.
/// </summary>
public sealed partial class SystemServicesVsh
{
    [Hle("sceSblGcAuthMgrAdhocBB160Auth1", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB160Auth2", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB160Auth3", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB160Auth4", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB160Auth5", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB160BroadCastDecrypt", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB160BroadCastEncrypt", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB160UniCastDecrypt", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB160UniCastEncrypt", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB224Auth1", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB224Auth2", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB224Auth3", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB224Auth4", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB224Auth5", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrAdhocBB224GetKeys", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrMsSaveBBMacFinal", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrMsSaveBBMacInit", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrPsmactCreateC1", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrPsmactVerifyR1", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrSclkGetData1", Library = "SceLibKernel", Default = true)]
    [Hle("sceSblGcAuthMgrSclkSetData2", Library = "SceLibKernel", Default = true)]
    int LibKernelGcAuthSecure() => SblUnsupported();

    /// <summary>Opens the BB224 ad hoc authentication session (no secure module needed to open it).</summary>
    [Hle("sceSblGcAuthMgrAdhocBB224Init", Library = "SceLibKernel", Default = true)]
    int LibKernelGcAuthAdhocSession() => 0;

    /// <summary>(a, b, count, uint32 *out): clears count words of the output and succeeds.</summary>
    [Hle("NID_70867F93", Nid = 0x70867F93, Library = "SceLibKernel")]
    int LibKernel70867F93(uint a, uint b, uint count, uint outp)
    {
        if (count != 0) GuestMemory.Fill(outp, 0, count << 2);
        return 0;
    }
}
