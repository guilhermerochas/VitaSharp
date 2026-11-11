using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Core.Hle.Modules;

// The GLES2 objects of libScePiglet. Their storage (buffer data, texture levels, renderbuffers) is guest memory the GXM
// draws read; the state is kept here and turned into GXM state at each draw (Piglet.Draw.cs).

/// <summary>A buffer object: its data store in guest memory.</summary>
sealed class PgBuffer(uint name)
{
    public readonly uint Name = name;
    public uint Addr, Size, Usage = Gl.STATIC_DRAW;
    /// <summary>GPU sequence of the last command that reads or writes the store (draws and ordered writes).</summary>
    public long GpuSeq;
    public bool Deleted;
}

/// <summary>Where one mip level of one face sits in a texture's store.</summary>
struct PgLevel
{
    public uint W, H, Offset, Size, RowPitch;
    public bool Defined;
}

/// <summary>The memory layout of a GLES texture format on the GXM side.</summary>
sealed record PgTexFormat(uint GlInternal, uint GxmFormat, int Bpp, bool Compressed, int BlockW = 1, int BlockH = 1, int BlockBytes = 0, bool Pvrtc = false,
    uint ColorFormat = 0xFFFFFFFF)
{
    public bool Renderable => ColorFormat != 0xFFFFFFFF;
}

/// <summary>A texture object (2D or cube map).</summary>
sealed class PgTexture(uint name)
{
    public readonly uint Name = name;
    public uint Target;
    public PgTexFormat? Format;
    public uint Width, Height, GxmType;
    public int LevelCount, Faces = 1;
    public uint Data, DataSize, FaceStride;
    public PgLevel[,] Levels = new PgLevel[1, 1];
    public uint MinFilter = Gl.NEAREST_MIPMAP_LINEAR, MagFilter = Gl.LINEAR, WrapS = Gl.REPEAT, WrapT = Gl.REPEAT;
    public float MaxAnisotropy = 1;
    public bool Immutable, Deleted;
    public long GpuSeq;
    /// <summary>Attached to a framebuffer object at least once: rendered by the GPU, so CPU updates are GPU ordered.</summary>
    public bool RenderTarget;
    public bool HasStorage => Data != 0;
}

/// <summary>A renderbuffer: colour, depth and/or stencil storage for a framebuffer object.</summary>
sealed class PgRenderbuffer(uint name)
{
    public readonly uint Name = name;
    public uint InternalFormat = Gl.RGBA4, Width, Height, Addr, Size, Stride;
    public bool Created, Deleted;
    public bool IsDepth => InternalFormat is Gl.DEPTH_COMPONENT16 or Gl.DEPTH_COMPONENT24_OES or Gl.DEPTH24_STENCIL8_OES;
    public bool IsStencil => InternalFormat is Gl.STENCIL_INDEX8 or Gl.DEPTH24_STENCIL8_OES;
    public bool IsColor => !IsDepth && !IsStencil;
}

sealed class PgAttachment
{
    public uint Type; // GL_NONE, GL_TEXTURE, GL_RENDERBUFFER
    public PgTexture? Texture;
    public PgRenderbuffer? Renderbuffer;
    public uint Level, Face;
    public uint Name => Texture?.Name ?? Renderbuffer?.Name ?? 0;
    public void Clear() { Type = 0; Texture = null; Renderbuffer = null; Level = 0; Face = 0; }
    public const uint TEXTURE = 0x1702;
}

sealed class PgFramebuffer(uint name)
{
    public readonly uint Name = name;
    public readonly PgAttachment Color = new(), Depth = new(), Stencil = new();
    public bool Created, Deleted;
}

sealed class PgShader(uint name, uint type)
{
    public readonly uint Name = name, Type = type;
    public string? Source;
    public bool Compiled, DeletePending;
    public string InfoLog = "";
    /// <summary>The compiled (or loaded) GXM program.</summary>
    public byte[]? Gxp;
    public int Attachments;
}

/// <summary>A vertex input of a linked program.</summary>
sealed record PgAttrib(string Name, uint Location, uint RegIndex, int Components, ParamType Type, uint GlType);

/// <summary>A uniform of a linked program: the values (floats, or the texture unit of a sampler) and where they go in
/// the vertex and fragment programs.</summary>
sealed class PgUniform(string name, uint glType, int arraySize, int components)
{
    public readonly string Name = name;
    public readonly uint GlType = glType;
    public readonly int ArraySize = arraySize, Components = components;
    public readonly float[] Values = new float[Math.Max(1, arraySize) * components];
    public GxpParameter? VsParam, FsParam;
    public bool IsSampler => GlType is Gl.SAMPLER_2D or Gl.SAMPLER_CUBE;
    public bool IsInt => GlType is Gl.INT or Gl.INT_VEC2 or Gl.INT_VEC3 or Gl.INT_VEC4 || IsSampler;
}

sealed class PgProgram(uint name)
{
    public readonly uint Name = name;
    public PgShader? Vs, Fs;
    public readonly Dictionary<string, uint> Bindings = new(StringComparer.Ordinal);
    public bool Linked, Validated, DeletePending, EverLinked;
    public string InfoLog = "";
    // link results
    public uint VsGxp, FsGxp, VsId, FsId;
    public GxpProgram? VsProgram, FsProgram;
    public List<PgAttrib> Attribs = [];
    public List<PgUniform> Uniforms = [];
}

