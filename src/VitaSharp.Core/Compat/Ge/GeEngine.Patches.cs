using System.Numerics;

namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// Bezier patches and splines (BEZIER, SPLINE) and the bounding box test (BOUNDINGBOX, for BJUMP). A patch is
/// tessellated as the GE evaluates it (PPSSPP's hardware probes gpu/probe exp48-51, exp139-145): the parameter is
/// k/256 rounded towards the middle of the patch, columns (v) are evaluated before rows (u) by de Casteljau (Bezier)
/// or de Boor (spline), each lerp in 16-bit fixed point at the larger operand's exponent, colours on 15 bits; without
/// UVs the coordinates are the parameter, without colour the first control point's, and the normal (with normals,
/// lighting or environment mapping) is the cross product of the two tangents. The vertices then go through the usual
/// vertex stage and clipper as triangles, lines (a zigzag per quad) or points (PATCHPRIMITIVE), PATCHFACING reversing
/// the normal.
/// </summary>
public sealed partial class GeEngine
{
    private const int MaxPatchVertices = 16384;

    private GeDecodedVertex[] _controlPoints = new GeDecodedVertex[64];
    private GeDecodedVertex[] _patchVertices = new GeDecodedVertex[256];
    private int[] _patchIndices = new int[1024];
    private PatchColumn[] _patchColumns = new PatchColumn[64];

    private struct PatchColumn
    {
        public Vector3 Position;
        public Vector3 First;   // de Casteljau's / de Boor's last two points, for the tangent
        public Vector3 Second;
        public float U;
        public float V;
        public int Red;
        public int Green;
        public int Blue;
        public int Alpha;
    }

    // ---- the GE's patch arithmetic

    /// <summary>Step <paramref name="index"/> of <paramref name="steps"/> as k in 1/256, rounded towards the middle.</summary>
    public static int PatchParameter(int index, int steps)
    {
        return 2 * index <= steps ? (256 * index) / steps : 256 - ((256 * (steps - index)) / steps);
    }

    /// <summary>
    /// a + (b - a) k / 256 as the GE lerps: both operands truncated towards zero to 16 bits at the larger exponent, the
    /// difference times k floored; k = 0 and 256 pass an operand through.
    /// </summary>
    public static float PatchLerp(float a, float b, int k)
    {
        if (k == 0)
        {
            return a;
        }

        if (k == 256)
        {
            return b;
        }

        uint bitsA = BitConverter.SingleToUInt32Bits(a);
        uint bitsB = BitConverter.SingleToUInt32Bits(b);
        int exponentA = (int)((bitsA >> 23) & 0xFF);
        int exponentB = (int)((bitsB >> 23) & 0xFF);
        int exponent = Math.Max(exponentA, exponentB);
        if (exponent == 0)
        {
            return 0;
        }

        int fixedA = ToFixed16(bitsA, exponentA, exponent);
        int fixedB = ToFixed16(bitsB, exponentB, exponent);
        int result = fixedA + (((fixedB - fixedA) * k) >> 8);
        return MathF.ScaleB(result, exponent - 127 - 15);
    }

    private static int ToFixed16(uint bits, int ownExponent, int exponent)
    {
        if (ownExponent == 0 || exponent - ownExponent >= 16)
        {
            return 0;
        }

        int magnitude = (int)(((bits & 0x7FFFFF) | 0x800000) >> (exponent - ownExponent + 8));
        return (bits & 0x80000000) != 0 ? -magnitude : magnitude;
    }

    private static int ColorLerp(int a, int b, int k)
    {
        return a + (((b - a) * k) >> 8);
    }

    /// <summary>The six blending factors of de Boor at one tessellation step, in 1/256, and the segment.</summary>
    private struct SplineStep
    {
        public int Segment;
        public int K;
        public int A0;
        public int A1;
        public int A2;
        public int A3;
        public int A4;
        public int A5;
    }

