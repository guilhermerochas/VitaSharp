namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Minimal AArch64 encoder for the native ARM64 backend: the integer, load/store, branch, system, scalar FP and
/// Advanced SIMD instructions <see cref="Arm64Emitter"/> needs, with forward/backward labels and a literal pool
/// appended after the code (64-bit and 128-bit constants, addressed PC-relative with LDR literal). Every encoding is
/// unit-tested against GNU as (aarch64-linux-gnu-as 2.40) output. All code is position independent: absolute
/// addresses (runtime entry points, tables) live in the literal pool.
/// <para>Registers are numbers 0-31; 31 is the zero register or SP depending on the instruction (as in the ISA).</para>
/// </summary>
public sealed class Arm64Assembler
{
    public const int ZR = 31, SP = 31, LR = 30, FP = 29;

    /// <summary>Condition codes (the same numbering as ARM32's).</summary>
    public enum Cond : uint { EQ, NE, HS, LO, MI, PL, VS, VC, HI, LS, GE, LT, GT, LE, AL }

    public static Cond Invert(Cond c) => (Cond)((uint)c ^ 1);

    readonly List<uint> _code = new(256);
    readonly List<int> _labels = new();
    readonly List<(int At, int Label, Fix Kind)> _fixups = new();
    readonly List<(int At, int Literal)> _litFixups = new();
    readonly List<(ulong Lo, ulong Hi, bool Wide)> _literals = new();
    readonly Dictionary<(ulong, ulong, bool), int> _literalIndex = new();

    enum Fix { B26, Imm19, Imm14, Adr }

    public int Count => _code.Count;
    public IReadOnlyList<uint> Words => _code;

    public void Reset()
    {
        _code.Clear(); _labels.Clear(); _fixups.Clear(); _litFixups.Clear(); _literals.Clear(); _literalIndex.Clear();
    }

    public void Emit(uint w) => _code.Add(w);

    // ------------------------------------------------------------------ labels

    public int NewLabel() { _labels.Add(-1); return _labels.Count - 1; }
    public void Bind(int label) => _labels[label] = _code.Count;
    public bool IsBound(int label) => _labels[label] >= 0;

    void Ref(int label, Fix kind, uint word) { _fixups.Add((_code.Count, label, kind)); _code.Add(word); }

    int Literal(ulong lo, ulong hi, bool wide)
    {
        if (!_literalIndex.TryGetValue((lo, hi, wide), out int i))
        {
            i = _literals.Count;
            _literals.Add((lo, hi, wide));
            _literalIndex[(lo, hi, wide)] = i;
        }
        return i;
    }

