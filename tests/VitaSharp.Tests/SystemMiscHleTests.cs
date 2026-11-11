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
public sealed class SystemMiscHleTests(GuestMemoryFixture fx) : IDisposable
{
    const int INVALID_VALUE = unchecked((int)0x805A1000), UNLOADED = unchecked((int)0x805A1001),
        NO_MOD = unchecked((int)0x8002D011), NOENT = unchecked((int)0x8002D01C), NO_MOD_BY_ADDR = unchecked((int)0x8002D082);

    readonly HleHarness _h = new(fx, "sysmisc");
    SystemMisc S => _h.Module<SystemMisc>();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void SysmoduleLoadStateById()
    {
        // 0x0001 SCE_SYSMODULE_NET is answered by the HLE
        Assert.Equal(INVALID_VALUE, Call(S, "sceSysmoduleIsLoaded", 0u));
        Assert.Equal(INVALID_VALUE, Call(S, "sceSysmoduleIsLoaded", 0x56u));
        Assert.Equal(UNLOADED, Call(S, "sceSysmoduleIsLoaded", 1u));
        Assert.Equal(0, Call(S, "sceSysmoduleLoadModule", 1u));
        Assert.Equal(0, Call(S, "sceSysmoduleIsLoaded", 1u));
        Assert.Equal(0, Call(S, "sceSysmoduleIsLoadedInternal", 1u));
        Assert.Equal(0, Call(S, "sceSysmoduleLoadModule", 1u)); // already loaded: fine
        Assert.Equal(0, Call(S, "sceSysmoduleUnloadModule", 1u));
        Assert.Equal(UNLOADED, Call(S, "sceSysmoduleIsLoaded", 1u));
        Assert.Equal(UNLOADED, Call(S, "sceSysmoduleIsLoadedInternal", 1u));
        // the WithArg forms report module_start's result through the option
        uint opt = _h.Alloc(0x10), res = _h.Word(0xFFFFFFFF);
        GuestMemory.W32(opt + 4, res);
        Assert.Equal(0, Call(S, "sceSysmoduleLoadModuleInternalWithArg", 1u, 0u, 0u, opt));
        Assert.Equal(0u, GuestMemory.R32(res));
        GuestMemory.W32(res, 0xFFFFFFFF);
        Assert.Equal(0, Call(S, "sceSysmoduleUnloadModuleInternalWithArg", 1u, 0u, 0u, opt));
        Assert.Equal(0u, GuestMemory.R32(res));
        Assert.Equal(UNLOADED, Call(S, "sceSysmoduleUnloadModuleInternalWithArg", 1u, 0u, 0u, opt));
    }

