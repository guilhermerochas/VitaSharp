namespace VitaSharp.Core.Compat.Ge;

/// <summary>How a draw's vertices are assembled once the GE's primitive was expanded (strips, fans and rectangles
/// become lists).</summary>
public enum GeTopology
{
    Points,
    Lines,
    Triangles,
}

/// <summary>
/// One vertex as the rasterizer takes it: the framebuffer pixel position (X, Y), the depth 0..1 (Z), W for the
/// perspective-correct interpolation (1 for screen-space vertices), the texture coordinates normalised to the
/// texture (0..1 over its width and height) and the colour (R in the low byte, A in the high one).
/// </summary>
public struct GeVertex
{
    public float X;
    public float Y;
    public float Z;
    public float W;
    public float U;
    public float V;
    public uint Color;

    /// <summary>The secondary colour (RGB, alpha unused): the separate specular light, added after the texture
    /// stage when LIGHTMODE bit 0 is set; 0 otherwise.</summary>
    public uint Color1;

    /// <summary>The fog factor, 0..1: the fragment is mixed as factor * colour + (1 - factor) * FOGCOLOR when
    /// FOGENABLE; 1 is no fog.</summary>
    public float Fog = 1f;

    /// <summary>The texture's q: with the texture matrix projection (TMAP mode 1) the rasterizer samples at
    /// (U / Q, V / Q), interpolated perspective-correctly; 1 otherwise.</summary>
    public float Q = 1f;

    /// <summary>A vertex with the neutral Color1, Fog and Q (note: <c>default(GeVertex)</c> has Fog = Q = 0; the
    /// transform always sets them).</summary>
    public GeVertex()
    {
    }
}

/// <summary>A blend factor or equation as the GE's BLEND command encodes it (bits 0..3, 4..7, 8..11).</summary>
public enum GeBlendFactor
{
    /// <summary>Source: the other side's colour; destination: the source colour (the GE names both "A"/"B" by side).</summary>
    OtherColor = 0,
    OneMinusOtherColor = 1,
    SourceAlpha = 2,
    OneMinusSourceAlpha = 3,
    DestinationAlpha = 4,
    OneMinusDestinationAlpha = 5,
    DoubleSourceAlpha = 6,
    OneMinusDoubleSourceAlpha = 7,
    DoubleDestinationAlpha = 8,
    OneMinusDoubleDestinationAlpha = 9,
    Fixed = 10,
}

/// <summary>
/// The state of one draw, read from the GE's command registers when the primitive runs: the targets, the per
/// fragment operations and the texture (decoded to RGBA8 by the GE side, so the rasterizer only samples it).
/// </summary>
public sealed class GeRenderState
{
    /// <summary>Clear mode (CLEARMODE bit 0): the primitive writes its colour, alpha and depth as they are, with no
    /// tests, texture or blending; which of them it writes is <see cref="ClearColor"/>, <see cref="ClearAlpha"/>,
    /// <see cref="ClearDepth"/>.</summary>
    public bool ClearMode;
    public bool ClearColor;
    public bool ClearAlpha;
    public bool ClearDepth;

    /// <summary>The scissor rectangle, inclusive, in framebuffer pixels.</summary>
    public int ScissorLeft;
    public int ScissorTop;
    public int ScissorRight;
    public int ScissorBottom;

    public bool DepthTest;

    /// <summary>ZTEST's function: 0 never, 1 always, 2 equal, 3 not equal, 4 less, 5 less or equal, 6 greater,
    /// 7 greater or equal (on the 16-bit depth, where the GE's "greater" is nearer).</summary>
    public int DepthFunction;
    public bool DepthWrite;

    public bool AlphaTest;

    /// <summary>ATEST's function (as the depth's, on alpha), reference and mask.</summary>
    public int AlphaFunction;
    public int AlphaReference;
    public int AlphaMask;

    public bool Blend;
    public int BlendSource;
    public int BlendDestination;

    /// <summary>BLEND bits 8..11: 0 add, 1 subtract, 2 reverse subtract, 3 min, 4 max, 5 absolute difference.</summary>
    public int BlendEquation;
    public uint BlendFixedA;
    public uint BlendFixedB;

    /// <summary>The colour bits the draw keeps (MASKRGB / MASKALPHA set bits are not written).</summary>
    public uint WriteMaskRgb;
    public uint WriteMaskAlpha;

    public bool Texture;
    public int TextureWidth;
    public int TextureHeight;

    /// <summary>The texture decoded to RGBA8 (row after row), when <see cref="Texture"/>.</summary>
    public uint[] TexturePixels = [];

    /// <summary>The identity of <see cref="TexturePixels"/>' content: the same id is the same texture (a rasterizer may
    /// keep it); a texture whose memory changed gets a new one.</summary>
    public long TextureId;

    /// <summary>TEXFUNC bits 0..2: 0 modulate, 1 decal, 2 blend, 3 replace, 4 add; bit 8: use the texture's alpha;
    /// bit 16: double the colour.</summary>
    public int TextureFunction;
    public bool TextureAlpha;
    public bool ColorDouble;
    public uint TextureEnvironment;

