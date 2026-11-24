namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The VFPU vector instructions (opcodes 0x18, 0x19, 0x1B, 0x34): lane arithmetic, the dot-product family, compares,
/// min/max, the unary and transcendental group, conversions, the random generator, sort/butterfly helpers and the
/// operations that rewrite their own prefixes. Each follows the prefix handling PPSSPP recorded from the hardware
/// (which prefix fields an instruction forces, which lanes the D prefix reaches), with the VFPU's own arithmetic
/// (<see cref="AllegrexVfpuMath"/>).
/// </summary>
public sealed partial class AllegrexCore
{
    private const uint AnySwizzle = 0xFF;
    private const int NoConstant = -1;
    private const int ConstantZero = 0;
    private const int ConstantOne = 1;
    private const int ConstantHalf = 3;
    private const int ConstantThird = 5;
    private const int ConstantFourth = 6;

    private static readonly uint[] s_vcstConstants = BuildVcstConstants();

    private static uint Swizzle(int x, int y, int z, int w)
    {
        return (uint)(x | (y << 2) | (z << 4) | (w << 6));
    }

    private static uint LaneMask(int x, int y, int z, int w)
    {
        return (uint)(x | (y << 1) | (z << 2) | (w << 3));
    }

    private static uint Negate(int x, int y, int z, int w)
    {
        return LaneMask(x, y, z, w) << 16;
    }

    private static uint Absolute(int x, int y, int z, int w)
    {
        return LaneMask(x, y, z, w) << 8;
    }

    /// <summary>Prefix bits that make lanes the constants 0, 1, 2, 1/2, 3, 1/3, 1/4, 1/6 (index; -1 leaves a lane alone).</summary>
    private static uint Constants(int x, int y, int z, int w)
    {
        return ConstantLane(x, 0) | ConstantLane(y, 1) | ConstantLane(z, 2) | ConstantLane(w, 3);
    }

    private static uint ConstantLane(int constant, int lane)
    {
        if (constant == NoConstant)
        {
            return 0;
        }

        return (uint)(((constant & 3) << (lane * 2)) | ((constant & 4) << (6 + lane)) | (1 << (12 + lane)));
    }

    private static uint[] BuildVcstConstants()
    {
        float pi = MathF.PI;
        float[] values =
        [
            0f,
            float.MaxValue,
            MathF.Sqrt(2f),
            MathF.Sqrt(0.5f),
            2f / MathF.Sqrt(pi),
            2f / pi,
            1f / pi,
            pi / 4,
            pi / 2,
            pi,
            MathF.E,
            (float)(1 / Math.Log(2)),
            (float)(1 / Math.Log(10)),
            (float)Math.Log(2),
            (float)Math.Log(10),
            2 * pi,
            pi / 6,
            MathF.Log10(2f),
            MathF.Log(10f) / MathF.Log(2f),
            MathF.Sqrt(3f) / 2f,
        ];
        uint[] bits = new uint[32];
        for (int i = 0; i < values.Length; i++)
        {
            bits[i] = BitConverter.SingleToUInt32Bits(values[i]);
        }

        return bits;
    }

    private Span<uint> ClearedLanes(uint[] lanes)
    {
        Array.Clear(lanes);
        return lanes.AsSpan(0, 4);
    }

    // ---- opcode 0x18: vadd, vsub, vsbn, vdiv ----

