namespace VitaSharp.Core.Gpu;

/// <summary>
/// GPU-visible memory the renderer lends the <see cref="UniformArena"/> for its chunks, so the backend binds the uniform
/// data where the guest side wrote it instead of copying every draw's blocks into its upload ring.
/// </summary>
public interface IUniformHeap
{
    /// <summary>Memory for a <see cref="UniformArena.ChunkSize"/> chunk and its id, or 0 when the heap is full. Called
    /// under the arena lock, from any thread.</summary>
    nint Alloc(out int id);
}

/// <summary>A block of uniform memory shared by many queued draws (see <see cref="UniformArena"/>).</summary>
public sealed unsafe class UniformChunk
{
    private byte[]? _data;
    private volatile nint _ptr;
    public readonly int Size;
    /// <summary>The heap whose memory this chunk is (null: managed memory) and the chunk's id in it.</summary>
    public IUniformHeap? Heap { get; private set; }
    public readonly int HeapId;
    /// <summary>Owned by the backend's worker thread (e.g. the frame that last took a reference).</summary>
    public long BackendTag = -1;
    internal int _used, _outstanding;
    internal bool _retired, _dedicated;
    /// <summary>On the free stack (guards against a second recycle: see UniformArena.Release).</summary>
    internal bool _inFree;

    internal UniformChunk(int size)
    {
        _data = new byte[size];
        Size = size;
        _written = new ulong[WrittenWords(size)];
    }
    internal UniformChunk(nint ptr, int size, IUniformHeap heap, int id)
    {
        _ptr = ptr;
        Size = size;
        Heap = heap;
        HeapId = id;
        _written = new ulong[WrittenWords(size)];
        Span.Clear(); // heap memory comes uninitialised: the zero invariant below starts here
    }

    // With UniformArena.ZeroOnRecycle (VITASHARP_UB_ZERO=recycle; by default the renter writes every byte instead):
    // A chunk the arena hands out is all zero except what was written into it since, and is zero again when it is
    // recycled: a draw's uniform slice must read zero where no buffer is copied, and clearing every slice whole (8 KB
    // per unknown-size buffer, most of it never written) was most of the guest thread's cost per draw (F1 2011 races,
    // ~1,200 draws a frame). Writers mark what they wrote, in 256-byte granules; a recycled chunk clears only those.
    private const int GranuleShift = 8;
    private readonly ulong[] _written;
    private static int WrittenWords(int size) => (((size + (1 << GranuleShift) - 1) >> GranuleShift) + 63) / 64;

    /// <summary>Marks <paramref name="length"/> bytes at <paramref name="offset"/> (chunk offset) as written: they are
    /// cleared when the chunk is recycled. Any thread.</summary>
    public void MarkWritten(int offset, int length)
    {
        if (length <= 0 || !UniformArena.ZeroOnRecycle)
        {
            return;
        }

        int g0 = offset >> GranuleShift, g1 = (offset + length - 1) >> GranuleShift;
        for (int w = g0 >> 6; w <= g1 >> 6; w++)
        {
            int lo = w == g0 >> 6 ? g0 & 63 : 0, hi = w == g1 >> 6 ? g1 & 63 : 63;
            ulong bits = (hi == 63 ? ulong.MaxValue : (1UL << (hi + 1)) - 1) & ~((1UL << lo) - 1);
            if ((Volatile.Read(ref _written[w]) & bits) != bits)
            {
                Interlocked.Or(ref _written[w], bits);
            }
        }
    }

    /// <summary>Clears the granules written since the chunk was handed out (no reader or writer is left).</summary>
    internal void ClearWritten()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Span<byte> span = Span;
        for (int w = 0; w < _written.Length; w++)
        {
            ulong bits = _written[w];
            if (bits == 0)
            {
                continue;
            }

            _written[w] = 0;
            for (int b = 0; b < 64;)
            {
                if ((bits >> b) == 0)
                {
                    break;
                }

                b += System.Numerics.BitOperations.TrailingZeroCount(bits >> b); // the next written granule
                int e = b;
                while (e < 64 && ((bits >> e) & 1) != 0)
                {
                    e++; // and the run of written ones from it
                }

                int start = ((w << 6) + b) << GranuleShift, end = Math.Min(((w << 6) + e) << GranuleShift, Size);
                if (start < end)
                {
                    span[start..end].Clear();
                    UniformArena.ClearedBytes += end - start;
                }
                b = e;
            }
        }
        UniformArena.ClearTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    public Span<byte> Span => _ptr is var p and not 0 ? new Span<byte>((void*)p, Size) : _data;

