using VitaSharp.Core.Compat;
using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Psp;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// The PSP interrupt controller (0xBC300000): the three register groups, level and latched sources, the
/// acknowledge, the reset, and a MIPS handler that takes IP2, reads the pending word and acknowledges it.
/// </summary>
public sealed class PspInterruptControllerTests
{
    private static PspInterruptController Controller(out List<bool> outputs)
    {
        PspInterruptController controller = new();
        List<bool> seen = [];
        controller.OutputChanged = seen.Add;
        outputs = seen;
        return controller;
    }

    [Fact]
    public void ResetStateHasNothingEnabledOrPending()
    {
        PspInterruptController controller = Controller(out _);
        for (uint offset = 0; offset < PspInterruptController.BlockSize; offset += 4)
        {
            Assert.Equal(0u, controller.Read32(offset));
        }

        Assert.False(controller.Output);
    }

    [Fact]
    public void LowGroupMaskedPendingRawAndEnable()
    {
        PspInterruptController controller = Controller(out List<bool> outputs);
        controller.SetLine(PspInterrupt.SystemTimer0, true);
        controller.SetLine(PspInterrupt.Dma0, true);
        Assert.Equal(0u, controller.Read32(0x00));
        Assert.Equal((1u << 15) | (1u << 22), controller.Read32(0x04));
        Assert.False(controller.Output);

        controller.Write32(0x08, 1u << 22);
        Assert.Equal(1u << 22, controller.Read32(0x00));
        Assert.Equal(1u << 22, controller.Read32(0x08));
        Assert.True(controller.Output);

        controller.SetLine(PspInterrupt.Dma0, false);
        Assert.False(controller.Output);
        Assert.Equal([true, false], outputs);
    }

    [Fact]
    public void EnableWordKeepsTheBitsTheKernelAlwaysSets()
    {
        // interruptman writes 0x2000000F | mask; the word reads back as written
        PspInterruptController controller = Controller(out _);
        controller.Write32(0x08, 0x2000000F | (1u << 19));
        Assert.Equal(0x2000000F | (1u << 19), controller.Read32(0x08));
    }

    [Fact]
    public void HighGroupLeavesLines38_39_46_47ToTheThirdGroup()
    {
        PspInterruptController controller = Controller(out _);
        controller.Write32(0x18, 0xFFFFFFFF);
        Assert.Equal(PspInterruptController.HighGroupBits, controller.Read32(0x18));
        Assert.Equal(0u, controller.Read32(0x28));
        Assert.Equal(PspInterruptController.HighGroupBits, (uint)(controller.Enabled >> 32));

        controller.Write32(0x28, 0xF);
        Assert.Equal(0xFu, controller.Read32(0x28));
        Assert.Equal(0xFFFFFFFFu, (uint)(controller.Enabled >> 32));
        Assert.Equal(PspInterruptController.HighGroupBits, controller.Read32(0x18));

        // a write of 0x18 does not touch the compact lines, and 0x28 does not touch the others
        controller.Write32(0x18, 0);
        Assert.Equal(0xFu, controller.Read32(0x28));
        controller.Write32(0x18, 1u << (PspInterrupt.HpRemote - 32));
        controller.Write32(0x28, 0);
        Assert.Equal(1u << (PspInterrupt.HpRemote - 32), controller.Read32(0x18));
    }

    [Theory]
    [InlineData(38, 0)]
    [InlineData(39, 1)]
    [InlineData(46, 2)]
    [InlineData(47, 3)]
    public void CompactLineReadsInTheThirdGroup(int line, int bit)
    {
        PspInterruptController controller = Controller(out _);
        controller.SetLine(line, true);
        Assert.Equal(0u, controller.Read32(0x14));
        Assert.Equal(1u << bit, controller.Read32(0x24));
        Assert.Equal(0u, controller.Read32(0x20));
        controller.Write32(0x28, 1u << bit);
        Assert.Equal(1u << bit, controller.Read32(0x20));
        Assert.Equal(0u, controller.Read32(0x10));
        Assert.True(controller.Output);
    }

    [Fact]
    public void HighLineReadsInTheHighGroup()
    {
        PspInterruptController controller = Controller(out _);
        controller.SetLine(PspInterrupt.MemoryStick1, true);
        uint bit = 1u << (PspInterrupt.MemoryStick1 - 32);
        Assert.Equal(bit, controller.Read32(0x14));
        controller.Write32(0x18, bit);
        Assert.Equal(bit, controller.Read32(0x10));
        Assert.True(controller.Output);
    }

    [Fact]
    public void LatchedSourceStaysUntilItsBitIsWrittenToTheFirstRegister()
    {
        PspInterruptController controller = Controller(out _);
        controller.Write32(0x08, 1u << PspInterrupt.Vblank);
        controller.Trigger(PspInterrupt.Vblank);
        Assert.True(controller.Output);
        controller.Write32(0x00, 1u << PspInterrupt.MeCodec);
        Assert.True(controller.Output);
        controller.Write32(0x00, 1u << PspInterrupt.Vblank);
        Assert.False(controller.Output);
        Assert.Equal(0u, controller.Read32(0x04));
    }

