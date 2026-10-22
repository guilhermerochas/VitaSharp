using System.Text;

namespace VitaSharp.Core.Gxm.Shader;

/// <summary>Information the shader translation depends on besides the program itself (part of the cache key).</summary>
/// <param name="FetchInputAttachment">programmable blending reads the colour attachment as a multisampled input
/// attachment (set 3 binding 2) instead of a copy of the target</param>
public sealed record ShaderHints(uint[] TextureFormats, (uint RegIndex, int ComponentCount, byte Format)[] Attributes, uint ColorFormat,
    bool FetchInputAttachment = false)
{
    /// <summary>Mixed into the key of <see cref="FetchInputAttachment"/> variants (other keys are unchanged).</summary>
    public const uint FetchInputKey = 0x46494E50;

    /// <summary>Allocation-free FNV-1a key; <see cref="KeyOf"/> computes the same value straight from draw state.</summary>
    public ulong Key()
    {
        ulong h = Fnv.Offset;
        foreach (var t in TextureFormats) h = Fnv.Mix(h, t);
        foreach (var a in Attributes) { h = Fnv.Mix(h, a.RegIndex); h = Fnv.Mix(h, (uint)a.ComponentCount); h = Fnv.Mix(h, a.Format); }
        h = Fnv.Mix(h, ColorFormat);
        return FetchInputAttachment ? Fnv.Mix(h, FetchInputKey) : h;
    }

    public static class Fnv
    {
        public const ulong Offset = 14695981039346656037, Prime = 1099511628211;
        public static ulong Mix(ulong h, uint v) => (h ^ v) * Prime;
    }
}

/// <summary>A vertex shader input: <see cref="RegIndex"/> is the SceGxmVertexAttribute regIndex it is fed from.</summary>
/// <param name="RawSize">1/2 = register-format attribute fetched as raw U8/U16 integers (0 = normal fetch)</param>
/// <param name="RawComponents">total raw components (may span several locations)</param>
public sealed record VertexInputInfo(uint RegIndex, int Location, char Kind, int Components, int LocationCount, int RawSize = 0, int RawComponents = 0);

/// <summary>Placement of a guest uniform buffer inside the per-stage storage buffer.</summary>
/// <remarks>
/// A <see cref="Mapped"/> slot is too large to copy per draw (games point such buffers at whole render targets and
/// read pixels with memory loads). It occupies one 16-byte header in the uniform data whose first word the renderer
/// fills with <see cref="ShaderGenerator.MappedFlag"/> | offset of the data in the stage's mapped-memory buffer.
/// </remarks>
public sealed record UniformBufferSlot(uint GuestIndex, int ByteOffset, int ByteSize, bool Mapped = false, int DeclaredBytes = 0);

public sealed class GeneratedShader
{
    /// <summary>Attributes were declared from the draw's vertex format (the program has no attribute parameters).</summary>
    public bool AttributeFallback { get; set; }
    public required string Glsl { get; init; }
    public required bool IsVertex { get; init; }
    public List<VertexInputInfo> VertexInputs { get; } = new();
    public List<UniformBufferSlot> UniformBuffers { get; } = new();
    public int UniformBytes { get; set; }
    /// <summary>Texture units read (binding = unit).</summary>
    public List<(uint Unit, bool IsCube)> Textures { get; } = new();
    public bool UsesFragColor { get; set; }
    public bool WritesDepth { get; set; }
    /// <summary>
    /// Non-zero when the fragment program writes raw register bits (see <see cref="ShaderGenerator.RawOutputBits"/>):
    /// the colour output is a uvec4 of unsigned components of this many bits, laid out like the surface's own
    /// components, and must be rendered through an integer view of the surface so no float conversion touches it.
    /// </summary>
    public int RawColorBits { get; set; }
    public HashSet<string> Warnings { get; set; } = new();
}

/// <summary>
/// Builds complete Vulkan GLSL for a GXP program. Descriptor layout:
/// set 0 binding 0 = vertex uniform SSBO, binding 1 = fragment uniform SSBO;
/// set 1 = vertex textures (binding = texture unit); set 2 = fragment textures; set 3 = framebuffer fetch (binding 0 =
/// single-sampled copy of the target, 1 = multisampled copy, 2 = multisampled colour input attachment).
/// The render state lives in a push-constant block shared by both stages.
/// </summary>
public static class ShaderGenerator
{
    public const int DefaultUniformHostIndex = 0;

    /// <summary>
    /// Version of the translator output. Persistent shader caches are keyed by it: bump it whenever a change to the
    /// USSE translation or to this generator alters the GLSL (or its metadata) produced for an existing program.
    /// </summary>
    public const int GeneratorVersion = 7;

    /// <summary>
    /// Programmable blending in a multisampled scene reads the sample the invocation shades (gl_SampleID, which makes
    /// such shaders run per sample, like GXM's NATIVECOLOR_MSAA programs). Set to false by a renderer whose device
    /// lacks sampleRateShading: the shaders then read sample 0. Part of the shader cache key.
    /// </summary>
    public static bool PerSampleFramebufferFetch = true;

    /// <summary>
    /// A debug environment variable (VITASHARP_DEBUG_TEX0 / VITASHARP_DEBUG_FS) changes the generated code: the
    /// output must not be read from or written to persistent shader caches.
    /// </summary>
    public static bool DebugOverridesActive => DebugTex0 || DebugId || DebugHash || DebugFs != null || ProgramInput.UnboundedUbsActive;

    const string PushConstants = """
layout(push_constant) uniform RenderInfo {
    float viewport_flag;
    float screen_width;
    float screen_height;
    float z_offset;
    float z_scale;
    float res_multiplier;
    float draw_id;
    float fb_samples;
} rinfo;
""";