    /// <summary>
    /// A spline step: each factor (t - knot) / span from the nearer of its two knots, floor(d / span) from the left one,
    /// 256 - floor(d / span) from the right; an open end (type bit 0 first, bit 1 last) repeats its knot.
    /// </summary>
    private static SplineStep SplineStepAt(int index, int steps, int patches, int type)
    {
        SplineStep step = new() { Segment = index / steps, K = PatchParameter(index % steps, steps) };
        if (step.Segment == patches)
        {
            step.Segment = patches - 1;
            step.K = 256;
        }

        int knotLevel = step.Segment + 3;
        Span<int> knots = stackalloc int[6];
        Span<int> levels = stackalloc int[6];
        knots[0] = knotLevel - 2;
        knots[1] = knotLevel - 1;
        knots[2] = knotLevel;
        knots[3] = knotLevel - 1;
        knots[4] = knotLevel;
        knots[5] = knotLevel;
        levels[0] = 1;
        levels[1] = 1;
        levels[2] = 1;
        levels[3] = 2;
        levels[4] = 2;
        levels[5] = 3;
        Span<int> factors = stackalloc int[6];
        for (int n = 0; n < 6; n++)
        {
            int i = knots[n];
            int span = Knot(i + 4 - levels[n], patches, type) - Knot(i, patches, type);
            int left = (256 * (step.Segment - Knot(i, patches, type))) + step.K;
            int right = (256 * span) - left;
            factors[n] = span == 0 ? 0 : (left <= right ? left / span : 256 - (right / span));
        }

        step.A0 = factors[0];
        step.A1 = factors[1];
        step.A2 = factors[2];
        step.A3 = factors[3];
        step.A4 = factors[4];
        step.A5 = factors[5];
        return step;
    }

    private static int Knot(int i, int patches, int type)
    {
        if (i < 3 && (type & 1) != 0)
        {
            return 0;
        }

        if (i > patches + 3 && (type & 2) != 0)
        {
            return patches;
        }

        return i - 3;
    }

    private static float BezierEvaluate(float p0, float p1, float p2, float p3, int k, out float first, out float second)
    {
        float a = PatchLerp(p0, p1, k);
        float b = PatchLerp(p1, p2, k);
        float c = PatchLerp(p2, p3, k);
        first = PatchLerp(a, b, k);
        second = PatchLerp(b, c, k);
        return PatchLerp(first, second, k);
    }

    private static int BezierColor(int p0, int p1, int p2, int p3, int k)
    {
        int a = ColorLerp(p0, p1, k);
        int b = ColorLerp(p1, p2, k);
        int c = ColorLerp(p2, p3, k);
        return ColorLerp(ColorLerp(a, b, k), ColorLerp(b, c, k), k);
    }

    private static float SplineEvaluate(float d0, float d1, float d2, float d3, in SplineStep s, out float first, out float second)
    {
        float l0 = PatchLerp(d0, d1, s.A0);
        float l1 = PatchLerp(d1, d2, s.A1);
        float l2 = PatchLerp(d2, d3, s.A2);
        first = PatchLerp(l0, l1, s.A3);
        second = PatchLerp(l1, l2, s.A4);
        return PatchLerp(first, second, s.A5);
    }

    private static int SplineColor(int d0, int d1, int d2, int d3, in SplineStep s)
    {
        int l0 = ColorLerp(d0, d1, s.A0);
        int l1 = ColorLerp(d1, d2, s.A1);
        int l2 = ColorLerp(d2, d3, s.A2);
        return ColorLerp(ColorLerp(l0, l1, s.A3), ColorLerp(l1, l2, s.A4), s.A5);
    }

    private static int Channel(uint color, int channel)
    {
        return (int)(((color >> (channel * 8)) & 0xFF) << 7) | 0x7F;
    }

    // ---- drawing

