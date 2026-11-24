namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// System instructions: syscall/break, the conditional traps, sync and cache (nothing to do without modelled caches),
/// COP0 moves and eret, halt/wait, mfic/mtic, and the FPU/VFPU instructions that are not emulated yet.
/// </summary>
public sealed partial class AllegrexCore
{
    /// <summary>syscall/break: offered to <see cref="TrapHandler"/>, else the syscall (SCCode = code &lt;&lt; 2) or breakpoint exception.</summary>
    private void SystemCall(uint instruction, AllegrexTrapKind kind)
    {
        uint code = (instruction >> 6) & 0xFFFFF;
        AllegrexTrapHandler? handler = TrapHandler;
        if (handler != null && handler(this, kind, code, _currentPc))
        {
            return;
        }

        if (kind == AllegrexTrapKind.Syscall)
        {
            _cop0[Cop0SyscallCode] = code << 2;
            RaiseException(AllegrexExceptionCode.Syscall);
        }
        else
        {
            RaiseException(AllegrexExceptionCode.Breakpoint);
        }
    }

    private void TrapIf(bool condition)
    {
        if (condition)
        {
            RaiseException(AllegrexExceptionCode.Trap);
        }
    }

    private void ExecuteTrap(uint instruction)
    {
        uint left = _gpr[Rs(instruction)];
        uint right = _gpr[Rt(instruction)];
        switch (instruction & 0x3F)
        {
            case 0x30:
                TrapIf((int)left >= (int)right);
                break;
            case 0x31:
                TrapIf(left >= right);
                break;
            case 0x32:
                TrapIf((int)left < (int)right);
                break;
            case 0x33:
                TrapIf(left < right);
                break;
            case 0x34:
                TrapIf(left == right);
                break;
            case 0x36:
                TrapIf(left != right);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    private void ExecuteCop0(uint instruction)
    {
        int rt = Rt(instruction);
        int rd = Rd(instruction);
        int format = Rs(instruction);
        if (format >= 0x10)
        {
            switch (instruction & 0x3F)
            {
                case 0x18:
                    ReturnFromException();
                    break;
                case 0x20:
                    _halted = true;
                    break;
                default:
                    RaiseException(AllegrexExceptionCode.ReservedInstruction);
                    break;
            }

            return;
        }

        switch (format)
        {
            case 0x00:
                _gpr[rt] = ReadCop0(rd);
                break;
            case 0x02:
                _gpr[rt] = _cop0Control[rd];
                break;
            case 0x04:
                WriteCop0(rd, _gpr[rt]);
                break;
            case 0x06:
                _cop0Control[rd] = _gpr[rt];
                break;
            case 0x0A:
                // rdpgpr: one register set, so a plain move
                _gpr[rd] = _gpr[rt];
                break;
            case 0x0B:
                // mfmc0 (di/ei, MIPS32 release 2): rt = Status, then Status.IE = the sc bit
                _gpr[rt] = _cop0[Cop0Status];
                if ((instruction & 0x20) != 0)
                {
                    _cop0[Cop0Status] |= StatusIe;
                }
                else
                {
                    _cop0[Cop0Status] &= ~StatusIe;
                }

                break;
            case 0x0E:
                // wrpgpr: one register set, so a plain move
                _gpr[rd] = _gpr[rt];
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    /// <summary>eret (MIPS32): to ErrorEPC clearing Status.ERL when it is set, else to EPC clearing Status.EXL; no delay slot; clears the LL bit.</summary>
    private void ReturnFromException()
    {
        uint status = _cop0[Cop0Status];
        _linkBit = false;
        if ((status & StatusErl) != 0)
        {
            _cop0[Cop0Status] = status & ~StatusErl;
            Pc = _cop0[Cop0ErrorEpc];
        }
        else
        {
            _cop0[Cop0Status] = status & ~StatusExl;
            Pc = _cop0[Cop0Epc];
        }
    }

    /// <summary>SPECIAL2: halt, mfic rt and mtic rt (only $ic, register 0, exists; only bit 0 of the value counts).</summary>
    private void ExecuteSpecial2(uint instruction)
    {
        switch (instruction & 0x3F)
        {
            case 0x00:
                _halted = true;
                break;
            case 0x24:
                _gpr[Rt(instruction)] = _interruptControl;
                break;
            case 0x26:
                _interruptControl = _gpr[Rt(instruction)] & 1;
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }
}
