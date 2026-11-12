using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Lowers an <see cref="IrBlock"/> to x86-64 machine code (Iced assembler) for the native runtime's frame:
/// rbx = CpuState*, r15 = guest memory base (fastmem: host address = r15 + zero-extended guest address), rsp = the
/// trampoline frame (32 bytes of call shadow space, then spill slots). rax/rcx/rdx are scratch; the other registers
/// hold temps, allocated by linear scan over the (forward-only) IR. Temps live across a callout are kept in
/// callee-saved registers or spilled. Every 32-bit value is kept zero-extended in its 64-bit register, so it can index
/// guest memory directly. Windows x64 and System V calling conventions are both supported (callouts, trampoline).
/// </summary>
public sealed unsafe partial class X64Emitter
{
    private const int RAX = 0, RCX = 1, RDX = 2, RBX = 3, RSP = 4, RBP = 5, RSI = 6, RDI = 7, R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, R13 = 13, R14 = 14, R15 = 15;

    private static readonly AssemblerRegister32[] s_g32 = [eax, ecx, edx, ebx, esp, ebp, esi, edi, r8d, r9d, r10d, r11d, r12d, r13d, r14d, r15d];
    private static readonly AssemblerRegister64[] s_g64 = [rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8, r9, r10, r11, r12, r13, r14, r15];
    private static readonly AssemblerRegister16[] s_g16 = [ax, cx, dx, bx, sp, bp, si, di, r8w, r9w, r10w, r11w, r12w, r13w, r14w, r15w];
    private static readonly AssemblerRegister8[] s_g8 = [al, cl, dl, bl, spl, bpl, sil, dil, r8b, r9b, r10b, r11b, r12b, r13b, r14b, r15b];

    public static readonly bool Windows = OperatingSystem.IsWindows();
    /// <summary>Callee-saved registers available for temps (survive callouts).</summary>
    private static readonly int[] s_calleeSaved = Windows ? [RSI, RDI, RBP, R12, R13, R14] : [RBP, R12, R13, R14];
    /// <summary>Caller-saved registers available for temps (not across callouts).</summary>
    private static readonly int[] s_callerSaved = Windows ? [R8, R9, R10, R11] : [RSI, RDI, R8, R9, R10, R11];

    /// <summary>Spill slots the trampoline frame provides (8 bytes each, above the 32-byte shadow space).</summary>
    public const int SpillSlots = 64;
    public const int ShadowSpace = 32;

    private static readonly bool s_hasLzcnt = System.Runtime.Intrinsics.X86.Lzcnt.IsSupported;

    // CpuState field offsets
    private static readonly int s_offR = 0;
    private static readonly int s_offN = StateLayout.Of(nameof(CpuState.N));
    private static readonly int s_offCycleAcc = StateLayout.Of(nameof(CpuState.CycleAcc));
    private static readonly int s_offCheckWindow = StateLayout.Of(nameof(CpuState.CheckWindow));
    private static readonly int s_offNBlocks = StateLayout.Of(nameof(CpuState.NBlocks));
    private static readonly int s_offNInstr = StateLayout.Of(nameof(CpuState.NInstr));
    private static readonly int s_offLastExitSlot = StateLayout.Of(nameof(CpuState.LastExitSlot));
    private static readonly int s_offLinkBreak = StateLayout.Of(nameof(CpuState.LinkBreak));
    private static readonly int s_offThumb = StateLayout.Of(nameof(CpuState.Thumb));
    private static readonly int s_offMxcsrHost = StateLayout.Of(nameof(CpuState.MxcsrHost));
    private static readonly int s_offMxcsrGuest = StateLayout.Of(nameof(CpuState.MxcsrGuest));
    private static readonly int s_offMxcsrNeon = StateLayout.Of(nameof(CpuState.MxcsrNeon));
    private static readonly int s_offFpscr = StateLayout.Of(nameof(CpuState.Fpscr));
    private static readonly int s_offD = StateLayout.Of(nameof(CpuState.D));
    private static readonly int s_offExit = StateLayout.Of(nameof(CpuState.Exit));
    private static readonly int s_offTierUp = StateLayout.Of(nameof(CpuState.TierUpRequest));
    private static readonly int s_offMemBase = StateLayout.Of(nameof(CpuState.MemBase));
    private static readonly int s_offDispatchTable = StateLayout.Of(nameof(CpuState.DispatchTable));
    private static readonly int s_offNSimdOps = StateLayout.Of(nameof(CpuState.NSimdOps));
    private static readonly int s_offNCondBranches = StateLayout.Of(nameof(CpuState.NCondBranches));

    /// <summary>Profiling counters in generated code (<see cref="Util.EnvVars.NativeCounters"/>).</summary>
    public static readonly bool Counters = Environment.GetEnvironmentVariable(Util.EnvVars.NativeCounters) == "1";

    /// <summary>Debug knob for tests: restricts the temp registers so spilling paths get exercised.</summary>
    public static int MaxTempRegisters = int.MaxValue;

    private Assembler? _assembler;
    private Assembler Asm { get => _assembler!; set => _assembler = value; }
    private readonly MemoryStream _code = new(1024);
    private StreamCodeWriter? _writer;
    private IrBlock _ir = null!;
    private int[] _loc = null!; // >= 0 register, <= -2 spill slot -(loc+2), -1 none
    private Label[] _labels = null!;
    private Label _abort;
    private bool _checkExit;

    /// <summary>Addresses of the runtime entry points the code jumps to or calls.</summary>
    public struct Runtime
    {
        public ulong Epilogue;
        public ulong Callout;
        public ulong DispatchTable;
    }

    /// <summary>
    /// Allocates registers and lowers the block into the assembler. <paramref name="slotFor"/> returns the address of
    /// the link slot for the k-th static exit. Returns false when the block needs more spill slots than the frame has.
    /// </summary>
    public bool Prepare(IrBlock ir, Runtime rt, Func<int, nint> slotFor, nint tierUpCounter = 0)
    {
        _ir = ir;
        if (_assembler == null)
        {
            _assembler = new Assembler(64);
        }
        else
        {
            _assembler.Reset();
        }

        _lastLabelAt = -1;
        _neonMxcsr = false;
        if (!Allocate())
        {
            return false;
        }

        _labels = new Label[ir.Labels];
        for (int i = 0; i < ir.Labels; i++)
        {
            _labels[i] = Asm.CreateLabel();
        }

        _abort = Asm.CreateLabel();
        _checkExit = ir.Callouts > 0; // a handler may raise an exit mid-block: such blocks never chain while Exit is set
        Label tierUp = Asm.CreateLabel();
        if (tierUpCounter != 0)
        {
            // execution budget of a block the IL backend runs better: at zero, ask the dispatcher to recompile it
            // (the block has not run: the state is exactly the block entry state)
            Asm.mov(rax, (ulong)tierUpCounter);
            Asm.dec(__dword_ptr[rax]);
            Asm.jz(tierUp);
        }
        if (Counters)
        {
            int simd = 0, cond = 0;
            foreach (IrInst x in ir.Insts)
            {
                if (x.Op is IrOp.VecOp or IrOp.FpOp)
                {
                    simd++;
                }
                else if (x.Op is IrOp.CondBranch or IrOp.BranchIf)
                {
                    cond++;
                }
            }
            if (simd > 0)
            {
                Asm.add(__qword_ptr[rbx + s_offNSimdOps], simd);
            }

            if (cond > 0)
            {
                Asm.add(__qword_ptr[rbx + s_offNCondBranches], cond);
            }
        }
        int exitIndex = 0;
        foreach (IrInst x in ir.Insts)
        {
            Lower(x, rt, slotFor, ref exitIndex);
        }
        // shared path for callouts that threw: account the block and leave
        Place(ref _abort);
        EmitAccounting();
        Asm.mov(__qword_ptr[rbx + s_offLastExitSlot], 0);
        Asm.jmp(rt.Epilogue);
        if (tierUpCounter != 0)
        {
            Place(ref tierUp);
            Asm.mov(__qword_ptr[rbx + s_offLastExitSlot], 0);
            Asm.mov(State(s_offTierUp), 1);
            Asm.jmp(rt.Epilogue);
        }
        return true;
    }

    /// <summary>Encodes the prepared code for execution at <paramref name="rip"/>.</summary>
    public byte[] Assemble(ulong rip)
    {
        if (FastEncode)
        {
            if (TryFastAssemble(rip, out byte[]? fast))
            {
                return fast;
            }

            Interlocked.Increment(ref FastEncodeFallbacks);
        }
        _code.SetLength(0);
        Asm.Assemble(_writer ??= new StreamCodeWriter(_code), rip);
        return _code.ToArray();
    }

    // ------------------------------------------------------------------ register allocation

    /// <summary>Stopwatch ticks the last <see cref="Prepare"/> spent in register allocation (compile-phase statistics).</summary>
    public long AllocTicks;

    private bool Allocate()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int[]? loc = LinearScan.Allocate(_ir, s_callerSaved, s_calleeSaved, SpillSlots, MaxTempRegisters, VerifyAllocation, reuse: true);
        AllocTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (loc == null)
        {
            return false;
        }

