using System.Buffers.Binary;
using System.Text;

namespace VitaSharp.Core.Gxm.Shader;

public enum DataType : byte { Int8, Int16, Int32, C10, F16, F32, UInt8, UInt16, UInt32, O8, Unk }

public enum ParamCategory : byte { Attribute, Uniform, Sampler, AuxiliarySurface, UniformBuffer }

public enum ParamType : byte { F32, F16, C10, U32, S32, U16, S16, U8, S8, Aggregate }

public sealed record GxpParameter(int Index, string RawName, ParamCategory Category, ParamType Type, int ComponentCount,
    int ContainerIndex, byte Semantic, byte SemanticIndex, uint ArraySize, uint ResourceIndex, uint Offset)
{
    public string Name => RawName.Replace('.', '_').Replace('[', '_').Replace(']', '_');
    public bool IsSamplerCube => ((SemanticIndex >> 4) & 1) != 0;
}

public sealed record GxpContainer(ushort Index, ushort BaseSaOffset, ushort SizeInF32);
public sealed record GxpLiteral(uint Offset, float Data);
public sealed record GxpUniformBufferInfo(ushort ResideBuffer, ushort LdstBaseOffset, int LdstBaseValue);
public sealed record GxpDependentSampler(ushort ResourceIndexLayoutOffset, ushort SaOffset);
public sealed record GxpAttributeDescriptor(uint AttributeInfo, uint ResourceIndex, uint Size, uint ComponentInfo);

/// <summary>Parsed view of a GXP (SceGxmProgram) binary.</summary>
public sealed class GxpProgram
{
    public const uint FlagFragment = 1, FlagDiscardUsed = 1 << 3, FlagDepthUsed = 1 << 4, FlagSpriteCoordUsed = 1 << 5,
        FlagNativeColorUsed = 1 << 6, FlagFragColorUsed = 1 << 7, FlagOutputUndefined = 1 << 16, FlagIndexUsed = 1 << 17, FlagInstanceUsed = 1 << 18;

    public byte[] Data { get; }
    public uint GuestAddress { get; }
    public ulong Hash { get; }

    public uint Flags => U32(0x14);
    public bool IsFragment => (Flags & FlagFragment) != 0;
    public bool IsVertex => !IsFragment;
    public bool IsNativeColor => (Flags & FlagNativeColorUsed) != 0;
    public bool IsFragColorUsed => (Flags & FlagFragColorUsed) != 0;
    public bool IsDiscardUsed => (Flags & FlagDiscardUsed) != 0;
    public bool IsDepthReplaceUsed => (Flags & FlagDepthUsed) != 0;
    public bool IsSpriteCoordUsed => (Flags & FlagSpriteCoordUsed) != 0;
    public uint TexunitFlags0 => U32(0x1C);
    public uint TexunitFlags1 => U32(0x20);
    public uint ParameterCount => U32(0x24);
    public ushort PrimaryRegCount => U16(0x30);
    public ushort SecondaryRegCount => U16(0x32);
    public uint PrimaryInstrCount => U32(0x3C);
    public uint ThreadBufferCount => U32(0x54);
    public uint LiteralBufferCount => U32(0x58);
    public uint TextureBufferCount => U32(0x60);
    public uint DefaultUniformBufferCount => U32(0x64);
    public uint LiteralsCount => U32(0x70);
    public uint UniformBufferCount => U32(0x78);
    public uint DependentSamplerCount => U32(0x80);
    public uint ContainerCount => U32(0x90);

    public int PrimaryProgramStart => Rel(0x40);
    public int SecondaryProgramStart => Rel(0x48);
    public int SecondaryProgramEnd => Rel(0x4C);
    public int LiteralBufferData => Rel(0x68);

    // varyings
    int Varyings => Rel(0x2C);
    public ulong UntypedPaRegs => U64(Varyings + 8);
    public byte FragmentOutputStart => Data[Varyings + 8];
    public ParamType OutputParamType => (ParamType)Data[Varyings + 10];
    public byte OutputCompCount => Data[Varyings + 11];
    public ushort VaryingsCount => U16(Varyings + 12);
    public uint VertexOutputs1 => U32(Varyings + 16);
    public uint VertexOutputs2 => U32(Varyings + 20);
    public uint TexcoordPackFormat => U32(Varyings + 24);
    public ushort SemanticIndexOffset => U16(Varyings + 28);
    public ushort SemanticInstanceOffset => U16(Varyings + 30);