sealed class PgVertexAttrib
{
    public bool Enabled, Normalized;
    public int Size = 4;
    public uint Type = Gl.FLOAT, Stride, Pointer, Divisor;
    public PgBuffer? Buffer;
    public readonly float[] Current = [0, 0, 0, 1];
    public uint EffectiveStride => Stride != 0 ? Stride : (uint)(Size * Piglet.TypeSize(Type));
}

struct PgStencilSide
{
    public uint Func, Ref, ValueMask, WriteMask, Fail, ZFail, ZPass;
    public static PgStencilSide Default => new() { Func = Gl.ALWAYS, ValueMask = 0xFFFFFFFF, WriteMask = 0xFFFFFFFF, Fail = Gl.KEEP, ZFail = Gl.KEEP, ZPass = Gl.KEEP };
}

/// <summary>Objects shared between contexts created with a share_context.</summary>
sealed class PgShared
{
    public readonly Dictionary<uint, PgBuffer> Buffers = [];
    public readonly Dictionary<uint, PgTexture> Textures = [];
    public readonly Dictionary<uint, PgRenderbuffer> Renderbuffers = [];
    public readonly Dictionary<uint, PgShader> Shaders = [];
    public readonly Dictionary<uint, PgProgram> Programs = [];
    public readonly HashSet<uint> BufferNames = [], TextureNames = [], RenderbufferNames = [];
    public uint NextBuffer = 1, NextTexture = 1, NextRenderbuffer = 1, NextShaderOrProgram = 1;
    public int RefCount = 1;
}

/// <summary>An EGL configuration.</summary>
sealed record PgConfig(int Id, int Red, int Green, int Blue, int Alpha, int Depth, int Stencil, int SurfaceType)
{
    public int BufferSize => Red + Green + Blue + Alpha;
    public bool Is565 => Green == 6;
}

/// <summary>An EGL surface: a window (the display, flipped through the GXM display queue) or a pbuffer.</summary>
sealed class PgSurface(uint handle, PgConfig config, bool window)
{
    public readonly uint Handle = handle;
    public readonly PgConfig Config = config;
    public readonly bool Window = window;
    public uint Width, Height, Stride;
    public uint[] ColorBuffers = [];
    public uint[] ColorSurfaces = [];
    public uint[] SyncObjects = [];
    public int Back;
    public uint DepthData, DepthSurface;
    public int SwapInterval = 1;
    public uint SwapBehavior = Egl.BUFFER_DESTROYED;
    public bool LargestPbuffer, Destroyed, Current;
    public int TextureFormat = Egl.NO_TEXTURE, TextureTarget = Egl.NO_TEXTURE, MipmapTexture, MipmapLevel;
    public PgTexture? BoundTexture;
    public uint ColorBuffer => ColorBuffers[Back];
    public uint ColorSurface => ColorSurfaces[Back];
}

/// <summary>A GLES2 rendering context.</summary>
sealed class PgContext(uint handle, PgConfig config, PgShared shared)
{
    public readonly uint Handle = handle;
    public readonly PgConfig Config = config;
    public PgShared Shared = shared;
    public PgSurface? Draw, Read;
    public bool Destroyed, Current, EverCurrent;
    public uint Error;

    // bindings
    public PgBuffer? ArrayBuffer, ElementBuffer;
    public uint ActiveUnit;
    public readonly PgTexture?[] Tex2D = new PgTexture?[Piglet.TextureUnits], TexCube = new PgTexture?[Piglet.TextureUnits];
    public PgFramebuffer? Framebuffer;
    public PgRenderbuffer? Renderbuffer;
    public PgProgram? Program;
    public readonly Dictionary<uint, PgFramebuffer> Framebuffers = [];
    public readonly HashSet<uint> FramebufferNames = [];
    public uint NextFramebuffer = 1;
    public readonly PgVertexAttrib[] Attribs = Enumerable.Range(0, Piglet.MaxAttribs).Select(_ => new PgVertexAttrib()).ToArray();

    // fixed-function state
    public bool CullFace, Blend, Dither = true, StencilTest, DepthTest, ScissorTest, PolygonOffsetFill, SampleAlphaToCoverage, SampleCoverage;
    public uint CullMode = Gl.BACK, FrontFace = Gl.CCW, DepthFunc = Gl.LESS;
    public bool DepthMask = true;
    public float DepthNear, DepthFar = 1, ClearDepth = 1;
    public int ClearStencil;
    public readonly float[] ClearColor = new float[4], BlendColor = new float[4];
    public readonly bool[] ColorMask = [true, true, true, true];
    public PgStencilSide StencilFront = PgStencilSide.Default, StencilBack = PgStencilSide.Default;
    public uint BlendSrcRgb = Gl.ONE, BlendDstRgb = Gl.ZERO, BlendSrcAlpha = Gl.ONE, BlendDstAlpha = Gl.ZERO, BlendEqRgb = Gl.FUNC_ADD, BlendEqAlpha = Gl.FUNC_ADD;
    public int ViewportX, ViewportY, ViewportW, ViewportH, ScissorX, ScissorY, ScissorW, ScissorH;
    public float LineWidth = 1, PolygonFactor, PolygonUnits, SampleCoverageValue = 1;
    public bool SampleCoverageInvert;
    public uint GenerateMipmapHint = Gl.DONT_CARE, DerivativeHint = Gl.DONT_CARE;
    public int UnpackAlignment = 4, PackAlignment = 4;

    public PgTexture? BoundTexture(uint target) => target == Gl.TEXTURE_CUBE_MAP ? TexCube[ActiveUnit] : Tex2D[ActiveUnit];
}
