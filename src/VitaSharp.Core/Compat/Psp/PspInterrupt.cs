namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// The PSP's interrupt numbers (the bits of <see cref="PspInterruptController"/>: 0..31 the low group, 32..63 the high
/// one), as the PSP kernel registers its handlers (<c>sceKernelRegisterIntrHandler</c>). Numbers 64..67 are not
/// controller lines but the CPU's own Cause bits (uOFW interruptman: 64 = IP0, 65 = IP1, 66 = IP4, 67 = IP7, the
/// COP0 Count/Compare timer). Source: Jpcsp <c>IntrManager</c> (the numbers its LLE mode runs the real PSP kernel
/// with) and uOFW. Which of them the compat core of the Vita still has is fixed by the boot trace (phase 3 of
/// docs/notes/psp-compat.md): the kermit drivers replace the ones whose hardware is on the ARM side.
/// </summary>
public static class PspInterrupt
{
    public const int Gpio = 4;
    public const int Ata = 5;
    public const int Umd = 6;
    public const int MemoryStick0 = 7;
    public const int Wlan = 8;
    public const int Audio = 10;
    public const int I2c = 12;
    public const int Sircs = 14;

    /// <summary>The four hardware timers (<see cref="PspHardwareTimers"/>): <see cref="SystemTimer0"/> + timer.</summary>
    public const int SystemTimer0 = 15;
    public const int SystemTimer1 = 16;
    public const int SystemTimer2 = 17;
    public const int SystemTimer3 = 18;

    /// <summary>The system time's alarm (<see cref="PspSystemTime"/>), the thread manager's timer.</summary>
    public const int Thread0 = 19;
    public const int Nand = 20;
    public const int DmacPlus = 21;

    /// <summary>DMA controller 0 (0xBC900000, channels 0..7) and 1 (0xBCA00000, channels 8..15).</summary>
    public const int Dma0 = 22;
    public const int Dma1 = 23;
    public const int Memlmd = 24;
    public const int Ge = 25;
    public const int EFlashAta2 = 27;
    public const int EFlashDma = 28;

    /// <summary>The display's vertical blank (latched: acknowledged by writing its bit to 0xBC300000).</summary>
    public const int Vblank = 30;

    /// <summary>The Media Engine codec (latched like <see cref="Vblank"/>).</summary>
    public const int MeCodec = 31;
    public const int HpRemote = 36;
    public const int MemoryStick1 = 60;
    public const int MemoryStick2 = 61;

    /// <summary>The controller's lines.</summary>
    public const int Count = 64;
}
