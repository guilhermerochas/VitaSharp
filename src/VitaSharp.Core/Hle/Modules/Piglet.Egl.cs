using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>EGL 1.4 of libScePiglet: one display (the Vita screen), the configurations below, window surfaces flipped
/// through the GXM display queue, pbuffers (also bindable as textures) and OpenGL ES 2.0 contexts.</summary>
public sealed partial class Piglet
{
    const uint EglDisplay = 1;
    const int EglTrue = 1, EglFalse = 0;

    /// <summary>The configurations: 32-bit colour with and without depth/stencil for windows and pbuffers, and 16-bit
    /// colour pbuffers (the display scans out 32-bit buffers only).</summary>
    static readonly PgConfig[] Configs =
    [
        new(1, 8, 8, 8, 8, 24, 8, Egl.WINDOW_BIT | Egl.PBUFFER_BIT),
        new(2, 8, 8, 8, 8, 0, 0, Egl.WINDOW_BIT | Egl.PBUFFER_BIT),
        new(3, 8, 8, 8, 8, 16, 0, Egl.WINDOW_BIT | Egl.PBUFFER_BIT),
        new(4, 5, 6, 5, 0, 24, 8, Egl.PBUFFER_BIT),
        new(5, 5, 6, 5, 0, 0, 0, Egl.PBUFFER_BIT),
    ];

    bool _eglInitialized;
    uint _nextEglHandle = 0x1000;
    readonly Dictionary<uint, PgContext> _contexts = [];
    readonly Dictionary<uint, PgSurface> _surfaces = [];
    uint _vendorStr, _versionStr, _extensionsStr, _apisStr;

    int EglError(int e) { lock (_lock) _eglErrorByThread[ThreadKey] = e; return e; }
    int EglOk() => EglError(Egl.SUCCESS);
    int EglFail(int e) { EglError(e); return EglFalse; }

    /// <summary>The display is valid and initialised (EGL_BAD_DISPLAY / EGL_NOT_INITIALIZED otherwise).</summary>
    bool CheckDisplay(uint dpy)
    {
        if (dpy != EglDisplay) { EglError(Egl.BAD_DISPLAY); return false; }
        if (!_eglInitialized) { EglError(Egl.NOT_INITIALIZED); return false; }
        return true;
    }

    static PgConfig? ConfigOf(uint handle) => handle >= 1 && handle <= Configs.Length ? Configs[handle - 1] : null;

    static IEnumerable<(int Attr, int Value)> Attribs(uint list)
    {
        if (list == 0) yield break;
        for (int i = 0; i < 256; i++)
        {
            int a = (int)GuestMemory.R32(list + (uint)i * 8);
            if (a == Egl.NONE) yield break;
            yield return (a, (int)GuestMemory.R32(list + (uint)i * 8 + 4));
        }
    }

    [Hle]
    uint eglGetDisplay(uint nativeDisplay)
    {
        EglOk();
        return EglDisplay; // EGL_DEFAULT_DISPLAY and any native display are the one screen
    }

    [Hle]
    int eglInitialize(uint dpy, Ptr<int> major, Ptr<int> minor)
    {
        if (dpy != EglDisplay) return EglFail(Egl.BAD_DISPLAY);
        _eglInitialized = true;
        major.TrySet(1);
        minor.TrySet(4);
        EglOk();
        return EglTrue;
    }

    [Hle]
    int eglTerminate(uint dpy)
    {
        if (dpy != EglDisplay) return EglFail(Egl.BAD_DISPLAY);
        lock (_lock)
        {
            // objects not current are destroyed now; current ones when they are released
            foreach (var c in _contexts.Values.ToList()) { c.Destroyed = true; if (!c.Current) _contexts.Remove(c.Handle); }
            foreach (var s in _surfaces.Values.ToList()) { if (!s.Current) DestroySurfaceNow(s); else s.Destroyed = true; }
        }
        _eglInitialized = false;
        EglOk();
        return EglTrue;
    }

    [Hle] int eglGetError() { lock (_lock) { int e = _eglErrorByThread.GetValueOrDefault(ThreadKey, Egl.SUCCESS); _eglErrorByThread[ThreadKey] = Egl.SUCCESS; return e; } }

