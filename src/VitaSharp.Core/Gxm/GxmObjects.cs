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
    private int _dataBytes = -1;
    /// <summary>Bytes of the per-draw uniform data: <see cref="UniformBytes"/> without the split slots when
    /// <see cref="ShaderGenerator.SplitUniformSlots"/> (they are bound apart, see <see cref="UniformBufferSlot.Split"/>).</summary>
    public int DataBytes => !ShaderGenerator.SplitUniformSlots ? UniformBytes : _dataBytes >= 0 ? _dataBytes : _dataBytes = ShaderGenerator.DataBytes(UniformSlots);
    /// <summary>The program has slots read from split bindings.</summary>
    public bool HasSplit => ShaderGenerator.SplitUniformSlots && UniformSlots.Exists(s => s.Split >= 0);
    public required uint TexturesUsed;
    /// <summary>Texture units the program samples as cube maps (samplerCube parameters).</summary>
    public uint CubeSamplers;

    private static readonly ConcurrentDictionary<uint, GxmProgramInfo> s_cache = new();

    public static GxmProgramInfo Get(uint address)
    {
        if (s_cache.TryGetValue(address, out GxmProgramInfo? info))
        {
            // programs can be unloaded and another one loaded at the same address: verify the size/flags quickly
            if (GuestMemory.R32(address + 8) == (uint)info.Program.Data.Length && GuestMemory.R32(address + 0x14) == info.Program.Flags)
            {
                return info;
            }
        }
        var p = GxpProgram.FromGuest(address);
        (List<UniformBufferSlot>? slots, int bytes) = ShaderGenerator.UniformLayout(p);
        info = new GxmProgramInfo { Address = address, Program = p, UniformSlots = slots, UniformBytes = bytes, TexturesUsed = p.TexturesUsed(), CubeSamplers = p.CubeSamplers() };
        s_cache[address] = info;
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
        if (addr == 0)
        {
            return null;
        }

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
    /// <summary>Times the buffer was queued for display (the new buffer of sceGxmDisplayQueueAddEntry) and how many of
    /// those displays are over (a later entry naming it as the old buffer flipped). Guarded by the object itself.</summary>
    public long Queued, Released;
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
    /// <summary>sceGxmDrawPrecomputed scratch copies of the bound uniform buffers/textures (copied into the draw command).</summary>
    public readonly uint[] PreVertexUbs = new uint[15], PreFragmentUbs = new uint[15];
    public readonly GxmTextureDesc[] PreVertexTextures = new GxmTextureDesc[16], PreFragmentTextures = new GxmTextureDesc[16];
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
    /// <summary>The bound precomputed states live in the context's own guest memory (its first two words; nothing else
    /// of ours is stored there), so sceGxmSetPrecomputed*State can be guest code (Hle/HleGuestCode).</summary>
    public const uint PrecomputedVertexStateOffset = 0, PrecomputedFragmentStateOffset = 4;
    public uint PrecomputedVertexState
    {
        get => GuestMemory.R32(Handle + PrecomputedVertexStateOffset);
        set => GuestMemory.W32(Handle + PrecomputedVertexStateOffset, value);
    }
    public uint PrecomputedFragmentState
    {
        get => GuestMemory.R32(Handle + PrecomputedFragmentStateOffset);
        set => GuestMemory.W32(Handle + PrecomputedFragmentStateOffset, value);
    }
    /// <summary>Guest bytes of the cull mode and the front/back polygon modes (set by sceGxmSetCullMode and
    /// sceGxmSet{Front,Back}PolygonMode, which may be guest code); <see cref="SyncGuestState"/> copies them into
    /// <see cref="State"/> before a draw reads it. Zero is the default of all three.</summary>
    public const uint CullModeOffset = 8, FrontPolygonModeOffset = 9, BackPolygonModeOffset = 10;

    // The rest of the state the hot setters write (Killzone: ~1,100 calls per frame) lives in the context's guest memory
    // too when the context has the guest layout (GuestStateFlagOffset = 1: immediate contexts, whose 128 bytes are ours),
    // so those setters can be guest code (Hle/HleGuestCode) instead of a round trip into C# per call. The bytes are the
    // only copy of these fields then: SyncGuestState reads them into State and the fields below wherever the context
    // state is read, and a C# writer of them edits between SyncGuestState and PublishGuestState.
    /// <summary>Byte: 1 = the fields below live in guest memory (guest-code setters write them).</summary>
    public const uint GuestStateFlagOffset = 11;
    /// <summary>Words: the bound vertex / fragment program handles, the front / back visibility test indices.</summary>
    public const uint VertexProgramOffset = 12, FragmentProgramOffset = 16, VisFrontIndexOffset = 20, VisBackIndexOffset = 24;
    /// <summary>Bytes: two-sided mode (0/1), depth functions (SceGxmDepthFunc &gt;&gt; 22), depth writes and fragment
    /// programs enabled (0/1), <see cref="SetFlagsOffset"/> (bit 0 viewport set, bit 1 region clip set), the region clip
    /// mode (SceGxmRegionClipMode &gt;&gt; 30).</summary>
    public const uint TwoSidedOffset = 28, FrontDepthFuncOffset = 29, BackDepthFuncOffset = 30, FrontDepthWriteOffset = 31,
        BackDepthWriteOffset = 32, FrontFragmentEnableOffset = 33, BackFragmentEnableOffset = 34, SetFlagsOffset = 35,
        RegionClipModeOffset = 43;
    /// <summary>Stencil state per side, 7 bytes: func (SceGxmStencilFunc &gt;&gt; 25), stencil fail, depth fail, depth pass
    /// ops (&amp; 7), compare mask, write mask, reference.</summary>
    public const uint FrontStencilOffset = 36, BackStencilOffset = 44;
    /// <summary>Region clip min x, min y, max x, max y (words); the viewport's x offset, x scale, y offset, y scale,
    /// z offset, z scale (floats, sceGxmSetViewport's order).</summary>
    public const uint ClipOffset = 64, ViewportOffset = 80;
    /// <summary>Guest bytes an immediate context's handle points to.</summary>
    public const uint GuestSize = 128;
    public const byte ViewportSetFlag = 1, RegionClipSetFlag = 2;

    /// <summary>The fields above live in guest memory (<see cref="GuestStateFlagOffset"/>).</summary>
    public bool GuestState;
    /// <summary>Resolve a program handle the guest stored (Gxm's registered programs; null = unknown, as the setters did).</summary>
    public Func<uint, GxmVertexProgram?>? ResolveVertexProgram;
    public Func<uint, GxmFragmentProgram?>? ResolveFragmentProgram;
    private uint _syncedVp, _syncedFp;

    private static void ReadStencil(uint a, ref StencilState stencilState)
    {
        ulong v = GuestMemory.Read<ulong>(a);
        stencilState.Func = (byte)v;
        stencilState.StencilFail = (byte)(v >> 8);
        stencilState.DepthFail = (byte)(v >> 16);
        stencilState.DepthPass = (byte)(v >> 24);
        stencilState.CompareMask = (byte)(v >> 32);
        stencilState.WriteMask = (byte)(v >> 40);
        stencilState.Ref = (byte)(v >> 48);
    }

    private static void WriteStencil(uint a, in StencilState stencilState)
    {
        GuestMemory.Write(a, stencilState.Func);
        GuestMemory.Write(a + 1, stencilState.StencilFail);
        GuestMemory.Write(a + 2, stencilState.DepthFail);
        GuestMemory.Write(a + 3, stencilState.DepthPass);
        GuestMemory.Write(a + 4, stencilState.CompareMask);
        GuestMemory.Write(a + 5, stencilState.WriteMask);
        GuestMemory.Write(a + 6, stencilState.Ref);
    }

    /// <summary>Copies the state guest code may have written into <see cref="State"/>, the programs, the visibility indices
    /// and the set flags.</summary>
    public void SyncGuestState()
    {
        uint h = Handle;
        uint w = GuestMemory.R32(h + CullModeOffset);
        State.CullMode = (byte)w;
        State.Front.PolygonMode = (byte)(w >> 8);
        State.Back.PolygonMode = (byte)(w >> 16);
        if (!GuestState)
        {
            return;
        }

        uint vp = GuestMemory.R32(h + VertexProgramOffset), fp = GuestMemory.R32(h + FragmentProgramOffset);
        if (vp != _syncedVp || (vp != 0 && VertexProgram == null))
        {
            _syncedVp = vp;
            VertexProgram = vp == 0 ? null : ResolveVertexProgram?.Invoke(vp);
        }
        if (fp != _syncedFp || (fp != 0 && FragmentProgram == null))
        {
            _syncedFp = fp;
            FragmentProgram = fp == 0 ? null : ResolveFragmentProgram?.Invoke(fp);
        }
        VisFrontIndex = GuestMemory.R32(h + VisFrontIndexOffset);
        VisBackIndex = GuestMemory.R32(h + VisBackIndexOffset);
        ulong b = GuestMemory.Read<ulong>(h + TwoSidedOffset); // bytes 28..35
        State.TwoSided = (byte)b != 0;
        State.Front.DepthFunc = (byte)(b >> 8);
        State.Back.DepthFunc = (byte)(b >> 16);
        State.Front.DepthWrite = (byte)(b >> 24) != 0;
        State.Back.DepthWrite = (byte)(b >> 32) != 0;
        State.Front.FragmentEnabled = (byte)(b >> 40) != 0;
        State.Back.FragmentEnabled = (byte)(b >> 48) != 0;
        byte flags = (byte)(b >> 56);
        ViewportSet = (flags & ViewportSetFlag) != 0;
        RegionClipSet = (flags & RegionClipSetFlag) != 0;
        ReadStencil(h + FrontStencilOffset, ref State.Front.Stencil);
        ReadStencil(h + BackStencilOffset, ref State.Back.Stencil);
        State.RegionClipMode = GuestMemory.Read<byte>(h + RegionClipModeOffset);
        uint c = h + ClipOffset;
        State.ClipMinX = (int)GuestMemory.R32(c);
        State.ClipMinY = (int)GuestMemory.R32(c + 4);
        State.ClipMaxX = (int)GuestMemory.R32(c + 8);
        State.ClipMaxY = (int)GuestMemory.R32(c + 12);
        uint v = h + ViewportOffset;
        State.VpOffX = GuestMemory.Read<float>(v);
        State.VpScaleX = GuestMemory.Read<float>(v + 4);
        State.VpOffY = GuestMemory.Read<float>(v + 8);
        State.VpScaleY = GuestMemory.Read<float>(v + 12);
        State.VpOffZ = GuestMemory.Read<float>(v + 16);
        State.VpScaleZ = GuestMemory.Read<float>(v + 20);
    }

    /// <summary>Writes <see cref="State"/>, the programs, the visibility indices and the set flags into the guest copy
    /// (context creation, a save state load, a C# writer after <see cref="SyncGuestState"/>); nothing without the guest
    /// layout.</summary>
    public void PublishGuestState()
    {
        if (!GuestState)
        {
            return;
        }

        uint h = Handle;
        GuestMemory.Write(h + CullModeOffset, State.CullMode);
        GuestMemory.Write(h + FrontPolygonModeOffset, State.Front.PolygonMode);
        GuestMemory.Write(h + BackPolygonModeOffset, State.Back.PolygonMode);
        GuestMemory.Write(h + GuestStateFlagOffset, (byte)1);
        GuestMemory.W32(h + VertexProgramOffset, _syncedVp = VertexProgram?.Handle ?? 0);
        GuestMemory.W32(h + FragmentProgramOffset, _syncedFp = FragmentProgram?.Handle ?? 0);
        GuestMemory.W32(h + VisFrontIndexOffset, VisFrontIndex);
        GuestMemory.W32(h + VisBackIndexOffset, VisBackIndex);
        GuestMemory.Write(h + TwoSidedOffset, (byte)(State.TwoSided ? 1 : 0));
        GuestMemory.Write(h + FrontDepthFuncOffset, State.Front.DepthFunc);
        GuestMemory.Write(h + BackDepthFuncOffset, State.Back.DepthFunc);
        GuestMemory.Write(h + FrontDepthWriteOffset, (byte)(State.Front.DepthWrite ? 1 : 0));
        GuestMemory.Write(h + BackDepthWriteOffset, (byte)(State.Back.DepthWrite ? 1 : 0));
        GuestMemory.Write(h + FrontFragmentEnableOffset, (byte)(State.Front.FragmentEnabled ? 1 : 0));
        GuestMemory.Write(h + BackFragmentEnableOffset, (byte)(State.Back.FragmentEnabled ? 1 : 0));
        GuestMemory.Write(h + SetFlagsOffset, (byte)((ViewportSet ? ViewportSetFlag : 0) | (RegionClipSet ? RegionClipSetFlag : 0)));
        WriteStencil(h + FrontStencilOffset, in State.Front.Stencil);
        WriteStencil(h + BackStencilOffset, in State.Back.Stencil);
        GuestMemory.Write(h + RegionClipModeOffset, State.RegionClipMode);
        uint c = h + ClipOffset;
        GuestMemory.W32(c, (uint)State.ClipMinX);
        GuestMemory.W32(c + 4, (uint)State.ClipMinY);
        GuestMemory.W32(c + 8, (uint)State.ClipMaxX);
        GuestMemory.W32(c + 12, (uint)State.ClipMaxY);
        uint v = h + ViewportOffset;
        GuestMemory.Write(v, State.VpOffX);
        GuestMemory.Write(v + 4, State.VpScaleX);
        GuestMemory.Write(v + 8, State.VpOffY);
        GuestMemory.Write(v + 12, State.VpScaleY);
        GuestMemory.Write(v + 16, State.VpOffZ);
        GuestMemory.Write(v + 20, State.VpScaleZ);
    }
    /// <summary>Visibility test state (sceGxmSet*VisibilityTest*, sceGxmSetVisibilityBuffer).</summary>
    public bool VisFrontEnable, VisBackEnable, VisFrontIncrement = true, VisBackIncrement = true;
    public uint VisFrontIndex, VisBackIndex, VisBuffer, VisStride;
    /// <summary>Razor user marker nesting (sceGxmPush/PopUserMarker), debug only.</summary>
    public int MarkerDepth;
    /// <summary>sceGxmSetYuvProfile for the two colour space converters (CSC0, CSC1): BT601 standard / full range by default.</summary>
    public readonly byte[] YuvProfile = [0, 2];

    // ---- deferred contexts (sceGxmCreateDeferredContext): draws are recorded into command lists
    public bool Deferred, InCommandList, ViewportSet, RegionClipSet;
    public uint VdmCallback, VertexCallback, FragmentCallback, CallbackData;
    public uint VdmBuffer, VdmBufferSize, VdmUsed, VdmListStart;
    /// <summary>4 guest bytes receiving the size written by the memory callbacks.</summary>
    public uint SizeScratch;
    public List<DrawRecipe>? Recording;

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

/// <summary>A draw recorded by a deferred context: everything sceGxmDraw captures, with uniform buffer addresses (the
/// data is read when the command list is executed, as the GPU reads it by pointer).</summary>
public sealed class DrawRecipe
{
    public required GxmVertexProgram Vp;
    public required GxmFragmentProgram Fp;
    public DrawState State;
    public readonly uint[] VUbs = new uint[15], FUbs = new uint[15], Streams = new uint[16];
    public readonly GxmTextureDesc[] VTex = new GxmTextureDesc[16], FTex = new GxmTextureDesc[16];
    public uint PrimType, IndexType, IndexData, IndexCount, InstanceCount;
    /// <summary>Visibility test of the draw: the index and op only, the word is resolved against the visibility buffer
    /// of the immediate context that executes the list.</summary>
    public bool VisEnabled, VisIncrement;
    public uint VisIndex;
    /// <summary>The deferred context set its own viewport / region clip before this draw; otherwise the executing
    /// immediate context's ones are used.</summary>
    public bool ViewportSet, RegionClipSet;
}

/// <summary>A finished command list (SceGxmCommandList words: "VSCL", id, owner).</summary>
public sealed class RecordedCommandList
{
    public required uint Id, Owner;
    public required DrawRecipe[] Draws;
    public uint VdmStart, VdmEnd;
    /// <summary>Recency stamp (creation or last execution): the table evicts the least recently used list.</summary>
    public long LastUse;
}