    public IReadOnlyList<GxpParameter> Parameters { get; }
    public IReadOnlyList<GxpContainer> Containers { get; }
    public IReadOnlyList<GxpLiteral> Literals { get; }
    public IReadOnlyList<GxpUniformBufferInfo> UniformBuffers { get; }
    public IReadOnlyList<GxpDependentSampler> DependentSamplers { get; }

    public GxpProgram(byte[] data, uint guestAddress)
    {
        Data = data;
        GuestAddress = guestAddress;
        Hash = System.IO.Hashing.XxHash64.HashToUInt64(data);

        int pbase = Rel(0x28);
        var ps = new List<GxpParameter>();
        for (int i = 0; i < ParameterCount; i++)
        {
            int p = pbase + i * 16;
            int nameOff = (int)U32(p);
            byte b4 = Data[p + 4], b5 = Data[p + 5];
            ps.Add(new GxpParameter(i, CString(p + nameOff), (ParamCategory)(b4 & 0xF), (ParamType)(b4 >> 4), b5 & 0xF, b5 >> 4,
                Data[p + 6], Data[p + 7], U32(p + 8), U32(p + 12), (uint)p));
        }
        Parameters = ps;

        var cs = new List<GxpContainer>();
        int cbase = Rel(0x94);
        for (int i = 0; i < ContainerCount; i++) cs.Add(new GxpContainer(U16(cbase + i * 8), U16(cbase + i * 8 + 4), U16(cbase + i * 8 + 6)));
        Containers = cs;

        var ls = new List<GxpLiteral>();
        int lbase = Rel(0x74);
        for (int i = 0; i < LiteralsCount; i++) ls.Add(new GxpLiteral(U32(lbase + i * 8), BitConverter.ToSingle(Data, lbase + i * 8 + 4)));
        Literals = ls;

        var ub = new List<GxpUniformBufferInfo>();
        int ubase = Rel(0x7C);
        for (int i = 0; i < UniformBufferCount; i++) ub.Add(new GxpUniformBufferInfo(U16(ubase + i * 8), U16(ubase + i * 8 + 2), (int)U32(ubase + i * 8 + 4)));
        UniformBuffers = ub;

        var ds = new List<GxpDependentSampler>();
        int dbase = Rel(0x84);
        for (int i = 0; i < DependentSamplerCount; i++) ds.Add(new GxpDependentSampler(U16(dbase + i * 4), U16(dbase + i * 4 + 2)));
        DependentSamplers = ds;
    }

    public static GxpProgram FromGuest(uint address)
    {
        uint size = Memory.GuestMemory.R32(address + 8);
        if (size == 0 || size > 16 * 1024 * 1024) size = 0x1000;
        return new GxpProgram(Memory.GuestMemory.Span(address, (int)size).ToArray(), address);
    }

    public IEnumerable<GxpAttributeDescriptor> FragmentAttributeDescriptors()
    {
        int field = Varyings + 16;
        int baseOff = field + (int)VertexOutputs1;
        for (int i = 0; i < VaryingsCount; i++)
        {
            int p = baseOff + i * 16;
            yield return new GxpAttributeDescriptor(U32(p), U32(p + 4), U32(p + 8), U32(p + 12));
        }
    }

    public GxpContainer? ContainerByIndex(int idx) => Containers.FirstOrDefault(c => c.Index == idx);

    public ulong ReadInstruction(int offset, int index) => U64(offset + index * 8);

    public int SecondaryInstrCount => Math.Max(0, (SecondaryProgramEnd - SecondaryProgramStart) / 8);

