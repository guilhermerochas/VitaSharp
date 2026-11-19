using Silk.NET.Vulkan;
using System.Collections.Concurrent;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// "Compilar shaders ao carregar o programa" (<see cref="VulkanRendererOptions.CompileOnProgramLoad"/>): a Vita game
/// creates its vertex and fragment program objects (sceGxmShaderPatcherCreateVertexProgram / CreateFragmentProgram)
/// while it loads, long before their first draw, and those calls already fix most of what a translated shader and its
/// pipeline depend on: the vertex attribute formats and streams, the fragment program's output register format,
/// multisample mode and blend, and the vertex program it is made for. The shaders are queued to the shader workers
/// right then, with the rest predicted: textures by class (<see cref="ShaderHints.CanonicalTextureFormat"/>: every
/// four-unsigned-byte format, compressed ones included, translates alike), the colour surface of the last scene, and
/// for each vertex program object made from the named program the whole pipeline (triangles, filled, the predicted
/// scene). The first draw then finds its module and pipeline built instead of compiling them on the GPU thread.
/// <para>A draw whose exact formats differ from the prediction but translate to the same shader (same
/// <see cref="ShaderHints.CanonicalKey"/>) is served the predicted module (<see cref="TryProgramLoadModule"/>), so its
/// pipeline key, which names the modules, is the predicted one too. A wrong prediction only builds a module or a
/// pipeline nobody uses; the draw then compiles its own as before. This is the Dolphin/Ryujinx idea of compiling ahead
/// of use, at the earliest moment the console's API gives; it is not an ubershader (the shader is still the program's
/// own translation, so the cost of a program never seen at creation stays at its draw).</para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private bool _compileOnProgramLoad;

    /// <summary>Vertex program objects by their program's hash (the latest <see cref="VertexProgramsPerProgram"/>): a
    /// fragment program names the program, the pipeline needs the object (attributes and streams).</summary>
    private readonly ConcurrentDictionary<ulong, GxmVertexProgram[]> _loadedVertexPrograms = new();
    private const int VertexProgramsPerProgram = 4;

    /// <summary>(program hash, <see cref="ShaderHints.CanonicalKey"/>) → the hint key a module was queued with at
    /// program load.</summary>
    private readonly ConcurrentDictionary<(ulong, ulong), ulong> _programLoadKeys = new();

    private int _statLoadShaders, _statLoadPipelines, _statLoadPipelinesBuilt, _statLoadAliases;

    /// <summary>Program-load jobs queued and modules/pipelines built (tests and the session line).</summary>
    public (int Shaders, int Pipelines, int PipelinesBuilt, int Aliases) ProgramLoadStats =>
        (Volatile.Read(ref _statLoadShaders), Volatile.Read(ref _statLoadPipelines), Volatile.Read(ref _statLoadPipelinesBuilt), Volatile.Read(ref _statLoadAliases));

    private void ConfigureProgramLoad(VulkanRendererOptions vulkanRendererOptions)
    {
        _compileOnProgramLoad = Environment.GetEnvironmentVariable(EnvVars.CompileOnProgramLoad)?.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "no" => false,
            "1" or "on" or "true" or "yes" => true,
            _ => vulkanRendererOptions.CompileOnProgramLoad,
        };
    }

    private bool ProgramLoadActive => _compileOnProgramLoad && !_stopShaderJobs && !_deviceLost;

    public void VertexProgramCreated(GxmVertexProgram vertexProgram)
    {
        if (!ProgramLoadActive || vertexProgram.Info == null)
        {
            return;
        }

        _loadedVertexPrograms.AddOrUpdate(vertexProgram.Info.Program.Hash, _ => [vertexProgram], (_, old) =>
        {
            if (Array.IndexOf(old, vertexProgram) >= 0)
            {
                return old;
            }

            int keep = Math.Min(old.Length, VertexProgramsPerProgram - 1);
            var list = new GxmVertexProgram[keep + 1];
            Array.Copy(old, old.Length - keep, list, 0, keep);
            list[keep] = vertexProgram;
            return list;
        });
        // without varying trimming the vertex shader does not depend on its fragment program (the usual case)
        if (!s_varyingTrim)
        {
            QueueProgramLoadShader(vertexProgram.Info.Program, PredictedVertexHints(vertexProgram, 0, 0));
        }
    }

    public void FragmentProgramCreated(GxmFragmentProgram fragmentProgram, GxmProgramInfo? vertexProgram)
    {
        if (!ProgramLoadActive || fragmentProgram.IsMaskUpdate || fragmentProgram.Info == null)
        {
            return;
        }

        _loadedFragmentPrograms.TryAdd(fragmentProgram, vertexProgram);
        // the last scene (or the default one before any), then every surface the game has made known
        QueueForScene(fragmentProgram, vertexProgram, _prefetchScene.HasColor ? new KnownScene(_prefetchColorFormat, _prefetchScene) : DefaultKnownScene());
        if (!DependsOnSurface(fragmentProgram.Info.Program))
        {
            return;
        }

        foreach (KnownScene known in _knownScenes.Keys)
        {
            QueueForScene(fragmentProgram, vertexProgram, known);
        }
    }

    /// <summary>
    /// Programs whose draws take another pipeline family on another surface: the ones that read the surface colour
    /// (programmable blending: the fetch pass or input attachment) and the native-colour / integer outputs (raw bits
    /// through an integer view). Only these are built for every known surface; building every program for every
    /// surface multiplied the pipelines (23 fragment programs x 8 surfaces of one dump: 231 queued, 19 used) for the two
    /// families that the draw itself cannot predict, while plain programs on another surface are left to the draw
    /// prefetch as before.
    /// </summary>
    private static bool DependsOnSurface(GxpProgram program) =>
        program.IsFragColorUsed || program.IsNativeColor || GxpProgram.StoreType(program.OutputParamType) is DataType.Int32 or DataType.UInt32;

    /// <summary>A surface draws are predicted to render into: the raw SceGxmColorFormat (the fragment shader's hint) and
    /// the scene state the pipeline key takes from it.</summary>
    private readonly record struct KnownScene(uint ColorFormat, PredictedScene Scene);

    /// <summary>Surfaces made known by the game (colour surfaces it initialised, scenes it began), each built for every
    /// created fragment program.</summary>
    private readonly ConcurrentDictionary<KnownScene, byte> _knownScenes = new();
    /// <summary>Every fragment program created, with the vertex program it was made for.</summary>
    private readonly ConcurrentDictionary<GxmFragmentProgram, GxmProgramInfo?> _loadedFragmentPrograms = new(ReferenceEqualityComparer.Instance);
    /// <summary>(fragment program, scene) pairs already queued.</summary>
    private readonly ConcurrentDictionary<(GxmFragmentProgram, KnownScene), byte> _queuedCombinations = new();
    /// <summary>Multisample modes of the render targets created (a downscaled colour surface renders with them).</summary>
    private int _knownMultisampleModes;
    private int _statKnownScenes;

    private KnownScene DefaultKnownScene() => new(0, DefaultScene());

    /// <summary>
    /// A surface the game will render into is known (sceGxmColorSurfaceInit, a scene it begins, the GPU dump replay's
    /// scan): its combinations with every fragment program created so far are built in the background, before their
    /// draws. A downscaled surface is also predicted with each multisample mode of the render targets created.
    /// </summary>
    public void ColorSurfaceKnown(uint colorFormat, uint gammaMode, uint multisample, bool downscale)
    {
        if (!ProgramLoadActive)
        {
            return;
        }

        AddKnownScene(new KnownScene(colorFormat, SceneFor(colorFormat, gammaMode, multisample, downscale)));
        if (downscale && multisample == 0)
        {
            int modes = Volatile.Read(ref _knownMultisampleModes);
            for (uint mode = 1; mode <= 2; mode++)
            {
                if ((modes & (1 << (int)mode)) != 0)
                {
                    AddKnownScene(new KnownScene(colorFormat, SceneFor(colorFormat, gammaMode, mode, downscale: true)));
                }
            }
        }
    }

    public void ColorSurfaceInitialised(uint colorFormat, bool downscale) => ColorSurfaceKnown(colorFormat, 0, 0, downscale);

    /// <summary>A render target was created with <paramref name="multisample"/> (SceGxmMultisampleMode).</summary>
    public void RenderTargetCreated(uint multisample)
    {
        if (multisample is 1 or 2)
        {
            int bit = 1 << (int)multisample;
            int old;
            do
            {
                old = Volatile.Read(ref _knownMultisampleModes);
            }
            while ((old & bit) == 0 && Interlocked.CompareExchange(ref _knownMultisampleModes, old | bit, old) != old);
        }
    }

    private void AddKnownScene(KnownScene known)
    {
        if (!_knownScenes.TryAdd(known, 0))
        {
            return;
        }

        Interlocked.Increment(ref _statKnownScenes);
        foreach ((GxmFragmentProgram fragmentProgram, GxmProgramInfo? vertexProgram) in _loadedFragmentPrograms)
        {
            if (DependsOnSurface(fragmentProgram.Info.Program))
            {
                QueueForScene(fragmentProgram, vertexProgram, known);
            }
        }
    }

    /// <summary>The <see cref="PredictedScene"/> of a surface, by the rules of <see cref="PredictScene"/>.</summary>
    private PredictedScene SceneFor(uint colorFormat, uint gammaMode, uint multisample, bool downscale)
    {
        Format f = RenderableFormat(SurfaceFormat(colorFormat));
        Format srgb = Formats.Srgb(f);
        Format attach = gammaMode != 0 && srgb != f && _core.SupportsColorAttachment(srgb) ? srgb : f;
        SampleCountFlags samples = SampleCountFlags.Count1Bit;
        if (multisample != 0 && downscale && _msaaSetting == MsaaEmulation.Native)
        {
            SampleCountFlags sampleCountFlags = GxmSamples(multisample);
            bool ok = _msOkPrefetch.GetOrAdd(((ulong)f, (ulong)sampleCountFlags), _ =>
                (_core.ImageSampleCounts(f, MsaaImageUsage) & sampleCountFlags) != 0 && (_core.Properties.Limits.FramebufferColorSampleCounts & sampleCountFlags) != 0);
            if (ok)
            {
                samples = sampleCountFlags;
            }
        }

        return new PredictedScene(true, f, attach, samples);
    }

    /// <summary>Queues the fragment program's shader for <paramref name="known"/> and, for each vertex program object of
    /// the program it was made for, the pipeline (with the variants the draw may need there: the integer view of a
    /// raw-bit program, the fetch pass of a programmable-blending one).</summary>
    private void QueueForScene(GxmFragmentProgram fragmentProgram, GxmProgramInfo? vertexProgram, KnownScene known)
    {
        if (!_queuedCombinations.TryAdd((fragmentProgram, known), 0))
        {
            return;
        }

        uint colorFormat = known.ColorFormat;
        PredictedScene scene = known.Scene;
        ulong fragmentKey = QueueProgramLoadShader(fragmentProgram.Info.Program, PredictedFragmentHints(fragmentProgram, colorFormat, fetchInput: false));
        if (vertexProgram == null || !_loadedVertexPrograms.TryGetValue(vertexProgram.Program.Hash, out GxmVertexProgram[]? vertexPrograms))
        {
            return;
        }

        bool multisampled = scene.Samples != SampleCountFlags.Count1Bit;
        ShaderHints fetchHints = multisampled ? PredictedFragmentHints(fragmentProgram, colorFormat, fetchInput: true)
            : PredictedFragmentHints(fragmentProgram, colorFormat, fetchInput: true) with { FetchInputSingleSample = true };
        uint varyingMask = s_varyingTrim ? ShaderGenerator.FragmentVaryingMask(fragmentProgram.Info.Program) : 0;
        foreach (GxmVertexProgram vp in vertexPrograms)
        {
            ulong vertexKey = QueueProgramLoadShader(vp.Info.Program, PredictedVertexHints(vp, varyingMask, fragmentProgram.TexcoordRemap));
            // drivers with broken extended dynamic state bake the draw's depth/stencil state into the pipeline: not predictable
            if (StaticEds || !EnsureShaderWorkers())
            {
                continue;
            }

            var job = new PipelineJob(vp, vertexKey, fragmentProgram.Info.Program, fragmentKey, fetchHints.Key(), fetchHints, scene, fragmentProgram.Blend,
                FragEnabled: true, PrimitiveType: 0, PolygonMode: 0, Static: 0, ProgramLoad: true);
            Interlocked.Increment(ref _statLoadPipelines);
            _pipelineJobs.Enqueue(job);
            _workAvailable.Release();
        }
    }

    /// <summary>Worker (program load and draw prefetch): the fetch-pass pipeline of a programmable-blending program in a
    /// one-sample scene (the draw's key with <see cref="PipelineKey.InputPass"/>, the single-sample input attachment
    /// variant of its shader, no blend).</summary>
    private void BuildFetchPassPipeline(PipelineJob j, VsEntry vs, PrimitiveTopology topology, byte mask, Format attach)
    {
        ModuleEntry? fetch = ModuleFor(j.Fs, j.FetchKey, j.FetchHints);
        if (fetch?.Gen == null || fetch.Module.Handle == 0)
        {
            return;
        }

        var key = new PipelineKey(vs.Module.Handle, fetch.Module.Handle, vs.LayoutHash, attach, true,
            false, j.Blend?.ColorFunc ?? 0, j.Blend?.AlphaFunc ?? 0, j.Blend?.ColorSrc ?? 1, j.Blend?.ColorDst ?? 0, j.Blend?.AlphaSrc ?? 1, j.Blend?.AlphaDst ?? 0,
            mask, TopologyClass(topology), Formats.Polygon(j.PolygonMode), SampleCountFlags.Count1Bit, j.Static, 0, InputPass: true);
        if (j.ProgramLoad)
        {
            DbgProgramLoadPipeline(j.Fs.Hash, j.FetchKey, key);
        }

        if (BuildSharedPipeline(key, vs, fetch.Module, topology) == SharedBuild.Built)
        {
            Interlocked.Increment(ref j.ProgramLoad ? ref _statLoadPipelinesBuilt : ref _statPipelinesPrefetched);
        }
    }

    /// <summary>The scene a draw before any submitted scene is predicted to render into: one-sample U8U8U8U8 (the
    /// colour format 0, A8B8G8R8).</summary>
    private PredictedScene DefaultScene()
    {
        Format f = RenderableFormat(SurfaceFormat(0));
        return new PredictedScene(true, f, f, SampleCountFlags.Count1Bit);
    }

    private static uint[] DefaultTextureFormats()
    {
        uint[] formats = new uint[16];
        Array.Fill(formats, 0x0C000000u);
        return formats;
    }

    /// <summary>The hints of the program's draws as <see cref="VertexHints"/> builds them, with vertex textures (rare)
    /// predicted as four unsigned bytes.</summary>
    private static ShaderHints PredictedVertexHints(GxmVertexProgram vertexProgram, uint varyingMask, ulong texcoordRemap) =>
        new(DefaultTextureFormats(), vertexProgram.Attributes.Select(a => ((uint)a.RegIndex, (int)a.ComponentCount, AttributeFormatKey(a, vertexProgram.Streams))).ToArray(), 0,
            VaryingMask: varyingMask, ScaledPointSize: vertexProgram.Info.Program.WritesPointSize, TexcoordRemap: texcoordRemap);

    /// <summary>The hints of the program's draws as <see cref="FragmentHints"/> / <see cref="FragmentHintsInputFetch"/>
    /// build them, with the textures predicted as four unsigned bytes.</summary>
    private static ShaderHints PredictedFragmentHints(GxmFragmentProgram fragmentProgram, uint colorFormat, bool fetchInput) =>
        new(DefaultTextureFormats(), [], colorFormat, FetchInputAttachment: fetchInput, OutputRegisterFormat: NativeOutputFormat(fragmentProgram));

    /// <summary>Queues the module of <paramref name="hints"/> unless it exists or is queued, and remembers it under its
    /// canonical key. Returns its hint key.</summary>
    private ulong QueueProgramLoadShader(GxpProgram program, ShaderHints hints)
    {
        ulong hintKey = hints.Key();
        _programLoadKeys.TryAdd((program.Hash, hints.CanonicalKey()), hintKey);
        if (DebugMode.Enabled)
        {
            Log.Info($"[dbg] program load: shader {program.Hash:X16} hint {hintKey:X16} canonical {hints.CanonicalKey():X16} textures {DbgTextureFormats(hints.TextureFormats, program)} colour 0x{hints.ColorFormat:X8} fetch {hints.FetchInputAttachment}/{hints.FetchInputSingleSample}");
        }

        if (NeedsJob(program, hintKey))
        {
            Interlocked.Increment(ref _statLoadShaders);
            Queue(program, hintKey, hints);
        }

        return hintKey;
    }

    /// <summary>
    /// A draw's module that is not in the tables under its own key: the module queued at program load for hints that
    /// translate to the same shader (<see cref="ShaderHints.CanonicalKey"/>), published under the draw's key as well
    /// (waited for when a worker is still building it). False when there is none.
    /// </summary>
    private bool TryProgramLoadModule(GxpProgram program, ulong hintKey, ShaderHints hints, bool wait, out ModuleEntry? module)
    {
        module = null;
        if (_programLoadKeys.IsEmpty || !_programLoadKeys.TryGetValue((program.Hash, hints.CanonicalKey()), out ulong loadKey) || loadKey == hintKey)
        {
            return false;
        }

        if (!_modules.TryGetValue((program.Hash, loadKey), out ModuleEntry? loaded))
        {
            if (!_inflight.TryGetValue((program.Hash, loadKey), out ShaderJob? job))
            {
                return _modules.TryGetValue((program.Hash, loadKey), out loaded) && PublishAlias(program.Hash, hintKey, loaded, out module);
            }

            if (!wait)
            {
                return true; // the GPU thread waits for it (or runs it) at the draw
            }

            loaded = job.RunOrWait(this);
            Interlocked.Increment(ref _statAsyncWaited);
        }

        return PublishAlias(program.Hash, hintKey, loaded, out module);
    }

    /// <summary>Publishes <paramref name="loaded"/> under a second key (never destroyed twice: <see cref="DestroyPipelines"/>
    /// destroys each module once).</summary>
    private bool PublishAlias(ulong programHash, ulong hintKey, ModuleEntry loaded, out ModuleEntry? module)
    {
        module = _modules.GetOrAdd((programHash, hintKey), loaded);
        if (ReferenceEquals(module, loaded))
        {
            Interlocked.Increment(ref _statLoadAliases);
        }

        return true;
    }

    /// <summary>
    /// Waits until the shader workers have nothing queued or running for draws or program loads (the boot rebuild and the
    /// prewarm are not waited for): the GPU dump replay's stand-in for a game's loading screen, and tests. False when
    /// <paramref name="cancel"/> stopped the wait first.
    /// </summary>
    public bool WaitForBackgroundCompiles(CancellationToken cancel)
    {
        var spin = new SpinWait();
        while (!_shaderJobs.IsEmpty || !_pipelineJobs.IsEmpty || !_inflight.IsEmpty || !_pipelineInflight.IsEmpty || Volatile.Read(ref _pipelineJobsRunning) != 0)
        {
            if (cancel.IsCancellationRequested || _stopShaderJobs || _deviceLost)
            {
                return false;
            }

            spin.SpinOnce(sleep1Threshold: 20);
        }

        return true;
    }

    /// <summary>Debug mode: the pipelines built at program load, by fragment program hash (with the fragment hint key),
    /// to tell what a pipeline the GPU thread had to build was predicted as.</summary>
    private readonly ConcurrentDictionary<ulong, ConcurrentQueue<(ulong FsHintKey, PipelineKey Key)>> _dbgProgramLoadPipelines = new();

    private void DbgProgramLoadPipeline(ulong fsProgramHash, ulong fsHintKey, in PipelineKey key)
    {
        if (DebugMode.Enabled)
        {
            _dbgProgramLoadPipelines.GetOrAdd(fsProgramHash, _ => new()).Enqueue((fsHintKey, key));
        }
    }

    /// <summary>Debug mode: a pipeline the GPU thread builds itself, with the closest program-load prediction for the
    /// same fragment program and the key fields that differ.</summary>
    private void DbgGpuThreadPipeline(in PipelineKey key, VsEntry vs, FsEntry fs)
    {
        string predicted = "none";
        if (_dbgProgramLoadPipelines.TryGetValue(fs.ProgramHash, out ConcurrentQueue<(ulong FsHintKey, PipelineKey Key)>? list))
        {
            int best = int.MaxValue;
            foreach ((ulong fsHintKey, PipelineKey candidate) in list)
            {
                List<string> diff = PipelineKeyDiff(key, candidate);
                if (fsHintKey != fs.HintKey)
                {
                    diff.Add($"fs hints {fsHintKey:X16}");
                }

                if (diff.Count < best)
                {
                    best = diff.Count;
                    predicted = $"{list.Count} predicted, closest differs in [{string.Join(", ", diff)}]";
                }
            }
        }

        Dbg($"pipeline built on the GPU thread: vs {vs.ProgramHash:X16} hints {vs.HintKey:X16} fs {fs.ProgramHash:X16} hints {fs.HintKey:X16} {key}; program load: {predicted}");
    }

    private static List<string> PipelineKeyDiff(in PipelineKey a, in PipelineKey b)
    {
        var diff = new List<string>();
        void Check(string name, object x, object y)
        {
            if (!Equals(x, y))
            {
                diff.Add($"{name} {y}");
            }
        }

        Check("Vs", a.Vs, b.Vs);
        Check("Fs", a.Fs, b.Fs);
        Check("VertexLayout", a.VertexLayout, b.VertexLayout);
        Check("ColorFormat", a.ColorFormat, b.ColorFormat);
        Check("HasColor", a.HasColor, b.HasColor);
        Check("Blend", a.Blend, b.Blend);
        Check("BlendFactors", (a.ColorFunc, a.AlphaFunc, a.ColorSrc, a.ColorDst, a.AlphaSrc, a.AlphaDst), (b.ColorFunc, b.AlphaFunc, b.ColorSrc, b.ColorDst, b.AlphaSrc, b.AlphaDst));
        Check("Mask", a.Mask, b.Mask);
        Check("TopologyClass", a.TopologyClass, b.TopologyClass);
        Check("Polygon", a.Polygon, b.Polygon);
        Check("Samples", a.Samples, b.Samples);
        Check("Static", a.Static, b.Static);
        Check("TileWord1", a.TileWord1, b.TileWord1);
        Check("InputPass", a.InputPass, b.InputPass);
        return diff;
    }

    private static string DbgTextureFormats(uint[] formats, GxpProgram program) => string.Join(",", formats.Select((f, unit) => (f, unit)).Where(t => (program.TexturesUsed() & (1u << t.unit)) != 0 || t.f != 0x0C000000u).Select(t => $"{t.unit}:{t.f:X8}"));

    private string ProgramLoadReport() => _compileOnProgramLoad
        ? $"; at program load: {_statLoadShaders} shaders and {_statLoadPipelines} pipelines queued for {_loadedFragmentPrograms.Count} fragment programs x {_statKnownScenes + 1} surfaces ({_statLoadPipelinesBuilt} built), {_statLoadAliases} draws served by a module of the same canonical hints"
        : "; at program load: off";
}
