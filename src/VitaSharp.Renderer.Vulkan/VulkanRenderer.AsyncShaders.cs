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
/// Configured by <see cref="VulkanRendererOptions.AsyncShaders"/> / <see cref="VulkanRendererOptions.AsyncPipelines"/> /
/// <see cref="VulkanRendererOptions.ShaderWorkers"/> (launcher "Compilar shaders e pipelines em segundo plano",
/// --async-shaders=, --shader-workers=); VITASHARP_ASYNC_SHADERS=0|1, VITASHARP_ASYNC_PIPELINES=0|1 and
/// VITASHARP_SHADER_WORKERS=N override them.
/// <para>Threading: workers only create shader modules and pipelines (vkCreateShaderModule / vkCreateGraphicsPipelines
/// need no external synchronisation; <see cref="_vkPipelineCache"/> is created without
/// VK_PIPELINE_CACHE_CREATE_EXTERNALLY_SYNCHRONIZED_BIT, so the driver synchronises concurrent pipeline creation and
/// vkGetPipelineCacheData itself; the only externally synchronised cache calls, vkMergePipelineCaches in BeginTitle and
/// vkDestroyPipelineCache in Dispose, run before the guest submits anything / after the workers stopped). Results are
/// published through the concurrent module/pipeline tables; a loser of a creation race destroys its own copy, which no
/// one else has seen.</para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    bool _asyncShaders, _asyncPipelines;
    int _shaderWorkerCount;

    /// <summary>VITASHARP_SHADER_WORKER_PRIO=normal|below: below-normal keeps a compile burst from taking cores from the guest
    /// threads (the GPU thread runs a job itself when no worker started it yet).</summary>
    static readonly ThreadPriority WorkerPriority = Environment.GetEnvironmentVariable(EnvVars.ShaderWorkerPrio) == "below" ? ThreadPriority.BelowNormal : ThreadPriority.Normal;

    /// <summary>Environment overrides win over the options (launcher / command line).</summary>
    void ConfigureAsyncShaders(VulkanRendererOptions o)
    {
        static bool? Flag(string name) => Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "no" => false,
            "1" or "on" or "true" or "yes" => true,
            _ => null,
        };
        _asyncShaders = Flag(EnvVars.AsyncShaders) ?? o.AsyncShaders;
        _asyncPipelines = _asyncShaders && (Flag(EnvVars.AsyncPipelines) ?? o.AsyncPipelines);
        int workers = int.TryParse(Environment.GetEnvironmentVariable(EnvVars.ShaderWorkers), out var n) && n > 0 ? n : o.ShaderWorkers;
        _shaderWorkerCount = workers > 0 ? Math.Min(workers, 64) : Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        Log.Info($"[gpu] shaders: shaderc optimisation {_compiler.OptLevel}, background compilation "
            + (_asyncShaders ? $"on ({_shaderWorkerCount} workers, pipelines {(_asyncPipelines ? "on" : "off")})" : "off"));
    }

    /// <param name="rebuild">queued by the boot rebuild (<see cref="RebuildFromRecipes"/>) rather than by a draw</param>
    sealed class ShaderJob(GxpProgram program, ulong hintKey, ShaderHints hints, bool rebuild = false)
    {
        int _state; // 0 queued, 1 running/cancelled, 2 done
        int _promoted;
        readonly ManualResetEventSlim _done = new();
        ModuleEntry? _result;

        /// <summary>Runs the job if nobody started it yet, else waits for it; returns the published module (an empty
        /// one if the job was cancelled at shutdown).</summary>
        public ModuleEntry RunOrWait(VulkanRenderer r, bool byGame = true)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                if (rebuild && byGame) Interlocked.Increment(ref r._statRebuildTakenByGame); // the game got there before the rebuild
                Run(r);
            }
            else _done.Wait();
            return _result!;
        }

        /// <summary>Worker side: runs the job unless the GPU thread took it (true when this call ran it).</summary>
        public bool TryRun(VulkanRenderer r)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
            Run(r);
            return true;
        }

        /// <summary>A rebuild job a draw needs now: true once, if it has not started (the caller moves it to the
        /// draw-driven queue, ahead of the rest of the rebuild; whichever queue reaches it first runs it).</summary>
        public bool TryPromote() => rebuild && Volatile.Read(ref _state) == 0 && Interlocked.Exchange(ref _promoted, 1) == 0;

        /// <summary>Shutdown: completes a job nobody started without compiling it (nothing is published, so a later
        /// renderer state never sees it; a waiter gets an empty module).</summary>
        public void Cancel(VulkanRenderer r)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
            _result = new ModuleEntry(default, null);
            Finish(r);
        }

        void Run(VulkanRenderer r)
        {
            var key = (program.Hash, hintKey);
            try
            {
                var e = r.LoadPackedShader(program.Hash, hintKey, program.IsVertex);
                if (e == null)
                {
                    e = r.TranslateAndCompile(program, hintKey, hints, background: true);
                    if (rebuild) Interlocked.Increment(ref r._statRebuiltShaders);
                }
                _result = r.PublishModule(key, e);
            }
            catch (Exception ex)
            {
                Log.Error($"[shader worker] {program.Hash:X16}: {ex.Message}");
                _result = r.PublishModule(key, new ModuleEntry(default, null));
            }
            finally { Finish(r); }
        }

        void Finish(VulkanRenderer r)
        {
            // published (or cancelled) before leaving the in-flight table: a lookup sees the job or the module
            r._inflight.TryRemove(new KeyValuePair<(ulong, ulong), ShaderJob>((program.Hash, hintKey), this));
            Volatile.Write(ref _state, 2);
            _done.Set();
        }
    }

    readonly ConcurrentDictionary<(ulong, ulong), ShaderJob> _inflight = new();
    readonly ConcurrentQueue<ShaderJob> _shaderJobs = new();
    Thread[]? _shaderWorkers;
    uint _prefetchColorFormat; // submitting thread(s): a stale value only mispredicts
    int _statAsyncQueued, _statAsyncWaited;

    /// <summary>Prefetch calls in progress (<see cref="StopShaderWorkers"/> waits for them before tearing down).</summary>
    int _prefetchActive;
    volatile bool _stopShaderJobs;
    /// <summary>Set when the device reported VK_ERROR_DEVICE_LOST: no more background work on it.</summary>
    volatile bool _deviceLost;

    /// <summary>Called on the submitting (guest) thread for every command, before it is queued.</summary>
    public void Prefetch(GpuCommand command)
    {
        if (!_asyncShaders || _stopShaderJobs || _deviceLost) return;
        Interlocked.Increment(ref _prefetchActive);
        try
        {
            if (_stopShaderJobs) return; // re-checked after announcing ourselves (see StopShaderWorkers)
            switch (command)
            {
                case BeginSceneCommand b:
                    _prefetchColorFormat = b.Color?.ColorFormat ?? 0;
                    if (_asyncPipelines) PredictScene(b);
                    break;
                case DrawCommand d:
                    if (d.IndexCount == 0 || d.FragmentProgram.IsMaskUpdate || d.FragmentProgram.Info == null || d.VertexProgram?.Info == null) return;
                    var vp = d.VertexProgram;
                    ulong vk = HintKey(d.VertexTextures, vp.Info.TexturesUsed, vp.Attributes, vp.Streams, 0);
                    if (NeedsJob(vp.Info.Program, vk)) Queue(vp.Info.Program, vk, VertexHints(d, 0));
                    else PromoteRebuildJob(vp.Info.Program.Hash, vk);
                    var fp = d.FragmentProgram;
                    uint cf = _prefetchColorFormat;
                    ulong fk = HintKey(d.FragmentTextures, fp.Info.TexturesUsed, null, null, cf, outputFormat: NativeOutputFormat(fp));
                    if (NeedsJob(fp.Info.Program, fk)) Queue(fp.Info.Program, fk, FragmentHints(d, cf));
                    else PromoteRebuildJob(fp.Info.Program.Hash, fk);
                    if (_asyncPipelines) PrefetchPipeline(d, vk, fk, cf);
                    break;
            }
        }
        finally { Interlocked.Decrement(ref _prefetchActive); }
    }

    // =================================================================== pipeline prefetch

    /// <summary>Scene state the pipeline key depends on, predicted from the last submitted BeginScene with the same
    /// rules as BeginScene/ChooseMsaaPath (a wrong prediction only builds a pipeline nobody uses). Immutable and swapped
    /// as one reference: several guest threads may submit, and a torn tuple could pair a format with a sample count it
    /// does not support.</summary>
    sealed record PredictedScene(bool HasColor, Format RtFormat, Format Attach, SampleCountFlags Samples);

    static readonly PredictedScene NoColorScene = new(false, Format.Undefined, Format.Undefined, SampleCountFlags.Count1Bit);
    PredictedScene _prefetchScene = NoColorScene;
    readonly ConcurrentDictionary<(ulong, ulong), bool> _msOkPrefetch = new();
    readonly ConcurrentDictionary<ulong, byte> _seenDrawSignatures = new(); // several guest threads may submit
    readonly ConcurrentDictionary<PipelineKey, ManualResetEventSlim> _pipelineInflight = new();
    int _statPipelinesPrefetched, _statPipelineWaits;

    void PredictScene(BeginSceneCommand b)
    {
        if (b.Color is not { } c) { _prefetchScene = NoColorScene; return; }
        var f = SurfaceFormat(c.ColorFormat);
        f = RenderableFormat(f);
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
        _prefetchScene = new PredictedScene(true, f, attach, samples);
    }

    void PrefetchPipeline(DrawCommand d, ulong vk, ulong fk, uint colorFormat)
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
        // static-state pipelines (driver quirk) also depend on the draw's depth/stencil/cull state
        ulong staticKey = StaticStateKey(in st, Formats.Topology(d.PrimitiveType));
        if (staticKey != 0) sig = ShaderHints.Fnv.Mix(ShaderHints.Fnv.Mix(sig, (uint)staticKey), (uint)(staticKey >> 32));
        if (!_seenDrawSignatures.TryAdd(sig, 0)) return;
        bool ms = scene.Samples != SampleCountFlags.Count1Bit;
        var job = new PipelineJob(vp, vk, d.FragmentProgram.Info.Program, fk,
            ms ? HintKey(d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed, null, null, colorFormat, true, NativeOutputFormat(d.FragmentProgram)) : 0,
            ms ? FragmentHintsInputFetch(d, colorFormat) : null,
            scene, blend, st.Front.FragmentEnabled, d.PrimitiveType, st.Front.PolygonMode, staticKey);
        if (!EnsureShaderWorkers()) return;
        _pipelineJobs.Enqueue(job);
        _workAvailable.Release();
    }

    readonly ConcurrentQueue<PipelineJob> _pipelineJobs = new();

    sealed record PipelineJob(GxmVertexProgram Vp, ulong Vk, GxpProgram Fs, ulong Fk, ulong FetchKey, ShaderHints? FetchHints,
        PredictedScene Scene, GxmBlendInfo? Blend, bool FragEnabled, byte PrimitiveType, byte PolygonMode, ulong Static);

    /// <summary>Module of a prefetched key: ready, in flight (wait / run it), or null when the shader job is gone.</summary>
    ModuleEntry? ModuleFor(GxpProgram p, ulong hk, ShaderHints? hints)
    {
        var key = (p.Hash, hk);
        if (_modules.TryGetValue(key, out var e)) return e;
        if (_inflight.TryGetValue(key, out var job)) return job.RunOrWait(this);
        if (_modules.TryGetValue(key, out e)) return e;
        if (hints == null || _stopShaderJobs) return null;
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
        byte mask = !j.FragEnabled || j.Fs.IsOutputUndefined ? (byte)0 : j.Blend?.ColorMask ?? 0xF; // see Draw
        bool blendOn = j.Blend is { } bi && !fsM.Gen.UsesFragColor && !(bi.ColorFunc == 0 && bi.AlphaFunc == 0) && !(bi.ColorSrc == 1 && bi.ColorDst == 0 && bi.AlphaSrc == 1 && bi.AlphaDst == 0);
        var key = new PipelineKey(vs.Module.Handle, fs.Module.Handle, vs.LayoutHash, s.Attach, s.HasColor,
            blendOn, j.Blend?.ColorFunc ?? 0, j.Blend?.AlphaFunc ?? 0, j.Blend?.ColorSrc ?? 1, j.Blend?.ColorDst ?? 0, j.Blend?.AlphaSrc ?? 1, j.Blend?.AlphaDst ?? 0,
            mask, TopologyClass(topology), Formats.Polygon(j.PolygonMode), s.Samples, j.Static);
        // (recorded in pipelines.list when the GPU thread first uses it, not here: a misprediction is never recorded)
        if (BuildSharedPipeline(key, vs, fs.Module, topology) == SharedBuild.Built) Interlocked.Increment(ref _statPipelinesPrefetched);
    }

    enum SharedBuild { Exists, Built, Failed }

    /// <summary>Background thread (worker or prewarm): builds a pipeline into the shared table unless it exists or
    /// someone else is building it. The key is claimed first (the GPU thread claims it too when it builds one
    /// itself), so nobody builds the same pipeline twice.</summary>
    SharedBuild BuildSharedPipeline(in PipelineKey key, VsEntry vs, ShaderModule fsModule, PrimitiveTopology topology)
    {
        if (_sharedPipelines.ContainsKey(key)) return SharedBuild.Exists;
        var done = new ManualResetEventSlim();
        if (!_pipelineInflight.TryAdd(key, done)) return SharedBuild.Exists;
        try
        {
            if (_sharedPipelines.ContainsKey(key)) return SharedBuild.Exists;
            long t0 = FrameLedger.Now();
            var p = CreatePipeline(key, vs.Module, fsModule, vs.Bindings, vs.Attributes, topology);
            FrameLedger.AddSince(FrameLedger.Cat.BgPipeline, t0);
            if (p.Handle == 0) return SharedBuild.Failed;
            if (_sharedPipelines.TryAdd(key, p))
            {
                NotePipelineCreated();
                return SharedBuild.Built;
            }
            _vk.DestroyPipeline(_dev, p, null); // another thread got there first
            return SharedBuild.Exists;
        }
        finally
        {
            // published before the claim is released: a lookup sees the claim or the pipeline
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
        long dt = FrameLedger.Now() - t0;
        FrameLedger.Add(FrameLedger.Cat.ShaderWait, dt);
        _statPipelineWaits++;
        _statPipelineWaitTicks += dt;
        _frameStallTicks += dt; // a frame waiting for a worker's pipeline stalls like one compiling it
        return _sharedPipelines.TryGetValue(key, out p);
    }

    bool NeedsJob(GxpProgram p, ulong hk) => !_modules.ContainsKey((p.Hash, hk)) && !_inflight.ContainsKey((p.Hash, hk));

    void Queue(GxpProgram p, ulong hk, ShaderHints hints)
    {
        var job = new ShaderJob(p, hk, hints);
        if (!_inflight.TryAdd((p.Hash, hk), job)) return;
        if (!EnsureShaderWorkers()) { job.Cancel(this); return; } // renderer shutting down: drop it
        Interlocked.Increment(ref _statAsyncQueued);
        _shaderJobs.Enqueue(job);
        _workAvailable.Release();
    }

    /// <summary>Starts the worker pool on first use; false once the renderer is shutting down.</summary>
    bool EnsureShaderWorkers()
    {
        if (_shaderWorkers != null) return !_stopShaderJobs;
        lock (_inflight)
        {
            if (_stopShaderJobs) return false;
            if (_shaderWorkers != null) return true;
            var workers = new Thread[_shaderWorkerCount];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(() =>
                {
                    NativeThread.Register("Shader worker");
                    WorkerLoop();
                }) { IsBackground = true, Name = $"Shader worker {i}", Priority = WorkerPriority };
                workers[i].Start();
            }
            _shaderWorkers = workers;
            return true;
        }
    }

    void WorkerLoop()
    {
        while (true)
        {
            // shader jobs first: pipeline jobs need their modules
            if (_shaderJobs.TryDequeue(out var sj))
            {
                if (_stopShaderJobs || _deviceLost) sj.Cancel(this);
                else sj.TryRun(this);
                continue;
            }
            if (_pipelineJobs.TryDequeue(out var pj))
            {
                if (!_stopShaderJobs && !_deviceLost)
                    try { RunPipelineJob(pj); }
                    catch (Exception e) { Log.WarnOnce("pipeline-prefetch", $"[shader worker] pipeline prefetch failed: {e.Message}"); }
                continue;
            }
            // the boot rebuild comes last: whatever the running game asks for goes first
            if (_rebuildJobs.TryDequeue(out var rj))
            {
                RunRebuildItem(rj);
                continue;
            }
            if (_stopShaderJobs) return;
            _workAvailable.Wait(50);
        }
    }

    readonly SemaphoreSlim _workAvailable = new(0);

    /// <summary>
    /// Renderer teardown, before any Vulkan object is destroyed: stops new prefetches (and waits for the ones running on
    /// guest threads), cancels queued jobs, waits for the jobs a worker is executing (they create objects on this
    /// device) and joins the workers. Anything published by then is owned by the module/pipeline tables and destroyed
    /// with them.
    /// </summary>
    void StopShaderWorkers()
    {
        lock (_inflight) _stopShaderJobs = true;
        var spin = new SpinWait();
        while (Volatile.Read(ref _prefetchActive) != 0) spin.SpinOnce();
        if (_shaderWorkers != null)
        {
            _workAvailable.Release(_shaderWorkers.Length);
            foreach (var t in _shaderWorkers) t.Join();
        }
        while (_shaderJobs.TryDequeue(out var sj)) sj.Cancel(this);
        _pipelineJobs.Clear();
        DrainRebuildQueue();
        foreach (var job in _inflight.Values) job.Cancel(this);
    }
}
