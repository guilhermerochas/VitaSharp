namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// Branches and jumps, all with one delay slot. When a branch executes, <c>_pc</c> already holds its delay slot (the
/// branch address + 4): the slot always runs, except for the branch-likely forms, which skip (annul) it when not
/// taken. A linking branch writes ra = branch + 8 whether or not it is taken.
/// </summary>
public sealed partial class AllegrexCore
{
    private void BranchIf(bool condition, uint instruction)
    {
        _nextIsDelaySlot = true;
        if (condition)
        {
            _nextPc = _pc + (SignedImmediate(instruction) << 2);
        }
    }

    private void BranchLikelyIf(bool condition, uint instruction)
    {
        if (condition)
        {
            _nextIsDelaySlot = true;
            _nextPc = _pc + (SignedImmediate(instruction) << 2);
        }
        else
        {
            // the delay slot is annulled: the straight line of the block ends here
            _pc = _nextPc;
            _nextPc += 4;
            _endBlock = true;
        }
    }

    private void Jump(uint instruction)
    {
        _nextIsDelaySlot = true;
        _nextPc = (_pc & 0xF0000000) | ((instruction & 0x03FFFFFF) << 2);
    }

    private void JumpAndLink(uint instruction)
    {
        _gpr[31] = _pc + 4;
        Jump(instruction);
    }

    private void JumpRegister(uint instruction)
    {
        _nextIsDelaySlot = true;
        _nextPc = _gpr[Rs(instruction)];
    }

    /// <summary>jalr rd, rs: the target is read before rd is written.</summary>
    private void JumpAndLinkRegister(uint instruction)
    {
        uint target = _gpr[Rs(instruction)];
        _gpr[Rd(instruction)] = _pc + 4;
        _nextIsDelaySlot = true;
        _nextPc = target;
    }

    private void ExecuteRegImm(uint instruction)
    {
        int value = (int)_gpr[Rs(instruction)];
        switch (Rt(instruction))
        {
            case 0x00:
                BranchIf(value < 0, instruction);
                break;
            case 0x01:
                BranchIf(value >= 0, instruction);
                break;
            case 0x02:
                BranchLikelyIf(value < 0, instruction);
                break;
            case 0x03:
                BranchLikelyIf(value >= 0, instruction);
                break;
            case 0x08:
                TrapIf(value >= (int)SignedImmediate(instruction));
                break;
            case 0x09:
                TrapIf((uint)value >= SignedImmediate(instruction));
                break;
            case 0x0A:
                TrapIf(value < (int)SignedImmediate(instruction));
                break;
            case 0x0B:
                TrapIf((uint)value < SignedImmediate(instruction));
                break;
            case 0x0C:
                TrapIf((uint)value == SignedImmediate(instruction));
                break;
            case 0x0E:
                TrapIf((uint)value != SignedImmediate(instruction));
                break;
            case 0x10:
                _gpr[31] = _pc + 4;
                BranchIf(value < 0, instruction);
                break;
            case 0x11:
                _gpr[31] = _pc + 4;
                BranchIf(value >= 0, instruction);
                break;
            case 0x12:
                _gpr[31] = _pc + 4;
                BranchLikelyIf(value < 0, instruction);
                break;
            case 0x13:
                _gpr[31] = _pc + 4;
                BranchLikelyIf(value >= 0, instruction);
                break;
            case 0x1F:
                // synci: instruction cache sync; caches are not modelled
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }
}
