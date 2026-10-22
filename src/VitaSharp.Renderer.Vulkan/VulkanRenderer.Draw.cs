using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    DescriptorSetLayout _uboLayout, _texLayoutV, _texLayoutF, _fbLayout;
    PipelineLayout _pipelineLayout;
    Buffer _dummyBuffer;
    DeviceMemory _dummyBufferMem;
    PipelineCache _vkPipelineCache;

    [StructLayout(LayoutKind.Sequential)]
    struct PushConstants
    {
        public float ViewportFlag, ScreenWidth, ScreenHeight, ZOffset, ZScale, ResMultiplier, DrawId;
        /// <summary>Samples of the framebuffer-fetch copy: above 1 the shaders read the multisampled binding.</summary>
        public float FbSamples;
    }

    void CreateLayouts()
    {
        // 0/1: vertex/fragment uniform data, 2/3: vertex/fragment mapped memory (large buffers read in place)
        var ssbo = stackalloc DescriptorSetLayoutBinding[4];
        ssbo[0] = new DescriptorSetLayoutBinding(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit);
        ssbo[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.FragmentBit);
        ssbo[2] = new DescriptorSetLayoutBinding(2, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit);
        ssbo[3] = new DescriptorSetLayoutBinding(3, DescriptorType.StorageBuffer, 1, ShaderStageFlags.FragmentBit);
        var ci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 4, PBindings = ssbo };
        _vk.CreateDescriptorSetLayout(_dev, in ci, null, out _uboLayout).Ok("set layout");

        _texLayoutV = TextureSetLayout(ShaderStageFlags.VertexBit, 16);
        _texLayoutF = TextureSetLayout(ShaderStageFlags.FragmentBit, 16);
        // binding 0: single-sampled framebuffer copy, binding 1: multisampled copy (real MSAA scenes), binding 2: the
        // multisampled colour attachment as input attachment (real MSAA scenes rendered in render passes)
        var fb = stackalloc DescriptorSetLayoutBinding[3];
        fb[0] = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        fb[1] = new DescriptorSetLayoutBinding(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        fb[2] = new DescriptorSetLayoutBinding(2, DescriptorType.InputAttachment, 1, ShaderStageFlags.FragmentBit);
        var fbci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 3, PBindings = fb };
        _vk.CreateDescriptorSetLayout(_dev, in fbci, null, out _fbLayout).Ok("set layout");

        var layouts = stackalloc DescriptorSetLayout[4] { _uboLayout, _texLayoutV, _texLayoutF, _fbLayout };
        var pcr = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants));
        var pli = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 4,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pcr,
        };
        _vk.CreatePipelineLayout(_dev, in pli, null, out _pipelineLayout).Ok("pipeline layout");

        var pc = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
        _vk.CreatePipelineCache(_dev, in pc, null, out _vkPipelineCache);
    }

    DescriptorSetLayout TextureSetLayout(ShaderStageFlags stage, int count)
    {
        var b = stackalloc DescriptorSetLayoutBinding[count];
        for (int i = 0; i < count; i++) b[i] = new DescriptorSetLayoutBinding((uint)i, DescriptorType.CombinedImageSampler, 1, stage);
        var ci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)count, PBindings = b };
        _vk.CreateDescriptorSetLayout(_dev, in ci, null, out var l).Ok("set layout");
        return l;
    }

    void CreateDummies()
    {
        var (b, m, p) = _core.CreateHostBuffer(4096, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit);
        new Span<byte>((void*)p, 4096).Clear();
        _dummyBuffer = b;
        _dummyBufferMem = m;
        CreateDummyTexture();
    }

    // =================================================================== descriptor allocation

    DescriptorSet AllocSet(DescriptorSetLayout layout)
    {
        var f = F;
        var sizes = stackalloc DescriptorPoolSize[3]; // outside the loop (CA2014)
        while (true)
        {
            if (f.DescPoolIndex >= f.DescPools.Count)
            {
                sizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 4 * 8192);
                sizes[1] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 65536);
                sizes[2] = new DescriptorPoolSize(DescriptorType.InputAttachment, 8192);
                var pci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 8192, PoolSizeCount = 3, PPoolSizes = sizes };
                _vk.CreateDescriptorPool(_dev, in pci, null, out var pool).Ok("descriptor pool");
                f.DescPools.Add(pool);
            }
            var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = f.DescPools[f.DescPoolIndex], DescriptorSetCount = 1, PSetLayouts = &layout };
            var r = _vk.AllocateDescriptorSets(_dev, in ai, out var set);
            if (r == Result.Success) return set;
            f.DescPoolIndex++;
        }
    }

    // =================================================================== shaders

    sealed class VsEntry
    {
        public ShaderModule Module;
        public GeneratedShader? Gen;
        public VertexInputBindingDescription[] Bindings = [];
        public VertexInputAttributeDescription[] Attributes = [];
        public ulong LayoutHash;
        public uint StreamMask;
        public bool Failed;
        public ulong ProgramHash, HintKey;
    }

    sealed class FsEntry
    {
        public ShaderModule Module;
        public GeneratedShader? Gen;
        public bool Failed;
        public ulong ProgramHash, HintKey;
    }

    /// <summary>A translated shader: its module (default when translation failed) and the metadata the renderer needs.</summary>
    sealed record ModuleEntry(ShaderModule Module, GeneratedShader? Gen);

    /// <summary>(program hash, hint key) → module. Shared with the prewarm thread, which loads modules from the
    /// per-title shader pack ahead of the first draws.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, ulong), ModuleEntry> _modules = new();
    readonly Dictionary<(GxmVertexProgram, ulong), VsEntry> _vsCache = new();
    readonly Dictionary<(GxmFragmentProgram, ulong), FsEntry> _fsCache = new();

    /// <summary>Same value as <see cref="ShaderHints.Key"/> for the hints a draw would produce, without allocating them.</summary>
    static ulong HintKey(GxmTextureDesc[] tex, uint used, GxmVertexAttribute[]? attrs, uint colorFormat, bool fetchInput = false)
    {
        ulong h = ShaderHints.Fnv.Offset;
        for (int i = 0; i < 16; i++) h = ShaderHints.Fnv.Mix(h, (used & (1u << i)) != 0 ? tex[i].Format : 0x0C000000);
        if (attrs != null)
            foreach (var a in attrs) { h = ShaderHints.Fnv.Mix(h, (uint)a.RegIndex); h = ShaderHints.Fnv.Mix(h, (uint)(int)a.ComponentCount); h = ShaderHints.Fnv.Mix(h, a.Format); }
        h = ShaderHints.Fnv.Mix(h, colorFormat);
        return fetchInput ? ShaderHints.Fnv.Mix(h, ShaderHints.FetchInputKey) : h;
    }

    static uint[] TextureFormats(GxmTextureDesc[] tex, uint used)
    {
        var f = new uint[16];
        for (int i = 0; i < 16; i++) f[i] = (used & (1u << i)) != 0 ? tex[i].Format : 0x0C000000;
        return f;
    }

    /// <summary>
    /// Module for a program + hints: from memory (possibly loaded by the prewarm thread), from the per-title shader
    /// pack (no translation, no compilation) or translated and compiled now (and then stored in the pack).
    /// </summary>
    ModuleEntry GetModule(GxpProgram program, ulong hintKey, Func<DrawCommand, uint, ShaderHints> hints, DrawCommand d, uint colorFormat)
    {
        var key = (program.Hash, hintKey);
        if (_modules.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _statShaderMemHits);
            return cached;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // queued by Prefetch on the submitting thread: wait for it (or run it here if no worker picked it up yet). Checked
        // before the pack: a running job may already have stored its SPIR-V there, and loading it again here would
        // create a second module for nothing
        if (_inflight.TryGetValue(key, out var job))
        {
            var r = job.RunOrWait(this);
            FrameLedger.AddSince(FrameLedger.Cat.ShaderWait, t0);
            NoteShaderTime(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref _statAsyncWaited);
            return r;
        }
        // a job publishes its module before it leaves the in-flight table: one may have finished since the first lookup
        if (_modules.TryGetValue(key, out cached)) return cached;
        if (LoadPackedShader(program.Hash, hintKey, program.IsVertex) is { } packed)
        {
            Interlocked.Increment(ref _statShaderPackHits);
            NoteShaderTime(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            FrameLedger.AddSince(FrameLedger.Cat.ShaderPackLoad, t0);
            return PublishModule(key, packed);
        }
        Interlocked.Increment(ref _statShaderMisses);
        var entry = TranslateAndCompile(program, hintKey, hints(d, colorFormat), background: false);
        NoteShaderTime(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        return PublishModule(key, entry);
    }

    /// <summary>GXP → GLSL → SPIR-V → module, stored in the shader pack (thread-safe: also run by the shader workers).</summary>
    ModuleEntry TranslateAndCompile(GxpProgram program, ulong hintKey, ShaderHints hints, bool background)
    {
        GeneratedShader? gen = null;
        ShaderModule m = default;
        try
        {
            long tg = System.Diagnostics.Stopwatch.GetTimestamp();
            gen = ShaderGenerator.Generate(program, hints);
            long tc = System.Diagnostics.Stopwatch.GetTimestamp();
            FrameLedger.Add(background ? FrameLedger.Cat.BgShader : FrameLedger.Cat.ShaderTranslate, tc - tg);
            var spv = _compiler.Compile(gen.Glsl, gen.IsVertex, out var err);
            FrameLedger.AddSince(background ? FrameLedger.Cat.BgShader : FrameLedger.Cat.ShaderCompile, tc);
            if (spv == null)
            {
                Log.Error($"Shader {program.Hash:X16} failed to compile: {err}");
                DumpShader(program, gen.Glsl + "\n/*\n" + err + "\n*/");
            }
            else
            {
                m = CreateShaderModule(spv);
                StorePackedShader(program.Hash, hintKey, gen, spv);
            }
            if (gen.Warnings.Count > 0) Log.WarnOnce($"shwarn{program.Hash:X16}", $"[gpu] shader {program.Hash:X16} ({(program.IsVertex ? "vertex" : "fragment")}): {string.Join("; ", gen.Warnings.Distinct())}");
        }
        catch (Exception e)
        {
            Log.Error($"Shader {program.Hash:X16} translation failed: {e}");
            if (m.Handle != 0) _vk.DestroyShaderModule(_dev, m, null);
            m = default;
        }
        return new ModuleEntry(m, gen);
    }

    // hints are only built when a shader really has to be translated (static: no per-draw closure allocation)
    static ShaderHints VertexHints(DrawCommand d, uint _) => new(TextureFormats(d.VertexTextures, d.VertexProgram.Info.TexturesUsed),
        d.VertexProgram.Attributes.Select(a => ((uint)a.RegIndex, (int)a.ComponentCount, a.Format)).ToArray(), 0);

    static ShaderHints FragmentHints(DrawCommand d, uint colorFormat) =>
        new(TextureFormats(d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed), [], colorFormat);

    static ShaderHints FragmentHintsInputFetch(DrawCommand d, uint colorFormat) =>
        new(TextureFormats(d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed), [], colorFormat, FetchInputAttachment: true);

    ShaderModule CreateShaderModule(ReadOnlySpan<byte> spv)
    {
        fixed (byte* p = spv)
        {
            var ci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spv.Length, PCode = (uint*)p };
            _vk.CreateShaderModule(_dev, in ci, null, out var m).Ok("shader module");
            return m;
        }
    }

    /// <summary>Adds a module unless another thread got there first (then that one is used and ours destroyed).</summary>
    ModuleEntry PublishModule((ulong, ulong) key, ModuleEntry e)
    {
        if (_modules.TryAdd(key, e)) return e;
        if (e.Module.Handle != 0) _vk.DestroyShaderModule(_dev, e.Module, null);
        return _modules[key];
    }

    void DumpShader(GxpProgram program, string text)
    {
        if (_opt.ShaderCacheDir == null) return;
        try
        {
            var dir = Path.Combine(_opt.ShaderCacheDir, "failed");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{program.Hash:x16}.{(program.IsVertex ? "vert" : "frag")}"), text);
            File.WriteAllBytes(Path.Combine(dir, $"{program.Hash:x16}.gxp"), program.Data);
        }
        catch { }
    }

    VsEntry GetVertexShader(DrawCommand d)
    {
        var vp = d.VertexProgram;
        // the cache key is computed without building the hints (thousands of draws per frame hit the cache)
        ulong hk = HintKey(d.VertexTextures, vp.Info.TexturesUsed, vp.Attributes, 0);
        if (_vsCache.TryGetValue((vp, hk), out var e)) return e;
        var me = GetModule(vp.Info.Program, hk, VertexHints, d, 0);
        e = new VsEntry { Module = me.Module, Gen = me.Gen, Failed = me.Module.Handle == 0 || me.Gen == null, ProgramHash = vp.Info.Program.Hash, HintKey = hk };
        if (me.Gen != null) BuildVertexInput(vp, me.Gen, e);
        _vsCache[(vp, hk)] = e;
        _fNewShaders++;
        if (DebugMode.Enabled) DbgVertexShader(e, d);
        return e;
    }

    /// <param name="fetchInput">the variant whose programmable blending reads the input attachment</param>
    FsEntry GetFragmentShader(DrawCommand d, bool fetchInput = false)
    {
        var fp = d.FragmentProgram;
        uint colorFormat = _rt?.ColorFormat ?? 0;
        ulong hk = HintKey(d.FragmentTextures, fp.Info.TexturesUsed, null, colorFormat, fetchInput);
        if (_fsCache.TryGetValue((fp, hk), out var e)) return e;
        var me = GetModule(fp.Info.Program, hk, fetchInput ? FragmentHintsInputFetch : FragmentHints, d, colorFormat);
        e = new FsEntry { Module = me.Module, Gen = me.Gen, Failed = me.Module.Handle == 0 || me.Gen == null, ProgramHash = fp.Info.Program.Hash, HintKey = hk };
        _fsCache[(fp, hk)] = e;
        _fNewShaders++;
        if (DebugMode.Enabled) DbgFragmentShader(e, d, fetchInput);
        return e;
    }

    static Format RawAttributeFormat(int size, int count) => (size, count) switch
    {
        (1, 1) => Format.R8Uint, (1, 2) => Format.R8G8Uint, (1, 3) => Format.R8G8B8Uint, (1, _) => Format.R8G8B8A8Uint,
        (2, 1) => Format.R16Uint, (2, 2) => Format.R16G16Uint, (2, 3) => Format.R16G16B16Uint, (2, _) => Format.R16G16B16A16Uint,
        (_, 1) => Format.R32Uint, (_, 2) => Format.R32G32Uint, (_, 3) => Format.R32G32B32Uint, _ => Format.R32G32B32A32Uint,
    };

    void BuildVertexInput(GxmVertexProgram vp, GeneratedShader gen, VsEntry e)
    {
        var bindings = new List<VertexInputBindingDescription>();
        var attrs = new List<VertexInputAttributeDescription>();
        uint streamMask = 0;
        bool needDummy = false;
        foreach (var input in gen.VertexInputs)
        {
            var a = vp.Attributes.FirstOrDefault(x => x.RegIndex == input.RegIndex);
            bool found = vp.Attributes.Any(x => x.RegIndex == input.RegIndex);
            for (int l = 0; l < input.LocationCount; l++)
            {
                if (!found || a.StreamIndex >= vp.Streams.Length)
                {
                    needDummy = true;
                    var fmt = input.Kind switch { 'i' => Format.R32G32B32A32Sint, 'u' => Format.R32G32B32A32Uint, _ => Format.R32G32B32A32Sfloat };
                    attrs.Add(new VertexInputAttributeDescription((uint)(input.Location + l), 16, fmt, 0));
                    continue;
                }
                if (input.RawSize > 0)
                {
                    // register-format attribute: raw unsigned integers of the parameter's size, stream format ignored
                    int rc = Math.Min(4, Math.Max(input.RawComponents - l * 4, 1));
                    var rf = RawAttributeFormat(input.RawSize, rc);
                    if (!_core.SupportsVertexFormat(rf) && rc == 3) rf = RawAttributeFormat(input.RawSize, 4);
                    attrs.Add(new VertexInputAttributeDescription((uint)(input.Location + l), a.StreamIndex, rf, (uint)(a.Offset + l * 4 * input.RawSize)));
                    streamMask |= 1u << a.StreamIndex;
                    continue;
                }
                int remaining = Math.Max(a.ComponentCount - l * 4, 1);
                int count = Math.Min(4, remaining);
                var (format, size) = Formats.Attribute(a.Format, count, input.Kind);
                if (!_core.SupportsVertexFormat(format) && count == 3) (format, _) = Formats.Attribute(a.Format, 4, input.Kind);
                int compSize = size / count;
                attrs.Add(new VertexInputAttributeDescription((uint)(input.Location + l), a.StreamIndex, format, (uint)(a.Offset + l * 4 * compSize)));
                streamMask |= 1u << a.StreamIndex;
            }
        }
        for (int s = 0; s < 16; s++)
        {
            if ((streamMask & (1u << s)) == 0) continue;
            var st = vp.Streams[s];
            bool instanced = st.IndexSource is 2 or 3;
            bindings.Add(new VertexInputBindingDescription((uint)s, st.Stride, instanced ? VertexInputRate.Instance : VertexInputRate.Vertex));
        }
        if (needDummy) bindings.Add(new VertexInputBindingDescription(16, 0, VertexInputRate.Vertex));
        e.Bindings = bindings.ToArray();
        e.Attributes = attrs.ToArray();
        e.LayoutHash = LayoutHash(e.Bindings, e.Attributes);
        e.StreamMask = streamMask;
    }

    /// <summary>Identity of a vertex input layout (part of the pipeline key; the prewarm thread computes it from
    /// the recorded descriptions, so both must hash the same way).</summary>
    static ulong LayoutHash(VertexInputBindingDescription[] bindings, VertexInputAttributeDescription[] attrs)
    {
        ulong h = System.IO.Hashing.XxHash64.HashToUInt64(MemoryMarshal.AsBytes(bindings.AsSpan()));
        return System.IO.Hashing.XxHash64.HashToUInt64(MemoryMarshal.AsBytes(attrs.AsSpan()), (long)h);
    }

    // =================================================================== pipelines

    readonly record struct PipelineKey(ulong Vs, ulong Fs, ulong VertexLayout, Format ColorFormat, bool HasColor,
        bool Blend, byte ColorFunc, byte AlphaFunc, byte ColorSrc, byte ColorDst, byte AlphaSrc, byte AlphaDst, byte Mask,
        int TopologyClass, PolygonMode Polygon, SampleCountFlags Samples);

    /// <summary>GPU-thread view of the pipelines (lock-free lookups on the draw path).</summary>
    readonly Dictionary<PipelineKey, Pipeline> _pipelines = new();
    /// <summary>Every pipeline created (owner for destruction), shared with the prewarm thread.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<PipelineKey, Pipeline> _sharedPipelines = new();
    Pipeline _boundPipeline;

    static int TopologyClass(PrimitiveTopology t) => t switch
    {
        PrimitiveTopology.PointList => 0,
        PrimitiveTopology.LineList or PrimitiveTopology.LineStrip => 1,
        _ => 2,
    };

    Pipeline GetPipeline(in PipelineKey key, VsEntry vs, FsEntry fs, PrimitiveTopology topology)
    {
        if (_pipelines.TryGetValue(key, out var p)) return p;
        _fNewPipelines++;
        if (DebugMode.Enabled) DbgPipeline(key, topology);
        if (_sharedPipelines.TryGetValue(key, out p))
        {
            // compiled ahead of time by the prewarm thread
            _statPrewarmUsed++;
            _pipelines[key] = p;
            return p;
        }
        if (WaitPrefetchedPipeline(key, out p) || _sharedPipelines.TryGetValue(key, out p)) // (a worker may finish between the lookups)
        {
            _pipelines[key] = p;
            return p;
        }
        // claim the key so a prefetch worker does not start the same pipeline while this thread builds it
        ManualResetEventSlim? claim = null;
        if (_asyncPipelines)
        {
            claim = new ManualResetEventSlim();
            if (!_pipelineInflight.TryAdd(key, claim))
            {
                claim = null;
                if (WaitPrefetchedPipeline(key, out p)) { _pipelines[key] = p; return p; }
            }
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { p = CreatePipeline(key, vs.Module, fs.Module, vs.Bindings, vs.Attributes, topology); }
        catch
        {
            if (claim != null) { _pipelineInflight.TryRemove(key, out _); claim.Set(); }
            throw;
        }
        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _statGpuPipelines++;
        FrameLedger.Add(FrameLedger.Cat.PipelineCreate, dt);
        _statGpuPipelineTicks += dt;
        _frameStallTicks += dt;
        if (dt > _statGpuPipelineMaxTicks) _statGpuPipelineMaxTicks = dt;
        if (p.Handle != 0)
        {
            if (_sharedPipelines.TryAdd(key, p))
            {
                RecordPipeline(key, vs, fs, topology);
                NotePipelineCreated();
            }
            else
            {
                // the prewarm thread finished the same pipeline meanwhile
                _vk.DestroyPipeline(_dev, p, null);
                p = _sharedPipelines[key];
            }
        }
        if (claim != null) { _pipelineInflight.TryRemove(key, out _); claim.Set(); } // after publishing
        _pipelines[key] = p;
        return p;
    }

    /// <summary>Builds a graphics pipeline (thread-safe: also used by the prewarm thread).</summary>
    Pipeline CreatePipeline(in PipelineKey key, ShaderModule vsModule, ShaderModule fsModule,
        VertexInputBindingDescription[] bindings, VertexInputAttributeDescription[] attributes, PrimitiveTopology topology)
    {
        Pipeline p;
        var entry = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vsModule, PName = entry };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fsModule, PName = entry };

        fixed (VertexInputBindingDescription* pb = bindings)
        fixed (VertexInputAttributeDescription* pa = attributes)
        {
            var vi = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)bindings.Length,
                PVertexBindingDescriptions = pb,
                VertexAttributeDescriptionCount = (uint)attributes.Length,
                PVertexAttributeDescriptions = pa,
            };
            var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = topology };
            var vpState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var rs = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = _core.FillModeNonSolid ? key.Polygon : PolygonMode.Fill,
                LineWidth = 1,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                // no near-plane clipping, depth clamped per fragment (the vertex shader clips the far plane itself)
                DepthClampEnable = _core.DepthClamp,
            };
            var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = key.Samples };
            var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.LessOrEqual };
            var att = new PipelineColorBlendAttachmentState
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
            var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = key.HasColor ? 1u : 0u, PAttachments = &att };
            var dyn = stackalloc DynamicState[]
            {
                DynamicState.Viewport, DynamicState.Scissor, DynamicState.LineWidth, DynamicState.DepthBias,
                DynamicState.StencilCompareMask, DynamicState.StencilWriteMask, DynamicState.StencilReference,
                DynamicState.CullMode, DynamicState.FrontFace, DynamicState.PrimitiveTopology,
                DynamicState.DepthTestEnable, DynamicState.DepthWriteEnable, DynamicState.DepthCompareOp,
                DynamicState.StencilTestEnable, DynamicState.StencilOp, DynamicState.DepthBiasEnable,
            };
            var dsi = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 16, PDynamicStates = dyn };
            var colorFormat = key.ColorFormat;
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = key.HasColor ? 1u : 0u,
                PColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = _core.DepthFormat,
                StencilAttachmentFormat = _core.DepthFormat,
            };
            // multisampled scenes render in render passes (input attachments for programmable blending)
            bool msPass = key.Samples != SampleCountFlags.Count1Bit;
            var gci = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = msPass ? null : &rendering,
                RenderPass = msPass ? MsaaCompatPass(colorFormat, key.Samples) : default,
                Subpass = 0,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vi,
                PInputAssemblyState = &ia,
                PViewportState = &vpState,
                PRasterizationState = &rs,
                PMultisampleState = &ms,
                PDepthStencilState = &ds,
                PColorBlendState = &cb,
                PDynamicState = &dsi,
                Layout = _pipelineLayout,
            };
            var r = _vk.CreateGraphicsPipelines(_dev, _vkPipelineCache, 1, in gci, null, out p);
            if (r != Result.Success)
            {
                if (r == Result.ErrorDeviceLost)
                {
                    _deviceLost = true; // stops the background shader/pipeline work
                    Log.WarnOnce("pipeline-device-lost", "vkCreateGraphicsPipelines failed: device lost");
                    ReportDeviceLost("vkCreateGraphicsPipelines");
                }
                else Log.Error($"vkCreateGraphicsPipelines failed: {r}");
                p = default;
            }
        }
        return p;
    }

    void DestroyPipelines()
    {
        foreach (var p in _sharedPipelines.Values) if (p.Handle != 0) _vk.DestroyPipeline(_dev, p, null);
        foreach (var m in _modules.Values) if (m.Module.Handle != 0) _vk.DestroyShaderModule(_dev, m.Module, null);
        _vk.DestroyPipelineCache(_dev, _vkPipelineCache, null);
        _vk.DestroyPipelineLayout(_dev, _pipelineLayout, null);
        _vk.DestroyDescriptorSetLayout(_dev, _uboLayout, null);
        _vk.DestroyDescriptorSetLayout(_dev, _texLayoutV, null);
        _vk.DestroyDescriptorSetLayout(_dev, _texLayoutF, null);
        _vk.DestroyDescriptorSetLayout(_dev, _fbLayout, null);
        _core.DestroyBuffer(_dummyBuffer, _dummyBufferMem);
    }

    // =================================================================== draw

    static uint MaxIndex(uint addr, uint count, bool i32)
    {
        uint max = 0;
        if (i32)
        {
            var s = GuestMemory.ReadSpan<uint>(addr, (int)count);
            foreach (var v in s) if (v > max) max = v;
        }
        else
        {
            var s = GuestMemory.ReadSpan<ushort>(addr, (int)count);
            foreach (var v in s) if (v > max) max = v;
        }
        return max;
    }

    /// <summary>Draw number within the presented frame (debug: VITASHARP_DEBUG_TEX0=id paints it, the draw trace logs it).</summary>
    int _drawSerial;

    readonly HashSet<(uint, Format)> _rawLogged = new();
    readonly Dictionary<Format, bool> _rawFormatOk = new();

    void Draw(DrawCommand d)
    {
        _drawSerial++;
        if (!_inScene || d.IndexCount == 0) return;
        if (d.FragmentProgram.IsMaskUpdate || d.FragmentProgram.Info == null) return;
        var st = d.State;
        if (st.RegionClipMode == 1) return; // clip everything
        BeginFrameIfNeeded();

        var vs = GetVertexShader(d);
        var fs = GetFragmentShader(d);
        TraceSnapshot(d);
        if (_traceFrame == _presentCount || (_rt != null && TraceRtAddrs.Contains(_rt.Address))) TraceDraw(d, vs, fs);
        if (vs.Failed || fs.Failed) return;

        // ---- textures (may need to leave the render pass)
        SetDrawArea(in st);
        var vSet = BuildTextureSet(_texLayoutV, d.VertexTextures, d.VertexProgram.Info.TexturesUsed, d.VertexProgram.Info.CubeSamplers);
        var fSet = BuildTextureSet(_texLayoutF, d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed, d.FragmentProgram.Info.CubeSamplers);
        // programmable blending in a multisampled render pass reads the attachment itself (input attachment);
        // raw integer outputs (integer attachment view) and VITASHARP_MSAA_FETCH=copy use a copy of the samples
        bool inputFetch = fs.Gen!.UsesFragColor && _ms && MsaaInputFetch && fs.Gen.RawColorBits == 0;
        if (inputFetch)
        {
            fs = GetFragmentShader(d, fetchInput: true);
            if (fs.Failed) return;
        }
        var fbSet = inputFetch ? BuildMsaaInputFetchSet() : fs.Gen!.UsesFragColor ? BuildFramebufferFetchSet() : _emptyFbSet ??= BuildEmptyFbSet();

        // ---- pipeline
        var topology = Formats.Topology(d.PrimitiveType);
        bool hasColor = _rt != null;
        var blend = d.FragmentProgram.Blend;
        bool fragEnabled = st.Front.FragmentEnabled;
        byte mask = !fragEnabled ? (byte)0 : blend?.ColorMask ?? 0xF;
        // a fragment program that reads the current framebuffer colour carries its blend in the shader itself (the
        // shader patcher compiles the blend info into USSE code): the fixed-function blend must not be applied on top
        bool blendOn = blend is { } bi && !fs.Gen!.UsesFragColor && !(bi.ColorFunc == 0 && bi.AlphaFunc == 0) && !(bi.ColorSrc == 1 && bi.ColorDst == 0 && bi.AlphaSrc == 1 && bi.AlphaDst == 0);
        // raw register bits go through an integer view of the surface (exact bits; integer attachments do not blend)
        var attachFormat = _rtAttachFormat;
        bool raw = hasColor && fs.Gen!.RawColorBits != 0;
        if (raw)
        {
            attachFormat = RawAttachmentFormat(_rt!.Format, fs.Gen!.RawColorBits);
            if (attachFormat == Format.Undefined)
            {
                Log.WarnOnce($"raw-out-{_rt.Format}-{fs.Gen.RawColorBits}", $"[gpu] raw {fs.Gen.RawColorBits}-bit fragment output on render target 0x{_rt.Address:X8} fmt 0x{_rt.ColorFormat:X8} ({_rt.Format}) has no integer view: draw skipped");
                return;
            }
            if (!_rawFormatOk.TryGetValue(attachFormat, out bool ok)) _rawFormatOk[attachFormat] = ok = _core.SupportsUnblendedColorAttachment(attachFormat);
            if (ok && _ms && (_core.ImageSampleCounts(attachFormat, ImageUsageFlags.ColorAttachmentBit) & _msSamples) == 0)
            {
                Log.WarnOnce($"raw-ms-{attachFormat}-{_msSamples}", $"[gpu] {attachFormat} cannot be multisampled ({_msSamples}) on this GPU: raw fragment outputs in MSAA scenes skipped");
                return;
            }
            if (!ok)
            {
                Log.WarnOnce($"raw-att-{attachFormat}", $"[gpu] {attachFormat} is not a colour attachment format on this GPU: raw fragment outputs skipped");
                return;
            }
            blendOn = false;
            if (_rawLogged.Add((_rt.Address, attachFormat)))
                Log.Info($"[gpu] render target 0x{_rt.Address:X8} {_rt.Width}x{_rt.Height} fmt 0x{_rt.ColorFormat:X8}: raw {fs.Gen.RawColorBits}-bit fragment outputs written through a {attachFormat} view");
        }
        var key = new PipelineKey(vs.Module.Handle, fs.Module.Handle, vs.LayoutHash, attachFormat, hasColor,
            blendOn, blend?.ColorFunc ?? 0, blend?.AlphaFunc ?? 0, blend?.ColorSrc ?? 1, blend?.ColorDst ?? 0, blend?.AlphaSrc ?? 1, blend?.AlphaDst ?? 0,
            mask, TopologyClass(topology), Formats.Polygon(st.Front.PolygonMode), SceneSamples);
        var pipeline = GetPipeline(key, vs, fs, topology);
        if (pipeline.Handle == 0) return;

        // ---- geometry upload
        int indexSize = d.Index32 ? 4 : 2;
        uint maxIndex = MaxIndex(d.IndexAddress, d.IndexCount, d.Index32);
        var (ib, ibOff) = UploadSpan(GuestMemory.ReadSpan(d.IndexAddress, (int)(d.IndexCount * indexSize)), 4);
        var vbufs = stackalloc Buffer[17];
        var voffs = stackalloc ulong[17];
        var vp = d.VertexProgram;
        for (int s = 0; s < 16; s++)
        {
            if ((vs.StreamMask & (1u << s)) == 0) continue;
            var stream = vp.Streams[s];
            bool instanced = stream.IndexSource is 2 or 3;
            uint end = 0;
            foreach (var a in vp.Attributes)
            {
                if (a.StreamIndex != s) continue;
                var (_, size) = Formats.Attribute(a.Format, Math.Min((int)a.ComponentCount, 4), 'f');
                uint attrBytes = (uint)(size * Math.Max(1, (a.ComponentCount + 3) / 4));
                end = Math.Max(end, a.Offset + attrBytes);
            }
            uint count = instanced ? d.InstanceCount : maxIndex + 1;
            uint length = (count - 1) * stream.Stride + end + 16;
            uint addr = d.Streams[s];
            if (addr == 0) { vbufs[s] = _dummyBuffer; voffs[s] = 0; continue; }
            var (b, off) = UploadSpan(GuestMemory.ReadSpan(addr, (int)length), 4);
            vbufs[s] = b;
            voffs[s] = off;
        }

        // ---- uniforms
        var bufInfos = stackalloc DescriptorBufferInfo[4];
        bufInfos[2] = ResolveMappedSlots(d.VertexProgram.Info.UniformSlots, d.VertexUniformSpan, d.VertexUniformAddrs);
        bufInfos[3] = ResolveMappedSlots(d.FragmentProgram.Info.UniformSlots, d.FragmentUniformSpan, d.FragmentUniformAddrs);
        var uSet = AllocSet(_uboLayout);
        bufInfos[0] = UniformInfo(d.VertexUniformSpan);
        bufInfos[1] = UniformInfo(d.FragmentUniformSpan);
        var writes = stackalloc WriteDescriptorSet[4];
        for (int i = 0; i < 4; i++)
            writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = uSet, DstBinding = (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufInfos[i] };
        _vk.UpdateDescriptorSets(_dev, 4, writes, 0, null);

        // ---- record
        if (_rendering && (_renderRaw != raw || (raw && _rawFormat != attachFormat))) EndRendering(resolve: false);
        _renderRaw = raw;
        if (raw) _rawFormat = attachFormat;
        BeginRendering();
        var cmd = Cmd;
        if (inputFetch) InputFetchBarrier(cmd);
        if (_boundPipeline.Handle != pipeline.Handle)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            _boundPipeline = pipeline;
        }
        var sets = stackalloc DescriptorSet[4] { uSet, vSet, fSet, fbSet };
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 4, sets, 0, null);
        for (int s = 0; s < 16; s++)
            if ((vs.StreamMask & (1u << s)) != 0) _vk.CmdBindVertexBuffers(cmd, (uint)s, 1, &vbufs[s], &voffs[s]);
        if (vs.Bindings.Any(b => b.Binding == 16))
        {
            var db = _dummyBuffer; ulong dz = 0;
            _vk.CmdBindVertexBuffers(cmd, 16, 1, &db, &dz);
        }
        _vk.CmdBindIndexBuffer(cmd, ib, ibOff, d.Index32 ? IndexType.Uint32 : IndexType.Uint16);
        SetDynamicState(cmd, st, topology);
        bool visQuery = BeginVisibilityQuery(d, out uint query);
        _vk.CmdDrawIndexed(cmd, d.IndexCount, d.InstanceCount, 0, 0, 0);
        if (visQuery) EndVisibilityQuery(query);
        _drawsSinceSubmit++;
        if (_rt != null) { _rt.Dirty = true; _rt.Version++; _rt.LastUsedFrame = FrameCount; }
    }

    DescriptorBufferInfo UniformInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return new DescriptorBufferInfo(_dummyBuffer, 0, 256);
        ulong align = Math.Max(_core.Properties.Limits.MinStorageBufferOffsetAlignment, 16);
        var (b, off) = UploadSpan(data, align);
        return new DescriptorBufferInfo(b, off, (ulong)data.Length);
    }

    void SetDynamicState(CommandBuffer cmd, in DrawState st, PrimitiveTopology topology)
    {
        float w = _sceneW, h = _sceneH;
        Viewport vpRect;
        var pc = new PushConstants { ScreenWidth = w, ScreenHeight = h, ResMultiplier = SceneScale, ZOffset = 0, ZScale = 1, DrawId = _drawSerial,
            FbSamples = _ms ? (_msSamples == SampleCountFlags.Count4Bit ? 4 : 2) : 1 };
        if (st.ViewportEnabled)
        {
            float vw = Math.Abs(2 * st.VpScaleX), vh = 2 * st.VpScaleY;
            float x = st.VpOffX - Math.Abs(st.VpScaleX), y = st.VpOffY - st.VpScaleY;
            vpRect = new Viewport(x, y, vw, vh, 0, 1);
            pc.ViewportFlag = 1;
            pc.ZOffset = st.VpOffZ;
            pc.ZScale = st.VpScaleZ;
        }
        else vpRect = new Viewport(0, 0, w, h, 0, 1);
        if (vpRect.Height == 0) vpRect.Height = 1;
        if (vpRect.Width == 0) vpRect.Width = 1;
        if (SceneScale == 2) { vpRect.X *= 2; vpRect.Y *= 2; vpRect.Width *= 2; vpRect.Height *= 2; }
        _vk.CmdSetViewport(cmd, 0, 1, in vpRect);

        Rect2D scissor;
        if (st.RegionClipMode == 2)
        {
            int x0 = Math.Clamp(st.ClipMinX, 0, (int)w), y0 = Math.Clamp(st.ClipMinY, 0, (int)h);
            int x1 = Math.Clamp(st.ClipMaxX + 1, x0, (int)w), y1 = Math.Clamp(st.ClipMaxY + 1, y0, (int)h);
            scissor = new Rect2D(new Offset2D(x0, y0), new Extent2D((uint)(x1 - x0), (uint)(y1 - y0)));
        }
        else scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h));
        if (SceneScale == 2) scissor = new Rect2D(new Offset2D(scissor.Offset.X * 2, scissor.Offset.Y * 2), new Extent2D(scissor.Extent.Width * 2, scissor.Extent.Height * 2));
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);
        _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &pc);

        _vk.CmdSetPrimitiveTopology(cmd, topology);
        _vk.CmdSetCullMode(cmd, st.CullMode switch { 1 => CullModeFlags.BackBit, 2 => CullModeFlags.FrontBit, _ => CullModeFlags.None });
        _vk.CmdSetFrontFace(cmd, FrontFace.CounterClockwise);
        _vk.CmdSetLineWidth(cmd, _core.WideLines ? Math.Max(1, (int)st.Front.PointLineWidth) * SceneScale : 1);

        var front = st.Front;
        var back = st.TwoSided ? st.Back : st.Front;
        bool depthTest = !(front.DepthFunc == 7 && !front.DepthWrite);
        _vk.CmdSetDepthTestEnable(cmd, depthTest);
        _vk.CmdSetDepthWriteEnable(cmd, front.DepthWrite);
        _vk.CmdSetDepthCompareOp(cmd, (CompareOp)front.DepthFunc);

        bool stencil = !(IsTrivial(front.Stencil) && IsTrivial(back.Stencil));
        _vk.CmdSetStencilTestEnable(cmd, stencil);
        if (stencil)
        {
            SetStencil(cmd, StencilFaceFlags.FaceFrontBit, front.Stencil);
            SetStencil(cmd, StencilFaceFlags.FaceBackBit, back.Stencil);
        }
        bool bias = front.DepthBiasFactor != 0 || front.DepthBiasUnits != 0;
        _vk.CmdSetDepthBiasEnable(cmd, bias);
        if (bias) _vk.CmdSetDepthBias(cmd, front.DepthBiasUnits, 0, front.DepthBiasFactor);
    }

    void TraceDraw(DrawCommand d, VsEntry vs, FsEntry fs)
    {
        var st = d.State;
        var b = d.FragmentProgram.Blend;
        var sb = new System.Text.StringBuilder();
        sb.Append($"[draw] id={_drawSerial} f={_presentCount} rt=0x{_rt?.Address ?? 0:X8}:{_rt?.ColorFormat ?? 0:X8} ds=0x{_depth?.Address ?? 0:X8}/{_depth?.Width ?? 0}x{_depth?.Height ?? 0} vs={d.VertexProgram.Info.Program.Hash:X16} fs={d.FragmentProgram.Info.Program.Hash:X16} prim={d.PrimitiveType} n={d.IndexCount}x{d.InstanceCount}");
        if (d.VisAddress != 0) sb.Append($" vis=0x{d.VisAddress:X8}{(d.VisIncrement ? "+" : "=")}");
        if (d.VisAddress != 0) sb.Append($" vis=0x{d.VisAddress:X8}{(d.VisIncrement ? "+" : "=")}");
        sb.Append($" blend={(b is { } bb ? $"{bb.ColorMask:X}:{bb.ColorFunc}/{bb.ColorSrc}/{bb.ColorDst}" : "none")} fragEn={st.Front.FragmentEnabled} cull={st.CullMode} twoSided={st.TwoSided} depth={st.Front.DepthFunc}/{st.Front.DepthWrite} bias={st.Front.DepthBiasFactor}/{st.Front.DepthBiasUnits} st={st.Front.Stencil.Func}/{st.Back.Stencil.Func} vp={st.ViewportEnabled}({st.VpOffX},{st.VpOffY},{st.VpScaleX},{st.VpScaleY},{st.VpOffZ},{st.VpScaleZ}) clip={st.RegionClipMode}[{st.ClipMinX},{st.ClipMinY}-{st.ClipMaxX},{st.ClipMaxY}]");
        for (int i = 0; i < 16; i++)
            if ((d.FragmentProgram.Info.TexturesUsed & (1u << i)) != 0)
                sb.Append($" t{i}=0x{d.FragmentTextures[i].DataAddress:X8}:{d.FragmentTextures[i].Format:X8}:{d.FragmentTextures[i].Width}x{d.FragmentTextures[i].Height}:T{d.FragmentTextures[i].TextureType >> 29}:S{d.FragmentTextures[i].StrideBytes}:G{d.FragmentTextures[i].GammaMode}:F{d.FragmentTextures[i].MagFilter}{d.FragmentTextures[i].MinFilter}{d.FragmentTextures[i].MipFilter}:B{d.FragmentTextures[i].LodBias}:L{d.FragmentTextures[i].LodMin}:M{d.FragmentTextures[i].MipCount}:A{d.FragmentTextures[i].UAddr}{d.FragmentTextures[i].VAddr}:W0={d.FragmentTextures[i].W0:X8}");
        foreach (var a in d.VertexProgram.Attributes) sb.Append($" a[r{a.RegIndex} s{a.StreamIndex}+{a.Offset} f{a.Format}x{a.ComponentCount}]");
        foreach (var s in d.VertexProgram.Streams) sb.Append($" st[{s.Stride}/{s.IndexSource}]");
        // first two vertices of stream 0 as raw bytes: lets attribute formats/offsets be checked against real data
        if (d.VertexProgram.Streams.Length > 0 && d.Streams[0] != 0)
        {
            int stride = Math.Clamp((int)d.VertexProgram.Streams[0].Stride, 4, 64);
            sb.Append($" v0@0x{d.Streams[0]:X8}=").Append(Convert.ToHexString(GuestMemory.ReadSpan(d.Streams[0], stride)));
            sb.Append(" v1=").Append(Convert.ToHexString(GuestMemory.ReadSpan(d.Streams[0] + (uint)stride, stride)));
        }
        sb.Append($" vu={d.VertexUniformsLength} fu={d.FragmentUniformsLength}");
        foreach (var s in d.FragmentProgram.Info.UniformSlots) sb.Append($" fub{s.GuestIndex}=0x{d.FragmentUniformAddrs[Math.Min(s.GuestIndex, 14)]:X8}+{s.ByteSize}");
        if (d.VertexUniformsLength > 0) sb.Append(" vuf=" + string.Join(",", Enumerable.Range(0, Math.Min(d.VertexUniformsLength / 4, 48)).Select(k => BitConverter.ToSingle(d.VertexUniforms, d.VertexUniformsOffset + k * 4).ToString("G4", System.Globalization.CultureInfo.InvariantCulture))));
        if (d.FragmentUniformsLength > 0) sb.Append(" fuh=" + string.Join(",", Enumerable.Range(0, Math.Min(d.FragmentUniformsLength / 2, 24)).Select(k => ((float)BitConverter.ToHalf(d.FragmentUniforms, d.FragmentUniformsOffset + k * 2)).ToString("G4", System.Globalization.CultureInfo.InvariantCulture))));
        if (d.FragmentUniformsLength >= 16) sb.Append($" fu0=({BitConverter.ToSingle(d.FragmentUniformSpan)},{BitConverter.ToSingle(d.FragmentUniformSpan[4..])},{BitConverter.ToSingle(d.FragmentUniformSpan[8..])},{BitConverter.ToSingle(d.FragmentUniformSpan[12..])})");
        sb.Append($" msaa={(_ms ? ((int)_msSamples).ToString() + "x" : _ss ? "ss2x" : "off")}");
        if (_diagActive is { } capture)
        {
            // capture: the list goes to draws.txt, each program's GLSL once to shaders/ (shaders loaded from the
            // per-title pack carry no GLSL: translated again here, without compiling)
            capture.DrawCount++;
            TraceLine(sb.ToString());
            var dir = Path.Combine(capture.Dir, "shaders");
            Directory.CreateDirectory(dir);
            WriteTraceGlsl(capture, dir, d.VertexProgram.Info.Program, vs.HintKey, vs.Gen, () => VertexHints(d, 0));
            uint colorFormat = _rt?.ColorFormat ?? 0;
            WriteTraceGlsl(capture, dir, d.FragmentProgram.Info.Program, fs.HintKey, fs.Gen, () => FragmentHints(d, colorFormat));
            return;
        }
        Log.Info(sb.ToString());
        if (_opt.ShaderCacheDir != null)
        {
            var dir = Path.Combine(_opt.ShaderCacheDir, "trace");
            Directory.CreateDirectory(dir);
            if (!string.IsNullOrEmpty(vs.Gen?.Glsl)) File.WriteAllText(Path.Combine(dir, $"{d.VertexProgram.Info.Program.Hash:x16}.vert"), vs.Gen!.Glsl);
            if (!string.IsNullOrEmpty(fs.Gen?.Glsl)) File.WriteAllText(Path.Combine(dir, $"{d.FragmentProgram.Info.Program.Hash:x16}.frag"), fs.Gen!.Glsl);
        }
    }

    void WriteTraceGlsl(DiagRequest capture, string dir, GxpProgram program, ulong hintKey, GeneratedShader? gen, Func<ShaderHints> hints)
    {
        if (!capture.ShadersWritten.Add((program.Hash, hintKey))) return;
        try
        {
            string glsl = !string.IsNullOrEmpty(gen?.Glsl) ? gen!.Glsl : ShaderGenerator.Generate(program, hints()).Glsl;
            File.WriteAllText(Path.Combine(dir, $"{program.Hash:x16}_{hintKey:x16}.{(program.IsVertex ? "vert" : "frag")}"), glsl);
        }
        catch (Exception e) { Log.Warn($"[diag] GLSL of {program.Hash:X16} not written: {e.Message}"); }
    }

    static bool IsTrivial(in StencilState s) => s.Func == 7 && s.StencilFail == 0 && s.DepthFail == 0 && s.DepthPass == 0;

    void SetStencil(CommandBuffer cmd, StencilFaceFlags face, in StencilState s)
    {
        _vk.CmdSetStencilOp(cmd, face, (StencilOp)s.StencilFail, (StencilOp)s.DepthPass, (StencilOp)s.DepthFail, (CompareOp)s.Func);
        _vk.CmdSetStencilCompareMask(cmd, face, s.CompareMask);
        _vk.CmdSetStencilWriteMask(cmd, face, s.WriteMask);
        _vk.CmdSetStencilReference(cmd, face, s.Ref);
    }
}