    private void ExecuteVfpu0(uint instruction)
    {
        switch ((instruction >> 23) & 7)
        {
            case 0:
            case 1:
            case 7:
                LaneArithmetic(instruction, (int)((instruction >> 23) & 7));
                break;
            case 2:
                ScaleByExponent(instruction);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    /// <summary>vadd (0), vsub (1), vdiv (7), vmul (8). vdiv takes the S/T prefixes on its last lane only.</summary>
    private void LaneArithmetic(uint instruction, int operation)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ReadVector(t, size, VfpuT(instruction));
        if (operation != 7)
        {
            ApplyPrefixS(s, size);
            ApplyPrefixT(t, size);
        }
        else
        {
            ApplyPrefixST(s.Slice(size - 1, 1), _vfpuControl[VfpuSPrefix], 1, 0xFF800000);
            ApplyPrefixST(t.Slice(size - 1, 1), _vfpuControl[VfpuTPrefix], 1, 0xFF800000);
        }

        for (int i = 0; i < size; i++)
        {
            d[i] = operation switch
            {
                0 => AllegrexVfpuMath.Add(s[i], t[i]),
                1 => AllegrexVfpuMath.Subtract(s[i], t[i]),
                7 => AllegrexVfpuMath.Divide(s[i], t[i]),
                _ => AllegrexVfpuMath.Multiply(s[i], t[i]),
            };
        }

        if (operation == 7)
        {
            if (LastLaneSwizzleInvalid(VfpuSPrefix) || LastLaneSwizzleInvalid(VfpuTPrefix))
            {
                d[size - 1] = 0;
            }

            RestrictDPrefixToLastLane(size);
        }
        else
        {
            ZeroInvalidSwizzles(d, size);
        }

        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vsbn: lane 0 gets the exponent 127 + t (as an integer); zero, denormal, inf and NaN pass through.</summary>
    private void ScaleByExponent(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        ReadVector(t, size, VfpuT(instruction));
        ApplyPrefixT(t, size);
        uint exponent = (uint)(byte)(127 + (int)t[0]);
        uint previous = s[0] & 0x7F800000;
        if (previous != 0 && previous != 0x7F800000)
        {
            s[0] = (s[0] & ~0x7F800000u) | (exponent << 23);
        }

        ApplyPrefixD(s, size);
        WriteVector(s, size, VfpuD(instruction));
        EatPrefixes();
    }

    // ---- opcode 0x19: vmul, vdot, vscl, vhdp, vcrs, vdet ----

    private void ExecuteVfpu1(uint instruction)
    {
        switch ((instruction >> 23) & 7)
        {
            case 0:
                LaneArithmetic(instruction, 8);
                break;
            case 1:
                DotProduct(instruction);
                break;
            case 2:
                Scale(instruction);
                break;
            case 4:
                HomogeneousDotProduct(instruction);
                break;
            case 5:
                HalfCrossProduct(instruction);
                break;
            case 6:
                Determinant(instruction);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    /// <summary>The summing operations' NaN is always positive; a zero sum is +0 (pspautotests cpu/vfpu/signed_zero).</summary>
    private static uint SumResult(uint bits)
    {
        return AllegrexVfpuMath.IsNaN(bits) ? AllegrexVfpuMath.PositiveNaN : bits;
    }

    private void WriteSingleResult(uint value, uint instruction)
    {
        Span<uint> d = _laneD.AsSpan(0, 1);
        d[0] = value;
        ApplyPrefixD(d, 1);
        WriteVector(d, 1, VfpuD(instruction));
        EatPrefixes();
    }

    private void DotProduct(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, 4);
        ReadVector(t, size, VfpuT(instruction));
        ApplyPrefixT(t, 4);
        WriteSingleResult(SumResult(AllegrexVfpuMath.Dot(s, t)), instruction);
    }

    /// <summary>vhdp: vdot with the S prefix forcing the constant 1 into the last lane.</summary>
    private void HomogeneousDotProduct(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        ReadVector(s, size, VfpuS(instruction));
        ReadVector(t, size, VfpuT(instruction));
        ApplyPrefixT(t, 4);
        int last = size - 1;
        uint remove = 3u << (last * 2);
        uint add = Constants(last == 0 ? ConstantOne : NoConstant, last == 1 ? ConstantOne : NoConstant, last == 2 ? ConstantOne : NoConstant, last == 3 ? ConstantOne : NoConstant);
        ApplyPrefixST(s, RewritePrefix(VfpuSPrefix, remove, add), 4);
        uint sum = AllegrexVfpuMath.Dot(s, t);
        WriteSingleResult(AllegrexVfpuMath.IsNaN(sum) ? sum & 0x7FFFFFFF : sum, instruction);
    }

    /// <summary>vscl: every lane times lane 0 of vt, through a T prefix forced to that lane.</summary>
    private void Scale(uint instruction)
    {
        int size = VfpuSize(instruction);
        int vt = VfpuT(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        int tLane = (vt >> 5) & 3;
        t[tLane] = _vfpr[VfpuIndex(vt)];
        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, AnySwizzle, Swizzle(tLane, tLane, tLane, tLane)), 4);
        for (int i = 0; i < size; i++)
        {
            d[i] = AllegrexVfpuMath.Multiply(s[i], t[i]);
        }

        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vcrs: half a cross product, the S prefix forced to y z x and the T prefix to z x y (lane by lane).</summary>
    private void HalfCrossProduct(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ReadVector(t, size, VfpuT(instruction));
        ApplyPrefixST(s, RewritePrefix(VfpuSPrefix, Swizzle(3, 3, 3, 0), Swizzle(1, 2, 0, 0)), size);
        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, Swizzle(3, 3, 3, 0), Swizzle(2, 0, 1, 0)), size);
        for (int i = 0; i < 4; i++)
        {
            d[i] = AllegrexVfpuMath.Multiply(s[i], t[i]);
        }

        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vdet: s.x * t.y - s.y * t.x, through a T prefix forced to y x and a dot product.</summary>
    private void Determinant(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, 4);
        ReadVector(t, size, VfpuT(instruction));
        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, Swizzle(3, 3, 0, 0), Swizzle(1, 0, 0, 0)), 4);
        s[1] ^= 0x80000000;
        uint result = AllegrexVfpuMath.Dot(s, t);
        Span<uint> d = _laneD.AsSpan(0, 1);
        d[0] = SumResult(result);
        ApplyPrefixD(d, 1);
        WriteVector(d, 1, VfpuD(instruction));
        EatPrefixes();
    }

    // ---- opcode 0x1B: vcmp, vmin, vmax, vscmp, vsge, vslt ----

    private void ExecuteVfpu3(uint instruction)
    {
        switch ((instruction >> 23) & 7)
        {
            case 0:
                VectorCompare(instruction);
                break;
            case 2:
            case 3:
                MinimumMaximum(instruction, ((instruction >> 23) & 7) == 3);
                break;
            case 5:
                SignCompare(instruction);
                break;
            case 6:
            case 7:
                SetIf(instruction, ((instruction >> 23) & 7) == 6);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    private void ReadSourceAndTarget(uint instruction, int size, out Span<uint> s, out Span<uint> t)
    {
        s = ClearedLanes(_laneS);
        t = ClearedLanes(_laneT);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        ReadVector(t, size, VfpuT(instruction));
        ApplyPrefixT(t, size);
    }

    /// <summary>
    /// vcmp.cond: per lane CC bit (0..size-1), bit 4 = any lane true, bit 5 = all lanes true; the other lane bits stay.
    /// Conditions 0..7: FL EQ LT LE TR NE GE GT; 8..15: EZ EN EI ES NZ NN NI NS (on the S lanes).
    /// </summary>
    private void VectorCompare(uint instruction)
    {
        int size = VfpuSize(instruction);
        ReadSourceAndTarget(instruction, size, out Span<uint> s, out Span<uint> t);
        uint condition = instruction & 0xF;
        uint codes = 0;
        uint any = 0;
        uint all = 1;
        uint affected = 0x30;
        for (int i = 0; i < size; i++)
        {
            float left = AllegrexVfpuMath.Input(s[i]);
            float right = AllegrexVfpuMath.Input(t[i]);
            bool nan = AllegrexVfpuMath.IsNaN(s[i]);
            bool infinity = AllegrexVfpuMath.IsInfinity(s[i]);
            bool result = condition switch
            {
                0 => false,
                1 => left == right,
                2 => left < right,
                3 => left <= right,
                4 => true,
                5 => left != right,
                6 => left >= right,
                7 => left > right,
                8 => left == 0,
                9 => nan,
                10 => infinity,
                11 => nan || infinity,
                12 => left != 0,
                13 => !nan,
                14 => !infinity,
                _ => !(nan || infinity),
            };
            uint bit = result ? 1u : 0u;
            codes |= bit << i;
            any |= bit;
            all &= bit;
            affected |= 1u << i;
        }

        uint value = codes | (any << 4) | (all << 5);
        _vfpuControl[VfpuConditionCodes] = (_vfpuControl[VfpuConditionCodes] & ~affected) | (value & affected);
        EatPrefixes();
    }

    /// <summary>
    /// The order vmin/vmax and the sorts use: zeros and denormals are all equal, -NaN &lt; -inf &lt; negative &lt;
    /// positive &lt; inf &lt; NaN; a tie returns the second operand, and the result keeps its bits.
    /// </summary>
    private static int OrderKey(uint bits)
    {
        if ((bits & 0x7F800000) == 0)
        {
            return 0;
        }

        return (int)(bits ^ ((uint)((int)bits >> 31) >> 1));
    }

    private static uint Minimum(uint left, uint right)
    {
        return OrderKey(left) < OrderKey(right) ? left : right;
    }

    private static uint Maximum(uint left, uint right)
    {
        return OrderKey(left) > OrderKey(right) ? left : right;
    }

    private void MinimumMaximum(uint instruction, bool maximum)
    {
        int size = VfpuSize(instruction);
        ReadSourceAndTarget(instruction, size, out Span<uint> s, out Span<uint> t);
        Span<uint> d = ClearedLanes(_laneD);
        for (int i = 0; i < size; i++)
        {
            d[i] = maximum ? Maximum(s[i], t[i]) : Minimum(s[i], t[i]);
        }

        ZeroInvalidSwizzles(d, size);
        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vscmp: the sign of s - t as -1, 0 or 1; with a NaN difference, by the signed magnitudes.</summary>
    private void SignCompare(uint instruction)
    {
        int size = VfpuSize(instruction);
        ReadSourceAndTarget(instruction, size, out Span<uint> s, out Span<uint> t);
        Span<uint> d = ClearedLanes(_laneD);
        for (int i = 0; i < size; i++)
        {
            float difference = AllegrexVfpuMath.Input(s[i]) - AllegrexVfpuMath.Input(t[i]);
            int sign;
            if (float.IsNaN(difference))
            {
                long left = (s[i] & 0x80000000) != 0 ? -(long)(s[i] & 0x7FFFFFFF) : s[i] & 0x7FFFFFFF;
                long right = (t[i] & 0x80000000) != 0 ? -(long)(t[i] & 0x7FFFFFFF) : t[i] & 0x7FFFFFFF;
                sign = Math.Sign(left - right);
            }
            else
            {
                sign = Math.Sign(difference);
            }

            d[i] = BitConverter.SingleToUInt32Bits(sign);
        }

        ZeroInvalidSwizzles(d, size);
        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vsge/vslt: 1.0 when s &gt;= t (s &lt; t), else 0; a NaN gives 0. Saturation cannot matter.</summary>
    private void SetIf(uint instruction, bool greaterOrEqual)
    {
        int size = VfpuSize(instruction);
        ReadSourceAndTarget(instruction, size, out Span<uint> s, out Span<uint> t);
        Span<uint> d = ClearedLanes(_laneD);
        for (int i = 0; i < size; i++)
        {
            bool result = false;
            if (!AllegrexVfpuMath.IsNaN(s[i]) && !AllegrexVfpuMath.IsNaN(t[i]))
            {
                float left = AllegrexVfpuMath.Input(s[i]);
                float right = AllegrexVfpuMath.Input(t[i]);
                result = greaterOrEqual ? left >= right : left < right;
            }

            d[i] = result ? 0x3F800000u : 0u;
        }

        ZeroInvalidSwizzles(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    // ---- opcode 0x34 ----

    private void ExecuteVfpu4Jump(uint instruction)
    {
        uint group = (instruction >> 21) & 0x1F;
        switch (group)
        {
            case 0:
                ExecuteVfpuUnary(instruction);
                break;
            case 1:
                ExecuteVfpu7(instruction);
                break;
            case 2:
                ExecuteVfpu9(instruction);
                break;
            case 3:
                LoadConstant(instruction);
                break;
            case 16:
            case 17:
            case 18:
            case 19:
                FloatToInteger(instruction, group);
                break;
            case 20:
                IntegerToFloat(instruction);
                break;
            case 21:
                ConditionalMove(instruction);
                break;
            default:
                if (group >= 24)
                {
                    WrapByExponent(instruction);
                }
                else
                {
                    RaiseException(AllegrexExceptionCode.ReservedInstruction);
                }

                break;
        }
    }

    /// <summary>The VFPU4 table: vmov vabs vneg vidt vsat0 vsat1 vzero vone, vrcp vrsq vsin vcos vexp2 vlog2 vsqrt vasin, vnrcp vnsin vrexp2.</summary>
    private void ExecuteVfpuUnary(uint instruction)
    {
        int operation = (int)((instruction >> 16) & 0x1F);
        switch (operation)
        {
            case 3:
                Identity(instruction);
                return;
            case 6:
            case 7:
                FillVector(instruction, operation == 7 ? ConstantOne : ConstantZero);
                return;
            case 0:
            case 1:
            case 2:
            case 4:
            case 5:
            case 24:
            case 26:
            case 28:
                break;
            default:
                if (operation < 16 || operation > 23)
                {
                    RaiseException(AllegrexExceptionCode.ReservedInstruction);
                    return;
                }

                break;
        }

        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        bool transcendental = operation >= 16;
        switch (operation)
        {
            case 1:
                ApplyPrefixST(s, RewritePrefix(VfpuSPrefix, 0, Absolute(1, 1, 1, 1)), size);
                break;
            case 2:
                ApplyPrefixST(s, RewritePrefix(VfpuSPrefix, 0, Negate(1, 1, 1, 1)), size);
                break;
            case 24:
            case 26:
                // like the others below, but the negate bit is ignored
                ApplyPrefixST(s.Slice(size - 1, 1), RewritePrefix(VfpuSPrefix, Negate(1, 0, 0, 0), 0), 1, 0xFF800000);
                break;
            case 28:
                ApplyPrefixST(s.Slice(size - 1, 1), RewritePrefix(VfpuSPrefix, Negate(1, 0, 0, 0), 0), 1, 0x7F800000);
                break;
            default:
                if (transcendental)
                {
                    // the prefix reaches the last lane only, read from prefix position 0
                    ApplyPrefixST(s.Slice(size - 1, 1), _vfpuControl[VfpuSPrefix], 1, 0x7F800000);
                }
                else
                {
                    ApplyPrefixS(s, size);
                }

                break;
        }

        for (int i = 0; i < size; i++)
        {
            d[i] = operation switch
            {
                0 or 1 or 2 => s[i],
                4 => SaturateZeroOne(s[i]),
                5 => SaturateMinusOneOne(s[i]),
                _ => AllegrexVfpuMath.Transcendental(operation, s[i]),
            };
        }

        if (operation == 5)
        {
            ApplyPrefixD(d, size, onlyWriteMask: true);
        }
        else if (transcendental)
        {
            if (LastLaneSwizzleInvalid(VfpuSPrefix))
            {
                d[size - 1] = 0;
            }

            RestrictDPrefixToLastLane(size);
            ApplyPrefixD(d, size);
        }
        else
        {
            ApplyPrefixD(d, size);
        }

        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vsat0: [0, 1], -0 to +0, NaN kept.</summary>
    private static uint SaturateZeroOne(uint bits)
    {
        float value = BitConverter.UInt32BitsToSingle(bits);
        if (value <= 0)
        {
            return 0;
        }

        return value > 1f ? 0x3F800000u : bits;
    }

    /// <summary>vsat1: [-1, 1], NaN kept.</summary>
    private static uint SaturateMinusOneOne(uint bits)
    {
        float value = BitConverter.UInt32BitsToSingle(bits);
        if (value < -1f)
        {
            return 0xBF800000;
        }

        return value > 1f ? 0x3F800000u : bits;
    }

    /// <summary>vidt: a row of the identity, the 1 at the lane matching the register's position; through S prefix constants.</summary>
    private void Identity(uint instruction)
    {
        int size = VfpuSize(instruction);
        int vd = VfpuD(instruction);
        int offsetMask = size == 4 || size == 3 ? 3 : 1;
        int offset = vd & offsetMask;
        Span<uint> d = ClearedLanes(_laneD);
        uint add = Constants(
            offset == (0 & offsetMask) ? ConstantOne : ConstantZero,
            offset == (1 & offsetMask) ? ConstantOne : ConstantZero,
            offset == (2 & offsetMask) ? ConstantOne : ConstantZero,
            offset == (3 & offsetMask) ? ConstantOne : ConstantZero);
        ApplyPrefixST(d, RewritePrefix(VfpuSPrefix, AnySwizzle, add), size);
        ApplyPrefixD(d, size);
        WriteVector(d, size, vd);
        EatPrefixes();
    }

    /// <summary>vzero/vone: S prefix constants (the negate bit still applies).</summary>
    private void FillVector(uint instruction, int constant)
    {
        int size = VfpuSize(instruction);
        Span<uint> d = ClearedLanes(_laneD);
        ApplyPrefixST(d, RewritePrefix(VfpuSPrefix, AnySwizzle, Constants(constant, constant, constant, constant)), size);
        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vcst: one of 32 constants (20 defined, the rest 0) in every lane.</summary>
    private void LoadConstant(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> d = ClearedLanes(_laneD);
        uint value = s_vcstConstants[(instruction >> 16) & 0x1F];
        for (int i = 0; i < 4; i++)
        {
            d[i] = value;
        }

        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>
    /// vf2in/vf2iz/vf2iu/vf2id: s * 2^imm to a word, rounded to nearest even, towards zero, up or down; saturating, NaN
    /// gives 0x7FFFFFFF, denormals count as zero. The D prefix masks but does not saturate.
    /// </summary>
    private void FloatToInteger(uint instruction, uint mode)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        double scale = Math.ScaleB(1.0, (int)((instruction >> 16) & 0x1F));
        for (int i = 0; i < size; i++)
        {
            if (AllegrexVfpuMath.IsNaN(s[i]))
            {
                d[i] = 0x7FFFFFFF;
                continue;
            }

            double value = (double)AllegrexVfpuMath.Input(s[i]) * scale;
            if (value > int.MaxValue)
            {
                d[i] = 0x7FFFFFFF;
            }
            else if (value <= int.MinValue)
            {
                d[i] = 0x80000000;
            }
            else
            {
                double rounded = mode switch
                {
                    16 => Math.Round(value, MidpointRounding.ToEven),
                    17 => Math.Truncate(value),
                    18 => Math.Ceiling(value),
                    _ => Math.Floor(value),
                };
                d[i] = (uint)(int)rounded;
            }
        }

        ApplyPrefixD(d, size, onlyWriteMask: true);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vi2f: a word times 2^-imm; the D prefix applies in full.</summary>
    private void IntegerToFloat(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        float scale = MathF.ScaleB(1f, -(int)((instruction >> 16) & 0x1F));
        for (int i = 0; i < size; i++)
        {
            float value = (int)s[i];
            d[i] = AllegrexVfpuMath.FlushDenormal(BitConverter.SingleToUInt32Bits(value * scale));
        }

        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>
    /// vcmovt/vcmovf: imm3 6 moves each lane whose CC bit is set (t) or clear (f); 0..5 test one CC bit for all lanes
    /// (7 tests a bit that is always clear). vd is read as T, with the T prefix.
    /// </summary>
    private void ConditionalMove(uint instruction)
    {
        int size = VfpuSize(instruction);
        int vd = VfpuD(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        ReadVector(d, size, vd);
        ApplyPrefixT(d, size);
        bool moveWhenSet = ((instruction >> 19) & 1) == 0;
        int imm3 = (int)((instruction >> 16) & 7);
        uint codes = imm3 == 7 ? 0 : _vfpuControl[VfpuConditionCodes];
        if (imm3 == 6)
        {
            for (int i = 0; i < size; i++)
            {
                if ((((codes >> i) & 1) != 0) == moveWhenSet)
                {
                    d[i] = s[i];
                }
            }
        }
        else if ((((codes >> imm3) & 1) != 0) == moveWhenSet)
        {
            for (int i = 0; i < size; i++)
            {
                d[i] = s[i];
            }
        }

        ApplyPrefixD(d, size);
        WriteVector(d, size, vd);
        EatPrefixes();
    }

    /// <summary>vwbn: lane 0 rescaled to the exponent imm8 (the mantissa shifted by the difference mod 16); the other lanes copied.</summary>
    private void WrapByExponent(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        uint exponent = (instruction >> 16) & 0xFF;
        uint sign = s[0] & 0x80000000;
        uint previous = (s[0] & 0x7F800000) >> 23;
        uint mantissa = (s[0] & 0x007FFFFF) | 0x00800000;
        if (previous != 0xFF && previous != 0)
        {
            if (exponent > previous)
            {
                mantissa >>= (int)((exponent - previous) & 0xF);
            }
            else
            {
                mantissa <<= (int)((previous - exponent) & 0xF);
            }

            s[0] = sign | (mantissa & 0x007FFFFF) | (exponent << 23);
        }
        else
        {
            s[0] |= exponent << 23;
        }

        ZeroInvalidSwizzles(s, size);
        ApplyPrefixD(s, size);
        WriteVector(s, size, VfpuD(instruction));
        EatPrefixes();
    }

    // ---- the VFPU7 table ----

    private void ExecuteVfpu7(uint instruction)
    {
        uint operation = (instruction >> 16) & 0x1F;
        switch (operation)
        {
            case 0:
                RandomSeed(instruction);
                break;
            case 1:
            case 2:
            case 3:
                RandomValues(instruction, operation);
                break;
            case 18:
                SingleToHalfVector(instruction);
                break;
            case 19:
                HalfToSingleVector(instruction);
                break;
            case 22:
                ScaleToZeroExponent(instruction);
                break;
            case 23:
                LogBinary(instruction);
                break;
            case 24:
            case 25:
            case 26:
            case 27:
                NarrowToInteger(instruction, operation & 3);
                break;
            case 28:
            case 29:
            case 30:
            case 31:
                IntegerToNarrow(instruction, operation & 3);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    /// <summary>vrnds: seeds the generator from vs (through the S prefix).</summary>
    private void RandomSeed(uint instruction)
    {
        Span<uint> s = ClearedLanes(_laneS);
        s[0] = _vfpr[VfpuIndex(VfpuS(instruction))];
        ApplyPrefixS(s, 1);
        AllegrexVfpuMath.SeedRandom(s[0], _vfpuControl.AsSpan(VfpuRandom0, 8));
        EatPrefixes();
    }

    /// <summary>vrndi (raw), vrndf1 ([1, 2)), vrndf2 ([2, 4)): written from the last lane back; D prefix on the last lane only.</summary>
    private void RandomValues(uint instruction, uint operation)
    {
        int size = VfpuSize(instruction);
        Span<uint> d = ClearedLanes(_laneD);
        Span<uint> state = _vfpuControl.AsSpan(VfpuRandom0, 8);
        for (int i = size - 1; i >= 0; i--)
        {
            uint value = AllegrexVfpuMath.NextRandom(state);
            d[i] = operation switch
            {
                1 => value,
                2 => 0x3F800000 | (value & 0x007FFFFF),
                _ => 0x40000000 | (value & 0x007FFFFF),
            };
        }

        RestrictDPrefixToLastLane(size);
        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vf2h: pairs of lanes into halves (a single or pair makes one word, a triple or quad two).</summary>
    private void SingleToHalfVector(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, 4);
        ZeroInvalidSwizzles(s, 4);
        int outputSize = size <= 2 ? 1 : 2;
        for (int i = 0; i < outputSize; i++)
        {
            d[i] = AllegrexVfpuMath.SingleToHalf(s[i * 2]) | ((uint)AllegrexVfpuMath.SingleToHalf(s[(i * 2) + 1]) << 16);
        }

        ApplyPrefixD(d, outputSize);
        WriteVector(d, outputSize, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vh2f: a single makes a pair, any larger size a quad (from the first two words).</summary>
    private void HalfToSingleVector(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        int words = size == 1 ? 1 : 2;
        for (int i = 0; i < words; i++)
        {
            d[i * 2] = AllegrexVfpuMath.HalfToSingle((ushort)s[i]);
            d[(i * 2) + 1] = AllegrexVfpuMath.HalfToSingle((ushort)(s[i] >> 16));
        }

        ApplyPrefixD(d, words * 2);
        WriteVector(d, words * 2, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vsbz: lane 0 gets exponent 0 (value in [1, 2)); NaN and denormals pass through; the other lanes copied.</summary>
    private void ScaleToZeroExponent(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        if (!AllegrexVfpuMath.IsNaN(s[0]) && (s[0] & 0x7F800000) != 0)
        {
            s[0] = (127u << 23) | (s[0] & 0x007FFFFF);
        }

        ApplyPrefixD(s, size);
        WriteVector(s, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vlgb: lane 0's unbiased exponent as a float (-inf for zero/denormal, inf/NaN kept); the other lanes copied.</summary>
    private void LogBinary(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        uint exponent = (s[0] & 0x7F800000) >> 23;
        if (exponent == 0)
        {
            s[0] = 0xFF800000;
        }
        else if (exponent != 0xFF)
        {
            s[0] = BitConverter.SingleToUInt32Bits((int)exponent - 127);
        }

        ZeroInvalidSwizzles(s, size);
        ApplyPrefixD(s, size);
        WriteVector(s, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vuc2ifs, vc2i, vus2i, vs2i: bytes or halves widened to words (saturation does apply).</summary>
    private void NarrowToInteger(uint instruction, uint kind)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        int outputSize;
        switch (kind)
        {
            case 0:
            {
                uint value = s[0];
                for (int i = 0; i < 4; i++)
                {
                    d[i] = ((value & 0xFF) * 0x01010101u) >> 1;
                    value >>= 8;
                }

                outputSize = 4;
                break;
            }
            case 1:
            {
                uint value = s[0];
                d[0] = (value & 0xFF) << 24;
                d[1] = (value & 0xFF00) << 16;
                d[2] = (value & 0xFF0000) << 8;
                d[3] = value & 0xFF000000;
                outputSize = 4;
                break;
            }
            default:
            {
                int words = size == 1 ? 1 : 2;
                outputSize = words * 2;
                for (int i = 0; i < words; i++)
                {
                    uint value = s[i];
                    if (kind == 2)
                    {
                        d[i * 2] = (value & 0xFFFF) << 15;
                        d[(i * 2) + 1] = (value & 0xFFFF0000) >> 1;
                    }
                    else
                    {
                        d[i * 2] = (value & 0xFFFF) << 16;
                        d[(i * 2) + 1] = value & 0xFFFF0000;
                    }
                }

                break;
            }
        }

        ApplyPrefixD(d, outputSize);
        WriteVector(d, outputSize, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vi2uc, vi2c, vi2us, vi2s: words narrowed to bytes or halves (unsigned forms clamp negatives to 0).</summary>
    private void IntegerToNarrow(uint instruction, uint kind)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, 4);
        int outputSize;
        switch (kind)
        {
            case 0:
                for (int i = 0; i < 4; i++)
                {
                    int value = Math.Max((int)s[i], 0) >> 23;
                    d[0] |= ((uint)value & 0xFF) << (i * 8);
                }

                outputSize = 1;
                break;
            case 1:
                for (int i = 0; i < 4; i++)
                {
                    d[0] |= (s[i] >> 24) << (i * 8);
                }

                outputSize = 1;
                break;
            default:
            {
                int words = (size + 1) / 2;
                for (int i = 0; i < words; i++)
                {
                    if (kind == 2)
                    {
                        uint low = (uint)(Math.Max((int)s[i * 2], 0) >> 15);
                        uint high = (uint)(Math.Max((int)s[(i * 2) + 1], 0) >> 15);
                        d[i] = low | (high << 16);
                    }
                    else
                    {
                        d[i] = (s[i * 2] >> 16) | ((s[(i * 2) + 1] >> 16) << 16);
                    }
                }

                outputSize = size >= 3 ? 2 : 1;
                break;
            }
        }

        ApplyPrefixD(d, outputSize);
        WriteVector(d, outputSize, VfpuD(instruction));
        EatPrefixes();
    }

    // ---- the VFPU9 table ----

    private void ExecuteVfpu9(uint instruction)
    {
        uint operation = (instruction >> 16) & 0x1F;
        switch (operation)
        {
            case 0:
            case 1:
            case 8:
            case 9:
                SortStep(instruction, operation);
                break;
            case 2:
            case 3:
                Butterfly(instruction, operation == 3);
                break;
            case 4:
                OneComplement(instruction);
                break;
            case 5:
                SignedOneComplement(instruction);
                break;
            case 6:
            case 7:
                FunnelAdd(instruction, operation == 7);
                break;
            case 10:
                SignOf(instruction);
                break;
            case 16:
                MoveFromVfpuControl(instruction);
                break;
            case 17:
                MoveToVfpuControl(instruction);
                break;
            case 25:
            case 26:
            case 27:
                ColorConvert(instruction, operation - 24);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    /// <summary>vsrt1..4: compare-exchange of lane pairs (T is S with a forced swizzle), ties as the hardware keeps them.</summary>
    private void SortStep(uint instruction, uint operation)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, size);
        ReadVector(t, size, VfpuS(instruction));
        uint swizzle = operation == 0 || operation == 8 ? Swizzle(1, 0, 3, 2) : Swizzle(3, 2, 1, 0);
        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, Swizzle(3, 3, 3, 3), swizzle), size);
        switch (operation)
        {
            case 0:
                d[0] = Minimum(t[0], s[0]);
                d[1] = Maximum(s[1], t[1]);
                d[2] = Minimum(t[2], s[2]);
                d[3] = Maximum(s[3], t[3]);
                break;
            case 1:
                d[0] = Minimum(t[0], s[0]);
                d[1] = Minimum(t[1], s[1]);
                d[2] = Maximum(s[2], t[2]);
                d[3] = Maximum(s[3], t[3]);
                break;
            case 8:
                d[0] = Maximum(s[0], t[0]);
                d[1] = Minimum(t[1], s[1]);
                d[2] = Maximum(s[2], t[2]);
                d[3] = Minimum(t[3], s[3]);
                break;
            default:
                d[0] = Maximum(s[0], t[0]);
                d[1] = Maximum(s[1], t[1]);
                d[2] = Minimum(t[2], s[2]);
                d[3] = Minimum(t[3], s[3]);
                break;
        }

        ZeroInvalidSwizzles(d, size);
        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vbfy1 (x+y, x-y, z+w, z-w) and vbfy2 (x+z, y+w, x-z, y-w), through forced prefixes.</summary>
    private void Butterfly(uint instruction, bool second)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ReadVector(t, size, VfpuS(instruction));
        if (second)
        {
            ApplyPrefixST(s, RewritePrefix(VfpuSPrefix, 0, Negate(0, 0, 1, 1)), size);
            ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, AnySwizzle, Swizzle(2, 3, 0, 1)), size);
        }
        else
        {
            ApplyPrefixST(s, RewritePrefix(VfpuSPrefix, 0, Negate(0, 1, 0, 1)), size);
            ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, AnySwizzle, Swizzle(1, 0, 3, 2)), size);
        }

        for (int i = 0; i < 4; i++)
        {
            d[i] = AllegrexVfpuMath.Add(s[i], t[i]);
        }

        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vocp: 1 - s (S forced negated, T forced to the constant 1); a NaN comes out positive.</summary>
    private void OneComplement(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixST(s, RewritePrefix(VfpuSPrefix, 0, Negate(1, 1, 1, 1)), size);
        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, AnySwizzle, Constants(ConstantOne, ConstantOne, ConstantOne, ConstantOne)), size);
        for (int i = 0; i < size; i++)
        {
            d[i] = AllegrexVfpuMath.IsNaN(s[i]) ? s[i] & 0x7FFFFFFF : AllegrexVfpuMath.Add(t[i], s[i]);
        }

        ZeroInvalidSwizzles(d, size);
        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vsocp: (1 - x, x, 1 - y, y) clamped to [0, 1]; a single makes a pair, a pair a quad.</summary>
    private void SignedOneComplement(uint instruction)
    {
        int size = VfpuSize(instruction);
        int outputSize = size == 1 ? 2 : 4;
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixST(s, RewritePrefix(VfpuSPrefix, AnySwizzle | Negate(1, 1, 1, 1), Swizzle(0, 0, 1, 1) | Negate(1, 0, 1, 0)), outputSize);
        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, AnySwizzle, Constants(ConstantOne, ConstantZero, ConstantOne, ConstantZero)), outputSize);
        for (int i = 0; i < outputSize; i++)
        {
            d[i] = ClampKeepingNaN(AllegrexVfpuMath.Add(t[i], s[i]), 0, 0x3F800000);
        }

        ApplyPrefixD(d, size, onlyWriteMask: true);
        WriteVector(d, outputSize, VfpuD(instruction));
        EatPrefixes();
    }

    private static uint ClampKeepingNaN(uint bits, uint minimumBits, uint maximumBits)
    {
        float value = BitConverter.UInt32BitsToSingle(bits);
        if (value <= BitConverter.UInt32BitsToSingle(minimumBits))
        {
            value = BitConverter.UInt32BitsToSingle(minimumBits);
        }

        if (value >= BitConverter.UInt32BitsToSingle(maximumBits))
        {
            return maximumBits;
        }

        return BitConverter.SingleToUInt32Bits(value);
    }

    /// <summary>vfad (the sum of the lanes) and vavg (their mean), as dot products with T prefix constants.</summary>
    private void FunnelAdd(uint instruction, bool average)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixS(s, 4);
        uint remove = AnySwizzle;
        int constant = ConstantOne;
        if (average)
        {
            remove |= Absolute(1, 1, 1, 1);
            constant = size switch
            {
                1 => ConstantZero,
                2 => ConstantHalf,
                3 => ConstantThird,
                _ => ConstantFourth,
            };
        }

        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, remove, Constants(constant, constant, constant, constant)), 4);
        WriteSingleResult(AllegrexVfpuMath.Dot(s, t), instruction);
    }

    /// <summary>vsgn: -1, 0 or 1 by the sign of s - t, T forced to zero constants; a NaN's sign counts, a denormal is 0.</summary>
    private void SignOf(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> t = ClearedLanes(_laneT);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, size, VfpuS(instruction));
        ApplyPrefixST(t, RewritePrefix(VfpuTPrefix, AnySwizzle, Constants(ConstantZero, ConstantZero, ConstantZero, ConstantZero)), size);
        for (int i = size; i < 4; i++)
        {
            s[i] = t[i];
        }

        ApplyPrefixS(s, 4);
        for (int i = 0; i < size; i++)
        {
            uint difference;
            if (AllegrexVfpuMath.IsNaN(s[i]))
            {
                difference = s[i];
            }
            else if (AllegrexVfpuMath.IsNaN(t[i]))
            {
                difference = t[i];
            }
            else
            {
                difference = BitConverter.SingleToUInt32Bits(AllegrexVfpuMath.Single(s[i]) - AllegrexVfpuMath.Single(t[i]));
            }

            if ((difference & 0x7F800000) == 0)
            {
                d[i] = 0;
            }
            else
            {
                d[i] = (difference & 0x80000000) == 0 ? 0x3F800000u : 0xBF800000u;
            }
        }

        ApplyPrefixD(d, size);
        WriteVector(d, size, VfpuD(instruction));
        EatPrefixes();
    }

    /// <summary>vt4444 (1), vt5551 (2), vt5650 (3): four 8888 colours to 16-bit ones (a pair of words).</summary>
    private void ColorConvert(uint instruction, uint format)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = ClearedLanes(_laneS);
        Span<uint> d = ClearedLanes(_laneD);
        ReadVector(s, 4, VfpuS(instruction));
        ApplyPrefixS(s, 4);
        Span<uint> colors = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
        {
            uint color = s[i];
            uint red = color & 0xFF;
            uint green = (color >> 8) & 0xFF;
            uint blue = (color >> 16) & 0xFF;
            uint alpha = color >> 24;
            colors[i] = format switch
            {
                1 => ((alpha >> 4) << 12) | ((blue >> 4) << 8) | ((green >> 4) << 4) | (red >> 4),
                2 => ((alpha >> 7) << 15) | ((blue >> 3) << 10) | ((green >> 3) << 5) | (red >> 3),
                _ => ((blue >> 3) << 11) | ((green >> 2) << 5) | (red >> 3),
            };
        }

        d[0] = colors[0] | (colors[1] << 16);
        d[1] = colors[2] | (colors[3] << 16);
        ApplyPrefixD(d, 2);
        WriteVector(d, size == 1 ? 1 : 2, VfpuD(instruction));
        EatPrefixes();
    }
}