    /// <summary>Resolves labels and literals and returns the little-endian machine code.</summary>
    public byte[] Finish()
    {
        // literal pool: 128-bit entries first (16-byte aligned), then 64-bit ones
        var words = new List<uint>(_code);
        var litPos = new int[_literals.Count];
        if (_literals.Count > 0)
        {
            while ((words.Count & 3) != 0) words.Add(Nop);
            for (int i = 0; i < _literals.Count; i++)
                if (_literals[i].Wide) { litPos[i] = words.Count; Add128(words, _literals[i].Lo, _literals[i].Hi); }
            for (int i = 0; i < _literals.Count; i++)
                if (!_literals[i].Wide) { litPos[i] = words.Count; words.Add((uint)_literals[i].Lo); words.Add((uint)(_literals[i].Lo >> 32)); }
        }
        foreach (var (at, label, kind) in _fixups)
        {
            int target = _labels[label];
            if (target < 0) throw new InvalidOperationException("unbound label");
            words[at] = Patch(words[at], target - at, kind);
        }
        foreach (var (at, lit) in _litFixups) words[at] = Patch(words[at], litPos[lit] - at, Fix.Imm19);
        var bytes = new byte[words.Count * 4];
        for (int i = 0; i < words.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    static void Add128(List<uint> w, ulong lo, ulong hi) { w.Add((uint)lo); w.Add((uint)(lo >> 32)); w.Add((uint)hi); w.Add((uint)(hi >> 32)); }

    static uint Patch(uint word, int delta, Fix kind)
    {
        switch (kind)
        {
            case Fix.B26:
                if (delta < -(1 << 25) || delta >= 1 << 25) throw new InvalidOperationException("branch out of range");
                return word | ((uint)delta & 0x03FFFFFF);
            case Fix.Imm19:
                if (delta < -(1 << 18) || delta >= 1 << 18) throw new InvalidOperationException("branch out of range");
                return word | (((uint)delta & 0x7FFFF) << 5);
            case Fix.Imm14:
                if (delta < -(1 << 13) || delta >= 1 << 13) throw new InvalidOperationException("branch out of range");
                return word | (((uint)delta & 0x3FFF) << 5);
            default:
            {
                int bytes = delta * 4;
                return word | (((uint)bytes & 3) << 29) | ((((uint)bytes >> 2) & 0x7FFFF) << 5);
            }
        }
    }

    // ------------------------------------------------------------------ branches

    public const uint Nop = 0xD503201F;

    public void B(int label) => Ref(label, Fix.B26, 0x14000000);
    public void Bl(int label) => Ref(label, Fix.B26, 0x94000000);
    public void BCond(Cond c, int label) => Ref(label, Fix.Imm19, 0x54000000 | (uint)c);
    public void Cbz(int rt, int label, bool x = false) => Ref(label, Fix.Imm19, 0x34000000 | Sf(x) | (uint)rt);
    public void Cbnz(int rt, int label, bool x = false) => Ref(label, Fix.Imm19, 0x35000000 | Sf(x) | (uint)rt);
    public void Tbz(int rt, int bit, int label) => Ref(label, Fix.Imm14, 0x36000000 | ((uint)bit >> 5 << 31) | (((uint)bit & 31) << 19) | (uint)rt);
    public void Tbnz(int rt, int bit, int label) => Ref(label, Fix.Imm14, 0x37000000 | ((uint)bit >> 5 << 31) | (((uint)bit & 31) << 19) | (uint)rt);
    public void Adr(int rd, int label) => Ref(label, Fix.Adr, 0x10000000 | (uint)rd);
    public void Br(int rn) => Emit(0xD61F0000 | (uint)rn << 5);
    public void Blr(int rn) => Emit(0xD63F0000 | (uint)rn << 5);
    public void Ret(int rn = LR) => Emit(0xD65F0000 | (uint)rn << 5);

    // ------------------------------------------------------------------ system

    public void DmbIsh() => Emit(0xD5033BBF);
    public void DsbIsh() => Emit(0xD5033B9F);
    public void Isb() => Emit(0xD5033FDF);
    public void Yield() => Emit(0xD503203F);
    public void Clrex() => Emit(0xD5033F5F);
    public void Brk(ushort imm) => Emit(0xD4200000 | (uint)imm << 5);

    public enum SysReg : uint { Nzcv = 0xDA10, Fpcr = 0xDA20, Fpsr = 0xDA21, CtrEl0 = 0xD801 }
    public void Mrs(int rt, SysReg r) => Emit(0xD5300000 | (uint)r << 5 | (uint)rt);
    public void Msr(SysReg r, int rt) => Emit(0xD5100000 | (uint)r << 5 | (uint)rt);
    public void DcCvau(int rt) => Emit(0xD50B7B20 | (uint)rt);
    public void IcIvau(int rt) => Emit(0xD50B7520 | (uint)rt);

    // ------------------------------------------------------------------ integer data processing

    static uint Sf(bool x) => x ? 0x80000000u : 0;

    static void CheckReg(int r) { if ((uint)r > 31) throw new ArgumentOutOfRangeException(nameof(r)); }

    /// <summary>ADD/SUB (immediate); imm 0..4095, or a multiple of 4096 up to 4095 &lt;&lt; 12.</summary>
    public static bool IsAddImm(uint imm) => imm < 4096 || ((imm & 0xFFF) == 0 && imm >> 12 < 4096);

    void AddSubImm(uint op, int rd, int rn, uint imm, bool x)
    {
        if (!IsAddImm(imm)) throw new ArgumentOutOfRangeException(nameof(imm));
        uint sh = imm < 4096 ? 0u : 1u;
        uint v = sh != 0 ? imm >> 12 : imm;
        Emit(op | Sf(x) | sh << 22 | v << 10 | (uint)rn << 5 | (uint)rd);
    }

    public void AddImm(int rd, int rn, uint imm, bool x = false) => AddSubImm(0x11000000, rd, rn, imm, x);
    public void AddsImm(int rd, int rn, uint imm, bool x = false) => AddSubImm(0x31000000, rd, rn, imm, x);
    public void SubImm(int rd, int rn, uint imm, bool x = false) => AddSubImm(0x51000000, rd, rn, imm, x);
    public void SubsImm(int rd, int rn, uint imm, bool x = false) => AddSubImm(0x71000000, rd, rn, imm, x);
    public void CmpImm(int rn, uint imm, bool x = false) => SubsImm(ZR, rn, imm, x);
    public void CmnImm(int rn, uint imm, bool x = false) => AddsImm(ZR, rn, imm, x);
    /// <summary>MOV to/from SP (ADD #0).</summary>
    public void MovSp(int rd, int rn) => AddImm(rd, rn, 0, true);

    public enum Shift : uint { Lsl, Lsr, Asr, Ror }

    void ShiftedReg(uint op, int rd, int rn, int rm, Shift sh, int amount, bool x)
    {
        Emit(op | Sf(x) | (uint)sh << 22 | (uint)rm << 16 | (uint)amount << 10 | (uint)rn << 5 | (uint)rd);
    }

    public void Add(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x0B000000, rd, rn, rm, sh, amount, x);
    public void Adds(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x2B000000, rd, rn, rm, sh, amount, x);
    public void Sub(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x4B000000, rd, rn, rm, sh, amount, x);
    public void Subs(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x6B000000, rd, rn, rm, sh, amount, x);
    public void Cmp(int rn, int rm, bool x = false) => Subs(ZR, rn, rm, x: x);
    public void Neg(int rd, int rm) => Sub(rd, ZR, rm);
    public void And(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x0A000000, rd, rn, rm, sh, amount, x);
    public void Bic(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x0A200000, rd, rn, rm, sh, amount, x);
    public void Orr(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x2A000000, rd, rn, rm, sh, amount, x);
    public void Orn(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x2A200000, rd, rn, rm, sh, amount, x);
    public void Eor(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x4A000000, rd, rn, rm, sh, amount, x);
    public void Ands(int rd, int rn, int rm, Shift sh = Shift.Lsl, int amount = 0, bool x = false) => ShiftedReg(0x6A000000, rd, rn, rm, sh, amount, x);
    public void Tst(int rn, int rm, bool x = false) => Ands(ZR, rn, rm, x: x);
    public void Mov(int rd, int rm, bool x = false) => Orr(rd, ZR, rm, x: x);
    public void Mvn(int rd, int rm) => Orn(rd, ZR, rm);

    public enum Extend : uint { Uxtb, Uxth, Uxtw, Uxtx, Sxtb, Sxth, Sxtw, Sxtx }

    /// <summary>ADD (extended register), 64-bit: Xd = Xn + extend(Rm) &lt;&lt; amount (Xn/Xd may be SP).</summary>
    public void AddExt(int rd, int rn, int rm, Extend ext, int amount = 0) =>
        Emit(0x8B200000 | (uint)rm << 16 | (uint)ext << 13 | (uint)amount << 10 | (uint)rn << 5 | (uint)rd);

    public void Adc(int rd, int rn, int rm) => Emit(0x1A000000 | (uint)rm << 16 | (uint)rn << 5 | (uint)rd);
    public void Adcs(int rd, int rn, int rm) => Emit(0x3A000000 | (uint)rm << 16 | (uint)rn << 5 | (uint)rd);
    public void Sbc(int rd, int rn, int rm) => Emit(0x5A000000 | (uint)rm << 16 | (uint)rn << 5 | (uint)rd);
    public void Sbcs(int rd, int rn, int rm) => Emit(0x7A000000 | (uint)rm << 16 | (uint)rn << 5 | (uint)rd);

    // logical immediates

    /// <summary>Encodes a bitmask immediate (N:immr:imms) for a 32- or 64-bit logical instruction.</summary>
    public static bool TryLogicalImm(ulong value, bool x, out uint enc)
    {
        enc = 0;
        if (!x) value = (value & 0xFFFFFFFF) | (value << 32);
        if (value == 0 || value == ulong.MaxValue) return false;
        // smallest element size whose repetition gives the value
        int size = 64;
        while (size > 2)
        {
            int half = size / 2;
            ulong mask = (1UL << half) - 1;
            if ((value & mask) != ((value >> half) & mask)) break;
            size = half;
        }
        ulong emask = size == 64 ? ulong.MaxValue : (1UL << size) - 1;
        ulong elem = value & emask;
        // the element must be a rotated run of ones
        int ones = System.Numerics.BitOperations.PopCount(elem);
        for (int rot = 0; rot < size; rot++)
        {
            // rotate right by rot within the element: a run starting at bit 0?
            ulong r = ((elem >> rot) | (elem << (size - rot))) & emask;
            if (r == ((ones == 64 ? ulong.MaxValue : (1UL << ones) - 1)))
            {
                uint immr = (uint)((size - rot) % size);
                uint imms = (uint)((~(size - 1) << 1) & 0x3F) | (uint)(ones - 1);
                uint n = size == 64 ? 1u : 0u;
                if (size == 64) imms = (uint)(ones - 1);
                enc = n << 12 | immr << 6 | (imms & 0x3F);
                return true;
            }
        }
        return false;
    }

    void LogicalImm(uint op, int rd, int rn, ulong imm, bool x)
    {
        if (!TryLogicalImm(imm, x, out uint enc)) throw new ArgumentOutOfRangeException(nameof(imm));
        Emit(op | Sf(x) | enc << 10 | (uint)rn << 5 | (uint)rd);
    }

    public void AndImm(int rd, int rn, ulong imm, bool x = false) => LogicalImm(0x12000000, rd, rn, imm, x);
    public void OrrImm(int rd, int rn, ulong imm, bool x = false) => LogicalImm(0x32000000, rd, rn, imm, x);
    public void EorImm(int rd, int rn, ulong imm, bool x = false) => LogicalImm(0x52000000, rd, rn, imm, x);
    public void AndsImm(int rd, int rn, ulong imm, bool x = false) => LogicalImm(0x72000000, rd, rn, imm, x);
    public void TstImm(int rn, ulong imm, bool x = false) => AndsImm(ZR, rn, imm, x);

    // moves

    public void Movz(int rd, uint imm16, int shift = 0, bool x = false) => Emit(0x52800000 | Sf(x) | (uint)(shift / 16) << 21 | (imm16 & 0xFFFF) << 5 | (uint)rd);
    public void Movn(int rd, uint imm16, int shift = 0, bool x = false) => Emit(0x12800000 | Sf(x) | (uint)(shift / 16) << 21 | (imm16 & 0xFFFF) << 5 | (uint)rd);
    public void Movk(int rd, uint imm16, int shift = 0, bool x = false) => Emit(0x72800000 | Sf(x) | (uint)(shift / 16) << 21 | (imm16 & 0xFFFF) << 5 | (uint)rd);

    /// <summary>Wd = imm (one or two instructions).</summary>
    public void MovImm(int rd, uint v)
    {
        if ((v & 0xFFFF0000) == 0) Movz(rd, v);
        else if ((v & 0xFFFF) == 0) Movz(rd, v >> 16, 16);
        else if ((~v & 0xFFFF0000) == 0) Movn(rd, ~v & 0xFFFF);
        else if ((~v & 0xFFFF) == 0) Movn(rd, (~v >> 16) & 0xFFFF, 16);
        else if (TryLogicalImm(v, false, out _)) OrrImm(rd, ZR, v);
        else { Movz(rd, v & 0xFFFF); Movk(rd, v >> 16, 16); }
    }

    /// <summary>Xd = imm (up to four instructions; addresses usually take three).</summary>
    public void MovImm64(int rd, ulong v)
    {
        if (v >> 32 == 0) { MovImm(rd, (uint)v); return; }
        if (TryLogicalImm(v, true, out _)) { OrrImm(rd, ZR, v, true); return; }
        bool first = true;
        for (int sh = 0; sh < 64; sh += 16)
        {
            uint part = (uint)(v >> sh) & 0xFFFF;
            if (part == 0) continue;
            if (first) { Movz(rd, part, sh, true); first = false; } else Movk(rd, part, sh, true);
        }
    }

    /// <summary>Xd = 64-bit literal (LDR literal from the pool).</summary>
    public void LdrLit(int rt, ulong v) { _litFixups.Add((_code.Count, Literal(v, 0, false))); Emit(0x58000000 | (uint)rt); }
    /// <summary>Wt = 32-bit literal (the low word of a 64-bit pool entry, shared with <see cref="LdrLit"/> entries of the same value).</summary>
    public void LdrLitW(int rt, uint v) { _litFixups.Add((_code.Count, Literal(v, 0, false))); Emit(0x18000000 | (uint)rt); }

    /// <summary>Whether <see cref="MovImm"/> needs two instructions for <paramref name="v"/> (MOVZ + MOVK).</summary>
    public static bool MovImmIsLong(uint v) =>
        (v & 0xFFFF0000) != 0 && (v & 0xFFFF) != 0 && (~v & 0xFFFF0000) != 0 && (~v & 0xFFFF) != 0 && !TryLogicalImm(v, false, out _);
    /// <summary>Qt = 128-bit literal.</summary>
    public void LdrLitQ(int vt, ulong lo, ulong hi) { _litFixups.Add((_code.Count, Literal(lo, hi, true))); Emit(0x9C000000 | (uint)vt); }

    // bitfield / shifts by immediate

    public void Ubfm(int rd, int rn, int immr, int imms, bool x = false) => Emit((x ? 0xD3400000u : 0x53000000u) | (uint)immr << 16 | (uint)imms << 10 | (uint)rn << 5 | (uint)rd);
    public void Sbfm(int rd, int rn, int immr, int imms, bool x = false) => Emit((x ? 0x93400000u : 0x13000000u) | (uint)immr << 16 | (uint)imms << 10 | (uint)rn << 5 | (uint)rd);
    public void Bfm(int rd, int rn, int immr, int imms, bool x = false) => Emit((x ? 0xB3400000u : 0x33000000u) | (uint)immr << 16 | (uint)imms << 10 | (uint)rn << 5 | (uint)rd);
    public void LslImm(int rd, int rn, int s, bool x = false) { int w = x ? 64 : 32; Ubfm(rd, rn, (w - s) % w, w - 1 - s, x); }
    public void LsrImm(int rd, int rn, int s, bool x = false) => Ubfm(rd, rn, s, x ? 63 : 31, x);
    public void AsrImm(int rd, int rn, int s, bool x = false) => Sbfm(rd, rn, s, x ? 63 : 31, x);
    public void RorImm(int rd, int rn, int s) => Emit(0x13800000 | (uint)rn << 16 | (uint)s << 10 | (uint)rn << 5 | (uint)rd);
    public void Ubfx(int rd, int rn, int lsb, int width, bool x = false) => Ubfm(rd, rn, lsb, lsb + width - 1, x);
    public void Sbfx(int rd, int rn, int lsb, int width, bool x = false) => Sbfm(rd, rn, lsb, lsb + width - 1, x);
    public void Bfi(int rd, int rn, int lsb, int width) => Bfm(rd, rn, (32 - lsb) % 32, width - 1);
    public void Bfxil(int rd, int rn, int lsb, int width) => Bfm(rd, rn, lsb, lsb + width - 1);
    public void Uxtb(int rd, int rn) => Ubfm(rd, rn, 0, 7);
    public void Uxth(int rd, int rn) => Ubfm(rd, rn, 0, 15);
    public void Sxtb(int rd, int rn) => Sbfm(rd, rn, 0, 7);
    public void Sxth(int rd, int rn) => Sbfm(rd, rn, 0, 15);
    /// <summary>Xd = sign-extended Wn.</summary>
    public void Sxtw(int rd, int rn) => Sbfm(rd, rn, 0, 31, true);

    // register shifts, misc

    void DataProc2(uint op, int rd, int rn, int rm, bool x) => Emit(op | Sf(x) | (uint)rm << 16 | (uint)rn << 5 | (uint)rd);
    public void Lslv(int rd, int rn, int rm, bool x = false) => DataProc2(0x1AC02000, rd, rn, rm, x);
    public void Lsrv(int rd, int rn, int rm, bool x = false) => DataProc2(0x1AC02400, rd, rn, rm, x);
    public void Asrv(int rd, int rn, int rm, bool x = false) => DataProc2(0x1AC02800, rd, rn, rm, x);
    public void Rorv(int rd, int rn, int rm, bool x = false) => DataProc2(0x1AC02C00, rd, rn, rm, x);
    public void Udiv(int rd, int rn, int rm) => DataProc2(0x1AC00800, rd, rn, rm, false);
    public void Sdiv(int rd, int rn, int rm) => DataProc2(0x1AC00C00, rd, rn, rm, false);
    public void Clz(int rd, int rn) => Emit(0x5AC01000 | (uint)rn << 5 | (uint)rd);
    public void Rbit(int rd, int rn) => Emit(0x5AC00000 | (uint)rn << 5 | (uint)rd);
    public void Rev16(int rd, int rn) => Emit(0x5AC00400 | (uint)rn << 5 | (uint)rd);
    public void Rev(int rd, int rn) => Emit(0x5AC00800 | (uint)rn << 5 | (uint)rd);

    public void Madd(int rd, int rn, int rm, int ra) => Emit(0x1B000000 | (uint)rm << 16 | (uint)ra << 10 | (uint)rn << 5 | (uint)rd);
    public void Msub(int rd, int rn, int rm, int ra) => Emit(0x1B008000 | (uint)rm << 16 | (uint)ra << 10 | (uint)rn << 5 | (uint)rd);
    public void Mul(int rd, int rn, int rm) => Madd(rd, rn, rm, ZR);
    /// <summary>Xd = Wn * Wm (unsigned / signed 64-bit product).</summary>
    public void Umull(int rd, int rn, int rm) => Emit(0x9BA00000 | (uint)rm << 16 | 31u << 10 | (uint)rn << 5 | (uint)rd);
    public void Smull(int rd, int rn, int rm) => Emit(0x9B200000 | (uint)rm << 16 | 31u << 10 | (uint)rn << 5 | (uint)rd);

    public void Csel(int rd, int rn, int rm, Cond c, bool x = false) => Emit(0x1A800000 | Sf(x) | (uint)rm << 16 | (uint)c << 12 | (uint)rn << 5 | (uint)rd);
    public void Csinc(int rd, int rn, int rm, Cond c) => Emit(0x1A800400 | (uint)rm << 16 | (uint)c << 12 | (uint)rn << 5 | (uint)rd);
    public void Cset(int rd, Cond c) => Csinc(rd, ZR, ZR, Invert(c));

    // ------------------------------------------------------------------ loads and stores

    /// <summary>Access size of a load/store: 0 = byte, 1 = half, 2 = word, 3 = doubleword (4 = 128-bit for SIMD).</summary>
    public enum Ls { B, H, W, X, Q }

    public enum LsKind
    {
        Strb, Ldrb, Ldrsb, Strh, Ldrh, Ldrsh, StrW, LdrW, StrX, LdrX,
        StrS, LdrS, StrD, LdrD, StrQ, LdrQ, StrH, LdrH, StrB, LdrB,
    }

    static (uint Op, int Scale) LsInfo(LsKind k) => k switch
    {
        LsKind.Strb => (0x39000000, 0), LsKind.Ldrb => (0x39400000, 0), LsKind.Ldrsb => (0x39C00000, 0),
        LsKind.Strh => (0x79000000, 1), LsKind.Ldrh => (0x79400000, 1), LsKind.Ldrsh => (0x79C00000, 1),
        LsKind.StrW => (0xB9000000, 2), LsKind.LdrW => (0xB9400000, 2),
        LsKind.StrX => (0xF9000000, 3), LsKind.LdrX => (0xF9400000, 3),
        LsKind.StrS => (0xBD000000, 2), LsKind.LdrS => (0xBD400000, 2),
        LsKind.StrD => (0xFD000000, 3), LsKind.LdrD => (0xFD400000, 3),
        LsKind.StrQ => (0x3D800000, 4), LsKind.LdrQ => (0x3DC00000, 4),
        LsKind.StrH => (0x7D000000, 1), LsKind.LdrH => (0x7D400000, 1),
        LsKind.StrB => (0x3D000000, 0), LsKind.LdrB => (0x3D400000, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    /// <summary>Whether [Xn, #off] is encodable directly (scaled unsigned or unscaled signed 9-bit offset).</summary>
    public static bool FitsOffset(LsKind k, int off)
    {
        int scale = LsInfo(k).Scale;
        return (off >= 0 && (off & ((1 << scale) - 1)) == 0 && off >> scale < 4096) || (off >= -256 && off < 256);
    }

    /// <summary>Load/store with an immediate offset: [Xn, #off] (scaled, or LDUR/STUR when unaligned or negative).</summary>
    public void LdSt(LsKind k, int rt, int rn, int off)
    {
        var (op, scale) = LsInfo(k);
        if (off >= 0 && (off & ((1 << scale) - 1)) == 0 && off >> scale < 4096)
            Emit(op | (uint)(off >> scale) << 10 | (uint)rn << 5 | (uint)rt);
        else if (off >= -256 && off < 256)
            Emit((op & ~0x01000000u) | ((uint)off & 0x1FF) << 12 | (uint)rn << 5 | (uint)rt); // unscaled form: bit 24 clear
        else throw new ArgumentOutOfRangeException(nameof(off));
    }

    /// <summary>Load/store with a register offset: [Xn, Wm, UXTW] (or [Xn, Xm] when <paramref name="xIndex"/>), optionally scaled.</summary>
    public void LdStReg(LsKind k, int rt, int rn, int rm, bool xIndex = false, bool scaled = false)
    {
        var (op, _) = LsInfo(k);
        uint baseOp = (op & ~0x01000000u) | 0x00200800u; // register-offset form
        uint option = xIndex ? 3u : 2u;
        Emit(baseOp | (uint)rm << 16 | option << 13 | (scaled ? 1u << 12 : 0) | (uint)rn << 5 | (uint)rt);
    }

    public void Ldr(int rt, int rn, int off) => LdSt(LsKind.LdrW, rt, rn, off);
    public void Str(int rt, int rn, int off) => LdSt(LsKind.StrW, rt, rn, off);
    public void LdrX(int rt, int rn, int off) => LdSt(LsKind.LdrX, rt, rn, off);
    public void StrX(int rt, int rn, int off) => LdSt(LsKind.StrX, rt, rn, off);

    /// <summary>STP/LDP of X registers: signed offset, pre-index (writeback before) or post-index.</summary>
    public enum PairMode : uint { Post = 1, Offset = 2, Pre = 3 }
    public void Stp(int rt, int rt2, int rn, int off, PairMode m = PairMode.Offset) => Emit(0xA8000000 | (uint)m << 23 | (((uint)(off / 8)) & 0x7F) << 15 | (uint)rt2 << 10 | (uint)rn << 5 | (uint)rt);
    public void Ldp(int rt, int rt2, int rn, int off, PairMode m = PairMode.Offset) => Emit(0xA8400000 | (uint)m << 23 | (((uint)(off / 8)) & 0x7F) << 15 | (uint)rt2 << 10 | (uint)rn << 5 | (uint)rt);
    /// <summary>Whether a 32-bit pair access can use <paramref name="off"/> (signed offset, multiple of 4 in -256..252).</summary>
    public static bool FitsPairW(int off) => off % 4 == 0 && off >= -256 && off <= 252;
    /// <summary>STP Wt, Wt2, [Xn, #off] (signed offset).</summary>
    public void StpW(int rt, int rt2, int rn, int off) { if (!FitsPairW(off)) throw new ArgumentOutOfRangeException(nameof(off)); Emit(0x29000000 | (((uint)(off / 4)) & 0x7F) << 15 | (uint)rt2 << 10 | (uint)rn << 5 | (uint)rt); }
    /// <summary>LDP Wt, Wt2, [Xn, #off] (signed offset; Wt != Wt2).</summary>
    public void LdpW(int rt, int rt2, int rn, int off)
    {
        if (!FitsPairW(off) || rt == rt2) throw new ArgumentOutOfRangeException(nameof(off));
        Emit(0x29400000 | (((uint)(off / 4)) & 0x7F) << 15 | (uint)rt2 << 10 | (uint)rn << 5 | (uint)rt);
    }

    // exclusives / acquire-release
    public void Ldaxr(int rt, int rn, bool x = false) => Emit((x ? 0xC85FFC00u : 0x885FFC00u) | (uint)rn << 5 | (uint)rt);
    public void Stlxr(int rs, int rt, int rn, bool x = false) => Emit((x ? 0xC800FC00u : 0x8800FC00u) | (uint)rs << 16 | (uint)rn << 5 | (uint)rt);
    public void Ldar(int rt, int rn, bool x = false) => Emit((x ? 0xC8DFFC00u : 0x88DFFC00u) | (uint)rn << 5 | (uint)rt);
    public void Stlr(int rt, int rn, bool x = false) => Emit((x ? 0xC89FFC00u : 0x889FFC00u) | (uint)rn << 5 | (uint)rt);

    // ------------------------------------------------------------------ scalar floating point (s = single, d = double)

    static uint Ty(bool dbl) => dbl ? 0x00400000u : 0;
    void Fp2(uint op, int rd, int rn, int rm, bool dbl) => Emit(op | Ty(dbl) | (uint)rm << 16 | (uint)rn << 5 | (uint)rd);
    public void Fadd(int rd, int rn, int rm, bool dbl = false) => Fp2(0x1E202800, rd, rn, rm, dbl);
    public void Fsub(int rd, int rn, int rm, bool dbl = false) => Fp2(0x1E203800, rd, rn, rm, dbl);
    public void Fmul(int rd, int rn, int rm, bool dbl = false) => Fp2(0x1E200800, rd, rn, rm, dbl);
    public void Fdiv(int rd, int rn, int rm, bool dbl = false) => Fp2(0x1E201800, rd, rn, rm, dbl);
    public void Fnmul(int rd, int rn, int rm, bool dbl = false) => Fp2(0x1E208800, rd, rn, rm, dbl);
    void Fp1(uint op, int rd, int rn, bool dbl) => Emit(op | Ty(dbl) | (uint)rn << 5 | (uint)rd);
    public void Fsqrt(int rd, int rn, bool dbl = false) => Fp1(0x1E21C000, rd, rn, dbl);
    public void Fneg(int rd, int rn, bool dbl = false) => Fp1(0x1E214000, rd, rn, dbl);
    public void Fabs(int rd, int rn, bool dbl = false) => Fp1(0x1E20C000, rd, rn, dbl);
    public void Fmov(int rd, int rn, bool dbl = false) => Fp1(0x1E204000, rd, rn, dbl);
    public void Fcmp(int rn, int rm, bool dbl = false) => Emit(0x1E202000 | Ty(dbl) | (uint)rm << 16 | (uint)rn << 5);
    public void FcmpZero(int rn, bool dbl = false) => Emit(0x1E202008 | Ty(dbl) | (uint)rn << 5);
    public void FcvtSD(int dd, int sn) => Emit(0x1E22C000 | (uint)sn << 5 | (uint)dd);   // single -> double
    public void FcvtDS(int sd, int dn) => Emit(0x1E624000 | (uint)dn << 5 | (uint)sd);   // double -> single
    public void FcvtSH(int hd, int sn) => Emit(0x1E23C000 | (uint)sn << 5 | (uint)hd);   // single -> half
    public void FcvtHS(int sd, int hn) => Emit(0x1EE24000 | (uint)hn << 5 | (uint)sd);   // half -> single
    public void Scvtf(int fd, int wn, bool dbl = false) => Emit(0x1E220000 | Ty(dbl) | (uint)wn << 5 | (uint)fd);
    public void Ucvtf(int fd, int wn, bool dbl = false) => Emit(0x1E230000 | Ty(dbl) | (uint)wn << 5 | (uint)fd);
    public void Fcvtzs(int wd, int fn, bool dbl = false) => Emit(0x1E380000 | Ty(dbl) | (uint)fn << 5 | (uint)wd);
    public void Fcvtzu(int wd, int fn, bool dbl = false) => Emit(0x1E390000 | Ty(dbl) | (uint)fn << 5 | (uint)wd);
    public void Fcvtns(int wd, int fn, bool dbl = false) => Emit(0x1E200000 | Ty(dbl) | (uint)fn << 5 | (uint)wd);
    public void Fcvtnu(int wd, int fn, bool dbl = false) => Emit(0x1E210000 | Ty(dbl) | (uint)fn << 5 | (uint)wd);
    /// <summary>Wd = bits of Sn / Sd = bits of Wn.</summary>
    public void FmovToGp(int wd, int sn) => Emit(0x1E260000 | (uint)sn << 5 | (uint)wd);
    public void FmovFromGp(int sd, int wn) => Emit(0x1E270000 | (uint)wn << 5 | (uint)sd);
    public void FmovToGpX(int xd, int dn) => Emit(0x9E660000 | (uint)dn << 5 | (uint)xd);
    public void FmovFromGpX(int dd, int xn) => Emit(0x9E670000 | (uint)xn << 5 | (uint)dd);

    // ------------------------------------------------------------------ Advanced SIMD (vector)

    static uint Qb(bool q) => q ? 0x40000000u : 0;

    /// <summary>Three registers, same type: opcode = bits 15:11, U = bit 29, size = bits 23:22.</summary>
    public void V3(uint opcode, bool u, uint size, bool q, int rd, int rn, int rm) =>
        Emit(0x0E200400 | Qb(q) | (u ? 1u << 29 : 0) | size << 22 | (uint)rm << 16 | opcode << 11 | (uint)rn << 5 | (uint)rd);

    /// <summary>Two-register miscellaneous: opcode = bits 16:12.</summary>
    public void V2(uint opcode, bool u, uint size, bool q, int rd, int rn) =>
        Emit(0x0E200800 | Qb(q) | (u ? 1u << 29 : 0) | size << 22 | opcode << 12 | (uint)rn << 5 | (uint)rd);

    /// <summary>Three registers, different types (SMULL/UMULL/PMULL/SMLAL...): opcode = bits 15:12, size = the narrow lanes.</summary>
    public void V3Diff(uint opcode, bool u, uint size, bool q, int rd, int rn, int rm) =>
        Emit(0x0E200000 | Qb(q) | (u ? 1u << 29 : 0) | size << 22 | (uint)rm << 16 | opcode << 12 | (uint)rn << 5 | (uint)rd);

    /// <summary>Permute (UZP1/TRN1/ZIP1 = 1/2/3, UZP2/TRN2/ZIP2 = 5/6/7).</summary>
    public void VPerm(uint opcode, uint size, bool q, int rd, int rn, int rm) =>
        Emit(0x0E000800 | Qb(q) | size << 22 | (uint)rm << 16 | opcode << 12 | (uint)rn << 5 | (uint)rd);

    /// <summary>Shift by immediate: immh:immb = bits 22:16 (see <see cref="ShrImmField"/> / <see cref="ShlImmField"/>), opcode = bits 15:11.</summary>
    public void VShiftImm(uint opcode, bool u, bool q, uint immhb, int rd, int rn) =>
        Emit(0x0F000400 | Qb(q) | (u ? 1u << 29 : 0) | immhb << 16 | opcode << 11 | (uint)rn << 5 | (uint)rd);

    /// <summary>Scalar shift by immediate (64-bit lanes of D registers).</summary>
    public void SShiftImm(uint opcode, bool u, uint immhb, int rd, int rn) =>
        Emit(0x5F000400 | (u ? 1u << 29 : 0) | immhb << 16 | opcode << 11 | (uint)rn << 5 | (uint)rd);

    /// <summary>immh:immb of a right shift by <paramref name="shift"/> (1..esize) for lanes of 8 &lt;&lt; size bits.</summary>
    public static uint ShrImmField(uint size, int shift) => (uint)((16 << (int)size) - shift);
    /// <summary>immh:immb of a left shift by <paramref name="shift"/> (0..esize-1).</summary>
    public static uint ShlImmField(uint size, int shift) => (uint)((8 << (int)size) + shift);

    public void Ext(bool q, int rd, int rn, int rm, int index) => Emit(0x2E000000 | Qb(q) | (uint)rm << 16 | (uint)index << 11 | (uint)rn << 5 | (uint)rd);
    /// <summary>TBL/TBX with <paramref name="regs"/> (1-4) consecutive table registers starting at Vn.</summary>
    public void Tbl(bool q, int rd, int rn, int regs, int rm, bool tbx = false) =>
        Emit(0x0E000000 | Qb(q) | (uint)rm << 16 | (uint)(regs - 1) << 13 | (tbx ? 1u << 12 : 0) | (uint)rn << 5 | (uint)rd);
    /// <summary>DUP Vd.T, Vn.Ts[index] (lane size = 8 &lt;&lt; size bits).</summary>
    public void DupElem(uint size, bool q, int rd, int rn, int index)
    {
        uint imm5 = (1u << (int)size) | ((uint)index << ((int)size + 1));
        Emit(0x0E000400 | Qb(q) | imm5 << 16 | (uint)rn << 5 | (uint)rd);
    }
    /// <summary>DUP Vd.T, Wn.</summary>
    public void DupGp(uint size, bool q, int rd, int rn) => Emit(0x0E000C00 | Qb(q) | (1u << (int)size) << 16 | (uint)rn << 5 | (uint)rd);
    /// <summary>INS Vd.Ts[index], Wn/Xn.</summary>
    public void InsGp(uint size, int rd, int index, int rn)
    {
        uint imm5 = (1u << (int)size) | ((uint)index << ((int)size + 1));
        Emit(0x4E001C00 | imm5 << 16 | (uint)rn << 5 | (uint)rd);
    }
    /// <summary>UMOV Wd/Xd, Vn.Ts[index].</summary>
    public void Umov(uint size, int rd, int rn, int index)
    {
        uint imm5 = (1u << (int)size) | ((uint)index << ((int)size + 1));
        Emit((size == 3 ? 0x4E003C00u : 0x0E003C00u) | imm5 << 16 | (uint)rn << 5 | (uint)rd);
    }
    /// <summary>MOV Vd.16B, Vn.16B (ORR).</summary>
    public void VMov(int rd, int rn) => V3(3, false, 2, true, rd, rn, rn);
    /// <summary>MOVI Vd.2D, #0 (all zero).</summary>
    public void VZero(int rd) => Emit(0x6F00E400 | (uint)rd);
    /// <summary>MOVI Vd.2D with an 8-bit byte mask (each bit of abcdefgh selects 0x00 or 0xFF per byte).</summary>
    public void MoviMask(int rd, uint abcdefgh, bool q = true) =>
        Emit(0x2F00E400 | Qb(q) | (abcdefgh >> 5 & 7) << 16 | (abcdefgh & 31) << 5 | (uint)rd);
    /// <summary>MOVI Vd.8B / Vd.16B, #imm8 (every byte).</summary>
    public void MoviB(int rd, uint imm8, bool q) =>
        Emit(0x0F00E400 | Qb(q) | (imm8 >> 5 & 7) << 16 | (imm8 & 31) << 5 | (uint)rd);
    /// <summary>INS Vd.D[1], Vn.D[0].</summary>
    public void InsD1(int rd, int rn) => Emit(0x6E180400 | (uint)rn << 5 | (uint)rd);
    /// <summary>LD1 {Vt.16B - Vt+n-1.16B}, [Xn] (n = 1-4).</summary>
    public void Ld1(int vt, int regs, int rn)
    {
        uint opcode = regs switch { 1 => 0x7u, 2 => 0xAu, 3 => 0x6u, _ => 0x2u };
        Emit(0x4C400000 | opcode << 12 | (uint)rn << 5 | (uint)vt);
    }
}
