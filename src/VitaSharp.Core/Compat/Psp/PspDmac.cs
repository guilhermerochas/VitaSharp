using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// One of the PSP's two DMA controllers (PSP 0xBC900000, channels 0..7, interrupt <see cref="PspInterrupt.Dma0"/>;
/// 0xBCA00000, channels 8..15, <see cref="PspInterrupt.Dma1"/>), the hardware of the kernel's <c>sceKernelDma*</c>
/// (dmacman). Its register file is the ARM PrimeCell PL080's (ARM DDI 0196, "PrimeCell DMA Controller (PL080)
/// Technical Reference Manual"):
/// <list type="table">
/// <item><term>0x000</term><description>IntStatus: channels with a terminal count or error interrupt (masked).</description></item>
/// <item><term>0x004 / 0x008</term><description>IntTCStatus (masked) / IntTCClear (write 1 to clear).</description></item>
/// <item><term>0x00C / 0x010</term><description>IntErrorStatus (masked) / IntErrClr.</description></item>
/// <item><term>0x014 / 0x018</term><description>RawIntTCStatus / RawIntErrorStatus.</description></item>
/// <item><term>0x01C</term><description>EnbldChns: the channels whose Enable bit is set.</description></item>
/// <item><term>0x020..0x02C</term><description>the software burst/single requests (kept; no peripheral here uses them).</description></item>
/// <item><term>0x030</term><description>Configuration: bit 0 the controller enable, bits 1..2 the AHB endianness.</description></item>
/// <item><term>0x034</term><description>Sync.</description></item>
/// <item><term>0x100 + n × 0x20</term><description>channel n: SrcAddr, DestAddr, LLI, Control, Configuration.</description></item>
/// </list>
/// Control: bits 0..11 the transfer size in source-width units, 12..14 / 15..17 the burst sizes, 18..20 / 21..23 the
/// source/destination widths (0 byte, 1 halfword, 2 word), 26 / 27 the source/destination increment, 31 the terminal
/// count interrupt of this item. Channel configuration: bit 0 Enable, 1..4 / 6..9 the peripherals, 11..13 the flow
/// control (0 memory to memory), 14 the error and 15 the terminal count interrupt masks (set = interrupt passes), 17
/// Active (read only), 18 Halt. A linked-list item in memory is {source, destination, next item, control}; the
/// transfer ends after the item whose next is 0, which clears Enable.
/// <para>
/// The PSP kernel uses it that way (uOFW <c>dmacman.c</c>: it programs SrcAddr, DestAddr, LLI, Control and last the
/// channel configuration with <c>0xC001</c> = Enable | ITC | IE, sets bit 0 of 0x030, and its interrupt handler reads
/// 0x004/0x00C and clears with 0x008/0x010); Jpcsp <c>MMIOHandlerDmac</c>/<c>DmacProcessor</c>/<c>DmacThread</c> run
/// the real kernel on the same layout and the same list format.
/// </para>
/// <para>
/// Memory-to-memory transfers run as soon as the channel is enabled with the controller enabled and the channel not
/// halted (in one go: the transfer itself takes no core time here). Transfers paced by a peripheral (flow control not
/// 0) wait for the peripheral's request; no PSP-side peripheral exists yet, so they stay active and log once. A width
/// above a word is an error: the channel stops with its error interrupt.
/// </para>
/// </summary>
public sealed partial class PspDmac : IPspMmioDevice, IPspTimedDevice
{
    /// <summary>PSP physical 0xBC900000.</summary>
    public const uint Base0 = 0x1C900000;

    /// <summary>PSP physical 0xBCA00000.</summary>
    public const uint Base1 = 0x1CA00000;

    public const uint BlockSize = 0x200;

    public const int ChannelCount = 8;

    public const uint ChannelRegisters = 0x100;

    public const uint ChannelStride = 0x20;

    public const uint ControlTransferSize = 0x00000FFF;
    public const uint ControlSourceIncrement = 1u << 26;
    public const uint ControlDestinationIncrement = 1u << 27;
    public const uint ControlTerminalCountInterrupt = 1u << 31;

    public const uint ConfigEnable = 1u << 0;
    public const uint ConfigErrorMask = 1u << 14;
    public const uint ConfigTerminalCountMask = 1u << 15;
    public const uint ConfigActive = 1u << 17;
    public const uint ConfigHalt = 1u << 18;

    /// <summary>The linked-list items one channel moves before the others get their turn (a circular list runs on
    /// over the next updates instead of hanging the core).</summary>
    public const int ItemsPerTurn = 4096;

    private readonly uint _base;
    private readonly int _interrupt;
    private readonly PspInterruptController _interrupts;
    private readonly IAllegrexBus _memory;
    private readonly Channel[] _channels = new Channel[ChannelCount];
    private readonly uint[] _softRequests = new uint[4];
    private uint _rawTerminalCount;
    private uint _rawError;
    private uint _configuration;
    private uint _sync;
    private bool _lineHigh;
    private bool _waiting;          // some channel has more list items to move
    private bool _peripheralLogged;

