using System.Collections.Concurrent;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Gxm;

/// <summary>Parsed GXP program shared by every patched program created from it (keyed by guest address).</summary>
public sealed class GxmProgramInfo
{
    public required uint Address;
    public required GxpProgram Program;
    public required List<UniformBufferSlot> UniformSlots;
    public required int UniformBytes;
    public required uint TexturesUsed;

    static readonly ConcurrentDictionary<uint, GxmProgramInfo> Cache = new();

    public static GxmProgramInfo Get(uint address)
    {
        if (Cache.TryGetValue(address, out var info))
        {
            // programs can be unloaded and another one loaded at the same address: verify the size/flags quickly
            if (GuestMemory.R32(address + 8) == (uint)info.Program.Data.Length && GuestMemory.R32(address + 0x14) == info.Program.Flags)
                return info;
        }
        var p = GxpProgram.FromGuest(address);
        var (slots, bytes) = ShaderGenerator.UniformLayout(p);
        info = new GxmProgramInfo { Address = address, Program = p, UniformSlots = slots, UniformBytes = bytes, TexturesUsed = p.TexturesUsed() };
        Cache[address] = info;
        return info;
    }
}

public readonly record struct GxmVertexAttribute(ushort StreamIndex, ushort Offset, byte Format, byte ComponentCount, ushort RegIndex);
public readonly record struct GxmVertexStream(ushort Stride, ushort IndexSource);

public sealed class GxmVertexProgram
{
    public required uint Handle;
    public required GxmProgramInfo Info;
    public required GxmVertexAttribute[] Attributes;
    public required GxmVertexStream[] Streams;
    public int RefCount = 1;
    /// <summary>Backend-owned cache slot (compiled shader module etc.).</summary>
    public object? BackendData;
}

public readonly record struct GxmBlendInfo(byte ColorMask, byte ColorFunc, byte AlphaFunc, byte ColorSrc, byte ColorDst, byte AlphaSrc, byte AlphaDst)
{
    public static GxmBlendInfo? Read(uint addr)
    {
        if (addr == 0) return null;
        uint v = GuestMemory.R32(addr);
        return new GxmBlendInfo((byte)(v & 0xFF), (byte)((v >> 8) & 0xF), (byte)((v >> 12) & 0xF), (byte)((v >> 16) & 0xF),
            (byte)((v >> 20) & 0xF), (byte)((v >> 24) & 0xF), (byte)((v >> 28) & 0xF));
    }
}

public sealed class GxmFragmentProgram
{
    public required uint Handle;
    public required GxmProgramInfo Info;
    public GxmBlendInfo? Blend;
    public uint OutputFormat;      // SceGxmOutputRegisterFormat
    public uint MultisampleMode;
    public bool IsMaskUpdate;
    public int RefCount = 1;
    public object? BackendData;
}

public sealed class GxmSyncObject
{
    public required uint Handle;
}

public sealed class GxmRenderTarget
{
    public required uint Handle;
    public ushort Width, Height, ScenesPerFrame;
    public uint MultisampleMode;
    public int DriverMemBlock;
}

/// <summary>Immediate GXM context state.</summary>
public sealed class GxmContext
{
    public readonly uint[] StreamScratch = new uint[16];
    public required uint Handle;
    public uint VdmRing, VdmRingSize, VertexRing, VertexRingSize, FragmentRing, FragmentRingSize;
    public uint VertexRingUsed, FragmentRingUsed;
    public bool VertexDefaultReserved, FragmentDefaultReserved;

    public bool InScene;
    public GxmRenderTarget? RenderTarget;
    public uint FragmentSyncObject;

    public DrawState State;
    public GxmVertexProgram? VertexProgram;
    public GxmFragmentProgram? FragmentProgram;
    public readonly uint[] VertexUniformBuffers = new uint[15];
    public readonly uint[] FragmentUniformBuffers = new uint[15];
    public readonly uint[] Streams = new uint[16];
    public readonly GxmTextureDesc[] FragmentTextures = new GxmTextureDesc[16];
    public readonly GxmTextureDesc[] VertexTextures = new GxmTextureDesc[16];
    public uint PrecomputedVertexState, PrecomputedFragmentState;
    /// <summary>Visibility test state (sceGxmSet*VisibilityTest*, sceGxmSetVisibilityBuffer).</summary>
    public bool VisFrontEnable, VisBackEnable, VisFrontIncrement = true, VisBackIncrement = true;
    public uint VisFrontIndex, VisBackIndex, VisBuffer, VisStride;

    public GxmContext()
    {
        State.Front = SideState.Default;
        State.Back = SideState.Default;
        State.ViewportEnabled = true;
    }
}

public sealed class GxmShaderPatcher
{
    public required uint Handle;
    public uint UserData;
    public uint HostAlloc, HostFree, BufferAlloc, BufferFree, VertexUsseAlloc, VertexUsseFree, FragmentUsseAlloc, FragmentUsseFree;
    public uint BufferMem, BufferMemSize, VertexUsseMem, VertexUsseMemSize, FragmentUsseMem, FragmentUsseMemSize;
    public readonly Dictionary<uint, uint> Registered = new();   // id -> program address
    public readonly Dictionary<(uint Program, ulong AttrHash), GxmVertexProgram> VertexCache = new();
    public readonly Dictionary<(uint Program, uint Format, uint Msaa, ulong Blend, uint VertexProgram), GxmFragmentProgram> FragmentCache = new();
}
