using BenchmarkDotNet.Attributes;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Benchmarks;

/// <summary>
/// The per-draw uniform path of sceGxmDraw: rent a slice of the shared arena, write the slots (guest bytes, then zeros
/// for the rest of the slot: Gxm.WriteSlot) and release it when the GPU worker is done. Slot sizes like Killzone's
/// (a few small slots and 16 KB array slots, PERF ubstats).
/// </summary>
public class UniformArenaBenchmarks
{
    [Params(256, 4096, 16384)] public int SlotBytes;

    private GuestMemory? _memory;
    private uint _src;
    private readonly UniformChunk[] _held = new UniformChunk[256];

    [GlobalSetup]
    public void Setup()
    {
        _memory = new GuestMemory();
        _src = _memory.Alloc(0x10000, "bench uniforms");
        for (uint i = 0; i < 0x10000; i += 4)
        {
            GuestMemory.W32(_src + i, 0x3F800000 + i);
        }
    }

    [GlobalCleanup] public void Cleanup() => _memory?.Dispose();

    /// <summary>One draw: rent, two slots (one full copy, one short copy plus zero tail), release.</summary>
    [Benchmark]
    public void RentWriteRelease()
    {
        int bytes = SlotBytes * 2;
        UniformChunk chunk = UniformArena.Rent(bytes, out int off, clear: false);
        Span<byte> data = chunk.Span.Slice(off, bytes);
        Gxm.WriteSlot(0, SlotBytes, _src, SlotBytes, data, chunk, off);
        Gxm.WriteSlot(SlotBytes, SlotBytes, _src + 0x100, SlotBytes / 3, data, chunk, off);
        UniformArena.Release(chunk);
    }

    /// <summary>A frame's worth of draws holding their slices until the frame retires (the arena's chunk churn).</summary>
    [Benchmark(OperationsPerInvoke = 256)]
    public void FrameOf256Draws()
    {
        UniformChunk[] held = _held;
        for (int d = 0; d < held.Length; d++)
        {
            UniformChunk chunk = UniformArena.Rent(SlotBytes, out int off, clear: false);
            Gxm.WriteSlot(0, SlotBytes, _src, SlotBytes, chunk.Span.Slice(off, SlotBytes), chunk, off);
            held[d] = chunk;
        }
        foreach (UniformChunk c in held)
        {
            UniformArena.Release(c);
        }
    }
}
