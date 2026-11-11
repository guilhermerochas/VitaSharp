using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// VITASHARP_TEXSET_REUSE=1: a stage whose texture units hold the same descriptors as a stage built earlier in the
/// frame reuses that descriptor set without resolving the textures again. Resolving every unit of every draw (texture
/// cache lookups, render-target overlap searches, region checks) was about 6 ms of the GPU worker's frame in Uncharted
/// on the phone, for ~540 draws that mostly bind the same few materials.
/// <para>
/// Only stages whose units all resolved to cached guest-memory textures or to colour targets other than the one being
/// rendered are kept, each with the targets it read and their <see cref="RenderTarget.Version"/>. A kept set is used
/// again while: the frame's descriptor pools were not reset, nothing released or replaced an image view since
/// (<see cref="_texEpoch"/>), no target it read changed, and none of them is the current scene's target. Depth/stencil
/// sources, scene-start copies, LOD-lowered views and cube emulation are never kept.
/// </para>
/// <para>
/// VITASHARP_TEXSET_PERSIST=1: kept sets are allocated from a pool of their own, so they outlive the frame's pool resets
/// (about two submits per presented frame in Uncharted, each of which made every first draw of a material resolve its
/// textures and allocate and write a new set). A set from an earlier submit is used again after its memory textures
/// were checked against guest memory once in this frame (a changed texture is re-uploaded into the same image, so its
/// view stays; a new image is a miss) and its targets' read barriers were placed as a resolve would. The pool is
/// recycled (every kept set forgotten) when it runs out.
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private static readonly bool s_texSetPersist = Environment.GetEnvironmentVariable(EnvVars.TexSetPersist) == "1";
    private static readonly bool s_texSetReuse = s_texSetPersist || Environment.GetEnvironmentVariable(EnvVars.TexSetReuse) == "1";

    private sealed class ReuseEntry
    {
        public readonly GxmTextureDesc[] Descs = new GxmTextureDesc[16];
        public uint Mask, Cube;
        public int Units;
        public long Serial, Epoch;
        public DescriptorSet Set;
        public readonly ImageView[] Views = new ImageView[16];
        public int ViewCount;
        public readonly (RenderTarget Rt, long Version)[] Deps = new (RenderTarget, long)[16];
        public int DepCount;
        /// <summary>Guest-memory textures the set reads (unit, cache entry): checked once per frame when the set
        /// persists across submits.</summary>
        public readonly (int Unit, Texture Tex)[] Mem = new (int, Texture)[16];
        public int MemCount;
    }

    private readonly Dictionary<ulong, ReuseEntry> _reuseV = new(), _reuseF = new();
    /// <summary>Bumped whenever an image view that a kept set may reference is released or replaced.</summary>
    private long _texEpoch;
    /// <summary>Why the epoch moved (report): 0 an image released, 1 a target's sampled view replaced, 2 a reinterpreted alias replaced.</summary>
    private readonly long[] _epochBumps = new long[3];
    private readonly Dictionary<string, long> _releaseSites = new();
    private void BumpTexEpoch(int site)
    {
        _texEpoch++;
        _epochBumps[site]++;
    }
    /// <summary>The colour target the last resolved texture came from (null: not a colour target).</summary>
    private RenderTarget? _texSrcRt;
    /// <summary>The texture cache entry the last resolved guest-memory texture came from.</summary>
    private Texture? _texSrcTex;
    private bool _reuseOk;
    private int _reuseDeps, _reuseMem;
    private readonly (RenderTarget Rt, long Version)[] _reuseDepScratch = new (RenderTarget, long)[16];
    private readonly (int Unit, Texture Tex)[] _reuseMemScratch = new (int, Texture)[16];
    private long _statReuseHits, _statReuseMisses, _statReuseRejected, _statReuseOld, _statReuseRechecks, _statTexPoolRecycles;
    /// <summary>VITASHARP_TEXSET_PERSIST: the pool kept sets are allocated from (recycled when full).</summary>
    private DescriptorPool _texSetPool;
    private const int TexSetPoolSets = 4096;

    private static ulong ReuseKey(GxmTextureDesc[] textures, uint mask, uint cube, int units)
    {
        ulong h = 14695981039346656037UL ^ mask ^ ((ulong)cube << 32) ^ ((ulong)units << 56);
        for (uint m = mask; m != 0; m &= m - 1)
        {
            GxmTextureDesc d = textures[System.Numerics.BitOperations.TrailingZeroCount(m)];
            h = (h ^ d.W0) * 1099511628211UL;
            h = (h ^ d.W1) * 1099511628211UL;
            h = (h ^ d.W2) * 1099511628211UL;
            h = (h ^ d.W3) * 1099511628211UL;
        }
        return h;
    }

    /// <summary>After resolving one unit: whether the stage can still be kept, and what it depends on.</summary>
    private void NoteReuseSource(int unit, ImageView v)
    {
        if (v.Handle == 0)
        {
            return; // the dummy texture: stable
        }

        TexSource kind = _texSrc & TexSource.KindMask;
        if ((_texSrc & (TexSource.SceneStart | TexSource.LodLowered | TexSource.Failed)) != 0)
        {
            _reuseOk = false;
            return;
        }
        if (kind == TexSource.Memory)
        {
            if (!s_texSetPersist)
            {
                return;
            }

            if (_texSrcTex is { } t && _reuseMem < 16)
            {
                _reuseMemScratch[_reuseMem++] = (unit, t);
                return;
            }
            _reuseOk = false;
            return;
        }
        if (kind is TexSource.RenderTarget or TexSource.Region or TexSource.Reinterpret or TexSource.RtMipmapped
            && _texSrcRt is { } rt && rt != _renderTarget && _reuseDeps < 16)
        {
            _reuseDepScratch[_reuseDeps++] = (rt, rt.Version);
            return;
        }
        _reuseOk = false;
    }

    private DescriptorSet? ReusedTextureSet(bool vertexStage, GxmTextureDesc[] textures, uint mask, uint cube, int units)
    {
        Dictionary<ulong, ReuseEntry> map = vertexStage ? _reuseV : _reuseF;
        if (!map.TryGetValue(ReuseKey(textures, mask, cube, units), out ReuseEntry? e))
        {
            _statReuseMisses++;
            return null;
        }
        bool fresh = e.Serial == _frameSerial;
        if ((!fresh && !s_texSetPersist) || e.Epoch != _texEpoch || e.Mask != mask || e.Cube != cube || e.Units != units)
        {
            _statReuseMisses++;
            return null;
        }
        for (uint m = mask; m != 0; m &= m - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(m);
            if (e.Descs[i] != textures[i])
            {
                _statReuseMisses++;
                return null;
            }
        }
        if (!fresh)
        {
            // a set from an earlier submit: its memory textures are validated against guest memory once per frame, as
            // a resolve would (a changed texture is uploaded into the same image; a new image means a new view)
            for (int i = 0; i < e.MemCount; i++)
            {
                (int unit, Texture? tex) = e.Mem[i];
                if (tex.CheckedFrame == FrameCount)
                {
                    tex.LastUsedFrame = FrameCount;
                    continue;
                }
                _statReuseRechecks++;
                (ImageView v, ImageLayout _) = ResolveTextureOrSkip(textures[unit], false);
                if (v.Handle != tex.View.Handle || _texSrcTex != tex)
                {
                    _statReuseMisses++;
                    return null;
                }
            }
        }
        for (int i = 0; i < e.DepCount; i++)
        {
            (RenderTarget? rt, long ver) = e.Deps[i];
            if (rt.Version != ver || rt == _renderTarget)
            {
                _statReuseMisses++;
                return null;
            }
            rt.LastTouchedFrame = FrameCount;
            rt.LastUsedFrame = FrameCount;
        }
        // what resolving would have done: the targets' read barriers (a target copied or uploaded into since the set was
        // built is dirty without a version change), and the views are sampled by this draw (scene-break hazards)
        if (!fresh)
        {
            for (int i = 0; i < e.DepCount; i++)
            {
                SyncRenderTargetForRead(e.Deps[i].Rt);
            }

            _statReuseOld++;
        }
        for (int i = 0; i < e.ViewCount; i++)
        {
            NoteSampled(e.Views[i]);
        }

        _statReuseHits++;
        return e.Set;
    }

    private void StoreReusedSet(bool vertexStage, GxmTextureDesc[] textures, uint mask, uint cube, int units, DescriptorSet set, DescriptorImageInfo* infos)
    {
        if (!_reuseOk)
        {
            if (s_texSetReuse)
            {
                _statReuseRejected++;
            }

            return;
        }
        Dictionary<ulong, ReuseEntry> map = vertexStage ? _reuseV : _reuseF;
        if (map.Count > 4096)
        {
            map.Clear();
        }

        ulong key = ReuseKey(textures, mask, cube, units);
        if (!map.TryGetValue(key, out ReuseEntry? e))
        {
            map[key] = e = new ReuseEntry();
        }

        e.Mask = mask;
        e.Cube = cube;
        e.Units = units;
        e.Serial = _frameSerial;
        e.Epoch = _texEpoch;
        e.Set = set;
        e.ViewCount = 0;
        for (uint m = mask; m != 0; m &= m - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(m);
            e.Descs[i] = textures[i];
            if (infos[i].ImageView.Handle != 0)
            {
                e.Views[e.ViewCount++] = infos[i].ImageView;
            }
        }
        e.DepCount = _reuseDeps;
        for (int i = 0; i < _reuseDeps; i++)
        {
            e.Deps[i] = _reuseDepScratch[i];
        }

        e.MemCount = _reuseMem;
        for (int i = 0; i < _reuseMem; i++)
        {
            e.Mem[i] = _reuseMemScratch[i];
        }
    }

    /// <summary>A set that will be kept across submits (VITASHARP_TEXSET_PERSIST): from the persistent texture-set pool.</summary>
    private DescriptorSet AllocKeptTexSet(DescriptorSetLayout layout)
    {
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[1]; // outside the loop (CA2014)
        while (true)
        {
            if (_texSetPool.Handle == 0)
            {
                sizes[0] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, TexSetPoolSets * 16);
                var pci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = TexSetPoolSets, PoolSizeCount = 1, PPoolSizes = sizes };
                _vk.CreateDescriptorPool(_dev, in pci, null, out _texSetPool).Ok("texture set pool");
            }
            var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _texSetPool, DescriptorSetCount = 1, PSetLayouts = &layout };
            Result r = _vk.AllocateDescriptorSets(_dev, in ai, out DescriptorSet set);
            if (r == Result.Success)
            {
                _statSets++;
                return set;
            }
            // full: every kept set is forgotten and the pool goes once the GPU is done with the frames that bind them
            DescriptorPool old = _texSetPool;
            _texSetPool = default;
            _statTexPoolRecycles++;
            DestroyAfterGpu(() => _vk.DestroyDescriptorPool(_dev, old, null));
            ForgetKeptTexSets();
        }
    }

    private void ForgetKeptTexSets()
    {
        _reuseV.Clear();
        _reuseF.Clear();
        _texSetV.Serial = _texSetF.Serial = -1;
    }

    private void DestroyTexSetPool()
    {
        if (_texSetPool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_dev, _texSetPool, null);
        }

        _texSetPool = default;
        ForgetKeptTexSets();
    }

    private string TexSetReuseReport() => !s_texSetReuse ? "" :
        $"; texture set reuse (VITASHARP_TEXSET_REUSE): {_statReuseHits / Math.Max(1.0, _workFrames):F0} hits/frame, {_statReuseMisses / Math.Max(1.0, _workFrames):F0} misses, {_statReuseRejected / Math.Max(1.0, _workFrames):F0} not kept"
        + $"; view epoch bumps/frame: {_epochBumps[0] / Math.Max(1.0, _workFrames):F2} released ({string.Join(" ", _releaseSites.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} {kv.Value / Math.Max(1.0, _workFrames):F2}"))}), {_epochBumps[1] / Math.Max(1.0, _workFrames):F2} sampled view, {_epochBumps[2] / Math.Max(1.0, _workFrames):F2} alias"
        + (!s_texSetPersist ? "" : $"; persistent sets (VITASHARP_TEXSET_PERSIST): {_statReuseOld / Math.Max(1.0, _workFrames):F0} hits/frame from earlier submits, {_statReuseRechecks / Math.Max(1.0, _workFrames):F1} texture re-checks/frame, {_statTexPoolRecycles} pool recycles");
}