    /// <summary>Bitmask of texture units (0..15) referenced as samplers.</summary>
    public uint TexturesUsed()
    {
        uint m = 0;
        foreach (var p in Parameters)
            if (p.Category == ParamCategory.Sampler && p.ResourceIndex < 16) m |= 1u << (int)p.ResourceIndex;
        foreach (var d in DependentSamplers) m |= 1u << (d.ResourceIndexLayoutOffset / 4);
        return m;
    }

    // ---- vertex outputs (gxp::get_vertex_outputs)
    public const int OutPosition = 1 << 0, OutFog = 1 << 1, OutColor0 = 1 << 2, OutColor1 = 1 << 3, OutTexcoord0 = 1 << 4, OutPsize = 1 << 14;

    public int VertexOutputs(out byte[] coordInfos)
    {
        coordInfos = new byte[10];
        if (!IsVertex) return 0;
        uint vo1 = VertexOutputs1, vo2 = VertexOutputs2;
        int res = OutPosition;
        if ((vo1 & 0x200) != 0) res |= OutFog;
        if ((vo1 & 0x800) != 0) res |= OutColor0;
        if ((vo1 & 0x400) != 0) res |= OutColor1;
        for (int i = 0; i < 10; i++)
        {
            uint info = (vo2 >> (i * 3)) & 7;
            if (info != 0) { res |= OutTexcoord0 << i; coordInfos[i] = (byte)info; }
        }
        if ((vo1 & 0x100) != 0) res |= OutPsize;
        for (int i = 0; i < 8; i++) if ((vo1 & (1u << i)) != 0) res |= 1 << (15 + i);
        return res;
    }

    // ---- raw access helpers (offsets are relative to the field position for *_offset fields)
    int Rel(int field) => field + (int)U32(field);
    public uint U32(int o) => o + 4 <= Data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(o)) : 0;
    public ushort U16(int o) => o + 2 <= Data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(o)) : (ushort)0;
    public ulong U64(int o) => o + 8 <= Data.Length ? BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(o)) : 0;

    string CString(int o)
    {
        if (o < 0 || o >= Data.Length) return "";
        int e = Array.IndexOf(Data, (byte)0, o);
        return Encoding.ASCII.GetString(Data, o, (e < 0 ? Data.Length : e) - o);
    }

    public static DataType StoreType(ParamType t) => t switch
    {
        ParamType.F32 => DataType.F32,
        ParamType.F16 => DataType.F16,
        ParamType.U16 => DataType.UInt16,
        ParamType.S16 => DataType.Int16,
        ParamType.U8 => DataType.UInt8,
        ParamType.S8 => DataType.Int8,
        ParamType.C10 => DataType.C10,
        ParamType.U32 => DataType.UInt32,
        ParamType.S32 => DataType.Int32,
        _ => DataType.Unk,
    };

    public static int TypeSize(DataType t) => t switch
    {
        DataType.Int8 or DataType.UInt8 or DataType.O8 or DataType.C10 => 1,
        DataType.Int16 or DataType.UInt16 or DataType.F16 => 2,
        _ => 4,
    };

    public static bool IsFloat(DataType t) => t is DataType.C10 or DataType.F16 or DataType.F32;
    public static bool IsUnsigned(DataType t) => t is DataType.UInt8 or DataType.UInt16 or DataType.UInt32;
    public static bool IsSigned(DataType t) => t is DataType.Int8 or DataType.Int16 or DataType.Int32;
    public static bool IsInteger(DataType t) => IsUnsigned(t) || IsSigned(t);
}

// ---------------------------------------------------------------------------------- program inputs

public enum InputBank { PrimAttr, SecAttr }

public abstract record InputSource;
public sealed record UniformBufferInputSource(uint Base, uint Index) : InputSource;
public sealed record LiteralInputSource(float Data) : InputSource;
public sealed record AttributeInputSource(string Name, uint Index, byte Semantic, bool RegFormat) : InputSource;
public sealed record DependentSamplerInputSource(string Name, uint Index, uint LayoutPosition) : InputSource;

public sealed record ProgramInputItem(InputBank Bank, DataType Type, uint Offset, uint ComponentCount, uint ArraySize, InputSource Source);
public sealed record SamplerDesc(string Name, uint Index, bool IsCube);

