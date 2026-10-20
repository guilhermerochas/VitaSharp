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
public sealed unsafe class X64Emitter
{
    const int RAX = 0, RCX = 1, RDX = 2, RBX = 3, RSP = 4, RBP = 5, RSI = 6, RDI = 7, R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, R13 = 13, R14 = 14, R15 = 15;

    static readonly AssemblerRegister32[] G32 = [eax, ecx, edx, ebx, esp, ebp, esi, edi, r8d, r9d, r10d, r11d, r12d, r13d, r14d, r15d];
    static readonly AssemblerRegister64[] G64 = [rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8, r9, r10, r11, r12, r13, r14, r15];
    static readonly AssemblerRegister16[] G16 = [ax, cx, dx, bx, sp, bp, si, di, r8w, r9w, r10w, r11w, r12w, r13w, r14w, r15w];
    static readonly AssemblerRegister8[] G8 = [al, cl, dl, bl, spl, bpl, sil, dil, r8b, r9b, r10b, r11b, r12b, r13b, r14b, r15b];

    public static readonly bool Windows = OperatingSystem.IsWindows();
    /// <summary>Callee-saved registers available for temps (survive callouts).</summary>
    static readonly int[] CalleeSaved = Windows ? [RSI, RDI, RBP, R12, R13, R14] : [RBP, R12, R13, R14];
    /// <summary>Caller-saved registers available for temps (not across callouts).</summary>
    static readonly int[] CallerSaved = Windows ? [R8, R9, R10, R11] : [RSI, RDI, R8, R9, R10, R11];

    /// <summary>Spill slots the trampoline frame provides (8 bytes each, above the 32-byte shadow space).</summary>
    public const int SpillSlots = 64;
    public const int ShadowSpace = 32;

    static readonly bool HasLzcnt = System.Runtime.Intrinsics.X86.Lzcnt.IsSupported;

    // CpuState field offsets
    static readonly int OffR = 0;
    static readonly int OffN = StateLayout.Of(nameof(CpuState.N));
    static readonly int OffCycleAcc = StateLayout.Of(nameof(CpuState.CycleAcc));
    static readonly int OffCheckWindow = StateLayout.Of(nameof(CpuState.CheckWindow));
    static readonly int OffNBlocks = StateLayout.Of(nameof(CpuState.NBlocks));
    static readonly int OffNInstr = StateLayout.Of(nameof(CpuState.NInstr));
    static readonly int OffLastExitSlot = StateLayout.Of(nameof(CpuState.LastExitSlot));
    static readonly int OffLinkBreak = StateLayout.Of(nameof(CpuState.LinkBreak));
    static readonly int OffThumb = StateLayout.Of(nameof(CpuState.Thumb));

    /// <summary>Debug knob for tests: restricts the temp registers so spilling paths get exercised.</summary>
    public static int MaxTempRegisters = int.MaxValue;

    Assembler? _assembler;
    Assembler _a { get => _assembler!; set => _assembler = value; }
    readonly MemoryStream _code = new(1024);
    StreamCodeWriter? _writer;
    IrBlock _ir = null!;
    int[] _loc = null!; // >= 0 register, <= -2 spill slot -(loc+2), -1 none
    Label[] _labels = null!;
    Label _abort;

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
    public bool Prepare(IrBlock ir, Runtime rt, Func<int, nint> slotFor)
    {
        _ir = ir;
        if (_assembler == null) _assembler = new Assembler(64); else _assembler.Reset();
        if (!Allocate()) return false;
        _labels = new Label[ir.Labels];
        for (int i = 0; i < ir.Labels; i++) _labels[i] = _a.CreateLabel();
        _abort = _a.CreateLabel();
        int exitIndex = 0;
        foreach (var x in ir.Insts) Lower(x, rt, slotFor, ref exitIndex);
        // shared path for callouts that request an exit (Exit set, exception): account the block and leave
        _a.Label(ref _abort);
        EmitAccounting();
        _a.mov(__qword_ptr[rbx + OffLastExitSlot], 0);
        _a.jmp(rt.Epilogue);
        return true;
    }

    /// <summary>Encodes the prepared code for execution at <paramref name="rip"/>.</summary>
    public byte[] Assemble(ulong rip)
    {
        _code.SetLength(0);
        _a.Assemble(_writer ??= new StreamCodeWriter(_code), rip);
        return _code.ToArray();
    }

    // ------------------------------------------------------------------ register allocation

