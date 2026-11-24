using VitaSharp.Core.Compat.Ge;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The GE's register block and display list processor (docs/notes/psp-compat.md, "The GE"; registers from uofw's
/// hardware.h): EXEC, list and stall addresses, CALL/RET with two levels, JUMP with BASE and the origin, SIGNAL /
/// FINISH / END and the interrupt line through the accepted mask, the command and matrix registers, the EDRAM block.
/// The lists are built by the tests.
/// </summary>
public sealed class GeListProcessorTests
{
    private const uint ListAddress = 0x08001000;

    private readonly GeTestMemory _memory = new();
    private readonly GeEngine _engine;
    private readonly List<bool> _levels = [];

    public GeListProcessorTests()
    {
        _engine = new GeEngine(_memory);
        _engine.InterruptLevel = _levels.Add;
        _engine.Write32(GeRegisters.InterruptEnable, GeEngine.InterruptSignal | GeEngine.InterruptEnd | GeEngine.InterruptFinish);
    }

    private GeListBuilder List(uint start = ListAddress)
    {
        return new GeListBuilder(_memory, start);
    }

    [Fact]
    public void AListRunsToEndAndRaisesTheInterruptUntilAcknowledged()
    {
        GeListBuilder list = List().Command(GeCommand.Nop).Command(GeCommand.VertexType, 0x123).End();
        GeRegisters.Run(_engine, list.Start);

        Assert.Equal(0u, _engine.Read32(GeRegisters.Exec) & GeEngine.ExecRunning);
        Assert.Equal(list.Position, _engine.Read32(GeRegisters.List));
        Assert.Equal(GeEngine.InterruptFinish | GeEngine.InterruptEnd, _engine.Read32(GeRegisters.InterruptStatus));
        Assert.Equal([true], _levels);
        Assert.Equal(0x12000123u, _engine.Read32(GeRegisters.Base + 0x800 + GeCommand.VertexType * 4));

        _engine.Write32(GeRegisters.InterruptAcknowledge, GeEngine.InterruptEnd | GeEngine.InterruptFinish);
        Assert.Equal(0u, _engine.Read32(GeRegisters.InterruptStatus));
        Assert.Equal([true, false], _levels);
    }

    [Fact]
    public void AStatusBitOutsideTheAcceptedMaskDoesNotRaiseTheLine()
    {
        _engine.Write32(GeRegisters.InterruptEnable, GeEngine.InterruptSignal);
        GeRegisters.Run(_engine, List().Command(GeCommand.Finish).Command(GeCommand.End).Start);
        Assert.Equal(GeEngine.InterruptFinish | GeEngine.InterruptEnd, _engine.Read32(GeRegisters.InterruptStatus));
        Assert.Empty(_levels);
        GeRegisters.Run(_engine, List(0x08002000).Command(GeCommand.Signal, 0x01_0000).Command(GeCommand.End).Start);
        Assert.Equal([true], _levels);
    }

    /// <summary>The GE waits at the stall address with EXEC still set, and goes on when the stall address moves.</summary>
    [Fact]
    public void AListWaitsAtTheStallAddressAndGoesOnWhenItMoves()
    {
        GeListBuilder list = List().Command(GeCommand.VertexType, 1);
        uint stall = list.Position;
        list.Command(GeCommand.VertexType, 2).End();
        GeRegisters.Run(_engine, list.Start, stall);

        Assert.Equal(GeEngine.ExecRunning, _engine.Read32(GeRegisters.Exec) & GeEngine.ExecRunning);
        Assert.Equal(stall, _engine.Read32(GeRegisters.List));
        Assert.Equal(1u, _engine.Command(GeCommand.VertexType) & 0xFFFFFF);
        Assert.Empty(_levels);

        _engine.Write32(GeRegisters.Stall, 0);
        Assert.Equal(0u, _engine.Read32(GeRegisters.Exec) & GeEngine.ExecRunning);
        Assert.Equal(2u, _engine.Command(GeCommand.VertexType) & 0xFFFFFF);
        Assert.Equal([true], _levels);
    }

