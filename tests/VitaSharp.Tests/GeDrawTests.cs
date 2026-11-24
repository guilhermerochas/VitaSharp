using VitaSharp.Core.Compat.Ge;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The GE's drawing on the core side, before any rasterizer: vertices decoded by VTYPE (through mode and the
/// world/view/projection/viewport transform), primitives assembled into lists (rectangles, strips, fans, flat
/// shading, culling), the state the draw carries, textures and CLUTs decoded to RGBA8, the framebuffer and depth
/// buffer converted for the rasterizer and written back, LOADCLUT and the block transfer. Synthetic lists only.
/// </summary>
public sealed class GeDrawTests
{
    private const uint ListAddress = 0x08001000, Vertices = 0x08010000, Texture = 0x08020000, Clut = 0x08030000;
    private const uint ThroughColorS16 = (1u << 23) | (7u << 2) | (2u << 7);   // through, 8888 colour, s16 position
    private const uint ThroughTextureS16 = (1u << 23) | 2u | (2u << 7);         // through, u16 texture, s16 position

    private readonly GeTestMemory _memory = new();
    private readonly GeEngine _engine;
    private readonly GeCapturingRasterizer _rasterizer = new();

    public GeDrawTests()
    {
        _engine = new GeEngine(_memory) { Rasterizer = _rasterizer };
    }

