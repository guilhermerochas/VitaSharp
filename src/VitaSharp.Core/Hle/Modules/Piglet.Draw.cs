using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Scenes, draws, clears and pixel reads: the GLES state of the current context becomes GXM state, programs from
/// the shader patcher, default uniform buffers, textures, vertex streams and a sceGxmDraw.</summary>
public sealed partial class Piglet
{
    /// <summary>What a scene renders into: a window/pbuffer surface or a framebuffer object.</summary>
    sealed class PgTarget
    {
        public PgSurface? Surface;
        public PgFramebuffer? Fbo;
        public uint ColorSurface, DepthSurface, ColorAddr, ColorStride, ColorFormat, Width, Height, FragmentSync;
        public int ColorBpp;
        public bool HasDepth, HasStencil;
        /// <summary>Rows go bottom-up in memory (a window or pbuffer, GL's window origin); framebuffer objects keep the
        /// texture's row order (row 0 = t 0).</summary>
        public bool FlipY;
        public bool Same(PgTarget o) => o.ColorAddr == ColorAddr && o.DepthSurface == DepthSurface && o.Width == Width && o.Height == Height;
    }

    PgTarget? _sceneTarget;
    readonly Dictionary<(uint, uint), uint> _renderTargets = [];
    readonly HashSet<uint> _depthWritten = [];
    readonly Dictionary<PgFramebuffer, (uint Color, uint Depth)> _fboSurfaces = [];

    uint RenderTargetFor(uint w, uint h)
    {
        if (_renderTargets.TryGetValue((w, h), out uint rt)) return rt;
        uint p = Alloc(0x14, "render target params"), outp = Alloc(16, "out");
        GuestMemory.Write(p + 4, (ushort)w);
        GuestMemory.Write(p + 6, (ushort)h);
        GuestMemory.Write(p + 8, (ushort)1);
        GuestMemory.W32(p + 16, unchecked((uint)-1));
        G.CreateRenderTarget(p, outp);
        rt = GuestMemory.R32(outp);
        Free(p); Free(outp);
        return _renderTargets[(w, h)] = rt;
    }

    PgTarget DefaultTarget(PgSurface s) => new()
    {
        Surface = s, ColorSurface = s.ColorSurface, DepthSurface = s.DepthSurface, ColorAddr = s.ColorBuffer, ColorStride = s.Stride,
        ColorFormat = s.Config.Is565 ? 0x30100000u : 0u, ColorBpp = s.Config.Is565 ? 2 : 4, Width = s.Width, Height = s.Height,
        FragmentSync = s.Window ? s.SyncObjects[s.Back] : 0, HasDepth = s.Config.Depth > 0, HasStencil = s.Config.Stencil > 0, FlipY = true,
    };

    /// <summary>The draw target of <paramref name="c"/>: its framebuffer object (when complete) or its draw surface.</summary>
    PgTarget? CurrentTarget(PgContext c)
    {
        if (c.Framebuffer is { } f) return FboStatus(f) == Gl.FRAMEBUFFER_COMPLETE ? FboTarget(f) : null;
        return c.Draw is { } s ? DefaultTarget(s) : null;
    }

    PgTarget FboTarget(PgFramebuffer f)
    {
        if (!_fboSurfaces.TryGetValue(f, out var surf))
        {
            surf = (Alloc(0x30, "fbo color surface"), Alloc(0x14, "fbo depth surface"));
            _fboSurfaces[f] = surf;
        }
        var t = new PgTarget { Fbo = f, FlipY = false };
        // colour
        if (f.Color.Texture is { } tex)
        {
            ref var lv = ref tex.Levels[f.Color.Face, f.Color.Level];
            t.ColorAddr = tex.Data + f.Color.Face * tex.FaceStride + lv.Offset;
            t.Width = lv.W; t.Height = lv.H;
            t.ColorBpp = tex.Format!.Bpp / 8;
            t.ColorStride = lv.RowPitch / (uint)t.ColorBpp;
            t.ColorFormat = tex.Format.ColorFormat;
            tex.RenderTarget = true;
        }
        else if (f.Color.Renderbuffer is { } rb)
        {
            t.ColorAddr = rb.Addr; t.Width = rb.Width; t.Height = rb.Height;
            var fmt = RenderbufferColorFormat(rb.InternalFormat)!;
            t.ColorBpp = fmt.Bpp / 8; t.ColorStride = rb.Stride; t.ColorFormat = fmt.ColorFormat;
        }
        G.ColorSurfaceInit(surf.Color, t.ColorFormat, t.Width, t.Height, t.ColorStride, t.ColorAddr);
        t.ColorSurface = surf.Color;
        // depth / stencil: a renderbuffer each (a packed DEPTH24_STENCIL8 one may be attached to both)
        var depthRb = f.Depth.Renderbuffer;
        var stencilRb = f.Stencil.Renderbuffer;
        if (depthRb != null || stencilRb != null)
        {
            var main = depthRb ?? stencilRb!;
            uint fmt = main.InternalFormat switch
            {
                Gl.DEPTH_COMPONENT16 when stencilRb == null => 0x02444000u, // D16
                Gl.STENCIL_INDEX8 when depthRb == null => 0x00022000u,     // S8
                _ => 0x01266000u,                                          // S8D24
            };
            G.DepthStencilSurfaceInit(surf.Depth, fmt, 0x00011000, (main.Width + 31) & ~31u, main.Addr, 0);
            t.DepthSurface = surf.Depth;
            t.HasDepth = depthRb != null;
            t.HasStencil = stencilRb != null || main.IsStencil;
        }
        return t;
    }