    const string Helpers = """
vec3 unpackC10(float f) {
    int v = floatBitsToInt(f);
    return vec3(ivec3(bitfieldExtract(v, 0, 10), bitfieldExtract(v, 10, 10), bitfieldExtract(v, 20, 10))) / 255.0;
}
float packC10(vec3 c) {
    ivec3 i = ivec3(round(clamp(c, vec3(-2.0), vec3(2.0)) * 255.0));
    uint r = (uint(i.x) & 0x3FFu) | ((uint(i.y) & 0x3FFu) << 10) | ((uint(i.z) & 0x3FFu) << 20);
    return uintBitsToFloat(r);
}
""";

    static int HostIndex(uint guestIndex) => guestIndex < 14 ? (int)guestIndex + 1 : DefaultUniformHostIndex;

    /// <summary>Where each guest uniform buffer lives inside the stage's storage buffer (independent of hints).</summary>
    /// <summary>Uniform buffers larger than this are read in place (see <see cref="UniformBufferSlot.Mapped"/>).</summary>
    public const int MappedSlotThreshold = 64 * 1024;
    /// <summary>Address bit that routes a memory load to the mapped-memory buffer instead of the uniform data.</summary>
    public const uint MappedFlag = 0x40000000;

    public static (List<UniformBufferSlot> Slots, int Bytes) UniformLayout(GxpProgram program) => UniformLayout(ProgramInput.From(program));

    static (List<UniformBufferSlot> Slots, int Bytes) UniformLayout(ProgramInput input)
    {
        var sizes = new SortedDictionary<int, (uint Size, uint Guest)>();
        foreach (var b in input.UniformBuffers)
        {
            if (b.Index >= 15) continue;
            sizes[HostIndex(b.Index)] = ((b.Size + 3) / 4, b.Index);
        }
        var slots = new List<UniformBufferSlot>();
        int lastBase = 0;
        foreach (var (_, (sz, guest)) in sizes)
        {
            long bytes = (long)sz * 16;
            if (bytes > MappedSlotThreshold)
            {
                slots.Add(new UniformBufferSlot(guest, lastBase, 16, true, (int)Math.Min(bytes, int.MaxValue)));
                lastBase += 16;
                continue;
            }
            slots.Add(new UniformBufferSlot(guest, lastBase, (int)bytes));
            lastBase += (int)bytes;
        }
        return (slots, lastBase);
    }