    private partial void DrawPatchBatch(uint argument, bool spline, GeVertexFormat format)
    {
        int pointsU = (int)(argument & 0xFF);
        int pointsV = (int)((argument >> 8) & 0xFF);
        // the GE draws nothing with fewer than four control points either way
        if (pointsU < 4 || pointsV < 4)
        {
            return;
        }

        uint division = Command(GeCommand.PatchDivision);
        int stepsU = Math.Max(1, (int)(division & 0x7F));
        int stepsV = Math.Max(1, (int)((division >> 8) & 0x7F));
        int patchesU = spline ? pointsU - 3 : (pointsU - 1) / 3;
        int patchesV = spline ? pointsV - 3 : (pointsV - 1) / 3;
        while (PatchVertexCount(spline, patchesU, patchesV, stepsU, stepsV) > MaxPatchVertices && (stepsU > 1 || stepsV > 1))
        {
            if (stepsU >= stepsV)
            {
                stepsU--;
            }
            else
            {
                stepsV--;
            }
        }

        _transformer ??= new GeVertexTransformer(this);
        _transformer.PrepareState(format);
        int pointCount = pointsU * pointsV;
        if (_controlPoints.Length < pointCount)
        {
            _controlPoints = new GeDecodedVertex[pointCount];
        }

        for (int i = 0; i < pointCount; i++)
        {
            int index = format.IndexSize switch
            {
                1 => (int)(_memory.Read32((IndexAddress + (uint)i) & ~3u) >> (int)(((IndexAddress + (uint)i) & 3) * 8)) & 0xFF,
                2 => (int)(_memory.Read32((IndexAddress + (uint)(i * 2)) & ~3u) >> (int)(((IndexAddress + (uint)(i * 2)) & 2) * 8)) & 0xFFFF,
                4 => (int)_memory.Read32(IndexAddress + (uint)(i * 4)),
                _ => i,
            };
            _controlPoints[i] = _transformer.Decode(VertexAddress + (uint)(index * format.Size));
        }

        bool sampleNormal = format.NormalOffset >= 0 || (Command(GeCommand.LightingEnable) & 1) != 0 || (Command(GeCommand.TextureMapMode) & 3) == 2;
        bool facing = (Command(GeCommand.PatchFacing) & 1) != 0;
        int vertexCount = PatchVertexCount(spline, patchesU, patchesV, stepsU, stepsV);
        if (_patchVertices.Length < vertexCount)
        {
            _patchVertices = new GeDecodedVertex[vertexCount];
        }

        if (spline)
        {
            TessellateSpline(format, pointsU, patchesU, patchesV, stepsU, stepsV, (int)((argument >> 16) & 3), (int)((argument >> 18) & 3), sampleNormal, facing);
        }
        else
        {
            TessellateBezier(format, pointsU, patchesU, patchesV, stepsU, stepsV, sampleNormal, facing);
        }

        int primitiveType = (int)(Command(GeCommand.PatchPrimitive) & 3);
        int indexCount = BuildPatchIndices(spline, patchesU, patchesV, stepsU, stepsV, primitiveType == 1);
        if (_clipVertices.Length < indexCount)
        {
            _clipVertices = new GeClipVertex[Math.Max(indexCount, _clipVertices.Length * 2)];
        }

        for (int i = 0; i < indexCount; i++)
        {
            _clipVertices[i] = _transformer.Finish(_patchVertices[_patchIndices[i]]);
        }

        int primitive = primitiveType switch
        {
            1 => GeCommand.PrimitiveLines,
            2 => GeCommand.PrimitivePoints,
            _ => GeCommand.PrimitiveTriangles,
        };
        DrawTransformed(primitive, indexCount, format);
    }

    private static int PatchVertexCount(bool spline, int patchesU, int patchesV, int stepsU, int stepsV)
    {
        return spline ? ((patchesU * stepsU) + 1) * ((patchesV * stepsV) + 1) : (stepsU + 1) * (stepsV + 1) * patchesU * patchesV;
    }

