namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The FPU (COP1): 32 single-precision registers (kept as bits, so NaN payloads survive), FCR0 = 0x3351 and FCR31
/// (rounding mode, the five IEEE flags with their enables and causes, the condition bit 23, flush-to-zero bit 24).
/// Results are computed exactly in software for every rounding mode, so they do not depend on the host's FPU mode.
/// PSP behaviour from PPSSPP's hardware-checked notes (docs/cpu-equivalence.md, pspautotests cpu/fpu): a NaN made from
/// non-NaN operands is 0x7FC00000 (sqrt of a negative, inf - inf, 0 * inf, 0 / 0); with FS set, denormal results are
/// flushed to zero with tininess decided before rounding, inputs are never flushed, and add/sub with a zero operand
/// return the other operand; round.w.s rounds half to even whatever the mode, cvt.w.s follows the mode; conversions
/// saturate (NaN gives 0x7FFFFFFF). An enabled exception takes the floating-point exception (ExcCode 15) and leaves
/// the destination alone, as MIPS32 defines.
/// </summary>
public sealed partial class AllegrexCore
{
    public const uint Fcr0Value = 0x00003351;
    public const uint Fcr31WriteMask = 0x0181FFFF;
    public const uint Fcr31Condition = 0x00800000;
    public const uint Fcr31FlushToZero = 0x01000000;

    /// <summary>The IEEE exceptions in FCR31 field order (flags at bit 2, enables at bit 7, causes at bit 12).</summary>
    public const uint FpuInexact = 0x01;
    public const uint FpuUnderflow = 0x02;
    public const uint FpuOverflow = 0x04;
    public const uint FpuDivideByZero = 0x08;
    public const uint FpuInvalid = 0x10;

    private const uint FpuDefaultNaN = 0x7FC00000;
    private const uint FpuCauseMask = 0x0003F000;
    private const double SmallestNormal = 1.1754943508222875E-38; // 2^-126

    private readonly uint[] _fpr = new uint[32];
    private uint _fcr31;

