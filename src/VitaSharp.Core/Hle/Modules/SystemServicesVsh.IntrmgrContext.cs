using VitaSharp.Core.Cpu;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The context switch primitives of SceIntrmgrForKernel the FW 3.74 thread manager switches threads with (intrmgr.skprx,
/// decompiled, study only). A context is 0x100 bytes of words:
/// <list type="bullet">
/// <item>+0x00 its kind: 0 saved by NID_71C5F42F (the callee-saved registers of a call, like setjmp), 1 a whole
/// interrupted state (r0-r12 at +0x04, the pc and cpsr at +0xC0/+0xC4);</item>
/// <item>+0x14..+0x30 r4-r11, +0x34 r12, +0x38 sp, +0x3C lr, +0x40 the cpsr (the flags and the Thumb bit), +0x44 the
/// user DACR, +0x48 the DACR, +0x4C CPACR;</item>
/// <item>+0x50 TPIDRURW, +0x54 TPIDRURO, +0x58 TTBR1, +0x5C CONTEXTIDR, +0x60 PAR, +0x64..+0x74 and +0xF0 the
/// performance monitor (PMCR, PMOVSR, PMSELR, PMUSERENR, PMCCNTR), +0x78..+0xA4 its six event counters (type and
/// count), +0xF0 PMCNTENSET;</item>
/// <item>+0xA8 DFSR, +0xAC IFSR, +0xB0 DFAR, +0xB4 IFAR, +0xB8 TEEHBR, +0xBC the word at +4 of the interrupt
/// manager's per-CPU block (the 0x810882D0 pointer).</item>
/// </list>
/// The VFP state goes to a separate area: with bit 0 of its address set the compact form (d8-d15 at +0, FPSCR +0x40,
/// FPEXC +0x44), else the whole form (d8-d15 at +0x40, as in d0-d31 at +0; FPSCR +0x100, FPEXC +0x104).
/// <para>
/// VitaSharp's CPU has, of the coprocessor registers, the thread ID registers and the fault registers: those are saved
/// and restored; the others read 0 there (what an MRC of them answers on VitaSharp's CPU) and restoring them changes
/// nothing. Restoring a kind 0 context returns from the NID_71C5F42F call a second time with r0 = 1 and r1 the
/// interrupt manager's per-CPU block; a kind 1 context resumes the interrupted state.
/// </para>
/// </summary>
public sealed partial class SystemServicesVsh
{
    const uint ContextKind = 0x00, ContextR0 = 0x04, ContextR4 = 0x14, ContextSp = 0x38, ContextLr = 0x3C,
        ContextCpsr = 0x40, ContextTpidrUrw = 0x50, ContextTpidrUro = 0x54, ContextDfsr = 0xA8, ContextIfsr = 0xAC,
        ContextDfar = 0xB0, ContextIfar = 0xB4, ContextIntrWord = 0xBC, ContextFullPc = 0xC0, ContextFullCpsr = 0xC4;

    /// <summary>The size of a context (NID_71E08D71 copies one).</summary>
    const uint IntrContextSize = 0x200;

    /// <summary>The CPSR bit of the Thumb state.</summary>
    const uint CpsrThumb = 0x20;

    /// <summary>The per-CPU block of the interrupt manager (the pointer at 0x810882D0); its +4 word is part of a context.</summary>
    uint IntrPerCpuWordAddress()
    {
        uint block = GuestMemory.R32(IntrWordAddress(0x810882D0));
        return block == 0 ? 0 : block + 4;
    }

    /// <summary>NID_71C5F42F(context, vfp): saves the caller's callee-saved state (kind 0) and answers 0.</summary>
    [Hle(Nid = 0x71C5F42F, Library = "SceIntrmgrForKernel")]
    unsafe int NID_71C5F42F(HleContext context, uint save, uint vfp)
    {
        CpuState* state = context.Cpu;
        GuestMemory.Fill(save, 0, 0xC0);
        GuestMemory.W32(save + ContextKind, 0);
        for (int i = 0; i < 8; i++)
        {
            GuestMemory.W32(save + ContextR4 + (uint)i * 4, state->R[4 + i]);
        }

        GuestMemory.W32(save + ContextSp, state->R[13]);
        GuestMemory.W32(save + ContextLr, state->R[14]);
        uint flags = (state->N << 4) | (state->Z << 3) | (state->C << 2) | (state->V << 1) | state->Q;
        GuestMemory.W32(save + ContextCpsr, (flags << 27) | CpsrThumb);
        GuestMemory.W32(save + ContextTpidrUrw, state->TpidrUrw);
        GuestMemory.W32(save + ContextTpidrUro, state->TpidrUro);
        GuestMemory.W32(save + ContextDfsr, state->Dfsr);
        GuestMemory.W32(save + ContextIfsr, state->Ifsr);
        GuestMemory.W32(save + ContextDfar, state->Dfar);
        GuestMemory.W32(save + ContextIfar, state->Ifar);
        uint intrWord = IntrPerCpuWordAddress();
        GuestMemory.W32(save + ContextIntrWord, intrWord == 0 ? 0 : GuestMemory.R32(intrWord));
        if (vfp != 0)
        {
            bool compact = (vfp & 1) != 0;
            uint calleeSaved = compact ? vfp & ~1u : vfp + 0x40;
            ulong* registers = (ulong*)state->D;
            for (int i = 0; i < 8; i++)
            {
                GuestMemory.Write(calleeSaved + (uint)i * 8, registers[8 + i]);
            }

            uint control = compact ? calleeSaved + 0x40 : calleeSaved + 0xC0;
            GuestMemory.W32(control, state->Fpscr);
            GuestMemory.W32(control + 4, 0);
        }

        return 0;
    }

    /// <summary>NID_08FDB0CE(context, unused, vfp): restores a context. Kind 0 returns from its save a second time
    /// (r0 = 1, r1 the per-CPU block), with the VFP callee-saved state from <paramref name="vfp"/>; kind 1 resumes the
    /// interrupted state whole.</summary>
    [Hle(Nid = 0x08FDB0CE, Library = "SceIntrmgrForKernel")]
    unsafe void NID_08FDB0CE(HleContext context, uint restore, uint unused, uint vfp)
    {
        CpuState* state = context.Cpu;
        uint intrWord = IntrPerCpuWordAddress();
        if (intrWord != 0)
        {
            GuestMemory.W32(intrWord, GuestMemory.R32(restore + ContextIntrWord));
        }

        state->TpidrUrw = GuestMemory.R32(restore + ContextTpidrUrw);
        state->TpidrUro = GuestMemory.R32(restore + ContextTpidrUro);
        state->Dfsr = GuestMemory.R32(restore + ContextDfsr);
        state->Ifsr = GuestMemory.R32(restore + ContextIfsr);
        state->Dfar = GuestMemory.R32(restore + ContextDfar);
        state->Ifar = GuestMemory.R32(restore + ContextIfar);
        for (int i = 0; i < 8; i++)
        {
            state->R[4 + i] = GuestMemory.R32(restore + ContextR4 + (uint)i * 4);
        }

        state->R[13] = GuestMemory.R32(restore + ContextSp);
        if (GuestMemory.R32(restore + ContextKind) == 1)
        {
            for (int i = 0; i < 4; i++)
            {
                state->R[i] = GuestMemory.R32(restore + ContextR0 + (uint)i * 4);
            }

            state->R[12] = GuestMemory.R32(restore + ContextR4 + 0x20);
            state->R[14] = GuestMemory.R32(restore + ContextLr);
            uint pc = GuestMemory.R32(restore + ContextFullPc);
            SetFlags(state, GuestMemory.R32(restore + ContextFullCpsr));
            state->Thumb = (GuestMemory.R32(restore + ContextFullCpsr) & CpsrThumb) != 0 ? 1u : 0u;
            state->ITState = 0;
            state->R[15] = state->Thumb != 0 ? pc & ~1u : pc & ~3u;
            if (context.Thread != null)
            {
                context.Thread.ContextReplaced = true;
            }

            return;
        }

        if (vfp != 0)
        {
            bool compact = (vfp & 1) != 0;
            uint calleeSaved = compact ? vfp & ~1u : vfp + 0x40;
            ulong* registers = (ulong*)state->D;
            for (int i = 0; i < 8; i++)
            {
                registers[8 + i] = GuestMemory.Read<ulong>(calleeSaved + (uint)i * 8);
            }

            state->Fpscr = GuestMemory.R32(compact ? calleeSaved + 0x40 : calleeSaved + 0xC0);
        }

        SetFlags(state, GuestMemory.R32(restore + ContextCpsr));
        state->R[14] = GuestMemory.R32(restore + ContextLr);
        state->R[0] = 1;
        state->R[1] = intrWord == 0 ? 0 : intrWord - 4;
    }

    static unsafe void SetFlags(CpuState* state, uint cpsr)
    {
        state->N = (cpsr >> 31) & 1;
        state->Z = (cpsr >> 30) & 1;
        state->C = (cpsr >> 29) & 1;
        state->V = (cpsr >> 28) & 1;
        state->Q = (cpsr >> 27) & 1;
    }

    /// <summary>NID_71E08D71(destination, source): copies a context (0x200 bytes) and answers the ends of both
    /// (destination + 0x200 in r0, source + 0x200 in r1).</summary>
    [Hle(Nid = 0x71E08D71, Library = "SceIntrmgrForKernel", ReturnsValue = true)]
    ulong NID_71E08D71(uint destination, uint source)
    {
        GuestMemory.Copy(destination, source, IntrContextSize);
        return (destination + IntrContextSize) | ((ulong)(source + IntrContextSize) << 32);
    }
}
