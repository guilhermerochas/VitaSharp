using System.Text;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Trace-frame texture inspection: every texture sampled during the traced frame is decoded on the CPU, written as
/// a PNG and summarised numerically (mean colour, neighbour noise, 4x4 block seams) in textures.txt, so wrong
/// decoders show up as numbers (noise near 0.33 = random data, seams well above 1 = broken block decoding)
/// instead of needing a human to look at every image.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    readonly HashSet<TexKey> _tracedTextures = new();
    StringBuilder? _traceTextureStats;
    long _traceTextureFrame = -1;

    bool TracingThisFrame => _traceFrame == _presentCount && _traceDir != null;
    /// <summary>Scene begins and mid-scene flushes are logged for the traced frame and the one after it (a frame's
    /// post-processing often runs after the previous frame was presented).</summary>
    bool TracingScenes => _traceDir != null && _traceFrame >= 0 && _presentCount >= _traceFrame && _presentCount <= _traceFrame + 1;

    void TraceTexture(TexKey key, GxmTextureDesc d, TexFormatInfo info, uint w, uint h)
    {
        if (_traceTextureFrame != _presentCount) { _traceTextureFrame = _presentCount; _tracedTextures.Clear(); _traceTextureStats = new StringBuilder(); }
        if (!_tracedTextures.Add(key)) return;
        string dir = Path.Combine(_traceDir!, "textures");
        Directory.CreateDirectory(dir);
        var sb = _traceTextureStats!;
        sb.Append($"tex 0x{d.DataAddress:X8} fmt 0x{d.Format:X8} ({GxmFormats.BaseOf(d.Format)}) type 0x{d.TextureType:X8} {w}x{h} mips {d.MipCount} lodMin {d.LodMin} gamma {d.GammaMode}");
        try
        {
            byte[]? rgba = DecodeForInspection(d, info, w, h, out uint iw, out uint ih);
            if (rgba == null) sb.AppendLine(" (not inspected)");
            else
            {
                sb.Append($" resident {iw}x{ih} ").Append(Inspect(rgba, (int)iw, (int)ih)).AppendLine();
                sb.Append(MipCheck(d, info, w, h, rgba, (int)iw, (int)ih));
                var opaque = (byte[])rgba.Clone();
                for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
                Png.Write(Path.Combine(dir, $"tex_{d.DataAddress:X8}_{d.Format:X8}_{w}x{h}.png"), opaque, (int)iw, (int)ih);
                // cube maps: the other five faces too (a wrong face stride shows garbage on faces 1-5 only)
                if (d.TextureType is GxmTextureDesc.TypeCube or GxmTextureDesc.TypeCubeArbitrary)
                    for (uint f = 1; f < 6; f++)
                    {
                        byte[]? face = DecodeForInspection(d, info, w, h, out uint fw, out uint fh, -1, f);
                        if (face == null) continue;
                        sb.Append($"    face {f}: ").Append(Inspect(face, (int)fw, (int)fh)).AppendLine();
                        for (int i = 3; i < face.Length; i += 4) face[i] = 255;
                        Png.Write(Path.Combine(dir, $"tex_{d.DataAddress:X8}_{d.Format:X8}_{w}x{h}_f{f}.png"), face, (int)fw, (int)fh);
                    }
            }
        }
        catch (Exception e) { sb.AppendLine($" decode failed: {e.Message}"); }
        File.WriteAllText(Path.Combine(dir, "textures.txt"), sb.ToString());
    }

    int _suspectTextures;

    /// <summary>
    /// Test-run inspection of a texture that was just uploaded: logs a warning (once per address/format) when its
    /// contents look like garbage, so broken decoders or unloaded memory show up in the report without screenshots.
    /// </summary>
    void InspectUploadedTexture(GxmTextureDesc d, TexFormatInfo info, uint w, uint h)
    {
        try
        {
            // pooled decode: every upload of a test run is inspected (video textures every frame)
            using var rgba = DecodeForInspectionPooled(d, info, w, h, out uint iw, out uint ih);
            if (rgba.Array == null || iw * ih < 256) return;
            string stats = Inspect(rgba.Span, (int)iw, (int)ih);
            if (!stats.Contains("SUSPECT")) return;
            _suspectTextures++;
            Log.WarnOnce($"texsuspect{d.DataAddress:X}{d.Format:X}", $"[gpu] TEXTURE SUSPECT 0x{d.DataAddress:X8} fmt 0x{d.Format:X8} ({GxmFormats.BaseOf(d.Format)}) type 0x{d.TextureType:X8} {w}x{h} lodMin {d.LodMin}: {stats}");
        }
        catch (Exception e) { Log.WarnOnce($"texinspect{d.Format:X}", $"[gpu] texture inspection failed for fmt 0x{d.Format:X8}: {e.Message}"); }
    }

    /// <summary>Mip 0 of face 0 as RGBA8 for formats the CPU decodes; BC formats are expanded here too.</summary>
    static byte[]? DecodeForInspection(GxmTextureDesc d, TexFormatInfo info, uint w, uint h, out uint iw, out uint ih, int level = -1, uint face = 0)
    {
        using var t = DecodeForInspectionPooled(d, info, w, h, out iw, out ih, level, face);
        return t.Array == null ? null : t.ToArray();
    }

    /// <summary><see cref="DecodeForInspection"/> with a pooled result (default when the format is not inspected).</summary>
    static Texels DecodeForInspectionPooled(GxmTextureDesc d, TexFormatInfo info, uint w, uint h, out uint iw, out uint ih, int level = -1, uint face = 0)
    {
        // inspect the first resident level (LOD min), the one the GPU samples at full detail (or a given level)
        uint mips = Math.Min(d.MipCount, (uint)System.Numerics.BitOperations.Log2(Math.Max(w, h)) + 1);
        uint lodMin = level >= 0 ? (uint)Math.Min(level, (int)mips - 1) : Math.Min(d.LodMin, mips - 1);
        // the face stride depends on the whole chain, so lay out every level when a later cube face is wanted
        var layout = ComputeLayout(d, info, w, h, face > 0 ? mips : lodMin + 1, face + 1);
        var lv = layout.Levels[(int)lodMin];
        w = lv.Width; h = lv.Height;
        var texels = TextureDecoder.Decode(d, info, d.DataAddress + face * layout.FaceStride + lv.Offset, lv.Width, lv.Height, lv.RowPitch, out _, out _);
        iw = w; ih = h;
        if (!(info.Compressed && info.Convert == TexConvert.None) && info.Format is Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb) return texels;
        byte[] data;
        using (texels) data = texels.ToArray();
        if (info.Compressed && info.Convert == TexConvert.None)
        {
            return GxmFormats.BaseOf(d.Format) switch
            {
                TexBase.UBC1 => BcDecode.Bc1(data, w, h),
                TexBase.UBC2 => BcDecode.Bc2(data, w, h),
                TexBase.UBC3 => BcDecode.Bc3(data, w, h),
                _ => default,
            };
        }
        return Wrap(info.Format switch
        {
            Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => data,
            Format.R8Unorm => Expand(data, w * h, 1, (s, i) => (s[i], s[i], s[i], (byte)255)),
            Format.R8G8Unorm => Expand(data, w * h, 2, (s, i) => (s[i], s[i + 1], (byte)0, (byte)255)),
            Format.R16Sfloat => Expand(data, w * h, 2, (s, i) => { byte v = ToByte((float)BitConverter.ToHalf(s, i)); return (v, v, v, (byte)255); }),
            Format.R16G16B16A16Sfloat => Expand(data, w * h, 8, (s, i) => (ToByte((float)BitConverter.ToHalf(s, i)), ToByte((float)BitConverter.ToHalf(s, i + 2)), ToByte((float)BitConverter.ToHalf(s, i + 4)), ToByte((float)BitConverter.ToHalf(s, i + 6)))),
            Format.R32Sfloat => Expand(data, w * h, 4, (s, i) => { byte v = ToByte(BitConverter.ToSingle(s, i)); return (v, v, v, (byte)255); }),
            Format.R5G6B5UnormPack16 => Expand(data, w * h, 2, (s, i) => { int v = s[i] | s[i + 1] << 8; return ((byte)((v >> 11) * 255 / 31), (byte)(((v >> 5) & 63) * 255 / 63), (byte)((v & 31) * 255 / 31), (byte)255); }),
            Format.R4G4B4A4UnormPack16 => Expand(data, w * h, 2, (s, i) => { int v = s[i] | s[i + 1] << 8; return ((byte)((v >> 12) * 17), (byte)(((v >> 8) & 15) * 17), (byte)(((v >> 4) & 15) * 17), (byte)((v & 15) * 17)); }),
            Format.A1R5G5B5UnormPack16 => Expand(data, w * h, 2, (s, i) => { int v = s[i] | s[i + 1] << 8; return ((byte)(((v >> 10) & 31) * 255 / 31), (byte)(((v >> 5) & 31) * 255 / 31), (byte)((v & 31) * 255 / 31), (byte)((v >> 15) * 255)); }),
            Format.R8G8B8A8SNorm => Expand(data, w * h, 4, (s, i) => ((byte)(s[i] + 128), (byte)(s[i + 1] + 128), (byte)(s[i + 2] + 128), (byte)255)),
            _ => null,
        });

        static Texels Wrap(byte[]? a) => a == null ? default : Texels.Wrap(a);
    }

    /// <summary>
    /// Compares each smaller mip level with the resident level box-filtered to the same size: a correct mip chain matches
    /// closely (mean abs diff a few %); a wrong mip layout shows unrelated data. Distant surfaces sample these levels, so
    /// a broken chain looks like flat or wrong colours only far from the camera.
    /// </summary>
    static string MipCheck(GxmTextureDesc d, TexFormatInfo info, uint w, uint h, byte[] top, int tw, int th)
    {
        uint mips = Math.Min(d.MipCount, (uint)System.Numerics.BitOperations.Log2(Math.Max(w, h)) + 1);
        int baseLevel = (int)Math.Min(d.LodMin, mips - 1);
        if (mips - baseLevel < 2) return "";
        var sb = new StringBuilder("    mips:");
        bool bad = false;
        for (int lvl = baseLevel + 1; lvl < Math.Min((int)mips, baseLevel + 6); lvl++)
        {
            var m = DecodeForInspection(d, info, w, h, out uint mw, out uint mh, lvl);
            if (m == null || mw < 2 || mh < 2) break;
            int fx = tw / (int)mw, fy = th / (int)mh;
            if (fx < 1 || fy < 1) break;
            double diff = 0;
            for (int y = 0; y < mh; y++)
                for (int x = 0; x < mw; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int sum = 0;
                        for (int yy = 0; yy < fy; yy++) for (int xx = 0; xx < fx; xx++) sum += top[(((y * fy + yy) * tw) + x * fx + xx) * 4 + c];
                        diff += Math.Abs(sum / (fx * fy) - m[(y * mw + x) * 4 + c]);
                    }
            diff /= mw * mh * 3 * 255.0;
            sb.Append($" L{lvl} {mw}x{mh} diff {diff:F3}");
            if (diff > 0.12) bad = true;
        }
        return sb.Append(bad ? " SUSPECT-MIPS" : "").AppendLine().ToString();
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    static byte[] Expand(byte[] src, uint count, int bpp, Func<byte[], int, (byte, byte, byte, byte)> f)
    {
        var dst = new byte[count * 4];
        for (int i = 0; i < count && (i + 1) * bpp <= src.Length; i++)
        {
            var (r, g, b, a) = f(src, i * bpp);
            dst[i * 4] = r; dst[i * 4 + 1] = g; dst[i * 4 + 2] = b; dst[i * 4 + 3] = a;
        }
        return dst;
    }

    /// <summary>
    /// mean: average RGB. noise: mean absolute horizontal neighbour difference (0..1); photographs/lightmaps sit
    /// around 0.02-0.15, random data around 0.33. seams: neighbour difference across 4-pixel block boundaries divided
    /// by the difference inside blocks; a correctly decoded block format stays near 1, a broken one jumps well above.
    /// </summary>
    static string Inspect(ReadOnlySpan<byte> px, int w, int h)
    {
        double sum = 0, sumA = 0, diffIn = 0, diffEdge = 0; long nIn = 0, nEdge = 0;
        int n = w * h;
        for (int i = 0; i < n; i++) { sum += px[i * 4] + px[i * 4 + 1] + px[i * 4 + 2]; sumA += px[i * 4 + 3]; }
        for (int y = 0; y < h; y++)
            for (int x = 0; x + 1 < w; x++)
            {
                int a = (y * w + x) * 4, b = a + 4;
                int dd = Math.Abs(px[a] - px[b]) + Math.Abs(px[a + 1] - px[b + 1]) + Math.Abs(px[a + 2] - px[b + 2]);
                if ((x & 3) == 3) { diffEdge += dd; nEdge++; } else { diffIn += dd; nIn++; }
            }
        double noise = (diffIn + diffEdge) / Math.Max(1, nIn + nEdge) / 765.0;
        double seams = nEdge > 0 && diffIn > 0 ? (diffEdge / nEdge) / (diffIn / nIn) : 1;
        string flag = noise > 0.28 ? " SUSPECT-NOISE" : seams > 2.6 && noise > 0.015 ? " SUSPECT-SEAMS" : "";
        return $"mean {sum / (3.0 * n) / 255:F3} alpha {sumA / n / 255:F3} noise {noise:F3} seams {seams:F2}{flag}";
    }
}