    /// <summary>TEXFILTER: linear magnification / minification; TEXWRAP: clamp on U / V.</summary>
    public bool TextureLinear;
    public bool ClampU;
    public bool ClampV;

    /// <summary>TEXFILTER in full: minification and magnification linear, and the mipmap filter of the minification
    /// (0 none, 1 nearest level, 2 between levels).</summary>
    public bool MinLinear;
    public bool MagLinear;
    public int MipFilter;

    /// <summary>The levels in <see cref="TexturePixels"/> (level 0 first, each half the one before, down to 1x1).</summary>
    public int TextureLevels = 1;

    /// <summary>TEXLEVEL: 0 the GPU picks the level (plus <see cref="LodBias"/>), 1 the level is <see cref="LodBias"/>,
    /// 2 by the slope (taken as 0 here).</summary>
    public int LodMode;
    public float LodBias;

    /// <summary>FOGENABLE (transformed vertices only): each fragment is factor * colour + (1 - factor) * FOGCOLOR with the
    /// vertices' <see cref="GeVertex.Fog"/> factor.</summary>
    public bool Fog;
    public uint FogColor;

    /// <summary>COLORTESTENABLE: the fragment passes when (colour & COLORTESTMASK) compares to (COLORREF & mask) by
    /// COLORTEST's function (0 never, 1 always, 2 equal, 3 not equal).</summary>
    public bool ColorTest;
    public int ColorTestFunction;
    public uint ColorReference;
    public uint ColorTestMask;

    /// <summary>DITHERENABLE into a 16-bit framebuffer: DITH0..3's 4x4 matrix of signed 4-bit values added to the
    /// colour (in 1/255) by the pixel's position; row r packed in <see cref="DitherRows"/>[r], column c at bits 4c.</summary>
    public bool Dither;
    public readonly int[] DitherRows = new int[4];

    /// <summary>LOGICOPENABLE: LOGICOP's operation (the 16 logic ops in Vulkan's order) replaces blending.</summary>
    public bool LogicOp;
    public int LogicOpCode;

    /// <summary>STENCILTESTENABLE: the framebuffer's alpha is the stencil (8 bits in 8888, 4 in 4444, 1 in 5551, none
    /// in 565). STENCILTEST: the function (as the depth's) comparing (reference & mask) with (stencil & mask);
    /// STENCILOP: the operations on a stencil failure, a depth failure and a pass (0 keep, 1 zero, 2 replace by the
    /// reference, 3 invert, 4 increment, 5 decrement); the stencil bits written are those MASKALPHA does not mask.</summary>
    public bool StencilTest;
    public int StencilFunction;
    public int StencilReference;
    public int StencilMask;
    public int StencilFailOperation;
    public int StencilDepthFailOperation;
    public int StencilPassOperation;
    public int StencilWriteMask = 0xFF;
}

/// <summary>
/// A buffer the GE draws into, in PSP memory: the colour framebuffer (<see cref="Format"/> one of the GePixels formats)
/// or the 16-bit depth buffer (<see cref="DepthFormat"/>), <see cref="Stride"/> pixels a row and the rows the draw can
/// reach (<see cref="Height"/>, its scissor's bottom + 1).
/// </summary>
public readonly record struct GeSurface(uint Address, int Stride, int Height, int Format)
{
    /// <summary>The format of a depth buffer.</summary>
    public const int DepthFormat = 4;

    public int BytesPerPixel => Format == GePixels.Format8888 ? 4 : 2;

    public int Bytes => Stride * Height * BytesPerPixel;

    public bool Overlaps(uint address, int length)
    {
        return address < Address + (uint)Bytes && Address < address + (uint)length;
    }
}

/// <summary>
/// Draws the GE's batches (the Vulkan rasterizer in VitaSharp.Renderer.Vulkan; tests give their own). A rasterizer
/// may keep the surfaces on its side between draws: memory has their pixels again after <see cref="Flush"/>, which the
/// GE calls when its list stops (END, the stall address) and before memory a surface overlaps is read (the LCD DMA, a
/// block transfer, a texture drawn into); <see cref="Invalidate"/> says memory changed outside the rasterizer (a
/// block transfer into a surface). Between flushes the GE side does not read or write the surfaces' memory.
/// </summary>
public interface IGeRasterizer
{
    /// <summary>A batch into <paramref name="color"/> (and <paramref name="depth"/> when the draw tests or writes
    /// depth), memory being where the surfaces live.</summary>
    void Draw(GeTopology topology, ReadOnlySpan<GeVertex> vertices, GeRenderState state, GeSurface color, GeSurface? depth, IGeMemory memory);

    /// <summary>The draws done, every surface they changed written back to memory.</summary>
    void Flush(IGeMemory memory);

    /// <summary>A surface the rasterizer changed and has not written back overlaps the range.</summary>
    bool Holds(uint address, int length);

    /// <summary>Memory in the range was changed outside the rasterizer: the surfaces in it are read again.</summary>
    void Invalidate(uint address, int length);
}