    void BeginSceneOn(PgTarget t)
    {
        EnsureGxm();
        if (_sceneTarget != null) EndScene();
        if (t.DepthSurface != 0)
        {
            // depth and stencil survive a scene break in the same frame (and an FBO between frames): load what an
            // earlier scene stored, and always store
            G.DepthStencilForce(t.DepthSurface, _depthWritten.Contains(t.DepthSurface), true);
            G.DepthStencilBackground(t.DepthSurface, 1.0f, 0);
        }
        int r = G.BeginScene(_gxmContext, RenderTargetFor(t.Width, t.Height), t.FragmentSync, t.ColorSurface, t.DepthSurface);
        if (r != 0) Log.WarnOnce("piglet-begin", $"[piglet] sceGxmBeginScene: 0x{r:X8}");
        _sceneTarget = t;
    }

    void EndScene()
    {
        if (_sceneTarget is not { } t) return;
        G.EndScene(_gxmContext);
        if (t.DepthSurface != 0) _depthWritten.Add(t.DepthSurface);
        _sceneTarget = null;
    }

    /// <summary>A scene is open on the context's current target; null (with GL_INVALID_FRAMEBUFFER_OPERATION) when the
    /// bound framebuffer object is incomplete or nothing can be drawn into.</summary>
    PgTarget? SceneFor(PgContext c)
    {
        if (CurrentTarget(c) is not { } t)
        {
            if (c.Framebuffer != null) Err(c, Gl.INVALID_FRAMEBUFFER_OPERATION);
            return null;
        }
        if (_sceneTarget == null || !_sceneTarget.Same(t)) BeginSceneOn(t);
        return _sceneTarget;
    }

    void FinishGpu(PgContext c)
    {
        if (!_gxmReady) return;
        if (_sceneTarget != null) EndScene();
        G.Finish(_gxmContext);
    }

    // ------------------------------------------------------------------ fixed-function state -> GXM

    static uint GxmCompare(uint glFunc) => (glFunc - Gl.NEVER) & 7;

    static uint GxmStencilOp(uint op) => op switch
    {
        Gl.ZERO => 1, Gl.REPLACE => 2, Gl.INCR => 3, Gl.DECR => 4, Gl.INVERT => 5, Gl.INCR_WRAP => 6, Gl.DECR_WRAP => 7, _ => 0,
    };

    static byte GxmBlendFactor(uint f) => f switch
    {
        Gl.ZERO => 0, Gl.ONE => 1, Gl.SRC_COLOR => 2, Gl.ONE_MINUS_SRC_COLOR => 3, Gl.SRC_ALPHA => 4, Gl.ONE_MINUS_SRC_ALPHA => 5,
        Gl.DST_COLOR => 6, Gl.ONE_MINUS_DST_COLOR => 7, Gl.DST_ALPHA => 8, Gl.ONE_MINUS_DST_ALPHA => 9, Gl.SRC_ALPHA_SATURATE => 10,
        // GXM has no constant blend colour: a constant factor acts as a white (1,1,1,1) blend colour
        Gl.CONSTANT_COLOR or Gl.CONSTANT_ALPHA => 1,
        _ => 0,
    };

    static byte GxmBlendFunc(uint eq) => eq switch
    {
        Gl.FUNC_ADD => 1, Gl.FUNC_SUBTRACT => 2, Gl.FUNC_REVERSE_SUBTRACT => 3, Gl.MIN_EXT => 4, Gl.MAX_EXT => 5, _ => 1,
    };