    private void TessellateBezier(GeVertexFormat format, int pointsU, int patchesU, int patchesV, int stepsU, int stepsV, bool sampleNormal, bool facing)
    {
        Span<PatchColumn> columns = PatchColumns((stepsV + 1) * 4);
        int perPatch = (stepsU + 1) * (stepsV + 1);
        for (int patchU = 0; patchU < patchesU; patchU++)
        {
            for (int patchV = 0; patchV < patchesV; patchV++)
            {
                int corner = (patchV * 3 * pointsU) + (patchU * 3);
                for (int tileV = 0; tileV <= stepsV; tileV++)
                {
                    int kv = PatchParameter(tileV, stepsV);
                    for (int c = 0; c < 4; c++)
                    {
                        ref GeDecodedVertex p0 = ref _controlPoints[corner + c];
                        ref GeDecodedVertex p1 = ref _controlPoints[corner + pointsU + c];
                        ref GeDecodedVertex p2 = ref _controlPoints[corner + (2 * pointsU) + c];
                        ref GeDecodedVertex p3 = ref _controlPoints[corner + (3 * pointsU) + c];
                        columns[(tileV * 4) + c] = EvaluateColumn(p0, p1, p2, p3, kv, default, false);
                    }
                }

                for (int tileU = 0; tileU <= stepsU; tileU++)
                {
                    int ku = PatchParameter(tileU, stepsU);
                    for (int tileV = 0; tileV <= stepsV; tileV++)
                    {
                        Span<PatchColumn> row = columns.Slice(tileV * 4, 4);
                        int output = ((patchV * patchesU) + patchU) * perPatch + (tileV * (stepsU + 1)) + tileU;
                        GeDecodedVertex vertex = EvaluateRow(row, format, ku, default, false, sampleNormal, facing);
                        if (format.TextureOffset < 0)
                        {
                            vertex.U = patchU + (ku / 256f);
                            vertex.V = patchV + (PatchParameter(tileV, stepsV) / 256f);
                        }

                        _patchVertices[output] = vertex;
                    }
                }
            }
        }
    }

    private void TessellateSpline(GeVertexFormat format, int pointsU, int patchesU, int patchesV, int stepsU, int stepsV, int typeU, int typeV, bool sampleNormal, bool facing)
    {
        int verticesU = (patchesU * stepsU) + 1;
        int verticesV = (patchesV * stepsV) + 1;
        Span<PatchColumn> columns = PatchColumns(pointsU);
        for (int iv = 0; iv < verticesV; iv++)
        {
            SplineStep stepV = SplineStepAt(iv, stepsV, patchesV, typeV);
            for (int c = 0; c < pointsU; c++)
            {
                ref GeDecodedVertex p0 = ref _controlPoints[(stepV.Segment * pointsU) + c];
                ref GeDecodedVertex p1 = ref _controlPoints[((stepV.Segment + 1) * pointsU) + c];
                ref GeDecodedVertex p2 = ref _controlPoints[((stepV.Segment + 2) * pointsU) + c];
                ref GeDecodedVertex p3 = ref _controlPoints[((stepV.Segment + 3) * pointsU) + c];
                columns[c] = EvaluateColumn(p0, p1, p2, p3, 0, stepV, true);
            }

            for (int iu = 0; iu < verticesU; iu++)
            {
                SplineStep stepU = SplineStepAt(iu, stepsU, patchesU, typeU);
                GeDecodedVertex vertex = EvaluateRow(columns.Slice(stepU.Segment, 4), format, 0, stepU, true, sampleNormal, facing);
                if (format.TextureOffset < 0)
                {
                    vertex.U = stepU.Segment + (stepU.K / 256f);
                    vertex.V = stepV.Segment + (stepV.K / 256f);
                }

                _patchVertices[(iv * verticesU) + iu] = vertex;
            }
        }
    }

    private Span<PatchColumn> PatchColumns(int count)
    {
        if (_patchColumns.Length < count)
        {
            _patchColumns = new PatchColumn[count];
        }

        return _patchColumns.AsSpan(0, count);
    }

