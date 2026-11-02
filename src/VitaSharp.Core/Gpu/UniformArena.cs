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
    byte[]? _data;
    volatile nint _ptr;
    public readonly int Size;
    /// <summary>The heap whose memory this chunk is (null: managed memory) and the chunk's id in it.</summary>
    public IUniformHeap? Heap { get; private set; }
    public readonly int HeapId;
    /// <summary>Owned by the backend's worker thread (e.g. the frame that last took a reference).</summary>
    public long BackendTag = -1;
    internal int Used, Outstanding;
    internal bool Retired, Dedicated;

    internal UniformChunk(int size) { _data = new byte[size]; Size = size; }
    internal UniformChunk(nint ptr, int size, IUniformHeap heap, int id) { _ptr = ptr; Size = size; Heap = heap; HeapId = id; }

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
    const int MaxFreeChunks = 16; // spare managed chunks kept after a burst of queued work (16 MB)
    static readonly object Lock = new();
    static readonly Stack<UniformChunk> Free = new();
    static readonly List<UniformChunk> HeapChunks = new();
    static UniformChunk? _current;
    static IUniformHeap? _heap;

    /// <summary>Alignment of every slice (a power of two, at least 16): the renderer's dynamic offset alignment.</summary>
    public static int Alignment { get; private set; } = 16;

    public static int Align(int bytes) => (bytes + Alignment - 1) & ~(Alignment - 1);

    public static void Attach(IUniformHeap heap, int alignment)
    {
        lock (Lock)
        {
            _heap = heap;
            Alignment = Math.Max(16, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)alignment));
            if (_current != null) { _current.Retired = true; if (_current.Outstanding == 0) Recycle(_current); _current = null; }
        }
    }

    /// <summary>The heap is destroyed: its chunks, free or still held by queued draws, move to managed memory.</summary>
    public static void Detach(IUniformHeap heap)
    {
        lock (Lock)
        {
            if (_heap == heap) _heap = null;
            foreach (var c in HeapChunks) if (c.Heap == heap) c.Detach();
            HeapChunks.RemoveAll(c => c.Heap == null);
        }
    }

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
            if (c == null || Align(c.Used) + bytes > c.Size)
            {
                if (c != null) { c.Retired = true; if (c.Outstanding == 0) Recycle(c); }
                c = _current = NewChunk();
                c.Retired = false;
                c.Used = 0;
            }
            offset = Align(c.Used);
            c.Used = offset + bytes;
            c.Outstanding++;
        }
        c.Span.Slice(offset, bytes).Clear();
        return c;
    }

    static UniformChunk NewChunk()
    {
        if (Free.Count > 0) return Free.Pop();
        if (_heap is { } h && h.Alloc(out int id) is var p and not 0)
        {
            var hc = new UniformChunk(p, ChunkSize, h, id);
            HeapChunks.Add(hc);
            return hc;
        }
        return new UniformChunk(ChunkSize);
    }

    /// <summary>One more reference on the chunk (a recorded frame that reads it); given back with <see cref="Release"/>.</summary>
    public static void AddRef(UniformChunk c)
    {
        lock (Lock) c.Outstanding++;
    }

    public static void Release(UniformChunk c)
    {
        lock (Lock)
            if (--c.Outstanding == 0 && c.Retired && !c.Dedicated) Recycle(c);
    }

    static void Recycle(UniformChunk c)
    {
        // heap chunks are bounded by the heap and always kept; managed ones up to MaxFreeChunks
        if (c.Heap != null || Free.Count < MaxFreeChunks) Free.Push(c);
    }
}