    /// <param name="physicalBase"><see cref="Base0"/> or <see cref="Base1"/>.</param>
    /// <param name="interrupt">Its interrupt line (<see cref="PspInterrupt.Dma0"/> / <see cref="PspInterrupt.Dma1"/>).</param>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="memory">The core's bus the transfers read and write (PSP physical addresses).</param>
    public PspDmac(uint physicalBase, int interrupt, PspInterruptController interrupts, IAllegrexBus memory)
    {
        _base = physicalBase;
        _interrupt = interrupt;
        _interrupts = interrupts;
        _memory = memory;
        Reset();
    }

    public uint PhysicalBase => _base;

    /// <summary>Called with (physical address, bytes) for every unit a transfer writes, so the core drops the decoded
    /// code it overwrote (<see cref="AllegrexCore.InvalidateCode"/>); on the core's thread, which runs the transfers.</summary>
    [field: SnapshotIgnore]
    public Action<uint, int>? CodeWritten { get; set; }

    public uint Size => BlockSize;

    public long NextEventNs => _waiting ? 0 : long.MaxValue;

    public uint Read32(uint offset)
    {
        if (offset >= ChannelRegisters)
        {
            return ReadChannel(offset - ChannelRegisters);
        }

        switch (offset)
        {
            case 0x000:
                return MaskedTerminalCount() | MaskedError();
            case 0x004:
                return MaskedTerminalCount();
            case 0x00C:
                return MaskedError();
            case 0x014:
                return _rawTerminalCount;
            case 0x018:
                return _rawError;
            case 0x01C:
                return EnabledChannels();
            case 0x020:
            case 0x024:
            case 0x028:
            case 0x02C:
                return _softRequests[(offset - 0x020) / 4];
            case 0x030:
                return _configuration;
            case 0x034:
                return _sync;
            default:
                return 0;
        }
    }

    public void Write32(uint offset, uint value)
    {
        if (offset >= ChannelRegisters)
        {
            WriteChannel(offset - ChannelRegisters, value);
            return;
        }

        switch (offset)
        {
            case 0x008:
                _rawTerminalCount &= ~(value & 0xFF);
                break;
            case 0x010:
                _rawError &= ~(value & 0xFF);
                break;
            case 0x020:
            case 0x024:
            case 0x028:
            case 0x02C:
                _softRequests[(offset - 0x020) / 4] = value & 0xFFFF;
                break;
            case 0x030:
                _configuration = value & 7;
                RunChannels();
                break;
            case 0x034:
                _sync = value & 0xFFFF;
                break;
            default:
                // the status registers are read only
                return;
        }

        UpdateLine();
    }

    public void Update()
    {
        if (_waiting)
        {
            RunChannels();
            UpdateLine();
        }
    }

    /// <summary>Controller disabled, every channel 0, no interrupt.</summary>
    public void Reset()
    {
        Array.Clear(_channels);
        Array.Clear(_softRequests);
        _rawTerminalCount = 0;
        _rawError = 0;
        _configuration = 0;
        _sync = 0;
        _waiting = false;
        _lineHigh = true;
        UpdateLine();
    }

    private uint ReadChannel(uint offset)
    {
        int index = (int)(offset / ChannelStride);
        ref Channel channel = ref _channels[index];
        switch (offset % ChannelStride)
        {
            case 0x00:
                return channel.Source;
            case 0x04:
                return channel.Destination;
            case 0x08:
                return channel.Next;
            case 0x0C:
                return channel.Control;
            case 0x10:
                uint active = (channel.Configuration & ConfigEnable) != 0 ? ConfigActive : 0;
                return channel.Configuration | active;
            default:
                return 0;
        }
    }

    private void WriteChannel(uint offset, uint value)
    {
        int index = (int)(offset / ChannelStride);
        ref Channel channel = ref _channels[index];
        switch (offset % ChannelStride)
        {
            case 0x00:
                channel.Source = value;
                break;
            case 0x04:
                channel.Destination = value;
                break;
            case 0x08:
                channel.Next = value;
                break;
            case 0x0C:
                channel.Control = value;
                break;
            case 0x10:
                // Active is read only; clearing Enable stops the channel where it is
                channel.Configuration = value & ~ConfigActive & 0x7FFFF;
                RunChannel(index);
                UpdateLine();
                break;
            default:
                break;
        }
    }

    private void RunChannels()
    {
        _waiting = false;
        for (int i = 0; i < ChannelCount; i++)
        {
            RunChannel(i);
        }
    }

