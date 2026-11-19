using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCtrlForDriver's external-controller and system part (SceCtrl 3.74): the entries of the virtual controller
/// driver answer 0x80340021 while none registered (always on the handheld: SceDs3 is loaded by the PS TV's
/// psp2config only), and the bridge's vshCtrl* forms answer what the kernel answers (before, the bridge answered 0
/// for the active port and kept the settings itself); the notify callbacks, the system sample hooks, the kept values
/// and port 0's analog and button emulation, merged into the samples for the given number of vertical blanks.
/// </summary>
[Collection("guest")]
public sealed class CtrlKernelDriverTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80340001), NoDriver = unchecked((int)0x80340021);
    private const int NoSlot = unchecked((int)0x803400FF), Perm = unchecked((int)0x80010058);

    private readonly HleHarness _harness = new(fixture, "ctrld");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private VshBridge Bridge => _harness.Module<VshBridge>();

    [Theory]
    [InlineData(0x02081494u)]
    [InlineData(0x4CCB2153u)]
    [InlineData(0x5B708A15u)]
    [InlineData(0x69E45836u)]
    [InlineData(0xB26C2231u)]
    [InlineData(0xC8CE3E43u)]
    [InlineData(0xDA07E970u)]
    [InlineData(0xFF9D4DD4u)]
    [InlineData(0x53381364u)]
    [InlineData(0xFA88D837u)]
    [InlineData(0x6E183B9Bu)]
    [InlineData(0xE170883Eu)]
    [InlineData(0x18823EEAu)]
    [InlineData(0x06577FE8u)]
    [InlineData(0x1E750326u)]
    [InlineData(0x7AD9467Eu)]
    [InlineData(0xED8F0129u)]
    [InlineData(0x4D8FE670u)]
    [InlineData(0x9DAB51C4u)]
    public void EveryEntryIsBound(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceCtrlForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void WithoutAVirtualDriverTheExternalControllerEntriesAnswerNoDriver()
    {
        Assert.Equal(NoDriver, Call(Services, "ksceCtrlGetActiveControllerPort"));
        Assert.Equal(NoDriver, Call(Services, "ksceCtrlChangePortAssign", 0u, 1u));
        Assert.Equal(NoDriver, Call(Services, "ksceCtrlSetTurnOffInterval", 600u));
        Assert.Equal(NoDriver, Call(Services, "CtrlDriverEntry1C", 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceCtrlRegisterVirtualControllerDriver", 0u));
    }

    [Fact]
    public void TheBridgeAnswersWhatTheKernelAnswers()
    {
        Assert.Equal(Perm, Call(Bridge, "vshCtrlGetActiveControllerPort"));
        Assert.Equal(Perm, Call(Bridge, "vshCtrlChangePortAssign", 0u, 1u));
        Assert.Equal(Perm, Call(Bridge, "vshCtrlSetVibrationSetting", 1));
        Assert.Equal(Perm, Call(Bridge, "vshCtrlSetTurnOffInterval", 600u));
        Assert.Equal(Perm, Call(Bridge, "vshCtrlRegisterNotifyCallBack", 1));
        Assert.Equal(Perm, Call(Bridge, "vshCtrlUnregisterNotifyCallBack", 1));
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(NoDriver, Call(Bridge, "vshCtrlGetActiveControllerPort"));
        Assert.Equal(NoDriver, Call(Bridge, "vshCtrlChangePortAssign", 0u, 1u));
        Assert.Equal(NoDriver, Call(Bridge, "vshCtrlSetTurnOffInterval", 600u));
        Assert.Equal(0, Call(Bridge, "vshCtrlSetVibrationSetting", 1));
        Assert.Equal(1, Services.CtrlKernelValues.Vibration);
        Assert.Equal(SceError.INVALID_UID | 0x40000000, Call(Bridge, "vshCtrlRegisterNotifyCallBack", 0x7FFF0001));
    }

    [Fact]
    public void TheNotifyCallbacksTakeSlotsAndAreNotified()
    {
        int callback = 0;
        ThreadMgr threadmgr = _harness.Module<ThreadMgr>();
        _harness.OnGuest(_ =>
        {
            callback = Call(threadmgr, "sceKernelCreateCallback", "ctrl-cb", 0u, 0x81000001u, 0u);
        });
        Assert.True(callback > 0);
        Callback target = _harness.Emu.Kernel.Objects.Get<Callback>(callback)!;

        Assert.Equal(0, Call(Services, "ksceCtrlRegisterNotifyCallBack", callback));
        Assert.Equal(0, Call(Services, "ksceCtrlNotifyCallBacks", 7));
        Assert.Equal(1, target.NotifyCount);
        Assert.Equal(7, target.NotifyArg);

        Assert.Equal(0, Call(Services, "ksceCtrlUnregisterNotifyCallBack", callback));
        Assert.Equal(0, Call(Services, "ksceCtrlNotifyCallBacks", 8));
        Assert.Equal(7, target.NotifyArg);

        // a full table keeps nothing and still answers 0; a handle the kernel cannot notify loses its slot
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(0, Call(Services, "ksceCtrlRegisterNotifyCallBack", 0x7FFF0001));
        }

        Assert.Equal(0, Call(Services, "ksceCtrlRegisterNotifyCallBack", callback));
        Assert.Equal(0, Call(Services, "ksceCtrlNotifyCallBacks", 9));
        Assert.Equal(7, target.NotifyArg);
        Assert.Equal(0, Call(Services, "ksceCtrlRegisterNotifyCallBack", callback));
        Assert.Equal(0, Call(Services, "ksceCtrlNotifyCallBacks", 10));
        Assert.Equal(10, target.NotifyArg);
    }

    [Fact]
    public void TheSystemSampleHooksTakeFourSlots()
    {
        uint buttons = _harness.Alloc(4);
        Assert.Equal(Invalid, Call(Services, "CtrlAddSampleHook", 1u, 0u, 0u, buttons));
        for (int slot = 0; slot < 4; slot++)
        {
            Assert.Equal(slot, Call(Services, "CtrlAddSampleHook", 1u, 0x81000101u, 2u, buttons));
        }

        GuestMemory.W32(buttons, 0xFFFFFFFFu);
        Assert.Equal(NoSlot, Call(Services, "CtrlAddSampleHook", 1u, 0x81000101u, 2u, buttons));
        Assert.Equal(0u, GuestMemory.R32(buttons)); // the buttons are written back even when full
        Assert.Equal(Invalid, Call(Services, "CtrlRemoveSampleHook", 4u));
        Assert.Equal(0, Call(Services, "CtrlRemoveSampleHook", 2u));
        Assert.Equal(2, Call(Services, "CtrlAddSampleHook", 1u, 0x81000101u, 2u, 0u));
    }

    [Fact]
    public void TheKeptValues()
    {
        uint pid = _harness.Alloc(4);
        Assert.Equal(Invalid, Call(Services, "CtrlGetForegroundProcess", 0u));
        Assert.Equal(0, Call(Services, "CtrlGetForegroundProcess", pid));
        Assert.Equal(uint.MaxValue, GuestMemory.R32(pid));
        Assert.Equal(0, Call(Services, "CtrlSetForegroundProcess", 0x10005));
        Assert.Equal(0, Call(Services, "CtrlGetForegroundProcess", pid));
        Assert.Equal(0x10005u, GuestMemory.R32(pid));

        Assert.Equal(Invalid, Call(Services, "ksceCtrlSetCtrlpCallback", 0u));
        Assert.Equal(0, Call(Services, "ksceCtrlSetCtrlpCallback", 0x81000201u));
        Assert.Equal(0, Call(Services, "CtrlSetCtrlpProcess", 0x10005, 0));
        Assert.Equal(-1, Services.CtrlKernelValues.CtrlpProcess);
        Assert.Equal(0, Call(Services, "CtrlSetCtrlpProcess", 0x10005, 1));
        Assert.Equal(0x10005, Services.CtrlKernelValues.CtrlpProcess);
        Assert.Equal(1, Services.CtrlKernelValues.StartMode);
        Assert.Equal(0, Call(Services, "CtrlSetStartMode", 0x100u));
        Assert.Equal(0, Services.CtrlKernelValues.StartMode);
        Assert.Equal(0, Call(Services, "CtrlSetLowLevelConfig", 0u, 0x43u, 1));
        Assert.Equal(0x10000043u, Services.CtrlKernelValues.LowLevel);
    }

    [Fact]
    public void TheEmulationChecksPortAndSlot()
    {
        Assert.Equal(Invalid, Call(Services, "ksceCtrlSetButtonEmulation", 1, 0u, 1u, 0u, 1));
        Assert.Equal(Invalid, Call(Services, "ksceCtrlSetButtonEmulation", 0, 4u, 1u, 0u, 1));
        Assert.Equal(Invalid, Call(Services, "ksceCtrlSetAnalogEmulation", 0, 4u, 0x80u, 0x80u, 0x80u, 0x80u, 0x80u, 0x80u,
            0x80u, 0x80u, 1));
    }

    [Fact]
    public void TheEmulationLastsItsSamples()
    {
        // the slots count their samples from the vertical blank they are set at, and the blank count follows the guest
        // clock: stopped, every set below starts at `now`, never at the next blank the host reached between the calls
        // (a slow runner crossed one, so the analog slot was still active at now + 2)
        using var clock = ControlledGuestClock.Stop();
        long now = VBlank.Count;
        Assert.Equal(0, Call(Services, "ksceCtrlSetButtonEmulation", 0, 0u, 0x10u, 0x20u, 3));
        Assert.Equal(0, Call(Services, "ksceCtrlSetButtonEmulation", 0, 1u, 0x4000u, 0u, 1));
        Assert.Equal(0, Call(Services, "ksceCtrlSetAnalogEmulation", 0, 0u, 0xFFu, 0x70u, 0x80u, 0x80u, 0x80u, 0x80u,
            0x80u, 0x80u, 2));
        Assert.Equal(0, Call(Services, "ksceCtrlSetAnalogEmulation", 0, 1u, 0xFFu, 0x80u, 0x00u, 0x80u, 0x80u, 0x80u,
            0x80u, 0x80u, 2));

        (uint buttons, byte leftX, byte leftY, byte rightX) = Apply(now);
        Assert.Equal(0x4011u, buttons); // the host's 1, the user buttons of both slots, never the kernel's 0x20
        Assert.Equal(255, leftX); // 0x80 + 0x7F + 0x7F, saturated
        Assert.Equal(0x70, leftY);
        Assert.Equal(0, rightX);

        (buttons, leftX, _, _) = Apply(now + 1);
        Assert.Equal(0x11u, buttons);
        Assert.Equal(255, leftX);

        (buttons, leftX, leftY, _) = Apply(now + 2);
        Assert.Equal(0x11u, buttons);
        Assert.Equal(0x80, leftX);
        Assert.Equal(0x80, leftY);

        (buttons, _, _, _) = Apply(now + 3);
        Assert.Equal(0x1u, buttons);
    }

    [Fact]
    public void ThePeekOfPort0CarriesTheEmulatedButtons()
    {
        Ctrl ctrl = _harness.Module<Ctrl>();
        uint data = _harness.Alloc(32);
        Assert.Equal(1, Call(ctrl, "sceCtrlPeekBufferPositive", 0, data, 1u));
        Assert.Equal(0u, GuestMemory.R32(data + 8) & 0x10u);
        Assert.Equal(0, Call(Services, "ksceCtrlSetButtonEmulation", 0, 0u, 0x10u, 0u, 1000));
        Assert.Equal(1, Call(ctrl, "sceCtrlPeekBufferPositive", 0, data, 1u));
        Assert.Equal(0x10u, GuestMemory.R32(data + 8) & 0x10u);
        Assert.Equal(1, Call(ctrl, "sceCtrlPeekBufferPositive", 2, data, 1u)); // port 1 is another controller
        Assert.Equal(0u, GuestMemory.R32(data + 8) & 0x10u);
    }

    private (uint Buttons, byte LeftX, byte LeftY, byte RightX) Apply(long vcount)
    {
        uint buttons = 1;
        byte leftX = 0x80;
        byte leftY = 0x80;
        byte rightX = 0x80;
        byte rightY = 0x80;
        Services.CtrlApplyEmulation(vcount, ref buttons, ref leftX, ref leftY, ref rightX, ref rightY);
        return (buttons, leftX, leftY, rightX);
    }
}
