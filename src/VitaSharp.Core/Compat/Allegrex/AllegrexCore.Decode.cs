namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>The instruction decoder: the primary opcode table and its sub-tables (SPECIAL, REGIMM, SPECIAL2/3, COP0).</summary>
public sealed partial class AllegrexCore
{
    static int Rs(uint instruction)
    {
        return (int)((instruction >> 21) & 31);
    }

    static int Rt(uint instruction)
    {
        return (int)((instruction >> 16) & 31);
    }

    static int Rd(uint instruction)
    {
        return (int)((instruction >> 11) & 31);
    }

    static int Sa(uint instruction)
    {
        return (int)((instruction >> 6) & 31);
    }

    static uint SignedImmediate(uint instruction)
    {
        return (uint)(int)(short)(ushort)instruction;
    }

    static uint UnsignedImmediate(uint instruction)
    {
        return instruction & 0xFFFF;
    }

    void Execute(uint instruction)
    {
        switch (instruction >> 26)
        {
            case 0x00:
                ExecuteSpecial(instruction);
                break;
            case 0x08:
                AddImmediate(instruction);
                break;
            case 0x09:
                _gpr[Rt(instruction)] = _gpr[Rs(instruction)] + SignedImmediate(instruction);
                break;
            case 0x0A:
                _gpr[Rt(instruction)] = (int)_gpr[Rs(instruction)] < (int)SignedImmediate(instruction) ? 1u : 0u;
                break;
            case 0x0B:
                _gpr[Rt(instruction)] = _gpr[Rs(instruction)] < SignedImmediate(instruction) ? 1u : 0u;
                break;
            case 0x0C:
                _gpr[Rt(instruction)] = _gpr[Rs(instruction)] & UnsignedImmediate(instruction);
                break;
            case 0x0D:
                _gpr[Rt(instruction)] = _gpr[Rs(instruction)] | UnsignedImmediate(instruction);
                break;
            case 0x0E:
                _gpr[Rt(instruction)] = _gpr[Rs(instruction)] ^ UnsignedImmediate(instruction);
                break;
            case 0x0F:
                _gpr[Rt(instruction)] = instruction << 16;
                break;
            case 0x1F:
                ExecuteSpecial3(instruction);
                break;
            default:
                Unimplemented(instruction, "integer");
                break;
        }
    }

    void ExecuteSpecial(uint instruction)
    {
        int rs = Rs(instruction);
        int rt = Rt(instruction);
        int rd = Rd(instruction);
        switch (instruction & 0x3F)
        {
            case 0x00:
                _gpr[rd] = _gpr[rt] << Sa(instruction);
                break;
            case 0x02:
                ShiftRightLogicalOrRotate(instruction);
                break;
            case 0x03:
                _gpr[rd] = (uint)((int)_gpr[rt] >> Sa(instruction));
                break;
            case 0x04:
                _gpr[rd] = _gpr[rt] << (int)(_gpr[rs] & 31);
                break;
            case 0x06:
                ShiftRightLogicalOrRotateVariable(instruction);
                break;
            case 0x07:
                _gpr[rd] = (uint)((int)_gpr[rt] >> (int)(_gpr[rs] & 31));
                break;
            case 0x0A:
                MoveIfZero(instruction);
                break;
            case 0x0B:
                MoveIfNotZero(instruction);
                break;
            case 0x16:
                CountLeadingZeros(instruction);
                break;
            case 0x17:
                CountLeadingOnes(instruction);
                break;
            case 0x20:
                Add(instruction);
                break;
            case 0x21:
                _gpr[rd] = _gpr[rs] + _gpr[rt];
                break;
            case 0x22:
                Subtract(instruction);
                break;
            case 0x23:
                _gpr[rd] = _gpr[rs] - _gpr[rt];
                break;
            case 0x24:
                _gpr[rd] = _gpr[rs] & _gpr[rt];
                break;
            case 0x25:
                _gpr[rd] = _gpr[rs] | _gpr[rt];
                break;
            case 0x26:
                _gpr[rd] = _gpr[rs] ^ _gpr[rt];
                break;
            case 0x27:
                _gpr[rd] = ~(_gpr[rs] | _gpr[rt]);
                break;
            case 0x2A:
                _gpr[rd] = (int)_gpr[rs] < (int)_gpr[rt] ? 1u : 0u;
                break;
            case 0x2B:
                _gpr[rd] = _gpr[rs] < _gpr[rt] ? 1u : 0u;
                break;
            case 0x2C:
                Maximum(instruction);
                break;
            case 0x2D:
                Minimum(instruction);
                break;
            default:
                Unimplemented(instruction, "integer");
                break;
        }
    }
}
