using System.Buffers.Binary;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Memory written by shader programs (USSE VLDST stores through a uniform buffer pointer, VulkanRenderer.Mapped.cs):
/// the GPU writes guest memory directly, wherever the buffer is mapped for it (CDRAM or LPDDR), in 32-, 16- and 8-bit
/// stores, and later draws read what it stored. A storing fragment program writes a float at +0 and an 8-bit and a
/// 16-bit value into the word at +4 of a buffer filled with 0x11 bytes; a reading program loads one of the words and
/// shows its bytes as the colour (native UCHAR4 output), in the same scene and in the next frame without stores.
/// Before, stores outside CDRAM went to a per-frame scratch copy (later draws read the stale guest memory) and 16/8-bit
/// stores were dropped everywhere.
/// </summary>
[Collection("guest")]
public sealed class RendererMemoryStoreTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;
    private const uint BufferBytes = 0x40000; // above the mapped-slot threshold: read and written in place
    private const string VldstPattern = "111oopppsnmycrbakkkkddeetgffihjlqquuvvvvvvvwwwwwwwxxxxxxxzzzzzzz";
    private const string VmovPattern = "00111pppstrydecbmmaanoooiwwwwkllffgghhhhjjjjjjqqqqqquuuuuuvvvvvv";

    /// <summary>VLDST store of the immediate <paramref name="value"/> as <paramref name="type"/> (0 F32, 1 INT16, 2
    /// INT8) at the buffer pointer (sa0) + <paramref name="index"/> elements.</summary>
    internal static ulong StoreImmediate(uint type, uint index, uint value) => UsseEncoding.Encode(VldstPattern,
        ('o', 2), ('f', type), ('m', 1), ('r', 1), ('h', 1), ('w', 0), ('b', 1), ('q', 2), ('x', index), ('a', 1), ('u', 2), ('z', value));

    /// <summary>VLDST load of one 32-bit word at the buffer pointer (sa0) + 4 * <paramref name="word"/> into pa0.</summary>
    internal static ulong LoadWordToPa0(uint word) => UsseEncoding.Encode(VldstPattern,
        ('o', 1), ('f', 0), ('m', 1), ('r', 1), ('h', 1), ('w', 0), ('b', 1), ('q', 2), ('x', word), ('a', 1), ('u', 2), ('z', 0), ('t', 1), ('v', 0));

    /// <summary>VMOV.S32 o0 = pa0.</summary>
    private static readonly ulong s_movPa0ToO0 = UsseEncoding.Encode(VmovPattern, ('s', 1), ('o', 2), ('h', 1), ('l', 1), ('j', 0), ('f', 2), ('u', 0));

    /// <summary>A fragment GXP with uniform buffer 0 (<see cref="BufferBytes"/>, its pointer in sa0) running
    /// <paramref name="code"/>; <paramref name="nativeColour"/>: the colour is o0 as UCHAR4.</summary>
    internal static byte[] BufferProgramGxp(ulong[] code, bool nativeColour)
    {
        byte[] d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        W32(0, 0x00505847);
        W32(8, (uint)d.Length);
        W32(0x14, GxpProgram.FlagFragment | (nativeColour ? GxpProgram.FlagNativeColorUsed : 0));
        W32(0x24, 1);             // one parameter
        W32(0x28, 0x80 - 0x28);   // parameters at 0x80
        W32(0x2C, 0x60 - 0x2C);   // varyings at 0x60
        d[0x30] = 4;              // primary registers (symbols not stripped)
        d[0x60 + 10] = (byte)(nativeColour ? ParamType.U8 : ParamType.F32);
        d[0x60 + 11] = 4;
        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40);  // primary program
        W32(0x78, 1);             // one uniform buffer info
        W32(0x7C, 0xC0 - 0x7C);   // at 0xC0: buffer 0, its pointer at sa0
        // the parameter: uniform buffer 0, BufferBytes long
        W32(0x80, 0x90 - 0x80);
        d[0x84] = (byte)ParamCategory.UniformBuffer;
        W32(0x88, BufferBytes);
        W32(0x8C, 0);
        "buf"u8.CopyTo(d.AsSpan(0x90));
        for (int i = 0; i < code.Length; i++)
        {
            W32(0x100 + i * 8, (uint)code[i]);
            W32(0x104 + i * 8, (uint)(code[i] >> 32));
        }
        return d;
    }

    /// <summary>The storing program: float 7 at +0, the byte 0x2A at +5, the half 0x0015 at +6.</summary>
    internal static byte[] StoringGxp() => BufferProgramGxp([StoreImmediate(0, 0, 7), StoreImmediate(2, 5, 0x2A), StoreImmediate(1, 3, 0x15)], nativeColour: false);

    internal static byte[] ReadingGxp(uint word) => BufferProgramGxp([LoadWordToPa0(word), s_movPa0ToO0], nativeColour: true);

    [Fact]
    public void StoresOfEverySizeAreTranslated()
    {
        var program = new GxpProgram(StoringGxp(), 0x81000000);
        GeneratedShader gen = ShaderGenerator.Generate(program, new ShaderHints([], [], 0, false, 0));
        Assert.True(gen.WritesMemory, gen.Glsl);
        Assert.DoesNotContain(gen.Warnings, w => w.Contains("store", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("storeMemory(", gen.Glsl.Split("void main")[1]);
        Assert.Contains("storeMemory8(", gen.Glsl.Split("void main")[1]);
        Assert.Contains("storeMemory16(", gen.Glsl.Split("void main")[1]);
        // the narrow stores keep the other bytes of their word
        Assert.Contains("atomicAnd(mem[j], ~(0xFFu << sh))", gen.Glsl);
        Assert.Contains("atomicAnd(mem[j], ~(0xFFFFu << sh))", gen.Glsl);
    }

    /// <summary>The RGB of every pixel of each presented frame: frame 0 stores then reads, frame 1 only reads, frame 2
    /// reads after the CPU rewrote the buffer with 0x33 bytes.</summary>
    private List<byte[]> Run(string tag, MemoryRegion region, uint word, string? gpuQuirks)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            VulkanRenderer renderer = new(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                GpuQuirks = gpuQuirks,
                DumpFramesDir = frames,
                DumpEvery = 1,
            });
            using (HleHarness harness = new(fx, tag))
            {
                uint buffer = fx.Memory.Alloc(BufferBytes, tag, region);
                try
                {
                    harness.Emu.Gpu.Backend = renderer;
                    GuestMemory.Fill(buffer, 0x11, BufferBytes);
                    RendererTextureTests.Workload workload = new(harness);
                    try
                    {
                        uint storing = workload.CreateFragmentProgram(StoringGxp(), 0);
                        uint reading = workload.CreateFragmentProgram(ReadingGxp(word), 1 /* UCHAR4 */);
                        Gxm gxm = harness.Module<Gxm>();
                        void Scene(bool store)
                        {
                            Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, workload.Rt, 0u, 0u, 0u, workload.Color, 0u));
                            Invoke(gxm, "sceGxmSetVertexProgram", workload.Ctx, workload.Vp);
                            Invoke(gxm, "sceGxmSetFrontDepthFunc", workload.Ctx, 0x00E00000u); // ALWAYS
                            Invoke(gxm, "sceGxmSetCullMode", workload.Ctx, 0u);
                            Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, workload.Verts));
                            if (store)
                            {
                                Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, storing);
                                Assert.Equal(0, Call(gxm, "sceGxmSetFragmentUniformBuffer", workload.Ctx, 0u, buffer));
                                Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
                            }

                            Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, reading);
                            Assert.Equal(0, Call(gxm, "sceGxmSetFragmentUniformBuffer", workload.Ctx, 0u, buffer));
                            Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
                            Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
                            harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
                            Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
                            harness.Emu.Gpu.WaitIdle();
                        }

                        Scene(store: true);
                        Scene(store: false);
                        GuestMemory.Fill(buffer, 0x33, BufferBytes);
                        Scene(store: false);
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
                    fx.Memory.Free(buffer);
                }
            }
            var result = new List<byte[]>();
            foreach (string file in Directory.GetFiles(frames, "frame_*.png").Order())
            {
                using var bitmap = SkiaSharp.SKBitmap.Decode(file);
                byte[] rgb = new byte[W * H * 3];
                for (int i = 0; i < W * H; i++)
                {
                    SkiaSharp.SKColor pixel = bitmap.GetPixel(i % W, i / W);
                    (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (pixel.Red, pixel.Green, pixel.Blue);
                }
                result.Add(rgb);
            }
            return result;
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

    private static int Matching(byte[] rgb, (byte R, byte G, byte B) want)
    {
        int n = 0;
        for (int i = 0; i < rgb.Length; i += 3)
        {
            if ((rgb[i], rgb[i + 1], rgb[i + 2]) == want)
            {
                n++;
            }
        }
        return n;
    }

    /// <param name="word">0: the float word (bytes 00 00 E0 40); 1: the word with the byte and the half (11 2A 15 00)</param>
    [SoftwareVulkanTheory]
    [InlineData(MemoryRegion.User, 0u, null)]
    [InlineData(MemoryRegion.User, 1u, null)]
    [InlineData(MemoryRegion.Cdram, 0u, null)]
    [InlineData(MemoryRegion.Cdram, 1u, null)]
    [InlineData(MemoryRegion.User, 1u, "+TileBased")]
    [InlineData(MemoryRegion.Cdram, 1u, "+TileBased")]
    public void LaterDrawsReadWhatAProgramStored(MemoryRegion region, uint word, string? gpuQuirks)
    {
        List<byte[]> frames = Run($"store{region}{word}{gpuQuirks}", region, word, gpuQuirks);
        Assert.Equal(3, frames.Count);
        (byte, byte, byte) stored = word == 0 ? ((byte)0x00, (byte)0x00, (byte)0xE0) : ((byte)0x11, (byte)0x2A, (byte)0x15);
        string setup = $"{region} memory, word {word}, GPU quirks {gpuQuirks ?? "of the device"}";
        int total = W * H;
        Assert.True(Matching(frames[0], stored) == total, $"{setup}: the draw after the store read {Matching(frames[0], stored)} of {total} pixels right ({Matching(frames[0], (0x11, 0x11, 0x11))} read the stale guest memory)");
        Assert.True(Matching(frames[1], stored) == total, $"{setup}: the next frame read {Matching(frames[1], stored)} of {total} pixels right ({Matching(frames[1], (0x11, 0x11, 0x11))} read the stale guest memory)");
        // the CPU rewrote the memory: its pages go back to the guest copy
        Assert.True(Matching(frames[2], (0x33, 0x33, 0x33)) == total, $"{setup}: after the CPU rewrote the buffer {Matching(frames[2], (0x33, 0x33, 0x33))} of {total} pixels read it");
    }
}
