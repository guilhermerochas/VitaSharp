namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// Interpreter of the Allegrex, the PSP's CPU: a little-endian MIPS32 integer core of the R4000 family without the
/// 64-bit instructions, plus the Allegrex additions (ext/ins, seb/seh, wsbh/wsbw, bitrev, clz/clo, min/max,
/// madd/msub(u), movz/movn, rotr/rotrv, halt, mfic/mtic). The Vita SoC has one, which runs the PSP compatibility
/// firmware under SceCompat (docs/notes/psp-compat.md). No TLB: every address goes to the bus as
/// <c>address &amp; 0x1FFFFFFF</c> (the 0x0/0x4/0x8/0xA segments are mirrors). Caches are not modelled (the cache
/// instruction does nothing). COP0 holds what the PSP kernel uses: Status, Cause, EPC, ErrorEPC, EBase, BadVAddr,
/// Count, the syscall code, the CPU id and the 32 control registers of cfc0/ctc0; every exception and interrupt goes to
/// EBase (0xBFC00200 while Status.BEV is set) as on the PSP. The FPU (COP1) and VFPU (COP2) are phase 2: their
/// instructions reach <see cref="TrapHandler"/> as <see cref="AllegrexTrapKind.Unimplemented"/>.
/// Not thread-safe: one thread calls <see cref="Run"/>, <see cref="RaiseInterrupt"/> and the accessors.
/// Sources: MIPS32 architecture manual (exceptions, delay slots), PPSSPP's interpreter and its hardware-checked notes
/// (pspautotests cpu_alu: division by zero, the Allegrex encodings, the ME reset state and interrupt gating) and Jpcsp's
/// COP0 layout, studied for behaviour only.
/// </summary>
public sealed partial class AllegrexCore
{
    /// <summary>The segment bits the core removes before an access reaches the bus.</summary>
    public const uint PhysicalMask = 0x1FFFFFFF;

    /// <summary>The exception vector while Status.BEV is set (the boot ROM area).</summary>
    public const uint BootExceptionVector = 0xBFC00200;

    /// <summary>Cause.IP lines: 0 and 1 are the software interrupts, 2..7 the hardware lines.</summary>
    public const int InterruptLineCount = 8;

    /// <summary>The hardware line the PSP's interrupt controller drives (Cause.IP2).</summary>
    public const int InterruptControllerLine = 2;

    public const int Cop0BadVAddr = 8;
    public const int Cop0Count = 9;
    public const int Cop0Compare = 11;
    public const int Cop0Status = 12;
    public const int Cop0Cause = 13;
    public const int Cop0Epc = 14;
    public const int Cop0Config = 16;
    public const int Cop0SyscallCode = 21;
    public const int Cop0CpuId = 22;
    public const int Cop0EBase = 25;
    public const int Cop0ErrorEpc = 30;

    public const uint StatusIe = 0x00000001;
    public const uint StatusExl = 0x00000002;
    public const uint StatusErl = 0x00000004;
    public const uint StatusInterruptMask = 0x0000FF00;
    public const uint StatusBev = 0x00400000;

    /// <summary>Status after a reset: CU1, BEV, SR, IM2 and ERL (PPSSPP's probe of a PSP-1000's ME core).</summary>
    public const uint ResetStatus = 0x20500404;

    /// <summary>Config: 16 KB instruction and data caches in the MIPS size encoding (Jpcsp).</summary>
    public const uint ResetConfig = 0x00000480;

    public const uint CauseBranchDelay = 0x80000000;
    public const uint CauseInterruptPending = 0x0000FF00;
    public const uint CauseSoftwareInterrupts = 0x00000300;
    public const uint CauseExceptionCodeMask = 0x0000007C;

    readonly IAllegrexBus _bus;
    readonly uint[] _gpr = new uint[32];
    readonly uint[] _cop0 = new uint[32];
    readonly uint[] _cop0Control = new uint[32];

    uint _pc;
    uint _nextPc;
    bool _nextIsDelaySlot;

    // the instruction being executed (exceptions report it)
    uint _currentPc;
    bool _currentInDelaySlot;

    uint _hardwareLines;     // Cause.IP2..IP7 as driven by RaiseInterrupt/ClearInterrupt (Cause bit positions)
    uint _interruptControl;  // the mtic/mfic register ($ic)
    uint _countOffset;       // Count = executed instructions + offset
    bool _linkBit;
    bool _halted;
    bool _stopped;
    long _executedInstructions;

