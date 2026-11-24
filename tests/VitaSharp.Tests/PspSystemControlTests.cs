using VitaSharp.Core.Compat.Psp;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>The PSP system control registers (0xBC100000) and the CPU/bus clock ratios (0xBC200000).</summary>
public sealed class PspSystemControlTests
{
    [Fact]
    public void ResetStateIsZero()
    {
        PspSystemControl sysreg = new();
        for (uint offset = 0; offset < PspSystemControl.BlockSize; offset += 4)
        {
            Assert.Equal(0u, sysreg.Read32(offset));
        }
    }

    [Theory]
    [InlineData(0x04Cu)]
    [InlineData(0x050u)]
    [InlineData(0x054u)]
    [InlineData(0x058u)]
    [InlineData(0x060u)]
    [InlineData(0x078u)]
    [InlineData(0x07Cu)]
    [InlineData(0x100u)]
    public void EnableAndClockRegistersKeepWhatIsWritten(uint offset)
    {
        PspSystemControl sysreg = new();
        sysreg.Write32(offset, 0x0001E0F0);
        Assert.Equal(0x0001E0F0u, sysreg.Read32(offset));
        sysreg.Reset();
        Assert.Equal(0u, sysreg.Read32(offset));
    }

    [Fact]
    public void RamSizeIsTwoBitsBesideTheChipVersion()
    {
        PspSystemControl sysreg = new() { TachyonVersion = 0x12 };
        sysreg.Write32(PspSystemControl.RamSizeRegister, 0xFFFFFFFE);
        Assert.Equal((0x12u << 8) | 2u, sysreg.Read32(PspSystemControl.RamSizeRegister));
    }

    [Fact]
    public void PllTakesOnlyWritesWithBitSeven()
    {
        PspSystemControl sysreg = new();
        sysreg.Write32(PspSystemControl.PllRegister, 0x85);
        Assert.Equal(5u, sysreg.Read32(PspSystemControl.PllRegister));
        sysreg.Write32(PspSystemControl.PllRegister, 0x03);
        Assert.Equal(5u, sysreg.Read32(PspSystemControl.PllRegister));
    }

    [Fact]
    public void FuseRegistersAreReadOnly()
    {
        PspSystemControl sysreg = new() { FuseId = 0x0000123489ABCDEF, FuseConfig = 0x2400 };
        sysreg.Write32(PspSystemControl.FuseIdLowRegister, 0);
        Assert.Equal(0x89ABCDEFu, sysreg.Read32(PspSystemControl.FuseIdLowRegister));
        Assert.Equal(0x1234u, sysreg.Read32(PspSystemControl.FuseIdHighRegister));
        Assert.Equal(0x2400u, sysreg.Read32(PspSystemControl.FuseConfigRegister));
    }

    [Fact]
    public void UsbMemoryStickRegisterHasNothingConnectedAndClearsByWritingOnes()
    {
        PspSystemControl sysreg = new();
        sysreg.Write32(PspSystemControl.UsbMemoryStickRegister, 0xFFFFFFFF);
        Assert.Equal(0u, sysreg.Read32(PspSystemControl.UsbMemoryStickRegister));
    }

    [Fact]
    public void RisingMainCpuResetBitAsksForACpuReset()
    {
        PspSystemControl sysreg = new();
        int resets = 0;
        sysreg.CpuResetRequested = () => resets++;
        sysreg.Write32(PspSystemControl.ResetRegister, 1u << 2);
        Assert.Equal(0, resets);
        sysreg.Write32(PspSystemControl.ResetRegister, PspSystemControl.ResetMainCpu | (1u << 2));
        Assert.Equal(1, resets);
        sysreg.Write32(PspSystemControl.ResetRegister, PspSystemControl.ResetMainCpu);
        Assert.Equal(1, resets);
        sysreg.Write32(PspSystemControl.ResetRegister, 0);
        sysreg.Write32(PspSystemControl.ResetRegister, PspSystemControl.ResetMainCpu);
        Assert.Equal(2, resets);
    }

    [Fact]
    public void ClockRatiosKeepTheirNineBitFields()
    {
        PspClockFrequency clocks = new();
        clocks.Write32(0x0, 0xFFFFFFFF);
        clocks.Write32(0x4, 0x00990001);
        Assert.Equal(0x01FF01FFu, clocks.Read32(0x0));
        Assert.Equal(0x00990001u, clocks.Read32(0x4));
        clocks.Reset();
        Assert.Equal(0u, clocks.Read32(0x0));
    }

    [Fact]
    public void MipsProgramBringsABlockUpThroughTheSystemControlRegisters()
    {
        // the dmacman pattern: read-modify-write of the bus clock enable (DMAC0 = bit 5), then read it back
        uint[] code =
        [
            Lui(T0, 0xBC10),
            Lw(T1, T0, 0x50),
            Ori(T1, T1, 1 << 5),
            Sw(T1, T0, 0x50),
            Lw(T2, T0, 0x50),
            Lui(T0, (int)(PspHardwareRig.DataBase >> 16)),
            Sw(T2, T0, 0),
            .. PspHardwareRig.IdleLoop(),
        ];
        PspHardwareRig rig = new(code);
        rig.Run(100);
        Assert.Equal(1u << 5, rig.Data(0));
    }
}
