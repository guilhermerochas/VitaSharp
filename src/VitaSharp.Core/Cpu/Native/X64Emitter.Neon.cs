using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Advanced SIMD lowering: every <see cref="VecKind"/> as SSE2-SSE4.1 (plus AVX2/FMA/F16C where the kind needs it) on
/// xmm0-xmm5, operating on the guest register file in the state. D forms load/store the low 8 bytes only (movq), so
/// the other half of the guest register is untouched. Floating-point kinds run with MXCSR = DAZ only, round to
/// nearest (inputs flushed like the handlers' Ftz), and flush denormal results / replace NaNs explicitly (FRes),
/// which reproduces the interpreter exactly; consecutive vector ops share one MXCSR switch.
/// </summary>
public sealed unsafe partial class X64Emitter
{
    /// <summary>The host features the vector lowering relies on (SSE4.1 implies SSSE3 on every shipping CPU, checked anyway).</summary>
    public static readonly bool NeonSupported = Sse41.IsSupported && Ssse3.IsSupported;
    static readonly bool HasSse42 = Sse42.IsSupported;
    static readonly bool HasAvx2 = Avx2.IsSupported;
    static readonly bool HasFma = Fma.IsSupported;
    static readonly bool HasF16c = Avx.IsSupported && (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0;

    /// <summary>Whether <paramref name="k"/> (at lane <paramref name="size"/>) has an inline lowering on this host.</summary>
    public static bool SupportsVec(VecKind k, uint size = 2)
    {
        if (!NeonSupported) return false;
        return k switch
        {
            VecKind.FmaF or VecKind.FmsF => HasFma,
            VecKind.ShlRegI => HasAvx2,
            VecKind.CvtF2H or VecKind.CvtH2F => HasF16c,
            VecKind.QaddI or VecKind.QsubI => size < 3 || HasSse42,
            _ => true,
        };
    }

    bool _neonMxcsr;

    static readonly AssemblerRegisterXMM X0 = xmm0, X1 = xmm1, X2 = xmm2, X3 = xmm3, X4 = xmm4, X5 = xmm5;

    // ------------------------------------------------------------------ constants

    static readonly ConcurrentDictionary<(ulong, ulong), ulong> Pool = new();

    /// <summary>Address of a 16-byte aligned constant (allocated once per value, never freed: the set is small).</summary>
    static ulong ConstAddr(ulong lo, ulong hi) => Pool.GetOrAdd((lo, hi), static k =>
    {
        var p = (ulong*)NativeMemory.AlignedAlloc(16, 16);
        p[0] = k.Item1; p[1] = k.Item2;
        return (ulong)p;
    });

    /// <summary>Memory operand for a constant (clobbers rax).</summary>
    AssemblerMemoryOperand K(ulong lo, ulong hi) { _a.mov(rax, ConstAddr(lo, hi)); return __xmmword_ptr[rax]; }
    AssemblerMemoryOperand K64(ulong v) => K(v, v);
    AssemblerMemoryOperand K32(uint v) => K64(v | ((ulong)v << 32));
    AssemblerMemoryOperand K16(uint v) => K64((v & 0xFFFF) * 0x0001000100010001ul);
    AssemblerMemoryOperand K8(uint v) => K64((v & 0xFF) * 0x0101010101010101ul);
    AssemblerMemoryOperand KLane(uint size, ulong v) => size switch
    {
        0 => K8((uint)v), 1 => K16((uint)v), 2 => K32((uint)v), _ => K64(v),
    };
    AssemblerMemoryOperand KBytes(byte[] b16) => K(BitConverter.ToUInt64(b16, 0), BitConverter.ToUInt64(b16, 8));

    static ulong LaneMask(uint size) => size == 3 ? ulong.MaxValue : (1ul << (8 << (int)size)) - 1;
    static uint F32(float f) => BitConverter.SingleToUInt32Bits(f);

    void NeonFp()
    {
        if (_neonMxcsr) return;
        _a.ldmxcsr(State(OffMxcsrNeon));
        _neonMxcsr = true;
    }

    // ------------------------------------------------------------------ operand helpers

    static int Reg8(int off) => OffD + off;
    void LdV(AssemblerRegisterXMM r, int off, bool q) { if (q) _a.movdqu(r, __xmmword_ptr[rbx + OffD + off]); else _a.movq(r, __qword_ptr[rbx + OffD + off]); }
    void StV(int off, AssemblerRegisterXMM r, bool q) { if (q) _a.movdqu(__xmmword_ptr[rbx + OffD + off], r); else _a.movq(__qword_ptr[rbx + OffD + off], r); }
    void Mov(AssemblerRegisterXMM d, AssemblerRegisterXMM s) { if (d != s) _a.movdqa(d, s); }

    void PAdd(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size) { case 0: _a.paddb(d, s); break; case 1: _a.paddw(d, s); break; case 2: _a.paddd(d, s); break; default: _a.paddq(d, s); break; }
    }