    /// <summary>The SceGxmBlendInfo of the context's blend state and colour mask (0: no blend info, all channels written).</summary>
    uint BlendInfo(PgContext c, bool[] mask, bool blend)
    {
        byte colorMask = (byte)((mask[3] ? 1 : 0) | (mask[0] ? 2 : 0) | (mask[1] ? 4 : 0) | (mask[2] ? 8 : 0));
        if (!blend && colorMask == 0xF) return 0;
        uint p = _scratch + ScratchSize - 16;
        if (blend && c.BlendColor.Any(v => v != 1) && new[] { c.BlendSrcRgb, c.BlendDstRgb, c.BlendSrcAlpha, c.BlendDstAlpha }.Any(f => f is >= Gl.CONSTANT_COLOR and <= Gl.ONE_MINUS_CONSTANT_ALPHA))
            Log.WarnOnce("piglet-blendcolor", "[piglet] constant blend factors: GXM has no blend colour (treated as white)");
        byte cf = blend ? GxmBlendFunc(c.BlendEqRgb) : (byte)0, af = blend ? GxmBlendFunc(c.BlendEqAlpha) : (byte)0;
        byte cs = blend ? GxmBlendFactor(c.BlendSrcRgb) : (byte)1, cd = blend ? GxmBlendFactor(c.BlendDstRgb) : (byte)0;
        byte asrc = blend ? GxmBlendFactor(c.BlendSrcAlpha) : (byte)1, ad = blend ? GxmBlendFactor(c.BlendDstAlpha) : (byte)0;
        if (blend)
        {
            // ONE_MINUS_CONSTANT_* with a white blend colour is ZERO
            if (c.BlendSrcRgb is Gl.ONE_MINUS_CONSTANT_COLOR or Gl.ONE_MINUS_CONSTANT_ALPHA) cs = 0;
            if (c.BlendDstRgb is Gl.ONE_MINUS_CONSTANT_COLOR or Gl.ONE_MINUS_CONSTANT_ALPHA) cd = 0;
            if (c.BlendSrcAlpha is Gl.ONE_MINUS_CONSTANT_COLOR or Gl.ONE_MINUS_CONSTANT_ALPHA) asrc = 0;
            if (c.BlendDstAlpha is Gl.ONE_MINUS_CONSTANT_COLOR or Gl.ONE_MINUS_CONSTANT_ALPHA) ad = 0;
            // MIN/MAX ignore the factors
        }
        GuestMemory.Write(p, colorMask);
        GuestMemory.Write(p + 1, (byte)(cf | (af << 4)));
        GuestMemory.Write(p + 2, (byte)(cs | (cd << 4)));
        GuestMemory.Write(p + 3, (byte)(asrc | (ad << 4)));
        return p;
    }

    /// <summary>The window-space winding (GXM's frame, rows top-down) of GL's front faces.</summary>
    static bool FrontIsFramebufferCcw(PgContext c, PgTarget t) => (c.FrontFace == Gl.CCW) != t.FlipY;

    /// <summary>Sets the raster state of a draw: false when nothing would be drawn (an empty scissor, culling every face).</summary>
    bool ApplyRasterState(PgContext c, PgTarget t, uint glMode, bool clear = false)
    {
        uint ctx = _gxmContext;
        bool polygons = glMode >= Gl.TRIANGLES;
        // culling: GXM CW (1) / CCW (2) name the winding with y up (GL's window sense), so a top-down framebuffer row
        // order (a window) sees the opposite winding; checked against the renderer by PigletHleTests
        uint cull = 0;
        if (c.CullFace && polygons && !clear)
        {
            if (c.CullMode == Gl.FRONT_AND_BACK) return false;
            bool frontCcw = FrontIsFramebufferCcw(c, t);
            bool culledCcw = c.CullMode == Gl.FRONT ? frontCcw : !frontCcw;
            cull = culledCcw ? 1u : 2u;
        }
        G.SetCullMode(ctx, cull);
        if (!clear)
        {
            bool depth = c.DepthTest && t.HasDepth;
            G.SetDepthFunc(ctx, (depth ? GxmCompare(c.DepthFunc) : 7) << 22);
            G.SetDepthWrite(ctx, depth && c.DepthMask);
            bool stencil = c.StencilTest && t.HasStencil;
            var front = stencil ? c.StencilFront : PgStencilSide.Default;
            var back = stencil ? c.StencilBack : PgStencilSide.Default;
            if (FrontIsFramebufferCcw(c, t)) (front, back) = (back, front); // GXM's front faces are the ones CULL_CW keeps
            bool twoSided = !front.Equals(back);
            G.SetTwoSided(ctx, twoSided);
            SetStencilSide(ctx, front, true);
            if (twoSided) SetStencilSide(ctx, back, false);
            if (c.PolygonOffsetFill && polygons) G.SetDepthBias(ctx, (int)c.PolygonFactor, (int)c.PolygonUnits);
            else G.SetDepthBias(ctx, 0, 0);
        }
        G.SetPointLineWidth(ctx, (uint)Math.Clamp((int)MathF.Round(c.LineWidth), 1, 16));
        G.SetPolygonMode(ctx, 0);
        G.SetFragmentProgramEnable(ctx, true);
        // viewport (window origin bottom-left for surfaces) and depth range
        float vx = c.ViewportX, vy = c.ViewportY, vw = c.ViewportW, vh = c.ViewportH;
        float zs = (c.DepthFar - c.DepthNear) / 2, zo = (c.DepthNear + c.DepthFar) / 2;
        if (clear) { vx = 0; vy = 0; vw = t.Width; vh = t.Height; zs = 0.5f; zo = 0.5f; }
        if (t.FlipY) G.SetViewport(ctx, vx + vw / 2, vw / 2, t.Height - (vy + vh / 2), -vh / 2, zo, zs);
        else G.SetViewport(ctx, vx + vw / 2, vw / 2, vy + vh / 2, vh / 2, zo, zs);
        // scissor as the region clip (pixel exact on the renderer)
        int x0 = 0, y0 = 0, x1 = (int)t.Width, y1 = (int)t.Height;
        if (c.ScissorTest)
        {
            x0 = Math.Max(c.ScissorX, 0); x1 = Math.Min(c.ScissorX + c.ScissorW, (int)t.Width);
            int sy0 = c.ScissorY, sy1 = c.ScissorY + c.ScissorH;
            if (t.FlipY) (sy0, sy1) = ((int)t.Height - sy1, (int)t.Height - sy0);
            y0 = Math.Max(sy0, 0); y1 = Math.Min(sy1, (int)t.Height);
        }
        if (x1 <= x0 || y1 <= y0) return false;
        G.SetRegionClip(ctx, 0x80000000u, (uint)x0, (uint)y0, (uint)(x1 - 1), (uint)(y1 - 1));
        return true;
    }

