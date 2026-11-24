using VitaSharp.Core.Compat.Ge;
using VitaSharp.Renderer.Vulkan;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The GE's Vulkan rasterizer on the software Vulkan device (SwiftShader / lavapipe / MoltenVK, see
/// <see cref="SoftwareVulkan"/>): synthetic display lists run by the GeEngine draw into the PSP framebuffer in
/// memory, and the pixels are checked there: a clear, a solid triangle, a textured sprite, alpha blending, the depth
/// test, the scissor and a 565 framebuffer.
/// </summary>
public sealed class GeVulkanRasterizerTests : IDisposable
{
    private const uint ListAddress = 0x08001000, Vertices = 0x08010000, Texture = 0x08020000;
    private const uint ThroughColorS16 = (1u << 23) | (7u << 2) | (2u << 7);
    private const uint ThroughTextureColorS16 = (1u << 23) | 2u | (7u << 2) | (2u << 7);
    private const int Stride = 512;

    private readonly IDisposable? _driver;
    private readonly VkCore? _core;
    private readonly ShaderCompiler? _compiler;
    private readonly GeVulkanRasterizer? _rasterizer;
    private readonly GeTestMemory _memory = new();
    private readonly GeEngine _engine;

    public GeVulkanRasterizerTests()
    {
        _engine = new GeEngine(_memory);
        if (SoftwareVulkan.SkipReason != null || ShadercAvailability.SkipReason != null)
        {
            return;
        }

        _driver = SoftwareVulkan.Use();
        _core = new VkCore(null, false);
        _compiler = new ShaderCompiler(null);
        _rasterizer = new GeVulkanRasterizer(_core, _compiler);
        _engine.Rasterizer = _rasterizer;
    }

    public void Dispose()
    {
        _rasterizer?.Dispose();
        _compiler?.Dispose();
        _core?.Dispose();
        _driver?.Dispose();
    }

    private GeListBuilder Setup(int pixelFormat = GePixels.Format8888, int scissorRight = 479, int scissorBottom = 271)
    {
        return new GeListBuilder(_memory, ListAddress).RamBase()
            .Command(GeCommand.FrameBufferPointer, 0)
            .Command(GeCommand.FrameBufferWidth, Stride)
            .Command(GeCommand.FrameBufferPixelFormat, (uint)pixelFormat)
            .Command(GeCommand.DepthBufferPointer, 0x110000)
            .Command(GeCommand.DepthBufferWidth, Stride)
            .Command(GeCommand.Scissor1, 0)
            .Command(GeCommand.Scissor2, (uint)scissorRight | ((uint)scissorBottom << 10))
            .Command(GeCommand.ShadeMode, 1);
    }

    /// <summary>Through-mode vertices (8888 colour, s16 position) at <paramref name="address"/>.</summary>
    private void Vertex(uint address, int index, short x, short y, ushort z, uint color)
    {
        uint at = address + (uint)(index * 12);
        _memory.Write32(at, color);
        _memory.Write16(at + 4, (ushort)x);
        _memory.Write16(at + 6, (ushort)y);
        _memory.Write16(at + 8, z);
    }