    public static GeneratedShader Generate(GxpProgram program, ShaderHints hints)
    {
        bool vertex = program.IsVertex;
        var input = ProgramInput.From(program);
        var t = new UsseTranslator(program);
        var result = new GeneratedShader { Glsl = "", IsVertex = vertex };
        var decl = new StringBuilder();
        string stagePrefix = vertex ? "vtex" : "ftex";
        int texSet = vertex ? 1 : 2;

        // ---- uniform buffers packed into one SSBO
        var (slots, lastBase) = UniformLayout(input);
        var bufferBase = slots.ToDictionary(s => HostIndex(s.GuestIndex), s => (Base: s.ByteOffset, Guest: s.GuestIndex, s.Mapped));
        result.UniformBuffers.AddRange(slots);
        result.UniformBytes = lastBase;
        bool hasUb = lastBase > 0;
        bool hasMapped = slots.Any(s => s.Mapped);
        if (hasUb)
        {
            decl.AppendLine($"layout(std430, set = 0, binding = {(vertex ? 0 : 1)}) readonly restrict buffer UniformData {{ uint ub[]; }};");
            if (hasMapped)
            {
                decl.AppendLine($"layout(std430, set = 0, binding = {(vertex ? 2 : 3)}) readonly restrict buffer MappedData {{ uint mem[]; }};");
                decl.AppendLine($"float fetchMemory(int addr) {{ if ((addr & 0x{MappedFlag:X}) != 0) {{ uint j = uint(addr & 0x{MappedFlag - 1:X}) >> 2; " +
                                "return j < uint(mem.length()) ? uintBitsToFloat(mem[j]) : 0.0; } " +
                                "uint i = uint(addr) >> 2; return i < uint(ub.length()) ? uintBitsToFloat(ub[i]) : 0.0; }");
            }
            else decl.AppendLine("float fetchMemory(int addr) { uint i = uint(addr) >> 2; return i < uint(ub.length()) ? uintBitsToFloat(ub[i]) : 0.0; }");
        }
        else decl.AppendLine("float fetchMemory(int addr) { return 0.0; }");

        // ---- samplers
        var samplerVars = new Dictionary<uint, UsseTranslator.SamplerUse>();
        UsseTranslator.SamplerUse SamplerFor(uint unit, bool cube)
        {
            if (samplerVars.TryGetValue(unit, out var s)) return s;
            uint fmt = unit < hints.TextureFormats.Length ? hints.TextureFormats[unit] : 0x0C000000;
            s = new UsseTranslator.SamplerUse($"{stagePrefix}{unit}", unit, cube, GxmFormats.TextureComponentType(fmt), GxmFormats.TextureComponentCount(fmt));
            samplerVars[unit] = s;
            result.Textures.Add((unit, cube));
            return s;
        }
        foreach (var s in input.Samplers) SamplerFor(s.Index, s.IsCube);

        if (program.ThreadBufferCount > 0)
        {
            uint n = Math.Max(1, program.ThreadBufferCount / (4 * 4 * 4));
            t.Line($"// thread buffer ({n} floats)");
            decl.AppendLine($"float thread_buffer[{n}];");
        }

        // ---- copy register-resident uniform blocks into SA
        foreach (var b in input.UniformBuffers)
        {
            if (b.Index >= 15 || b.RegBlockSize == 0) continue;
            int host = HostIndex(b.Index);
            if (!bufferBase.TryGetValue(host, out var bb)) continue;
            int count = (int)Math.Min(b.RegBlockSize, UsseTranslator.RegSaCount - b.RegStartOffset);
            if (count <= 0) continue;
            int start = (int)b.RegStartOffset;
            t.MaxSa = Math.Max(t.MaxSa, (start + count + 3) >> 2);
            t.Line($"for (int k = 0; k < {count}; k++) {{ int w = {start} + k; sa[w >> 2][w & 3] = fetchMemory({bb.Base} + k * 4); }}");
        }

        // ---- program inputs
        int inAllocated = 0;
        var literals = new List<(uint Offset, float Value)>();
        foreach (var inp in input.Inputs)
        {
            switch (inp.Source)
            {
                case LiteralInputSource l:
                    literals.Add((inp.Offset, l.Data));
                    break;
                case UniformBufferInputSource u:
                    if (u.Index == ProgramInput.TextureBuffer) { t.TextureBufferSaOffset = (int)inp.Offset; t.TextureBufferBase = (int)u.Base; }
                    else if (u.Index == ProgramInput.LiteralBuffer) { t.LiteralBufferSaOffset = (int)inp.Offset; t.LiteralBufferBase = (int)u.Base; }
                    else if (u.Index == ProgramInput.ThreadBuffer) { t.ThreadBufferSaOffset = (int)inp.Offset; t.ThreadBufferBase = (int)u.Base; }
                    else
                    {
                        int host = HostIndex(u.Index);
                        bool found = bufferBase.TryGetValue(host, out var bb);
                        var reg = Operand.Make(3); reg.Bank = Bank.SecAttr; reg.Num = (int)inp.Offset; reg.Type = DataType.Int32;
                        if (found && bb.Mapped)
                        {
                            // runtime base (flag | offset in the mapped-memory buffer) written into the slot header by the renderer
                            t.Store(reg, new Val($"(int(ub[{bb.Base / 4}]) + {(int)u.Base})", 'i', 1), 1);
                        }
                        else
                        {
                            int baseAddr = (found ? bb.Base : 0) + (int)u.Base;
                            t.Store(reg, new Val(baseAddr.ToString(), 'i', 1), 1);
                        }
                    }
                    break;
                case DependentSamplerInputSource ds:
                {
                    var s = SamplerFor(ds.Index, input.Samplers.FirstOrDefault(x => x.Index == ds.Index)?.IsCube ?? false);
                    t.Samplers[(int)inp.Offset] = s;
                    break;
                }
                case AttributeInputSource a when vertex:
                {
                    var reg = Operand.Make(3); reg.Bank = Bank.PrimAttr; reg.Num = (int)inp.Offset; reg.Type = inp.Type;
                    if (a.Semantic == 15 || a.Semantic == 16)
                    {
                        reg.Type = DataType.UInt32;
                        t.Store(reg, new Val(a.Semantic == 15 ? "uint(gl_VertexIndex)" : "uint(gl_InstanceIndex)", 'u', 1), 1);
                        break;
                    }
                    int location = inAllocated / 4;
                    int n = (int)(inp.ArraySize * inp.ComponentCount);
                    if (a.RegFormat)
                    {
                        // register-format attribute: the hardware copies the raw stream bytes into the PA registers
                        // (one byte / half per component, packed), whatever the stream's declared format
                        int ts = GxpProgram.TypeSize(inp.Type);
                        int comps = inp.Type == DataType.C10 ? (n * 10 + 7) / 8 : Math.Max(n, 1);
                        if (ts >= 4) EmitUntypedAttribute(t, decl, result, reg, a.Index, location, comps, 4);
                        else EmitRawAttribute(t, decl, result, reg, a.Index, location, comps, ts);
                        inAllocated += (comps + 3) / 4 * 4;
                        break;
                    }
                    var hint = hints.Attributes.FirstOrDefault(h => h.RegIndex == inp.Offset);
                    if (hint.Format == AttributeFormatUntyped && hint.ComponentCount > 0)
                    {
                        EmitUntypedAttribute(t, decl, result, reg, a.Index, location, hint.ComponentCount);
                        inAllocated += (hint.ComponentCount + 3) / 4 * 4;
                        break;
                    }
                    EmitAttribute(t, decl, result, reg, a.Index, location, n, hintCount: hint.ComponentCount);
                    inAllocated += (n + 3) / 4 * 4;
                    break;
                }
            }
        }

        if (vertex && inAllocated == 0 && program.PrimaryRegCount != 0)
        {
            result.AttributeFallback = true;
            for (int i = 0; i < hints.Attributes.Length; i++)
            {
                var (regIndex, cc, _) = hints.Attributes[i];
                var reg = Operand.Make(3); reg.Bank = Bank.PrimAttr; reg.Num = (int)regIndex;
                reg.Type = DataType.F32;
                int arr = Math.Max(1, (cc + 3) >> 2);
                EmitAttribute(t, decl, result, reg, regIndex, i, arr * 4, forceArray: arr > 1);
            }
        }

        foreach (var (off, val) in literals)
        {
            var reg = Operand.Make(3); reg.Bank = Bank.SecAttr; reg.Num = (int)off; reg.Type = DataType.F32;
            t.Store(reg, new Val(UsseTranslator.F(val), 'f', 1), 1);
        }

        if (!vertex) EmitFragmentInputs(program, hints, t, decl, result, SamplerFor);

        // ---- the programs: secondary (sample-rate) phase first, then primary
        if (vertex) t.Line("for (int k = 0; k < o.length(); k++) o[k] = vec4(0.0);");
        if (program.SecondaryInstrCount > 0)
            t.TranslateProgram(ReadCode(program, program.SecondaryProgramStart, program.SecondaryInstrCount), true);
        if (program.PrimaryInstrCount > 0)
            t.TranslateProgram(ReadCode(program, program.PrimaryProgramStart, (int)program.PrimaryInstrCount), false);

        if (vertex) EmitVertexFinalize(program, t, decl);
        else EmitFragmentFinalize(program, hints, t, decl, result);

        result.WritesDepth = t.UsesFragDepth;

        // ---- assemble
        var sb = new StringBuilder();
        sb.AppendLine("#version 450");
        sb.AppendLine($"// gxp {program.Hash:X16} ({(vertex ? "vertex" : "fragment")}) vo1 0x{program.VertexOutputs1:X8} vo2 0x{program.VertexOutputs2:X8}" +
                      (vertex ? "" : $" out {program.OutputParamType}x{program.OutputCompCount} surface 0x{hints.ColorFormat:X8}"));
        if (result.UniformBuffers.Count > 0)
            sb.AppendLine("// ub " + string.Join(" ", result.UniformBuffers.Select(s => $"[{s.GuestIndex}]@{s.ByteOffset}+{s.ByteSize}{(s.Mapped ? "M" : "")}")));
        sb.AppendLine(PushConstants);
        foreach (var s in samplerVars.Values)
            sb.AppendLine($"layout(set = {texSet}, binding = {s.Index}) uniform {(s.IsCube ? "samplerCube" : "sampler2D")} {s.GlslName};");
        sb.Append(decl);
        int Size(int max, int min) => Math.Max(Math.Min(max + 1, 64), min);
        sb.AppendLine($"vec4 pa[{Size(t.MaxPa, 8)}];");
        sb.AppendLine($"vec4 sa[{Size(t.MaxSa, 8)}];");
        sb.AppendLine($"vec4 r[{Size(t.MaxTemp, 8)}];");
        sb.AppendLine($"vec4 o[{Size(t.MaxOut, vertex ? 16 : 8)}];");
        sb.AppendLine("vec4 fi[8];");
        sb.AppendLine("bool p[4];");
        sb.AppendLine("int idx[2];");
        sb.AppendLine(Helpers);
        sb.AppendLine("void main() {");
        sb.Append(t.Body);
        sb.AppendLine("}");
        foreach (var w in t.Warnings) result.Warnings.Add(w);
        return new GeneratedShader
        {
            Glsl = sb.ToString(),
            IsVertex = vertex,
            UniformBytes = result.UniformBytes,
            UsesFragColor = result.UsesFragColor,
            WritesDepth = result.WritesDepth,
            RawColorBits = result.RawColorBits,
            Warnings = result.Warnings,
        }.CopyLists(result);
    }