    /// <summary>JUMP and CALL go to BASE's bits 24..27 above the argument plus the origin; CALL keeps two levels
    /// (RADR1/2, EXEC bits 8 and 9) and RET comes back; a third CALL stops the list with the error status.</summary>
    [Fact]
    public void CallKeepsTwoLevelsAndJumpUsesBaseAndTheOrigin()
    {
        uint inner = 0x08003000;
        uint middle = 0x08002000;
        List(inner).Command(GeCommand.VertexType, 0x30).Command(GeCommand.Return);
        GeListBuilder middleList = List(middle).Command(GeCommand.VertexType, 0x20).Command(GeCommand.Call, inner & 0xFFFFFF);
        uint middleReturn = middleList.Position;
        middleList.Command(GeCommand.Return);
        GeListBuilder list = List().RamBase().Command(GeCommand.Call, middle & 0xFFFFFF);
        uint mainReturn = list.Position;
        list.Command(GeCommand.OffsetAddress, 0x000010) // origin 0x1000: the jump lands 0x1000 further
            .Command(GeCommand.Jump, 0x003000);
        List(0x08004000).Command(GeCommand.VertexType, 0x40).End();

        uint seenDepth;
        List(inner).Command(GeCommand.VertexType, 0x30).Command(GeCommand.Signal).Command(GeCommand.End);
        GeRegisters.Run(_engine, list.Start);
        seenDepth = _engine.Read32(GeRegisters.Exec);
        Assert.Equal(GeEngine.ExecDepth1 | GeEngine.ExecDepth2, seenDepth & (GeEngine.ExecDepth1 | GeEngine.ExecDepth2));
        Assert.Equal(mainReturn, _engine.Read32(GeRegisters.Return1));
        Assert.Equal(middleReturn, _engine.Read32(GeRegisters.Return2));

        // the inner list again with its RET: both levels come back, then the jump
        List(inner).Command(GeCommand.VertexType, 0x30).Command(GeCommand.Return);
        _engine.Write32(GeRegisters.InterruptAcknowledge, 0xF);
        _engine.Write32(GeRegisters.List, inner + 4);
        _engine.Write32(GeRegisters.Exec, GeEngine.ExecRunning);
        Assert.Equal(0x40u, _engine.Command(GeCommand.VertexType) & 0xFFFFFF);
        Assert.Equal(0u, _engine.Read32(GeRegisters.Exec));
        Assert.Equal(GeEngine.InterruptFinish | GeEngine.InterruptEnd, _engine.Read32(GeRegisters.InterruptStatus));
    }

    [Fact]
    public void AThirdCallAndAReturnWithoutCallStopWithTheErrorStatus()
    {
        uint level2 = 0x08003000;
        List(level2).Command(GeCommand.Call, 0x003800);
        List(0x08002000).Command(GeCommand.Call, level2 & 0xFFFFFF);
        GeRegisters.Run(_engine, List().RamBase().Command(GeCommand.Call, 0x002000).Start);
        Assert.Equal(GeEngine.InterruptError, _engine.Read32(GeRegisters.InterruptStatus) & GeEngine.InterruptError);
        Assert.Equal(0u, _engine.Read32(GeRegisters.Exec) & GeEngine.ExecRunning);

        GeEngine fresh = new(_memory);
        GeRegisters.Run(fresh, List(0x08005000).Command(GeCommand.Return).Start);
        Assert.Equal(GeEngine.InterruptError, fresh.Read32(GeRegisters.InterruptStatus));
    }

    /// <summary>ORIGIN makes the origin the command's own address; VADDR/IADDR are relative like JUMP.</summary>
    [Fact]
    public void OriginAndTheVertexAddresses()
    {
        GeListBuilder list = List().Command(GeCommand.Origin); // BASE 0: relative to the list itself
        uint origin = list.Start;
        list.Command(GeCommand.VertexAddress, 0x000100).Command(GeCommand.IndexAddress, 0x000200).End();
        GeRegisters.Run(_engine, list.Start);
        Assert.Equal(origin, _engine.Read32(GeRegisters.Origin));
        Assert.Equal(origin + 0x100, _engine.Read32(GeRegisters.Vertex));
        Assert.Equal(origin + 0x200, _engine.Read32(GeRegisters.Index));
    }

