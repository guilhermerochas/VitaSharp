using System.Reflection;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// libScePiglet (EGL + GLES2 over GXM, Piglet*.cs). The calls run on a guest thread as an application's would; the
/// state, object and error tests need nothing else. The shader tests compile Cg through the real runtime compiler
/// libshacccg.suprx (LLE) when one is installed (ur0:data of the VitaSharp or Vita3K data folder); the drawing test also
/// renders through the Vulkan renderer and reads the pixels back.
/// </summary>
[Collection("guest")]
public sealed class PigletHleTests(GuestMemoryFixture fx, ITestOutputHelper output)
{
    // GL / EGL values used below
    const uint GL_NO_ERROR = 0, GL_INVALID_ENUM = 0x500, GL_INVALID_VALUE = 0x501, GL_INVALID_OPERATION = 0x502, GL_INVALID_FRAMEBUFFER_OPERATION = 0x506;
    const uint GL_ARRAY_BUFFER = 0x8892, GL_ELEMENT_ARRAY_BUFFER = 0x8893, GL_STATIC_DRAW = 0x88E4, GL_BUFFER_SIZE = 0x8764;
    const uint GL_TEXTURE_2D = 0xDE1, GL_TEXTURE_CUBE_MAP_POSITIVE_X = 0x8515, GL_RGB = 0x1907, GL_RGBA = 0x1908, GL_UNSIGNED_BYTE = 0x1401, GL_FLOAT = 0x1406;
    const uint GL_FRAMEBUFFER = 0x8D40, GL_RENDERBUFFER = 0x8D41, GL_COLOR_ATTACHMENT0 = 0x8CE0, GL_DEPTH_ATTACHMENT = 0x8D00, GL_STENCIL_ATTACHMENT = 0x8D20;
    const uint GL_RGBA4 = 0x8056, GL_DEPTH_COMPONENT16 = 0x81A5, GL_STENCIL_INDEX8 = 0x8D48, GL_DEPTH24_STENCIL8 = 0x88F0;
    const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5, GL_INCOMPLETE_ATTACHMENT = 0x8CD6, GL_INCOMPLETE_MISSING = 0x8CD7, GL_INCOMPLETE_DIMENSIONS = 0x8CD9, GL_UNSUPPORTED = 0x8CDD;
    const uint GL_VERTEX_SHADER = 0x8B31, GL_FRAGMENT_SHADER = 0x8B30, GL_COMPILE_STATUS = 0x8B81, GL_LINK_STATUS = 0x8B82, GL_INFO_LOG_LENGTH = 0x8B84;
    const uint GL_ACTIVE_UNIFORMS = 0x8B86, GL_ACTIVE_ATTRIBUTES = 0x8B89;
    const uint GL_DEPTH_TEST = 0xB71, GL_BLEND = 0xBE2, GL_COLOR_CLEAR_VALUE = 0xC22, GL_VIEWPORT = 0xBA2, GL_SRC_ALPHA = 0x302, GL_ONE_MINUS_SRC_ALPHA = 0x303;
    const uint GL_BLEND_SRC_RGB = 0x80C9, GL_DEPTH_FUNC = 0xB74, GL_LEQUAL = 0x203, GL_MAX_TEXTURE_SIZE = 0xD33, GL_COLOR_WRITEMASK = 0xC23;
    const uint GL_COLOR_BUFFER_BIT = 0x4000, GL_DEPTH_BUFFER_BIT = 0x100, GL_TRIANGLES = 4, GL_TRIANGLE_STRIP = 5, GL_UNSIGNED_SHORT = 0x1403;
    const uint GL_TEXTURE_MIN_FILTER = 0x2801, GL_NEAREST = 0x2600, GL_COMPRESSED_RGBA_S3TC_DXT1 = 0x83F1;
    const int EGL_SUCCESS = 0x3000, EGL_NOT_INITIALIZED = 0x3001, EGL_BAD_ALLOC = 0x3003, EGL_BAD_CONFIG = 0x3005, EGL_BAD_MATCH = 0x3009,
        EGL_BAD_SURFACE = 0x300D, EGL_BAD_ATTRIBUTE = 0x3004, EGL_NONE = 0x3038, EGL_RED_SIZE = 0x3024, EGL_DEPTH_SIZE = 0x3025,
        EGL_STENCIL_SIZE = 0x3026, EGL_SURFACE_TYPE = 0x3033, EGL_WINDOW_BIT = 4, EGL_PBUFFER_BIT = 1, EGL_CONTEXT_CLIENT_VERSION = 0x3098,
        EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056, EGL_CONFIG_ID = 0x3028, EGL_RENDERABLE_TYPE = 0x3040, EGL_OPENGL_ES2_BIT = 4, EGL_DRAW = 0x3059;

