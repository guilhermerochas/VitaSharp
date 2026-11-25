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
        // the header's fields run to 0x98 (dependent samplers at 0x80, containers at 0x90): the tables come after it
        W32(0x28, 0xC0 - 0x28);   // parameters at 0xC0
        W32(0x2C, 0xA0 - 0x2C);   // varyings at 0xA0
        d[0x30] = 4;              // primary registers (symbols not stripped)
        d[0xA0 + 10] = (byte)(nativeColour ? ParamType.U8 : ParamType.F32);
        d[0xA0 + 11] = 4;
        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40);  // primary program
        W32(0x78, 1);             // one uniform buffer info
        W32(0x7C, 0xE0 - 0x7C);   // at 0xE0: buffer 0, its pointer at sa0
        // the parameter: uniform buffer 0, BufferBytes long
        W32(0xC0, 0xD0 - 0xC0);
        d[0xC4] = (byte)ParamCategory.UniformBuffer;
        W32(0xC8, BufferBytes);
        W32(0xCC, 0);
        "buf"u8.CopyTo(d.AsSpan(0xD0));
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

    /// <summary>The test programs read no texture: with their tables inside the header's fields, the parameter's
    /// bytes were read as 16 dependent samplers (texture units up to 24) and garbage containers, and on a device that
    /// binds 14 fragment units (MoltenVK in the macOS CI) the renderer skipped every draw of the test.</summary>
    [Fact]
    public void TheTestProgramsAreWellFormed()
    {
        foreach (byte[] gxp in new[] { StoringGxp(), ReadingGxp(0), ReadingGxp(1) })
        {
            var program = new GxpProgram(gxp, 0x81000000);
            Assert.Equal(0u, program.TexturesUsed());
            Assert.Empty(program.DependentSamplers);
            Assert.Empty(program.Containers);
            Assert.Empty(program.Literals);
            GxpParameter parameter = Assert.Single(program.Parameters);
            Assert.Equal((ParamCategory.UniformBuffer, "buf", BufferBytes), (parameter.Category, parameter.RawName, parameter.ArraySize));
            GxpUniformBufferInfo info = Assert.Single(program.UniformBuffers);
            Assert.Equal((0, 0, 0), ((int)info.ResideBuffer, (int)info.LdstBaseOffset, info.LdstBaseValue));
            Assert.Equal(0, program.VaryingsCount);
        }
    }

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
    private List<byte[]> Run(string tag, MemoryRegion region, uint word, string? gpuQuirks, byte[]? memoryAfterStore = null)
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
                        if (memoryAfterStore != null)
                        {
                            GuestMemory.ReadSpan(buffer, memoryAfterStore.Length).CopyTo(memoryAfterStore);
                        }

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

    /// <summary>
    /// The CPU reads what a program stored once the scene's GPU work is done (on the Vita the USSE stores into the
    /// memory itself): the float at +0, the byte at +5 and the half at +6 are in guest memory after the storing frame,
    /// and the bytes the program did not store keep the CPU's 0x11. Before, the stores stayed in the renderer's mirror
    /// and the CPU read the stale 0x11 bytes.
    /// </summary>
    [SoftwareVulkanTheory]
    [InlineData(MemoryRegion.User)]
    [InlineData(MemoryRegion.Cdram)]
    public void TheCpuReadsWhatAProgramStored(MemoryRegion region)
    {
        byte[] memory = new byte[0x2000];
        Run($"storecpu{region}", region, 0, null, memory);
        byte[] want = [0x00, 0x00, 0xE0, 0x40, 0x11, 0x2A, 0x15, 0x00];
        Assert.True(memory.AsSpan(0, 8).SequenceEqual(want), $"{region} memory after the storing frame: {Convert.ToHexString(memory, 0, 8)}, want {Convert.ToHexString(want)}");
        int other = memory.AsSpan(8).IndexOfAnyExcept((byte)0x11);
        Assert.True(other < 0, $"{region} memory: byte +0x{other + 8:X} the program did not store changed to 0x{memory[Math.Max(0, other) + 8]:X2}");
    }

    /// <summary>
    /// The publishing contract (<see cref="VulkanRenderer.PublishStored"/>): a stored range is the program's whole
    /// window, where the CPU also writes. Pages the CPU wrote since the store (swept or still marked) keep the CPU's
    /// data; on the other pages only the words that changed are written, and the pages are not marked written (a mark
    /// would send the stored range back to an older guest copy while newer stores are in flight).
    /// </summary>
    [Fact]
    public void PublishingKeepsWhatTheCpuWroteSinceTheStore()
    {
        if (!DirtyPages.Enabled)
        {
            return;
        }

        using HleHarness harness = new(fx, "storepublish");
        const uint Page = 1u << DirtyPages.PageShift;
        uint buffer = fx.Memory.Alloc(4 * Page, "storepublish", MemoryRegion.User);
        try
        {
            GuestMemory.Fill(buffer, 0x11, 4 * Page);
            DirtyPages.Sweep();
            DirtyPages.Sweep();
            uint stamp = DirtyPages.Epoch + 1;
            byte[] stored = new byte[4 * Page];
            stored.AsSpan().Fill(0x11);
            stored.AsSpan(0, 8).Fill(0x22);                     // page 0: stored, the CPU did not touch it
            stored.AsSpan((int)Page + 16, 4).Fill(0x22);        // page 1: stored, the CPU wrote the page since (swept)
            stored.AsSpan((int)(2 * Page) + 32, 4).Fill(0x22);  // page 2: stored, the CPU wrote it since (still marked)
            stored.AsSpan((int)(3 * Page) + 1, 3).Fill(0x22);   // page 3: a partial word
            GuestMemory.Write(buffer + Page + 64, 0x33333333u);
            DirtyPages.Sweep();
            GuestMemory.Write(buffer + 2 * Page + 64, 0x44444444u);

            int written = VulkanRenderer.PublishStored(stored, buffer, stamp);

            Assert.Equal(12, written);
            Assert.True(GuestMemory.ReadSpan(buffer, 8).SequenceEqual(stored.AsSpan(0, 8)), "page 0: the stored words were not written");
            Assert.Equal(0x11111111u, GuestMemory.Read<uint>(buffer + Page + 16));
            Assert.Equal(0x33333333u, GuestMemory.Read<uint>(buffer + Page + 64));
            Assert.Equal(0x11111111u, GuestMemory.Read<uint>(buffer + 2 * Page + 32));
            Assert.Equal(0x44444444u, GuestMemory.Read<uint>(buffer + 2 * Page + 64));
            Assert.Equal(0x22222211u, GuestMemory.Read<uint>(buffer + 3 * Page));
            Assert.False(DirtyPages.MarkedNow(buffer, Page), "publishing marked page 0 written");
            Assert.False(DirtyPages.MarkedNow(buffer + 3 * Page, Page), "publishing marked page 3 written");
        }
        finally
        {
            fx.Memory.Free(buffer);
        }
    }

    [Fact]
    public void OnlyTheWordsThatChangedAreCopied()
    {
        byte[] guest = new byte[133];
        byte[] stored = new byte[133];
        guest.AsSpan().Fill(0x11);
        stored.AsSpan().Fill(0x11);
        stored[70] = 0x5A;   // a word inside the second 64-byte chunk
        stored[132] = 0x6B;  // the tail byte

        Assert.Equal(5, VulkanRenderer.CopyChangedWords(stored, guest));
        Assert.Equal(stored, guest);
        Assert.Equal(0, VulkanRenderer.CopyChangedWords(stored, guest));
    }
}
