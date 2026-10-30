using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>
/// Programmable-blending draws split into pieces (Core/Gpu/FetchSplit.cs): triangle lists per triangle, triangle
/// strips per pair of triangles from an even index, degenerate triangles left out.
/// </summary>
public sealed class FetchSplitTests
{
    static byte[] U16(params ushort[] v)
    {
        var b = new byte[v.Length * 2];
        for (int i = 0; i < v.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), v[i]);
        return b;
    }

    static byte[] U32(params uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), v[i]);
        return b;
    }

    [Fact]
    public void List_OneTrianglePerPiece_SkipsDegenerates()
    {
        var pieces = new List<(uint, uint)>();
        Assert.True(FetchSplit.Pieces(U16(0, 1, 2, 2, 1, 3, 4, 4, 5, 4, 5, 6), false, false, pieces, 256));
        Assert.Equal([(0u, 3u), (3u, 3u), (9u, 3u)], pieces);
    }

    [Fact]
    public void Strip_OfQuadsJoinedByDegenerates_OnePiecePerQuad()
    {
        // three quads (0-3, 4-7, 8-11) joined by repeated indices, as particle systems emit them
        var pieces = new List<(uint, uint)>();
        Assert.True(FetchSplit.Pieces(U32(0, 1, 2, 3, 3, 4, 4, 5, 6, 7, 7, 8, 8, 9, 10, 11), true, true, pieces, 256));
        Assert.Equal([(0u, 4u), (6u, 4u), (12u, 4u)], pieces);
    }

    [Fact]
    public void Strip_PiecesStartAtEvenIndices_OddTailIsOneTriangle()
    {
        var pieces = new List<(uint, uint)>();
        Assert.True(FetchSplit.Pieces(U16(0, 1, 2, 3, 4, 5, 6), false, true, pieces, 256));
        Assert.Equal([(0u, 4u), (2u, 4u), (4u, 3u)], pieces);
    }

    [Fact]
    public void Strip_PieceWithOneRealTriangleIsKept()
    {
        // triangle 0 (0,0,1) is degenerate, triangle 1 (0,1,2) is not: the piece keeps its even start
        var pieces = new List<(uint, uint)>();
        Assert.True(FetchSplit.Pieces(U16(0, 0, 1, 2, 3, 4), false, true, pieces, 256));
        Assert.Equal([(0u, 4u), (2u, 4u)], pieces);
    }

    [Fact]
    public void SinglePieceOrTooManyPieces_DrawnWhole()
    {
        var pieces = new List<(uint, uint)>();
        Assert.False(FetchSplit.Pieces(U16(0, 1, 2, 3), false, true, pieces, 256)); // one quad
        Assert.False(FetchSplit.Pieces(U16(0, 1, 2, 3, 4, 5, 6, 7, 8), false, false, pieces, 2)); // three triangles, max 2
    }
}