    [Hle]
    uint eglQueryString(uint dpy, int name)
    {
        if (!CheckDisplay(dpy)) return 0;
        uint Str(ref uint slot, string s)
        {
            if (slot == 0) { slot = Alloc((uint)s.Length + 1, "egl string", MemoryRegion.User, 4); GuestMemory.WriteCString(slot, s); }
            return slot;
        }
        uint r = name switch
        {
            Egl.VENDOR => Str(ref _vendorStr, "Sony Computer Entertainment"),
            Egl.VERSION => Str(ref _versionStr, "1.4 Piglet"),
            Egl.EXTENSIONS => Str(ref _extensionsStr, ""),
            Egl.CLIENT_APIS => Str(ref _apisStr, "OpenGL_ES"),
            _ => 0,
        };
        if (r == 0) { EglError(Egl.BAD_PARAMETER); return 0; }
        EglOk();
        return r;
    }

    [Hle]
    int eglGetConfigs(uint dpy, uint configs, int configSize, Ptr<int> numConfig)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (numConfig.IsNull) return EglFail(Egl.BAD_PARAMETER);
        int n = configs == 0 ? Configs.Length : Math.Min(configSize, Configs.Length);
        for (int i = 0; i < n && configs != 0; i++) GuestMemory.W32(configs + (uint)i * 4, (uint)Configs[i].Id);
        numConfig.Value = Math.Max(n, 0);
        EglOk();
        return EglTrue;
    }

    static int ConfigAttrib(PgConfig c, int attr) => attr switch
    {
        Egl.BUFFER_SIZE => c.BufferSize,
        Egl.RED_SIZE => c.Red, Egl.GREEN_SIZE => c.Green, Egl.BLUE_SIZE => c.Blue, Egl.ALPHA_SIZE => c.Alpha,
        Egl.DEPTH_SIZE => c.Depth, Egl.STENCIL_SIZE => c.Stencil,
        Egl.CONFIG_CAVEAT => Egl.NONE, Egl.CONFIG_ID => c.Id, Egl.LEVEL => 0,
        Egl.MAX_PBUFFER_WIDTH or Egl.MAX_PBUFFER_HEIGHT => MaxRenderbufferSize, Egl.MAX_PBUFFER_PIXELS => MaxRenderbufferSize * MaxRenderbufferSize,
        Egl.NATIVE_RENDERABLE => 0, Egl.NATIVE_VISUAL_ID => 0, Egl.NATIVE_VISUAL_TYPE => Egl.NONE,
        Egl.SAMPLES => 0, Egl.SAMPLE_BUFFERS => 0, Egl.SURFACE_TYPE => c.SurfaceType,
        Egl.TRANSPARENT_TYPE => Egl.NONE, Egl.TRANSPARENT_RED_VALUE or Egl.TRANSPARENT_GREEN_VALUE or Egl.TRANSPARENT_BLUE_VALUE => 0,
        Egl.BIND_TO_TEXTURE_RGB => (c.SurfaceType & Egl.PBUFFER_BIT) != 0 ? 1 : 0,
        Egl.BIND_TO_TEXTURE_RGBA => (c.SurfaceType & Egl.PBUFFER_BIT) != 0 && c.Alpha > 0 ? 1 : 0,
        Egl.MIN_SWAP_INTERVAL => 0, Egl.MAX_SWAP_INTERVAL => 4, Egl.LUMINANCE_SIZE => 0, Egl.ALPHA_MASK_SIZE => 0,
        Egl.COLOR_BUFFER_TYPE => Egl.RGB_BUFFER, Egl.RENDERABLE_TYPE or Egl.CONFORMANT => Egl.OPENGL_ES2_BIT,
        _ => int.MinValue,
    };

    [Hle]
    int eglGetConfigAttrib(uint dpy, uint config, int attribute, Ptr<int> value)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (ConfigOf(config) is not { } c) return EglFail(Egl.BAD_CONFIG);
        int v = ConfigAttrib(c, attribute);
        if (v == int.MinValue) return EglFail(Egl.BAD_ATTRIBUTE);
        if (value.IsNull) return EglFail(Egl.BAD_PARAMETER);
        value.Value = v;
        EglOk();
        return EglTrue;
    }

    /// <summary>EGL 1.4 table 3.4: which attributes are "at least", exact, mask or ignored, and the sort order. Every
    /// configuration is OpenGL ES 2 only, so an unset EGL_RENDERABLE_TYPE does not ask for OpenGL ES 1 (EGL's default)
    /// and leaves no configuration.</summary>
    [Hle]
    int eglChooseConfig(uint dpy, uint attribList, uint configs, int configSize, Ptr<int> numConfig)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (numConfig.IsNull) return EglFail(Egl.BAD_PARAMETER);
        var want = new Dictionary<int, int>
        {
            [Egl.SURFACE_TYPE] = Egl.WINDOW_BIT, [Egl.RENDERABLE_TYPE] = Egl.DONT_CARE, [Egl.COLOR_BUFFER_TYPE] = Egl.RGB_BUFFER,
            [Egl.CONFIG_CAVEAT] = Egl.DONT_CARE, [Egl.CONFIG_ID] = Egl.DONT_CARE, [Egl.TRANSPARENT_TYPE] = Egl.NONE,
            [Egl.MATCH_NATIVE_PIXMAP] = Egl.NONE, [Egl.NATIVE_RENDERABLE] = Egl.DONT_CARE, [Egl.NATIVE_VISUAL_TYPE] = Egl.DONT_CARE,
            [Egl.MIN_SWAP_INTERVAL] = Egl.DONT_CARE, [Egl.MAX_SWAP_INTERVAL] = Egl.DONT_CARE,
            [Egl.BIND_TO_TEXTURE_RGB] = Egl.DONT_CARE, [Egl.BIND_TO_TEXTURE_RGBA] = Egl.DONT_CARE,
        };
        foreach (var (a, v) in Attribs(attribList))
        {
            if (a != Egl.MATCH_NATIVE_PIXMAP && a != Egl.LEVEL && ConfigAttrib(Configs[0], a) == int.MinValue) return EglFail(Egl.BAD_ATTRIBUTE);
            want[a] = v;
        }
        if (want.TryGetValue(Egl.CONFIG_ID, out int id) && id != Egl.DONT_CARE)
        {
            var exact = Configs.Where(c => c.Id == id).ToList();
            return WriteConfigs(exact, configs, configSize, numConfig);
        }
        bool Match(PgConfig c)
        {
            foreach (var (a, v) in want)
            {
                if (v == Egl.DONT_CARE) continue;
                int have = ConfigAttrib(c, a);
                switch (a)
                {
                    case Egl.SURFACE_TYPE or Egl.RENDERABLE_TYPE or Egl.CONFORMANT:
                        if ((have & v) != v) return false;
                        break;
                    case Egl.BUFFER_SIZE or Egl.RED_SIZE or Egl.GREEN_SIZE or Egl.BLUE_SIZE or Egl.ALPHA_SIZE or Egl.DEPTH_SIZE or Egl.STENCIL_SIZE
                        or Egl.SAMPLES or Egl.SAMPLE_BUFFERS or Egl.LUMINANCE_SIZE or Egl.ALPHA_MASK_SIZE:
                        if (have < v) return false;
                        break;
                    case Egl.MATCH_NATIVE_PIXMAP or Egl.MAX_PBUFFER_WIDTH or Egl.MAX_PBUFFER_HEIGHT or Egl.MAX_PBUFFER_PIXELS or Egl.NATIVE_VISUAL_ID:
                        break; // ignored for selection
                    case Egl.LEVEL:
                        if (v != 0) return false;
                        break;
                    default:
                        if (have != v) return false;
                        break;
                }
            }
            return true;
        }
        int Bits(PgConfig c, int a) => want.TryGetValue(a, out int v) && v > 0 && v != Egl.DONT_CARE ? ConfigAttrib(c, a) : 0;
        var list = Configs.Where(Match)
            .OrderByDescending(c => Bits(c, Egl.RED_SIZE) + Bits(c, Egl.GREEN_SIZE) + Bits(c, Egl.BLUE_SIZE) + Bits(c, Egl.ALPHA_SIZE))
            .ThenBy(c => c.BufferSize).ThenBy(c => c.Depth).ThenBy(c => c.Stencil).ThenBy(c => c.Id).ToList();
        return WriteConfigs(list, configs, configSize, numConfig);
    }

    int WriteConfigs(List<PgConfig> list, uint configs, int configSize, Ptr<int> numConfig)
    {
        int n = configs == 0 ? list.Count : Math.Min(Math.Max(configSize, 0), list.Count);
        for (int i = 0; i < n && configs != 0; i++) GuestMemory.W32(configs + (uint)i * 4, (uint)list[i].Id);
        numConfig.Value = n;
        EglOk();
        return EglTrue;
    }

    [Hle]
    int eglBindAPI(uint api)
    {
        if (api != Egl.OPENGL_ES_API) return EglFail(Egl.BAD_PARAMETER);
        lock (_lock) _apiByThread[ThreadKey] = (int)api;
        EglOk();
        return EglTrue;
    }

    [Hle] uint eglQueryAPI() { EglOk(); lock (_lock) return (uint)_apiByThread.GetValueOrDefault(ThreadKey, Egl.OPENGL_ES_API); }

    // ------------------------------------------------------------------ surfaces

    [Hle]
    uint eglCreateWindowSurface(uint dpy, uint config, uint window, uint attribList)
    {
        if (!CheckDisplay(dpy)) return 0;
        if (ConfigOf(config) is not { } c) { EglError(Egl.BAD_CONFIG); return 0; }
        if ((c.SurfaceType & Egl.WINDOW_BIT) == 0) { EglError(Egl.BAD_MATCH); return 0; }
        foreach (var (a, v) in Attribs(attribList))
        {
            if (a == Egl.RENDER_BUFFER) { if (v != Egl.BACK_BUFFER && v != Egl.SINGLE_BUFFER) { EglError(Egl.BAD_ATTRIBUTE); return 0; } }
            else if (a is not (0x3087 or 0x3088)) { EglError(Egl.BAD_ATTRIBUTE); return 0; } // VG colourspace/alpha format: no effect
        }
        lock (_lock)
            if (_surfaces.Values.Any(s => s.Window && !s.Destroyed)) { EglError(Egl.BAD_ALLOC); return 0; } // one window: the screen
        EnsureGxm();
        var surf = new PgSurface(_nextEglHandle++, c, true) { Width = DisplayWidth, Height = DisplayHeight, Stride = DisplayWidth };
        const int count = 3;
        surf.ColorBuffers = new uint[count];
        surf.ColorSurfaces = new uint[count];
        surf.SyncObjects = new uint[count];
        uint outp = Alloc(16, "out");
        for (int i = 0; i < count; i++)
        {
            surf.ColorBuffers[i] = Alloc(surf.Stride * surf.Height * 4, "display buffer", MemoryRegion.Cdram, 256 * 1024);
            surf.ColorSurfaces[i] = Alloc(0x30, "color surface");
            G.ColorSurfaceInit(surf.ColorSurfaces[i], 0 /* U8U8U8U8_ABGR */, surf.Width, surf.Height, surf.Stride, surf.ColorBuffers[i]);
            G.SyncObjectCreate(outp);
            surf.SyncObjects[i] = GuestMemory.R32(outp);
        }
        Free(outp);
        CreateDepthStencil(surf);
        lock (_lock) _surfaces[surf.Handle] = surf;
        Log.Info($"[piglet] window surface 0x{surf.Handle:X} {surf.Width}x{surf.Height} config {c.Id} (native window 0x{window:X})");
        EglOk();
        return surf.Handle;
    }

    void CreateDepthStencil(PgSurface surf)
    {
        if (surf.Config.Depth == 0 && surf.Config.Stencil == 0) return;
        uint w = (surf.Width + 31) & ~31u, h = (surf.Height + 31) & ~31u;
        surf.DepthData = Alloc(w * h * 4, "depth buffer", MemoryRegion.User, 4096);
        surf.DepthSurface = Alloc(0x14, "depth surface");
        uint fmt = surf.Config.Stencil > 0 ? 0x01266000u /* S8D24 */ : 0x02444000u /* D16 */;
        G.DepthStencilSurfaceInit(surf.DepthSurface, fmt, 0x00011000 /* tiled */, w, surf.DepthData, 0);
    }

    [Hle]
    uint eglCreatePbufferSurface(uint dpy, uint config, uint attribList)
    {
        if (!CheckDisplay(dpy)) return 0;
        if (ConfigOf(config) is not { } c) { EglError(Egl.BAD_CONFIG); return 0; }
        if ((c.SurfaceType & Egl.PBUFFER_BIT) == 0) { EglError(Egl.BAD_MATCH); return 0; }
        int w = 0, h = 0, texFormat = Egl.NO_TEXTURE, texTarget = Egl.NO_TEXTURE, mip = 0;
        bool largest = false;
        foreach (var (a, v) in Attribs(attribList))
        {
            switch (a)
            {
                case Egl.WIDTH: w = v; break;
                case Egl.HEIGHT: h = v; break;
                case Egl.LARGEST_PBUFFER: largest = v != 0; break;
                case Egl.TEXTURE_FORMAT: texFormat = v; break;
                case Egl.TEXTURE_TARGET: texTarget = v; break;
                case Egl.MIPMAP_TEXTURE: mip = v; break;
                case 0x3087 or 0x3088: break;
                default: EglError(Egl.BAD_ATTRIBUTE); return 0;
            }
        }
        if (w < 0 || h < 0) { EglError(Egl.BAD_PARAMETER); return 0; }
        if (texFormat is not (Egl.NO_TEXTURE or Egl.TEXTURE_RGB or Egl.TEXTURE_RGBA) || texTarget is not (Egl.NO_TEXTURE or Egl.TEXTURE_2D))
        { EglError(Egl.BAD_ATTRIBUTE); return 0; }
        if ((texFormat == Egl.NO_TEXTURE) != (texTarget == Egl.NO_TEXTURE)) { EglError(Egl.BAD_MATCH); return 0; }
        if (texFormat == Egl.TEXTURE_RGBA && c.Alpha == 0) { EglError(Egl.BAD_MATCH); return 0; }
        if (w > MaxRenderbufferSize || h > MaxRenderbufferSize)
        {
            if (!largest) { EglError(Egl.BAD_ALLOC); return 0; }
            w = Math.Min(w, MaxRenderbufferSize); h = Math.Min(h, MaxRenderbufferSize);
        }
        EnsureGxm();
        var surf = new PgSurface(_nextEglHandle++, c, false)
        {
            Width = (uint)Math.Max(w, 1), Height = (uint)Math.Max(h, 1), LargestPbuffer = largest,
            TextureFormat = texFormat, TextureTarget = texTarget, MipmapTexture = mip,
        };
        surf.Stride = (surf.Width + 7) & ~7u;
        int bpp = c.Is565 ? 2 : 4;
        surf.ColorBuffers = [Alloc(surf.Stride * surf.Height * (uint)bpp, "pbuffer", MemoryRegion.User, 4096)];
        surf.ColorSurfaces = [Alloc(0x30, "color surface")];
        G.ColorSurfaceInit(surf.ColorSurfaces[0], c.Is565 ? 0x30100000u /* U5U6U5_RGB */ : 0, surf.Width, surf.Height, surf.Stride, surf.ColorBuffers[0]);
        surf.SyncObjects = [0];
        CreateDepthStencil(surf);
        lock (_lock) _surfaces[surf.Handle] = surf;
        EglOk();
        return surf.Handle;
    }

    /// <summary>No native pixmaps exist on the Vita.</summary>
    [Hle]
    uint eglCreatePixmapSurface(uint dpy, uint config, uint pixmap, uint attribList)
    {
        if (!CheckDisplay(dpy)) return 0;
        if (ConfigOf(config) is null) { EglError(Egl.BAD_CONFIG); return 0; }
        EglError(Egl.BAD_MATCH); // no configuration has EGL_PIXMAP_BIT
        return 0;
    }

    /// <summary>Only OpenVG client buffers exist for this entry point, and Piglet has no OpenVG.</summary>
    [Hle]
    uint eglCreatePbufferFromClientBuffer(uint dpy, uint bufType, uint buffer, uint config, uint attribList)
    {
        if (!CheckDisplay(dpy)) return 0;
        if (ConfigOf(config) is null) { EglError(Egl.BAD_CONFIG); return 0; }
        EglError(Egl.BAD_PARAMETER);
        return 0;
    }

    PgSurface? SurfaceOf(uint handle) { lock (_lock) return _surfaces.TryGetValue(handle, out var s) && !s.Destroyed ? s : null; }
    PgContext? ContextOf(uint handle) { lock (_lock) return _contexts.TryGetValue(handle, out var c) && !c.Destroyed ? c : null; }

    [Hle]
    int eglDestroySurface(uint dpy, uint surface)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (SurfaceOf(surface) is not { } s) return EglFail(Egl.BAD_SURFACE);
        if (s.Current) s.Destroyed = true; // freed when no longer current
        else DestroySurfaceNow(s);
        EglOk();
        return EglTrue;
    }

    void DestroySurfaceNow(PgSurface s)
    {
        s.Destroyed = true;
        if (_sceneTarget?.Surface == s) EndScene();
        if (s.BoundTexture is { } t) ReleaseTexImage(s, t);
        long seq = Emu.Gpu.Submitted;
        if (s.Window) { G.DisplayQueueFinish(); seq = Emu.Gpu.Submitted; }
        foreach (uint b in s.ColorBuffers) FreeAfterGpu(b, seq);
        foreach (uint cs in s.ColorSurfaces) FreeAfterGpu(cs, seq);
        foreach (uint so in s.SyncObjects) if (so != 0) G.SyncObjectDestroy(so);
        FreeAfterGpu(s.DepthData, seq);
        FreeAfterGpu(s.DepthSurface, seq);
        lock (_lock) _surfaces.Remove(s.Handle);
    }

    [Hle]
    int eglQuerySurface(uint dpy, uint surface, int attribute, Ptr<int> value)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (SurfaceOf(surface) is not { } s) return EglFail(Egl.BAD_SURFACE);
        int? v = attribute switch
        {
            Egl.CONFIG_ID => s.Config.Id,
            Egl.WIDTH => (int)s.Width,
            Egl.HEIGHT => (int)s.Height,
            Egl.LARGEST_PBUFFER => s.Window ? null : s.LargestPbuffer ? 1 : 0,
            Egl.TEXTURE_FORMAT => s.Window ? null : s.TextureFormat,
            Egl.TEXTURE_TARGET => s.Window ? null : s.TextureTarget,
            Egl.MIPMAP_TEXTURE => s.Window ? null : s.MipmapTexture,
            Egl.MIPMAP_LEVEL => s.Window ? null : s.MipmapLevel,
            Egl.RENDER_BUFFER => s.Window ? Egl.BACK_BUFFER : Egl.BACK_BUFFER,
            Egl.HORIZONTAL_RESOLUTION or Egl.VERTICAL_RESOLUTION or Egl.PIXEL_ASPECT_RATIO => Egl.UNKNOWN,
            Egl.SWAP_BEHAVIOR => (int)s.SwapBehavior,
            Egl.MULTISAMPLE_RESOLVE => Egl.MULTISAMPLE_RESOLVE_DEFAULT,
            0x3087 => 0x3089, // EGL_VG_COLORSPACE_sRGB
            0x3088 => 0x308B, // EGL_VG_ALPHA_FORMAT_NONPRE
            _ => int.MinValue,
        };
        if (v == int.MinValue) return EglFail(Egl.BAD_ATTRIBUTE);
        if (v is int val && !value.IsNull) value.Value = val; // a pbuffer-only attribute on a window leaves the value unchanged
        EglOk();
        return EglTrue;
    }

    [Hle]
    int eglSurfaceAttrib(uint dpy, uint surface, int attribute, int value)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (SurfaceOf(surface) is not { } s) return EglFail(Egl.BAD_SURFACE);
        switch (attribute)
        {
            case Egl.MIPMAP_LEVEL:
                s.MipmapLevel = Math.Max(value, 0);
                break;
            case Egl.SWAP_BEHAVIOR:
                if (value == Egl.BUFFER_PRESERVED && (s.Config.SurfaceType & Egl.SWAP_BEHAVIOR_PRESERVED_BIT) == 0) return EglFail(Egl.BAD_MATCH);
                if (value is not (Egl.BUFFER_PRESERVED or Egl.BUFFER_DESTROYED)) return EglFail(Egl.BAD_PARAMETER);
                s.SwapBehavior = (uint)value;
                break;
            case Egl.MULTISAMPLE_RESOLVE:
                if (value == Egl.MULTISAMPLE_RESOLVE_DEFAULT) break;
                return EglFail(value == 0x309B ? Egl.BAD_MATCH : Egl.BAD_PARAMETER); // EGL_MULTISAMPLE_RESOLVE_BOX
            default:
                return EglFail(Egl.BAD_ATTRIBUTE);
        }
        EglOk();
        return EglTrue;
    }

    /// <summary>A pbuffer created with a texture format gives its colour buffer to the texture bound to TEXTURE_2D on the
    /// current context's active unit: the texture samples the pbuffer's pixels until eglReleaseTexImage.</summary>
    [Hle]
    int eglBindTexImage(uint dpy, uint surface, int buffer)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (SurfaceOf(surface) is not { } s || s.Window) return EglFail(Egl.BAD_SURFACE);
        if (buffer != Egl.BACK_BUFFER) return EglFail(Egl.BAD_PARAMETER);
        if (s.TextureFormat == Egl.NO_TEXTURE) return EglFail(Egl.BAD_MATCH);
        if (s.BoundTexture != null) return EglFail(Egl.BAD_ACCESS);
        if (Cur is not { } c || c.Tex2D[c.ActiveUnit] is not { } t) { EglOk(); return EglTrue; } // no texture bound: no effect
        if (_sceneTarget?.Surface == s) EndScene();
        BindPbufferToTexture(s, t);
        EglOk();
        return EglTrue;
    }

    [Hle]
    int eglReleaseTexImage(uint dpy, uint surface, int buffer)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (SurfaceOf(surface) is not { } s || s.Window) return EglFail(Egl.BAD_SURFACE);
        if (buffer != Egl.BACK_BUFFER) return EglFail(Egl.BAD_PARAMETER);
        if (s.TextureFormat == Egl.NO_TEXTURE) return EglFail(Egl.BAD_MATCH);
        if (s.BoundTexture is { } t) ReleaseTexImage(s, t);
        EglOk();
        return EglTrue;
    }

    // ------------------------------------------------------------------ contexts

    [Hle]
    uint eglCreateContext(uint dpy, uint config, uint shareContext, uint attribList)
    {
        if (!CheckDisplay(dpy)) return 0;
        if (ConfigOf(config) is not { } cfg) { EglError(Egl.BAD_CONFIG); return 0; }
        int version = 1;
        foreach (var (a, v) in Attribs(attribList))
        {
            if (a != Egl.CONTEXT_CLIENT_VERSION) { EglError(Egl.BAD_ATTRIBUTE); return 0; }
            version = v;
        }
        lock (_lock)
            if (_apiByThread.GetValueOrDefault(ThreadKey, Egl.OPENGL_ES_API) != Egl.OPENGL_ES_API) { EglError(Egl.BAD_MATCH); return 0; }
        // Piglet is an OpenGL ES 2.0 implementation only
        if (version != 2) { EglError(Egl.BAD_CONFIG); return 0; }
        PgShared shared;
        if (shareContext != 0)
        {
            if (ContextOf(shareContext) is not { } sc) { EglError(Egl.BAD_CONTEXT); return 0; }
            shared = sc.Shared;
            shared.RefCount++;
        }
        else shared = new PgShared();
        var ctx = new PgContext(_nextEglHandle++, cfg, shared);
        lock (_lock) _contexts[ctx.Handle] = ctx;
        EglOk();
        return ctx.Handle;
    }

    [Hle]
    int eglDestroyContext(uint dpy, uint context)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (ContextOf(context) is not { } c) return EglFail(Egl.BAD_CONTEXT);
        c.Destroyed = true;
        if (!c.Current) lock (_lock) _contexts.Remove(c.Handle);
        EglOk();
        return EglTrue;
    }

    [Hle]
    int eglMakeCurrent(uint dpy, uint draw, uint read, uint context)
    {
        if (dpy != EglDisplay) return EglFail(Egl.BAD_DISPLAY);
        if (context == 0)
        {
            if (draw != 0 || read != 0) return EglFail(Egl.BAD_MATCH);
            ReleaseCurrent();
            EglOk();
            return EglTrue;
        }
        if (!_eglInitialized) return EglFail(Egl.NOT_INITIALIZED);
        if (ContextOf(context) is not { } c) return EglFail(Egl.BAD_CONTEXT);
        if ((draw == 0) != (read == 0)) return EglFail(Egl.BAD_MATCH);
        PgSurface? d = null, r = null;
        if (draw != 0)
        {
            d = SurfaceOf(draw);
            r = SurfaceOf(read);
            if (d == null || r == null) return EglFail(Egl.BAD_SURFACE);
            if (d.Config != c.Config && (d.Config.Red != c.Config.Red || d.Config.Depth != c.Config.Depth)) return EglFail(Egl.BAD_MATCH);
        }
        lock (_lock)
            if (c.Current && _currentByThread.GetValueOrDefault(ThreadKey) != c) return EglFail(Egl.BAD_ACCESS); // current to another thread
        ReleaseCurrent();
        c.Current = true;
        c.Draw = d; c.Read = r;
        if (d != null) d.Current = true;
        if (r != null) r.Current = true;
        if (!c.EverCurrent && d != null)
        {
            // the viewport and scissor start as the size of the first draw surface
            c.ViewportW = c.ScissorW = (int)d.Width;
            c.ViewportH = c.ScissorH = (int)d.Height;
            c.EverCurrent = true;
        }
        lock (_lock) _currentByThread[ThreadKey] = c;
        EglOk();
        return EglTrue;
    }

    /// <summary>The calling thread's context stops being current (destroyed objects that were current go now).</summary>
    void ReleaseCurrent()
    {
        PgContext? old;
        lock (_lock) { _currentByThread.Remove(ThreadKey, out old); }
        if (old == null) return;
        if (_sceneTarget != null) EndScene();
        old.Current = false;
        foreach (var s in new[] { old.Draw, old.Read })
        {
            if (s == null) continue;
            s.Current = false;
            if (s.Destroyed) DestroySurfaceNow(s);
        }
        old.Draw = old.Read = null;
        if (old.Destroyed) lock (_lock) _contexts.Remove(old.Handle);
    }

    [Hle]
    uint eglGetCurrentContext() { EglOk(); return Cur?.Handle ?? 0; }

    [Hle]
    uint eglGetCurrentDisplay() { EglOk(); return Cur != null ? EglDisplay : 0; }

    [Hle]
    uint eglGetCurrentSurface(int readdraw)
    {
        if (readdraw is not (Egl.READ or Egl.DRAW)) { EglError(Egl.BAD_PARAMETER); return 0; }
        EglOk();
        var c = Cur;
        return readdraw == Egl.DRAW ? c?.Draw?.Handle ?? 0 : c?.Read?.Handle ?? 0;
    }

    [Hle]
    int eglQueryContext(uint dpy, uint context, int attribute, Ptr<int> value)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (ContextOf(context) is not { } c) return EglFail(Egl.BAD_CONTEXT);
        int v;
        switch (attribute)
        {
            case Egl.CONFIG_ID: v = c.Config.Id; break;
            case Egl.CONTEXT_CLIENT_TYPE: v = Egl.OPENGL_ES_API; break;
            case Egl.CONTEXT_CLIENT_VERSION: v = 2; break;
            case Egl.RENDER_BUFFER: v = c.Draw == null ? Egl.NONE : c.Draw.Window ? Egl.BACK_BUFFER : Egl.BACK_BUFFER; break;
            default: return EglFail(Egl.BAD_ATTRIBUTE);
        }
        if (!value.IsNull) value.Value = v;
        EglOk();
        return EglTrue;
    }

    [Hle]
    int eglReleaseThread()
    {
        ReleaseCurrent();
        lock (_lock) { _apiByThread.Remove(ThreadKey); _eglErrorByThread.Remove(ThreadKey); }
        return EglTrue;
    }

    [Hle]
    int eglWaitClient()
    {
        EglOk();
        if (Cur is { } c) FinishGpu(c);
        return EglTrue;
    }

    [Hle] int eglWaitGL() => eglWaitClient();

    /// <summary>The only native engine is the core one; it renders nothing Piglet waits on.</summary>
    [Hle]
    int eglWaitNative(int engine)
    {
        if (engine != Egl.CORE_NATIVE_ENGINE) return EglFail(Egl.BAD_PARAMETER);
        EglOk();
        return EglTrue;
    }

    [Hle]
    int eglSwapInterval(uint dpy, int interval)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (Cur is not { } c) return EglFail(Egl.BAD_CONTEXT);
        if (c.Draw is not { } s) return EglFail(Egl.BAD_SURFACE);
        s.SwapInterval = Math.Clamp(interval, 0, 4);
        EglOk();
        return EglTrue;
    }

    [Hle]
    int eglSwapBuffers(uint dpy, uint surface)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (SurfaceOf(surface) is not { } s) return EglFail(Egl.BAD_SURFACE);
        if (Cur is not { } c || c.Draw != s) return EglFail(Egl.BAD_SURFACE);
        if (!s.Window) { EglOk(); return EglTrue; } // a pbuffer has no back buffer to post
        // the frame's last scene ends; an empty frame still gets one so the flip waits for nothing stale
        if (_sceneTarget == null || _sceneTarget.Surface != s) BeginSceneOn(DefaultTarget(s));
        EndScene();
        uint data = _displayCallbackData;
        GuestMemory.W32(data, s.ColorBuffer);
        GuestMemory.W32(data + 4, s.Width);
        GuestMemory.W32(data + 8, s.Height);
        GuestMemory.W32(data + 12, s.Stride);
        GuestMemory.W32(data + 16, (uint)s.SwapInterval);
        int front = (s.Back + s.ColorBuffers.Length - 1) % s.ColorBuffers.Length;
        int r = G.DisplayQueueAddEntry(s.SyncObjects[front], s.SyncObjects[s.Back], data);
        if (r != 0) Log.WarnOnce("piglet-flip", $"[piglet] sceGxmDisplayQueueAddEntry: 0x{r:X8}");
        s.Back = (s.Back + 1) % s.ColorBuffers.Length;
        RetireTransient();
        EglOk();
        return EglTrue;
    }

    /// <summary>No native pixmaps exist on the Vita.</summary>
    [Hle]
    int eglCopyBuffers(uint dpy, uint surface, uint pixmap)
    {
        if (!CheckDisplay(dpy)) return EglFalse;
        if (SurfaceOf(surface) is null) return EglFail(Egl.BAD_SURFACE);
        return EglFail(Egl.BAD_NATIVE_PIXMAP);
    }

    readonly Dictionary<string, uint> _procStubs = [];

    /// <summary>The address of an exported libScePiglet function (an HLE stub), or NULL for an unknown name.</summary>
    [Hle]
    uint eglGetProcAddress(string name)
    {
        lock (_procStubs)
        {
            if (_procStubs.TryGetValue(name, out uint a)) return a;
            var info = NidDatabase.LookupName(name).FirstOrDefault(n => n.Library == "libScePiglet");
            if (info == null || !Emu.Hle.IsImplemented(info.Nid)) return 0;
            a = Alloc(16, "proc " + name, MemoryRegion.Kernel, 16);
            Loader.Linker.WriteHleStub(a, Emu.Hle.GetHleFunctionIndex(info.Nid, 0xB4FE1ABB, "libScePiglet"), info.Nid);
            Emu.Code.Invalidate(a, 16);
            return _procStubs[name] = a;
        }
    }
}
