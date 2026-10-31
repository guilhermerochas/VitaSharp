namespace VitaSharp.Core.Gxm.Shader;

public sealed partial class UsseTranslator
{
    public const int RegSaCount = 32 * 4;

    /// <summary>Byte layout of uniform buffers inside the per-stage storage buffer: (gxm index, base byte offset, size bytes).</summary>
    public readonly List<(int Index, int Base, int Size)> BufferLayout = new();

    static readonly DataType[] SmpDestFmt = [DataType.Unk, DataType.Unk, DataType.F16, DataType.F32];

    /// <summary>GLSL for a texture fetch (port of do_fetch_texture).</summary>
    Val FetchTexture(SamplerUse s, string coords, int dim, DataType destType, int lodMode, string? extra1 = null, string? extra2 = null, int gatherComp = -1)
    {
        string e;
        if (gatherComp >= 0) e = $"textureGather({s.GlslName}, {coords}, {gatherComp})";
        else if (extra1 == null) e = lodMode == 4 ? $"textureProj({s.GlslName}, {coords})" : $"texture({s.GlslName}, {coords})";
        else e = lodMode switch
        {
            1 => $"texture({s.GlslName}, {coords}, {extra1})",
            2 => $"textureLod({s.GlslName}, {coords}, {extra1})",
            3 => $"textureGrad({s.GlslName}, {coords}, {extra1}, {extra2})",
            _ => $"texture({s.GlslName}, {coords})",
        };
        var v = new Val(e, 'f', 4);
        if (GxpProgram.IsInteger(destType)) v = ToInt(v, destType, true);
        return v;
    }