    void SetStencilSide(uint ctx, PgStencilSide s, bool front)
    {
        uint func = GxmCompare(s.Func) << 25;
        if (front) G.SetFrontStencil(ctx, func, GxmStencilOp(s.Fail), GxmStencilOp(s.ZFail), GxmStencilOp(s.ZPass), s.ValueMask & 0xFF, s.WriteMask & 0xFF, (uint)Math.Clamp((int)s.Ref, 0, 255));
        else G.SetBackStencil(ctx, func, GxmStencilOp(s.Fail), GxmStencilOp(s.ZFail), GxmStencilOp(s.ZPass), s.ValueMask & 0xFF, s.WriteMask & 0xFF, (uint)Math.Clamp((int)s.Ref, 0, 255));
    }

    // ------------------------------------------------------------------ vertex input

    static byte GxmAttribFormat(uint type, bool normalized) => type switch
    {
        Gl.UNSIGNED_BYTE => (byte)(normalized ? 4 : 0),
        Gl.BYTE => (byte)(normalized ? 5 : 1),
        Gl.UNSIGNED_SHORT => (byte)(normalized ? 6 : 2),
        Gl.SHORT => (byte)(normalized ? 7 : 3),
        Gl.HALF_FLOAT_OES => 8,
        _ => 9, // FLOAT (FIXED is converted to float)
    };

    /// <summary>Indices of a draw, as GXM takes them (16- or 32-bit, in guest memory).</summary>
    readonly record struct DrawIndices(uint Addr, uint Count, bool U32, uint Min, uint Max);

