using System.Diagnostics;
using VitaSharp.Core.Compat.Ge;
using VitaSharp.Renderer.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// A measurement, not a check (Category=Slow, no assertion on time): a heavy synthetic list (many textured sprites
/// into a 480x272 framebuffer, as a PSP frame of 2D sprites) run by the GeEngine on the Vulkan rasterizer, the host
/// time per list printed. Run by hand before and after a change to the rasterizer's performance:
/// <c>dotnet test --filter FullyQualifiedName~GeRasterizerBenchmarkTests</c>.
/// </summary>
[Trait("Category", "Slow")]
public sealed class GeRasterizerBenchmarkTests(ITestOutputHelper output)
{
    private const uint ListAddress = 0x08001000, Vertices = 0x08100000, Texture = 0x08020000;
    private const uint ThroughTextureColorS16 = (1u << 23) | 2u | (7u << 2) | (2u << 7);
    private const int Sprites = 2000, Lists = 10;

    [SoftwareVulkanFact]
    public void ManyTexturedSpritesPerList()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using GeVulkanRasterizerDevice device = Assert.IsType<GeVulkanRasterizerDevice>(GeVulkanRasterizerDevice.Create());
        GeTestMemory memory = new();
        GeEngine engine = new(memory) { RasterizerFactory = () => device };
        for (int i = 0; i < 16 * 16; i++)
        {
            memory.Write32(Texture + (uint)(i * 4), 0xFF000000u | (uint)(i * 0x010203));
        }

        GeListBuilder list = new GeListBuilder(memory, ListAddress).RamBase()
            .Command(GeCommand.FrameBufferPointer, 0).Command(GeCommand.FrameBufferWidth, 512)
            .Command(GeCommand.FrameBufferPixelFormat, GePixels.Format8888)
            .Command(GeCommand.Scissor1, 0).Command(GeCommand.Scissor2, 479 | (271u << 10))
            .Command(GeCommand.TextureMapEnable, 1).Command(GeCommand.TextureAddress0, Texture & 0xFFFFFF)
            .Command(GeCommand.TextureBufferWidth0, 16 | ((Texture >> 8) & 0x0F0000)).Command(GeCommand.TextureSize0, 4 | (4 << 8))
            .Command(GeCommand.TextureFormat, GePixels.Format8888).Command(GeCommand.TextureFunction, 0)
            .Command(GeCommand.AlphaBlendEnable, 1).Command(GeCommand.BlendMode, 2 | (3 << 4))
            .Command(GeCommand.VertexType, ThroughTextureColorS16)
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF);
        for (int i = 0; i < Sprites; i++)
        {
            uint vertex = Vertices + (uint)(i * 32);
            short x = (short)(i * 7 % 464);
            short y = (short)(i * 13 % 256);
            WriteVertex(memory, vertex, 0, 0, x, y);
            WriteVertex(memory, vertex + 16, 16, 16, (short)(x + 16), (short)(y + 16));
            list.Command(GeCommand.Primitive, (6u << 16) | 2);
        }

        list.End();
        GeRegisters.Run(engine, list.Start); // warm-up: pipelines, images
        var watch = Stopwatch.StartNew();
        for (int run = 0; run < Lists; run++)
        {
            engine.Write32(GeRegisters.InterruptAcknowledge, 0xF);
            GeRegisters.Run(engine, list.Start);
        }

        watch.Stop();
        output.WriteLine($"GE benchmark: {Sprites} textured sprites per list, {watch.Elapsed.TotalMilliseconds / Lists:F1} ms per list on {SoftwareVulkan.Driver}");
        Assert.Equal((long)(Lists + 1) * Sprites, engine.Batches);
    }

    private static void WriteVertex(GeTestMemory memory, uint address, ushort u, ushort v, short x, short y)
    {
        memory.Write16(address, u);
        memory.Write16(address + 2, v);
        memory.Write32(address + 4, 0xC0FFFFFF);
        memory.Write16(address + 8, (ushort)x);
        memory.Write16(address + 10, (ushort)y);
        memory.Write16(address + 12, 0);
    }
}