    static GeneratedShader CopyLists(this GeneratedShader dst, GeneratedShader src)
    {
        dst.VertexInputs.AddRange(src.VertexInputs);
        dst.UniformBuffers.AddRange(src.UniformBuffers);
        dst.Textures.AddRange(src.Textures);
        return dst;
    }

    static ulong[] ReadCode(GxpProgram p, int start, int count)
    {
        var code = new ulong[Math.Max(0, count)];
        for (int i = 0; i < code.Length; i++) code[i] = p.ReadInstruction(start, i);
        return code;
    }

    const byte AttributeFormatUntyped = 10; // SCE_GXM_ATTRIBUTE_FORMAT_UNTYPED

    /// <summary>
    /// UNTYPED attributes are copied into the parameter's registers as raw 32-bit words without conversion; the
    /// program then reads them with its own type (packed u8/s8/f16 ...). They are fetched as R32_UINT words.
    /// </summary>
    static void EmitUntypedAttribute(UsseTranslator t, StringBuilder decl, GeneratedShader result, Operand reg, uint regIndex, int location, int words, int rawSize = 0)
    {
        var raw = reg;
        raw.Type = DataType.UInt32;
        string name = $"in_attr{location}";
        int k = (words + 3) / 4;
        if (k == 1)
        {
            decl.AppendLine($"layout(location = {location}) in {(words == 1 ? "uint" : $"uvec{words}")} {name};");
            t.Store(raw, new Val(name, 'u', words), (1 << words) - 1);
        }
        else
        {
            decl.AppendLine($"layout(location = {location}) in uvec4 {name}[{k}];");
            for (int i = 0; i < k; i++) t.Store(raw, new Val($"{name}[{i}]", 'u', 4), 0b1111, i * 4);
        }
        result.VertexInputs.Add(new VertexInputInfo(regIndex, location, 'u', Math.Min(words, 4), k, rawSize, rawSize > 0 ? words : 0));
    }