public sealed class UniformBufferDesc
{
    public uint Index;
    public uint Size;          // in 32-bit words
    public uint RegStartOffset;
    public uint RegBlockSize;
    /// <summary>The buffer ends with an array (bone palettes, light tables). Hardware does no bounds checks on uniform
    /// buffer loads and shaders do index such arrays past their declared length (Uncharted declares
    /// g_mSkinTransforms[96] and reads entry 147), so the buffer is read as far as the next buffer / mapped memory.</summary>
    public bool EndsWithArray;
}

public sealed class ProgramInput
{
    public const uint TextureBuffer = 15, LiteralBuffer = 16, ThreadBuffer = 18, DefaultUniformBuffer = 14, MaxUbInFloat = 2048;
    /// <summary>Debug (VITASHARP_UB_UNBOUNDED=1): size every indexed uniform buffer as unbounded (read up to the next buffer).</summary>
    static readonly bool UnboundedUbs = Environment.GetEnvironmentVariable("VITASHARP_UB_UNBOUNDED") == "1";
    public static bool UnboundedUbsActive => UnboundedUbs;

    public List<ProgramInputItem> Inputs { get; } = new();
    public List<SamplerDesc> Samplers { get; } = new();
    public List<UniformBufferDesc> UniformBuffers { get; } = new();