    /// <summary>Four control points of a column evaluated at one v (Bezier k or a spline step).</summary>
    private static PatchColumn EvaluateColumn(in GeDecodedVertex p0, in GeDecodedVertex p1, in GeDecodedVertex p2, in GeDecodedVertex p3, int k, in SplineStep step, bool spline)
    {
        PatchColumn column = default;
        column.Position = new Vector3(
            Evaluate(p0.Position.X, p1.Position.X, p2.Position.X, p3.Position.X, k, step, spline, out float fx, out float sx),
            Evaluate(p0.Position.Y, p1.Position.Y, p2.Position.Y, p3.Position.Y, k, step, spline, out float fy, out float sy),
            Evaluate(p0.Position.Z, p1.Position.Z, p2.Position.Z, p3.Position.Z, k, step, spline, out float fz, out float sz));
        column.First = new Vector3(fx, fy, fz);
        column.Second = new Vector3(sx, sy, sz);
        column.U = Evaluate(p0.U, p1.U, p2.U, p3.U, k, step, spline, out _, out _);
        column.V = Evaluate(p0.V, p1.V, p2.V, p3.V, k, step, spline, out _, out _);
        column.Red = EvaluateColor(Channel(p0.Color, 0), Channel(p1.Color, 0), Channel(p2.Color, 0), Channel(p3.Color, 0), k, step, spline);
        column.Green = EvaluateColor(Channel(p0.Color, 1), Channel(p1.Color, 1), Channel(p2.Color, 1), Channel(p3.Color, 1), k, step, spline);
        column.Blue = EvaluateColor(Channel(p0.Color, 2), Channel(p1.Color, 2), Channel(p2.Color, 2), Channel(p3.Color, 2), k, step, spline);
        column.Alpha = EvaluateColor(Channel(p0.Color, 3), Channel(p1.Color, 3), Channel(p2.Color, 3), Channel(p3.Color, 3), k, step, spline);
        return column;
    }

    private static float Evaluate(float a, float b, float c, float d, int k, in SplineStep step, bool spline, out float first, out float second)
    {
        return spline ? SplineEvaluate(a, b, c, d, step, out first, out second) : BezierEvaluate(a, b, c, d, k, out first, out second);
    }

    private static int EvaluateColor(int a, int b, int c, int d, int k, in SplineStep step, bool spline)
    {
        return spline ? SplineColor(a, b, c, d, step) : BezierColor(a, b, c, d, k);
    }

    /// <summary>The vertex at one u from four evaluated columns: position, the UV, colour and the tangents' normal.</summary>
    private GeDecodedVertex EvaluateRow(ReadOnlySpan<PatchColumn> row, GeVertexFormat format, int k, in SplineStep step, bool spline, bool sampleNormal, bool facing)
    {
        GeDecodedVertex vertex = default;
        vertex.Position = new Vector3(
            Evaluate(row[0].Position.X, row[1].Position.X, row[2].Position.X, row[3].Position.X, k, step, spline, out float fx, out float sx),
            Evaluate(row[0].Position.Y, row[1].Position.Y, row[2].Position.Y, row[3].Position.Y, k, step, spline, out float fy, out float sy),
            Evaluate(row[0].Position.Z, row[1].Position.Z, row[2].Position.Z, row[3].Position.Z, k, step, spline, out float fz, out float sz));
        vertex.U = Evaluate(row[0].U, row[1].U, row[2].U, row[3].U, k, step, spline, out _, out _);
        vertex.V = Evaluate(row[0].V, row[1].V, row[2].V, row[3].V, k, step, spline, out _, out _);
        vertex.HasColor = format.ColorOffset >= 0;
        if (vertex.HasColor)
        {
            int red = EvaluateColor(row[0].Red, row[1].Red, row[2].Red, row[3].Red, k, step, spline) >> 7;
            int green = EvaluateColor(row[0].Green, row[1].Green, row[2].Green, row[3].Green, k, step, spline) >> 7;
            int blue = EvaluateColor(row[0].Blue, row[1].Blue, row[2].Blue, row[3].Blue, k, step, spline) >> 7;
            int alpha = EvaluateColor(row[0].Alpha, row[1].Alpha, row[2].Alpha, row[3].Alpha, k, step, spline) >> 7;
            vertex.Color = (uint)(red | (green << 8) | (blue << 16) | (alpha << 24));
        }
        else
        {
            vertex.Color = _controlPoints[0].Color;
        }

        if (sampleNormal)
        {
            Vector3 tangentU = new Vector3(sx, sy, sz) - new Vector3(fx, fy, fz);
            Vector3 firstV = new(
                Evaluate(row[0].First.X, row[1].First.X, row[2].First.X, row[3].First.X, k, step, spline, out _, out _),
                Evaluate(row[0].First.Y, row[1].First.Y, row[2].First.Y, row[3].First.Y, k, step, spline, out _, out _),
                Evaluate(row[0].First.Z, row[1].First.Z, row[2].First.Z, row[3].First.Z, k, step, spline, out _, out _));
            Vector3 secondV = new(
                Evaluate(row[0].Second.X, row[1].Second.X, row[2].Second.X, row[3].Second.X, k, step, spline, out _, out _),
                Evaluate(row[0].Second.Y, row[1].Second.Y, row[2].Second.Y, row[3].Second.Y, k, step, spline, out _, out _),
                Evaluate(row[0].Second.Z, row[1].Second.Z, row[2].Second.Z, row[3].Second.Z, k, step, spline, out _, out _));
            var normal = Vector3.Cross(tangentU, secondV - firstV);
            vertex.Normal = facing ? -normal : normal;
        }
        else
        {
            vertex.Normal = Vector3.UnitZ;
        }

        return vertex;
    }

