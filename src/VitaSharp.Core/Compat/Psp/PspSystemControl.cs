namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// The PSP's system control registers (PSP 0xBC100000, "sysreg", 0x104 bytes): the reset, bus clock, clock and IO
/// enable bits of every block, the clock selections, the RAM size and chip version, the fuse id. The kernel's
/// <c>sceSysreg*</c> sets them while it brings each block up. Registers:
/// <list type="table">
/// <item><term>0x040</term><description>bits 0..1 the RAM size (0 16 MB, 1 32, 2 64, 3 128; written), the chip
/// (Tachyon) version from bit 8 (read only, <see cref="TachyonVersion"/>).</description></item>
/// <item><term>0x04C</term><description>the reset bits (0 TOP, 1 SC: the main CPU, 2 ME, 3 AW, 4 VME, 5 AVC, 6 USB,
/// 7 ATA, 8 MSIF0, 9 MSIF1, 10 KIRK, 12 ATA_HDD, 13 USB_HOST): set holds the block in reset. The 0→1 edge of SC
/// resets the main CPU (<see cref="CpuResetRequested"/>).</description></item>
/// <item><term>0x050 / 0x054 / 0x058</term><description>bus clock, clock-1 and clock enable bits (0x050: 0 ME,
/// 3 EDRAM, 4 DMACPLUS, 5 DMAC0, 6 DMAC1, 7 KIRK...; 0x058: SPI, UART, the APB timers, audio, SIRCS, GPIO).</description></item>
/// <item><term>0x05C..0x064</term><description>the ATA, timer and SPI clock selections.</description></item>
/// <item><term>0x068</term><description>the PLL: a write with bit 7 set selects bits 0..3; other writes are ignored.</description></item>
/// <item><term>0x070, 0x078, 0x07C</term><description>the AVC power, IO enable and GPIO enable bits.</description></item>
/// <item><term>0x080</term><description>USB and Memory Stick state: bits 0 / 8 / 16 connected (read only), bits
/// 1..4 / 9..12 / 17..20 their interrupts, write 1 to clear. On the Vita's compat core both live on the ARM side
/// (kermit), so nothing is connected here.</description></item>
/// <item><term>0x090 / 0x094 / 0x098</term><description>the fuse id (low, high) and fuse configuration, read only
/// (<see cref="FuseId"/>, <see cref="FuseConfig"/>).</description></item>
/// <item><term>0x044</term><description>the interrupt to the Media Engine; the compat core has no ME (the ME work goes
/// to the ARM side through kermit), so it reaches nothing.</description></item>
/// </list>
/// Every other word of the block keeps what is written. Reset: everything 0. Sources: Jpcsp
/// <c>MMIOHandlerSystemControl</c> (offsets, bit names, the PLL and USB/MS rules, the SC reset), uOFW <c>sysreg</c>
/// and <c>dmacman</c> (0x050 DMAC bits), psp-ghidra-scripts (HW_SYS_CONTROL, 0x104 bytes). The chip version and
/// the fuse values the Vita's compat core reports are not known: 0 until the boot trace shows them.
/// </summary>
public sealed partial class PspSystemControl : IPspMmioDevice
{
    /// <summary>PSP physical 0xBC100000.</summary>
    public const uint Base = 0x1C100000;

    public const uint BlockSize = 0x104;

    public const uint RamSizeRegister = 0x040;
    public const uint MeInterruptRegister = 0x044;
    public const uint ResetRegister = 0x04C;
    public const uint PllRegister = 0x068;
    public const uint UsbMemoryStickRegister = 0x080;
    public const uint FuseIdLowRegister = 0x090;
    public const uint FuseIdHighRegister = 0x094;
    public const uint FuseConfigRegister = 0x098;

    /// <summary>The SC bit of the reset register: the main CPU.</summary>
    public const uint ResetMainCpu = 1u << 1;

    private const uint UsbMemoryStickInterrupts = 0x001E1E1E;

    private readonly uint[] _words = new uint[BlockSize / 4];

    public uint PhysicalBase => Base;

    public uint Size => BlockSize;

    /// <summary>The chip version read in 0x040 from bit 8.</summary>
    public uint TachyonVersion { get; set; }

    /// <summary>The fuse id (0x090/0x094).</summary>
    public ulong FuseId { get; set; }

    /// <summary>The fuse configuration (0x098).</summary>
    public uint FuseConfig { get; set; }

    /// <summary>The 0→1 edge of the SC reset bit: the kernel resets the main CPU (a reboot through loadexec).
    /// Whoever runs the core resets it; the hardware state stays as it is.</summary>
    public Action? CpuResetRequested { get; set; }

    public uint Read32(uint offset)
    {
        switch (offset)
        {
            case RamSizeRegister:
                return (TachyonVersion << 8) | (_words[offset / 4] & 3);
            case FuseIdLowRegister:
                return (uint)FuseId;
            case FuseIdHighRegister:
                return (uint)(FuseId >> 32);
            case FuseConfigRegister:
                return FuseConfig;
            case MeInterruptRegister:
                return 0;
            default:
                return _words[offset / 4];
        }
    }

    public void Write32(uint offset, uint value)
    {
        uint index = offset / 4;
        switch (offset)
        {
            case RamSizeRegister:
                _words[index] = value & 3;
                break;
            case ResetRegister:
                bool resetsCpu = (_words[index] & ResetMainCpu) == 0 && (value & ResetMainCpu) != 0;
                _words[index] = value;
                if (resetsCpu)
                {
                    CpuResetRequested?.Invoke();
                }

                break;
            case PllRegister:
                if ((value & 0x80) != 0)
                {
                    _words[index] = value & 0xF;
                }

                break;
            case UsbMemoryStickRegister:
                _words[index] &= ~(value & UsbMemoryStickInterrupts);
                break;
            case MeInterruptRegister:
            case FuseIdLowRegister:
            case FuseIdHighRegister:
            case FuseConfigRegister:
                break;
            default:
                _words[index] = value;
                break;
        }
    }

    /// <summary>Every register 0 (the version and fuses are the chip's).</summary>
    public void Reset()
    {
        Array.Clear(_words);
    }
}

/// <summary>
/// The CPU and bus clock ratios (PSP 0xBC200000): 0x00 the CPU, 0x04 the bus, each a numerator in bits 16..24 and a
/// denominator in bits 0..8 of the PLL frequency (the kernel's <c>scePowerSetClockFrequency</c> path). The other bits
/// are not stored. Reset: 0. Source: Jpcsp <c>MMIOHandlerCpuBusFrequency</c>. The compat core's time keeps the
/// compat clock SceCompat sets on the ARM side (333 MHz); these ratios do not change it.
/// </summary>
public sealed partial class PspClockFrequency : IPspMmioDevice
{
    /// <summary>PSP physical 0xBC200000.</summary>
    public const uint Base = 0x1C200000;

    public const uint BlockSize = 0x8;

    private const uint FieldMask = 0x01FF01FF;

    private uint _cpu;
    private uint _bus;

    public uint PhysicalBase => Base;

    public uint Size => BlockSize;

    public uint Read32(uint offset)
    {
        return offset == 0 ? _cpu : _bus;
    }

    public void Write32(uint offset, uint value)
    {
        if (offset == 0)
        {
            _cpu = value & FieldMask;
        }
        else
        {
            _bus = value & FieldMask;
        }
    }

    public void Reset()
    {
        _cpu = 0;
        _bus = 0;
    }
}