    private enum FpuOperation
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        SquareRoot,
    }

    /// <summary>The FPU registers as their bits.</summary>
    public uint[] Fpr => _fpr;

    /// <summary>FCR31 as cfc1 reads it; writing keeps only the bits ctc1 keeps.</summary>
    public uint Fcr31
    {
        get => _fcr31;
        set => _fcr31 = value & Fcr31WriteMask;
    }

    /// <summary>The FPU condition (FCR31 bit 23) that c.cond sets and bc1t/bc1f test.</summary>
    public bool FpuCondition => (_fcr31 & Fcr31Condition) != 0;

    private static int Ft(uint instruction)
    {
        return (int)((instruction >> 16) & 31);
    }

    private static int Fs(uint instruction)
    {
        return (int)((instruction >> 11) & 31);
    }

    private static int Fd(uint instruction)
    {
        return (int)((instruction >> 6) & 31);
    }

    private static bool IsNaNBits(uint bits)
    {
        return (bits & 0x7F800000) == 0x7F800000 && (bits & 0x007FFFFF) != 0;
    }

    private static bool IsSignalingNaNBits(uint bits)
    {
        return IsNaNBits(bits) && (bits & 0x00400000) == 0;
    }

    private static bool IsZeroBits(uint bits)
    {
        return (bits & 0x7FFFFFFF) == 0;
    }

    private static bool IsDenormalBits(uint bits)
    {
        return (bits & 0x7F800000) == 0 && (bits & 0x007FFFFF) != 0;
    }

    private void ExecuteCop1(uint instruction)
    {
        int rt = Rt(instruction);
        int fs = Fs(instruction);
        switch (Rs(instruction))
        {
            case 0x00:
                _gpr[rt] = _fpr[fs];
                break;
            case 0x02:
                if (fs == 31)
                {
                    _gpr[rt] = _fcr31;
                }
                else if (fs == 0)
                {
                    _gpr[rt] = Fcr0Value;
                }
                else
                {
                    _gpr[rt] = 0;
                }

                break;
            case 0x04:
                _fpr[fs] = _gpr[rt];
                break;
            case 0x06:
                if (fs == 31)
                {
                    _fcr31 = _gpr[rt] & Fcr31WriteMask;
                }

                break;
            case 0x08:
                FpuBranch(instruction);
                break;
            case 0x10:
                ExecuteFpuSingle(instruction);
                break;
            case 0x14:
                if ((instruction & 0x3F) == 0x20)
                {
                    ConvertWordToSingle(instruction);
                }
                else
                {
                    RaiseException(AllegrexExceptionCode.ReservedInstruction);
                }

                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    private void FpuBranch(uint instruction)
    {
        bool condition = FpuCondition;
        switch (Rt(instruction))
        {
            case 0:
                BranchIf(!condition, instruction);
                break;
            case 1:
                BranchIf(condition, instruction);
                break;
            case 2:
                BranchLikelyIf(!condition, instruction);
                break;
            case 3:
                BranchLikelyIf(condition, instruction);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    private void ExecuteFpuSingle(uint instruction)
    {
        int fd = Fd(instruction);
        uint source = _fpr[Fs(instruction)];
        uint target = _fpr[Ft(instruction)];
        uint function = instruction & 0x3F;
        if (function >= 0x30)
        {
            Compare(instruction, source, target);
            return;
        }

        uint exceptions;
        uint result;
        switch (function)
        {
            case 0x00:
                result = Arithmetic(FpuOperation.Add, source, target, out exceptions);
                break;
            case 0x01:
                result = Arithmetic(FpuOperation.Subtract, source, target, out exceptions);
                break;
            case 0x02:
                result = Arithmetic(FpuOperation.Multiply, source, target, out exceptions);
                break;
            case 0x03:
                result = Arithmetic(FpuOperation.Divide, source, target, out exceptions);
                break;
            case 0x04:
                result = Arithmetic(FpuOperation.SquareRoot, source, 0, out exceptions);
                break;
            case 0x05:
                _fpr[fd] = source & 0x7FFFFFFF;
                return;
            case 0x06:
                _fpr[fd] = source;
                return;
            case 0x07:
                _fpr[fd] = source ^ 0x80000000;
                return;
            case 0x0C:
                result = ConvertToWord(source, MidpointRounding.ToEven, out exceptions);
                break;
            case 0x0D:
                result = ConvertToWord(source, MidpointRounding.ToZero, out exceptions);
                break;
            case 0x0E:
                result = ConvertToWord(source, MidpointRounding.ToPositiveInfinity, out exceptions);
                break;
            case 0x0F:
                result = ConvertToWord(source, MidpointRounding.ToNegativeInfinity, out exceptions);
                break;
            case 0x24:
                result = ConvertToWord(source, RoundingModeOf(_fcr31), out exceptions);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                return;
        }

        CommitFpu(fd, result, exceptions);
    }

    /// <summary>Sets the causes; an enabled one takes the exception (fd unchanged), else the flags gather them.</summary>
    private void CommitFpu(int fd, uint result, uint exceptions)
    {
        _fcr31 = (_fcr31 & ~FpuCauseMask) | (exceptions << 12);
        uint enabled = (_fcr31 >> 7) & 0x1F;
        if ((exceptions & enabled) != 0)
        {
            RaiseException(AllegrexExceptionCode.FloatingPoint);
            return;
        }

        _fcr31 |= exceptions << 2;
        _fpr[fd] = result;
    }

    private static MidpointRounding RoundingModeOf(uint fcr31)
    {
        switch (fcr31 & 3)
        {
            case 0:
                return MidpointRounding.ToEven;
            case 1:
                return MidpointRounding.ToZero;
            case 2:
                return MidpointRounding.ToPositiveInfinity;
            default:
                return MidpointRounding.ToNegativeInfinity;
        }
    }

    /// <summary>
    /// add/sub/mul/div/sqrt. The double result <c>d</c> rounds correctly to the nearest float (double has more than
    /// twice the precision); the sign of the exact result minus that float (an error-free sum, the exact product,
    /// or an FMA residual) moves it one step for the directed modes and gives the inexact flag.
    /// </summary>
    private uint Arithmetic(FpuOperation operation, uint leftBits, uint rightBits, out uint exceptions)
    {
        exceptions = 0;
        bool unary = operation == FpuOperation.SquareRoot;
        if (IsNaNBits(leftBits) || (!unary && IsNaNBits(rightBits)))
        {
            if (IsSignalingNaNBits(leftBits) || (!unary && IsSignalingNaNBits(rightBits)))
            {
                exceptions = FpuInvalid;
            }

            uint nan = IsNaNBits(leftBits) ? leftBits : rightBits;
            return nan | 0x00400000;
        }

        bool flush = (_fcr31 & Fcr31FlushToZero) != 0;
        if (flush && (operation == FpuOperation.Add || operation == FpuOperation.Subtract))
        {
            // the PSP returns the other operand when one is zero, even a denormal one it would otherwise flush
            if (IsZeroBits(leftBits) && IsDenormalBits(rightBits))
            {
                return operation == FpuOperation.Add ? rightBits : rightBits ^ 0x80000000;
            }

            if (IsZeroBits(rightBits) && IsDenormalBits(leftBits))
            {
                return leftBits;
            }
        }

        double left = BitConverter.UInt32BitsToSingle(leftBits);
        double right = BitConverter.UInt32BitsToSingle(rightBits);
        if (operation == FpuOperation.Subtract)
        {
            right = -right;
        }

        double exact;
        switch (operation)
        {
            case FpuOperation.Add:
            case FpuOperation.Subtract:
                if (double.IsInfinity(left) && double.IsInfinity(right) && left != right)
                {
                    exceptions = FpuInvalid;
                    return FpuDefaultNaN;
                }

                exact = left + right;
                break;
            case FpuOperation.Multiply:
                if ((double.IsInfinity(left) && right == 0) || (double.IsInfinity(right) && left == 0))
                {
                    exceptions = FpuInvalid;
                    return FpuDefaultNaN;
                }

                exact = left * right;
                break;
            case FpuOperation.Divide:
                if ((left == 0 && right == 0) || (double.IsInfinity(left) && double.IsInfinity(right)))
                {
                    exceptions = FpuInvalid;
                    return FpuDefaultNaN;
                }

                if (right == 0 && !double.IsInfinity(left))
                {
                    exceptions = FpuDivideByZero;
                }

                exact = left / right;
                break;
            default:
                if (left < 0)
                {
                    exceptions = FpuInvalid;
                    return FpuDefaultNaN;
                }

                exact = Math.Sqrt(left);
                break;
        }

        float nearest = (float)exact;
        if (double.IsInfinity(exact))
        {
            // an exact infinity: from an infinite operand or a division by zero
            return BitConverter.SingleToUInt32Bits(nearest);
        }

        if (exact == 0)
        {
            // an exact zero; a sum of opposite values is -0 only when rounding towards -inf (IEEE 754)
            bool sum = operation == FpuOperation.Add || operation == FpuOperation.Subtract;
            if (sum && RoundingModeOf(_fcr31) == MidpointRounding.ToNegativeInfinity && !(left == 0 && right == 0 && !double.IsNegative(left) && !double.IsNegative(right)))
            {
                return 0x80000000;
            }

            return BitConverter.SingleToUInt32Bits(nearest);
        }

        int residual = Residual(operation, left, right, exact, nearest);
        float rounded = RoundDirected(nearest, residual, RoundingModeOf(_fcr31));
        if (residual != 0)
        {
            exceptions |= FpuInexact;
        }

        if (Math.Abs(exact) > float.MaxValue && (float.IsInfinity(nearest) || RoundingModeOf(_fcr31) != MidpointRounding.ToEven))
        {
            exceptions |= FpuOverflow | FpuInexact;
        }

        if (Math.Abs(exact) < SmallestNormal)
        {
            if (flush)
            {
                exceptions |= FpuUnderflow | FpuInexact;
                return exact < 0 ? 0x80000000u : 0u;
            }

            if (residual != 0)
            {
                exceptions |= FpuUnderflow;
            }
        }

        return BitConverter.SingleToUInt32Bits(rounded);
    }

    /// <summary>The sign of (the exact result - <paramref name="nearest"/>).</summary>
    private static int Residual(FpuOperation operation, double left, double right, double rounded, float nearest)
    {
        if (float.IsInfinity(nearest))
        {
            return nearest > 0 ? -1 : 1;
        }

        double candidate = nearest;
        switch (operation)
        {
            case FpuOperation.Add:
            case FpuOperation.Subtract:
            {
                // two-sum: left + right = rounded + error exactly
                double rightPart = rounded - left;
                double leftPart = rounded - rightPart;
                double error = (left - leftPart) + (right - rightPart);
                double difference = rounded - candidate;
                return difference != 0 ? Math.Sign(difference) : Math.Sign(error);
            }
            case FpuOperation.Multiply:
                return Math.Sign(rounded - candidate);
            case FpuOperation.Divide:
                return Math.Sign(Math.FusedMultiplyAdd(-candidate, right, left)) * Math.Sign(right);
            default:
                return Math.Sign(Math.FusedMultiplyAdd(-candidate, candidate, left));
        }
    }

    /// <summary>Moves a round-to-nearest result one step when the exact value lies on the other side for the mode.</summary>
    private static float RoundDirected(float nearest, int residual, MidpointRounding mode)
    {
        if (residual == 0)
        {
            return nearest;
        }

        switch (mode)
        {
            case MidpointRounding.ToPositiveInfinity:
                return residual > 0 ? MathF.BitIncrement(nearest) : nearest;
            case MidpointRounding.ToNegativeInfinity:
                return residual < 0 ? MathF.BitDecrement(nearest) : nearest;
            case MidpointRounding.ToZero:
                if (nearest > 0 && residual < 0)
                {
                    return MathF.BitDecrement(nearest);
                }

                if (nearest < 0 && residual > 0)
                {
                    return MathF.BitIncrement(nearest);
                }

                return nearest;
            default:
                return nearest;
        }
    }

    /// <summary>To a word: NaN gives 0x7FFFFFFF, out of range saturates (both invalid), a dropped fraction is inexact.</summary>
    private static uint ConvertToWord(uint bits, MidpointRounding mode, out uint exceptions)
    {
        exceptions = 0;
        if (IsNaNBits(bits))
        {
            exceptions = FpuInvalid;
            return 0x7FFFFFFF;
        }

        double value = BitConverter.UInt32BitsToSingle(bits);
        double rounded = Math.Round(value, mode);
        if (rounded >= 2147483648.0)
        {
            exceptions = FpuInvalid;
            return 0x7FFFFFFF;
        }

        if (rounded < -2147483648.0)
        {
            exceptions = FpuInvalid;
            return 0x80000000;
        }

        if (rounded != value)
        {
            exceptions = FpuInexact;
        }

        return (uint)(int)rounded;
    }

    private void ConvertWordToSingle(uint instruction)
    {
        double exact = (int)_fpr[Fs(instruction)];
        float nearest = (float)exact;
        int residual = Math.Sign(exact - nearest);
        float rounded = RoundDirected(nearest, residual, RoundingModeOf(_fcr31));
        CommitFpu(Fd(instruction), BitConverter.SingleToUInt32Bits(rounded), residual != 0 ? FpuInexact : 0);
    }

    /// <summary>
    /// c.cond.s: bit 0 of the condition = true when unordered, bit 1 when equal, bit 2 when less; bit 3 makes a NaN
    /// operand signal invalid (a quiet compare signals only for a signaling NaN). Inputs are never flushed.
    /// </summary>
    private void Compare(uint instruction, uint sourceBits, uint targetBits)
    {
        uint condition = instruction & 0xF;
        bool unordered = IsNaNBits(sourceBits) || IsNaNBits(targetBits);
        uint exceptions = 0;
        bool result;
        if (unordered)
        {
            result = (condition & 1) != 0;
            if ((condition & 8) != 0 || IsSignalingNaNBits(sourceBits) || IsSignalingNaNBits(targetBits))
            {
                exceptions = FpuInvalid;
            }
        }
        else
        {
            float source = BitConverter.UInt32BitsToSingle(sourceBits);
            float target = BitConverter.UInt32BitsToSingle(targetBits);
            result = ((condition & 2) != 0 && source == target) || ((condition & 4) != 0 && source < target);
        }

        _fcr31 = (_fcr31 & ~FpuCauseMask) | (exceptions << 12);
        uint enabled = (_fcr31 >> 7) & 0x1F;
        if ((exceptions & enabled) != 0)
        {
            RaiseException(AllegrexExceptionCode.FloatingPoint);
            return;
        }

        _fcr31 |= exceptions << 2;
        _fcr31 = result ? _fcr31 | Fcr31Condition : _fcr31 & ~Fcr31Condition;
    }

    private void LoadWordToFpu(uint instruction)
    {
        uint address = EffectiveAddress(instruction);
        if ((address & 3) != 0)
        {
            AddressError(address, AllegrexExceptionCode.AddressErrorLoad);
            return;
        }

        _fpr[Ft(instruction)] = _bus.Read32(address & PhysicalMask);
    }

    private void StoreWordFromFpu(uint instruction)
    {
        uint address = EffectiveAddress(instruction);
        if ((address & 3) != 0)
        {
            AddressError(address, AllegrexExceptionCode.AddressErrorStore);
            return;
        }

        Store32(address & PhysicalMask, _fpr[Ft(instruction)]);
    }
}
