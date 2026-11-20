using System.Runtime.CompilerServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ScePower over the syscon chip and the system's power state (FW 3.74 power.skprx): an unplugged retail unit's
/// battery, the AC adapter and charging, no battery, the battery fake status counting down in guest time, the
/// clocks (rounding, ranges, the highest process's), the idle timers and the kernel exports of the census. The modules
/// and the state are reached by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed unsafe class KernelPowerTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x802B0000), Busy = unchecked((int)0x802B0001);
    private const int NoBattery = unchecked((int)0x802B0100);

    private readonly HleHarness _harness = new(fixture, "kpower");

    public void Dispose() => _harness.Dispose();

    private HleModule Module(string name) => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == name);

    private HleModule Misc => Module("SystemMisc");

    private HleModule Power => Module("PowerKernel");

    private object SystemMember(string name) => _harness.Emu.Vita.GetType().GetProperty(name)!.GetValue(_harness.Emu.Vita)!;

    private static void Set(object target, string name, object value)
    {
        if (target.GetType().GetField(name) is { } field)
        {
            field.SetValue(target, value);
        }
        else
        {
            target.GetType().GetProperty(name)!.SetValue(target, value);
        }
    }

    private object Chip => SystemMember("Syscon");

    private object Battery => Chip.GetType().GetField("Battery")!.GetValue(Chip)!;

    private static string RepoRoot([CallerFilePath] string source = "")
    {
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VitaSharp.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("the repository");
    }

    [Fact]
    public void EveryPowerExportOfTheCensusIsBound()
    {
        string tsv = Path.Combine(RepoRoot(), "docs", "hle", "data", "kernel-exports.tsv");
        var census = File.ReadLines(tsv).Select(line => line.Split('\t'))
            .Where(parts => parts.Length > 3 && parts[0] == "ScePowerForDriver")
            .Select(parts => Convert.ToUInt32(parts[1], 16)).ToList();
        Assert.Equal(109, census.Count);
        Assert.All(census, nid => Assert.True(_harness.Emu.Hle.ImplOf(nid) != null, $"0x{nid:X8} has no implementation"));
    }

    [Fact]
    public void AnUnpluggedRetailUnitRunsOnItsChargedBattery()
    {
        Assert.Equal(0, Call(Misc, "scePowerIsPowerOnline"));
        Assert.Equal(0, Call(Misc, "scePowerIsBatteryCharging"));
        Assert.Equal(0, Call(Misc, "scePowerGetBatteryChargingStatus"));
        Assert.Equal(1, Call(Misc, "scePowerIsBatteryExist"));
        Assert.Equal(100, Call(Misc, "scePowerGetBatteryLifePercent"));
        Assert.True(Call(Misc, "scePowerGetBatteryLifeTime") > 0);
        Assert.Equal(0, Call(Misc, "scePowerIsLowBattery"));

        uint elec = _harness.Alloc(4);
        GuestMemory.W32(elec, 0xFFFFFFFF);
        Assert.Equal(0, Call(Misc, "scePowerGetBatteryElec", elec));
        Assert.Equal(0u, GuestMemory.R32(elec));

        // the kernel side reads the same chip: the battery status word, the AC adapter
        Assert.Equal(0, Call(Power, "NID_8C0D2051", elec));
        Assert.Equal(0x40u, GuestMemory.R32(elec));
    }

    /// <summary>Unplugged does not mean a draining battery: in a long session the battery stays full and no low
    /// battery flag or event bit ever appears, so neither the shell nor a game warns, suspends or slows down.</summary>
    [Fact]
    public void ALongUnpluggedSessionNeverRunsTheBatteryLow()
    {
        using var clock = ControlledGuestClock.Stop();
        HleModule syscon = Module("SysconKernel");
        for (int hour = 0; hour < 12; hour++)
        {
            clock.Advance(3_600_000_000L);
            Assert.Equal(100, Call(Misc, "scePowerGetBatteryLifePercent"));
            Assert.Equal(2200, Call(Misc, "scePowerGetBatteryRemainCapacity"));
            Assert.Equal(0, Call(Misc, "scePowerIsLowBattery"));
            Assert.Equal(0, Call(Misc, "scePowerIsSuspendRequired"));
            Assert.Equal(0, Call(Misc, "scePowerIsLowBatteryInhibitUpdateDownload"));
            Assert.Equal(0, Call(syscon, "NID_27758A64")); // the chip's low battery bit
            Assert.Equal(0, Call(syscon, "NID_D7F5A797")); // its suspend-required bit
            Assert.Equal(0u, Convert.ToUInt32(Invoke(syscon, "NID_B7BCC638"))); // no event bit at all
        }

        // the clocks have no battery limit: the application maximum is taken off the adapter too
        Assert.Equal(0, Call(Misc, "scePowerSetGpuXbarClockFrequency", 166));
        Assert.Equal(166, Call(Misc, "scePowerGetGpuXbarClockFrequency"));
        Assert.Equal(0x10000u, Convert.ToUInt32(Invoke(Power, "kscePowerGetDisplayMaxBrightness")));
    }

    [Fact]
    public void OnTheAcAdapterTheBatteryChargesUnlessForbidden()
    {
        Set(Chip, "AcAdapter", true);
        Set(Battery, "Charging", true);
        Assert.Equal(1, Call(Misc, "scePowerIsPowerOnline"));
        Assert.Equal(1, Call(Misc, "scePowerIsBatteryCharging"));
        Assert.Equal(1, Call(Misc, "scePowerGetBatteryChargingStatus"));
        Assert.Equal(0, Call(Misc, "scePowerGetBatteryLifeTime"));

        Assert.Equal(0, Call(Power, "kscePowerBatteryForbidCharging"));
        Assert.Equal(0, Call(Power, "kscePowerBatteryForbidCharging"));
        Assert.Equal(2, Call(Misc, "scePowerGetBatteryChargingStatus"));
        Assert.Equal(0, Call(Misc, "scePowerIsBatteryCharging"));
        Assert.Equal(0, Call(Power, "kscePowerBatteryPermitCharging"));
        Assert.Equal(2, Call(Misc, "scePowerGetBatteryChargingStatus"));
        Assert.Equal(0, Call(Power, "kscePowerBatteryPermitCharging"));
        Assert.Equal(0, Call(Power, "kscePowerBatteryPermitCharging"));
        Assert.Equal(1, Call(Misc, "scePowerIsBatteryCharging"));
    }

    [Fact]
    public void WithoutABatteryItsValuesAreRefused()
    {
        Set(Battery, "Present", false);
        Assert.Equal(0, Call(Misc, "scePowerIsBatteryExist"));
        Assert.Equal(NoBattery, Call(Misc, "scePowerGetBatteryLifePercent"));
        Assert.Equal(NoBattery, Call(Misc, "scePowerGetBatteryTemp"));
        Assert.Equal(NoBattery, Call(Misc, "scePowerIsBatteryCharging"));
        Assert.Equal(NoBattery, Call(Misc, "scePowerGetBatteryElec", _harness.Alloc(4)));
    }

    [Fact]
    public void TheFakeBatteryCountsDownInGuestTime()
    {
        using var clock = ControlledGuestClock.Stop();
        uint status = _harness.Alloc(0x10);
        GuestMemory.W32(status, 0x10);
        GuestMemory.W32(status + 4, 1000);
        GuestMemory.W32(status + 8, 2000);
        GuestMemory.W32(status + 12, 10_000_000);
        Assert.Equal(0, Call(Power, "kscePowerSetBatteryFakeStatus", status));
        Assert.Equal(1000, Call(Misc, "scePowerGetBatteryRemainCapacity"));
        Assert.Equal(2000, Call(Misc, "scePowerGetBatteryFullCapacity"));
        Assert.Equal(0, Call(Misc, "scePowerIsPowerOnline"));

        clock.Advance(5_000_000);
        Assert.Equal(500, Call(Misc, "scePowerGetBatteryRemainCapacity"));
        Assert.Equal(25, Call(Misc, "scePowerGetBatteryLifePercent"));
        Assert.Equal(0, Call(Misc, "scePowerIsLowBattery"));

        clock.Advance(5_000_000);
        Assert.Equal(0, Call(Misc, "scePowerGetBatteryRemainCapacity"));
        Assert.Equal(1, Call(Misc, "scePowerIsLowBattery"));
        Assert.Equal(1, Call(Power, "NID_0D56C601"));

        // another size is refused; NULL turns it off
        GuestMemory.W32(status, 0x14);
        Assert.Equal(Invalid, Call(Power, "kscePowerSetBatteryFakeStatus", status));
        Assert.Equal(0, Call(Power, "kscePowerSetBatteryFakeStatus", 0u));
        Assert.Equal(2200, Call(Misc, "scePowerGetBatteryRemainCapacity"));
        Assert.Equal(0, Call(Misc, "scePowerIsLowBattery"));
    }

    [Fact]
    public void TheClocksAreRoundedCheckedAndShared()
    {
        Assert.Equal(0, Call(Power, "kscePowerSetDmac5ClockFrequency", 50));
        Assert.Equal(0x53, Call(Power, "kscePowerGetDmac5ClockFrequency"));
        Assert.Equal(Invalid, Call(Power, "kscePowerSetCameraBusClockFrequency", 0xA7));
        Assert.Equal(0, Call(Power, "kscePowerSetCameraBusClockFrequency", 0x44));
        Assert.Equal(0x53, Call(Power, "kscePowerGetCameraBusClockFrequency"));

        uint core = _harness.Alloc(4), second = _harness.Alloc(4);
        Assert.Equal(0, Call(Power, "kscePowerSetGpuClockFrequencyInternal", 100, 50));
        Assert.Equal(0, Call(Power, "kscePowerGetGpuClockFrequencyInternal", core, second));
        Assert.Equal(0x6Fu, GuestMemory.R32(core));
        Assert.Equal(0x37u, GuestMemory.R32(second));
        Assert.Equal(Invalid, Call(Power, "kscePowerSetGpuClockFrequencyInternal", 50, 100));
        Assert.Equal(Invalid, Call(Power, "kscePowerSetGpuClockFrequencyInternal", 0xDF, 0));

        // the bus runs at the highest clock a process asked for; the user call and the kernel one see the same clock
        Assert.Equal(0, Call(Power, "kscePowerSetBusClockFrequencyProc", 0x10, 100));
        Assert.Equal(0, Call(Power, "kscePowerSetBusClockFrequencyProc", 0x20, 200));
        Assert.Equal(200, Call(Misc, "scePowerGetBusClockFrequency"));
        Assert.Equal(100, Call(Power, "kscePowerGetBusClockFrequencyProc", 0x10));
        Assert.Equal(Invalid, Call(Power, "kscePowerSetBusClockFrequencyProc", 0x10, 223));
        Assert.Equal(0, Call(Misc, "scePowerSetBusClockFrequency", 111));
        Assert.Equal(111, Call(Misc, "scePowerGetBusClockFrequency"));
        Assert.Equal(Invalid, Call(Misc, "scePowerSetGpuXbarClockFrequency", 167));
    }

    [Fact]
    public void AnIdleTimerCountsFromItsLastTick()
    {
        using var clock = ControlledGuestClock.Stop();
        CpuState* cpu = CpuState.Allocate();
        try
        {
            var context = new HleContext { Cpu = cpu, Emu = _harness.Emu };
            uint stack = _harness.Alloc(0x10);
            cpu->R[0] = 1;
            cpu->R[1] = 0;
            cpu->R[2] = 5_000_000;
            cpu->R[3] = 0;
            cpu->R[13] = stack;
            GuestMemory.W32(stack, 0x81000001);
            GuestMemory.W32(stack + 4, 0x1234);
            Invoke(Power, "kscePowerSetIdleCallback", context);
            Assert.Equal(0u, cpu->R[0]);
            cpu->R[0] = 1;
            Invoke(Power, "kscePowerSetIdleCallback", context);
            Assert.Equal(unchecked((uint)Busy), cpu->R[0]);
            cpu->R[0] = 8;
            Invoke(Power, "kscePowerSetIdleCallback", context);
            Assert.Equal(unchecked((uint)Invalid), cpu->R[0]);
        }
        finally
        {
            CpuState.Free(cpu);
        }

        uint elapsed = _harness.Alloc(8), remaining = _harness.Alloc(8);
        clock.Advance(2_000_000);
        Assert.Equal(0, Call(Power, "kscePowerGetIdleTimer", 1u, elapsed, remaining));
        Assert.Equal(2_000_000L, GuestMemory.Read<long>(elapsed));
        Assert.Equal(3_000_000L, GuestMemory.Read<long>(remaining));

        // a tick of another timer leaves it; its own bit (or 0, all of them) starts it over
        Assert.Equal(0, Call(Power, "kscePowerTick", 1u));
        Assert.Equal(0, Call(Power, "kscePowerGetIdleTimer", 1u, elapsed, remaining));
        Assert.Equal(2_000_000L, GuestMemory.Read<long>(elapsed));
        Assert.Equal(0, Call(Power, "kscePowerTick", 2u));
        Assert.Equal(0, Call(Power, "kscePowerGetIdleTimer", 1u, elapsed, remaining));
        Assert.Equal(0L, GuestMemory.Read<long>(elapsed));
        Assert.Equal(Invalid, Call(Power, "kscePowerGetIdleTimer", 8u, elapsed, remaining));
    }

    [Fact]
    public void TheModesAndPushTimesAreKept()
    {
        Assert.Equal(2, Call(Power, "kscePowerGetPowerSwMode"));
        Assert.Equal(Invalid, Call(Power, "kscePowerSetPowerSwMode", 8u));
        Assert.Equal(0, Call(Power, "kscePowerSetPowerSwMode2", 4u));
        Assert.Equal(6, Call(Power, "kscePowerGetPowerSwMode"));
        Assert.Equal(0, Call(Power, "kscePowerSetStandbyButtonPushTime", 3_000_000u));
        Assert.Equal(3_000_000L, _harness.Emu.Vita.Power.Button.StandbyPushTimeUs);
        Assert.Equal(0, Call(Power, "kscePowerWlanActivate"));
        Assert.Equal(1, Call(Power, "NID_2B51FE2F"));
        Assert.Equal(0, Call(Power, "kscePowerBatteryEnableUsbCharging"));
        Assert.Equal(1, Call(Power, "kscePowerBatteryEnableUsbCharging"));
    }
}