    /// <summary>The vertex program for the program's attributes and the context's arrays, its streams set; false when
    /// the draw cannot be made (GL error recorded).</summary>
    bool SetupVertexInput(PgContext c, PgProgram p, DrawIndices ix, uint instances, out uint vp)
    {
        vp = 0;
        int n = p.Attribs.Count;
        uint attrs = _scratch, streams = _scratch + 0x100;
        var data = new uint[n];
        for (int i = 0; i < n; i++)
        {
            var a = p.Attribs[i];
            var va = c.Attribs[a.Location];
            uint at = attrs + (uint)i * 8, st = streams + (uint)i * 4;
            byte fmt;
            int comps;
            uint stride, addr;
            ushort indexSource = (ushort)(ix.U32 ? 1 : 0);
            if (va.Enabled)
            {
                comps = va.Size;
                stride = va.EffectiveStride;
                fmt = GxmAttribFormat(va.Type, va.Normalized);
                uint elemBytes = (uint)(va.Size * TypeSize(va.Type));
                uint first = va.Divisor != 0 ? 0 : ix.Min;
                uint last = va.Divisor != 0 ? (instances - 1) / va.Divisor : ix.Max;
                if (va.Buffer is { } b)
                {
                    if (b.Addr == 0 || va.Pointer + (ulong)last * stride + elemBytes > b.Size)
                    {
                        // reading past the buffer object's store is an application error; the GPU reads zeros instead
                        Log.WarnOnce("piglet-vbo-range", "[piglet] an attribute array reaches past its buffer object");
                        if (b.Addr == 0) { Err(c, Gl.INVALID_OPERATION); return false; }
                    }
                    addr = b.Addr + va.Pointer;
                }
                else
                {
                    if (va.Pointer == 0) { Err(c, Gl.INVALID_OPERATION); return false; }
                    // client memory: copied, since the application may change it once the call returns
                    uint bytes = (last - first) * stride + elemBytes;
                    uint copy = Transient(bytes + 4);
                    GuestMemory.Copy(copy, va.Pointer + first * stride, bytes);
                    addr = copy - first * stride;
                }
                if (va.Type == Gl.FIXED)
                {
                    // 16.16 fixed point as float
                    uint conv = Transient((last + 1) * (uint)comps * 4);
                    for (uint v = first; v <= last; v++)
                        for (int k = 0; k < comps; k++)
                            GuestMemory.Write(conv + (v * (uint)comps + (uint)k) * 4, (int)GuestMemory.R32(addr + v * stride + (uint)k * 4) / 65536f);
                    addr = conv; stride = (uint)comps * 4; fmt = 9;
                }
                if (va.Divisor > 1)
                {
                    // GXM steps an instanced stream once per instance: repeat each element divisor times
                    uint es = (uint)comps * (uint)TypeSize(va.Type == Gl.FIXED ? Gl.FLOAT : va.Type);
                    uint rep = Transient(instances * es);
                    for (uint k = 0; k < instances; k++) GuestMemory.Copy(rep + k * es, addr + k / va.Divisor * stride, es);
                    addr = rep; stride = es;
                }
                if (va.Divisor != 0) indexSource = (ushort)(ix.U32 ? 3 : 2);
            }
            else
            {
                // a constant generic attribute: one value, stride 0
                comps = 4;
                fmt = 9;
                stride = 0;
                addr = Transient(16);
                for (int k = 0; k < 4; k++) GuestMemory.Write(addr + (uint)k * 4, va.Current[k]);
            }
            if (stride > 0xFFFF) { Err(c, Gl.INVALID_VALUE); return false; }
            GuestMemory.Write(at, (ushort)i);
            GuestMemory.Write(at + 2, (ushort)0);
            GuestMemory.Write(at + 4, fmt);
            GuestMemory.Write(at + 5, (byte)comps);
            GuestMemory.Write(at + 6, (ushort)a.RegIndex);
            GuestMemory.Write(st, (ushort)stride);
            GuestMemory.Write(st + 2, indexSource);
            data[i] = addr;
        }
        uint outp = _scratch + 0x200;
        int r = G.CreateVertexProgram(_patcher, p.VsId, attrs, (uint)n, streams, (uint)n, outp);
        if (r != 0) { Log.WarnOnce("piglet-vp", $"[piglet] vertex program: 0x{r:X8}"); Err(c, Gl.INVALID_OPERATION); return false; }
        vp = GuestMemory.R32(outp);
        G.SetVertexProgram(_gxmContext, vp);
        for (int i = 0; i < n; i++) G.SetVertexStream(_gxmContext, (uint)i, data[i]);
        return true;
    }

    /// <summary>Default uniform buffers of both programs and their textures.</summary>
    void SetupUniformsAndTextures(PgContext c, PgProgram p, List<PgTexture> used)
    {
        uint outp = _scratch + 0x200;
        uint vbuf = 0, fbuf = 0;
        if (p.VsProgram!.DefaultUniformBufferCount > 0 && G.ReserveVertexDefaultUniformBuffer(_gxmContext, outp) == 0) vbuf = GuestMemory.R32(outp);
        if (p.FsProgram!.DefaultUniformBufferCount > 0 && G.ReserveFragmentDefaultUniformBuffer(_gxmContext, outp) == 0) fbuf = GuestMemory.R32(outp);
        uint src = _scratch + 0x400;
        foreach (var u in p.Uniforms)
        {
            if (u.IsSampler)
            {
                int unit = (int)u.Values[0];
                PgTexture? tex = unit is >= 0 and < TextureUnits ? (u.GlType == Gl.SAMPLER_CUBE ? c.TexCube[unit] : c.Tex2D[unit]) : null;
                uint desc = TextureDescriptor(tex, u.GlType == Gl.SAMPLER_CUBE);
                if (tex != null && desc != _blackTexture && desc != _blackCube) used.Add(tex);
                if (u.VsParam is { } vsp) G.SetVertexTexture(_gxmContext, vsp.ResourceIndex, desc);
                if (u.FsParam is { } fsp) G.SetFragmentTexture(_gxmContext, fsp.ResourceIndex, desc);
                continue;
            }
            int count = u.Values.Length;
            if (count * 4 > 0x8000) count = 0x8000 / 4;
            for (int i = 0; i < count; i++) GuestMemory.Write(src + (uint)i * 4, u.Values[i]);
            if (u.VsParam is { } v && vbuf != 0) G.SetUniformDataF(vbuf, p.VsGxp + v.Offset, 0, (uint)count, src);
            if (u.FsParam is { } f && fbuf != 0) G.SetUniformDataF(fbuf, p.FsGxp + f.Offset, 0, (uint)count, src);
        }
    }

