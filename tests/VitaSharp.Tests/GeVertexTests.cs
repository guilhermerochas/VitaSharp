using VitaSharp.Core.Compat.Ge;

namespace VitaSharp.Tests;

/// <summary>
/// The GE's vertex stage (GeVertexTransformer and the primitive clipper): VTYPE decoding in every format, morphing,
/// skinning, indices, through mode, the transform and viewport, the screen range and near-plane clipping, fog,
/// texture coordinate generation and lighting. Synthetic lists and vertices only.
/// Transform setup: identity matrices, viewport X = 100 x + 200, Y = 100 y + 200 (after the screen offset), depth
/// 10000 z + 30000.
/// </summary>
public sealed class GeVertexTests
{
    private const uint ListAddress = 0x08001000, Vertices = 0x08010000, Indices = 0x08018000;
    private const uint FloatPosition = 3u << 7;
    private const uint Through = 1u << 23;

    private readonly GeTestMemory _memory = new();
    private readonly GeEngine _engine;
    private readonly GeCapturingRasterizer _rasterizer = new();

    public GeVertexTests()
    {
        _engine = new GeEngine(_memory) { Rasterizer = _rasterizer };
    }

    private GeListBuilder Setup(uint vertexType)
    {
        GeListBuilder list = new GeListBuilder(_memory, ListAddress).RamBase()
            .Command(GeCommand.FrameBufferPointer, 0)
            .Command(GeCommand.FrameBufferWidth, 512)
            .Command(GeCommand.FrameBufferPixelFormat, (uint)GePixels.Format8888)
            .Command(GeCommand.Scissor1, 0)
            .Command(GeCommand.Scissor2, 479 | (271u << 10))
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF)
            .Command(GeCommand.IndexAddress, Indices & 0xFFFFFF)
            .Command(GeCommand.VertexType, vertexType)
            .Command(GeCommand.ShadeMode, 1);
        LoadMatrix(list, GeCommand.WorldMatrixNumber, GeCommand.WorldMatrixData, Identity(12));
        LoadMatrix(list, GeCommand.ViewMatrixNumber, GeCommand.ViewMatrixData, Identity(12));
        LoadMatrix(list, GeCommand.ProjectionMatrixNumber, GeCommand.ProjectionMatrixData, Identity(16));
        return list.Float(GeCommand.ViewportXScale, 100).Float(GeCommand.ViewportYScale, 100).Float(GeCommand.ViewportZScale, 10000)
            .Float(GeCommand.ViewportXCenter, 2048).Float(GeCommand.ViewportYCenter, 2048).Float(GeCommand.ViewportZCenter, 30000)
            .Command(GeCommand.OffsetX, (2048 - 200) * 16).Command(GeCommand.OffsetY, (2048 - 200) * 16)
            .Float(GeCommand.TexScaleU, 1).Float(GeCommand.TexScaleV, 1);
    }

    private static float[] Identity(int count)
    {
        int columns = count == 16 ? 4 : 3;
        float[] values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = i / columns == i % columns ? 1f : 0f;
        }

        return values;
    }

    private static void LoadMatrix(GeListBuilder list, int number, int data, float[] values, int start = 0)
    {
        list.Command(number, (uint)start);
        foreach (float value in values)
        {
            list.Float(data, value);
        }
    }

    private GeVertex[] Draw(GeListBuilder list, int primitive, int count)
    {
        _rasterizer.Draws.Clear();
        GeRegisters.Run(_engine, list.Command(GeCommand.Primitive, ((uint)primitive << 16) | (uint)count).End().Start);
        return _rasterizer.Draws.Count == 0 ? [] : _rasterizer.Draws[0].Vertices;
    }

    private void FloatVertex(int index, float x, float y, float z, int stride = 12, int positionOffset = 0)
    {
        uint address = Vertices + (uint)(index * stride) + (uint)positionOffset;
        _memory.WriteFloat(address, x);
        _memory.WriteFloat(address + 4, y);
        _memory.WriteFloat(address + 8, z);
    }

    // ---- decoding

    [Theory]
    [InlineData(1u, 3)]     // s8: x/128
    [InlineData(2u, 6)]     // s16: x/32768
    [InlineData(3u, 12)]    // float
    public void PositionFormatsScaleToTheirRange(uint format, int size)
    {
        uint address = Vertices;
        switch (format)
        {
            case 1:
                _memory.Span(address, 3)[0] = 64;        // 0.5
                _memory.Span(address, 3)[1] = 0xC0;      // -0.5
                _memory.Span(address, 3)[2] = 0;
                break;
            case 2:
                _memory.Write16(address, 16384);
                _memory.Write16(address + 2, unchecked((ushort)-16384));
                _memory.Write16(address + 4, 0);
                break;
            default:
                FloatVertex(0, 0.5f, -0.5f, 0);
                break;
        }

        Assert.Equal(size, new GeVertexFormat(format << 7).Size);
        GeVertex vertex = Assert.Single(Draw(Setup(format << 7), 0, 1));
        Assert.Equal(250f, vertex.X);
        Assert.Equal(150f, vertex.Y);
        Assert.Equal(30000f / 65535f, vertex.Z, 5);
        Assert.Equal(1f, vertex.W);
    }

    [Fact]
    public void ThroughPositionsAreScreenCoordinatesWithAnIntegerDepth()
    {
        // s16: x, y signed, z unsigned
        _memory.Write16(Vertices, unchecked((ushort)-5));
        _memory.Write16(Vertices + 2, 7);
        _memory.Write16(Vertices + 4, 0xFFFF);
        GeVertex s16 = Assert.Single(Draw(Setup(Through | (2u << 7)), 0, 1));
        Assert.Equal((-5f, 7f, 1f), (s16.X, s16.Y, s16.Z));

        // float: the depth truncated and clamped to 16 bits
        FloatVertex(0, 10.5f, 20.25f, 70000f);
        GeVertex single = Assert.Single(Draw(Setup(Through | FloatPosition), 0, 1));
        Assert.Equal((10.5f, 20.25f, 1f), (single.X, single.Y, single.Z));
        FloatVertex(0, 0, 0, 100.9f);
        Assert.Equal(100f / 65535f, Assert.Single(Draw(Setup(Through | FloatPosition), 0, 1)).Z, 6);

        // s8 decodes to 0 in through mode
        _memory.Span(Vertices, 3).Fill(0x40);
        GeVertex s8 = Assert.Single(Draw(Setup(Through | (1u << 7)), 0, 1));
        Assert.Equal((0f, 0f, 0f), (s8.X, s8.Y, s8.Z));
    }

    [Theory]
    [InlineData(4u, 0xF81Fu, 0xFFFF00FFu)]    // 565: magenta
    [InlineData(5u, 0x7C1Fu, 0x00FF00FFu)]    // 5551 without alpha
    [InlineData(6u, 0xF00Fu, 0xFF0000FFu)]    // 4444
    [InlineData(7u, 0x80402010u, 0x80402010u)] // 8888
    public void ColorFormatsExpandToRgba8(uint format, uint raw, uint expected)
    {
        uint vertexType = Through | (format << 2) | FloatPosition;
        GeVertexFormat layout = new(vertexType);
        if (format == 7)
        {
            _memory.Write32(Vertices + (uint)layout.ColorOffset, raw);
        }
        else
        {
            _memory.Write16(Vertices + (uint)layout.ColorOffset, (ushort)raw);
        }

        Assert.Equal(expected, Assert.Single(Draw(Setup(vertexType), 0, 1)).Color);
    }

    [Fact]
    public void WithoutAColourTheMaterialAmbientIsUsed()
    {
        FloatVertex(0, 0, 0, 0);
        GeListBuilder list = Setup(Through | FloatPosition).Command(GeCommand.MaterialAmbient, 0x332211).Command(GeCommand.MaterialAlpha, 0x44);
        Assert.Equal(0x44332211u, Assert.Single(Draw(list, 0, 1)).Color);
    }

    [Fact]
    public void ThroughTextureCoordinatesAreTexels()
    {
        uint vertexType = Through | 2u | FloatPosition; // u16 UVs
        GeVertexFormat layout = new(vertexType);
        _memory.Write16(Vertices, 32);
        _memory.Write16(Vertices + 2, 16);
        FloatVertex(0, 0, 0, 0, layout.Size, layout.PositionOffset);
        GeListBuilder list = Setup(vertexType).Command(GeCommand.TextureSize0, 6 | (5u << 8)); // 64 x 32
        GeVertex vertex = Assert.Single(Draw(list, 0, 1));
        Assert.Equal((0.5f, 0.5f), (vertex.U, vertex.V));
    }

    [Theory]
    [InlineData(1u, 64u, 0.5f)]       // u8 / 128
    [InlineData(2u, 16384u, 0.5f)]    // u16 / 32768
    public void TransformedTextureCoordinatesAreNormalisedThenScaledAndOffset(uint format, uint raw, float normalised)
    {
        uint vertexType = format | FloatPosition;
        GeVertexFormat layout = new(vertexType);
        if (format == 1)
        {
            _memory.Span(Vertices, 2)[0] = (byte)raw;
            _memory.Span(Vertices, 2)[1] = (byte)raw;
        }
        else
        {
            _memory.Write16(Vertices, (ushort)raw);
            _memory.Write16(Vertices + 2, (ushort)raw);
        }

        FloatVertex(0, 0, 0, 0, layout.Size, layout.PositionOffset);
        GeListBuilder list = Setup(vertexType).Float(GeCommand.TexScaleU, 2).Float(GeCommand.TexOffsetU, 0.25f)
            .Float(GeCommand.TexScaleV, 1).Float(GeCommand.TexOffsetV, 0);
        GeVertex vertex = Assert.Single(Draw(list, 0, 1));
        Assert.Equal((normalised * 2) + 0.25f, vertex.U);
        Assert.Equal(normalised, vertex.V);
        Assert.Equal(1f, vertex.Q);
    }

    [Fact]
    public void AFormatWithoutUvsKeepsTheLastOnesRead()
    {
        uint withUv = 3u | FloatPosition;
        GeVertexFormat layout = new(withUv);
        _memory.WriteFloat(Vertices, 0.75f);
        _memory.WriteFloat(Vertices + 4, 0.125f);
        FloatVertex(0, 0, 0, 0, layout.Size, layout.PositionOffset);
        Draw(Setup(withUv), 0, 1);
        FloatVertex(0, 0, 0, 0);
        GeVertex carried = Assert.Single(Draw(Setup(FloatPosition), 0, 1));
        Assert.Equal((0.75f, 0.125f), (carried.U, carried.V));
    }

    [Fact]
    public void MorphTargetsBlendByTheirWeights()
    {
        uint vertexType = (7u << 2) | FloatPosition | (1u << 18); // colour + position, two targets
        GeVertexFormat layout = new(vertexType);
        Assert.Equal(2, layout.MorphCount);
        _memory.Write32(Vertices, 0xFF000000);
        FloatVertex(0, 0, 0, 0, layout.Stride, layout.PositionOffset);
        _memory.Write32(Vertices + (uint)layout.Stride, 0xFF0000C8);
        FloatVertex(1, 1, 1, 0, layout.Stride, layout.PositionOffset);
        GeListBuilder list = Setup(vertexType).Float(GeCommand.MorphWeight0, 0.25f).Float(GeCommand.MorphWeight0 + 1, 0.75f);
        GeVertex vertex = Assert.Single(Draw(list, 0, 1));
        Assert.Equal(275f, vertex.X); // x = 0.75
        Assert.Equal(0xFF000096u, vertex.Color); // 200 * 0.75 = 150
    }

    [Fact]
    public void SkinningBlendsTheBoneMatrices()
    {
        uint vertexType = (3u << 9) | (1u << 14) | FloatPosition; // two float weights
        GeVertexFormat layout = new(vertexType);
        _memory.WriteFloat(Vertices, 0.5f);
        _memory.WriteFloat(Vertices + 4, 0.5f);
        FloatVertex(0, 0, 0, 0, layout.Size, layout.PositionOffset);
        GeListBuilder list = Setup(vertexType);
        float[] first = Identity(12);
        first[9] = 1;   // translate x by 1
        float[] second = Identity(12);
        second[10] = 1; // translate y by 1
        LoadMatrix(list, GeCommand.BoneMatrixNumber, GeCommand.BoneMatrixData, [.. first, .. second]);
        GeVertex vertex = Assert.Single(Draw(list, 0, 1));
        Assert.Equal((250f, 250f), (vertex.X, vertex.Y));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void IndexedVerticesAreReadThroughTheIndexBuffer(uint indexFormat)
    {
        FloatVertex(0, 0, 0, 0);
        FloatVertex(1, 1, 0, 0);
        FloatVertex(2, 0, 1, 0);
        int[] order = [2, 0, 1];
        for (int i = 0; i < 3; i++)
        {
            switch (indexFormat)
            {
                case 1:
                    _memory.Span(Indices + (uint)i, 1)[0] = (byte)order[i];
                    break;
                case 2:
                    _memory.Write16(Indices + (uint)(i * 2), (ushort)order[i]);
                    break;
                default:
                    _memory.Write32(Indices + (uint)(i * 4), (uint)order[i]);
                    break;
            }
        }

        GeVertex[] points = Draw(Setup(FloatPosition | (indexFormat << 11)), 0, 3);
        Assert.Equal([(200f, 300f), (200f, 200f), (300f, 200f)], points.Select(v => (v.X, v.Y)));
    }

    [Fact]
    public void PositionFormatZeroTakesRoomButIsNotDrawn()
    {
        Assert.Equal(8, new GeVertexFormat(7u << 2).Size); // colour + 3 bytes, aligned to 4
        Assert.True(new GeVertexFormat(0).PositionInvalid);
        Assert.Empty(Draw(Setup(7u << 2), 0, 1));
    }

    // ---- screen range and clipping

    [Fact]
    public void AVertexOutsideTheScreenRangeDropsItsPrimitive()
    {
        FloatVertex(0, 0, 0, 0);
        FloatVertex(1, 1, 0, 0);
        FloatVertex(2, 50, 1, 0);    // x = 5200: beyond 4096
        Assert.Empty(Draw(Setup(FloatPosition), 3, 3));
        FloatVertex(2, 0, 1, 0);
        Assert.Equal(3, Draw(Setup(FloatPosition), 3, 3).Length);
    }

    [Fact]
    public void PrimitivesOutsideOneClipPlaneAreDropped()
    {
        // all three beyond x = w (x > 1), still inside the screen range
        FloatVertex(0, 2, 0, 0);
        FloatVertex(1, 3, 0, 0);
        FloatVertex(2, 2, 1, 0);
        Assert.Empty(Draw(Setup(FloatPosition), 3, 3));
        // one inside: drawn (the rasterizer's scissor cuts the rest)
        FloatVertex(0, 0, 0, 0);
        Assert.Equal(3, Draw(Setup(FloatPosition), 3, 3).Length);
    }

    [Fact]
    public void WithDepthClampTheNearPlaneClipsATriangle()
    {
        FloatVertex(0, 0, 0, 0);
        FloatVertex(1, 1, 0, 0);
        FloatVertex(2, 0, 1, -3);     // z < -w: beyond the near plane
        GeVertex[] clipped = Draw(Setup(FloatPosition).Command(GeCommand.DepthClampEnable, 1), 3, 3);
        Assert.Equal(6, clipped.Length); // one vertex out: the quad left becomes two triangles
        // the new vertices sit on the near plane (z = -w = -1: depth 10000 * -1 + 30000)
        Assert.Contains(clipped, v => Math.Abs(v.Z - (20000f / 65535f)) < 1e-5f);
        Assert.DoesNotContain(clipped, v => v.Z < 20000f / 65535f - 1e-5f);

        FloatVertex(1, 1, 0, -3);
        Assert.Equal(3, Draw(Setup(FloatPosition).Command(GeCommand.DepthClampEnable, 1), 3, 3).Length); // two out: one triangle
    }

    [Fact]
    public void WithoutDepthClampAVertexBehindTheCameraDropsTheTriangle()
    {
        FloatVertex(0, 0, 0, 0);
        FloatVertex(1, 0.5f, 0, 0);
        FloatVertex(2, 0, 0.5f, 0);
        float[] projection = Identity(16);
        GeListBuilder list = Setup(FloatPosition);
        projection[15] = 1;
        projection[11] = 1; // w = z + 1
        LoadMatrix(list, GeCommand.ProjectionMatrixNumber, GeCommand.ProjectionMatrixData, projection);
        Assert.Equal(3, Draw(list, 3, 3).Length);
        FloatVertex(2, 0, 0.5f, -2); // w = -1
        list = Setup(FloatPosition);
        LoadMatrix(list, GeCommand.ProjectionMatrixNumber, GeCommand.ProjectionMatrixData, projection);
        Assert.Empty(Draw(list, 3, 3));
    }

    [Fact]
    public void ALineCrossingTheNearPlaneIsCutWithDepthClamp()
    {
        FloatVertex(0, 0, 0, 0);
        FloatVertex(1, 1, 0, -3);
        GeVertex[] line = Draw(Setup(FloatPosition).Command(GeCommand.DepthClampEnable, 1), 1, 2);
        Assert.Equal(2, line.Length);
        Assert.Equal(200f, line[0].X);
        // the near plane (z + w = 0) is a third of the way: x = 1/3, X = 233.33 snapped to the 1/16 pixel grid
        Assert.Equal(233.3125f, line[1].X);
    }

    // ---- fog, texture generation, lighting

    [Fact]
    public void FogFactorFromTheViewDepth()
    {
        FloatVertex(0, 0, 0, -0.5f);
        GeListBuilder list = Setup(FloatPosition).Command(GeCommand.FogEnable, 1).Float(GeCommand.Fog1, 1).Float(GeCommand.Fog2, 0.5f);
        Assert.Equal(0.25f, Assert.Single(Draw(list, 0, 1)).Fog); // (z + end) * slope
        Assert.Equal(1f, Assert.Single(Draw(Setup(FloatPosition).Command(GeCommand.FogEnable, 0), 0, 1)).Fog);
    }

    [Fact]
    public void TextureMatrixProjectsThePositionWithQ()
    {
        FloatVertex(0, 0.5f, 0.25f, 0);
        GeListBuilder list = Setup(FloatPosition).Command(GeCommand.TextureMapMode, 1); // texture matrix, from the position
        float[] matrix = Identity(12);
        matrix[11] = 2; // q = z + 2
        LoadMatrix(list, GeCommand.TexGenMatrixNumber, GeCommand.TexGenMatrixData, matrix);
        GeVertex vertex = Assert.Single(Draw(list, 0, 1));
        Assert.Equal((0.5f, 0.25f, 2f), (vertex.U, vertex.V, vertex.Q));
    }

    [Fact]
    public void EnvironmentMapCoordinatesFromTwoLights()
    {
        uint vertexType = (3u << 5) | FloatPosition; // float normal
        GeVertexFormat layout = new(vertexType);
        _memory.WriteFloat(Vertices + (uint)layout.NormalOffset, 0);
        _memory.WriteFloat(Vertices + (uint)layout.NormalOffset + 4, 0);
        _memory.WriteFloat(Vertices + (uint)layout.NormalOffset + 8, 1);
        FloatVertex(0, 0, 0, 0, layout.Size, layout.PositionOffset);
        GeListBuilder list = Setup(vertexType).Command(GeCommand.TextureMapMode, 2).Command(GeCommand.TextureShadeLights, 0 | (1u << 8))
            .Float(GeCommand.LightPosition0 + 2, 1)          // light 0 along +z: N.L = 1 → s = 1
            .Float(GeCommand.LightPosition0 + 3, 1);         // light 1 along +x: N.L = 0 → t = 0.5
        GeVertex vertex = Assert.Single(Draw(list, 0, 1));
        Assert.Equal((1f, 0.5f), (vertex.U, vertex.V));
    }

    private GeListBuilder LitSetup(uint vertexType)
    {
        return Setup(vertexType).Command(GeCommand.LightingEnable, 1).Command(GeCommand.LightEnable0, 1)
            .Command(GeCommand.MaterialDiffuse, 0xFFFFFF).Command(GeCommand.MaterialAmbient, 0).Command(GeCommand.MaterialAlpha, 0xFF)
            .Command(GeCommand.AmbientColor, 0).Command(GeCommand.AmbientAlpha, 0xFF)
            .Command(GeCommand.LightColor0 + 1, 0xFFFFFF);  // light 0 diffuse white
    }

    private void NormalVertex(uint vertexType, float nx, float ny, float nz, float x = 0, float y = 0, float z = 0)
    {
        GeVertexFormat layout = new(vertexType);
        _memory.WriteFloat(Vertices + (uint)layout.NormalOffset, nx);
        _memory.WriteFloat(Vertices + (uint)layout.NormalOffset + 4, ny);
        _memory.WriteFloat(Vertices + (uint)layout.NormalOffset + 8, nz);
        FloatVertex(0, x, y, z, layout.Size, layout.PositionOffset);
    }

    [Theory]
    [InlineData(1f, 0xFFFFFFFFu)]      // N.L = 1: full diffuse, alpha from the ambient alphas
    [InlineData(0.5f, 0xFF808080u)]    // N.L = 0.5: s = 128 → ((2*255+1) * 257) >> 10 = 128
    [InlineData(-1f, 0xFF000000u)]     // facing away: no diffuse
    public void DirectionalDiffuseLight(float normalZ, uint expected)
    {
        uint vertexType = (3u << 5) | FloatPosition;
        float nz = normalZ;
        float nx = normalZ == 0.5f ? MathF.Sqrt(0.75f) : 0;
        NormalVertex(vertexType, nx, 0, nz);
        GeListBuilder list = LitSetup(vertexType).Float(GeCommand.LightPosition0 + 2, 1); // directional along +z
        Assert.Equal(expected, Assert.Single(Draw(list, 0, 1)).Color);
    }

    [Fact]
    public void PointLightAttenuates()
    {
        uint vertexType = (3u << 5) | FloatPosition;
        NormalVertex(vertexType, 0, 0, 1);
        GeListBuilder list = LitSetup(vertexType).Command(GeCommand.LightType0, 1u << 8) // point
            .Float(GeCommand.LightPosition0 + 2, 2)                                   // at (0, 0, 2): distance 2
            .Float(GeCommand.LightAttenuation0 + 2, 1);                               // 1 / d^2 = 0.25
        // 255 scaled by 0.25: s = 64, ((2*255+1) * 129) >> 10 = 64
        Assert.Equal(0xFF404040u, Assert.Single(Draw(list, 0, 1)).Color);
    }

    [Fact]
    public void SpotLightOutsideItsConeGivesNothing()
    {
        uint vertexType = (3u << 5) | FloatPosition;
        NormalVertex(vertexType, 0, 0, 1);
        GeListBuilder list = LitSetup(vertexType).Command(GeCommand.LightType0, 2u << 8) // spot
            .Float(GeCommand.LightPosition0 + 2, 2).Float(GeCommand.LightAttenuation0, 1)
            .Float(GeCommand.LightDirection0 + 2, 1).Float(GeCommand.LightSpotCutoff0, 0.5f);
        // the GE compares the spot direction with the vertex-to-light vector (PPSSPP's Lighting.cpp)
        Assert.Equal(0xFFFFFFFFu, Assert.Single(Draw(list, 0, 1)).Color);
        list = LitSetup(vertexType).Command(GeCommand.LightType0, 2u << 8)
            .Float(GeCommand.LightPosition0 + 2, 2).Float(GeCommand.LightAttenuation0, 1)
            .Float(GeCommand.LightDirection0 + 2, -1).Float(GeCommand.LightSpotCutoff0, 0.5f);
        Assert.Equal(0xFF000000u, Assert.Single(Draw(list, 0, 1)).Color); // outside the cone
    }

    [Fact]
    public void SeparateSpecularGoesToColor1()
    {
        uint vertexType = (3u << 5) | FloatPosition;
        NormalVertex(vertexType, 0, 0, 1);
        float[] view = Identity(12); // the view direction is (view[2], view[5], view[8]) = (0, 0, 1)
        GeListBuilder list = LitSetup(vertexType).Command(GeCommand.LightType0, 1)   // directional, diffuse + specular
            .Float(GeCommand.LightPosition0 + 2, 1)
            .Command(GeCommand.LightColor0 + 1, 0).Command(GeCommand.LightColor0 + 2, 0xFFFFFF)
            .Command(GeCommand.MaterialSpecular, 0xFFFFFF).Float(GeCommand.MaterialSpecularCoefficient, 1)
            .Command(GeCommand.LightMode, 1);
        LoadMatrix(list, GeCommand.ViewMatrixNumber, GeCommand.ViewMatrixData, view);
        GeVertex vertex = Assert.Single(Draw(list, 0, 1));
        Assert.Equal(0x00FFFFFFu, vertex.Color1);
        Assert.Equal(0xFF000000u, vertex.Color);
    }

    [Fact]
    public void MaterialUpdateTakesTheVertexColour()
    {
        uint vertexType = (7u << 2) | (3u << 5) | FloatPosition;
        GeVertexFormat layout = new(vertexType);
        _memory.Write32(Vertices + (uint)layout.ColorOffset, 0xFF0000FF); // red
        NormalVertex(vertexType, 0, 0, 1);
        GeListBuilder list = LitSetup(vertexType).Float(GeCommand.LightPosition0 + 2, 1).Command(GeCommand.MaterialUpdate, 2);
        Assert.Equal(0xFF0000FFu, Assert.Single(Draw(list, 0, 1)).Color);
    }

    [Theory]
    [InlineData(0.5f, 2f, 0.25f)]
    [InlineData(0.5f, 0f, 1f)]
    [InlineData(-0.5f, 2f, -0.5f)]
    public void LightPowerFollowsTheGe(float value, float exponent, float expected)
    {
        Assert.Equal(expected, GeVertexTransformer.LightPower(value, exponent));
    }
}