    /// <summary>Port of Vita3K shader::get_program_input.</summary>
    public static ProgramInput From(GxpProgram program)
    {
        var pi = new ProgramInput();
        var ubs = new SortedDictionary<uint, UniformBufferDesc>();
        uint investigated = 0;
        bool symbolsStripped = program.PrimaryRegCount == 0;

        foreach (var p in program.Parameters)
        {
            switch (p.Category)
            {
                case ParamCategory.Uniform:
                case ParamCategory.Attribute:
                {
                    bool isUniform = p.Category == ParamCategory.Uniform;
                    var container = program.ContainerByIndex(p.ContainerIndex);
                    uint offset = p.ResourceIndex;
                    if (container != null && isUniform) offset = container.BaseSaOffset + p.ResourceIndex;
                    var storeType = GxpProgram.StoreType(p.Type);
                    if (!isUniform)
                    {
                        bool regformat = (program.UntypedPaRegs & (1ul << (int)p.ResourceIndex)) != 0;
                        pi.Inputs.Add(new ProgramInputItem(InputBank.PrimAttr, storeType, offset, (uint)p.ComponentCount, p.ArraySize,
                            new AttributeInputSource(p.Name, p.ResourceIndex, p.Semantic, regformat)));
                    }
                    else
                    {
                        uint vecSize = (uint)(p.ComponentCount * GxpProgram.TypeSize(storeType));
                        if (p.ArraySize != 1)
                            vecSize = GxpProgram.IsFloat(storeType) && p.ComponentCount != 1 ? (vecSize + 7) & ~7u : (vecSize + 3) & ~3u;
                        uint paramSizeF32 = (p.ArraySize * vecSize + 3) / 4;
                        investigated |= 1u << p.ContainerIndex;
                        uint ci = (uint)p.ContainerIndex;
                        if (!ubs.TryGetValue(ci, out var b))
                        {
                            b = new UniformBufferDesc
                            {
                                Index = ci,
                                RegBlockSize = container?.SizeInF32 ?? 0,
                                RegStartOffset = container != null ? container.BaseSaOffset : offset,
                                Size = p.ResourceIndex + paramSizeF32,
                                EndsWithArray = p.ArraySize > 1,
                            };
                            ubs[ci] = b;
                        }
                        else
                        {
                            uint end = p.ResourceIndex + paramSizeF32;
                            if (end > b.Size) { b.Size = end; b.EndsWithArray = p.ArraySize > 1; }
                            else if (end == b.Size && p.ArraySize > 1) b.EndsWithArray = true;
                            if (container == null) b.RegStartOffset = Math.Min(b.RegStartOffset, offset);
                        }
                    }
                    break;
                }
                case ParamCategory.Sampler:
                    pi.Samplers.Add(new SamplerDesc(p.Name, p.ResourceIndex, p.IsSamplerCube));
                    break;
                case ParamCategory.UniformBuffer:
                    if (!ubs.TryGetValue(p.ResourceIndex, out var ub))
                        ubs[p.ResourceIndex] = new UniformBufferDesc { Index = p.ResourceIndex, Size = (p.ArraySize + 3) / 4 };
                    else ub.Size = Math.Max((p.ArraySize + 3) / 4, ub.Size);
                    break;
            }
        }

        foreach (var (idx, b) in ubs)
        {
            if ((((investigated & (1u << (int)idx)) == 0) && symbolsStripped) || b.Size == 1024) b.Size = MaxUbInFloat;
            else if ((UnboundedUbs || b.EndsWithArray) && idx < DefaultUniformBuffer) b.Size = Math.Max(b.Size, MaxUbInFloat);
            pi.UniformBuffers.Add(b);
        }

        if (!pi.UniformBuffers.Any(b => b.Index == DefaultUniformBuffer) && program.DefaultUniformBufferCount != 0)
        {
            var b = new UniformBufferDesc { Index = DefaultUniformBuffer, RegBlockSize = program.DefaultUniformBufferCount, Size = program.DefaultUniformBufferCount };
            pi.UniformBuffers.Add(b);
            ubs[DefaultUniformBuffer] = b;
        }

        var c19 = program.ContainerByIndex(19);
        uint baseOffset = c19?.BaseSaOffset ?? 0;
        foreach (var info in program.UniformBuffers)
        {
            uint offset = baseOffset + info.LdstBaseOffset;
            if (info.ResideBuffer >= 15)
            {
                var b = new UniformBufferDesc { Index = info.ResideBuffer };
                b.Size = (uint)info.ResideBuffer switch
                {
                    TextureBuffer => program.TextureBufferCount,
                    LiteralBuffer => program.LiteralBufferCount,
                    ThreadBuffer => program.ThreadBufferCount,
                    _ => 0,
                };
                if (b.Size == 0) continue;
                pi.UniformBuffers.Add(b);
                ubs[info.ResideBuffer] = b;
            }
            if (ubs.TryGetValue(info.ResideBuffer, out var found))
                pi.Inputs.Add(new ProgramInputItem(InputBank.SecAttr, DataType.UInt32, offset, 1, 1, new UniformBufferInputSource((uint)info.LdstBaseValue, found.Index)));
        }

        if (c19 != null)
        {
            foreach (var ds in program.DependentSamplers)
            {
                uint rsc = ds.ResourceIndexLayoutOffset / 4u;
                var s = pi.Samplers.FirstOrDefault(x => x.Index == rsc);
                string name;
                if (s == null)
                {
                    name = $"anonymousSampler{rsc}";
                    pi.Samplers.Add(new SamplerDesc(name, rsc, false));
                }
                else name = s.Name;
                pi.Inputs.Add(new ProgramInputItem(InputBank.SecAttr, DataType.UInt32, (uint)(c19.BaseSaOffset + ds.SaOffset), 1, 1,
                    new DependentSamplerInputSource(name, rsc, ds.ResourceIndexLayoutOffset % 4u)));
            }
        }

        var lc = program.ContainerByIndex(16) ?? c19;
        if (lc != null)
            foreach (var l in program.Literals)
                pi.Inputs.Add(new ProgramInputItem(InputBank.SecAttr, DataType.F32, lc.BaseSaOffset + l.Offset, 1, 1, new LiteralInputSource(l.Data)));

        if (program.IsVertex)
        {
            if ((program.Flags & GxpProgram.FlagIndexUsed) != 0)
                pi.Inputs.Add(new ProgramInputItem(InputBank.PrimAttr, DataType.UInt32, program.SemanticIndexOffset, 1, 1,
                    new AttributeInputSource("gl_VertexIndex", 0, 15, false)));
            if ((program.Flags & GxpProgram.FlagInstanceUsed) != 0)
                pi.Inputs.Add(new ProgramInputItem(InputBank.PrimAttr, DataType.UInt32, program.SemanticInstanceOffset, 1, 1,
                    new AttributeInputSource("gl_InstanceIndex", 0, 16, false)));
        }
        return pi;
    }
}
