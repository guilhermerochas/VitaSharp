using System.Collections.Concurrent;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Shader prefetch: the guest submits a whole frame of draws while the GPU thread is still executing earlier ones,
/// so when a scene brings new shaders, the submitting thread can see them first. <see cref="Prefetch"/> computes the
/// module keys of each submitted draw exactly as <see cref="GetVertexShader"/>/<see cref="GetFragmentShader"/> will
/// (the fragment key needs the colour format of the scene, taken from the last submitted BeginScene) and queues the
/// unknown ones to a small pool of workers. The GPU thread then finds the module ready, or waits for the job (or runs
/// it itself when no worker started it yet), so a burst of N new shaders costs about N / workers instead of N.
/// Nothing is skipped or approximated: a wrong guess only compiles a module nobody uses.
/// VITASHARP_ASYNC_SHADERS=0 disables it; VITASHARP_SHADER_WORKERS=N sets the pool size.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    static readonly bool AsyncShaders = Environment.GetEnvironmentVariable("VITASHARP_ASYNC_SHADERS") != "0";
    static readonly int ShaderWorkerCount = int.TryParse(Environment.GetEnvironmentVariable("VITASHARP_SHADER_WORKERS"), out var n) && n > 0
        ? n : Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

    /// <summary>VITASHARP_SHADER_WORKER_PRIO=normal|below: below-normal keeps a compile burst from taking cores from the guest
    /// threads (the GPU thread runs a job itself when no worker started it yet).</summary>
    static readonly ThreadPriority WorkerPriority = Environment.GetEnvironmentVariable("VITASHARP_SHADER_WORKER_PRIO") == "below" ? ThreadPriority.BelowNormal : ThreadPriority.Normal;

    sealed class ShaderJob(GxpProgram program, ulong hintKey, ShaderHints hints)
    {
        int _state; // 0 queued, 1 running, 2 done
        readonly ManualResetEventSlim _done = new();
        ModuleEntry? _result;

        /// <summary>Runs the job if nobody started it yet, else waits for it; returns the published module.</summary>
        public ModuleEntry RunOrWait(VulkanRenderer r)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0) Run(r);
            else _done.Wait();
            return _result!;
        }

        /// <summary>Worker side: runs the job unless the GPU thread took it.</summary>
        public void TryRun(VulkanRenderer r)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0) Run(r);
        }

        void Run(VulkanRenderer r)
        {
            var key = (program.Hash, hintKey);
            try
            {
                var e = r.LoadPackedShader(program.Hash, hintKey, program.IsVertex) ?? r.TranslateAndCompile(program, hintKey, hints, background: true);
                _result = r.PublishModule(key, e);
            }
            catch (Exception ex)
            {
                Log.Error($"[shader worker] {program.Hash:X16}: {ex.Message}");
                _result = r.PublishModule(key, new ModuleEntry(default, null));
            }
            finally
            {
                r._inflight.TryRemove(key, out _);
                Volatile.Write(ref _state, 2);
                _done.Set();
            }
        }
    }

    readonly ConcurrentDictionary<(ulong, ulong), ShaderJob> _inflight = new();
    readonly BlockingCollection<ShaderJob> _shaderJobs = new(new ConcurrentQueue<ShaderJob>());
    Thread[]? _shaderWorkers;
    uint _prefetchColorFormat; // submitting thread only
    int _statAsyncQueued, _statAsyncWaited;

    /// <summary>Called on the submitting (guest) thread for every command, before it is queued.</summary>
    public void Prefetch(GpuCommand command)
    {
        if (!AsyncShaders || _stopShaderJobs) return;
        switch (command)
        {
            case BeginSceneCommand b:
                _prefetchColorFormat = b.Color?.ColorFormat ?? 0;
                if (AsyncPipelines) PredictScene(b);
                break;
            case DrawCommand d:
                if (d.IndexCount == 0 || d.FragmentProgram.IsMaskUpdate || d.FragmentProgram.Info == null || d.VertexProgram?.Info == null) return;
                var vp = d.VertexProgram;
                ulong vk = HintKey(d.VertexTextures, vp.Info.TexturesUsed, vp.Attributes, 0);
                if (NeedsJob(vp.Info.Program, vk)) Queue(vp.Info.Program, vk, VertexHints(d, 0));
                var fp = d.FragmentProgram;
                uint cf = _prefetchColorFormat;
                ulong fk = HintKey(d.FragmentTextures, fp.Info.TexturesUsed, null, cf);
                if (NeedsJob(fp.Info.Program, fk)) Queue(fp.Info.Program, fk, FragmentHints(d, cf));
                if (AsyncPipelines) PrefetchPipeline(d, vk, fk);
                break;
        }
    }

    // =================================================================== pipeline prefetch

    static readonly bool AsyncPipelines = Environment.GetEnvironmentVariable("VITASHARP_ASYNC_PIPELINES") != "0";

    /// <summary>Scene state the pipeline key depends on, predicted from the last submitted BeginScene with the same
    /// rules as BeginScene/ChooseMsaaPath (a wrong prediction only builds a pipeline nobody uses).</summary>
    (bool HasColor, Format RtFormat, Format Attach, SampleCountFlags Samples) _prefetchScene;
    readonly ConcurrentDictionary<(ulong, ulong), bool> _msOkPrefetch = new();
    readonly ConcurrentDictionary<ulong, byte> _seenDrawSignatures = new(); // several guest threads may submit
    readonly ConcurrentDictionary<PipelineKey, ManualResetEventSlim> _pipelineInflight = new();
    int _statPipelinesPrefetched, _statPipelineWaits;

    void PredictScene(BeginSceneCommand b)
    {
        if (b.Color is not { } c) { _prefetchScene = (false, Format.Undefined, Format.Undefined, SampleCountFlags.Count1Bit); return; }
        var f = SurfaceFormat(c.ColorFormat);
        if (!_core.SupportsColorAttachment(f)) f = Format.R8G8B8A8Unorm;
        var srgb = Formats.Srgb(f);
        var attach = c.GammaMode != 0 && srgb != f && _core.SupportsColorAttachment(srgb) ? srgb : f;
        var samples = SampleCountFlags.Count1Bit;
        if (b.Multisample != 0 && c.Downscale && MsaaSetting == MsaaEmulation.Native)
        {
            var s = GxmSamples(b.Multisample);
            bool ok = _msOkPrefetch.GetOrAdd(((ulong)f, (ulong)s), _ =>
                (_core.ImageSampleCounts(f, MsaaImageUsage) & s) != 0 && (_core.Properties.Limits.FramebufferColorSampleCounts & s) != 0);
            if (ok) samples = s;
        }
        _prefetchScene = (true, f, attach, samples);
    }

    void PrefetchPipeline(DrawCommand d, ulong vk, ulong fk)
    {
        var scene = _prefetchScene;
        var st = d.State;
        var blend = d.FragmentProgram.Blend;
        var vp = d.VertexProgram;
        // one job per distinct draw signature (everything the pipeline key is derived from)
        ulong sig = ShaderHints.Fnv.Mix(ShaderHints.Fnv.Mix(vk, (uint)fk), (uint)(fk >> 32));
        sig = ShaderHints.Fnv.Mix(sig, (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(vp));
        sig = ShaderHints.Fnv.Mix(sig, (uint)scene.Attach | (uint)scene.Samples << 24 | (scene.HasColor ? 1u << 31 : 0));
        sig = ShaderHints.Fnv.Mix(sig, blend is { } bl ? (uint)(bl.ColorMask | bl.ColorFunc << 4 | bl.AlphaFunc << 8 | bl.ColorSrc << 12 | bl.ColorDst << 16 | bl.AlphaSrc << 20 | bl.AlphaDst << 24) | 1u << 31 : 0);
        sig = ShaderHints.Fnv.Mix(sig, (uint)(d.PrimitiveType | st.Front.PolygonMode << 8 | (st.Front.FragmentEnabled ? 1 << 16 : 0)));
        sig = ShaderHints.Fnv.Mix(sig, (uint)(vp.Info.Program.Hash ^ d.FragmentProgram.Info.Program.Hash));
        if (!_seenDrawSignatures.TryAdd(sig, 0)) return;
        var job = new PipelineJob(vp, vk, d.FragmentProgram.Info.Program, fk,
            scene.Samples != SampleCountFlags.Count1Bit ? HintKey(d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed, null, _prefetchColorFormat, true) : 0,
            scene.Samples != SampleCountFlags.Count1Bit ? FragmentHintsInputFetch(d, _prefetchColorFormat) : null,
            scene, blend, st.Front.FragmentEnabled, d.PrimitiveType, st.Front.PolygonMode);
        EnsureShaderWorkers();
        try { _pipelineJobs.Add(job); _workAvailable.Release(); } catch (InvalidOperationException) { }
    }

    readonly BlockingCollection<PipelineJob> _pipelineJobs = new(new ConcurrentQueue<PipelineJob>());

    sealed record PipelineJob(GxmVertexProgram Vp, ulong Vk, GxpProgram Fs, ulong Fk, ulong FetchKey, ShaderHints? FetchHints,
        (bool HasColor, Format RtFormat, Format Attach, SampleCountFlags Samples) Scene, GxmBlendInfo? Blend, bool FragEnabled, byte PrimitiveType, byte PolygonMode);

    /// <summary>Module of a prefetched key: ready, in flight (wait / run it), or null when the shader job is gone.</summary>
    ModuleEntry? ModuleFor(GxpProgram p, ulong hk, ShaderHints? hints)
    {
        var key = (p.Hash, hk);
        if (_modules.TryGetValue(key, out var e)) return e;
        if (_inflight.TryGetValue(key, out var job)) return job.RunOrWait(this);
        if (_modules.TryGetValue(key, out e)) return e;
        if (hints == null) return null;
        var j = new ShaderJob(p, hk, hints);
        return _inflight.TryAdd(key, j) ? j.RunOrWait(this) : ModuleFor(p, hk, null);
    }

    void RunPipelineJob(PipelineJob j)
    {
        var vsM = ModuleFor(j.Vp.Info.Program, j.Vk, null);
        var fsM = ModuleFor(j.Fs, j.Fk, null);
        if (vsM?.Gen == null || fsM?.Gen == null || vsM.Module.Handle == 0 || fsM.Module.Handle == 0) return;
        var s = j.Scene;
        bool ms = s.Samples != SampleCountFlags.Count1Bit;
        ulong fsKey = j.Fk;
        if (fsM.Gen.UsesFragColor && ms && MsaaInputFetch && fsM.Gen.RawColorBits == 0)
        {
            fsM = ModuleFor(j.Fs, j.FetchKey, j.FetchHints);
            fsKey = j.FetchKey;
            if (fsM?.Gen == null || fsM.Module.Handle == 0) return;
        }
        if (fsM.Gen.RawColorBits != 0 && s.HasColor) return; // integer views: left to the GPU thread
        var vs = new VsEntry { Module = vsM.Module, Gen = vsM.Gen, ProgramHash = j.Vp.Info.Program.Hash, HintKey = j.Vk };
        BuildVertexInput(j.Vp, vsM.Gen, vs);
        var fs = new FsEntry { Module = fsM.Module, Gen = fsM.Gen, ProgramHash = j.Fs.Hash, HintKey = fsKey };
        var topology = Formats.Topology(j.PrimitiveType);
        byte mask = !j.FragEnabled ? (byte)0 : j.Blend?.ColorMask ?? 0xF;
        bool blendOn = j.Blend is { } bi && !fsM.Gen.UsesFragColor && !(bi.ColorFunc == 0 && bi.AlphaFunc == 0) && !(bi.ColorSrc == 1 && bi.ColorDst == 0 && bi.AlphaSrc == 1 && bi.AlphaDst == 0);
        var key = new PipelineKey(vs.Module.Handle, fs.Module.Handle, vs.LayoutHash, s.Attach, s.HasColor,
            blendOn, j.Blend?.ColorFunc ?? 0, j.Blend?.AlphaFunc ?? 0, j.Blend?.ColorSrc ?? 1, j.Blend?.ColorDst ?? 0, j.Blend?.AlphaSrc ?? 1, j.Blend?.AlphaDst ?? 0,
            mask, TopologyClass(topology), Formats.Polygon(j.PolygonMode), s.Samples);
        if (_sharedPipelines.ContainsKey(key)) return;
        var done = new ManualResetEventSlim();
        if (!_pipelineInflight.TryAdd(key, done)) return;
        try
        {
            if (_sharedPipelines.ContainsKey(key)) return;
            long t0 = FrameLedger.Now();
            var p = CreatePipeline(key, vs.Module, fs.Module, vs.Bindings, vs.Attributes, topology);
            FrameLedger.AddSince(FrameLedger.Cat.BgPipeline, t0);
            if (p.Handle == 0) return;
            if (_sharedPipelines.TryAdd(key, p))
            {
                Interlocked.Increment(ref _statPipelinesPrefetched);
                RecordPipeline(key, vs, fs, topology); // BlobPack is thread-safe
                NotePipelineCreated();
            }
            else _vk.DestroyPipeline(_dev, p, null);
        }
        finally
        {
            _pipelineInflight.TryRemove(key, out _);
            done.Set();
        }
    }

    /// <summary>GPU thread: a pipeline a worker is building right now is waited for instead of being built twice.</summary>
    bool WaitPrefetchedPipeline(in PipelineKey key, out Pipeline p)
    {
        p = default;
        if (!_pipelineInflight.TryGetValue(key, out var ev)) return false;
        long t0 = FrameLedger.Now();
        ev.Wait();
        FrameLedger.AddSince(FrameLedger.Cat.ShaderWait, t0);
        _statPipelineWaits++;
        return _sharedPipelines.TryGetValue(key, out p);
    }

    bool NeedsJob(GxpProgram p, ulong hk) => !_modules.ContainsKey((p.Hash, hk)) && !_inflight.ContainsKey((p.Hash, hk));

    void Queue(GxpProgram p, ulong hk, ShaderHints hints)
    {
        var job = new ShaderJob(p, hk, hints);
        if (!_inflight.TryAdd((p.Hash, hk), job)) return;
        EnsureShaderWorkers();
        Interlocked.Increment(ref _statAsyncQueued);
        try { _shaderJobs.Add(job); _workAvailable.Release(); }
        catch (InvalidOperationException) { _inflight.TryRemove((p.Hash, hk), out _); } // renderer shutting down: drop it
    }

    void EnsureShaderWorkers()
    {
        if (_shaderWorkers != null) return;
        lock (_inflight)
        {
            if (_shaderWorkers != null) return;
            var workers = new Thread[ShaderWorkerCount];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(() =>
                {
                    NativeThread.Register("Shader worker");
                    // shader jobs first: pipeline jobs need their modules
                    WorkerLoop();
                }) { IsBackground = true, Name = $"Shader worker {i}", Priority = WorkerPriority };
                workers[i].Start();
            }
            _shaderWorkers = workers;
        }
    }

    void WorkerLoop()
    {
        while (true)
        {
            if (_shaderJobs.TryTake(out var sj)) { sj.TryRun(this); continue; }
            if (_pipelineJobs.TryTake(out var pj))
            {
                if (!_stopShaderJobs)
                    try { RunPipelineJob(pj); }
                    catch (Exception e) { Log.WarnOnce("pipeline-prefetch", $"[shader worker] pipeline prefetch failed: {e.Message}"); }
                continue;
            }
            if (_shaderJobs.IsCompleted && _pipelineJobs.IsCompleted) return;
            _workAvailable.Wait(50);
        }
    }

    readonly SemaphoreSlim _workAvailable = new(0);

    /// <summary>Renderer teardown: finish queued jobs (they create modules on this device) and stop the workers.</summary>
    void StopShaderWorkers()
    {
        _stopShaderJobs = true;
        _shaderJobs.CompleteAdding();
        _pipelineJobs.CompleteAdding();
        _workAvailable.Release(ShaderWorkerCount);
        if (_shaderWorkers != null) foreach (var t in _shaderWorkers) t.Join();
        foreach (var job in _inflight.Values) job.TryRun(this);
    }

    volatile bool _stopShaderJobs;
}
