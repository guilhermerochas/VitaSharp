using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Psp;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// The PSP DMA controllers (0xBC900000 / 0xBCA00000, a PL080 register file): memory-to-memory items and linked
/// lists, widths and increments, the terminal count and error interrupts with their masks and clears, the controller
/// and halt gates, peripheral flow, and a MIPS program that copies memory and takes the end interrupt.
/// </summary>
public sealed class PspDmacTests
{
    private const uint Source = 0x08020000;
    private const uint Destination = 0x08030000;
    private const uint List = 0x08040000;

    private const uint WordToWord = (2u << 18) | (2u << 21) | PspDmac.ControlSourceIncrement | PspDmac.ControlDestinationIncrement;

    /// <summary>Enable | IE | ITC, as dmacman writes it.</summary>
    private const uint StartChannel = 0xC001;

    private sealed class Fixture
    {
        public Fixture(uint physicalBase = PspDmac.Base0, int interrupt = PspInterrupt.Dma0)
        {
            Memory = new AllegrexTestBus();
            Interrupts = new PspInterruptController();
            Interrupts.Write32(0x08, (1u << PspInterrupt.Dma0) | (1u << PspInterrupt.Dma1));
            Dmac = new PspDmac(physicalBase, interrupt, Interrupts, Memory);
        }

        public AllegrexTestBus Memory { get; }

        public PspInterruptController Interrupts { get; }

        public PspDmac Dmac { get; }

        public void Fill(uint address, int words, uint seed)
        {
            for (int i = 0; i < words; i++)
            {
                Memory.Write32(address + (uint)(i * 4), seed + (uint)i);
            }
        }

        public void Program(int channel, uint source, uint destination, uint next, uint control, uint configuration)
        {
            uint offset = PspDmac.ChannelRegisters + ((uint)channel * PspDmac.ChannelStride);
            Dmac.Write32(offset, source);
            Dmac.Write32(offset + 4, destination);
            Dmac.Write32(offset + 8, next);
            Dmac.Write32(offset + 12, control);
            Dmac.Write32(offset + 16, configuration);
        }

        public uint Channel(int channel, uint register)
        {
            return Dmac.Read32(PspDmac.ChannelRegisters + ((uint)channel * PspDmac.ChannelStride) + register);
        }
    }

    [Fact]
    public void ResetStateIsIdle()
    {
        Fixture fixture = new();
        for (uint offset = 0; offset < PspDmac.BlockSize; offset += 4)
        {
            Assert.Equal(0u, fixture.Dmac.Read32(offset));
        }

        Assert.False(fixture.Interrupts.Output);
    }

    [Fact]
    public void MemoryToMemoryItemCopiesAndRaisesTheTerminalCount()
    {
        Fixture fixture = new();
        fixture.Fill(Source, 16, 0x1000);
        fixture.Dmac.Write32(0x030, 1);
        fixture.Program(2, Source, Destination, 0, 16 | WordToWord | PspDmac.ControlTerminalCountInterrupt, StartChannel);

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(0x1000u + (uint)i, fixture.Memory.Read32(Destination + (uint)(i * 4)));
        }

        Assert.Equal(0u, fixture.Channel(2, 0x10) & (PspDmac.ConfigEnable | PspDmac.ConfigActive));
        Assert.Equal(0u, fixture.Dmac.Read32(0x01C));
        Assert.Equal(Source + 64, fixture.Channel(2, 0x00));
        Assert.Equal(Destination + 64, fixture.Channel(2, 0x04));
        Assert.Equal(0u, fixture.Channel(2, 0x0C) & PspDmac.ControlTransferSize);
        Assert.Equal(1u << 2, fixture.Dmac.Read32(0x004));
        Assert.Equal(1u << 2, fixture.Dmac.Read32(0x000));
        Assert.Equal(1u << 2, fixture.Dmac.Read32(0x014));
        Assert.True(fixture.Interrupts.Output);
        Assert.Equal(1u << PspInterrupt.Dma0, fixture.Interrupts.Read32(0x00));