    [Fact]
    public void ModuleManagerRefusesUnknownModules()
    {
        _h.OnGuest(_ =>
        {
            uint st = _h.Word(9), info = _h.Alloc(0x1B8);
            Assert.Equal(NOENT, Call(S, "sceKernelLoadModule", "app0:nothere.suprx", 0u, 0u));
            Assert.Equal(NOENT, Call(S, "sceKernelLoadStartModule", "app0:nothere.suprx", 0u, 0u, 0u, 0u, new Ptr<int>(st)));
            Assert.Equal(NOENT, Call(S, "sceKernelOpenModule", "ur0:data/libshacccg.suprx", 0u, 0u, 0u));
            Assert.Equal(NO_MOD, Call(S, "sceKernelStartModule", 0x7FFF0001, 0u, 0u, 0u, 0u, new Ptr<int>(st)));
            Assert.Equal(NO_MOD, Call(S, "sceKernelStopModule", 0x7FFF0001, 0u, 0u, 0u, 0u, new Ptr<int>(st)));
            Assert.Equal(NO_MOD, Call(S, "sceKernelStopUnloadModule", 0x7FFF0001, 0u, 0u, 0u, 0u, new Ptr<int>(st)));
            Assert.Equal(NO_MOD, Call(S, "sceKernelUnloadModule", 0x7FFF0001, 0u, 0u));
            Assert.Equal(NO_MOD, Call(S, "sceKernelCloseModule", 0x7FFF0001, 0u, 0u, 0u));
            Assert.Equal(NO_MOD, Call(S, "sceKernelGetModuleInfo", 0x7FFF0001, info));
            Assert.Equal(NO_MOD, Call(S, "sceKernelGetLibraryInfoByNID", 0x7FFF0001, 0u, info));
            Assert.Equal(NO_MOD_BY_ADDR, Call(S, "sceKernelGetModuleInfoByAddr", 0x81000000u, info));
            Assert.Equal(NO_MOD_BY_ADDR, Call(S, "sceKernelGetModuleIdByAddr", 0x81000000u));
            Assert.Equal(0, Call(S, "sceKernelIsCalledFromSysModule", 0x81000000u));
            uint uids = _h.Alloc(64), count = _h.Word(16);
            Assert.Equal(0, Call(S, "sceKernelGetModuleList", 0, uids, new Ptr<uint>(count)));
            Assert.True(GuestMemory.R32(count) <= 16);
            Assert.Equal(0, Call(S, "sceKernelCallModuleExit"));
            Assert.Equal((uint)Invoke(S, "sceKernelGetAllowedSdkVersionOnSystem")!, (uint)Invoke(S, "sceKernelGetAllowedSdkVersionOnSystem")!);
            uint res = _h.Alloc(4);
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(S, "sceSblACMgrIsGameProgram", new Ptr<int>(0)));
            Assert.Equal(0, Call(S, "sceSblACMgrIsGameProgram", new Ptr<int>(res)));
            Assert.Equal((uint)Call(S, "sceAppMgrIsGameProgram"), GuestMemory.R32(res));
        });
    }

    [Fact]
    public void AppMgrParamsEventsAndBudget()
    {
        uint buf = _h.Alloc(0x400);
        Assert.Equal(unchecked((int)0x80800001), Call(S, "sceAppMgrAppParamGetString", 0, 12, 0u, 0x400u));
        Assert.Equal(unchecked((int)0x80801000), Call(S, "sceAppMgrAppParamGetString", 0, 5, buf, 0x400u)); // not a string parameter
        Assert.Equal(unchecked((int)0x8080201A), Call(S, "sceAppMgrAppParamGetString", 0x7FFF0001, 12, buf, 0x400u)); // unknown process
        // overrides of CONTENT_ID (100) and TITLE (101), and the size check (room for the terminator)
        S.AppParamOverrides[101] = "My Title";
        Assert.Equal(0, Call(S, "sceAppMgrAppParamGetString", 0, 101, buf, 0x400u));
        Assert.Equal("My Title", GuestMemory.ReadCString(buf, 0x400));
        Assert.Equal(unchecked((int)0x80801000), Call(S, "sceAppMgrAppParamGetString", 0, 101, buf, 8u));
        uint v = _h.Word(9);
        Assert.Equal(0, Call(S, "sceAppMgrAppParamGetInt", 0, 0x10, new Ptr<int>(v)));
        Assert.InRange(GuestMemory.R32(v), 0u, 1u); // the enter button: circle 0 or cross 1
        Assert.Equal(0, Call(S, "sceAppMgrAppParamGetInt", 0, 3, new Ptr<int>(v)));
        Assert.Equal(0u, GuestMemory.R32(v));

        // system events: none queued, then the one posted
        uint ev = _h.Alloc(0x40);
        Assert.Equal(unchecked((int)0x80801000), Call(S, "sceAppMgrReceiveSystemEvent", ev));
        S.PostSystemEvent(0x10000003, "hello world!X");
        Assert.Equal(0, Call(S, "sceAppMgrReceiveSystemEvent", ev));
        Assert.Equal(0x10000003u, GuestMemory.R32(ev));
        Assert.Equal("hello world!", GuestMemory.ReadCString(ev + 4, 0xD));
        Assert.Equal(unchecked((int)0x80801000), Call(S, "sceAppMgrReceiveSystemEvent", ev));

        // only system software gets a memory budget
        Assert.NotEqual(0, Call(S, "sceAppMgrGetBudgetInfo", _h.Alloc(0x88)));
        uint state = _h.Alloc(0x100);
        GuestMemory.Fill(state, 0xFF, 0x100);
        Assert.Equal(0, Call(S, "_sceAppMgrGetAppState", state, 0x40u, 0u));
        Assert.Equal(0u, GuestMemory.R32(state + 0x3C));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(state + 0x40));
        Assert.Equal(0, Call(S, "sceAppMgrIsNonGameProgram"));
        Assert.Equal(0, Call(S, "sceAppMgrAcquireBgmPort"));
        Assert.Equal(0, Call(S, "sceAppMgrReleaseBgmPort"));
        Assert.Equal(0, Call(S, "sceAppMgrSetInfobarState", 1, 0, 0));
        Assert.Equal(0, Call(S, "sceAppMgrSetNetworkDisconnectionWarningDialogState", 1));
    }

    [Fact]
    public void PowerReportsAFullBatteryOnAcPower()
    {
        Assert.Equal(0, Call(S, "scePowerIsLowBattery"));
        Assert.Equal(1, Call(S, "scePowerIsBatteryCharging"));
        Assert.Equal(1, Call(S, "scePowerIsPowerOnline"));
        Assert.Equal(100, Call(S, "scePowerGetBatteryLifePercent"));
        Assert.Equal(1, Call(S, "scePowerIsBatteryExist"));
        Assert.Equal(Call(S, "scePowerGetBatteryFullCapacity"), Call(S, "scePowerGetBatteryRemainCapacity"));
        Assert.True(Call(S, "scePowerGetBatteryLifeTime") > 0);
        Assert.True(Call(S, "scePowerGetArmClockFrequency") > 0);
        Assert.Equal(222, Call(S, "scePowerGetBusClockFrequency"));
        Assert.Equal(222, Call(S, "scePowerGetGpuClockFrequency"));
        Assert.Equal(166, Call(S, "scePowerGetGpuXbarClockFrequency"));
        foreach (var fn in new[] { "scePowerSetBusClockFrequency", "scePowerSetGpuClockFrequency", "scePowerSetGpuXbarClockFrequency", "scePowerSetConfigurationMode", "scePowerRegisterCallback", "scePowerUnregisterCallback" })
        {
            Assert.Equal(0, Call(S, fn, 1));
        }

        foreach (var fn in new[] { "scePowerRequestDisplayOn", "scePowerRequestDisplayOff", "scePowerRequestSuspend", "scePowerRequestStandby", "scePowerRequestColdReset",
            "scePowerIsLowBatteryInhibitUpdateDownload", "scePowerIsLowBatteryInhibitUpdateReboot", "scePowerBatteryUpdateInfo", "scePowerGetBatteryElec", "scePowerGetBatteryChargingStatus" })
        {
            Assert.Equal(0, Call(S, fn));
        }

        Assert.Equal(3000, Call(S, "scePowerGetBatteryTemp"));
        Assert.Equal(4150, Call(S, "scePowerGetBatteryVolt"));
        Assert.Equal(100, Call(S, "scePowerGetBatterySOH"));
        Assert.Equal(4, Call(S, "scePowerGetBatteryRemainLevel"));
        Assert.Equal(4, Call(S, "scePowerGetBatteryRemainMaxLevel"));
        Assert.True(Call(S, "scePowerGetBatteryCycleCount") >= 0);
        Assert.Equal(3000, Call(S, "scePowerGetCaseTemp"));
        Assert.InRange(Call(S, "scePowerGetUsingWireless"), 0, 1);
    }
}
