using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Native;

namespace VitaSharp.Tests;

/// <summary>
/// The native code arena gives back the ranges of code nothing can run any more (a block translated ahead that the
/// precompilation evicted before it ever ran, <see cref="Jit.EvictAhead"/>), and placements that ask for it fill those
/// ranges before the arena grows: what the arena holds then stays the code that runs plus the bounded speculative code,
/// instead of every guess ever translated.
/// </summary>
public sealed unsafe class ExecutableMemoryReuseTests
{
    private static byte[] Code(int length, byte fill)
    {
        byte[] code = new byte[length];
        Array.Fill(code, fill);
        return code;
    }

    [Fact]
    public void FreedRangesAreReusedOnlyByPlacementsThatAskAndTheRestStaysFree()
    {
        if (ExecutableMemory.UnsupportedReason != null)
        {
            return; // no native code arena on this host (the IL backend runs)
        }

        using var arena = new ExecutableMemory();
        byte* first = arena.Place(Code(256, 1));
        byte* second = arena.Place(Code(512, 2));
        arena.Place(Code(64, 3));
        int end = arena.Used;
        arena.Free(first, 256);
        Assert.Equal(256, arena.FreeBytes);

        // a placement that does not ask (the JIT compiling for the running game: no second encoding) grows the arena
        byte* plain = arena.Place(Code(200, 4), reuseFreed: false);
        Assert.True(plain >= arena.ExecBase + end);
        end = arena.Used;

        // one that asks takes the freed range; the rest of it (256 - 208) stays free
        byte* reused = arena.Place(Code(200, 5), reuseFreed: true);
        Assert.True(reused == first);
        Assert.Equal(end, arena.Used);
        Assert.Equal(48, arena.FreeBytes);
        Assert.Equal(5, reused[0]);
        Assert.Equal(5, reused[199]);

        // address-dependent code (x64): encoded for the freed range it goes to, with room to spare for a longer encoding
        arena.Free(second, 512);
        var addresses = new List<ulong>();
        byte* encoded = arena.Place(rip =>
        {
            addresses.Add(rip);
            return Code(300, 6);
        }, reuseFreed: true);
        Assert.True(encoded == second);
        Assert.Equal((ulong)second, addresses[^1]);
        Assert.Equal(end, arena.Used);
        Assert.Equal(48 + 512 - 304, arena.FreeBytes);

        // nothing freed fits: the arena grows
        byte* large = arena.Place(Code(1000, 7), reuseFreed: true);
        Assert.True(large >= arena.ExecBase + end);
    }

    [Fact]
    public void RangesOutsideThePlacedCodeAreRejected()
    {
        if (ExecutableMemory.UnsupportedReason != null)
        {
            return;
        }

        using var arena = new ExecutableMemory();
        byte* code = arena.Place(Code(64, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Free(code + 4096, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Free(code + 8, 32)); // not a placement start
    }

    [Fact]
    public void TheBudgetsAreSmallerOnAndroid()
    {
        // what runs plus a bounded guess: the phone has the least memory
        Assert.Equal(128L << 20, Jit.DefaultAheadBudget(android: false));
        Assert.Equal(64L << 20, Jit.DefaultAheadBudget(android: true));
        Assert.True(Jit.DefaultSpeculativeBudget(android: true) < Jit.DefaultSpeculativeBudget(android: false));
        Assert.True(Jit.DefaultSpeculativeBudget(android: true) < Jit.DefaultAheadBudget(android: true) / 2);
        Assert.True(Jit.DefaultAheadBudget(android: false) <= ExecutableMemory.ReserveSize / 2);
    }
}