    public AllegrexCore(IAllegrexBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        Reset(BootExceptionVector - 0x200);
    }

    /// <summary>The bus the core reads and writes.</summary>
    public IAllegrexBus Bus => _bus;

    /// <summary>The general registers; r0 reads 0 whatever is written to it.</summary>
    public uint[] Gpr => _gpr;

    public uint Hi { get; set; }

    public uint Lo { get; set; }

    /// <summary>The address of the next instruction. Setting it starts there (no pending branch).</summary>
    public uint Pc
    {
        get => _pc;
        set
        {
            _pc = value;
            _nextPc = value + 4;
            _nextIsDelaySlot = false;
        }
    }

    /// <summary>True after halt or wait, until an interrupt line unmasked in Status.IM is raised.</summary>
    public bool Halted => _halted;

    /// <summary>True after <see cref="Stop"/>: <see cref="Run"/> does nothing until <see cref="Reset"/>.</summary>
    public bool Stopped => _stopped;

    /// <summary>The instructions executed since the last reset.</summary>
    public long ExecutedInstructions => _executedInstructions;

    /// <summary>The interrupt control register of mfic/mtic: interrupts are taken when bit 0 or Status.IE is set.</summary>
    public uint InterruptControl
    {
        get => _interruptControl;
        set => _interruptControl = value & 1;
    }

    /// <summary>The LL bit of ll/sc.</summary>
    public bool LinkBit => _linkBit;

    /// <summary>Offered syscall, break and not-yet-emulated instructions first (see <see cref="AllegrexTrapHandler"/>).</summary>
    public AllegrexTrapHandler? TrapHandler { get; set; }

    public uint Status
    {
        get => _cop0[Cop0Status];
        set => _cop0[Cop0Status] = value;
    }

    /// <summary>Cause, with IP2..IP7 showing the hardware lines.</summary>
    public uint Cause => (_cop0[Cop0Cause] & ~(CauseInterruptPending & ~CauseSoftwareInterrupts)) | _hardwareLines;

    public uint Epc
    {
        get => _cop0[Cop0Epc];
        set => _cop0[Cop0Epc] = value;
    }

    public uint ErrorEpc
    {
        get => _cop0[Cop0ErrorEpc];
        set => _cop0[Cop0ErrorEpc] = value;
    }

    public uint EBase
    {
        get => _cop0[Cop0EBase];
        set => _cop0[Cop0EBase] = value;
    }

    /// <summary>Resets the core and starts it at <paramref name="pc"/>; the general registers keep their values.</summary>
    public void Reset(uint pc)
    {
        Array.Clear(_cop0);
        Array.Clear(_cop0Control);
        _cop0[Cop0Status] = ResetStatus;
        _cop0[Cop0Config] = ResetConfig;
        _cop0[Cop0ErrorEpc] = _pc;
        _hardwareLines = 0;
        _interruptControl = 0;
        _countOffset = 0;
        _linkBit = false;
        _halted = false;
        _stopped = false;
        _executedInstructions = 0;
        _gpr[0] = 0;
        Pc = pc;
    }

    /// <summary>Stops the core: <see cref="Run"/> returns at once until the next <see cref="Reset"/>.</summary>
    public void Stop()
    {
        _stopped = true;
    }

