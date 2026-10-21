namespace VitaSharp.Core.Gpu;

/// <summary>A block of uniform memory shared by many queued draws (see <see cref="UniformArena"/>).</summary>
public sealed class UniformChunk(int size)
{
    public readonly byte[] Data = new byte[size];
    internal int Used, Outstanding;
    internal bool Retired, Dedicated;
}

/// <summary>
/// Uniform data of queued draws, carved sequentially out of 1 MB chunks. Each draw holds a slice from submission until
/// the GPU worker executed it (<see cref="DrawCommand.ReleaseUniforms"/>); a chunk that is full and has no outstanding
/// slices is reused, so steady-state draws allocate nothing and memory follows the amount of queued work. Slices are
/// counted per chunk, so release order does not matter (several guest threads may submit draws).
/// </summary>
public static class UniformArena
{
    const int ChunkSize = 1 << 20, MaxFreeChunks = 16; // spare chunks kept after a burst of queued work (16 MB)
    static readonly object Lock = new();
    static readonly Stack<UniformChunk> Free = new();
    static UniformChunk? _current;

    /// <summary>A zero-filled slice of <paramref name="bytes"/> bytes at <paramref name="offset"/> in the returned chunk.</summary>
    public static UniformChunk Rent(int bytes, out int offset)
    {
        UniformChunk c;
        lock (Lock)
        {
            if (bytes > ChunkSize / 4)
            {
                // unusually large block: a chunk of its own, dropped once released
                c = new UniformChunk(bytes) { Outstanding = 1, Dedicated = true, Retired = true };
                offset = 0;
                return c;
            }
            c = _current!;
            if (c == null || c.Used + bytes > c.Data.Length)
            {
                if (c != null) { c.Retired = true; if (c.Outstanding == 0) Recycle(c); }
                c = _current = Free.Count > 0 ? Free.Pop() : new UniformChunk(ChunkSize);
                c.Retired = false;
                c.Used = 0;
            }
            offset = c.Used;
            c.Used += (bytes + 15) & ~15;
            c.Outstanding++;
        }
        c.Data.AsSpan(offset, bytes).Clear();
        return c;
    }

    public static void Release(UniformChunk c)
    {
        lock (Lock)
            if (--c.Outstanding == 0 && c.Retired && !c.Dedicated) Recycle(c);
    }

    static void Recycle(UniformChunk c)
    {
        if (Free.Count < MaxFreeChunks) Free.Push(c);
    }
}