    /// <summary>Raw 8/16-bit register-format attribute: fetched as unsigned integers and packed into 32-bit PA registers.</summary>
    static void EmitRawAttribute(UsseTranslator t, StringBuilder decl, GeneratedShader result, Operand reg, uint regIndex, int location, int comps, int typeSize)
    {
        var raw = reg;
        raw.Type = DataType.UInt32;
        string name = $"in_attr{location}";
        int k = (comps + 3) / 4;
        if (k == 1) decl.AppendLine($"layout(location = {location}) in {(comps == 1 ? "uint" : $"uvec{comps}")} {name};");
        else decl.AppendLine($"layout(location = {location}) in uvec4 {name}[{k}];");
        string C(int i) => k == 1 ? (comps == 1 ? name : $"{name}.{"xyzw"[i]}") : $"{name}[{i / 4}].{"xyzw"[i % 4]}";
        int perWord = 4 / typeSize, bits = typeSize * 8;
        uint mask = typeSize == 1 ? 0xFFu : 0xFFFFu;
        int words = (comps + perWord - 1) / perWord;
        for (int w = 0; w < words; w++)
        {
            var parts = new List<string>();
            for (int j = w * perWord; j < Math.Min(comps, (w + 1) * perWord); j++)
            {
                int shift = (j - w * perWord) * bits;
                string v = $"({C(j)} & 0x{mask:X}u)";
                parts.Add(shift == 0 ? v : $"({v} << {shift})");
            }
            t.Store(raw, new Val($"({string.Join(" | ", parts)})", 'u', 1), 1, w);
        }
        result.VertexInputs.Add(new VertexInputInfo(regIndex, location, 'u', Math.Min(comps, 4), k, typeSize, comps));
    }

    static void EmitAttribute(UsseTranslator t, StringBuilder decl, GeneratedShader result, Operand reg, uint regIndex, int location, int n, bool forceArray = false, int hintCount = 0)
    {
        char kind = GxpProgram.IsSigned(reg.Type) ? 'i' : GxpProgram.IsUnsigned(reg.Type) ? 'u' : 'f';
        string vecT = kind switch { 'i' => "ivec", 'u' => "uvec", _ => "vec" };
        string scalarT = kind switch { 'i' => "int", 'u' => "uint", _ => "float" };
        string name = $"in_attr{location}";
        if (n <= 4 && !forceArray)
        {
            n = Math.Max(n, 1);
            decl.AppendLine($"layout(location = {location}) in {(n == 1 ? scalarT : vecT + n)} {name};");
            string value = name;
            // 3-component 8/16-bit vertex formats may be fetched as 4 components: the 4th must read as 1
            if (n == 4 && hintCount == 3) value = kind switch { 'f' => $"vec4({name}.xyz, 1.0)", 'i' => $"ivec4({name}.xyz, 1)", _ => $"uvec4({name}.xyz, 1u)" };
            t.Store(reg, new Val(value, kind, n), (1 << n) - 1);
            result.VertexInputs.Add(new VertexInputInfo(regIndex, location, kind, n, 1));
        }
        else
        {
            int k = (n + 3) / 4;
            decl.AppendLine($"layout(location = {location}) in {vecT}4 {name}[{k}];");
            int stride = 4 * GxpProgram.TypeSize(reg.Type) / 4;
            for (int i = 0; i < k; i++)
                t.Store(reg, new Val($"{name}[{i}]", kind, 4), 0b1111, i * Math.Max(stride, 1));
            result.VertexInputs.Add(new VertexInputInfo(regIndex, location, kind, 4, k));
        }
    }

    // ------------------------------------------------------------------ fragment inputs (create_fragment_inputs)

