using Silk.NET.Vulkan;
using VitaSharp.Core.Memory;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Vertex and index data that did not change since an earlier draw is not uploaded again. Every draw copied its
/// streams and indices from guest memory into the frame's upload ring: F1 2011's race uploaded 18.7 MB a frame, mostly
/// static meshes, on the phone's GPU worker. Ranges of at least <see cref="MinBytes"/> are kept in host buffers of their
/// own, keyed by address and length, and reused while the page-dirty tracker (Core/Memory/DirtyPages.cs) reports no
/// write into them: neither a page version newer than the copy nor a page marked since the last sweep. A changed
/// range gets a new buffer (the old one is freed when the current frame retires: earlier frames that read it retire
/// first). VITASHARP_VERTEX_CACHE=0 uploads everything every draw, as before.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    sealed class CachedRange
    {
        public Buffer Buffer;
        public DeviceMemory Memory;
        public uint Stamp;
        public long LastUsedFrame;
        /// <summary>Copies made after the first (the range was written again); from <see cref="VolatileRewrites"/> on
        /// it is volatile (a vertex ring the CPU refills every frame) and goes through the upload ring.</summary>
        public byte Rewrites;
        public bool Volatile => Buffer.Handle == 0;
    }

    const int VolatileRewrites = 2;

    const uint MinBytes = 16 * 1024;
    const long MaxCachedBytes = 96L << 20;
    static readonly bool VertexCacheOn = DirtyPages.Enabled
        && Environment.GetEnvironmentVariable(Core.Util.EnvVars.VertexCache) != "0";
    readonly Dictionary<(uint Addr, uint Length, int Space), CachedRange> _rangeCache = new();
    long _rangeCacheBytes, _statRangeVolatile, _statRangeVolatileBytes, _statRangeHits, _statRangeHitBytes, _statRangeMisses, _rangeSweepFrame;

    /// <summary>The guest range as a vertex/index buffer: a cached copy when it is unchanged, else the upload ring.</summary>
    (Buffer Buffer, ulong Offset) UploadRange(uint addr, uint length, ulong align = 4)
    {
        if (!VertexCacheOn || length < MinBytes)
        {
            if (SmallCacheOn && VertexCacheOn && length >= SmallMinBytes)
            {
                return UploadSmallRange(addr, length, align);
            }

            _statSmallUploadBytes += length;
            return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
        }
        SweepDirtyPages();
        var key = (addr, length, SpaceIdOf(addr));
        if (_rangeCache.TryGetValue(key, out var e) && e.Volatile)
        {
            // a new buffer per rewrite costs a vkAllocateMemory/vkFreeMemory pair: the ring is a copy
            e.LastUsedFrame = FrameCount;
            _statRangeVolatileBytes += length;
            return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
        }
        if (e != null && !DirtyPages.WrittenSince(addr, length, e.Stamp) && !DirtyPages.MarkedNow(addr, length))
        {
            e.LastUsedFrame = FrameCount;
            _statRangeHits++;
            _statRangeHitBytes += length;
            return (e.Buffer, 0);
        }
        _statRangeMisses++;
        if (e == null && _rangeCacheBytes + length > MaxCachedBytes)
        {
            EvictRanges(force: true);
        }

        if (e == null && _rangeCacheBytes + length > MaxCachedBytes)
        {
            return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
        }

        if (e != null)
        {
            F.Overflow.Add((e.Buffer, e.Memory)); // frames in flight may still read the old copy
            if (++e.Rewrites >= VolatileRewrites)
            {
                (e.Buffer, e.Memory, e.LastUsedFrame) = (default, default, FrameCount);
                _rangeCacheBytes -= length;
                _statRangeVolatile++;
                return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
            }
        }
        else
        {
            e = new CachedRange();
            _rangeCache[key] = e;
            _rangeCacheBytes += length;
        }
        var (b, m, p) = _core.CreateHostBuffer(length, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit, MemCat.UploadRing);
        Core.Util.FastCopy.Copy(GuestMemory.ReadSpan(addr, (int)length), new Span<byte>((void*)p, (int)length));
        _uploadBytes += length;
        (e.Buffer, e.Memory, e.Stamp, e.LastUsedFrame) = (b, m, DirtyPages.Epoch, FrameCount);
        if (_rangeSweepFrame + 300 < FrameCount)
        {
            EvictRanges(force: false);
        }

        return (b, 0);
    }

    // ------------------------------------------------------------------ small ranges (VITASHARP_VERTEX_CACHE_SMALL)

    /// <summary>
    /// VITASHARP_VERTEX_CACHE_SMALL=1: ranges below <see cref="MinBytes"/> (Uncharted draws ~540 small meshes a frame,
    /// about 6 MB of copies on the phone's GPU worker) are cached too, sub-allocated from a few large host buffers
    /// instead of one buffer each. A range written again twice is volatile (skinned or CPU-built every frame) and goes
    /// through the upload ring from then on. When the arena is full every small entry is dropped and the chunks are
    /// freed once the frame retires (frames in flight finish with them first).
    /// </summary>
    static readonly bool SmallCacheOn = Environment.GetEnvironmentVariable(Core.Util.EnvVars.VertexCacheSmall) == "1";
    static readonly uint SmallMinBytes = uint.TryParse(Environment.GetEnvironmentVariable(Core.Util.EnvVars.VertexCacheSmallMin), out var smin) ? Math.Max(16u, smin) : 512u;
    const uint SmallChunkBytes = 4u << 20;
    const int SmallMaxChunks = 12;

    sealed class SmallRange
    {
        public int Chunk;
        public uint Offset, Stamp, Generation;
        public byte Misses;
    }

    readonly Dictionary<(uint Addr, uint Length, int Space), SmallRange> _smallCache = new();
    readonly List<(Buffer Buffer, DeviceMemory Memory, nint Ptr)> _smallChunks = new();
    uint _smallUsed, _smallGeneration;
    long _statUniformBytes, _statMirrorCopyBytes;
    long _statSmallHitBytes, _statSmallCopyBytes, _statSmallUploadBytes, _statSmallResets;

    (Buffer Buffer, ulong Offset) UploadSmallRange(uint addr, uint length, ulong align)
    {
        SweepDirtyPages();
        var key = (addr, length, SpaceIdOf(addr));
        if (_smallCache.TryGetValue(key, out var e))
        {
            bool written = DirtyPages.WrittenSince(addr, length, e.Stamp) || DirtyPages.MarkedNow(addr, length);
            if (!written && e.Generation == _smallGeneration && e.Chunk >= 0)
            {
                _statSmallHitBytes += length;
                return (_smallChunks[e.Chunk].Buffer, e.Offset);
            }
            if (written && e.Misses < 255)
            {
                e.Misses++;
            }

            if (e.Misses >= 2)
            {
                // volatile: the ring is cheaper than re-caching it every frame
                _statSmallUploadBytes += length;
                e.Stamp = DirtyPages.Epoch;
                e.Chunk = -1; // its cached copy is stale from now on

                return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
            }
        }
        else
        {
            _smallCache[key] = e = new SmallRange();
        }

        uint off = (_smallUsed + 15) & ~15u;
        if (_smallChunks.Count == 0 || off + length > SmallChunkBytes)
        {
            if (_smallChunks.Count >= SmallMaxChunks)
            {
                ResetSmallCache();
            }

            var (b, m, p) = _core.CreateHostBuffer(SmallChunkBytes, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit, MemCat.UploadRing);
            _smallChunks.Add((b, m, p));
            off = 0;
        }
        int chunk = _smallChunks.Count - 1;
        Core.Util.FastCopy.Copy(GuestMemory.ReadSpan(addr, (int)length), new Span<byte>((void*)(_smallChunks[chunk].Ptr + (nint)off), (int)length));
        _smallUsed = off + length;
        _uploadBytes += length;
        _statSmallCopyBytes += length;
        (e.Chunk, e.Offset, e.Stamp, e.Generation) = (chunk, off, DirtyPages.Epoch, _smallGeneration);
        return (_smallChunks[chunk].Buffer, off);
    }

    /// <summary>The arena is full: every entry is invalidated (their volatility kept) and the chunks freed after the frame.</summary>
    void ResetSmallCache()
    {
        foreach (var (b, m, _) in _smallChunks)
        {
            F.Overflow.Add((b, m));
        }

        _smallChunks.Clear();
        _smallUsed = 0;
        _smallGeneration++;
        _statSmallResets++;
        if (_smallCache.Count > 200_000)
        {
            _smallCache.Clear();
        }
    }

    void DestroySmallCache()
    {
        foreach (var (b, m, _) in _smallChunks)
        {
            _core.DestroyBuffer(b, m);
        }

        _smallChunks.Clear();
        _smallCache.Clear();
    }

    /// <summary>Frees ranges unused for 300 frames (all unused ones this frame when <paramref name="force"/>).</summary>
    void EvictRanges(bool force)
    {
        _rangeSweepFrame = FrameCount;
        foreach (var (k, e) in _rangeCache.ToList())
        {
            if (e.LastUsedFrame >= FrameCount - (force ? 0 : 300))
            {
                continue;
            }

            _rangeCache.Remove(k);
            if (e.Volatile)
            {
                continue;
            }

            F.Overflow.Add((e.Buffer, e.Memory));
            _rangeCacheBytes -= k.Length;
        }
    }

    void DestroyRangeCache()
    {
        foreach (var e in _rangeCache.Values)
        {
            if (!e.Volatile)
            {
                _core.DestroyBuffer(e.Buffer, e.Memory);
            }
        }

        _rangeCache.Clear();
        _rangeCacheBytes = 0;
        DestroySmallCache();
    }

    string RangeCacheReport()
    {
        if (!VertexCacheOn)
        {
            return "";
        }

        double f = Math.Max(1.0, _workFrames);
        return $"; vertex/index cache: {_statRangeHits / f:F0} hits/frame ({_statRangeHitBytes / f / 1024:F0} KB/frame not uploaded), {_statRangeMisses / f:F0} copies/frame, {_rangeCache.Count} ranges ({_rangeCacheBytes >> 20} MB), {_statRangeVolatile} volatile ({_statRangeVolatileBytes / f / 1024:F0} KB/frame through the ring)"
            + $"; small ranges: {_statSmallUploadBytes / f / 1024:F0} KB/frame through the ring"
            + (SmallCacheOn ? $", cached (VITASHARP_VERTEX_CACHE_SMALL) {_statSmallHitBytes / f / 1024:F0} KB/frame reused, {_statSmallCopyBytes / f / 1024:F0} KB/frame copied, {_smallCache.Count} entries, {_smallChunks.Count} chunks, {_statSmallResets} resets" : "");
    }
}
