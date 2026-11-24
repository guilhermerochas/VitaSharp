namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>The instruction decoder: the primary opcode table and its sub-tables (SPECIAL, REGIMM, SPECIAL2/3, COP0).</summary>
public sealed partial class AllegrexCore
{
    private static int Rs(uint instruction)
    {
        return (int)((instruction >> 21) & 31);
    }

    private static int Rt(uint instruction)
    {
        return (int)((instruction >> 16) & 31);
    }

    private static int Rd(uint instruction)
    {
        return (int)((instruction >> 11) & 31);
    }

    private static int Sa(uint instruction)
    {
        return (int)((instruction >> 6) & 31);
    }

    private static uint SignedImmediate(uint instruction)
    {
        return (uint)(int)(short)(ushort)instruction;
    }

    private static uint UnsignedImmediate(uint instruction)
    {
        return instruction & 0xFFFF;
    }

    private void Execute(uint instruction)
    {
        switch (instruction >> 26)
        {
            case 0x00:
                ExecuteSpecial(instruction);
                break;
            case 0x01:
                ExecuteRegImm(instruction);
                break;
            case 0x02:
                Jump(instruction);
                break;
            case 0x03:
                JumpAndLink(instruction);
                break;
            case 0x04:
                BranchIf(_gpr[Rs(instruction)] == _gpr[Rt(instruction)], instruction);
                break;
            case 0x05:
                BranchIf(_gpr[Rs(instruction)] != _gpr[Rt(instruction)], instruction);
                break;
            case 0x06:
                BranchIf((int)_gpr[Rs(instruction)] <= 0, instruction);
                break;
            case 0x07:
                BranchIf((int)_gpr[Rs(instruction)] > 0, instruction);
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
            case 0x10:
                ExecuteCop0(instruction);
                break;
            case 0x11:
                ExecuteCop1(instruction);
                break;
            case 0x31:
                LoadWordToFpu(instruction);
                break;
            case 0x39:
                StoreWordFromFpu(instruction);
                break;
            case 0x12:
                ExecuteCop2(instruction);
                break;
            case 0x32:
            case 0x35:
            case 0x36:
            case 0x3A:
            case 0x3D:
            case 0x3E:
                ExecuteVfpuMemory(instruction);
                break;
            case 0x37:
                ExecuteVfpu5(instruction);
                break;
            case 0x3F:
                ExecuteVfpuFlush(instruction);
                break;
            case 0x18:
                ExecuteVfpu0(instruction);
                break;
            case 0x19:
                ExecuteVfpu1(instruction);
                break;
            case 0x1B:
                ExecuteVfpu3(instruction);
                break;
            case 0x34:
                ExecuteVfpu4Jump(instruction);
                break;
            case 0x3C:
                ExecuteVfpu6(instruction);
                break;
            case 0x14:
                BranchLikelyIf(_gpr[Rs(instruction)] == _gpr[Rt(instruction)], instruction);
                break;
            case 0x15:
                BranchLikelyIf(_gpr[Rs(instruction)] != _gpr[Rt(instruction)], instruction);
                break;
            case 0x16:
                BranchLikelyIf((int)_gpr[Rs(instruction)] <= 0, instruction);
                break;
            case 0x17:
                BranchLikelyIf((int)_gpr[Rs(instruction)] > 0, instruction);
                break;
            case 0x1C:
                ExecuteSpecial2(instruction);
                break;
            case 0x1F:
                ExecuteSpecial3(instruction);
                break;
            case 0x2F:
                // cache: caches are not modelled
                break;
            case 0x20:
            case 0x21:
            case 0x22:
            case 0x23:
            case 0x24:
            case 0x25:
            case 0x26:
            case 0x30:
                ExecuteLoad(instruction);
                break;
            case 0x28:
            case 0x29:
            case 0x2A:
            case 0x2B:
            case 0x2E:
            case 0x38:
                ExecuteStore(instruction);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    private void ExecuteSpecial(uint instruction)
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
            case 0x08:
                JumpRegister(instruction);
                break;
            case 0x09:
                JumpAndLinkRegister(instruction);
                break;
            case 0x0A:
                MoveIfZero(instruction);
                break;
            case 0x0B:
                MoveIfNotZero(instruction);
                break;
            case 0x0C:
                SystemCall(instruction, AllegrexTrapKind.Syscall);
                break;
            case 0x0D:
                SystemCall(instruction, AllegrexTrapKind.Break);
                break;
            case 0x0F:
                // sync: accesses are already in order
                break;
            case 0x16:
                CountLeadingZeros(instruction);
                break;
            case 0x17:
                CountLeadingOnes(instruction);
                break;
            case 0x10:
                _gpr[rd] = Hi;
                break;
            case 0x11:
                Hi = _gpr[rs];
                break;
            case 0x12:
                _gpr[rd] = Lo;
                break;
            case 0x13:
                Lo = _gpr[rs];
                break;
            case 0x18:
                Multiply(instruction);
                break;
            case 0x19:
                MultiplyUnsigned(instruction);
                break;
            case 0x1A:
                Divide(instruction);
                break;
            case 0x1B:
                DivideUnsigned(instruction);
                break;
            case 0x1C:
                MultiplyAdd(instruction);
                break;
            case 0x1D:
                MultiplyAddUnsigned(instruction);
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
            case 0x30:
            case 0x31:
            case 0x32:
            case 0x33:
            case 0x34:
            case 0x36:
                ExecuteTrap(instruction);
                break;
            case 0x2E:
                MultiplySubtract(instruction);
                break;
            case 0x2F:
                MultiplySubtractUnsigned(instruction);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }
}