/// <summary>CPU BC1/2/3 decoding for inspection only (the GPU samples the compressed data directly).</summary>
static class BcDecode
{
    public static Texels Bc1(byte[] src, uint w, uint h) => Decode(src, w, h, 8, false, false);
    public static Texels Bc2(byte[] src, uint w, uint h) => Decode(src, w, h, 16, true, false);
    public static Texels Bc3(byte[] src, uint w, uint h) => Decode(src, w, h, 16, false, true);

    /// <summary>Any BC base format to RGBA8 (signed BC4/BC5 are decoded as unsigned: a close approximation).</summary>
    public static Texels Decode(TexBase format, byte[] src, uint w, uint h) => format switch
    {
        TexBase.UBC1 => Bc1(src, w, h),
        TexBase.UBC2 => Bc2(src, w, h),
        TexBase.UBC3 => Bc3(src, w, h),
        TexBase.UBC4 or TexBase.SBC4 => Bc45(src, w, h, false),
        TexBase.UBC5 or TexBase.SBC5 => Bc45(src, w, h, true),
        _ => Texels.Wrap(src),
    };

    /// <summary>BC4 (one 8-byte alpha-style block = R) and BC5 (two blocks = R, G).</summary>
    static Texels Bc45(byte[] src, uint w, uint h, bool two)
    {
        uint bw = (w + 3) / 4, bh = (h + 3) / 4;
        int blockBytes = two ? 16 : 8;
        var dstT = Texels.Rent(w * h * 4L); // pooled: dispose once used
        var dst = dstT.Array;
        Span<byte> ch = stackalloc byte[16];
        for (uint by = 0; by < bh; by++)
            for (uint bx = 0; bx < bw; bx++)
            {
                int o = (int)((by * bw + bx) * blockBytes);
                if (o + blockBytes > src.Length) return dstT;
                for (int c = 0; c < (two ? 2 : 1); c++)
                {
                    DecodeAlphaBlock(src, o + c * 8, ch);
                    for (int i = 0; i < 16; i++)
                    {
                        uint x = bx * 4 + (uint)(i & 3), y = by * 4 + (uint)(i >> 2);
                        if (x >= w || y >= h) continue;
                        int p = (int)((y * w + x) * 4);
                        if (c == 0) { dst[p] = ch[i]; if (!two) { dst[p + 1] = ch[i]; dst[p + 2] = ch[i]; } }
                        else dst[p + 1] = ch[i];
                        dst[p + 3] = 255;
                    }
                }
            }
        return dstT;
    }

