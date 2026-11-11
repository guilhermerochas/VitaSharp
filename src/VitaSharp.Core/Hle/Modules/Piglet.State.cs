using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>The GLES 2.0 fixed-function state, queries, vertex attributes and draw entry points of libScePiglet.</summary>
public sealed partial class Piglet
{
    const string Extensions = "GL_OES_texture_npot GL_OES_element_index_uint GL_OES_rgb8_rgba8 GL_OES_depth24 GL_OES_packed_depth_stencil " +
        "GL_OES_texture_half_float GL_OES_vertex_half_float GL_OES_compressed_ETC1_RGB8_texture GL_EXT_texture_compression_dxt1 " +
        "GL_EXT_texture_compression_s3tc GL_IMG_texture_compression_pvrtc GL_IMG_texture_compression_pvrtc2 GL_EXT_texture_format_BGRA8888 " +
        "GL_EXT_texture_storage GL_EXT_blend_minmax GL_EXT_draw_instanced GL_EXT_instanced_arrays GL_EXT_texture_filter_anisotropic " +
        "GL_OES_standard_derivatives GL_SCE_piglet_shader_binary";

    readonly Dictionary<uint, uint> _glStrings = [];

    // ------------------------------------------------------------------ errors and strings

    [Hle]
    uint glGetError()
    {
        if (Cur is not { } c) return Gl.NO_ERROR;
        uint e = c.Error;
        c.Error = Gl.NO_ERROR;
        return e;
    }

    [Hle]
    uint glGetString(uint name)
    {
        if (Cur is not { } c) return 0;
        string? s = name switch
        {
            Gl.VENDOR => "Sony Computer Entertainment",
            Gl.RENDERER => "Piglet",
            Gl.VERSION => "OpenGL ES 2.0 Piglet",
            Gl.SHADING_LANGUAGE_VERSION => "OpenGL ES GLSL ES 1.00",
            Gl.EXTENSIONS => Extensions,
            _ => null,
        };
        if (s == null) { Err(c, Gl.INVALID_ENUM); return 0; }
        lock (_glStrings)
        {
            if (_glStrings.TryGetValue(name, out uint a)) return a;
            a = Alloc((uint)s.Length + 1, "gl string", MemoryRegion.User, 4);
            GuestMemory.WriteCString(a, s);
            return _glStrings[name] = a;
        }
    }

    // ------------------------------------------------------------------ capabilities

    bool? Capability(PgContext c, uint cap) => cap switch
    {
        Gl.CULL_FACE => c.CullFace, Gl.BLEND => c.Blend, Gl.DITHER => c.Dither, Gl.STENCIL_TEST => c.StencilTest,
        Gl.DEPTH_TEST => c.DepthTest, Gl.SCISSOR_TEST => c.ScissorTest, Gl.POLYGON_OFFSET_FILL => c.PolygonOffsetFill,
        Gl.SAMPLE_ALPHA_TO_COVERAGE => c.SampleAlphaToCoverage, Gl.SAMPLE_COVERAGE => c.SampleCoverage,
        _ => null,
    };

    void SetCapability(uint cap, bool on)
    {
        if (Cur is not { } c) return;
        switch (cap)
        {
            case Gl.CULL_FACE: c.CullFace = on; break;
            case Gl.BLEND: c.Blend = on; break;
            case Gl.DITHER: c.Dither = on; break;
            case Gl.STENCIL_TEST: c.StencilTest = on; break;
            case Gl.DEPTH_TEST: c.DepthTest = on; break;
            case Gl.SCISSOR_TEST: c.ScissorTest = on; break;
            case Gl.POLYGON_OFFSET_FILL: c.PolygonOffsetFill = on; break;
            case Gl.SAMPLE_ALPHA_TO_COVERAGE: c.SampleAlphaToCoverage = on; break;
            case Gl.SAMPLE_COVERAGE: c.SampleCoverage = on; break;
            default: Err(c, Gl.INVALID_ENUM); break;
        }
    }

    [Hle] void glEnable(uint cap) => SetCapability(cap, true);
    [Hle] void glDisable(uint cap) => SetCapability(cap, false);

    [Hle]
    bool glIsEnabled(uint cap)
    {
        if (Cur is not { } c) return false;
        if (Capability(c, cap) is not bool v) { Err(c, Gl.INVALID_ENUM); return false; }
        return v;
    }

