namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>The integer ALU: add/sub with the overflow exception, and the shifts (rotr/rotrv share srl/srlv).</summary>
public sealed partial class AllegrexCore
{
    /// <summary>add: as addu, but a signed overflow raises the overflow exception and leaves rd unchanged (MIPS32).</summary>
    private void Add(uint instruction)
    {
        uint left = _gpr[Rs(instruction)];
        uint right = _gpr[Rt(instruction)];
        uint sum = left + right;
        if (((left ^ sum) & (right ^ sum) & 0x80000000) != 0)
        {
            RaiseException(AllegrexExceptionCode.Overflow);
            return;
        }

        _gpr[Rd(instruction)] = sum;
    }

    private void AddImmediate(uint instruction)
    {
        uint left = _gpr[Rs(instruction)];
        uint right = SignedImmediate(instruction);
        uint sum = left + right;
        if (((left ^ sum) & (right ^ sum) & 0x80000000) != 0)
        {
            RaiseException(AllegrexExceptionCode.Overflow);
            return;
        }

        _gpr[Rt(instruction)] = sum;
    }

    private void Subtract(uint instruction)
    {
        uint left = _gpr[Rs(instruction)];
        uint right = _gpr[Rt(instruction)];
        uint difference = left - right;
        if (((left ^ right) & (left ^ difference) & 0x80000000) != 0)
        {
            RaiseException(AllegrexExceptionCode.Overflow);
            return;
        }

        _gpr[Rd(instruction)] = difference;
    }

    /// <summary>srl, or rotr when bit 0 of the (otherwise unused) rs field is set: the Allegrex decodes only that bit.</summary>
    private void ShiftRightLogicalOrRotate(uint instruction)
    {
        uint value = _gpr[Rt(instruction)];
        int amount = Sa(instruction);
        if ((Rs(instruction) & 1) != 0)
        {
            _gpr[Rd(instruction)] = uint.RotateRight(value, amount);
        }
        else
        {
            _gpr[Rd(instruction)] = value >> amount;
        }
    }

    /// <summary>srlv, or rotrv when bit 0 of the (otherwise unused) sa field is set.</summary>
    private void ShiftRightLogicalOrRotateVariable(uint instruction)
    {
        uint value = _gpr[Rt(instruction)];
        int amount = (int)(_gpr[Rs(instruction)] & 31);
        if ((Sa(instruction) & 1) != 0)
        {
            _gpr[Rd(instruction)] = uint.RotateRight(value, amount);
        }
        else
        {
            _gpr[Rd(instruction)] = value >> amount;
        }
    }
}