    static void EmitFragmentInputs(GxpProgram program, ShaderHints hints, UsseTranslator t, StringBuilder decl, GeneratedShader result,
        Func<uint, bool, UsseTranslator.SamplerUse> samplerFor)
    {
        var declared = new HashSet<int>();
        string Varying(int loc, string name)
        {
            if (declared.Add(loc)) decl.AppendLine($"layout(location = {loc}) in vec4 {name};");
            return name;
        }

        uint paOffset = 0;
        var coords = new string?[11];
        var queries = new List<(UsseTranslator.SamplerUse S, int CoordIndex, int ProdPos, uint Dest, DataType Store)>();

        foreach (var d in program.FragmentAttributeDescriptors())
        {
            if ((d.AttributeInfo & 0x4000F000) != 0xF000)
            {
                uint inputId = d.AttributeInfo & 0x4000F000;
                var dtype = DataType.UInt8;
                uint inputType = d.AttributeInfo & 0x30100000;
                bool isColor = inputId is 0xA000 or 0xB000;
                if (inputType == 0x20000000) dtype = DataType.F16;
                else if (inputType == 0x10000000) { }
                else if (inputType == 0x100000) { if (isColor) dtype = DataType.F32; }
                else if (!isColor) dtype = DataType.F32;

                int numComp = (int)((d.AttributeInfo >> 22) & 3) + 1;
                string src;
                if ((inputId & 0x40000000) != 0) src = "vec4(gl_PointCoord, 0.0, 1.0)";
                else if (inputId == 0xD000) src = "(gl_FragCoord / vec4(rinfo.res_multiplier, rinfo.res_multiplier, 1.0, 1.0))";
                else
                {
                    (string name, int loc) = inputId switch
                    {
                        0xC000 => ("v_Fog", 3),
                        0xA000 => ("v_Color0", 1),
                        0xB000 => ("v_Color1", 2),
                        _ => ($"v_TexCoord{inputId / 0x1000}", 4 + (int)(inputId / 0x1000)),
                    };
                    src = Varying(loc, name);
                    if (inputId <= 0x9000) coords[inputId / 0x1000] = name;
                }
                var reg = Operand.Make(3); reg.Bank = Bank.PrimAttr; reg.Num = (int)paOffset; reg.Type = dtype;
                var v = new Val(src, 'f', 4);
                Val stored = numComp == 4 ? v : new Val(numComp == 1 ? $"{Paren(src)}.x" : $"{Paren(src)}.{"xyzw"[..numComp]}", 'f', numComp);
                if (GxpProgram.IsInteger(dtype)) stored = t.ToInt(stored, dtype, true);
                t.Store(reg, stored, (1 << numComp) - 1);
                paOffset += ((d.Size >> 4) & 3) + 1;
            }

            uint texCoordIndex = d.AttributeInfo & 0x40F;
            if (texCoordIndex != 0xF)
            {
                if (texCoordIndex == 0x400) texCoordIndex = 10;
                uint unit = d.ResourceIndex;
                var param = program.Parameters.FirstOrDefault(p => p.ResourceIndex == unit && p.Category == ParamCategory.Sampler);
                bool cube = param?.IsSamplerCube ?? false;
                var s = samplerFor(unit, cube);
                var storeType = ((d.ComponentInfo >> 4) & 3) switch { 0 => DataType.Unk, 2 => DataType.F16, 3 => DataType.F32, _ => DataType.F16 };
                uint swz = d.AttributeInfo & 0x300;
                int prod = swz == 0x300 ? 2 : swz == 0x200 ? 3 : -1;
                if (cube) prod = -1;
                queries.Add((s, (int)texCoordIndex, swz != 0x100 ? prod : -1, paOffset, storeType));
                paOffset += ((d.Size >> 6) & 3) + 1;
            }
        }

        foreach (var q in queries)
        {
            string coord;
            if (q.CoordIndex == 10) coord = "vec4(gl_PointCoord, 0.0, 1.0)";
            else coord = coords[q.CoordIndex] ?? Varying(4 + q.CoordIndex, $"v_TexCoord{q.CoordIndex}");
            string sampleExpr;
            if (q.S.IsCube) sampleExpr = $"texture({q.S.GlslName}, {coord}.xyz)";
            else if (q.ProdPos >= 0) sampleExpr = $"textureProj({q.S.GlslName}, vec3({coord}.xy, {coord}.{"xyzw"[q.ProdPos]}))";
            else sampleExpr = $"texture({q.S.GlslName}, {coord}.xy)";
            var type = q.Store == DataType.Unk ? q.S.ComponentType : q.Store;
            var reg = Operand.Make(3); reg.Bank = Bank.PrimAttr; reg.Num = (int)q.Dest; reg.Type = type;
            var val = new Val(sampleExpr, 'f', 4);
            if (GxpProgram.IsInteger(type)) val = t.ToInt(val, type, true);
            int cc = Math.Clamp(q.S.ComponentCount, 1, 4);
            t.Store(reg, cc == 4 ? val : new Val($"({val.E}).{"xyzw"[..cc]}", val.K, cc), (1 << cc) - 1);
        }

        if (program.IsFragColorUsed)
        {
            result.UsesFragColor = true;
            var target = Operand.Make(3); target.Bank = Bank.Output; target.Num = 0;
            target.Type = OutputType(program, hints);
            string sample = PerSampleFramebufferFetch ? "gl_SampleID" : "0";
            Val src;
            if (hints.FetchInputAttachment)
            {
                // multisampled scene rendered in a render pass: the colour attachment itself is the input attachment
                // (subpass self-dependency), read per sample
                decl.AppendLine("layout(input_attachment_index = 0, set = 3, binding = 2) uniform subpassInputMS last_frag_in;");
                src = new Val($"subpassLoad(last_frag_in, {sample})", 'f', 4);
            }
            else
            {
                // binding 0: single-sampled copy of the target; binding 1: multisampled copy (scenes rendered with
                // real MSAA, rinfo.fb_samples > 1), read per sample
                decl.AppendLine("layout(set = 3, binding = 0) uniform sampler2D last_frag_tex;");
                decl.AppendLine("layout(set = 3, binding = 1) uniform sampler2DMS last_frag_ms;");
                src = new Val($"(rinfo.fb_samples > 1.5 ? texelFetch(last_frag_ms, ivec2(gl_FragCoord.xy), {sample}) : texelFetch(last_frag_tex, ivec2(gl_FragCoord.xy), 0))", 'f', 4);
            }
            if (!GxpProgram.IsFloat(target.Type)) src = t.ToInt(src, target.Type, true);
            t.Store(target, src, 0b1111);
        }
    }

    static string Paren(string e) => e.StartsWith('(') || e.All(c => char.IsLetterOrDigit(c) || c == '_') ? e : $"({e})";

    static DataType OutputType(GxpProgram program, ShaderHints hints)
    {
        var type = GxpProgram.StoreType(program.OutputParamType);
        // U32/S32 outputs are raw bits: the surface stores the output registers unconverted, so read them in the
        // surface's own component layout. Resistance: Burning Skies packs two RGBA8 G-buffer words (VPCK) into a
        // 64-bit F16F16F16F16 target and samples it back as a 1440-wide U8U8U8U8 texture; reading the registers as
        // four F32 values instead stored four converted floats (garbage halves, red/cyan world).
        if (type is DataType.Int32 or DataType.UInt32)
        {
            uint fmt = hints.ColorFormat & 0xF1800000;
            if (fmt == 0x01000000) return DataType.F16;        // F16F16F16F16
        }
        if (type is DataType.Int32 or DataType.UInt32 or DataType.Unk) type = DataType.F32;
        // F32F32 colour surfaces with more than 2 components pack lower precision data
        if ((hints.ColorFormat & 0xF1800000) == 0x11000000 && program.OutputCompCount > 2 && type == DataType.F16) type = DataType.F32;
        return type;
    }