    /// <summary>
    /// The index list: per quad (top-left, top-right, bottom-left, bottom-right) two triangles (tl, bl, tr) and (tr, bl,
    /// br), or for lines a zigzag (left edge down, the diagonal up, and on a patch's last column the right edge).
    /// </summary>
    private int BuildPatchIndices(bool spline, int patchesU, int patchesV, int stepsU, int stepsV, bool lines)
    {
        int quads = patchesU * patchesV * stepsU * stepsV;
        if (_patchIndices.Length < quads * 6)
        {
            _patchIndices = new int[quads * 6];
        }

        int count = 0;
        if (spline)
        {
            AddQuadIndices(patchesU * stepsU, patchesV * stepsV, stepsU, 0, lines, ref count);
            return count;
        }

        int perPatch = (stepsU + 1) * (stepsV + 1);
        for (int patchU = 0; patchU < patchesU; patchU++)
        {
            for (int patchV = 0; patchV < patchesV; patchV++)
            {
                AddQuadIndices(stepsU, stepsV, stepsU, ((patchV * patchesU) + patchU) * perPatch, lines, ref count);
            }
        }

        return count;
    }

    private void AddQuadIndices(int columns, int rows, int patchColumns, int first, bool lines, ref int count)
    {
        for (int v = 0; v < rows; v++)
        {
            for (int u = 0; u < columns; u++)
            {
                int topLeft = (v * (columns + 1)) + u + first;
                int bottomLeft = ((v + 1) * (columns + 1)) + u + first;
                int topRight = topLeft + 1;
                int bottomRight = bottomLeft + 1;
                if (lines)
                {
                    _patchIndices[count++] = topLeft;
                    _patchIndices[count++] = bottomLeft;
                    _patchIndices[count++] = bottomLeft;
                    _patchIndices[count++] = topRight;
                    if ((u + 1) % patchColumns == 0)
                    {
                        _patchIndices[count++] = topRight;
                        _patchIndices[count++] = bottomRight;
                    }
                }
                else
                {
                    _patchIndices[count++] = topLeft;
                    _patchIndices[count++] = bottomLeft;
                    _patchIndices[count++] = topRight;
                    _patchIndices[count++] = topRight;
                    _patchIndices[count++] = bottomLeft;
                    _patchIndices[count++] = bottomRight;
                }
            }
        }
    }

    // ---- bounding box