    bool Allocate()
    {
        var ir = _ir;
        int n = ir.TempCount;
        var def = new int[n];
        var last = new int[n];
        Array.Fill(def, -1);
        Array.Fill(last, -1);
        var insts = ir.Insts;
        int count = insts.Count;
        var calloutsBefore = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            var x = insts[i];
            calloutsBefore[i + 1] = calloutsBefore[i] + (x.Op == IrOp.Callout ? 1 : 0);
            void Out(int t, int idx) { if (t >= 0) def[t] = idx; }
            void In(int t, int idx) { if (t >= 0) last[t] = idx; }
            Out(x.D, i); Out(x.D2, i); Out(x.Fn, i); Out(x.Fz, i); Out(x.Fc, i); Out(x.Fv, i);
            In(x.A, i); In(x.B, i); In(x.C, i); In(x.E, i);
        }
        _loc = new int[n];
        Array.Fill(_loc, -1);
        var freeRegs = new List<int>();
        int limit = MaxTempRegisters;
        foreach (var r in CallerSaved) if (freeRegs.Count < limit) freeRegs.Add(r);
        foreach (var r in CalleeSaved) if (freeRegs.Count < limit) freeRegs.Add(r);
        var active = new List<int>(); // temps currently holding registers
        // a slot is free for a whole live range [from, until] when its previous occupant died before "from"
        // (spill victims are spilled retroactively from their definition, not from the current instruction)
        var slotFreeAfter = new int[SpillSlots];
        Array.Fill(slotFreeAfter, -1);
        int TakeSlot(int from, int until)
        {
            for (int s = 0; s < SpillSlots; s++)
                if (slotFreeAfter[s] < from) { slotFreeAfter[s] = until; return s; }
            return -1;
        }

        bool Crosses(int t) => last[t] > def[t] && calloutsBefore[last[t]] - calloutsBefore[def[t] + 1] > 0;
        static bool IsCallee(int r) => Array.IndexOf(CalleeSaved, r) >= 0;

