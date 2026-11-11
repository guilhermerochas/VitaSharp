using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSysmodule (load state per module id, SCE_SYSMODULE_ERROR_*), the module manager's lookups of what is not loaded
/// (SCE_KERNEL_ERROR_MODULEMGR_*), SceAppMgr's per-process answers (app params, system events, budget) and ScePower's
/// fixed hardware answers (the emulated unit is on AC power with a full battery).
/// </summary>
[Collection("guest")]
public sealed class SystemMiscHleTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int INVALID_VALUE = unchecked((int)0x805A1000), UNLOADED = unchecked((int)0x805A1001),
        NO_MOD = unchecked((int)0x8002D011), NOENT = unchecked((int)0x8002D01C), NO_MOD_BY_ADDR = unchecked((int)0x8002D082);

    private readonly HleHarness _harness = new(fixture, "sysmisc");
    private SystemMisc SystemMisc => _harness.Module<SystemMisc>();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SysmoduleLoadStateById()
    {
        // 0x0001 SCE_SYSMODULE_NET is answered by the HLE
        Assert.Equal(INVALID_VALUE, Call(SystemMisc, "sceSysmoduleIsLoaded", 0u));
        Assert.Equal(INVALID_VALUE, Call(SystemMisc, "sceSysmoduleIsLoaded", 0x56u));
        Assert.Equal(UNLOADED, Call(SystemMisc, "sceSysmoduleIsLoaded", 1u));
        Assert.Equal(0, Call(SystemMisc, "sceSysmoduleLoadModule", 1u));
        Assert.Equal(0, Call(SystemMisc, "sceSysmoduleIsLoaded", 1u));
        Assert.Equal(0, Call(SystemMisc, "sceSysmoduleIsLoadedInternal", 1u));
        Assert.Equal(0, Call(SystemMisc, "sceSysmoduleLoadModule", 1u)); // already loaded: fine
        Assert.Equal(0, Call(SystemMisc, "sceSysmoduleUnloadModule", 1u));
        Assert.Equal(UNLOADED, Call(SystemMisc, "sceSysmoduleIsLoaded", 1u));
        Assert.Equal(UNLOADED, Call(SystemMisc, "sceSysmoduleIsLoadedInternal", 1u));
        // the WithArg forms report module_start's result through the option
        uint opt = _harness.Alloc(0x10), res = _harness.Word(0xFFFFFFFF);
        GuestMemory.W32(opt + 4, res);
        Assert.Equal(0, Call(SystemMisc, "sceSysmoduleLoadModuleInternalWithArg", 1u, 0u, 0u, opt));
        Assert.Equal(0u, GuestMemory.R32(res));
        GuestMemory.W32(res, 0xFFFFFFFF);
        Assert.Equal(0, Call(SystemMisc, "sceSysmoduleUnloadModuleInternalWithArg", 1u, 0u, 0u, opt));
        Assert.Equal(0u, GuestMemory.R32(res));
        Assert.Equal(UNLOADED, Call(SystemMisc, "sceSysmoduleUnloadModuleInternalWithArg", 1u, 0u, 0u, opt));
    }

    [Fact]
    public void ModuleManagerRefusesUnknownModules()
    {
        _harness.OnGuest(_ =>
        {
            uint st = _harness.Word(9), info = _harness.Alloc(0x1B8);
            Assert.Equal(NOENT, Call(SystemMisc, "sceKernelLoadModule", "app0:nothere.suprx", 0u, 0u));
            Assert.Equal(NOENT, Call(SystemMisc, "sceKernelLoadStartModule", "app0:nothere.suprx", 0u, 0u, 0u, 0u, new Ptr<int>(st)));
            Assert.Equal(NOENT, Call(SystemMisc, "sceKernelOpenModule", "ur0:data/libshacccg.suprx", 0u, 0u, 0u));
            Assert.Equal(NO_MOD, Call(SystemMisc, "sceKernelStartModule", 0x7FFF0001, 0u, 0u, 0u, 0u, new Ptr<int>(st)));
            Assert.Equal(NO_MOD, Call(SystemMisc, "sceKernelStopModule", 0x7FFF0001, 0u, 0u, 0u, 0u, new Ptr<int>(st)));
            Assert.Equal(NO_MOD, Call(SystemMisc, "sceKernelStopUnloadModule", 0x7FFF0001, 0u, 0u, 0u, 0u, new Ptr<int>(st)));
            Assert.Equal(NO_MOD, Call(SystemMisc, "sceKernelUnloadModule", 0x7FFF0001, 0u, 0u));
            Assert.Equal(NO_MOD, Call(SystemMisc, "sceKernelCloseModule", 0x7FFF0001, 0u, 0u, 0u));
            Assert.Equal(NO_MOD, Call(SystemMisc, "sceKernelGetModuleInfo", 0x7FFF0001, info));
            Assert.Equal(NO_MOD, Call(SystemMisc, "sceKernelGetLibraryInfoByNID", 0x7FFF0001, 0u, info));
            Assert.Equal(NO_MOD_BY_ADDR, Call(SystemMisc, "sceKernelGetModuleInfoByAddr", 0x81000000u, info));
            Assert.Equal(NO_MOD_BY_ADDR, Call(SystemMisc, "sceKernelGetModuleIdByAddr", 0x81000000u));
            Assert.Equal(0, Call(SystemMisc, "sceKernelIsCalledFromSysModule", 0x81000000u));
            uint uids = _harness.Alloc(64), count = _harness.Word(16);
            Assert.Equal(0, Call(SystemMisc, "sceKernelGetModuleList", 0, uids, new Ptr<uint>(count)));
            Assert.True(GuestMemory.R32(count) <= 16);
            Assert.Equal(0, Call(SystemMisc, "sceKernelCallModuleExit"));
            Assert.Equal((uint)Invoke(SystemMisc, "sceKernelGetAllowedSdkVersionOnSystem")!, (uint)Invoke(SystemMisc, "sceKernelGetAllowedSdkVersionOnSystem")!);
            uint res = _harness.Alloc(4);
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(SystemMisc, "sceSblACMgrIsGameProgram", new Ptr<int>(0)));
            Assert.Equal(0, Call(SystemMisc, "sceSblACMgrIsGameProgram", new Ptr<int>(res)));
            Assert.Equal((uint)Call(SystemMisc, "sceAppMgrIsGameProgram"), GuestMemory.R32(res));
        });
    }

    [Fact]
    public void AppMgrParamsEventsAndBudget()
    {
        uint buf = _harness.Alloc(0x400);
        Assert.Equal(unchecked((int)0x80800001), Call(SystemMisc, "sceAppMgrAppParamGetString", 0, 12, 0u, 0x400u));
        Assert.Equal(unchecked((int)0x80801000), Call(SystemMisc, "sceAppMgrAppParamGetString", 0, 5, buf, 0x400u)); // not a string parameter
        Assert.Equal(unchecked((int)0x8080201A), Call(SystemMisc, "sceAppMgrAppParamGetString", 0x7FFF0001, 12, buf, 0x400u)); // unknown process
        // overrides of CONTENT_ID (100) and TITLE (101), and the size check (room for the terminator)
        SystemMisc.AppParamOverrides[101] = "My Title";
        Assert.Equal(0, Call(SystemMisc, "sceAppMgrAppParamGetString", 0, 101, buf, 0x400u));
        Assert.Equal("My Title", GuestMemory.ReadCString(buf, 0x400));
        Assert.Equal(unchecked((int)0x80801000), Call(SystemMisc, "sceAppMgrAppParamGetString", 0, 101, buf, 8u));
        uint v = _harness.Word(9);
        Assert.Equal(0, Call(SystemMisc, "sceAppMgrAppParamGetInt", 0, 0x10, new Ptr<int>(v)));
        Assert.InRange(GuestMemory.R32(v), 0u, 1u); // the enter button: circle 0 or cross 1
        Assert.Equal(0, Call(SystemMisc, "sceAppMgrAppParamGetInt", 0, 3, new Ptr<int>(v)));
        Assert.Equal(0u, GuestMemory.R32(v));

        // system events: none queued, then the one posted
        uint ev = _harness.Alloc(0x40);
        Assert.Equal(unchecked((int)0x80801000), Call(SystemMisc, "sceAppMgrReceiveSystemEvent", ev));
        SystemMisc.PostSystemEvent(0x10000003, "hello world!X");
        Assert.Equal(0, Call(SystemMisc, "sceAppMgrReceiveSystemEvent", ev));
        Assert.Equal(0x10000003u, GuestMemory.R32(ev));
        Assert.Equal("hello world!", GuestMemory.ReadCString(ev + 4, 0xD));
        Assert.Equal(unchecked((int)0x80801000), Call(SystemMisc, "sceAppMgrReceiveSystemEvent", ev));

        // only system software gets a memory budget
        Assert.NotEqual(0, Call(SystemMisc, "sceAppMgrGetBudgetInfo", _harness.Alloc(0x88)));
        uint state = _harness.Alloc(0x100);
        GuestMemory.Fill(state, 0xFF, 0x100);
        Assert.Equal(0, Call(SystemMisc, "_sceAppMgrGetAppState", state, 0x40u, 0u));
        Assert.Equal(0u, GuestMemory.R32(state + 0x3C));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(state + 0x40));
        Assert.Equal(0, Call(SystemMisc, "sceAppMgrIsNonGameProgram"));
        Assert.Equal(0, Call(SystemMisc, "sceAppMgrAcquireBgmPort"));
        Assert.Equal(0, Call(SystemMisc, "sceAppMgrReleaseBgmPort"));
        Assert.Equal(0, Call(SystemMisc, "sceAppMgrSetInfobarState", 1, 0, 0));
        Assert.Equal(0, Call(SystemMisc, "sceAppMgrSetNetworkDisconnectionWarningDialogState", 1));
    }

    [Fact]
    public void PowerReportsAFullBatteryOnAcPower()
    {
        Assert.Equal(0, Call(SystemMisc, "scePowerIsLowBattery"));
        Assert.Equal(1, Call(SystemMisc, "scePowerIsBatteryCharging"));
        Assert.Equal(1, Call(SystemMisc, "scePowerIsPowerOnline"));
        Assert.Equal(100, Call(SystemMisc, "scePowerGetBatteryLifePercent"));
        Assert.Equal(1, Call(SystemMisc, "scePowerIsBatteryExist"));
        Assert.Equal(Call(SystemMisc, "scePowerGetBatteryFullCapacity"), Call(SystemMisc, "scePowerGetBatteryRemainCapacity"));
        Assert.True(Call(SystemMisc, "scePowerGetBatteryLifeTime") > 0);
        Assert.True(Call(SystemMisc, "scePowerGetArmClockFrequency") > 0);
        Assert.Equal(222, Call(SystemMisc, "scePowerGetBusClockFrequency"));
        Assert.Equal(222, Call(SystemMisc, "scePowerGetGpuClockFrequency"));
        Assert.Equal(166, Call(SystemMisc, "scePowerGetGpuXbarClockFrequency"));
        foreach (string? fn in new[] { "scePowerSetBusClockFrequency", "scePowerSetGpuClockFrequency", "scePowerSetGpuXbarClockFrequency", "scePowerSetConfigurationMode", "scePowerRegisterCallback", "scePowerUnregisterCallback" })
        {
            Assert.Equal(0, Call(SystemMisc, fn, 1));
        }

        foreach (string? fn in new[] { "scePowerRequestDisplayOn", "scePowerRequestDisplayOff", "scePowerRequestSuspend", "scePowerRequestStandby", "scePowerRequestColdReset",
            "scePowerIsLowBatteryInhibitUpdateDownload", "scePowerIsLowBatteryInhibitUpdateReboot", "scePowerBatteryUpdateInfo", "scePowerGetBatteryElec", "scePowerGetBatteryChargingStatus" })
        {
            Assert.Equal(0, Call(SystemMisc, fn));
        }

        Assert.Equal(3000, Call(SystemMisc, "scePowerGetBatteryTemp"));
        Assert.Equal(4150, Call(SystemMisc, "scePowerGetBatteryVolt"));
        Assert.Equal(100, Call(SystemMisc, "scePowerGetBatterySOH"));
        Assert.Equal(4, Call(SystemMisc, "scePowerGetBatteryRemainLevel"));
        Assert.Equal(4, Call(SystemMisc, "scePowerGetBatteryRemainMaxLevel"));
        Assert.True(Call(SystemMisc, "scePowerGetBatteryCycleCount") >= 0);
        Assert.Equal(3000, Call(SystemMisc, "scePowerGetCaseTemp"));
        Assert.InRange(Call(SystemMisc, "scePowerGetUsingWireless"), 0, 1);
    }
}
