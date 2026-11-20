using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceDmacmgrForDriver DMA operations (dmacmgr.skprx 3.74): Alloc, Assign a controller, SetupChain/SetupDirect/
/// Concatenate the blocks ([0] source, [1] destination, [2] length, [3] control, [6] burst sizes, [7] next or -1), EnQueue
/// runs the transfer (a copy, or a fill with the options' fill word when the control has 0xC), the completion sets the
/// event flag, marks it done and calls the callback (op, status, argument, error block, bytes); Sync polls or waits; the
/// argument checks and state errors of each entry, and the crypto engine (DMAC5) refused at the queue.
/// </summary>
[Collection("guest")]
public sealed class DmacmgrOpsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80020005), InvalidSize = unchecked((int)0x80020009);
    private const int NotQueued = unchecked((int)0x80027202), NoChain = unchecked((int)0x80027203);
    private const int Secure = unchecked((int)0x80020004), NoTranslation = unchecked((int)0x80022007);
    private const int EventFlagCondition = unchecked((int)0x800280E3);
    private const uint End = 0xFFFFFFFF;

    private readonly HleHarness _harness = new(fixture, "dmaop");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private uint Block(uint source, uint destination, uint length, uint control = 0x3000, uint next = End, uint burst = 0)
    {
        uint block = _harness.Alloc(0x20);
        GuestMemory.W32(block, source);
        GuestMemory.W32(block + 4, destination);
        GuestMemory.W32(block + 8, length);
        GuestMemory.W32(block + 0xC, control);
        GuestMemory.W32(block + 0x18, burst);
        GuestMemory.W32(block + 0x1C, next);
        return block;
    }

    private uint Options(uint fill = 0, uint size = 0xC)
    {
        uint options = _harness.Alloc(0x60);
        GuestMemory.W32(options, size);
        GuestMemory.W32(options + 4, 0x3E600);
        GuestMemory.W32(options + 8, fill);
        return options;
    }

    private uint Pattern(uint size, byte seed)
    {
        uint buffer = _harness.Alloc(size);
        for (uint i = 0; i < size; i++)
        {
            GuestMemory.Write(buffer + i, (byte)(seed + i));
        }

        return buffer;
    }

    private uint NewOperation(uint kind = 0x10, uint channel = 0)
    {
        uint handle = (uint)Call(Services, "ksceKernelDmaOpAlloc", _harness.Str("dma-test"));
        Assert.Equal(1u, handle & 1);
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpAssign", handle, kind, channel));
        return handle;
    }

    [Theory]
    [InlineData(0x7CD5088Au)]
    [InlineData(0xFCE4171Au)]
    [InlineData(0xA4454DEAu)]
    [InlineData(0x7433F70Fu)]
    [InlineData(0x543F54CFu)]
    [InlineData(0xADFF1186u)]
    [InlineData(0x03052D0Du)]
    [InlineData(0xCF627CFDu)]
    [InlineData(0x167079FCu)]
    [InlineData(0x01A599E0u)]
    [InlineData(0x397A917Cu)]
    public void EveryOperationEntryHasABinding(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceDmacmgrForDriver").ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void AChainCopiesFromTheSourceToTheDestination()
    {
        uint source = Pattern(0x300, 7);
        uint destination = _harness.Alloc(0x300);
        uint second = Block(source + 0x100, destination + 0x100, 0x200);
        uint first = Block(source, destination, 0x100, next: second);
        uint op = NewOperation();
        uint status = _harness.Word(0xDEAD);
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpSetupChain", op, first, Options(), 0x11u));
        Assert.Equal(1, Call(Services, "ksceKernelDmaOpSync", op, 1u, 0u, status));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpEnQueue", op));
        Assert.True(GuestMemory.Span(source, 0x300).SequenceEqual(GuestMemory.Span(destination, 0x300)));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpSync", op, 1u, 0u, status));
        Assert.Equal(0u, GuestMemory.R32(status));
        Assert.Equal(EventFlagCondition, Call(Services, "ksceKernelDmaOpSync", op, 1u, 0u, 0u));
        Assert.Equal(NoChain, Call(Services, "ksceKernelDmaOpEnQueue", op));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpFree", op));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpEnQueue", op));
    }

    [Fact]
    public void AFillBlockWritesTheOptionsFillWord()
    {
        uint destination = Pattern(0x43, 1);
        uint op = NewOperation(0x11);
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(0, destination, 0x42, 0x300C), Options(0x11223344), 0x10u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpEnQueue", op));
        for (uint i = 0; i < 0x42; i++)
        {
            Assert.Equal((byte)(0x44 - (i & 3) * 0x11), GuestMemory.Read<byte>(destination + i));
        }

        Assert.Equal((byte)(1 + 0x42), GuestMemory.Read<byte>(destination + 0x42));
    }

    [Fact]
    public void SetupDirectMovesOneBlock()
    {
        uint source = Pattern(0x80, 3);
        uint destination = _harness.Alloc(0x80);
        uint direct = _harness.Alloc(0x40);
        GuestMemory.W32(direct, source);
        GuestMemory.W32(direct + 4, destination);
        GuestMemory.W32(direct + 8, 0x80);
        GuestMemory.W32(direct + 0xC, 0x3000);
        uint op = NewOperation(0x12, 15);
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpSetupDirect", op, 0u, 0x11u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpSetupDirect", op, direct, 0x11u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpEnQueue", op));
        Assert.True(GuestMemory.Span(source, 0x80).SequenceEqual(GuestMemory.Span(destination, 0x80)));
    }

    [Fact]
    public void ConcatenateAppendsOrRunsAFinishedOperationAgain()
    {
        uint source = Pattern(0x40, 9);
        uint destination = _harness.Alloc(0x40);
        uint op = NewOperation();
        Assert.Equal(NoChain, Call(Services, "ksceKernelDmaOpConcatenate", op, Block(source, destination, 0x10), 0x11u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(source, destination, 0x10), 0u, 0x11u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpConcatenate", op, Block(source + 0x10, destination + 0x10, 0x10), 0x11u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpEnQueue", op));
        Assert.True(GuestMemory.Span(source, 0x20).SequenceEqual(GuestMemory.Span(destination, 0x20)));
        Assert.Equal(0, GuestMemory.Read<byte>(destination + 0x20));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpConcatenate", op, Block(source + 0x20, destination + 0x20, 0x20), 0x11u));
        Assert.True(GuestMemory.Span(source, 0x40).SequenceEqual(GuestMemory.Span(destination, 0x40)));
    }

    [Fact]
    public void TheCompletionCallsTheCallback()
    {
        uint seen = _harness.Alloc(0x20);
        uint callback = Recorder(seen);
        uint source = Pattern(0x30, 5);
        uint destination = _harness.Alloc(0x30);
        uint op = NewOperation();
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpSetCallback", op, 0u, 0x1234u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpSetCallback", op, callback, 0x1234u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(source, destination, 0x30), 0u, 0u));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Services, "ksceKernelDmaOpEnQueue", op));
            Assert.Equal(0, Call(Services, "ksceKernelDmaOpSync", op, 2u, 0u, 0u));
        });
        Assert.Equal(op, GuestMemory.R32(seen));
        Assert.Equal(0u, GuestMemory.R32(seen + 4));
        Assert.Equal(0x1234u, GuestMemory.R32(seen + 8));
        Assert.Equal(0u, GuestMemory.R32(seen + 12));
        Assert.Equal(0x30u, GuestMemory.R32(seen + 16));
    }

    [Fact]
    public void TheArgumentsAndStatesAreChecked()
    {
        uint source = Pattern(0x20, 0);
        uint destination = _harness.Alloc(0x20);
        uint op = (uint)Call(Services, "ksceKernelDmaOpAlloc", 0u);
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpAssign", op, 0x12u, 16u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpAssign", op, 0x15u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpAssign", op, 0x14u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpAssign", 0x12345u, 0x10u, 0u));
        Assert.Equal(NoChain, Call(Services, "ksceKernelDmaOpEnQueue", op));
        Assert.Equal(NoChain, Call(Services, "ksceKernelDmaOpSync", op, 2u, 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpAssign", op, 0x10u, 0u));
        Assert.Equal(InvalidSize, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(source, destination, 0x20), Options(size: 0x5C), 0x11u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpSetupChain", op, 0u, 0u, 0x11u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpSetupChain", op, End, 0u, 0x11u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(source, destination, 0x20), 0u, 2u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(source, destination, 0x1C, burst: 0x10), 0u, 0x11u));
        Assert.Equal(NoTranslation, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(0x100, destination, 0x20), 0u, 0x11u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(source, destination, 0x20, burst: 0x10), 0u, 0x11u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpSync", op, 9u, 0u, 0u));
        Assert.Equal(NotQueued, Call(Services, "ksceKernelDmaOpDeQueue", op));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpQuit", op));
        Assert.Equal(NoChain, Call(Services, "ksceKernelDmaOpEnQueue", op));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpFree", op));
        Assert.Equal(Invalid, Call(Services, "ksceKernelDmaOpFree", op));
    }

    [Fact]
    public void TheCryptoEngineIsRefusedAtTheQueue()
    {
        uint source = Pattern(0x20, 0);
        uint destination = _harness.Alloc(0x20);
        uint op = NewOperation(0x13);
        Assert.Equal(InvalidSize, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(source, destination, 0x20), Options(), 0x11u));
        Assert.Equal(0, Call(Services, "ksceKernelDmaOpSetupChain", op, Block(source, destination, 0x20), Options(size: 0x5C), 0x11u));
        Assert.Equal(Secure, Call(Services, "ksceKernelDmaOpEnQueue", op));
        Assert.Equal(0u, GuestMemory.R32(destination));
    }

    /// <summary>ARM code that stores r0..r3 and the first stack argument at <paramref name="target"/> and returns.</summary>
    private uint Recorder(uint target)
    {
        uint code = _harness.Alloc(24);
        GuestMemory.W32(code, 0xE59FC00C); // ldr r12, [pc, #12]  (the word at +20)
        GuestMemory.W32(code + 4, 0xE88C000F); // stmia r12, {r0-r3}
        GuestMemory.W32(code + 8, 0xE59D0000); // ldr r0, [sp]
        GuestMemory.W32(code + 12, 0xE58C0010); // str r0, [r12, #16]
        GuestMemory.W32(code + 16, 0xE12FFF1E); // bx lr
        GuestMemory.W32(code + 20, target);
        return code;
    }
}
