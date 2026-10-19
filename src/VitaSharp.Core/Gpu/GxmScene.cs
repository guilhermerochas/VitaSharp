using VitaSharp.Core.Gxm;

namespace VitaSharp.Core.Gpu;

public abstract class GpuCommand
{
    public long Sequence;
}

public sealed record ColorSurfaceInfo(uint Data, uint Width, uint Height, uint StrideInPixels, uint ColorFormat, uint SurfaceType, bool Downscale, uint GammaMode);

public sealed record DepthSurfaceInfo(uint DepthData, uint StencilData, uint StrideInSamples, uint Format, uint Type,
    float BackgroundDepth, byte BackgroundStencil, bool ForceLoad, bool ForceStore);

/// <summary>Starts a scene (render pass) on a render target.</summary>
public sealed class BeginSceneCommand : GpuCommand
{
    public required uint Width, Height;
    /// <summary>SceGxmMultisampleMode of the render target: 0 = none, 1 = 2X, 2 = 4X.</summary>
    public uint Multisample;
    public ColorSurfaceInfo? Color;
    public DepthSurfaceInfo? Depth;
}

public sealed class EndSceneCommand : GpuCommand
{
    public (uint Address, uint Value)[] Notifications = [];
    /// <summary>Set by the backend when it signals the notifications itself, once the GPU work and its memory results completed.</summary>
    public bool NotificationsDeferred;
}

/// <summary>sceGxmFinish: the backend completes every submitted GPU operation, including render-target writebacks, before retiring it.</summary>
public sealed class FinishCommand : GpuCommand;

/// <summary>sceGxmMidSceneFlush: what the scene rendered so far is stored to memory, so later draws of the same scene can sample it.</summary>
public sealed class MidSceneFlushCommand : GpuCommand;

public sealed class PresentCommand : GpuCommand
{
    public required DisplayFrame Frame;
}

/// <summary>Arbitrary work that must happen in GPU order (transfers done on the CPU, sync pointsâ€¦).</summary>
public sealed class CallbackCommand : GpuCommand
{
    public required Action Action;
    /// <summary>Guest memory range the action writes (backends must invalidate cached copies of it).</summary>
    public uint WriteAddress, WriteSize;
}

public struct StencilState
{
    public byte Func, StencilFail, DepthFail, DepthPass, CompareMask, WriteMask, Ref;
    public static StencilState Default => new() { Func = 7, CompareMask = 0xFF, WriteMask = 0xFF };
}

public struct SideState
{
    public byte DepthFunc;       // SceGxmDepthFunc >> 22 (0 never .. 7 always)
    public bool DepthWrite;
    public StencilState Stencil;
    public byte PolygonMode;     // 0 fill, 1 line, 2 point, 3 triangle point
    public bool FragmentEnabled;
    public int DepthBiasFactor, DepthBiasUnits;
    public byte PointLineWidth;
    /// <summary>sceGxmSet*LineFillLastPixelEnable: rasterize the last pixel of line segments (not expressible in Vulkan; kept for completeness).</summary>
    public bool LineFillLastPixel;
    public static SideState Default => new() { DepthFunc = 3, DepthWrite = true, Stencil = StencilState.Default, FragmentEnabled = true, PointLineWidth = 1 };
}

/// <summary>Fixed-function state captured at draw time.</summary>
public struct DrawState
{
    public byte CullMode;        // 0 none, 1 cw, 2 ccw
    public bool TwoSided;
    public SideState Front, Back;
    public bool ViewportEnabled;
    public float VpOffX, VpOffY, VpOffZ, VpScaleX, VpScaleY, VpScaleZ;
    public byte RegionClipMode;  // 0 none, 1 all, 2 outside, 3 inside
    public int ClipMinX, ClipMinY, ClipMaxX, ClipMaxY;
    /// <summary>sceGxmSetWBufferEnable: depth uses the vertex W instead of Z/W. Depth ordering is the same under a perspective
    /// projection, so the renderer may ignore it.</summary>
    public bool WBuffer;
    /// <summary>sceGxmSetWClampEnable/Value: clamp of the transformed vertex W (renderer support pending).</summary>
    public bool WClampEnabled;
    public float WClampValue;
}

