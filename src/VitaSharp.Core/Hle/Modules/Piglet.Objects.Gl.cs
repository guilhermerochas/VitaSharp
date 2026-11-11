using System.Numerics;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Buffer, texture, renderbuffer and framebuffer objects of libScePiglet, with their GLES 2.0 errors.</summary>
public sealed partial class Piglet
{
    const uint TexLinear = 0x60000000, TexSwizzled = 0, TexSwizzledArbitrary = 0xA0000000, TexCube = 0x40000000, TexCubeArbitrary = 0xE0000000;

    // ------------------------------------------------------------------ names

    static void GenNames(int n, uint names, HashSet<uint> reserved, ref uint next, Func<uint, bool> used)
    {
        for (int i = 0; i < n; i++)
        {
            while (reserved.Contains(next) || used(next)) next++;
            reserved.Add(next);
            GuestMemory.W32(names + (uint)i * 4, next++);
        }
    }

    bool GenCheck(PgContext? c, int n, uint names)
    {
        if (c == null) return false;
        if (n < 0) { Err(c, Gl.INVALID_VALUE); return false; }
        return names != 0 && n > 0;
    }

    // ------------------------------------------------------------------ buffers

    [Hle]
    void glGenBuffers(int n, uint buffers)
    {
        var c = Cur;
        if (!GenCheck(c, n, buffers)) return;
        var sh = c!.Shared;
        GenNames(n, buffers, sh.BufferNames, ref sh.NextBuffer, sh.Buffers.ContainsKey);
    }

    [Hle]
    void glDeleteBuffers(int n, uint buffers)
    {
        var c = Cur;
        if (c == null) return;
        if (n < 0) { Err(c, Gl.INVALID_VALUE); return; }
        for (int i = 0; i < n && buffers != 0; i++)
        {
            uint name = GuestMemory.R32(buffers + (uint)i * 4);
            if (name == 0) continue;
            c.Shared.BufferNames.Remove(name);
            if (!c.Shared.Buffers.Remove(name, out var b)) continue;
            b.Deleted = true;
            if (c.ArrayBuffer == b) c.ArrayBuffer = null;
            if (c.ElementBuffer == b) c.ElementBuffer = null;
            foreach (var a in c.Attribs) if (a.Buffer == b) a.Buffer = null;
            FreeAfterGpu(b.Addr, b.GpuSeq);
            b.Addr = 0;
        }
    }

    [Hle]
    void glBindBuffer(uint target, uint buffer)
    {
        if (Cur is not { } c) return;
        if (target is not (Gl.ARRAY_BUFFER or Gl.ELEMENT_ARRAY_BUFFER)) { Err(c, Gl.INVALID_ENUM); return; }
        PgBuffer? b = null;
        if (buffer != 0 && !c.Shared.Buffers.TryGetValue(buffer, out b))
        {
            b = new PgBuffer(buffer);
            c.Shared.Buffers[buffer] = b;
            c.Shared.BufferNames.Add(buffer);
        }
        if (target == Gl.ARRAY_BUFFER) c.ArrayBuffer = b; else c.ElementBuffer = b;
    }

    [Hle] bool glIsBuffer(uint buffer) => Cur is { } c && buffer != 0 && c.Shared.Buffers.ContainsKey(buffer);

    PgBuffer? BoundBuffer(PgContext c, uint target, out bool badEnum)
    {
        badEnum = target is not (Gl.ARRAY_BUFFER or Gl.ELEMENT_ARRAY_BUFFER);
        return target == Gl.ARRAY_BUFFER ? c.ArrayBuffer : target == Gl.ELEMENT_ARRAY_BUFFER ? c.ElementBuffer : null;
    }