    // ------------------------------------------------------------------ blending, masks, clear values

    static bool BlendFactorOk(uint f, bool dst) => f is Gl.ZERO or Gl.ONE or Gl.SRC_COLOR or Gl.ONE_MINUS_SRC_COLOR or Gl.SRC_ALPHA or Gl.ONE_MINUS_SRC_ALPHA
        or Gl.DST_ALPHA or Gl.ONE_MINUS_DST_ALPHA or Gl.DST_COLOR or Gl.ONE_MINUS_DST_COLOR or Gl.CONSTANT_COLOR or Gl.ONE_MINUS_CONSTANT_COLOR
        or Gl.CONSTANT_ALPHA or Gl.ONE_MINUS_CONSTANT_ALPHA || (!dst && f == Gl.SRC_ALPHA_SATURATE);

    static bool BlendEqOk(uint e) => e is Gl.FUNC_ADD or Gl.FUNC_SUBTRACT or Gl.FUNC_REVERSE_SUBTRACT or Gl.MIN_EXT or Gl.MAX_EXT;

    [Hle] void glBlendFunc(uint sfactor, uint dfactor) => glBlendFuncSeparate(sfactor, dfactor, sfactor, dfactor);

    [Hle]
    void glBlendFuncSeparate(uint srcRgb, uint dstRgb, uint srcAlpha, uint dstAlpha)
    {
        if (Cur is not { } c) return;
        if (!BlendFactorOk(srcRgb, false) || !BlendFactorOk(dstRgb, true) || !BlendFactorOk(srcAlpha, false) || !BlendFactorOk(dstAlpha, true))
        { Err(c, Gl.INVALID_ENUM); return; }
        c.BlendSrcRgb = srcRgb; c.BlendDstRgb = dstRgb; c.BlendSrcAlpha = srcAlpha; c.BlendDstAlpha = dstAlpha;
    }

    [Hle] void glBlendEquation(uint mode) => glBlendEquationSeparate(mode, mode);

    [Hle]
    void glBlendEquationSeparate(uint modeRgb, uint modeAlpha)
    {
        if (Cur is not { } c) return;
        if (!BlendEqOk(modeRgb) || !BlendEqOk(modeAlpha)) { Err(c, Gl.INVALID_ENUM); return; }
        c.BlendEqRgb = modeRgb; c.BlendEqAlpha = modeAlpha;
    }

    static float Clamp01(float v) => float.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

    [Hle]
    void glBlendColor(float r, float g, float b, float a)
    {
        if (Cur is not { } c) return;
        c.BlendColor[0] = Clamp01(r); c.BlendColor[1] = Clamp01(g); c.BlendColor[2] = Clamp01(b); c.BlendColor[3] = Clamp01(a);
    }

    [Hle]
    void glClearColor(float r, float g, float b, float a)
    {
        if (Cur is not { } c) return;
        c.ClearColor[0] = Clamp01(r); c.ClearColor[1] = Clamp01(g); c.ClearColor[2] = Clamp01(b); c.ClearColor[3] = Clamp01(a);
    }

    [Hle] void glClearDepthf(float depth) { if (Cur is { } c) c.ClearDepth = Clamp01(depth); }
    [Hle] void glClearStencil(int s) { if (Cur is { } c) c.ClearStencil = s; }

    [Hle]
    void glColorMask(bool r, bool g, bool b, bool a)
    {
        if (Cur is not { } c) return;
        c.ColorMask[0] = r; c.ColorMask[1] = g; c.ColorMask[2] = b; c.ColorMask[3] = a;
    }

    [Hle]
    void glClear(uint mask)
    {
        if (Cur is not { } c) return;
        Clear(c, mask);
    }

    // ------------------------------------------------------------------ faces, depth, stencil

    [Hle]
    void glCullFace(uint mode)
    {
        if (Cur is not { } c) return;
        if (mode is not (Gl.FRONT or Gl.BACK or Gl.FRONT_AND_BACK)) { Err(c, Gl.INVALID_ENUM); return; }
        c.CullMode = mode;
    }

    [Hle]
    void glFrontFace(uint mode)
    {
        if (Cur is not { } c) return;
        if (mode is not (Gl.CW or Gl.CCW)) { Err(c, Gl.INVALID_ENUM); return; }
        c.FrontFace = mode;
    }

