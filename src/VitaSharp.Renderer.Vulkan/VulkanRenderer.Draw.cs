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
        /// <summary>1 when the draw culls one winding (GXM cull CW/CCW): fragment programs see G16 (the facing) only then (UsseTranslator.Core).</summary>
        public float FaceCull;
    }

    void CreateLayouts()
    {
        // 0/1: vertex/fragment uniform data, 2/3: vertex/fragment mapped memory (large buffers read in place)
        var ssbo = stackalloc DescriptorSetLayoutBinding[5];
        // 0/1 are dynamic: the uniform data of every draw is at its own offset of the upload ring, and one set (per
        // buffer and data length) serves every draw of the frame (VulkanRenderer.UniformSets.cs)
        ssbo[0] = new DescriptorSetLayoutBinding(0, DescriptorType.StorageBufferDynamic, 1, ShaderStageFlags.VertexBit);
        ssbo[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageBufferDynamic, 1, ShaderStageFlags.FragmentBit);
        ssbo[2] = new DescriptorSetLayoutBinding(2, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit);
        ssbo[3] = new DescriptorSetLayoutBinding(3, DescriptorType.StorageBuffer, 1, ShaderStageFlags.FragmentBit);
        // 4: VITASHARP_DEBUG_NAN=draws only, the NaN draw buffer the instrumented fragment shaders write (NanCheck.cs)
        ssbo[4] = new DescriptorSetLayoutBinding((uint)ShaderGenerator.NanDrawBinding, DescriptorType.StorageBuffer, 1, ShaderStageFlags.FragmentBit);
        var ci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = ShaderGenerator.NanDraws ? 5u : 4u, PBindings = ssbo };
        _vk.CreateDescriptorSetLayout(_dev, in ci, null, out _uboLayout).Ok("set layout");

        ChooseTextureUnits();
        _texLayoutV = TextureSetLayout(ShaderStageFlags.VertexBit, _texUnitsV);
        _texLayoutF = TextureSetLayout(ShaderStageFlags.FragmentBit, _texUnitsF);
        // binding 0: single-sampled framebuffer copy, binding 1: multisampled copy (real MSAA scenes), binding 2: the
        // multisampled colour attachment as input attachment (real MSAA scenes rendered in render passes); 3/4: the
        // scene's tile-word attachment as input attachment / a copy of it (VulkanRenderer.TileWord1.cs; written only
        // by the sets of draws whose shaders read it: descriptors no pipeline uses need not be valid)
        var fb = stackalloc DescriptorSetLayoutBinding[5];
        fb[0] = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        fb[1] = new DescriptorSetLayoutBinding(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        fb[2] = new DescriptorSetLayoutBinding(2, DescriptorType.InputAttachment, 1, ShaderStageFlags.FragmentBit);
        fb[3] = new DescriptorSetLayoutBinding(3, DescriptorType.InputAttachment, 1, ShaderStageFlags.FragmentBit);
        fb[4] = new DescriptorSetLayoutBinding(4, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        var fbci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 5, PBindings = fb };
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
        var (b, m, p) = _core.CreateHostBuffer(4096, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit, MemCat.PipelineBuffer);
        new Span<byte>((void*)p, 4096).Clear();
        _dummyBuffer = b;
        _dummyBufferMem = m;
        CreateDummyTexture();
    }

    // =================================================================== descriptor allocation

    DescriptorSet AllocSet(DescriptorSetLayout layout)
    {
        _statSets++;
        var f = F;
        var sizes = stackalloc DescriptorPoolSize[4]; // outside the loop (CA2014)
        while (true)
        {
            if (f.DescPoolIndex >= f.DescPools.Count)
            {
                sizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 5 * 8192);
                sizes[1] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 65536);
                sizes[2] = new DescriptorPoolSize(DescriptorType.InputAttachment, 16384); // two per framebuffer-fetch set
                sizes[3] = new DescriptorPoolSize(DescriptorType.StorageBufferDynamic, 2 * 8192);
                var pci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 8192, PoolSizeCount = 4, PPoolSizes = sizes };
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
        /// <summary>Guest vertex program (its attributes/streams are recorded with the pipelines this shader is used in).</summary>
        public GxmVertexProgram? Vp;
        /// <summary>Per stream, the end of the last attribute in a vertex (bytes), for <see cref="StreamEndsVp"/>; and
        /// whether the layout has the dummy binding 16. Computed on the first draw instead of on every draw.</summary>
        public uint[]? StreamEnds;
        public GxmVertexProgram? StreamEndsVp;
        public bool DummyBinding;
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
    /// <summary>
    /// An attribute's format for the shader hints: the GXM format, plus <see cref="ShaderGenerator.ByteFetchFlag"/> when
    /// its address is not a multiple of its component size (offset or stream stride). Vulkan requires that alignment;
    /// GXM does not, and God of War's character streams have a 19-byte stride with F16 attributes at offsets 3 and 11:
    /// NVIDIA read them anyway, Mali-G57 returned garbage (yellow, purple and blue patches on Kratos and the enemies).
    /// Such attributes are fetched as bytes (R8 formats need no alignment) and rebuilt in the shader: no copy, and
    /// aligned attributes keep their shaders.
    /// </summary>
    static byte AttributeFormatKey(GxmVertexAttribute a, GxmVertexStream[]? streams)
    {
        if (streams == null || a.StreamIndex >= streams.Length) return a.Format;
        return ShaderGenerator.AttributeMisaligned(a.Format, a.Offset, streams[a.StreamIndex].Stride) ? (byte)(a.Format | ShaderGenerator.ByteFetchFlag) : a.Format;
    }

    static ulong HintKey(GxmTextureDesc[] tex, uint used, GxmVertexAttribute[]? attrs, GxmVertexStream[]? streams, uint colorFormat, bool fetchInput = false, uint outputFormat = 0, bool tileWord1 = false,
        bool fetchSingle = false, uint varyingMask = 0, bool scaledPointSize = false, uint[]? rtBits = null)
    {
        ulong h = ShaderHints.Fnv.Offset;
        for (int i = 0; i < 16; i++) h = ShaderHints.Fnv.Mix(h, (used & (1u << i)) != 0 ? tex[i].Format | (rtBits?[i] ?? 0) : 0x0C000000);
        if (attrs != null)
            foreach (var a in attrs) { h = ShaderHints.Fnv.Mix(h, (uint)a.RegIndex); h = ShaderHints.Fnv.Mix(h, (uint)(int)a.ComponentCount); h = ShaderHints.Fnv.Mix(h, AttributeFormatKey(a, streams)); }
        h = ShaderHints.Fnv.Mix(h, colorFormat);
        if (outputFormat != 0) h = ShaderHints.Fnv.Mix(h, ShaderHints.OutputFormatKey ^ outputFormat);
        if (fetchInput) h = ShaderHints.Fnv.Mix(h, ShaderHints.FetchInputKey);
        if (fetchSingle) h = ShaderHints.Fnv.Mix(h, ShaderHints.FetchInputSingleKey);
        if (tileWord1) h = ShaderHints.Fnv.Mix(h, ShaderHints.TileWord1Key);
        if (scaledPointSize) h = ShaderHints.Fnv.Mix(h, ShaderHints.ScaledPointSizeKey);
        return varyingMask != 0 ? ShaderHints.Fnv.Mix(ShaderHints.Fnv.Mix(h, ShaderHints.VaryingMaskKey), varyingMask) : h;
    }

    static readonly string? VaryingTrimSetting = Environment.GetEnvironmentVariable(EnvVars.VaryingTrim)?.Trim();

    /// <summary>
    /// VITASHARP_VARYING_TRIM=1: the vertex shader variant for this draw declares and writes only the varyings its
    /// fragment program reads (<see cref="ShaderHints.VaryingMask"/>). Without it every vertex shader writes all 13
    /// vec4 varyings, most of them zero: on a tile-based GPU (Mali IDVS) each one is a store per vertex and memory the
    /// tiler keeps for the fragment phase (Uncharted's scene vertex shaders: about 20 load/store cycles per vertex, 4 of
    /// their 13 varyings used). One vertex variant per fragment-input set; the pipelines are per pair already.
    /// </summary>
    static readonly bool VaryingTrim = Switch(VaryingTrimSetting, false);

    /// <summary>The <see cref="ShaderHints.VaryingMask"/> of the draw's vertex shader (0: all varyings).</summary>
    static uint VertexVaryingMask(DrawCommand d) => VaryingTrim ? ShaderGenerator.FragmentVaryingMask(d.FragmentProgram.Info.Program) : 0;

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
        // first use by the GPU thread in this session: make sure the version-independent record knows how to rebuild it
        RecordShaderRecipe(program, hintKey, hints, d, colorFormat);
