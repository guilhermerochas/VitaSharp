using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// libpaf 3.74's sce_paf_(private_)setjmp / longjmp: the same jmp_buf as libc's without the TLS key (r4-r11, sp, lr,
// d8-d15, fpscr); setjmp clears the FPSCR exception/flag bits 0xF80000FF, writes that back to the FPSCR and keeps it,
// longjmp restores everything (the whole FPSCR) and returns val, 1 for 0.
public sealed partial class Libc
{
    [Hle("sce_paf_private_setjmp", Library = "ScePafStdc")]
    unsafe int PafSetjmp(HleContext ctx, uint env)
    {
        var c = ctx.Cpu;
        for (int i = 0; i < 8; i++) GuestMemory.W32(env + (uint)i * 4, c->R[4 + i]);
        GuestMemory.W32(env + 32, c->R[13]);
        GuestMemory.W32(env + 36, c->R[14]);
        var d = (ulong*)c->D;
        for (int i = 0; i < 8; i++) GuestMemory.Write(env + 40 + (uint)i * 8, d[8 + i]);
        uint fpscr = c->Fpscr & ~0xF80000FFu;
        c->Fpscr = fpscr;
        GuestMemory.W32(env + 104, fpscr);
        return 0;
    }

    [Hle("sce_paf_private_longjmp", Library = "ScePafStdc")]
    unsafe void PafLongjmp(HleContext ctx, uint env, int val)
    {
        var c = ctx.Cpu;
        for (int i = 0; i < 8; i++) c->R[4 + i] = GuestMemory.R32(env + (uint)i * 4);
        c->R[13] = GuestMemory.R32(env + 32);
        c->R[14] = GuestMemory.R32(env + 36);
        var d = (ulong*)c->D;
        for (int i = 0; i < 8; i++) d[8 + i] = GuestMemory.Read<ulong>(env + 40 + (uint)i * 8);
        c->Fpscr = GuestMemory.R32(env + 104);
        c->R[0] = val == 0 ? 1u : (uint)val;
    }
}
