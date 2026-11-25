namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// Shortcuts the block cache calls for the FPU and VFPU instructions it decoded once: the instruction goes straight to
/// its handler instead of down the decoder's switches, and the common cases take a short path with the same result:
/// add.s/sub.s/mul.s of two normal operands with a normal result in round-to-nearest (the result and the inexact cause
/// as <see cref="Arithmetic"/> gives them), and vadd/vsub/vmul/vdot with the S, T and D prefixes at rest (no swizzle,
/// constant, saturation or write mask to apply). Anything else takes the full handler.
/// </summary>
public sealed partial class AllegrexCore
{
    /// <summary>A COP1 instruction other than a branch.</summary>
    private void ExecuteCop1Direct(uint instruction)
    {
        if (Rs(instruction) == 0x10)
        {
            ExecuteFpuSingleDirect(instruction);
            return;
        }

        ExecuteCop1(instruction);
    }

    /// <summary>add.s/sub.s/mul.s as the block decoded them (fd, fs, ft, the function).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void FpuArithmeticDirect(ref DecodedInstruction d)
    {
        if (!FpuArithmeticAtNearest(d.Immediate, _fpr[d.S], _fpr[d.T], d.D))
        {
            ExecuteFpuSingle(d.Word);
        }
    }

    private void ExecuteFpuSingleDirect(uint instruction)
    {
        uint function = instruction & 0x3F;
        if (function > 2 || !FpuArithmeticAtNearest(function, _fpr[Fs(instruction)], _fpr[Ft(instruction)], Fd(instruction)))
        {
            ExecuteFpuSingle(instruction);
        }
    }

    /// <summary>add.s (0), sub.s (1), mul.s (2) of normal or zero operands in round-to-nearest with a normal or zero
    /// result: committed, true; anything else false (nothing done).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool FpuArithmeticAtNearest(uint function, uint left, uint right, int fd)
    {
        if ((_fcr31 & 3) == 0 && IsNormalOrZeroBits(left) && IsNormalOrZeroBits(right))
        {
            float a = BitConverter.UInt32BitsToSingle(left);
            float b = BitConverter.UInt32BitsToSingle(right);
            double exact;
            bool inexact;
            float nearest;
            if (function == 2)
            {
                // a product of two floats is exact in double
                exact = (double)a * b;
                nearest = (float)exact;
                inexact = nearest != exact;
            }
            else
            {
                double y = function == 0 ? b : -(double)b;
                exact = a + y;
                nearest = (float)exact;
                int gap = (int)((left >> 23) & 0xFF) - (int)((right >> 23) & 0xFF);
                if (gap >= -28 && gap <= 28)
                {
                    // the exponents 28 apart or less: the sum of the two 24-bit significands fits the double exactly
                    inexact = nearest != exact;
                }
                else
                {
                    // two-sum: a + y = exact + error exactly
                    double rightPart = exact - a;
                    double leftPart = exact - rightPart;
                    double error = (a - leftPart) + (y - rightPart);
                    inexact = nearest != exact || error != 0;
                }
            }

            if (exact == 0)
            {
                // an exact zero: IEEE's sign in round-to-nearest, no exception (as Arithmetic gives it)
                CommitFpu(fd, BitConverter.SingleToUInt32Bits(nearest), 0);
                return true;
            }

            double magnitude = Math.Abs(exact);
            if (magnitude >= SmallestNormal && magnitude <= float.MaxValue)
            {
                CommitFpu(fd, BitConverter.SingleToUInt32Bits(nearest), inexact ? FpuInexact : 0);
                return true;
            }
        }

        return false;
    }

    private static bool IsNormalOrZeroBits(uint bits)
    {
        uint exponent = (bits >> 23) & 0xFF;
        return exponent != 0xFF && (exponent != 0 || (bits & 0x7FFFFF) == 0);
    }

    /// <summary>The IL tier's add.s/sub.s/mul.s.</summary>
    private void FpuArithmeticCompiled(uint word, int fd, int fs, int ft, uint function)
    {
        if (!FpuArithmeticAtNearest(function, _fpr[fs], _fpr[ft], fd))
        {
            ExecuteFpuSingle(word);
        }
    }

    /// <summary>vadd/vsub/vmul as the block decoded them (the lanes, the count, the operation).</summary>
    private void VfpuLanesDirect(ref DecodedInstruction d)
    {
        VfpuLanesCompiled(d.Word, d.D, d.Immediate, d.LanesS, d.LanesT, d.LanesD);
    }

