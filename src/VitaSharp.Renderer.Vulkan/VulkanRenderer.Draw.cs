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
        public float ViewportFlag, ScreenWidth, ScreenHeight, ZOffset, ZScale, ResMultiplier;
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
        _fbLayout = TextureSetLayout(ShaderStageFlags.FragmentBit, 1);

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
        var sizes = stackalloc DescriptorPoolSize[2]; // outside the loop (CA2014)
        while (true)
        {
            if (f.DescPoolIndex >= f.DescPools.Count)
            {
                sizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 4 * 8192);
                sizes[1] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 65536);
                var pci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 8192, PoolSizeCount = 2, PPoolSizes = sizes };
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
    }

    sealed class FsEntry
    {
        public ShaderModule Module;
        public GeneratedShader? Gen;
        public bool Failed;
    }

    readonly Dictionary<(ulong, ulong), ShaderModule> _modules = new();
    readonly Dictionary<(GxmVertexProgram, ulong), VsEntry> _vsCache = new();
    readonly Dictionary<(GxmFragmentProgram, ulong), FsEntry> _fsCache = new();

    /// <summary>Same value as <see cref="ShaderHints.Key"/> for the hints a draw would produce, without allocating them.</summary>
    static ulong HintKey(GxmTextureDesc[] tex, uint used, GxmVertexAttribute[]? attrs, uint colorFormat)
    {
        ulong h = ShaderHints.Fnv.Offset;
        for (int i = 0; i < 16; i++) h = ShaderHints.Fnv.Mix(h, (used & (1u << i)) != 0 ? tex[i].Format : 0x0C000000);
        if (attrs != null)
            foreach (var a in attrs) { h = ShaderHints.Fnv.Mix(h, (uint)a.RegIndex); h = ShaderHints.Fnv.Mix(h, (uint)(int)a.ComponentCount); h = ShaderHints.Fnv.Mix(h, a.Format); }
        return ShaderHints.Fnv.Mix(h, colorFormat);
    }

    static uint[] TextureFormats(GxmTextureDesc[] tex, uint used)
    {
        var f = new uint[16];
        for (int i = 0; i < 16; i++) f[i] = (used & (1u << i)) != 0 ? tex[i].Format : 0x0C000000;
        return f;
    }

    (ShaderModule Module, GeneratedShader? Gen) GetModule(GxpProgram program, ShaderHints hints)
    {
        var key = (program.Hash, hints.Key());
        GeneratedShader? gen = null;
        if (_modules.TryGetValue(key, out var m)) return (m, null);
        try
        {
            gen = ShaderGenerator.Generate(program, hints);
            var spv = _compiler.Compile(gen.Glsl, gen.IsVertex, out var err);
            if (spv == null)
            {
                Log.Error($"Shader {program.Hash:X16} failed to compile: {err}");
                DumpShader(program, gen.Glsl + "\n/*\n" + err + "\n*/");
                _modules[key] = default;
                return (default, gen);
            }
            fixed (byte* p = spv)
            {
                var ci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spv.Length, PCode = (uint*)p };
                _vk.CreateShaderModule(_dev, in ci, null, out m).Ok("shader module");
            }
            if (gen.Warnings.Count > 0) Log.Debug($"Shader {program.Hash:X16}: {string.Join("; ", gen.Warnings)}");
        }
        catch (Exception e)
        {
            Log.Error($"Shader {program.Hash:X16} translation failed: {e}");
            m = default;
        }
        _modules[key] = m;
        return (m, gen);
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
        var hints = new ShaderHints(TextureFormats(d.VertexTextures, vp.Info.TexturesUsed),
            vp.Attributes.Select(a => ((uint)a.RegIndex, (int)a.ComponentCount, a.Format)).ToArray(), 0);
        e = new VsEntry();
        var (module, gen) = GetModule(vp.Info.Program, hints);
        gen ??= ShaderGenerator.Generate(vp.Info.Program, hints); // module was cached: regenerate metadata only
        e.Module = module;
        e.Gen = gen;
        e.Failed = module.Handle == 0;
        BuildVertexInput(vp, gen, e);
        _vsCache[(vp, hk)] = e;
        return e;
    }

    FsEntry GetFragmentShader(DrawCommand d)
    {
        var fp = d.FragmentProgram;
        uint colorFormat = _rt?.ColorFormat ?? 0;
        ulong hk = HintKey(d.FragmentTextures, fp.Info.TexturesUsed, null, colorFormat);
        if (_fsCache.TryGetValue((fp, hk), out var e)) return e;
        var hints = new ShaderHints(TextureFormats(d.FragmentTextures, fp.Info.TexturesUsed), [], colorFormat);
        e = new FsEntry();
        var (module, gen) = GetModule(fp.Info.Program, hints);
        gen ??= ShaderGenerator.Generate(fp.Info.Program, hints);
        e.Module = module;
        e.Gen = gen;
        e.Failed = module.Handle == 0;
        _fsCache[(fp, hk)] = e;
        return e;
    }

    void BuildVertexInput(GxmVertexProgram vp, GeneratedShader gen, VsEntry e)
    {
        var bindings = new List<VertexInputBindingDescription>();
        var attrs = new List<VertexInputAttributeDescription>();
        var hash = new System.IO.Hashing.XxHash64();
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
        foreach (var b in bindings) { hash.Append(BitConverter.GetBytes(b.Binding)); hash.Append(BitConverter.GetBytes(b.Stride)); hash.Append(BitConverter.GetBytes((int)b.InputRate)); }
        foreach (var at in attrs) { hash.Append(BitConverter.GetBytes(at.Location)); hash.Append(BitConverter.GetBytes(at.Binding)); hash.Append(BitConverter.GetBytes((int)at.Format)); hash.Append(BitConverter.GetBytes(at.Offset)); }
        e.Bindings = bindings.ToArray();
        e.Attributes = attrs.ToArray();
        e.LayoutHash = hash.GetCurrentHashAsUInt64();
        e.StreamMask = streamMask;
    }

    // =================================================================== pipelines

    readonly record struct PipelineKey(ulong Vs, ulong Fs, ulong VertexLayout, Format ColorFormat, bool HasColor,
        bool Blend, byte ColorFunc, byte AlphaFunc, byte ColorSrc, byte ColorDst, byte AlphaSrc, byte AlphaDst, byte Mask,
        int TopologyClass, PolygonMode Polygon);

    readonly Dictionary<PipelineKey, Pipeline> _pipelines = new();
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
        var entry = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vs.Module, PName = entry };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fs.Module, PName = entry };

        fixed (VertexInputBindingDescription* pb = vs.Bindings)
        fixed (VertexInputAttributeDescription* pa = vs.Attributes)
        {
            var vi = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)vs.Bindings.Length,
                PVertexBindingDescriptions = pb,
                VertexAttributeDescriptionCount = (uint)vs.Attributes.Length,
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
            };
            var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
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
            var gci = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
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
                Log.Error($"vkCreateGraphicsPipelines failed: {r}");
                p = default;
            }
        }
        _pipelines[key] = p;
        return p;
    }

    void DestroyPipelines()
    {
        foreach (var p in _pipelines.Values) if (p.Handle != 0) _vk.DestroyPipeline(_dev, p, null);
        foreach (var m in _modules.Values) if (m.Handle != 0) _vk.DestroyShaderModule(_dev, m, null);
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
            var s = GuestMemory.Span<uint>(addr, (int)count);
            foreach (var v in s) if (v > max) max = v;
        }
        else
        {
            var s = GuestMemory.Span<ushort>(addr, (int)count);
            foreach (var v in s) if (v > max) max = v;
        }
        return max;
    }

    void Draw(DrawCommand d)
    {
        if (!_inScene || d.IndexCount == 0) return;
        if (d.FragmentProgram.IsMaskUpdate || d.FragmentProgram.Info == null) return;
        var st = d.State;
        if (st.RegionClipMode == 1) return; // clip everything
        BeginFrameIfNeeded();

        var vs = GetVertexShader(d);
        var fs = GetFragmentShader(d);
        if (_opt.TraceDrawsFrame == _presentCount) TraceDraw(d, vs, fs);
        if (vs.Failed || fs.Failed) return;

        // ---- textures (may need to leave the render pass)
        var vSet = BuildTextureSet(_texLayoutV, d.VertexTextures, d.VertexProgram.Info.TexturesUsed);
        var fSet = BuildTextureSet(_texLayoutF, d.FragmentTextures, d.FragmentProgram.Info.TexturesUsed);
        var fbSet = fs.Gen!.UsesFragColor ? BuildFramebufferFetchSet() : _emptyFbSet ??= BuildEmptyFbSet();

        // ---- pipeline
        var topology = Formats.Topology(d.PrimitiveType);
        bool hasColor = _rt != null;
        var blend = d.FragmentProgram.Blend;
        bool fragEnabled = st.Front.FragmentEnabled;
        byte mask = !fragEnabled ? (byte)0 : blend?.ColorMask ?? 0xF;
        // a fragment program that reads the current framebuffer colour carries its blend in the shader itself (the
        // shader patcher compiles the blend info into USSE code): the fixed-function blend must not be applied on top
        bool blendOn = blend is { } bi && !fs.Gen!.UsesFragColor && !(bi.ColorFunc == 0 && bi.AlphaFunc == 0) && !(bi.ColorSrc == 1 && bi.ColorDst == 0 && bi.AlphaSrc == 1 && bi.AlphaDst == 0);
        var key = new PipelineKey(vs.Module.Handle, fs.Module.Handle, vs.LayoutHash, _rtAttachFormat, hasColor,
            blendOn, blend?.ColorFunc ?? 0, blend?.AlphaFunc ?? 0, blend?.ColorSrc ?? 1, blend?.ColorDst ?? 0, blend?.AlphaSrc ?? 1, blend?.AlphaDst ?? 0,
            mask, TopologyClass(topology), Formats.Polygon(st.Front.PolygonMode));
        var pipeline = GetPipeline(key, vs, fs, topology);
        if (pipeline.Handle == 0) return;

        // ---- geometry upload
        int indexSize = d.Index32 ? 4 : 2;
        uint maxIndex = MaxIndex(d.IndexAddress, d.IndexCount, d.Index32);
        var (ib, ibOff) = UploadSpan(GuestMemory.Span(d.IndexAddress, (int)(d.IndexCount * indexSize)), 4);
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
            var (b, off) = UploadSpan(GuestMemory.Span(addr, (int)length), 4);
            vbufs[s] = b;
            voffs[s] = off;
        }

        // ---- uniforms
        var bufInfos = stackalloc DescriptorBufferInfo[4];
        bufInfos[2] = ResolveMappedSlots(d.VertexProgram.Info.UniformSlots, d.VertexUniforms, d.VertexUniformsLength, d.VertexUniformAddrs);
        bufInfos[3] = ResolveMappedSlots(d.FragmentProgram.Info.UniformSlots, d.FragmentUniforms, d.FragmentUniformsLength, d.FragmentUniformAddrs);
        var uSet = AllocSet(_uboLayout);
        bufInfos[0] = UniformInfo(d.VertexUniformSpan);
        bufInfos[1] = UniformInfo(d.FragmentUniformSpan);
        var writes = stackalloc WriteDescriptorSet[4];
        for (int i = 0; i < 4; i++)
            writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = uSet, DstBinding = (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufInfos[i] };
        _vk.UpdateDescriptorSets(_dev, 4, writes, 0, null);

        // ---- record
        BeginRendering();
        var cmd = Cmd;
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
        var pc = new PushConstants { ScreenWidth = w, ScreenHeight = h, ResMultiplier = 1, ZOffset = 0, ZScale = 1 };
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
        _vk.CmdSetViewport(cmd, 0, 1, in vpRect);

        Rect2D scissor;
        if (st.RegionClipMode == 2)
        {
            int x0 = Math.Clamp(st.ClipMinX, 0, (int)w), y0 = Math.Clamp(st.ClipMinY, 0, (int)h);
            int x1 = Math.Clamp(st.ClipMaxX + 1, x0, (int)w), y1 = Math.Clamp(st.ClipMaxY + 1, y0, (int)h);
            scissor = new Rect2D(new Offset2D(x0, y0), new Extent2D((uint)(x1 - x0), (uint)(y1 - y0)));
        }
        else scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h));
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);
        _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &pc);

        _vk.CmdSetPrimitiveTopology(cmd, topology);
        _vk.CmdSetCullMode(cmd, st.CullMode switch { 1 => CullModeFlags.BackBit, 2 => CullModeFlags.FrontBit, _ => CullModeFlags.None });
        _vk.CmdSetFrontFace(cmd, FrontFace.CounterClockwise);
        _vk.CmdSetLineWidth(cmd, _core.WideLines ? Math.Max(1, (int)st.Front.PointLineWidth) : 1);

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
        sb.Append($"[draw] rt=0x{_rt?.Address ?? 0:X8} vs={d.VertexProgram.Info.Program.Hash:X16} fs={d.FragmentProgram.Info.Program.Hash:X16} prim={d.PrimitiveType} n={d.IndexCount}x{d.InstanceCount}");
        sb.Append($" blend={(b is { } bb ? $"{bb.ColorMask:X}:{bb.ColorFunc}/{bb.ColorSrc}/{bb.ColorDst}" : "none")} fragEn={st.Front.FragmentEnabled} depth={st.Front.DepthFunc}/{st.Front.DepthWrite} vp={st.ViewportEnabled}({st.VpOffX},{st.VpOffY},{st.VpScaleX},{st.VpScaleY},{st.VpOffZ},{st.VpScaleZ}) clip={st.RegionClipMode}[{st.ClipMinX},{st.ClipMinY}-{st.ClipMaxX},{st.ClipMaxY}]");
        for (int i = 0; i < 16; i++)
            if ((d.FragmentProgram.Info.TexturesUsed & (1u << i)) != 0)
                sb.Append($" t{i}=0x{d.FragmentTextures[i].DataAddress:X8}:{d.FragmentTextures[i].Format:X8}:{d.FragmentTextures[i].Width}x{d.FragmentTextures[i].Height}:T{d.FragmentTextures[i].TextureType >> 29}:S{d.FragmentTextures[i].StrideBytes}:G{d.FragmentTextures[i].GammaMode}");
        foreach (var a in d.VertexProgram.Attributes) sb.Append($" a[r{a.RegIndex} s{a.StreamIndex}+{a.Offset} f{a.Format}x{a.ComponentCount}]");
        foreach (var s in d.VertexProgram.Streams) sb.Append($" st[{s.Stride}/{s.IndexSource}]");
        // first two vertices of stream 0 as raw bytes: lets attribute formats/offsets be checked against real data
        if (d.VertexProgram.Streams.Length > 0 && d.Streams[0] != 0)
        {
            int stride = Math.Clamp((int)d.VertexProgram.Streams[0].Stride, 4, 64);
            sb.Append($" v0@0x{d.Streams[0]:X8}=").Append(Convert.ToHexString(GuestMemory.Span(d.Streams[0], stride)));
            sb.Append(" v1=").Append(Convert.ToHexString(GuestMemory.Span(d.Streams[0] + (uint)stride, stride)));
        }
        sb.Append($" vu={d.VertexUniformsLength} fu={d.FragmentUniformsLength}");
        foreach (var s in d.FragmentProgram.Info.UniformSlots) sb.Append($" fub{s.GuestIndex}=0x{d.FragmentUniformAddrs[Math.Min(s.GuestIndex, 14)]:X8}+{s.ByteSize}");
        if (d.VertexUniformsLength > 0) sb.Append(" vuf=" + string.Join(",", Enumerable.Range(0, Math.Min(d.VertexUniformsLength / 4, 28)).Select(k => BitConverter.ToSingle(d.VertexUniforms, k * 4).ToString("G4", System.Globalization.CultureInfo.InvariantCulture))));
        if (d.FragmentUniformsLength > 0) sb.Append(" fuh=" + string.Join(",", Enumerable.Range(0, Math.Min(d.FragmentUniformsLength / 2, 24)).Select(k => ((float)BitConverter.ToHalf(d.FragmentUniforms, k * 2)).ToString("G4", System.Globalization.CultureInfo.InvariantCulture))));
        if (d.FragmentUniformsLength >= 16) sb.Append($" fu0=({BitConverter.ToSingle(d.FragmentUniforms, 0)},{BitConverter.ToSingle(d.FragmentUniforms, 4)},{BitConverter.ToSingle(d.FragmentUniforms, 8)},{BitConverter.ToSingle(d.FragmentUniforms, 12)})");
        Log.Info(sb.ToString());
        if (_opt.ShaderCacheDir != null)
        {
            var dir = Path.Combine(_opt.ShaderCacheDir, "trace");
            Directory.CreateDirectory(dir);
            if (vs.Gen != null) File.WriteAllText(Path.Combine(dir, $"{d.VertexProgram.Info.Program.Hash:x16}.vert"), vs.Gen.Glsl);
            if (fs.Gen != null) File.WriteAllText(Path.Combine(dir, $"{d.FragmentProgram.Info.Program.Hash:x16}.frag"), fs.Gen.Glsl);
        }
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