    static bool CompareOk(uint f) => f >= Gl.NEVER && f <= Gl.ALWAYS;

    [Hle]
    void glDepthFunc(uint func)
    {
        if (Cur is not { } c) return;
        if (!CompareOk(func)) { Err(c, Gl.INVALID_ENUM); return; }
        c.DepthFunc = func;
    }

    [Hle] void glDepthMask(bool flag) { if (Cur is { } c) c.DepthMask = flag; }

    [Hle]
    void glDepthRangef(float zNear, float zFar)
    {
        if (Cur is not { } c) return;
        c.DepthNear = Clamp01(zNear); c.DepthFar = Clamp01(zFar);
    }

    static bool StencilOpOk(uint op) => op is Gl.KEEP or Gl.ZERO or Gl.REPLACE or Gl.INCR or Gl.DECR or Gl.INVERT or Gl.INCR_WRAP or Gl.DECR_WRAP;

    void ForFaces(PgContext c, uint face, Action<PgContext, bool> set)
    {
        if (face is Gl.FRONT or Gl.FRONT_AND_BACK) set(c, true);
        if (face is Gl.BACK or Gl.FRONT_AND_BACK) set(c, false);
    }

    [Hle] void glStencilFunc(uint func, int reference, uint mask) => glStencilFuncSeparate(Gl.FRONT_AND_BACK, func, reference, mask);

    [Hle]
    void glStencilFuncSeparate(uint face, uint func, int reference, uint mask)
    {
        if (Cur is not { } c) return;
        if (face is not (Gl.FRONT or Gl.BACK or Gl.FRONT_AND_BACK) || !CompareOk(func)) { Err(c, Gl.INVALID_ENUM); return; }
        uint r = (uint)Math.Clamp(reference, 0, 255);
        ForFaces(c, face, (ctx, front) =>
        {
            if (front) { ctx.StencilFront.Func = func; ctx.StencilFront.Ref = r; ctx.StencilFront.ValueMask = mask; }
            else { ctx.StencilBack.Func = func; ctx.StencilBack.Ref = r; ctx.StencilBack.ValueMask = mask; }
        });
    }

    [Hle] void glStencilMask(uint mask) => glStencilMaskSeparate(Gl.FRONT_AND_BACK, mask);

    [Hle]
    void glStencilMaskSeparate(uint face, uint mask)
    {
        if (Cur is not { } c) return;
        if (face is not (Gl.FRONT or Gl.BACK or Gl.FRONT_AND_BACK)) { Err(c, Gl.INVALID_ENUM); return; }
        ForFaces(c, face, (ctx, front) => { if (front) ctx.StencilFront.WriteMask = mask; else ctx.StencilBack.WriteMask = mask; });
    }

    [Hle] void glStencilOp(uint fail, uint zfail, uint zpass) => glStencilOpSeparate(Gl.FRONT_AND_BACK, fail, zfail, zpass);

    [Hle]
    void glStencilOpSeparate(uint face, uint fail, uint zfail, uint zpass)
    {
        if (Cur is not { } c) return;
        if (face is not (Gl.FRONT or Gl.BACK or Gl.FRONT_AND_BACK) || !StencilOpOk(fail) || !StencilOpOk(zfail) || !StencilOpOk(zpass))
        { Err(c, Gl.INVALID_ENUM); return; }
        ForFaces(c, face, (ctx, front) =>
        {
            if (front) { ctx.StencilFront.Fail = fail; ctx.StencilFront.ZFail = zfail; ctx.StencilFront.ZPass = zpass; }
            else { ctx.StencilBack.Fail = fail; ctx.StencilBack.ZFail = zfail; ctx.StencilBack.ZPass = zpass; }
        });
    }

    // ------------------------------------------------------------------ rasterisation

    [Hle]
    void glViewport(int x, int y, int width, int height)
    {
        if (Cur is not { } c) return;
        if (width < 0 || height < 0) { Err(c, Gl.INVALID_VALUE); return; }
        c.ViewportX = x; c.ViewportY = y;
        c.ViewportW = Math.Min(width, MaxRenderbufferSize); c.ViewportH = Math.Min(height, MaxRenderbufferSize);
    }

    [Hle]
    void glScissor(int x, int y, int width, int height)
    {
        if (Cur is not { } c) return;
        if (width < 0 || height < 0) { Err(c, Gl.INVALID_VALUE); return; }
        c.ScissorX = x; c.ScissorY = y; c.ScissorW = width; c.ScissorH = height;
    }

