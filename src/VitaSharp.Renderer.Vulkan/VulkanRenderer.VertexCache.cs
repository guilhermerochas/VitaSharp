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
    }

    const uint MinBytes = 16 * 1024;
    const long MaxCachedBytes = 96L << 20;
    static readonly bool VertexCacheOn = DirtyPages.Enabled
        && Environment.GetEnvironmentVariable(Core.Util.EnvVars.VertexCache) != "0";
    readonly Dictionary<(uint Addr, uint Length, int Space), CachedRange> _rangeCache = new();
    long _rangeCacheBytes, _statRangeHits, _statRangeHitBytes, _statRangeMisses, _rangeSweepFrame;

    /// <summary>The guest range as a vertex/index buffer: a cached copy when it is unchanged, else the upload ring.</summary>
    (Buffer Buffer, ulong Offset) UploadRange(uint addr, uint length, ulong align = 4)
    {
        if (!VertexCacheOn || length < MinBytes)
            return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
        SweepDirtyPages();
        var key = (addr, length, SpaceIdOf(addr));
        if (_rangeCache.TryGetValue(key, out var e) && !DirtyPages.WrittenSince(addr, length, e.Stamp) && !DirtyPages.MarkedNow(addr, length))
        {
            e.LastUsedFrame = FrameCount;
            _statRangeHits++;
            _statRangeHitBytes += length;
            return (e.Buffer, 0);
        }
        _statRangeMisses++;
        if (e == null && _rangeCacheBytes + length > MaxCachedBytes) EvictRanges(force: true);
        if (e == null && _rangeCacheBytes + length > MaxCachedBytes) return UploadSpan(GuestMemory.ReadSpan(addr, (int)length), align);
        if (e != null) F.Overflow.Add((e.Buffer, e.Memory)); // frames in flight may still read the old copy
        else { e = new CachedRange(); _rangeCache[key] = e; _rangeCacheBytes += length; }
        var (b, m, p) = _core.CreateHostBuffer(length, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit, MemCat.UploadRing);
        Core.Util.FastCopy.Copy(GuestMemory.ReadSpan(addr, (int)length), new Span<byte>((void*)p, (int)length));
        _uploadBytes += length;
        (e.Buffer, e.Memory, e.Stamp, e.LastUsedFrame) = (b, m, DirtyPages.Epoch, FrameCount);
        if (_rangeSweepFrame + 300 < FrameCount) EvictRanges(force: false);
        return (b, 0);
    }

    /// <summary>Frees ranges unused for 300 frames (all unused ones this frame when <paramref name="force"/>).</summary>
    void EvictRanges(bool force)
    {
        _rangeSweepFrame = FrameCount;
        foreach (var (k, e) in _rangeCache.ToList())
        {
            if (e.LastUsedFrame >= FrameCount - (force ? 0 : 300)) continue;
            F.Overflow.Add((e.Buffer, e.Memory));
            _rangeCache.Remove(k);
            _rangeCacheBytes -= k.Length;
        }
    }

    void DestroyRangeCache()
    {
        foreach (var e in _rangeCache.Values) _core.DestroyBuffer(e.Buffer, e.Memory);
        _rangeCache.Clear();
        _rangeCacheBytes = 0;
    }

    string RangeCacheReport() => VertexCacheOn
        ? $"; vertex/index cache: {_statRangeHits / Math.Max(1.0, _workFrames):F0} hits/frame ({_statRangeHitBytes / Math.Max(1.0, _workFrames) / 1024:F0} KB/frame not uploaded), {_statRangeMisses / Math.Max(1.0, _workFrames):F0} copies/frame, {_rangeCache.Count} ranges ({_rangeCacheBytes >> 20} MB)"
        : "";
}
