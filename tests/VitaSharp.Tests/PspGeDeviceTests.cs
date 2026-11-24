using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Ge;
using VitaSharp.Core.Compat.Psp;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// The GE on the core's bus (0xBD400000 and its EDRAM controller 0xBD500000) and its line, interrupt 25: the routing,
/// the reset, and a MIPS program that starts a short display list by MMIO and takes the end-of-list interrupt.
/// </summary>
public sealed class PspGeDeviceTests
{
    private const uint ListAddress = PspHardwareRig.DataBase + 0x400;
    private const uint EndAndFinish = GeEngine.InterruptEnd | GeEngine.InterruptFinish;

    [Fact]
    public void StandardHardwareRoutesBothGeRegionsToOneEngine()
    {
        PspTestBus bus = new(new AllegrexTestBus());
        var hardware = PspHardware.CreateStandard(bus);
        Assert.NotNull(hardware.Ge);
        Assert.True(hardware.TryRead32(0x1D400008, out uint edramSize));
        Assert.Equal(GeEngine.VramSizeKb, edramSize);
        Assert.True(hardware.TryWrite32(0x1D400108, 0x08010400));
        Assert.Equal(0x08010400u, hardware.Ge!.ListAddress);
        Assert.True(hardware.TryWrite32(0x1D500020, 0x1234));
        Assert.True(hardware.TryRead32(0x1D500020, out uint edramWord));
        Assert.Equal(0x1234u, edramWord);
        Assert.False(hardware.TryRead32(0x1D401000, out _));
    }

    [Fact]
    public void BusWithoutGeMemoryHasNoGe()
    {
        var hardware = PspHardware.CreateStandard(new AllegrexTestBus());
        Assert.Null(hardware.Ge);
        Assert.False(hardware.TryRead32(0x1D400008, out _));
    }

    [Fact]
    public void EndOfListRaisesInterrupt25AndTheResetDropsIt()
    {
        PspHardwareRig rig = new([.. PspHardwareRig.IdleLoop()]);
        rig.Memory.Load(ListAddress, [(uint)GeCommand.Finish << 24, (uint)GeCommand.End << 24]);
        rig.Hardware.Interrupts.Write32(0x08, 1u << PspInterrupt.Ge);
        rig.Bus.Write32(0xBD400308, EndAndFinish);
        rig.Bus.Write32(0xBD400108, ListAddress & AllegrexCore.PhysicalMask);
        rig.Bus.Write32(0xBD400100, GeEngine.ExecRunning);
        Assert.Equal(1u << PspInterrupt.Ge, rig.Hardware.Interrupts.Read32(0x00));
        Assert.True(rig.Hardware.Interrupts.Output);

        rig.Hardware.Reset();
        Assert.False(rig.Hardware.Interrupts.Output);
        Assert.Equal(0u, rig.Bus.Read32(0xBD400304));
        Assert.Equal(0u, rig.Bus.Read32(0xBD400308));
    }

    /// <summary>
    /// Main: enables interrupt 25 in the controller, accepts END and FINISH in the GE (+0x308), sets the list address
    /// (+0x108) to a list of FINISH, END and starts it (EXEC = 1), then halts. Handler: reads the GE's interrupt status
    /// (+0x304), acknowledges it (+0x310) when <paramref name="acknowledge"/>, reads EXEC, counts in data[0], keeps the
    /// status in data[1] and EXEC in data[2].
    /// </summary>
    private static PspHardwareRig ListProgram(bool acknowledge)
    {
        uint[] code =
        [
            Lui(T0, 0xBC30),
            Lui(T1, 1 << (PspInterrupt.Ge - 16)),
            Sw(T1, T0, 8),
            Lui(T0, 0xBD40),
            Ori(T1, Zero, (int)EndAndFinish),
            Sw(T1, T0, 0x308),
            .. PspHardwareRig.LoadImmediate(T1, ListAddress & AllegrexCore.PhysicalMask),
            Sw(T1, T0, 0x108),
            Sw(Zero, T0, 0x10C),
            Ori(T2, Zero, PspHardwareRig.InterruptsOn),
            Mtc0(T2, AllegrexCore.Cop0Status),
            Ori(T1, Zero, (int)GeEngine.ExecRunning),
            Sw(T1, T0, 0x100),
            .. PspHardwareRig.IdleLoop(),
        ];
        List<uint> handler = [Lui(K0, 0xBD40), Lw(K1, K0, 0x304)];
        if (acknowledge)
        {
            handler.Add(Sw(K1, K0, 0x310));
        }

        handler.Add(Lw(T1, K0, 0x100));
        handler.Add(Lui(T2, (int)(PspHardwareRig.DataBase >> 16)));
        handler.Add(Sw(K1, T2, 4));
        handler.Add(Sw(T1, T2, 8));
        handler.Add(Lw(T1, T2, 0));
        handler.Add(Addiu(T1, T1, 1));
        handler.Add(Sw(T1, T2, 0));
        handler.Add(Eret);
        PspHardwareRig rig = new(code, [.. handler]);
        rig.Memory.Load(ListAddress, [(uint)GeCommand.Finish << 24, (uint)GeCommand.End << 24]);
        return rig;
    }

    [Fact]
    public void MipsProgramRunsAListAndTakesTheEndOfListInterruptOnce()
    {
        PspHardwareRig rig = ListProgram(acknowledge: true);
        rig.Run(2000);
        Assert.Equal(1u, rig.Data(0));
        Assert.Equal(EndAndFinish, rig.Data(1));
        Assert.Equal(0u, rig.Data(2) & GeEngine.ExecRunning);
        Assert.False(rig.Hardware.Ge!.Running);
        Assert.False(rig.Hardware.Interrupts.Output);
        Assert.True(rig.Core.Halted);
    }

    [Fact]
    public void WithoutTheAcknowledgeTheGeInterruptIsTakenAgainAndAgain()
    {
        PspHardwareRig rig = ListProgram(acknowledge: false);
        rig.Run(2000);
        Assert.True(rig.Data(0) > 1);
        Assert.False(rig.Core.Halted);
    }
}