#if VITASHARP_DEVELOPER
        if ((Developer.GpuBenchState.ShaderDir != null || Developer.GpuBenchState.FragmentDiscard) && DevShaderOverride(program, hintKey, hints, d, colorFormat) is { } dev) return dev;

#endif
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
    /// <param name="varyingMask">the draw's <see cref="VertexVaryingMask"/> (the colour-format slot of the hints delegate)</param>
    static ShaderHints VertexHints(DrawCommand d, uint varyingMask) => new(TextureFormats(d.VertexTextures, d.VertexProgram.Info.TexturesUsed),
        d.VertexProgram.Attributes.Select(a => ((uint)a.RegIndex, (int)a.ComponentCount, AttributeFormatKey(a, d.VertexProgram.Streams))).ToArray(), 0,
        VaryingMask: varyingMask, ScaledPointSize: d.VertexProgram.Info.Program.WritesPointSize);

    static ShaderHints FragmentHints(DrawCommand d, uint colorFormat) =>
        new(TextureFormats(d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed), [], colorFormat, OutputRegisterFormat: NativeOutputFormat(d.FragmentProgram));

    static ShaderHints FragmentHintsInputFetch(DrawCommand d, uint colorFormat) =>
        new(TextureFormats(d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed), [], colorFormat, FetchInputAttachment: true,
            OutputRegisterFormat: NativeOutputFormat(d.FragmentProgram));

    // one-sample scenes: the draw renders in a render pass and reads the surface as a single-sampled input attachment
    static ShaderHints FragmentHintsInputFetchSingle(DrawCommand d, uint colorFormat) =>
        new(TextureFormats(d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed), [], colorFormat, FetchInputAttachment: true,
            OutputRegisterFormat: NativeOutputFormat(d.FragmentProgram), FetchInputSingleSample: true);

    // variants that keep the second output register word in the scene's tile-word attachment (VulkanRenderer.TileWord1.cs)
    static ShaderHints FragmentHintsTileWord1(DrawCommand d, uint colorFormat) =>
        new(TextureFormats(d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed), [], colorFormat,
            OutputRegisterFormat: NativeOutputFormat(d.FragmentProgram), TileWord1: true);

    static ShaderHints FragmentHintsInputFetchTileWord1(DrawCommand d, uint colorFormat) =>
        new(TextureFormats(d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed), [], colorFormat, FetchInputAttachment: true,
            OutputRegisterFormat: NativeOutputFormat(d.FragmentProgram), TileWord1: true);

    /// <summary>The hints with the <see cref="RtReinterpretHints"/> bits in their texture formats (same key as <see cref="HintKey"/> with them).</summary>
    static ShaderHints WithRtReinterpret(ShaderHints h, uint[] rtBits)
    {
        var f = (uint[])h.TextureFormats.Clone();
        for (int i = 0; i < f.Length && i < rtBits.Length; i++) f[i] |= rtBits[i];
        return h with { TextureFormats = f };
    }

    /// <summary>The <see cref="ShaderHints.OutputRegisterFormat"/> of a fragment program (part of its hint key).</summary>
    static uint NativeOutputFormat(GxmFragmentProgram fp) => ShaderHints.NativeOutputFormat(fp.Info.Program, fp.OutputFormat);

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

    /// <summary>
    /// The shader entry a draw's program and texture formats gave last time, so most draws skip the hint key (an FNV
    /// over the 16 texture units and every vertex attribute) and the cache lookup: the key depends only on the program
    /// object, the formats of the units it samples and (fragment) the colour format and variant flags, all compared
    /// here. Direct-mapped by program. VITASHARP_SHADER_MEMO=0 turns it off (A/B).
    /// </summary>
    sealed class ShaderMemo
    {
        public object? Program;
        public uint Extra;
        public readonly uint[] Formats = new uint[16];
        public object? Entry;
    }

    static readonly bool ShaderMemoOn = Environment.GetEnvironmentVariable(Core.Util.EnvVars.ShaderMemo) != "0";
    readonly ShaderMemo[] _vsMemo = NewShaderMemos(), _fsMemo = NewShaderMemos();
    static ShaderMemo[] NewShaderMemos() { var a = new ShaderMemo[64]; for (int i = 0; i < a.Length; i++) a[i] = new ShaderMemo(); return a; }
    static ShaderMemo MemoSlot(ShaderMemo[] table, object program) => table[System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(program) & 63];

    static bool MemoHit(ShaderMemo m, object program, uint extra, GxmTextureDesc[] tex, uint used)
    {
        if (!ReferenceEquals(m.Program, program) || m.Extra != extra || m.Entry == null) return false;
        for (uint u = used & 0xFFFF; u != 0; u &= u - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(u);
            if (m.Formats[i] != tex[i].Format) return false;
        }
        return true;
    }

    static void MemoStore(ShaderMemo m, object program, uint extra, GxmTextureDesc[] tex, uint used, object entry)
    {
        m.Program = program; m.Extra = extra; m.Entry = entry;
        for (uint u = used & 0xFFFF; u != 0; u &= u - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(u);
            m.Formats[i] = tex[i].Format;
        }
    }

    VsEntry GetVertexShader(DrawCommand d)
    {
        var vp = d.VertexProgram;
        // the cache key is computed without building the hints (thousands of draws per frame hit the cache)
        uint mask = VertexVaryingMask(d);
        var memo = MemoSlot(_vsMemo, vp);
        if (ShaderMemoOn && MemoHit(memo, vp, mask, d.VertexTextures, vp.Info.TexturesUsed)) return (VsEntry)memo.Entry!;
        var found = GetVertexShaderKeyed(d, vp, mask);
        if (ShaderMemoOn) MemoStore(memo, vp, mask, d.VertexTextures, vp.Info.TexturesUsed, found);
        return found;
    }

    VsEntry GetVertexShaderKeyed(DrawCommand d, GxmVertexProgram vp, uint mask)
    {
        ulong hk = HintKey(d.VertexTextures, vp.Info.TexturesUsed, vp.Attributes, vp.Streams, 0, varyingMask: mask, scaledPointSize: vp.Info.Program.WritesPointSize);
        if (_vsCache.TryGetValue((vp, hk), out var e)) return e;
        var me = GetModule(vp.Info.Program, hk, VertexHints, d, mask);
        e = new VsEntry { Module = me.Module, Gen = me.Gen, Failed = me.Module.Handle == 0 || me.Gen == null, ProgramHash = vp.Info.Program.Hash, HintKey = hk, Vp = vp };
        if (me.Gen != null) BuildVertexInput(vp, me.Gen, e);
        _vsCache[(vp, hk)] = e;
        _fNewShaders++;
        if (DebugMode.Enabled) DbgVertexShader(e, d);
        return e;
    }

    /// <param name="fetchInput">the variant whose programmable blending reads the input attachment</param>
    /// <param name="tileWord1">the variant that keeps the second output register word in the tile-word attachment</param>
    /// <param name="fetchSingle">with <paramref name="fetchInput"/>: the single-sampled input attachment variant</param>
    FsEntry GetFragmentShader(DrawCommand d, bool fetchInput = false, bool tileWord1 = false, bool fetchSingle = false)
    {
        var fp = d.FragmentProgram;
        uint colorFormat = _rt?.ColorFormat ?? 0;
        // the plain variant only (the fetch and tile-word variants are rarer); render-target reinterpretation hints depend
        // on the textures' memory, not only their formats: no memo above native resolution
        if (!ShaderMemoOn || ResScale > 1f || fetchInput || tileWord1 || fetchSingle) return GetFragmentShaderKeyed(d, fp, colorFormat, fetchInput, tileWord1, fetchSingle);
        var memo = MemoSlot(_fsMemo, fp);
        if (MemoHit(memo, fp, colorFormat, d.FragmentTextures, fp.Info.TexturesUsed)) return (FsEntry)memo.Entry!;
        var found = GetFragmentShaderKeyed(d, fp, colorFormat, false, false, false);
        MemoStore(memo, fp, colorFormat, d.FragmentTextures, fp.Info.TexturesUsed, found);
        return found;
    }

    FsEntry GetFragmentShaderKeyed(DrawCommand d, GxmFragmentProgram fp, uint colorFormat, bool fetchInput, bool tileWord1, bool fetchSingle)
    {
        // above native: textures that reinterpret a render target are sampled at the native texel mapping
        var rtBits = RtReinterpretHints(d.FragmentTextures, fp.Info.TexturesUsed);
        ulong hk = HintKey(d.FragmentTextures, fp.Info.TexturesUsed, null, null, colorFormat, fetchInput, NativeOutputFormat(fp), tileWord1, fetchSingle, rtBits: rtBits);
        if (_fsCache.TryGetValue((fp, hk), out var e)) return e;
        Func<DrawCommand, uint, ShaderHints> hints = (fetchInput, tileWord1) switch
        {
            (true, false) when fetchSingle => FragmentHintsInputFetchSingle,
            (true, true) => FragmentHintsInputFetchTileWord1,
            (false, true) => FragmentHintsTileWord1,
            (true, false) => FragmentHintsInputFetch,
            _ => FragmentHints,
        };
        if (rtBits != null)
        {
            var plain = hints;
            hints = (dd, cf) => WithRtReinterpret(plain(dd, cf), rtBits);
        }
        var me = GetModule(fp.Info.Program, hk, hints, d, colorFormat);
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

    void BuildVertexInput(GxmVertexProgram vp, GeneratedShader gen, VsEntry e) => BuildVertexInput(vp.Attributes, vp.Streams, gen, e);

    /// <summary>Vulkan vertex input of a vertex shader fed by these guest attributes/streams (also used for recorded pipelines).</summary>
    void BuildVertexInput(GxmVertexAttribute[] attributes, GxmVertexStream[] streams, GeneratedShader gen, VsEntry e)
    {
        var bindings = new List<VertexInputBindingDescription>();
        var attrs = new List<VertexInputAttributeDescription>();
        uint streamMask = 0;
        bool needDummy = false;
        foreach (var input in gen.VertexInputs)
        {
            var a = attributes.FirstOrDefault(x => x.RegIndex == input.RegIndex);
            bool found = attributes.Any(x => x.RegIndex == input.RegIndex);
            for (int l = 0; l < input.LocationCount; l++)
            {
                if (!found || a.StreamIndex >= streams.Length)
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
            var st = streams[s];
            bool instanced = st.IndexSource is 2 or 3;
            bindings.Add(new VertexInputBindingDescription((uint)s, st.Stride, instanced ? VertexInputRate.Instance : VertexInputRate.Vertex));
        }
        if (needDummy) bindings.Add(new VertexInputBindingDescription(16, 0, VertexInputRate.Vertex));
        e.Bindings = bindings.ToArray();
        e.Attributes = attrs.ToArray();
        e.LayoutHash = LayoutHash(e.Bindings, e.Attributes);
        e.StreamMask = streamMask;
        e.StreamEnds = null; // recomputed by the next draw
    }

    /// <summary>Identity of a vertex input layout (part of the pipeline key; the prewarm thread computes it from
    /// the recorded descriptions, so both must hash the same way).</summary>
    static ulong LayoutHash(VertexInputBindingDescription[] bindings, VertexInputAttributeDescription[] attrs)
    {
        ulong h = System.IO.Hashing.XxHash64.HashToUInt64(MemoryMarshal.AsBytes(bindings.AsSpan()));
        return System.IO.Hashing.XxHash64.HashToUInt64(MemoryMarshal.AsBytes(attrs.AsSpan()), (long)h);
    }

    // =================================================================== pipelines

    /// <param name="TileWord1">0: no auxiliary attachment; 1: the scene's tile-word attachment (VulkanRenderer.TileWord1.cs)
    /// is attached at location 1 and left unwritten; 2: attached and written</param>
    readonly record struct PipelineKey(ulong Vs, ulong Fs, ulong VertexLayout, Format ColorFormat, bool HasColor,
        bool Blend, byte ColorFunc, byte AlphaFunc, byte ColorSrc, byte ColorDst, byte AlphaSrc, byte AlphaDst, byte Mask,
        int TopologyClass, PolygonMode Polygon, SampleCountFlags Samples, ulong Static = 0, byte TileWord1 = 0, bool InputPass = false);

    /// <summary>GPU-thread view of the pipelines (lock-free lookups on the draw path).</summary>
    readonly Dictionary<PipelineKey, Pipeline> _pipelines = new();
    /// <summary>Every pipeline created (owner for destruction), shared with the prewarm thread.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<PipelineKey, Pipeline> _sharedPipelines = new();
    Pipeline _boundPipeline;
    bool _pointsSeen;

    static int TopologyClass(PrimitiveTopology t) => t switch
    {
        PrimitiveTopology.PointList => 0,
        PrimitiveTopology.LineList or PrimitiveTopology.LineStrip => 1,
        _ => 2,
    };

    Pipeline GetPipeline(in PipelineKey key, VsEntry vs, FsEntry fs, PrimitiveTopology topology)
    {
        if (_pipelines.TryGetValue(key, out var p)) return p;
        p = ResolvePipeline(key, vs, fs, topology);
        // first use by the GPU thread in this session (whoever built it): pipelines.list keeps first-use order
        if (p.Handle != 0) RecordPipeline(key, vs, fs, topology);
        return p;
    }

    Pipeline ResolvePipeline(in PipelineKey key, VsEntry vs, FsEntry fs, PrimitiveTopology topology)
    {
        Pipeline p;
        _fNewPipelines++;
        if (DebugMode.Enabled) DbgPipeline(key, vs, fs, topology);
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
        if (_asyncPipelines || _rebuildActive) // (the boot rebuild builds recorded pipelines on workers too)
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
            if (_sharedPipelines.TryAdd(key, p)) NotePipelineCreated();
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
                // off: the near plane clips, as GXM does (VkCore.DepthClamp); the vertex shader also clips the far plane
                DepthClampEnable = _core.DepthClamp,
            };
            var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = key.Samples };
            var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.LessOrEqual };
            // drivers with broken extended dynamic state: the draw's state is baked in (VulkanRenderer.StaticState.cs)
            ApplyStaticState(key.Static, ref ia, ref rs, ref ds);
            if (key.Static != 0) Interlocked.Increment(ref _statPipelinesStatic);
            if (StaticBias) rs.DepthBiasEnable = true;
            var att = stackalloc PipelineColorBlendAttachmentState[2];
            att[0] = new PipelineColorBlendAttachmentState
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
            // the tile-word attachment (location 1): written only by programs that write the word, never blended
            bool tileWord = key.HasColor && key.TileWord1 != 0;
            att[1] = new PipelineColorBlendAttachmentState { ColorWriteMask = key.TileWord1 == 2 ? ColorComponentFlags.RBit : 0 };
            uint colorCount = key.HasColor ? tileWord ? 2u : 1u : 0u;
            // multisampled scenes and one-sample programmable-blending draws (VulkanRenderer.SingleFetch.cs) render in
            // render passes (input attachments for programmable blending)
            bool msPass = key.Samples != SampleCountFlags.Count1Bit || key.InputPass;
            var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = colorCount, PAttachments = att,
                // those render passes declare rasterization-order colour access when the device has it
                Flags = msPass && key.HasColor && _core.RasterizationOrderColor ? RoaaBlendFlag : 0 };
            // viewport, scissor, line width, depth bias, stencil masks/reference and (unless a driver quirk bakes them
            // into the pipeline) cull mode, front face, topology, depth/stencil test state and depth bias enable
            var dyn = stackalloc DynamicState[16];
            int dynCount = DrawDynamicStates(dyn, key.Static != 0);
            var dsi = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = (uint)dynCount, PDynamicStates = dyn };
            var colorFormat = key.ColorFormat;
            var colorFormats = stackalloc Format[2] { colorFormat, TileWord1Format };
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = colorCount,
                PColorAttachmentFormats = colorFormats,
                DepthAttachmentFormat = _core.DepthFormat,
                StencilAttachmentFormat = _core.DepthFormat,
            };
            // without dynamic rendering single-sampled scenes are render passes too (VulkanRenderer.RenderPasses.cs)
            bool rpPass = !msPass && UseRenderPasses;
            var gci = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = msPass || rpPass ? null : &rendering,
                RenderPass = msPass ? MsaaCompatPass(colorFormat, key.Samples, tileWord) : rpPass ? SingleCompatPass(key.HasColor ? colorFormat : Format.Undefined, tileWord) : default,
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

    static uint MaxIndex(uint addr, uint count, bool i32) => MinMaxIndex(addr, count, i32).Max;

    static (uint Min, uint Max) MinMaxIndex(uint addr, uint count, bool i32)
    {
        if (count == 0) return (0, 0);
        if (MinMaxOld) return MinMaxIndexOld(addr, count, i32);
        return i32 ? MinMax(GuestMemory.ReadSpan<uint>(addr, (int)count)) : MinMax(GuestMemory.ReadSpan<ushort>(addr, (int)count));
    }

    /// <summary>VITASHARP_MINMAX=old: the scalar scan of main b29e26a, for A/B runs on the phone.</summary>
    static readonly bool MinMaxOld = Environment.GetEnvironmentVariable(EnvVars.MinMaxScan)?.Trim() == "old";

    static (uint Min, uint Max) MinMaxIndexOld(uint addr, uint count, bool i32)
    {
        uint max = 0, min = uint.MaxValue;
        if (i32)
        {
            var s = GuestMemory.ReadSpan<uint>(addr, (int)count);
            foreach (var v in s) { if (v > max) max = v; if (v < min) min = v; }
        }
        else
        {
            var s = GuestMemory.ReadSpan<ushort>(addr, (int)count);
            foreach (var v in s) { if (v > max) max = v; if (v < min) min = v; }
        }
        return (min, max);
    }

    /// <summary>Lowest and highest value, 128 bits at a time (the scalar loop was ~2% of the GPU worker on the phone).
    /// Not generic: Mono's AOT compiler left a generic-math version to the JIT.</summary>
    public static (uint Min, uint Max) MinMax(ReadOnlySpan<ushort> s)
    {
        uint min = ushort.MaxValue, max = 0;
        int i = 0;
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated && s.Length >= 16)
        {
            var vmin = System.Runtime.Intrinsics.Vector128.Create(ushort.MaxValue);
            var vmax = System.Runtime.Intrinsics.Vector128<ushort>.Zero;
            for (; i <= s.Length - 8; i += 8)
            {
                var v = System.Runtime.Intrinsics.Vector128.Create(s.Slice(i, 8));
                vmin = System.Runtime.Intrinsics.Vector128.Min(vmin, v);
                vmax = System.Runtime.Intrinsics.Vector128.Max(vmax, v);
            }
            for (int k = 0; k < 8; k++) { min = Math.Min(min, vmin[k]); max = Math.Max(max, vmax[k]); }
        }
        for (; i < s.Length; i++) { uint v = s[i]; if (v < min) min = v; if (v > max) max = v; }
        return (min, max);
    }

    /// <inheritdoc cref="MinMax(ReadOnlySpan{ushort})"/>
    public static (uint Min, uint Max) MinMax(ReadOnlySpan<uint> s)
    {
        uint min = uint.MaxValue, max = 0;
        int i = 0;
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated && s.Length >= 8)
        {
            var vmin = System.Runtime.Intrinsics.Vector128.Create(uint.MaxValue);
            var vmax = System.Runtime.Intrinsics.Vector128<uint>.Zero;
            for (; i <= s.Length - 4; i += 4)
            {
                var v = System.Runtime.Intrinsics.Vector128.Create(s.Slice(i, 4));
                vmin = System.Runtime.Intrinsics.Vector128.Min(vmin, v);
                vmax = System.Runtime.Intrinsics.Vector128.Max(vmax, v);
            }
            for (int k = 0; k < 4; k++) { min = Math.Min(min, vmin[k]); max = Math.Max(max, vmax[k]); }
        }
        for (; i < s.Length; i++) { uint v = s[i]; if (v < min) min = v; if (v > max) max = v; }
        return (min, max);
    }

    /// <summary>Draw number within the presented frame (debug: VITASHARP_DEBUG_TEX0=id paints it, the draw trace logs it).</summary>
    int _drawSerial;

    readonly HashSet<(uint, Format)> _rawLogged = new();
    readonly Dictionary<Format, bool> _rawFormatOk = new();

    void Draw(DrawCommand d)
    {
        _drawSerial++;
        if (_traceManual) _traceDrawId++;
        if (!_inScene || d.IndexCount == 0) return;
        if (d.FragmentProgram.IsMaskUpdate && _diagActive != null && (_traceManual || _traceFrame == _presentCount))
            TraceLine($"[draw] id={NextDrawId - 1} f={_presentCount} mask update (not rendered): prim={d.PrimitiveType} n={d.IndexCount}x{d.InstanceCount} " +
                      $"st={StencilText(d.State.Front.Stencil)}/{StencilText(d.State.Back.Stencil)} clip={d.State.RegionClipMode}[{d.State.ClipMinX},{d.State.ClipMinY}-{d.State.ClipMaxX},{d.State.ClipMaxY}]");
        if (d.FragmentProgram.IsMaskUpdate || d.FragmentProgram.Info == null) return;
        var st = d.State;
        if (st.RegionClipMode == 1) return; // clip everything
        BeginFrameIfNeeded();
        long stall0 = _frameStallTicks;
        ProfStart();

        var vs = GetVertexShader(d);
        var fs = GetFragmentShader(d);
        TraceSnapshot(d);
        if (_traceManual || _traceFrame == _presentCount || (_rt != null && TraceRtAddrs.Contains(_rt.Address))) TraceDraw(d, vs, fs);
        if (_drawRing != null) DrawRingDraw(d, vs, fs);
        ProfMark(ProfPhase.Shader);
        if (vs.Failed || fs.Failed) return;

        // ---- textures (may need to leave the render pass)
        SetDrawArea(in st);
        if (UsesMissingTextureUnit(d.VertexProgram.Info.TexturesUsed, _texUnitsV, d.VertexProgram.Info.Program.Hash) || UsesMissingTextureUnit(d.FragmentProgram.Info.TexturesUsed, _texUnitsF, d.FragmentProgram.Info.Program.Hash)) return;
        // a texture of the scene's own depth may be read in place when the draw can render in a read-only depth fetch
        // pass (VulkanRenderer.SingleFetch.cs): decided before the textures are resolved
        _feedbackUsed = false;
        _feedbackAllowed = DepthFeedback && !st.Front.DepthWrite && SingleFetchPossible() && !_ss && SceneScale == 1
            && fs.Gen!.RawColorBits == 0 && fs.Gen.TileWord1 == 0;
        var vSet = BuildTextureSet(_texLayoutV, d.VertexTextures, d.VertexProgram.Info.TexturesUsed, d.VertexProgram.Info.CubeSamplers);
        var fSet = BuildTextureSet(_texLayoutF, d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed, d.FragmentProgram.Info.CubeSamplers);
        ProfMark(ProfPhase.TexSets);
        _feedbackAllowed = false;
        // programmable blending in a multisampled render pass reads the attachment itself (input attachment);
        // raw integer outputs (integer attachment view) and VITASHARP_MSAA_FETCH=copy use a copy of the samples
        // the second output register word a one-word native colour leaves in the tile: the first program of the scene
        // that writes it attaches the tile-word attachment; from then on the programs that use it read/write it there
        if (fs.Gen!.TileWord1 != 0 && !_tw1Active && (fs.Gen.TileWord1 & TileWord1Use.Writes) != 0 && fs.Gen.RawColorBits == 0) ActivateTileWord1();
        bool tw1 = _tw1Active && fs.Gen!.TileWord1 != 0 && fs.Gen.RawColorBits == 0;
        bool inputFetch = (fs.Gen!.UsesFragColor || tw1) && _ms && MsaaInputFetch && fs.Gen.RawColorBits == 0;
        var topology = Formats.Topology(d.PrimitiveType);
        if (topology == PrimitiveTopology.PointList && !_pointsSeen)
        {
            _pointsSeen = true; // once per renderer: tells whether a game draws point sprites (their size follows res_multiplier)
            Log.Info($"[gpu] first point-list draw: vertex program {d.VertexProgram.Info.Program.Hash:X16} {(d.VertexProgram.Info.Program.WritesPointSize ? "writes the point size" : "has no point size (1 pixel)")}, scene scale {SceneScale}");
        }
        // a small programmable-blending draw in a one-sample scene: rendered in its own render pass that reads the
        // surface as an input attachment, piece by piece (FetchSplitDraw; VulkanRenderer.SingleFetch.cs)
        // (any size with rasterization-order access, VITASHARP_FETCH_LARGE: no pieces needed)
        bool singleFetch = !inputFetch && !tw1 && fs.Gen.UsesFragColor && fs.Gen.RawColorBits == 0 && SingleFetchPossible()
            && (FetchLarge || FetchSplitDraw(d, topology));
        // a draw that renders in a fetch pass without reading the colour: it reads the scene's depth in place, or the
        // scene's open fetch pass takes it instead of ending (VITASHARP_FETCH_PASS_JOIN)
        bool passDraw = singleFetch || _feedbackUsed
            || FetchPassJoin && !inputFetch && !tw1 && fs.Gen.RawColorBits == 0 && SingleFetchPossible()
               && FetchPassContinues(_rtAttachFormat, st.Front.DepthWrite, false);
        // a small programmable-blending draw of a multisampled scene that reads copies of the samples
        // (VITASHARP_MSAA_FETCH=copy): drawn piece by piece, each piece reading a new copy (what the earlier pieces drew)
        bool copySplit = _ms && !inputFetch && !tw1 && fs.Gen.UsesFragColor && fs.Gen.RawColorBits == 0 && FetchSplitDraw(d, topology);
        if (inputFetch || tw1 || singleFetch)
        {
            fs = GetFragmentShader(d, fetchInput: inputFetch || singleFetch, tileWord1: tw1, fetchSingle: singleFetch);
            if (fs.Failed) return;
        }
        var fbSet = inputFetch ? BuildMsaaInputFetchSet(tw1) : singleFetch ? BuildSingleInputFetchSet()
            : fs.Gen!.UsesFragColor || tw1 ? BuildFramebufferFetchSet(tw1) : _emptyFbSet ??= BuildEmptyFbSet();

        ProfMark(ProfPhase.Texture);
        // ---- pipeline
        bool hasColor = _rt != null;
        var blend = d.FragmentProgram.Blend;
        bool fragEnabled = st.Front.FragmentEnabled;
        // a program flagged OUTPUT_UNDEFINED writes no colour: its (uninitialised) output must not reach the surface, as
        // in Vita3K (Tearaway draws its stencil masks with such an empty program: the screen filled with random noise)
        byte mask = !fragEnabled || d.FragmentProgram.Info.Program.IsOutputUndefined ? (byte)0 : blend?.ColorMask ?? 0xF;
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
        // a surface format the GPU renders but cannot blend (32-bit float on Mali): the draw is drawn unblended
        if (blendOn && !raw && hasColor && !BlendSupported(attachFormat))
        {
            Log.WarnOnce($"noblend-{attachFormat}", $"[gpu] {attachFormat} cannot blend on this GPU: blended draws into render target 0x{_rt!.Address:X8} drawn without blending");
            blendOn = false;
        }
        byte tw1Key = !_tw1Active ? (byte)0 : tw1 && mask != 0 && (fs.Gen!.TileWord1 & TileWord1Use.Writes) != 0 ? (byte)2 : (byte)1;
        var key = new PipelineKey(vs.Module.Handle, fs.Module.Handle, vs.LayoutHash, attachFormat, hasColor,
            blendOn, blend?.ColorFunc ?? 0, blend?.AlphaFunc ?? 0, blend?.ColorSrc ?? 1, blend?.ColorDst ?? 0, blend?.AlphaSrc ?? 1, blend?.AlphaDst ?? 0,
            mask, TopologyClass(topology), Formats.Polygon(st.Front.PolygonMode), SceneSamples, StaticStateKey(in st, topology), tw1Key, passDraw);
        var pipeline = GetPipeline(key, vs, fs, topology);
        if (_frameStallTicks != stall0) NoteDrawStall(d, mask, tw1Key, fs.Gen!.UsesFragColor || tw1, _frameStallTicks - stall0);
        ProfMark(ProfPhase.Pipeline);
        if (pipeline.Handle == 0) return;

        // ---- geometry upload
        int indexSize = d.Index32 ? 4 : 2;
        var (minIndex, maxIndex) = MinMaxIndex(d.IndexAddress, d.IndexCount, d.Index32);
        // per-vertex streams are uploaded from the lowest index used, the draw rebased with vertexOffset (meshes drawn in
        // pieces out of one big vertex buffer uploaded it whole for every piece: F1 2011's races copied tens of MB per
        // frame); not when the program reads the vertex index itself (gl_VertexIndex includes the offset)
        uint firstVertex = minIndex > 0 && (d.VertexProgram.Info.Program.Flags & Core.Gxm.Shader.GxpProgram.FlagIndexUsed) == 0 ? minIndex : 0;
        int vertexOffset = -(int)firstVertex;
        var (ib, ibOff) = UploadRange(d.IndexAddress, d.IndexCount * (uint)indexSize);
        var vbufs = stackalloc Buffer[17];
        var voffs = stackalloc ulong[17];
        var vp = d.VertexProgram;
        if (!ReferenceEquals(vs.StreamEndsVp, vp) || vs.StreamEnds == null)
        {
            var ends = new uint[16];
            foreach (var a in vp.Attributes)
            {
                if (a.StreamIndex >= 16) continue;
                var (_, size) = Formats.Attribute(a.Format, Math.Min((int)a.ComponentCount, 4), 'f');
                uint attrBytes = (uint)(size * Math.Max(1, (a.ComponentCount + 3) / 4));
                ends[a.StreamIndex] = Math.Max(ends[a.StreamIndex], a.Offset + attrBytes);
            }
            vs.DummyBinding = vs.Bindings.Any(b => b.Binding == 16);
            vs.StreamEnds = ends;
            vs.StreamEndsVp = vp;
        }
        for (int s = 0; s < 16; s++)
        {
            if ((vs.StreamMask & (1u << s)) == 0) continue;
            var stream = vp.Streams[s];
            bool instanced = stream.IndexSource is 2 or 3;
            uint end = vs.StreamEnds[s];
            uint count = instanced ? d.InstanceCount : maxIndex + 1 - firstVertex;
            uint length = (count - 1) * stream.Stride + end + 16;
            uint addr = d.Streams[s];
            if (addr == 0) { vbufs[s] = _dummyBuffer; voffs[s] = 0; continue; }
            if (!instanced) addr += firstVertex * stream.Stride;
            // vertices an earlier draw's program stored to memory are only in the mirror (VulkanRenderer.Mapped.cs)
            if (GpuWrittenStream(addr, length) is >= 0 and var mirrorOff)
            {
                vbufs[s] = _mirror; voffs[s] = (ulong)mirrorOff;
                if (_diagActive != null) TraceLine($"[stream] id={NextDrawId - 1} s{s}=0x{addr:X8}+{length}: read from the mirror (a program stored there)");
                continue;
            }
            var (b, off) = UploadRange(addr, length);
            vbufs[s] = b;
            voffs[s] = off;
        }

        ProfMark(ProfPhase.Geometry);
        // ---- uniforms
        var bufInfos = stackalloc DescriptorBufferInfo[4];
        bufInfos[2] = ResolveMappedSlots(d.VertexProgram.Info.UniformSlots, d.VertexUniformSpan, d.VertexUniformAddrs, vs.Gen?.WritesMemory == true);
        bufInfos[3] = ResolveMappedSlots(d.FragmentProgram.Info.UniformSlots, d.FragmentUniformSpan, d.FragmentUniformAddrs, fs.Gen?.WritesMemory == true);
        DrawUniformInfos(d, bufInfos);
        var dynOffsets = stackalloc uint[2] { (uint)bufInfos[0].Offset, (uint)bufInfos[1].Offset };
        var uSet = UniformSet(bufInfos);

        ProfMark(ProfPhase.Uniform);
        // ---- record
        // copySplit: one pass of this loop per piece, each after a new copy of the samples (the rendering ends for it)
        int passes = copySplit ? _fetchPieces.Count : 1;
        var sets = stackalloc DescriptorSet[4] { uSet, vSet, fSet, fbSet };
        if (copySplit) _statFetchSplitDraws++;
        for (int piece = 0; piece < passes; piece++)
        {
            if (piece > 0) fbSet = BuildFramebufferFetchSet();
            if (_rendering && (_renderRaw != raw || (raw && _rawFormat != attachFormat))) EndRendering(resolve: false);
            _renderRaw = raw;
            if (raw) _rawFormat = attachFormat;
            CommandBuffer cmd;
            if (passDraw && FetchPassMerge && FetchPassContinues(attachFormat, st.Front.DepthWrite, _feedbackUsed))
            {
                cmd = F.Cmd;
                ContinueSingleFetchPass();
                if (singleFetch && !_core.RasterizationOrderColor) InputFetchBarrier(cmd);
            }
            // a pass begun without depth writes keeps its depth read-only, ready for draws that read it in place
            else if (passDraw) cmd = BeginSingleFetchPass(attachFormat, DepthFeedback && !st.Front.DepthWrite);
            else
            {
                BeginRendering();
                cmd = Cmd;
                // with rasterization-order attachment access the reads are ordered without barriers (and need no split)
                if (inputFetch && !_core.RasterizationOrderColor) InputFetchBarrier(cmd);
            }
            ProfMark(ProfPhase.Begin);
            BeginCostQuery(cmd, d);
            // dynamic state set by an earlier draw holds in this command buffer while only draw pipelines were bound
            // (they all declare the same dynamic state); another pipeline or command buffer starts over
            if (_boundPipeline.Handle == 0 || cmd.Handle != _dyn.Cmd) _dyn = new DynShadow { Cmd = cmd.Handle };
            if (_boundPipeline.Handle != pipeline.Handle)
            {
                _cmd.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
                _statPipelineBinds++;
                _boundPipeline = pipeline;
            }
            sets[3] = fbSet;
            // set 0 is bound for every draw (its dynamic offsets); sets 1-3 only from the first one that changed
            uint setCount = 1;
            for (uint i = 3; i >= 1; i--)
                if (!_dyn.SetsBound || sets[i].Handle != _dyn.Sets[(int)i]) { setCount = i + 1; break; }
            _cmd.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, setCount, sets, 2, dynOffsets);
            for (int i = 1; i < 4; i++) _dyn.Sets[i] = sets[i].Handle;
            _dyn.SetsBound = true;
            // one call for every stream (the bindings between them get the dummy buffer): each vkCmd* costs driver CPU
            if (vs.StreamMask != 0)
            {
                int lo = System.Numerics.BitOperations.TrailingZeroCount(vs.StreamMask), hi = 31 - System.Numerics.BitOperations.LeadingZeroCount(vs.StreamMask);
                for (int s = lo + 1; s < hi; s++)
                    if ((vs.StreamMask & (1u << s)) == 0) { vbufs[s] = _dummyBuffer; voffs[s] = 0; }
                _cmd.CmdBindVertexBuffers(cmd, (uint)lo, (uint)(hi - lo + 1), &vbufs[lo], &voffs[lo]);
            }
            if (vs.DummyBinding)
            {
                var db = _dummyBuffer; ulong dz = 0;
                _cmd.CmdBindVertexBuffers(cmd, 16, 1, &db, &dz);
            }
            _cmd.CmdBindIndexBuffer(cmd, ib, ibOff, d.Index32 ? IndexType.Uint32 : IndexType.Uint16);
            SetDynamicState(cmd, st, topology);
            // Programmable blending reads the attachment behind a barrier placed before the draw, but on hardware every
            // fragment reads what earlier primitives of the same draw wrote. Small draws (glyph quads of a text string
            // blending over each other, strips of particle quads) are drawn piece by piece with a barrier in between.
            // Input attachments: multisampled render passes, and one-sample images (surface or supersample image) in a
            // render pass of their own for this (VulkanRenderer.SingleFetch.cs). Copies of a one-sample image would be
            // re-taken for every piece (Uncharted's thousands of small particle draws went from 6.7 to 32 ms of GPU
            // time per frame); only the multisampled copy path (VITASHARP_MSAA_FETCH=copy, a fallback) does that
            // (copySplit). The pieces of a singleFetch / copySplit draw were built when it was chosen (_fetchPieces).
            if (copySplit)
                _cmd.CmdDrawIndexed(cmd, _fetchPieces[piece].Count, 1, _fetchPieces[piece].First, vertexOffset, 0);
            else if ((singleFetch || inputFetch && FetchSplitDraw(d, topology)) && !_core.RasterizationOrderColor)
            {
                for (int c = 0; c < _fetchPieces.Count; c++)
                {
                    if (c > 0) InputFetchBarrier(cmd);
                    _cmd.CmdDrawIndexed(cmd, _fetchPieces[c].Count, 1, _fetchPieces[c].First, vertexOffset, 0);
                }
                _statFetchSplitDraws++;
            }
            else
            {
                bool visQuery = BeginVisibilityQuery(cmd, d, out uint query);
                _cmd.CmdDrawIndexed(cmd, d.IndexCount, d.InstanceCount, 0, vertexOffset, 0);
                _statDraws++;
                if (visQuery) { EndVisibilityQuery(cmd, query); CostNoteVisQuery(query); }
            }
            EndCostQuery(cmd);
            if (passDraw && !FetchPassMerge) EndSingleFetchPass();
        }
        ProfMark(ProfPhase.Record);
        if (ShaderGenerator.NanDraws) NanNoteDraw(d, vs, fs);
        if (CostOn) CostNoteDraw(d, vs, fs);
        _drawsSinceSubmit++;
        if (_rt != null) { _rt.Dirty = true; _rt.Version++; _rt.LastUsedFrame = FrameCount; NoteWritebackRegion(in st); NoteDrawn(); LogDraw(_rt); }
    }

    /// <summary>VITASHARP_FETCH_SPLIT=0 disables the piece-by-piece split of programmable-blending draws.</summary>
    static readonly bool FetchSplit = Environment.GetEnvironmentVariable(EnvVars.FetchSplit)?.Trim() != "0";
    /// <summary>Largest programmable-blending draw drawn piece by piece (each piece costs a barrier).</summary>
    const int FetchSplitMax = 256;
    long _statFetchSplitDraws;
    readonly List<(uint First, uint Count)> _fetchPieces = new();

    /// <summary>
    /// Splits a programmable-blending draw into pieces drawn one by one with a barrier in between (false = draw it
    /// whole): triangle lists and strips without instancing or visibility queries, up to <see cref="FetchSplitMax"/>
    /// pieces (<see cref="Core.Gpu.FetchSplit.Pieces"/>: one triangle each in lists, two in strips).
    /// </summary>
    bool FetchSplitDraw(DrawCommand d, PrimitiveTopology topology)
    {
        if (!FetchSplit || d.InstanceCount != 1 || d.VisAddress != 0) return false;
        if (topology is not (PrimitiveTopology.TriangleList or PrimitiveTopology.TriangleStrip)) return false;
        var raw = GuestMemory.ReadSpan(d.IndexAddress, (int)(d.IndexCount * (d.Index32 ? 4 : 2)));
        return Core.Gpu.FetchSplit.Pieces(raw, d.Index32, topology == PrimitiveTopology.TriangleStrip, _fetchPieces, FetchSplitMax);
    }

    DescriptorBufferInfo UniformInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return new DescriptorBufferInfo(_dummyBuffer, 0, 256);
        ulong align = Math.Max(_core.Properties.Limits.MinStorageBufferOffsetAlignment, 16);
        var (b, off) = UploadSpan(data, align);
        _statUniformBytes += data.Length;
        return new DescriptorBufferInfo(b, off, (ulong)data.Length);
    }

    void SetDynamicState(CommandBuffer cmd, in DrawState st, PrimitiveTopology topology)
    {
        float w = _sceneW, h = _sceneH;
        Viewport vpRect;
        var pc = new PushConstants { ScreenWidth = w, ScreenHeight = h, ResMultiplier = SceneScale, ZOffset = 0, ZScale = 1, DrawId = _drawSerial,
            FbSamples = _ms ? (_msSamples == SampleCountFlags.Count4Bit ? 4 : 2) : 1, FaceCull = st.CullMode != 0 ? 1 : 0 };
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
        float scale = SceneScale; // 2x surfaces, internal resolution (VulkanRenderer.ResScale.cs)
        if (scale != 1f) { vpRect.X *= scale; vpRect.Y *= scale; vpRect.Width *= scale; vpRect.Height *= scale; }
        if (!_dyn.Valid || !SameViewport(vpRect, _dyn.Vp)) { _cmd.CmdSetViewport(cmd, 0, 1, in vpRect); _dyn.Vp = vpRect; }

        Rect2D scissor;
        if (st.RegionClipMode == 2)
        {
            int x0 = Math.Clamp(st.ClipMinX, 0, (int)w), y0 = Math.Clamp(st.ClipMinY, 0, (int)h);
            int x1 = Math.Clamp(st.ClipMaxX + 1, x0, (int)w), y1 = Math.Clamp(st.ClipMaxY + 1, y0, (int)h);
            scissor = new Rect2D(new Offset2D(x0, y0), new Extent2D((uint)(x1 - x0), (uint)(y1 - y0)));
        }
        else scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h));
        if (scale == 2f) scissor = new Rect2D(new Offset2D(scissor.Offset.X * 2, scissor.Offset.Y * 2), new Extent2D(scissor.Extent.Width * 2, scissor.Extent.Height * 2));
        else if (scale != 1f) scissor = ScaleScissor(scissor, scale);
        if (!_dyn.Valid || !SameRect(scissor, _dyn.Scissor)) { _cmd.CmdSetScissor(cmd, 0, 1, in scissor); _dyn.Scissor = scissor; }
        _cmd.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &pc);

        // extended dynamic state, unless a driver quirk baked it into the pipeline (VulkanRenderer.StaticState.cs)
        bool staticEds = StaticEds;
        bool valid = _dyn.Valid;
        if (!staticEds)
        {
            var cull = st.CullMode switch { 1 => CullModeFlags.BackBit, 2 => CullModeFlags.FrontBit, _ => CullModeFlags.None };
            if (!valid || topology != _dyn.Topology) { _cmd.CmdSetPrimitiveTopology(cmd, topology); _dyn.Topology = topology; }
            if (!valid || cull != _dyn.Cull) { _cmd.CmdSetCullMode(cmd, cull); _dyn.Cull = cull; }
            if (!valid) _cmd.CmdSetFrontFace(cmd, FrontFace.CounterClockwise);
        }
        float lineWidth = _core.WideLines ? Math.Max(1f, Math.Max(1, (int)st.Front.PointLineWidth) * scale) : 1;
        if (!valid || lineWidth != _dyn.LineWidth) { _cmd.CmdSetLineWidth(cmd, lineWidth); _dyn.LineWidth = lineWidth; }

        var front = st.Front;
        var back = st.TwoSided ? st.Back : st.Front;
        bool depthTest = !(front.DepthFunc == 7 && !front.DepthWrite);
        if (!staticEds)
        {
            if (!valid || depthTest != _dyn.DepthTest) { _cmd.CmdSetDepthTestEnable(cmd, depthTest); _dyn.DepthTest = depthTest; }
            if (!valid || front.DepthWrite != _dyn.DepthWrite) { _cmd.CmdSetDepthWriteEnable(cmd, front.DepthWrite); _dyn.DepthWrite = front.DepthWrite; }
            if (!valid || front.DepthFunc != _dyn.DepthFunc) { _cmd.CmdSetDepthCompareOp(cmd, (CompareOp)front.DepthFunc); _dyn.DepthFunc = front.DepthFunc; }
        }

        bool stencil = !(IsTrivial(front.Stencil) && IsTrivial(back.Stencil));
        // VITASHARP_DEPTH_OPS_NONE: what the pending tiled renderings do with depth/stencil (a stencil test counts as a write)
        _chainDepthUse |= depthTest || stencil;
        _chainDepthWrite |= front.DepthWrite || back.DepthWrite || stencil;
        if (!staticEds && (!valid || stencil != _dyn.Stencil)) { _cmd.CmdSetStencilTestEnable(cmd, stencil); _dyn.Stencil = stencil; }
        if (stencil)
        {
            if (!valid || !_dyn.StencilSet || !SameStencil(front.Stencil, _dyn.Front)) { SetStencil(cmd, StencilFaceFlags.FaceFrontBit, front.Stencil, !staticEds); _dyn.Front = front.Stencil; }
            if (!valid || !_dyn.StencilSet || !SameStencil(back.Stencil, _dyn.Back)) { SetStencil(cmd, StencilFaceFlags.FaceBackBit, back.Stencil, !staticEds); _dyn.Back = back.Stencil; }
            _dyn.StencilSet = true;
        }
        _dyn.Valid = true;
        bool bias = front.DepthBiasFactor != 0 || front.DepthBiasUnits != 0;
        if (StaticBias)
        {
            // depth bias is enabled in every pipeline: no bias = zero constant and slope (exact)
            float bu = bias ? front.DepthBiasUnits : 0, bf = bias ? front.DepthBiasFactor : 0;
            if (!valid || !_dyn.BiasSet || bu != _dyn.BiasUnits || bf != _dyn.BiasFactor) { _cmd.CmdSetDepthBias(cmd, bu, 0, bf); _dyn.BiasUnits = bu; _dyn.BiasFactor = bf; _dyn.BiasSet = true; }
            return;
        }
        if (!valid || bias != _dyn.Bias) { _cmd.CmdSetDepthBiasEnable(cmd, bias); _dyn.Bias = bias; }
        if (bias && (!valid || !_dyn.BiasSet || front.DepthBiasUnits != _dyn.BiasUnits || front.DepthBiasFactor != _dyn.BiasFactor))
        {
            _cmd.CmdSetDepthBias(cmd, front.DepthBiasUnits, 0, front.DepthBiasFactor);
            _dyn.BiasUnits = front.DepthBiasUnits; _dyn.BiasFactor = front.DepthBiasFactor; _dyn.BiasSet = true;
        }
    }

    /// <summary>The dynamic state last set in <see cref="Cmd"/> by a draw (calls that would set the same value again
    /// are skipped: a draw set about 15 of them).</summary>
    struct DynShadow
    {
        public nint Cmd;
        public bool Valid, DepthTest, DepthWrite, Stencil, StencilSet, Bias, BiasSet, SetsBound;
        /// <summary>Descriptor sets 1-3 bound by the last draw.</summary>
        public SetHandles Sets;
        public Viewport Vp;
        public Rect2D Scissor;
        public PrimitiveTopology Topology;
        public CullModeFlags Cull;
        public float LineWidth, BiasUnits, BiasFactor;
        public byte DepthFunc;
        public StencilState Front, Back;
    }
    DynShadow _dyn;

    [System.Runtime.CompilerServices.InlineArray(4)]
    struct SetHandles { ulong _e; }

    static bool SameViewport(in Viewport a, in Viewport b) => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
    static bool SameRect(in Rect2D a, in Rect2D b) => a.Offset.X == b.Offset.X && a.Offset.Y == b.Offset.Y && a.Extent.Width == b.Extent.Width && a.Extent.Height == b.Extent.Height;
    static bool SameStencil(in StencilState a, in StencilState b) => a.Func == b.Func && a.StencilFail == b.StencilFail && a.DepthFail == b.DepthFail
        && a.DepthPass == b.DepthPass && a.CompareMask == b.CompareMask && a.WriteMask == b.WriteMask && a.Ref == b.Ref;

    /// <summary>VITASHARP_TRACE_UNIFORMS=&lt;dir&gt;: each traced draw's vertex and fragment uniform blocks as raw files.</summary>
    static readonly string? TraceUniformsDir = Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.TraceUniforms) is { Length: > 0 } tu ? tu : null;

    /// <summary>Stencil side for the draw trace: the function alone when the ops keep the buffer, else
    /// func.sfail.dfail.dpass:ref/compareMask/writeMask (SceGxmStencilOp: 0 keep, 1 zero, 2 replace, 3 incr, 4 decr, 5 invert, 6 incr wrap, 7 decr wrap).</summary>
    static string StencilText(in StencilState s) =>
        s.StencilFail == 0 && s.DepthFail == 0 && s.DepthPass == 0 && s.Func is 0 or 7 ? s.Func.ToString()
        : $"{s.Func}.{s.StencilFail}.{s.DepthFail}.{s.DepthPass}:{s.Ref:X}/{s.CompareMask:X}/{s.WriteMask:X}";

    void TraceDraw(DrawCommand d, VsEntry vs, FsEntry fs)
    {
        var st = d.State;
        var b = d.FragmentProgram.Blend;
        var sb = new System.Text.StringBuilder();
        sb.Append($"[draw] id={NextDrawId - 1} f={_presentCount} rt=0x{_rt?.Address ?? 0:X8}:{_rt?.ColorFormat ?? 0:X8} ds=0x{_depth?.Address ?? 0:X8}/{_depth?.Width ?? 0}x{_depth?.Height ?? 0} vs={d.VertexProgram.Info.Program.Hash:X16} fs={d.FragmentProgram.Info.Program.Hash:X16} prim={d.PrimitiveType} n={d.IndexCount}x{d.InstanceCount}");
        if (d.VisAddress != 0) sb.Append($" vis=0x{d.VisAddress:X8}{(d.VisIncrement ? "+" : "=")}");
        sb.Append($" blend={(b is { } bb ? $"{bb.ColorMask:X}:{bb.ColorFunc}/{bb.ColorSrc}/{bb.ColorDst}" : "none")} fragEn={st.Front.FragmentEnabled} cull={st.CullMode} twoSided={st.TwoSided} depth={st.Front.DepthFunc}/{st.Front.DepthWrite} bias={st.Front.DepthBiasFactor}/{st.Front.DepthBiasUnits} st={StencilText(st.Front.Stencil)}/{StencilText(st.Back.Stencil)} vp={st.ViewportEnabled}({st.VpOffX},{st.VpOffY},{st.VpScaleX},{st.VpScaleY},{st.VpOffZ},{st.VpScaleZ}) clip={st.RegionClipMode}[{st.ClipMinX},{st.ClipMinY}-{st.ClipMaxX},{st.ClipMaxY}]");
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
        foreach (var s in d.VertexProgram.Info.UniformSlots) sb.Append($" vub{s.GuestIndex}=0x{d.VertexUniformAddrs[Math.Min(s.GuestIndex, 14)]:X8}+{s.ByteSize}");
        byte[] vu = d.VertexUniformSpan.ToArray(), fu = d.FragmentUniformSpan.ToArray();
        if (TraceUniformsDir is { } tud)
        {
            Directory.CreateDirectory(tud);
            File.WriteAllBytes(Path.Combine(tud, $"f{_presentCount}_d{_drawSerial}_v.bin"), vu);
            File.WriteAllBytes(Path.Combine(tud, $"f{_presentCount}_d{_drawSerial}_f.bin"), fu);
            // the indices and each vertex stream up to the highest index: lets a bad vertex be traced on the CPU
            if (d.IndexAddress != 0 && d.IndexCount is > 0 and <= 1u << 20)
            {
                int isz = d.Index32 ? 4 : 2;
                var ix = GuestMemory.ReadSpan(d.IndexAddress, (int)d.IndexCount * isz).ToArray();
                File.WriteAllBytes(Path.Combine(tud, $"f{_presentCount}_d{_drawSerial}_ix{isz * 8}.bin"), ix);
                uint max = 0;
                for (int k = 0; k < (int)d.IndexCount; k++) max = Math.Max(max, d.Index32 ? BitConverter.ToUInt32(ix, k * 4) : BitConverter.ToUInt16(ix, k * 2));
                for (int si = 0; si < d.VertexProgram.Streams.Length && si < d.Streams.Length; si++)
                    if (d.Streams[si] != 0 && max < 1u << 20)
                        File.WriteAllBytes(Path.Combine(tud, $"f{_presentCount}_d{_drawSerial}_s{si}_stride{d.VertexProgram.Streams[si].Stride}.bin"),
                            GuestMemory.ReadSpan(d.Streams[si], (int)((max + 1) * d.VertexProgram.Streams[si].Stride)).ToArray());
            }
        }
        if (d.VertexUniformsLength > 0) sb.Append(" vuf=" + string.Join(",", Enumerable.Range(0, Math.Min(d.VertexUniformsLength / 4, 48)).Select(k => BitConverter.ToSingle(vu, k * 4).ToString("G4", System.Globalization.CultureInfo.InvariantCulture))));
        if (d.FragmentUniformsLength > 0) sb.Append(" fuh=" + string.Join(",", Enumerable.Range(0, Math.Min(d.FragmentUniformsLength / 2, 24)).Select(k => ((float)BitConverter.ToHalf(fu, k * 2)).ToString("G4", System.Globalization.CultureInfo.InvariantCulture))));
        if (d.FragmentUniformsLength >= 16) sb.Append($" fu0=({BitConverter.ToSingle(d.FragmentUniformSpan)},{BitConverter.ToSingle(d.FragmentUniformSpan[4..])},{BitConverter.ToSingle(d.FragmentUniformSpan[8..])},{BitConverter.ToSingle(d.FragmentUniformSpan[12..])})");
        sb.Append($" msaa={(_ms ? ((int)_msSamples).ToString() + "x" : _ss ? "ss2x" : "off")}");
        if (vs.Gen?.WritesMemory == true || fs.Gen?.WritesMemory == true)
        {
            // programs that store to memory (VLDST): the mapped slots they may write (VulkanRenderer.Mapped.cs)
            sb.Append(vs.Gen?.WritesMemory == true ? " vsStore" : " fsStore");
            var addrs = vs.Gen?.WritesMemory == true ? d.VertexUniformAddrs : d.FragmentUniformAddrs;
            for (int i = 0; i < addrs.Length; i++) if (addrs[i] != 0) sb.Append($" ub{i}@0x{addrs[i]:X8}");
        }
        if (_diagActive is { } capture)
        {
            // capture: the list goes to draws.txt, each program's GLSL once to shaders/ (shaders loaded from the
            // per-title pack carry no GLSL: translated again here, without compiling)
            capture.DrawCount++;
            TraceLine(sb.ToString());
            var dir = Path.Combine(capture.Dir, "shaders");
            Directory.CreateDirectory(dir);
            WriteTraceGlsl(capture, dir, d.VertexProgram.Info.Program, vs.HintKey, vs.Gen, () => VertexHints(d, VertexVaryingMask(d)));
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

    void SetStencil(CommandBuffer cmd, StencilFaceFlags face, in StencilState s, bool ops = true)
    {
        if (ops) _cmd.CmdSetStencilOp(cmd, face, (StencilOp)s.StencilFail, (StencilOp)s.DepthPass, (StencilOp)s.DepthFail, (CompareOp)s.Func);
        _cmd.CmdSetStencilCompareMask(cmd, face, s.CompareMask);
        _cmd.CmdSetStencilWriteMask(cmd, face, s.WriteMask);
        _cmd.CmdSetStencilReference(cmd, face, s.Ref);
    }
}