    void PSub(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size) { case 0: _a.psubb(d, s); break; case 1: _a.psubw(d, s); break; case 2: _a.psubd(d, s); break; default: _a.psubq(d, s); break; }
    }

    void PCmpEq(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size) { case 0: _a.pcmpeqb(d, s); break; case 1: _a.pcmpeqw(d, s); break; case 2: _a.pcmpeqd(d, s); break; default: _a.pcmpeqq(d, s); break; }
    }

    /// <summary>Signed d > s per lane (64-bit lanes need SSE4.2).</summary>
    void PCmpGt(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size) { case 0: _a.pcmpgtb(d, s); break; case 1: _a.pcmpgtw(d, s); break; case 2: _a.pcmpgtd(d, s); break; default: _a.pcmpgtq(d, s); break; }
    }

    void PMax(uint size, bool unsigned, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size)
        {
            case 0: if (unsigned) _a.pmaxub(d, s); else _a.pmaxsb(d, s); break;
            case 1: if (unsigned) _a.pmaxuw(d, s); else _a.pmaxsw(d, s); break;
            default: if (unsigned) _a.pmaxud(d, s); else _a.pmaxsd(d, s); break;
        }
    }

    void PMin(uint size, bool unsigned, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size)
        {
            case 0: if (unsigned) _a.pminub(d, s); else _a.pminsb(d, s); break;
            case 1: if (unsigned) _a.pminuw(d, s); else _a.pminsw(d, s); break;
            default: if (unsigned) _a.pminud(d, s); else _a.pminsd(d, s); break;
        }
    }

    void PMul(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s, AssemblerRegisterXMM t1, AssemblerRegisterXMM t2)
    {
        switch (size)
        {
            case 0: // bytes: even bytes from one 16-bit product, odd bytes from another
                Mov(t1, d); Mov(t2, s);
                _a.pmullw(d, s);
                _a.pand(d, K16(0x00FF));
                _a.psrlw(t1, 8); _a.psrlw(t2, 8);
                _a.pmullw(t1, t2);
                _a.psllw(t1, 8);
                _a.por(d, t1);
                break;
            case 1: _a.pmullw(d, s); break;
            default: _a.pmulld(d, s); break;
        }
    }

    void Not(AssemblerRegisterXMM r, AssemblerRegisterXMM t) { _a.pcmpeqd(t, t); _a.pxor(r, t); }

    /// <summary>r = ~mask &amp; r | mask &amp; value (mask lanes all ones or zero); mask is clobbered.</summary>
    void Blend(AssemblerRegisterXMM r, AssemblerRegisterXMM mask, AssemblerRegisterXMM value)
    {
        _a.pand(value, mask);
        _a.pandn(mask, r);
        _a.por(mask, value);
        Mov(r, mask);
    }

    // shifts by an immediate count; counts at or beyond the lane width give 0 (logical) or the sign (arithmetic)
    void ShlI(AssemblerRegisterXMM r, uint size, int n)
    {
        if (n == 0) return;
        if (n >= (8 << (int)size)) { _a.pxor(r, r); return; }
        switch (size)
        {
            case 0: _a.psllw(r, (byte)n); _a.pand(r, K8((0xFFu << n) & 0xFF)); break;
            case 1: _a.psllw(r, (byte)n); break;
            case 2: _a.pslld(r, (byte)n); break;
            default: _a.psllq(r, (byte)n); break;
        }
    }

    void ShrU(AssemblerRegisterXMM r, uint size, int n)
    {
        if (n == 0) return;
        if (n >= (8 << (int)size)) { _a.pxor(r, r); return; }
        switch (size)
        {
            case 0: _a.psrlw(r, (byte)n); _a.pand(r, K8(0xFFu >> n)); break;
            case 1: _a.psrlw(r, (byte)n); break;
            case 2: _a.psrld(r, (byte)n); break;
            default: _a.psrlq(r, (byte)n); break;
        }
    }

    void ShrS(AssemblerRegisterXMM r, uint size, int n, AssemblerRegisterXMM t)
    {
        int bits = 8 << (int)size;
        n = Math.Min(n, bits - 1);
        if (n == 0) return;
        switch (size)
        {
            case 1: _a.psraw(r, (byte)n); break;
            case 2: _a.psrad(r, (byte)n); break;
            default:
            {
                // logical shift, then sign-extend from the shifted sign position: (x ^ m) - m
                ShrU(r, size, n);
                ulong m = 1ul << (bits - 1 - n);
                _a.movdqa(t, KLane(size, m));
                _a.pxor(r, t);
                PSub(size, r, t);
                break;
            }
        }
    }

    void Shr(AssemblerRegisterXMM r, uint size, int n, bool signed, AssemblerRegisterXMM t)
    {
        if (signed) ShrS(r, size, n, t); else ShrU(r, size, n);
    }

    /// <summary>r = (r >> n) with ARM rounding (adds bit n-1 of the operand): (x + 2^(n-1)) >> n without overflow.</summary>
    void RoundShr(AssemblerRegisterXMM r, uint size, int n, bool signed, AssemblerRegisterXMM t, AssemblerRegisterXMM t2)
    {
        Mov(t, r);
        ShrU(t, size, n - 1);
        _a.pand(t, KLane(size, 1));
        Shr(r, size, n, signed, t2);
        PAdd(size, r, t);
    }

    /// <summary>Truncating narrow of the wide lanes (size = wide lane size) into the low 8 bytes.</summary>
    void NarrowTrunc(AssemblerRegisterXMM r, uint wsize)
    {
        switch (wsize)
        {
            case 1: _a.pand(r, K16(0x00FF)); _a.packuswb(r, r); break;
            case 2: _a.pand(r, K32(0xFFFF)); _a.packusdw(r, r); break;
            default: _a.pshufd(r, r, 0x08); break;
        }
    }

    void Widen(AssemblerRegisterXMM d, AssemblerRegisterXMM s, uint size, bool unsigned)
    {
        switch (size)
        {
            case 0: if (unsigned) _a.pmovzxbw(d, s); else _a.pmovsxbw(d, s); break;
            case 1: if (unsigned) _a.pmovzxwd(d, s); else _a.pmovsxwd(d, s); break;
            default: if (unsigned) _a.pmovzxdq(d, s); else _a.pmovsxdq(d, s); break;
        }
    }

    /// <summary>Sets FPSCR.QC when any byte of <paramref name="mask"/> is non-zero (D forms: low 8 bytes).</summary>
    void QcIfAny(AssemblerRegisterXMM mask, bool q)
    {
        var skip = _a.CreateLabel();
        _a.pmovmskb(eax, mask);
        if (!q) _a.and(eax, 0xFF);
        _a.test(eax, eax);
        _a.jz(skip);
        _a.or(State(OffFpscr), 0x08000000);
        Place(ref skip);
    }

    /// <summary>Sets FPSCR.QC when <paramref name="eqMask"/> (all-ones lanes = no saturation) has a zero byte.</summary>
    void QcIfNotAll(AssemblerRegisterXMM eqMask, bool q)
    {
        var skip = _a.CreateLabel();
        _a.pmovmskb(eax, eqMask);
        if (!q) { _a.and(eax, 0xFF); _a.cmp(eax, 0xFF); }
        else _a.cmp(eax, 0xFFFF);
        _a.je(skip);
        _a.or(State(OffFpscr), 0x08000000);
        Place(ref skip);
    }

    /// <summary>Handler FRes on f32 lanes: denormal results -> signed zero, NaN -> default NaN (clobbers xmm4, xmm5).</summary>
    void FRes(AssemblerRegisterXMM r)
    {
        var a = _a;
        a.movdqa(X4, r);
        a.pand(X4, K32(0x7FFFFFFF));
        a.movdqa(X5, K32(0x00800000));
        a.pcmpgtd(X5, X4);            // |r| < smallest normal (zero or denormal)
        a.pcmpgtd(X4, K32(0x7F800000)); // NaN
        a.pand(X5, K32(0x7FFFFFFF));
        a.pandn(X5, r);               // magnitude of denormals cleared, sign kept
        a.movdqa(r, X5);
        a.movdqa(X5, X4);
        a.pandn(X5, r);
        a.pand(X4, K32(0x7FC00000));
        a.por(X4, X5);
        a.movdqa(r, X4);
    }

    /// <summary>Handler Ftz on f32 lanes (denormal inputs -> signed zero), for kinds that look at the bits (clobbers xmm4, xmm5).</summary>
    void FlushIn(AssemblerRegisterXMM r)
    {
        var a = _a;
        a.movdqa(X4, r);
        a.pand(X4, K32(0x7FFFFFFF));
        a.movdqa(X5, K32(0x00800000));
        a.pcmpgtd(X5, X4);
        a.pand(X5, K32(0x7FFFFFFF));
        a.pandn(X5, r);
        a.movdqa(r, X5);
    }

    /// <summary>pshufb masks gathering the even (odd) lanes of a 16-byte vector into its low 8 bytes.</summary>
    static byte[] PairMask(uint size, bool odd)
    {
        int eb = 1 << (int)size;
        var m = new byte[16];
        Array.Fill(m, (byte)0x80);
        for (int j = 0; j < 8; j++) m[j] = (byte)((2 * (j / eb) + (odd ? 1 : 0)) * eb + j % eb);
        return m;
    }

    /// <summary>Loads n (low 8 bytes) and m (high 8 bytes) of a pairwise op into r.</summary>
    void LdPair(AssemblerRegisterXMM r, AssemblerRegisterXMM t, int nOff, int mOff)
    {
        _a.movq(r, __qword_ptr[rbx + OffD + nOff]);
        _a.movq(t, __qword_ptr[rbx + OffD + mOff]);
        _a.punpcklqdq(r, t);
    }

    /// <summary>Broadcasts the scalar element at byte offset <paramref name="off"/> (16 or 32 bits) to every lane of r.</summary>
    void Broadcast(AssemblerRegisterXMM r, int off, uint size)
    {
        if (size == 2) _a.movd(r, __dword_ptr[rbx + OffD + off]);
        else
        {
            _a.movzx(eax, __word_ptr[rbx + OffD + off]);
            _a.imul(eax, eax, 0x10001);
            _a.movd(r, eax);
        }
        _a.pshufd(r, r, 0);
    }

    /// <summary>
    /// out = pshufb(a, ma) | pshufb(b, mb) where each output byte names its source (0-15: a, 16-31: b, 0x80: zero).
    /// </summary>
    void Permute(AssemblerRegisterXMM dst, AssemblerRegisterXMM srcA, AssemblerRegisterXMM srcB, AssemblerRegisterXMM t, byte[] src)
    {
        var ma = new byte[16]; var mb = new byte[16];
        bool anyA = false, anyB = false;
        for (int j = 0; j < 16; j++)
        {
            byte s = src[j];
            ma[j] = s < 16 ? s : (byte)0x80;
            mb[j] = s is >= 16 and < 32 ? (byte)(s - 16) : (byte)0x80;
            anyA |= s < 16; anyB |= s is >= 16 and < 32;
        }
        Mov(dst, srcA);
        _a.pshufb(dst, KBytes(ma));
        if (anyB)
        {
            Mov(t, srcB);
            _a.pshufb(t, KBytes(mb));
            if (anyA) _a.por(dst, t); else Mov(dst, t);
        }
        else if (!anyA) _a.pxor(dst, dst);
    }

    // ------------------------------------------------------------------ estimate tables (built from the handlers)

    static readonly ulong RecpeTable = BuildTable(256, i => BitConverter.SingleToUInt32Bits(Neon.RecipEstimate(BitConverter.UInt32BitsToSingle(0x3F000000u | ((uint)i << 15)))) & 0x7FFFFF);
    static readonly ulong RsqrteTable = BuildTable(256, i =>
        BitConverter.SingleToUInt32Bits(Neon.RSqrtEstimate(BitConverter.UInt32BitsToSingle((i < 128 ? 126u : 125u) << 23 | ((uint)(i & 127) << 16)))) & 0x7FFFFF);
    static readonly ulong URecpeTable = BuildTable(256, i => Neon.URecipEstimate(0x80000000u | ((uint)i << 23)));
    static readonly ulong URsqrteTable = BuildTable(512, i => Neon.URSqrtEstimate((uint)i << 23));

    static ulong BuildTable(int n, Func<int, uint> f)
    {
        var p = (uint*)NativeMemory.AlignedAlloc((nuint)(n * 4), 64);
        for (int i = 0; i < n; i++) p[i] = f(i);
        return (ulong)p;
    }

    /// <summary>One lane of VRECPE/VRSQRTE (f32 or u32) in eax (result in eax; clobbers ecx, edx).</summary>
    void EstimateLane(VecKind kind)
    {
        var a = _a;
        var done = a.CreateLabel();
        switch (kind)
        {
            case VecKind.RecpeF:
            {
                var nan = a.CreateLabel(); var zero = a.CreateLabel(); var inf = a.CreateLabel();
                a.mov(edx, eax);
                a.and(edx, 0x7FFFFFFF);
                a.cmp(edx, 0x7F800000);
                a.ja(nan);
                a.cmp(edx, 0x7E800000);
                a.jae(zero);                  // |x| >= 2^126 (and infinity): signed zero
                a.cmp(edx, 0x00800000);
                a.jb(inf);                    // zero / flushed denormal: signed infinity
                a.mov(ecx, edx);
                a.shr(ecx, (byte)15);
                a.and(ecx, 0xFF);
                a.shr(edx, (byte)23);
                a.neg(edx);
                a.add(edx, 253);
                a.shl(edx, (byte)23);
                a.and(eax, unchecked((int)0x80000000));
                a.or(edx, eax);
                a.mov(rax, RecpeTable);
                a.or(edx, __dword_ptr[rax + rcx * 4]);
                a.mov(eax, edx);
                a.jmp(done);
                Place(ref nan);
                a.mov(eax, 0x7FC00000u);
                a.jmp(done);
                Place(ref zero);
                a.and(eax, unchecked((int)0x80000000));
                a.jmp(done);
                Place(ref inf);
                a.and(eax, unchecked((int)0x80000000));
                a.or(eax, 0x7F800000);
                break;
            }
            case VecKind.RsqrteF:
            {
                var nan = a.CreateLabel(); var inf = a.CreateLabel(); var zero = a.CreateLabel();
                a.mov(edx, eax);
                a.and(edx, 0x7FFFFFFF);
                a.cmp(edx, 0x7F800000);
                a.ja(nan);
                a.cmp(edx, 0x00800000);
                a.jb(inf);                    // zero / flushed denormal: signed infinity
                a.test(eax, eax);
                a.js(nan);                    // negative: default NaN
                a.cmp(eax, 0x7F800000);
                a.je(zero);                   // +infinity: +0
                a.mov(ecx, eax);
                a.shr(ecx, (byte)16);
                a.and(ecx, 0x7F);
                a.mov(edx, eax);
                a.shr(edx, (byte)23);         // exponent
                a.test(edx, 1);
                a.jz(Lbl(out var even));
                a.or(ecx, 0x80);
                Place(ref even);
                a.neg(edx);
                a.add(edx, 380);
                a.shr(edx, (byte)1);
                a.shl(edx, (byte)23);
                a.mov(rax, RsqrteTable);
                a.or(edx, __dword_ptr[rax + rcx * 4]);
                a.mov(eax, edx);
                a.jmp(done);
                Place(ref nan);
                a.mov(eax, 0x7FC00000u);
                a.jmp(done);
                Place(ref zero);
                a.xor(eax, eax);
                a.jmp(done);
                Place(ref inf);
                a.and(eax, unchecked((int)0x80000000));
                a.or(eax, 0x7F800000);
                break;
            }
            case VecKind.RecpeU:
            {
                var ones = a.CreateLabel();
                a.test(eax, eax);
                a.jns(ones);
                a.shr(eax, (byte)23);
                a.and(eax, 0xFF);
                a.mov(rcx, URecpeTable);
                a.mov(eax, __dword_ptr[rcx + rax * 4]);
                a.jmp(done);
                Place(ref ones);
                a.mov(eax, 0xFFFFFFFFu);
                break;
            }
            default: // RsqrteU
            {
                var ones = a.CreateLabel();
                a.test(eax, 0xC0000000u.AsInt());
                a.jz(ones);
                a.shr(eax, (byte)23);
                a.mov(rcx, URsqrteTable);
                a.mov(eax, __dword_ptr[rcx + rax * 4]);
                a.jmp(done);
                Place(ref ones);
                a.mov(eax, 0xFFFFFFFFu);
                break;
            }
        }
        Place(ref done);
    }

    Label Lbl(out Label l) { l = _a.CreateLabel(); return l; }

    // ------------------------------------------------------------------ VecOp

    Label _tinySlow;

    /// <summary>
    /// One vector op. Kinds flagged <see cref="VecFlags.SlowPath"/> check their rounded products for +/- the smallest
    /// normal (possibly a tiny value rounded up, which ARM flushes) and run the interpreter handler for the whole
    /// instruction in that rare case, before anything was written back.
    /// </summary>
    void VecOp(IrInst x, Runtime rt)
    {
        var a = _a;
        if ((x.Imm & (uint)VecFlags.SlowPath) == 0) { VecOpCore(x); return; }
        _tinySlow = a.CreateLabel();
        var done = a.CreateLabel();
        VecOpCore(x);
        a.jmp(done);
        Place(ref _tinySlow);
        a.ldmxcsr(State(OffMxcsrHost));
        uint op = x.Imm2 >> 16;
        if (Windows) { a.mov(rcx, rbx); a.mov(edx, op); a.mov(r8d, x.Imm3); a.xor(r9d, r9d); }
        else { a.mov(rdi, rbx); a.mov(esi, op); a.mov(edx, x.Imm3); a.xor(ecx, ecx); }
        a.mov(rax, rt.Callout);
        a.call(rax);
        a.test(eax, eax);
        a.jnz(_abort);
        a.ldmxcsr(State(OffMxcsrNeon));   // the fast path's MXCSR state at the join
        Place(ref done);
    }

    /// <summary>Jumps to the slow path when a lane of r is +/- the smallest normal (clobbers rax, xmm4).</summary>
    void TinyGuard(AssemblerRegisterXMM r, bool q)
    {
        var a = _a;
        a.movdqa(X4, r);
        a.pand(X4, K32(0x7FFFFFFF));
        a.pcmpeqd(X4, K32(0x00800000));
        a.pmovmskb(eax, X4);
        if (!q) a.and(eax, 0xFF);
        a.test(eax, eax);
        a.jnz(_tinySlow);
    }

    void VecOpCore(IrInst x)
    {
        var a = _a;
        var kind = (VecKind)(x.Imm & 0xFF);
        bool q = (x.Imm & (uint)VecFlags.Q) != 0;
        uint size = (x.Imm >> 9) & 3;
        bool u = (x.Imm & (uint)VecFlags.Unsigned) != 0;
        bool round = (x.Imm & (uint)VecFlags.Round) != 0;
        bool acc = (x.Imm & (uint)VecFlags.Accumulate) != 0;
        int aux = (int)(x.Imm2 & 0xFFFF);
        int dOff = (int)(x.Imm4 & 0xFF), nOff = (int)((x.Imm4 >> 8) & 0xFF), mOff = (int)((x.Imm4 >> 16) & 0xFF);
        int bits = 8 << (int)size;

        switch (kind)
        {
            // ---------------------------------------------------------- bitwise and integer three-same
            case VecKind.And: case VecKind.Bic: case VecKind.Orr: case VecKind.Orn: case VecKind.Eor:
            case VecKind.Bsl: case VecKind.Bit: case VecKind.Bif:
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                switch (kind)
                {
                    case VecKind.And: a.pand(X0, X1); break;
                    case VecKind.Bic: a.pandn(X1, X0); Mov(X0, X1); break;              // n & ~m
                    case VecKind.Orr: a.por(X0, X1); break;
                    case VecKind.Orn: Not(X1, X2); a.por(X0, X1); break;
                    case VecKind.Eor: a.pxor(X0, X1); break;
                    case VecKind.Bsl: LdV(X2, dOff, q); a.pand(X0, X2); a.pandn(X2, X1); a.por(X0, X2); break; // (n&d)|(m&~d)
                    case VecKind.Bit: LdV(X2, dOff, q); a.pand(X0, X1); a.pandn(X1, X2); a.por(X0, X1); break; // (n&m)|(d&~m)
                    default: LdV(X2, dOff, q); a.pand(X2, X1); a.pandn(X1, X0); Mov(X0, X2); a.por(X0, X1); break; // (d&m)|(n&~m)
                }
                StV(dOff, X0, q);
                return;
            case VecKind.AddI: case VecKind.SubI:
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                if (kind == VecKind.AddI) PAdd(size, X0, X1); else PSub(size, X0, X1);
                StV(dOff, X0, q);
                return;
            case VecKind.MulI: case VecKind.MlaI: case VecKind.MlsI:
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                PMul(size, X0, X1, X2, X3);
                if (kind != VecKind.MulI)
                {
                    LdV(X2, dOff, q);
                    if (kind == VecKind.MlaI) PAdd(size, X2, X0); else PSub(size, X2, X0);
                    Mov(X0, X2);
                }
                StV(dOff, X0, q);
                return;
            case VecKind.CeqI:
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                PCmpEq(size, X0, X1);
                StV(dOff, X0, q);
                return;
            case VecKind.TstI:
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                a.pand(X0, X1);
                a.pxor(X1, X1);
                PCmpEq(size, X0, X1);
                Not(X0, X1);
                StV(dOff, X0, q);
                return;
            case VecKind.CgtI: case VecKind.CgeI:
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                if (u)
                {
                    a.movdqa(X2, KLane(size, 1ul << (bits - 1)));
                    a.pxor(X0, X2); a.pxor(X1, X2);
                }
                if (kind == VecKind.CgtI) PCmpGt(size, X0, X1);
                else { PCmpGt(size, X1, X0); Not(X1, X2); Mov(X0, X1); } // n >= m == !(m > n)
                StV(dOff, X0, q);
                return;
            case VecKind.MaxI: case VecKind.MinI:
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                if (kind == VecKind.MaxI) PMax(size, u, X0, X1); else PMin(size, u, X0, X1);
                StV(dOff, X0, q);
                return;
            case VecKind.AbdI: case VecKind.AbaI:
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                Mov(X2, X0);
                PMax(size, u, X0, X1);
                PMin(size, u, X2, X1);
                PSub(size, X0, X2);
                if (kind == VecKind.AbaI) { LdV(X1, dOff, q); PAdd(size, X0, X1); }
                StV(dOff, X0, q);
                return;
            case VecKind.HaddI: // (x & y) + ((x ^ y) >> 1)
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                Mov(X2, X0);
                a.pand(X0, X1);
                a.pxor(X2, X1);
                Shr(X2, size, 1, !u, X3);
                PAdd(size, X0, X2);
                StV(dOff, X0, q);
                return;
            case VecKind.RhaddI: // (x | y) - ((x ^ y) >> 1)
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                if (u && size == 0) a.pavgb(X0, X1);
                else if (u && size == 1) a.pavgw(X0, X1);
                else
                {
                    Mov(X2, X0);
                    a.por(X0, X1);
                    a.pxor(X2, X1);
                    Shr(X2, size, 1, !u, X3);
                    PSub(size, X0, X2);
                }
                StV(dOff, X0, q);
                return;
            case VecKind.HsubI: // (x >> 1) - (y >> 1) - (~x & y & 1)
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                Mov(X2, X0);
                a.pandn(X2, X1);
                a.pand(X2, KLane(size, 1));
                Shr(X0, size, 1, !u, X3);
                Shr(X1, size, 1, !u, X3);
                PSub(size, X0, X1);
                PSub(size, X0, X2);
                StV(dOff, X0, q);
                return;
            case VecKind.QaddI: case VecKind.QsubI:
                SatAddSub(kind == VecKind.QsubI, size, u, q, dOff, nOff, mOff);
                return;
            case VecKind.PaddI: case VecKind.PmaxI: case VecKind.PminI:
                LdPair(X0, X1, nOff, mOff);
                Mov(X1, X0);
                a.pshufb(X1, KBytes(PairMask(size, false)));
                a.pshufb(X0, KBytes(PairMask(size, true)));
                if (kind == VecKind.PaddI) PAdd(size, X1, X0);
                else if (kind == VecKind.PmaxI) PMax(size, u, X1, X0);
                else PMin(size, u, X1, X0);
                StV(dOff, X1, false);
                return;
            case VecKind.QdmulhI: case VecKind.QrdmulhI: // 16-bit lanes
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                Mov(X2, X0); Mov(X3, X1);
                if (kind == VecKind.QrdmulhI) a.pmulhrsw(X0, X1); // (x*y + 2^14) >> 15
                else
                {
                    // (2xy) >> 16 = (hi << 1) | (lo >> 15)
                    Mov(X4, X0);
                    a.pmulhw(X0, X1);
                    a.pmullw(X4, X1);
                    a.psllw(X0, 1);
                    a.psrlw(X4, 15);
                    a.por(X0, X4);
                }
                // -32768 * -32768 is the only product that overflows: saturate to 0x7FFF and set QC
                a.movdqa(X4, K16(0x8000));
                a.pcmpeqw(X2, X4);
                a.pcmpeqw(X3, X4);
                a.pand(X2, X3);
                a.pxor(X0, X2);
                QcIfAny(X2, q);
                StV(dOff, X0, q);
                return;
            case VecKind.ShlRegI: // 32-bit lanes, AVX2: VSHL (register), shift = signed low byte of each n lane
                LdV(X0, mOff, q); LdV(X1, nOff, q);
                a.pslld(X1, 24);
                a.psrad(X1, 24);
                a.vpsllvd(X2, X0, X1);
                a.pxor(X3, X3);
                a.psubd(X3, X1);
                if (u) a.vpsrlvd(X0, X0, X3); else a.vpsravd(X0, X0, X3);
                a.vblendvps(X0, X2, X0, X1);
                StV(dOff, X0, q);
                return;

            // ---------------------------------------------------------- float three-same
            case VecKind.AddF: case VecKind.SubF: case VecKind.MulF: case VecKind.MlaF: case VecKind.MlsF:
            case VecKind.AbdF:
                NeonFp();
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                switch (kind)
                {
                    case VecKind.AddF: a.addps(X0, X1); break;
                    case VecKind.SubF: a.subps(X0, X1); break;
                    case VecKind.MulF: a.mulps(X0, X1); TinyGuard(X0, q); break;
                    case VecKind.AbdF: a.subps(X0, X1); a.pand(X0, K32(0x7FFFFFFF)); break;
                    default: // VMLA/VMLS: d +/- round(n * m); a denormal product reads as zero (DAZ) like the handler's FRes
                        a.mulps(X0, X1);
                        TinyGuard(X0, q);
                        LdV(X1, dOff, q);
                        if (kind == VecKind.MlaF) a.addps(X1, X0); else a.subps(X1, X0);
                        Mov(X0, X1);
                        break;
                }
                FRes(X0);
                StV(dOff, X0, q);
                return;
            case VecKind.FmaF: case VecKind.FmsF:
                NeonFp();
                LdV(X0, nOff, q); LdV(X1, mOff, q); LdV(X2, dOff, q);
                if (kind == VecKind.FmsF) a.pxor(X0, K32(0x80000000));
                a.vfmadd231ps(X2, X0, X1);
                TinyGuard(X2, q);
                FRes(X2);
                StV(dOff, X2, q);
                return;
            case VecKind.PaddF: // D only: [n0+n1, m0+m1]
                NeonFp();
                LdPair(X0, X1, nOff, mOff);
                a.haddps(X0, X0);
                FRes(X0);
                StV(dOff, X0, false);
                return;
            case VecKind.CeqF: case VecKind.CgeF: case VecKind.CgtF: case VecKind.AcgeF: case VecKind.AcgtF:
                NeonFp();
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                if (kind is VecKind.AcgeF or VecKind.AcgtF)
                {
                    a.movdqa(X2, K32(0x7FFFFFFF));
                    a.pand(X0, X2); a.pand(X1, X2);
                }
                switch (kind)
                {
                    case VecKind.CeqF: a.cmpeqps(X0, X1); break;
                    case VecKind.CgeF: case VecKind.AcgeF: a.cmpleps(X1, X0); Mov(X0, X1); break;
                    default: a.cmpltps(X1, X0); Mov(X0, X1); break;
                }
                StV(dOff, X0, q);
                return;
            case VecKind.MaxF: case VecKind.MinF:
                NeonFp();
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                MaxMinF(kind == VecKind.MaxF);
                StV(dOff, X0, q);
                return;
            case VecKind.PmaxF: case VecKind.PminF: // D only
                NeonFp();
                LdPair(X2, X1, nOff, mOff);   // [n0 n1 m0 m1]
                Mov(X0, X2);
                a.shufps(X0, X2, 0x88);      // [n0 m0 n0 m0]
                Mov(X1, X2);
                a.shufps(X1, X2, 0xDD);      // [n1 m1 n1 m1]
                MaxMinF(kind == VecKind.PmaxF);
                StV(dOff, X0, false);
                return;
            case VecKind.RecpsF: case VecKind.RsqrtsF:
            {
                // 2 - n*m (VRECPS) / (3 - n*m) / 2 (VRSQRTS); infinity * zero gives exactly 2 / 1.5
                bool rs = kind == VecKind.RsqrtsF;
                NeonFp();
                LdV(X0, nOff, q); LdV(X1, mOff, q);
                Mov(X2, X0);
                a.mulps(X2, X1);              // p
                Mov(X3, X0);
                a.cmpordps(X3, X1);           // neither operand NaN
                Mov(X4, X2);
                a.cmpunordps(X4, X2);         // p NaN
                a.pand(X3, X4);               // infinity * zero
                a.movdqa(X0, K32(F32(rs ? 3f : 2f)));
                a.subps(X0, X2);
                if (rs) a.mulps(X0, K32(F32(0.5f)));
                a.movdqa(X1, K32(F32(rs ? 1.5f : 2f)));
                Blend(X0, X3, X1);
                FRes(X0);
                StV(dOff, X0, q);
                return;
            }

            // ---------------------------------------------------------- by scalar
            case VecKind.MulFS: case VecKind.MlaFS: case VecKind.MlsFS:
                NeonFp();
                LdV(X0, nOff, q);
                Broadcast(X1, aux, 2);
                a.mulps(X0, X1);
                TinyGuard(X0, q);
                if (kind != VecKind.MulFS)
                {
                    LdV(X1, dOff, q);
                    if (kind == VecKind.MlaFS) a.addps(X1, X0); else a.subps(X1, X0);
                    Mov(X0, X1);
                }
                FRes(X0);
                StV(dOff, X0, q);
                return;
            case VecKind.MulIS: case VecKind.MlaIS: case VecKind.MlsIS:
                LdV(X0, nOff, q);
                Broadcast(X1, aux, size);
                PMul(size, X0, X1, X2, X3);
                if (kind != VecKind.MulIS)
                {
                    LdV(X1, dOff, q);
                    if (kind == VecKind.MlaIS) PAdd(size, X1, X0); else PSub(size, X1, X0);
                    Mov(X0, X1);
                }
                StV(dOff, X0, q);
                return;

            // ---------------------------------------------------------- shifts by immediate
            case VecKind.ShrImm:
                LdV(X0, mOff, q);
                if (round) RoundShr(X0, size, aux, !u, X1, X2); else Shr(X0, size, aux, !u, X1);
                if (acc) { LdV(X1, dOff, q); PAdd(size, X0, X1); }
                StV(dOff, X0, q);
                return;
            case VecKind.ShlImm:
                LdV(X0, mOff, q);
                ShlI(X0, size, aux);
                StV(dOff, X0, q);
                return;
            case VecKind.SriImm: case VecKind.SliImm:
            {
                ulong mask = LaneMask(size);
                ulong keep = kind == VecKind.SriImm
                    ? (aux >= bits ? mask : ~(mask >> aux) & mask)
                    : ~(mask << aux) & mask;
                LdV(X0, mOff, q);
                if (kind == VecKind.SriImm) ShrU(X0, size, aux); else ShlI(X0, size, aux);
                LdV(X1, dOff, q);
                a.pand(X1, KLane(size, keep));
                a.por(X0, X1);
                StV(dOff, X0, q);
                return;
            }
            case VecKind.ShrnImm: // Q source, D result, truncating
            {
                uint w = size + 1;
                LdV(X0, mOff, true);
                if (round) RoundShr(X0, w, aux, false, X1, X2); else ShrU(X0, w, aux);
                NarrowTrunc(X0, w);
                StV(dOff, X0, false);
                return;
            }
            case VecKind.QshrnImm: case VecKind.QshrunImm:
            {
                uint w = size + 1;
                bool srcSigned = kind == VecKind.QshrunImm || !u;
                LdV(X0, mOff, true);
                if (round) RoundShr(X0, w, aux, srcSigned, X1, X2); else Shr(X0, w, aux, srcSigned, X1);
                Mov(X1, X0);
                if (kind == VecKind.QshrunImm)
                {
                    // signed source, unsigned saturation
                    if (w == 1) { a.packuswb(X1, X1); a.pmovzxbw(X2, X1); a.pcmpeqw(X2, X0); }
                    else { a.packusdw(X1, X1); a.pmovzxwd(X2, X1); a.pcmpeqd(X2, X0); }
                }
                else if (!u)
                {
                    if (w == 1) { a.packsswb(X1, X1); a.pmovsxbw(X2, X1); a.pcmpeqw(X2, X0); }
                    else { a.packssdw(X1, X1); a.pmovsxwd(X2, X1); a.pcmpeqd(X2, X0); }
                }
                else
                {
                    // unsigned: clamp to the narrow maximum, then truncate
                    if (w == 1) { a.pminuw(X1, K16(0xFF)); Mov(X2, X1); a.pcmpeqw(X2, X0); a.packuswb(X1, X1); }
                    else { a.pminud(X1, K32(0xFFFF)); Mov(X2, X1); a.pcmpeqd(X2, X0); a.packusdw(X1, X1); }
                }
                QcIfNotAll(X2, true);
                StV(dOff, X1, false);
                return;
            }
            case VecKind.ShllImm: // D source, Q result
                LdV(X1, mOff, false);
                Widen(X0, X1, size, u);
                ShlI(X0, size + 1, aux);
                StV(dOff, X0, true);
                return;

            // ---------------------------------------------------------- two registers, miscellaneous
            case VecKind.Rev:
            {
                int eb = 1 << (int)size, per = aux / eb;
                var m = new byte[16];
                for (int j = 0; j < 16; j++)
                {
                    int e = j / eb, gbase = e - e % per;
                    m[j] = (byte)((gbase + per - 1 - e % per) * eb + j % eb);
                }
                LdV(X0, mOff, q);
                a.pshufb(X0, KBytes(m));
                StV(dOff, X0, q);
                return;
            }
            case VecKind.Mvn:
                LdV(X0, mOff, q); Not(X0, X1); StV(dOff, X0, q); return;
            case VecKind.AbsF:
                LdV(X0, mOff, q); a.pand(X0, K32(0x7FFFFFFF)); StV(dOff, X0, q); return;
            case VecKind.NegF:
                LdV(X0, mOff, q); a.pxor(X0, K32(0x80000000)); StV(dOff, X0, q); return;
            case VecKind.AbsI:
                LdV(X0, mOff, q);
                switch (size) { case 0: a.pabsb(X0, X0); break; case 1: a.pabsw(X0, X0); break; default: a.pabsd(X0, X0); break; }
                StV(dOff, X0, q);
                return;
            case VecKind.NegI:
                LdV(X1, mOff, q); a.pxor(X0, X0); PSub(size, X0, X1); StV(dOff, X0, q); return;
            case VecKind.CmpZeroI:
                LdV(X0, mOff, q);
                a.pxor(X1, X1);
                switch (aux)
                {
                    case 0: PCmpGt(size, X0, X1); break;                         // x > 0
                    case 1: PCmpGt(size, X1, X0); Not(X1, X2); Mov(X0, X1); break; // x >= 0 == !(0 > x)
                    case 2: PCmpEq(size, X0, X1); break;
                    case 3: PCmpGt(size, X0, X1); Not(X0, X2); break;           // x <= 0 == !(x > 0)
                    default: PCmpGt(size, X1, X0); Mov(X0, X1); break;          // x < 0
                }
                StV(dOff, X0, q);
                return;
            case VecKind.CmpZeroF:
                NeonFp();
                LdV(X0, mOff, q);
                a.xorps(X1, X1);
                switch (aux)
                {
                    case 0: a.cmpltps(X1, X0); Mov(X0, X1); break;  // 0 < x
                    case 1: a.cmpleps(X1, X0); Mov(X0, X1); break;  // 0 <= x
                    case 2: a.cmpeqps(X0, X1); break;
                    case 3: a.cmpleps(X0, X1); break;               // x <= 0
                    default: a.cmpltps(X0, X1); break;              // x < 0
                }
                StV(dOff, X0, q);
                return;
            case VecKind.Swp:
                LdV(X0, dOff, q); LdV(X1, mOff, q);
                StV(mOff, X0, q); StV(dOff, X1, q);
                return;
            case VecKind.Trn: case VecKind.Uzp: case VecKind.Zip:
            {
                // byte sources of the new d and new m: 0-15 = old d, 16-31 = old m (the handlers store m first, then d)
                int eb = 1 << (int)size, bytes = q ? 16 : 8, count = bytes / eb;
                var nd = new byte[16]; var nm = new byte[16];
                Array.Fill(nd, (byte)0x80); Array.Fill(nm, (byte)0x80);
                byte Src(int globalElem, int k) => (byte)(globalElem < count ? globalElem * eb + k : 16 + (globalElem - count) * eb + k);
                for (int i = 0; i < count; i++)
                    for (int k = 0; k < eb; k++)
                    {
                        int j = i * eb + k;
                        switch (kind)
                        {
                            case VecKind.Trn:
                                nd[j] = (i & 1) == 0 ? Src(i, k) : Src(count + i - 1, k);
                                nm[j] = (i & 1) == 0 ? Src(i + 1, k) : Src(count + i, k);
                                break;
                            case VecKind.Uzp:
                                nd[j] = Src(2 * i, k);
                                nm[j] = Src(2 * i + 1, k);
                                break;
                            default:
                            {
                                // all[2e] = d[e], all[2e+1] = m[e]; new d = all[0..count), new m = all[count..2count)
                                int ed = i, em = count + i;
                                nd[j] = (ed & 1) == 0 ? Src(ed / 2, k) : Src(count + ed / 2, k);
                                nm[j] = (em & 1) == 0 ? Src(em / 2, k) : Src(count + em / 2, k);
                                break;
                            }
                        }
                    }
                LdV(X0, dOff, q); LdV(X1, mOff, q);
                Permute(X2, X0, X1, X4, nm);
                Permute(X3, X0, X1, X4, nd);
                StV(mOff, X2, q);
                StV(dOff, X3, q);
                return;
            }
            case VecKind.Movn:
                LdV(X0, mOff, true);
                NarrowTrunc(X0, size + 1);
                StV(dOff, X0, false);
                return;
            case VecKind.Paddl: // pairs of size lanes -> one lane of size + 1 (exact: the sums never wrap)
                LdV(X0, mOff, q);
                switch (size)
                {
                    case 0:
                        if (u) a.pmaddubsw(X0, K8(1));                       // unsigned bytes * signed 1
                        else { a.movdqa(X1, K8(1)); a.pmaddubsw(X1, X0); Mov(X0, X1); } // signed bytes * unsigned 1
                        break;
                    case 1:
                        if (u)
                        {
                            a.pxor(X0, K16(0x8000));                          // (a - 2^15) + (b - 2^15) = a + b - 2^16
                            a.pmaddwd(X0, K16(1));
                            a.paddd(X0, K32(0x10000));
                        }
                        else a.pmaddwd(X0, K16(1));
                        break;
                    default:
                        a.pshufd(X1, X0, 0x08);                               // even lanes [0, 2]
                        a.pshufd(X2, X0, 0x0D);                               // odd lanes [1, 3]
                        Widen(X0, X1, 2, u);
                        Widen(X3, X2, 2, u);
                        a.paddq(X0, X3);
                        break;
                }
                if (acc) { LdV(X1, dOff, q); PAdd(size + 1, X0, X1); }
                StV(dOff, X0, q);
                return;
            case VecKind.Clz:
            {
                // bytes: clz8 = clz4(hi) + (hi == 0 ? clz4(lo) : 0); wider lanes combine the two halves the same way
                var lut = new byte[16];
                for (int i = 0; i < 16; i++) lut[i] = (byte)(i == 0 ? 4 : System.Numerics.BitOperations.LeadingZeroCount((uint)i) - 28);
                LdV(X0, mOff, q);
                a.movdqa(X3, K8(0x0F));
                Mov(X1, X0);
                a.psrlw(X1, 4);
                a.pand(X1, X3);                // hi nibbles
                a.pand(X0, X3);                // lo nibbles
                a.movdqa(X2, KBytes(lut));
                Mov(X4, X2);
                a.pshufb(X2, X1);              // clz4(hi)
                a.pshufb(X4, X0);              // clz4(lo)
                a.pxor(X3, X3);
                a.pcmpeqb(X1, X3);             // hi == 0
                a.pand(X4, X1);
                a.paddb(X2, X4);               // clz8 per byte
                for (uint s = 1; s <= size; s++)
                {
                    Mov(X1, X2);
                    if (s == 1) a.psrlw(X1, 8); else a.psrld(X1, 16);              // clz of the high half
                    Mov(X4, X1);
                    if (s == 1) a.pcmpeqw(X4, K16(8)); else a.pcmpeqd(X4, K32(16)); // high half all zero
                    a.pand(X2, s == 1 ? K16(0x00FF) : K32(0xFFFF));               // clz of the low half
                    a.pand(X2, X4);
                    if (s == 1) a.paddw(X2, X1); else a.paddd(X2, X1);
                }
                StV(dOff, X2, q);
                return;
            }
            case VecKind.Cnt: // bytes: nibble popcount table
            {
                var lut = new byte[16];
                for (int i = 0; i < 16; i++) lut[i] = (byte)System.Numerics.BitOperations.PopCount((uint)i);
                LdV(X0, mOff, q);
                a.movdqa(X3, K8(0x0F));
                Mov(X1, X0);
                a.psrlw(X1, 4);
                a.pand(X0, X3);
                a.pand(X1, X3);
                a.movdqa(X2, KBytes(lut));
                Mov(X3, X2);
                a.pshufb(X2, X0);
                a.pshufb(X3, X1);
                a.paddb(X2, X3);
                StV(dOff, X2, q);
                return;
            }
            case VecKind.RecpeF: case VecKind.RsqrteF: case VecKind.RecpeU: case VecKind.RsqrteU:
            {
                int lanes = q ? 4 : 2;
                for (int i = 0; i < lanes; i++)
                {
                    a.mov(eax, __dword_ptr[rbx + OffD + mOff + 4 * i]);
                    EstimateLane(kind);
                    a.mov(__dword_ptr[rbx + OffD + dOff + 4 * i], eax);
                }
                return;
            }
            case VecKind.CvtS2F:
                LdV(X0, mOff, q); a.cvtdq2ps(X0, X0); StV(dOff, X0, q); return;
            case VecKind.CvtU2F: // hi16 * 65536 + lo16: both exact, one rounding
                LdV(X0, mOff, q);
                Mov(X1, X0);
                a.psrld(X1, 16);
                a.pand(X0, K32(0xFFFF));
                a.cvtdq2ps(X0, X0);
                a.cvtdq2ps(X1, X1);
                a.mulps(X1, K32(F32(65536f)));
                a.addps(X0, X1);
                StV(dOff, X0, q);
                return;
            case VecKind.CvtF2S: // truncate, saturate, NaN -> 0
                NeonFp();
                LdV(X0, mOff, q);
                a.movdqa(X2, K32(F32(2147483648f)));
                a.cmpleps(X2, X0);            // x >= 2^31
                Mov(X3, X0);
                a.cmpordps(X3, X3);           // not NaN
                a.cvttps2dq(X0, X0);
                a.pxor(X0, X2);               // 0x80000000 -> 0x7FFFFFFF for positive overflow
                a.pand(X0, X3);
                StV(dOff, X0, q);
                return;
            case VecKind.CvtF2U: // truncate, negative and NaN -> 0, >= 2^32 -> 0xFFFFFFFF
                NeonFp();
                LdV(X0, mOff, q);
                a.movdqa(X2, K32(F32(2147483648f)));
                a.cmpleps(X2, X0);            // big: x >= 2^31
                Mov(X1, X2);
                a.pand(X1, K32(F32(2147483648f)));
                Mov(X3, X0);
                a.subps(X3, X1);              // big lanes: x - 2^31 (exact)
                a.cvttps2dq(X3, X3);
                a.pand(X2, K32(0x80000000));
                a.pxor(X3, X2);               // add 2^31 back
                a.movdqa(X1, K32(F32(4294967296f)));
                a.cmpleps(X1, X0);            // x >= 2^32
                a.por(X3, X1);
                a.xorps(X2, X2);
                a.cmpltps(X2, X0);            // 0 < x (false for NaN): keep
                a.pand(X3, X2);
                StV(dOff, X3, q);
                return;
            case VecKind.CvtF2H: // Q source, D result; FZ inputs, round to nearest, NaN -> 0x7E00
                NeonFp();
                LdV(X0, mOff, true);
                Mov(X1, X0);
                a.cmpunordps(X1, X1);
                a.vcvtps2ph(X0, X0, 0);
                a.packssdw(X1, X1);
                a.movdqa(X2, K16(0x7E00));
                Blend(X0, X1, X2);
                StV(dOff, X0, false);
                return;
            case VecKind.CvtH2F: // D source, Q result
                NeonFp();
                LdV(X0, mOff, false);
                a.vcvtph2ps(X0, X0);
                FRes(X0);
                StV(dOff, X0, true);
                return;
            case VecKind.Ext:
                if (q)
                {
                    LdV(X0, mOff, true); LdV(X1, nOff, true);
                    if (aux != 0) a.palignr(X0, X1, (byte)aux); else Mov(X0, X1);
                }
                else
                {
                    LdPair(X0, X1, nOff, mOff);
                    if (aux != 0) a.psrldq(X0, (byte)aux);
                }
                StV(dOff, X0, q);
                return;
            case VecKind.Tbl:
            {
                int len = aux & 0xF;
                bool tbx = (aux & 0x10) != 0;
                int n = nOff / 8;
                LdV(X0, mOff, false);         // indices
                a.pxor(X4, X4);               // result
                for (int c = 0; c * 2 < len; c++)
                {
                    int regs = Math.Min(2, len - 2 * c);
                    a.movq(X1, __qword_ptr[rbx + OffD + ((n + 2 * c) & 31) * 8]);
                    if (regs == 2) { a.movq(X2, __qword_ptr[rbx + OffD + ((n + 2 * c + 1) & 31) * 8]); a.punpcklqdq(X1, X2); }
                    Mov(X3, X0);
                    if (c > 0) a.psubb(X3, K8((uint)(16 * c)));
                    a.movdqa(X5, X3);
                    a.pminub(X5, K8((uint)(8 * regs - 1)));
                    a.pcmpeqb(X5, X3);        // index inside this chunk
                    a.pshufb(X1, X3);
                    a.pand(X1, X5);
                    a.por(X4, X1);
                }
                if (tbx)
                {
                    a.movdqa(X5, X0);
                    a.pminub(X5, K8((uint)(8 * len - 1)));
                    a.pcmpeqb(X5, X0);        // index inside the table
                    LdV(X1, dOff, false);
                    a.pandn(X5, X1);
                    a.por(X4, X5);
                }
                StV(dOff, X4, false);
                return;
            }
            default: throw new NotSupportedException($"vector kind {kind}");
        }
    }

    /// <summary>
    /// Handler FMax/FMin on xmm0 (a) and xmm1 (b), result in xmm0: flushed inputs, NaN -> default NaN, +0 beats -0 for
    /// max (and -0 for min).
    /// </summary>
    void MaxMinF(bool max)
    {
        var a = _a;
        FlushIn(X0);
        FlushIn(X1);
        Mov(X2, X0);
        if (max) a.maxps(X2, X1); else a.minps(X2, X1);
        Mov(X3, X0);
        a.cmpeqps(X3, X1);                // equal (incl. +0/-0)
        Mov(X4, X0);
        if (max) a.pand(X4, X1); else a.por(X4, X1);
        Blend(X2, X3, X4);
        Mov(X3, X0);
        a.cmpunordps(X3, X1);
        a.movdqa(X4, K32(0x7FC00000));
        Blend(X2, X3, X4);
        Mov(X0, X2);
    }

    /// <summary>VQADD/VQSUB (signed and unsigned, all lane sizes) with QC.</summary>
    void SatAddSub(bool sub, uint size, bool u, bool q, int dOff, int nOff, int mOff)
    {
        var a = _a;
        LdV(X0, nOff, q); LdV(X1, mOff, q);
        if (size <= 1)
        {
            Mov(X2, X0);
            if (sub) PSub(size, X2, X1); else PAdd(size, X2, X1); // wrapped result
            if (size == 0)
            {
                if (sub) { if (u) a.psubusb(X0, X1); else a.psubsb(X0, X1); }
                else { if (u) a.paddusb(X0, X1); else a.paddsb(X0, X1); }
            }
            else
            {
                if (sub) { if (u) a.psubusw(X0, X1); else a.psubsw(X0, X1); }
                else { if (u) a.paddusw(X0, X1); else a.paddsw(X0, X1); }
            }
            a.pcmpeqb(X2, X0);
            QcIfNotAll(X2, q);
            StV(dOff, X0, q);
            return;
        }
        Mov(X2, X0);
        if (sub) PSub(size, X2, X1); else PAdd(size, X2, X1);     // r
        if (!u)
        {
            // overflow: add (x^r) & (y^r), sub (x^y) & (x^r); saturated value = (x >> top) ^ max
            Mov(X3, X0);
            a.pxor(X3, X2);
            if (sub) { Mov(X4, X0); a.pxor(X4, X1); } else { Mov(X4, X1); a.pxor(X4, X2); }
            a.pand(X3, X4);
            SignMask(X3, size);                                // overflow lanes
            Mov(X4, X0);
            SignMask(X4, size);
            a.pxor(X4, KLane(size, size == 2 ? 0x7FFFFFFFul : 0x7FFFFFFFFFFFFFFFul));
            Mov(X5, X3);
            Blend(X2, X5, X4);
        }
        else
        {
            // unsigned: add overflows when r < x, sub when x < y (biased signed compares)
            a.movdqa(X5, KLane(size, size == 2 ? 0x80000000ul : 0x8000000000000000ul));
            if (sub) { Mov(X3, X1); Mov(X4, X0); } else { Mov(X3, X0); Mov(X4, X2); }
            a.pxor(X3, X5); a.pxor(X4, X5);
            PCmpGt(size, X3, X4);                              // overflow lanes
            if (sub) a.pandn(X3, X2); else a.por(X3, X2);
            Mov(X4, X3);
            Mov(X3, X2);
            a.pcmpeqd(X3, X4);                                 // unchanged lanes
            Mov(X2, X4);
            QcIfNotAll(X3, q);
            StV(dOff, X2, q);
            return;
        }
        QcIfAny(X3, q);
        StV(dOff, X2, q);
    }

    /// <summary>Each lane of r becomes all ones when its sign bit is set (32- or 64-bit lanes).</summary>
    void SignMask(AssemblerRegisterXMM r, uint size)
    {
        _a.psrad(r, 31);
        if (size == 3) _a.pshufd(r, r, 0xF5);
    }
}

static class IntExt
{
    public static int AsInt(this uint v) => unchecked((int)v);
}