    /// <summary>The heap goes away: the contents move to managed memory (the arena lock is held).</summary>
    internal void Detach()
    {
        _data = Span.ToArray();
        Heap = null;
        _ptr = 0;
    }
}

/// <summary>
/// Uniform data of queued draws, carved sequentially out of 1 MB chunks. Each draw holds a slice from submission until
/// the GPU worker executed it (<see cref="DrawCommand.ReleaseUniforms"/>); a chunk that is full and has no outstanding
/// slices is reused, so steady-state draws allocate nothing and memory follows the amount of queued work. Slices are
/// counted per chunk, so release order does not matter (several guest threads may submit draws). With a renderer's
/// <see cref="IUniformHeap"/> attached, chunks are GPU-visible memory: the renderer keeps a reference
/// (<see cref="AddRef"/>) on the chunks a recorded frame reads until the GPU finished it.
/// </summary>
public static class UniformArena
{
    public const int ChunkSize = 1 << 20;
    /// <summary>VITASHARP_UB_ZERO=recycle (A/B): the old invariant, a chunk is zero except what was written since it was
    /// handed out and the written granules are cleared when it is recycled. Default: no clearing; whoever rents a slice
    /// writes every byte the backend binds (Gxm.WriteSlot zeroes what a slot does not copy). Killzone cleared ~6.5 MB a
    /// frame on recycle (0.9 ms of the PC's GPU worker) for ~0.3 MB of slot tails.</summary>
    public static readonly bool ZeroOnRecycle = Environment.GetEnvironmentVariable(Util.EnvVars.UbZero) == "recycle";
    /// <summary>Statistics (PERF uniforms, under the arena lock): bytes zeroed when chunks were recycled, and the time.</summary>
    public static long ClearedBytes, ClearTicks;
    // spare managed chunks kept after a burst of queued work (64 MB). Counted apart from the heap's chunks: with the
    // 48 heap chunks on the same stack the old bound of 16 kept none, so a game past the heap (Jak 1's title screen,
    // ~27 MB of uniforms a frame) allocated a new 1 MB array per chunk, ~20 MB a frame and a gen 2 GC almost every frame
    private const int MaxFreeChunks = 64;
    private static int s_freeManaged;
    private static readonly object s_lock = new();
    private static readonly Stack<UniformChunk> s_free = new();
    private static readonly List<UniformChunk> s_heapChunks = new();
    private static UniformChunk? s_current;
    private static IUniformHeap? s_heap;

    /// <summary>Alignment of every slice (a power of two, at least 16): the renderer's dynamic offset alignment.</summary>
    public static int Alignment { get; private set; } = 16;

    public static int Align(int bytes) => (bytes + Alignment - 1) & ~(Alignment - 1);

