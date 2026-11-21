using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// What the renderer made of a process that ended (<see cref="ProcessEndedCommand"/>). On the Vita a process's memory
/// goes with it when it ends (the kernel frees its memory blocks, CDRAM included), and with it everything the GPU held
/// there. The renderer's caches outlived it: God of War's 720x408 and 960x544 surfaces (CDRAM at 0x6B8xxxxx), the
/// textures and vertex copies of its CDRAM and the reinterpreted views of its surfaces stayed after the shell closed it
/// and Metal Gear Solid ran on, device memory held for nothing and stale data at addresses the next allocation of
/// another process reuses (a texture checked in the current frame is not checked again, so a process drawing from the
/// same CDRAM address in that frame was shown the ended one's texels).
/// <para>Ownership is the memory's: what was read from the process's address space (<see cref="GpuCommand.Space"/>)
/// or from the CDRAM and kernel blocks it held (<see cref="ProcessEndedCommand.SharedMemory"/>, still allocated when
/// the command runs: the process's memory is freed once the GPU is idle), plus the surfaces its scenes rendered last
/// (<see cref="RenderTarget.LastPid"/>; a surface in memory of another process that it rendered into last). What
/// another process's open or suspended scene renders into stays, as does whatever lives in memory of other processes
/// (the shell). Every image and buffer is destroyed once the frames in flight that may use it completed
/// (<see cref="DestroyAfterGpu"/>).</para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>What <see cref="DropEndedProcess"/> released, by kind (logged, and read by tests).</summary>
    public readonly record struct ProcessEndRelease(int RenderTargets, int DepthTargets, int Textures, int Views, int VertexRanges, int KeptContents, int TransferImages)
    {
        public int Total => RenderTargets + DepthTargets + Textures + Views + VertexRanges + KeptContents + TransferImages;
    }

    /// <summary>The last process end's release (tests).</summary>
    public ProcessEndRelease LastProcessEndRelease { get; private set; }

    /// <summary>Bytes the renderer holds for <paramref name="category"/> (device-local, host): tests and reports.</summary>
    public (long Device, long Host) HeldBytes(MemCat category) => _core.CategoryBytes(category);

    /// <summary>The ended process's memory: its private space and the shared blocks it held.</summary>
    private readonly struct EndedMemory((uint Address, uint Size)[] shared, int spaceId)
    {
        private readonly (uint Address, uint Size)[] _shared = shared;

        public int SpaceId { get; } = spaceId;

        public bool Any => SpaceId != 0 || _shared.Length > 0;

        /// <summary>[<paramref name="start"/>, + <paramref name="size"/>) read in space <paramref name="dataSpaceId"/>
        /// (0: shared memory) is the ended process's memory.</summary>
        public bool Holds(uint start, ulong size, int dataSpaceId)
        {
            if (dataSpaceId != 0)
            {
                return dataSpaceId == SpaceId;
            }

            return SharedHolds(start, size);
        }

        /// <summary>The range overlaps a CDRAM or kernel block the process held.</summary>
        public bool SharedHolds(uint start, ulong size)
        {
            ulong end = start + Math.Max(size, 1);
            foreach ((uint address, uint blockSize) in _shared)
            {
                if (start < (ulong)address + blockSize && address < end)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Releases everything of the process that ended (see the class notes).</summary>
    private void DropEndedProcess(ProcessEndedCommand processEndedCommand)
    {
        int pid = processEndedCommand.Pid;
        var ended = new EndedMemory(processEndedCommand.SharedMemory, processEndedCommand.Space?.Id ?? 0);
        // host work (pid 0) is never a process's: such an end names no surfaces, only memory
        if (pid == 0 && !ended.Any)
        {
            return;
        }

        int targets = 0, depths = 0, textures = 0, views = 0;
        foreach (KeyValuePair<(uint, uint, uint, uint), RenderTarget> kv in _renderTargets.ToList())
        {
            RenderTarget renderTarget = kv.Value;
            bool owned = (pid != 0 && renderTarget.LastPid == pid) || ended.SharedHolds(renderTarget.Address, 1);
            if (!owned || (_inScene && renderTarget == _renderTarget) || renderTarget == _cpuFramebuffer || InSuspendedScene(renderTarget))
            {
                continue;
            }

            _renderTargets.Remove(kv.Key);
            _rtRangesValid = false;
            _targetGen++;
            views += ReleaseAliasesOf(renderTarget);
            ReleaseRenderTarget(renderTarget, false);
            if (renderTarget == _renderTarget)
            {
                _renderTarget = null; // the last scene's target, ended: nothing refers to it any more
            }

            targets++;
        }

        foreach (KeyValuePair<(uint, uint, uint, SampleCountFlags), DepthTarget> kv in _depthTargets.ToList())
        {
            DepthTarget depthTarget = kv.Value;
            bool owned = (pid != 0 && depthTarget.LastPid == pid) || ended.SharedHolds(depthTarget.Address, 1);
            if (!owned || (_inScene && depthTarget == _depth) || InSuspendedScene(depthTarget))
            {
                continue;
            }

            ReleaseDepthTarget(kv.Key, depthTarget, false);
            if (depthTarget == _depth)
            {
                _depth = null;
            }

            depths++;
        }

        int ranges = 0, kept = 0, transfers = 0;
        if (ended.Any)
        {
            foreach (KeyValuePair<TexKey, Texture> kv in _textures.ToList())
            {
                if (ended.Holds(kv.Value.DataStart, kv.Value.DataSize, kv.Value.SpaceId))
                {
                    ReleaseTexture(kv.Key, kv.Value, false);
                    textures++;
                }
            }

            views += ReleaseAliasesIn(ended);
            ranges = ReleaseVertexRangesIn(ended);
            kept = ReleaseKeptContentsIn(ended);
            transfers = ReleaseTransferImagesIn(ended);
        }

        var release = new ProcessEndRelease(targets, depths, textures, views, ranges, kept, transfers);
        LastProcessEndRelease = release;
        if (release.Total == 0)
        {
            return;
        }

        BumpTexEpoch(0);
        _texSrcTex = null;
        _texSrcRt = null;
        Log.Info($"[vram] process 0x{pid:X8} ended: releasing {targets} render targets, {depths} depth targets, {textures} textures, {views} reinterpreted views, " +
                 $"{ranges} vertex/index copies, {kept} kept texture contents and {transfers} transfer images once the frames in flight complete; {VramSummary()}");
        // queued after the releases: runs once they ran (the memory the process's caches held is back)
        DestroyAfterGpu(() => Log.Info($"[vram] process 0x{pid:X8} released: {VramSummary()}"));
    }

    /// <summary>The reinterpreted views copied from <paramref name="renderTarget"/> (a released surface takes them along).</summary>
    private int ReleaseAliasesOf(RenderTarget renderTarget)
    {
        int released = 0;
        foreach (KeyValuePair<(uint Addr, uint Format, uint W, uint H, uint Row, bool Gamma), RtAlias> kv in _rtAliases.ToList())
        {
            if (kv.Value.Source == renderTarget)
            {
                _rtAliases.Remove(kv.Key);
                ReleaseImage(kv.Value.Image, kv.Value.View, kv.Value.Mem, false);
                released++;
            }
        }

        return released;
    }

    /// <summary>Reinterpreted views of the ended process's CDRAM (whatever surface they were copied from).</summary>
    private int ReleaseAliasesIn(in EndedMemory ended)
    {
        int released = 0;
        foreach (KeyValuePair<(uint Addr, uint Format, uint W, uint H, uint Row, bool Gamma), RtAlias> kv in _rtAliases.ToList())
        {
            if (ended.SharedHolds(kv.Key.Addr, (ulong)kv.Key.Row * kv.Key.H))
            {
                _rtAliases.Remove(kv.Key);
                ReleaseImage(kv.Value.Image, kv.Value.View, kv.Value.Mem, false);
                released++;
            }
        }

        return released;
    }

    /// <summary>Vertex and index copies of the ended process's memory (VulkanRenderer.VertexCache.cs).</summary>
    private int ReleaseVertexRangesIn(in EndedMemory ended)
    {
        int released = 0;
        foreach (KeyValuePair<(uint Addr, uint Length, int Space), CachedRange> kv in _rangeCache.ToList())
        {
            if (!ended.Holds(kv.Key.Addr, kv.Key.Length, kv.Key.Space))
            {
                continue;
            }

            _rangeCache.Remove(kv.Key);
            released++;
            if (kv.Value.Volatile)
            {
                continue;
            }

            _rangeCacheBytes -= kv.Key.Length;
            (Buffer buffer, DeviceMemory memory) = (kv.Value.Buffer, kv.Value.Memory);
            DestroyAfterGpu(() => _core.DestroyBuffer(buffer, memory));
        }

        // small copies live in shared chunks: the entries go (the chunk space returns at the next arena reset)
        foreach (KeyValuePair<(uint Addr, uint Length, int Space), SmallRange> kv in _smallCache.ToList())
        {
            if (ended.Holds(kv.Key.Addr, kv.Key.Length, kv.Key.Space))
            {
                _smallCache.Remove(kv.Key);
                released++;
            }
        }

        return released;
    }

    /// <summary>Images kept for earlier contents of the ended process's textures (VulkanRenderer.TexContentCache.cs).</summary>
    private int ReleaseKeptContentsIn(in EndedMemory ended)
    {
        int released = 0;
        foreach (KeyValuePair<ContentKey, KeptImage> kv in _contentCache.ToList())
        {
            if (ended.Holds(kv.Value.Address, kv.Value.Bytes, kv.Value.SpaceId))
            {
                _contentCache.Remove(kv.Key);
                _contentCacheBytes -= kv.Value.Mem.Size;
                ReleaseImage(kv.Value.Image, kv.Value.View, kv.Value.Mem, false);
                released++;
            }
        }

        return released;
    }

    /// <summary>Transfer images of the ended process's memory whose CPU transfer ran (VulkanRenderer.TransferDefer.cs;
    /// a pending one runs when its frame retires and goes with the usual eviction after).</summary>
    private int ReleaseTransferImagesIn(in EndedMemory ended)
    {
        int released = 0;
        foreach (KeyValuePair<uint, TransferImage> kv in _xferImages.ToList())
        {
            TransferImage image = kv.Value;
            if (image.Pending == null && ended.Holds(kv.Key, (ulong)image.DstStride * image.Height, image.SpaceId))
            {
                _xferImages.Remove(kv.Key);
                ReleaseTransferImage(image);
                released++;
            }
        }

        return released;
    }
}