    [Hle]
    void glLineWidth(float width)
    {
        if (Cur is not { } c) return;
        if (!(width > 0)) { Err(c, Gl.INVALID_VALUE); return; }
        c.LineWidth = width;
    }

    [Hle] void glPolygonOffset(float factor, float units) { if (Cur is { } c) { c.PolygonFactor = factor; c.PolygonUnits = units; } }

    [Hle]
    void glSampleCoverage(float value, bool invert)
    {
        if (Cur is not { } c) return;
        c.SampleCoverageValue = Clamp01(value);
        c.SampleCoverageInvert = invert;
    }

    [Hle]
    void glHint(uint target, uint mode)
    {
        if (Cur is not { } c) return;
        if (mode is not (Gl.DONT_CARE or Gl.FASTEST or Gl.NICEST)) { Err(c, Gl.INVALID_ENUM); return; }
        if (target == Gl.GENERATE_MIPMAP_HINT) c.GenerateMipmapHint = mode;
        else if (target == Gl.FRAGMENT_SHADER_DERIVATIVE_HINT_OES) c.DerivativeHint = mode;
        else Err(c, Gl.INVALID_ENUM);
    }

    [Hle]
    void glFlush()
    {
        // GXM queues the work as it is recorded; nothing is held back on the CPU side
    }

    [Hle]
    void glFinish()
    {
        if (Cur is { } c) FinishGpu(c);
    }

    // ------------------------------------------------------------------ state queries

    enum QKind { Int, Float, Bool, Enum }

