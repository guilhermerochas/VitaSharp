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

    /// <summary>FOGENABLE (transformed vertices only): each fragment is factor * colour + (1 - factor) * FOGCOLOR with the
    /// vertices' <see cref="GeVertex.Fog"/> factor.</summary>
    public bool Fog;
    public uint FogColor;
}

/// <summary>
/// The framebuffer region a draw renders into, as RGBA8 and 16-bit depth arrays the GE side converts from and back
/// to PSP memory: <see cref="Width"/> x <see cref="Height"/> pixels from the framebuffer's first row.
/// </summary>
public sealed class GeTarget
{
    public int Width;
    public int Height;
    public uint[] Color = [];

    /// <summary>The depth buffer (null when the draw neither tests nor writes depth).</summary>
    public ushort[]? Depth;
}

/// <summary>Draws the GE's batches (the Vulkan rasterizer in VitaSharp.Renderer.Vulkan; tests give their own).</summary>
public interface IGeRasterizer
{
    void Draw(GeTopology topology, ReadOnlySpan<GeVertex> vertices, GeRenderState state, GeTarget target);
}