    private GeListBuilder Rectangle(GeListBuilder list, uint address, short left, short top, short right, short bottom, ushort z, uint color)
    {
        Vertex(address, 0, left, top, z, color);
        Vertex(address, 1, right, bottom, z, color);
        return list.Command(GeCommand.VertexAddress, address & 0xFFFFFF)
            .Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.Primitive, (6u << 16) | 2);
    }

    private uint Pixel(int x, int y)
    {
        return _memory.Read32(GeTestMemory.VramBase + (uint)((y * Stride + x) * 4));
    }

    private ushort Depth(int x, int y)
    {
        return _memory.Read16(GeTestMemory.VramBase + 0x110000 + (uint)((y * Stride + x) * 2));
    }

    [SoftwareVulkanFact]
    public void ClearModeFillsTheScissorWithColourAlphaAndDepth()
    {
        GeListBuilder list = Setup(scissorRight: 99, scissorBottom: 49).Command(GeCommand.ClearMode, 1 | 0x100 | 0x200 | 0x400);
        Rectangle(list, Vertices, 0, 0, 480, 272, 0x1234, 0x80336699).Command(GeCommand.ClearMode, 0).End();
        GeRegisters.Run(_engine, list.Start);
        Assert.Equal(1, _rasterizer!.Draws);
        Assert.Equal(0x80336699u, Pixel(0, 0));
        Assert.Equal(0x80336699u, Pixel(99, 49));
        Assert.Equal(0u, Pixel(100, 0)); // outside the scissor
        Assert.Equal(0u, Pixel(0, 50));
        Assert.Equal((ushort)0x1234, Depth(50, 20));
    }

    [SoftwareVulkanFact]
    public void ASolidTriangleCoversItsInsideOnly()
    {
        Vertex(Vertices, 0, 10, 10, 0, 0xFF0000FF);
        Vertex(Vertices, 1, 110, 10, 0, 0xFF0000FF);
        Vertex(Vertices, 2, 10, 110, 0, 0xFF0000FF);
        GeRegisters.Run(_engine, Setup().Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF).Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.Primitive, (3u << 16) | 3).End().Start);
        Assert.Equal(0xFFu, Pixel(20, 20) & 0xFFFFFF); // red inside (alpha is the stencil: left as it was)
        Assert.Equal(0u, Pixel(100, 100)); // beyond the hypotenuse
        Assert.Equal(0u, Pixel(5, 5));
    }

    /// <summary>A 2x2 texture on an 8x8 sprite (nearest, replace): each quadrant takes its texel.</summary>
    [SoftwareVulkanFact]
    public void ATexturedSpriteTakesItsTexels()
    {
        uint[] texels = [0xFF0000FF, 0xFF00FF00, 0xFFFF0000, 0xFFFFFFFF];
        for (int i = 0; i < 4; i++)
        {
            _memory.Write32(Texture + (uint)(i * 4), texels[i]);
        }

        // u16 texture, 8888 colour, s16 position: 4 + 4 + 6 = 14, aligned to 16
        uint first = Vertices;
        _memory.Write16(first, 0);
        _memory.Write16(first + 2, 0);
        _memory.Write32(first + 4, 0xFFFFFFFF);
        _memory.Write16(first + 8, 40);
        _memory.Write16(first + 10, 40);
        uint second = Vertices + 16;
        _memory.Write16(second, 2);
        _memory.Write16(second + 2, 2);
        _memory.Write32(second + 4, 0xFFFFFFFF);
        _memory.Write16(second + 8, 48);
        _memory.Write16(second + 10, 48);
        GeRegisters.Run(_engine, Setup()
            .Command(GeCommand.TextureMapEnable, 1)
            .Command(GeCommand.TextureAddress0, Texture & 0xFFFFFF)
            .Command(GeCommand.TextureBufferWidth0, 2 | ((Texture >> 8) & 0x0F0000))
            .Command(GeCommand.TextureSize0, 1 | (1 << 8))
            .Command(GeCommand.TextureFormat, GePixels.Format8888)
            .Command(GeCommand.TextureFunction, 3 | 0x100) // replace, with the texture's alpha
            .Command(GeCommand.TextureFilter, 0)
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF).Command(GeCommand.VertexType, ThroughTextureColorS16)
            .Command(GeCommand.Primitive, (6u << 16) | 2).End().Start);
        Assert.Equal(0x0000FFu, Pixel(41, 41) & 0xFFFFFF);
        Assert.Equal(0x00FF00u, Pixel(46, 41) & 0xFFFFFF);
        Assert.Equal(0xFF0000u, Pixel(41, 46) & 0xFFFFFF);
        Assert.Equal(0xFFFFFFu, Pixel(46, 46) & 0xFFFFFF);
        Assert.Equal(0u, Pixel(48, 48));
    }

    /// <summary>A half-transparent blue over red with SRC_ALPHA / ONE_MINUS_SRC_ALPHA gives about half of each.</summary>
    [SoftwareVulkanFact]
    public void AlphaBlendingMixesWithTheFramebuffer()
    {
        GeListBuilder list = Setup();
        Rectangle(list, Vertices, 0, 0, 16, 16, 0, 0xFF0000FF);
        list.Command(GeCommand.AlphaBlendEnable, 1).Command(GeCommand.BlendMode, 2 | (3 << 4));
        Rectangle(list, Vertices + 0x100, 0, 0, 16, 16, 0, 0x80FF0000).End();
        GeRegisters.Run(_engine, list.Start);
        uint pixel = Pixel(8, 8);
        Assert.InRange((int)(pixel & 0xFF), 0x78, 0x88);
        Assert.InRange((int)((pixel >> 16) & 0xFF), 0x78, 0x88);
        Assert.Equal(0u, (pixel >> 8) & 0xFF);
    }

    /// <summary>With ZTEST "greater or equal" (the GE's nearer is the larger depth) a farther rectangle drawn second
    /// stays behind; a nearer one covers, and writes its depth.</summary>
    [SoftwareVulkanFact]
    public void TheDepthTestKeepsTheNearerFragment()
    {
        GeListBuilder list = Setup().Command(GeCommand.ClearMode, 1 | 0x400);
        Rectangle(list, Vertices, 0, 0, 480, 272, 0, 0).Command(GeCommand.ClearMode, 0)
            .Command(GeCommand.DepthTestEnable, 1).Command(GeCommand.DepthTest, 7);
        Rectangle(list, Vertices + 0x100, 0, 0, 32, 32, 40000, 0xFF0000FF);
        Rectangle(list, Vertices + 0x200, 16, 16, 48, 48, 20000, 0xFF00FF00);
        Rectangle(list, Vertices + 0x300, 24, 24, 40, 40, 60000, 0xFFFF0000).End();
        GeRegisters.Run(_engine, list.Start);
        Assert.Equal(0x0000FFu, Pixel(20, 20) & 0xFFFFFF); // the farther green stays behind the red
        Assert.Equal(0x00FF00u, Pixel(44, 44) & 0xFFFFFF); // green where nothing was nearer
        Assert.Equal(0xFF0000u, Pixel(30, 30) & 0xFFFFFF); // the nearest blue on top
        Assert.InRange(Depth(30, 30), (ushort)59990, (ushort)60010);
    }

    /// <summary>Without the depth test nothing writes the depth buffer, even with depth writes on.</summary>
    [SoftwareVulkanFact]
    public void WithoutTheDepthTestTheDepthBufferIsLeft()
    {
        GeListBuilder list = Setup().Command(GeCommand.ClearMode, 1 | 0x400);
        Rectangle(list, Vertices, 0, 0, 64, 64, 0x1234, 0).Command(GeCommand.ClearMode, 0)
            .Command(GeCommand.DepthTestEnable, 0).Command(GeCommand.DepthWriteDisable, 0);
        Rectangle(list, Vertices + 0x100, 0, 0, 32, 32, 50000, 0xFF0000FF).End();
        GeRegisters.Run(_engine, list.Start);
        Assert.Equal(0x0000FFu, Pixel(10, 10) & 0xFFFFFF);
        Assert.Equal((ushort)0x1234, Depth(10, 10));
    }

    /// <summary>A 16x16 target and a screen-covering pair of triangles with the given vertex fields, for the
    /// rasterizer's own inputs (the secondary colour, fog, Q), drawn without the GE side.</summary>
    private uint DrawQuad(GeRenderState state, Func<GeVertex, GeVertex> fill)
    {
        GeTarget target = new() { Width = 16, Height = 16, Color = new uint[256] };
        state.ScissorRight = 15;
        state.ScissorBottom = 15;
        (float X, float Y, float U)[] corners = [(0, 0, 0), (16, 0, 1), (0, 16, 0), (16, 0, 1), (16, 16, 1), (0, 16, 0)];
        var vertices = new GeVertex[corners.Length];
        for (int i = 0; i < corners.Length; i++)
        {
            vertices[i] = fill(new GeVertex { X = corners[i].X, Y = corners[i].Y, W = 1, U = corners[i].U, V = 0.5f, Color = 0xFF404040 });
        }

        _rasterizer!.Draw(GeTopology.Triangles, vertices, state, target);
        return target.Color[8 * 16 + 4];
    }

    /// <summary>The secondary (separate specular) colour is added to the colour after the texture stage.</summary>
    [SoftwareVulkanFact]
    public void TheSecondaryColourIsAddedAfterTheTexture()
    {
        uint pixel = DrawQuad(new GeRenderState(), vertex =>
        {
            vertex.Color1 = 0x00203040;
            return vertex;
        });
        Assert.InRange((int)(pixel & 0xFF), 0x7F, 0x81); // 0x40 + 0x40
        Assert.InRange((int)((pixel >> 8) & 0xFF), 0x6F, 0x71);
        Assert.InRange((int)((pixel >> 16) & 0xFF), 0x5F, 0x61); // 0x40 + 0x20
    }

    /// <summary>With fog on, the vertices' factor mixes the colour with FOGCOLOR (0: all fog).</summary>
    [SoftwareVulkanFact]
    public void FogMixesTowardsTheFogColour()
    {
        GeRenderState state = new() { Fog = true, FogColor = 0x00FF00 };
        uint fogged = DrawQuad(state, vertex =>
        {
            vertex.Fog = 0;
            return vertex;
        });
        Assert.Equal(0x00FF00u, fogged & 0xFFFFFF);
        uint clear = DrawQuad(new GeRenderState { Fog = true, FogColor = 0x00FF00 }, vertex => vertex);
        Assert.Equal(0x404040u, clear & 0xFFFFFF);
    }

    /// <summary>The texture is sampled at U/Q: with Q = 0.5 the pixel at x = 4 (U = 4.5 / 16) samples U/Q = 0.56, the
    /// right texel (without the division it would be the left one).</summary>
    [SoftwareVulkanFact]
    public void TheTextureIsSampledAtUOverQ()
    {
        GeRenderState state = new()
        {
            Texture = true,
            TextureWidth = 2,
            TextureHeight = 1,
            TexturePixels = [0xFF0000FF, 0xFF00FF00],
            TextureFunction = 3,
            TextureAlpha = true,
        };
        uint sampled = DrawQuad(state, vertex =>
        {
            vertex.Q = 0.5f;
            return vertex;
        });
        Assert.Equal(0x00FF00u, sampled & 0xFFFFFF);
    }

    [SoftwareVulkanFact]
    public void A565FramebufferGetsItsPixels()
    {
        GeListBuilder list = Setup(GePixels.Format565);
        Rectangle(list, Vertices, 4, 4, 8, 8, 0, 0xFF00FF00).End();
        GeRegisters.Run(_engine, list.Start);
        Assert.Equal(GePixels.To565(0x0000FF00), _memory.Read16(GeTestMemory.VramBase + (uint)((5 * Stride + 5) * 2)));
        Assert.Equal((ushort)0, _memory.Read16(GeTestMemory.VramBase + (uint)((3 * Stride + 3) * 2)));
    }
}