    /// <summary>The values of a glGet* parameter (null: GL_INVALID_ENUM), with the natural type of the state.</summary>
    (double[] V, QKind Kind)? Query(PgContext c, uint pname)
    {
        static (double[], QKind) I(params double[] v) => (v, QKind.Int);
        static (double[], QKind) F(params double[] v) => (v, QKind.Float);
        static (double[], QKind) B(params bool[] v) => (v.Select(x => x ? 1.0 : 0.0).ToArray(), QKind.Bool);
        static (double[], QKind) E(params uint[] v) => (v.Select(x => (double)x).ToArray(), QKind.Enum);
        int r, g, b, a, depth, stencil;
        if (c.Framebuffer is { } f)
        {
            var cf = f.Color.Texture?.Format ?? (f.Color.Renderbuffer is { } crb ? RenderbufferColorFormat(crb.InternalFormat) : null);
            (r, g, b, a) = cf?.GlInternal switch { Gl.RGBA4 => (4, 4, 4, 4), Gl.RGB5_A1 => (5, 5, 5, 1), Gl.RGB565 => (5, 6, 5, 0), Gl.RGB => (8, 8, 8, 0), null => (0, 0, 0, 0), _ => (8, 8, 8, 8) };
            var drb = f.Depth.Renderbuffer;
            depth = drb == null ? 0 : drb.InternalFormat == Gl.DEPTH_COMPONENT16 ? 16 : 24;
            stencil = f.Stencil.Renderbuffer != null || drb?.IsStencil == true ? 8 : 0;
        }
        else
        {
            var cfg = c.Draw?.Config ?? c.Config;
            (r, g, b, a, depth, stencil) = (cfg.Red, cfg.Green, cfg.Blue, cfg.Alpha, cfg.Depth, cfg.Stencil);
        }
        uint Name<T>(T? o, Func<T, uint> n) where T : class => o == null ? 0 : n(o);
        return pname switch
        {
            Gl.ACTIVE_TEXTURE => E(Gl.TEXTURE0 + c.ActiveUnit),
            Gl.ALIASED_LINE_WIDTH_RANGE => F(1, 16),
            Gl.ALIASED_POINT_SIZE_RANGE => F(1, 64),
            Gl.ALPHA_BITS => I(a), Gl.RED_BITS => I(r), Gl.GREEN_BITS => I(g), Gl.BLUE_BITS => I(b),
            Gl.DEPTH_BITS => I(depth), Gl.STENCIL_BITS => I(stencil),
            Gl.ARRAY_BUFFER_BINDING => I(Name(c.ArrayBuffer, x => x.Name)),
            Gl.ELEMENT_ARRAY_BUFFER_BINDING => I(Name(c.ElementBuffer, x => x.Name)),
            Gl.BLEND => B(c.Blend),
            Gl.BLEND_COLOR => F(c.BlendColor[0], c.BlendColor[1], c.BlendColor[2], c.BlendColor[3]),
            Gl.BLEND_DST_ALPHA => E(c.BlendDstAlpha), Gl.BLEND_DST_RGB => E(c.BlendDstRgb),
            Gl.BLEND_SRC_ALPHA => E(c.BlendSrcAlpha), Gl.BLEND_SRC_RGB => E(c.BlendSrcRgb),
            Gl.BLEND_EQUATION_RGB => E(c.BlendEqRgb), Gl.BLEND_EQUATION_ALPHA => E(c.BlendEqAlpha),
            Gl.COLOR_CLEAR_VALUE => F(c.ClearColor[0], c.ClearColor[1], c.ClearColor[2], c.ClearColor[3]),
            Gl.COLOR_WRITEMASK => B(c.ColorMask),
            Gl.COMPRESSED_TEXTURE_FORMATS => E([.. CompressedFormats]),
            Gl.NUM_COMPRESSED_TEXTURE_FORMATS => I(CompressedFormats.Count),
            Gl.CULL_FACE => B(c.CullFace), Gl.CULL_FACE_MODE => E(c.CullMode),
            Gl.CURRENT_PROGRAM => I(Name(c.Program, x => x.Name)),
            Gl.DEPTH_CLEAR_VALUE => F(c.ClearDepth), Gl.DEPTH_FUNC => E(c.DepthFunc),
            Gl.DEPTH_RANGE => F(c.DepthNear, c.DepthFar), Gl.DEPTH_TEST => B(c.DepthTest), Gl.DEPTH_WRITEMASK => B(c.DepthMask),
            Gl.DITHER => B(c.Dither),
            Gl.FRAMEBUFFER_BINDING => I(Name(c.Framebuffer, x => x.Name)),
            Gl.FRONT_FACE => E(c.FrontFace),
            Gl.GENERATE_MIPMAP_HINT => E(c.GenerateMipmapHint),
            Gl.FRAGMENT_SHADER_DERIVATIVE_HINT_OES => E(c.DerivativeHint),
            Gl.IMPLEMENTATION_COLOR_READ_FORMAT => E(Gl.RGB),
            Gl.IMPLEMENTATION_COLOR_READ_TYPE => E(Gl.UNSIGNED_SHORT_5_6_5),
            Gl.LINE_WIDTH => F(c.LineWidth),
            Gl.MAX_COMBINED_TEXTURE_IMAGE_UNITS => I(TextureUnits),
            Gl.MAX_CUBE_MAP_TEXTURE_SIZE => I(MaxTextureSize),
            Gl.MAX_FRAGMENT_UNIFORM_VECTORS => I(64),
            Gl.MAX_RENDERBUFFER_SIZE => I(MaxRenderbufferSize),
            Gl.MAX_TEXTURE_IMAGE_UNITS => I(TextureUnits),
            Gl.MAX_TEXTURE_SIZE => I(MaxTextureSize),
            Gl.MAX_VARYING_VECTORS => I(8),
            Gl.MAX_VERTEX_ATTRIBS => I(MaxAttribs),
            Gl.MAX_VERTEX_TEXTURE_IMAGE_UNITS => I(TextureUnits),
            Gl.MAX_VERTEX_UNIFORM_VECTORS => I(128),
            Gl.MAX_VIEWPORT_DIMS => I(MaxRenderbufferSize, MaxRenderbufferSize),
            Gl.MAX_TEXTURE_MAX_ANISOTROPY_EXT => F(16),
            Gl.NUM_SHADER_BINARY_FORMATS => I(1),
            Gl.SHADER_BINARY_FORMATS => E(Gl.SHADER_BINARY_GXP_SCE),
            Gl.PACK_ALIGNMENT => I(c.PackAlignment), Gl.UNPACK_ALIGNMENT => I(c.UnpackAlignment),
            Gl.POLYGON_OFFSET_FACTOR => F(c.PolygonFactor), Gl.POLYGON_OFFSET_UNITS => F(c.PolygonUnits),
            Gl.POLYGON_OFFSET_FILL => B(c.PolygonOffsetFill),
            Gl.RENDERBUFFER_BINDING => I(Name(c.Renderbuffer, x => x.Name)),
            Gl.SAMPLE_ALPHA_TO_COVERAGE => B(c.SampleAlphaToCoverage), Gl.SAMPLE_COVERAGE => B(c.SampleCoverage),
            Gl.SAMPLE_BUFFERS => I(0), Gl.SAMPLES => I(0),
            Gl.SAMPLE_COVERAGE_INVERT => B(c.SampleCoverageInvert), Gl.SAMPLE_COVERAGE_VALUE => F(c.SampleCoverageValue),
            Gl.SCISSOR_BOX => I(c.ScissorX, c.ScissorY, c.ScissorW, c.ScissorH), Gl.SCISSOR_TEST => B(c.ScissorTest),
            Gl.SHADER_COMPILER => B(true),
            Gl.STENCIL_BACK_FAIL => E(c.StencilBack.Fail), Gl.STENCIL_BACK_FUNC => E(c.StencilBack.Func),
            Gl.STENCIL_BACK_PASS_DEPTH_FAIL => E(c.StencilBack.ZFail), Gl.STENCIL_BACK_PASS_DEPTH_PASS => E(c.StencilBack.ZPass),
            Gl.STENCIL_BACK_REF => I(c.StencilBack.Ref), Gl.STENCIL_BACK_VALUE_MASK => I(c.StencilBack.ValueMask),
            Gl.STENCIL_BACK_WRITEMASK => I(c.StencilBack.WriteMask),
            Gl.STENCIL_CLEAR_VALUE => I(c.ClearStencil),
            Gl.STENCIL_FAIL => E(c.StencilFront.Fail), Gl.STENCIL_FUNC => E(c.StencilFront.Func),
            Gl.STENCIL_PASS_DEPTH_FAIL => E(c.StencilFront.ZFail), Gl.STENCIL_PASS_DEPTH_PASS => E(c.StencilFront.ZPass),
            Gl.STENCIL_REF => I(c.StencilFront.Ref), Gl.STENCIL_TEST => B(c.StencilTest),
            Gl.STENCIL_VALUE_MASK => I(c.StencilFront.ValueMask), Gl.STENCIL_WRITEMASK => I(c.StencilFront.WriteMask),
            Gl.SUBPIXEL_BITS => I(8),
            Gl.TEXTURE_BINDING_2D => I(Name(c.Tex2D[c.ActiveUnit], x => x.Name)),
            Gl.TEXTURE_BINDING_CUBE_MAP => I(Name(c.TexCube[c.ActiveUnit], x => x.Name)),
            Gl.VIEWPORT => I(c.ViewportX, c.ViewportY, c.ViewportW, c.ViewportH),
            _ => null,
        };
    }