    [Fact]
    public void Egl_DisplayConfigsSurfacesAndErrors()
    {
        using var h = new PigletHarness(fx, false);
        h.Run(() =>
        {
            uint dpy = h.U("eglGetDisplay", 0u);
            Assert.Equal(1u, dpy);
            uint num = h.Mem(4), cfgs = h.Mem(64);
            Assert.Equal(0u, h.U("eglGetConfigs", dpy, cfgs, 16, new Ptr<int>(num)));
            Assert.Equal(EGL_NOT_INITIALIZED, h.I("eglGetError"));
            uint maj = h.Mem(4), min = h.Mem(4);
            Assert.Equal(1u, h.U("eglInitialize", dpy, new Ptr<int>(maj), new Ptr<int>(min)));
            Assert.Equal((1u, 4u), (GuestMemory.R32(maj), GuestMemory.R32(min)));
            Assert.Equal(EGL_SUCCESS, h.I("eglGetError"));
            Assert.Equal(1u, h.U("eglGetConfigs", dpy, 0u, 0, new Ptr<int>(num)));
            Assert.True(GuestMemory.R32(num) >= 3);
            // a window config with depth and stencil, ES2 renderable: the 32-bit one with D24S8 comes first
            uint attrs = h.Ints(EGL_RED_SIZE, 8, EGL_DEPTH_SIZE, 16, EGL_STENCIL_SIZE, 8, EGL_SURFACE_TYPE, EGL_WINDOW_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT, EGL_NONE);
            Assert.Equal(1u, h.U("eglChooseConfig", dpy, attrs, cfgs, 16, new Ptr<int>(num)));
            Assert.Equal(1u, GuestMemory.R32(num));
            uint config = GuestMemory.R32(cfgs);
            uint val = h.Mem(4);
            Assert.Equal(1u, h.U("eglGetConfigAttrib", dpy, config, EGL_DEPTH_SIZE, new Ptr<int>(val)));
            Assert.Equal(24u, GuestMemory.R32(val));
            Assert.Equal(0u, h.U("eglGetConfigAttrib", dpy, config, 0x1234, new Ptr<int>(val)));
            Assert.Equal(EGL_BAD_ATTRIBUTE, h.I("eglGetError"));
            // pbuffer-only configs are no window configs
            uint pb = h.Ints(EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_NONE);
            Assert.Equal(1u, h.U("eglChooseConfig", dpy, pb, 0u, 0, new Ptr<int>(num)));
            Assert.True(GuestMemory.R32(num) > 3);
            Assert.Equal(1u, h.U("eglChooseConfig", dpy, h.Ints(EGL_CONFIG_ID, 4, EGL_NONE), cfgs, 4, new Ptr<int>(num)));
            Assert.Equal(0u, h.U("eglCreateWindowSurface", dpy, GuestMemory.R32(cfgs), 0u, 0u));
            Assert.Equal(EGL_BAD_MATCH, h.I("eglGetError"));
            // contexts: OpenGL ES 2 only
            Assert.Equal(0u, h.U("eglCreateContext", dpy, config, 0u, 0u));
            Assert.Equal(EGL_BAD_CONFIG, h.I("eglGetError"));
            uint ctx = h.U("eglCreateContext", dpy, config, 0u, h.Ints(EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE));
            Assert.NotEqual(0u, ctx);
            uint win = h.U("eglCreateWindowSurface", dpy, config, 0u, 0u);
            Assert.NotEqual(0u, win);
            Assert.Equal(0u, h.U("eglCreateWindowSurface", dpy, config, 0u, 0u));
            Assert.Equal(EGL_BAD_ALLOC, h.I("eglGetError")); // one window: the screen
            Assert.Equal(1u, h.U("eglQuerySurface", dpy, win, EGL_WIDTH, new Ptr<int>(val)));
            Assert.Equal(960u, GuestMemory.R32(val));
            Assert.Equal(1u, h.U("eglQuerySurface", dpy, win, EGL_HEIGHT, new Ptr<int>(val)));
            Assert.Equal(544u, GuestMemory.R32(val));
            Assert.Equal(0u, h.U("eglMakeCurrent", dpy, win, 0u, ctx));
            Assert.Equal(EGL_BAD_MATCH, h.I("eglGetError"));
            Assert.Equal(1u, h.U("eglMakeCurrent", dpy, win, win, ctx));
            Assert.Equal(ctx, h.U("eglGetCurrentContext"));
            Assert.Equal(win, h.U("eglGetCurrentSurface", EGL_DRAW));
            // the viewport starts as the surface
            uint vp = h.Mem(16);
            h.Call("glGetIntegerv", GL_VIEWPORT, vp);
            Assert.Equal([0u, 0u, 960u, 544u], [GuestMemory.R32(vp), GuestMemory.R32(vp + 4), GuestMemory.R32(vp + 8), GuestMemory.R32(vp + 12)]);
            // a pbuffer
            uint pbuf = h.U("eglCreatePbufferSurface", dpy, config, h.Ints(EGL_WIDTH, 64, EGL_HEIGHT, 32, EGL_NONE));
            Assert.NotEqual(0u, pbuf);
            Assert.Equal(1u, h.U("eglQuerySurface", dpy, pbuf, EGL_WIDTH, new Ptr<int>(val)));
            Assert.Equal(64u, GuestMemory.R32(val));
            Assert.Equal(0u, h.U("eglSwapBuffers", dpy, pbuf)); // not the current draw surface
            Assert.Equal(EGL_BAD_SURFACE, h.I("eglGetError"));
            Assert.Equal(1u, h.U("eglSwapBuffers", dpy, win));
            Assert.NotEqual(0u, h.U("eglQueryString", dpy, 0x3054)); // EGL_VERSION
            Assert.Equal("1.4 Piglet", GuestMemory.ReadCString(h.U("eglQueryString", dpy, 0x3054)));
            Assert.NotEqual(0u, h.U("eglGetProcAddress", "glDrawArrays"));
            Assert.Equal(0u, h.U("eglGetProcAddress", "glNotAFunction"));
            Assert.Equal(1u, h.U("eglMakeCurrent", dpy, 0u, 0u, 0u));
            Assert.Equal(0u, h.U("eglGetCurrentContext"));
            Assert.Equal(1u, h.U("eglDestroySurface", dpy, win));
            Assert.Equal(1u, h.U("eglTerminate", dpy));
        });
    }