/// <summary>Raw 16-byte SceGxmTexture control words.</summary>
public readonly record struct GxmTextureDesc(uint W0, uint W1, uint W2, uint W3)
{
    public bool IsNull => W2 == 0 && W1 == 0;
    public uint DataAddress => W2 & ~3u;
    public uint PaletteAddress => (W3 & 0x03FFFFFF) << 6;
    public uint Type => (W1 >> 29) & 7;            // << 29 gives SceGxmTextureType
    public uint Swizzle => (W3 >> 28) & 7;
    public uint Format => (((W0 >> 31) & 1) << 31) | (((W1 >> 24) & 0x1F) << 24) | (Swizzle << 12);
    public uint UAddr => (W0 >> 6) & 7;
    public uint VAddr => (W0 >> 3) & 7;
    public uint MipFilter => (W0 >> 9) & 1;
    public uint MinFilter => (W0 >> 10) & 3;
    public uint MagFilter => (W0 >> 12) & 3;
    public uint MipCountRaw => (W0 >> 17) & 0xF;
    public uint LodBias => (W0 >> 21) & 0x3F;
    public uint GammaMode => (W0 >> 27) & 3;
    /// <summary>
    /// Minimum LOD (4 bits). Word 2 bits 0-1 hold the UPPER two bits (lod_min0) and word 3 bits 26-27 the lower two
    /// (lod_min1), as in the VitaSDK SceGxmTexture layout and Vita3K 50c3e835.
    /// </summary>
    public uint LodMin => ((W2 & 3) << 2) | ((W3 >> 26) & 3);
    public bool Normalize => (W3 >> 31) != 0;

    public uint TextureType => Type << 29;
    public const uint TypeSwizzled = 0x00000000, TypeCube = 0x40000000, TypeLinear = 0x60000000, TypeTiled = 0x80000000,
        TypeSwizzledArbitrary = 0xA0000000, TypeLinearStrided = 0xC0000000, TypeCubeArbitrary = 0xE0000000;

    public uint Width => TextureType is TypeSwizzled or TypeCube ? 1u << (int)((W1 >> 16) & 0xF) : ((W1 >> 12) & 0xFFF) + 1;
    public uint Height => TextureType is TypeSwizzled or TypeCube ? 1u << (int)(W1 & 0xF) : (W1 & 0xFFF) + 1;

    public uint MipCount
    {
        get
        {
            if (TextureType == TypeLinearStrided) return 1;
            uint c = (MipCountRaw + 1) & 15;
            return c == 0 ? 1 : c;
        }
    }

    /// <summary>Byte stride for linear strided textures (encoded across several fields).</summary>
    public uint StrideBytes
    {
        get
        {
            uint s = MipFilter | (MinFilter << 1) | (MipCountRaw << 3) | (LodBias << 7);
            return (s + 1) << 2;
        }
    }

    public static GxmTextureDesc Read(uint addr) => new(Memory.GuestMemory.R32(addr), Memory.GuestMemory.R32(addr + 4),
        Memory.GuestMemory.R32(addr + 8), Memory.GuestMemory.R32(addr + 12));
}

/// <summary>
/// One draw call. Instances are pooled by <see cref="GpuQueue"/> (thousands per frame): the uniform arrays are
/// capacity buffers reused across draws, so readers must use the *Length fields / spans, never the array lengths.
/// </summary>
public sealed class DrawCommand : GpuCommand
{
    public GxmVertexProgram VertexProgram = null!;
    public GxmFragmentProgram FragmentProgram = null!;
    public DrawState State;
    public byte[] VertexUniforms = [];
    public byte[] FragmentUniforms = [];
    public int VertexUniformsLength, FragmentUniformsLength;
    public ReadOnlySpan<byte> VertexUniformSpan => VertexUniforms.AsSpan(0, VertexUniformsLength);
    public ReadOnlySpan<byte> FragmentUniformSpan => FragmentUniforms.AsSpan(0, FragmentUniformsLength);
    /// <summary>Guest addresses of the uniform buffers (index 0-14) bound for this draw.</summary>
    public readonly uint[] VertexUniformAddrs = new uint[15], FragmentUniformAddrs = new uint[15];

    /// <summary>Clears per-draw state before the command is reused.</summary>
    public void Reset()
    {
        VertexUniformsLength = FragmentUniformsLength = 0;
        VisAddress = 0;
        VisIncrement = false;
        InstanceCount = 1;
    }
    public readonly GxmTextureDesc[] VertexTextures = new GxmTextureDesc[16];
    public readonly GxmTextureDesc[] FragmentTextures = new GxmTextureDesc[16];
    public readonly uint[] Streams = new uint[16];
    public byte PrimitiveType;   // 0 tris, 1 lines, 2 points, 3 strip, 4 fan, 5 triangle edges
    public bool Index32;
    public uint IndexAddress;
    public uint IndexCount;
    public uint InstanceCount = 1;
    /// <summary>Visibility test: guest address of this draw's result word (0 = test disabled); INCREMENT counts samples, SET
    /// stores 1 when any sample passes.</summary>
    public uint VisAddress;
    public bool VisIncrement;
}