    /// <summary>The fragment program for the program, the blend state and the target's colour format.</summary>
    bool SetupFragmentProgram(PgContext c, PgProgram p, uint vp, bool[] mask, bool blend)
    {
        uint outp = _scratch + 0x204;
        uint blendInfo = BlendInfo(c, mask, blend);
        int r = G.CreateFragmentProgram(_patcher, p.FsId, 1 /* UCHAR4 */, blendInfo, p.VsGxp, outp);
        if (r != 0) { Log.WarnOnce("piglet-fp", $"[piglet] fragment program: 0x{r:X8}"); Err(c, Gl.INVALID_OPERATION); return false; }
        G.SetFragmentProgram(_gxmContext, GuestMemory.R32(outp));
        return true;
    }

    static uint GxmPrimitive(uint mode) => mode switch
    {
        Gl.POINTS => 0x08000000u,
        Gl.LINES or Gl.LINE_STRIP or Gl.LINE_LOOP => 0x04000000u,
        Gl.TRIANGLE_STRIP => 0x0C000000u,
        Gl.TRIANGLE_FAN => 0x10000000u,
        _ => 0u,
    };

    /// <summary>Line strips and loops as line lists (GXM draws lists only).</summary>
    static List<uint>? Lines(uint mode, IReadOnlyList<uint> idx)
    {
        if (mode is not (Gl.LINE_STRIP or Gl.LINE_LOOP)) return null;
        var o = new List<uint>(idx.Count * 2);
        for (int i = 0; i + 1 < idx.Count; i++) { o.Add(idx[i]); o.Add(idx[i + 1]); }
        if (mode == Gl.LINE_LOOP && idx.Count > 1) { o.Add(idx[^1]); o.Add(idx[0]); }
        return o;
    }