    [Hle]
    void glGetBooleanv(uint pname, uint data)
    {
        if (Cur is not { } c) return;
        if (Query(c, pname) is not var (v, _)) { Err(c, Gl.INVALID_ENUM); return; }
        for (int i = 0; i < v.Length && data != 0; i++) GuestMemory.Write(data + (uint)i, (byte)(v[i] != 0 ? 1 : 0));
    }

    [Hle]
    void glGetFloatv(uint pname, uint data)
    {
        if (Cur is not { } c) return;
        if (Query(c, pname) is not var (v, _)) { Err(c, Gl.INVALID_ENUM); return; }
        for (int i = 0; i < v.Length && data != 0; i++) GuestMemory.Write(data + (uint)i * 4, (float)v[i]);
    }

    /// <summary>glGetIntegerv: colours and depth values map [0, 1] to the full integer range (GLES 2.0 6.1.2), other
    /// floats round; masks keep their bits.</summary>
    [Hle]
    void glGetIntegerv(uint pname, uint data)
    {
        if (Cur is not { } c) return;
        if (Query(c, pname) is not var (v, kind)) { Err(c, Gl.INVALID_ENUM); return; }
        bool normalized = pname is Gl.COLOR_CLEAR_VALUE or Gl.BLEND_COLOR or Gl.DEPTH_RANGE or Gl.DEPTH_CLEAR_VALUE or Gl.SAMPLE_COVERAGE_VALUE;
        for (int i = 0; i < v.Length && data != 0; i++)
        {
            double x = v[i];
            uint o = normalized ? (x >= 1 ? 0x7FFFFFFFu : x <= 0 ? 0 : (uint)(int)Math.Round(x * 2147483647.0))
                : kind == QKind.Float ? (uint)(int)Math.Round(x)
                : (uint)(long)x;
            GuestMemory.W32(data + (uint)i * 4, o);
        }
    }

