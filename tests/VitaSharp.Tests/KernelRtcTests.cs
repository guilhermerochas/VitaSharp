using System.Runtime.CompilerServices;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceRtcForDriver (FW 3.74 rtc.skprx): every export of the census bound, the clocks (set, read, not set), the
/// configuration's time zone in the local conversions (kernel and user), the syscon alarm ringing in guest time
/// through the single scheduler and notifying its callback, the secure alarm, and the calendar exports' errors as
/// the module answers them. The modules are reached by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class KernelRtcTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80251000), Null = unchecked((int)0x80251001);
    private const int NotSet = unchecked((int)0x80251002), Registered = unchecked((int)0x80251003);
    private const int BadYear = unchecked((int)0x80251081), BadMonth = unchecked((int)0x80251082);
    private const int BadDay = unchecked((int)0x80251083), BadHour = unchecked((int)0x80251084);

    private readonly HleHarness _harness = new(fixture, "krtc");

    public void Dispose() => _harness.Dispose();

    private HleModule Module(string name) => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == name);

    private HleModule Kernel => Module("RtcKernel");

    private HleModule User => Module("Rtc");

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

    private uint Tick(ulong value)
    {
        uint address = _harness.Alloc(8);
        GuestMemory.Write(address, value);
        return address;
    }

    private int CreateCallback()
    {
        ThreadMgr threadmgr = _harness.Module<ThreadMgr>();
        int uid = 0;
        _harness.OnGuest(_ => uid = Call(threadmgr, "sceKernelCreateCallback", "rtc-cb", 0u, 0x81000001u, 0u));
        Assert.True(uid > 0, $"0x{uid:X8}");
        return uid;
    }

    private Callback CallbackObject(int uid) => _harness.Emu.Kernel.Objects.Get<Callback>(uid)!;

    [Fact]
    public void EveryRtcExportOfTheCensusIsBound()
    {
        string tsv = Path.Combine(RepoRoot(), "docs", "hle", "data", "kernel-exports.tsv");
        var census = File.ReadLines(tsv).Select(line => line.Split('\t'))
            .Where(parts => parts.Length > 3 && parts[0] == "SceRtcForDriver")
            .Select(parts => Convert.ToUInt32(parts[1], 16)).ToList();
        Assert.Equal(65, census.Count);
        Assert.All(census, nid => Assert.True(_harness.Emu.Hle.ImplOf(nid) != null, $"0x{nid:X8} has no implementation"));
    }

    [Fact]
    public void ASetClockRunsFromItsTickAndTheUserSideSeesIt()
    {
        using var clock = ControlledGuestClock.Stop();
        const ulong Set = 63_900_000_000_000_000UL;
        Assert.Equal(0, Call(Kernel, "ksceRtcSetCurrentTick", Tick(Set)));
        clock.Advance(1_500_000);
        uint read = _harness.Alloc(8);
        Assert.Equal(0, Call(User, "sceRtcGetCurrentTick", new Ptr<ulong>(read)));
        Assert.Equal(Set + 1_500_000, GuestMemory.Read<ulong>(read));

        // the chip's counter started again and the tick is in its scratchpad
        uint stored = _harness.Alloc(8);
        Assert.Equal(0, Call(Module("SysconKernel"), "ksceSysconReadCommand", 0x10u, stored, 8u));
        Assert.Equal(Set, GuestMemory.Read<ulong>(stored));

        Assert.Equal(0, Call(Kernel, "ksceRtcSetCurrentSecureTick", Tick(Set + 10)));
        Assert.Equal(0, Call(Kernel, "ksceRtcGetCurrentSecureTick", read));
        Assert.Equal(Set + 10, GuestMemory.Read<ulong>(read));
        Assert.Equal(Null, Call(Kernel, "ksceRtcGetCurrentSecureTick", 0u));

        Assert.Equal(0, Call(Kernel, "ksceRtcSetCurrentNetworkTick", Tick(Set + 20)));
        Assert.Equal(0, Call(User, "sceRtcGetCurrentNetworkTick", new Ptr<ulong>(read)));
        Assert.Equal(Set + 20, GuestMemory.Read<ulong>(read));

        // the module's reset: no clock is set any more
        Assert.Equal(0, Call(Kernel, "NID_ABA035B7"));
        Assert.Equal(NotSet, Call(Kernel, "ksceRtcGetCurrentSecureTick", read));
        Assert.Equal(NotSet, Call(User, "sceRtcGetCurrentTick", new Ptr<ulong>(read)));
    }

    [Fact]
    public void TheConfigurationsTimeZoneMakesTheLocalTime()
    {
        Assert.Equal(Invalid, Call(Kernel, "ksceRtcSetConf", 3u, 0u, 0, 0u));
        Assert.Equal(Invalid, Call(Kernel, "ksceRtcSetConf", 0u, 0u, 1440, 0u));
        Assert.Equal(0, Call(Kernel, "ksceRtcSetConf", 0u, 0u, -180, 60u));
        const ulong Utc = 63_900_000_000_000_000UL;
        uint local = _harness.Alloc(8), back = _harness.Alloc(8);
        Assert.Equal(0, Call(Kernel, "ksceRtcConvertUtcToLocal", Tick(Utc), local));
        Assert.Equal(Utc - 120UL * 60_000_000, GuestMemory.Read<ulong>(local));
        Assert.Equal(0, Call(User, "sceRtcConvertUtcToLocalTime", new Ptr<ulong>(Tick(Utc)), new Ptr<ulong>(back)));
        Assert.Equal(Utc - 120UL * 60_000_000, GuestMemory.Read<ulong>(back));
        Assert.Equal(0, Call(User, "sceRtcConvertLocalTimeToUtc", new Ptr<ulong>(local), new Ptr<ulong>(back)));
        Assert.Equal(Utc, GuestMemory.Read<ulong>(back));
    }

    [Fact]
    public void TheAlarmRingsAtItsTimeAndNotifiesItsCallback()
    {
        using var clock = ControlledGuestClock.Stop();
        int uid = CreateCallback();
        Assert.Equal(0, Call(Kernel, "ksceRtcRegisterCallback", uid));
        Callback callback = CallbackObject(uid);
        Assert.Equal(1, callback.NotifyCount);
        Assert.Equal(0, callback.NotifyArg);
        Assert.Equal(Registered, Call(Kernel, "ksceRtcRegisterCallback", uid));
        callback.NotifyCount = 0;

        uint now = _harness.Alloc(8);
        Assert.Equal(0, Call(User, "sceRtcGetCurrentTick", new Ptr<ulong>(now)));
        ulong alarm = GuestMemory.Read<ulong>(now) + 2_000_000;
        Assert.Equal(Invalid, Call(Kernel, "ksceRtcSetAlarmTick", Tick(alarm - 3_000_000), 0u));
        Assert.Equal(0, Call(Kernel, "ksceRtcSetAlarmTick", Tick(alarm), 7u));
        uint read = _harness.Alloc(8), flag = _harness.Alloc(4);
        Assert.Equal(0, Call(Kernel, "ksceRtcGetAlarmTick", read, flag));
        Assert.Equal(alarm, GuestMemory.Read<ulong>(read));
        Assert.Equal(7u, GuestMemory.R32(flag));
        Assert.Equal(0, Call(Kernel, "ksceRtcIsAlarmed"));

        clock.Advance(1_999_999);
        Assert.Equal(0, Call(Kernel, "ksceRtcIsAlarmed"));
        Assert.Equal(0, callback.NotifyCount);

        // the scheduler rings it at its time (the dispatcher thread; the wait has its own bound)
        clock.Advance(1);
        Until(() => callback.NotifyCount == 1, "the alarm's callback");
        Assert.Equal(1, callback.NotifyArg);
        Assert.Equal(1, Call(Kernel, "ksceRtcIsAlarmed"));
        Assert.Equal(1, Call(Module("SysconKernel"), "NID_86BAAF7D")); // the chip's alarm bit

        // it rings once; clearing it lowers the chip's bit
        Assert.Equal(0, Call(Kernel, "ksceRtcSetAlarmTick", 0u, 0u));
        Assert.Equal(0, Call(Kernel, "ksceRtcIsAlarmed"));
        Assert.Equal(0, Call(Module("SysconKernel"), "NID_86BAAF7D"));
        Assert.Equal(1, callback.NotifyCount);
        Assert.Equal(0, Call(Kernel, "ksceRtcUnregisterCallback", uid));
        Assert.Equal(unchecked((int)0x80251004), Call(Kernel, "ksceRtcUnregisterCallback", uid));
    }

    [Fact]
    public void TheSecureAlarmFollowsTheSecureClock()
    {
        using var clock = ControlledGuestClock.Stop();
        int uid = CreateCallback();
        Assert.Equal(0, Call(Kernel, "ksceRtcRegisterSecureAlarmCallback", uid));
        Callback callback = CallbackObject(uid);
        callback.NotifyCount = 0;

        uint now = _harness.Alloc(8);
        Assert.Equal(0, Call(Kernel, "ksceRtcGetCurrentSecureTick", now));
        ulong alarm = GuestMemory.Read<ulong>(now) + 1_000_000;
        Assert.Equal(0, Call(Kernel, "ksceRtcSetSecureAlarmTick", Tick(alarm)));
        Assert.Equal(0, Call(Kernel, "ksceRtcIsSecureAlarmed"));
        clock.Advance(1_000_000);
        Until(() => callback.NotifyArg == 1 && callback.NotifyCount >= 2, "the secure alarm's callback");
        Assert.Equal(1, Call(Kernel, "ksceRtcIsSecureAlarmed"));

        Assert.Equal(0, Call(Kernel, "NID_ABA035B7"));
        Assert.Equal(NotSet, Call(Kernel, "ksceRtcIsSecureAlarmed"));
        Assert.Equal(NotSet, Call(Kernel, "ksceRtcSetSecureAlarmTick", Tick(alarm)));
    }

    [Fact]
    public void TheCalendarExportsAnswerTheModulesErrors()
    {
        Assert.Equal(BadYear, Call(User, "sceRtcIsLeapYear", 0));
        Assert.Equal(1, Call(User, "sceRtcIsLeapYear", 2000));
        Assert.Equal(0, Call(User, "sceRtcIsLeapYear", 1900));
        Assert.Equal(BadMonth, Call(User, "sceRtcGetDaysInMonth", 2024, 13));
        Assert.Equal(29, Call(User, "sceRtcGetDaysInMonth", 2024, 2));
        Assert.Equal(BadDay, Call(User, "sceRtcGetDayOfWeek", 2024, 2, 30));
        Assert.Equal(BadYear, Call(User, "sceRtcGetDayOfWeek", 0, 1, 1));
        Assert.Equal(1, Call(User, "sceRtcGetDayOfWeek", 2026, 11, 16)); // a Monday

        uint date = _harness.Alloc(16);
        GuestMemory.Write(date, (ushort)2026);
        GuestMemory.Write(date + 2, (ushort)11);
        GuestMemory.Write(date + 4, (ushort)20);
        GuestMemory.Write(date + 6, (ushort)24);
        Assert.Equal(BadHour, Call(User, "sceRtcCheckValid", date));
        GuestMemory.Write(date + 6, (ushort)23);
        Assert.Equal(0, Call(User, "sceRtcCheckValid", date));
        GuestMemory.Write(date + 2, (ushort)0);
        Assert.Equal(BadMonth, Call(User, "sceRtcCheckValid", date));
    }
}
