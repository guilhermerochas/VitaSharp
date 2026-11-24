namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The VFPU matrix instructions (opcode 0x3C): vmmul, vtfm/vhtfm, vmscl, vcrsp.t/vqmul.q, vmmov, vmidt/vmzero/vmone and
/// vrot. The S/T prefixes reach only the last row or the last dot product, and the D prefix only the last vector or
/// lane, as PPSSPP recorded from the hardware; every sum is the exact VFPU dot product.
/// </summary>
public sealed partial class AllegrexCore
{
    private readonly uint[] _matrixS = new uint[16];
    private readonly uint[] _matrixT = new uint[16];
    private readonly uint[] _matrixD = new uint[16];

    private static readonly uint[] s_identityMatrix =
    [
        0x3F800000, 0, 0, 0,
        0, 0x3F800000, 0, 0,
        0, 0, 0x3F800000, 0,
        0, 0, 0, 0x3F800000,
    ];

    private void ExecuteVfpu6(uint instruction)
    {
        uint group = (instruction >> 21) & 0x1F;
        if (group < 4)
        {
            MatrixMultiply(instruction);
        }
        else if (group < 16)
        {
            Transform(instruction);
        }
        else if (group < 20)
        {
            MatrixScale(instruction);
        }
        else if (group < 24)
        {
            CrossOrQuaternion(instruction);
        }
        else if (group == 28)
        {
            ExecuteMatrixSet(instruction);
        }
        else if (group == 29)
        {
            Rotate(instruction);
        }
        else
        {
            RaiseException(AllegrexExceptionCode.ReservedInstruction);
        }
    }

    private Span<uint> ClearedMatrix(uint[] matrix)
    {
        Array.Clear(matrix);
        return matrix;
    }

    /// <summary>A dot product's result as the matrix ops keep it: the positive VFPU NaN, a signed zero below FLT_MIN.</summary>
    private static uint MatrixSum(uint bits)
    {
        if (AllegrexVfpuMath.IsNaN(bits))
        {
            return AllegrexVfpuMath.PositiveNaN;
        }

        return (bits & 0x7F800000) == 0 ? bits & 0xFF800000 : bits;
    }