    [Fact]
    public void Gl_ErrorsStickUntilRead_AndStateQueries()
    {
        using var h = new PigletHarness(fx, false);
        h.Run(() =>
        {
            h.MakeCurrentPbuffer(64, 64);
            h.Call("glEnable", 0x1234u);
            h.Call("glViewport", 0, 0, -1, 4);
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError")); // the first error stays
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            h.Call("glEnable", GL_DEPTH_TEST);
            Assert.True(h.B("glIsEnabled", GL_DEPTH_TEST));
            Assert.False(h.B("glIsEnabled", GL_BLEND));
            h.Call("glBlendFunc", GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
            h.Call("glDepthFunc", GL_LEQUAL);
            h.Call("glClearColor", 1f, 0.5f, 0f, 2f);
            h.Call("glColorMask", true, false, true, false);
            uint v = h.Mem(16);
            h.Call("glGetIntegerv", GL_BLEND_SRC_RGB, v);
            Assert.Equal(GL_SRC_ALPHA, GuestMemory.R32(v));
            h.Call("glGetIntegerv", GL_DEPTH_FUNC, v);
            Assert.Equal(GL_LEQUAL, GuestMemory.R32(v));
            h.Call("glGetFloatv", GL_COLOR_CLEAR_VALUE, v);
            Assert.Equal([1f, 0.5f, 0f, 1f], [GuestMemory.Read<float>(v), GuestMemory.Read<float>(v + 4), GuestMemory.Read<float>(v + 8), GuestMemory.Read<float>(v + 12)]);
            h.Call("glGetIntegerv", GL_COLOR_CLEAR_VALUE, v); // normalized: 1.0 -> the largest integer
            Assert.Equal(0x7FFFFFFFu, GuestMemory.R32(v));
            Assert.Equal(0u, GuestMemory.R32(v + 8));
            h.Call("glGetBooleanv", GL_COLOR_WRITEMASK, v);
            Assert.Equal(0x00010001u, GuestMemory.R32(v));
            h.Call("glGetIntegerv", GL_MAX_TEXTURE_SIZE, v);
            Assert.Equal(4096u, GuestMemory.R32(v));
            h.Call("glGetIntegerv", 0xFFFFu, v);
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError"));
            h.Call("glStencilFunc", 0x1234u, 0, 0xFFu);
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError"));
            h.Call("glLineWidth", 0f);
            Assert.Equal(GL_INVALID_VALUE, h.U("glGetError"));
            h.Call("glActiveTexture", 0x84C0u + 16);
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError"));
            Assert.Contains("GL_EXT_texture_compression_s3tc", GuestMemory.ReadCString(h.U("glGetString", 0x1F03u)));
        });
    }

    [Fact]
    public void Gl_BufferObjects()
    {
        using var h = new PigletHarness(fx, false);
        h.Run(() =>
        {
            h.MakeCurrentPbuffer(16, 16);
            uint names = h.Mem(8);
            h.Call("glGenBuffers", 2, names);
            uint a = GuestMemory.R32(names), b = GuestMemory.R32(names + 4);
            Assert.NotEqual(a, b);
            Assert.False(h.B("glIsBuffer", a)); // a name only, until bound
            h.Call("glBufferData", GL_ARRAY_BUFFER, 16, 0u, GL_STATIC_DRAW);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // buffer 0 bound
            h.Call("glBindBuffer", GL_ARRAY_BUFFER, a);
            Assert.True(h.B("glIsBuffer", a));
            uint data = h.Mem(16);
            for (uint i = 0; i < 16; i++) GuestMemory.Write(data + i, (byte)i);
            h.Call("glBufferData", GL_ARRAY_BUFFER, 16, data, 0x1234u);
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError"));
            h.Call("glBufferData", GL_ARRAY_BUFFER, 16, data, GL_STATIC_DRAW);
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            h.Call("glBufferSubData", GL_ARRAY_BUFFER, 12, 8, data);
            Assert.Equal(GL_INVALID_VALUE, h.U("glGetError"));
            GuestMemory.W32(data, 0xAABBCCDD);
            h.Call("glBufferSubData", GL_ARRAY_BUFFER, 4, 4, data);
            uint sz = h.Mem(4);
            h.Call("glGetBufferParameteriv", GL_ARRAY_BUFFER, GL_BUFFER_SIZE, new Ptr<int>(sz));
            Assert.Equal(16u, GuestMemory.R32(sz));
            var buf = h.Piglet.Buffer(a)!;
            Assert.Equal(0xAABBCCDDu, GuestMemory.R32(buf.Addr + 4));
            Assert.Equal(0x0B0A0908u, GuestMemory.R32(buf.Addr + 8));
            h.Call("glBindBuffer", 0x1234u, a);
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError"));
            h.Call("glDeleteBuffers", 2, names);
            Assert.False(h.B("glIsBuffer", a));
            uint bound = h.Mem(4);
            h.Call("glGetIntegerv", 0x8894u, bound); // ARRAY_BUFFER_BINDING: reset by the delete
            Assert.Equal(0u, GuestMemory.R32(bound));
        });
    }

    [Fact]
    public void Gl_TextureStorageAndUploads()
    {
        using var h = new PigletHarness(fx, false);
        h.Run(() =>
        {
            h.MakeCurrentPbuffer(16, 16);
            uint names = h.Mem(8);
            h.Call("glGenTextures", 2, names);
            uint tex = GuestMemory.R32(names);
            h.Call("glTexImage2D", GL_TEXTURE_2D, 0, (int)GL_RGB, 3, 2, 0, GL_RGB, GL_UNSIGNED_BYTE, 0u);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // texture 0
            h.Call("glBindTexture", GL_TEXTURE_2D, tex);
            Assert.True(h.B("glIsTexture", tex));
            // 3x2 RGB with 4-byte row alignment: rows of 9 bytes padded to 12
            uint px = h.Mem(24);
            byte[] rgb = [1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 0, 0, 11, 12, 13, 14, 15, 16, 17, 18, 19, 0, 0, 0];
            for (int i = 0; i < rgb.Length; i++) GuestMemory.Write(px + (uint)i, rgb[i]);
            h.Call("glTexImage2D", GL_TEXTURE_2D, 0, (int)GL_RGB, 3, 2, 0, GL_RGB, GL_UNSIGNED_BYTE, px);
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            var t = h.Piglet.Texture(tex)!;
            // stored as X8U8U8U8_1BGR, linear rows 8 texels wide; level 1 (1x1) follows level 0 padded to 4x2 (rows of 8)
            Assert.Equal(0x0C004000u, t.Format!.GxmFormat);
            Assert.Equal(32u, t.Levels[0, 0].RowPitch);
            Assert.Equal(0xFF030201u, GuestMemory.R32(t.Data));
            Assert.Equal(0xFF090807u, GuestMemory.R32(t.Data + 8));
            Assert.Equal(0xFF0D0C0Bu, GuestMemory.R32(t.Data + 32));
            Assert.Equal(1u, t.Levels[0, 1].W);
            Assert.Equal(8u * 4 * 2, t.Levels[0, 1].Offset);
            // a sub-image
            GuestMemory.W32(px, 0x00636261);
            h.Call("glTexSubImage2D", GL_TEXTURE_2D, 0, 2, 1, 1, 1, GL_RGB, GL_UNSIGNED_BYTE, px);
            Assert.Equal(0xFF636261u, GuestMemory.R32(t.Data + 32 + 8));
            h.Call("glTexSubImage2D", GL_TEXTURE_2D, 0, 2, 1, 2, 1, GL_RGB, GL_UNSIGNED_BYTE, px);
            Assert.Equal(GL_INVALID_VALUE, h.U("glGetError"));
            h.Call("glTexSubImage2D", GL_TEXTURE_2D, 0, 0, 0, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, px);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // another format
            h.Call("glTexImage2D", GL_TEXTURE_2D, 0, (int)GL_RGBA, 3, 2, 0, GL_RGB, GL_UNSIGNED_BYTE, px);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // internalformat != format
            h.Call("glTexImage2D", GL_TEXTURE_2D, 0, (int)GL_RGB, 3, 2, 1, GL_RGB, GL_UNSIGNED_BYTE, px);
            Assert.Equal(GL_INVALID_VALUE, h.U("glGetError")); // border
            h.Call("glTexImage2D", GL_TEXTURE_2D, 0, (int)GL_RGB, 3, 2, 0, GL_RGB, GL_FLOAT, px);
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError"));
            // cube faces are square; binding a 2D texture as a cube map is an error
            h.Call("glBindTexture", 0x8513u, tex);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError"));
            uint cube = GuestMemory.R32(names + 4);
            h.Call("glBindTexture", 0x8513u, cube);
            h.Call("glTexImage2D", GL_TEXTURE_CUBE_MAP_POSITIVE_X, 0, (int)GL_RGBA, 4, 2, 0, GL_RGBA, GL_UNSIGNED_BYTE, 0u);
            Assert.Equal(GL_INVALID_VALUE, h.U("glGetError"));
            // DXT1 8x8: 2x2 blocks of 8 bytes, morton ordered in a swizzled texture
            h.Call("glBindTexture", GL_TEXTURE_2D, tex);
            uint blocks = h.Mem(32);
            for (uint i = 0; i < 4; i++) GuestMemory.W32(blocks + i * 8, 0x11111111 * (i + 1));
            h.Call("glCompressedTexImage2D", GL_TEXTURE_2D, 0, GL_COMPRESSED_RGBA_S3TC_DXT1, 8, 8, 0, 31, blocks);
            Assert.Equal(GL_INVALID_VALUE, h.U("glGetError")); // wrong image size
            h.Call("glCompressedTexImage2D", GL_TEXTURE_2D, 0, GL_COMPRESSED_RGBA_S3TC_DXT1, 8, 8, 0, 32, blocks);
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            t = h.Piglet.Texture(tex)!;
            Assert.Equal(0u, t.GxmType); // swizzled
            // block (1,0) is morton index 2, block (0,1) index 1
            Assert.Equal(0x22222222u, GuestMemory.R32(t.Data + 16));
            Assert.Equal(0x33333333u, GuestMemory.R32(t.Data + 8));
            h.Call("glTexParameteri", GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)0x1234);
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError"));
            h.Call("glTexParameteri", GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
            uint p = h.Mem(4);
            h.Call("glGetTexParameteriv", GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, p);
            Assert.Equal(GL_NEAREST, GuestMemory.R32(p));
            h.Call("glGenerateMipmap", GL_TEXTURE_2D);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // compressed
        });
    }

    [Fact]
    public void Gl_FramebufferCompleteness()
    {
        using var h = new PigletHarness(fx, false);
        h.Run(() =>
        {
            h.MakeCurrentPbuffer(16, 16);
            uint n = h.Mem(16);
            h.Call("glGenFramebuffers", 1, n);
            uint fbo = GuestMemory.R32(n);
            h.Call("glGenRenderbuffers", 3, n);
            uint rbColor = GuestMemory.R32(n), rbDepth = GuestMemory.R32(n + 4), rbStencil = GuestMemory.R32(n + 8);
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, rbColor);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // framebuffer 0
            Assert.Equal(GL_FRAMEBUFFER_COMPLETE, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            h.Call("glBindFramebuffer", GL_FRAMEBUFFER, fbo);
            Assert.Equal(GL_INCOMPLETE_MISSING, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            h.Call("glBindRenderbuffer", GL_RENDERBUFFER, rbColor);
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, rbColor);
            Assert.Equal(GL_INCOMPLETE_ATTACHMENT, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER)); // no storage yet
            h.Call("glRenderbufferStorage", GL_RENDERBUFFER, GL_RGBA4, 32, 32);
            Assert.Equal(GL_FRAMEBUFFER_COMPLETE, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            h.Call("glBindRenderbuffer", GL_RENDERBUFFER, rbDepth);
            h.Call("glRenderbufferStorage", GL_RENDERBUFFER, GL_DEPTH_COMPONENT16, 16, 16);
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, rbDepth);
            Assert.Equal(GL_INCOMPLETE_DIMENSIONS, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            h.Call("glRenderbufferStorage", GL_RENDERBUFFER, GL_DEPTH_COMPONENT16, 32, 32);
            Assert.Equal(GL_FRAMEBUFFER_COMPLETE, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            // a colour renderbuffer as the depth attachment
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_STENCIL_ATTACHMENT, GL_RENDERBUFFER, rbColor);
            Assert.Equal(GL_INCOMPLETE_ATTACHMENT, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            // separate depth and stencil buffers: GXM has one depth/stencil surface
            h.Call("glBindRenderbuffer", GL_RENDERBUFFER, rbStencil);
            h.Call("glRenderbufferStorage", GL_RENDERBUFFER, GL_STENCIL_INDEX8, 32, 32);
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_STENCIL_ATTACHMENT, GL_RENDERBUFFER, rbStencil);
            Assert.Equal(GL_UNSUPPORTED, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            // a packed depth/stencil buffer on both points
            h.Call("glBindRenderbuffer", GL_RENDERBUFFER, rbDepth);
            h.Call("glRenderbufferStorage", GL_RENDERBUFFER, GL_DEPTH24_STENCIL8, 32, 32);
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_STENCIL_ATTACHMENT, GL_RENDERBUFFER, rbDepth);
            Assert.Equal(GL_FRAMEBUFFER_COMPLETE, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            // a texture colour attachment instead
            h.Call("glGenTextures", 1, n);
            uint tex = GuestMemory.R32(n);
            h.Call("glBindTexture", GL_TEXTURE_2D, tex);
            h.Call("glTexImage2D", GL_TEXTURE_2D, 0, (int)GL_RGBA, 32, 32, 0, GL_RGBA, GL_UNSIGNED_BYTE, 0u);
            h.Call("glFramebufferTexture2D", GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, tex, 0);
            Assert.Equal(GL_FRAMEBUFFER_COMPLETE, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            uint v = h.Mem(4);
            h.Call("glGetFramebufferAttachmentParameteriv", GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, 0x8CD1u, new Ptr<int>(v));
            Assert.Equal(tex, GuestMemory.R32(v));
            // drawing into an incomplete framebuffer
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, 0u);
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, 0u);
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_STENCIL_ATTACHMENT, GL_RENDERBUFFER, 0u);
            h.Call("glClear", GL_COLOR_BUFFER_BIT);
            Assert.Equal(GL_INVALID_FRAMEBUFFER_OPERATION, h.U("glGetError"));
            h.Call("glDrawArrays", GL_TRIANGLES, 0, 3);
            Assert.Equal(GL_INVALID_FRAMEBUFFER_OPERATION, h.U("glGetError"));
        });
    }