        for (int i = 0; i < count; i++)
        {
            // release temps whose last use is before this instruction
            for (int k = active.Count - 1; k >= 0; k--)
                if (last[active[k]] < i) { freeRegs.Add(_loc[active[k]]); active.RemoveAt(k); }

            var x = insts[i];
            foreach (int t in (ReadOnlySpan<int>)[x.D, x.D2, x.Fn, x.Fz, x.Fc, x.Fv])
            {
                if (t < 0 || ir.IsConst[t]) continue;
                if (last[t] < 0) { last[t] = i; } // defined but unused (kept for side effects): short interval
                bool crosses = Crosses(t);
                int reg = -1;
                // pick: crossing temps need callee-saved; others prefer caller-saved
                for (int k = 0; k < freeRegs.Count; k++)
                {
                    int r = freeRegs[k];
                    if (crosses && !IsCallee(r)) continue;
                    if (reg < 0 || (!crosses && IsCallee(reg) && !IsCallee(r))) reg = r;
                }
                if (reg >= 0)
                {
                    freeRegs.Remove(reg);
                    _loc[t] = reg;
                    active.Add(t);
                    continue;
                }
                // spill the compatible active temp that lives longest (or this one)
                int victim = -1;
                foreach (int a in active)
                {
                    if (crosses && !IsCallee(_loc[a])) continue;
                    if (victim < 0 || last[a] > last[victim]) victim = a;
                }
                if (victim >= 0 && last[victim] > last[t])
                {
                    int slot = TakeSlot(def[victim], last[victim]);
                    if (slot < 0) return false;
                    _loc[t] = _loc[victim];
                    active.Remove(victim);
                    active.Add(t);
                    _loc[victim] = -2 - slot;
                }
                else
                {
                    int slot = TakeSlot(i, last[t]);
                    if (slot < 0) return false;
                    _loc[t] = -2 - slot;
                }
            }
        }
        if (VerifyAllocation)
        {
            // no two temps whose live ranges overlap may share a register or a spill slot
            var temps = Enumerable.Range(0, n).Where(t => _loc[t] != -1).ToArray();
            foreach (int x in temps)
                foreach (int y in temps)
                    if (x < y && _loc[x] == _loc[y] && def[x] <= last[y] && def[y] <= last[x])
                        throw new InvalidOperationException($"allocation: t{x} [{def[x]},{last[x]}] and t{y} [{def[y]},{last[y]}] share {_loc[x]}\n{ir}");
        }
        return true;
    }

    /// <summary>Debug: check the register allocation of every block (tests).</summary>
    public static bool VerifyAllocation;

    // ------------------------------------------------------------------ operand helpers

    bool IsConst(int t, out uint v) => _ir.TryConst(t, out v);
    bool InReg(int t, out int r) { r = t >= 0 ? _loc[t] : -1; return r >= 0; }
    AssemblerMemoryOperand Slot(int t) => __dword_ptr[rsp + (ShadowSpace + 8 * (-(_loc[t] + 2)))];
    static AssemblerMemoryOperand State(int off) => __dword_ptr[rbx + off];

    /// <summary>Loads temp <paramref name="t"/> into register <paramref name="r"/> (flags preserved).</summary>
    void LoadTo(int r, int t)
    {
        if (IsConst(t, out uint v)) _a.mov(G32[r], v);
        else if (InReg(t, out int s)) { if (s != r) _a.mov(G32[r], G32[s]); }
        else _a.mov(G32[r], Slot(t));
    }

    /// <summary>A register holding temp t: its own, or <paramref name="scratch"/> loaded with it.</summary>
    int Use(int t, int scratch)
    {
        if (!IsConst(t, out _) && InReg(t, out int r)) return r;
        LoadTo(scratch, t);
        return scratch;
    }

    int Def(int t, int scratch) => t >= 0 && InReg(t, out int r) ? r : scratch;

    void Commit(int t, int r)
    {
        if (t < 0) return;
        if (InReg(t, out int own)) { if (own != r) _a.mov(G32[own], G32[r]); return; }
        _a.mov(Slot(t), G32[r]);
    }

    enum Alu { Add, Sub, And, Or, Xor, Adc, Sbb, Cmp, Imul }

    /// <summary>d = d (op) t, with t as immediate, register or spill slot.</summary>
    void AluOp(Alu k, int d, int t)
    {
        var D = G32[d];
        if (IsConst(t, out uint v))
        {
            int iv = (int)v;
            switch (k)
            {
                case Alu.Add: _a.add(D, iv); break;
                case Alu.Sub: _a.sub(D, iv); break;
                case Alu.And: _a.and(D, iv); break;
                case Alu.Or: _a.or(D, iv); break;
                case Alu.Xor: _a.xor(D, iv); break;
                case Alu.Adc: _a.adc(D, iv); break;
                case Alu.Sbb: _a.sbb(D, iv); break;
                case Alu.Cmp: _a.cmp(D, iv); break;
                case Alu.Imul: _a.imul(D, D, iv); break;
            }
        }
        else if (InReg(t, out int r))
        {
            var S = G32[r];
            switch (k)
            {
                case Alu.Add: _a.add(D, S); break;
                case Alu.Sub: _a.sub(D, S); break;
                case Alu.And: _a.and(D, S); break;
                case Alu.Or: _a.or(D, S); break;
                case Alu.Xor: _a.xor(D, S); break;
                case Alu.Adc: _a.adc(D, S); break;
                case Alu.Sbb: _a.sbb(D, S); break;
                case Alu.Cmp: _a.cmp(D, S); break;
                case Alu.Imul: _a.imul(D, S); break;
            }
        }
        else
        {
            var M = Slot(t);
            switch (k)
            {
                case Alu.Add: _a.add(D, M); break;
                case Alu.Sub: _a.sub(D, M); break;
                case Alu.And: _a.and(D, M); break;
                case Alu.Or: _a.or(D, M); break;
                case Alu.Xor: _a.xor(D, M); break;
                case Alu.Adc: _a.adc(D, M); break;
                case Alu.Sbb: _a.sbb(D, M); break;
                case Alu.Cmp: _a.cmp(D, M); break;
                case Alu.Imul: _a.imul(D, M); break;
            }
        }
    }

    /// <summary>Stores 0/1 of the current host condition into flag temp t (setcc + movzx: flags preserved).</summary>
    void SetFlagOut(int t, Action<AssemblerRegister8> setcc)
    {
        if (t < 0) return;
        int r = Def(t, RDX);
        setcc(G8[r]);
        _a.movzx(G32[r], G8[r]);
        Commit(t, r);
    }

    /// <summary>Guest memory operand for address temp t (loads it into rax unless it is in a register).</summary>
    AssemblerMemoryOperand GuestMem(int t, MemorySize size)
    {
        int r = Use(t, RAX);
        var m = r15 + G64[r];
        return size switch
        {
            MemorySize.UInt8 => __byte_ptr[m],
            MemorySize.UInt16 => __word_ptr[m],
            _ => __dword_ptr[m],
        };
    }

    // ------------------------------------------------------------------ lowering

    void EmitAccounting()
    {
        _a.add(__qword_ptr[rbx + OffCycleAcc], (int)Math.Min(_ir.Cycles, int.MaxValue));
        _a.inc(__qword_ptr[rbx + OffNBlocks]);
        _a.add(__qword_ptr[rbx + OffNInstr], _ir.GuestInstructions);
    }

    void Lower(IrInst x, Runtime rt, Func<int, nint> slotFor, ref int exitIndex)
    {
        var a = _a;
        switch (x.Op)
        {
            case IrOp.Nop: return;

            // ---------------- state
            case IrOp.GetReg: { int d = Def(x.D, RAX); a.mov(G32[d], State(OffR + 4 * (int)x.Imm)); Commit(x.D, d); return; }
            case IrOp.GetFlag: { int d = Def(x.D, RAX); a.mov(G32[d], State(OffN + 4 * (int)x.Imm)); Commit(x.D, d); return; }
            case IrOp.LoadState: { int d = Def(x.D, RAX); a.mov(G32[d], State((int)x.Imm)); Commit(x.D, d); return; }
            case IrOp.SetReg: StoreTo(State(OffR + 4 * (int)x.Imm), x.A); return;
            case IrOp.SetFlag: StoreTo(State(OffN + 4 * (int)x.Imm), x.A); return;
            case IrOp.StoreState: StoreTo(State((int)x.Imm), x.A); return;

            // ---------------- ALU
            case IrOp.Add: Bin(Alu.Add, x); return;
            case IrOp.Sub: Bin(Alu.Sub, x); return;
            case IrOp.And: Bin(Alu.And, x); return;
            case IrOp.Or: Bin(Alu.Or, x); return;
            case IrOp.Xor: Bin(Alu.Xor, x); return;
            case IrOp.Mul: Bin(Alu.Imul, x); return;
            case IrOp.AndNot: case IrOp.OrNot:
            {
                int d = Def(x.D, RAX);
                LoadTo(d, x.B);
                a.not(G32[d]);
                AluOp(x.Op == IrOp.AndNot ? Alu.And : Alu.Or, d, x.A);
                Commit(x.D, d);
                return;
            }
            case IrOp.Not: { int d = Def(x.D, RAX); LoadTo(d, x.A); a.not(G32[d]); Commit(x.D, d); return; }
            case IrOp.Neg: { int d = Def(x.D, RAX); LoadTo(d, x.A); a.neg(G32[d]); Commit(x.D, d); return; }
            case IrOp.Shl: case IrOp.Shr: case IrOp.Sar: case IrOp.Ror: Shift(x); return;
            case IrOp.Clz:
            {
                int d = Def(x.D, RAX);
                if (HasLzcnt)
                {
                    int s = Use(x.A, RCX);
                    a.lzcnt(G32[d], G32[s]);
                }
                else
                {
                    int s = Use(x.A, RCX);
                    var zero = a.CreateLabel(); var done = a.CreateLabel();
                    a.bsr(G32[d], G32[s]);
                    a.jz(zero);
                    a.xor(G32[d], 31);
                    a.jmp(done);
                    a.Label(ref zero);
                    a.mov(G32[d], 32u);
                    a.Label(ref done);
                }
                Commit(x.D, d);
                return;
            }
            case IrOp.Bswap: { int d = Def(x.D, RAX); LoadTo(d, x.A); a.bswap(G32[d]); Commit(x.D, d); return; }
            case IrOp.Rev16: { int d = Def(x.D, RAX); LoadTo(d, x.A); a.bswap(G32[d]); a.ror(G32[d], (byte)16); Commit(x.D, d); return; }
            case IrOp.RevSh:
            {
                LoadTo(RAX, x.A);
                a.rol(ax, (byte)8);
                int d = Def(x.D, RAX);
                a.movsx(G32[d], ax);
                Commit(x.D, d);
                return;
            }
            case IrOp.Rbit:
            {
                LoadTo(RAX, x.A);
                foreach (var (sh, mask) in new[] { (1, 0x55555555), (2, 0x33333333), (4, 0x0F0F0F0F) })
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
                if (d != RAX) a.mov(G32[d], eax);
                Commit(x.D, d);
                return;
            }
            case IrOp.SExt8: case IrOp.SExt16: case IrOp.ZExt8: case IrOp.ZExt16:
            {
                int d = Def(x.D, RAX);
                int s = Use(x.A, RCX);
                switch (x.Op)
                {
                    case IrOp.SExt8: a.movsx(G32[d], G8[s]); break;
                    case IrOp.SExt16: a.movsx(G32[d], G16[s]); break;
                    case IrOp.ZExt8: a.movzx(G32[d], G8[s]); break;
                    default: a.movzx(G32[d], G16[s]); break;
                }
                Commit(x.D, d);
                return;
            }
            case IrOp.Bit:
            {
                int d = Def(x.D, RAX);
                LoadTo(d, x.A);
                if (x.Imm != 0) a.shr(G32[d], (byte)x.Imm);
                a.and(G32[d], 1);
                Commit(x.D, d);
                return;
            }
            case IrOp.CmpEq:
            {
                int s = Use(x.A, RCX);
                AluOp(Alu.Cmp, s, x.B);
                int d = Def(x.D, RAX);
                a.sete(G8[d]);
                a.movzx(G32[d], G8[d]);
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
                a.test(G32[c], G32[c]);
                a.cmovne(G32[d], G32[va]);
                Commit(x.D, d);
                return;
            }
            case IrOp.UDiv: case IrOp.SDiv:
            {
                var zero = a.CreateLabel(); var done = a.CreateLabel(); var div = a.CreateLabel();
                LoadTo(RAX, x.A);
                LoadTo(RCX, x.B);
                a.test(ecx, ecx);
                a.jz(zero);
                if (x.Op == IrOp.UDiv) { a.xor(edx, edx); a.div(ecx); }
                else
                {
                    a.cmp(ecx, -1);
                    a.jne(div);
                    a.cmp(eax, unchecked((int)0x80000000));
                    a.je(done); // INT_MIN / -1 = INT_MIN (x64 would fault)
                    a.Label(ref div);
                    a.cdq();
                    a.idiv(ecx);
                }
                a.jmp(done);
                a.Label(ref zero);
                a.xor(eax, eax);
                a.Label(ref done);
                int d = Def(x.D, RAX);
                if (d != RAX) a.mov(G32[d], eax);
                Commit(x.D, d);
                return;
            }

            // ---------------- flags
            case IrOp.AddC: case IrOp.SubC: ArithFlags(x); return;
            case IrOp.TestNZ:
            {
                int s = Use(x.A, RAX);
                a.test(G32[s], G32[s]);
                SetFlagOut(x.Fn, r => a.sets(r));
                SetFlagOut(x.Fz, r => a.sete(r));
                return;
            }
            case IrOp.ShiftReg: ShiftReg(x); return;

            // ---------------- 64-bit
            case IrOp.UMulL: case IrOp.SMulL:
            {
                if (x.Op == IrOp.UMulL) { LoadTo(RAX, x.A); LoadTo(RCX, x.B); }
                else
                {
                    LoadTo(RAX, x.A); a.movsxd(rax, eax);
                    LoadTo(RCX, x.B); a.movsxd(rcx, ecx);
                }
                a.imul(rax, rcx);
                if (x.D >= 0) { int d = Def(x.D, RCX); a.mov(G32[d], eax); Commit(x.D, d); }
                if (x.D2 >= 0) { a.shr(rax, (byte)32); int d = Def(x.D2, RAX); if (d != RAX) a.mov(G32[d], eax); Commit(x.D2, d); }
                return;
            }
            case IrOp.Add64:
            {
                LoadTo(RAX, x.A);
                LoadTo(RDX, x.B);
                AluOp(Alu.Add, RAX, x.C);
                AluOp(Alu.Adc, RDX, x.E);
                if (x.D >= 0) Commit(x.D, RAX);
                if (x.D2 >= 0) Commit(x.D2, RDX);
                return;
            }

            // ---------------- memory
            case IrOp.Load8: case IrOp.Load8S: case IrOp.Load16: case IrOp.Load16S: case IrOp.Load32:
            {
                int d = Def(x.D, RCX);
                switch (x.Op)
                {
                    case IrOp.Load8: a.movzx(G32[d], GuestMem(x.A, MemorySize.UInt8)); break;
                    case IrOp.Load8S: a.movsx(G32[d], GuestMem(x.A, MemorySize.UInt8)); break;
                    case IrOp.Load16: a.movzx(G32[d], GuestMem(x.A, MemorySize.UInt16)); break;
                    case IrOp.Load16S: a.movsx(G32[d], GuestMem(x.A, MemorySize.UInt16)); break;
                    default: a.mov(G32[d], GuestMem(x.A, MemorySize.UInt32)); break;
                }
                Commit(x.D, d);
                return;
            }
            case IrOp.Store8: case IrOp.Store16: case IrOp.Store32:
            {
                var mem = GuestMem(x.A, x.Op == IrOp.Store8 ? MemorySize.UInt8 : x.Op == IrOp.Store16 ? MemorySize.UInt16 : MemorySize.UInt32);
                if (IsConst(x.B, out uint v))
                {
                    if (x.Op == IrOp.Store8) a.mov(mem, (byte)v);
                    else if (x.Op == IrOp.Store16) a.mov(mem, (short)v);
                    else a.mov(mem, (int)v);
                }
                else
                {
                    int s = Use(x.B, RDX);
                    if (x.Op == IrOp.Store8) a.mov(mem, G8[s]);
                    else if (x.Op == IrOp.Store16) a.mov(mem, G16[s]);
                    else a.mov(mem, G32[s]);
                }
                return;
            }
            case IrOp.Fence: a.mfence(); return;

            // ---------------- control
            case IrOp.Label: { var l = _labels[x.Imm]; a.Label(ref l); _labels[x.Imm] = l; return; }
            case IrOp.BranchIf:
            {
                int s = Use(x.A, RAX);
                a.test(G32[s], G32[s]);
                if (x.Imm != 0) a.jnz(_labels[x.Imm2]); else a.jz(_labels[x.Imm2]);
                return;
            }
            case IrOp.CondBranch: CondBranch(x); return;
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
                var ret = a.CreateLabel();
                EmitAccounting();
                a.mov(rax, __qword_ptr[rbx + OffCycleAcc]);
                a.cmp(rax, __qword_ptr[rbx + OffCheckWindow]);
                a.jge(ret);
                a.cmp(State(OffLinkBreak), 0);
                a.jne(ret);
                a.mov(rax, (ulong)slot);
                a.mov(rax, __qword_ptr[rax]);
                a.test(rax, rax);
                a.jz(ret);
                a.jmp(rax);
                a.Label(ref ret);
                a.mov(rax, (ulong)slot);
                a.mov(__qword_ptr[rbx + OffLastExitSlot], rax);
                a.jmp(rt.Epilogue);
                return;
            }
            case IrOp.ExitDynamic:
            {
                var ret = a.CreateLabel();
                EmitAccounting();
                if (x.Imm == 0 && rt.DispatchTable != 0)
                {
                    // indirect exit: look the target up in the dispatch table (code is read before the key, see
                    // NativeRuntime.Publish) and jump straight to it when the pacing window allows
                    a.mov(rax, __qword_ptr[rbx + OffCycleAcc]);
                    a.cmp(rax, __qword_ptr[rbx + OffCheckWindow]);
                    a.jge(ret);
                    a.cmp(State(OffLinkBreak), 0);
                    a.jne(ret);
                    a.mov(eax, State(OffR + 60));
                    a.mov(edx, eax);
                    a.or(eax, State(OffThumb));
                    a.shr(edx, (byte)1);
                    a.and(edx, (1 << NativeRuntime.DispatchBits) - 1);
                    a.shl(rdx, (byte)4);
                    a.mov(rcx, rt.DispatchTable);
                    a.add(rcx, rdx);
                    a.mov(rdx, __qword_ptr[rcx + 8]);
                    a.cmp(__dword_ptr[rcx], eax);
                    a.jne(ret);
                    a.jmp(rdx);
                }
                a.Label(ref ret);
                a.mov(__qword_ptr[rbx + OffLastExitSlot], 0);
                a.jmp(rt.Epilogue);
                return;
            }
            default: throw new NotSupportedException($"IR op {x.Op}");
        }
    }

    void StoreTo(AssemblerMemoryOperand m, int t)
    {
        if (IsConst(t, out uint v)) _a.mov(m, (int)v);
        else _a.mov(m, G32[Use(t, RAX)]);
    }

    void Bin(Alu k, IrInst x)
    {
        int d = Def(x.D, RAX);
        // commutative ops with a constant first operand: swap so the constant becomes the immediate
        int A = x.A, B = x.B;
        if (k is Alu.Add or Alu.And or Alu.Or or Alu.Xor or Alu.Imul && IsConst(A, out _) && !IsConst(B, out _)) (A, B) = (B, A);
        // additions (addresses mostly) as one 32-bit lea: wraps modulo 2^32 like the guest, leaves flags alone
        if (k == Alu.Add && !IsConst(A, out _) && InReg(A, out int ra))
        {
            if (IsConst(B, out uint imm)) { _a.lea(G32[d], __dword_ptr[G64[ra] + (int)imm]); Commit(x.D, d); return; }
            if (InReg(B, out int rb)) { _a.lea(G32[d], __dword_ptr[G64[ra] + G64[rb]]); Commit(x.D, d); return; }
        }
        LoadTo(d, A);
        AluOp(k, d, B);
        Commit(x.D, d);
    }

    void Shift(IrInst x)
    {
        var a = _a;
        int d = Def(x.D, RAX);
        if (IsConst(x.B, out uint n))
        {
            LoadTo(d, x.A);
            n &= 31;
            if (n != 0)
            {
                switch (x.Op)
                {
                    case IrOp.Shl: a.shl(G32[d], (byte)n); break;
                    case IrOp.Shr: a.shr(G32[d], (byte)n); break;
                    case IrOp.Sar: a.sar(G32[d], (byte)n); break;
                    default: a.ror(G32[d], (byte)n); break;
                }
            }
        }
        else
        {
            LoadTo(RCX, x.B);
            LoadTo(d, x.A);
            switch (x.Op)
            {
                case IrOp.Shl: a.shl(G32[d], cl); break;
                case IrOp.Shr: a.shr(G32[d], cl); break;
                case IrOp.Sar: a.sar(G32[d], cl); break;
                default: a.ror(G32[d], cl); break;
            }
        }
        Commit(x.D, d);
    }

    /// <summary>ARM AddWithCarry with NZCV outputs (SubC: x - y with ARM's inverted borrow).</summary>
    void ArithFlags(IrInst x)
    {
        var a = _a;
        bool sub = x.Op == IrOp.SubC;
        int d = Def(x.D, RAX);
        Alu k;
        if (sub && x.D < 0 && x.C < 0 && !IsConst(x.A, out _) && InReg(x.A, out int ra))
        {
            d = ra; k = Alu.Cmp; // CMP: flags only, the operand register stays intact
        }
        else
        {
            LoadTo(d, x.A);
            k = Alu.Add;
        }
        if (k == Alu.Cmp) { }
        else if (x.C < 0) k = sub ? Alu.Sub : Alu.Add;      // carry-in 1 (sub) / 0 (add)
        else if (IsConst(x.C, out uint cin))
        {
            if (sub) { if (cin != 0) k = Alu.Sub; else { a.stc(); k = Alu.Sbb; } }
            else { if (cin == 0) k = Alu.Add; else { a.stc(); k = Alu.Adc; } }
        }
        else
        {
            // host CF = ARM carry (add) or NOT carry (sub: x64 subtracts the borrow)
            if (sub)
            {
                if (InReg(x.C, out int cr)) a.cmp(G32[cr], 1); else a.cmp(Slot(x.C), 1);
                k = Alu.Sbb;
            }
            else
            {
                if (InReg(x.C, out int cr)) a.bt(G32[cr], (byte)0); else a.bt(Slot(x.C), (byte)0);
                k = Alu.Adc;
            }
        }
        AluOp(k, d, x.B);
        // flags are preserved by setcc/movzx/mov from here on
        SetFlagOut(x.Fn, r => a.sets(r));
        SetFlagOut(x.Fz, r => a.sete(r));
        if (sub) SetFlagOut(x.Fc, r => a.setae(r)); else SetFlagOut(x.Fc, r => a.setb(r));
        SetFlagOut(x.Fv, r => a.seto(r));
        if (x.D >= 0) Commit(x.D, d);
    }

    /// <summary>ARM register-controlled shift (Shift_C with the amount in the low byte of Rs).</summary>
    void ShiftReg(IrInst x)
    {
        var a = _a;
        uint type = x.Imm;
        bool wantC = x.Fc >= 0;
        LoadTo(RCX, x.B);
        a.movzx(ecx, cl);
        int d = Def(x.D, RAX);
        LoadTo(d, x.A);
        var D = G32[d];
        var done = a.CreateLabel();
        if (!wantC)
        {
            switch (type)
            {
                case 0: case 1:
                {
                    var small = a.CreateLabel();
                    a.cmp(ecx, 32);
                    a.jb(small);
                    a.xor(D, D);
                    a.jmp(done);
                    a.Label(ref small);
                    if (type == 0) a.shl(D, cl); else a.shr(D, cl);
                    break;
                }
                case 2:
                {
                    var small = a.CreateLabel();
                    a.cmp(ecx, 32);
                    a.jb(small);
                    a.mov(ecx, 31u);
                    a.Label(ref small);
                    a.sar(D, cl);
                    break;
                }
                default: a.ror(D, cl); break;
            }
            a.Label(ref done);
            Commit(x.D, d);
            return;
        }
        // with carry-out in edx
        var zero = a.CreateLabel(); var setc = a.CreateLabel(); var store = a.CreateLabel();
        a.test(ecx, ecx);
        a.jz(zero);
        switch (type)
        {
            case 0: case 1:
            {
                var eq = a.CreateLabel(); var gt = a.CreateLabel();
                a.cmp(ecx, 32);
                a.je(eq);
                a.ja(gt);
                if (type == 0) a.shl(D, cl); else a.shr(D, cl);
                a.jmp(setc);
                a.Label(ref eq);
                a.mov(edx, D);
                if (type == 0) a.and(edx, 1); else a.shr(edx, (byte)31);
                a.xor(D, D);
                a.jmp(store);
                a.Label(ref gt);
                a.xor(edx, edx);
                a.xor(D, D);
                a.jmp(store);
                break;
            }
            case 2:
            {
                var big = a.CreateLabel();
                a.cmp(ecx, 32);
                a.jae(big);
                a.sar(D, cl);
                a.jmp(setc);
                a.Label(ref big);
                a.sar(D, (byte)31);
                a.mov(edx, D);
                a.and(edx, 1);
                a.jmp(store);
                break;
            }
            default:
            {
                var mult = a.CreateLabel();
                a.and(ecx, 31);
                a.jz(mult);
                a.ror(D, cl);
                a.jmp(setc);
                a.Label(ref mult);
                a.mov(edx, D);
                a.shr(edx, (byte)31);
                a.jmp(store);
                break;
            }
        }
        a.Label(ref zero);
        if (x.C >= 0) LoadTo(RDX, x.C); else a.xor(edx, edx);
        a.jmp(store);
        a.Label(ref setc);
        a.setb(dl);
        a.movzx(edx, dl);
        a.Label(ref store);
        Commit(x.Fc, RDX);
        Commit(x.D, d);
    }

    /// <summary>Jumps to the label when ARM condition Imm does not hold (A=N, B=Z, C=C, E=V).</summary>
    void CondBranch(IrInst x)
    {
        var a = _a;
        var skip = _labels[x.Imm2];
        uint cond = x.Imm;
        void TestFlag(int t) { int r = Use(t, RAX); a.test(G32[r], G32[r]); }
        switch (cond)
        {
            case 0: TestFlag(x.B); a.jz(skip); break;   // EQ: Z set
            case 1: TestFlag(x.B); a.jnz(skip); break;  // NE
            case 2: TestFlag(x.C); a.jz(skip); break;   // CS
            case 3: TestFlag(x.C); a.jnz(skip); break;  // CC
            case 4: TestFlag(x.A); a.jz(skip); break;   // MI
            case 5: TestFlag(x.A); a.jnz(skip); break;  // PL
            case 6: TestFlag(x.E); a.jz(skip); break;   // VS
            case 7: TestFlag(x.E); a.jnz(skip); break;  // VC
            case 8: case 9: // HI: C && !Z ; LS: !C || Z
                LoadTo(RAX, x.B);
                a.xor(eax, 1);
                AluOp(Alu.And, RAX, x.C);
                if (cond == 8) a.jz(skip); else a.jnz(skip);
                break;
            case 10: case 11: // GE: N == V ; LT: N != V
                LoadTo(RAX, x.A);
                AluOp(Alu.Cmp, RAX, x.E);
                if (cond == 10) a.jne(skip); else a.je(skip);
                break;
            case 12: case 13: // GT: !Z && N == V ; LE: Z || N != V
                LoadTo(RAX, x.A);
                AluOp(Alu.Xor, RAX, x.E);
                AluOp(Alu.Or, RAX, x.B);
                if (cond == 12) a.jnz(skip); else a.jz(skip);
                break;
            default: break; // AL
        }
    }

    // ------------------------------------------------------------------ runtime stubs

    /// <summary>
    /// The entry trampoline: saves the callee-saved registers, builds the frame, loads rbx/r15 and jumps to the block.
    /// Blocks leave through the epilogue (returned in <paramref name="epilogueOffset"/>).
    /// </summary>
    public static byte[] EmitTrampoline(ulong rip, ulong memBase, out int epilogueOffset)
    {
        var a = new Assembler(64);
        var epi = a.CreateLabel();
        // 8 pushes keep rsp == 8 (mod 16); the frame (32 shadow + spill + 8) realigns it to 16 for callouts
        AssemblerRegister64[] saved = [rbx, rbp, rsi, rdi, r12, r13, r14, r15];
        foreach (var r in saved) a.push(r);
        int frame = ShadowSpace + 8 * SpillSlots + 8;
        a.sub(rsp, frame);
        if (Windows) { a.mov(rbx, rcx); a.mov(r15, memBase); a.jmp(rdx); }
        else { a.mov(rbx, rdi); a.mov(r15, memBase); a.jmp(rsi); }
        a.Label(ref epi);
        a.add(rsp, frame);
        for (int i = saved.Length - 1; i >= 0; i--) a.pop(saved[i]);
        a.ret();
        using var ms = new MemoryStream();
        var res = a.Assemble(new StreamCodeWriter(ms), rip, BlockEncoderOptions.ReturnNewInstructionOffsets);
        epilogueOffset = (int)res.GetLabelRIP(epi) - (int)rip;
        return ms.ToArray();
    }
}

/// <summary>Debug view of generated code (tests, VITASHARP_NATIVE_DUMP).</summary>
public static unsafe class X64Disassembler
{
    public static string Disassemble(nint code, int length)
    {
        var bytes = new ReadOnlySpan<byte>((void*)code, length).ToArray();
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes), (ulong)code);
        var formatter = new NasmFormatter();
        var output = new StringOutput();
        var sb = new System.Text.StringBuilder();
        while (decoder.IP < (ulong)code + (ulong)length)
        {
            decoder.Decode(out var ins);
            formatter.Format(ins, output);
            sb.Append($"{ins.IP - (ulong)code,5:X}: {output.ToStringAndReset()}\n");
        }
        return sb.ToString();
    }
}