    /// <summary>Drives Cause.IP[<paramref name="line"/>] high (2..7: hardware lines; 0 and 1 set the software bits).</summary>
    public void RaiseInterrupt(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, InterruptLineCount);
        uint bit = 1u << (8 + line);
        if (line < InterruptControllerLine)
        {
            _cop0[Cop0Cause] |= bit;
        }
        else
        {
            _hardwareLines |= bit;
        }
    }

    /// <summary>Drives Cause.IP[<paramref name="line"/>] low again.</summary>
    public void ClearInterrupt(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, InterruptLineCount);
        uint bit = 1u << (8 + line);
        if (line < InterruptControllerLine)
        {
            _cop0[Cop0Cause] &= ~bit;
        }
        else
        {
            _hardwareLines &= ~bit;
        }
    }

    /// <summary>Reads a COP0 register as mfc0 does.</summary>
    public uint ReadCop0(int register)
    {
        switch (register & 31)
        {
            case Cop0Count:
                return (uint)_executedInstructions + _countOffset;
            case Cop0Cause:
                return Cause;
            default:
                return _cop0[register & 31];
        }
    }

    /// <summary>Writes a COP0 register as mtc0 does (Cause keeps its read-only fields; the CPU id is read-only).</summary>
    public void WriteCop0(int register, uint value)
    {
        switch (register & 31)
        {
            case Cop0Count:
                _countOffset = value - (uint)_executedInstructions;
                break;
            case Cop0Cause:
                _cop0[Cop0Cause] = (_cop0[Cop0Cause] & ~CauseSoftwareInterrupts) | (value & CauseSoftwareInterrupts);
                break;
            case Cop0CpuId:
                break;
            default:
                _cop0[register & 31] = value;
                break;
        }
    }

    /// <summary>Reads a COP0 control register (cfc0).</summary>
    public uint ReadCop0Control(int register)
    {
        return _cop0Control[register & 31];
    }

    /// <summary>Writes a COP0 control register (ctc0).</summary>
    public void WriteCop0Control(int register, uint value)
    {
        _cop0Control[register & 31] = value;
    }

    /// <summary>
    /// Runs up to <paramref name="cycles"/> instructions (one cycle each for now). Returns early, with the number
    /// executed, when the core halts (halt/wait with no unmasked interrupt pending) or is stopped.
    /// </summary>
    public int Run(int cycles)
    {
        int executed = 0;
        while (executed < cycles && !_stopped)
        {
            if (((Cause & _cop0[Cop0Status]) & CauseInterruptPending) != 0)
            {
                _halted = false;
                if (InterruptsAccepted())
                {
                    _currentPc = _pc;
                    _currentInDelaySlot = _nextIsDelaySlot;
                    RaiseException(AllegrexExceptionCode.Interrupt);
                }
            }

            if (_halted)
            {
                break;
            }

            Step();
            executed++;
        }

        return executed;
    }

    bool InterruptsAccepted()
    {
        uint status = _cop0[Cop0Status];
        bool enabled = (status & StatusIe) != 0 || _interruptControl != 0;
        return enabled && (status & (StatusExl | StatusErl)) == 0;
    }

    void Step()
    {
        uint pc = _pc;
        _currentPc = pc;
        _currentInDelaySlot = _nextIsDelaySlot;
        _nextIsDelaySlot = false;
        _executedInstructions++;
        if ((pc & 3) != 0)
        {
            AddressError(pc, AllegrexExceptionCode.AddressErrorLoad);
            return;
        }

        uint instruction = _bus.Read32(pc & PhysicalMask);
        _pc = _nextPc;
        _nextPc += 4;
        Execute(instruction);
        _gpr[0] = 0;
    }

    /// <summary>
    /// Takes an exception for the current instruction: EPC (unless Status.EXL is already set) gets the instruction, or
    /// the branch before it when it is in a delay slot (Cause.BD), Cause.ExcCode the code, Status.EXL is set and the
    /// core goes to EBase (0xBFC00200 with Status.BEV).
    /// </summary>
    void RaiseException(AllegrexExceptionCode code)
    {
        uint status = _cop0[Cop0Status];
        uint cause = _cop0[Cop0Cause] & ~(CauseExceptionCodeMask | CauseBranchDelay);
        cause |= (uint)code << 2;
        if ((status & StatusExl) == 0)
        {
            if (_currentInDelaySlot)
            {
                _cop0[Cop0Epc] = _currentPc - 4;
                cause |= CauseBranchDelay;
            }
            else
            {
                _cop0[Cop0Epc] = _currentPc;
            }
        }

        _cop0[Cop0Cause] = cause;
        _cop0[Cop0Status] = status | StatusExl;
        _linkBit = false;
        Pc = (status & StatusBev) != 0 ? BootExceptionVector : _cop0[Cop0EBase];
    }

    void AddressError(uint address, AllegrexExceptionCode code)
    {
        _cop0[Cop0BadVAddr] = address;
        RaiseException(code);
    }

    void Unimplemented(uint instruction, string unit)
    {
        AllegrexTrapHandler? handler = TrapHandler;
        if (handler != null && handler(this, AllegrexTrapKind.Unimplemented, instruction, _currentPc))
        {
            return;
        }

        throw new AllegrexUnimplementedException(_currentPc, instruction, unit);
    }
}