    [Hle]
    void glBufferData(uint target, int size, uint data, uint usage)
    {
        if (Cur is not { } c) return;
        var b = BoundBuffer(c, target, out bool bad);
        if (bad || usage is not (Gl.STREAM_DRAW or Gl.STATIC_DRAW or Gl.DYNAMIC_DRAW)) { Err(c, Gl.INVALID_ENUM); return; }
        if (size < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (b == null) { Err(c, Gl.INVALID_OPERATION); return; }
        // a new data store: the old one stays for the draws still reading it (orphaning)
        if (b.Addr == 0 || (uint)size != b.Size || b.GpuSeq > Emu.Gpu.Completed)
        {
            FreeAfterGpu(b.Addr, b.GpuSeq);
            b.Addr = size > 0 ? Alloc((uint)size, "buffer object", MemoryRegion.User, 16) : 0;
            if (size > 0 && b.Addr == 0) { b.Size = 0; Err(c, Gl.OUT_OF_MEMORY); return; }
            b.GpuSeq = 0;
        }
        b.Size = (uint)size;
        b.Usage = usage;
        if (data != 0 && size > 0) GuestMemory.Copy(b.Addr, data, (uint)size);
    }

    [Hle]
    void glBufferSubData(uint target, int offset, int size, uint data)
    {
        if (Cur is not { } c) return;
        var b = BoundBuffer(c, target, out bool bad);
        if (bad) { Err(c, Gl.INVALID_ENUM); return; }
        if (offset < 0 || size < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (b == null) { Err(c, Gl.INVALID_OPERATION); return; }
        if ((long)offset + size > b.Size) { Err(c, Gl.INVALID_VALUE); return; }
        if (size == 0 || data == 0) return;
        b.GpuSeq = OrderedWrite(b.Addr + (uint)offset, ReadBytes(data, size), b.GpuSeq);
    }

    [Hle]
    void glGetBufferParameteriv(uint target, uint pname, Ptr<int> param)
    {
        if (Cur is not { } c) return;
        var b = BoundBuffer(c, target, out bool bad);
        if (bad || pname is not (Gl.BUFFER_SIZE or Gl.BUFFER_USAGE)) { Err(c, Gl.INVALID_ENUM); return; }
        if (b == null) { Err(c, Gl.INVALID_OPERATION); return; }
        param.TrySet(pname == Gl.BUFFER_SIZE ? (int)b.Size : (int)b.Usage);
    }

    // ------------------------------------------------------------------ texture formats and layout

    static readonly PgTexFormat[] Uncompressed =
    [
        new(Gl.RGBA, 0x0C000000, 32, false, ColorFormat: 0x00000000),  // U8U8U8U8_ABGR (bytes R G B A)
        new(Gl.RGB, 0x0C004000, 32, false, ColorFormat: 0x00000000),   // X8U8U8U8_1BGR, from RGB bytes
        new(Gl.RGBA4, 0x02002000, 16, false, ColorFormat: 0x50200000), // U4U4U4U4_RGBA
        new(Gl.RGB5_A1, 0x04002000, 16, false, ColorFormat: 0x40200000), // U5U5U5U1_RGBA
        new(Gl.RGB565, 0x05001000, 16, false, ColorFormat: 0x30100000), // U5U6U5_RGB
        new(Gl.LUMINANCE, 0x00005000, 8, false),       // U8_1RRR
        new(Gl.ALPHA, 0x00006000, 8, false),           // U8_R000
        new(Gl.LUMINANCE_ALPHA, 0x07002000, 16, false), // U8U8_GRRR
        new(Gl.BGRA_EXT, 0x0C001000, 32, false, ColorFormat: 0x00100000), // U8U8U8U8_ARGB (bytes B G R A)
        new(Gl.HALF_FLOAT_OES | 0x10000, 0x1B000000, 64, false),   // RGBA half: F16F16F16F16_ABGR
        new(Gl.HALF_FLOAT_OES | 0x20000, 0x0B005000, 16, false),   // LUMINANCE half: F16_1RRR
        new(Gl.HALF_FLOAT_OES | 0x30000, 0x0B006000, 16, false),   // ALPHA half: F16_R000
        new(Gl.HALF_FLOAT_OES | 0x40000, 0x11002000, 32, false),   // LUMINANCE_ALPHA half: F16F16_GRRR
    ];

    static readonly PgTexFormat[] Compressed =
    [
        new(Gl.COMPRESSED_RGB_S3TC_DXT1, 0x85004000, 4, true, 4, 4, 8),
        new(Gl.COMPRESSED_RGBA_S3TC_DXT1, 0x85000000, 4, true, 4, 4, 8),
        new(Gl.COMPRESSED_RGBA_S3TC_DXT3, 0x86000000, 8, true, 4, 4, 16),
        new(Gl.COMPRESSED_RGBA_S3TC_DXT5, 0x87000000, 8, true, 4, 4, 16),
        new(Gl.ETC1_RGB8_OES, 0x84000000, 4, true, 4, 4, 8),
        new(Gl.COMPRESSED_RGB_PVRTC_4BPPV1, 0x81004000, 4, true, 4, 4, 8, Pvrtc: true),
        new(Gl.COMPRESSED_RGB_PVRTC_2BPPV1, 0x80004000, 2, true, 8, 4, 8, Pvrtc: true),
        new(Gl.COMPRESSED_RGBA_PVRTC_4BPPV1, 0x81000000, 4, true, 4, 4, 8, Pvrtc: true),
        new(Gl.COMPRESSED_RGBA_PVRTC_2BPPV1, 0x80000000, 2, true, 8, 4, 8, Pvrtc: true),
        new(Gl.COMPRESSED_RGBA_PVRTC_2BPPV2_IMG, 0x82000000, 2, true, 8, 4, 8, Pvrtc: true),
        new(Gl.COMPRESSED_RGBA_PVRTC_4BPPV2_IMG, 0x83000000, 4, true, 4, 4, 8, Pvrtc: true),
    ];

    internal static IReadOnlyList<uint> CompressedFormats => Compressed.Select(f => f.GlInternal).ToArray();

    /// <summary>The texture format of a (format, type) pair of glTexImage2D; null: GL_INVALID_ENUM (or GL_INVALID_OPERATION
    /// for a valid but mismatched pair, <paramref name="mismatch"/>).</summary>
    static PgTexFormat? TexFormat(uint format, uint type, out bool mismatch)
    {
        mismatch = false;
        uint key = (format, type) switch
        {
            (Gl.RGBA, Gl.UNSIGNED_BYTE) => Gl.RGBA,
            (Gl.RGB, Gl.UNSIGNED_BYTE) => Gl.RGB,
            (Gl.RGBA, Gl.UNSIGNED_SHORT_4_4_4_4) => Gl.RGBA4,
            (Gl.RGBA, Gl.UNSIGNED_SHORT_5_5_5_1) => Gl.RGB5_A1,
            (Gl.RGB, Gl.UNSIGNED_SHORT_5_6_5) => Gl.RGB565,
            (Gl.LUMINANCE, Gl.UNSIGNED_BYTE) => Gl.LUMINANCE,
            (Gl.ALPHA, Gl.UNSIGNED_BYTE) => Gl.ALPHA,
            (Gl.LUMINANCE_ALPHA, Gl.UNSIGNED_BYTE) => Gl.LUMINANCE_ALPHA,
            (Gl.BGRA_EXT, Gl.UNSIGNED_BYTE) => Gl.BGRA_EXT,
            (Gl.RGBA or Gl.RGB, Gl.HALF_FLOAT_OES) => Gl.HALF_FLOAT_OES | 0x10000,
            (Gl.LUMINANCE, Gl.HALF_FLOAT_OES) => Gl.HALF_FLOAT_OES | 0x20000,
            (Gl.ALPHA, Gl.HALF_FLOAT_OES) => Gl.HALF_FLOAT_OES | 0x30000,
            (Gl.LUMINANCE_ALPHA, Gl.HALF_FLOAT_OES) => Gl.HALF_FLOAT_OES | 0x40000,
            _ => 0,
        };
        if (key == 0)
        {
            bool fmtOk = format is Gl.RGBA or Gl.RGB or Gl.LUMINANCE or Gl.ALPHA or Gl.LUMINANCE_ALPHA or Gl.BGRA_EXT;
            bool typeOk = type is Gl.UNSIGNED_BYTE or Gl.UNSIGNED_SHORT_4_4_4_4 or Gl.UNSIGNED_SHORT_5_5_5_1 or Gl.UNSIGNED_SHORT_5_6_5 or Gl.HALF_FLOAT_OES;
            mismatch = fmtOk && typeOk;
            return null;
        }
        return Uncompressed.First(f => f.GlInternal == key);
    }

    /// <summary>Bytes per pixel of client data in (format, type).</summary>
    static int ClientPixelBytes(uint format, uint type) => type switch
    {
        Gl.UNSIGNED_SHORT_4_4_4_4 or Gl.UNSIGNED_SHORT_5_5_5_1 or Gl.UNSIGNED_SHORT_5_6_5 => 2,
        Gl.HALF_FLOAT_OES => 2 * Components(format),
        _ => Components(format),
    };

    static int Components(uint format) => format switch
    {
        Gl.RGBA or Gl.BGRA_EXT => 4, Gl.RGB => 3, Gl.LUMINANCE_ALPHA => 2, _ => 1,
    };

    static uint Align(uint v, uint a) => (v + a - 1) / a * a;

    /// <summary>Sizes and offsets of every level, as the renderer reads a GXM texture of this type (VulkanRenderer.Textures
    /// TextureLayout): linear rows aligned to 8 texels, mipmapped linear levels padded to power-of-two sizes, swizzled
    /// levels at power-of-two sizes, compressed levels in blocks.</summary>
    static void LayOut(PgTexture t)
    {
        var f = t.Format!;
        uint type = t.GxmType;
        bool swizzled = type is TexSwizzled or TexCube or TexSwizzledArbitrary or TexCubeArbitrary;
        bool pow2 = type is TexSwizzled or TexCube;
        int levels = t.LevelCount;
        var old = t.Levels;
        t.Levels = new PgLevel[t.Faces, levels];
        uint offset = 0;
        for (int m = 0; m < levels; m++)
        {
            uint mw = Math.Max(t.Width >> m, 1), mh = Math.Max(t.Height >> m, 1);
            uint pw = mw, ph = mh;
            if (pow2 || (swizzled && !f.Compressed)) { pw = BitOperations.RoundUpToPowerOf2(mw); ph = BitOperations.RoundUpToPowerOf2(mh); }
            uint rowPitch, bytes;
            if (f.Compressed)
            {
                uint bw = Math.Max(Align(pw, (uint)f.BlockW) / (uint)f.BlockW, 1), bh = Math.Max(Align(ph, (uint)f.BlockH) / (uint)f.BlockH, 1);
                if (f.Pvrtc) { bw = Math.Max(bw, 2); bh = Math.Max(bh, 2); }
                rowPitch = bw * (uint)f.BlockBytes;
                bytes = rowPitch * bh;
                if (swizzled && !pow2 && !f.Pvrtc)
                    bytes = Math.Max(bytes, (TextureLayout.Morton(bw - 1, bh - 1, BitOperations.RoundUpToPowerOf2(bw), BitOperations.RoundUpToPowerOf2(bh)) + 1) * (uint)f.BlockBytes);
            }
            else if (swizzled)
            {
                rowPitch = (uint)(pw * f.Bpp / 8);
                bytes = (uint)((ulong)pw * ph * (uint)f.Bpp / 8);
            }
            else
            {
                rowPitch = (uint)(Align(mw, 8) * f.Bpp / 8);
                bytes = rowPitch * mh;
            }
            for (int face = 0; face < t.Faces; face++)
                t.Levels[face, m] = new PgLevel { W = mw, H = mh, Offset = offset, Size = bytes, RowPitch = rowPitch,
                    Defined = face < old.GetLength(0) && m < old.GetLength(1) && old[face, m].Defined && old[face, m].W == mw && old[face, m].H == mh };
            if (levels > 1 && !f.Compressed && type == TexLinear)
            {
                uint lw = Math.Max(BitOperations.RoundUpToPowerOf2(t.Width) >> m, 1), lh = Math.Max(BitOperations.RoundUpToPowerOf2(t.Height) >> m, 1);
                offset += (uint)((ulong)Align(lw, 8) * lh * (uint)f.Bpp / 8);
            }
            else offset += bytes;
        }
        bool cube = type is TexCube or TexCubeArbitrary;
        t.FaceStride = cube
            ? TextureLayout.CubeFaceStride((uint)(levels - 1), (uint)levels, t.Width, t.Height, f.Bpp, f.Compressed, (uint)f.BlockW, (uint)f.BlockH, (uint)f.BlockBytes, offset)
            : offset;
        t.DataSize = t.FaceStride * (uint)(t.Faces - 1) + offset;
    }

    static int FullChain(uint w, uint h) => BitOperations.Log2(Math.Max(Math.Max(w, h), 1)) + 1;

    /// <summary>(Re)allocates the texture's store for level-0 size <paramref name="w"/>x<paramref name="h"/> in
    /// <paramref name="f"/>, keeping the levels already defined when nothing changed.</summary>
    bool EnsureStorage(PgContext c, PgTexture t, PgTexFormat f, uint w, uint h, int levels = 0)
    {
        bool cube = t.Target == Gl.TEXTURE_CUBE_MAP;
        uint type = cube ? (BitOperations.IsPow2(w) && BitOperations.IsPow2(h) ? TexCube : TexCubeArbitrary)
            : f.Compressed ? (BitOperations.IsPow2(w) && BitOperations.IsPow2(h) ? TexSwizzled : TexSwizzledArbitrary)
            : TexLinear;
        if (f.Pvrtc && type == TexSwizzledArbitrary) { Err(c, Gl.INVALID_VALUE); return false; } // PVRTC needs power-of-two sizes
        int count = levels > 0 ? levels : FullChain(w, h);
        if (t.HasStorage && t.Format == f && t.Width == w && t.Height == h && t.GxmType == type && t.LevelCount == count) return true;
        FreeAfterGpu(t.Data, t.GpuSeq);
        t.Format = f; t.Width = w; t.Height = h; t.GxmType = type; t.LevelCount = count; t.Faces = cube ? 6 : 1;
        t.Levels = new PgLevel[t.Faces, count];
        LayOut(t);
        t.Data = Alloc(t.DataSize, "texture", MemoryRegion.User, 1024);
        t.GpuSeq = 0;
        if (t.Data == 0) { Err(c, Gl.OUT_OF_MEMORY); return false; }
        return true;
    }

    /// <summary>Writes an image (GL rows bottom-up = the texture's t order, already in the stored texel format) into a
    /// level, at (x, y) for a sub-image; GPU ordered when draws read the texture.</summary>
    void PlaceTexels(PgTexture t, int face, int level, int x, int y, int w, int h, byte[] texels)
    {
        var lv = t.Levels[face, level];
        var f = t.Format!;
        uint baseAddr = t.Data + (uint)face * t.FaceStride + lv.Offset;
        bool swizzled = t.GxmType != TexLinear;
        var segs = new List<(uint, byte[])>();
        if (f.Compressed)
        {
            int bw = f.BlockW, bh = f.BlockH, bb = f.BlockBytes;
            int bx0 = x / bw, by0 = y / bh, nbx = (w + bw - 1) / bw, nby = (h + bh - 1) / bh;
            if (f.Pvrtc || !swizzled) { segs.Add((baseAddr, texels)); }
            else
            {
                uint gw = Math.Max(Align(t.GxmType is TexSwizzled or TexCube ? BitOperations.RoundUpToPowerOf2(lv.W) : lv.W, (uint)bw) / (uint)bw, 1);
                uint gh = Math.Max(Align(t.GxmType is TexSwizzled or TexCube ? BitOperations.RoundUpToPowerOf2(lv.H) : lv.H, (uint)bh) / (uint)bh, 1);
                uint pgw = BitOperations.RoundUpToPowerOf2(gw), pgh = BitOperations.RoundUpToPowerOf2(gh);
                for (int by = 0; by < nby; by++)
                    for (int bx = 0; bx < nbx; bx++)
                    {
                        int src = (by * nbx + bx) * bb;
                        if (src + bb > texels.Length) continue;
                        uint m = TextureLayout.Morton((uint)(bx0 + bx), (uint)(by0 + by), pgw, pgh);
                        segs.Add((baseAddr + m * (uint)bb, texels.AsSpan(src, bb).ToArray()));
                    }
            }
        }
        else
        {
            int bpp = f.Bpp / 8;
            if (!swizzled)
            {
                if (x == 0 && w == lv.W && lv.RowPitch == (uint)(w * bpp))
                    segs.Add((baseAddr + (uint)y * lv.RowPitch, texels));
                else
                    for (int row = 0; row < h; row++)
                        segs.Add((baseAddr + (uint)(y + row) * lv.RowPitch + (uint)(x * bpp), texels.AsSpan(row * w * bpp, w * bpp).ToArray()));
            }
            else
            {
                uint pw = BitOperations.RoundUpToPowerOf2(lv.W), ph = BitOperations.RoundUpToPowerOf2(lv.H);
                for (int row = 0; row < h; row++)
                    for (int col = 0; col < w; col++)
                        segs.Add((baseAddr + TextureLayout.Morton((uint)(x + col), (uint)(y + row), pw, ph) * (uint)bpp,
                            texels.AsSpan((row * w + col) * bpp, bpp).ToArray()));
            }
        }
        t.GpuSeq = OrderedWrites(segs, t.RenderTarget ? Math.Max(t.GpuSeq, Emu.Gpu.Submitted) : t.GpuSeq);
    }

    /// <summary>Several writes in GPU order (one command).</summary>
    long OrderedWrites(List<(uint Addr, byte[] Data)> segs, long busySeq)
    {
        if (segs.Count == 0) return busySeq;
        if (busySeq <= Emu.Gpu.Completed)
        {
            foreach (var (a, d) in segs) WriteBytes(a, d);
            return busySeq;
        }
        uint lo = segs.Min(s => s.Addr), hi = segs.Max(s => s.Addr + (uint)s.Data.Length);
        return Emu.Gpu.Submit(new Gpu.CallbackCommand
        {
            WriteAddress = lo, WriteSize = hi - lo,
            Action = () => { foreach (var (a, d) in segs) WriteBytes(a, d); },
        });
    }

    /// <summary>Client pixels (rows padded to the unpack alignment) as stored texels: RGB gets an opaque fourth byte, RGB
    /// half floats a 1.0 alpha.</summary>
    static byte[] ConvertClientPixels(uint src, int w, int h, uint format, uint type, int unpackAlignment, PgTexFormat f)
    {
        int cpb = ClientPixelBytes(format, type), row = w * cpb, pitch = (int)Align((uint)row, (uint)unpackAlignment);
        int spb = f.Bpp / 8;
        var outp = new byte[w * h * spb];
        if (src == 0) return outp;
        var data = GuestMemory.ReadSpan(src, pitch * (h - 1) + row);
        for (int y = 0; y < h; y++)
        {
            var s = data.Slice(y * pitch, row);
            var d = outp.AsSpan(y * w * spb, w * spb);
            if (cpb == spb) { s.CopyTo(d); continue; }
            for (int x = 0; x < w; x++)
            {
                if (type == Gl.HALF_FLOAT_OES)
                {
                    s.Slice(x * 6, 6).CopyTo(d.Slice(x * 8));
                    d[x * 8 + 6] = 0x00; d[x * 8 + 7] = 0x3C;
                }
                else
                {
                    d[x * 4] = s[x * 3]; d[x * 4 + 1] = s[x * 3 + 1]; d[x * 4 + 2] = s[x * 3 + 2]; d[x * 4 + 3] = 0xFF;
                }
            }
        }
        return outp;
    }

    // ------------------------------------------------------------------ texture objects

    [Hle]
    void glGenTextures(int n, uint textures)
    {
        var c = Cur;
        if (!GenCheck(c, n, textures)) return;
        var sh = c!.Shared;
        GenNames(n, textures, sh.TextureNames, ref sh.NextTexture, sh.Textures.ContainsKey);
    }

    [Hle]
    void glDeleteTextures(int n, uint textures)
    {
        var c = Cur;
        if (c == null) return;
        if (n < 0) { Err(c, Gl.INVALID_VALUE); return; }
        for (int i = 0; i < n && textures != 0; i++)
        {
            uint name = GuestMemory.R32(textures + (uint)i * 4);
            if (name == 0) continue;
            c.Shared.TextureNames.Remove(name);
            if (!c.Shared.Textures.Remove(name, out var t)) continue;
            t.Deleted = true;
            for (int u = 0; u < TextureUnits; u++) { if (c.Tex2D[u] == t) c.Tex2D[u] = null; if (c.TexCube[u] == t) c.TexCube[u] = null; }
            if (c.Framebuffer?.Color.Texture == t) c.Framebuffer.Color.Clear();
            if (_sceneTarget?.Fbo?.Color.Texture == t) EndScene();
            foreach (var s in _surfaces.Values) if (s.BoundTexture == t) { s.BoundTexture = null; t.Data = 0; }
            FreeAfterGpu(t.Data, t.GpuSeq);
            t.Data = 0;
        }
    }

    [Hle]
    void glBindTexture(uint target, uint texture)
    {
        if (Cur is not { } c) return;
        if (target is not (Gl.TEXTURE_2D or Gl.TEXTURE_CUBE_MAP)) { Err(c, Gl.INVALID_ENUM); return; }
        PgTexture? t = null;
        if (texture != 0)
        {
            if (!c.Shared.Textures.TryGetValue(texture, out t))
            {
                t = new PgTexture(texture) { Target = target };
                c.Shared.Textures[texture] = t;
                c.Shared.TextureNames.Add(texture);
            }
            else if (t.Target == 0) t.Target = target;
            else if (t.Target != target) { Err(c, Gl.INVALID_OPERATION); return; }
        }
        if (target == Gl.TEXTURE_2D) c.Tex2D[c.ActiveUnit] = t; else c.TexCube[c.ActiveUnit] = t;
    }

    [Hle] bool glIsTexture(uint texture) => Cur is { } c && texture != 0 && c.Shared.Textures.TryGetValue(texture, out var t) && t.Target != 0;

    [Hle]
    void glActiveTexture(uint texture)
    {
        if (Cur is not { } c) return;
        if (texture < Gl.TEXTURE0 || texture >= Gl.TEXTURE0 + TextureUnits) { Err(c, Gl.INVALID_ENUM); return; }
        c.ActiveUnit = texture - Gl.TEXTURE0;
    }

    /// <summary>The texture and face a glTexImage2D-style target addresses; GL_INVALID_ENUM / GL_INVALID_OPERATION as needed.</summary>
    PgTexture? ImageTarget(PgContext c, uint target, out int face)
    {
        face = 0;
        PgTexture? t;
        if (target == Gl.TEXTURE_2D) t = c.Tex2D[c.ActiveUnit];
        else if (target >= Gl.TEXTURE_CUBE_MAP_POSITIVE_X && target <= Gl.TEXTURE_CUBE_MAP_NEGATIVE_Z) { t = c.TexCube[c.ActiveUnit]; face = (int)(target - Gl.TEXTURE_CUBE_MAP_POSITIVE_X); }
        else { Err(c, Gl.INVALID_ENUM); return null; }
        if (t == null) { Err(c, Gl.INVALID_OPERATION); return null; } // texture 0: no image can be specified
        return t;
    }

    bool CheckLevelSize(PgContext c, uint target, int level, int w, int h, int border)
    {
        int maxLevel = BitOperations.Log2(MaxTextureSize);
        if (level < 0 || level > maxLevel || w < 0 || h < 0 || border != 0 || w > MaxTextureSize >> level || h > MaxTextureSize >> level)
        { Err(c, Gl.INVALID_VALUE); return false; }
        if (target != Gl.TEXTURE_2D && w != h) { Err(c, Gl.INVALID_VALUE); return false; } // cube faces are square
        return true;
    }

    /// <summary>Defines level <paramref name="level"/> of <paramref name="t"/> with <paramref name="w"/>x<paramref name="h"/>:
    /// the store follows the level-0 size it implies.</summary>
    bool DefineLevel(PgContext c, PgTexture t, int face, int level, PgTexFormat f, int w, int h)
    {
        if (t.Immutable) { Err(c, Gl.INVALID_OPERATION); return false; }
        uint w0 = (uint)Math.Max(w, 1) << level, h0 = (uint)Math.Max(h, 1) << level;
        bool fits = t.HasStorage && t.Format == f && level < t.LevelCount && t.Levels[face, level].W == (uint)Math.Max(w, 1) && t.Levels[face, level].H == (uint)Math.Max(h, 1);
        if (!fits)
        {
            // a level 0 picks the size; another level keeps the existing store when it matches, else implies one
            if (level == 0 || !t.HasStorage || t.Format != f) { if (!EnsureStorage(c, t, f, level == 0 ? (uint)Math.Max(w, 1) : w0, level == 0 ? (uint)Math.Max(h, 1) : h0)) return false; }
            else
            {
                Log.WarnOnce("piglet-level-size", "[piglet] a texture level whose size does not follow level 0: the texture is incomplete");
                for (int fc = 0; fc < t.Faces; fc++) for (int m = 0; m < t.LevelCount; m++) t.Levels[fc, m].Defined = false;
                if (!EnsureStorage(c, t, f, w0, h0)) return false;
            }
        }
        if (level >= t.LevelCount) { Err(c, Gl.INVALID_VALUE); return false; }
        t.Levels[face, level].Defined = w > 0 && h > 0;
        return true;
    }

    [Hle]
    void glTexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, uint pixels)
    {
        if (Cur is not { } c) return;
        var f = TexFormat(format, type, out bool mismatch);
        if (f == null) { Err(c, mismatch ? Gl.INVALID_OPERATION : Gl.INVALID_ENUM); return; }
        if ((uint)internalFormat != format) { Err(c, (uint)internalFormat is Gl.RGBA or Gl.RGB or Gl.LUMINANCE or Gl.ALPHA or Gl.LUMINANCE_ALPHA or Gl.BGRA_EXT ? Gl.INVALID_OPERATION : Gl.INVALID_VALUE); return; }
        if (ImageTarget(c, target, out int face) is not { } t) return;
        if (!CheckLevelSize(c, target, level, width, height, border)) return;
        if (!DefineLevel(c, t, face, level, f, width, height)) return;
        if (width == 0 || height == 0) return;
        PlaceTexels(t, face, level, 0, 0, width, height, ConvertClientPixels(pixels, width, height, format, type, c.UnpackAlignment, f));
    }

    [Hle]
    void glTexSubImage2D(uint target, int level, int xoffset, int yoffset, int width, int height, uint format, uint type, uint pixels)
    {
        if (Cur is not { } c) return;
        var f = TexFormat(format, type, out bool mismatch);
        if (f == null) { Err(c, mismatch ? Gl.INVALID_OPERATION : Gl.INVALID_ENUM); return; }
        if (ImageTarget(c, target, out int face) is not { } t) return;
        if (level < 0 || width < 0 || height < 0 || xoffset < 0 || yoffset < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (!t.HasStorage || level >= t.LevelCount || !t.Levels[face, level].Defined) { Err(c, Gl.INVALID_OPERATION); return; }
        var lv = t.Levels[face, level];
        if (xoffset + width > lv.W || yoffset + height > lv.H) { Err(c, Gl.INVALID_VALUE); return; }
        if (t.Format != f) { Err(c, Gl.INVALID_OPERATION); return; }
        if (width == 0 || height == 0) return;
        PlaceTexels(t, face, level, xoffset, yoffset, width, height, ConvertClientPixels(pixels, width, height, format, type, c.UnpackAlignment, f));
    }

    static int CompressedSize(PgTexFormat f, int w, int h)
    {
        int bw = (w + f.BlockW - 1) / f.BlockW, bh = (h + f.BlockH - 1) / f.BlockH;
        if (f.Pvrtc) { bw = Math.Max(bw, 2); bh = Math.Max(bh, 2); }
        return bw * bh * f.BlockBytes;
    }

    [Hle]
    void glCompressedTexImage2D(uint target, int level, uint internalFormat, int width, int height, int border, int imageSize, uint data)
    {
        if (Cur is not { } c) return;
        var f = Compressed.FirstOrDefault(x => x.GlInternal == internalFormat);
        if (f == null) { Err(c, Gl.INVALID_ENUM); return; }
        if (ImageTarget(c, target, out int face) is not { } t) return;
        if (!CheckLevelSize(c, target, level, width, height, border)) return;
        if (imageSize != CompressedSize(f, width, height)) { Err(c, Gl.INVALID_VALUE); return; }
        if (!DefineLevel(c, t, face, level, f, width, height)) return;
        if (width == 0 || height == 0 || data == 0) return;
        PlaceTexels(t, face, level, 0, 0, width, height, ReadBytes(data, imageSize));
    }

    [Hle]
    void glCompressedTexSubImage2D(uint target, int level, int xoffset, int yoffset, int width, int height, uint format, int imageSize, uint data)
    {
        if (Cur is not { } c) return;
        var f = Compressed.FirstOrDefault(x => x.GlInternal == format);
        if (f == null) { Err(c, Gl.INVALID_ENUM); return; }
        if (ImageTarget(c, target, out int face) is not { } t) return;
        if (level < 0 || width < 0 || height < 0 || xoffset < 0 || yoffset < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (!t.HasStorage || t.Format != f || level >= t.LevelCount || !t.Levels[face, level].Defined) { Err(c, Gl.INVALID_OPERATION); return; }
        var lv = t.Levels[face, level];
        if (xoffset + width > lv.W || yoffset + height > lv.H) { Err(c, Gl.INVALID_VALUE); return; }
        // PVRTC images are replaced whole; block formats on block boundaries (or up to the level's edge)
        bool aligned = xoffset % f.BlockW == 0 && yoffset % f.BlockH == 0
            && (width % f.BlockW == 0 || xoffset + width == lv.W) && (height % f.BlockH == 0 || yoffset + height == lv.H);
        if (f.Pvrtc ? (xoffset != 0 || yoffset != 0 || width != lv.W || height != lv.H) : !aligned) { Err(c, Gl.INVALID_OPERATION); return; }
        if (imageSize != CompressedSize(f, width, height)) { Err(c, Gl.INVALID_VALUE); return; }
        if (width == 0 || height == 0 || data == 0) return;
        PlaceTexels(t, face, level, xoffset, yoffset, width, height, ReadBytes(data, imageSize));
    }

    static uint? TexParamValue(uint pname, int v) => pname switch
    {
        Gl.TEXTURE_MIN_FILTER when (uint)v is Gl.NEAREST or Gl.LINEAR or Gl.NEAREST_MIPMAP_NEAREST or Gl.LINEAR_MIPMAP_NEAREST or Gl.NEAREST_MIPMAP_LINEAR or Gl.LINEAR_MIPMAP_LINEAR => (uint)v,
        Gl.TEXTURE_MAG_FILTER when (uint)v is Gl.NEAREST or Gl.LINEAR => (uint)v,
        Gl.TEXTURE_WRAP_S or Gl.TEXTURE_WRAP_T when (uint)v is Gl.REPEAT or Gl.CLAMP_TO_EDGE or Gl.MIRRORED_REPEAT => (uint)v,
        _ => null,
    };

    void TexParameter(uint target, uint pname, float fv, int iv, bool isFloat)
    {
        if (Cur is not { } c) return;
        if (target is not (Gl.TEXTURE_2D or Gl.TEXTURE_CUBE_MAP)) { Err(c, Gl.INVALID_ENUM); return; }
        var t = c.BoundTexture(target);
        if (pname == Gl.TEXTURE_MAX_ANISOTROPY_EXT)
        {
            float a = isFloat ? fv : iv;
            if (a < 1) { Err(c, Gl.INVALID_VALUE); return; }
            if (t != null) t.MaxAnisotropy = a;
            return;
        }
        if (pname is not (Gl.TEXTURE_MIN_FILTER or Gl.TEXTURE_MAG_FILTER or Gl.TEXTURE_WRAP_S or Gl.TEXTURE_WRAP_T)) { Err(c, Gl.INVALID_ENUM); return; }
        int value = isFloat ? (int)fv : iv;
        if (TexParamValue(pname, value) is not uint v) { Err(c, Gl.INVALID_ENUM); return; }
        if (t == null) return; // texture 0 of the unit: the default object's state is not kept
        switch (pname)
        {
            case Gl.TEXTURE_MIN_FILTER: t.MinFilter = v; break;
            case Gl.TEXTURE_MAG_FILTER: t.MagFilter = v; break;
            case Gl.TEXTURE_WRAP_S: t.WrapS = v; break;
            default: t.WrapT = v; break;
        }
    }

    [Hle] void glTexParameterf(uint target, uint pname, float param) => TexParameter(target, pname, param, 0, true);
    [Hle] void glTexParameteri(uint target, uint pname, int param) => TexParameter(target, pname, 0, param, false);
    [Hle] void glTexParameterfv(uint target, uint pname, uint pparams) { if (pparams != 0) TexParameter(target, pname, GuestMemory.Read<float>(pparams), 0, true); else if (Cur is { } c) Err(c, Gl.INVALID_VALUE); }
    [Hle] void glTexParameteriv(uint target, uint pname, uint pparams) { if (pparams != 0) TexParameter(target, pname, 0, (int)GuestMemory.R32(pparams), false); else if (Cur is { } c) Err(c, Gl.INVALID_VALUE); }

    float? GetTexParameter(uint target, uint pname)
    {
        if (Cur is not { } c) return null;
        if (target is not (Gl.TEXTURE_2D or Gl.TEXTURE_CUBE_MAP)) { Err(c, Gl.INVALID_ENUM); return null; }
        var t = c.BoundTexture(target);
        float? v = pname switch
        {
            Gl.TEXTURE_MIN_FILTER => t?.MinFilter ?? Gl.NEAREST_MIPMAP_LINEAR,
            Gl.TEXTURE_MAG_FILTER => t?.MagFilter ?? Gl.LINEAR,
            Gl.TEXTURE_WRAP_S => t?.WrapS ?? Gl.REPEAT,
            Gl.TEXTURE_WRAP_T => t?.WrapT ?? Gl.REPEAT,
            Gl.TEXTURE_MAX_ANISOTROPY_EXT => t?.MaxAnisotropy ?? 1,
            Gl.TEXTURE_IMMUTABLE_FORMAT_EXT => t?.Immutable == true ? 1 : 0,
            _ => null,
        };
        if (v == null) Err(c, Gl.INVALID_ENUM);
        return v;
    }

    [Hle] void glGetTexParameterfv(uint target, uint pname, uint pparams) { if (GetTexParameter(target, pname) is float v && pparams != 0) GuestMemory.Write(pparams, v); }
    [Hle] void glGetTexParameteriv(uint target, uint pname, uint pparams) { if (GetTexParameter(target, pname) is float v && pparams != 0) GuestMemory.W32(pparams, (uint)(int)v); }

    [Hle]
    void glPixelStorei(uint pname, int param)
    {
        if (Cur is not { } c) return;
        if (pname is not (Gl.UNPACK_ALIGNMENT or Gl.PACK_ALIGNMENT)) { Err(c, Gl.INVALID_ENUM); return; }
        if (param is not (1 or 2 or 4 or 8)) { Err(c, Gl.INVALID_VALUE); return; }
        if (pname == Gl.UNPACK_ALIGNMENT) c.UnpackAlignment = param; else c.PackAlignment = param;
    }

    /// <summary>EXT_texture_storage: every level defined at once, the format fixed.</summary>
    void TexStorage(PgContext c, PgTexture? t, uint target, int levels, uint internalFormat, int width, int height)
    {
        if (target is not (Gl.TEXTURE_2D or Gl.TEXTURE_CUBE_MAP)) { Err(c, Gl.INVALID_ENUM); return; }
        var f = internalFormat switch
        {
            Gl.RGBA8_OES => Uncompressed[0], Gl.RGB8_OES => Uncompressed[1], Gl.RGBA4 => Uncompressed[2], Gl.RGB5_A1 => Uncompressed[3],
            Gl.RGB565 => Uncompressed[4], Gl.LUMINANCE8_EXT => Uncompressed[5], Gl.ALPHA8_EXT => Uncompressed[6],
            Gl.LUMINANCE8_ALPHA8_EXT => Uncompressed[7], Gl.BGRA8_EXT => Uncompressed[8],
            _ => Compressed.FirstOrDefault(x => x.GlInternal == internalFormat),
        };
        if (f == null) { Err(c, Gl.INVALID_ENUM); return; }
        if (width < 1 || height < 1 || levels < 1 || width > MaxTextureSize || height > MaxTextureSize) { Err(c, Gl.INVALID_VALUE); return; }
        if (target == Gl.TEXTURE_CUBE_MAP && width != height) { Err(c, Gl.INVALID_VALUE); return; }
        if (levels > FullChain((uint)width, (uint)height)) { Err(c, Gl.INVALID_OPERATION); return; }
        if (t == null || t.Immutable) { Err(c, Gl.INVALID_OPERATION); return; }
        if (t.Target != target) { Err(c, Gl.INVALID_OPERATION); return; }
        t.Data = 0; // a fresh store
        if (!EnsureStorage(c, t, f, (uint)width, (uint)height, levels)) return;
        for (int fc = 0; fc < t.Faces; fc++) for (int m = 0; m < levels; m++) t.Levels[fc, m].Defined = true;
        t.Immutable = true;
    }

    [Hle]
    void glTexStorage2DEXT(uint target, int levels, uint internalFormat, int width, int height)
    {
        if (Cur is not { } c) return;
        TexStorage(c, target is Gl.TEXTURE_2D or Gl.TEXTURE_CUBE_MAP ? c.BoundTexture(target) : null, target, levels, internalFormat, width, height);
    }

    [Hle]
    void glTextureStorage2DEXT(uint texture, uint target, int levels, uint internalFormat, int width, int height)
    {
        if (Cur is not { } c) return;
        if (!c.Shared.Textures.TryGetValue(texture, out var t)) { Err(c, Gl.INVALID_OPERATION); return; }
        if (t.Target == 0) t.Target = target;
        TexStorage(c, t, target, levels, internalFormat, width, height);
    }

    /// <summary>glGenerateMipmap: each level box-filtered from the one above, on the GPU queue (level 0 may be a render
    /// target's content).</summary>
    [Hle]
    void glGenerateMipmap(uint target)
    {
        if (Cur is not { } c) return;
        if (target is not (Gl.TEXTURE_2D or Gl.TEXTURE_CUBE_MAP)) { Err(c, Gl.INVALID_ENUM); return; }
        var t = c.BoundTexture(target);
        if (t == null || !t.HasStorage || t.Format!.Compressed) { Err(c, Gl.INVALID_OPERATION); return; }
        for (int fc = 0; fc < t.Faces; fc++) if (!t.Levels[fc, 0].Defined) { Err(c, Gl.INVALID_OPERATION); return; }
        if (target == Gl.TEXTURE_CUBE_MAP && t.Width != t.Height) { Err(c, Gl.INVALID_OPERATION); return; }
        if (_sceneTarget?.Fbo?.Color.Texture == t) EndScene();
        var f = t.Format;
        int bpp = f.Bpp / 8;
        var levels = t.Levels;
        uint data = t.Data, faceStride = t.FaceStride, type = t.GxmType;
        int count = t.LevelCount, faces = t.Faces;
        uint Addr(int face, int m, uint x, uint y)
        {
            var lv = levels[face, m];
            uint b = data + (uint)face * faceStride + lv.Offset;
            if (type == TexLinear) return b + y * lv.RowPitch + x * (uint)bpp;
            return b + TextureLayout.Morton(x, y, BitOperations.RoundUpToPowerOf2(lv.W), BitOperations.RoundUpToPowerOf2(lv.H)) * (uint)bpp;
        }
        t.GpuSeq = Emu.Gpu.Submit(new Gpu.CallbackCommand
        {
            ReadAddress = data, ReadSize = t.DataSize, WriteAddress = data, WriteSize = t.DataSize,
            Action = () =>
            {
                Span<float> acc = stackalloc float[4], px = stackalloc float[4];
                for (int face = 0; face < faces; face++)
                    for (int m = 1; m < count; m++)
                    {
                        var lv = levels[face, m];
                        var up = levels[face, m - 1];
                        for (uint y = 0; y < lv.H; y++)
                            for (uint x = 0; x < lv.W; x++)
                            {
                                acc.Clear();
                                int n = 0;
                                for (uint dy = 0; dy < 2; dy++)
                                    for (uint dx = 0; dx < 2; dx++)
                                    {
                                        uint sx = Math.Min(x * 2 + dx, up.W - 1), sy = Math.Min(y * 2 + dy, up.H - 1);
                                        DecodeTexel(f, Addr(face, m - 1, sx, sy), px);
                                        for (int k = 0; k < 4; k++) acc[k] += px[k];
                                        n++;
                                    }
                                for (int k = 0; k < 4; k++) acc[k] /= n;
                                EncodeTexel(f, Addr(face, m, x, y), acc);
                            }
                    }
            },
        });
        for (int fc = 0; fc < t.Faces; fc++) for (int m = 0; m < t.LevelCount; m++) t.Levels[fc, m].Defined = true;
    }

    /// <summary>A stored texel as four floats in the stored channel order (the mip filter averages channels as stored).</summary>
    static void DecodeTexel(PgTexFormat f, uint a, Span<float> o)
    {
        switch (f.Bpp)
        {
            case 8: o[0] = GuestMemory.Read<byte>(a); o[1] = o[2] = o[3] = 0; break;
            case 16 when f.GxmFormat == 0x07002000: o[0] = GuestMemory.Read<byte>(a); o[1] = GuestMemory.Read<byte>(a + 1); o[2] = o[3] = 0; break;
            case 16 when f.GxmFormat is 0x0B005000 or 0x0B006000: o[0] = (float)BitConverter.UInt16BitsToHalf(GuestMemory.Read<ushort>(a)); o[1] = o[2] = o[3] = 0; break;
            case 16:
            {
                uint v = GuestMemory.Read<ushort>(a);
                (int r, int g, int b, int al) = f.GxmFormat switch { 0x02002000 => (4, 4, 4, 4), 0x04002000 => (5, 5, 5, 1), _ => (5, 6, 5, 0) };
                int sh = 16;
                sh -= r; o[0] = (v >> sh) & ((1u << r) - 1);
                sh -= g; o[1] = (v >> sh) & ((1u << g) - 1);
                sh -= b; o[2] = (v >> sh) & ((1u << b) - 1);
                o[3] = al == 0 ? 0 : v & ((1u << al) - 1);
                break;
            }
            case 32 when f.GxmFormat == 0x11002000:
                o[0] = (float)BitConverter.UInt16BitsToHalf(GuestMemory.Read<ushort>(a)); o[1] = (float)BitConverter.UInt16BitsToHalf(GuestMemory.Read<ushort>(a + 2)); o[2] = o[3] = 0; break;
            case 32:
                for (int k = 0; k < 4; k++) o[k] = GuestMemory.Read<byte>(a + (uint)k);
                break;
            default: // 64: four halves
                for (int k = 0; k < 4; k++) o[k] = (float)BitConverter.UInt16BitsToHalf(GuestMemory.Read<ushort>(a + (uint)k * 2));
                break;
        }
    }

    static void EncodeTexel(PgTexFormat f, uint a, ReadOnlySpan<float> v)
    {
        static uint R(float x, int bits) => (uint)Math.Clamp((int)MathF.Round(x), 0, (1 << bits) - 1);
        switch (f.Bpp)
        {
            case 8: GuestMemory.Write(a, (byte)R(v[0], 8)); break;
            case 16 when f.GxmFormat == 0x07002000: GuestMemory.Write(a, (byte)R(v[0], 8)); GuestMemory.Write(a + 1, (byte)R(v[1], 8)); break;
            case 16 when f.GxmFormat is 0x0B005000 or 0x0B006000: GuestMemory.Write(a, BitConverter.HalfToUInt16Bits((Half)v[0])); break;
            case 16:
            {
                (int r, int g, int b, int al) = f.GxmFormat switch { 0x02002000 => (4, 4, 4, 4), 0x04002000 => (5, 5, 5, 1), _ => (5, 6, 5, 0) };
                uint o = R(v[0], r) << (16 - r) | R(v[1], g) << (16 - r - g) | R(v[2], b) << (16 - r - g - b) | (al == 0 ? 0 : R(v[3], al));
                GuestMemory.Write(a, (ushort)o);
                break;
            }
            case 32 when f.GxmFormat == 0x11002000:
                GuestMemory.Write(a, BitConverter.HalfToUInt16Bits((Half)v[0])); GuestMemory.Write(a + 2, BitConverter.HalfToUInt16Bits((Half)v[1])); break;
            case 32:
                for (int k = 0; k < 4; k++) GuestMemory.Write(a + (uint)k, (byte)R(v[k], 8));
                break;
            default:
                for (int k = 0; k < 4; k++) GuestMemory.Write(a + (uint)k * 2, BitConverter.HalfToUInt16Bits((Half)v[k]));
                break;
        }
    }

    /// <summary>glCopyTexImage2D / glCopyTexSubImage2D: framebuffer pixels into a texture level, GPU ordered.</summary>
    void CopyToTexture(PgContext c, PgTexture t, int face, int level, int dx, int dy, int x, int y, int w, int h)
    {
        if (w == 0 || h == 0) return;
        var rgba = ReadColor(c, x, y, w, h, out _);
        if (rgba == null) { Err(c, Gl.INVALID_FRAMEBUFFER_OPERATION); return; }
        var f = t.Format!;
        int bpp = f.Bpp / 8;
        var texels = new byte[w * h * bpp];
        Span<float> v = stackalloc float[4];
        uint tmp = _scratch + 0x300;
        for (int i = 0; i < w * h; i++)
        {
            // RGBA8 -> the texture's stored texel (through a scratch texel for the packed formats)
            byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2], a = rgba[i * 4 + 3];
            switch (f.GlInternal)
            {
                case Gl.LUMINANCE: v[0] = r; break;
                case Gl.ALPHA: v[0] = a; break;
                case Gl.LUMINANCE_ALPHA: v[0] = r; v[1] = a; break;
                case Gl.RGBA4: v[0] = r / 17f; v[1] = g / 17f; v[2] = b / 17f; v[3] = a / 17f; break;
                case Gl.RGB5_A1: v[0] = r * 31 / 255f; v[1] = g * 31 / 255f; v[2] = b * 31 / 255f; v[3] = a >= 128 ? 1 : 0; break;
                case Gl.RGB565: v[0] = r * 31 / 255f; v[1] = g * 63 / 255f; v[2] = b * 31 / 255f; break;
                case Gl.BGRA_EXT: v[0] = b; v[1] = g; v[2] = r; v[3] = a; break;
                default: v[0] = r; v[1] = g; v[2] = b; v[3] = f.GlInternal == Gl.RGB ? 255 : a; break;
            }
            EncodeTexel(f, tmp, v);
            GuestMemory.ReadSpan(tmp, bpp).CopyTo(texels.AsSpan(i * bpp));
        }
        PlaceTexels(t, face, level, dx, dy, w, h, texels);
    }

    [Hle]
    void glCopyTexImage2D(uint target, int level, uint internalFormat, int x, int y, int width, int height, int border)
    {
        if (Cur is not { } c) return;
        var f = internalFormat switch
        {
            Gl.RGBA => Uncompressed[0], Gl.RGB => Uncompressed[1], Gl.LUMINANCE => Uncompressed[5], Gl.ALPHA => Uncompressed[6],
            Gl.LUMINANCE_ALPHA => Uncompressed[7], _ => null,
        };
        if (f == null) { Err(c, Gl.INVALID_ENUM); return; }
        if (ImageTarget(c, target, out int face) is not { } t) return;
        if (!CheckLevelSize(c, target, level, width, height, border)) return;
        if (c.Framebuffer is { } fb && FboStatus(fb) != Gl.FRAMEBUFFER_COMPLETE) { Err(c, Gl.INVALID_FRAMEBUFFER_OPERATION); return; }
        if (!DefineLevel(c, t, face, level, f, width, height)) return;
        CopyToTexture(c, t, face, level, 0, 0, x, y, width, height);
    }

    [Hle]
    void glCopyTexSubImage2D(uint target, int level, int xoffset, int yoffset, int x, int y, int width, int height)
    {
        if (Cur is not { } c) return;
        if (ImageTarget(c, target, out int face) is not { } t) return;
        if (level < 0 || width < 0 || height < 0 || xoffset < 0 || yoffset < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (!t.HasStorage || level >= t.LevelCount || !t.Levels[face, level].Defined || t.Format!.Compressed) { Err(c, Gl.INVALID_OPERATION); return; }
        var lv = t.Levels[face, level];
        if (xoffset + width > lv.W || yoffset + height > lv.H) { Err(c, Gl.INVALID_VALUE); return; }
        if (c.Framebuffer is { } fb && FboStatus(fb) != Gl.FRAMEBUFFER_COMPLETE) { Err(c, Gl.INVALID_FRAMEBUFFER_OPERATION); return; }
        CopyToTexture(c, t, face, level, xoffset, yoffset, x, y, width, height);
    }

    // ------------------------------------------------------------------ sampling

    uint _blackTexture, _blackCube, _blackData;

    static bool UsesMips(uint minFilter) => minFilter is Gl.NEAREST_MIPMAP_NEAREST or Gl.LINEAR_MIPMAP_NEAREST or Gl.NEAREST_MIPMAP_LINEAR or Gl.LINEAR_MIPMAP_LINEAR;

    /// <summary>Levels the texture samples (0: incomplete, GLES 2.0 3.8.2).</summary>
    static int CompleteLevels(PgTexture t)
    {
        if (!t.HasStorage || t.Format == null) return 0;
        for (int fc = 0; fc < t.Faces; fc++) if (!t.Levels[fc, 0].Defined) return 0;
        if (t.Faces == 6 && t.Width != t.Height) return 0;
        if (!UsesMips(t.MinFilter)) return 1;
        for (int fc = 0; fc < t.Faces; fc++)
            for (int m = 0; m < t.LevelCount; m++)
                if (!t.Levels[fc, m].Defined) return 0;
        return t.LevelCount;
    }

    static uint GxmWrap(uint w) => w switch { Gl.MIRRORED_REPEAT => 1, Gl.CLAMP_TO_EDGE => 2, _ => 0 };

    /// <summary>A SceGxmTexture for <paramref name="t"/> (in the scratch area; sceGxmSet*Texture copies it), or the black
    /// texture of an incomplete one (sampling gives (0, 0, 0, 1)).</summary>
    uint TextureDescriptor(PgTexture? t, bool cube)
    {
        int levels = t == null ? 0 : CompleteLevels(t);
        if (t == null || levels == 0 || (t.Faces == 6) != cube)
        {
            if (_blackData == 0)
            {
                _blackData = Alloc(6 * 64, "black texture", MemoryRegion.User, 64);
                for (uint i = 0; i < 6 * 64; i += 4) GuestMemory.W32(_blackData + i, 0xFF000000);
                _blackTexture = Alloc(16, "black texture desc");
                _blackCube = Alloc(16, "black cube desc");
            }
            uint d = cube ? _blackCube : _blackTexture;
            G.TextureInit(d, _blackData, 0x0C000000, cube ? 1u : 1u, 1, 1, cube ? TexCube : TexLinear);
            return d;
        }
        uint desc = _scratch + 0x300;
        int mips = t.Faces == 6 ? t.LevelCount : levels;
        G.TextureInit(desc, t.Data, t.Format!.GxmFormat, t.Width, t.Height, (uint)mips, t.GxmType);
        uint min = t.MinFilter is Gl.LINEAR or Gl.LINEAR_MIPMAP_NEAREST or Gl.LINEAR_MIPMAP_LINEAR ? 1u : 0u;
        uint mag = t.MagFilter == Gl.LINEAR ? 1u : 0u;
        uint mip = t.MinFilter is Gl.NEAREST_MIPMAP_LINEAR or Gl.LINEAR_MIPMAP_LINEAR ? 0x200u : 0u;
        G.TextureSampling(desc, min, mag, mip, GxmWrap(t.WrapS), GxmWrap(t.WrapT), 31);
        return desc;
    }

    void BindPbufferToTexture(PgSurface s, PgTexture t)
    {
        var f = s.Config.Is565 ? Uncompressed[4] : s.TextureFormat == Egl.TEXTURE_RGB ? Uncompressed[1] : Uncompressed[0];
        FreeAfterGpu(t.Data, t.GpuSeq);
        t.Target = Gl.TEXTURE_2D;
        t.Format = f; t.Width = s.Width; t.Height = s.Height; t.GxmType = TexLinear; t.LevelCount = 1; t.Faces = 1;
        t.Levels = new PgLevel[1, 1];
        LayOut(t);
        t.Levels[0, 0].Defined = true;
        t.Data = s.ColorBuffer; // the pbuffer's rows are 8-texel aligned, as a linear texture's
        t.RenderTarget = true;
        t.Immutable = false;
        s.BoundTexture = t;
    }

    void ReleaseTexImage(PgSurface s, PgTexture t)
    {
        if (t.Data == s.ColorBuffer) { t.Data = 0; t.Format = null; t.Levels = new PgLevel[1, 1]; }
        s.BoundTexture = null;
    }

    // ------------------------------------------------------------------ renderbuffers

    static PgTexFormat? RenderbufferColorFormat(uint internalFormat) => internalFormat switch
    {
        Gl.RGBA4 => Uncompressed[2], Gl.RGB5_A1 => Uncompressed[3], Gl.RGB565 => Uncompressed[4],
        Gl.RGBA8_OES => Uncompressed[0], Gl.RGB8_OES => Uncompressed[1], Gl.BGRA8_EXT => Uncompressed[8],
        _ => null,
    };

    [Hle]
    void glGenRenderbuffers(int n, uint renderbuffers)
    {
        var c = Cur;
        if (!GenCheck(c, n, renderbuffers)) return;
        var sh = c!.Shared;
        GenNames(n, renderbuffers, sh.RenderbufferNames, ref sh.NextRenderbuffer, sh.Renderbuffers.ContainsKey);
    }

    [Hle]
    void glDeleteRenderbuffers(int n, uint renderbuffers)
    {
        var c = Cur;
        if (c == null) return;
        if (n < 0) { Err(c, Gl.INVALID_VALUE); return; }
        for (int i = 0; i < n && renderbuffers != 0; i++)
        {
            uint name = GuestMemory.R32(renderbuffers + (uint)i * 4);
            if (name == 0) continue;
            c.Shared.RenderbufferNames.Remove(name);
            if (!c.Shared.Renderbuffers.Remove(name, out var rb)) continue;
            rb.Deleted = true;
            if (c.Renderbuffer == rb) c.Renderbuffer = null;
            if (c.Framebuffer is { } f)
                foreach (var a in new[] { f.Color, f.Depth, f.Stencil }) if (a.Renderbuffer == rb) a.Clear();
            if (_sceneTarget?.Fbo is { } sf && (sf.Color.Renderbuffer == rb || sf.Depth.Renderbuffer == rb || sf.Stencil.Renderbuffer == rb)) EndScene();
            FreeAfterGpu(rb.Addr, Emu.Gpu.Submitted);
            rb.Addr = 0;
        }
    }

    [Hle]
    void glBindRenderbuffer(uint target, uint renderbuffer)
    {
        if (Cur is not { } c) return;
        if (target != Gl.RENDERBUFFER) { Err(c, Gl.INVALID_ENUM); return; }
        PgRenderbuffer? rb = null;
        if (renderbuffer != 0 && !c.Shared.Renderbuffers.TryGetValue(renderbuffer, out rb))
        {
            rb = new PgRenderbuffer(renderbuffer) { Created = true };
            c.Shared.Renderbuffers[renderbuffer] = rb;
            c.Shared.RenderbufferNames.Add(renderbuffer);
        }
        c.Renderbuffer = rb;
    }

    [Hle] bool glIsRenderbuffer(uint renderbuffer) => Cur is { } c && renderbuffer != 0 && c.Shared.Renderbuffers.ContainsKey(renderbuffer);

    [Hle]
    void glRenderbufferStorage(uint target, uint internalFormat, int width, int height)
    {
        if (Cur is not { } c) return;
        if (target != Gl.RENDERBUFFER) { Err(c, Gl.INVALID_ENUM); return; }
        bool depthStencil = internalFormat is Gl.DEPTH_COMPONENT16 or Gl.DEPTH_COMPONENT24_OES or Gl.DEPTH24_STENCIL8_OES or Gl.STENCIL_INDEX8;
        var cf = RenderbufferColorFormat(internalFormat);
        if (cf == null && !depthStencil) { Err(c, Gl.INVALID_ENUM); return; }
        if (width < 0 || height < 0 || width > MaxRenderbufferSize || height > MaxRenderbufferSize) { Err(c, Gl.INVALID_VALUE); return; }
        if (c.Renderbuffer is not { } rb) { Err(c, Gl.INVALID_OPERATION); return; }
        if (_sceneTarget?.Fbo is { } sf && (sf.Color.Renderbuffer == rb || sf.Depth.Renderbuffer == rb || sf.Stencil.Renderbuffer == rb)) EndScene();
        FreeAfterGpu(rb.Addr, Emu.Gpu.Submitted);
        rb.InternalFormat = internalFormat;
        rb.Width = (uint)width; rb.Height = (uint)height;
        if (depthStencil)
        {
            rb.Stride = Align(rb.Width, 32);
            rb.Size = rb.Stride * Align(rb.Height, 32) * 4;
        }
        else
        {
            rb.Stride = Align(rb.Width, 8);
            rb.Size = rb.Stride * rb.Height * (uint)(cf!.Bpp / 8);
        }
        rb.Addr = width > 0 && height > 0 ? Alloc(rb.Size, "renderbuffer", MemoryRegion.User, 4096) : 0;
    }

    [Hle]
    void glGetRenderbufferParameteriv(uint target, uint pname, Ptr<int> param)
    {
        if (Cur is not { } c) return;
        if (target != Gl.RENDERBUFFER) { Err(c, Gl.INVALID_ENUM); return; }
        if (c.Renderbuffer is not { } rb) { Err(c, Gl.INVALID_OPERATION); return; }
        var cf = RenderbufferColorFormat(rb.InternalFormat);
        (int r, int g, int b, int a) = rb.Width == 0 ? (0, 0, 0, 0) : cf?.GlInternal switch
        {
            Gl.RGBA4 => (4, 4, 4, 4), Gl.RGB5_A1 => (5, 5, 5, 1), Gl.RGB565 => (5, 6, 5, 0), Gl.RGB => (8, 8, 8, 0), null => (0, 0, 0, 0), _ => (8, 8, 8, 8),
        };
        int? v = pname switch
        {
            Gl.RENDERBUFFER_WIDTH => (int)rb.Width,
            Gl.RENDERBUFFER_HEIGHT => (int)rb.Height,
            Gl.RENDERBUFFER_INTERNAL_FORMAT => (int)rb.InternalFormat,
            Gl.RENDERBUFFER_RED_SIZE => r, Gl.RENDERBUFFER_GREEN_SIZE => g, Gl.RENDERBUFFER_BLUE_SIZE => b, Gl.RENDERBUFFER_ALPHA_SIZE => a,
            Gl.RENDERBUFFER_DEPTH_SIZE => rb.Width == 0 ? 0 : rb.InternalFormat switch { Gl.DEPTH_COMPONENT16 => 16, Gl.DEPTH_COMPONENT24_OES or Gl.DEPTH24_STENCIL8_OES => 24, _ => 0 },
            Gl.RENDERBUFFER_STENCIL_SIZE => rb.Width != 0 && rb.IsStencil ? 8 : 0,
            _ => null,
        };
        if (v == null) { Err(c, Gl.INVALID_ENUM); return; }
        param.TrySet(v.Value);
    }

    // ------------------------------------------------------------------ framebuffers

    [Hle]
    void glGenFramebuffers(int n, uint framebuffers)
    {
        var c = Cur;
        if (!GenCheck(c, n, framebuffers)) return;
        GenNames(n, framebuffers, c!.FramebufferNames, ref c.NextFramebuffer, c.Framebuffers.ContainsKey);
    }

    [Hle]
    void glDeleteFramebuffers(int n, uint framebuffers)
    {
        var c = Cur;
        if (c == null) return;
        if (n < 0) { Err(c, Gl.INVALID_VALUE); return; }
        for (int i = 0; i < n && framebuffers != 0; i++)
        {
            uint name = GuestMemory.R32(framebuffers + (uint)i * 4);
            if (name == 0) continue;
            c.FramebufferNames.Remove(name);
            if (!c.Framebuffers.Remove(name, out var f)) continue;
            f.Deleted = true;
            if (_sceneTarget?.Fbo == f) EndScene();
            if (c.Framebuffer == f) c.Framebuffer = null;
            if (_fboSurfaces.Remove(f, out var surf)) { FreeAfterGpu(surf.Color, Emu.Gpu.Submitted); FreeAfterGpu(surf.Depth, Emu.Gpu.Submitted); }
        }
    }

    [Hle]
    void glBindFramebuffer(uint target, uint framebuffer)
    {
        if (Cur is not { } c) return;
        if (target != Gl.FRAMEBUFFER) { Err(c, Gl.INVALID_ENUM); return; }
        PgFramebuffer? f = null;
        if (framebuffer != 0 && !c.Framebuffers.TryGetValue(framebuffer, out f))
        {
            f = new PgFramebuffer(framebuffer) { Created = true };
            c.Framebuffers[framebuffer] = f;
            c.FramebufferNames.Add(framebuffer);
        }
        c.Framebuffer = f;
    }

    [Hle] bool glIsFramebuffer(uint framebuffer) => Cur is { } c && framebuffer != 0 && c.Framebuffers.ContainsKey(framebuffer);

    PgAttachment? AttachmentPoint(PgFramebuffer f, uint attachment) => attachment switch
    {
        Gl.COLOR_ATTACHMENT0 => f.Color, Gl.DEPTH_ATTACHMENT => f.Depth, Gl.STENCIL_ATTACHMENT => f.Stencil, _ => null,
    };

    [Hle]
    void glFramebufferTexture2D(uint target, uint attachment, uint textarget, uint texture, int level)
    {
        if (Cur is not { } c) return;
        if (target != Gl.FRAMEBUFFER) { Err(c, Gl.INVALID_ENUM); return; }
        if (c.Framebuffer is not { } f) { Err(c, Gl.INVALID_OPERATION); return; }
        if (AttachmentPoint(f, attachment) is not { } a) { Err(c, Gl.INVALID_ENUM); return; }
        if (_sceneTarget?.Fbo == f) EndScene();
        if (texture == 0) { a.Clear(); return; }
        bool cubeFace = textarget >= Gl.TEXTURE_CUBE_MAP_POSITIVE_X && textarget <= Gl.TEXTURE_CUBE_MAP_NEGATIVE_Z;
        if (textarget != Gl.TEXTURE_2D && !cubeFace) { Err(c, Gl.INVALID_ENUM); return; }
        if (!c.Shared.Textures.TryGetValue(texture, out var t)) { Err(c, Gl.INVALID_OPERATION); return; }
        if (t.Target != 0 && (t.Target == Gl.TEXTURE_CUBE_MAP) != cubeFace) { Err(c, Gl.INVALID_OPERATION); return; }
        if (level < 0 || level > BitOperations.Log2(MaxTextureSize)) { Err(c, Gl.INVALID_VALUE); return; }
        a.Clear();
        a.Type = PgAttachment.TEXTURE;
        a.Texture = t;
        a.Level = (uint)level;
        a.Face = cubeFace ? textarget - Gl.TEXTURE_CUBE_MAP_POSITIVE_X : 0;
        t.RenderTarget = true;
    }

    [Hle]
    void glFramebufferRenderbuffer(uint target, uint attachment, uint renderbuffertarget, uint renderbuffer)
    {
        if (Cur is not { } c) return;
        if (target != Gl.FRAMEBUFFER) { Err(c, Gl.INVALID_ENUM); return; }
        if (c.Framebuffer is not { } f) { Err(c, Gl.INVALID_OPERATION); return; }
        if (AttachmentPoint(f, attachment) is not { } a) { Err(c, Gl.INVALID_ENUM); return; }
        if (renderbuffer != 0 && renderbuffertarget != Gl.RENDERBUFFER) { Err(c, Gl.INVALID_ENUM); return; }
        if (_sceneTarget?.Fbo == f) EndScene();
        if (renderbuffer == 0) { a.Clear(); return; }
        if (!c.Shared.Renderbuffers.TryGetValue(renderbuffer, out var rb)) { Err(c, Gl.INVALID_OPERATION); return; }
        a.Clear();
        a.Type = Gl.RENDERBUFFER;
        a.Renderbuffer = rb;
    }

    /// <summary>GLES 2.0 4.4.5 framebuffer completeness, plus what GXM cannot render: separate depth and stencil
    /// renderbuffers (one depth/stencil surface) and swizzled (cube map) or non-renderable colour textures.</summary>
    uint FboStatus(PgFramebuffer f)
    {
        var atts = new[] { f.Color, f.Depth, f.Stencil }.Where(a => a.Type != 0).ToList();
        if (atts.Count == 0) return Gl.FRAMEBUFFER_INCOMPLETE_MISSING_ATTACHMENT;
        var sizes = new List<(uint, uint)>();
        foreach (var a in atts)
        {
            if (a.Texture is { } t)
            {
                if (t.Deleted || !t.HasStorage || a.Level >= t.LevelCount || !t.Levels[a.Face, a.Level].Defined) return Gl.FRAMEBUFFER_INCOMPLETE_ATTACHMENT;
                if (a != f.Color || !t.Format!.Renderable) return Gl.FRAMEBUFFER_INCOMPLETE_ATTACHMENT;
                if (t.GxmType != TexLinear) return Gl.FRAMEBUFFER_UNSUPPORTED;
                var lv = t.Levels[a.Face, a.Level];
                sizes.Add((lv.W, lv.H));
            }
            else if (a.Renderbuffer is { } rb)
            {
                if (rb.Deleted || rb.Width == 0 || rb.Height == 0) return Gl.FRAMEBUFFER_INCOMPLETE_ATTACHMENT;
                bool ok = a == f.Color ? rb.IsColor : a == f.Depth ? rb.IsDepth : rb.IsStencil;
                if (!ok) return Gl.FRAMEBUFFER_INCOMPLETE_ATTACHMENT;
                sizes.Add((rb.Width, rb.Height));
            }
        }
        if (sizes.Distinct().Count() > 1) return Gl.FRAMEBUFFER_INCOMPLETE_DIMENSIONS;
        if (f.Depth.Renderbuffer is { } d && f.Stencil.Renderbuffer is { } s && d != s) return Gl.FRAMEBUFFER_UNSUPPORTED;
        if (f.Color.Type == 0) return Gl.FRAMEBUFFER_UNSUPPORTED; // GXM scenes render a colour surface
        return Gl.FRAMEBUFFER_COMPLETE;
    }

    [Hle]
    uint glCheckFramebufferStatus(uint target)
    {
        if (Cur is not { } c) return 0;
        if (target != Gl.FRAMEBUFFER) { Err(c, Gl.INVALID_ENUM); return 0; }
        return c.Framebuffer is { } f ? FboStatus(f) : Gl.FRAMEBUFFER_COMPLETE;
    }

    [Hle]
    void glGetFramebufferAttachmentParameteriv(uint target, uint attachment, uint pname, Ptr<int> param)
    {
        if (Cur is not { } c) return;
        if (target != Gl.FRAMEBUFFER) { Err(c, Gl.INVALID_ENUM); return; }
        if (c.Framebuffer is not { } f) { Err(c, Gl.INVALID_OPERATION); return; }
        if (AttachmentPoint(f, attachment) is not { } a) { Err(c, Gl.INVALID_ENUM); return; }
        int? v = pname switch
        {
            Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE => (int)a.Type,
            Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME when a.Type != 0 => (int)a.Name,
            Gl.FRAMEBUFFER_ATTACHMENT_TEXTURE_LEVEL when a.Type == PgAttachment.TEXTURE => (int)a.Level,
            Gl.FRAMEBUFFER_ATTACHMENT_TEXTURE_CUBE_MAP_FACE when a.Type == PgAttachment.TEXTURE =>
                a.Texture!.Target == Gl.TEXTURE_CUBE_MAP ? (int)(Gl.TEXTURE_CUBE_MAP_POSITIVE_X + a.Face) : 0,
            _ => null,
        };
        if (v == null) { Err(c, Gl.INVALID_ENUM); return; }
        param.TrySet(v.Value);
    }

    // ------------------------------------------------------------------ glReadPixels

    [Hle]
    void glReadPixels(int x, int y, int width, int height, uint format, uint type, uint pixels)
    {
        if (Cur is not { } c) return;
        bool rgbaUb = format == Gl.RGBA && type == Gl.UNSIGNED_BYTE;
        bool impl = format == Gl.RGB && type == Gl.UNSIGNED_SHORT_5_6_5;
        if (!rgbaUb && !impl)
        {
            bool fmtOk = format is Gl.RGBA or Gl.RGB or Gl.ALPHA or Gl.LUMINANCE or Gl.LUMINANCE_ALPHA;
            bool typeOk = type is Gl.UNSIGNED_BYTE or Gl.UNSIGNED_SHORT_5_6_5 or Gl.UNSIGNED_SHORT_4_4_4_4 or Gl.UNSIGNED_SHORT_5_5_5_1;
            Err(c, fmtOk && typeOk ? Gl.INVALID_OPERATION : Gl.INVALID_ENUM);
            return;
        }
        if (width < 0 || height < 0) { Err(c, Gl.INVALID_VALUE); return; }
        if (c.Framebuffer is { } fb && FboStatus(fb) != Gl.FRAMEBUFFER_COMPLETE) { Err(c, Gl.INVALID_FRAMEBUFFER_OPERATION); return; }
        if (width == 0 || height == 0 || pixels == 0) return;
        var rgba = ReadColor(c, x, y, width, height, out _);
        if (rgba == null) return;
        int bpp = rgbaUb ? 4 : 2, row = width * bpp, pitch = (int)Align((uint)row, (uint)c.PackAlignment);
        for (int r = 0; r < height; r++)
        {
            uint dst = pixels + (uint)(r * pitch);
            if (rgbaUb) WriteBytes(dst, rgba.AsSpan(r * width * 4, width * 4));
            else
                for (int i = 0; i < width; i++)
                {
                    int o = (r * width + i) * 4;
                    ushort v = (ushort)((rgba[o] >> 3) << 11 | (rgba[o + 1] >> 2) << 5 | rgba[o + 2] >> 3);
                    GuestMemory.Write(dst + (uint)i * 2, v);
                }
        }
    }
}