    static void DecodeAlphaBlock(byte[] src, int o, Span<byte> alpha)
    {
        int a0 = src[o], a1 = src[o + 1];
        ulong bits = 0; for (int i = 0; i < 6; i++) bits |= (ulong)src[o + 2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
        {
            int code = (int)((bits >> (3 * i)) & 7);
            alpha[i] = code switch
            {
                0 => (byte)a0,
                1 => (byte)a1,
                _ when a0 > a1 => (byte)(((8 - code) * a0 + (code - 1) * a1) / 7),
                6 => 0,
                7 => 255,
                _ => (byte)(((6 - code) * a0 + (code - 1) * a1) / 5),
            };
        }
    }

    static Texels Decode(byte[] src, uint w, uint h, int blockBytes, bool bc2, bool bc3)
    {
        uint bw = (w + 3) / 4, bh = (h + 3) / 4;
        var dstT = Texels.Rent(w * h * 4L); // pooled: dispose once used
        var dst = dstT.Array;
        Span<byte> c = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[16];
        for (uint by = 0; by < bh; by++)
            for (uint bx = 0; bx < bw; bx++)
            {
                int o = (int)((by * bw + bx) * blockBytes);
                if (o + blockBytes > src.Length) return dstT;
                int colorOff = o + (bc2 || bc3 ? 8 : 0);
                alpha.Fill(255);
                if (bc2)
                    for (int i = 0; i < 16; i++) alpha[i] = (byte)(((src[o + i / 2] >> ((i & 1) * 4)) & 15) * 17);
                else if (bc3)
                {
                    int a0 = src[o], a1 = src[o + 1];
                    ulong bits = 0; for (int i = 0; i < 6; i++) bits |= (ulong)src[o + 2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++)
                    {
                        int code = (int)((bits >> (3 * i)) & 7);
                        alpha[i] = code switch
                        {
                            0 => (byte)a0,
                            1 => (byte)a1,
                            _ when a0 > a1 => (byte)(((8 - code) * a0 + (code - 1) * a1) / 7),
                            6 => 0,
                            7 => 255,
                            _ => (byte)(((6 - code) * a0 + (code - 1) * a1) / 5),
                        };
                    }
                }
                int c0 = src[colorOff] | src[colorOff + 1] << 8, c1 = src[colorOff + 2] | src[colorOff + 3] << 8;
                Rgb565(c0, c, 0); Rgb565(c1, c, 4);
                bool fourColor = c0 > c1 || bc2 || bc3;
                for (int k = 0; k < 3; k++)
                {
                    c[8 + k] = fourColor ? (byte)((2 * c[k] + c[4 + k]) / 3) : (byte)((c[k] + c[4 + k]) / 2);
                    c[12 + k] = fourColor ? (byte)((c[k] + 2 * c[4 + k]) / 3) : (byte)0;
                }
                c[3] = c[7] = c[11] = 255; c[15] = fourColor ? (byte)255 : (byte)0;
                uint idx = (uint)(src[colorOff + 4] | src[colorOff + 5] << 8 | src[colorOff + 6] << 16 | src[colorOff + 7] << 24);
                for (int i = 0; i < 16; i++)
                {
                    uint x = bx * 4 + (uint)(i & 3), y = by * 4 + (uint)(i >> 2);
                    if (x >= w || y >= h) continue;
                    int ci = (int)((idx >> (2 * i)) & 3) * 4, p = (int)((y * w + x) * 4);
                    dst[p] = c[ci]; dst[p + 1] = c[ci + 1]; dst[p + 2] = c[ci + 2];
                    dst[p + 3] = bc2 || bc3 ? alpha[i] : c[ci + 3];
                }
            }
        return dstT;
    }

    static void Rgb565(int v, Span<byte> c, int o)
    {
        c[o] = (byte)(((v >> 11) & 31) * 255 / 31);
        c[o + 1] = (byte)(((v >> 5) & 63) * 255 / 63);
        c[o + 2] = (byte)((v & 31) * 255 / 31);
        c[o + 3] = 255;
    }
}

/// <summary>
/// Trace-frame snapshots of the current colour target taken just before chosen draws
/// (VITASHARP_TRACE_SNAP=id,id,... with the ids of the draw trace), written next to the target dumps as
/// snap_&lt;id&gt;_rt_&lt;addr&gt;.png plus an alpha image, to see what each pass of a post-processing chain produced.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>VITASHARP_TRACE_RT=ADDR,ADDR: log every draw into these colour surfaces in any frame (draw trace format).</summary>
    static readonly HashSet<uint> TraceRtAddrs = (Environment.GetEnvironmentVariable("VITASHARP_TRACE_RT") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => Convert.ToUInt32(s, 16)).ToHashSet();