    [Fact]
    public void Gl_ShaderAndProgramObjectErrors()
    {
        using var h = new PigletHarness(fx, false);
        h.Run(() =>
        {
            h.MakeCurrentPbuffer(16, 16);
            Assert.Equal(0u, h.U("glCreateShader", 0x1234u));
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError"));
            uint vs = h.U("glCreateShader", GL_VERTEX_SHADER), prog = h.U("glCreateProgram");
            Assert.NotEqual(vs, prog);
            h.Call("glAttachShader", vs, prog);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // names swapped
            h.Call("glAttachShader", prog, 999u);
            Assert.Equal(GL_INVALID_VALUE, h.U("glGetError"));
            h.Call("glAttachShader", prog, vs);
            h.Call("glAttachShader", prog, vs);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError"));
            h.Call("glLinkProgram", prog);
            uint v = h.Mem(4);
            h.Call("glGetProgramiv", prog, GL_LINK_STATUS, new Ptr<int>(v));
            Assert.Equal(0u, GuestMemory.R32(v));
            h.Call("glGetProgramiv", prog, GL_INFO_LOG_LENGTH, new Ptr<int>(v));
            Assert.True(GuestMemory.R32(v) > 1);
            h.Call("glUseProgram", prog);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // not linked
            Assert.Equal(-1, h.I("glGetUniformLocation", prog, "x"));
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError"));
            // a shader binary that is no GXM program
            uint names = h.Mem(4), junk = h.Mem(16);
            GuestMemory.W32(names, vs);
            h.Call("glShaderBinary", 1, names, 0x9130u, junk, 16);
            Assert.Equal(GL_INVALID_VALUE, h.U("glGetError"));
            h.Call("glShaderBinary", 1, names, 0x1234u, junk, 16);
            Assert.Equal(GL_INVALID_ENUM, h.U("glGetError"));
            h.Call("glDeleteShader", vs);
            Assert.True(h.B("glIsShader", vs)); // still attached
            h.Call("glDeleteProgram", prog);
            Assert.False(h.B("glIsProgram", prog));
            Assert.False(h.B("glIsShader", vs));
            h.Call("glUniform1f", 0, 1f);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // no program
            h.Call("glUniform1f", -1, 1f);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // -1 is ignored only with a program current
        });
    }

