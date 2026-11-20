using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAppMgrForDriver (SceAppMgr 3.74): every export has an entry; the kernel entries run the user and bridge HLE on
/// the same state (mounts, cloud data registration, local backups) with the kernel side's own checks (0x80800001,
/// 0x80800009, 0x80801001); process calls answer 0x80802002 for an unknown process.
/// </summary>
[Collection("guest")]
public sealed class AppMgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Argument = unchecked((int)0x80800001), Caller = unchecked((int)0x80800009);
    private const int NoProcess = unchecked((int)0x80802002);

    private readonly HleHarness _harness = new(fixture, "appmgrk");

    public void Dispose() => _harness.Dispose();

    private VshBridge Bridge => _harness.Module<VshBridge>();

    private AppMgr Manager => _harness.Module<AppMgr>();

    private AppMgrShell Shell => _harness.Module<AppMgrShell>();

    [Fact]
    public void EveryExportHasAnEntry()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Library == "SceAppMgrForDriver").ToList();
        Assert.Equal(73, bindings.Select(b => b.Nid).Distinct().Count());
        Assert.Equal(bindings.Count, bindings.Select(b => b.Nid).Distinct().Count());
    }

    [Fact]
    public void TheCloudDataRegistrationIsTheBridges()
    {
        uint id = _harness.Alloc(8);
        GuestMemory.Write(id, 0x1122334455667788UL);
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCloudDataSetMcId", 0, id));
        uint read = _harness.Alloc(8);
        Assert.Equal(0, Call(Bridge, "_vshAppMgrCloudDataGetMcId", 0, read));
        Assert.Equal(0x1122334455667788UL, GuestMemory.Read<ulong>(read));
        Assert.Equal(0, Call(Bridge, "vshAppMgrCloudDataClearMcId"));
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCloudDataGetMcId", 0, read));
        Assert.Equal(0UL, GuestMemory.Read<ulong>(read));
    }

    [Fact]
    public void TheKernelSideChecksItsArguments()
    {
        uint block = _harness.Alloc(0x200);
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrCheckPfsMounted", 0u));
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCheckPfsMounted", _harness.Str("ux0:app/X")));
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrCheckPfsMounted", _harness.Str(new string('a', 0x124))));
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrCloudDataCreateHeader", block, 0x40000001u, block, block));
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCloudDataCreateHeader", block, 0x100u, block + 0x20, block + 0x40));
        Assert.Equal(0, Call(Bridge, "ksceAppMgrCloudDataVerifyHeader", block + 0x40));
        Assert.Equal(Caller, Call(Bridge, "ksceAppMgrUpdateRifInfo", 0u));
        Assert.Equal(unchecked((int)0x80801001), Call(Bridge, "ksceAppMgrBgdlSetQueueStatus", 0u));
        Assert.Equal(unchecked((int)0x80802014), Call(Bridge, "ksceAppMgrCloudDataSrcMount", block, block, block));
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrCloudDataSrcMount", block, 0u, block));
        Assert.Equal(Argument, Call(Bridge, "ksceAppMgrRegisterPath", 0u, 0));
        Assert.Equal(Argument, Call(Manager, "ksceAppMgr_DCADB9FE", 0u));
        Assert.Equal(Argument, Call(Manager, "ksceAppMgr_0813AFCA", 1, block, 0x10u));
        Assert.Equal(0, Call(Manager, "ksceAppMgr_0813AFCA", 1, block, 0x20u));
        Assert.Equal(unchecked((int)0x80802016), Call(Manager, "ksceAppMgrLaunchAppByPath", 0u, 0u, 0u, 0u, 0u, 0u));
        GuestMemory.Fill(block, 0, 0x40);
        GuestMemory.W32(block + 4, 1);
        Assert.Equal(NoProcess, Call(Manager, "ksceAppMgrLaunchAppByPath", _harness.Str("ux0:app/NONE00000/eboot.bin"), 0u, 0u, 0u, block, 0u));
        GuestMemory.W32(block + 4, 0);
        Assert.Equal(unchecked((int)0x80800006), Call(Manager, "ksceAppMgrLaunchAppByPath", _harness.Str("ux0:app/NONE00000/eboot.bin"), 0u, 0u, 0u, block, 0u));
    }

    [Fact]
    public void ProcessCallsNeedARunningApplication()
    {
        uint state = _harness.Alloc(4);
        Assert.Equal(NoProcess, Call(Shell, "ksceAppMgrKillProcess", 0x7FFF0001));
        Assert.Equal(unchecked((int)0x80802012), Call(Shell, "ksceAppMgrDestroyOtherAppByPid", 0x7FFF0001));
        Assert.Equal(NoProcess, Call(Shell, "ksceAppMgr_29421DCD", 0x7FFF0001));
        Assert.Equal(unchecked((int)0x80802016), Call(Shell, "ksceAppMgr_21BE167A", 0x7FFF0001, 0u));
        Assert.Equal(NoProcess, Call(Shell, "ksceAppMgr_21BE167A", 0x7FFF0001, state));
        Assert.Equal(unchecked((int)0x8080201A), Call(Shell, "ksceAppMgr_99AA37C1", _harness.Str("")));
    }
}