        fixture.Dmac.Write32(0x008, 1u << 2);
        Assert.Equal(0u, fixture.Dmac.Read32(0x014));
        Assert.False(fixture.Interrupts.Output);
    }

    [Fact]
    public void ItemWithoutTheInterruptBitRaisesNothing()
    {
        Fixture fixture = new();
        fixture.Dmac.Write32(0x030, 1);
        fixture.Program(0, Source, Destination, 0, 4 | WordToWord, StartChannel);
        Assert.Equal(0u, fixture.Dmac.Read32(0x014));
        Assert.False(fixture.Interrupts.Output);
    }

    [Fact]
    public void MaskedTerminalCountIsRawOnly()
    {
        Fixture fixture = new();
        fixture.Dmac.Write32(0x030, 1);
        fixture.Program(1, Source, Destination, 0, 4 | WordToWord | PspDmac.ControlTerminalCountInterrupt, PspDmac.ConfigEnable);
        Assert.Equal(1u << 1, fixture.Dmac.Read32(0x014));
        Assert.Equal(0u, fixture.Dmac.Read32(0x004));
        Assert.False(fixture.Interrupts.Output);

        // unmasking it afterwards lets the pending count through
        fixture.Dmac.Write32(PspDmac.ChannelRegisters + PspDmac.ChannelStride + 0x10, PspDmac.ConfigTerminalCountMask);
        Assert.True(fixture.Interrupts.Output);
    }

    [Fact]
    public void DisabledControllerHoldsTheChannelUntilItIsEnabled()
    {
        Fixture fixture = new();
        fixture.Fill(Source, 4, 0xAA00);
        fixture.Program(0, Source, Destination, 0, 4 | WordToWord | PspDmac.ControlTerminalCountInterrupt, StartChannel);
        Assert.Equal(0u, fixture.Memory.Read32(Destination));
        Assert.Equal(1u, fixture.Dmac.Read32(0x01C));
        Assert.NotEqual(0u, fixture.Channel(0, 0x10) & PspDmac.ConfigActive);

        fixture.Dmac.Write32(0x030, 1);
        Assert.Equal(0xAA03u, fixture.Memory.Read32(Destination + 12));
        Assert.True(fixture.Interrupts.Output);
    }

    [Fact]
    public void HaltedChannelWaitsForTheHaltToClear()
    {
        Fixture fixture = new();
        fixture.Fill(Source, 4, 0x5500);
        fixture.Dmac.Write32(0x030, 1);
        fixture.Program(3, Source, Destination, 0, 4 | WordToWord, StartChannel | PspDmac.ConfigHalt);
        Assert.Equal(0u, fixture.Memory.Read32(Destination));
        fixture.Dmac.Write32(PspDmac.ChannelRegisters + (3 * PspDmac.ChannelStride) + 0x10, StartChannel);
        Assert.Equal(0x5503u, fixture.Memory.Read32(Destination + 12));
    }

    [Fact]
    public void LinkedListRunsEveryItemAndInterruptsAtTheMarkedOne()
    {
        Fixture fixture = new();
        fixture.Fill(Source, 12, 0x2000);

        // item 2: words 4..7 → Destination + 0x100; item 3: words 8..11 → Destination + 0x200 (interrupt)
        fixture.Memory.Load(List, [Source + 16, Destination + 0x100, List + 16, 4 | WordToWord]);
        fixture.Memory.Load(List + 16, [Source + 32, Destination + 0x200, 0, 4 | WordToWord | PspDmac.ControlTerminalCountInterrupt]);
        fixture.Dmac.Write32(0x030, 1);
        fixture.Program(4, Source, Destination, List, 4 | WordToWord, StartChannel);

        Assert.Equal(0x2003u, fixture.Memory.Read32(Destination + 12));
        Assert.Equal(0x2004u, fixture.Memory.Read32(Destination + 0x100));
        Assert.Equal(0x200Bu, fixture.Memory.Read32(Destination + 0x20C));
        Assert.Equal(0u, fixture.Channel(4, 0x08));
        Assert.Equal(Source + 48, fixture.Channel(4, 0x00));
        Assert.Equal(0u, fixture.Channel(4, 0x10) & PspDmac.ConfigEnable);
        Assert.Equal(1u << 4, fixture.Dmac.Read32(0x004));
    }

    [Fact]
    public void BytesPackIntoWordsAndAFixedDestinationKeepsTheLast()
    {
        Fixture fixture = new();
        fixture.Memory.Load(Source, [0x44332211, 0x88776655]);
        fixture.Dmac.Write32(0x030, 1);

        // 8 bytes in, 2 words out to an incrementing destination
        uint bytesToWords = (0u << 18) | (2u << 21) | PspDmac.ControlSourceIncrement | PspDmac.ControlDestinationIncrement;
        fixture.Program(0, Source, Destination, 0, 8 | bytesToWords, StartChannel);
        Assert.Equal(0x44332211u, fixture.Memory.Read32(Destination));
        Assert.Equal(0x88776655u, fixture.Memory.Read32(Destination + 4));

        // 2 words to one fixed word (a FIFO register): the second one stays
        uint wordsToFixed = WordToWord & ~PspDmac.ControlDestinationIncrement;
        fixture.Program(0, Source, Destination + 0x40, 0, 2 | wordsToFixed, StartChannel);
        Assert.Equal(0x88776655u, fixture.Memory.Read32(Destination + 0x40));
        Assert.Equal(0u, fixture.Memory.Read32(Destination + 0x44));

        // halfwords from a fixed source
        uint fixedHalves = (1u << 18) | (1u << 21) | PspDmac.ControlDestinationIncrement;
        fixture.Program(0, Source + 2, Destination + 0x80, 0, 3 | fixedHalves, StartChannel);
        Assert.Equal(0x44334433u, fixture.Memory.Read32(Destination + 0x80));
        Assert.Equal(0x4433u, fixture.Memory.Read16(Destination + 0x84));
    }

    [Fact]
    public void WidthAboveAWordIsAnErrorThatStopsTheChannel()
    {
        Fixture fixture = new();
        fixture.Fill(Source, 4, 0x77);
        fixture.Dmac.Write32(0x030, 1);
        fixture.Program(5, Source, Destination, 0, 4 | (3u << 18) | (2u << 21) | PspDmac.ControlTerminalCountInterrupt, StartChannel);
        Assert.Equal(0u, fixture.Memory.Read32(Destination));
        Assert.Equal(1u << 5, fixture.Dmac.Read32(0x018));
        Assert.Equal(1u << 5, fixture.Dmac.Read32(0x00C));
        Assert.Equal(0u, fixture.Dmac.Read32(0x014));
        Assert.Equal(0u, fixture.Channel(5, 0x10) & PspDmac.ConfigEnable);
        Assert.True(fixture.Interrupts.Output);
        fixture.Dmac.Write32(0x010, 1u << 5);
        Assert.False(fixture.Interrupts.Output);
    }

    [Fact]
    public void PeripheralFlowWaitsForItsPeripheral()
    {
        Fixture fixture = new();
        fixture.Fill(Source, 4, 0x99);
        fixture.Dmac.Write32(0x030, 1);
        fixture.Program(6, Source, Destination, 0, 4 | WordToWord, StartChannel | (1u << 11));
        Assert.Equal(0u, fixture.Memory.Read32(Destination));
        Assert.Equal(1u << 6, fixture.Dmac.Read32(0x01C));
    }

    [Fact]
    public void CircularListKeepsTheChannelActiveAcrossUpdatesUntilDisabled()
    {
        Fixture fixture = new();
        fixture.Memory.Load(List, [Source, Destination, List, 1 | WordToWord]);
        fixture.Dmac.Write32(0x030, 1);
        fixture.Program(7, Source, Destination, List, 1 | WordToWord, StartChannel);
        Assert.Equal(1u << 7, fixture.Dmac.Read32(0x01C));
        Assert.Equal(0, fixture.Dmac.NextEventNs);
        fixture.Dmac.Update();
        Assert.Equal(1u << 7, fixture.Dmac.Read32(0x01C));

        fixture.Dmac.Write32(PspDmac.ChannelRegisters + (7 * PspDmac.ChannelStride) + 0x10, 0);
        fixture.Dmac.Update();
        Assert.Equal(long.MaxValue, fixture.Dmac.NextEventNs);
        Assert.Equal(0u, fixture.Dmac.Read32(0x01C));
    }

    [Fact]
    public void SecondControllerRaisesItsOwnLine()
    {
        Fixture fixture = new(PspDmac.Base1, PspInterrupt.Dma1);
        fixture.Dmac.Write32(0x030, 1);
        fixture.Program(0, Source, Destination, 0, 1 | WordToWord | PspDmac.ControlTerminalCountInterrupt, StartChannel);
        Assert.Equal(1u << PspInterrupt.Dma1, fixture.Interrupts.Read32(0x00));
    }

    [Fact]
    public void RegistersKeepTheirFieldsAndStatusIsReadOnly()
    {
        Fixture fixture = new();
        fixture.Dmac.Write32(0x030, 0xFFFFFFFF);
        Assert.Equal(7u, fixture.Dmac.Read32(0x030));
        fixture.Dmac.Write32(0x034, 0xFFFFFFFF);
        Assert.Equal(0xFFFFu, fixture.Dmac.Read32(0x034));
        fixture.Dmac.Write32(0x014, 0xFF);
        fixture.Dmac.Write32(0x004, 0xFF);
        Assert.Equal(0u, fixture.Dmac.Read32(0x014));
        Assert.Equal(0u, fixture.Dmac.Read32(0x004));
    }

    /// <summary>
    /// Copies 32 words from data+0x100 to data+0x200 with channel 0 of DMAC 0, the end interrupt on, Dma0 enabled in
    /// the controller, then halt. The handler reads IntTCStatus (0xBC900004), clears it through 0xBC900008 when
    /// <paramref name="clear"/>, counts in data[0], keeps the status in data[1].
    /// </summary>
    private static PspHardwareRig DmaProgram(bool clear)
    {
        uint source = PspHardwareRig.DataBase + 0x100;
        uint destination = PspHardwareRig.DataBase + 0x200;
        uint control = 32 | WordToWord | PspDmac.ControlTerminalCountInterrupt;
        uint[] code =
        [
            Lui(T0, 0xBC90),
            Ori(T1, Zero, 1),
            Sw(T1, T0, 0x30),
            .. PspHardwareRig.LoadImmediate(T1, source & AllegrexCore.PhysicalMask),
            Sw(T1, T0, 0x100),
            .. PspHardwareRig.LoadImmediate(T1, destination & AllegrexCore.PhysicalMask),
            Sw(T1, T0, 0x104),
            Sw(Zero, T0, 0x108),
            .. PspHardwareRig.LoadImmediate(T1, control),
            Sw(T1, T0, 0x10C),
            Lui(T3, 0xBC30),
            .. PspHardwareRig.LoadImmediate(T1, 1u << PspInterrupt.Dma0),
            Sw(T1, T3, 8),
            Ori(T2, Zero, PspHardwareRig.InterruptsOn),
            Mtc0(T2, AllegrexCore.Cop0Status),
            Ori(T1, Zero, (int)StartChannel),
            Sw(T1, T0, 0x110),
            .. PspHardwareRig.IdleLoop(),
        ];
        List<uint> handler = [Lui(K0, 0xBC90), Lw(K1, K0, 4)];
        if (clear)
        {
            handler.Add(Sw(K1, K0, 8));
        }

        handler.Add(Lui(T2, (int)(PspHardwareRig.DataBase >> 16)));
        handler.Add(Lw(T1, T2, 0));
        handler.Add(Addiu(T1, T1, 1));
        handler.Add(Sw(T1, T2, 0));
        handler.Add(Sw(K1, T2, 4));
        handler.Add(Eret);
        PspHardwareRig rig = new(code, [.. handler]);
        for (int i = 0; i < 32; i++)
        {
            rig.Memory.Write32(source + (uint)(i * 4), 0xC0DE0000 | (uint)i);
        }

        return rig;
    }

    private const int T3 = 11;

    [Fact]
    public void MipsProgramCopiesMemoryAndTakesTheEndInterruptOnce()
    {
        PspHardwareRig rig = DmaProgram(clear: true);
        rig.Run(2000);
        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(0xC0DE0000u | (uint)i, rig.Memory.Read32(PspHardwareRig.DataBase + 0x200 + (uint)(i * 4)));
        }

        Assert.Equal(1u, rig.Data(0));
        Assert.Equal(1u, rig.Data(1));
        Assert.False(rig.Hardware.Interrupts.Output);
        Assert.True(rig.Core.Halted);
    }

    [Fact]
    public void WithoutTheClearTheDmaInterruptIsTakenAgainAndAgain()
    {
        PspHardwareRig rig = DmaProgram(clear: false);
        rig.Run(2000);
        Assert.True(rig.Data(0) > 1);
        Assert.False(rig.Core.Halted);
    }
}