    const string TriVs = "void main(float2 aPos : POSITION, float4 aColor : COLOR, float2 aUv : TEXCOORD0, uniform float4 uOffset, uniform float4 uTint[2],\n" +
        " out float4 oPos : POSITION, out float4 oColor : COLOR, out float2 oUv : TEXCOORD0) { oPos = float4(aPos + uOffset.xy, uOffset.z, 1.0f); oColor = aColor * uTint[1]; oUv = aUv; }";
    const string TriFs = "float4 main(float4 vColor : COLOR, float2 vUv : TEXCOORD0, uniform sampler2D uTex, uniform float uUseTex) : COLOR\n" +
        " { return lerp(vColor, tex2D(uTex, vUv), uUseTex); }";

    [PigletCompilerFact]
    public void Shaders_CompileLinkAndUniforms()
    {
        using var h = new PigletHarness(fx, true);
        h.Run(() =>
        {
            h.MakeCurrentPbuffer(16, 16);
            uint prog = h.Program(TriVs, TriFs, out string log);
            output.WriteLine(log);
            Assert.NotEqual(0u, prog);
            uint v = h.Mem(4);
            h.Call("glGetProgramiv", prog, GL_ACTIVE_ATTRIBUTES, new Ptr<int>(v));
            Assert.Equal(3u, GuestMemory.R32(v));
            h.Call("glGetProgramiv", prog, GL_ACTIVE_UNIFORMS, new Ptr<int>(v));
            Assert.Equal(4u, GuestMemory.R32(v));
            Assert.Equal(1, h.I("glGetAttribLocation", prog, "aColor")); // bound by Program()
            Assert.Equal(-1, h.I("glGetAttribLocation", prog, "nope"));
            int tint = h.I("glGetUniformLocation", prog, "uTint");
            int tint1 = h.I("glGetUniformLocation", prog, "uTint[1]");
            Assert.True(tint >= 0 && tint1 == tint + 1);
            Assert.Equal(-1, h.I("glGetUniformLocation", prog, "uTint[2]"));
            h.Call("glUseProgram", prog);
            uint vals = h.Floats(1, 2, 3, 4);
            h.Call("glUniform4fv", tint1, 1, vals);
            uint got = h.Mem(16);
            h.Call("glGetUniformfv", prog, tint1, got);
            Assert.Equal(3f, GuestMemory.Read<float>(got + 8));
            h.Call("glUniform3fv", tint, 1, vals);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // size mismatch
            int tex = h.I("glGetUniformLocation", prog, "uTex");
            h.Call("glUniform1f", tex, 1f);
            Assert.Equal(GL_INVALID_OPERATION, h.U("glGetError")); // samplers take glUniform1i
            h.Call("glUniform1i", tex, 99);
            Assert.Equal(GL_INVALID_VALUE, h.U("glGetError")); // no such texture unit
            h.Call("glUniform1i", tex, 3);
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            // a compile error is reported in the info log
            uint bad = h.U("glCreateShader", GL_FRAGMENT_SHADER);
            h.Source(bad, "float4 main() : COLOR { return undefined_thing; }");
            h.Call("glCompileShader", bad);
            h.Call("glGetShaderiv", bad, GL_COMPILE_STATUS, new Ptr<int>(v));
            Assert.Equal(0u, GuestMemory.R32(v));
            Assert.Contains("error", h.ShaderLog(bad));
            // the compiled program comes back through glPigletGetShaderBinarySCE and loads with glShaderBinary
            var p = h.Piglet.ProgramObject(prog)!;
            uint len = h.Mem(4), fmt = h.Mem(4), blob = h.Mem(64 * 1024);
            h.Call("glPigletGetShaderBinarySCE", p.Vs!.Name, 64 * 1024, new Ptr<int>(len), new Ptr<uint>(fmt), blob);
            Assert.Equal((uint)p.Vs.Gxp!.Length, GuestMemory.R32(len));
            Assert.Equal(0x00505847u, GuestMemory.R32(blob));
            uint vs2 = h.U("glCreateShader", GL_VERTEX_SHADER), names = h.Mem(4);
            GuestMemory.W32(names, vs2);
            h.Call("glShaderBinary", 1, names, GuestMemory.R32(fmt), blob, (int)GuestMemory.R32(len));
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            h.Call("glGetShaderiv", vs2, GL_COMPILE_STATUS, new Ptr<int>(v));
            Assert.Equal(1u, GuestMemory.R32(v));
        });
    }

