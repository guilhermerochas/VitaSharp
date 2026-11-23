using System.Buffers.Binary;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The second word of a pixel's 64-bit output register (VulkanRenderer.TileWord1.cs): on the PS Vita the tile buffer
/// keeps the whole output register of every pixel for the scene, so a native-colour program with a one-word (UCHAR4)
/// colour can leave data in o0.y that a later program of the same scene reads back with its fragment colour. A writer
/// program puts <see cref="Colour"/> in o0.x and <see cref="Word"/> in o0.y; a reader program then moves o0.y to its
/// colour: the surface must show <see cref="Word"/> in every way a scene can be rendered — one sample, real MSAA
/// (input attachment and copy fetch, VITASHARP_MSAA_FETCH=copy), supersampled MSAA (VITASHARP_MSAA=ss) — on desktop
/// and tile-based GPUs (+TileBased forced on the software device). Where the word was not kept it read back as zero.
/// </summary>
[Collection("guest")]
public sealed class RendererTileWordTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;
    /// <summary>The writer's colour (red byte) and the word it leaves in o0.y (the reader's red byte).</summary>
    private const uint Colour = 9, Word = 51;
    private const uint OutputUchar4 = 1;
    private const string VmovPattern = "00111pppstrydecbmmaanoooiwwwwkllffgghhhhjjjjjjqqqqqquuuuuuvvvvvv";

    /// <summary>VMOV.S32 o[<paramref name="destWord"/>] = the immediate <paramref name="value"/> (0-63).</summary>
    private static ulong MovImmediateToOutput(uint destWord, uint value) => UsseEncoding.Encode(VmovPattern,
        ('s', 1), ('o', 2 /* S32 */), ('h', 1), ('l', 1 /* output bank */), ('j', destWord), ('c', 1), ('f', 2 /* immediate */), ('u', value));

    /// <summary>VMOV.S32 o[<paramref name="destWord"/>] = o[<paramref name="srcWord"/>].</summary>
    private static ulong MovOutputToOutput(uint destWord, uint srcWord) => UsseEncoding.Encode(VmovPattern,
        ('s', 1), ('o', 2 /* S32 */), ('h', 1), ('l', 1 /* output bank */), ('j', destWord), ('f', 1 /* output bank */), ('u', srcWord));

    /// <summary>A native-colour fragment GXP (UCHAR4 output declared) running <paramref name="code"/>.</summary>
    internal static byte[] NativeColourGxp(ulong[] code, bool fragColor)
    {
        byte[] d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        W32(0, 0x00505847);
        W32(8, (uint)d.Length);
        W32(0x14, GxpProgram.FlagFragment | GxpProgram.FlagNativeColorUsed | (fragColor ? GxpProgram.FlagFragColorUsed : 0));
        W32(0x2C, 0x60 - 0x2C); // varyings at 0x60
        d[0x60 + 10] = (byte)ParamType.U8;
        d[0x60 + 11] = 4;
        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40); // primary program
        for (int i = 0; i < code.Length; i++)
        {
            W32(0x100 + i * 8, (uint)code[i]);
            W32(0x104 + i * 8, (uint)(code[i] >> 32));
        }
        return d;
    }

    /// <summary>The writer: o0.x = <see cref="Colour"/>, o0.y = <see cref="Word"/>.</summary>
    internal static byte[] WriterGxp() => NativeColourGxp([MovImmediateToOutput(0, Colour), MovImmediateToOutput(1, Word)], fragColor: false);

    /// <summary>The reader: reads the pixel's output register (fragment colour), then o0.x = o0.y.</summary>
    internal static byte[] ReaderGxp() => NativeColourGxp([MovOutputToOutput(0, 1)], fragColor: true);

    [Fact]
    public void WriterAndReaderUseTheTileWord()
    {
        var writer = new GxpProgram(WriterGxp(), 0x81000000);
        var reader = new GxpProgram(ReaderGxp(), 0x81001000);
        var hints = new ShaderHints([], [], 0, false, OutputUchar4);
        GeneratedShader written = ShaderGenerator.Generate(writer, hints);
        GeneratedShader read = ShaderGenerator.Generate(reader, hints);
        Assert.True((written.TileWord1 & TileWord1Use.Writes) != 0, written.Glsl);
        Assert.True((read.TileWord1 & TileWord1Use.Reads) != 0, read.Glsl);
        Assert.Contains("out_tile_word1 = floatBitsToUint(o[0].y);", ShaderGenerator.Generate(writer, hints with { TileWord1 = true }).Glsl);
    }

    /// <summary>The red byte of every pixel of the last presented frame after the writer and the reader drew the whole
    /// surface in one scene.</summary>
    private byte[] Run(string tag, ushort multisample, string msaa, string? msaaFetch, string? gpuQuirks)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-tileword-" + Guid.NewGuid().ToString("N"));
        try
        {
            VulkanRenderer renderer = new(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                Msaa = msaa,
                MsaaFetch = msaaFetch,
                GpuQuirks = gpuQuirks,
                DumpFramesDir = frames,
                DumpEvery = 1,
            });
            using (HleHarness harness = new(fx, tag))
            {
                try
                {
                    harness.Emu.Gpu.Backend = renderer;
                    RendererTextureTests.Workload workload = new(harness, multisample, scaleMode: multisample != 0 ? 1u : 0u);
                    try
                    {
                        uint writer = workload.CreateFragmentProgram(WriterGxp(), OutputUchar4);
                        uint reader = workload.CreateFragmentProgram(ReaderGxp(), OutputUchar4);
                        Gxm gxm = harness.Module<Gxm>();
                        Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, workload.Rt, 0u, 0u, 0u, workload.Color, 0u));
                        Invoke(gxm, "sceGxmSetVertexProgram", workload.Ctx, workload.Vp);
                        Invoke(gxm, "sceGxmSetFrontDepthFunc", workload.Ctx, 0x00E00000u); // ALWAYS
                        Invoke(gxm, "sceGxmSetCullMode", workload.Ctx, 0u);
                        Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, workload.Verts));
                        Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, writer);
                        Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
                        Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, reader);
                        Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
                        Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
                        harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
                        Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
                        harness.Emu.Gpu.WaitIdle();
                    }
                    finally
                    {
                        workload.Terminate();
                    }
                }
                finally
                {
                    harness.Emu.Gpu.WaitIdle();
                    harness.Emu.Gpu.Backend = null;
                    renderer.Dispose();
                }
            }
            string last = Directory.GetFiles(frames, "frame_*.png").Order().Last();
            using var bitmap = SkiaSharp.SKBitmap.Decode(last);
            Assert.Equal((W, H), (bitmap.Width, bitmap.Height));
            byte[] red = new byte[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    red[y * W + x] = bitmap.GetPixel(x, y).Red;
                }
            }
            return red;
        }
        finally
        {
            try
            {
                Directory.Delete(frames, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <param name="msaa">VITASHARP_MSAA: off (one-sample scene), native (real MSAA), ss (supersampled)</param>
    /// <param name="msaaFetch">VITASHARP_MSAA_FETCH: null (input attachment) or copy</param>
    /// <param name="gpuQuirks">null: the device's; +TileBased: the tile-based renderings of mobile GPUs</param>
    [SoftwareVulkanTheory]
    [InlineData("off", null, null)]
    [InlineData("off", null, "+TileBased")]
    [InlineData("native", null, null)]
    [InlineData("native", null, "+TileBased")]
    [InlineData("native", "copy", null)]
    [InlineData("native", "copy", "+TileBased")]
    [InlineData("ss", null, null)]
    [InlineData("ss", null, "+TileBased")]
    public void ALaterProgramOfTheSceneReadsTheWordAnEarlierOneLeft(string msaa, string? msaaFetch, string? gpuQuirks)
    {
        ushort multisample = msaa == "off" ? (ushort)0 : (ushort)2; // 4X with downscale
        byte[] red = Run($"tw1{msaa}{msaaFetch}{gpuQuirks}", multisample, msaa, msaaFetch, gpuQuirks);
        int kept = red.Count(r => r == Word), lost = red.Count(r => r == 0), colour = red.Count(r => r == Colour);
        Assert.True(kept == red.Length, $"MSAA {msaa}, fetch {msaaFetch ?? "input"}, GPU quirks {gpuQuirks ?? "of the device"}: {kept} of {red.Length} pixels read the word back ({lost} read zero, {colour} kept the writer's colour)");
    }
}