    /// <summary>A list that sets a 480x272 8888 framebuffer at VRAM 0 with the full scissor.</summary>
    private GeListBuilder Setup(int pixelFormat = GePixels.Format8888)
    {
        return new GeListBuilder(_memory, ListAddress).RamBase()
            .Command(GeCommand.FrameBufferPointer, 0)
            .Command(GeCommand.FrameBufferWidth, 512)
            .Command(GeCommand.FrameBufferPixelFormat, (uint)pixelFormat)
            .Command(GeCommand.Scissor1, 0)
            .Command(GeCommand.Scissor2, 479 | (271u << 10))
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF);
    }

    private void ThroughVertex(int index, short x, short y, ushort z, uint color)
    {
        uint address = Vertices + (uint)(index * 12);
        _memory.Write32(address, color);
        _memory.Write16(address + 4, (ushort)x);
        _memory.Write16(address + 6, (ushort)y);
        _memory.Write16(address + 8, z);
    }

    [Fact]
    public void AThroughModeRectangleBecomesTwoTrianglesWithTheSecondCornersColour()
    {
        ThroughVertex(0, 10, 20, 0, 0xFF0000FF);
        ThroughVertex(1, 30, 40, 0xFFFF, 0xFF00FF00);
        GeRegisters.Run(_engine, Setup().Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.Primitive, (6u << 16) | 2).End().Start);

        (GeTopology topology, GeVertex[] vertices) = Assert.Single(_rasterizer.Draws);
        Assert.Equal(GeTopology.Triangles, topology);
        Assert.Equal(6, vertices.Length);
        Assert.All(vertices, v => Assert.Equal(0xFF00FF00u, v.Color));
        Assert.All(vertices, v => Assert.Equal(1f, v.Z));
        Assert.Contains(vertices, v => v.X == 10 && v.Y == 20);
        Assert.Contains(vertices, v => v.X == 30 && v.Y == 40);
        Assert.Contains(vertices, v => v.X == 30 && v.Y == 20);
        Assert.Contains(vertices, v => v.X == 10 && v.Y == 40);
        Assert.Equal(Vertices + 24, _engine.VertexAddress);
    }

    /// <summary>Flat shading (SHADEMODE 0) gives a triangle its last vertex's colour; Gouraud keeps each.</summary>
    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, false)]
    public void FlatShadingTakesTheLastVertexsColour(uint shadeMode, bool flat)
    {
        ThroughVertex(0, 0, 0, 0, 0xFF0000FF);
        ThroughVertex(1, 10, 0, 0, 0xFF00FF00);
        ThroughVertex(2, 0, 10, 0, 0xFFFF0000);
        GeRegisters.Run(_engine, Setup().Command(GeCommand.ShadeMode, shadeMode).Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.Primitive, (3u << 16) | 3).End().Start);
        GeVertex[] vertices = Assert.Single(_rasterizer.Draws).Vertices;
        Assert.Equal(flat ? 0xFFFF0000u : 0xFF0000FFu, vertices[0].Color);
        Assert.Equal(0xFFFF0000u, vertices[2].Color);
    }

    [Fact]
    public void StripsAndFansBecomeTriangleLists()
    {
        for (int i = 0; i < 5; i++)
        {
            ThroughVertex(i, (short)(i * 10), (short)(i % 2 * 10), 0, 0xFFFFFFFF);
        }

        GeRegisters.Run(_engine, Setup().Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.Primitive, (4u << 16) | 5)
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF)
            .Command(GeCommand.Primitive, (5u << 16) | 5).End().Start);
        Assert.Equal(2, _rasterizer.Draws.Count);
        Assert.Equal(9, _rasterizer.Draws[0].Vertices.Length);
        Assert.Equal(9, _rasterizer.Draws[1].Vertices.Length);
        GeVertex[] strip = _rasterizer.Draws[0].Vertices;
        Assert.Equal((20f, 0f), (strip[3].X, strip[3].Y)); // the second triangle (1, 2, 3) swaps its first two: (2, 1, 3)
        Assert.Equal((0f, 0f), (_rasterizer.Draws[1].Vertices[6].X, _rasterizer.Draws[1].Vertices[6].Y)); // a fan pivots on vertex 0
    }

    /// <summary>A transformed vertex goes through world, view, projection and the viewport, minus the screen offset.</summary>
    [Fact]
    public void ATransformedVertexLandsWhereTheViewportPutsIt()
    {
        const uint FloatPosition = 3u << 7;
        _memory.WriteFloat(Vertices, 0.5f);
        _memory.WriteFloat(Vertices + 4, -0.5f);
        _memory.WriteFloat(Vertices + 8, 0f);
        GeListBuilder list = Setup().Command(GeCommand.VertexType, FloatPosition);
        LoadIdentity(list, GeCommand.WorldMatrixNumber, GeCommand.WorldMatrixData, 12);
        LoadIdentity(list, GeCommand.ViewMatrixNumber, GeCommand.ViewMatrixData, 12);
        LoadIdentity(list, GeCommand.ProjectionMatrixNumber, GeCommand.ProjectionMatrixData, 16);
        list.Float(GeCommand.ViewportXScale, 240).Float(GeCommand.ViewportYScale, -136).Float(GeCommand.ViewportZScale, -32767.5f)
            .Float(GeCommand.ViewportXCenter, 2048).Float(GeCommand.ViewportYCenter, 2048).Float(GeCommand.ViewportZCenter, 32767.5f)
            .Command(GeCommand.OffsetX, (2048 - 240) * 16).Command(GeCommand.OffsetY, (2048 - 136) * 16)
            .Command(GeCommand.Primitive, 1).End();
        GeRegisters.Run(_engine, list.Start);
        GeVertex vertex = Assert.Single(Assert.Single(_rasterizer.Draws).Vertices);
        Assert.Equal(360f, vertex.X, 3);
        Assert.Equal(204f, vertex.Y, 3);
        Assert.Equal(0.5f, vertex.Z, 3);
        Assert.Equal(1f, vertex.W, 3);
    }

    private static void LoadIdentity(GeListBuilder list, int number, int data, int count)
    {
        list.Command(number, 0);
        int columns = count == 16 ? 4 : 3;
        for (int i = 0; i < count; i++)
        {
            list.Float(data, i / columns == i % columns ? 1f : 0f);
        }
    }

    /// <summary>With CULLFACEENABLE a triangle of the wrong winding is dropped; CULL bit 0 picks the kept winding.</summary>
    [Theory]
    [InlineData(0u, 3)]
    [InlineData(1u, 0)]
    public void CullingDropsTheOtherWinding(uint cull, int kept)
    {
        const uint FloatPosition = 3u << 7;
        float[] corners = [-0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f]; // clockwise as seen on screen (y down)
        for (int i = 0; i < 3; i++)
        {
            _memory.WriteFloat(Vertices + (uint)(i * 12), corners[i * 2]);
            _memory.WriteFloat(Vertices + (uint)(i * 12) + 4, corners[i * 2 + 1]);
            _memory.WriteFloat(Vertices + (uint)(i * 12) + 8, 0);
        }

        GeListBuilder list = Setup().Command(GeCommand.VertexType, FloatPosition)
            .Command(GeCommand.CullFaceEnable, 1).Command(GeCommand.Cull, cull);
        LoadIdentity(list, GeCommand.WorldMatrixNumber, GeCommand.WorldMatrixData, 12);
        LoadIdentity(list, GeCommand.ViewMatrixNumber, GeCommand.ViewMatrixData, 12);
        LoadIdentity(list, GeCommand.ProjectionMatrixNumber, GeCommand.ProjectionMatrixData, 16);
        // centred in the GE's 4096-pixel screen space: a vertex left of or above it is out of range and drops the triangle
        list.Float(GeCommand.ViewportXScale, 100).Float(GeCommand.ViewportYScale, 100).Float(GeCommand.ViewportZScale, 1)
            .Float(GeCommand.ViewportXCenter, 2048).Float(GeCommand.ViewportYCenter, 2048)
            .Command(GeCommand.Primitive, (3u << 16) | 3).End();
        GeRegisters.Run(_engine, list.Start);
        int drawn = _rasterizer.Draws.Count == 0 ? 0 : _rasterizer.Draws[0].Vertices.Length;
        Assert.Equal(kept, drawn);
    }

    /// <summary>The state the draw carries, from the command registers.</summary>
    [Fact]
    public void TheDrawCarriesTheStateOfTheCommands()
    {
        ThroughVertex(0, 0, 0, 0, 0xFFFFFFFF);
        ThroughVertex(1, 4, 4, 0, 0xFFFFFFFF);
        GeRegisters.Run(_engine, Setup().Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.ClearMode, 1 | 0x100 | 0x400)
            .Command(GeCommand.AlphaBlendEnable, 1).Command(GeCommand.BlendMode, 2 | (3 << 4) | (1 << 8))
            .Command(GeCommand.BlendFixedA, 0x112233).Command(GeCommand.DepthTestEnable, 1).Command(GeCommand.DepthTest, 6)
            .Command(GeCommand.DepthWriteDisable, 1).Command(GeCommand.AlphaTestEnable, 1).Command(GeCommand.AlphaTest, 5 | (0x80 << 8) | (0xFF << 16))
            .Command(GeCommand.MaskRgb, 0x0000FF).Command(GeCommand.TextureFunction, 3 | 0x100 | 0x10000)
            .Command(GeCommand.Primitive, (6u << 16) | 2).End().Start);
        GeRenderState state = _rasterizer.LastState!;
        Assert.True(state.ClearMode && state.ClearColor && !state.ClearAlpha && state.ClearDepth);
        Assert.True(state.Blend);
        Assert.Equal((2, 3, 1), (state.BlendSource, state.BlendDestination, state.BlendEquation));
        Assert.Equal(0x112233u, state.BlendFixedA);
        Assert.True(state.DepthTest);
        Assert.Equal(6, state.DepthFunction);
        Assert.False(state.DepthWrite);
        Assert.Equal((5, 0x80, 0xFF), (state.AlphaFunction, state.AlphaReference, state.AlphaMask));
        Assert.Equal(0xFFu, state.WriteMaskRgb);
        Assert.Equal((3, true, true), (state.TextureFunction, state.TextureAlpha, state.ColorDouble));
        Assert.Equal((0, 0, 479, 271), (state.ScissorLeft, state.ScissorTop, state.ScissorRight, state.ScissorBottom));
        Assert.NotNull(_rasterizer.LastTarget!.Depth); // a clear that writes depth reads the depth buffer
    }

    /// <summary>As on the PSP, the depth buffer is written only while the depth test is on: with the test off (and
    /// depth writes not disabled) the draw neither writes nor reads depth.</summary>
    [Fact]
    public void DepthWritesNeedTheDepthTest()
    {
        ThroughVertex(0, 0, 0, 0, 0xFFFFFFFF);
        ThroughVertex(1, 4, 4, 0, 0xFFFFFFFF);
        GeRegisters.Run(_engine, Setup().Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.DepthTestEnable, 0).Command(GeCommand.DepthWriteDisable, 0)
            .Command(GeCommand.Primitive, (6u << 16) | 2).End().Start);
        Assert.False(_rasterizer.LastState!.DepthWrite);
        Assert.Null(_rasterizer.LastTarget!.Depth);
    }

    /// <summary>The framebuffer goes to the rasterizer as RGBA8 and comes back in its own format (565 here).</summary>
    [Fact]
    public void TheFramebufferIsConvertedForTheRasterizerAndBack()
    {
        _memory.Write16(GeTestMemory.VramBase + 2, GePixels.To565(0x0000FF00)); // pixel (1, 0): green
        _rasterizer.Paint = target =>
        {
            Assert.Equal(GePixels.From565(GePixels.To565(0x0000FF00)), target.Color[1]);
            target.Color[512 * 3 + 2] = 0x000000FF; // pixel (2, 3): red
        };
        ThroughVertex(0, 0, 0, 0, 0xFFFFFFFF);
        ThroughVertex(1, 4, 4, 0, 0xFFFFFFFF);
        GeRegisters.Run(_engine, Setup(GePixels.Format565).Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.Primitive, (6u << 16) | 2).End().Start);
        Assert.Equal(GePixels.To565(0x000000FF), _memory.Read16(GeTestMemory.VramBase + (512 * 3 + 2) * 2));
        Assert.Equal(GePixels.To565(0x0000FF00), _memory.Read16(GeTestMemory.VramBase + 2));
    }

    private void TextureSetup(GeListBuilder list, int format, int log2Width, int log2Height, int bufferWidth, bool swizzled = false)
    {
        list.Command(GeCommand.TextureMapEnable, 1)
            .Command(GeCommand.TextureAddress0, Texture & 0xFFFFFF)
            .Command(GeCommand.TextureBufferWidth0, (uint)bufferWidth | ((Texture >> 8) & 0x0F0000))
            .Command(GeCommand.TextureSize0, (uint)(log2Width | (log2Height << 8)))
            .Command(GeCommand.TextureFormat, (uint)format)
            .Command(GeCommand.TextureMode, swizzled ? 1u : 0u)
            .Command(GeCommand.VertexType, ThroughTextureS16);
        _memory.Write16(Vertices, 0);
        _memory.Write16(Vertices + 2, 0);
        _memory.Write16(Vertices + 4, 0);
        _memory.Write16(Vertices + 6, 0);
        _memory.Write16(Vertices + 8, 0);
        _memory.Write16(Vertices + 10, (ushort)(1 << log2Width));
        _memory.Write16(Vertices + 12, (ushort)(1 << log2Height));
        _memory.Write16(Vertices + 14, (ushort)(1 << log2Width));
        _memory.Write16(Vertices + 16, (ushort)(1 << log2Height));
        list.Command(GeCommand.Primitive, (6u << 16) | 2).End();
    }

    [Fact]
    public void A8888TextureIsDecodedAndTheThroughCoordinatesNormalised()
    {
        for (int i = 0; i < 16; i++)
        {
            _memory.Write32(Texture + (uint)(i * 4), 0xFF000000u | (uint)i);
        }

        GeListBuilder list = Setup();
        TextureSetup(list, GePixels.Format8888, 2, 2, 4);
        GeRegisters.Run(_engine, list.Start);
        GeRenderState state = _rasterizer.LastState!;
        Assert.True(state.Texture);
        Assert.Equal((4, 4), (state.TextureWidth, state.TextureHeight));
        Assert.Equal(0xFF000005u, state.TexturePixels[5]);
        GeVertex[] vertices = Assert.Single(_rasterizer.Draws).Vertices;
        Assert.Contains(vertices, v => v.U == 1 && v.V == 1);
        Assert.Contains(vertices, v => v.U == 0 && v.V == 0);
    }

    /// <summary>CLUT8 through LOADCLUT and CLUTFORMAT (shift, mask, start; 32-bit entries); CLUT4's low nibble is
    /// the even texel.</summary>
    [Fact]
    public void IndexedTexturesGoThroughTheLoadedClut()
    {
        for (int i = 0; i < 64; i++)
        {
            _memory.Write32(Clut + (uint)(i * 4), 0xAA000000u | (uint)i);
        }

        _memory.Write32(Texture, 0x03020100); // CLUT8 indices 0, 1, 2, 3
        GeListBuilder list = Setup()
            .Command(GeCommand.ClutAddress, Clut & 0xFFFFFF)
            .Command(GeCommand.ClutAddressUpper, (Clut >> 8) & 0x0F0000)
            .Command(GeCommand.LoadClut, 8) // 8 blocks of 32 bytes: 64 entries
            .Command(GeCommand.ClutFormat, 3 | (1 << 2) | (0xFF << 8) | (1 << 16)); // 8888, index >> 1, start 16
        TextureSetup(list, 5, 2, 0, 4);
        GeRegisters.Run(_engine, list.Start);
        uint[] pixels = _rasterizer.LastState!.TexturePixels;
        Assert.Equal(0xAA000010u, pixels[0]); // (0 >> 1) | 16
        Assert.Equal(0xAA000010u, pixels[1]); // (1 >> 1) | 16
        Assert.Equal(0xAA000011u, pixels[2]); // (2 >> 1) | 16

        _rasterizer.Draws.Clear();
        _memory.Write32(Texture, 0x00000021); // CLUT4: texel 0 = 1, texel 1 = 2
        GeListBuilder clut4 = new GeListBuilder(_memory, 0x08002000).RamBase()
            .Command(GeCommand.ClutFormat, 3 | (0xFF << 8))
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF);
        TextureSetup(clut4, 4, 2, 0, 4);
        GeRegisters.Run(_engine, clut4.Start);
        Assert.Equal(0xAA000001u, _rasterizer.LastState!.TexturePixels[0]);
        Assert.Equal(0xAA000002u, _rasterizer.LastState!.TexturePixels[1]);
    }

    /// <summary>A swizzled texture is read in 16-byte x 8-row blocks.</summary>
    [Fact]
    public void ASwizzledTextureIsReadInBlocks()
    {
        // 8x8 8888 texture, buffer width 8 (32 bytes a row: two blocks across). Texel (4, 1) is byte 16 of row 1:
        // block 1, row 1 of the block: offset 128 + 16.
        _memory.Write32(Texture + 128 + 16, 0x12345678);
        GeListBuilder list = Setup();
        TextureSetup(list, GePixels.Format8888, 3, 3, 8, swizzled: true);
        GeRegisters.Run(_engine, list.Start);
        Assert.Equal(0x12345678u, _rasterizer.LastState!.TexturePixels[1 * 8 + 4]);
    }

    [Fact]
    public void ABlockTransferCopiesTheRectangle()
    {
        const uint Source = 0x08040000, Destination = 0x04000000;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 8; column++)
            {
                _memory.Write32(Source + (uint)((row * 16 + column) * 4), (uint)(row << 8 | column));
            }
        }

        GeRegisters.Run(_engine, new GeListBuilder(_memory, ListAddress)
            .Command(GeCommand.TransferSource, Source & 0xFFFFFF).Command(GeCommand.TransferSourceWidth, 16 | ((Source >> 8) & 0x0F0000))
            .Command(GeCommand.TransferDestination, Destination & 0xFFFFFF).Command(GeCommand.TransferDestinationWidth, 512 | ((Destination >> 8) & 0x0F0000))
            .Command(GeCommand.TransferSourcePosition, 2 | (1u << 10)).Command(GeCommand.TransferDestinationPosition, 10 | (20u << 10))
            .Command(GeCommand.TransferSize, 2 | (1u << 10)) // 3 x 2
            .Command(GeCommand.TransferStart, 1).End().Start);
        Assert.Equal((1u << 8) | 2, _memory.Read32(Destination + (20 * 512 + 10) * 4));
        Assert.Equal((2u << 8) | 4, _memory.Read32(Destination + (21 * 512 + 12) * 4));
        Assert.Equal(0u, _memory.Read32(Destination + (21 * 512 + 13) * 4));
    }

    /// <summary>A GE without a rasterizer makes one from its factory at its first batch, once; a list that draws
    /// nothing never asks for it.</summary>
    [Fact]
    public void TheRasterizerIsMadeAtTheFirstBatchOnce()
    {
        int made = 0;
        GeCapturingRasterizer rasterizer = new();
        GeEngine engine = new(_memory)
        {
            RasterizerFactory = () =>
            {
                made++;
                return rasterizer;
            },
        };
        GeRegisters.Run(engine, Setup().Command(GeCommand.VertexType, ThroughColorS16).End().Start);
        Assert.Equal(0, made);

        ThroughVertex(0, 0, 0, 0, 0xFFFFFFFF);
        ThroughVertex(1, 4, 4, 0, 0xFFFFFFFF);
        GeRegisters.Run(engine, Setup().Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.Primitive, (6u << 16) | 2)
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF)
            .Command(GeCommand.Primitive, (6u << 16) | 2).End().Start);
        Assert.Equal(1, made);
        Assert.Equal(2, rasterizer.Draws.Count);
        Assert.Same(rasterizer, engine.Rasterizer);
    }

    /// <summary>ReleaseRasterizer leaves a rasterizer set from outside alone (it is its setter's).</summary>
    [Fact]
    public void ReleaseKeepsARasterizerSetFromOutside()
    {
        _engine.ReleaseRasterizer();
        Assert.Same(_rasterizer, _engine.Rasterizer);
    }

    /// <summary>A 565 texel has no alpha bits and reads as opaque (as the PSP samples it), not as transparent.</summary>
    [Fact]
    public void A565TextureIsOpaque()
    {
        _memory.Write16(Texture, GePixels.To565(0x000000FF));
        uint[] pixels = new uint[1];
        Assert.True(GeTextureDecoder.Decode(_memory, Texture, 4, 1, 1, GePixels.Format565, false, new uint[256], 0, pixels));
        Assert.Equal(0xFF0000FFu, pixels[0]);
    }

    [Theory]
    [InlineData(0xFF0000FFu)]
    [InlineData(0x80FF8040u)]
    [InlineData(0x00000000u)]
    public void PixelFormatsRoundTripTheirTopBits(uint rgba)
    {
        Assert.Equal(GePixels.To565(rgba), GePixels.To565(GePixels.From565(GePixels.To565(rgba))));
        Assert.Equal(GePixels.To5551(rgba), GePixels.To5551(GePixels.From5551(GePixels.To5551(rgba))));
        Assert.Equal(GePixels.To4444(rgba), GePixels.To4444(GePixels.From4444(GePixels.To4444(rgba))));
        Assert.Equal(0xFFu, GePixels.From565(0x001F) & 0xFF);
    }
}
