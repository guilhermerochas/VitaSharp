using Iced.Intel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
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
    private static readonly bool s_hasSse42 = Sse42.IsSupported;
    private static readonly bool s_hasAvx2 = Avx2.IsSupported;
    private static readonly bool s_hasFma = Fma.IsSupported;
    private static readonly bool s_hasF16c = Avx.IsSupported && (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0;

    /// <summary>Whether <paramref name="k"/> (at lane <paramref name="size"/>) has an inline lowering on this host.</summary>
    public static bool SupportsVec(VecKind k, uint size = 2)
    {
        if (!NeonSupported)
        {
            return false;
        }

        return k switch
        {
            VecKind.FmaF or VecKind.FmsF => s_hasFma,
            VecKind.ShlRegI => size == 3 || (size == 2 && s_hasAvx2), // 8/16-bit lanes: no variable SSE shifts (callouts)
            VecKind.CvtF2H or VecKind.CvtH2F => s_hasF16c,
            VecKind.QaddI or VecKind.QsubI => size < 3 || s_hasSse42,
            _ => true,
        };
    }

    private bool _neonMxcsr;

    private static readonly AssemblerRegisterXMM s_x0 = xmm0, s_x1 = xmm1, s_x2 = xmm2, s_x3 = xmm3, s_x4 = xmm4, s_x5 = xmm5;

    // ------------------------------------------------------------------ constants

    private static readonly ConcurrentDictionary<(ulong, ulong), ulong> s_pool = new();

    /// <summary>Address of a 16-byte aligned constant (allocated once per value, never freed: the set is small).</summary>
    private static ulong ConstAddr(ulong lo, ulong hi) => s_pool.GetOrAdd((lo, hi), static k =>
    {
        ulong* p = (ulong*)NativeMemory.AlignedAlloc(16, 16);
        p[0] = k.Item1;
        p[1] = k.Item2;
        return (ulong)p;
    });

    /// <summary>Memory operand for a constant (clobbers rax).</summary>
    private AssemblerMemoryOperand K(ulong lo, ulong hi)
    {
        Asm.mov(rax, ConstAddr(lo, hi));
        return __xmmword_ptr[rax];
    }
    private AssemblerMemoryOperand K64(ulong v) => K(v, v);
    private AssemblerMemoryOperand K32(uint v) => K64(v | ((ulong)v << 32));
    private AssemblerMemoryOperand K16(uint v) => K64((v & 0xFFFF) * 0x0001000100010001ul);
    private AssemblerMemoryOperand K8(uint v) => K64((v & 0xFF) * 0x0101010101010101ul);
    private AssemblerMemoryOperand KLane(uint size, ulong v) => size switch
    {
        0 => K8((uint)v),
        1 => K16((uint)v),
        2 => K32((uint)v),
        _ => K64(v),
    };
    private AssemblerMemoryOperand KBytes(byte[] b16) => K(BitConverter.ToUInt64(b16, 0), BitConverter.ToUInt64(b16, 8));

    private static ulong LaneMask(uint size) => size == 3 ? ulong.MaxValue : (1ul << (8 << (int)size)) - 1;
    private static uint F32(float f) => BitConverter.SingleToUInt32Bits(f);

    private void NeonFp()
    {
        if (_neonMxcsr)
        {
            return;
        }

        Asm.ldmxcsr(State(s_offMxcsrNeon));
        _neonMxcsr = true;
    }

    // ------------------------------------------------------------------ operand helpers

    private static int Reg8(int off) => s_offD + off;
    private void LdV(AssemblerRegisterXMM r, int off, bool q)
    {
        if (q)
        {
            Asm.movdqu(r, __xmmword_ptr[rbx + s_offD + off]);
        }
        else
        {
            Asm.movq(r, __qword_ptr[rbx + s_offD + off]);
        }
    }
    private void StV(int off, AssemblerRegisterXMM r, bool q)
    {
        if (q)
        {
            Asm.movdqu(__xmmword_ptr[rbx + s_offD + off], r);
        }
        else
        {
            Asm.movq(__qword_ptr[rbx + s_offD + off], r);
        }
    }
    private void Mov(AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        if (d != s)
        {
            Asm.movdqa(d, s);
        }
    }

    private void PAdd(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size)
        {
            case 0:
                Asm.paddb(d, s);
                break;
            case 1:
                Asm.paddw(d, s);
                break;
            case 2:
                Asm.paddd(d, s);
                break;
            default:
                Asm.paddq(d, s);
                break;
        }
    }

    private void PSub(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size)
        {
            case 0:
                Asm.psubb(d, s);
                break;
            case 1:
                Asm.psubw(d, s);
                break;
            case 2:
                Asm.psubd(d, s);
                break;
            default:
                Asm.psubq(d, s);
                break;
        }
    }

    private void PCmpEq(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size)
        {
            case 0:
                Asm.pcmpeqb(d, s);
                break;
            case 1:
                Asm.pcmpeqw(d, s);
                break;
            case 2:
                Asm.pcmpeqd(d, s);
                break;
            default:
                Asm.pcmpeqq(d, s);
                break;
        }
    }

    /// <summary>Signed d > s per lane (64-bit lanes need SSE4.2).</summary>
    private void PCmpGt(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size)
        {
            case 0:
                Asm.pcmpgtb(d, s);
                break;
            case 1:
                Asm.pcmpgtw(d, s);
                break;
            case 2:
                Asm.pcmpgtd(d, s);
                break;
            default:
                Asm.pcmpgtq(d, s);
                break;
        }
    }

    private void PMax(uint size, bool unsigned, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size)
        {
            case 0:
                if (unsigned)
                {
                    Asm.pmaxub(d, s);
                }
                else
                {
                    Asm.pmaxsb(d, s);
                }

                break;
            case 1:
                if (unsigned)
                {
                    Asm.pmaxuw(d, s);
                }
                else
                {
                    Asm.pmaxsw(d, s);
                }

                break;
            default:
                if (unsigned)
                {
                    Asm.pmaxud(d, s);
                }
                else
                {
                    Asm.pmaxsd(d, s);
                }

                break;
        }
    }

    private void PMin(uint size, bool unsigned, AssemblerRegisterXMM d, AssemblerRegisterXMM s)
    {
        switch (size)
        {
            case 0:
                if (unsigned)
                {
                    Asm.pminub(d, s);
                }
                else
                {
                    Asm.pminsb(d, s);
                }

                break;
            case 1:
                if (unsigned)
                {
                    Asm.pminuw(d, s);
                }
                else
                {
                    Asm.pminsw(d, s);
                }

                break;
            default:
                if (unsigned)
                {
                    Asm.pminud(d, s);
                }
                else
                {
                    Asm.pminsd(d, s);
                }

                break;
        }
    }

    private void PMul(uint size, AssemblerRegisterXMM d, AssemblerRegisterXMM s, AssemblerRegisterXMM t1, AssemblerRegisterXMM t2)
    {
        switch (size)
        {
            case 0: // bytes: even bytes from one 16-bit product, odd bytes from another
                Mov(t1, d);
                Mov(t2, s);
                Asm.pmullw(d, s);
                Asm.pand(d, K16(0x00FF));
                Asm.psrlw(t1, 8);
                Asm.psrlw(t2, 8);
                Asm.pmullw(t1, t2);
                Asm.psllw(t1, 8);
                Asm.por(d, t1);
                break;
            case 1:
                Asm.pmullw(d, s);
                break;
            default:
                Asm.pmulld(d, s);
                break;
        }
    }

    private void Not(AssemblerRegisterXMM r, AssemblerRegisterXMM t)
    {
        Asm.pcmpeqd(t, t);
        Asm.pxor(r, t);
    }

    /// <summary>r = ~mask &amp; r | mask &amp; value (mask lanes all ones or zero); mask is clobbered.</summary>
    private void Blend(AssemblerRegisterXMM r, AssemblerRegisterXMM mask, AssemblerRegisterXMM value)
    {
        Asm.pand(value, mask);
        Asm.pandn(mask, r);
        Asm.por(mask, value);
        Mov(r, mask);
    }

    // shifts by an immediate count; counts at or beyond the lane width give 0 (logical) or the sign (arithmetic)
    private void ShlI(AssemblerRegisterXMM r, uint size, int n)
    {
        if (n == 0)
        {
            return;
        }

        if (n >= (8 << (int)size))
        {
            Asm.pxor(r, r);
            return;
        }
        switch (size)
        {
            case 0:
                Asm.psllw(r, (byte)n);
                Asm.pand(r, K8((0xFFu << n) & 0xFF));
                break;
            case 1:
                Asm.psllw(r, (byte)n);
                break;
            case 2:
                Asm.pslld(r, (byte)n);
                break;
            default:
                Asm.psllq(r, (byte)n);
                break;
        }
    }

    private void ShrU(AssemblerRegisterXMM r, uint size, int n)
    {
        if (n == 0)
        {
            return;
        }

        if (n >= (8 << (int)size))
        {
            Asm.pxor(r, r);
            return;
        }
        switch (size)
        {
            case 0:
                Asm.psrlw(r, (byte)n);
                Asm.pand(r, K8(0xFFu >> n));
                break;
            case 1:
                Asm.psrlw(r, (byte)n);
                break;
            case 2:
                Asm.psrld(r, (byte)n);
                break;
            default:
                Asm.psrlq(r, (byte)n);
                break;
        }
    }

    private void ShrS(AssemblerRegisterXMM r, uint size, int n, AssemblerRegisterXMM t)
    {
        int bits = 8 << (int)size;
        n = Math.Min(n, bits - 1);
        if (n == 0)
        {
            return;
        }

        switch (size)
        {
            case 1:
                Asm.psraw(r, (byte)n);
                break;
            case 2:
                Asm.psrad(r, (byte)n);
                break;
            default:
            {
                // logical shift, then sign-extend from the shifted sign position: (x ^ m) - m
                ShrU(r, size, n);
                ulong m = 1ul << (bits - 1 - n);
                Asm.movdqa(t, KLane(size, m));
                Asm.pxor(r, t);
                PSub(size, r, t);
                break;
            }
        }
    }

    private void Shr(AssemblerRegisterXMM r, uint size, int n, bool signed, AssemblerRegisterXMM t)
    {
        if (signed)
        {
            ShrS(r, size, n, t);
        }
        else
        {
            ShrU(r, size, n);
        }
    }

    /// <summary>r = (r >> n) with ARM rounding (adds bit n-1 of the operand): (x + 2^(n-1)) >> n without overflow.</summary>
    private void RoundShr(AssemblerRegisterXMM r, uint size, int n, bool signed, AssemblerRegisterXMM t, AssemblerRegisterXMM t2)
    {
        Mov(t, r);
        ShrU(t, size, n - 1);
        Asm.pand(t, KLane(size, 1));
        Shr(r, size, n, signed, t2);
        PAdd(size, r, t);
    }

    /// <summary>Truncating narrow of the wide lanes (size = wide lane size) into the low 8 bytes.</summary>
    private void NarrowTrunc(AssemblerRegisterXMM r, uint wsize)
    {
        switch (wsize)
        {
            case 1:
                Asm.pand(r, K16(0x00FF));
                Asm.packuswb(r, r);
                break;
            case 2:
                Asm.pand(r, K32(0xFFFF));
                Asm.packusdw(r, r);
                break;
            default:
                Asm.pshufd(r, r, 0x08);
                break;
        }
    }

    private void Widen(AssemblerRegisterXMM d, AssemblerRegisterXMM s, uint size, bool unsigned)
    {
        switch (size)
        {
            case 0:
                if (unsigned)
                {
                    Asm.pmovzxbw(d, s);
                }
                else
                {
                    Asm.pmovsxbw(d, s);
                }

                break;
            case 1:
                if (unsigned)
                {
                    Asm.pmovzxwd(d, s);
                }
                else
                {
                    Asm.pmovsxwd(d, s);
                }

                break;
            default:
                if (unsigned)
                {
                    Asm.pmovzxdq(d, s);
                }
                else
                {
                    Asm.pmovsxdq(d, s);
                }

                break;
        }
    }

    /// <summary>Sets FPSCR.QC when any byte of <paramref name="mask"/> is non-zero (D forms: low 8 bytes).</summary>
    private void QcIfAny(AssemblerRegisterXMM mask, bool q)
    {
        Label skip = Asm.CreateLabel();
        Asm.pmovmskb(eax, mask);
        if (!q)
        {
            Asm.and(eax, 0xFF);
        }

        Asm.test(eax, eax);
        Asm.jz(skip);
        Asm.or(State(s_offFpscr), 0x08000000);
        Place(ref skip);
    }

    /// <summary>Sets FPSCR.QC when <paramref name="eqMask"/> (all-ones lanes = no saturation) has a zero byte.</summary>
    private void QcIfNotAll(AssemblerRegisterXMM eqMask, bool q)
    {
        Label skip = Asm.CreateLabel();
        Asm.pmovmskb(eax, eqMask);
        if (!q)
        {
            Asm.and(eax, 0xFF);
            Asm.cmp(eax, 0xFF);
        }
        else
        {
            Asm.cmp(eax, 0xFFFF);
        }

        Asm.je(skip);
        Asm.or(State(s_offFpscr), 0x08000000);
        Place(ref skip);
    }

    /// <summary>Handler FRes on f32 lanes: denormal results -> signed zero, NaN -> default NaN (clobbers xmm4, xmm5).</summary>
    private void FRes(AssemblerRegisterXMM r)
    {
        Assembler assembler = Asm;
        assembler.movdqa(s_x4, r);
        assembler.pand(s_x4, K32(0x7FFFFFFF));
        assembler.movdqa(s_x5, K32(0x00800000));
        assembler.pcmpgtd(s_x5, s_x4);            // |r| < smallest normal (zero or denormal)
        assembler.pcmpgtd(s_x4, K32(0x7F800000)); // NaN
        assembler.pand(s_x5, K32(0x7FFFFFFF));
        assembler.pandn(s_x5, r);               // magnitude of denormals cleared, sign kept
        assembler.movdqa(r, s_x5);
        assembler.movdqa(s_x5, s_x4);
        assembler.pandn(s_x5, r);
        assembler.pand(s_x4, K32(0x7FC00000));
        assembler.por(s_x4, s_x5);
        assembler.movdqa(r, s_x4);
    }

    /// <summary>Handler Ftz on f32 lanes (denormal inputs -> signed zero), for kinds that look at the bits (clobbers xmm4, xmm5).</summary>
    private void FlushIn(AssemblerRegisterXMM r)
    {
        Assembler assembler = Asm;
        assembler.movdqa(s_x4, r);
        assembler.pand(s_x4, K32(0x7FFFFFFF));
        assembler.movdqa(s_x5, K32(0x00800000));
        assembler.pcmpgtd(s_x5, s_x4);
        assembler.pand(s_x5, K32(0x7FFFFFFF));
        assembler.pandn(s_x5, r);
        assembler.movdqa(r, s_x5);
    }

    /// <summary>pshufb masks gathering the even (odd) lanes of a 16-byte vector into its low 8 bytes.</summary>
    private static byte[] PairMask(uint size, bool odd)
    {
        int eb = 1 << (int)size;
        byte[] m = new byte[16];
        Array.Fill(m, (byte)0x80);
        for (int j = 0; j < 8; j++)
        {
            m[j] = (byte)((2 * (j / eb) + (odd ? 1 : 0)) * eb + j % eb);
        }

        return m;
    }

    /// <summary>Loads n (low 8 bytes) and m (high 8 bytes) of a pairwise op into r.</summary>
    private void LdPair(AssemblerRegisterXMM r, AssemblerRegisterXMM t, int nOff, int mOff)
    {
        Asm.movq(r, __qword_ptr[rbx + s_offD + nOff]);
        Asm.movq(t, __qword_ptr[rbx + s_offD + mOff]);
        Asm.punpcklqdq(r, t);
    }

    /// <summary>Broadcasts the scalar element at byte offset <paramref name="off"/> (16 or 32 bits) to every lane of r.</summary>
    private void Broadcast(AssemblerRegisterXMM r, int off, uint size)
    {
        if (size == 2)
        {
            Asm.movd(r, __dword_ptr[rbx + s_offD + off]);
        }
        else
        {
            Asm.movzx(eax, __word_ptr[rbx + s_offD + off]);
            Asm.imul(eax, eax, 0x10001);
            Asm.movd(r, eax);
        }
        Asm.pshufd(r, r, 0);
    }

    /// <summary>
    /// out = pshufb(a, ma) | pshufb(b, mb) where each output byte names its source (0-15: a, 16-31: b, 0x80: zero).
    /// </summary>
    private void Permute(AssemblerRegisterXMM dst, AssemblerRegisterXMM srcA, AssemblerRegisterXMM srcB, AssemblerRegisterXMM t, byte[] src)
    {
        byte[] ma = new byte[16];
        byte[] mb = new byte[16];
        bool anyA = false, anyB = false;
        for (int j = 0; j < 16; j++)
        {
            byte s = src[j];
            ma[j] = s < 16 ? s : (byte)0x80;
            mb[j] = s is >= 16 and < 32 ? (byte)(s - 16) : (byte)0x80;
            anyA |= s < 16;
            anyB |= s is >= 16 and < 32;
        }
        Mov(dst, srcA);
        Asm.pshufb(dst, KBytes(ma));
        if (anyB)
        {
            Mov(t, srcB);
            Asm.pshufb(t, KBytes(mb));
            if (anyA)
            {
                Asm.por(dst, t);
            }
            else
            {
                Mov(dst, t);
            }
        }
        else if (!anyA)
        {
            Asm.pxor(dst, dst);
        }
    }

    // ------------------------------------------------------------------ estimate tables (built from the handlers)

    private static readonly ulong s_recpeTable = BuildTable(256, i => BitConverter.SingleToUInt32Bits(Neon.RecipEstimate(BitConverter.UInt32BitsToSingle(0x3F000000u | ((uint)i << 15)))) & 0x7FFFFF);
    private static readonly ulong s_rsqrteTable = BuildTable(256, i =>
        BitConverter.SingleToUInt32Bits(Neon.RSqrtEstimate(BitConverter.UInt32BitsToSingle((i < 128 ? 126u : 125u) << 23 | ((uint)(i & 127) << 16)))) & 0x7FFFFF);
    private static readonly ulong s_uRecpeTable = BuildTable(256, i => Neon.URecipEstimate(0x80000000u | ((uint)i << 23)));
    private static readonly ulong s_uRsqrteTable = BuildTable(512, i => Neon.URSqrtEstimate((uint)i << 23));

    private static ulong BuildTable(int n, Func<int, uint> f)
    {
        uint* p = (uint*)NativeMemory.AlignedAlloc((nuint)(n * 4), 64);
        for (int i = 0; i < n; i++)
        {
            p[i] = f(i);
        }

        return (ulong)p;
    }

    /// <summary>One lane of VRECPE/VRSQRTE (f32 or u32) in eax (result in eax; clobbers ecx, edx).</summary>
    private void EstimateLane(VecKind kind)
    {
        Assembler assembler = Asm;
        Label done = assembler.CreateLabel();
        switch (kind)
        {
            case VecKind.RecpeF:
            {
                Label nan = assembler.CreateLabel();
                Label zero = assembler.CreateLabel();
                Label inf = assembler.CreateLabel();
                assembler.mov(edx, eax);
                assembler.and(edx, 0x7FFFFFFF);
                assembler.cmp(edx, 0x7F800000);
                assembler.ja(nan);
                assembler.cmp(edx, 0x7E800000);
                assembler.jae(zero);                  // |x| >= 2^126 (and infinity): signed zero
                assembler.cmp(edx, 0x00800000);
                assembler.jb(inf);                    // zero / flushed denormal: signed infinity
                assembler.mov(ecx, edx);
                assembler.shr(ecx, (byte)15);
                assembler.and(ecx, 0xFF);
                assembler.shr(edx, (byte)23);
                assembler.neg(edx);
                assembler.add(edx, 253);
                assembler.shl(edx, (byte)23);
                assembler.and(eax, unchecked((int)0x80000000));
                assembler.or(edx, eax);
                assembler.mov(rax, s_recpeTable);
                assembler.or(edx, __dword_ptr[rax + rcx * 4]);
                assembler.mov(eax, edx);
                assembler.jmp(done);
                Place(ref nan);
                assembler.mov(eax, 0x7FC00000u);
                assembler.jmp(done);
                Place(ref zero);
                assembler.and(eax, unchecked((int)0x80000000));
                assembler.jmp(done);
                Place(ref inf);
                assembler.and(eax, unchecked((int)0x80000000));
                assembler.or(eax, 0x7F800000);
                break;
            }
            case VecKind.RsqrteF:
            {
                Label nan = assembler.CreateLabel();
                Label inf = assembler.CreateLabel();
                Label zero = assembler.CreateLabel();
                assembler.mov(edx, eax);
                assembler.and(edx, 0x7FFFFFFF);
                assembler.cmp(edx, 0x7F800000);
                assembler.ja(nan);
                assembler.cmp(edx, 0x00800000);
                assembler.jb(inf);                    // zero / flushed denormal: signed infinity
                assembler.test(eax, eax);
                assembler.js(nan);                    // negative: default NaN
                assembler.cmp(eax, 0x7F800000);
                assembler.je(zero);                   // +infinity: +0
                assembler.mov(ecx, eax);
                assembler.shr(ecx, (byte)16);
                assembler.and(ecx, 0x7F);
                assembler.mov(edx, eax);
                assembler.shr(edx, (byte)23);         // exponent
                assembler.test(edx, 1);
                assembler.jz(Lbl(out Label even));
                assembler.or(ecx, 0x80);
                Place(ref even);
                assembler.neg(edx);
                assembler.add(edx, 380);
                assembler.shr(edx, (byte)1);
                assembler.shl(edx, (byte)23);
                assembler.mov(rax, s_rsqrteTable);
                assembler.or(edx, __dword_ptr[rax + rcx * 4]);
                assembler.mov(eax, edx);
                assembler.jmp(done);
                Place(ref nan);
                assembler.mov(eax, 0x7FC00000u);
                assembler.jmp(done);
                Place(ref zero);
                assembler.xor(eax, eax);
                assembler.jmp(done);
                Place(ref inf);
                assembler.and(eax, unchecked((int)0x80000000));
                assembler.or(eax, 0x7F800000);
                break;
            }
            case VecKind.RecpeU:
            {
                Label ones = assembler.CreateLabel();
                assembler.test(eax, eax);
                assembler.jns(ones);
                assembler.shr(eax, (byte)23);
                assembler.and(eax, 0xFF);
                assembler.mov(rcx, s_uRecpeTable);
                assembler.mov(eax, __dword_ptr[rcx + rax * 4]);
                assembler.jmp(done);
                Place(ref ones);
                assembler.mov(eax, 0xFFFFFFFFu);
                break;
            }
            default: // RsqrteU
            {
                Label ones = assembler.CreateLabel();
                assembler.test(eax, 0xC0000000u.AsInt());
                assembler.jz(ones);
                assembler.shr(eax, (byte)23);
                assembler.mov(rcx, s_uRsqrteTable);
                assembler.mov(eax, __dword_ptr[rcx + rax * 4]);
                assembler.jmp(done);
                Place(ref ones);
                assembler.mov(eax, 0xFFFFFFFFu);
                break;
            }
        }
        Place(ref done);
    }

    private Label Lbl(out Label label)
    {
        label = Asm.CreateLabel();
        return label;
    }

    // ------------------------------------------------------------------ VecOp

    private Label _tinySlow;

    /// <summary>
    /// One vector op. Kinds flagged <see cref="VecFlags.SlowPath"/> check their rounded products for +/- the smallest
    /// normal (possibly a tiny value rounded up, which ARM flushes) and run the interpreter handler for the whole
    /// instruction in that rare case, before anything was written back.
    /// </summary>
    private void VecOp(IrInst x, Runtime rt)
    {
        Assembler assembler = Asm;
        if ((x.Imm & (uint)VecFlags.SlowPath) == 0)
        {
            VecOpCore(x);
            return;
        }
        _tinySlow = assembler.CreateLabel();
        Label done = assembler.CreateLabel();
        VecOpCore(x);
        assembler.jmp(done);
        Place(ref _tinySlow);
        assembler.ldmxcsr(State(s_offMxcsrHost));
        uint op = x.Imm2 >> 16;
        if (Windows)
        {
            assembler.mov(rcx, rbx);
            assembler.mov(edx, op);
            assembler.mov(r8d, x.Imm3);
            assembler.xor(r9d, r9d);
        }
        else
        {
            assembler.mov(rdi, rbx);
            assembler.mov(esi, op);
            assembler.mov(edx, x.Imm3);
            assembler.xor(ecx, ecx);
        }
        assembler.mov(rax, rt.Callout);
        assembler.call(rax);
        assembler.test(eax, eax);
        assembler.jnz(_abort);
        assembler.ldmxcsr(State(s_offMxcsrNeon));   // the fast path's MXCSR state at the join
        Place(ref done);
    }

    /// <summary>Jumps to the slow path when a lane of r is +/- the smallest normal (clobbers rax, xmm4).</summary>
    private void TinyGuard(AssemblerRegisterXMM r, bool q)
    {
        Assembler assembler = Asm;
        assembler.movdqa(s_x4, r);
        assembler.pand(s_x4, K32(0x7FFFFFFF));
        assembler.pcmpeqd(s_x4, K32(0x00800000));
        assembler.pmovmskb(eax, s_x4);
        if (!q)
        {
            assembler.and(eax, 0xFF);
        }

        assembler.test(eax, eax);
        assembler.jnz(_tinySlow);
    }

    /// <summary>Per-lane count of leading zeros of xmm0 into xmm2 (clobbers xmm0, xmm1, xmm3, xmm4, rax).</summary>
    private void ClzLanes(uint size)
    {
        // bytes: clz8 = clz4(hi) + (hi == 0 ? clz4(lo) : 0); wider lanes combine the two halves the same way
        Assembler assembler = Asm;
        assembler.movdqa(s_x3, K8(0x0F));
        Mov(s_x1, s_x0);
        assembler.psrlw(s_x1, 4);
        assembler.pand(s_x1, s_x3);                // hi nibbles
        assembler.pand(s_x0, s_x3);                // lo nibbles
        assembler.movdqa(s_x2, K(ClzLutLo, ClzLutHi));
        Mov(s_x4, s_x2);
        assembler.pshufb(s_x2, s_x1);              // clz4(hi)
        assembler.pshufb(s_x4, s_x0);              // clz4(lo)
        assembler.pxor(s_x3, s_x3);
        assembler.pcmpeqb(s_x1, s_x3);             // hi == 0
        assembler.pand(s_x4, s_x1);
        assembler.paddb(s_x2, s_x4);               // clz8 per byte
        for (uint s = 1; s <= size; s++)
        {
            Mov(s_x1, s_x2);
            if (s == 1)
            {
                assembler.psrlw(s_x1, 8);
            }
            else
            {
                assembler.psrld(s_x1, 16);              // clz of the high half
            }

            Mov(s_x4, s_x1);
            if (s == 1)
            {
                assembler.pcmpeqw(s_x4, K16(8));
            }
            else
            {
                assembler.pcmpeqd(s_x4, K32(16)); // high half all zero
            }

            assembler.pand(s_x2, s == 1 ? K16(0x00FF) : K32(0xFFFF));               // clz of the low half
            assembler.pand(s_x2, s_x4);
            if (s == 1)
            {
                assembler.paddw(s_x2, s_x1);
            }
            else
            {
                assembler.paddd(s_x2, s_x1);
            }
        }
    }

    // clz of a nibble (0 -> 4), as a pshufb table
    private const ulong ClzLutLo = 0x0101010102020304, ClzLutHi = 0x0000000000000000;

    /// <summary>
    /// Saturating narrow of the Q register at m (wide lanes of size + 1, shifted right by <paramref name="shift"/>,
    /// optionally rounding) into the D register at d: VQ(R)SHRN, VQ(R)SHRUN (<paramref name="toUnsigned"/>: signed source,
    /// unsigned result) and VQMOVN/VQMOVUN (shift 0). QC when a lane saturates. Narrow sizes 0 and 1.
    /// </summary>
    private void QNarrow(bool toUnsigned, bool u, uint size, bool round, int shift, int dOff, int mOff)
    {
        Assembler assembler = Asm;
        uint w = size + 1;
        bool srcSigned = toUnsigned || !u;
        LdV(s_x0, mOff, true);
        if (round)
        {
            RoundShr(s_x0, w, shift, srcSigned, s_x1, s_x2);
        }
        else
        {
            Shr(s_x0, w, shift, srcSigned, s_x1);
        }

        Mov(s_x1, s_x0);
        if (toUnsigned)
        {
            // signed source, unsigned saturation
            if (w == 1)
            {
                assembler.packuswb(s_x1, s_x1);
                assembler.pmovzxbw(s_x2, s_x1);
                assembler.pcmpeqw(s_x2, s_x0);
            }
            else
            {
                assembler.packusdw(s_x1, s_x1);
                assembler.pmovzxwd(s_x2, s_x1);
                assembler.pcmpeqd(s_x2, s_x0);
            }
        }
        else if (!u)
        {
            if (w == 1)
            {
                assembler.packsswb(s_x1, s_x1);
                assembler.pmovsxbw(s_x2, s_x1);
                assembler.pcmpeqw(s_x2, s_x0);
            }
            else
            {
                assembler.packssdw(s_x1, s_x1);
                assembler.pmovsxwd(s_x2, s_x1);
                assembler.pcmpeqd(s_x2, s_x0);
            }
        }
        else
        {
            // unsigned: clamp to the narrow maximum, then truncate
            if (w == 1)
            {
                assembler.pminuw(s_x1, K16(0xFF));
                Mov(s_x2, s_x1);
                assembler.pcmpeqw(s_x2, s_x0);
                assembler.packuswb(s_x1, s_x1);
            }
            else
            {
                assembler.pminud(s_x1, K32(0xFFFF));
                Mov(s_x2, s_x1);
                assembler.pcmpeqd(s_x2, s_x0);
                assembler.packusdw(s_x1, s_x1);
            }
        }
        QcIfNotAll(s_x2, true);
        StV(dOff, s_x1, false);
    }

    /// <summary>VQMOVN/VQMOVUN from 64-bit lanes (no SSE pack for them): both lanes in GPRs, read before the D store.</summary>
    private void QNarrow64(bool toUnsigned, bool u, int dOff, int mOff)
    {
        Assembler assembler = Asm;
        assembler.mov(rax, __qword_ptr[rbx + s_offD + mOff]);
        assembler.mov(rdx, __qword_ptr[rbx + s_offD + mOff + 8]);
        for (int lane = 0; lane < 2; lane++)
        {
            AssemblerRegister64 r = lane == 0 ? rax : rdx;
            AssemblerRegister32 r32 = lane == 0 ? eax : edx;
            Label ok = assembler.CreateLabel();
            Label sat = assembler.CreateLabel();
            if (toUnsigned)
            {
                Label pos = assembler.CreateLabel();
                assembler.test(r, r);
                assembler.jns(pos);
                assembler.xor(r32, r32);
                assembler.jmp(sat);
                Place(ref pos);
                assembler.mov(rcx, r);
                assembler.shr(rcx, 32);
                assembler.jz(ok);
                assembler.mov(r32, -1);
            }
            else if (u)
            {
                assembler.mov(rcx, r);
                assembler.shr(rcx, 32);
                assembler.jz(ok);
                assembler.mov(r32, -1);
            }
            else
            {
                assembler.movsxd(rcx, r32);
                assembler.cmp(rcx, r);
                assembler.je(ok);
                assembler.sar(r, 63);
                assembler.xor(r32, 0x7FFFFFFF);    // 0x7FFFFFFF for positive, 0x80000000 for negative values
            }
            Place(ref sat);
            assembler.or(State(s_offFpscr), 0x08000000);
            Place(ref ok);
        }
        assembler.mov(__dword_ptr[rbx + s_offD + dOff], eax);
        assembler.mov(__dword_ptr[rbx + s_offD + dOff + 4], edx);
    }

    /// <summary>VSHL (register) on 64-bit lanes: shift = signed low byte of the n lane; |shift| >= 64 gives 0 or the sign.</summary>
    private void ShlReg64(bool u, bool q, int dOff, int nOff, int mOff)
    {
        Assembler assembler = Asm;
        for (int lane = 0; lane < (q ? 2 : 1); lane++)
        {
            int o = lane * 8;
            Label neg = assembler.CreateLabel();
            Label left = assembler.CreateLabel();
            Label right = assembler.CreateLabel();
            Label store = assembler.CreateLabel();
            assembler.mov(rax, __qword_ptr[rbx + s_offD + mOff + o]);
            assembler.movsx(ecx, __byte_ptr[rbx + s_offD + nOff + o]);
            assembler.test(ecx, ecx);
            assembler.js(neg);
            assembler.cmp(ecx, 64);
            assembler.jb(left);
            assembler.xor(eax, eax);
            assembler.jmp(store);
            Place(ref left);
            assembler.shl(rax, cl);
            assembler.jmp(store);
            Place(ref neg);
            assembler.neg(ecx);
            assembler.cmp(ecx, 64);
            assembler.jb(right);
            if (u)
            {
                assembler.xor(eax, eax);
            }
            else
            {
                assembler.sar(rax, 63);
            }

            assembler.jmp(store);
            Place(ref right);
            if (u)
            {
                assembler.shr(rax, cl);
            }
            else
            {
                assembler.sar(rax, cl);
            }

            Place(ref store);
            assembler.mov(__qword_ptr[rbx + s_offD + dOff + o], rax);
        }
    }

    /// <summary>
    /// VMULL (signed, unsigned; polynomial with <paramref name="aux"/> bit 0), VMLAL / VMLSL (accumulate, aux bit 1 =
    /// subtract): D n x D m -> Q d with lanes of twice the size.
    /// </summary>
    private void MulLong(uint size, bool u, bool acc, int aux, int dOff, int nOff, int mOff)
    {
        Assembler assembler = Asm;
        LdV(s_x1, nOff, false);
        LdV(s_x2, mOff, false);
        if ((aux & 1) != 0)
        {
            // carry-less 8x8 -> 16: xor of the shifted multiplicand for every set bit of the multiplier
            assembler.pmovzxbw(s_x0, s_x1);
            assembler.pmovzxbw(s_x3, s_x2);
            assembler.pxor(s_x4, s_x4);
            for (int i = 0; i < 8; i++)
            {
                assembler.movdqa(s_x5, K16(1u << i));
                Mov(s_x1, s_x3);
                assembler.pand(s_x1, s_x5);
                assembler.pcmpeqw(s_x1, s_x5);
                assembler.pand(s_x1, s_x0);
                assembler.pxor(s_x4, s_x1);
                if (i < 7)
                {
                    assembler.psllw(s_x0, 1);
                }
            }
            Mov(s_x0, s_x4);
        }
        else
        {
            Widen(s_x0, s_x1, size, u);
            Widen(s_x3, s_x2, size, u);
            switch (size)
            {
                case 0:
                    assembler.pmullw(s_x0, s_x3);
                    break;
                case 1:
                    assembler.pmulld(s_x0, s_x3);
                    break;
                default:
                    if (u)
                    {
                        assembler.pmuludq(s_x0, s_x3);
                    }
                    else
                    {
                        assembler.pmuldq(s_x0, s_x3);
                    }

                    break;
            }
        }
        if (acc)
        {
            LdV(s_x1, dOff, true);
            if ((aux & 2) != 0)
            {
                PSub(size + 1, s_x1, s_x0);
                Mov(s_x0, s_x1);
            }
            else
            {
                PAdd(size + 1, s_x0, s_x1);
            }
        }
        StV(dOff, s_x0, true);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void VecOpCore(IrInst x)
    {
        Assembler assembler = Asm;
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
            case VecKind.And:
            case VecKind.Bic:
            case VecKind.Orr:
            case VecKind.Orn:
            case VecKind.Eor:
            case VecKind.Bsl:
            case VecKind.Bit:
            case VecKind.Bif:
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                switch (kind)
                {
                    case VecKind.And:
                        assembler.pand(s_x0, s_x1);
                        break;
                    case VecKind.Bic:
                        assembler.pandn(s_x1, s_x0);
                        Mov(s_x0, s_x1);
                        break;              // n & ~m
                    case VecKind.Orr:
                        assembler.por(s_x0, s_x1);
                        break;
                    case VecKind.Orn:
                        Not(s_x1, s_x2);
                        assembler.por(s_x0, s_x1);
                        break;
                    case VecKind.Eor:
                        assembler.pxor(s_x0, s_x1);
                        break;
                    case VecKind.Bsl:
                        LdV(s_x2, dOff, q);
                        assembler.pand(s_x0, s_x2);
                        assembler.pandn(s_x2, s_x1);
                        assembler.por(s_x0, s_x2);
                        break; // (n&d)|(m&~d)
                    case VecKind.Bit:
                        LdV(s_x2, dOff, q);
                        assembler.pand(s_x0, s_x1);
                        assembler.pandn(s_x1, s_x2);
                        assembler.por(s_x0, s_x1);
                        break; // (n&m)|(d&~m)
                    default:
                        LdV(s_x2, dOff, q);
                        assembler.pand(s_x2, s_x1);
                        assembler.pandn(s_x1, s_x0);
                        Mov(s_x0, s_x2);
                        assembler.por(s_x0, s_x1);
                        break; // (d&m)|(n&~m)
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.AddI:
            case VecKind.SubI:
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                if (kind == VecKind.AddI)
                {
                    PAdd(size, s_x0, s_x1);
                }
                else
                {
                    PSub(size, s_x0, s_x1);
                }

                StV(dOff, s_x0, q);
                return;
            case VecKind.MulI:
            case VecKind.MlaI:
            case VecKind.MlsI:
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                PMul(size, s_x0, s_x1, s_x2, s_x3);
                if (kind != VecKind.MulI)
                {
                    LdV(s_x2, dOff, q);
                    if (kind == VecKind.MlaI)
                    {
                        PAdd(size, s_x2, s_x0);
                    }
                    else
                    {
                        PSub(size, s_x2, s_x0);
                    }

                    Mov(s_x0, s_x2);
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.CeqI:
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                PCmpEq(size, s_x0, s_x1);
                StV(dOff, s_x0, q);
                return;
            case VecKind.TstI:
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                assembler.pand(s_x0, s_x1);
                assembler.pxor(s_x1, s_x1);
                PCmpEq(size, s_x0, s_x1);
                Not(s_x0, s_x1);
                StV(dOff, s_x0, q);
                return;
            case VecKind.CgtI:
            case VecKind.CgeI:
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                if (u)
                {
                    assembler.movdqa(s_x2, KLane(size, 1ul << (bits - 1)));
                    assembler.pxor(s_x0, s_x2);
                    assembler.pxor(s_x1, s_x2);
                }
                if (kind == VecKind.CgtI)
                {
                    PCmpGt(size, s_x0, s_x1);
                }
                else
                {
                    PCmpGt(size, s_x1, s_x0);
                    Not(s_x1, s_x2);
                    Mov(s_x0, s_x1);
                } // n >= m == !(m > n)
                StV(dOff, s_x0, q);
                return;
            case VecKind.MaxI:
            case VecKind.MinI:
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                if (kind == VecKind.MaxI)
                {
                    PMax(size, u, s_x0, s_x1);
                }
                else
                {
                    PMin(size, u, s_x0, s_x1);
                }

                StV(dOff, s_x0, q);
                return;
            case VecKind.AbdI:
            case VecKind.AbaI:
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                Mov(s_x2, s_x0);
                PMax(size, u, s_x0, s_x1);
                PMin(size, u, s_x2, s_x1);
                PSub(size, s_x0, s_x2);
                if (kind == VecKind.AbaI)
                {
                    LdV(s_x1, dOff, q);
                    PAdd(size, s_x0, s_x1);
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.HaddI: // (x & y) + ((x ^ y) >> 1)
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                Mov(s_x2, s_x0);
                assembler.pand(s_x0, s_x1);
                assembler.pxor(s_x2, s_x1);
                Shr(s_x2, size, 1, !u, s_x3);
                PAdd(size, s_x0, s_x2);
                StV(dOff, s_x0, q);
                return;
            case VecKind.RhaddI: // (x | y) - ((x ^ y) >> 1)
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                if (u && size == 0)
                {
                    assembler.pavgb(s_x0, s_x1);
                }
                else if (u && size == 1)
                {
                    assembler.pavgw(s_x0, s_x1);
                }
                else
                {
                    Mov(s_x2, s_x0);
                    assembler.por(s_x0, s_x1);
                    assembler.pxor(s_x2, s_x1);
                    Shr(s_x2, size, 1, !u, s_x3);
                    PSub(size, s_x0, s_x2);
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.HsubI: // (x >> 1) - (y >> 1) - (~x & y & 1)
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                Mov(s_x2, s_x0);
                assembler.pandn(s_x2, s_x1);
                assembler.pand(s_x2, KLane(size, 1));
                Shr(s_x0, size, 1, !u, s_x3);
                Shr(s_x1, size, 1, !u, s_x3);
                PSub(size, s_x0, s_x1);
                PSub(size, s_x0, s_x2);
                StV(dOff, s_x0, q);
                return;
            case VecKind.QaddI:
            case VecKind.QsubI:
                SatAddSub(kind == VecKind.QsubI, size, u, q, dOff, nOff, mOff);
                return;
            case VecKind.PaddI:
            case VecKind.PmaxI:
            case VecKind.PminI:
                LdPair(s_x0, s_x1, nOff, mOff);
                Mov(s_x1, s_x0);
                assembler.pshufb(s_x1, KBytes(PairMask(size, false)));
                assembler.pshufb(s_x0, KBytes(PairMask(size, true)));
                if (kind == VecKind.PaddI)
                {
                    PAdd(size, s_x1, s_x0);
                }
                else if (kind == VecKind.PmaxI)
                {
                    PMax(size, u, s_x1, s_x0);
                }
                else
                {
                    PMin(size, u, s_x1, s_x0);
                }

                StV(dOff, s_x1, false);
                return;
            case VecKind.QdmulhI:
            case VecKind.QrdmulhI: // 16-bit lanes
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                Mov(s_x2, s_x0);
                Mov(s_x3, s_x1);
                if (kind == VecKind.QrdmulhI)
                {
                    assembler.pmulhrsw(s_x0, s_x1); // (x*y + 2^14) >> 15
                }
                else
                {
                    // (2xy) >> 16 = (hi << 1) | (lo >> 15)
                    Mov(s_x4, s_x0);
                    assembler.pmulhw(s_x0, s_x1);
                    assembler.pmullw(s_x4, s_x1);
                    assembler.psllw(s_x0, 1);
                    assembler.psrlw(s_x4, 15);
                    assembler.por(s_x0, s_x4);
                }
                // -32768 * -32768 is the only product that overflows: saturate to 0x7FFF and set QC
                assembler.movdqa(s_x4, K16(0x8000));
                assembler.pcmpeqw(s_x2, s_x4);
                assembler.pcmpeqw(s_x3, s_x4);
                assembler.pand(s_x2, s_x3);
                assembler.pxor(s_x0, s_x2);
                QcIfAny(s_x2, q);
                StV(dOff, s_x0, q);
                return;
            case VecKind.ShlRegI: // VSHL (register), shift = signed low byte of each n lane: 32-bit lanes with AVX2, 64-bit in GPRs
                if (size == 3)
                {
                    ShlReg64(u, q, dOff, nOff, mOff);
                    return;
                }
                LdV(s_x0, mOff, q);
                LdV(s_x1, nOff, q);
                assembler.pslld(s_x1, 24);
                assembler.psrad(s_x1, 24);
                assembler.vpsllvd(s_x2, s_x0, s_x1);
                assembler.pxor(s_x3, s_x3);
                assembler.psubd(s_x3, s_x1);
                if (u)
                {
                    assembler.vpsrlvd(s_x0, s_x0, s_x3);
                }
                else
                {
                    assembler.vpsravd(s_x0, s_x0, s_x3);
                }

                assembler.vblendvps(s_x0, s_x2, s_x0, s_x1);
                StV(dOff, s_x0, q);
                return;

            // ---------------------------------------------------------- float three-same
            case VecKind.AddF:
            case VecKind.SubF:
            case VecKind.MulF:
            case VecKind.MlaF:
            case VecKind.MlsF:
            case VecKind.AbdF:
                NeonFp();
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                switch (kind)
                {
                    case VecKind.AddF:
                        assembler.addps(s_x0, s_x1);
                        break;
                    case VecKind.SubF:
                        assembler.subps(s_x0, s_x1);
                        break;
                    case VecKind.MulF:
                        assembler.mulps(s_x0, s_x1);
                        TinyGuard(s_x0, q);
                        break;
                    case VecKind.AbdF:
                        assembler.subps(s_x0, s_x1);
                        assembler.pand(s_x0, K32(0x7FFFFFFF));
                        break;
                    default: // VMLA/VMLS: d +/- round(n * m); a denormal product reads as zero (DAZ) like the handler's FRes
                        assembler.mulps(s_x0, s_x1);
                        TinyGuard(s_x0, q);
                        LdV(s_x1, dOff, q);
                        if (kind == VecKind.MlaF)
                        {
                            assembler.addps(s_x1, s_x0);
                        }
                        else
                        {
                            assembler.subps(s_x1, s_x0);
                        }

                        Mov(s_x0, s_x1);
                        break;
                }
                FRes(s_x0);
                StV(dOff, s_x0, q);
                return;
            case VecKind.FmaF:
            case VecKind.FmsF:
                NeonFp();
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                LdV(s_x2, dOff, q);
                if (kind == VecKind.FmsF)
                {
                    assembler.pxor(s_x0, K32(0x80000000));
                }

                assembler.vfmadd231ps(s_x2, s_x0, s_x1);
                TinyGuard(s_x2, q);
                FRes(s_x2);
                StV(dOff, s_x2, q);
                return;
            case VecKind.PaddF: // D only: [n0+n1, m0+m1]
                NeonFp();
                LdPair(s_x0, s_x1, nOff, mOff);
                assembler.haddps(s_x0, s_x0);
                FRes(s_x0);
                StV(dOff, s_x0, false);
                return;
            case VecKind.CeqF:
            case VecKind.CgeF:
            case VecKind.CgtF:
            case VecKind.AcgeF:
            case VecKind.AcgtF:
                NeonFp();
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                if (kind is VecKind.AcgeF or VecKind.AcgtF)
                {
                    assembler.movdqa(s_x2, K32(0x7FFFFFFF));
                    assembler.pand(s_x0, s_x2);
                    assembler.pand(s_x1, s_x2);
                }
                switch (kind)
                {
                    case VecKind.CeqF:
                        assembler.cmpeqps(s_x0, s_x1);
                        break;
                    case VecKind.CgeF:
                    case VecKind.AcgeF:
                        assembler.cmpleps(s_x1, s_x0);
                        Mov(s_x0, s_x1);
                        break;
                    default:
                        assembler.cmpltps(s_x1, s_x0);
                        Mov(s_x0, s_x1);
                        break;
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.MaxF:
            case VecKind.MinF:
                NeonFp();
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                MaxMinF(kind == VecKind.MaxF);
                StV(dOff, s_x0, q);
                return;
            case VecKind.PmaxF:
            case VecKind.PminF: // D only
                NeonFp();
                LdPair(s_x2, s_x1, nOff, mOff);   // [n0 n1 m0 m1]
                Mov(s_x0, s_x2);
                assembler.shufps(s_x0, s_x2, 0x88);      // [n0 m0 n0 m0]
                Mov(s_x1, s_x2);
                assembler.shufps(s_x1, s_x2, 0xDD);      // [n1 m1 n1 m1]
                MaxMinF(kind == VecKind.PmaxF);
                StV(dOff, s_x0, false);
                return;
            case VecKind.RecpsF:
            case VecKind.RsqrtsF:
            {
                // 2 - n*m (VRECPS) / (3 - n*m) / 2 (VRSQRTS); infinity * zero gives exactly 2 / 1.5
                bool rs = kind == VecKind.RsqrtsF;
                NeonFp();
                LdV(s_x0, nOff, q);
                LdV(s_x1, mOff, q);
                Mov(s_x2, s_x0);
                assembler.mulps(s_x2, s_x1);              // p
                Mov(s_x3, s_x0);
                assembler.cmpordps(s_x3, s_x1);           // neither operand NaN
                Mov(s_x4, s_x2);
                assembler.cmpunordps(s_x4, s_x2);         // p NaN
                assembler.pand(s_x3, s_x4);               // infinity * zero
                assembler.movdqa(s_x0, K32(F32(rs ? 3f : 2f)));
                assembler.subps(s_x0, s_x2);
                if (rs)
                {
                    assembler.mulps(s_x0, K32(F32(0.5f)));
                }

                assembler.movdqa(s_x1, K32(F32(rs ? 1.5f : 2f)));
                Blend(s_x0, s_x3, s_x1);
                FRes(s_x0);
                StV(dOff, s_x0, q);
                return;
            }

            // ---------------------------------------------------------- by scalar
            case VecKind.MulFS:
            case VecKind.MlaFS:
            case VecKind.MlsFS:
                NeonFp();
                LdV(s_x0, nOff, q);
                Broadcast(s_x1, aux, 2);
                assembler.mulps(s_x0, s_x1);
                TinyGuard(s_x0, q);
                if (kind != VecKind.MulFS)
                {
                    LdV(s_x1, dOff, q);
                    if (kind == VecKind.MlaFS)
                    {
                        assembler.addps(s_x1, s_x0);
                    }
                    else
                    {
                        assembler.subps(s_x1, s_x0);
                    }

                    Mov(s_x0, s_x1);
                }
                FRes(s_x0);
                StV(dOff, s_x0, q);
                return;
            case VecKind.MulIS:
            case VecKind.MlaIS:
            case VecKind.MlsIS:
                LdV(s_x0, nOff, q);
                Broadcast(s_x1, aux, size);
                PMul(size, s_x0, s_x1, s_x2, s_x3);
                if (kind != VecKind.MulIS)
                {
                    LdV(s_x1, dOff, q);
                    if (kind == VecKind.MlaIS)
                    {
                        PAdd(size, s_x1, s_x0);
                    }
                    else
                    {
                        PSub(size, s_x1, s_x0);
                    }

                    Mov(s_x0, s_x1);
                }
                StV(dOff, s_x0, q);
                return;

            // ---------------------------------------------------------- shifts by immediate
            case VecKind.ShrImm:
                LdV(s_x0, mOff, q);
                if (round)
                {
                    RoundShr(s_x0, size, aux, !u, s_x1, s_x2);
                }
                else
                {
                    Shr(s_x0, size, aux, !u, s_x1);
                }

                if (acc)
                {
                    LdV(s_x1, dOff, q);
                    PAdd(size, s_x0, s_x1);
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.ShlImm:
                LdV(s_x0, mOff, q);
                ShlI(s_x0, size, aux);
                StV(dOff, s_x0, q);
                return;
            case VecKind.SriImm:
            case VecKind.SliImm:
            {
                ulong mask = LaneMask(size);
                ulong keep = kind == VecKind.SriImm
                    ? (aux >= bits ? mask : ~(mask >> aux) & mask)
                    : ~(mask << aux) & mask;
                LdV(s_x0, mOff, q);
                if (kind == VecKind.SriImm)
                {
                    ShrU(s_x0, size, aux);
                }
                else
                {
                    ShlI(s_x0, size, aux);
                }

                LdV(s_x1, dOff, q);
                assembler.pand(s_x1, KLane(size, keep));
                assembler.por(s_x0, s_x1);
                StV(dOff, s_x0, q);
                return;
            }
            case VecKind.ShrnImm: // Q source, D result, truncating
            {
                uint w = size + 1;
                LdV(s_x0, mOff, true);
                if (round)
                {
                    RoundShr(s_x0, w, aux, false, s_x1, s_x2);
                }
                else
                {
                    ShrU(s_x0, w, aux);
                }

                NarrowTrunc(s_x0, w);
                StV(dOff, s_x0, false);
                return;
            }
            case VecKind.QshrnImm:
            case VecKind.QshrunImm:
                QNarrow(kind == VecKind.QshrunImm, u, size, round, aux, dOff, mOff);
                return;
            case VecKind.ShllImm: // D source, Q result
                LdV(s_x1, mOff, false);
                Widen(s_x0, s_x1, size, u);
                ShlI(s_x0, size + 1, aux);
                StV(dOff, s_x0, true);
                return;

            // ---------------------------------------------------------- two registers, miscellaneous
            case VecKind.Rev:
            {
                int eb = 1 << (int)size, per = aux / eb;
                byte[] m = new byte[16];
                for (int j = 0; j < 16; j++)
                {
                    int e = j / eb, gbase = e - e % per;
                    m[j] = (byte)((gbase + per - 1 - e % per) * eb + j % eb);
                }
                LdV(s_x0, mOff, q);
                assembler.pshufb(s_x0, KBytes(m));
                StV(dOff, s_x0, q);
                return;
            }
            case VecKind.Mvn:
                LdV(s_x0, mOff, q);
                Not(s_x0, s_x1);
                StV(dOff, s_x0, q);
                return;
            case VecKind.AbsF:
                LdV(s_x0, mOff, q);
                assembler.pand(s_x0, K32(0x7FFFFFFF));
                StV(dOff, s_x0, q);
                return;
            case VecKind.NegF:
                LdV(s_x0, mOff, q);
                assembler.pxor(s_x0, K32(0x80000000));
                StV(dOff, s_x0, q);
                return;
            case VecKind.AbsI:
                LdV(s_x0, mOff, q);
                switch (size)
                {
                    case 0:
                        assembler.pabsb(s_x0, s_x0);
                        break;
                    case 1:
                        assembler.pabsw(s_x0, s_x0);
                        break;
                    default:
                        assembler.pabsd(s_x0, s_x0);
                        break;
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.NegI:
                LdV(s_x1, mOff, q);
                assembler.pxor(s_x0, s_x0);
                PSub(size, s_x0, s_x1);
                StV(dOff, s_x0, q);
                return;
            case VecKind.CmpZeroI:
                LdV(s_x0, mOff, q);
                assembler.pxor(s_x1, s_x1);
                switch (aux)
                {
                    case 0:
                        PCmpGt(size, s_x0, s_x1);
                        break;                         // x > 0
                    case 1:
                        PCmpGt(size, s_x1, s_x0);
                        Not(s_x1, s_x2);
                        Mov(s_x0, s_x1);
                        break; // x >= 0 == !(0 > x)
                    case 2:
                        PCmpEq(size, s_x0, s_x1);
                        break;
                    case 3:
                        PCmpGt(size, s_x0, s_x1);
                        Not(s_x0, s_x2);
                        break;           // x <= 0 == !(x > 0)
                    default:
                        PCmpGt(size, s_x1, s_x0);
                        Mov(s_x0, s_x1);
                        break;          // x < 0
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.CmpZeroF:
                NeonFp();
                LdV(s_x0, mOff, q);
                assembler.xorps(s_x1, s_x1);
                switch (aux)
                {
                    case 0:
                        assembler.cmpltps(s_x1, s_x0);
                        Mov(s_x0, s_x1);
                        break;  // 0 < x
                    case 1:
                        assembler.cmpleps(s_x1, s_x0);
                        Mov(s_x0, s_x1);
                        break;  // 0 <= x
                    case 2:
                        assembler.cmpeqps(s_x0, s_x1);
                        break;
                    case 3:
                        assembler.cmpleps(s_x0, s_x1);
                        break;               // x <= 0
                    default:
                        assembler.cmpltps(s_x0, s_x1);
                        break;              // x < 0
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.Swp:
                LdV(s_x0, dOff, q);
                LdV(s_x1, mOff, q);
                StV(mOff, s_x0, q);
                StV(dOff, s_x1, q);
                return;
            case VecKind.Trn:
            case VecKind.Uzp:
            case VecKind.Zip:
            {
                // byte sources of the new d and new m: 0-15 = old d, 16-31 = old m (the handlers store m first, then d)
                int eb = 1 << (int)size, bytes = q ? 16 : 8, count = bytes / eb;
                byte[] nd = new byte[16];
                byte[] nm = new byte[16];
                Array.Fill(nd, (byte)0x80);
                Array.Fill(nm, (byte)0x80);
                byte Src(int globalElem, int k) => (byte)(globalElem < count ? globalElem * eb + k : 16 + (globalElem - count) * eb + k);
                for (int i = 0; i < count; i++)
                {
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
                }

                LdV(s_x0, dOff, q);
                LdV(s_x1, mOff, q);
                Permute(s_x2, s_x0, s_x1, s_x4, nm);
                Permute(s_x3, s_x0, s_x1, s_x4, nd);
                if (kind == VecKind.Trn)
                {
                    StV(mOff, s_x2, q);
                    StV(dOff, s_x3, q);
                }
                else
                {
                    StV(dOff, s_x3, q);
                    StV(mOff, s_x2, q);
                } // VZIP/VUZP with Vd == Vm keep the second half (interpreter)
                return;
            }
            case VecKind.Movn:
                LdV(s_x0, mOff, true);
                NarrowTrunc(s_x0, size + 1);
                StV(dOff, s_x0, false);
                return;
            case VecKind.Paddl: // pairs of size lanes -> one lane of size + 1 (exact: the sums never wrap)
                LdV(s_x0, mOff, q);
                switch (size)
                {
                    case 0:
                        if (u)
                        {
                            assembler.pmaddubsw(s_x0, K8(1));                       // unsigned bytes * signed 1
                        }
                        else
                        {
                            assembler.movdqa(s_x1, K8(1));
                            assembler.pmaddubsw(s_x1, s_x0);
                            Mov(s_x0, s_x1);
                        } // signed bytes * unsigned 1
                        break;
                    case 1:
                        if (u)
                        {
                            assembler.pxor(s_x0, K16(0x8000));                          // (a - 2^15) + (b - 2^15) = a + b - 2^16
                            assembler.pmaddwd(s_x0, K16(1));
                            assembler.paddd(s_x0, K32(0x10000));
                        }
                        else
                        {
                            assembler.pmaddwd(s_x0, K16(1));
                        }

                        break;
                    default:
                        assembler.pshufd(s_x1, s_x0, 0x08);                               // even lanes [0, 2]
                        assembler.pshufd(s_x2, s_x0, 0x0D);                               // odd lanes [1, 3]
                        Widen(s_x0, s_x1, 2, u);
                        Widen(s_x3, s_x2, 2, u);
                        assembler.paddq(s_x0, s_x3);
                        break;
                }
                if (acc)
                {
                    LdV(s_x1, dOff, q);
                    PAdd(size + 1, s_x0, s_x1);
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.Clz:
                LdV(s_x0, mOff, q);
                ClzLanes(size);
                StV(dOff, s_x2, q);
                return;
            case VecKind.Cls: // cls(x) = clz(x ^ (x >> 1, arithmetic)) - 1
                LdV(s_x0, mOff, q);
                Mov(s_x1, s_x0);
                ShrS(s_x1, size, 1, s_x2);
                assembler.pxor(s_x0, s_x1);
                ClzLanes(size);
                assembler.movdqa(s_x1, KLane(size, 1));
                PSub(size, s_x2, s_x1);
                StV(dOff, s_x2, q);
                return;
            case VecKind.MullI:
                MulLong(size, u, acc, aux, dOff, nOff, mOff);
                return;
            case VecKind.Qmovn:
                if (size < 2)
                {
                    QNarrow(aux == 1, u, size, false, 0, dOff, mOff);
                }
                else
                {
                    QNarrow64(aux == 1, u, dOff, mOff);
                }

                return;
            case VecKind.Cnt: // bytes: nibble popcount table
            {
                byte[] lut = new byte[16];
                for (int i = 0; i < 16; i++)
                {
                    lut[i] = (byte)System.Numerics.BitOperations.PopCount((uint)i);
                }

                LdV(s_x0, mOff, q);
                assembler.movdqa(s_x3, K8(0x0F));
                Mov(s_x1, s_x0);
                assembler.psrlw(s_x1, 4);
                assembler.pand(s_x0, s_x3);
                assembler.pand(s_x1, s_x3);
                assembler.movdqa(s_x2, KBytes(lut));
                Mov(s_x3, s_x2);
                assembler.pshufb(s_x2, s_x0);
                assembler.pshufb(s_x3, s_x1);
                assembler.paddb(s_x2, s_x3);
                StV(dOff, s_x2, q);
                return;
            }
            case VecKind.RecpeF:
            case VecKind.RsqrteF:
            case VecKind.RecpeU:
            case VecKind.RsqrteU:
            {
                int lanes = q ? 4 : 2;
                for (int i = 0; i < lanes; i++)
                {
                    assembler.mov(eax, __dword_ptr[rbx + s_offD + mOff + 4 * i]);
                    EstimateLane(kind);
                    assembler.mov(__dword_ptr[rbx + s_offD + dOff + 4 * i], eax);
                }
                return;
            }
            case VecKind.CvtS2F:
                LdV(s_x0, mOff, q);
                assembler.cvtdq2ps(s_x0, s_x0);
                StV(dOff, s_x0, q);
                return;
            case VecKind.CvtU2F: // hi16 * 65536 + lo16: both exact, one rounding
                LdV(s_x0, mOff, q);
                Mov(s_x1, s_x0);
                assembler.psrld(s_x1, 16);
                assembler.pand(s_x0, K32(0xFFFF));
                assembler.cvtdq2ps(s_x0, s_x0);
                assembler.cvtdq2ps(s_x1, s_x1);
                assembler.mulps(s_x1, K32(F32(65536f)));
                assembler.addps(s_x0, s_x1);
                StV(dOff, s_x0, q);
                return;
            case VecKind.CvtF2S: // truncate, saturate, NaN -> 0
                NeonFp();
                LdV(s_x0, mOff, q);
                assembler.movdqa(s_x2, K32(F32(2147483648f)));
                assembler.cmpleps(s_x2, s_x0);            // x >= 2^31
                Mov(s_x3, s_x0);
                assembler.cmpordps(s_x3, s_x3);           // not NaN
                assembler.cvttps2dq(s_x0, s_x0);
                assembler.pxor(s_x0, s_x2);               // 0x80000000 -> 0x7FFFFFFF for positive overflow
                assembler.pand(s_x0, s_x3);
                StV(dOff, s_x0, q);
                return;
            case VecKind.CvtF2U: // truncate, negative and NaN -> 0, >= 2^32 -> 0xFFFFFFFF
                NeonFp();
                LdV(s_x0, mOff, q);
                assembler.movdqa(s_x2, K32(F32(2147483648f)));
                assembler.cmpleps(s_x2, s_x0);            // big: x >= 2^31
                Mov(s_x1, s_x2);
                assembler.pand(s_x1, K32(F32(2147483648f)));
                Mov(s_x3, s_x0);
                assembler.subps(s_x3, s_x1);              // big lanes: x - 2^31 (exact)
                assembler.cvttps2dq(s_x3, s_x3);
                assembler.pand(s_x2, K32(0x80000000));
                assembler.pxor(s_x3, s_x2);               // add 2^31 back
                assembler.movdqa(s_x1, K32(F32(4294967296f)));
                assembler.cmpleps(s_x1, s_x0);            // x >= 2^32
                assembler.por(s_x3, s_x1);
                assembler.xorps(s_x2, s_x2);
                assembler.cmpltps(s_x2, s_x0);            // 0 < x (false for NaN): keep
                assembler.pand(s_x3, s_x2);
                StV(dOff, s_x3, q);
                return;
            case VecKind.CvtF2H: // Q source, D result; FZ inputs, round to nearest, NaN -> 0x7E00
                NeonFp();
                LdV(s_x0, mOff, true);
                Mov(s_x1, s_x0);
                assembler.cmpunordps(s_x1, s_x1);
                assembler.vcvtps2ph(s_x0, s_x0, 0);
                assembler.packssdw(s_x1, s_x1);
                assembler.movdqa(s_x2, K16(0x7E00));
                Blend(s_x0, s_x1, s_x2);
                StV(dOff, s_x0, false);
                return;
            case VecKind.CvtH2F: // D source, Q result
                NeonFp();
                LdV(s_x0, mOff, false);
                assembler.vcvtph2ps(s_x0, s_x0);
                FRes(s_x0);
                StV(dOff, s_x0, true);
                return;
            case VecKind.Ext:
                if (q)
                {
                    LdV(s_x0, mOff, true);
                    LdV(s_x1, nOff, true);
                    if (aux != 0)
                    {
                        assembler.palignr(s_x0, s_x1, (byte)aux);
                    }
                    else
                    {
                        Mov(s_x0, s_x1);
                    }
                }
                else
                {
                    LdPair(s_x0, s_x1, nOff, mOff);
                    if (aux != 0)
                    {
                        assembler.psrldq(s_x0, (byte)aux);
                    }
                }
                StV(dOff, s_x0, q);
                return;
            case VecKind.Tbl:
            {
                int len = aux & 0xF;
                bool tbx = (aux & 0x10) != 0;
                int n = nOff / 8;
                LdV(s_x0, mOff, false);         // indices
                assembler.pxor(s_x4, s_x4);               // result
                for (int c = 0; c * 2 < len; c++)
                {
                    int regs = Math.Min(2, len - 2 * c);
                    assembler.movq(s_x1, __qword_ptr[rbx + s_offD + ((n + 2 * c) & 31) * 8]);
                    if (regs == 2)
                    {
                        assembler.movq(s_x2, __qword_ptr[rbx + s_offD + ((n + 2 * c + 1) & 31) * 8]);
                        assembler.punpcklqdq(s_x1, s_x2);
                    }
                    Mov(s_x3, s_x0);
                    if (c > 0)
                    {
                        assembler.psubb(s_x3, K8((uint)(16 * c)));
                    }

                    assembler.movdqa(s_x5, s_x3);
                    assembler.pminub(s_x5, K8((uint)(8 * regs - 1)));
                    assembler.pcmpeqb(s_x5, s_x3);        // index inside this chunk
                    assembler.pshufb(s_x1, s_x3);
                    assembler.pand(s_x1, s_x5);
                    assembler.por(s_x4, s_x1);
                }
                if (tbx)
                {
                    assembler.movdqa(s_x5, s_x0);
                    assembler.pminub(s_x5, K8((uint)(8 * len - 1)));
                    assembler.pcmpeqb(s_x5, s_x0);        // index inside the table
                    LdV(s_x1, dOff, false);
                    assembler.pandn(s_x5, s_x1);
                    assembler.por(s_x4, s_x5);
                }
                StV(dOff, s_x4, false);
                return;
            }
            default:
                throw new NotSupportedException($"vector kind {kind}");
        }
    }

    /// <summary>
    /// Handler FMax/FMin on xmm0 (a) and xmm1 (b), result in xmm0: flushed inputs, NaN -> default NaN, +0 beats -0 for
    /// max (and -0 for min).
    /// </summary>
    private void MaxMinF(bool max)
    {
        Assembler assembler = Asm;
        FlushIn(s_x0);
        FlushIn(s_x1);
        Mov(s_x2, s_x0);
        if (max)
        {
            assembler.maxps(s_x2, s_x1);
        }
        else
        {
            assembler.minps(s_x2, s_x1);
        }

        Mov(s_x3, s_x0);
        assembler.cmpeqps(s_x3, s_x1);                // equal (incl. +0/-0)
        Mov(s_x4, s_x0);
        if (max)
        {
            assembler.pand(s_x4, s_x1);
        }
        else
        {
            assembler.por(s_x4, s_x1);
        }

        Blend(s_x2, s_x3, s_x4);
        Mov(s_x3, s_x0);
        assembler.cmpunordps(s_x3, s_x1);
        assembler.movdqa(s_x4, K32(0x7FC00000));
        Blend(s_x2, s_x3, s_x4);
        Mov(s_x0, s_x2);
    }

    /// <summary>VQADD/VQSUB (signed and unsigned, all lane sizes) with QC.</summary>
    private void SatAddSub(bool sub, uint size, bool u, bool q, int dOff, int nOff, int mOff)
    {
        Assembler assembler = Asm;
        LdV(s_x0, nOff, q);
        LdV(s_x1, mOff, q);
        if (size <= 1)
        {
            Mov(s_x2, s_x0);
            if (sub)
            {
                PSub(size, s_x2, s_x1);
            }
            else
            {
                PAdd(size, s_x2, s_x1); // wrapped result
            }

            if (size == 0)
            {
                if (sub)
                {
                    if (u)
                    {
                        assembler.psubusb(s_x0, s_x1);
                    }
                    else
                    {
                        assembler.psubsb(s_x0, s_x1);
                    }
                }
                else
                {
                    if (u)
                    {
                        assembler.paddusb(s_x0, s_x1);
                    }
                    else
                    {
                        assembler.paddsb(s_x0, s_x1);
                    }
                }
            }
            else
            {
                if (sub)
                {
                    if (u)
                    {
                        assembler.psubusw(s_x0, s_x1);
                    }
                    else
                    {
                        assembler.psubsw(s_x0, s_x1);
                    }
                }
                else
                {
                    if (u)
                    {
                        assembler.paddusw(s_x0, s_x1);
                    }
                    else
                    {
                        assembler.paddsw(s_x0, s_x1);
                    }
                }
            }
            assembler.pcmpeqb(s_x2, s_x0);
            QcIfNotAll(s_x2, q);
            StV(dOff, s_x0, q);
            return;
        }
        Mov(s_x2, s_x0);
        if (sub)
        {
            PSub(size, s_x2, s_x1);
        }
        else
        {
            PAdd(size, s_x2, s_x1);     // r
        }

        if (!u)
        {
            // overflow: add (x^r) & (y^r), sub (x^y) & (x^r); saturated value = (x >> top) ^ max
            Mov(s_x3, s_x0);
            assembler.pxor(s_x3, s_x2);
            if (sub)
            {
                Mov(s_x4, s_x0);
                assembler.pxor(s_x4, s_x1);
            }
            else
            {
                Mov(s_x4, s_x1);
                assembler.pxor(s_x4, s_x2);
            }
            assembler.pand(s_x3, s_x4);
            SignMask(s_x3, size);                                // overflow lanes
            Mov(s_x4, s_x0);
            SignMask(s_x4, size);
            assembler.pxor(s_x4, KLane(size, size == 2 ? 0x7FFFFFFFul : 0x7FFFFFFFFFFFFFFFul));
            Mov(s_x5, s_x3);
            Blend(s_x2, s_x5, s_x4);
        }
        else
        {
            // unsigned: add overflows when r < x, sub when x < y (biased signed compares)
            assembler.movdqa(s_x5, KLane(size, size == 2 ? 0x80000000ul : 0x8000000000000000ul));
            if (sub)
            {
                Mov(s_x3, s_x1);
                Mov(s_x4, s_x0);
            }
            else
            {
                Mov(s_x3, s_x0);
                Mov(s_x4, s_x2);
            }
            assembler.pxor(s_x3, s_x5);
            assembler.pxor(s_x4, s_x5);
            PCmpGt(size, s_x3, s_x4);                              // overflow lanes
            if (sub)
            {
                assembler.pandn(s_x3, s_x2);
            }
            else
            {
                assembler.por(s_x3, s_x2);
            }

            Mov(s_x4, s_x3);
            Mov(s_x3, s_x2);
            assembler.pcmpeqd(s_x3, s_x4);                                 // unchanged lanes
            Mov(s_x2, s_x4);
            QcIfNotAll(s_x3, q);
            StV(dOff, s_x2, q);
            return;
        }
        QcIfAny(s_x3, q);
        StV(dOff, s_x2, q);
    }

    /// <summary>Each lane of r becomes all ones when its sign bit is set (32- or 64-bit lanes).</summary>
    private void SignMask(AssemblerRegisterXMM r, uint size)
    {
        Asm.psrad(r, 31);
        if (size == 3)
        {
            Asm.pshufd(r, r, 0xF5);
        }
    }
}

internal static class IntExt
{
    public static int AsInt(this uint v) => unchecked((int)v);
}