    /// <summary>PRIM moves the vertex address past its vertices (indexed: the index address past its indices).</summary>
    [Fact]
    public void APrimitiveMovesTheVertexOrIndexAddress()
    {
        const uint ColorAndFloatPosition = (7u << 2) | (3u << 7); // 4 + 12 bytes
        GeListBuilder list = List().RamBase().Command(GeCommand.VertexType, ColorAndFloatPosition)
            .Command(GeCommand.VertexAddress, 0x010000).Command(GeCommand.Primitive, (3u << 16) | 3).End();
        GeRegisters.Run(_engine, list.Start);
        Assert.Equal(0x08010000u + 3 * 16, _engine.Read32(GeRegisters.Vertex));

        GeListBuilder indexed = List(0x08002000).RamBase().Command(GeCommand.VertexType, ColorAndFloatPosition | (2u << 11))
            .Command(GeCommand.VertexAddress, 0x010000).Command(GeCommand.IndexAddress, 0x020000)
            .Command(GeCommand.Primitive, (3u << 16) | 6).End();
        GeRegisters.Run(_engine, indexed.Start);
        Assert.Equal(0x08010000u, _engine.Read32(GeRegisters.Vertex));
        Assert.Equal(0x08020000u + 6 * 2, _engine.Read32(GeRegisters.Index));
    }

    [Fact]
    public void TheMatricesLoadByIndexAndReadAsTheirTopBits()
    {
        GeListBuilder list = List().Command(GeCommand.WorldMatrixNumber, 0);
        for (int i = 0; i < 12; i++)
        {
            list.Float(GeCommand.WorldMatrixData, i + 1);
        }

        list.Command(GeCommand.ProjectionMatrixNumber, 15).Float(GeCommand.ProjectionMatrixData, -2.5f).End();
        GeRegisters.Run(_engine, list.Start);
        Assert.Equal(12f, _engine.WorldMatrix[11]);
        Assert.Equal(-2.5f, _engine.ProjectionMatrix[15]);
        Assert.Equal(BitConverter.SingleToUInt32Bits(3f) >> 8, _engine.Read32(GeRegisters.Base + 0xD80 + 2 * 4));
        Assert.Equal(BitConverter.SingleToUInt32Bits(-2.5f) >> 8, _engine.Read32(GeRegisters.Base + 0xDE0 + 15 * 4));
    }

    [Fact]
    public void TheEdramBlockFinishesItsInitialisationAtOnce()
    {
        Assert.Equal(GeEngine.VramSizeKb, _engine.Read32(GeRegisters.Base + 0x008));
        _engine.Write32(GeEngine.EdramPhysicalBase + 0x10, 1);
        Assert.Equal(0u, _engine.Read32(GeEngine.EdramPhysicalBase + 0x10));
        _engine.Write32(GeEngine.EdramPhysicalBase + 0x20, 0x6C4);
        Assert.Equal(0x6C4u, _engine.Read32(GeEngine.EdramPhysicalBase + 0x20));
    }

    [Theory]
    [InlineData((3u << 7), 12)] // float position
    [InlineData((2u << 7) | 2u, 10)] // u16 texture + s16 position
    [InlineData((4u << 2) | (3u << 7), 16)] // 565 colour, padding, float position
    [InlineData(3u | (7u << 2) | (3u << 7), 24)] // float texture, 8888 colour, float position
    [InlineData((1u << 7) | (1u << 9) | (2u << 14), 6)] // 3 u8 weights + s8 position
    [InlineData((3u << 7) | (1u << 18), 24)] // two morph targets of a float position
    public void VertexSizesFollowTheLayoutRules(uint vertexType, int size)
    {
        Assert.Equal(size, new GeVertexFormat(vertexType).Size);
    }
}
