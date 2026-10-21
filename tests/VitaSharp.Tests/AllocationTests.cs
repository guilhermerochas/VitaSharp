using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>Allocation-free helpers: logging handlers and the uniform arena.</summary>
public sealed class AllocationTests
{
    static int _evaluations;
    static int Evaluate() { _evaluations++; return 42; }

    [Fact]
    public void DisabledDebugDoesNotFormat()
    {
        var level = Log.Level;
        try
        {
            Log.Level = LogLevel.Info;
            _evaluations = 0;
            Log.Debug($"value {Evaluate()}");
            Log.Trace($"value {Evaluate()}");
            Assert.Equal(0, _evaluations);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) Log.Debug($"value {i} {i * 2.5}");
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally { Log.Level = level; }
    }

    [Fact]
    public void WarnOnceWithInterpolatedKeyFormatsTheMessageOnce()
    {
        string tag = Guid.NewGuid().ToString("N");
        _evaluations = 0;
        for (int i = 0; i < 3; i++) Log.WarnOnce($"alloc-test-{tag}-{0x1234:X8}", $"first {Evaluate()}");
        Assert.Equal(1, _evaluations);
        // the same key built as a plain string is the same entry
        Log.WarnOnce($"alloc-test-{tag}-00001234", "never");
        Log.WarnOnce($"alloc-test-{tag}-{0x1234:X8}", $"again {Evaluate()}");
        Assert.Equal(1, _evaluations);
        // a seen key costs no allocation
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Log.WarnOnce($"alloc-test-{tag}-{0x1234:X8}", $"again {i}");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void UniformArenaSlicesAreZeroedAndReused()
    {
        var chunks = new List<(UniformChunk Chunk, int Offset)>();
        for (int i = 0; i < 4000; i++)
        {
            var c = UniformArena.Rent(1000, out int off);
            Assert.All(c.Data.AsSpan(off, 1000).ToArray(), b => Assert.Equal(0, b));
            c.Data.AsSpan(off, 1000).Fill(0xAB);
            chunks.Add((c, off));
        }
        // slices of one chunk never overlap
        foreach (var g in chunks.GroupBy(x => x.Chunk))
        {
            var offs = g.Select(x => x.Offset).OrderBy(x => x).ToList();
            for (int i = 1; i < offs.Count; i++) Assert.True(offs[i] - offs[i - 1] >= 1000);
        }
        foreach (var (c, _) in chunks) UniformArena.Release(c);
        // released chunks come back
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) UniformArena.Release(UniformArena.Rent(1000, out _));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
    }
}