    /// <summary>
    /// BOUNDINGBOX (PPSSPP's TestBoundingBox and its measurements): false when every vertex is beyond the same side of
    /// the drawing region (the larger of REGION and SCISSOR, widened by a pixel, plus the screen offset) or, with
    /// DEPTHCLAMP, of the near or far plane; true when that region reaches the 4096-pixel screen's edge. 0 vertices give
    /// false; above 0x100 only one range of 0x100 is tested, as the hardware does.
    /// </summary>
    private partial bool BoundingBoxVisible(int count, GeVertexFormat format)
    {
        if (count == 0)
        {
            return false;
        }

        int first = 0;
        int tested = count;
        if (count > 0x200)
        {
            first = count - 0x200;
            tested = 0x100;
        }
        else if (count > 0x100)
        {
            tested = count - 0x100;
        }

        float offsetX = (Command(GeCommand.OffsetX) & 0xFFFF) / 16f;
        float offsetY = (Command(GeCommand.OffsetY) & 0xFFFF) / 16f;
        uint region1 = Command(GeCommand.Region1);
        uint region2 = Command(GeCommand.Region2);
        uint scissor1 = Command(GeCommand.Scissor1);
        uint scissor2 = Command(GeCommand.Scissor2);
        float left = offsetX + Math.Max(region1 & 0x3FF, scissor1 & 0x3FF) - 1;
        float top = offsetY + Math.Max((region1 >> 10) & 0x3FF, (scissor1 >> 10) & 0x3FF) - 1;
        float right = offsetX + Math.Min(region2 & 0x3FF, scissor2 & 0x3FF) + 1;
        float bottom = offsetY + Math.Min((region2 >> 10) & 0x3FF, (scissor2 >> 10) & 0x3FF) + 1;
        if (right >= 4096 || bottom >= 4096 || left < 1 || top < 1)
        {
            return true;
        }

        _transformer ??= new GeVertexTransformer(this);
        _transformer.PrepareState(format);
        float scaleX = GeVertexTransformer.Float24(Command(GeCommand.ViewportXScale));
        float scaleY = GeVertexTransformer.Float24(Command(GeCommand.ViewportYScale));
        float centerX = GeVertexTransformer.Float24(Command(GeCommand.ViewportXCenter));
        float centerY = GeVertexTransformer.Float24(Command(GeCommand.ViewportYCenter));
        Span<int> inside = stackalloc int[6];
        inside.Clear();
        for (int i = first; i < first + tested; i++)
        {
            int index = format.IndexSize switch
            {
                1 => (int)(_memory.Read32((IndexAddress + (uint)i) & ~3u) >> (int)(((IndexAddress + (uint)i) & 3) * 8)) & 0xFF,
                2 => (int)(_memory.Read32((IndexAddress + (uint)(i * 2)) & ~3u) >> (int)(((IndexAddress + (uint)(i * 2)) & 2) * 8)) & 0xFFFF,
                4 => (int)(_memory.Read32(IndexAddress + (uint)(i * 4)) & 0xFFFF),
                _ => i,
            };
            GeClipVertex clip = _transformer.ClipPosition(_transformer.DecodePosition(VertexAddress + (uint)(index * format.Size)));
            float w = clip.ClipW;
            if (clip.ClipZ >= -w)
            {
                inside[4]++;
            }

            if (clip.ClipZ <= w)
            {
                inside[5]++;
            }

            float screenX = (clip.ClipX * scaleX) + (centerX * w);
            float screenY = (clip.ClipY * scaleY) + (centerY * w);
            if (screenX >= left * w)
            {
                inside[0]++;
            }

            if (screenX <= right * w)
            {
                inside[1]++;
            }

            if (screenY >= top * w)
            {
                inside[2]++;
            }

            if (screenY <= bottom * w)
            {
                inside[3]++;
            }
        }

        int planes = (Command(GeCommand.DepthClampEnable) & 1) != 0 ? 6 : 4;
        for (int plane = 0; plane < planes; plane++)
        {
            if (inside[plane] == 0)
            {
                return false;
            }
        }

        return true;
    }
}