    /// <summary>vmmul: d[a][b] = s vector b . t vector a; the prefixes act on the last dot product and its result only.</summary>
    private void MatrixMultiply(uint instruction)
    {
        int side = VfpuSize(instruction);
        Span<uint> s = ClearedMatrix(_matrixS);
        Span<uint> t = ClearedMatrix(_matrixT);
        Span<uint> d = ClearedMatrix(_matrixD);
        ReadMatrix(s, side, VfpuS(instruction));
        ReadMatrix(t, side, VfpuT(instruction));
        for (int a = 0; a < side; a++)
        {
            for (int b = 0; b < side; b++)
            {
                if (a == side - 1 && b == side - 1)
                {
                    ApplyPrefixS(s.Slice(b * 4, 4), 4);
                    ApplyPrefixT(t.Slice(a * 4, 4), 4);
                }

                d[(a * 4) + b] = MatrixSum(AllegrexVfpuMath.Dot(s.Slice(b * 4, 4), t.Slice(a * 4, 4)));
            }
        }

        RestrictDPrefixToLastLane(side);
        ApplyPrefixD(d.Slice((side - 1) * 4, 4), 4);
        WriteMatrix(d, side, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>
    /// vtfm2/3/4 and vhtfm2/3/4 (bits 23-24 give the matrix size, the vector size picks the homogeneous form): each
    /// result lane is a row of the matrix dotted with the vector (a missing last component reads 1); the prefixes reach
    /// the last row only.
    /// </summary>
    private void Transform(uint instruction)
    {
        int last = (int)((instruction >> 23) & 3);
        int side = last + 1;
        int count = VfpuSize(instruction);
        int used = Math.Min(count, side);
        Span<uint> s = ClearedMatrix(_matrixS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        Span<uint> padded = ClearedLanes(_laneS);
        ReadMatrix(s, side, VfpuS(instruction));
        ReadVector(t, side, VfpuT(instruction));
        for (int i = 0; i < 4; i++)
        {
            if (i < used)
            {
                padded[i] = t[i];
            }
            else
            {
                padded[i] = i == last ? 0x3F800000u : 0u;
            }
        }

        for (int i = 0; i < last; i++)
        {
            d[i] = MatrixSum(AllegrexVfpuMath.Dot(s.Slice(i * 4, 4), padded));
        }

        ApplyPrefixS(s.Slice(last * 4, 4), 4);
        int constantY = count < 2 ? ConstantZero : NoConstant;
        int constantZ = count < 3 ? ConstantZero : NoConstant;
        int constantW = count < 4 ? ConstantZero : NoConstant;
        if (last >= count)
        {
            if (last == 1)
            {
                constantY = ConstantOne;
            }
            else if (last == 2)
            {
                constantZ = ConstantOne;
            }
            else if (last == 3)
            {
                constantW = ConstantOne;
            }
        }

        uint remove = Swizzle(0, count < 2 ? 3 : 0, count < 3 ? 3 : 0, count < 4 ? 3 : 0);
        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, remove, Constants(NoConstant, constantY, constantZ, constantW)), 4);
        d[last] = MatrixSum(AllegrexVfpuMath.Dot(s.Slice(last * 4, 4), t));
        uint prefix = _vfpuControl[VfpuDPrefix];
        _vfpuControl[VfpuDPrefix] = ((prefix & (1u << 8)) << last) | ((prefix & 3) << (last * 2));
        ApplyPrefixD(d, side);
        WriteVector(d, side, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vmscl: the matrix times lane 0 of vt; the prefixes reach the last vector only.</summary>
    private void MatrixScale(uint instruction)
    {
        int side = VfpuSize(instruction);
        int vt = VfpuT(instruction);
        Span<uint> s = ClearedMatrix(_matrixS);
        Span<uint> d = ClearedMatrix(_matrixD);
        Span<uint> t = ClearedLanes(_laneT);
        ReadMatrix(s, side, VfpuS(instruction));
        uint scale = _vfpr[VfpuIndex(vt)];
        for (int a = 0; a < side - 1; a++)
        {
            for (int b = 0; b < side; b++)
            {
                d[(a * 4) + b] = AllegrexVfpuMath.Multiply(s[(a * 4) + b], scale);
            }
        }

        int lastRow = (side - 1) * 4;
        ApplyPrefixS(s.Slice(lastRow, 4), 4);
        int tLane = (vt >> 5) & 3;
        t[tLane] = scale;
        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, AnySwizzle, Swizzle(tLane, tLane, tLane, tLane)), 4);
        for (int b = 0; b < side; b++)
        {
            d[lastRow + b] = AllegrexVfpuMath.Multiply(s[lastRow + b], t[b]);
        }

        ApplyPrefixD(d.Slice(lastRow, 4), 4);
        WriteMatrix(d, side, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vcrsp.t (cross product) and vqmul.q (quaternion product) as dot products; the T prefix is forced on the last one.</summary>
    private void CrossOrQuaternion(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        Span<uint> row = _matrixT.AsSpan(0, 4);
        ReadVector(s, size, VfpuS(instruction));
        ReadVector(t, size, VfpuT(instruction));
        uint remove = AnySwizzle | Negate(1, 1, 1, 1);
        switch (size)
        {
            case 3:
                SetRow(row, 0, t[2], t[1] ^ 0x80000000, 0);
                d[0] = MatrixSum(AllegrexVfpuMath.Dot(s, row));
                SetRow(row, t[2] ^ 0x80000000, 0, t[0], 0);
                d[1] = MatrixSum(AllegrexVfpuMath.Dot(s, row));
                ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, remove, Swizzle(1, 0, 3, 2) | Negate(0, 1, 0, 0)), 4);
                ApplyPrefixS(s, 4);
                d[2] = MatrixSum(AllegrexVfpuMath.Dot(s, t));
                break;
            case 4:
                SetRow(row, t[3], t[2], t[1] ^ 0x80000000, t[0]);
                d[0] = MatrixSum(AllegrexVfpuMath.Dot(s, row));
                SetRow(row, t[2] ^ 0x80000000, t[3], t[0], t[1]);
                d[1] = MatrixSum(AllegrexVfpuMath.Dot(s, row));
                SetRow(row, t[1], t[0] ^ 0x80000000, t[3], t[2]);
                d[2] = MatrixSum(AllegrexVfpuMath.Dot(s, row));
                ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, remove, Swizzle(0, 1, 2, 3) | Negate(1, 1, 1, 0)), 4);
                ApplyPrefixS(s, 4);
                d[3] = MatrixSum(AllegrexVfpuMath.Dot(s, t));
                break;
            case 2:
                d[0] = 0;
                ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, remove, 0), 4);
                ApplyPrefixS(s, 4);
                d[1] = AllegrexVfpuMath.Multiply(s[2], t[2]);
                break;
            default:
                d[0] = 0;
                break;
        }

        if (size != 1)
        {
            RestrictDPrefixToLastLane(size);
            ApplyPrefixD(d, size);
        }
        else
        {
            _vfpuControl[VfpuDPrefix] = 0;
        }

        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    private static void SetRow(Span<uint> row, uint x, uint y, uint z, uint w)
    {
        row[0] = x;
        row[1] = y;
        row[2] = z;
        row[3] = w;
    }

    /// <summary>Opcode 0x3C, group 28: vmmov (0), vmidt (3), vmzero (6), vmone (7) by bits 16-19.</summary>
    private void ExecuteMatrixSet(uint instruction)
    {
        uint operation = (instruction >> 16) & 0xF;
        if (operation == 0)
        {
            MatrixMove(instruction);
        }
        else if (operation == 3 || operation == 6 || operation == 7)
        {
            MatrixFill(instruction, operation);
        }
        else
        {
            RaiseException(AllegrexExceptionCode.ReservedInstruction);
        }
    }

    /// <summary>vmmov: the S and D prefixes apply to the last vector.</summary>
    private void MatrixMove(uint instruction)
    {
        int side = VfpuSize(instruction);
        Span<uint> s = ClearedMatrix(_matrixS);
        ReadMatrix(s, side, VfpuS(instruction));
        int lastRow = (side - 1) * 4;
        ApplyPrefixS(s.Slice(lastRow, 4), 4);
        ApplyPrefixD(s.Slice(lastRow, 4), 4);
        WriteMatrix(s, side, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vmidt/vmzero/vmone; an S prefix with abs or negate turns the last vector into its constants.</summary>
    private void MatrixFill(uint instruction, uint operation)
    {
        int side = VfpuSize(instruction);
        Span<uint> d = ClearedMatrix(_matrixD);
        for (int i = 0; i < 16; i++)
        {
            d[i] = operation switch
            {
                3 => s_identityMatrix[i],
                6 => 0u,
                _ => 0x3F800000u,
            };
        }

        if ((_vfpuControl[VfpuSPrefix] & 0xF0F00) != 0)
        {
            int last = side - 1;
            uint add = operation switch
            {
                3 => Constants(last == 0 ? ConstantOne : ConstantZero, last == 1 ? ConstantOne : ConstantZero, last == 2 ? ConstantOne : ConstantZero, last == 3 ? ConstantOne : ConstantZero),
                6 => Constants(ConstantZero, ConstantZero, ConstantZero, ConstantZero),
                _ => Constants(ConstantOne, ConstantOne, ConstantOne, ConstantOne),
            };
            ApplyPrefixST(d.Slice(last * 4, 4), RewritePrefix(VfpuSPrefix, AnySwizzle, add), 4);
        }

        WriteMatrix(d, side, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>The S-notation numbers of a vector's lanes (to find vs among vd's lanes).</summary>
    private static int VectorLaneRegister(int size, int register, int lane)
    {
        int matrix = (register >> 2) & 7;
        int column = register & 3;
        bool transposed = ((register >> 5) & 1) != 0;
        int row;
        if (size == 1)
        {
            transposed = false;
            row = (register >> 5) & 3;
        }
        else
        {
            row = VectorStartLane(size, register);
        }

        int index = matrix * 4;
        return transposed ? index + ((row + lane) & 3) + (column * 32) : index + column + (((row + lane) & 3) * 32);
    }

    /// <summary>
    /// vrot: one row of a rotation matrix from the angle in vs (quarter turns): imm5 bits 0-1 the cosine lane, 2-3 the
    /// sine lane (equal: the sine everywhere), bit 4 negates the sine; the other lanes 0. The D prefix skips the cosine lane.
    /// </summary>
    private void Rotate(uint instruction)
    {
        int size = VfpuSize(instruction);
        int vd = VfpuD(instruction);
        int vs = VfpuS(instruction);
        uint immediate = (instruction >> 16) & 0x1F;
        bool negateSine = (immediate & 0x10) != 0;
        int sineLane = (int)((immediate >> 2) & 3);
        int cosineLane = (int)(immediate & 3);
        uint angle = _vfpr[VfpuIndex(vs)];
        uint sine;
        uint cosine = AllegrexVfpuMath.Transcendental(19, angle);
        Span<uint> d = ClearedLanes(_laneD);
        if (_vfpuControl[VfpuSPrefix] == VfpuIdentityPrefix)
        {
            sine = AllegrexVfpuMath.Transcendental(18, angle);
        }
        else
        {
            Span<uint> s = ClearedLanes(_laneS);
            s[0] = angle;
            ApplyPrefixST(s, RewritePrefix(VfpuSPrefix, Negate(1, 0, 0, 0), 0), 1);
            sine = AllegrexVfpuMath.Transcendental(18, s[0]);
        }

        if (negateSine)
        {
            sine ^= 0x80000000;
        }

        if (_vfpuControl[VfpuSPrefix] != VfpuIdentityPrefix)
        {
            Span<uint> lane = _laneS.AsSpan(0, 1);
            lane[0] = sine;
            ZeroInvalidSwizzles(lane, 1);
            sine = lane[0];
        }

        if (sineLane == cosineLane)
        {
            for (int i = 0; i < 4; i++)
            {
                d[i] = sine;
            }
        }
        else
        {
            d[sineLane] = sine;
        }

        if (((vd >> 2) & 7) == ((vs >> 2) & 7))
        {
            // vs is one of vd's lanes: the cosine is of what that lane now holds (the sine or zero)
            for (int i = 0; i < size; i++)
            {
                if (VectorLaneRegister(size, vd, i) == vs)
                {
                    cosine = AllegrexVfpuMath.Transcendental(19, d[i]);
                    break;
                }
            }
        }

        d[cosineLane] = cosine;

        uint remove = (3u << (cosineLane * 2)) | (1u << (8 + cosineLane));
        _vfpuControl[VfpuDPrefix] &= 0xFFFFF ^ remove;
        ApplyPrefixD(d, size);
        WriteVector(d, size, vd);
        EatPrefixes();
    }
}
