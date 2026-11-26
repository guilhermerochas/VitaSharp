using Silk.NET.Vulkan;
using System.Collections.Concurrent;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// First-use pipelines through VK_EXT_graphics_pipeline_library, as DXVK (dxvk_graphics.cpp: "fast-linked" pipelines
/// from shader libraries, optimised pipelines compiled in the background) and vkd3d-proton do. A pipeline is split into
/// four libraries: vertex input (layout and topology class), pre-rasterisation (the vertex module), fragment shader
/// (the fragment module and sample count) and fragment output (formats, blend, write mask). The two shader libraries
/// are what the driver compiles; each is built once per module and render pass and reused by every pipeline that has
/// that module, and linking them without link-time optimisation costs a fraction of a millisecond.
/// <para>Where a pipeline used to be compiled whole on the GPU thread or on a prefetch worker, it is now linked
/// (<see cref="LinkPipeline"/>), and the whole, optimised pipeline is queued to the shader workers with the lowest
/// priority (after the draw-driven shader and pipeline jobs, the boot rebuild and the prewarm). The GPU thread swaps
/// it in at its next pipeline lookup once it is ready (<see cref="SwapOptimizedPipelines"/>); the linked pipeline is
/// destroyed after the GPU finished the frames that used it. The image does not change: the stages, the state and the
/// SPIR-V are the same in both pipelines.</para>
/// <para>Off by default (VITASHARP_PIPELINE_LIBRARY=1 / <see cref="VulkanRendererOptions.PipelineLibrary"/> turn it
/// on): with cold caches on the GTX 1650 a shader library costs about half a whole pipeline and the libraries took
/// worker time from the prefetch, so Killzone stalled more than with whole pipelines (docs/notes/pipeline-skip.md).
/// Used only when the device reports graphicsPipelineLibraryFastLinking, with dynamic rendering and the extended
/// dynamic state (<see cref="VkCore.GraphicsPipelineLibrary"/>); everywhere else (MoltenVK, Dozen, phone drivers,
/// static-state quirks) every pipeline is compiled whole as before.</para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Fast-linked first-use pipelines are in use (device support and VITASHARP_PIPELINE_LIBRARY).</summary>
    private bool _pipelineLibrary;

    private readonly ConcurrentDictionary<(ulong Layout, int TopologyClass), Lazy<Pipeline>> _vertexInputLibraries = new();
    private readonly ConcurrentDictionary<(ulong VertexModule, PolygonMode Polygon, ulong Pass), Lazy<Pipeline>> _preRasterLibraries = new();
    private readonly ConcurrentDictionary<(ulong FragmentModule, SampleCountFlags Samples, ulong Pass), Lazy<Pipeline>> _fragmentLibraries = new();
    private readonly ConcurrentDictionary<OutputLibraryKey, Lazy<Pipeline>> _outputLibraries = new();

    private readonly record struct OutputLibraryKey(Format ColorFormat, bool HasColor, byte TileWord1, bool Blend, byte ColorFunc, byte AlphaFunc,
        byte ColorSrc, byte ColorDst, byte AlphaSrc, byte AlphaDst, byte Mask, SampleCountFlags Samples, ulong Pass);

    /// <summary>Keys whose pipeline in <see cref="_sharedPipelines"/> is a linked one (any thread links; the GPU thread
    /// swaps).</summary>
    private readonly ConcurrentDictionary<PipelineKey, byte> _linkedKeys = new();
    /// <summary>Optimised pipelines finished by a worker and not yet swapped in by the GPU thread.</summary>
    private readonly ConcurrentDictionary<PipelineKey, Pipeline> _optimizedPipelines = new();
    private readonly ConcurrentQueue<OptimizeJob> _optimizeJobs = new();
    private int _optimizedReady, _optimizedSeen;
    /// <summary>Optimise jobs queued and finished (whatever their outcome), for <see cref="WaitForOptimizedPipelines"/>.</summary>
    private int _optimizeQueued, _optimizeFinished;

    private sealed record OptimizeJob(PipelineKey Key, VsEntry Vs, ShaderModule FragmentModule, PrimitiveTopology Topology);

    private int _statLinkedOnGpuThread, _statLibrariesOnGpuThread, _statLinkedInBackground, _statOptimized, _statSwapped, _statLinkFailed;
    private long _statLinkTicks;

    /// <summary>Pipeline creation counters of this renderer (the deterministic measure of what the GPU thread compiled
    /// on its own critical path and what was left to the background).</summary>
    /// <param name="Supported">the device can link pipelines from libraries (<see cref="VkCore.GraphicsPipelineLibrary"/>)</param>
    /// <param name="PipelineLibrary">this renderer links them (supported and turned on)</param>
    /// <param name="CompiledOnGpuThread">whole pipelines compiled by the GPU thread</param>
    /// <param name="WaitedFor">pipelines the GPU thread waited for while a worker built them</param>
    /// <param name="LinkedOnGpuThread">pipelines the GPU thread linked from libraries</param>
    /// <param name="LibrariesOnGpuThread">shader libraries the GPU thread compiled (or waited for) to link one</param>
    /// <param name="BuiltInBackground">whole pipelines a prefetch worker built before the GPU thread needed them</param>
    /// <param name="LinkedInBackground">pipelines a prefetch worker linked before the GPU thread needed them</param>
    /// <param name="Optimized">optimised pipelines built in the background to replace linked ones</param>
    /// <param name="Swapped">linked pipelines the GPU thread replaced with their optimised pipeline</param>
    public readonly record struct PipelineStatistics(bool Supported, bool PipelineLibrary, int CompiledOnGpuThread, int WaitedFor, int LinkedOnGpuThread,
        int LibrariesOnGpuThread, int BuiltInBackground, int LinkedInBackground, int Optimized, int Swapped)
    {
        /// <summary>Pipelines whose shaders the GPU thread compiled or waited for: whole pipelines, waits, and links
        /// that needed a shader library first.</summary>
        public int CriticalPath => CompiledOnGpuThread + WaitedFor + LibrariesOnGpuThread;
    }

    public PipelineStatistics PipelineStats => new(_core.GraphicsPipelineLibrary, _pipelineLibrary, _statGpuPipelines, _statPipelineWaits, Volatile.Read(ref _statLinkedOnGpuThread),
        Volatile.Read(ref _statLibrariesOnGpuThread), Volatile.Read(ref _statPipelinesPrefetched), Volatile.Read(ref _statLinkedInBackground),
        Volatile.Read(ref _statOptimized), _statSwapped);

    private void InitPipelineLibrary(bool option)
    {
        string? setting = Environment.GetEnvironmentVariable(EnvVars.PipelineLibrary)?.Trim().ToLowerInvariant();
        bool wanted = setting switch
        {
            "0" or "off" or "false" or "no" => false,
            "1" or "on" or "true" or "yes" => true,
            _ => option,
        };
        _pipelineLibrary = wanted && _core.GraphicsPipelineLibrary && !UseRenderPasses && !StaticEds && !StaticBias;
        string state = _pipelineLibrary ? "first-use pipelines linked from shader libraries, optimised in the background"
            : !_core.GraphicsPipelineLibrary ? "not available (whole pipelines)"
            : wanted ? "available, not used (render passes or static pipeline state)"
            : "off (VITASHARP_PIPELINE_LIBRARY=1 turns it on)";
        Log.Info($"[gpu] graphics pipeline library: {state}");
    }

    /// <summary>The render pass a pipeline of this key is built for, as in <see cref="CreatePipeline"/>: multisampled
    /// scenes and one-sample programmable-blending draws render in render passes, the rest with dynamic rendering.</summary>
    private RenderPass LibraryPass(in PipelineKey key)
    {
        bool msPass = key.Samples != SampleCountFlags.Count1Bit || key.InputPass;
        return msPass ? MsaaCompatPass(key.ColorFormat, key.Samples, key.HasColor && key.TileWord1 != 0, key.Rtss) : default;
    }

    /// <summary>
    /// Any thread: the pipeline of <paramref name="key"/> linked from its four libraries (built here when missing),
    /// published in <see cref="_sharedPipelines"/>, with the optimised pipeline queued. Returns default when a library
    /// or the link failed: the caller compiles the whole pipeline instead.
    /// </summary>
    /// <param name="gpuThread">counts the shader libraries this call had to compile or wait for as GPU-thread work</param>
    private Pipeline LinkPipeline(in PipelineKey key, VsEntry vs, ShaderModule fragmentModule, PrimitiveTopology topology, bool gpuThread)
    {
        RenderPass pass = LibraryPass(key);
        PolygonMode polygon = _core.FillModeNonSolid ? key.Polygon : PolygonMode.Fill;
        Lazy<Pipeline> preRaster = PreRasterLibrary(vs.Module, polygon, pass);
        Lazy<Pipeline> fragment = FragmentLibrary(fragmentModule, key.Samples, pass);
        if (gpuThread)
        {
            int missing = (preRaster.IsValueCreated ? 0 : 1) + (fragment.IsValueCreated ? 0 : 1);
            _statLibrariesOnGpuThread += missing;
        }

        Pipeline vertexInput = VertexInputLibrary(key, vs, topology).Value;
        Pipeline output = OutputLibrary(key, pass).Value;
        Pipeline preRasterPipeline = preRaster.Value;
        Pipeline fragmentPipeline = fragment.Value;
        if (vertexInput.Handle == 0 || preRasterPipeline.Handle == 0 || fragmentPipeline.Handle == 0 || output.Handle == 0)
        {
            Interlocked.Increment(ref _statLinkFailed);
            return default;
        }

        long t0 = FrameLedger.Now();
        Pipeline* libraries = stackalloc Pipeline[4] { vertexInput, preRasterPipeline, fragmentPipeline, output };
        var link = new PipelineLibraryCreateInfoKHR { SType = StructureType.PipelineLibraryCreateInfoKhr, LibraryCount = 4, PLibraries = libraries };
        var createInfo = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, PNext = &link, Layout = _pipelineLayout };
        Result result = _vk.CreateGraphicsPipelines(_dev, default, 1, in createInfo, null, out Pipeline linked);
        Interlocked.Add(ref _statLinkTicks, FrameLedger.Now() - t0);
        if (result != Result.Success)
        {
            Log.WarnOnce("gpl-link", $"[gpu] pipeline library link failed: {result}");
            Interlocked.Increment(ref _statLinkFailed);
            return default;
        }

        if (!_sharedPipelines.TryAdd(key, linked))
        {
            _vk.DestroyPipeline(_dev, linked, null); // another thread published this key first
            return _sharedPipelines[key];
        }

        NotePipelineCreated();
        _linkedKeys.TryAdd(key, 0);
        if (gpuThread)
        {
            _statLinkedOnGpuThread++;
        }
        else
        {
            Interlocked.Increment(ref _statLinkedInBackground);
        }

        if (EnsureShaderWorkers())
        {
            Interlocked.Increment(ref _optimizeQueued);
            _optimizeJobs.Enqueue(new OptimizeJob(key, vs, fragmentModule, topology));
            _workAvailable.Release();
        }

        return linked;
    }

    /// <summary>Shader worker, when nothing else is queued: the whole pipeline of a linked key, handed to the GPU
    /// thread through <see cref="_optimizedPipelines"/>.</summary>
    private bool RunOptimizeJob()
    {
        if (!_optimizeJobs.TryDequeue(out OptimizeJob? job))
        {
            return false;
        }

        try
        {
            BuildOptimized(job);
        }
        finally
        {
            Interlocked.Increment(ref _optimizeFinished);
        }

        return true;
    }

    private void BuildOptimized(OptimizeJob job)
    {
        if (_stopShaderJobs || _deviceLost)
        {
            return;
        }

        long t0 = FrameLedger.Now();
        Pipeline optimized = CreatePipeline(job.Key, job.Vs.Module, job.FragmentModule, job.Vs.Bindings, job.Vs.Attributes, job.Topology);
        FrameLedger.AddSince(FrameLedger.Cat.BgPipeline, t0);
        if (optimized.Handle == 0)
        {
            return; // the linked pipeline stays
        }

        if (!_optimizedPipelines.TryAdd(job.Key, optimized))
        {
            _vk.DestroyPipeline(_dev, optimized, null);
            return;
        }

        Interlocked.Increment(ref _statOptimized);
        Interlocked.Increment(ref _optimizedReady);
    }

    /// <summary>Tests: returns once every optimised pipeline queued so far was built (or given up); the GPU thread
    /// swaps them in at its next pipeline lookup.</summary>
    public void WaitForOptimizedPipelines()
    {
        var spin = new SpinWait();
        while (Volatile.Read(ref _optimizeFinished) != Volatile.Read(ref _optimizeQueued) && !_stopShaderJobs)
        {
            spin.SpinOnce();
        }
    }

    /// <summary>GPU thread, at a pipeline lookup: optimised pipelines that finished replace their linked ones (the
    /// linked pipeline is destroyed once the GPU is done with the frames that used it).</summary>
    private void SwapOptimizedPipelines()
    {
        int ready = Volatile.Read(ref _optimizedReady);
        if (ready == _optimizedSeen)
        {
            return;
        }

        _optimizedSeen = ready;
        foreach (PipelineKey key in _optimizedPipelines.Keys)
        {
            if (!_optimizedPipelines.TryRemove(key, out Pipeline optimized))
            {
                continue;
            }

            if (!_sharedPipelines.TryGetValue(key, out Pipeline linked) || !_linkedKeys.TryRemove(key, out _))
            {
                _vk.DestroyPipeline(_dev, optimized, null);
                continue;
            }

            _sharedPipelines[key] = optimized;
            if (_pipelines.ContainsKey(key))
            {
                _pipelines[key] = optimized;
            }

            if (_boundPipeline.Handle == linked.Handle)
            {
                _boundPipeline = default;
            }

            Pipeline retired = linked;
            DestroyAfterGpu(() => _vk.DestroyPipeline(_dev, retired, null));
            _statSwapped++;
        }
    }

    // =================================================================== the four libraries

    private static readonly byte* s_mainEntry = (byte*)System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("main");

    private Pipeline CreateLibrary(GraphicsPipelineLibraryFlagsEXT part, ref GraphicsPipelineCreateInfo createInfo, string what)
    {
        var library = new GraphicsPipelineLibraryCreateInfoEXT { SType = StructureType.GraphicsPipelineLibraryCreateInfoExt, PNext = createInfo.PNext, Flags = part };
        createInfo.SType = StructureType.GraphicsPipelineCreateInfo;
        createInfo.PNext = &library;
        createInfo.Flags |= PipelineCreateFlags.CreateLibraryBitKhr;
        Result result = _vk.CreateGraphicsPipelines(_dev, _vkPipelineCache, 1, in createInfo, null, out Pipeline pipeline);
        if (result != Result.Success)
        {
            Log.WarnOnce("gpl-" + what, $"[gpu] {what} pipeline library failed: {result}");
            return default;
        }

        return pipeline;
    }

    private Lazy<Pipeline> VertexInputLibrary(in PipelineKey key, VsEntry vs, PrimitiveTopology topology)
    {
        (ulong LayoutHash, int TopologyClass) libraryKey = (vs.LayoutHash, key.TopologyClass);
        if (_vertexInputLibraries.TryGetValue(libraryKey, out Lazy<Pipeline>? existing))
        {
            return existing;
        }

        VertexInputBindingDescription[] bindings = vs.Bindings;
        VertexInputAttributeDescription[] attributes = vs.Attributes;
        return _vertexInputLibraries.GetOrAdd(libraryKey, new Lazy<Pipeline>(() => BuildVertexInputLibrary(bindings, attributes, topology)));
    }

    private Pipeline BuildVertexInputLibrary(VertexInputBindingDescription[] bindings, VertexInputAttributeDescription[] attributes, PrimitiveTopology topology)
    {
        fixed (VertexInputBindingDescription* bindingPointer = bindings)
        fixed (VertexInputAttributeDescription* attributePointer = attributes)
        {
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)bindings.Length,
                PVertexBindingDescriptions = bindingPointer,
                VertexAttributeDescriptionCount = (uint)attributes.Length,
                PVertexAttributeDescriptions = attributePointer,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = topology };
            DynamicState* dynamicStates = stackalloc DynamicState[16];
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = (uint)DrawDynamicStates(dynamicStates, false),
                PDynamicStates = dynamicStates,
            };
            var createInfo = new GraphicsPipelineCreateInfo { PVertexInputState = &vertexInput, PInputAssemblyState = &inputAssembly, PDynamicState = &dynamicState };
            return CreateLibrary(GraphicsPipelineLibraryFlagsEXT.VertexInputInterfaceBitExt, ref createInfo, "vertex input");
        }
    }

    private Lazy<Pipeline> PreRasterLibrary(ShaderModule vertexModule, PolygonMode polygon, RenderPass pass)
    {
        (ulong Handle, PolygonMode polygon, ulong) libraryKey = (vertexModule.Handle, polygon, pass.Handle);
        if (_preRasterLibraries.TryGetValue(libraryKey, out Lazy<Pipeline>? existing))
        {
            return existing;
        }

        return _preRasterLibraries.GetOrAdd(libraryKey, new Lazy<Pipeline>(() => BuildPreRasterLibrary(vertexModule, polygon, pass)));
    }

    private Pipeline BuildPreRasterLibrary(ShaderModule vertexModule, PolygonMode polygon, RenderPass pass)
    {
        var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertexModule, PName = s_mainEntry };
        var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        // as CreatePipeline: cull mode and front face are dynamic
        var rasterization = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = polygon,
            LineWidth = 1,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            DepthClampEnable = _core.DepthClamp,
        };
        DynamicState* dynamicStates = stackalloc DynamicState[16];
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = (uint)DrawDynamicStates(dynamicStates, false),
            PDynamicStates = dynamicStates,
        };
        var rendering = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo };
        var createInfo = new GraphicsPipelineCreateInfo
        {
            PNext = pass.Handle != 0 ? null : &rendering,
            RenderPass = pass,
            StageCount = 1,
            PStages = &stage,
            PViewportState = &viewport,
            PRasterizationState = &rasterization,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
        };
        return CreateLibrary(GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt, ref createInfo, "pre-rasterisation");
    }

    private Lazy<Pipeline> FragmentLibrary(ShaderModule fragmentModule, SampleCountFlags samples, RenderPass pass)
    {
        (ulong Handle, SampleCountFlags samples, ulong) libraryKey = (fragmentModule.Handle, samples, pass.Handle);
        if (_fragmentLibraries.TryGetValue(libraryKey, out Lazy<Pipeline>? existing))
        {
            return existing;
        }

        return _fragmentLibraries.GetOrAdd(libraryKey, new Lazy<Pipeline>(() => BuildFragmentLibrary(fragmentModule, samples, pass)));
    }

    private Pipeline BuildFragmentLibrary(ShaderModule fragmentModule, SampleCountFlags samples, RenderPass pass)
    {
        var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragmentModule, PName = s_mainEntry };
        var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = samples };
        // as CreatePipeline: the depth/stencil test state is dynamic
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = true,
            DepthWriteEnable = true,
            DepthCompareOp = CompareOp.LessOrEqual,
        };
        DynamicState* dynamicStates = stackalloc DynamicState[16];
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = (uint)DrawDynamicStates(dynamicStates, false),
            PDynamicStates = dynamicStates,
        };
        var rendering = new PipelineRenderingCreateInfo
        {
            SType = StructureType.PipelineRenderingCreateInfo,
            DepthAttachmentFormat = _core.DepthFormat,
            StencilAttachmentFormat = _core.DepthFormat,
        };
        var createInfo = new GraphicsPipelineCreateInfo
        {
            PNext = pass.Handle != 0 ? null : &rendering,
            RenderPass = pass,
            StageCount = 1,
            PStages = &stage,
            PMultisampleState = &multisample,
            PDepthStencilState = &depthStencil,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
        };
        return CreateLibrary(GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt, ref createInfo, "fragment shader");
    }

    private Lazy<Pipeline> OutputLibrary(in PipelineKey key, RenderPass pass)
    {
        var libraryKey = new OutputLibraryKey(key.ColorFormat, key.HasColor, key.TileWord1, key.Blend, key.ColorFunc, key.AlphaFunc, key.ColorSrc, key.ColorDst,
            key.AlphaSrc, key.AlphaDst, key.Mask, key.Samples, pass.Handle);
        if (_outputLibraries.TryGetValue(libraryKey, out Lazy<Pipeline>? existing))
        {
            return existing;
        }

        return _outputLibraries.GetOrAdd(libraryKey, new Lazy<Pipeline>(() => BuildOutputLibrary(libraryKey, pass)));
    }

    private Pipeline BuildOutputLibrary(OutputLibraryKey key, RenderPass pass)
    {
        // the blend and attachment state of CreatePipeline
        PipelineColorBlendAttachmentState* attachments = stackalloc PipelineColorBlendAttachmentState[2];
        attachments[0] = new PipelineColorBlendAttachmentState
        {
            BlendEnable = key.Blend,
            SrcColorBlendFactor = Formats.Factor(key.ColorSrc),
            DstColorBlendFactor = Formats.Factor(key.ColorDst),
            ColorBlendOp = Formats.Op(key.ColorFunc),
            SrcAlphaBlendFactor = Formats.Factor(key.AlphaSrc),
            DstAlphaBlendFactor = Formats.Factor(key.AlphaDst),
            AlphaBlendOp = Formats.Op(key.AlphaFunc),
            ColorWriteMask = Formats.Mask(key.Mask),
        };
        bool tileWord = key.HasColor && key.TileWord1 != 0;
        attachments[1] = new PipelineColorBlendAttachmentState { ColorWriteMask = key.TileWord1 == 2 ? ColorComponentFlags.RBit : 0 };
        uint colorCount = key.HasColor ? tileWord ? 2u : 1u : 0u;
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = colorCount,
            PAttachments = attachments,
            Flags = pass.Handle != 0 && key.HasColor && _core.RasterizationOrderColor ? RoaaBlendFlag : 0,
        };
        var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = key.Samples };
        Format* colorFormats = stackalloc Format[2] { key.ColorFormat, TileWord1Format };
        var rendering = new PipelineRenderingCreateInfo
        {
            SType = StructureType.PipelineRenderingCreateInfo,
            ColorAttachmentCount = colorCount,
            PColorAttachmentFormats = colorFormats,
            DepthAttachmentFormat = _core.DepthFormat,
            StencilAttachmentFormat = _core.DepthFormat,
        };
        DynamicState* dynamicStates = stackalloc DynamicState[16];
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = (uint)DrawDynamicStates(dynamicStates, false),
            PDynamicStates = dynamicStates,
        };
        var createInfo = new GraphicsPipelineCreateInfo
        {
            PNext = pass.Handle != 0 ? null : &rendering,
            RenderPass = pass,
            PColorBlendState = &colorBlend,
            PMultisampleState = &multisample,
            PDynamicState = &dynamicState,
        };
        return CreateLibrary(GraphicsPipelineLibraryFlagsEXT.FragmentOutputInterfaceBitExt, ref createInfo, "fragment output");
    }

    /// <summary>Teardown (workers stopped): the libraries and the optimised pipelines never swapped in. Linked and
    /// optimised pipelines in <see cref="_sharedPipelines"/> are destroyed with the rest.</summary>
    private void DestroyPipelineLibraries()
    {
        foreach (Pipeline pipeline in _optimizedPipelines.Values)
        {
            _vk.DestroyPipeline(_dev, pipeline, null);
        }

        _optimizedPipelines.Clear();
        _optimizeJobs.Clear();
        _linkedKeys.Clear();
        DestroyLibraries(_vertexInputLibraries.Values);
        DestroyLibraries(_preRasterLibraries.Values);
        DestroyLibraries(_fragmentLibraries.Values);
        DestroyLibraries(_outputLibraries.Values);
        _vertexInputLibraries.Clear();
        _preRasterLibraries.Clear();
        _fragmentLibraries.Clear();
        _outputLibraries.Clear();
    }

    private void DestroyLibraries(IEnumerable<Lazy<Pipeline>> libraries)
    {
        foreach (Lazy<Pipeline> library in libraries)
        {
            if (library.IsValueCreated && library.Value.Handle != 0)
            {
                _vk.DestroyPipeline(_dev, library.Value, null);
            }
        }
    }

    /// <summary>"; pipeline library: ..." for the session line (empty when the libraries are not in use).</summary>
    private string PipelineLibraryReport() => !_pipelineLibrary ? "" :
        $"; pipeline library: {_statLinkedOnGpuThread} linked on the GPU thread ({_statLibrariesOnGpuThread} shader libraries compiled or waited for there),"
        + $" {_statLinkedInBackground} linked by the prefetch, {Ms(_statLinkTicks):F0} ms linking, {_statOptimized} optimised in the background,"
        + $" {_statSwapped} swapped in, {_statLinkFailed} link failures; {_preRasterLibraries.Count} vertex and {_fragmentLibraries.Count} fragment shader libraries";
}
