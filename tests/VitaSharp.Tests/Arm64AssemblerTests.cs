using VitaSharp.Core.Cpu.Native;
using static VitaSharp.Core.Cpu.Native.Arm64Assembler;
using A = VitaSharp.Core.Cpu.Native.Arm64Assembler;

namespace VitaSharp.Tests;

/// <summary>
/// Encodings of the ARM64 backend's assembler against GNU as. The expected words were produced by
/// aarch64-linux-gnu-as 2.40 (binutils-aarch64-linux-gnu, Debian bookworm) from the assembly text next to each case
/// and checked back with aarch64-linux-gnu-objdump -d; branch cases jump over one instruction, the literal case over the
/// pool padding.
/// To regenerate: feed the strings to `aarch64-linux-gnu-as` one per line and read the words with objdump.
/// </summary>
public sealed class Arm64AssemblerTests
{
    static readonly (uint Expected, string Asm, Action<A> Emit)[] Cases =
    [
        // ---- add/sub
        (0x11000420, "add w0, w1, #1", a => a.AddImm(0, 1, 1)),
        (0x113FFFFF, "add wsp, wsp, #4095", a => a.AddImm(31, 31, 4095)),
        (0x11400C41, "add w1, w2, #3, lsl #12", a => a.AddImm(1, 2, 3 << 12)),
        (0x91001273, "add x19, x19, #4", a => a.AddImm(19, 19, 4, true)),
        (0x51000420, "sub w0, w1, #1", a => a.SubImm(0, 1, 1)),
        (0x31001C40, "adds w0, w2, #7", a => a.AddsImm(0, 2, 7)),
        (0x7100803F, "cmp w1, #32", a => a.CmpImm(1, 32)),
        (0x3100047F, "cmn w3, #1", a => a.CmnImm(3, 1)),
        (0xD10843FF, "sub sp, sp, #528", a => a.SubImm(SP, SP, 528, true)),
        (0x910003FD, "mov x29, sp", a => a.MovSp(29, SP)),
        (0xD1440291, "sub x17, x20, #256, lsl #12", a => a.SubImm(17, 20, 0x100000, true)),
        (0x0B020020, "add w0, w1, w2", a => a.Add(0, 1, 2)),
        (0x8B021063, "add x3, x3, x2, lsl #4", a => a.Add(3, 3, 2, Shift.Lsl, 4, true)),
        (0x4B1F0020, "sub w0, w1, wzr", a => a.Sub(0, 1, ZR)),
        (0x2B0300A4, "adds w4, w5, w3", a => a.Adds(4, 5, 3)),
        (0x6B11021F, "cmp w16, w17", a => a.Cmp(16, 17)),
        (0xEB11021F, "cmp x16, x17", a => a.Cmp(16, 17, true)),
        (0x4B0203E1, "neg w1, w2", a => a.Neg(1, 2)),
        (0x8B214A82, "add x2, x20, w1, uxtw #2", a => a.AddExt(2, 20, 1, Extend.Uxtw, 2)),
        (0x8B214282, "add x2, x20, w1, uxtw", a => a.AddExt(2, 20, 1, Extend.Uxtw)),
        (0x1A020020, "adc w0, w1, w2", a => a.Adc(0, 1, 2)),
        (0x3A020020, "adcs w0, w1, w2", a => a.Adcs(0, 1, 2)),
        (0x5A020020, "sbc w0, w1, w2", a => a.Sbc(0, 1, 2)),
        (0x7A020020, "sbcs w0, w1, w2", a => a.Sbcs(0, 1, 2)),
        (0x6B1F03FF, "cmp wzr, wzr", a => a.Cmp(ZR, ZR)),
        (0x2B1F03FF, "cmn wzr, wzr", a => a.Adds(ZR, ZR, ZR)),
        // ---- logical
        (0x0A020020, "and w0, w1, w2", a => a.And(0, 1, 2)),
        (0x0A220020, "bic w0, w1, w2", a => a.Bic(0, 1, 2)),
        (0x2A020020, "orr w0, w1, w2", a => a.Orr(0, 1, 2)),
        (0x2A227C20, "orn w0, w1, w2, lsl #31", a => a.Orn(0, 1, 2, Shift.Lsl, 31)),
        (0x4A020020, "eor w0, w1, w2", a => a.Eor(0, 1, 2)),
        (0x6A01003F, "tst w1, w1", a => a.Tst(1, 1)),
        (0x2A0103E0, "mov w0, w1", a => a.Mov(0, 1)),
        (0x2A2103E0, "mvn w0, w1", a => a.Mvn(0, 1)),
        (0xAA060CC6, "orr x6, x6, x6, lsl #3", a => a.Orr(6, 6, 6, Shift.Lsl, 3, true)),
        (0xAA0780C6, "orr x6, x6, x7, lsl #32", a => a.Orr(6, 6, 7, Shift.Lsl, 32, true)),
        (0x2A015400, "orr w0, w0, w1, lsl #21", a => a.Orr(0, 0, 1, Shift.Lsl, 21)),
        (0x8A310210, "bic x16, x16, x17", a => a.Bic(16, 16, 17, x: true)),
        (0x12000020, "and w0, w1, #0x1", a => a.AndImm(0, 1, 1)),
        (0x12006C31, "and w17, w1, #0xfffffff", a => a.AndImm(17, 1, 0x0FFFFFFF)),
        (0x12001C20, "and w0, w1, #0xff", a => a.AndImm(0, 1, 0xFF)),
        (0x121F7820, "and w0, w1, #0xfffffffe", a => a.AndImm(0, 1, 0xFFFFFFFE)),
        (0x121E7420, "and w0, w1, #0xfffffffc", a => a.AndImm(0, 1, 0xFFFFFFFC)),
        (0x32010020, "orr w0, w1, #0x80000000", a => a.OrrImm(0, 1, 0x80000000)),
        (0x52000000, "eor w0, w0, #0x1", a => a.EorImm(0, 0, 1)),
        (0x12003C20, "and w0, w1, #0xffff", a => a.AndImm(0, 1, 0xFFFF)),
        (0x1200C020, "and w0, w1, #0x1010101", a => a.AndImm(0, 1, 0x01010101)),
        (0x1201F020, "and w0, w1, #0xaaaaaaaa", a => a.AndImm(0, 1, 0xAAAAAAAA)),
        (0xB2680610, "orr x16, x16, #0x3000000", a => a.OrrImm(16, 16, 0x03000000, true)),
        (0x924004C6, "and x6, x6, #0x3", a => a.AndImm(6, 6, 3, true)),
        (0xB200C3E0, "mov x0, #0x101010101010101", a => a.OrrImm(0, ZR, 0x0101010101010101, true)),
        (0x7200083F, "tst w1, #0x7", a => a.TstImm(1, 7)),
        // ---- moves
        (0x52800540, "mov w0, #0x2a", a => a.Movz(0, 42)),
        (0x52A00060, "mov w0, #0x30000", a => a.Movz(0, 3, 16)),
        (0x12800000, "mov w0, #0xffffffff", a => a.Movn(0, 0)),
        (0x72A12340, "movk w0, #0x91a, lsl #16", a => a.Movk(0, 0x91A, 16)),
        (0xD2E00021, "mov x1, #0x1000000000000", a => a.Movz(1, 1, 48, true)),
        (0xF2C00021, "movk x1, #0x1, lsl #32", a => a.Movk(1, 1, 32, true)),
        // ---- bitfield / shifts
        (0x53040C20, "lsl w0, w1, #28", a => a.LslImm(0, 1, 28)),
        (0x531F7C20, "lsr w0, w1, #31", a => a.LsrImm(0, 1, 31)),
        (0xD360FC20, "lsr x0, x1, #32", a => a.LsrImm(0, 1, 32, true)),
        (0xD37FF8A3, "lsl x3, x5, #1", a => a.LslImm(3, 5, 1, true)),
        (0x13037C20, "asr w0, w1, #3", a => a.AsrImm(0, 1, 3)),
        (0x13811420, "ror w0, w1, #5", a => a.RorImm(0, 1, 5)),
        (0x53030C20, "ubfx w0, w1, #3, #1", a => a.Ubfx(0, 1, 3, 1)),
        (0xD3608043, "ubfx x3, x2, #32, #1", a => a.Ubfx(3, 2, 32, 1, true)),
        (0x53013C02, "ubfx w2, w0, #1, #15", a => a.Ubfx(2, 0, 1, 15)),
        (0x53033822, "ubfx w2, w1, #3, #12", a => a.Ubfx(2, 1, 3, 12)),
        (0xD3504C64, "ubfx x4, x3, #16, #4", a => a.Ubfx(4, 3, 16, 4, true)),
        (0x53001C20, "uxtb w0, w1", a => a.Uxtb(0, 1)),
        (0x53003C20, "uxth w0, w1", a => a.Uxth(0, 1)),
        (0x13001C20, "sxtb w0, w1", a => a.Sxtb(0, 1)),
        (0x13003C20, "sxth w0, w1", a => a.Sxth(0, 1)),
        (0x93407C25, "sxtw x5, w1", a => a.Sxtw(5, 1)),
        (0x331C0C20, "bfi w0, w1, #4, #4", a => a.Bfi(0, 1, 4, 4)),
        (0x33041C20, "bfxil w0, w1, #4, #4", a => a.Bfxil(0, 1, 4, 4)),
        (0x1AC22020, "lsl w0, w1, w2", a => a.Lslv(0, 1, 2)),
        (0x9AC02022, "lsl x2, x1, x0", a => a.Lslv(2, 1, 0, true)),
        (0x1AC22420, "lsr w0, w1, w2", a => a.Lsrv(0, 1, 2)),
        (0x9AC42AA2, "asr x2, x21, x4", a => a.Asrv(2, 21, 4, true)),
        (0x1AC22C20, "ror w0, w1, w2", a => a.Rorv(0, 1, 2)),
        (0x1AC20820, "udiv w0, w1, w2", a => a.Udiv(0, 1, 2)),
        (0x1AC20C20, "sdiv w0, w1, w2", a => a.Sdiv(0, 1, 2)),
        (0x5AC01020, "clz w0, w1", a => a.Clz(0, 1)),
        (0x5AC00020, "rbit w0, w1", a => a.Rbit(0, 1)),
        (0x5AC00420, "rev16 w0, w1", a => a.Rev16(0, 1)),
        (0x5AC00820, "rev w0, w1", a => a.Rev(0, 1)),
        (0x1B020C20, "madd w0, w1, w2, w3", a => a.Madd(0, 1, 2, 3)),
        (0x1B028C20, "msub w0, w1, w2, w3", a => a.Msub(0, 1, 2, 3)),
        (0x1B027C20, "mul w0, w1, w2", a => a.Mul(0, 1, 2)),
        (0x9BA27C20, "umull x0, w1, w2", a => a.Umull(0, 1, 2)),
        (0x9B227C20, "smull x0, w1, w2", a => a.Smull(0, 1, 2)),
        (0x1A821020, "csel w0, w1, w2, ne", a => a.Csel(0, 1, 2, Cond.NE)),
        (0x9A8B3020, "csel x0, x1, x11, lo", a => a.Csel(0, 1, 11, Cond.LO, true)),
        (0x1A9F17E0, "cset w0, eq", a => a.Cset(0, Cond.EQ)),
        (0x1A9F37E3, "cset w3, hs", a => a.Cset(3, Cond.HS)),
        (0x1A9F77E3, "cset w3, vs", a => a.Cset(3, Cond.VS)),
        (0x1A9F57E3, "cset w3, mi", a => a.Cset(3, Cond.MI)),
        // ---- loads/stores
        (0xB9400260, "ldr w0, [x19]", a => a.Ldr(0, 19, 0)),
        (0xB9403E61, "ldr w1, [x19, #60]", a => a.Ldr(1, 19, 60)),
        (0xB9000A61, "str w1, [x19, #8]", a => a.Str(1, 19, 8)),
        (0xB8402260, "ldur w0, [x19, #2]", a => a.Ldr(0, 19, 2)),
        (0xB81FC260, "stur w0, [x19, #-4]", a => a.Str(0, 19, -4)),
        (0xF9410270, "ldr x16, [x19, #512]", a => a.LdrX(16, 19, 512)),
        (0xF90003FF, "str xzr, [sp]", a => a.StrX(ZR, SP, 0)),
        (0xF94007F0, "ldr x16, [sp, #8]", a => a.LdrX(16, SP, 8)),
        (0xB90013E3, "str w3, [sp, #16]", a => a.Str(3, SP, 16)),
        (0x39400260, "ldrb w0, [x19]", a => a.LdSt(LsKind.Ldrb, 0, 19, 0)),
        (0x39C00660, "ldrsb w0, [x19, #1]", a => a.LdSt(LsKind.Ldrsb, 0, 19, 1)),
        (0x79400660, "ldrh w0, [x19, #2]", a => a.LdSt(LsKind.Ldrh, 0, 19, 2)),
        (0x79C00660, "ldrsh w0, [x19, #2]", a => a.LdSt(LsKind.Ldrsh, 0, 19, 2)),
        (0x39000660, "strb w0, [x19, #1]", a => a.LdSt(LsKind.Strb, 0, 19, 1)),
        (0x79000660, "strh w0, [x19, #2]", a => a.LdSt(LsKind.Strh, 0, 19, 2)),
        (0xBD406261, "ldr s1, [x19, #96]", a => a.LdSt(LsKind.LdrS, 1, 19, 96)),
        (0xBD006260, "str s0, [x19, #96]", a => a.LdSt(LsKind.StrS, 0, 19, 96)),
        (0xFD403262, "ldr d2, [x19, #96]", a => a.LdSt(LsKind.LdrD, 2, 19, 96)),
        (0xFD003260, "str d0, [x19, #96]", a => a.LdSt(LsKind.StrD, 0, 19, 96)),
        (0x3DC01A61, "ldr q1, [x19, #96]", a => a.LdSt(LsKind.LdrQ, 1, 19, 96)),
        (0x3D801A60, "str q0, [x19, #96]", a => a.LdSt(LsKind.StrQ, 0, 19, 96)),
        (0x7D40C261, "ldr h1, [x19, #96]", a => a.LdSt(LsKind.LdrH, 1, 19, 96)),
        (0x7D00C660, "str h0, [x19, #98]", a => a.LdSt(LsKind.StrH, 0, 19, 98)),
        (0x3CC63260, "ldur q0, [x19, #99]", a => a.LdSt(LsKind.LdrQ, 0, 19, 99)),
        (0xB8644A83, "ldr w3, [x20, w4, uxtw]", a => a.LdStReg(LsKind.LdrW, 3, 20, 4)),
        (0xB8244A83, "str w3, [x20, w4, uxtw]", a => a.LdStReg(LsKind.StrW, 3, 20, 4)),
        (0x38704A80, "ldrb w0, [x20, w16, uxtw]", a => a.LdStReg(LsKind.Ldrb, 0, 20, 16)),
        (0x38F04A80, "ldrsb w0, [x20, w16, uxtw]", a => a.LdStReg(LsKind.Ldrsb, 0, 20, 16)),
        (0x78704A80, "ldrh w0, [x20, w16, uxtw]", a => a.LdStReg(LsKind.Ldrh, 0, 20, 16)),
        (0x78F04A80, "ldrsh w0, [x20, w16, uxtw]", a => a.LdStReg(LsKind.Ldrsh, 0, 20, 16)),
        (0x38304A91, "strb w17, [x20, w16, uxtw]", a => a.LdStReg(LsKind.Strb, 17, 20, 16)),
        (0x78304A9F, "strh wzr, [x20, w16, uxtw]", a => a.LdStReg(LsKind.Strh, ZR, 20, 16)),
        (0xF8704A80, "ldr x0, [x20, w16, uxtw]", a => a.LdStReg(LsKind.LdrX, 0, 20, 16)),
        (0xF8304A80, "str x0, [x20, w16, uxtw]", a => a.LdStReg(LsKind.StrX, 0, 20, 16)),
        (0xB87F4A80, "ldr w0, [x20, wzr, uxtw]", a => a.LdStReg(LsKind.LdrW, 0, 20, ZR)),
        (0x38706A28, "ldrb w8, [x17, x16]", a => a.LdStReg(LsKind.Ldrb, 8, 17, 16, xIndex: true)),
        (0xA9BA7BFD, "stp x29, x30, [sp, #-96]!", a => a.Stp(29, 30, SP, -96, PairMode.Pre)),
        (0xA90153F3, "stp x19, x20, [sp, #16]", a => a.Stp(19, 20, SP, 16)),
        (0xA94573FB, "ldp x27, x28, [sp, #80]", a => a.Ldp(27, 28, SP, 80)),
        (0xA8C67BFD, "ldp x29, x30, [sp], #96", a => a.Ldp(29, 30, SP, 96, PairMode.Post)),
        (0x885FFC68, "ldaxr w8, [x3]", a => a.Ldaxr(8, 3)),
        (0xC85FFC48, "ldaxr x8, [x2]", a => a.Ldaxr(8, 2, true)),
        (0x8811FC65, "stlxr w17, w5, [x3]", a => a.Stlxr(17, 5, 3)),
        (0xC811FC46, "stlxr w17, x6, [x2]", a => a.Stlxr(17, 6, 2, true)),
        (0x88DFFC64, "ldar w4, [x3]", a => a.Ldar(4, 3)),
        (0xC8DFFCA6, "ldar x6, [x5]", a => a.Ldar(6, 5, true)),
        (0x889FFC64, "stlr w4, [x3]", a => a.Stlr(4, 3)),
        // ---- branches and system
        (0xD61F0200, "br x16", a => a.Br(16)),
        (0xD63F0200, "blr x16", a => a.Blr(16)),
        (0xD65F03C0, "ret", a => a.Ret()),
        (0xD5033BBF, "dmb ish", a => a.DmbIsh()),
        (0xD5033B9F, "dsb ish", a => a.DsbIsh()),
        (0xD5033FDF, "isb", a => a.Isb()),
        (0xD503203F, "yield", a => a.Yield()),
        (0xD5033F5F, "clrex", a => a.Clrex()),
        (0xD53B4410, "mrs x16, fpcr", a => a.Mrs(16, SysReg.Fpcr)),
        (0xD51B4410, "msr fpcr, x16", a => a.Msr(SysReg.Fpcr, 16)),
        (0xD53B4430, "mrs x16, fpsr", a => a.Mrs(16, SysReg.Fpsr)),
        (0xD51B443F, "msr fpsr, xzr", a => a.Msr(SysReg.Fpsr, ZR)),
        (0xD53B4210, "mrs x16, nzcv", a => a.Mrs(16, SysReg.Nzcv)),
        (0xD51B4200, "msr nzcv, x0", a => a.Msr(SysReg.Nzcv, 0)),
        (0xD53B0023, "mrs x3, ctr_el0", a => a.Mrs(3, SysReg.CtrEl0)),
        (0xD50B7B22, "dc cvau, x2", a => a.DcCvau(2)),
        (0xD50B7522, "ic ivau, x2", a => a.IcIvau(2)),
        (0x14000002, "b 1f; nop; 1:", a => { int l = a.NewLabel(); a.B(l); a.Emit(A.Nop); a.Bind(l); }),
        (0x54000041, "b.ne 1f; nop; 1:", a => { int l = a.NewLabel(); a.BCond(Cond.NE, l); a.Emit(A.Nop); a.Bind(l); }),
        (0x34000040, "cbz w0, 1f; nop; 1:", a => { int l = a.NewLabel(); a.Cbz(0, l); a.Emit(A.Nop); a.Bind(l); }),
        (0xB5000051, "cbnz x17, 1f; nop; 1:", a => { int l = a.NewLabel(); a.Cbnz(17, l, true); a.Emit(A.Nop); a.Bind(l); }),
        (0x36000044, "tbz w4, #0, 1f; nop; 1:", a => { int l = a.NewLabel(); a.Tbz(4, 0, l); a.Emit(A.Nop); a.Bind(l); }),
        (0x17FFFFFF, "1: nop; b 1b", a => { int l = a.NewLabel(); a.Bind(l); a.Emit(A.Nop); a.B(l); }),
        (0x58000090, "ldr x16, 1f; nop; nop; nop; 1:", a => a.LdrLit(16, 0x1122334455667788)), // pool after padding to 16 bytes
        // ---- scalar FP
        (0x1E222820, "fadd s0, s1, s2", a => a.Fadd(0, 1, 2)),
        (0x1E622820, "fadd d0, d1, d2", a => a.Fadd(0, 1, 2, true)),
        (0x1E223820, "fsub s0, s1, s2", a => a.Fsub(0, 1, 2)),
        (0x1E220820, "fmul s0, s1, s2", a => a.Fmul(0, 1, 2)),
        (0x1E621820, "fdiv d0, d1, d2", a => a.Fdiv(0, 1, 2, true)),
        (0x1E228820, "fnmul s0, s1, s2", a => a.Fnmul(0, 1, 2)),
        (0x1E21C020, "fsqrt s0, s1", a => a.Fsqrt(0, 1)),
        (0x1E614063, "fneg d3, d3", a => a.Fneg(3, 3, true)),
        (0x1E20C020, "fabs s0, s1", a => a.Fabs(0, 1)),
        (0x1E212000, "fcmp s0, s1", a => a.Fcmp(0, 1)),
        (0x1E602008, "fcmp d0, #0.0", a => a.FcmpZero(0, true)),
        (0x1E22C020, "fcvt d0, s1", a => a.FcvtSD(0, 1)),
        (0x1E624020, "fcvt s0, d1", a => a.FcvtDS(0, 1)),
        (0x1E23C020, "fcvt h0, s1", a => a.FcvtSH(0, 1)),
        (0x1EE24020, "fcvt s0, h1", a => a.FcvtHS(0, 1)),
        (0x1E220200, "scvtf s0, w16", a => a.Scvtf(0, 16)),
        (0x1E630200, "ucvtf d0, w16", a => a.Ucvtf(0, 16, true)),
        (0x1E380030, "fcvtzs w16, s1", a => a.Fcvtzs(16, 1)),
        (0x1E790030, "fcvtzu w16, d1", a => a.Fcvtzu(16, 1, true)),
        (0x1E200030, "fcvtns w16, s1", a => a.Fcvtns(16, 1)),
        (0x1E610030, "fcvtnu w16, d1", a => a.Fcvtnu(16, 1, true)),
        (0x1E260030, "fmov w16, s1", a => a.FmovToGp(16, 1)),
        (0x1E270201, "fmov s1, w16", a => a.FmovFromGp(1, 16)),
        // ---- Advanced SIMD
        (0x0E221C20, "and v0.8b, v1.8b, v2.8b", a => a.V3(3, false, 0, false, 0, 1, 2)),
        (0x4EE21C20, "orn v0.16b, v1.16b, v2.16b", a => a.V3(3, false, 3, true, 0, 1, 2)),
        (0x6E621C20, "bsl v0.16b, v1.16b, v2.16b", a => a.V3(3, true, 1, true, 0, 1, 2)),
        (0x2EE21C20, "bif v0.8b, v1.8b, v2.8b", a => a.V3(3, true, 3, false, 0, 1, 2)),
        (0x4EA11C20, "mov v0.16b, v1.16b", a => a.VMov(0, 1)),
        (0x4E628420, "add v0.8h, v1.8h, v2.8h", a => a.V3(0x10, false, 1, true, 0, 1, 2)),
        (0x6EE28420, "sub v0.2d, v1.2d, v2.2d", a => a.V3(0x10, true, 3, true, 0, 1, 2)),
        (0x5EE28420, "add d0, d1, d2", a => a.Emit(0x5E200400 | 3u << 22 | 2u << 16 | 0x10u << 11 | 1u << 5)),
        (0x0EA29C20, "mul v0.2s, v1.2s, v2.2s", a => a.V3(0x13, false, 2, false, 0, 1, 2)),
        (0x6E229420, "mls v0.16b, v1.16b, v2.16b", a => a.V3(0x12, true, 0, true, 0, 1, 2)),
        (0x6E628C20, "cmeq v0.8h, v1.8h, v2.8h", a => a.V3(0x11, true, 1, true, 0, 1, 2)),
        (0x2E223420, "cmhi v0.8b, v1.8b, v2.8b", a => a.V3(0x06, true, 0, false, 0, 1, 2)),
        (0x4EA23C20, "cmge v0.4s, v1.4s, v2.4s", a => a.V3(0x07, false, 2, true, 0, 1, 2)),
        (0x6E227C20, "uaba v0.16b, v1.16b, v2.16b", a => a.V3(0x0F, true, 0, true, 0, 1, 2)),
        (0x0E620C20, "sqadd v0.4h, v1.4h, v2.4h", a => a.V3(0x01, false, 1, false, 0, 1, 2)),
        (0x7EE22C20, "uqsub d0, d1, d2", a => a.Emit(0x5E200400 | 1u << 29 | 3u << 22 | 2u << 16 | 0x05u << 11 | 1u << 5)),
        (0x0E22BC20, "addp v0.8b, v1.8b, v2.8b", a => a.V3(0x17, false, 0, false, 0, 1, 2)),
        (0x4E62B420, "sqdmulh v0.8h, v1.8h, v2.8h", a => a.V3(0x16, false, 1, true, 0, 1, 2)),
        (0x6E62B420, "sqrdmulh v0.8h, v1.8h, v2.8h", a => a.V3(0x16, true, 1, true, 0, 1, 2)),
        (0x4EA14440, "sshl v0.4s, v2.4s, v1.4s", a => a.V3(0x08, false, 2, true, 0, 2, 1)),
        (0x4E22D420, "fadd v0.4s, v1.4s, v2.4s", a => a.V3(0x1A, false, 0, true, 0, 1, 2)),
        (0x0EA2D420, "fsub v0.2s, v1.2s, v2.2s", a => a.V3(0x1A, false, 2, false, 0, 1, 2)),
        (0x6E22DC23, "fmul v3.4s, v1.4s, v2.4s", a => a.V3(0x1B, true, 0, true, 3, 1, 2)),
        (0x4E22CC20, "fmla v0.4s, v1.4s, v2.4s", a => a.V3(0x19, false, 0, true, 0, 1, 2)),
        (0x4EA2CC20, "fmls v0.4s, v1.4s, v2.4s", a => a.V3(0x19, false, 2, true, 0, 1, 2)),
        (0x2E22D420, "faddp v0.2s, v1.2s, v2.2s", a => a.V3(0x1A, true, 0, false, 0, 1, 2)),
        (0x6EA2D420, "fabd v0.4s, v1.4s, v2.4s", a => a.V3(0x1A, true, 2, true, 0, 1, 2)),
        (0x4E22E420, "fcmeq v0.4s, v1.4s, v2.4s", a => a.V3(0x1C, false, 0, true, 0, 1, 2)),
        (0x6E22E420, "fcmge v0.4s, v1.4s, v2.4s", a => a.V3(0x1C, true, 0, true, 0, 1, 2)),
        (0x6EA2E420, "fcmgt v0.4s, v1.4s, v2.4s", a => a.V3(0x1C, true, 2, true, 0, 1, 2)),
        (0x6E22EC20, "facge v0.4s, v1.4s, v2.4s", a => a.V3(0x1D, true, 0, true, 0, 1, 2)),
        (0x6EA2EC20, "facgt v0.4s, v1.4s, v2.4s", a => a.V3(0x1D, true, 2, true, 0, 1, 2)),
        (0x4E22F420, "fmax v0.4s, v1.4s, v2.4s", a => a.V3(0x1E, false, 0, true, 0, 1, 2)),
        (0x4EA2F420, "fmin v0.4s, v1.4s, v2.4s", a => a.V3(0x1E, false, 2, true, 0, 1, 2)),
        (0x2E22F420, "fmaxp v0.2s, v1.2s, v2.2s", a => a.V3(0x1E, true, 0, false, 0, 1, 2)),
        (0x2EA2F420, "fminp v0.2s, v1.2s, v2.2s", a => a.V3(0x1E, true, 2, false, 0, 1, 2)),
        (0x4EA0F825, "fabs v5.4s, v1.4s", a => a.V2(0x0F, false, 2, true, 5, 1)),
        (0x6EA0F840, "fneg v0.4s, v2.4s", a => a.V2(0x0F, true, 2, true, 0, 2)),
        (0x4EA0D823, "fcmeq v3.4s, v1.4s, #0.0", a => a.V2(0x0D, false, 2, true, 3, 1)),
        (0x0EA0C840, "fcmgt v0.2s, v2.2s, #0.0", a => a.V2(0x0C, false, 2, false, 0, 2)),
        (0x2EA0C840, "fcmge v0.2s, v2.2s, #0.0", a => a.V2(0x0C, true, 2, false, 0, 2)),
        (0x2EA0D840, "fcmle v0.2s, v2.2s, #0.0", a => a.V2(0x0D, true, 2, false, 0, 2)),
        (0x0EA0E840, "fcmlt v0.2s, v2.2s, #0.0", a => a.V2(0x0E, false, 2, false, 0, 2)),
        (0x4E208840, "cmgt v0.16b, v2.16b, #0", a => a.V2(0x08, false, 0, true, 0, 2)),
        (0x6E608840, "cmge v0.8h, v2.8h, #0", a => a.V2(0x08, true, 1, true, 0, 2)),
        (0x0EA09840, "cmeq v0.2s, v2.2s, #0", a => a.V2(0x09, false, 2, false, 0, 2)),
        (0x2E209840, "cmle v0.8b, v2.8b, #0", a => a.V2(0x09, true, 0, false, 0, 2)),
        (0x0E20A840, "cmlt v0.8b, v2.8b, #0", a => a.V2(0x0A, false, 0, false, 0, 2)),
        (0x4E20B840, "abs v0.16b, v2.16b", a => a.V2(0x0B, false, 0, true, 0, 2)),
        (0x6E60B840, "neg v0.8h, v2.8h", a => a.V2(0x0B, true, 1, true, 0, 2)),
        (0x0E200840, "rev64 v0.8b, v2.8b", a => a.V2(0x00, false, 0, false, 0, 2)),
        (0x6E600840, "rev32 v0.8h, v2.8h", a => a.V2(0x00, true, 1, true, 0, 2)),
        (0x0E201840, "rev16 v0.8b, v2.8b", a => a.V2(0x01, false, 0, false, 0, 2)),
        (0x6E205840, "mvn v0.16b, v2.16b", a => a.V2(0x05, true, 0, true, 0, 2)),
        (0x0E205840, "cnt v0.8b, v2.8b", a => a.V2(0x05, false, 0, false, 0, 2)),
        (0x6E604840, "clz v0.8h, v2.8h", a => a.V2(0x04, true, 1, true, 0, 2)),
        (0x0E202840, "saddlp v0.4h, v2.8b", a => a.V2(0x02, false, 0, false, 0, 2)),
        (0x6E606840, "uadalp v0.4s, v2.8h", a => a.V2(0x06, true, 1, true, 0, 2)),
        (0x0E212840, "xtn v0.8b, v2.8h", a => a.V2(0x12, false, 0, false, 0, 2)),
        (0x0E214840, "sqxtn v0.8b, v2.8h", a => a.V2(0x14, false, 0, false, 0, 2)),
        (0x2E614840, "uqxtn v0.4h, v2.4s", a => a.V2(0x14, true, 1, false, 0, 2)),
        (0x2EA12840, "sqxtun v0.2s, v2.2d", a => a.V2(0x12, true, 2, false, 0, 2)),
        (0x0E204840, "cls v0.8b, v2.8b", a => a.V2(0x04, false, 0, false, 0, 2)),
        (0x4EA04840, "cls v0.4s, v2.4s", a => a.V2(0x04, false, 2, true, 0, 2)),
        (0x2E62C020, "umull v0.4s, v1.4h, v2.4h", a => a.V3Diff(0xC, true, 1, false, 0, 1, 2)),
        (0x0EA2C020, "smull v0.2d, v1.2s, v2.2s", a => a.V3Diff(0xC, false, 2, false, 0, 1, 2)),
        (0x0E22E020, "pmull v0.8h, v1.8b, v2.8b", a => a.V3Diff(0xE, false, 0, false, 0, 1, 2)),
        (0x0E628020, "smlal v0.4s, v1.4h, v2.4h", a => a.V3Diff(0x8, false, 1, false, 0, 1, 2)),
        (0x2EA2A020, "umlsl v0.2d, v1.2s, v2.2s", a => a.V3Diff(0xA, true, 2, false, 0, 1, 2)),
        (0x2E228020, "umlal v0.8h, v1.8b, v2.8b", a => a.V3Diff(0x8, true, 0, false, 0, 1, 2)),
        (0x5EE14440, "sshl d0, d2, d1", a => a.Emit(0x5E200400 | 3u << 22 | 1u << 16 | 0x08u << 11 | 2u << 5)), // the emitter's scalar I3 form
        (0x7EE14440, "ushl d0, d2, d1", a => a.Emit(0x5E200400 | 1u << 29 | 3u << 22 | 1u << 16 | 0x08u << 11 | 2u << 5)),
        (0x4EE14440, "sshl v0.2d, v2.2d, v1.2d", a => a.V3(0x08, false, 3, true, 0, 2, 1)),
        (0x2E214440, "ushl v0.8b, v2.8b, v1.8b", a => a.V3(0x08, true, 0, false, 0, 2, 1)),
        (0x2E213840, "shll v0.8h, v2.8b, #8", a => a.V2(0x13, true, 0, false, 0, 2)),
        (0x0E216840, "fcvtn v0.4h, v2.4s", a => a.V2(0x16, false, 0, false, 0, 2)),
        (0x0E217840, "fcvtl v0.4s, v2.4h", a => a.V2(0x17, false, 0, false, 0, 2)),
        (0x4EA1D840, "frecpe v0.4s, v2.4s", a => a.V2(0x1D, false, 2, true, 0, 2)),
        (0x6EA1D840, "frsqrte v0.4s, v2.4s", a => a.V2(0x1D, true, 2, true, 0, 2)),
        (0x4EA1C840, "urecpe v0.4s, v2.4s", a => a.V2(0x1C, false, 2, true, 0, 2)),
        (0x2EA1C840, "ursqrte v0.2s, v2.2s", a => a.V2(0x1C, true, 2, false, 0, 2)),
        (0x4E21D840, "scvtf v0.4s, v2.4s", a => a.V2(0x1D, false, 0, true, 0, 2)),
        (0x6E21D840, "ucvtf v0.4s, v2.4s", a => a.V2(0x1D, true, 0, true, 0, 2)),
        (0x4EA1B840, "fcvtzs v0.4s, v2.4s", a => a.V2(0x1B, false, 2, true, 0, 2)),
        (0x6EA1B840, "fcvtzu v0.4s, v2.4s", a => a.V2(0x1B, true, 2, true, 0, 2)),
        (0x4E422803, "trn1 v3.8h, v0.8h, v2.8h", a => a.VPerm(2, 1, true, 3, 0, 2)),
        (0x4E426804, "trn2 v4.8h, v0.8h, v2.8h", a => a.VPerm(6, 1, true, 4, 0, 2)),
        (0x0E021803, "uzp1 v3.8b, v0.8b, v2.8b", a => a.VPerm(1, 0, false, 3, 0, 2)),
        (0x0E025804, "uzp2 v4.8b, v0.8b, v2.8b", a => a.VPerm(5, 0, false, 4, 0, 2)),
        (0x4E823803, "zip1 v3.4s, v0.4s, v2.4s", a => a.VPerm(3, 2, true, 3, 0, 2)),
        (0x4E827804, "zip2 v4.4s, v0.4s, v2.4s", a => a.VPerm(7, 2, true, 4, 0, 2)),
        (0x0F0D0440, "sshr v0.8b, v2.8b, #3", a => a.VShiftImm(0x00, false, false, ShrImmField(0, 3), 0, 2)),
        (0x6F401440, "usra v0.2d, v2.2d, #64", a => a.VShiftImm(0x02, true, true, ShrImmField(3, 64), 0, 2)),
        (0x4F1F2440, "srshr v0.8h, v2.8h, #1", a => a.VShiftImm(0x04, false, true, ShrImmField(1, 1), 0, 2)),
        (0x2F203440, "ursra v0.2s, v2.2s, #32", a => a.VShiftImm(0x06, true, false, ShrImmField(2, 32), 0, 2)),
        (0x7F7F0440, "ushr d0, d2, #1", a => a.SShiftImm(0x00, true, ShrImmField(3, 1), 0, 2)),
        (0x5F7F5440, "shl d0, d2, #63", a => a.SShiftImm(0x0A, false, ShlImmField(3, 63), 0, 2)),
        (0x4F0F5440, "shl v0.16b, v2.16b, #7", a => a.VShiftImm(0x0A, false, true, ShlImmField(0, 7), 0, 2)),
        (0x6F215440, "sli v0.4s, v2.4s, #1", a => a.VShiftImm(0x0A, true, true, ShlImmField(2, 1), 0, 2)),
        (0x2F1C4440, "sri v0.4h, v2.4h, #4", a => a.VShiftImm(0x08, true, false, ShrImmField(1, 4), 0, 2)),
        (0x0F0E8440, "shrn v0.8b, v2.8h, #2", a => a.VShiftImm(0x10, false, false, ShrImmField(0, 2), 0, 2)),
        (0x0F108C40, "rshrn v0.4h, v2.4s, #16", a => a.VShiftImm(0x11, false, false, ShrImmField(1, 16), 0, 2)),
        (0x2F0F9440, "uqshrn v0.8b, v2.8h, #1", a => a.VShiftImm(0x12, true, false, ShrImmField(0, 1), 0, 2)),
        (0x0F189C40, "sqrshrn v0.4h, v2.4s, #8", a => a.VShiftImm(0x13, false, false, ShrImmField(1, 8), 0, 2)),
        (0x2F088440, "sqshrun v0.8b, v2.8h, #8", a => a.VShiftImm(0x10, true, false, ShrImmField(0, 8), 0, 2)),
        (0x2F0B8C40, "sqrshrun v0.8b, v2.8h, #5", a => a.VShiftImm(0x11, true, false, ShrImmField(0, 5), 0, 2)),
        (0x0F0BA440, "sshll v0.8h, v2.8b, #3", a => a.VShiftImm(0x14, false, false, ShlImmField(0, 3), 0, 2)),
        (0x2F20A440, "ushll v0.2d, v2.2s, #0", a => a.VShiftImm(0x14, true, false, ShlImmField(2, 0), 0, 2)),
        (0x6E022020, "ext v0.16b, v1.16b, v2.16b, #4", a => a.Ext(true, 0, 1, 2, 4)),
        (0x2E023820, "ext v0.8b, v1.8b, v2.8b, #7", a => a.Ext(false, 0, 1, 2, 7)),
        (0x0E020200, "tbl v0.8b, {v16.16b}, v2.8b", a => a.Tbl(false, 0, 16, 1, 2)),
        (0x0E023200, "tbx v0.8b, {v16.16b, v17.16b}, v2.8b", a => a.Tbl(false, 0, 16, 2, 2, true)),
        (0x4E040442, "dup v2.4s, v2.s[0]", a => a.DupElem(2, true, 2, 2, 0)),
        (0x0E020442, "dup v2.4h, v2.h[0]", a => a.DupElem(1, false, 2, 2, 0)),
        (0x4E1C0420, "dup v0.4s, v1.s[3]", a => a.DupElem(2, true, 0, 1, 3)),
        (0x4E040E00, "dup v0.4s, w16", a => a.DupGp(2, true, 0, 16)),
        (0x4E0C1E00, "mov v0.s[1], w16", a => a.InsGp(2, 0, 1, 16)),
        (0x0E0C3C30, "mov w16, v1.s[1]", a => a.Umov(2, 16, 1, 1)),
        (0x4E083C30, "mov x16, v1.d[0]", a => a.Umov(3, 16, 1, 0)),
        (0x6E1804B0, "mov v16.d[1], v5.d[0]", a => a.InsD1(16, 5)),
        (0x6F00E400, "movi v0.2d, #0", a => a.VZero(0)),
        (0x0F01E507, "movi v7.8b, #0x28", a => a.MoviB(7, 0x28, false)),
        (0x4F07E7E7, "movi v7.16b, #0xff", a => a.MoviB(7, 0xFF, true)),
        (0x4C40A200, "ld1 {v0.16b, v1.16b}, [x16]", a => a.Ld1(0, 2, 16)),
        (0x4C407200, "ld1 {v0.16b}, [x16]", a => a.Ld1(0, 1, 16)),
        // ---- perf/arm64-jit: pinned block counters, state pairs, FPSCR check, SIMD register cache (GNU as 2.40)
        (0xB100339C, "adds x28, x28, #0xc", a => a.AddsImm(28, 28, 12, true)),
        (0xB1400F9C, "adds x28, x28, #3, lsl #12", a => a.AddsImm(28, 28, 3 << 12, true)),
        (0xAB08039C, "adds x28, x28, x8", a => a.Adds(28, 28, 8, x: true)),
        (0x9100075A, "add x26, x26, #1", a => a.AddImm(26, 26, 1, true)),
        (0x9100177B, "add x27, x27, #5", a => a.AddImm(27, 27, 5, true)),
        (0xCB11039C, "sub x28, x28, x17", a => a.Sub(28, 28, 17, x: true)),
        (0x8B110390, "add x16, x28, x17", a => a.Add(16, 28, 17, x: true)),
        (0xF941127C, "ldr x28, [x19, #544]", a => a.LdrX(28, 19, 544)),
        (0xF9011A7A, "str x26, [x19, #560]", a => a.StrX(26, 19, 560)),
        (0xB9424A68, "ldr w8, [x19, #584]", a => a.Ldr(8, 19, 584)),
        (0x29080660, "stp w0, w1, [x19, #64]", a => a.StpW(0, 1, 19, 64)),
        (0x291F267F, "stp wzr, w9, [x19, #248]", a => a.StpW(ZR, 9, 19, 248)),
        (0x29145A75, "stp w21, w22, [x19, #160]", a => a.StpW(21, 22, 19, 160)),
        (0x29412A69, "ldp w9, w10, [x19, #8]", a => a.LdpW(9, 10, 19, 8)),
        (0x295F0660, "ldp w0, w1, [x19, #248]", a => a.LdpW(0, 1, 19, 248)),
        (0x53106610, "ubfx w16, w16, #16, #10", a => a.Ubfx(16, 16, 16, 10)),
        (0x710C021F, "cmp w16, #0x300", a => a.CmpImm(16, 0x300)),
        (0x4EB31E72, "mov v18.16b, v19.16b", a => a.VMov(18, 19)),
        (0x3DC02A72, "ldr q18, [x19, #160]", a => a.LdSt(LsKind.LdrQ, 18, 19, 160)),
        (0xFD005674, "str d20, [x19, #168]", a => a.LdSt(LsKind.StrD, 20, 19, 168)),
        (0xFD40CE7F, "ldr d31, [x19, #408]", a => a.LdSt(LsKind.LdrD, 31, 19, 408)),
        (0x4E3FD672, "fadd v18.4s, v19.4s, v31.4s", a => a.V3(0x1A, false, 0, true, 18, 19, 31)),
        (0x2E34DE40, "fmul v0.2s, v18.2s, v20.2s", a => a.V3(0x1B, true, 0, false, 0, 18, 20)),
        (0x4EB38655, "add v21.4s, v18.4s, v19.4s", a => a.V3(0x10, false, 2, true, 21, 18, 19)),
        (0x3D806676, "str q22, [x19, #400]", a => a.LdSt(LsKind.StrQ, 22, 19, 400)),
    ];