    // ------------------------------------------------------------------ vertex attributes

    PgVertexAttrib? Attrib(PgContext c, uint index)
    {
        if (index < MaxAttribs) return c.Attribs[index];
        Err(c, Gl.INVALID_VALUE);
        return null;
    }

    [Hle] void glEnableVertexAttribArray(uint index) { if (Cur is { } c && Attrib(c, index) is { } a) a.Enabled = true; }
    [Hle] void glDisableVertexAttribArray(uint index) { if (Cur is { } c && Attrib(c, index) is { } a) a.Enabled = false; }

    void VertexAttrib(uint index, int n, Func<int, float> v)
    {
        if (Cur is not { } c || Attrib(c, index) is not { } a) return;
        for (int i = 0; i < 4; i++) a.Current[i] = i < n ? v(i) : i == 3 ? 1 : 0;
    }

    [Hle] void glVertexAttrib1f(uint index, float x) => VertexAttrib(index, 1, _ => x);
    [Hle] void glVertexAttrib2f(uint index, float x, float y) => VertexAttrib(index, 2, i => i == 0 ? x : y);
    [Hle] void glVertexAttrib3f(uint index, float x, float y, float z) => VertexAttrib(index, 3, i => i switch { 0 => x, 1 => y, _ => z });
    [Hle] void glVertexAttrib4f(uint index, float x, float y, float z, float w) => VertexAttrib(index, 4, i => i switch { 0 => x, 1 => y, 2 => z, _ => w });
    [Hle] void glVertexAttrib1fv(uint index, uint v) => VertexAttrib(index, 1, i => GuestMemory.Read<float>(v + (uint)i * 4));
    [Hle] void glVertexAttrib2fv(uint index, uint v) => VertexAttrib(index, 2, i => GuestMemory.Read<float>(v + (uint)i * 4));
    [Hle] void glVertexAttrib3fv(uint index, uint v) => VertexAttrib(index, 3, i => GuestMemory.Read<float>(v + (uint)i * 4));
    [Hle] void glVertexAttrib4fv(uint index, uint v) => VertexAttrib(index, 4, i => GuestMemory.Read<float>(v + (uint)i * 4));

