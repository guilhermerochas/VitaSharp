using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Every GXM texture format in every memory layout (linear, swizzled, tiled, linear strided, cube) sampled by a draw on
/// the software Vulkan device, then the same frames recorded as a GPU dump and replayed on a fresh renderer
/// (<see cref="GpuDumpPlayer"/>, what <c>replay-gpu --compare</c> runs): the replay presents the same pixels.
/// </summary>
[Collection("guest")]
public sealed class RendererTextureTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;

    private static readonly TexBase[] s_formats = Enum.GetValues<TexBase>();

    /// <summary>The GXM side of the workload, on whatever backend the emulator's GPU queue has.</summary>
    internal sealed class Workload
    {
        private readonly HleHarness _h;
        private readonly Gxm _g;
        public uint Ctx, Rt, Color, Data, Vp, Fp, FpCube, Verts, Idx;
        /// <summary>The cube textures among <see cref="Textures"/>: drawn with <see cref="FpCube"/>, whose unit 3 is a samplerCube.</summary>
        public readonly HashSet<uint> CubeTextures = [];
        public readonly List<uint> Textures = [];
        /// <summary>The format of each of <see cref="Textures"/>.</summary>
        public readonly List<TexBase> TextureFormats = [];

        public Workload(HleHarness h, ushort multisample = 0, uint colorFormat = 0, uint scaleMode = 0)
        {
            _h = h;
            _g = h.Module<Gxm>();
            uint ip = h.Alloc(0x14), o = h.Word(0);
            GuestMemory.W32(ip + 4, 2);
            Assert.Equal(0, Call(_g, "sceGxmInitialize", ip));
            uint cp = h.Alloc(0x24);
            GuestMemory.W32(cp + 8, h.Alloc(0x1000));
            GuestMemory.W32(cp + 12, 0x1000);
            GuestMemory.W32(cp + 16, h.Alloc(0x10000));
            GuestMemory.W32(cp + 20, 0x10000);
            GuestMemory.W32(cp + 24, h.Alloc(0x10000));
            GuestMemory.W32(cp + 28, 0x10000);
            Assert.Equal(0, Call(_g, "sceGxmCreateContext", cp, new Ptr<uint>(o)));
            Ctx = GuestMemory.R32(o);
            uint rp = h.Alloc(0x14);
            GuestMemory.Write(rp + 4, (ushort)W);
            GuestMemory.Write(rp + 6, (ushort)H);
            GuestMemory.Write(rp + 8, (ushort)1);
            GuestMemory.Write(rp + 10, multisample);
            Assert.Equal(0, Call(_g, "sceGxmCreateRenderTarget", rp, new Ptr<uint>(o)));
            Rt = GuestMemory.R32(o);
            Data = h.Alloc(W * H * 8); // room for 64-bit colour formats
            Color = h.Alloc(0x40);
            Assert.Equal(0, Call(_g, "sceGxmColorSurfaceInit", Color, colorFormat, 0u, scaleMode, 0u, (uint)W, (uint)H, (uint)W, Data));

            uint pp = h.Alloc(0x40);
            Assert.Equal(0, Call(_g, "sceGxmShaderPatcherCreate", pp, new Ptr<uint>(o)));
            uint patcher = GuestMemory.R32(o), vid = h.Word(0), fid = h.Word(0), cid = h.Word(0);
            Call(_g, "sceGxmShaderPatcherRegisterProgram", patcher, VertexProgram(h), new Ptr<uint>(vid));
            Call(_g, "sceGxmShaderPatcherRegisterProgram", patcher, SamplingFragmentProgram(h), new Ptr<uint>(fid));
            Call(_g, "sceGxmShaderPatcherRegisterProgram", patcher, SamplingFragmentProgram(h, cube: true), new Ptr<uint>(cid));
            uint attr = h.Alloc(8), stream = h.Alloc(4), vp = h.Word(0), fp = h.Word(0), fc = h.Word(0);
            GuestMemory.Write(attr + 4, (byte)9); // F32
            GuestMemory.Write(attr + 5, (byte)4); // x, y, z, w
            GuestMemory.Write(stream, (ushort)16);
            Assert.Equal(0, Call(_g, "sceGxmShaderPatcherCreateVertexProgram", patcher, GuestMemory.R32(vid), attr, 1u, stream, 1u, new Ptr<uint>(vp)));
            Assert.Equal(0, Call(_g, "sceGxmShaderPatcherCreateFragmentProgram", patcher, GuestMemory.R32(fid), 0u, 0u, 0u, GuestMemory.R32(vp), new Ptr<uint>(fp)));
            Assert.Equal(0, Call(_g, "sceGxmShaderPatcherCreateFragmentProgram", patcher, GuestMemory.R32(cid), 0u, 0u, 0u, GuestMemory.R32(vp), new Ptr<uint>(fc)));
            Vp = GuestMemory.R32(vp);
            Fp = GuestMemory.R32(fp);
            FpCube = GuestMemory.R32(fc);
            Verts = h.Alloc(48);
            Idx = h.Alloc(8);
            float[] v = [-1, -1, 0.5f, 1, 3, -1, 0.5f, 1, -1, 3, 0.5f, 1]; // a triangle over the whole surface
            for (int i = 0; i < v.Length; i++)
            {
                GuestMemory.Write(Verts + (uint)i * 4, v[i]);
            }

            GuestMemory.Write(Idx + 2, (ushort)1);
            GuestMemory.Write(Idx + 4, (ushort)2);

            // one texture per format and layout over the same pseudo-random texels (and a palette)
            uint texels = h.Alloc(W * H * 16 * 6), palette = h.Alloc(256 * 4 + 64) + 63 & ~63u;
            var rng = new Random(7);
            for (uint i = 0; i < W * H * 16 * 6; i += 4)
            {
                GuestMemory.W32(texels + i, (uint)rng.Next());
            }

            for (uint i = 0; i < 256; i++)
            {
                GuestMemory.W32(palette + i * 4, 0xFF000000u | (i * 0x010203u));
            }

            foreach (TexBase f in s_formats)
            {
                foreach (string init in new[] { "sceGxmTextureInitLinear", "sceGxmTextureInitSwizzled", "sceGxmTextureInitTiled", "sceGxmTextureInitCube", "strided" })
                {
                    uint t = h.Alloc(16);
                    int r = init == "strided"
                        ? Call(_g, "sceGxmTextureInitLinearStrided", t, texels, (uint)f, (uint)W, (uint)H, (uint)W * 8)
                        : Call(_g, init, t, texels, (uint)f, (uint)W, (uint)H, 1u);
                    if (r != 0)
                    {
                        continue;
                    }

                    if (f is TexBase.P4 or TexBase.P8)
                    {
                        Call(_g, "sceGxmTextureSetPalette", t, palette);
                    }

                    Call(_g, "sceGxmTextureSetMinFilter", t, 1u);
                    Call(_g, "sceGxmTextureSetMagFilter", t, 1u);
                    Textures.Add(t);
                    TextureFormats.Add(f);
                    if (init == "sceGxmTextureInitCube")
                    {
                        CubeTextures.Add(t);
                    }
                }
            }
        }

        /// <summary>One scene per group of textures, each drawn once, and a present after each scene.</summary>
        public int Run(int group)
        {
            int frames = 0;
            for (int start = 0; start < Textures.Count; start += group)
            {
                Assert.Equal(0, Call(_g, "sceGxmBeginScene", Ctx, 0u, Rt, 0u, 0u, 0u, Color, 0u));
                Invoke(_g, "sceGxmSetVertexProgram", Ctx, Vp);
                Invoke(_g, "sceGxmSetFragmentProgram", Ctx, Fp);
                Invoke(_g, "sceGxmSetFrontDepthFunc", Ctx, 0x00E00000u); // ALWAYS
                Invoke(_g, "sceGxmSetCullMode", Ctx, 0u);
                Assert.Equal(0, Call(_g, "sceGxmSetVertexStream", Ctx, 0u, Verts));
                foreach (uint t in Textures.Skip(start).Take(group))
                {
                    Invoke(_g, "sceGxmSetFragmentProgram", Ctx, CubeTextures.Contains(t) ? FpCube : Fp);
                    Assert.Equal(0, Call(_g, "sceGxmSetFragmentTexture", Ctx, 3u, t));
                    Assert.Equal(0, Call(_g, "sceGxmDraw", Ctx, 0u, 0u, Idx, 3u));
                }
                Assert.Equal(0, Call(_g, "sceGxmEndScene", Ctx, 0u, 0u));
                _h.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
                frames++;
            }
            Assert.Equal(0, Call(_g, "sceGxmFinish", Ctx));
            return frames;
        }

        public void Terminate() => Call(_g, "sceGxmTerminate");
    }

    /// <summary>The vertex program's code: o0-o3 (position) and o4-o7 (texture coordinate 0) = pa0-pa3, the vertex.</summary>
    internal static readonly ulong[] s_vertexCode = [UsseEncoding.MovF32OutputFromPa(0, 0), UsseEncoding.MovF32OutputFromPa(4, 0)];

    /// <summary>The vertex program's GXP: position and texture coordinate 0 (four components) out, <see cref="s_vertexCode"/>.</summary>
    internal static byte[] VertexGxp()
    {
        byte[] d = new byte[0x100 + s_vertexCode.Length * 8];
        void W32(int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        W32(0, 0x00505847);
        W32(8, (uint)d.Length);
        W32(0x2C, 0x60 - 0x2C);     // varyings at 0x60
        W32(0x60 + 20, 7);          // vertex outputs 2: texture coordinate 0, four components
        // one parameter: the attribute "pos", F32x4 in pa0 (the patcher binds attribute register 0 to it)
        W32(0x24, 1);
        W32(0x28, 0x80 - 0x28);     // parameters at 0x80
        W32(0x80, 0x90 - 0x80);     // its name at 0x90
        d[0x84] = 0;                // attribute, F32
        d[0x85] = 4;                // four components, container 0
        W32(0x88, 1);               // array size
        W32(0x8C, 0);               // resource index: pa0
        "pos"u8.CopyTo(d.AsSpan(0x90));
        W32(0x3C, (uint)s_vertexCode.Length);
        W32(0x40, 0x100 - 0x40);    // primary program
        for (int i = 0; i < s_vertexCode.Length; i++)
        {
            W32(0x100 + i * 8, (uint)s_vertexCode[i]);
            W32(0x104 + i * 8, (uint)(s_vertexCode[i] >> 32));
        }
        return d;
    }

    /// <summary>A fragment program without code: its only input is a non-dependent texture read of texture coordinate 0
    /// through unit 3 (a fragment varying descriptor, as stripped programs have), stored as F32 in pa0-pa3, and its
    /// output is pa0-pa3 as F32x4: the draw shows the texture.</summary>
    internal static byte[] SamplingFragmentGxp(bool cube = false)
    {
        byte[] d = new byte[0x200];
        void W32(int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        W32(0, 0x00505847);
        W32(8, (uint)d.Length);
        W32(0x14, GxpProgram.FlagFragment);
        W32(0x2C, 0x100 - 0x2C);    // varyings at 0x100
        d[0x100 + 10] = (byte)ParamType.F32;
        d[0x100 + 11] = 4;          // output: F32x4 from pa0
        d[0x100 + 12] = 1;          // one varying descriptor
        W32(0x110, 0x20);           // descriptors at 0x130
        W32(0x130, 0xF000);         // no attribute input, texture coordinate 0
        W32(0x134, 3);              // texture unit 3
        W32(0x138, 3 << 6);         // four registers
        W32(0x13C, 3 << 4);         // stored as F32
        if (cube)
        {
            // one parameter: a samplerCube on unit 3 (a cube texture is read through a cube sampler)
            W32(0x24, 1);
            W32(0x28, 0x60 - 0x28); // parameters at 0x60
            W32(0x60, 0x70 - 0x60); // its name at 0x70
            d[0x64] = 2;            // sampler
            d[0x67] = 1 << 4;       // cube
            W32(0x6C, 3);           // unit 3
            "env"u8.CopyTo(d.AsSpan(0x70));
        }
        return d;
    }

    private static uint Copy(HleHarness h, byte[] gxp)
    {
        uint p = h.Alloc((uint)gxp.Length);
        gxp.CopyTo(GuestMemory.Span(p, gxp.Length));
        return p;
    }

    internal static uint VertexProgram(HleHarness h) => Copy(h, VertexGxp());

    internal static uint SamplingFragmentProgram(HleHarness h, bool cube = false) => Copy(h, SamplingFragmentGxp(cube));

    private static VulkanRenderer NewRenderer(string frames) => new(new VulkanRendererOptions
    {
        VSync = false,
        AsyncShaders = false,
        AsyncPipelines = false,
        DumpFramesDir = frames,
        DumpEvery = 1,
    });

    [SoftwareVulkanFact]
    public void EveryTextureFormatAndLayoutSamplesAndTheDumpReplaysTheSame()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vktex");
        string live = Path.Combine(h.Root, "live"), replay = Path.Combine(h.Root, "replay"), dump = Path.Combine(h.Root, "tex.vsgd");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(replay);
        var w = new Workload(h);
        Assert.True(w.Textures.Count > s_formats.Length * 3, $"only {w.Textures.Count} textures were accepted");

        VulkanRenderer renderer = NewRenderer(live);
        int frames;
        try
        {
            h.Emu.Gpu.Backend = renderer;
            // a first frame before the recording: the recorder starts at the next present
            w.Run(w.Textures.Count);
            var done = new ManualResetEventSlim();
            var rec = GpuDumpRecorder.Start(new GpuDumpOptions { Path = dump, MaxFrames = 0, Header = new GpuDumpHeader { ["title-id"] = "PCSA00000" }, Finished = _ => done.Set() });
            Assert.NotNull(rec);
            // the recording starts at this present: every scene after it is in the dump
            h.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(w.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
            frames = w.Run(24);
            rec!.Stop();
            h.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(w.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
            h.Emu.Gpu.WaitIdle();
            Assert.True(done.Wait(30_000), "the dump was not finished");
            w.Terminate();
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }
        Assert.True(frames > 5);
        Assert.True(new FileInfo(dump).Length > 0);

        // the replay, on a fresh renderer and an empty guest memory: the same frames come out
        VulkanRenderer second = NewRenderer(replay);
        GpuDumpPlayStats stats;
        try
        {
            using var reader = new GpuDumpReader(dump);
            Assert.Equal("PCSA00000", reader.Header["title-id"]);
            stats = GpuDumpPlayer.Play(reader, second);
            second.Execute(new FinishCommand());
        }
        finally
        {
            second.Dispose();
        }
        Assert.True(stats.Draws >= w.Textures.Count, $"{stats.Draws} draws replayed of {w.Textures.Count}");
        Assert.True(stats.Frames >= frames);
        string[] liveFrames = Directory.GetFiles(live, "*.png").Order().ToArray();
        string[] replayFrames = Directory.GetFiles(replay, "*.png").Order().ToArray();
        Assert.NotEmpty(replayFrames);
        Assert.NotEmpty(liveFrames);
        // replay-gpu --compare: every replayed frame is one the game showed, pixel for pixel
        static string Hash(string f) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)));
        var shown = liveFrames.Select(Hash).ToHashSet();
        Assert.All(replayFrames, f => Assert.Contains(Hash(f), shown));

        // the front-end's replay-gpu on the dump: its statistics and a recompressed copy (a full replay needs a guest
        // memory of its own, one per process: GpuDumpPlayer above is the same replay in this process)
        Assert.Equal(0, VitaSharp.Desktop.ReplayGpu.Run(dump, ["--info"]));
        string small = Path.Combine(h.Root, "small.vsgd");
        Assert.Equal(0, VitaSharp.Desktop.ReplayGpu.Run(dump, [$"--recompress={small}", "--quality=1"]));
        using (var re = new GpuDumpReader(small))
        {
            int draws = 0;
            while (re.Next())
            {
                if (re.Kind == GpuDumpRecord.Draw)
                {
                    draws++;
                }
            }

            Assert.Equal(stats.Draws, draws); // the same commands, compressed again
        }
    }
}