    public static IEnumerable<object[]> CaseIndices() => Enumerable.Range(0, Cases.Length).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(CaseIndices))]
    public void EncodingMatchesGnuAs(int index)
    {
        var (expected, asm, emit) = Cases[index];
        var a = new A();
        emit(a);
        var bytes = a.Finish();
        uint word = BitConverter.ToUInt32(bytes, asm.StartsWith("1:", StringComparison.Ordinal) ? 4 : 0); // backward branch: 2nd word
        Assert.True(expected == word, $"{asm}: expected {expected:X8}, got {word:X8}");
    }

    /// <summary>The assembly text of every case (input for regenerating the expected words with GNU as).</summary>
    public static string AllAsm() => string.Join("\n", Cases.Select(c => c.Asm.Replace("; ", "\n")));

    [Fact]
    public void LogicalImmediatesRoundTrip()
    {
        // every encodable 32-bit bitmask decodes back to itself (DecodeBitMasks)
        var rng = new Random(7);
        for (int size = 2; size <= 32; size *= 2)
            for (int ones = 1; ones < size; ones++)
                for (int rot = 0; rot < size; rot++)
                {
                    ulong elem = ((1UL << ones) - 1);
                    elem = ((elem >> rot) | (elem << (size - rot))) & ((1UL << size) - 1);
                    ulong v = 0;
                    for (int i = 0; i < 32; i += size) v |= elem << i;
                    Assert.True(TryLogicalImm(v, false, out uint enc), $"{v:X8}");
                    Assert.Equal(v, Decode(enc, false));
                }
        for (int i = 0; i < 2000; i++)
        {
            ulong v = (ulong)rng.NextInt64();
            if (TryLogicalImm(v, true, out uint enc)) Assert.Equal(v, Decode(enc, true));
        }
        Assert.False(TryLogicalImm(0, false, out _));
        Assert.False(TryLogicalImm(0xFFFFFFFF, false, out _));
        Assert.False(TryLogicalImm(0x03F70000, false, out _));
    }

    /// <summary>The ARM ARM's DecodeBitMasks (immediate form).</summary>
    static ulong Decode(uint enc, bool x)
    {
        uint n = (enc >> 12) & 1, immr = (enc >> 6) & 0x3F, imms = enc & 0x3F;
        int len = 31 - System.Numerics.BitOperations.LeadingZeroCount((n << 6) | (~imms & 0x3F));
        int size = 1 << len;
        int s = (int)(imms & (uint)(size - 1)), r = (int)(immr & (uint)(size - 1));
        ulong welem = s + 1 == 64 ? ulong.MaxValue : (1UL << (s + 1)) - 1;
        ulong mask = size == 64 ? ulong.MaxValue : (1UL << size) - 1;
        ulong elem = r == 0 ? welem : ((welem >> r) | (welem << (size - r))) & mask;
        ulong v = 0;
        for (int i = 0; i < 64; i += size) v |= elem << i;
        return x ? v : v & 0xFFFFFFFF;
    }
}