    [Hle]
    void glVertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, uint pointer)
    {
        if (Cur is not { } c) return;
        if (index >= MaxAttribs || size < 1 || size > 4 || stride < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (type is not (Gl.BYTE or Gl.UNSIGNED_BYTE or Gl.SHORT or Gl.UNSIGNED_SHORT or Gl.FLOAT or Gl.FIXED or Gl.HALF_FLOAT_OES)) { Err(c, Gl.INVALID_ENUM); return; }
        var a = c.Attribs[index];
        a.Size = size; a.Type = type; a.Normalized = normalized; a.Stride = (uint)stride; a.Pointer = pointer;
        a.Buffer = c.ArrayBuffer;
    }

    [Hle]
    void glVertexAttribDivisorEXT(uint index, uint divisor)
    {
        if (Cur is not { } c || Attrib(c, index) is not { } a) return;
        a.Divisor = divisor;
    }

    void GetVertexAttrib(uint index, uint pname, uint dst, bool asFloat)
    {
        if (Cur is not { } c || Attrib(c, index) is not { } a) return;
        double[]? v = pname switch
        {
            Gl.VERTEX_ATTRIB_ARRAY_BUFFER_BINDING => [a.Buffer?.Name ?? 0],
            Gl.VERTEX_ATTRIB_ARRAY_ENABLED => [a.Enabled ? 1 : 0],
            Gl.VERTEX_ATTRIB_ARRAY_SIZE => [a.Size],
            Gl.VERTEX_ATTRIB_ARRAY_STRIDE => [a.Stride],
            Gl.VERTEX_ATTRIB_ARRAY_TYPE => [a.Type],
            Gl.VERTEX_ATTRIB_ARRAY_NORMALIZED => [a.Normalized ? 1 : 0],
            Gl.VERTEX_ATTRIB_ARRAY_DIVISOR_EXT => [a.Divisor],
            Gl.CURRENT_VERTEX_ATTRIB => a.Current.Select(x => (double)x).ToArray(),
            _ => null,
        };
        if (v == null) { Err(c, Gl.INVALID_ENUM); return; }
        for (int i = 0; i < v.Length && dst != 0; i++)
        {
            if (asFloat) GuestMemory.Write(dst + (uint)i * 4, (float)v[i]);
            else GuestMemory.W32(dst + (uint)i * 4, (uint)(int)Math.Round(v[i]));
        }
    }

    [Hle] void glGetVertexAttribfv(uint index, uint pname, uint pparams) => GetVertexAttrib(index, pname, pparams, true);
    [Hle] void glGetVertexAttribiv(uint index, uint pname, uint pparams) => GetVertexAttrib(index, pname, pparams, false);

    [Hle]
    void glGetVertexAttribPointerv(uint index, uint pname, uint pointer)
    {
        if (Cur is not { } c || Attrib(c, index) is not { } a) return;
        if (pname != Gl.VERTEX_ATTRIB_ARRAY_POINTER) { Err(c, Gl.INVALID_ENUM); return; }
        if (pointer != 0) GuestMemory.W32(pointer, a.Pointer);
    }

    // ------------------------------------------------------------------ draws

    static bool ModeOk(uint mode) => mode <= Gl.TRIANGLE_FAN;

    sealed class RangeList(uint first, uint count) : IReadOnlyList<uint>
    {
        public uint this[int index] => first + (uint)index;
        public int Count => (int)count;
        public IEnumerator<uint> GetEnumerator() { for (uint i = 0; i < count; i++) yield return first + i; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    void DrawArrays(uint mode, int first, int count, int instances)
    {
        if (Cur is not { } c) return;
        if (!ModeOk(mode)) { Err(c, Gl.INVALID_ENUM); return; }
        if (first < 0 || count < 0 || instances < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (c.Framebuffer is { } f && FboStatus(f) != Gl.FRAMEBUFFER_COMPLETE) { Err(c, Gl.INVALID_FRAMEBUFFER_OPERATION); return; }
        if (count == 0 || instances == 0) return;
        Draw(c, mode, new RangeList((uint)first, (uint)count), (uint)instances);
    }

    void DrawElements(uint mode, int count, uint type, uint indices, int instances)
    {
        if (Cur is not { } c) return;
        if (!ModeOk(mode) || type is not (Gl.UNSIGNED_BYTE or Gl.UNSIGNED_SHORT or Gl.UNSIGNED_INT)) { Err(c, Gl.INVALID_ENUM); return; }
        if (count < 0 || instances < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (c.Framebuffer is { } f && FboStatus(f) != Gl.FRAMEBUFFER_COMPLETE) { Err(c, Gl.INVALID_FRAMEBUFFER_OPERATION); return; }
        if (count == 0 || instances == 0) return;
        int size = TypeSize(type);
        uint src;
        PgBuffer? eb = c.ElementBuffer;
        if (eb != null)
        {
            if (eb.Addr == 0 || indices + (ulong)count * (uint)size > eb.Size) { Err(c, Gl.INVALID_OPERATION); return; }
            src = eb.Addr + indices;
        }
        else
        {
            if (indices == 0) { Err(c, Gl.INVALID_OPERATION); return; }
            src = indices;
        }
        var list = new uint[count];
        for (int i = 0; i < count; i++)
            list[i] = size switch
            {
                1 => GuestMemory.Read<byte>(src + (uint)i),
                2 => GuestMemory.Read<ushort>(src + (uint)i * 2),
                _ => GuestMemory.R32(src + (uint)i * 4),
            };
        // an element buffer of 16/32-bit indices is drawn in place; bytes and client arrays are converted
        (PgBuffer, uint, bool)? direct = eb != null && size >= 2 && (indices & (uint)(size - 1)) == 0 ? (eb, indices, size == 4) : null;
        Draw(c, mode, list, (uint)instances, direct);
    }

    [Hle] void glDrawArrays(uint mode, int first, int count) => DrawArrays(mode, first, count, 1);
    [Hle] void glDrawArraysInstancedEXT(uint mode, int first, int count, int primcount) => DrawArrays(mode, first, count, primcount);
    [Hle] void glDrawElements(uint mode, int count, uint type, uint indices) => DrawElements(mode, count, type, indices, 1);
    [Hle] void glDrawElementsInstancedEXT(uint mode, int count, uint type, uint indices, int primcount) => DrawElements(mode, count, type, indices, primcount);
}
