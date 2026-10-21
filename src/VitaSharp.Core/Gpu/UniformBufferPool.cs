using System.Collections.Concurrent;
using System.Numerics;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// Power-of-two byte buffers for the uniform data of pooled <see cref="DrawCommand"/>s. A command swaps its buffer for
/// one of another size class only when the draw's uniform block does not fit (or is much smaller), and the old buffer
/// goes back here, so steady-state draws allocate nothing and commands do not all grow to the largest block.
/// Only the guest thread filling a recycled command rents/returns its buffers (the GPU worker never touches them).
/// </summary>
public static class UniformBufferPool
{
    const int MinShift = 8, Classes = 24 - MinShift + 1; // 256 B .. 16 MB
    const int MaxPerClass = 4096;
    static readonly ConcurrentQueue<byte[]>[] Free = Enumerable.Range(0, Classes).Select(_ => new ConcurrentQueue<byte[]>()).ToArray();

    static int ClassOf(int bytes) => Math.Max(BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)Math.Max(bytes, 1))), MinShift) - MinShift;

    public static byte[] Rent(int bytes)
    {
        int c = ClassOf(bytes);
        if (c < Classes && Free[c].TryDequeue(out var a)) return a;
        return new byte[c < Classes ? 1 << (c + MinShift) : bytes];
    }

    public static void Return(byte[] buffer)
    {
        int len = buffer.Length;
        if (len < 1 << MinShift || !BitOperations.IsPow2(len)) return;
        int c = ClassOf(len);
        if (c < Classes && Free[c].Count < MaxPerClass) Free[c].Enqueue(buffer);
    }

    /// <summary>Makes <paramref name="data"/> hold at least <paramref name="bytes"/> bytes (contents undefined).</summary>
    public static void Ensure(ref byte[] data, int bytes)
    {
        // keep the buffer unless too small or more than 4x too big (then trade it for a better-sized one)
        if (data.Length >= bytes && data.Length <= Math.Max(bytes, 1 << MinShift) * 4) return;
        Return(data);
        data = Rent(bytes);
    }
}