        _loc = loc;
        return true;
    }

    /// <summary>Debug: check the register allocation of every block (tests).</summary>
    public static bool VerifyAllocation;

    // ------------------------------------------------------------------ operand helpers

    private bool IsConst(int t, out uint v) => _ir.TryConst(t, out v);
    private bool InReg(int t, out int r)
    {
        r = t >= 0 ? _loc[t] : -1;
        return r >= 0;
    }
    private AssemblerMemoryOperand Slot(int t) => __dword_ptr[rsp + (ShadowSpace + 8 * (-(_loc[t] + 2)))];
    private static AssemblerMemoryOperand State(int off) => __dword_ptr[rbx + off];

    /// <summary>Loads temp <paramref name="t"/> into register <paramref name="r"/> (flags preserved).</summary>
    private void LoadTo(int r, int t)
    {
        if (IsConst(t, out uint v))
        {
            Asm.mov(s_g32[r], v);
        }
        else if (InReg(t, out int s))
        {
            if (s != r)
            {
                Asm.mov(s_g32[r], s_g32[s]);
            }
        }
        else
        {
            Asm.mov(s_g32[r], Slot(t));
        }
    }

    /// <summary>A register holding temp t: its own, or <paramref name="scratch"/> loaded with it.</summary>
    private int Use(int t, int scratch)
    {
        if (!IsConst(t, out _) && InReg(t, out int r))
        {
            return r;
        }

        LoadTo(scratch, t);
        return scratch;
    }

    private int Def(int t, int scratch) => t >= 0 && InReg(t, out int r) ? r : scratch;

    private void Commit(int t, int r)
    {
        if (t < 0)
        {
            return;
        }

        if (InReg(t, out int own))
        {
            if (own != r)
            {
                Asm.mov(s_g32[own], s_g32[r]);
            }

            return;
        }
        Asm.mov(Slot(t), s_g32[r]);
    }

    private enum Alu
    {
        Add, Sub, And, Or, Xor, Adc, Sbb, Cmp, Imul
    }

    /// <summary>d = d (op) t, with t as immediate, register or spill slot.</summary>
    private void AluOp(Alu k, int d, int t)
    {
        AssemblerRegister32 D = s_g32[d];
        if (IsConst(t, out uint v))
        {
            int iv = (int)v;
            switch (k)
            {
                case Alu.Add:
                    Asm.add(D, iv);
                    break;
                case Alu.Sub:
                    Asm.sub(D, iv);
                    break;
                case Alu.And:
                    Asm.and(D, iv);
                    break;
                case Alu.Or:
                    Asm.or(D, iv);
                    break;
                case Alu.Xor:
                    Asm.xor(D, iv);
                    break;
                case Alu.Adc:
                    Asm.adc(D, iv);
                    break;
                case Alu.Sbb:
                    Asm.sbb(D, iv);
                    break;
                case Alu.Cmp:
                    Asm.cmp(D, iv);
                    break;
                case Alu.Imul:
                    Asm.imul(D, D, iv);
                    break;
            }
        }
        else if (InReg(t, out int r))
        {
            AssemblerRegister32 S = s_g32[r];
            switch (k)
            {
                case Alu.Add:
                    Asm.add(D, S);
                    break;
                case Alu.Sub:
                    Asm.sub(D, S);
                    break;
                case Alu.And:
                    Asm.and(D, S);
                    break;
                case Alu.Or:
                    Asm.or(D, S);
                    break;
                case Alu.Xor:
                    Asm.xor(D, S);
                    break;
                case Alu.Adc:
                    Asm.adc(D, S);
                    break;
                case Alu.Sbb:
                    Asm.sbb(D, S);
                    break;
                case Alu.Cmp:
                    Asm.cmp(D, S);
                    break;
                case Alu.Imul:
                    Asm.imul(D, S);
                    break;
            }
        }
        else
        {
            AssemblerMemoryOperand M = Slot(t);
            switch (k)
            {
                case Alu.Add:
                    Asm.add(D, M);
                    break;
                case Alu.Sub:
                    Asm.sub(D, M);
                    break;
                case Alu.And:
                    Asm.and(D, M);
                    break;
                case Alu.Or:
                    Asm.or(D, M);
                    break;
                case Alu.Xor:
                    Asm.xor(D, M);
                    break;
                case Alu.Adc:
                    Asm.adc(D, M);
                    break;
                case Alu.Sbb:
                    Asm.sbb(D, M);
                    break;
                case Alu.Cmp:
                    Asm.cmp(D, M);
                    break;
                case Alu.Imul:
                    Asm.imul(D, M);
                    break;
            }
        }
    }

    /// <summary>Stores 0/1 of the current host condition into flag temp t (setcc + movzx: flags preserved).</summary>
    private void SetFlagOut(int t, Action<AssemblerRegister8> setcc)
    {
        if (t < 0)
        {
            return;
        }

        int r = Def(t, RDX);
        setcc(s_g8[r]);
        Asm.movzx(s_g32[r], s_g8[r]);
        Commit(t, r);
    }

    /// <summary>Guest memory operand for address temp t (loads it into rax unless it is in a register).</summary>
    private AssemblerMemoryOperand GuestMem(int t, MemorySize size)
    {
        int r = Use(t, RAX);
        AssemblerMemoryOperand assemblerMemoryOperand = r15 + s_g64[r];
        return size switch
        {
            MemorySize.UInt8 => __byte_ptr[assemblerMemoryOperand],
            MemorySize.UInt16 => __word_ptr[assemblerMemoryOperand],
            _ => __dword_ptr[assemblerMemoryOperand],
        };
    }

    // ------------------------------------------------------------------ lowering

    private void EmitAccounting()
    {
        Asm.add(__qword_ptr[rbx + s_offCycleAcc], (int)Math.Min(_ir.Cycles, int.MaxValue));
        Asm.inc(__qword_ptr[rbx + s_offNBlocks]);
        Asm.add(__qword_ptr[rbx + s_offNInstr], _ir.GuestInstructions);
    }

    // the compile-time hot spots are compiled fully optimized from the start: a game compiles only thousands of blocks
    // per worker, much of it while these big methods would still run as unoptimized tier-0 code
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void Lower(IrInst x, Runtime rt, Func<int, nint> slotFor, ref int exitIndex)
    {
        Assembler a = Asm;
        if (_neonMxcsr && x.Op is not (IrOp.VecOp or IrOp.VecLdSt))
        {
            a.ldmxcsr(State(s_offMxcsrHost));
            _neonMxcsr = false;
        }
        switch (x.Op)
        {
            case IrOp.Nop:
                return;

            // ---------------- state
            case IrOp.GetReg:
            {
                int d = Def(x.D, RAX);
                a.mov(s_g32[d], State(s_offR + 4 * (int)x.Imm));
                Commit(x.D, d);
                return;
            }
            case IrOp.GetFlag:
            {
                int d = Def(x.D, RAX);
                a.mov(s_g32[d], State(s_offN + 4 * (int)x.Imm));
                Commit(x.D, d);
                return;
            }
            case IrOp.LoadState:
            {
                int d = Def(x.D, RAX);
                a.mov(s_g32[d], State((int)x.Imm));
                Commit(x.D, d);
                return;
            }
            case IrOp.SetReg:
                StoreTo(State(s_offR + 4 * (int)x.Imm), x.A);
                return;
            case IrOp.SetFlag:
                StoreTo(State(s_offN + 4 * (int)x.Imm), x.A);
                return;
            case IrOp.StoreState:
                StoreTo(State((int)x.Imm), x.A);
                return;

            // ---------------- ALU
            case IrOp.Add:
                Bin(Alu.Add, x);
                return;
            case IrOp.Sub:
                Bin(Alu.Sub, x);
                return;
            case IrOp.And:
                Bin(Alu.And, x);
                return;
            case IrOp.Or:
                Bin(Alu.Or, x);
                return;
            case IrOp.Xor:
                Bin(Alu.Xor, x);
                return;
            case IrOp.Mul:
                Bin(Alu.Imul, x);
                return;
            case IrOp.AndNot:
            case IrOp.OrNot:
            {
                int d = Def(x.D, RAX);
                LoadTo(d, x.B);
                a.not(s_g32[d]);
                AluOp(x.Op == IrOp.AndNot ? Alu.And : Alu.Or, d, x.A);
                Commit(x.D, d);
                return;
            }
            case IrOp.Not:
            {
                int d = Def(x.D, RAX);
                LoadTo(d, x.A);
                a.not(s_g32[d]);
                Commit(x.D, d);
                return;
            }
            case IrOp.Neg:
            {
                int d = Def(x.D, RAX);
                LoadTo(d, x.A);
                a.neg(s_g32[d]);
                Commit(x.D, d);
                return;
            }
            case IrOp.Shl:
            case IrOp.Shr:
            case IrOp.Sar:
            case IrOp.Ror:
                Shift(x);
                return;
            case IrOp.Clz:
            {
                int d = Def(x.D, RAX);
                if (s_hasLzcnt)
                {
                    int s = Use(x.A, RCX);
                    a.lzcnt(s_g32[d], s_g32[s]);
                }
                else
                {
                    int s = Use(x.A, RCX);
                    Label zero = a.CreateLabel();
                    Label done = a.CreateLabel();
                    a.bsr(s_g32[d], s_g32[s]);
                    a.jz(zero);
                    a.xor(s_g32[d], 31);
                    a.jmp(done);
                    Place(ref zero);
                    a.mov(s_g32[d], 32u);
                    Place(ref done);
                }
                Commit(x.D, d);
                return;
            }
            case IrOp.Bswap:
            {
                int d = Def(x.D, RAX);
                LoadTo(d, x.A);
                a.bswap(s_g32[d]);
                Commit(x.D, d);
                return;
            }
            case IrOp.Rev16:
            {
                int d = Def(x.D, RAX);
                LoadTo(d, x.A);
                a.bswap(s_g32[d]);
                a.ror(s_g32[d], (byte)16);
                Commit(x.D, d);
                return;
            }
            case IrOp.RevSh:
            {
                LoadTo(RAX, x.A);
                a.rol(ax, (byte)8);
                int d = Def(x.D, RAX);
                a.movsx(s_g32[d], ax);
                Commit(x.D, d);
                return;
            }
            case IrOp.Rbit:
            {
                LoadTo(RAX, x.A);
                foreach ((int sh, int mask) in new[] { (1, 0x55555555), (2, 0x33333333), (4, 0x0F0F0F0F) })
                {
                    a.mov(edx, eax);
                    a.shr(edx, (byte)sh);
                    a.and(edx, mask);
                    a.and(eax, mask);
                    a.shl(eax, (byte)sh);
                    a.or(eax, edx);
                }
                a.bswap(eax);
                int d = Def(x.D, RAX);
                if (d != RAX)
                {
                    a.mov(s_g32[d], eax);
                }

                Commit(x.D, d);
                return;
            }
            case IrOp.SExt8:
            case IrOp.SExt16:
            case IrOp.ZExt8:
            case IrOp.ZExt16:
            {
                int d = Def(x.D, RAX);
                int s = Use(x.A, RCX);
                switch (x.Op)
                {
                    case IrOp.SExt8:
                        a.movsx(s_g32[d], s_g8[s]);
                        break;
                    case IrOp.SExt16:
                        a.movsx(s_g32[d], s_g16[s]);
                        break;
                    case IrOp.ZExt8:
                        a.movzx(s_g32[d], s_g8[s]);
                        break;
                    default:
                        a.movzx(s_g32[d], s_g16[s]);
                        break;
                }
                Commit(x.D, d);
                return;
            }
            case IrOp.Bit:
            {
                int d = Def(x.D, RAX);
                LoadTo(d, x.A);
                if (x.Imm != 0)
                {
                    a.shr(s_g32[d], (byte)x.Imm);
                }

                a.and(s_g32[d], 1);
                Commit(x.D, d);
                return;
            }
            case IrOp.CmpEq:
            {
                int s = Use(x.A, RCX);
                AluOp(Alu.Cmp, s, x.B);
                int d = Def(x.D, RAX);
                a.sete(s_g8[d]);
                a.movzx(s_g32[d], s_g8[d]);
                Commit(x.D, d);
                return;
            }
            case IrOp.Select:
            {
                // d = c ? A : B
                int d = Def(x.D, RAX);
                int va = Use(x.A, RDX);
                LoadTo(d, x.B);
                int c = Use(x.C, RCX);
                a.test(s_g32[c], s_g32[c]);
                a.cmovne(s_g32[d], s_g32[va]);
                Commit(x.D, d);
                return;
            }
            case IrOp.UDiv:
            case IrOp.SDiv:
            {
                Label zero = a.CreateLabel();
                Label done = a.CreateLabel();
                Label div = a.CreateLabel();
                LoadTo(RAX, x.A);
                LoadTo(RCX, x.B);
                a.test(ecx, ecx);
                a.jz(zero);
                if (x.Op == IrOp.UDiv)
                {
                    a.xor(edx, edx);
                    a.div(ecx);
                }
                else
                {
                    a.cmp(ecx, -1);
                    a.jne(div);
                    a.cmp(eax, unchecked((int)0x80000000));
                    a.je(done); // INT_MIN / -1 = INT_MIN (x64 would fault)
                    Place(ref div);
                    a.cdq();
                    a.idiv(ecx);
                }
                a.jmp(done);
                Place(ref zero);
                a.xor(eax, eax);
                Place(ref done);
                int d = Def(x.D, RAX);
                if (d != RAX)
                {
                    a.mov(s_g32[d], eax);
                }

                Commit(x.D, d);
                return;
            }

            // ---------------- flags
            case IrOp.AddC:
            case IrOp.SubC:
                ArithFlags(x);
                return;
            case IrOp.TestNZ:
            {
                int s = Use(x.A, RAX);
                a.test(s_g32[s], s_g32[s]);
                SetFlagOut(x.Fn, r => a.sets(r));
                SetFlagOut(x.Fz, r => a.sete(r));
                return;
            }
            case IrOp.ShiftReg:
                ShiftReg(x);
                return;

            // ---------------- 64-bit
            case IrOp.UMulL:
            case IrOp.SMulL:
            {
                if (x.Op == IrOp.UMulL)
                {
                    LoadTo(RAX, x.A);
                    LoadTo(RCX, x.B);
                }
                else
                {
                    LoadTo(RAX, x.A);
                    a.movsxd(rax, eax);
                    LoadTo(RCX, x.B);
                    a.movsxd(rcx, ecx);
                }
                a.imul(rax, rcx);
                if (x.D >= 0)
                {
                    int d = Def(x.D, RCX);
                    a.mov(s_g32[d], eax);
                    Commit(x.D, d);
                }
                if (x.D2 >= 0)
                {
                    a.shr(rax, (byte)32);
                    int d = Def(x.D2, RAX);
                    if (d != RAX)
                    {
                        a.mov(s_g32[d], eax);
                    }

                    Commit(x.D2, d);
                }
                return;
            }
            case IrOp.Add64:
            {
                LoadTo(RAX, x.A);
                LoadTo(RDX, x.B);
                AluOp(Alu.Add, RAX, x.C);
                AluOp(Alu.Adc, RDX, x.E);
                if (x.D >= 0)
                {
                    Commit(x.D, RAX);
                }

                if (x.D2 >= 0)
                {
                    Commit(x.D2, RDX);
                }

                return;
            }

            // ---------------- memory
            case IrOp.Load8:
            case IrOp.Load8S:
            case IrOp.Load16:
            case IrOp.Load16S:
            case IrOp.Load32:
            {
                int d = Def(x.D, RCX);
                switch (x.Op)
                {
                    case IrOp.Load8:
                        a.movzx(s_g32[d], GuestMem(x.A, MemorySize.UInt8));
                        break;
                    case IrOp.Load8S:
                        a.movsx(s_g32[d], GuestMem(x.A, MemorySize.UInt8));
                        break;
                    case IrOp.Load16:
                        a.movzx(s_g32[d], GuestMem(x.A, MemorySize.UInt16));
                        break;
                    case IrOp.Load16S:
                        a.movsx(s_g32[d], GuestMem(x.A, MemorySize.UInt16));
                        break;
                    default:
                        a.mov(s_g32[d], GuestMem(x.A, MemorySize.UInt32));
                        break;
                }
                Commit(x.D, d);
                return;
            }
            case IrOp.Store8:
            case IrOp.Store16:
            case IrOp.Store32:
            {
                if (LogStores)
                {
                    EmitStoreLog(x);
                }

                AssemblerMemoryOperand mem = GuestMem(x.A, x.Op == IrOp.Store8 ? MemorySize.UInt8 : x.Op == IrOp.Store16 ? MemorySize.UInt16 : MemorySize.UInt32);
                if (IsConst(x.B, out uint v))
                {
                    if (x.Op == IrOp.Store8)
                    {
                        a.mov(mem, (byte)v);
                    }
                    else if (x.Op == IrOp.Store16)
                    {
                        a.mov(mem, (short)v);
                    }
                    else
                    {
                        a.mov(mem, (int)v);
                    }
                }
                else
                {
                    int s = Use(x.B, RDX);
                    if (x.Op == IrOp.Store8)
                    {
                        a.mov(mem, s_g8[s]);
                    }
                    else if (x.Op == IrOp.Store16)
                    {
                        a.mov(mem, s_g16[s]);
                    }
                    else
                    {
                        a.mov(mem, s_g32[s]);
                    }
                }
                MarkDirty(x.A);
                return;
            }
            case IrOp.Fence:
                a.mfence();
                return;
            case IrOp.FpOp:
                FpOp(x, rt);
                return;
            case IrOp.VecOp:
                VecOp(x, rt);
                return;
            case IrOp.LoadD:
            {
                int r = Use(x.A, RAX);
                a.mov(rcx, __qword_ptr[r15 + s_g64[r]]);
                a.mov(__qword_ptr[rbx + (int)x.Imm], rcx);
                return;
            }
            case IrOp.StoreD:
            {
                int r = Use(x.A, RAX);
                a.mov(rcx, __qword_ptr[rbx + (int)x.Imm]);
                a.mov(__qword_ptr[r15 + s_g64[r]], rcx);
                MarkDirty(x.A);
                return;
            }
            case IrOp.LdrEx:
                LoadExclusive(x);
                return;
            case IrOp.StrEx:
                StoreExclusive(x);
                return;

            // ---------------- control
            case IrOp.Label:
            {
                Label l = _labels[x.Imm];
                Place(ref l);
                _labels[x.Imm] = l;
                return;
            }
            case IrOp.BranchIf:
            {
                int s = Use(x.A, RAX);
                a.test(s_g32[s], s_g32[s]);
                if (x.Imm != 0)
                {
                    a.jnz(_labels[x.Imm2]);
                }
                else
                {
                    a.jz(_labels[x.Imm2]);
                }

                return;
            }
            case IrOp.CondBranch:
                CondBranch(x);
                return;
            case IrOp.Callout:
            {
                if (Windows)
                {
                    a.mov(rcx, rbx);
                    a.mov(edx, x.Imm);
                    a.mov(r8d, x.Imm2);
                    a.mov(r9d, x.Imm3);
                }
                else
                {
                    a.mov(rdi, rbx);
                    a.mov(esi, x.Imm);
                    a.mov(edx, x.Imm2);
                    a.mov(ecx, x.Imm3);
                }
                a.mov(rax, rt.Callout);
                a.call(rax);
                a.test(eax, eax);
                a.jnz(_abort);
                return;
            }
            case IrOp.ExitStatic:
            {
                nint slot = slotFor(exitIndex++);
                Label ret = a.CreateLabel();
                EmitAccounting();
                a.mov(rax, __qword_ptr[rbx + s_offCycleAcc]);
                a.cmp(rax, __qword_ptr[rbx + s_offCheckWindow]);
                a.jge(ret);
                a.cmp(State(s_offLinkBreak), 0);
                a.jne(ret);
                if (_checkExit)
                {
                    a.cmp(State(s_offExit), 0);
                    a.jne(ret);
                }
                a.mov(rax, (ulong)slot);
                a.mov(rax, __qword_ptr[rax]);
                a.test(rax, rax);
                a.jz(ret);
                a.jmp(rax);
                Place(ref ret);
                a.mov(rax, (ulong)slot);
                a.mov(__qword_ptr[rbx + s_offLastExitSlot], rax);
                a.jmp(rt.Epilogue);
                return;
            }
            case IrOp.ExitDynamic:
            {
                Label ret = a.CreateLabel();
                EmitAccounting();
                if (x.Imm == 0 && rt.DispatchTable != 0)
                {
                    // indirect exit: look the target up in the dispatch table (key, code, key: see
                    // NativeRuntime.Publish) and jump straight to it when the pacing window allows
                    a.mov(rax, __qword_ptr[rbx + s_offCycleAcc]);
                    a.cmp(rax, __qword_ptr[rbx + s_offCheckWindow]);
                    a.jge(ret);
                    a.cmp(State(s_offLinkBreak), 0);
                    a.jne(ret);
                    if (_checkExit)
                    {
                        a.cmp(State(s_offExit), 0);
                        a.jne(ret);
                    }
                    a.mov(eax, State(s_offR + 60));
                    a.mov(edx, eax);
                    a.or(eax, State(s_offThumb));
                    a.shr(edx, (byte)1);
                    a.and(edx, (1 << NativeRuntime.DispatchBits) - 1);
                    a.shl(rdx, (byte)4);
                    a.mov(rcx, __qword_ptr[rbx + s_offDispatchTable]); // the thread's process's table
                    a.add(rcx, rdx);
                    // key, code, key again: a concurrent republish (key=invalid, code=new, key=new) between the
                    // loads is detected by the second compare (x64 never reorders loads with other loads)
                    a.cmp(__dword_ptr[rcx], eax);
                    a.jne(ret);
                    a.mov(rdx, __qword_ptr[rcx + 8]);
                    a.cmp(__dword_ptr[rcx], eax);
                    a.jne(ret);
                    a.jmp(rdx);
                }
                Place(ref ret);
                a.mov(__qword_ptr[rbx + s_offLastExitSlot], 0);
                a.jmp(rt.Epilogue);
                return;
            }
            default:
                throw new NotSupportedException($"IR op {x.Op}");
        }
    }

    // ------------------------------------------------------------------ exclusive monitor

    private static readonly ulong s_gensBase = (ulong)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(Arm.s_exclusiveGens, 0);
    private static readonly int s_offExAddr = StateLayout.Of(nameof(CpuState.ExclusiveAddr));
    private static readonly int s_offExValue = StateLayout.Of(nameof(CpuState.ExclusiveValue));
    private static readonly int s_offExValid = StateLayout.Of(nameof(CpuState.ExclusiveValid));
    private static readonly int s_offExGen = StateLayout.Of(nameof(CpuState.ExclusiveGen));
    private static readonly int s_offExTmp = StateLayout.Of(nameof(CpuState.ExTmp));
    private static readonly int s_offExTmpHi = StateLayout.Of(nameof(CpuState.ExTmpHi));

    /// <summary>rdx = address of the generation counter of the granule of guest address ecx (clobbers rax, rdx).</summary>
    private void GenPointer()
    {
        Assembler a = Asm;
        a.mov(edx, ecx);
        a.shr(edx, (byte)3);
        a.and(edx, Arm.ExclusiveStripes - 1);
        a.mov(rax, s_gensBase);
        a.lea(rdx, __[rax + rdx * 4]);
    }

    /// <summary>LDREX (word): Arm.ReserveExclusive + read + SetExclusive.</summary>
    private void LoadExclusive(IrInst x)
    {
        Assembler a = Asm;
        Label spin = a.CreateLabel();
        Label ok = a.CreateLabel();
        LoadTo(RCX, x.A);
        GenPointer();
        Place(ref spin);
        a.mov(eax, __dword_ptr[rdx]);
        a.test(eax, 1);
        a.jz(ok);
        a.pause();                           // an exclusive store of another core is in flight
        a.jmp(spin);
        Place(ref ok);
        a.mov(State(s_offExGen), eax);
        if (x.Imm == 1)
        {
            a.mov(rax, __qword_ptr[r15 + rcx]); // LDREXD: one 64-bit read, like Volatile.Read(ulong)
        }
        else
        {
            a.mov(eax, __dword_ptr[r15 + rcx]);
        }

        a.mov(State(s_offExAddr), ecx);
        a.mov(__qword_ptr[rbx + s_offExValue], rax); // word: zero-extended to 64 bits, like SetExclusive(uint)
        a.mov(State(s_offExValid), 1);
        if (x.Imm == 1 && x.D2 >= 0)
        {
            a.mov(rdx, rax);
            a.shr(rdx, (byte)32);
            int d2 = Def(x.D2, RDX);
            if (d2 != RDX)
            {
                a.mov(s_g32[d2], edx);
            }

            Commit(x.D2, d2);
        }
        if (x.D >= 0)
        {
            int d = Def(x.D, RAX);
            if (d != RAX)
            {
                a.mov(s_g32[d], eax);
            }

            Commit(x.D, d);
        }
    }

    /// <summary>STREX (word): Arm.StoreExclusive32 (generation lock, value CAS against the LDREX value, release).</summary>
    private void StoreExclusive(IrInst x)
    {
        Assembler a = Asm;
        Label fail = a.CreateLabel();
        Label done = a.CreateLabel();
        Label keep = a.CreateLabel();
        // the value goes through the state: only rax/rcx/rdx are free here
        bool dual = x.Imm == 1;
        if (IsConst(x.B, out uint bv))
        {
            a.mov(State(s_offExTmp), (int)bv);
        }
        else
        {
            a.mov(State(s_offExTmp), s_g32[Use(x.B, RAX)]);
        }

        if (dual)
        {
            if (IsConst(x.C, out uint cv))
            {
                a.mov(State(s_offExTmpHi), (int)cv);
            }
            else
            {
                a.mov(State(s_offExTmpHi), s_g32[Use(x.C, RAX)]);
            }
        }
        LoadTo(RCX, x.A);
        a.cmp(State(s_offExValid), 0);
        a.je(fail);
        a.cmp(State(s_offExAddr), ecx);
        a.jne(fail);
        a.mov(State(s_offExValid), 0);
        GenPointer();
        a.mov(eax, State(s_offExGen));
        a.lea(ecx, __dword_ptr[rax + 1]);
        a.@lock.cmpxchg(__dword_ptr[rdx], ecx); // gen: snap -> snap+1 (odd: store in flight)
        a.jne(fail);
        a.mov(edx, State(s_offExAddr));
        if (dual)
        {
            a.mov(rcx, __qword_ptr[rbx + s_offExTmp]);    // ExTmp:ExTmpHi = Rt | Rt2 << 32
            a.mov(rax, __qword_ptr[rbx + s_offExValue]);
            a.@lock.cmpxchg(__qword_ptr[r15 + rdx], rcx); // 64-bit CAS against what LDREXD read
        }
        else
        {
            a.mov(ecx, State(s_offExTmp));
            a.mov(eax, State(s_offExValue));
            a.@lock.cmpxchg(__dword_ptr[r15 + rdx], ecx); // value CAS against what LDREX read
        }
        a.sete(cl);
        a.movzx(ecx, cl);                       // ecx = ok
        a.mov(edx, State(s_offExAddr));
        a.mov(eax, edx);
        a.mov(edx, ecx);                        // keep ok in edx while computing the gen pointer in rcx/rax
        a.mov(ecx, eax);
        a.shr(ecx, (byte)3);
        a.and(ecx, Arm.ExclusiveStripes - 1);
        a.mov(rax, s_gensBase);
        a.lea(rcx, __[rax + rcx * 4]);
        a.mov(eax, State(s_offExGen));
        a.test(edx, edx);
        a.jz(keep);
        a.add(eax, 2);
        Place(ref keep);
        a.mov(__dword_ptr[rcx], eax);           // release: snap+2 on success, snap on failure
        if (Memory.DirtyPages.Enabled)
        {
            Label clean = a.CreateLabel();
            a.test(edx, edx);
            a.jz(clean);
            a.mov(ecx, State(s_offExAddr));
            MarkDirtyEcx();
            Place(ref clean);
        }
        a.mov(ecx, edx);
        a.xor(ecx, 1);                          // status: 0 ok, 1 failed
        a.jmp(done);
        Place(ref fail);
        a.mov(ecx, 1u);
        Place(ref done);
        int d = Def(x.D, RCX);
        if (d != RCX)
        {
            a.mov(s_g32[d], ecx);
        }

        Commit(x.D, d);
    }

    // ------------------------------------------------------------------ VFP

    /// <summary>
    /// Scalar VFP operation. Fast path only in the Vita's FPSCR mode (DN=1, FZ=1, round to nearest, no short vectors):
    /// SSE under FTZ+DAZ matches ARM flush-to-zero except for results that round up to the smallest normal (ARM
    /// detects tininess before rounding, x86 after): those, any other FPSCR mode and unrepresentable conversions run
    /// the interpreter handler instead. NaN results become the default NaN. MXCSR is the host's outside fast paths.
    /// </summary>
    private void FpOp(IrInst x, Runtime rt)
    {
        Assembler a = Asm;
        var kind = (FpKind)(x.Imm & 0xFF);
        bool dbl = (x.Imm & (uint)FpFlags.Double) != 0;
        Op op = (x.Imm & (uint)FpFlags.OtherOp) != 0 ? Op.Vfp_Other : Op.Vfp_Arith;
        int dOff = s_offD + (int)(x.Imm4 & 0xFF), nOff = s_offD + (int)((x.Imm4 >> 8) & 0xFF), mOff = s_offD + (int)((x.Imm4 >> 16) & 0xFF);
        Label slow = a.CreateLabel();
        Label slowGuest = a.CreateLabel();
        Label done = a.CreateLabel();
        AssemblerMemoryOperand S(int off) => __dword_ptr[rbx + off];
        AssemblerMemoryOperand Dq(int off) => __qword_ptr[rbx + off];

        a.mov(eax, State(s_offFpscr));
        a.and(eax, 0x03F70000);
        a.cmp(eax, 0x03000000);
        a.jne(slow);
        a.ldmxcsr(State(s_offMxcsrGuest));

        switch (kind)
        {
            case FpKind.Add:
            case FpKind.Sub:
            case FpKind.Mul:
            case FpKind.NMul:
            case FpKind.Div:
                if (dbl)
                {
                    a.movsd(xmm0, Dq(nOff));
                    switch (kind)
                    {
                        case FpKind.Add:
                            a.addsd(xmm0, Dq(mOff));
                            break;
                        case FpKind.Sub:
                            a.subsd(xmm0, Dq(mOff));
                            break;
                        case FpKind.Div:
                            a.divsd(xmm0, Dq(mOff));
                            break;
                        default:
                            a.mulsd(xmm0, Dq(mOff));
                            break;
                    }
                }
                else
                {
                    a.movss(xmm0, S(nOff));
                    switch (kind)
                    {
                        case FpKind.Add:
                            a.addss(xmm0, S(mOff));
                            break;
                        case FpKind.Sub:
                            a.subss(xmm0, S(mOff));
                            break;
                        case FpKind.Div:
                            a.divss(xmm0, S(mOff));
                            break;
                        default:
                            a.mulss(xmm0, S(mOff));
                            break;
                    }
                }
                StoreFpResult(dOff, dbl, kind == FpKind.NMul, slowGuest, done);
                break;
            case FpKind.Mla:
            case FpKind.Mls:
            case FpKind.Nmla:
            case FpKind.Nmls:
                // d = (+/-)d (+/-) round(n * m): the product is rounded (and flushed / NaN-defaulted) on its own
                if (dbl)
                {
                    a.movsd(xmm1, Dq(nOff));
                    a.mulsd(xmm1, Dq(mOff));
                    a.movsd(xmm0, Dq(dOff));
                }
                else
                {
                    a.movss(xmm1, S(nOff));
                    a.mulss(xmm1, S(mOff));
                    a.movss(xmm0, S(dOff));
                }
                if (kind is FpKind.Nmla or FpKind.Nmls)
                {
                    NegateXmm0(dbl);
                }

                if (kind is FpKind.Mla or FpKind.Nmls)
                {
                    if (dbl)
                    {
                        a.addsd(xmm0, xmm1);
                    }
                    else
                    {
                        a.addss(xmm0, xmm1);
                    }
                }
                else
                {
                    if (dbl)
                    {
                        a.subsd(xmm0, xmm1);
                    }
                    else
                    {
                        a.subss(xmm0, xmm1);
                    }
                }
                StoreFpResult(dOff, dbl, false, slowGuest, done);
                break;
            case FpKind.Sqrt:
                if (dbl)
                {
                    a.sqrtsd(xmm0, Dq(mOff));
                }
                else
                {
                    a.sqrtss(xmm0, S(mOff));
                }

                StoreFpResult(dOff, dbl, false, slowGuest, done);
                break;
            case FpKind.Cmp:
            case FpKind.CmpZero:
            {
                Label set = a.CreateLabel();
                if (dbl)
                {
                    a.movsd(xmm0, Dq(dOff));
                }
                else
                {
                    a.movss(xmm0, S(dOff));
                }

                if (kind == FpKind.CmpZero)
                {
                    a.xorps(xmm1, xmm1);
                    if (dbl)
                    {
                        a.ucomisd(xmm0, xmm1);
                    }
                    else
                    {
                        a.ucomiss(xmm0, xmm1);
                    }
                }
                else if (dbl)
                {
                    a.ucomisd(xmm0, Dq(mOff));
                }
                else
                {
                    a.ucomiss(xmm0, S(mOff));
                }

                a.mov(eax, 0x30000000u); // unordered: C V
                a.jp(set);
                a.mov(eax, 0x60000000u); // equal: Z C
                a.je(set);
                a.mov(eax, 0x80000000u); // less: N
                a.jb(set);
                a.mov(eax, 0x20000000u); // greater: C
                Place(ref set);
                a.ldmxcsr(State(s_offMxcsrHost));
                a.mov(edx, State(s_offFpscr));
                a.and(edx, 0x0FFFFFFF);
                a.or(edx, eax);
                a.mov(State(s_offFpscr), edx);
                a.jmp(done);
                break;
            }
            case FpKind.CvtToDouble:
                a.cvtss2sd(xmm0, S(mOff));
                StoreFpResult(dOff, true, false, slowGuest, done);
                break;
            case FpKind.CvtToSingle:
                a.cvtsd2ss(xmm0, Dq(mOff));
                StoreFpResult(dOff, false, false, slowGuest, done);
                break;
            case FpKind.CvtFromS32:
            case FpKind.CvtFromU32:
                if (kind == FpKind.CvtFromS32)
                {
                    if (dbl)
                    {
                        a.cvtsi2sd(xmm0, S(mOff));
                    }
                    else
                    {
                        a.cvtsi2ss(xmm0, S(mOff));
                    }
                }
                else
                {
                    a.mov(eax, S(mOff)); // zero-extended: the 64-bit conversion is exact for every uint
                    if (dbl)
                    {
                        a.cvtsi2sd(xmm0, rax);
                    }
                    else
                    {
                        a.cvtsi2ss(xmm0, rax);
                    }
                }
                // integers convert to normal numbers or zero: no fix-up needed
                a.ldmxcsr(State(s_offMxcsrHost));
                if (dbl)
                {
                    a.movsd(Dq(dOff), xmm0);
                }
                else
                {
                    a.movss(S(dOff), xmm0);
                }

                a.jmp(done);
                break;
            case FpKind.CvtToS32:
            case FpKind.CvtToS32R:
                // 0x80000000 is the x86 "indefinite" result (NaN, out of range) but also a real value: the handler decides
                if (kind == FpKind.CvtToS32)
                {
                    if (dbl)
                    {
                        a.cvttsd2si(eax, Dq(mOff));
                    }
                    else
                    {
                        a.cvttss2si(eax, S(mOff));
                    }
                }
                else
                {
                    if (dbl)
                    {
                        a.cvtsd2si(eax, Dq(mOff));
                    }
                    else
                    {
                        a.cvtss2si(eax, S(mOff));
                    }
                }
                a.cmp(eax, unchecked((int)0x80000000));
                a.je(slowGuest);
                a.ldmxcsr(State(s_offMxcsrHost));
                a.mov(S(dOff), eax);
                a.jmp(done);
                break;
            case FpKind.CvtToU32:
            case FpKind.CvtToU32R:
            {
                Label store = a.CreateLabel();
                Label pos = a.CreateLabel();
                if (kind == FpKind.CvtToU32)
                {
                    if (dbl)
                    {
                        a.cvttsd2si(rax, Dq(mOff));
                    }
                    else
                    {
                        a.cvttss2si(rax, S(mOff));
                    }
                }
                else
                {
                    if (dbl)
                    {
                        a.cvtsd2si(rax, Dq(mOff));
                    }
                    else
                    {
                        a.cvtss2si(rax, S(mOff));
                    }
                }
                a.mov(rdx, 0x8000000000000000UL);
                a.cmp(rax, rdx);
                a.je(slowGuest);       // NaN or beyond 2^63
                a.test(rax, rax);
                a.jns(pos);
                a.xor(eax, eax);       // negative: 0
                a.jmp(store);
                Place(ref pos);
                a.mov(edx, 0xFFFFFFFFu);
                a.cmp(rax, rdx);
                a.jbe(store);
                a.mov(eax, edx);       // saturate
                Place(ref store);
                a.ldmxcsr(State(s_offMxcsrHost));
                a.mov(S(dOff), eax);
                a.jmp(done);
                break;
            }
            case FpKind.CvtToHalf:
            {
                // round to nearest even; DAZ flushes the input like InF; half denormals are produced (vcvtps2ph ignores
                // FTZ), like the handler's (Half) conversion; NaN -> the default half NaN (DN)
                Label store = a.CreateLabel();
                a.movss(xmm0, S(mOff));
                a.mov(eax, 0x7E00);
                a.ucomiss(xmm0, xmm0);
                a.jp(store);
                a.vcvtps2ph(xmm0, xmm0, 0);
                a.movd(eax, xmm0);
                Place(ref store);
                a.ldmxcsr(State(s_offMxcsrHost));
                a.mov(__word_ptr[rbx + dOff], ax);
                a.jmp(done);
                break;
            }
            case FpKind.CvtFromHalf:
            {
                // exact (every half is a normal single or zero); NaN -> default NaN
                Label store = a.CreateLabel();
                a.movzx(eax, __word_ptr[rbx + mOff]);
                a.movd(xmm0, eax);
                a.vcvtph2ps(xmm0, xmm0);
                a.movd(eax, xmm0);
                a.ucomiss(xmm0, xmm0);
                a.jnp(store);
                a.mov(eax, 0x7FC00000);
                Place(ref store);
                a.ldmxcsr(State(s_offMxcsrHost));
                a.mov(S(dOff), eax);
                a.jmp(done);
                break;
            }
            case FpKind.CvtToFixed:
            {
                // trunc(x * 2^frac) in double (exact: a power-of-two scale), saturated to the fixed-point size and
                // sign- or zero-extended to the register; NaN, infinities and |x| >= 2^63 go to the handler
                Vfp.FixedParams(x.Imm2, out _, out bool unsigned, out int size, out int frac);
                if (dbl)
                {
                    a.movsd(xmm0, Dq(dOff));
                }
                else
                {
                    a.cvtss2sd(xmm0, S(dOff)); // DAZ flushes a denormal input, like InF
                }

                a.mov(rax, BitConverter.DoubleToUInt64Bits(Math.Pow(2, frac)));
                a.movq(xmm1, rax);
                a.mulsd(xmm0, xmm1);
                a.cvttsd2si(rax, xmm0);
                a.mov(rdx, 0x8000000000000000UL);
                a.cmp(rax, rdx);
                a.je(slowGuest);
                long min = unsigned ? 0 : size == 32 ? int.MinValue : short.MinValue;
                long max = unsigned ? (size == 32 ? uint.MaxValue : ushort.MaxValue) : size == 32 ? int.MaxValue : short.MaxValue;
                a.mov(rdx, unchecked((ulong)min));
                a.cmp(rax, rdx);
                a.cmovl(rax, rdx);
                a.mov(rdx, (ulong)max);
                a.cmp(rax, rdx);
                a.cmovg(rax, rdx);
                a.ldmxcsr(State(s_offMxcsrHost));
                if (dbl)
                {
                    a.mov(Dq(dOff), rax);
                }
                else
                {
                    a.mov(S(dOff), eax);
                }

                a.jmp(done);
                break;
            }
            case FpKind.CvtFromFixed:
            {
                // the integer is exact in a double and so is the power-of-two scale: one rounding to single, as the
                // handler's (float)(v / 2^frac); results are normal numbers or zero (|v| >= 2^-32)
                Vfp.FixedParams(x.Imm2, out _, out bool unsigned, out int size, out int frac);
                if (size == 32)
                {
                    if (unsigned)
                    {
                        a.mov(eax, S(dOff));
                    }
                    else
                    {
                        a.movsxd(rax, S(dOff));
                    }
                }
                else if (unsigned)
                {
                    a.movzx(eax, __word_ptr[rbx + dOff]);
                }
                else
                {
                    a.movsx(rax, __word_ptr[rbx + dOff]);
                }

                a.cvtsi2sd(xmm0, rax);
                a.mov(rax, BitConverter.DoubleToUInt64Bits(Math.Pow(2, -frac)));
                a.movq(xmm1, rax);
                a.mulsd(xmm0, xmm1);
                if (!dbl)
                {
                    a.cvtsd2ss(xmm0, xmm0);
                }

                a.ldmxcsr(State(s_offMxcsrHost));
                if (dbl)
                {
                    a.movsd(Dq(dOff), xmm0);
                }
                else
                {
                    a.movss(S(dOff), xmm0);
                }

                a.jmp(done);
                break;
            }
            default:
                a.jmp(slowGuest);
                break;
        }

        Place(ref slowGuest);
        a.ldmxcsr(State(s_offMxcsrHost));
        Place(ref slow);
        if (Windows)
        {
            a.mov(rcx, rbx);
            a.mov(edx, (uint)op);
            a.mov(r8d, x.Imm2);
            a.mov(r9d, x.Imm3);
        }
        else
        {
            a.mov(rdi, rbx);
            a.mov(esi, (uint)op);
            a.mov(edx, x.Imm2);
            a.mov(ecx, x.Imm3);
        }
        a.mov(rax, rt.Callout);
        a.call(rax);
        a.test(eax, eax);
        a.jnz(_abort);
        Place(ref done);
    }

    private void NegateXmm0(bool dbl)
    {
        if (dbl)
        {
            Asm.movq(rax, xmm0);
            Asm.btc(rax, 63);
            Asm.movq(xmm0, rax);
        }
        else
        {
            Asm.movd(eax, xmm0);
            Asm.xor(eax, unchecked((int)0x80000000));
            Asm.movd(xmm0, eax);
        }
    }

    /// <summary>
    /// Stores the arithmetic result in xmm0 to the guest register: NaN -> default NaN (then optionally negated, like
    /// VNMUL), a result equal to +/- the smallest normal goes to the handler (FZ tininess before rounding).
    /// </summary>
    private void StoreFpResult(int dOff, bool dbl, bool negate, Label slowGuest, Label done)
    {
        Assembler a = Asm;
        Label nan = a.CreateLabel();
        Label store = a.CreateLabel();
        if (dbl)
        {
            a.movq(rax, xmm0);
            a.mov(rdx, rax);
            a.btr(rdx, 63);
            a.mov(rcx, 0x7FF0000000000000UL);
            a.cmp(rdx, rcx);
            a.ja(nan);
            a.mov(rcx, 0x0010000000000000UL);
            a.cmp(rdx, rcx);
            a.je(slowGuest);
            a.jmp(store);
            Place(ref nan);
            a.mov(rax, 0x7FF8000000000000UL);
            Place(ref store);
            if (negate)
            {
                a.btc(rax, 63);
            }

            a.ldmxcsr(State(s_offMxcsrHost));
            a.mov(__qword_ptr[rbx + dOff], rax);
        }
        else
        {
            a.movd(eax, xmm0);
            a.mov(edx, eax);
            a.and(edx, 0x7FFFFFFF);
            a.cmp(edx, 0x7F800000);
            a.ja(nan);
            a.cmp(edx, 0x00800000);
            a.je(slowGuest);
            a.jmp(store);
            Place(ref nan);
            a.mov(eax, 0x7FC00000u);
            Place(ref store);
            if (negate)
            {
                a.xor(eax, unchecked((int)0x80000000));
            }

            a.ldmxcsr(State(s_offMxcsrHost));
            a.mov(State(dOff), eax);
        }
        a.jmp(done);
    }

    /// <summary>Verify mode: blocks record every guest store (address, size, previous value) so it can be undone.</summary>
    public static bool LogStores = NativeVerifier.Enabled;
    public const int StoreLogEntries = 8192;
    private static readonly int s_offStoreLog = StateLayout.Of(nameof(CpuState.StoreLog));
    private static readonly int s_offStoreLogCount = StateLayout.Of(nameof(CpuState.StoreLogCount));

    /// <summary>Appends {address, size, old value} (16-byte entry) to the state's store log; clobbers rcx, rdx, flags.</summary>
    private void EmitStoreLog(IrInst x)
    {
        Assembler a = Asm;
        int size = x.Op == IrOp.Store8 ? 1 : x.Op == IrOp.Store16 ? 2 : 4;
        LoadTo(RCX, x.A);
        a.mov(edx, State(s_offStoreLogCount));
        a.shl(rdx, (byte)4);
        a.add(rdx, __qword_ptr[rbx + s_offStoreLog]);
        a.mov(__dword_ptr[rdx], ecx);
        a.mov(__dword_ptr[rdx + 4], size);
        if (size == 1)
        {
            a.movzx(ecx, __byte_ptr[r15 + rcx]);
        }
        else if (size == 2)
        {
            a.movzx(ecx, __word_ptr[r15 + rcx]);
        }
        else
        {
            a.mov(ecx, __dword_ptr[r15 + rcx]);
        }

        a.mov(__dword_ptr[rdx + 8], ecx);
        a.inc(State(s_offStoreLogCount));
    }

    private int _lastLabelAt = -1;

    /// <summary>Places a label; Iced allows one label per instruction, so two in a row get a nop between them.</summary>
    /// <summary>
    /// Marks the page of the guest address in temp t as written (<see cref="Memory.DirtyPages"/>), after the store.
    /// The table sits right below guest address 0, so it is addressed from the guest base register. Clobbers rcx and
    /// flags (no flags are live across IR instructions).
    /// </summary>
    private void MarkDirty(int t)
    {
        if (!Memory.DirtyPages.Enabled)
        {
            return;
        }

        if (IsConst(t, out uint addr))
        {
            Asm.mov(ecx, addr);
        }
        else
        {
            Asm.mov(ecx, s_g32[Use(t, RAX)]);
        }

        MarkDirtyEcx();
    }

    /// <summary>Dirty mark for the guest address in ecx: the byte is only written while clear (keeps the line shared).</summary>
    private void MarkDirtyEcx()
    {
        Assembler a = Asm;
        Label done = a.CreateLabel();
        a.shr(ecx, (byte)Memory.DirtyPages.PageShift);
        a.cmp(__byte_ptr[r15 + rcx - (int)Memory.DirtyPages.TableBytes], 0);
        a.jne(done);
        a.mov(__byte_ptr[r15 + rcx - (int)Memory.DirtyPages.TableBytes], 1);
        Place(ref done);
    }

    private void Place(ref Label l)
    {
        if (Asm.Instructions.Count == _lastLabelAt)
        {
            Asm.nop();
        }

        Asm.Label(ref l);
        _lastLabelAt = Asm.Instructions.Count;
    }

    private void StoreTo(AssemblerMemoryOperand assemblerMemoryOperand, int t)
    {
        if (IsConst(t, out uint v))
        {
            Asm.mov(assemblerMemoryOperand, (int)v);
        }
        else
        {
            Asm.mov(assemblerMemoryOperand, s_g32[Use(t, RAX)]);
        }
    }

    private void Bin(Alu k, IrInst x)
    {
        int d = Def(x.D, RAX);
        // commutative ops with a constant first operand: swap so the constant becomes the immediate
        int A = x.A, B = x.B;
        if (k is Alu.Add or Alu.And or Alu.Or or Alu.Xor or Alu.Imul && IsConst(A, out _) && !IsConst(B, out _))
        {
            (A, B) = (B, A);
        }
        // additions (addresses mostly) as one 32-bit lea: wraps modulo 2^32 like the guest, leaves flags alone
        if (k == Alu.Add && !IsConst(A, out _) && InReg(A, out int ra))
        {
            if (IsConst(B, out uint imm))
            {
                Asm.lea(s_g32[d], __dword_ptr[s_g64[ra] + (int)imm]);
                Commit(x.D, d);
                return;
            }
            if (InReg(B, out int rb))
            {
                Asm.lea(s_g32[d], __dword_ptr[s_g64[ra] + s_g64[rb]]);
                Commit(x.D, d);
                return;
            }
        }
        LoadTo(d, A);
        AluOp(k, d, B);
        Commit(x.D, d);
    }

    private void Shift(IrInst x)
    {
        Assembler a = Asm;
        int d = Def(x.D, RAX);
        if (IsConst(x.B, out uint n))
        {
            LoadTo(d, x.A);
            n &= 31;
            if (n != 0)
            {
                switch (x.Op)
                {
                    case IrOp.Shl:
                        a.shl(s_g32[d], (byte)n);
                        break;
                    case IrOp.Shr:
                        a.shr(s_g32[d], (byte)n);
                        break;
                    case IrOp.Sar:
                        a.sar(s_g32[d], (byte)n);
                        break;
                    default:
                        a.ror(s_g32[d], (byte)n);
                        break;
                }
            }
        }
        else
        {
            LoadTo(RCX, x.B);
            LoadTo(d, x.A);
            switch (x.Op)
            {
                case IrOp.Shl:
                    a.shl(s_g32[d], cl);
                    break;
                case IrOp.Shr:
                    a.shr(s_g32[d], cl);
                    break;
                case IrOp.Sar:
                    a.sar(s_g32[d], cl);
                    break;
                default:
                    a.ror(s_g32[d], cl);
                    break;
            }
        }
        Commit(x.D, d);
    }

    /// <summary>ARM AddWithCarry with NZCV outputs (SubC: x - y with ARM's inverted borrow).</summary>
    private void ArithFlags(IrInst x)
    {
        Assembler a = Asm;
        bool sub = x.Op == IrOp.SubC;
        int d = Def(x.D, RAX);
        Alu k;
        if (sub && x.D < 0 && x.C < 0 && !IsConst(x.A, out _) && InReg(x.A, out int ra))
        {
            d = ra;
            k = Alu.Cmp; // CMP: flags only, the operand register stays intact
        }
        else
        {
            LoadTo(d, x.A);
            k = Alu.Add;
        }
        if (k == Alu.Cmp)
        {
        }
        else if (x.C < 0)
        {
            k = sub ? Alu.Sub : Alu.Add;      // carry-in 1 (sub) / 0 (add)
        }
        else if (IsConst(x.C, out uint cin))
        {
            if (sub)
            {
                if (cin != 0)
                {
                    k = Alu.Sub;
                }
                else
                {
                    a.stc();
                    k = Alu.Sbb;
                }
            }
            else
            {
                if (cin == 0)
                {
                    k = Alu.Add;
                }
                else
                {
                    a.stc();
                    k = Alu.Adc;
                }
            }
        }
        else
        {
            // host CF = ARM carry (add) or NOT carry (sub: x64 subtracts the borrow)
            if (sub)
            {
                if (InReg(x.C, out int cr))
                {
                    a.cmp(s_g32[cr], 1);
                }
                else
                {
                    a.cmp(Slot(x.C), 1);
                }

                k = Alu.Sbb;
            }
            else
            {
                if (InReg(x.C, out int cr))
                {
                    a.bt(s_g32[cr], (byte)0);
                }
                else
                {
                    a.bt(Slot(x.C), (byte)0);
                }

                k = Alu.Adc;
            }
        }
        AluOp(k, d, x.B);
        // flags are preserved by setcc/movzx/mov from here on
        SetFlagOut(x.Fn, r => a.sets(r));
        SetFlagOut(x.Fz, r => a.sete(r));
        if (sub)
        {
            SetFlagOut(x.Fc, r => a.setae(r));
        }
        else
        {
            SetFlagOut(x.Fc, r => a.setb(r));
        }

        SetFlagOut(x.Fv, r => a.seto(r));
        if (x.D >= 0)
        {
            Commit(x.D, d);
        }
    }

    /// <summary>ARM register-controlled shift (Shift_C with the amount in the low byte of Rs).</summary>
    private void ShiftReg(IrInst x)
    {
        Assembler a = Asm;
        uint type = x.Imm;
        bool wantC = x.Fc >= 0;
        LoadTo(RCX, x.B);
        a.movzx(ecx, cl);
        int d = Def(x.D, RAX);
        LoadTo(d, x.A);
        AssemblerRegister32 D = s_g32[d];
        Label done = a.CreateLabel();
        if (!wantC)
        {
            switch (type)
            {
                case 0:
                case 1:
                {
                    Label small = a.CreateLabel();
                    a.cmp(ecx, 32);
                    a.jb(small);
                    a.xor(D, D);
                    a.jmp(done);
                    Place(ref small);
                    if (type == 0)
                    {
                        a.shl(D, cl);
                    }
                    else
                    {
                        a.shr(D, cl);
                    }

                    break;
                }
                case 2:
                {
                    Label small = a.CreateLabel();
                    a.cmp(ecx, 32);
                    a.jb(small);
                    a.mov(ecx, 31u);
                    Place(ref small);
                    a.sar(D, cl);
                    break;
                }
                default:
                    a.ror(D, cl);
                    break;
            }
            Place(ref done);
            Commit(x.D, d);
            return;
        }
        // with carry-out in edx
        Label zero = a.CreateLabel();
        Label setc = a.CreateLabel();
        Label store = a.CreateLabel();
        a.test(ecx, ecx);
        a.jz(zero);
        switch (type)
        {
            case 0:
            case 1:
            {
                Label eq = a.CreateLabel();
                Label gt = a.CreateLabel();
                a.cmp(ecx, 32);
                a.je(eq);
                a.ja(gt);
                if (type == 0)
                {
                    a.shl(D, cl);
                }
                else
                {
                    a.shr(D, cl);
                }

                a.jmp(setc);
                Place(ref eq);
                a.mov(edx, D);
                if (type == 0)
                {
                    a.and(edx, 1);
                }
                else
                {
                    a.shr(edx, (byte)31);
                }

                a.xor(D, D);
                a.jmp(store);
                Place(ref gt);
                a.xor(edx, edx);
                a.xor(D, D);
                a.jmp(store);
                break;
            }
            case 2:
            {
                Label big = a.CreateLabel();
                a.cmp(ecx, 32);
                a.jae(big);
                a.sar(D, cl);
                a.jmp(setc);
                Place(ref big);
                a.sar(D, (byte)31);
                a.mov(edx, D);
                a.and(edx, 1);
                a.jmp(store);
                break;
            }
            default:
            {
                Label mult = a.CreateLabel();
                a.and(ecx, 31);
                a.jz(mult);
                a.ror(D, cl);
                a.jmp(setc);
                Place(ref mult);
                a.mov(edx, D);
                a.shr(edx, (byte)31);
                a.jmp(store);
                break;
            }
        }
        Place(ref zero);
        if (x.C >= 0)
        {
            LoadTo(RDX, x.C);
        }
        else
        {
            a.xor(edx, edx);
        }

        a.jmp(store);
        Place(ref setc);
        a.setb(dl);
        a.movzx(edx, dl);
        Place(ref store);
        Commit(x.Fc, RDX);
        Commit(x.D, d);
    }

    /// <summary>Jumps to the label when ARM condition Imm does not hold (A=N, B=Z, C=C, E=V).</summary>
    private void CondBranch(IrInst x)
    {
        Assembler a = Asm;
        Label skip = _labels[x.Imm2];
        uint cond = x.Imm;
        void TestFlag(int t)
        {
            int r = Use(t, RAX);
            a.test(s_g32[r], s_g32[r]);
        }
        switch (cond)
        {
            case 0:
                TestFlag(x.B);
                a.jz(skip);
                break;   // EQ: Z set
            case 1:
                TestFlag(x.B);
                a.jnz(skip);
                break;  // NE
            case 2:
                TestFlag(x.C);
                a.jz(skip);
                break;   // CS
            case 3:
                TestFlag(x.C);
                a.jnz(skip);
                break;  // CC
            case 4:
                TestFlag(x.A);
                a.jz(skip);
                break;   // MI
            case 5:
                TestFlag(x.A);
                a.jnz(skip);
                break;  // PL
            case 6:
                TestFlag(x.E);
                a.jz(skip);
                break;   // VS
            case 7:
                TestFlag(x.E);
                a.jnz(skip);
                break;  // VC
            case 8:
            case 9: // HI: C && !Z ; LS: !C || Z
                LoadTo(RAX, x.B);
                a.xor(eax, 1);
                AluOp(Alu.And, RAX, x.C);
                if (cond == 8)
                {
                    a.jz(skip);
                }
                else
                {
                    a.jnz(skip);
                }

                break;
            case 10:
            case 11: // GE: N == V ; LT: N != V
                LoadTo(RAX, x.A);
                AluOp(Alu.Cmp, RAX, x.E);
                if (cond == 10)
                {
                    a.jne(skip);
                }
                else
                {
                    a.je(skip);
                }

                break;
            case 12:
            case 13: // GT: !Z && N == V ; LE: Z || N != V
                LoadTo(RAX, x.A);
                AluOp(Alu.Xor, RAX, x.E);
                AluOp(Alu.Or, RAX, x.B);
                if (cond == 12)
                {
                    a.jnz(skip);
                }
                else
                {
                    a.jz(skip);
                }

                break;
            default:
                break; // AL
        }
    }

    // ------------------------------------------------------------------ runtime stubs

    /// <summary>
    /// The entry trampoline: saves the callee-saved registers, builds the frame, loads rbx/r15 and jumps to the block.
    /// Blocks leave through the epilogue (returned in <paramref name="epilogueOffset"/>).
    /// </summary>
    /// <summary>
    /// Vectored exception handler front: tail-calls <paramref name="handler"/> only when the faulting RIP lies in
    /// [lo, hi) (Windows x64: rcx = EXCEPTION_POINTERS*), else returns EXCEPTION_CONTINUE_SEARCH without touching
    /// managed code.
    /// </summary>
    public static byte[] EmitFaultFilter(ulong rip, ulong lo, ulong hi, ulong handler)
    {
        var a = new Assembler(64);
        Label no = a.CreateLabel();
        a.mov(rax, __qword_ptr[rcx + 8]);
        a.mov(rax, __qword_ptr[rax + 0xF8]);
        a.mov(rdx, lo);
        a.cmp(rax, rdx);
        a.jb(no);
        a.mov(rdx, hi);
        a.cmp(rax, rdx);
        a.jae(no);
        a.mov(rax, handler);
        a.jmp(rax);
        a.Label(ref no);
        a.xor(eax, eax);
        a.ret();
        using var ms = new MemoryStream();
        a.Assemble(new StreamCodeWriter(ms), rip);
        return ms.ToArray();
    }

    /// <summary>The entry trampoline (rcx/rdi = CpuState*, rdx/rsi = block entry): r15 = the state's guest memory base
    /// (its process's address space), then jumps to the block.</summary>
    public static byte[] EmitTrampoline(ulong rip, out int epilogueOffset)
    {
        var a = new Assembler(64);
        Label epi = a.CreateLabel();
        // 8 pushes keep rsp == 8 (mod 16); the frame (32 shadow + spill + 8) realigns it to 16 for callouts
        AssemblerRegister64[] saved = [rbx, rbp, rsi, rdi, r12, r13, r14, r15];
        foreach (AssemblerRegister64 r in saved)
        {
            a.push(r);
        }

        int frame = ShadowSpace + 8 * SpillSlots + 8;
        a.sub(rsp, frame);
        if (Windows)
        {
            a.mov(rbx, rcx);
        }
        else
        {
            a.mov(rbx, rdi);
        }

        a.mov(r15, __qword_ptr[rbx + s_offMemBase]);
        // VFP fast paths switch to FTZ+DAZ, round to nearest, all exceptions masked, and back to the host mode
        a.stmxcsr(__dword_ptr[rbx + s_offMxcsrHost]);
        a.mov(eax, __dword_ptr[rbx + s_offMxcsrHost]);
        a.and(eax, ~0x6000);
        a.or(eax, 0x8040 | 0x1F80);
        a.mov(__dword_ptr[rbx + s_offMxcsrGuest], eax);
        // NEON fast paths: DAZ only (denormal inputs read as zero), results are flushed explicitly like the handlers do
        a.and(eax, ~0x8000);
        a.mov(__dword_ptr[rbx + s_offMxcsrNeon], eax);
        if (Windows)
        {
            a.jmp(rdx);
        }
        else
        {
            a.jmp(rsi);
        }

        a.Label(ref epi);
        a.add(rsp, frame);
        for (int i = saved.Length - 1; i >= 0; i--)
        {
            a.pop(saved[i]);
        }

        a.ret();
        using var ms = new MemoryStream();
        AssemblerResult res = a.Assemble(new StreamCodeWriter(ms), rip, BlockEncoderOptions.ReturnNewInstructionOffsets);
        epilogueOffset = (int)res.GetLabelRIP(epi) - (int)rip;
        return ms.ToArray();
    }
}

/// <summary>Debug view of generated code (tests, VITASHARP_NATIVE_DUMP).</summary>
public static unsafe class X64Disassembler
{
    public static string Disassemble(nint code, int length)
    {
        byte[] bytes = new ReadOnlySpan<byte>((void*)code, length).ToArray();
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes), (ulong)code);
        var formatter = new NasmFormatter();
        var output = new StringOutput();
        var sb = new System.Text.StringBuilder();
        while (decoder.IP < (ulong)code + (ulong)length)
        {
            decoder.Decode(out Instruction ins);
            formatter.Format(ins, output);
            sb.Append($"{ins.IP - (ulong)code,5:X}: {output.ToStringAndReset()}\n");
        }
        return sb.ToString();
    }
}
