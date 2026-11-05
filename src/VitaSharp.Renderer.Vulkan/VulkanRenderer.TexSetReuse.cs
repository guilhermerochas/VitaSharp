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
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    static readonly bool TexSetReuse = Environment.GetEnvironmentVariable(EnvVars.TexSetReuse) == "1";

    sealed class ReuseEntry
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
    }

    readonly Dictionary<ulong, ReuseEntry> _reuseV = new(), _reuseF = new();
    /// <summary>Bumped whenever an image view that a kept set may reference is released or replaced.</summary>
    long _texEpoch;
    /// <summary>The colour target the last resolved texture came from (null: not a colour target).</summary>
    RenderTarget? _texSrcRt;
    bool _reuseOk;
    int _reuseDeps;
    readonly (RenderTarget Rt, long Version)[] _reuseDepScratch = new (RenderTarget, long)[16];
    long _statReuseHits, _statReuseMisses, _statReuseRejected;

    static ulong ReuseKey(GxmTextureDesc[] textures, uint mask, uint cube, int units)
    {
        ulong h = 14695981039346656037UL ^ mask ^ ((ulong)cube << 32) ^ ((ulong)units << 56);
        for (uint m = mask; m != 0; m &= m - 1)
        {
            var d = textures[System.Numerics.BitOperations.TrailingZeroCount(m)];
            h = (h ^ d.W0) * 1099511628211UL; h = (h ^ d.W1) * 1099511628211UL;
            h = (h ^ d.W2) * 1099511628211UL; h = (h ^ d.W3) * 1099511628211UL;
        }
        return h;
    }

    /// <summary>After resolving one unit: whether the stage can still be kept, and what it depends on.</summary>
    void NoteReuseSource(ImageView v)
    {
        if (v.Handle == 0) return; // the dummy texture: stable
        var kind = _texSrc & TexSource.KindMask;
        if ((_texSrc & (TexSource.SceneStart | TexSource.LodLowered | TexSource.Failed)) != 0) { _reuseOk = false; return; }
        if (kind == TexSource.Memory) return;
        if (kind is TexSource.RenderTarget or TexSource.Region or TexSource.Reinterpret or TexSource.RtMipmapped
            && _texSrcRt is { } rt && rt != _rt && _reuseDeps < 16)
        {
            _reuseDepScratch[_reuseDeps++] = (rt, rt.Version);
            return;
        }
        _reuseOk = false;
    }

    DescriptorSet? ReusedTextureSet(bool vertexStage, GxmTextureDesc[] textures, uint mask, uint cube, int units)
    {
        var map = vertexStage ? _reuseV : _reuseF;
        if (!map.TryGetValue(ReuseKey(textures, mask, cube, units), out var e)) { _statReuseMisses++; return null; }
        if (e.Serial != _frameSerial || e.Epoch != _texEpoch || e.Mask != mask || e.Cube != cube || e.Units != units) { _statReuseMisses++; return null; }
        for (uint m = mask; m != 0; m &= m - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(m);
            if (e.Descs[i] != textures[i]) { _statReuseMisses++; return null; }
        }
        for (int i = 0; i < e.DepCount; i++)
        {
            var (rt, ver) = e.Deps[i];
            if (rt.Version != ver || rt == _rt) { _statReuseMisses++; return null; }
            rt.LastTouchedFrame = FrameCount;
            rt.LastUsedFrame = FrameCount;
        }
        // what resolving would have noted: the views are sampled by this draw (scene-break hazards)
        for (int i = 0; i < e.ViewCount; i++) NoteSampled(e.Views[i]);
        _statReuseHits++;
        return e.Set;
    }

    void StoreReusedSet(bool vertexStage, GxmTextureDesc[] textures, uint mask, uint cube, int units, DescriptorSet set, DescriptorImageInfo* infos)
    {
        if (!_reuseOk) { if (TexSetReuse) _statReuseRejected++; return; }
        var map = vertexStage ? _reuseV : _reuseF;
        if (map.Count > 4096) map.Clear();
        ulong key = ReuseKey(textures, mask, cube, units);
        if (!map.TryGetValue(key, out var e)) map[key] = e = new ReuseEntry();
        e.Mask = mask; e.Cube = cube; e.Units = units; e.Serial = _frameSerial; e.Epoch = _texEpoch; e.Set = set;
        e.ViewCount = 0;
        for (uint m = mask; m != 0; m &= m - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(m);
            e.Descs[i] = textures[i];
            if (infos[i].ImageView.Handle != 0) e.Views[e.ViewCount++] = infos[i].ImageView;
        }
        e.DepCount = _reuseDeps;
        for (int i = 0; i < _reuseDeps; i++) e.Deps[i] = _reuseDepScratch[i];
    }

    string TexSetReuseReport() => !TexSetReuse ? "" :
        $"; texture set reuse (VITASHARP_TEXSET_REUSE): {_statReuseHits / Math.Max(1.0, _workFrames):F0} hits/frame, {_statReuseMisses / Math.Max(1.0, _workFrames):F0} misses, {_statReuseRejected / Math.Max(1.0, _workFrames):F0} not kept";
}