    [Fact]
    public void AcknowledgeDoesNotClearALineItsDeviceStillDrives()
    {
        PspInterruptController controller = Controller(out _);
        controller.Write32(0x08, 1u << PspInterrupt.SystemTimer1);
        controller.SetLine(PspInterrupt.SystemTimer1, true);
        controller.Write32(0x00, 0xFFFFFFFF);
        Assert.True(controller.Output);
        Assert.Equal(1u << PspInterrupt.SystemTimer1, controller.Read32(0x00));
    }

    [Fact]
    public void RawRegistersAreReadOnly()
    {
        PspInterruptController controller = Controller(out _);
        controller.Write32(0x04, 0xFFFFFFFF);
        controller.Write32(0x14, 0xFFFFFFFF);
        controller.Write32(0x24, 0xFFFFFFFF);
        Assert.Equal(0u, controller.Pending);
    }

    [Fact]
    public void ExternalLevelHoldsTheOutputAlone()
    {
        PspInterruptController controller = Controller(out List<bool> outputs);
        controller.SetExternalLevel(true);
        Assert.True(controller.Output);
        controller.Write32(0x08, 1u << 4);
        controller.SetLine(4, true);
        controller.SetExternalLevel(false);
        Assert.True(controller.Output);
        controller.SetLine(4, false);
        Assert.Equal([true, false], outputs);
    }

    [Fact]
    public void ResetDisablesEverythingAndDropsTheOutput()
    {
        PspInterruptController controller = Controller(out _);
        controller.Write32(0x08, 1u << PspInterrupt.Vblank);
        controller.Trigger(PspInterrupt.Vblank);
        controller.Reset();
        Assert.False(controller.Output);
        Assert.Equal(0u, controller.Read32(0x08));
        Assert.Equal(0u, controller.Read32(0x04));
    }

    [Fact]
    public void BusReachesTheControllerBehindRamAndSram()
    {
        PspHardware hardware = new();
        PspBus bus = new(new CompatSharedSram(), new CompatMailbox()) { Hardware = hardware };
        bus.Write32(0xBC300008, 1u << 22);
        Assert.Equal(1u << 22, bus.Read32(0xBC300008));
        Assert.Equal(1u << 22, bus.Read32(0x1C300008));
        Assert.Equal(0x40u, bus.Read8(0xBC30000A));
        Assert.Equal(0, bus.UnknownAccesses);
        bus.Read32(0xBC300030);
        Assert.Equal(1, bus.UnknownAccesses);
    }

    /// <summary>
    /// The handler: k1 = masked pending (0xBC300000); writes it back (the acknowledge of latched lines); counts the
    /// interrupts in data[0] and keeps the pending word in data[1]; eret.
    /// </summary>
    private static uint[] CountingHandler(bool acknowledge)
    {
        List<uint> handler = [Lui(K0, 0xBC30), Lw(K1, K0, 0)];
        if (acknowledge)
        {
            handler.Add(Sw(K1, K0, 0));
        }

        handler.Add(Lui(T2, (int)(PspHardwareRig.DataBase >> 16)));
        handler.Add(Lw(T1, T2, 0));
        handler.Add(Addiu(T1, T1, 1));
        handler.Add(Sw(T1, T2, 0));
        handler.Add(Sw(K1, T2, 4));
        handler.Add(Eret);
        return [.. handler];
    }

    private static uint[] EnableAndIdle(uint enableMask)
    {
        return
        [
            Lui(T0, 0xBC30),
            .. PspHardwareRig.LoadImmediate(T1, enableMask),
            Sw(T1, T0, 8),
            Ori(T2, Zero, PspHardwareRig.InterruptsOn),
            Mtc0(T2, AllegrexCore.Cop0Status),
            .. PspHardwareRig.IdleLoop(),
        ];
    }

    [Fact]
    public void MipsHandlerTakesTheInterruptReadsItAndAcknowledgesIt()
    {
        PspHardwareRig rig = new(EnableAndIdle(1u << PspInterrupt.Vblank), CountingHandler(acknowledge: true));
        rig.Run(100);
        Assert.True(rig.Core.Halted);
        Assert.Equal(0u, rig.Data(0));

        rig.Hardware.Interrupts.Trigger(PspInterrupt.Vblank);
        rig.Run(100);
        Assert.Equal(1u, rig.Data(0));
        Assert.Equal(1u << PspInterrupt.Vblank, rig.Data(1));
        Assert.False(rig.Hardware.Interrupts.Output);
        Assert.True(rig.Core.Halted);

        rig.Hardware.Interrupts.Trigger(PspInterrupt.Vblank);
        rig.Run(100);
        Assert.Equal(2u, rig.Data(0));
    }

    [Fact]
    public void WithoutTheAcknowledgeTheInterruptIsTakenAgainAndAgain()
    {
        PspHardwareRig rig = new(EnableAndIdle(1u << PspInterrupt.Vblank), CountingHandler(acknowledge: false));
        rig.Run(100);
        rig.Hardware.Interrupts.Trigger(PspInterrupt.Vblank);
        rig.Run(200);
        Assert.True(rig.Data(0) > 1);
        Assert.False(rig.Core.Halted);
    }

    [Fact]
    public void DisabledLineDoesNotInterruptTheCore()
    {
        PspHardwareRig rig = new(EnableAndIdle(1u << PspInterrupt.MeCodec), CountingHandler(acknowledge: true));
        rig.Run(100);
        rig.Hardware.Interrupts.Trigger(PspInterrupt.Vblank);
        rig.Run(100);
        Assert.Equal(0u, rig.Data(0));
        Assert.True(rig.Core.Halted);
    }
}