    /// <summary>vadd/vsub/vmul from the decoded fields (the IL tier calls it with them as constants).</summary>
    private void VfpuLanesCompiled(uint word, int size, uint operation, uint lanesS, uint lanesT, uint lanesD)
    {
        if (!VfpuPrefixesAtRest())
        {
            ExecuteVfpuOriginal(word);
            return;
        }

        uint[] v = _vfpr;
        uint[] results = _laneD;
        for (int i = 0; i < size; i++)
        {
            uint s = v[(lanesS >> (i * 8)) & 0x7F];
            uint t = v[(lanesT >> (i * 8)) & 0x7F];
            results[i] = operation switch
            {
                0 => AllegrexVfpuMath.Add(s, t),
                1 => AllegrexVfpuMath.Subtract(s, t),
                _ => AllegrexVfpuMath.Multiply(s, t),
            };
        }

        for (int i = 0; i < size; i++)
        {
            v[(lanesD >> (i * 8)) & 0x7F] = results[i];
        }
    }

    /// <summary>vdot as the block decoded it.</summary>
    private void VfpuDotDirect(ref DecodedInstruction d)
    {
        VfpuDotCompiled(d.Word, d.D, d.LanesS, d.LanesT, d.LanesD);
    }

    /// <summary>vdot from the decoded fields (the IL tier calls it with them as constants).</summary>
    private void VfpuDotCompiled(uint word, int size, uint lanesS, uint lanesT, uint lanesD)
    {
        if (!VfpuPrefixesAtRest())
        {
            ExecuteVfpuOriginal(word);
            return;
        }

        uint[] v = _vfpr;
        Span<uint> s = _laneS.AsSpan(0, 4);
        Span<uint> t = _laneT.AsSpan(0, 4);
        for (int i = 0; i < 4; i++)
        {
            s[i] = i < size ? v[(lanesS >> (i * 8)) & 0x7F] : 0;
            t[i] = i < size ? v[(lanesT >> (i * 8)) & 0x7F] : 0;
        }

        v[lanesD & 0x7F] = SumResult(AllegrexVfpuMath.Dot(s, t));
    }

    private void ExecuteVfpuOriginal(uint instruction)
    {
        if ((instruction >> 26) == 0x18)
        {
            ExecuteVfpu0(instruction);
        }
        else
        {
            ExecuteVfpu1(instruction);
        }
    }

    private bool VfpuPrefixesAtRest()
    {
        return _vfpuControl[VfpuSPrefix] == VfpuIdentityPrefix && _vfpuControl[VfpuTPrefix] == VfpuIdentityPrefix && _vfpuControl[VfpuDPrefix] == 0;
    }

    /// <summary>Opcode 0x18: vadd and vsub with the prefixes at rest directly, the rest through <see cref="ExecuteVfpu0"/>.</summary>
    private void ExecuteVfpu0Direct(uint instruction)
    {
        uint operation = (instruction >> 23) & 7;
        if (operation <= 1 && VfpuPrefixesAtRest())
        {
            LaneArithmeticAtRest(instruction, (int)operation);
            return;
        }

        ExecuteVfpu0(instruction);
    }

    /// <summary>Opcode 0x19: vmul and vdot with the prefixes at rest directly, the rest through <see cref="ExecuteVfpu1"/>.</summary>
    private void ExecuteVfpu1Direct(uint instruction)
    {
        uint operation = (instruction >> 23) & 7;
        if (operation <= 1 && VfpuPrefixesAtRest())
        {
            if (operation == 0)
            {
                LaneArithmeticAtRest(instruction, 8);
            }
            else
            {
                DotProductAtRest(instruction);
            }

            return;
        }

        ExecuteVfpu1(instruction);
    }

    /// <summary><see cref="LaneArithmetic"/> for vadd (0), vsub (1) and vmul (8) with nothing for the prefixes to do.</summary>
    private void LaneArithmeticAtRest(uint instruction, int operation)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = stackalloc uint[4];
        Span<uint> t = stackalloc uint[4];
        ReadVector(s, size, VfpuS(instruction));
        ReadVector(t, size, VfpuT(instruction));
        for (int i = 0; i < size; i++)
        {
            s[i] = operation switch
            {
                0 => AllegrexVfpuMath.Add(s[i], t[i]),
                1 => AllegrexVfpuMath.Subtract(s[i], t[i]),
                _ => AllegrexVfpuMath.Multiply(s[i], t[i]),
            };
        }

        WriteVector(s, size, VfpuD(instruction));
    }

    /// <summary><see cref="DotProduct"/> with nothing for the prefixes to do.</summary>
    private void DotProductAtRest(uint instruction)
    {
        int size = VfpuSize(instruction);
        Span<uint> s = stackalloc uint[4];
        Span<uint> t = stackalloc uint[4];
        s.Clear();
        t.Clear();
        ReadVector(s, size, VfpuS(instruction));
        ReadVector(t, size, VfpuT(instruction));
        _vfpr[VfpuIndex(VfpuD(instruction))] = SumResult(AllegrexVfpuMath.Dot(s, t));
    }
}