    static void EmitFragmentFinalize(GxpProgram program, ShaderHints hints, UsseTranslator t, StringBuilder decl, GeneratedShader result)
    {
        var color = Operand.Make(0);
        color.Bank = program.IsNativeColor ? Bank.Output : Bank.PrimAttr;
        color.Num = 0;
        color.Type = OutputType(program, hints);
        int regOff = 0;
        if (!program.IsNativeColor && (byte)program.OutputParamType == 1) regOff = program.FragmentOutputStart;
        int rawBits = RawOutputBits(program, hints.ColorFormat);
        if (rawBits != 0)
        {
            // raw register bits: written through an integer view of the surface (exact bits, no float conversion)
            result.RawColorBits = rawBits;
            var words = Operand.Make(0);
            words.Bank = color.Bank;
            words.Num = 0;
            words.Type = DataType.UInt32;
            int count = program.OutputCompCount;
            var w = t.Load(words, (1 << count) - 1, regOff);
            decl.AppendLine("layout(location = 0) out uvec4 out_raw;");
            decl.AppendLine("vec4 out_color;"); // debug overrides below assign it (re-packed as halves into out_raw)
            t.Line($"uvec4 raw_words = uvec4({w.E}{string.Concat(Enumerable.Repeat(", 0u", 4 - count))});");
            t.Line(rawBits switch
            {
                32 => "out_raw = raw_words;",
                16 => "out_raw = uvec4(raw_words.x & 0xFFFFu, raw_words.x >> 16, raw_words.y & 0xFFFFu, raw_words.y >> 16);",
                _ => "out_raw = uvec4(raw_words.x & 0xFFu, (raw_words.x >> 8) & 0xFFu, (raw_words.x >> 16) & 0xFFu, raw_words.x >> 24);",
            });
            t.Line("out_color = vec4(0.0);");
        }
        else
        {
            var v = t.Load(color, 0xF, regOff);
            if (!GxpProgram.IsFloat(color.Type)) v = t.ToFloat(v, color.Type, true);
            decl.AppendLine("layout(location = 0) out vec4 out_color;");
            t.Line($"out_color = {v.E};");
        }
        int bodyLength = t.Body.Length;
        // debug bisection (VITASHARP_DEBUG_TEX0=1): show the raw first texture at the first coordinate, bypassing the
        // shader math, to tell texture/coordinate problems from lighting/fog problems
        if (DebugTex0 && t.Body.Contains("texture(ftex0, v_TexCoord0") && decl.ToString().Contains("in vec4 v_TexCoord0;"))
            t.Line(DebugUv ? "out_color = vec4(fract(v_TexCoord0.xy), fract(v_TexCoord0.x * 0.0625), out_color.a);" : DebugLod ? "out_color = vec4(clamp(textureQueryLod(ftex0, v_TexCoord0.xy).y / 16.0 + 0.5, 0.0, 1.0), clamp(log2(max(fwidth(v_TexCoord0.x), fwidth(v_TexCoord0.y)) * float(textureSize(ftex0, 0).x)) / 16.0 + 0.5, 0.0, 1.0), float(textureSize(ftex0, 0).x) / 1024.0, 1.0);" : DebugAlpha ? "out_color = vec4(texture(ftex0, v_TexCoord0.xy).aaa, 1.0);" : "out_color = vec4(texture(ftex0, v_TexCoord0.xy).rgb, out_color.a);");
        // VITASHARP_DEBUG_FS=<hash>:<glsl vec4 expression>: replace one fragment program's output (term bisection)
        if (DebugFs is { } dfs && dfs.StartsWith($"{program.Hash:X16}:", StringComparison.OrdinalIgnoreCase))
            t.Line($"out_color = {dfs[17..]};");
        // VITASHARP_DEBUG_TEX0=hash: every pixel shows the fragment program that wrote it (low 3 bytes of its hash as RGB)
        if (DebugHash)
            t.Line($"out_color = vec4({program.Hash & 0xFF}.0, {(program.Hash >> 8) & 0xFF}.0, {(program.Hash >> 16) & 0xFF}.0, 255.0) / 255.0;");
        // VITASHARP_DEBUG_TEX0=id: every pixel shows the number of the draw that wrote it, 4 bits per channel (R low, G mid, B high), so it survives packed float targets
        if (DebugId)
            t.Line("{ uint did = uint(rinfo.draw_id); out_color = vec4(float(did & 15u) / 15.0, float((did >> 4) & 15u) / 15.0, float((did >> 8) & 15u) / 15.0, 1.0); }");
        // a debug override replaced the colour of a raw-output program: store it as the surface's halves/bytes/floats
        if (rawBits != 0 && t.Body.Length != bodyLength)
            t.Line(rawBits switch
            {
                32 => "out_raw = floatBitsToUint(out_color);",
                16 => "out_raw = uvec4(packHalf2x16(out_color.xy) & 0xFFFFu, packHalf2x16(out_color.xy) >> 16, packHalf2x16(out_color.zw) & 0xFFFFu, packHalf2x16(out_color.zw) >> 16);",
                _ => "out_raw = uvec4(round(clamp(out_color, 0.0, 1.0) * 255.0));",
            });
    }