    DrawIndices WriteIndices(IReadOnlyList<uint> idx, uint instances)
    {
        uint min = uint.MaxValue, max = 0;
        foreach (uint v in idx) { if (v < min) min = v; if (v > max) max = v; }
        if (idx.Count == 0) min = 0;
        bool u32 = max >= 0xFFFF;
        uint per = (uint)idx.Count, total = per * Math.Max(instances, 1);
        uint addr = Transient(total * (u32 ? 4u : 2u) + 4, 4);
        var span = GuestMemory.Span(addr, (int)(total * (u32 ? 4u : 2u)));
        int o = 0;
        for (uint k = 0; k < Math.Max(instances, 1); k++)
            foreach (uint v in idx)
            {
                if (u32) { System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[o..], v); o += 4; }
                else { System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[o..], (ushort)v); o += 2; }
            }
        return new DrawIndices(addr, per, u32, min, max);
    }

    /// <summary>The common draw path of glDrawArrays/glDrawElements (and their instanced forms).</summary>
    void Draw(PgContext c, uint mode, IReadOnlyList<uint> indices, uint instances, (PgBuffer Buffer, uint Offset, bool U32)? direct = null)
    {
        if (c.Program is not { Linked: true } p) { if (c.Program != null) Err(c, Gl.INVALID_OPERATION); return; }
        if (indices.Count == 0 || instances == 0) return;
        if (SceneFor(c) is not { } t) return;
        if (!ApplyRasterState(c, t, mode)) return;
        var lines = Lines(mode, indices);
        DrawIndices ix;
        if (direct is { } d && lines == null && instances <= 1)
        {
            uint min = uint.MaxValue, max = 0;
            foreach (uint v in indices) { if (v < min) min = v; if (v > max) max = v; }
            ix = new DrawIndices(d.Buffer.Addr + d.Offset, (uint)indices.Count, d.U32, min, max);
        }
        else ix = WriteIndices(lines ?? indices, instances);
        if (!SetupVertexInput(c, p, ix, instances, out uint vp)) return;
        if (!SetupFragmentProgram(c, p, vp, c.ColorMask, c.Blend)) return;
        var used = new List<PgTexture>();
        SetupUniformsAndTextures(c, p, used);
        uint prim = GxmPrimitive(mode), indexType = ix.U32 ? 0x01000000u : 0;
        int r = instances > 1
            ? G.DrawInstanced(_gxmContext, prim, indexType, ix.Addr, ix.Count * instances, ix.Count)
            : G.Draw(_gxmContext, prim, indexType, ix.Addr, ix.Count);
        if (r != 0) Log.WarnOnce($"piglet-draw-{r:X}", $"[piglet] sceGxmDraw: 0x{r:X8}");
        long seq = Emu.Gpu.Submitted;
        foreach (var va in c.Attribs) if (va.Enabled && va.Buffer is { } b) b.GpuSeq = seq;
        if (direct is { } dd) dd.Buffer.GpuSeq = seq;
        foreach (var tex in used) tex.GpuSeq = seq;
        if (t.Fbo?.Color.Texture is { } rt) rt.GpuSeq = seq;
    }

    // ------------------------------------------------------------------ clear

    PgProgram? _clearProgram;
    PgUniform? _clearColorU, _clearDepthU;
    bool _clearProgramFailed;

    const string ClearVs = "void main(float2 aPosition : POSITION, uniform float uDepth, out float4 oPosition : POSITION) { oPosition = float4(aPosition, uDepth, 1.0f); }";
    const string ClearFs = "float4 main(uniform float4 uColor) : COLOR { return uColor; }";

    /// <summary>glClear draws a quad over the whole target with the clear values (the scissor and the masks apply), as
    /// GXM has no clear command.</summary>
    void Clear(PgContext c, uint mask)
    {
        if ((mask & ~(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT | Gl.STENCIL_BUFFER_BIT)) != 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (SceneFor(c) is not { } t) return;
        bool color = (mask & Gl.COLOR_BUFFER_BIT) != 0 && c.ColorMask.Any(m => m);
        bool depth = (mask & Gl.DEPTH_BUFFER_BIT) != 0 && c.DepthMask && t.HasDepth;
        bool stencil = (mask & Gl.STENCIL_BUFFER_BIT) != 0 && t.HasStencil && (c.StencilFront.WriteMask & 0xFF) != 0;
        if (!color && !depth && !stencil) return;
        var p = ClearProgram();
        if (p == null)
        {
            ClearWithoutShaders(c, t, color);
            return;
        }
        if (!ApplyRasterState(c, t, Gl.TRIANGLES, clear: true)) return;
        uint ctx = _gxmContext;
        G.SetDepthFunc(ctx, 7u << 22);
        G.SetDepthWrite(ctx, depth);
        G.SetTwoSided(ctx, false);
        var st = stencil
            ? new PgStencilSide { Func = Gl.ALWAYS, Fail = Gl.REPLACE, ZFail = Gl.REPLACE, ZPass = Gl.REPLACE, Ref = (uint)(c.ClearStencil & 0xFF), ValueMask = 0xFF, WriteMask = c.StencilFront.WriteMask }
            : PgStencilSide.Default with { WriteMask = 0 };
        SetStencilSide(ctx, st, true);
        G.SetDepthBias(ctx, 0, 0);
        uint quad = Transient(32);
        float[] pos = [-1, -1, 1, -1, -1, 1, 1, 1];
        for (int i = 0; i < 8; i++) GuestMemory.Write(quad + (uint)i * 4, pos[i]);
        uint attrs = _scratch, streams = _scratch + 0x100, outp = _scratch + 0x200;
        GuestMemory.Write(attrs, (ushort)0); GuestMemory.Write(attrs + 2, (ushort)0); GuestMemory.Write(attrs + 4, (byte)9);
        GuestMemory.Write(attrs + 5, (byte)2); GuestMemory.Write(attrs + 6, (ushort)p.Attribs[0].RegIndex);
        GuestMemory.Write(streams, (ushort)8); GuestMemory.Write(streams + 2, (ushort)0);
        if (G.CreateVertexProgram(_patcher, p.VsId, attrs, 1, streams, 1, outp) != 0) return;
        uint vp = GuestMemory.R32(outp);
        G.SetVertexProgram(ctx, vp);
        G.SetVertexStream(ctx, 0, quad);
        bool[] cmask = color ? c.ColorMask : [false, false, false, false];
        if (!SetupFragmentProgram(c, p, vp, cmask, false)) return;
        for (int i = 0; i < 4; i++) _clearColorU!.Values[i] = Math.Clamp(c.ClearColor[i], 0, 1);
        _clearDepthU!.Values[0] = Math.Clamp(c.ClearDepth, 0, 1) * 2 - 1;
        SetupUniformsAndTextures(c, p, []);
        uint idx = Transient(8, 4);
        for (uint i = 0; i < 4; i++) GuestMemory.Write(idx + i * 2, (ushort)i);
        G.Draw(ctx, 0x0C000000u, 0, idx, 4);
        if (t.Fbo?.Color.Texture is { } rt) rt.GpuSeq = Emu.Gpu.Submitted;
    }

    /// <summary>Without the runtime compiler there is no clear program: the colour clear becomes a GXM transfer fill of the
    /// (scissored) target between scenes; depth and stencil start a scene from their background values.</summary>
    void ClearWithoutShaders(PgContext c, PgTarget t, bool color)
    {
        Log.WarnOnce("piglet-clear-noshader", "[piglet] glClear without libshacccg.suprx: colour cleared by a transfer fill, depth/stencil reset at the next scene");
        EndScene();
        if (t.DepthSurface != 0) _depthWritten.Remove(t.DepthSurface);
        if (color && t.ColorBpp == 4)
        {
            uint rgba = 0;
            for (int i = 0; i < 4; i++) rgba |= (uint)Math.Clamp((int)MathF.Round(c.ClearColor[i] * 255), 0, 255) << (8 * i);
            G.TransferFill(rgba, 0, t.ColorAddr, 0, 0, t.Width, t.Height, (int)(t.ColorStride * 4));
        }
    }

    PgProgram? ClearProgram()
    {
        if (_clearProgram != null || _clearProgramFailed) return _clearProgram;
        var vs = new PgShader(0, Gl.VERTEX_SHADER) { Source = ClearVs };
        var fs = new PgShader(0, Gl.FRAGMENT_SHADER) { Source = ClearFs };
        CompileShader(vs);
        CompileShader(fs);
        if (!vs.Compiled || !fs.Compiled) { _clearProgramFailed = true; return null; }
        var p = new PgProgram(0) { Vs = vs, Fs = fs };
        LinkProgram(p);
        _clearColorU = p.Uniforms.FirstOrDefault(u => u.Name == "uColor");
        _clearDepthU = p.Uniforms.FirstOrDefault(u => u.Name == "uDepth");
        if (!p.Linked || _clearColorU == null || _clearDepthU == null || p.Attribs.Count != 1) { _clearProgramFailed = true; return null; }
        return _clearProgram = p;
    }

    // ------------------------------------------------------------------ pixel reads

    /// <summary>Reads the colour of the context's read target in GPU order (rendered surfaces are written back to guest
    /// memory first) as RGBA8 rows, top row = GL row 0 (the bottom of a window).</summary>
    byte[]? ReadColor(PgContext c, int x, int y, int w, int h, out PgTarget? target)
    {
        target = c.Framebuffer is { } f ? (FboStatus(f) == Gl.FRAMEBUFFER_COMPLETE ? FboTarget(f) : null) : c.Read is { } s ? DefaultTarget(s) : null;
        if (target is not { } t) return null;
        if (_sceneTarget != null) EndScene();
        var result = new byte[w * h * 4];
        uint size = t.ColorStride * t.Height * (uint)t.ColorBpp;
        long seq = Emu.Gpu.Submit(new Gpu.CallbackCommand
        {
            ReadAddress = t.ColorAddr, ReadSize = size,
            Action = () =>
            {
                for (int row = 0; row < h; row++)
                {
                    int gy = y + row;
                    for (int col = 0; col < w; col++)
                    {
                        int gx = x + col;
                        if (gx < 0 || gy < 0 || gx >= t.Width || gy >= t.Height) continue;
                        uint my = t.FlipY ? t.Height - 1 - (uint)gy : (uint)gy;
                        uint a = t.ColorAddr + (my * t.ColorStride + (uint)gx) * (uint)t.ColorBpp;
                        uint px = PixelToRgba(t.ColorFormat, a);
                        int o = (row * w + col) * 4;
                        result[o] = (byte)px; result[o + 1] = (byte)(px >> 8); result[o + 2] = (byte)(px >> 16); result[o + 3] = (byte)(px >> 24);
                    }
                }
            },
        });
        Emu.Gpu.WaitFor(seq);
        return result;
    }

    /// <summary>A colour surface pixel as RGBA8 (R in the low byte).</summary>
    static uint PixelToRgba(uint colorFormat, uint a)
    {
        static uint E(uint v, int bits) => bits == 0 ? 255 : (v * 255 + ((1u << bits) - 1) / 2) / ((1u << bits) - 1);
        switch (colorFormat)
        {
            case 0x30100000: // U5U6U5_RGB
            {
                uint v = GuestMemory.Read<ushort>(a);
                return E(v >> 11, 5) | E((v >> 5) & 63, 6) << 8 | E(v & 31, 5) << 16 | 0xFF000000;
            }
            case 0x50200000: // U4U4U4U4_RGBA
            {
                uint v = GuestMemory.Read<ushort>(a);
                return E(v >> 12, 4) | E((v >> 8) & 15, 4) << 8 | E((v >> 4) & 15, 4) << 16 | E(v & 15, 4) << 24;
            }
            case 0x40200000: // U5U5U5U1_RGBA
            {
                uint v = GuestMemory.Read<ushort>(a);
                return E(v >> 11, 5) | E((v >> 6) & 31, 5) << 8 | E((v >> 1) & 31, 5) << 16 | ((v & 1) != 0 ? 0xFF000000 : 0);
            }
            case 0x00100000: // U8U8U8U8_ARGB (bytes B G R A)
            {
                uint v = GuestMemory.R32(a);
                return ((v >> 16) & 0xFF) | (v & 0xFF00) | ((v & 0xFF) << 16) | (v & 0xFF000000);
            }
            default:
                return GuestMemory.R32(a);
        }
    }
}
