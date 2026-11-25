namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// The DDR memory controller (PSP 0xBD000000, 0x48 bytes): the write-buffer flush the kernel asks for before another
/// bus master reads what the CPU wrote (<c>sceDdrFlush</c>; on the Vita's compat core, the kermit driver before it
/// rings the ARM: it writes 0xF to 0x04, waits for 0x04 to read 0 and for 0x00 to read 0xF).
/// <list type="table">
/// <item><term>0x04</term><description>flush request: a write starts a flush of the selected buffers (bits 0..3);
/// reads give the flushes still in progress.</description></item>
/// <item><term>0x00</term><description>the buffers of the last flush that completed.</description></item>
/// <item><term>0x40</term><description>a status the PLL change polls: bit 8 toggles on every read.</description></item>
/// <item><term>0x20, 0x2C, 0x30</term><description>read 0, writes ignored (used at boot and by the PLL change).</description></item>
/// </list>
/// The other words keep what is written. In VitaSharp the core's writes reach memory at once, so a flush completes
/// on the write. Sources: Jpcsp <c>MMIOHandlerDdr</c> (0x04 the flush request with 4 bits, 0x40, the ignored
/// registers) and Adrenaline <c>cef/binary/main.c</c> (<c>sceKermitWait</c>: the 0x04/0x00 handshake). The meaning of
/// 0x00 is inferred from that handshake.
/// </summary>
public sealed partial class PspDdrController : IPspMmioDevice
{
    /// <summary>PSP physical 0xBD000000.</summary>
    public const uint Base = 0x1D000000;

    public const uint BlockSize = 0x48;

    public const uint FlushDoneRegister = 0x00;
    public const uint FlushRegister = 0x04;
    public const uint PllStatusRegister = 0x40;

    private readonly uint[] _words = new uint[BlockSize / 4];
    private uint _flushDone;
    private uint _pllStatus;

    public uint PhysicalBase => Base;

    public uint Size => BlockSize;

    /// <summary>The flushes requested so far.</summary>
    public int Flushes { get; private set; }

    public uint Read32(uint offset)
    {
        switch (offset)
        {
            case FlushDoneRegister:
                return _flushDone;
            case FlushRegister:
                return 0;
            case PllStatusRegister:
                uint value = _pllStatus;
                _pllStatus ^= 0x100;
                return value;
            case 0x20:
            case 0x2C:
            case 0x30:
                return 0;
            default:
                return _words[offset / 4];
        }
    }

    public void Write32(uint offset, uint value)
    {
        switch (offset)
        {
            case FlushRegister:
                _flushDone = value & 0xF;
                Flushes++;
                break;
            case FlushDoneRegister:
            case PllStatusRegister:
            case 0x20:
            case 0x2C:
            case 0x30:
                break;
            default:
                _words[offset / 4] = value;
                break;
        }
    }

    public void Reset()
    {
        Array.Clear(_words);
        _flushDone = 0;
        _pllStatus = 0;
        Flushes = 0;
    }
}
