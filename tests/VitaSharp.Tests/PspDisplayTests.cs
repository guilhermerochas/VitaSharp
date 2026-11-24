using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Psp;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// The PSP display side of the compat core: the display controller (0xBE740000: the scan timing, the vertical blank
/// on the latched interrupt 30) and the DMACplus scan-out channel (0xBC800100); a MIPS program that takes the vblank
/// interrupts and acknowledges them.
/// </summary>
public sealed class PspDisplayTests
{
    /// <summary>One frame: 286 lines of 525 pixels at 9 MHz.</summary>
    private const long FrameNs = 286L * 525 * 1000 / 9;

    /// <summary>The first vertical blank: line 272.</summary>
    private const long FirstVblankNs = (272L * 525 * 1000 + 8) / 9;

    private static PspDisplayController Display(out PspHardwareClock clock, out PspInterruptController interrupts)
    {
        clock = new PspHardwareClock();
        interrupts = new PspInterruptController();
        interrupts.Write32(0x08, 1u << PspInterrupt.Vblank);
        return new PspDisplayController(clock, interrupts);
    }

    [Fact]
    public void TheBlankStartsAfterTheVisibleLinesEveryFrame()
    {
        PspDisplayController display = Display(out PspHardwareClock clock, out PspInterruptController interrupts);
        int started = 0;
        display.VblankStarted = () => started++;
        Assert.Equal(FirstVblankNs, display.NextEventNs);
        clock.AdvanceTo(FirstVblankNs - 1);
        display.Update();
        Assert.False(interrupts.Output);
        Assert.Equal(271u, display.Read32(0x04));
        clock.AdvanceTo(FirstVblankNs);
        display.Update();
        Assert.True(interrupts.Output);
        Assert.Equal(1u, display.Read32(0x08));
        Assert.Equal(272u, display.Read32(0x04));
        Assert.Equal(1, started);
        Assert.InRange(display.NextEventNs - FirstVblankNs, FrameNs - 1, FrameNs + 1);
    }

    [Fact]
    public void TheVblankIsLatchedUntilItsBitIsWritten()
    {
        PspDisplayController display = Display(out PspHardwareClock clock, out PspInterruptController interrupts);
        clock.AdvanceTo(FirstVblankNs);
        display.Update();
        clock.AdvanceTo(FirstVblankNs + FrameNs / 2);
        display.Update();
        Assert.True(interrupts.Output); // nothing drops a latched line but the acknowledge
        interrupts.Write32(0x00, 1u << PspInterrupt.Vblank);
        Assert.False(interrupts.Output);
    }

    [Fact]
    public void FramesTheClockJumpsOverCountButRaiseOnce()
    {
        PspDisplayController display = Display(out PspHardwareClock clock, out _);
        int started = 0;
        display.VblankStarted = () => started++;
        clock.AdvanceTo(FirstVblankNs + (3 * FrameNs) + 1000);
        display.Update();
        Assert.Equal(4u, display.Read32(0x08));
        Assert.Equal(1, started);
    }

    [Fact]
    public void WritingTheLineRegisterRestartsTheTiming()
    {
        PspDisplayController display = Display(out PspHardwareClock clock, out _);
        clock.AdvanceTo(FirstVblankNs + 5000);
        display.Update();
        display.Write32(0x04, 0);
        Assert.Equal(0u, display.Read32(0x04));
        Assert.Equal(0u, display.Read32(0x08));
        Assert.Equal(FirstVblankNs + 5000 + FirstVblankNs, display.NextEventNs);
    }

    [Fact]
    public void ControlWordsKeepTheirValueAndTheStatusReadsIdle()
    {
        PspDisplayController display = Display(out _, out _);
        display.Write32(0x00, 0x20201);
        display.Write32(0x24, 1);
        display.Write32(0x20, 0xFF);
        Assert.Equal(0x20201u, display.Read32(0x00));
        Assert.Equal(1u, display.Read32(0x24));
        Assert.Equal(0u, display.Read32(0x20));
        display.Reset();
        Assert.Equal(0u, display.Read32(0x00));
    }