    private void RunChannel(int index)
    {
        ref Channel channel = ref _channels[index];
        if ((channel.Configuration & ConfigEnable) == 0
            || (channel.Configuration & ConfigHalt) != 0
            || (_configuration & 1) == 0)
        {
            return;
        }

        uint flowControl = (channel.Configuration >> 11) & 7;
        if (flowControl != 0)
        {
            if (!_peripheralLogged)
            {
                _peripheralLogged = true;
                Log.Warn($"PSP DMAC 0x{_base | 0xA0000000:X8}: channel {index} waits for a peripheral (flow control {flowControl}), not emulated");
            }

            return;
        }

        uint bit = 1u << index;
        for (int item = 0; item < ItemsPerTurn; item++)
        {
            if (!TransferItem(ref channel))
            {
                _rawError |= bit;
                channel.Configuration &= ~ConfigEnable;
                return;
            }

            if ((channel.Control & ControlTerminalCountInterrupt) != 0)
            {
                _rawTerminalCount |= bit;
            }

            uint next = channel.Next & ~3u;
            if (next == 0)
            {
                channel.Configuration &= ~ConfigEnable;
                return;
            }

            channel.Source = _memory.Read32(next);
            channel.Destination = _memory.Read32(next + 4);
            channel.Next = _memory.Read32(next + 8);
            channel.Control = _memory.Read32(next + 12);
        }

        _waiting = true;
    }

    /// <summary>Moves the current item (the channel's registers); false on a width the controller does not have.</summary>
    private bool TransferItem(ref Channel channel)
    {
        uint control = channel.Control;
        int sourceWidth = (int)((control >> 18) & 7);
        int destinationWidth = (int)((control >> 21) & 7);
        if (sourceWidth > 2 || destinationWidth > 2)
        {
            return false;
        }

        uint units = control & ControlTransferSize;
        int sourceBytes = 1 << sourceWidth;
        int destinationBytes = 1 << destinationWidth;
        bool sourceIncrement = (control & ControlSourceIncrement) != 0;
        bool destinationIncrement = (control & ControlDestinationIncrement) != 0;
        uint source = channel.Source;
        uint destination = channel.Destination;

        // the bytes move as a stream: source units in, destination units out (little endian)
        ulong pending = 0;
        int pendingBytes = 0;
        for (uint i = 0; i < units; i++)
        {
            pending |= (ulong)ReadUnit(source, sourceWidth) << (pendingBytes * 8);
            pendingBytes += sourceBytes;
            if (sourceIncrement)
            {
                source += (uint)sourceBytes;
            }

            while (pendingBytes >= destinationBytes)
            {
                WriteUnit(destination, destinationWidth, (uint)pending);
                pending >>= destinationBytes * 8;
                pendingBytes -= destinationBytes;
                if (destinationIncrement)
                {
                    destination += (uint)destinationBytes;
                }
            }
        }

        // a tail shorter than a destination unit goes out byte by byte
        while (pendingBytes > 0)
        {
            WriteUnit(destination, 0, (uint)pending);
            pending >>= 8;
            pendingBytes--;
            if (destinationIncrement)
            {
                destination++;
            }
        }

        channel.Source = source;
        channel.Destination = destination;
        channel.Control = control & ~ControlTransferSize;
        return true;
    }

    private uint ReadUnit(uint address, int width)
    {
        switch (width)
        {
            case 0:
                return _memory.Read8(address);
            case 1:
                return _memory.Read16(address & ~1u);
            default:
                return _memory.Read32(address & ~3u);
        }
    }

    private void WriteUnit(uint address, int width, uint value)
    {
        switch (width)
        {
            case 0:
                _memory.Write8(address, (byte)value);
                CodeWritten?.Invoke(address, 1);
                break;
            case 1:
                _memory.Write16(address & ~1u, (ushort)value);
                CodeWritten?.Invoke(address & ~1u, 2);
                break;
            default:
                _memory.Write32(address & ~3u, value);
                CodeWritten?.Invoke(address & ~3u, 4);
                break;
        }
    }

    private uint MaskedTerminalCount()
    {
        uint masked = 0;
        for (int i = 0; i < ChannelCount; i++)
        {
            if ((_channels[i].Configuration & ConfigTerminalCountMask) != 0)
            {
                masked |= _rawTerminalCount & (1u << i);
            }
        }

        return masked;
    }

    private uint MaskedError()
    {
        uint masked = 0;
        for (int i = 0; i < ChannelCount; i++)
        {
            if ((_channels[i].Configuration & ConfigErrorMask) != 0)
            {
                masked |= _rawError & (1u << i);
            }
        }

        return masked;
    }

    private uint EnabledChannels()
    {
        uint enabled = 0;
        for (int i = 0; i < ChannelCount; i++)
        {
            if ((_channels[i].Configuration & ConfigEnable) != 0)
            {
                enabled |= 1u << i;
            }
        }

        return enabled;
    }

    private void UpdateLine()
    {
        bool high = (MaskedTerminalCount() | MaskedError()) != 0;
        if (high != _lineHigh)
        {
            _lineHigh = high;
            _interrupts.SetLine(_interrupt, high);
        }
    }

    private struct Channel
    {
        public uint Source;
        public uint Destination;
        public uint Next;
        public uint Control;
        public uint Configuration;
    }
}
