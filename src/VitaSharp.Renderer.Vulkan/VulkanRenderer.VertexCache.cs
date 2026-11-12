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
    private sealed class CachedRange
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

    private const int VolatileRewrites = 2;

    private const uint MinBytes = 16 * 1024;
    private const long MaxCachedBytes = 96L << 20;
    private static readonly bool s_vertexCacheOn = DirtyPages.Enabled
        && Environment.GetEnvironmentVariable(Core.Util.EnvVars.VertexCache) != "0";
    private readonly Dictionary<(uint Addr, uint Length, int Space), CachedRange> _rangeCache = new();
    private long _rangeCacheBytes, _statRangeVolatile, _statRangeVolatileBytes, _statRangeHits, _statRangeHitBytes, _statRangeMisses, _rangeSweepFrame;

    /// <summary>The guest range as a vertex/index buffer: a cached copy when it is unchanged, else the upload ring.</summary>
    private (Buffer Buffer, ulong Offset) UploadRange(uint addr, uint length, ulong align = 4)
    {
        if (!s_vertexCacheOn || length < MinBytes)
        {
            if (s_smallCacheOn && s_vertexCacheOn && length >= s_smallMinBytes)
            {
                return UploadSmallRange(addr, length, align);
            }

            _statSmallUploadBytes += length;
            return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
        }
        SweepDirtyPages();
        (uint addr, uint length, int) key = (addr, length, SpaceIdOf(addr));
        if (_rangeCache.TryGetValue(key, out CachedRange? cachedRange) && cachedRange.Volatile)
        {
            // a new buffer per rewrite costs a vkAllocateMemory/vkFreeMemory pair: the ring is a copy
            cachedRange.LastUsedFrame = FrameCount;
            _statRangeVolatileBytes += length;
            return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
        }
        if (cachedRange != null && !DirtyPages.WrittenSince(addr, length, cachedRange.Stamp) && !DirtyPages.MarkedNow(addr, length))
        {
            cachedRange.LastUsedFrame = FrameCount;
            _statRangeHits++;
            _statRangeHitBytes += length;
            return (cachedRange.Buffer, 0);
        }
        _statRangeMisses++;
        if (cachedRange == null && _rangeCacheBytes + length > MaxCachedBytes)
        {
            EvictRanges(force: true);
        }

        if (cachedRange == null && _rangeCacheBytes + length > MaxCachedBytes)
        {
            return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
        }

        if (cachedRange != null)
        {
            F.Overflow.Add((cachedRange.Buffer, cachedRange.Memory)); // frames in flight may still read the old copy
            if (++cachedRange.Rewrites >= VolatileRewrites)
            {
                (cachedRange.Buffer, cachedRange.Memory, cachedRange.LastUsedFrame) = (default, default, FrameCount);
                _rangeCacheBytes -= length;
                _statRangeVolatile++;
                return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
            }
        }
        else
        {
            cachedRange = new CachedRange();
            _rangeCache[key] = cachedRange;
            _rangeCacheBytes += length;
        }
        (Buffer b, DeviceMemory m, nint p) = _core.CreateHostBuffer(length, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit, MemCat.UploadRing);
        Core.Util.FastCopy.Copy(GuestMemory.ReadSpan(addr, (int)length), new Span<byte>((void*)p, (int)length));
        _uploadBytes += length;
        (cachedRange.Buffer, cachedRange.Memory, cachedRange.Stamp, cachedRange.LastUsedFrame) = (b, m, DirtyPages.Epoch, FrameCount);
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
    private static readonly bool s_smallCacheOn = Environment.GetEnvironmentVariable(Core.Util.EnvVars.VertexCacheSmall) == "1";
    private static readonly uint s_smallMinBytes = uint.TryParse(Environment.GetEnvironmentVariable(Core.Util.EnvVars.VertexCacheSmallMin), out uint smin) ? Math.Max(16u, smin) : 512u;
    private const uint SmallChunkBytes = 4u << 20;
    private const int SmallMaxChunks = 12;

    private sealed class SmallRange
    {
        public int Chunk;
        public uint Offset, Stamp, Generation;
        public byte Misses;
    }

    private readonly Dictionary<(uint Addr, uint Length, int Space), SmallRange> _smallCache = new();
    private readonly List<(Buffer Buffer, DeviceMemory Memory, nint Ptr)> _smallChunks = new();
    private uint _smallUsed, _smallGeneration;
    private long _statUniformBytes, _statMirrorCopyBytes;
    private long _statSmallHitBytes, _statSmallCopyBytes, _statSmallUploadBytes, _statSmallResets;

    private (Buffer Buffer, ulong Offset) UploadSmallRange(uint addr, uint length, ulong align)
    {
        SweepDirtyPages();
        (uint addr, uint length, int) key = (addr, length, SpaceIdOf(addr));
        if (_smallCache.TryGetValue(key, out SmallRange? smallRange))
        {
            bool written = DirtyPages.WrittenSince(addr, length, smallRange.Stamp) || DirtyPages.MarkedNow(addr, length);
            if (!written && smallRange.Generation == _smallGeneration && smallRange.Chunk >= 0)
            {
                _statSmallHitBytes += length;
                return (_smallChunks[smallRange.Chunk].Buffer, smallRange.Offset);
            }
            if (written && smallRange.Misses < 255)
            {
                smallRange.Misses++;
            }

            if (smallRange.Misses >= 2)
            {
                // volatile: the ring is cheaper than re-caching it every frame
                _statSmallUploadBytes += length;
                smallRange.Stamp = DirtyPages.Epoch;
                smallRange.Chunk = -1; // its cached copy is stale from now on

                return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
            }
        }
        else
        {
            _smallCache[key] = smallRange = new SmallRange();
        }

        uint off = (_smallUsed + 15) & ~15u;
        if (_smallChunks.Count == 0 || off + length > SmallChunkBytes)
        {
            if (_smallChunks.Count >= SmallMaxChunks)
            {
                ResetSmallCache();
            }

            (Buffer b, DeviceMemory m, nint p) = _core.CreateHostBuffer(SmallChunkBytes, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit, MemCat.UploadRing);
            _smallChunks.Add((b, m, p));
            off = 0;
        }
        int chunk = _smallChunks.Count - 1;
        Core.Util.FastCopy.Copy(GuestMemory.ReadSpan(addr, (int)length), new Span<byte>((void*)(_smallChunks[chunk].Ptr + (nint)off), (int)length));
        _smallUsed = off + length;
        _uploadBytes += length;
        _statSmallCopyBytes += length;
        (smallRange.Chunk, smallRange.Offset, smallRange.Stamp, smallRange.Generation) = (chunk, off, DirtyPages.Epoch, _smallGeneration);
        return (_smallChunks[chunk].Buffer, off);
    }

    /// <summary>The arena is full: every entry is invalidated (their volatility kept) and the chunks freed after the frame.</summary>
    private void ResetSmallCache()
    {
        foreach ((Buffer b, DeviceMemory m, nint _) in _smallChunks)
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

    private void DestroySmallCache()
    {
        foreach ((Buffer b, DeviceMemory m, nint _) in _smallChunks)
        {
            _core.DestroyBuffer(b, m);
        }

        _smallChunks.Clear();
        _smallCache.Clear();
    }

    /// <summary>Frees ranges unused for 300 frames (all unused ones this frame when <paramref name="force"/>).</summary>
    private void EvictRanges(bool force)
    {
        _rangeSweepFrame = FrameCount;
        foreach (((uint Addr, uint Length, int Space) k, CachedRange? cachedRange) in _rangeCache.ToList())
        {
            if (cachedRange.LastUsedFrame >= FrameCount - (force ? 0 : 300))
            {
                continue;
            }

            _rangeCache.Remove(k);
            if (cachedRange.Volatile)
            {
                continue;
            }

            F.Overflow.Add((cachedRange.Buffer, cachedRange.Memory));
            _rangeCacheBytes -= k.Length;
        }
    }

    private void DestroyRangeCache()
    {
        foreach (CachedRange cachedRange in _rangeCache.Values)
        {
            if (!cachedRange.Volatile)
            {
                _core.DestroyBuffer(cachedRange.Buffer, cachedRange.Memory);
            }
        }

        _rangeCache.Clear();
        _rangeCacheBytes = 0;
        DestroySmallCache();
    }

    private string RangeCacheReport()
    {
        if (!s_vertexCacheOn)
        {
            return "";
        }

        double f = Math.Max(1.0, _workFrames);
        return $"; vertex/index cache: {_statRangeHits / f:F0} hits/frame ({_statRangeHitBytes / f / 1024:F0} KB/frame not uploaded), {_statRangeMisses / f:F0} copies/frame, {_rangeCache.Count} ranges ({_rangeCacheBytes >> 20} MB), {_statRangeVolatile} volatile ({_statRangeVolatileBytes / f / 1024:F0} KB/frame through the ring)"
            + $"; small ranges: {_statSmallUploadBytes / f / 1024:F0} KB/frame through the ring"
            + (s_smallCacheOn ? $", cached (VITASHARP_VERTEX_CACHE_SMALL) {_statSmallHitBytes / f / 1024:F0} KB/frame reused, {_statSmallCopyBytes / f / 1024:F0} KB/frame copied, {_smallCache.Count} entries, {_smallChunks.Count} chunks, {_statSmallResets} resets" : "");
    }
}