    [Fact]
    public void LcdcKeepsTheScanOutRegisters()
    {
        PspLcdc lcdc = new();
        lcdc.Write32(0x00, 0x44000000);
        lcdc.Write32(0x04, 7);
        lcdc.Write32(0x08, 480);
        lcdc.Write32(0x0C, 512);
        lcdc.Write32(0x10, 0xFFFFFFFF);
        Assert.Equal(0x04000000u, lcdc.FramebufferAddress);
        Assert.Equal(3u, lcdc.PixelFormat);
        Assert.Equal(480u, lcdc.Width);
        Assert.Equal(512u, lcdc.Stride);
        Assert.Equal(3u, lcdc.Read32(0x10));
        Assert.True(lcdc.ScanningOut);
        lcdc.Reset();
        Assert.False(lcdc.ScanningOut);
        Assert.Equal(0u, lcdc.Read32(0x00));
    }

    [Fact]
    public void StandardHardwareHasTheDisplaySide()
    {
        var hardware = PspHardware.CreateStandard(new AllegrexTestBus());
        Assert.NotNull(hardware.Display);
        Assert.NotNull(hardware.Lcdc);
        Assert.True(hardware.TryWrite32(0x1C800100, 0x04000000));
        Assert.Equal(0x04000000u, hardware.Lcdc!.FramebufferAddress);
        Assert.True(hardware.TryRead32(0x1E740004, out _));
        Assert.Equal(FirstVblankNs, hardware.NextEventNs);
    }

    /// <summary>
    /// Main: enables interrupt 30, sets the scan-out channel (a framebuffer, ABGR8888, 480 of 512) and enables it, then
    /// halts. Handler: counts the vblanks in data[0], keeps the controller's frame count in data[1], acknowledges the
    /// line (1 &lt;&lt; 30 to 0xBC300000) when <paramref name="acknowledge"/>.
    /// </summary>
    private static PspHardwareRig VblankProgram(bool acknowledge)
    {
        uint[] code =
        [
            Lui(T0, 0xBC30),
            Lui(T1, 1 << (PspInterrupt.Vblank - 16)),
            Sw(T1, T0, 8),
            Lui(T0, 0xBC80),
            Lui(T1, 0x0402),
            Sw(T1, T0, 0x100),
            Ori(T1, Zero, 3),
            Sw(T1, T0, 0x104),
            Ori(T1, Zero, 480),
            Sw(T1, T0, 0x108),
            Ori(T1, Zero, 512),
            Sw(T1, T0, 0x10C),
            Ori(T1, Zero, 1),
            Sw(T1, T0, 0x110),
            Ori(T2, Zero, PspHardwareRig.InterruptsOn),
            Mtc0(T2, AllegrexCore.Cop0Status),
            .. PspHardwareRig.IdleLoop(),
        ];
        List<uint> handler = [Lui(K0, 0xBC30)];
        if (acknowledge)
        {
            handler.Add(Lui(K1, 1 << (PspInterrupt.Vblank - 16)));
            handler.Add(Sw(K1, K0, 0));
        }

        handler.Add(Lui(K0, 0xBE74));
        handler.Add(Lw(K1, K0, 8));
        handler.Add(Lui(T2, (int)(PspHardwareRig.DataBase >> 16)));
        handler.Add(Sw(K1, T2, 4));
        handler.Add(Lw(T1, T2, 0));
        handler.Add(Addiu(T1, T1, 1));
        handler.Add(Sw(T1, T2, 0));
        handler.Add(Eret);
        return new PspHardwareRig(code, [.. handler]);
    }

    [Fact]
    public void MipsProgramTakesOneVblankInterruptPerFrame()
    {
        PspHardwareRig rig = VblankProgram(acknowledge: true);
        rig.Run((FirstVblankNs + (4 * FrameNs) + 1000) / PspHardwareRig.NsPerInstruction);
        Assert.Equal(5u, rig.Data(0));
        Assert.Equal(5u, rig.Data(1));
        Assert.True(rig.Core.Halted);
        Assert.Equal(0x04020000u, rig.Hardware.Lcdc!.FramebufferAddress);
        Assert.True(rig.Hardware.Lcdc.ScanningOut);
    }

    [Fact]
    public void WithoutTheAcknowledgeTheVblankIsTakenAgainAndAgain()
    {
        PspHardwareRig rig = VblankProgram(acknowledge: false);
        rig.Run((FirstVblankNs + 100_000) / PspHardwareRig.NsPerInstruction);
        Assert.True(rig.Data(0) > 1);
        Assert.False(rig.Core.Halted);
    }
}
