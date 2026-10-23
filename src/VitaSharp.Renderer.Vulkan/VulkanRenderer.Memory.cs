using System.Diagnostics;
using System.Text;
using Silk.NET.Vulkan;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// GPU memory: VRAM visibility and graceful degradation when memory runs out (docs/notes/vram.md).
/// <para>
/// Visibility: <see cref="VkCore"/> counts every allocation by <see cref="MemCat"/> (image pool sub-allocations and
/// whole buffers) and reads the per-heap budget and usage of VK_EXT_memory_budget. <see cref="VramSummary"/> combines
/// both; it is logged at startup, every 600 presents in debug mode, in the PERF vram line (periodic and end of run),
/// in renderer.txt of captures and in every allocation-failure message.
/// </para>
/// <para>
/// Eviction: everything the renderer caches can be recreated from guest memory or from a render target: textures
/// (re-uploaded), render-target regions / reinterpreted / mip-chained views and depth/stencil textures (copied
/// again), render targets whose content is in guest memory (written back, recreated by the upload from memory) and
/// idle depth targets. Each carries the GPU frame it was last used in (<see cref="FrameCount"/> = the frame being
/// recorded), so whatever the recording frame uses is never evicted.
/// <list type="bullet">
/// <item>Out of memory (<see cref="Reclaim"/>, from <see cref="VkCore.AllocateMemory"/> / <see cref="ImageMemoryPool.Allocate"/>):
/// wait for the frames in flight, run their deferred destroys, evict least recently used caches (level 0: unused for
/// a few frames, up to the request + 128 MB; level 1: everything not used by the recording frame), destroy them at
/// once, return empty pool pages to the driver and retry. If the allocation still fails the resource is skipped:
/// a texture binds the dummy texture, a draw or scene that cannot get its target is dropped (rate-limited warning
/// with the budget numbers); nothing throws past <see cref="Execute"/>.</item>
/// <item>Pressure (<see cref="TrimForPressure"/>, every 4th present): when device-local usage is above 90% of the
/// budget (or of the VITASHARP_VRAM_BUDGET_MB test cap), least recently used caches are evicted (destroyed once
/// the GPU is done with them) until the pool has a reserve of free space, and empty pages are released.</item>
/// <item>Expiry (every 256 presents): textures and render-target views unused for <see cref="ExpiryFrames"/> GPU
/// frames are evicted, so streaming games do not accumulate textures of memory that now holds something else.</item>
/// </list>
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Usage over the budget (or test cap) at which caches are trimmed at frame boundaries.</summary>
    const double PressureHigh = 0.90;
    /// <summary>Free pool space kept under pressure, as a fraction of the budget (cap) and at least this many bytes.</summary>
    const double PressureReserve = 0.08;
    const ulong MinReserve = 48ul << 20;
    /// <summary>Caches unused for this many GPU frames (about 3-4 presents) may be trimmed under pressure.</summary>
    const long TrimMinAge = 8;
    /// <summary>Caches unused this long (GPU frames, ~1.5 min at 30 fps with 2 submits per present) expire.</summary>
    const long ExpiryFrames = 6000;
    /// <summary>Render targets whose content is in guest memory may be evicted once unused this long (GPU frames).</summary>
    const long RtIdleFrames = 120;
    /// <summary>Render targets or depth targets whose content would be lost: only as the last resort, unused this long.</summary>
    const long RtLostIdleFrames = 600;

    long _evTextures, _evTextureBytes, _evViews, _evViewBytes, _evTargets, _evTargetBytes, _evDepth, _evDepthBytes;
    long _reclaims, _reclaimsLevel1, _reclaimTicks, _pressureTrims, _expirySweeps;
    long _oomDraws, _oomScenes, _oomTextures, _oomOther;
    bool _reclaiming;
    /// <summary>Multisampled images with 2 samples (the rest have 4); a counter so other threads need not enumerate.</summary>
    int _ms2xImages;
    long _lastOomLog, _oomSinceLog;
    int _pressureLogged;
    /// <summary>Destroys waiting for a submitted frame (SubmitSeq) to complete (see <see cref="DestroyAfterGpu"/>).</summary>
    readonly List<(long Seq, Action Destroy)> _afterGpu = new();

    void InitMemory()
    {
        _core.Reclaim = Reclaim;
        _core.DescribeMemory = () => VramSummary();
        Log.Info($"[vram] {VramSummary()}");
        if (_core.BudgetCap > 0) Log.Warn($"[vram] test switch VITASHARP_VRAM_BUDGET_MB={_core.BudgetCap >> 20}: the renderer may hold at most {_core.BudgetCap >> 20} MB of device-local memory");
    }

    /// <summary>Heap budgets/usage plus what the renderer holds by category (with counts).</summary>
    public string VramSummary() => _core.MemorySummary(VramDetail);

    string VramDetail(MemCat c)
    {
        // read on other threads too (watchdog, captures): counts may be slightly stale
        try
        {
            switch (c)
            {
                case MemCat.RenderTarget: return $"{_renderTargets.Count} targets";
                case MemCat.Msaa:
                {
                    int n2 = _ms2xImages, n4 = _msImages.Count - n2;
                    return $"{n4} at 4x" + (n2 > 0 ? $", {n2} at 2x" : "") + (_ssImages.Count > 0 ? $", {_ssImages.Count} supersampled 2x2" : "");
                }
                case MemCat.Depth: return $"{_depthTargets.Count} targets";
                case MemCat.Texture: return $"{_textures.Count} textures";
                case MemCat.Region: return $"{_rtRegions.Count} regions, {_rtAliases.Count} reinterpreted, {_rtMipTextures.Count} mip-chained, {_depthTextures.Count + _stencilTextures.Count} depth/stencil, {_sceneStartCopies.Count} scene-start, {_fbCopies.Count + (_fbCopy.Handle != 0 ? 1 : 0) + _msFetchCopies.Count} fetch copies";
                case MemCat.DebugRing:
                    var parts = new List<string>();
                    if (_replay != null) parts.Add("replay on");
                    if (_drawRing != null) parts.Add("draw ring on (19 MB managed memory, no VRAM)");
                    if (_frames.Any(f => f?.Nan != null)) parts.Add("NaN scan buffers");
                    return string.Join(", ", parts);
                case MemCat.UploadRing: return $"{FramesInFlight} x {RingSize >> 20} MB";
                default: return "";
            }
        }
        catch { return ""; }
    }

    /// <summary>"PERF vram: ..." (end of run, renderer.txt): the summary plus eviction and failure counters.</summary>
    string VramReport() =>
        $"PERF vram: {VramSummary()}; pool {_imagePool.PageCount} pages ({_imagePool.PagesFreed} returned to the driver); " +
        $"evicted {_evTextures} textures ({_evTextureBytes >> 20} MB; {_evOverlap} replaced in memory by another texture), {_evViews} render-target views ({_evViewBytes >> 20} MB), " +
        $"{_evTargets} render targets ({_evTargetBytes >> 20} MB), {_evDepth} depth targets ({_evDepthBytes >> 20} MB); " +
        $"pressure trims {_pressureTrims}, expiry sweeps {_expirySweeps}, out-of-memory reclaims {_reclaims} ({_reclaimsLevel1} at level 1, " +
        $"{_reclaimTicks * 1000.0 / Stopwatch.Frequency:F0} ms); allocation failures {_core.AllocationFailures}: " +
        $"{_oomDraws} draws and {_oomScenes} scenes dropped, {_oomTextures} textures skipped, {_oomOther} other commands";

    // =================================================================== deferred destruction

    /// <summary>Runs <paramref name="destroy"/> once the GPU no longer uses what the recorded/submitted frames use.</summary>
    void DestroyAfterGpu(Action destroy)
    {
        if (F.Recording) { F.Deferred.Add(destroy); return; }
        // between frames: the newest submitted frame may still use it
        long seq = 0;
        foreach (var f in _frames) if (f.Submitted && !f.Retired) seq = Math.Max(seq, f.SubmitSeq);
        if (seq == 0) destroy();
        else _afterGpu.Add((seq, destroy));
    }

    void Release(Action destroy, bool now)
    {
        if (now) destroy();
        else DestroyAfterGpu(destroy);
    }

    /// <summary>A frame retired: destroys that waited for it (RetireFrame).</summary>
    void RunAfterGpu(long retiredSeq)
    {
        if (_afterGpu.Count == 0) return;
        for (int i = 0; i < _afterGpu.Count;)
        {
            if (_afterGpu[i].Seq <= retiredSeq) { var a = _afterGpu[i].Destroy; _afterGpu.RemoveAt(i); a(); }
            else i++;
        }
    }

    // =================================================================== releasing cached objects

    void ReleaseTexture(TexKey key, Texture t, bool now)
    {
        _textures.Remove(key);
        _evTextures++;
        _evTextureBytes += (long)t.Mem.Size;
        Release(() => { _vk.DestroyImageView(_dev, t.View, null); _vk.DestroyImage(_dev, t.Image, null); _imagePool.Free(t.Mem); }, now);
    }

    void ReleaseImage(Image image, ImageView view, ImageMemoryPool.Allocation mem, bool now)
    {
        _evViews++;
        _evViewBytes += (long)mem.Size;
        Release(() => { _vk.DestroyImageView(_dev, view, null); _vk.DestroyImage(_dev, image, null); _imagePool.Free(mem); }, now);
    }

    /// <summary>Destroys a render target and everything derived from it (also used when a surface is replaced).</summary>
    void ReleaseRenderTarget(RenderTarget rt, bool now)
    {
        DestroySupersampleLater(rt, now);
        DestroyMsaaLater(rt, now);
        DestroySceneStartCopyLater(rt, now);
        ForgetRenderTargetViews(rt, now);
        _mirroredRt.Remove(rt);
        Release(() =>
        {
            _vk.DestroyImageView(_dev, rt.View, null);
            if (rt.SampledView.Handle != 0) _vk.DestroyImageView(_dev, rt.SampledView, null);
            if (rt.SrgbView.Handle != 0) _vk.DestroyImageView(_dev, rt.SrgbView, null);
            if (rt.RawView.Handle != 0) _vk.DestroyImageView(_dev, rt.RawView, null);
            _vk.DestroyImage(_dev, rt.Image, null);
            _imagePool.Free(rt.Mem);
        }, now);
    }

    readonly List<(RenderTarget, uint, uint, uint, uint, uint, bool)> _regionKeys = new();
    readonly List<(RenderTarget, uint X, uint Y, uint Format, uint W, uint H, uint Mips, bool Gamma)> _mipKeys = new();

    /// <summary>Regions and mip-chained textures made from <paramref name="rt"/> (keyed by the target object: they
    /// leaked whenever a surface was replaced).</summary>
    void ForgetRenderTargetViews(RenderTarget rt, bool now)
    {
        _regionKeys.Clear();
        foreach (var k in _rtRegions.Keys) if (k.Item1 == rt) _regionKeys.Add(k);
        foreach (var k in _regionKeys) { _rtRegions.Remove(k, out var r); ReleaseImage(r!.Image, r.View, r.Mem, now); }
        _mipKeys.Clear();
        foreach (var k in _rtMipTextures.Keys) if (k.Item1 == rt) _mipKeys.Add(k);
        foreach (var k in _mipKeys) { _rtMipTextures.Remove(k, out var t); ReleaseImage(t!.Image, t.View, t.Mem, now); }
        foreach (var a in _rtAliases.Values) if (a.Source == rt) a.Source = null; // copied from the mirror again
    }

    void ReleaseDepthTarget((uint, uint, uint, SampleCountFlags) key, DepthTarget dt, bool now)
    {
        _depthTargets.Remove(key);
        _evDepth++;
        _evDepthBytes += (long)dt.Mem.Size;
        if (_depthTextures.Remove(dt, out var t)) ReleaseImage(t.Image, t.View, t.Mem, now);
        if (_stencilTextures.Remove(dt, out var s)) ReleaseImage(s.Image, s.View, s.Mem, now);
        _mirroredDepth.Remove((dt, false));
        _mirroredDepth.Remove((dt, true));
        // multisampled framebuffers that attach its view
        var view = dt.View;
        foreach (var m in _msImages.Values)
        {
            List<(ulong, ulong, ulong)>? dead = null;
            foreach (var (k, _) in m.Framebuffers) if (k.Item2 == view.Handle) (dead ??= new()).Add(k);
            if (dead == null) continue;
            foreach (var k in dead)
            {
                m.Framebuffers.Remove(k, out var fb);
                Release(() => _vk.DestroyFramebuffer(_dev, fb, null), now);
            }
        }
        Release(() => { _vk.DestroyImageView(_dev, dt.View, null); _vk.DestroyImage(_dev, dt.Image, null); _imagePool.Free(dt.Mem); }, now);
    }

    // =================================================================== eviction

    readonly List<TexKey> _overlapKeys = new();
    long _evOverlap;

    /// <summary>Test switch VITASHARP_VRAM_EVICT=0: no expiry, overlap or pressure eviction (the cache behaves as
    /// before this existed, to measure its growth); out-of-memory reclaim still runs.</summary>
    static readonly bool ProactiveEviction = Environment.GetEnvironmentVariable("VITASHARP_VRAM_EVICT")?.Trim() != "0";

    /// <summary>A new texture is created over [addr, addr + bytes): cached textures of that memory that were not used
    /// for <see cref="TrimMinAge"/> frames describe what it held before and are dropped.</summary>
    void EvictOverlappingTextures(uint addr, uint bytes)
    {
        if (!ProactiveEviction) return;
        ulong end = (ulong)addr + bytes;
        _overlapKeys.Clear();
        foreach (var (k, t) in _textures)
            if (t.DataStart < end && addr < (ulong)t.DataStart + t.DataSize && FrameCount - t.LastUsedFrame >= TrimMinAge) _overlapKeys.Add(k);
        foreach (var k in _overlapKeys) { _evOverlap++; ReleaseTexture(k, _textures[k], now: false); }
    }

    /// <summary>Whether a render target is still needed as is: in use by the current scene/presentation, or its
    /// content exists only on the GPU (unless it has been idle for <paramref name="lostIdle"/> frames).</summary>
    bool RenderTargetEvictable(RenderTarget rt, long minAge, long lostIdle)
    {
        if (rt == _rt || rt == _cpuFramebuffer) return false;
        long age = FrameCount - rt.LastTouchedFrame;
        if (age < minAge) return false;
        // content in guest memory: written back (lossless) and not rendered since, or loaded from memory
        bool inMemory = rt.InMemoryVersion == rt.Version;
        return inMemory || age >= lostIdle;
    }

    readonly record struct Candidate(long LastUsed, ulong Bytes, int Kind, object Key, object Value);

    readonly List<Candidate> _candidates = new();

    /// <summary>
    /// Evicts least recently used caches unused for at least <paramref name="minAge"/> GPU frames until
    /// <paramref name="target"/> bytes were freed. <paramref name="now"/>: destroyed at once (every submitted frame
    /// has completed), else once the GPU is done with them.
    /// </summary>
    ulong Evict(ulong target, long minAge, long rtIdle, long lostIdle, bool now)
    {
        var c = _candidates;
        c.Clear();
        foreach (var (k, t) in _textures)
            if (FrameCount - t.LastUsedFrame >= minAge) c.Add(new Candidate(t.LastUsedFrame, t.Mem.Size, 0, k, t));
        foreach (var (k, r) in _rtRegions)
            if (FrameCount - r.LastUsedFrame >= minAge) c.Add(new Candidate(r.LastUsedFrame, r.Mem.Size, 1, k, r));
        foreach (var (k, a) in _rtAliases)
            if (FrameCount - a.LastUsedFrame >= minAge) c.Add(new Candidate(a.LastUsedFrame, a.Mem.Size, 2, k, a));
        foreach (var (k, m) in _rtMipTextures)
            if (FrameCount - m.LastUsedFrame >= minAge) c.Add(new Candidate(m.LastUsedFrame, m.Mem.Size, 3, k, m));
        foreach (var (k, d) in _depthTextures)
            if (FrameCount - d.LastUsedFrame >= minAge) c.Add(new Candidate(d.LastUsedFrame, d.Mem.Size, 4, k, d));
        foreach (var (k, s) in _stencilTextures)
            if (FrameCount - s.LastUsedFrame >= minAge) c.Add(new Candidate(s.LastUsedFrame, s.Mem.Size, 5, k, s));
        if (rtIdle < long.MaxValue)
        {
            foreach (var (k, rt) in _renderTargets)
                if (RenderTargetEvictable(rt, Math.Max(minAge, rtIdle), lostIdle))
                    c.Add(new Candidate(rt.LastTouchedFrame, RenderTargetBytes(rt), 6, k, rt));
            foreach (var (k, dt) in _depthTargets)
                if (dt != _depth && FrameCount - dt.LastTouchedFrame >= Math.Max(minAge, lostIdle))
                    c.Add(new Candidate(dt.LastTouchedFrame, dt.Mem.Size, 7, k, dt));
        }
        if (c.Count == 0) return 0;
        c.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        ulong freed = 0;
        foreach (var e in c)
        {
            if (freed >= target) break;
            // an entry may already be gone: releasing a render target or depth target releases its views too
            switch (e.Kind)
            {
                case 0:
                    if (!_textures.ContainsKey((TexKey)e.Key)) continue;
                    ReleaseTexture((TexKey)e.Key, (Texture)e.Value, now);
                    break;
                case 1:
                    if (!_rtRegions.Remove(((RenderTarget, uint, uint, uint, uint, uint, bool))e.Key)) continue;
                    { var r = (RtRegion)e.Value; ReleaseImage(r.Image, r.View, r.Mem, now); }
                    break;
                case 2:
                    if (!_rtAliases.Remove(((uint, uint, uint, uint, uint, bool))e.Key)) continue;
                    { var a = (RtAlias)e.Value; ReleaseImage(a.Image, a.View, a.Mem, now); }
                    break;
                case 3:
                    if (!_rtMipTextures.Remove(((RenderTarget, uint, uint, uint, uint, uint, uint, bool))e.Key)) continue;
                    { var m = (RtMipTexture)e.Value; ReleaseImage(m.Image, m.View, m.Mem, now); }
                    break;
                case 4:
                    if (!_depthTextures.Remove((DepthTarget)e.Key)) continue;
                    { var d = (DepthTex)e.Value; ReleaseImage(d.Image, d.View, d.Mem, now); }
                    break;
                case 5:
                    if (!_stencilTextures.Remove((DepthTarget)e.Key)) continue;
                    { var s = (StencilTex)e.Value; ReleaseImage(s.Image, s.View, s.Mem, now); }
                    break;
                case 6:
                {
                    var rt = (RenderTarget)e.Value;
                    if (!_renderTargets.Remove(((uint, uint, uint, uint))e.Key)) continue;
                    _evTargets++;
                    _evTargetBytes += (long)e.Bytes;
                    ReleaseRenderTarget(rt, now);
                    break;
                }
                case 7:
                    if (!_depthTargets.ContainsKey(((uint, uint, uint, SampleCountFlags))e.Key)) continue;
                    ReleaseDepthTarget(((uint, uint, uint, SampleCountFlags))e.Key, (DepthTarget)e.Value, now);
                    break;
            }
            freed += e.Bytes;
        }
        c.Clear();
        return freed;
    }

    /// <summary>Device memory a render target and its derived images hold.</summary>
    ulong RenderTargetBytes(RenderTarget rt)
    {
        ulong b = rt.Mem.Size;
        if (_msImages.TryGetValue(rt, out var m)) b += m.Mem.Size;
        if (_ssImages.TryGetValue(rt, out var ss)) b += ss.Mem.Size;
        if (_sceneStartCopies.TryGetValue(rt, out var sc)) b += sc.Mem.Size;
        return b;
    }

    /// <summary>
    /// <see cref="VkCore.Reclaim"/>: an allocation of <paramref name="need"/> bytes failed with an out-of-memory
    /// error. Waits for the frames in flight so their deferred destroys and anything not used by the recording frame
    /// can be destroyed immediately, evicts, returns empty pages and reports whether the allocation is worth retrying.
    /// </summary>
    bool Reclaim(ulong need, bool device, int level)
    {
        if (_reclaiming) return false;
        _reclaiming = true;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            _reclaims++;
            if (level > 0) _reclaimsLevel1++;
            long pool0 = _imagePool.PageCount;
            ulong poolFree0 = _imagePool.FreeBytes;
            // 1. every submitted frame completes: what they used is free to destroy
            while (OldestInFlight() is { } f) RetireFrame(f, RetireCause.Memory);
            RunAfterGpu(long.MaxValue);
            int deferred = 0;
            foreach (var f in _frames)
            {
                if (f.Recording) continue;
                deferred += f.Deferred.Count + f.Overflow.Count;
                foreach (var a in f.Deferred) a();
                f.Deferred.Clear();
                foreach (var (b, m) in f.Overflow) _core.DestroyBuffer(b, m);
                f.Overflow.Clear();
            }
            // 2. evict least recently used caches; level 1 takes everything the recording frame does not use
            ulong freed = level == 0
                ? Evict(need + (128ul << 20), minAge: 2, rtIdle: RtIdleFrames, lostIdle: long.MaxValue, now: true)
                : Evict(ulong.MaxValue, minAge: 1, rtIdle: 1, lostIdle: RtLostIdleFrames, now: true);
            // 3. empty pages back to the driver (a buffer or a page of another size may fit where the page was)
            ulong trimmed = _imagePool.TrimEmptyPages(keepSpare: device ? 0 : 1);
            _reclaimTicks += Stopwatch.GetTimestamp() - t0;
            bool progress = freed > 0 || trimmed > 0 || deferred > 0 || _imagePool.FreeBytes > poolFree0;
            LogOom($"[vram] out of {(device ? "device" : "host")} memory for {need / 1048576.0:F1} MB (reclaim level {level}): " +
                   $"{deferred} deferred destroys run, evicted {freed >> 20} MB, returned {trimmed >> 20} MB of empty pages ({pool0} -> {_imagePool.PageCount} pages), " +
                   $"pool free {_imagePool.FreeBytes >> 20} MB, {(progress ? "retrying" : "nothing to free")} ({(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency:F1} ms); {VramSummary()}", force: level == 0 && _reclaims <= 3);
            return progress;
        }
        catch (Exception e)
        {
            Log.Warn($"[vram] reclaim failed: {e.Message}");
            return false;
        }
        finally { _reclaiming = false; }
    }

    /// <summary>
    /// Frame boundary (Present, the frame still recording): under memory pressure, trims least recently used caches
    /// so the pool keeps a reserve of free space; every 256 presents, expires long-unused caches and returns spare
    /// empty pages.
    /// </summary>
    void TrimAtFrameBoundary()
    {
        if (ProactiveEviction && _presentCount % 256 == 0)
        {
            _expirySweeps++;
            Evict(ulong.MaxValue, minAge: ExpiryFrames, rtIdle: long.MaxValue, lostIdle: long.MaxValue, now: false);
            _imagePool.TrimEmptyPages(keepSpare: 1);
        }
        if (_presentCount % 4 == 0) TrimForPressure();
        if (DebugMode.Enabled && _presentCount % 600 == 0) Log.Info($"[vram] p{_presentCount} pressure {_core.DevicePressure() * 100:F0}%: {VramSummary()}");
    }

    void TrimForPressure()
    {
        double p = _core.DevicePressure();
        // log crossings of 75% / 90% (with hysteresis) in normal play too
        int level = p >= PressureHigh ? 2 : p >= 0.75 ? 1 : 0;
        if (level > _pressureLogged) Log.Info($"[vram] device-local usage at {p * 100:F0}% of the {(_core.BudgetCap > 0 ? "test cap" : "budget")}: {VramSummary()}");
        if (level > _pressureLogged || p < 0.70) _pressureLogged = level;
        if (p < PressureHigh || !ProactiveEviction) return;
        // free space the pool should hold: a share of the budget (or cap)
        ulong budget = _core.BudgetCap;
        if (budget == 0)
            foreach (var h in _core.Heaps()) if (h.DeviceLocal) budget = Math.Max(budget, h.Budget);
        ulong reserve = Math.Max(MinReserve, (ulong)(budget * PressureReserve));
        ulong free = _imagePool.FreeBytes;
        ulong freed = 0;
        if (free < reserve)
        {
            freed = Evict(reserve - free, minAge: TrimMinAge, rtIdle: RtIdleFrames, lostIdle: long.MaxValue, now: false);
            if (freed > 0) _pressureTrims++;
        }
        ulong trimmed = _imagePool.TrimEmptyPages(keepSpare: 0);
        if ((freed > 0 || trimmed > 0) && (_pressureTrims <= 5 || _pressureTrims % 100 == 0))
            Log.Info($"[vram] pressure {p * 100:F0}% at present {_presentCount}: evicting {freed >> 20} MB of least recently used caches, {trimmed >> 20} MB of empty pages returned (trim #{_pressureTrims}); {VramSummary()}");
    }

    // =================================================================== failures

    /// <summary>Rate-limited out-of-memory messages: the first few in full, then one per 10 s with the count.</summary>
    void LogOom(string message, bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        _oomSinceLog++;
        if (!force && now - _lastOomLog < 10 * Stopwatch.Frequency) return;
        Log.Warn(_oomSinceLog > 1 ? $"{message} ({_oomSinceLog - 1} similar messages suppressed)" : message);
        _lastOomLog = now;
        _oomSinceLog = 0;
    }

    /// <summary>A command could not get the memory it needs: skip it and keep the renderer consistent.</summary>
    void OnOutOfMemory(Core.Gpu.GpuCommand command, VkOutOfMemoryException e)
    {
        string what;
        switch (command)
        {
            case Core.Gpu.DrawCommand: _oomDraws++; what = "draw dropped"; break;
            case Core.Gpu.BeginSceneCommand:
                _oomScenes++;
                what = "scene dropped";
                // no target: the scene's draws are skipped (Draw checks _inScene)
                EndRendering();
                _inScene = false;
                _scene = null;
                _rt = null;
                _ss = _ms = _msaaNoDownscale = false;
                break;
            default: _oomOther++; what = $"{command.GetType().Name} skipped"; break;
        }
        LogOom($"[vram] out of GPU memory, {what} (present {_presentCount}, draw {_drawSerial}): {e.Message}", force: _oomDraws + _oomScenes + _oomOther <= 3);
    }
}