    static readonly string[] TraceSnapSpec = (Environment.GetEnvironmentVariable("VITASHARP_TRACE_SNAP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
    static readonly HashSet<int> TraceSnapIds = TraceSnapSpec.Select(s => int.TryParse(s, out var v) ? v : -1).ToHashSet();
    /// <summary>"rt:ADDR" entries: snapshot before every draw into that colour surface.</summary>
    static readonly HashSet<uint> TraceSnapTargets = TraceSnapSpec.Where(s => s.StartsWith("rt:"))
        .Select(s => Convert.ToUInt32(s[3..], 16)).ToHashSet();

    readonly List<(int Id, RenderTarget Rt, Silk.NET.Vulkan.Buffer Buf, DeviceMemory Mem, nint Ptr)> _traceSnaps = new();
    readonly List<(int Id, uint W, uint H, Silk.NET.Vulkan.Buffer Buf, DeviceMemory Mem, nint Ptr)> _depthSnaps = new();

    /// <summary>"src:ADDR" entries: snapshot that surface whenever a draw samples it (id = draw id + 10000).</summary>
    static readonly HashSet<uint> TraceSnapSampled = TraceSnapSpec.Where(s => s.StartsWith("src:"))
        .Select(s => Convert.ToUInt32(s[4..], 16)).ToHashSet();

    void TraceSampledSnapshot(RenderTarget rt)
    {
        if (!TracingThisFrame || !TraceSnapSampled.Contains(rt.Address)) return;
        int bpp = HostBytesPerPixel(rt.Format);
        if (bpp != 4 && bpp != 8) return;
        var (buf, mem, ptr) = _core.CreateHostBuffer(rt.Width * rt.Height * (uint)bpp, BufferUsageFlags.TransferDstBit, MemCat.Readback);
        EndRendering();
        var region = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D(rt.Width, rt.Height, 1) };
        _vk.CmdCopyImageToBuffer(Cmd, rt.Image, ImageLayout.General, buf, 1, in region);
        _traceSnaps.Add((_drawSerial + 10000, rt, buf, mem, ptr));
    }

    /// <summary>Debug: VITASHARP_SNAP_FS=FSHASH:FROM:TO snapshots colour and depth before every draw of that fragment
    /// program in presented frames FROM..TO (files f{frame}_snap_*), e.g. to see what a mesh is drawn over.</summary>
    static readonly (ulong Hash, long From, long To)? SnapFs = Environment.GetEnvironmentVariable("VITASHARP_SNAP_FS")?.Split(':') is { Length: 3 } sf
        ? (Convert.ToUInt64(sf[0], 16), long.Parse(sf[1]), long.Parse(sf[2])) : null;

    void TraceSnapshot(DrawCommand d)
    {
        bool snapFs = SnapFs is { } sf && d.FragmentProgram.Info.Program.Hash == sf.Hash && _presentCount >= sf.From && _presentCount <= sf.To;
        if (snapFs && _rt is { } srt) { TakeSnapshot(srt); return; }
        // snapshots are taken in the logged frame (draw ids match the draw trace) and written when it is presented
        if (TraceSnapSpec.Length == 0 || _opt.ShaderCacheDir == null || !TracingThisFrame || _rt is not { } rt) return;
        if (!TraceSnapIds.Contains(_drawSerial) && !TraceSnapTargets.Contains(rt.Address)) return;
        TakeSnapshot(rt);
    }

    void TakeSnapshot(RenderTarget rt)
    {
        int bpp = HostBytesPerPixel(rt.Format);
        if (bpp != 4 && bpp != 8) return;
        var (buf, mem, ptr) = _core.CreateHostBuffer(rt.Width * rt.Height * (uint)bpp, BufferUsageFlags.TransferDstBit, MemCat.Readback);
        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        var region = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D(rt.Width, rt.Height, 1) };
        _vk.CmdCopyImageToBuffer(Cmd, rt.Image, ImageLayout.General, buf, 1, in region);
        _traceSnaps.Add((_drawSerial, rt, buf, mem, ptr));
        if (_depth != null && _core.DepthFormat == Format.D24UnormS8Uint)
        {
            var dt = _depth;
            var (dbuf, dmem, dptr) = _core.CreateHostBuffer(dt.Width * dt.Height * 4, BufferUsageFlags.TransferDstBit, MemCat.Readback);
            const ImageAspectFlags ds = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
            Barrier(dt.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.TransferSrcOptimal, ds, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.TransferBit, AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.TransferReadBit);
            var dregion = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1), ImageExtent = new Extent3D(dt.Width, dt.Height, 1) };
            _vk.CmdCopyImageToBuffer(Cmd, dt.Image, ImageLayout.TransferSrcOptimal, dbuf, 1, in dregion);
            Barrier(dt.Image, ImageLayout.TransferSrcOptimal, ImageLayout.DepthStencilAttachmentOptimal, ds, PipelineStageFlags.TransferBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, AccessFlags.TransferReadBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
            _depthSnaps.Add((_drawSerial, dt.Width, dt.Height, dbuf, dmem, dptr));
        }
    }

    /// <summary>Writes the snapshots (called after the traced frame completed on the GPU).</summary>
    void WriteTraceSnapshots(string dir)
    {
        Directory.CreateDirectory(dir);
        string prefix = SnapFs != null ? $"f{_presentCount}_" : "";
        foreach (var (id, rt, buf, mem, ptr) in _traceSnaps)
        {
            int n = (int)(rt.Width * rt.Height);
            var rgb = new byte[n * 4];
            var alpha = new byte[n * 4];
            bool half = HostBytesPerPixel(rt.Format) == 8;
            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < 4; c++)
                {
                    byte v = half ? (byte)Math.Clamp((int)((float)((Half*)ptr)[i * 4 + c] * 255f + 0.5f), 0, 255) : ((byte*)ptr)[i * 4 + c];
                    if (c < 3) rgb[i * 4 + c] = v; else { alpha[i * 4] = alpha[i * 4 + 1] = alpha[i * 4 + 2] = v; }
                }
                if (rt.Format == Format.B8G8R8A8Unorm) (rgb[i * 4], rgb[i * 4 + 2]) = (rgb[i * 4 + 2], rgb[i * 4]);
                rgb[i * 4 + 3] = alpha[i * 4 + 3] = 255;
            }
            Png.Write(Path.Combine(dir, $"{prefix}snap_{id:D4}_rt_{rt.Address:X8}.png"), rgb, (int)rt.Width, (int)rt.Height);
            if (SnapFs == null) Png.Write(Path.Combine(dir, $"snap_{id:D4}_rt_{rt.Address:X8}_alpha.png"), alpha, (int)rt.Width, (int)rt.Height);
            _core.DestroyBuffer(buf, mem);
        }
        _traceSnaps.Clear();
        foreach (var (id, w, h, buf, mem, ptr) in _depthSnaps)
        {
            int n = (int)(w * h);
            var img = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                uint v = ((uint*)ptr)[i] & 0xFFFFFF;
                img[i * 4] = (byte)(v >> 16); img[i * 4 + 1] = (byte)(v >> 8); img[i * 4 + 2] = (byte)v; img[i * 4 + 3] = 255;
            }
            Png.Write(Path.Combine(dir, $"{prefix}depth_{id:D4}.png"), img, (int)w, (int)h);
            _core.DestroyBuffer(buf, mem);
        }
        _depthSnaps.Clear();
    }
}