    void Smp(UsseInst i)
    {
        // "11100pppsn-ymrceffaaddlltbbggkhhiijjoooooooqqqqqqquuuuuuuvvvvvvv"
        var src0 = DecodeSrc0(0, i['q'], i['k'], i.Bit('r'), true, 8);
        uint src0Type = i['g'];
        src0.Type = src0Type == 0 ? DataType.F32 : src0Type == 1 ? DataType.F16 : DataType.C10;
        var src1 = DecodeSrc12(1, i['u'], i['i'], i.Bit('c'), true, 8);
        src0.Swizzle = (Sw[])Default4.Clone();
        // the texture state normally sits in the dependent sampler's SA words; a program can also copy those words into
        // temporaries first and name the copy (Silent Hill: Book of Memories' blur passes); any other operand keeps its
        // register number as the SA offset
        int stateSa = src1.Bank is Bank.Indexed1 or Bank.Indexed2 ? -1 : SaWordOf(src1.Bank, src1.Num);
        if (stateSa < 0 || !Samplers.ContainsKey(stateSa)) stateSa = src1.Num;
        if (!Samplers.TryGetValue(stateSa, out var sampler))
        {
            Warnings.Add($"SMP: no sampler bound at SA {src1.Num} (texture buffer path not supported)");
            return;
        }
        uint fconv = i['f'];
        var dest = Operand.Make(3);
        bool usePa = i.Bit('t');
        dest.Bank = usePa ? (SecondProgram ? Bank.SecAttr : Bank.PrimAttr) : Bank.Temp;
        dest.Num = (int)i['o'];
        dest.Type = SmpDestFmt[fconv];
        if (dest.Type == DataType.Unk) dest.Type = sampler.ComponentType;
        int dim = (int)i['d'] + 1;
        int coordMask = dim == 3 ? 0b0111 : dim == 1 ? 0b0001 : 0b0011;
        uint sbMode = i['b'];
        int lodMode = (int)i['l'];
        var coords = Load(src0, coordMask);
        string c = coords.E;
        if (dim == 1) { c = $"vec2({c}, 0.0)"; dim = 2; }
        if (sampler.IsCube && dim == 2) { Warnings.Add("cube sampled with 2D coords"); c = $"vec3({c}, 1.0)"; }
        c = Bind(new Val(c, 'f', sampler.IsCube ? 3 : dim)).E;

        if (sbMode == 2)
        {
            // info query: (u, v fractional, trilinear fraction, lod)
            string lod = Bind(new Val($"textureQueryLod({sampler.GlslName}, {c}).x", 'f', 1)).E;
            string size = $"vec2(textureSize({sampler.GlslName}, int({lod})))";
            string uv = $"fract({c}.xy * {size} - vec2(0.5))";
            dest.Type = DataType.UInt8;
            var v = new Val($"uvec4(uvec2(round(clamp({uv}, 0.0, 1.0) * 255.0)), uint(round(fract({lod}) * 255.0)), uint({lod}))", 'u', 4);
            Store(dest, v, 0b1111);
            return;
        }

        string? e1 = null, e2 = null;
        if (lodMode != 0)
        {
            var src2 = DecodeSrc12(2, i['v'], i['j'], i.Bit('e'), true, 8);
            src2.Type = src0.Type;
            switch (lodMode)
            {
                case 1:
                case 2:
                    e1 = Bind(Load(src2, 0b1)).E;
                    break;
                case 3:
                    if (dim == 2) { e1 = Bind(Load(src2, 0b0011)).E; e2 = Bind(Load(src2, 0b1100)).E; }
                    else { e1 = Bind(Load(src2, 0b0111)).E; e2 = Bind(Load(src2, 0b0111, 1)).E; }
                    break;
            }
        }
        int compCount = Math.Clamp(sampler.ComponentCount, 1, 4);
        if (sbMode == 0)
        {
            var res = FetchTexture(sampler, c, dim, dest.Type, lodMode, e1, e2);
            Store(dest, res, (1 << compCount) - 1);
        }
        else
        {
            // gather4: per-component gathers, interleaved by pixel
            var gathers = new List<string>();
            for (int comp = 0; comp < compCount; comp++)
                gathers.Add(Bind(FetchTexture(sampler, c, dim, dest.Type, lodMode, e1, e2, comp)).E);
            int step = GxpProgram.TypeSize(dest.Type);
            if (compCount == 1)
            {
                Store(dest, new Val(gathers[0], GxpProgram.IsInteger(dest.Type) ? (GxpProgram.IsSigned(dest.Type) ? 'i' : 'u') : 'f', 4), 0b1111);
                dest.Num += step;
            }
            else
            {
                var all = new List<string>();
                for (int px = 0; px < 4; px++)
                    for (int comp = 0; comp < compCount; comp++) all.Add($"{gathers[comp]}.{"xyzw"[px]}");
                char k = GxpProgram.IsInteger(dest.Type) ? (GxpProgram.IsSigned(dest.Type) ? 'i' : 'u') : 'f';
                for (int idx = 0; idx + 3 < all.Count; idx += 4)
                {
                    Store(dest, Compose(all.GetRange(idx, 4), k), 0b1111);
                    dest.Num += step;
                }
            }
            if (sbMode == 3)
            {
                string lod2 = $"textureQueryLod({sampler.GlslName}, {c}).x";
                string uv = Bind(new Val($"fract({c}.xy * vec2(textureSize({sampler.GlslName}, int({lod2}))) - vec2(0.5))", 'f', 2)).E;
                dest.Type = DataType.F16;
                var coeffs = new Val($"vec4(vec2(1.0 - {uv}.x, {uv}.x) * {uv}.y, vec2(1.0 - {uv}.x, {uv}.x) * (1.0 - {uv}.y)).zwyx", 'f', 4);
                Store(dest, coeffs, 0b1111);
            }
        }
    }

    // ===================================================================== memory loads/stores

