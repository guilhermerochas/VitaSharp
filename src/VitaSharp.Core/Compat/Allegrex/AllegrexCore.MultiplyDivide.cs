namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// HI/LO: mult/multu, div/divu with the hardware's division-by-zero results, the Allegrex madd/maddu/msub/msubu
/// accumulators, mfhi/mthi/mflo/mtlo.
/// </summary>
public sealed partial class AllegrexCore
{
    ulong HiLo
    {
        get => ((ulong)Hi << 32) | Lo;
        set
        {
            Hi = (uint)(value >> 32);
            Lo = (uint)value;
        }
    }

    void Multiply(uint instruction)
    {
        HiLo = (ulong)((long)(int)_gpr[Rs(instruction)] * (int)_gpr[Rt(instruction)]);
    }

    void MultiplyUnsigned(uint instruction)
    {
        HiLo = (ulong)_gpr[Rs(instruction)] * _gpr[Rt(instruction)];
    }

    void MultiplyAdd(uint instruction)
    {
        HiLo += (ulong)((long)(int)_gpr[Rs(instruction)] * (int)_gpr[Rt(instruction)]);
    }

    void MultiplyAddUnsigned(uint instruction)
    {
        HiLo += (ulong)_gpr[Rs(instruction)] * _gpr[Rt(instruction)];
    }

    void MultiplySubtract(uint instruction)
    {
        HiLo -= (ulong)((long)(int)_gpr[Rs(instruction)] * (int)_gpr[Rt(instruction)]);
    }

    void MultiplySubtractUnsigned(uint instruction)
    {
        HiLo -= (ulong)_gpr[Rs(instruction)] * _gpr[Rt(instruction)];
    }

    /// <summary>
    /// div: LO = quotient, HI = remainder. MIPS32 leaves two cases unpredictable; the PSP's results (pspautotests
    /// cpu_alu/cpu_div, as PPSSPP records them): 0x80000000 / -1 gives LO = 0x80000000, HI = 0; a division by zero
    /// gives LO = 1 for a negative dividend, else 0xFFFFFFFF, and HI = the dividend. No exception either way.
    /// </summary>
    void Divide(uint instruction)
    {
        int dividend = (int)_gpr[Rs(instruction)];
        int divisor = (int)_gpr[Rt(instruction)];
        if (dividend == int.MinValue && divisor == -1)
        {
            Lo = 0x80000000;
            Hi = 0;
        }
        else if (divisor == 0)
        {
            Lo = dividend < 0 ? 1u : 0xFFFFFFFFu;
            Hi = (uint)dividend;
        }
        else
        {
            Lo = (uint)(dividend / divisor);
            Hi = (uint)(dividend % divisor);
        }
    }

    /// <summary>divu: a division by zero gives LO = 0xFFFF for a dividend up to 0xFFFF, else 0xFFFFFFFF, and HI = the dividend.</summary>
    void DivideUnsigned(uint instruction)
    {
        uint dividend = _gpr[Rs(instruction)];
        uint divisor = _gpr[Rt(instruction)];
        if (divisor == 0)
        {
            Lo = dividend <= 0xFFFF ? 0xFFFFu : 0xFFFFFFFFu;
            Hi = dividend;
        }
        else
        {
            Lo = dividend / divisor;
            Hi = dividend % divisor;
        }
    }
}