    public static void Attach(IUniformHeap heap, int alignment)
    {
        lock (s_lock)
        {
            s_heap = heap;
            Alignment = Math.Max(16, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)alignment));
            if (s_current != null)
            {
                Retire(s_current);
                s_current = null;
            }
        }
    }

    /// <summary>The heap is destroyed: its chunks, free or still held by queued draws, move to managed memory.</summary>
    public static void Detach(IUniformHeap heap)
    {
        lock (s_lock)
        {
            if (s_heap == heap)
            {
                s_heap = null;
            }

            foreach (UniformChunk uniformChunk in s_heapChunks)
            {
                if (uniformChunk.Heap == heap)
                {
                    uniformChunk.Detach();
                }
            }

            s_heapChunks.RemoveAll(c => c.Heap == null);
            s_freeManaged = s_free.Count(c => c.Heap == null);
        }
    }

    /// <summary>A slice of <paramref name="bytes"/> bytes at <paramref name="offset"/> in the returned chunk. The caller
    /// writes every byte of it that a reader uses: its contents are whatever an earlier slice left, unless
    /// <see cref="ZeroOnRecycle"/>, where it is zero-filled and <paramref name="clear"/> false means the caller marks
    /// what it writes (<see cref="UniformChunk.MarkWritten"/>), true that the whole slice counts as written.</summary>
    public static UniformChunk Rent(int bytes, out int offset, bool clear = true)
    {
        UniformChunk uniformChunk;
        lock (s_lock)
        {
            if (bytes > ChunkSize / 4)
            {
                // unusually large block: a chunk of its own, dropped once released
                uniformChunk = new UniformChunk(bytes) { _outstanding = 1, _dedicated = true, _retired = true };
                offset = 0;
                return uniformChunk;
            }
            uniformChunk = s_current!;
            if (uniformChunk == null || Align(uniformChunk._used) + bytes > uniformChunk.Size)
            {
                if (uniformChunk != null)
                {
                    Retire(uniformChunk);
                }
                uniformChunk = s_current = NewChunk();
                uniformChunk._retired = false;
                uniformChunk._used = 0;
            }
            offset = Align(uniformChunk._used);
            uniformChunk._used = offset + bytes;
            Interlocked.Increment(ref uniformChunk._outstanding);
        }
        if (clear)
        {
            uniformChunk.MarkWritten(offset, bytes); // zero already; the caller may write any of it
        }

        return uniformChunk;
    }

    private static UniformChunk NewChunk()
    {
        if (s_free.Count > 0)
        {
            UniformChunk uniformChunk = s_free.Pop();
            uniformChunk._inFree = false;
            if (uniformChunk.Heap == null)
            {
                s_freeManaged--;
            }

            return uniformChunk;
        }
        if (s_heap is { } h && h.Alloc(out int id) is var p and not 0)
        {
            var hc = new UniformChunk(p, ChunkSize, h, id);
            s_heapChunks.Add(hc);
            return hc;
        }
        return new UniformChunk(ChunkSize);
    }

    /// <summary>One more reference on the chunk (a recorded frame that reads it); given back with <see cref="Release"/>.</summary>
    public static void AddRef(UniformChunk uniformChunk)
    {
        Interlocked.Increment(ref uniformChunk._outstanding); // the chunk is live (held by the caller): no recycle can race this
    }

    /// <summary>The GPU worker releases a slice per executed draw: only the release that frees a retired chunk takes the
    /// lock (the guest thread holds it in <see cref="Rent"/> for every draw). Recycling always happens under the lock,
    /// after checking again that the chunk is retired, unreferenced and not already free.</summary>
    public static void Release(UniformChunk uniformChunk)
    {
        if (Interlocked.Decrement(ref uniformChunk._outstanding) != 0 || uniformChunk._dedicated || !Volatile.Read(ref uniformChunk._retired))
        {
            return;
        }

        lock (s_lock)
        {
            if (Volatile.Read(ref uniformChunk._outstanding) == 0 && uniformChunk._retired)
            {
                Recycle(uniformChunk);
            }
        }
    }

    /// <summary>A full chunk is no longer rented from (under the lock): free now, or by its last <see cref="Release"/>.</summary>
    private static void Retire(UniformChunk uniformChunk)
    {
        Volatile.Write(ref uniformChunk._retired, true);
        Interlocked.MemoryBarrier(); // pairs with the full fence of Release's decrement: one of the two sees the other
        if (Volatile.Read(ref uniformChunk._outstanding) == 0)
        {
            Recycle(uniformChunk);
        }
    }

    private static void Recycle(UniformChunk uniformChunk)
    {
        if (uniformChunk._inFree)
        {
            return;
        }

        if (ZeroOnRecycle)
        {
            uniformChunk.ClearWritten();
        }
        // heap chunks are bounded by the heap and always kept; managed ones up to MaxFreeChunks
        if (uniformChunk.Heap != null)
        {
            uniformChunk._inFree = true;
            s_free.Push(uniformChunk);
        }
        else if (s_freeManaged < MaxFreeChunks)
        {
            uniformChunk._inFree = true;
            s_free.Push(uniformChunk);
            s_freeManaged++;
        }
    }
}