    /// <summary>
    /// Bits per unsigned component of a fragment program's raw colour output, or 0 when its output is converted.
    /// A U32/S32 output parameter means the program wrote the registers' raw contents (e.g. VPCK-packed RGBA8 words);
    /// when those registers exactly fill one pixel of the colour surface the hardware stores them unconverted.
    /// Resistance: Burning Skies packs two RGBA8 G-buffer words into a 64-bit F16F16F16F16 surface and samples them
    /// back as U8U8U8U8: written as halves through a float attachment, packed bytes that form half NaNs/denormals
    /// (alpha 0xFF, normal components near 0x80) were canonicalised or flushed by the host GPU. The renderer writes
    /// such outputs through an integer view with components of this size (the surface's own component layout).
    /// Surfaces with a component swizzle or a host layout that differs from the guest are left to the float path.
    /// </summary>
    public static int RawOutputBits(GxpProgram program, uint colorFormat)
    {
        var type = GxpProgram.StoreType(program.OutputParamType);
        if (type is not (DataType.Int32 or DataType.UInt32)) return 0;
        if (((colorFormat >> 20) & 3) != 0 && (colorFormat & 0xF1800000) is 0x00000000 or 0x01000000) return 0; // swizzled
        var (bits, bytes) = (colorFormat & 0xF1800000) switch
        {
            0x01000000 => (16, 8), // F16F16F16F16
            0x00800000 => (16, 4), // F16F16
            0x11000000 => (32, 8), // F32F32
            0x10800000 => (32, 4), // F32
            0x00000000 => (8, 4),  // U8U8U8U8
            _ => (0, 0),
        };
        return bits != 0 && program.OutputCompCount * 4 == bytes ? bits : 0;
    }

    static readonly bool DebugTex0 = Environment.GetEnvironmentVariable("VITASHARP_DEBUG_TEX0") is "1" or "uv" or "alpha" or "lod";
    static readonly bool DebugHash = Environment.GetEnvironmentVariable("VITASHARP_DEBUG_TEX0") == "hash";
    static readonly string? DebugFs = Environment.GetEnvironmentVariable("VITASHARP_DEBUG_FS");
    static readonly bool DebugLod = Environment.GetEnvironmentVariable("VITASHARP_DEBUG_TEX0") == "lod";
    static readonly bool DebugAlpha = Environment.GetEnvironmentVariable("VITASHARP_DEBUG_TEX0") == "alpha";
    static readonly bool DebugUv = Environment.GetEnvironmentVariable("VITASHARP_DEBUG_TEX0") == "uv";
    static readonly bool DebugId = Environment.GetEnvironmentVariable("VITASHARP_DEBUG_TEX0") == "id";

    // ------------------------------------------------------------------ vertex outputs

    static int CopyCount(byte info) => ((info & 1) != 0 ? 2 : 0) + ((info & 2) != 0 ? 1 : 0) + ((info & 4) != 0 ? 1 : 0);

    static void EmitVertexFinalize(GxpProgram program, UsseTranslator t, StringBuilder decl)
    {
        int outputs = program.VertexOutputs(out var coordInfos);
        var list = new List<(int Flag, string Name, int Count, int Loc)>
        {
            (GxpProgram.OutPosition, "gl_Position", 4, -1),
            (GxpProgram.OutColor0, "v_Color0", 4, 1),
            (GxpProgram.OutColor1, "v_Color1", 4, 2),
            (GxpProgram.OutFog, "v_Fog", 2, 3),
        };
        for (int i = 0; i < 10; i++) list.Add((GxpProgram.OutTexcoord0 << i, $"v_TexCoord{i}", CopyCount(coordInfos[i]), 4 + i));
        list.Add((GxpProgram.OutPsize, "gl_PointSize", 1, -2));

        // declare every varying location so any fragment program links
        for (int loc = 1; loc <= 13; loc++)
        {
            string name = loc switch { 1 => "v_Color0", 2 => "v_Color1", 3 => "v_Fog", _ => $"v_TexCoord{loc - 4}" };
            decl.AppendLine($"layout(location = {loc}) out vec4 {name};");
        }
        var written = new HashSet<string>();
        int num = 0;
        foreach (var (flag, name, count, loc) in list)
        {
            if ((outputs & flag) == 0) continue;
            var o = Operand.Make(0); o.Bank = Bank.Output; o.Num = num; o.Type = DataType.F32;
            if (flag == GxpProgram.OutPosition)
            {
                var pos = t.Load(o, 0xF);
                t.Line($"vec4 vpos = {pos.E};");
                t.Open("if (rinfo.viewport_flag < 0.5)");
                t.Line("gl_Position = vpos * vec4(2.0 / rinfo.screen_width, 2.0 / rinfo.screen_height, 1.0, 1.0) + vec4(-1.0, -1.0, 0.0, 0.0);");
                t.Line("gl_Position.z = gl_Position.w;");
                t.Close();
                t.Open("else");
                // screen z = z / w * z_scale + z_offset, kept affine in clip space (multiplied back by w). GXM does
                // not clip at the near plane: depth below 0 is clamped, which must happen per fragment (the
                // renderer enables depth clamp). Clamping per vertex, as before, bent the depth interpolated across
                // a triangle with a vertex in front of the near plane (a wall next to the camera then lost the depth
                // test against the geometry behind it). Depth clamp also disables the far-plane clip, so that one
                // is done with a clip distance (z <= w).
                t.Line("gl_Position = vpos;");
                t.Line("gl_Position.z = vpos.z * rinfo.z_scale + rinfo.z_offset * vpos.w;");
                t.Close();
                t.Line("gl_ClipDistance[0] = gl_Position.w - gl_Position.z;");
            }
            else if (flag == GxpProgram.OutPsize)
            {
                t.Line($"gl_PointSize = {t.Load(o, 1).E};");
            }
            else
            {
                t.Line($"{name} = {t.Load(o, 0xF).E};");
                written.Add(name);
            }
            num += count;
        }
        for (int loc = 1; loc <= 13; loc++)
        {
            string name = loc switch { 1 => "v_Color0", 2 => "v_Color1", 3 => "v_Fog", _ => $"v_TexCoord{loc - 4}" };
            if (!written.Contains(name)) t.Line($"{name} = vec4(0.0);");
        }
    }
}