    void Vldst(UsseInst i)
    {
        // "111oopppsnmycrbakkkkddeetgffihjlqquuvvvvvvvwwwwwwwxxxxxxxzzzzzzz"
        uint op1 = i['o'];
        if (op1 != 1 && op1 != 2) { Warnings.Add($"vldst op {op1}"); return; }
        bool isStore = op1 == 2;
        var type = i['f'] switch { 0 => DataType.F32, 1 => DataType.Int16, 2 => DataType.Int8, _ => DataType.Unk };
        int maskCount = (int)i['k'];
        int total = maskCount + 1;
        int totalBytes = GxpProgram.TypeSize(type) * total;
        var src0 = DecodeSrc0(0, i['w'], i['h'], i.Bit('r'), false, 7);
        var src1 = DecodeSrc12(1, i['x'], i['q'], i.Bit('b'), false, 7);
        var src2 = DecodeSrc12(2, i['z'], i['u'], i.Bit('a'), false, 7);
        src0.Type = src1.Type = src2.Type = DataType.Int32;
        Operand toStore;
        if (isStore)
        {
            src2.Type = type;
            toStore = src2;
        }
        else
        {
            toStore = Operand.Make(3);
            toStore.Bank = SecondProgram ? Bank.SecAttr : (i.Bit('t') ? Bank.PrimAttr : Bank.Temp);
            toStore.Num = (int)i['v'];
            toStore.Type = DataType.F32;
        }
        if (src1.Bank == Bank.Immediate) src1.Num *= GxpProgram.TypeSize(type);
        bool moeExpand = i.Bit('m');

        // Literal / texture buffers are resolved at translation time
        if (src0.Bank == Bank.SecAttr && src0.Num == LiteralBufferSaOffset && !isStore && src1.Bank == Bank.Immediate && src2.Bank == Bank.Immediate)
        {
            int off = LiteralBufferBase + src1.Num + src2.Num;
            float lit = off + 4 <= _program.Data.Length - _program.LiteralBufferData ? BitConverter.ToSingle(_program.Data, _program.LiteralBufferData + off) : 0;
            Store(toStore, new Val(F(lit), 'f', 1), 1);
            return;
        }
        if (src0.Bank == Bank.SecAttr && src0.Num == TextureBufferSaOffset)
        {
            int off = (TextureBufferBase + src1.Num + src2.Num) / 4;
            toStore.Type = DataType.Int32;
            Store(toStore, new Val($"{off / 4}", 'i', 1), 1);
            return;
        }

        int repeats = isStore ? maskCount + 1 : 1;
        SetRepeatMultiplier(1, 1, 1, 1);
        bool thread = src0.Bank == Bank.SecAttr && src0.Num == ThreadBufferSaOffset;
        for (int r = 0; r < repeats; r++)
        {
            int storeOff = isStore ? RepeatOffset(src2, r, RepeatMode.Slmsi) : 0;
            int s0off = isStore ? RepeatOffset(src0, r, RepeatMode.Slmsi) : 0;
            int s1off = isStore ? RepeatOffset(Operand.Make(3), r, RepeatMode.Slmsi) : 0;
            var b0 = Load(src0, 1, s0off);
            var b1 = Load(src1, 1, s1off);
            string addr = $"int({b1})";
            int regIndexBase = thread ? 0x1000000 : 0x10000;
            if (src1.Bank != Bank.Immediate) addr = $"({addr} - {regIndexBase})";
            if (!moeExpand) addr = $"({addr} + 4)";
            if (!isStore) addr = $"({addr} + int({Load(src2, 1)}))";
            if (thread)
            {
                UsesThreadBuffer = true;
                string index = $"(({addr} + {ThreadBufferBase}) >> 2)";
                if (isStore) Line($"thread_buffer[{index}] = {Load(toStore, 1, storeOff)};");
                else Store(toStore, new Val($"thread_buffer[{index}]", 'f', 1), 1);
                continue;
            }
            string baseAddr = Bind(new Val($"(int({b0}) + {addr})", 'i', 1)).E;
            if (isStore)
            {
                // memory written by the program (Croixleur Sigma skins its meshes in a vertex program that stores the
                // vertices for later draws): only through a mapped slot's pointer, into the renderer's mirror of
                // guest memory; 32-bit words only (a narrower store would have to merge into its word)
                if (!ShaderGenerator.MemoryStores(_program.IsVertex) || type != DataType.F32)
                {
                    Warnings.Add(type != DataType.F32 ? $"buffer store of {type} unsupported" : "buffer store unsupported (no shader stores on this device)");
                    continue;
                }
                WritesMemory = true;
                Line($"storeMemory({baseAddr}, {Load(toStore, 1, storeOff)});");
                continue;
            }
            for (int k = 0; k < totalBytes / 4; k++)
            {
                Store(toStore, new Val($"fetchMemory({baseAddr} + {4 * k})", 'f', 1), 1);
                toStore.Num += 1;
            }
        }
        ResetRepeatMultiplier();
    }
}