    /// <summary>A frame through the whole path: a clear, a vertex-coloured triangle from client arrays, a textured quad
    /// from buffer objects with indices, into the window surface; glReadPixels reads the rendered pixels back.</summary>
    [PigletRenderFact]
    public void Draw_TriangleAndTexturedQuad_ReadBack()
    {
        IGpuBackend backend;
        try { backend = PigletHarness.NewRenderer(); }
        catch (Exception e) { output.WriteLine($"no Vulkan device: {e.Message}"); return; }
        using var h = new PigletHarness(fx, true, backend);
        byte[]? pixels = null, quadPixels = null;
        h.Run(() =>
        {
            h.MakeCurrentWindow();
            uint prog = h.Program(TriVs, TriFs, out string log);
            output.WriteLine(log);
            Assert.NotEqual(0u, prog);
            h.Call("glUseProgram", prog);
            h.Call("glUniform4f", h.I("glGetUniformLocation", prog, "uOffset"), 0f, 0f, 0f, 0f);
            h.Call("glUniform4f", h.I("glGetUniformLocation", prog, "uTint[1]"), 1f, 1f, 1f, 1f);
            h.Call("glUniform1f", h.I("glGetUniformLocation", prog, "uUseTex"), 0f);
            h.Call("glClearColor", 0f, 0f, 1f, 1f);
            h.Call("glClear", GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            // a red triangle on the left half (client arrays): (-1,-1) (0,-1) (-0.5, 1)
            uint pos = h.Floats(-1, -1, 0, -1, -0.5f, 1), col = h.Mem(12);
            for (int i = 0; i < 3; i++) GuestMemory.W32(col + (uint)i * 4, 0xFF0000FF);
            h.Call("glEnableVertexAttribArray", 0u);
            h.Call("glVertexAttribPointer", 0u, 2, GL_FLOAT, false, 0, pos);
            h.Call("glEnableVertexAttribArray", 1u);
            h.Call("glVertexAttribPointer", 1u, 4, GL_UNSIGNED_BYTE, true, 0, col);
            h.Call("glDisableVertexAttribArray", 2u);
            h.Call("glDrawArrays", GL_TRIANGLES, 0, 3);
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            // a textured quad on the right half from buffer objects: a 2x2 texture (green, white / black, yellow)
            uint names = h.Mem(16);
            h.Call("glGenTextures", 1, names);
            h.Call("glActiveTexture", 0x84C0u + 2);
            h.Call("glBindTexture", GL_TEXTURE_2D, GuestMemory.R32(names));
            uint texels = h.Mem(16);
            GuestMemory.W32(texels, 0xFF00FF00); GuestMemory.W32(texels + 4, 0xFFFFFFFF);
            GuestMemory.W32(texels + 8, 0xFF000000); GuestMemory.W32(texels + 12, 0xFF00FFFF);
            h.Call("glTexImage2D", GL_TEXTURE_2D, 0, (int)GL_RGBA, 2, 2, 0, GL_RGBA, GL_UNSIGNED_BYTE, texels);
            h.Call("glTexParameteri", GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
            h.Call("glTexParameteri", GL_TEXTURE_2D, 0x2800u, (int)GL_NEAREST);
            h.Call("glUniform1i", h.I("glGetUniformLocation", prog, "uTex"), 2);
            h.Call("glUniform1f", h.I("glGetUniformLocation", prog, "uUseTex"), 1f);
            h.Call("glGenBuffers", 2, names);
            h.Call("glBindBuffer", GL_ARRAY_BUFFER, GuestMemory.R32(names));
            // x, y, u, v
            uint quad = h.Floats(0.25f, -0.5f, 0, 0, 0.75f, -0.5f, 1, 0, 0.25f, 0.5f, 0, 1, 0.75f, 0.5f, 1, 1);
            h.Call("glBufferData", GL_ARRAY_BUFFER, 64, quad, GL_STATIC_DRAW);
            h.Call("glVertexAttribPointer", 0u, 2, GL_FLOAT, false, 16, 0u);
            h.Call("glEnableVertexAttribArray", 2u);
            h.Call("glVertexAttribPointer", 2u, 2, GL_FLOAT, false, 16, 8u);
            h.Call("glDisableVertexAttribArray", 1u);
            h.Call("glVertexAttrib4f", 1u, 1f, 1f, 1f, 1f);
            h.Call("glBindBuffer", GL_ELEMENT_ARRAY_BUFFER, GuestMemory.R32(names + 4));
            uint idx = h.Mem(12);
            ushort[] ix = [0, 1, 2, 2, 1, 3];
            for (int i = 0; i < 6; i++) GuestMemory.Write(idx + (uint)i * 2, ix[i]);
            h.Call("glBufferData", GL_ELEMENT_ARRAY_BUFFER, 12, idx, GL_STATIC_DRAW);
            h.Call("glDrawElements", GL_TRIANGLES, 6, GL_UNSIGNED_SHORT, 0u);
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            uint rb = h.Mem(960 * 544 * 4);
            h.Call("glReadPixels", 0, 0, 960, 544, GL_RGBA, GL_UNSIGNED_BYTE, rb);
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            pixels = GuestMemory.ReadSpan(rb, 960 * 544 * 4).ToArray();
            Assert.Equal(1u, h.U("eglSwapBuffers", 1u, h.Window));
            quadPixels = pixels;
        });
        Assert.NotNull(pixels);
        uint Px(int x, int y) => BitConverter.ToUInt32(pixels!, (y * 960 + x) * 4);
        output.WriteLine($"bg {Px(10, 500):X8} tri {Px(240, 200):X8} q00 {Px(650, 180):X8} q10 {Px(790, 180):X8} q01 {Px(650, 360):X8} q11 {Px(790, 360):X8}");
        Assert.Equal(0xFFFF0000u, Px(10, 500));      // the clear colour (blue) in a corner the triangle misses
        Assert.Equal(0xFF0000FFu, Px(240, 200));     // inside the red triangle (GL rows bottom-up)
        Assert.Equal(0xFFFF0000u, Px(900, 20));      // right of the quad
        // the quad (x 600..840, GL rows 136..408): texel (0,0) green at the bottom left (GL t = 0), (1,0) white, (0,1) black, (1,1) yellow
        Assert.Equal(0xFF00FF00u, Px(650, 180));
        Assert.Equal(0xFFFFFFFFu, Px(790, 180));
        Assert.Equal(0xFF000000u, Px(650, 360));
        Assert.Equal(0xFF00FFFFu, Px(790, 360));
    }

    /// <summary>A framebuffer object (texture colour + depth renderbuffer) with the depth test, the scissor and face
    /// culling, then its texture drawn into the window: the pixels of both are read back.</summary>
    [PigletRenderFact]
    public void Draw_FramebufferObject_DepthScissorCull()
    {
        IGpuBackend backend;
        try { backend = PigletHarness.NewRenderer(); }
        catch (Exception e) { output.WriteLine($"no Vulkan device: {e.Message}"); return; }
        using var h = new PigletHarness(fx, true, backend);
        byte[]? fbo = null, win = null;
        h.Run(() =>
        {
            h.MakeCurrentWindow();
            uint prog = h.Program(TriVs, TriFs, out string log);
            Assert.True(prog != 0, log);
            h.Call("glUseProgram", prog);
            int off = h.I("glGetUniformLocation", prog, "uOffset"), tint = h.I("glGetUniformLocation", prog, "uTint[1]");
            int useTex = h.I("glGetUniformLocation", prog, "uUseTex"), tex = h.I("glGetUniformLocation", prog, "uTex");
            h.Call("glUniform1f", useTex, 0f);
            h.Call("glDisableVertexAttribArray", 1u);
            h.Call("glDisableVertexAttribArray", 2u);
            h.Call("glEnableVertexAttribArray", 0u);
            uint n = h.Mem(16);
            h.Call("glGenTextures", 1, n);
            uint ct = GuestMemory.R32(n);
            h.Call("glBindTexture", GL_TEXTURE_2D, ct);
            h.Call("glTexImage2D", GL_TEXTURE_2D, 0, (int)GL_RGBA, 64, 64, 0, GL_RGBA, GL_UNSIGNED_BYTE, 0u);
            h.Call("glTexParameteri", GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
            h.Call("glTexParameteri", GL_TEXTURE_2D, 0x2800u, (int)GL_NEAREST);
            h.Call("glGenRenderbuffers", 1, n);
            uint depth = GuestMemory.R32(n);
            h.Call("glBindRenderbuffer", GL_RENDERBUFFER, depth);
            h.Call("glRenderbufferStorage", GL_RENDERBUFFER, GL_DEPTH_COMPONENT16, 64, 64);
            h.Call("glGenFramebuffers", 1, n);
            h.Call("glBindFramebuffer", GL_FRAMEBUFFER, GuestMemory.R32(n));
            h.Call("glFramebufferTexture2D", GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, ct, 0);
            h.Call("glFramebufferRenderbuffer", GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, depth);
            Assert.Equal(GL_FRAMEBUFFER_COMPLETE, h.U("glCheckFramebufferStatus", GL_FRAMEBUFFER));
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            h.Call("glViewport", 0, 0, 64, 64);
            h.Call("glClearColor", 0f, 1f, 0f, 1f);
            h.Call("glClear", GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            h.Call("glEnable", GL_DEPTH_TEST);
            // red on the left half at depth 0.5, then white over everything at 0.8: white only on the right
            uint left = h.Floats(-1, -1, 0, -1, -1, 1, 0, 1), full = h.Floats(-1, -1, 1, -1, -1, 1, 1, 1);
            h.Call("glVertexAttrib4f", 1u, 1f, 0f, 0f, 1f);
            h.Call("glUniform4f", tint, 1f, 1f, 1f, 1f);
            h.Call("glUniform4f", off, 0f, 0f, 0f, 0f);
            h.Call("glVertexAttribPointer", 0u, 2, GL_FLOAT, false, 0, left);
            h.Call("glDrawArrays", GL_TRIANGLE_STRIP, 0, 4);
            h.Call("glVertexAttrib4f", 1u, 1f, 1f, 1f, 1f);
            h.Call("glUniform4f", off, 0f, 0f, 0.6f, 0f);
            h.Call("glVertexAttribPointer", 0u, 2, GL_FLOAT, false, 0, full);
            h.Call("glDrawArrays", GL_TRIANGLE_STRIP, 0, 4);
            h.Call("glDisable", GL_DEPTH_TEST);
            // the scissored clear: blue in the bottom-left 16x16
            h.Call("glEnable", 0xC11u);
            h.Call("glScissor", 0, 0, 16, 16);
            h.Call("glClearColor", 0f, 0f, 1f, 1f);
            h.Call("glClear", GL_COLOR_BUFFER_BIT);
            h.Call("glDisable", 0xC11u);
            // culling back faces: a clockwise triangle (top right) is culled, a counter-clockwise one (top left) drawn
            h.Call("glEnable", 0xB44u);
            h.Call("glUniform4f", off, 0f, 0f, 0f, 0f);
            h.Call("glVertexAttrib4f", 1u, 1f, 1f, 0f, 1f);
            uint ccw = h.Floats(-1, 0.5f, -0.5f, 0.5f, -0.75f, 1), cw = h.Floats(0.5f, 0.5f, 0.75f, 1, 1, 0.5f);
            h.Call("glVertexAttribPointer", 0u, 2, GL_FLOAT, false, 0, ccw);
            h.Call("glDrawArrays", GL_TRIANGLES, 0, 3);
            h.Call("glVertexAttribPointer", 0u, 2, GL_FLOAT, false, 0, cw);
            h.Call("glDrawArrays", GL_TRIANGLES, 0, 3);
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            uint rb = h.Mem(64 * 64 * 4);
            h.Call("glReadPixels", 0, 0, 64, 64, GL_RGBA, GL_UNSIGNED_BYTE, rb);
            fbo = GuestMemory.ReadSpan(rb, 64 * 64 * 4).ToArray();
            // the window: black, the same culling test, and the FBO's texture on a quad
            h.Call("glBindFramebuffer", GL_FRAMEBUFFER, 0u);
            h.Call("glViewport", 0, 0, 960, 544);
            h.Call("glClearColor", 0f, 0f, 0f, 1f);
            h.Call("glClear", GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            h.Call("glVertexAttribPointer", 0u, 2, GL_FLOAT, false, 0, ccw);
            h.Call("glDrawArrays", GL_TRIANGLES, 0, 3);
            h.Call("glVertexAttribPointer", 0u, 2, GL_FLOAT, false, 0, cw);
            h.Call("glDrawArrays", GL_TRIANGLES, 0, 3);
            h.Call("glDisable", 0xB44u);
            uint quad = h.Floats(-0.5f, -1, 0, 0, 0.5f, -1, 1, 0, -0.5f, 0, 0, 1, 0.5f, 0, 1, 1);
            h.Call("glVertexAttribPointer", 0u, 2, GL_FLOAT, false, 16, quad);
            h.Call("glEnableVertexAttribArray", 2u);
            h.Call("glVertexAttribPointer", 2u, 2, GL_FLOAT, false, 16, quad + 8);
            h.Call("glUniform1i", tex, 0);
            h.Call("glActiveTexture", 0x84C0u);
            h.Call("glBindTexture", GL_TEXTURE_2D, ct);
            h.Call("glUniform1f", useTex, 1f);
            h.Call("glDrawArrays", GL_TRIANGLE_STRIP, 0, 4);
            Assert.Equal(GL_NO_ERROR, h.U("glGetError"));
            uint wb = h.Mem(960 * 544 * 4);
            h.Call("glReadPixels", 0, 0, 960, 544, GL_RGBA, GL_UNSIGNED_BYTE, wb);
            win = GuestMemory.ReadSpan(wb, 960 * 544 * 4).ToArray();
        });
        uint F(int x, int y) => BitConverter.ToUInt32(fbo!, (y * 64 + x) * 4);
        uint W(int x, int y) => BitConverter.ToUInt32(win!, (y * 960 + x) * 4);
        output.WriteLine($"fbo: left {F(20, 30):X8} right {F(45, 30):X8} scissor {F(5, 5):X8} ccw {F(8, 52):X8} cw {F(56, 52):X8}");
        output.WriteLine($"win: ccw {W(120, 440):X8} cw {W(840, 440):X8} tex-left {W(400, 136):X8} tex-right {W(560, 136):X8} tex-corner {W(250, 10):X8}");
        Assert.Equal(0xFF0000FFu, F(20, 30));  // red in front
        Assert.Equal(0xFFFFFFFFu, F(45, 30));  // white where nothing was in front
        Assert.Equal(0xFFFF0000u, F(5, 5));    // scissored clear
        Assert.Equal(0xFF00FFFFu, F(8, 52));  // the counter-clockwise triangle
        Assert.Equal(0xFFFFFFFFu, F(56, 52));  // the clockwise one was culled: the white quad stays
        Assert.Equal(0xFF00FFFFu, W(120, 440));
        Assert.Equal(0xFF000000u, W(840, 440));
        // the FBO texture on the quad (x 240..720, GL rows 0..272): its left half red, right half white, blue corner
        Assert.Equal(0xFF0000FFu, W(400, 136));
        Assert.Equal(0xFFFFFFFFu, W(560, 136));
        Assert.Equal(0xFFFF0000u, W(250, 10));
    }
}

/// <summary>Runs Piglet calls on a guest thread of a test emulator.</summary>
sealed class PigletHarness : IDisposable
{
    readonly string _root;
    readonly Emulator _emu;
    public readonly Piglet Piglet;
    private uint _bump;
    private readonly uint _arena;
    const uint ArenaSize = 8 << 20;
    public uint Window, Context;

    public PigletHarness(GuestMemoryFixture fx, bool compiler, IGpuBackend? backend = null)
    {
        _root = Path.Combine(Path.GetTempPath(), "vitasharp-piglet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        if (compiler && PigletCompiler.Path is { } shacccg)
        {
            Directory.CreateDirectory(Path.Combine(_root, "ur0", "data"));
            File.Copy(shacccg, Path.Combine(_root, "ur0", "data", "libshacccg.suprx"));
        }
        _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
        if (backend != null) _emu.Gpu.Backend = backend;
        Piglet = _emu.Hle.Modules.OfType<Piglet>().First();
        _arena = _emu.Memory.Alloc(ArenaSize, "piglet test arena");
    }

    public static IGpuBackend NewRenderer() => new VitaSharp.Renderer.Vulkan.VulkanRenderer(new VitaSharp.Renderer.Vulkan.VulkanRendererOptions { AsyncShaders = false });

    public void Run(Action body)
    {
        Exception? failure = null;
        var t = _emu.Kernel.CreateThread("piglet-test", 0, 0x10000100, 0x40000, 0, 0x70000);
        t.HostBody = _ => { try { body(); } catch (Exception e) { failure = e; } };
        t.Start(0, 0);
        Assert.True(t.WaitForExit(600_000), "piglet test thread did not finish");
        _emu.Kernel.DeleteThread(t);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static readonly Dictionary<string, MethodInfo> Methods = typeof(Piglet).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());

    public object? Call(string name, params object[] args)
    {
        var m = Methods[name];
        var ps = m.GetParameters();
        var conv = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var a = args[i];
            var pt = ps[i].ParameterType;
            conv[i] = a.GetType() == pt ? a : pt.IsEnum ? Enum.ToObject(pt, a) : pt == typeof(string) || pt.IsGenericType ? a : Convert.ChangeType(a, pt);
        }
        return m.Invoke(Piglet, conv);
    }

    public uint U(string name, params object[] args) => Call(name, args) switch { uint u => u, int i => (uint)i, bool b => b ? 1u : 0u, null => 0, var o => Convert.ToUInt32(o) };
    public int I(string name, params object[] args) => (int)U(name, args);
    public bool B(string name, params object[] args) => U(name, args) != 0;

    public uint Mem(uint size)
    {
        uint p = _arena + _bump;
        _bump = (_bump + size + 15) & ~15u;
        if (_bump > ArenaSize) throw new InvalidOperationException("piglet test arena exhausted");
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    public uint Ints(params int[] v) { uint p = Mem((uint)v.Length * 4); for (int i = 0; i < v.Length; i++) GuestMemory.W32(p + (uint)i * 4, (uint)v[i]); return p; }
    public uint Floats(params float[] v) { uint p = Mem((uint)v.Length * 4); for (int i = 0; i < v.Length; i++) GuestMemory.Write(p + (uint)i * 4, v[i]); return p; }

    public uint Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        uint p = Mem((uint)b.Length + 1);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    uint Init()
    {
        uint dpy = U("eglGetDisplay", 0u);
        U("eglInitialize", dpy, new Ptr<int>(0), new Ptr<int>(0));
        return dpy;
    }

    public void MakeCurrentPbuffer(int w, int hgt)
    {
        uint dpy = Init();
        uint surf = U("eglCreatePbufferSurface", dpy, 1u, Ints(0x3057, w, 0x3056, hgt, 0x3038));
        Context = U("eglCreateContext", dpy, 1u, 0u, Ints(0x3098, 2, 0x3038));
        Assert.Equal(1u, U("eglMakeCurrent", dpy, surf, surf, Context));
    }

    public void MakeCurrentWindow()
    {
        uint dpy = Init();
        Window = U("eglCreateWindowSurface", dpy, 1u, 0u, 0u);
        Context = U("eglCreateContext", dpy, 1u, 0u, Ints(0x3098, 2, 0x3038));
        Assert.Equal(1u, U("eglMakeCurrent", dpy, Window, Window, Context));
    }

    public void Source(uint shader, string src)
    {
        uint strs = Mem(4);
        GuestMemory.W32(strs, Str(src));
        Call("glShaderSource", shader, 1, strs, 0u);
    }

    public string ShaderLog(uint shader)
    {
        uint buf = Mem(4096), len = Mem(4);
        Call("glGetShaderInfoLog", shader, 4096, new Ptr<int>(len), buf);
        return GuestMemory.ReadCString(buf);
    }

    /// <summary>A linked program (aPos at 0, aColor at 1, aUv at 2), or 0 with the logs.</summary>
    public uint Program(string vsSrc, string fsSrc, out string log)
    {
        uint vs = U("glCreateShader", 0x8B31u), fs = U("glCreateShader", 0x8B30u);
        Source(vs, vsSrc);
        Source(fs, fsSrc);
        Call("glCompileShader", vs);
        Call("glCompileShader", fs);
        uint p = U("glCreateProgram");
        Call("glAttachShader", p, vs);
        Call("glAttachShader", p, fs);
        Call("glBindAttribLocation", p, 0u, "aPos");
        Call("glBindAttribLocation", p, 1u, "aColor");
        Call("glBindAttribLocation", p, 2u, "aUv");
        Call("glLinkProgram", p);
        uint st = Mem(4), buf = Mem(4096), len = Mem(4);
        Call("glGetProgramiv", p, 0x8B82u, new Ptr<int>(st));
        Call("glGetProgramInfoLog", p, 4096, new Ptr<int>(len), buf);
        log = $"vs: {ShaderLog(vs)}\nfs: {ShaderLog(fs)}\nprogram: {GuestMemory.ReadCString(buf)}";
        return GuestMemory.R32(st) != 0 ? p : 0;
    }

    public void Dispose()
    {
        var backend = _emu.Gpu.Backend;
        if (backend != null)
        {
            _emu.Gpu.WaitIdle();
            _emu.Gpu.Backend = null;
        }
        _emu.Dispose();
        backend?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>The runtime compiler: ur0:data/libshacccg.suprx of the VitaSharp or Vita3K data folder (read only: copied
/// into each test's own file system).</summary>
static class PigletCompiler
{
    static readonly Lazy<string?> _path = new(() =>
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return null;
        foreach (var p in new[] { System.IO.Path.Combine(appData, "VitaSharp", "fs", "ur0", "data", "libshacccg.suprx"), System.IO.Path.Combine(appData, "Vita3K", "Vita3K", "ur0", "data", "libshacccg.suprx") })
            if (File.Exists(p)) return p;
        return null;
    });
    public static string? Path => _path.Value;
}

public sealed class PigletCompilerFactAttribute : FactAttribute
{
    public PigletCompilerFactAttribute()
    {
        if (PigletCompiler.Path == null) Skip = "libshacccg.suprx not found in ur0:data of the VitaSharp or Vita3K data folder";
    }
}

public sealed class PigletRenderFactAttribute : FactAttribute
{
    public PigletRenderFactAttribute()
    {
        if (PigletCompiler.Path == null) Skip = "libshacccg.suprx not found in ur0:data of the VitaSharp or Vita3K data folder";
    }
}
