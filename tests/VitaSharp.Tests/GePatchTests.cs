using VitaSharp.Core.Compat.Ge;

namespace VitaSharp.Tests;

/// <summary>
/// The GE's Bezier patches and splines (the GE's parameter and fixed-point lerps, tessellation, generated UVs,
/// colours and normals, the triangle/line/point output), the bounding box test with BJUMP, and the 24-bit arithmetic
/// of the transform and viewport. Synthetic lists and vertices only.
/// </summary>
public sealed class GePatchTests
{
    private const uint ListAddress = 0x08001000, Vertices = 0x08010000, Skipped = 0x08004000;
    private const uint Through = 1u << 23;
    private const uint FloatPosition = 3u << 7;

    private readonly GeTestMemory _memory = new();
    private readonly GeEngine _engine;
    private readonly GeCapturingRasterizer _rasterizer = new();

    public GePatchTests()
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
            .Command(GeCommand.Region1, 0)
            .Command(GeCommand.Region2, 479 | (271u << 10))
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF)
            .Command(GeCommand.VertexType, vertexType)
            .Command(GeCommand.ShadeMode, 1);
        Load(list, GeCommand.WorldMatrixNumber, GeCommand.WorldMatrixData, 12);
        Load(list, GeCommand.ViewMatrixNumber, GeCommand.ViewMatrixData, 12);
        Load(list, GeCommand.ProjectionMatrixNumber, GeCommand.ProjectionMatrixData, 16);
        return list.Float(GeCommand.ViewportXScale, 100).Float(GeCommand.ViewportYScale, 100).Float(GeCommand.ViewportZScale, 10000)
            .Float(GeCommand.ViewportXCenter, 2048).Float(GeCommand.ViewportYCenter, 2048).Float(GeCommand.ViewportZCenter, 30000)
            .Command(GeCommand.OffsetX, (2048 - 200) * 16).Command(GeCommand.OffsetY, (2048 - 200) * 16)
            .Float(GeCommand.TexScaleU, 1).Float(GeCommand.TexScaleV, 1);
    }

    private static void Load(GeListBuilder list, int number, int data, int count)
    {
        int columns = count == 16 ? 4 : 3;
        list.Command(number, 0);
        for (int i = 0; i < count; i++)
        {
            list.Float(data, i / columns == i % columns ? 1f : 0f);
        }
    }

    /// <summary>A 4 x 4 grid of float positions (and an 8888 colour first when <paramref name="colors"/> is given).</summary>
    private void Grid(float scale, uint[]? colors = null)
    {
        int stride = colors == null ? 12 : 16;
        int offset = colors == null ? 0 : 4;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                uint address = Vertices + (uint)(((row * 4) + column) * stride);
                if (colors != null)
                {
                    _memory.Write32(address, colors[row]);
                }

                _memory.WriteFloat(address + (uint)offset, column * scale);
                _memory.WriteFloat(address + (uint)offset + 4, row * scale);
                _memory.WriteFloat(address + (uint)offset + 8, 0);
            }
        }
    }

    private GeVertex[] Run(GeListBuilder list)
    {
        _rasterizer.Draws.Clear();
        GeRegisters.Run(_engine, list.End().Start);
        return _rasterizer.Draws.Count == 0 ? [] : _rasterizer.Draws[0].Vertices;
    }

    // ---- the GE's patch arithmetic

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(1, 4, 64)]
    [InlineData(2, 4, 128)]
    [InlineData(4, 4, 256)]
    [InlineData(1, 3, 85)]
    [InlineData(2, 3, 171)]   // past the middle, rounded from the far end: 256 - 85
    public void ParameterRoundsTowardsTheMiddle(int index, int steps, int expected)
    {
        Assert.Equal(expected, GeEngine.PatchParameter(index, steps));
    }

    [Fact]
    public void PatchLerpIsSixteenBitFixedPoint()
    {
        Assert.Equal(0.5f, GeEngine.PatchLerp(0, 1, 128));
        Assert.Equal(1.25f, GeEngine.PatchLerp(1, 2, 64));
        Assert.Equal(7f, GeEngine.PatchLerp(7, 9, 0));
        Assert.Equal(9f, GeEngine.PatchLerp(7, 9, 256));
        // both operands truncated to 16 bits at the larger exponent: a step of 2^-20 is lost
        Assert.Equal(1f, GeEngine.PatchLerp(1, 1 + MathF.Pow(2, -20), 128));
    }

    // ---- Bezier

    [Fact]
    public void ABezierPatchWithOneDivisionIsItsCornerQuad()
    {
        Grid(10);
        GeVertex[] vertices = Run(Setup(Through | FloatPosition).Command(GeCommand.PatchDivision, 1 | (1u << 8))
            .Command(GeCommand.Bezier, 4 | (4u << 8)));
        Assert.Equal(6, vertices.Length);
        Assert.Equal([(0f, 0f), (0f, 30f), (30f, 0f), (30f, 0f), (0f, 30f), (30f, 30f)], vertices.Select(v => (v.X, v.Y)));
        Assert.Equal(Vertices + (16 * 12), _engine.VertexAddress);
    }

    [Fact]
    public void ABezierPatchSubdividesAtTheGeParameter()
    {
        Grid(10);
        GeVertex[] vertices = Run(Setup(Through | FloatPosition).Command(GeCommand.PatchDivision, 2 | (2u << 8))
            .Command(GeCommand.Bezier, 4 | (4u << 8)));
        Assert.Equal(4 * 6, vertices.Length);
        Assert.Contains(vertices, v => v.X == 15 && v.Y == 15); // the middle of a linear grid
    }

    [Fact]
    public void ColoursAreLerpedOnFifteenBits()
    {
        Grid(10, [0xFF0000FF, 0xFF0000FF, 0xFFFF0000, 0xFFFF0000]); // red rows, then blue rows
        GeVertex[] vertices = Run(Setup(Through | (7u << 2) | FloatPosition).Command(GeCommand.PatchDivision, 2 | (2u << 8))
            .Command(GeCommand.Bezier, 4 | (4u << 8)));
        GeVertex middle = vertices.First(v => v.Y == 15);
        Assert.Equal(0xFF800080u, middle.Color);
    }

    [Fact]
    public void WithoutUvsThePatchParameterIsTheTextureCoordinate()
    {
        Grid(0.1f);
        GeVertex[] vertices = Run(Setup(FloatPosition).Command(GeCommand.PatchDivision, 1 | (1u << 8))
            .Command(GeCommand.Bezier, 4 | (4u << 8)));
        GeVertex farCorner = vertices.Last();
        Assert.Equal((1f, 1f), (farCorner.U, farCorner.V));
        Assert.Equal((0f, 0f), (vertices[0].U, vertices[0].V));
    }

    [Theory]
    [InlineData(0u, 0xFFFFFFFFu)]  // the tangents' cross product faces +z: lit by a light along +z
    [InlineData(1u, 0xFF000000u)]  // PATCHFACING reverses it
    public void TheGeneratedNormalFollowsPatchFacing(uint facing, uint expected)
    {
        Grid(0.1f);
        GeListBuilder list = Setup(FloatPosition).Command(GeCommand.PatchDivision, 1 | (1u << 8)).Command(GeCommand.PatchFacing, facing)
            .Command(GeCommand.LightingEnable, 1).Command(GeCommand.LightEnable0, 1).Float(GeCommand.LightPosition0 + 2, 1)
            .Command(GeCommand.LightColor0 + 1, 0xFFFFFF).Command(GeCommand.MaterialDiffuse, 0xFFFFFF)
            .Command(GeCommand.MaterialAmbient, 0).Command(GeCommand.MaterialAlpha, 0xFF).Command(GeCommand.AmbientAlpha, 0xFF)
            .Command(GeCommand.Bezier, 4 | (4u << 8));
        Assert.All(Run(list), v => Assert.Equal(expected, v.Color));
    }

    [Theory]
    [InlineData(1u, 6)]  // lines: left edge, the diagonal, and the last column's right edge
    [InlineData(2u, 6)]  // points: the triangle list's indices
    public void PatchPrimitiveLinesAndPoints(uint primitive, int count)
    {
        Grid(10);
        GeListBuilder list = Setup(Through | FloatPosition).Command(GeCommand.PatchDivision, 1 | (1u << 8))
            .Command(GeCommand.PatchPrimitive, primitive).Command(GeCommand.Bezier, 4 | (4u << 8));
        _rasterizer.Draws.Clear();
        GeRegisters.Run(_engine, list.End().Start);
        (GeTopology topology, GeVertex[] vertices) = Assert.Single(_rasterizer.Draws);
        Assert.Equal(primitive == 1 ? GeTopology.Lines : GeTopology.Points, topology);
        Assert.Equal(count, vertices.Length);
        if (primitive == 1)
        {
            Assert.Equal([(0f, 0f), (0f, 30f), (0f, 30f), (30f, 0f), (30f, 0f), (30f, 30f)], vertices.Select(v => (v.X, v.Y)));
        }
    }

    [Fact]
    public void FewerThanFourControlPointsDrawNothingButMoveTheAddress()
    {
        Grid(10);
        Assert.Empty(Run(Setup(Through | FloatPosition).Command(GeCommand.Bezier, 3 | (4u << 8))));
        Assert.Equal(Vertices + (12 * 12), _engine.VertexAddress);
    }

    // ---- splines

    [Fact]
    public void AnOpenSplinePassesThroughItsEndPoints()
    {
        Grid(10);
        GeVertex[] vertices = Run(Setup(Through | FloatPosition).Command(GeCommand.PatchDivision, 1 | (1u << 8))
            .Command(GeCommand.Spline, 4 | (4u << 8) | (3u << 16) | (3u << 18)));
        Assert.Equal(6, vertices.Length);
        Assert.Contains(vertices, v => v.X == 0 && v.Y == 0);
        Assert.Contains(vertices, v => v.X == 30 && v.Y == 30);
    }

    [Fact]
    public void AClosedSplineStartsInsideItsControlPolygon()
    {
        Grid(10);
        GeVertex[] vertices = Run(Setup(Through | FloatPosition).Command(GeCommand.PatchDivision, 1 | (1u << 8))
            .Command(GeCommand.Spline, 4 | (4u << 8)));
        // the uniform cubic B-spline of 0, 10, 20, 30 starts at (0 + 4 * 10 + 20) / 6 = 10
        Assert.Equal(10f, vertices[0].X, 1);
        Assert.Equal(10f, vertices[0].Y, 1);
    }

    // ---- bounding box

    private GeVertex[] BoundingBox(float x, int count, uint depthClamp = 0)
    {
        for (int i = 0; i < 8; i++)
        {
            _memory.WriteFloat(Vertices + (uint)(i * 12), x + (i * 0.1f));
            _memory.WriteFloat(Vertices + (uint)(i * 12) + 4, 0);
            _memory.WriteFloat(Vertices + (uint)(i * 12) + 8, 0);
        }

        // the marker point (drawn only when BJUMP does not branch)
        _memory.WriteFloat(Vertices + 0x100, 0);
        _memory.WriteFloat(Vertices + 0x104, 0);
        _memory.WriteFloat(Vertices + 0x108, 0);
        new GeListBuilder(_memory, Skipped).Command(GeCommand.Finish).Command(GeCommand.End);
        GeListBuilder list = Setup(FloatPosition).Command(GeCommand.DepthClampEnable, depthClamp)
            .Command(GeCommand.BoundingBox, (uint)count)
            .Command(GeCommand.BoundingBoxJump, Skipped & 0xFFFFFF)
            .Command(GeCommand.VertexAddress, (Vertices + 0x100) & 0xFFFFFF)
            .Command(GeCommand.Primitive, 1);
        return Run(list);
    }

    [Fact]
    public void AVisibleBoxDoesNotBranch()
    {
        Assert.Single(BoundingBox(0, 8));
    }

    [Fact]
    public void ABoxBeyondOneSideBranches()
    {
        Assert.Empty(BoundingBox(-30, 8));   // all far left of the region
    }

    [Fact]
    public void AnEmptyBoxBranches()
    {
        Assert.Empty(BoundingBox(0, 0));
    }

    [Fact]
    public void TheBoxMovesTheVertexAddressPastItsVertices()
    {
        for (int i = 0; i < 9; i++)
        {
            _memory.WriteFloat(Vertices + (uint)(i * 12), i == 8 ? 0.5f : 0);
            _memory.WriteFloat(Vertices + (uint)(i * 12) + 4, i == 8 ? 0.5f : 0);
            _memory.WriteFloat(Vertices + (uint)(i * 12) + 8, 0);
        }

        // the point drawn after the box is vertex 8
        GeVertex point = Assert.Single(Run(Setup(FloatPosition).Command(GeCommand.BoundingBox, 8).Command(GeCommand.Primitive, 1)));
        Assert.Equal((250f, 250f), (point.X, point.Y));
    }

    // ---- 24-bit arithmetic

    [Fact]
    public void Float24KeepsFifteenMantissaBits()
    {
        Assert.Equal(1f, GeFloat24.Truncate(1 + MathF.Pow(2, -20)));
        Assert.Equal(1f + MathF.Pow(2, -15), GeFloat24.Truncate(1 + MathF.Pow(2, -15)));
        Assert.Equal(1f, GeFloat24.Add(1, 3 * MathF.Pow(2, -24))); // no guard bits: the small term vanishes
        Assert.Equal(0.5f, GeFloat24.Reciprocal(2));
        Assert.InRange(GeFloat24.Reciprocal(3), (1f / 3) - MathF.Pow(2, -17), (1f / 3) + MathF.Pow(2, -17));
    }

    [Fact]
    public void TheClipPositionUsesTwentyFourBitOperands()
    {
        Span<float> combined = stackalloc float[16];
        float[] identity43 = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        float[] identity44 = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        GeFloat24.CombineMatrices(identity43, identity43, identity44, combined);
        Assert.Equal(1f, combined[0]);
        Assert.Equal(1f, combined[15]);
        Assert.Equal(1f, GeFloat24.ClipComponent(new System.Numerics.Vector3(1 + MathF.Pow(2, -20), 0, 0), combined, 0));
    }

    [Theory]
    [InlineData(0.123456f)]
    [InlineData(-0.777777f)]
    [InlineData(0.333333f)]
    public void TheViewportLandsOnTheSixteenthPixelGrid(float clipX)
    {
        float x = GeFloat24.Viewport(clipX, 1, 240, 2048);
        Assert.Equal(MathF.Floor(x * 16), x * 16);
    }
}
