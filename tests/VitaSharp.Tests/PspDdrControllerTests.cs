using VitaSharp.Core.Compat.Psp;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>The DDR controller (0xBD000000): the flush handshake the kermit driver waits on.</summary>
public sealed class PspDdrControllerTests
{
    [Fact]
    public void FlushCompletesOnTheWrite()
    {
        PspDdrController ddr = new();
        Assert.Equal(0u, ddr.Read32(PspDdrController.FlushDoneRegister));
        ddr.Write32(PspDdrController.FlushRegister, 0xF);
        Assert.Equal(0u, ddr.Read32(PspDdrController.FlushRegister));
        Assert.Equal(0xFu, ddr.Read32(PspDdrController.FlushDoneRegister));
        Assert.Equal(1, ddr.Flushes);
        ddr.Write32(PspDdrController.FlushRegister, 0x14);
        Assert.Equal(4u, ddr.Read32(PspDdrController.FlushDoneRegister));
    }

    [Fact]
    public void PllStatusTogglesOnEveryRead()
    {
        PspDdrController ddr = new();
        Assert.Equal(0u, ddr.Read32(PspDdrController.PllStatusRegister));
        Assert.Equal(0x100u, ddr.Read32(PspDdrController.PllStatusRegister));
        Assert.Equal(0u, ddr.Read32(PspDdrController.PllStatusRegister));
    }

    [Fact]
    public void IgnoredRegistersReadZeroAndOthersKeepTheirValue()
    {
        PspDdrController ddr = new();
        ddr.Write32(0x20, 5);
        ddr.Write32(0x30, 5);
        ddr.Write32(0x24, 7);
        Assert.Equal(0u, ddr.Read32(0x20));
        Assert.Equal(0u, ddr.Read32(0x30));
        Assert.Equal(7u, ddr.Read32(0x24));
        ddr.Reset();
        Assert.Equal(0u, ddr.Read32(0x24));
    }

    [Fact]
    public void MipsKermitWaitFinishes()
    {
        // sceKermitWait: sync; 0xBD000004 = 0xF; while (0xBD000004 != 0); while (0xBD000000 != 0xF); then data[0] = 1
        uint[] code =
        [
            Lui(T0, 0xBD00),
            Ori(T1, Zero, 0xF),
            Sw(T1, T0, 4),
            Lw(T2, T0, 4),
            Bne(T2, Zero, -2),
            Nop,
            Lw(T2, T0, 0),
            Bne(T2, T1, -2),
            Nop,
            Lui(T0, (int)(PspHardwareRig.DataBase >> 16)),
            Ori(T1, Zero, 1),
            Sw(T1, T0, 0),
            .. PspHardwareRig.IdleLoop(),
        ];
        PspHardwareRig rig = new(code);
        rig.Run(200);
        Assert.Equal(1u, rig.Data(0));
        Assert.True(rig.Core.Halted);
    }
}
