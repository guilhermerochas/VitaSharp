using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibGcc (libkernel.suprx 3.74): the ARM EHABI unwinder entry points for code with its own C++ runtime, over the
/// same unwinder as the HLE C library (Libc.Eh.cs). The _Unwind_Context is libkernel's phase VRS: +0 demand-save
/// flags, +4 r0-r15, +0x44 prev_sp, +0x48 d0-d31; personality routines find the UCB in its r12.
/// </summary>
public sealed class LibGcc : HleModule
{
    const int UVRSR_OK = 0, UVRSR_NOT_IMPLEMENTED = 1, UVRSR_FAILED = 2;
    const int UVRSC_CORE = 0, UVRSC_VFP = 1;
    const int UVRSD_UINT32 = 0, UVRSD_VFPX = 1, UVRSD_DOUBLE = 5;

    Libc? _libc;
    Libc C => _libc ??= Emu.Hle.Modules.OfType<Libc>().First();

    static uint Core(uint ctx, int r) => ctx + Libc.Vrs.GuestCore + (uint)r * 4;
    static uint Vfp(uint ctx, int d) => ctx + Libc.Vrs.GuestVfp + (uint)d * 8;

    [Hle]
    int _Unwind_VRS_Get(uint ctx, int regclass, uint regno, int representation, uint valuep)
    {
        switch (regclass)
        {
            case UVRSC_CORE:
                if (representation != UVRSD_UINT32 || regno > 15) return UVRSR_FAILED;
                GuestMemory.W32(valuep, GuestMemory.R32(Core(ctx, (int)regno)));
                return UVRSR_OK;
            case UVRSC_VFP:
                if ((representation != UVRSD_VFPX && representation != UVRSD_DOUBLE) || regno > 31) return UVRSR_FAILED;
                GuestMemory.Write(valuep, GuestMemory.Read<ulong>(Vfp(ctx, (int)regno)));
                return UVRSR_OK;
            case 3: case 4: return UVRSR_NOT_IMPLEMENTED; // iWMMX
            default: return UVRSR_FAILED;
        }
    }

    [Hle]
    int _Unwind_VRS_Set(uint ctx, int regclass, uint regno, int representation, uint valuep)
    {
        switch (regclass)
        {
            case UVRSC_CORE:
                if (representation != UVRSD_UINT32 || regno > 15) return UVRSR_FAILED;
                GuestMemory.W32(Core(ctx, (int)regno), GuestMemory.R32(valuep));
                return UVRSR_OK;
            case UVRSC_VFP:
                if ((representation != UVRSD_VFPX && representation != UVRSD_DOUBLE) || regno > 31) return UVRSR_FAILED;
                GuestMemory.Write(Vfp(ctx, (int)regno), GuestMemory.Read<ulong>(valuep));
                return UVRSR_OK;
            case 3: case 4: return UVRSR_NOT_IMPLEMENTED;
            default: return UVRSR_FAILED;
        }
    }

    /// <summary>_Unwind_VRS_Pop(ctx, class, discriminator, representation): core registers by mask (sp, if in the
    /// mask, becomes the loaded value), VFP doubles by (first &lt;&lt; 16 | count), FSTMFDX (VFPX) with its pad word.</summary>
    [Hle]
    int _Unwind_VRS_Pop(uint ctx, int regclass, uint discriminator, int representation)
    {
        uint sp = GuestMemory.R32(Core(ctx, 13));
        switch (regclass)
        {
            case UVRSC_CORE:
            {
                if (representation != UVRSD_UINT32) return UVRSR_FAILED;
                uint mask = discriminator & 0xFFFF, newSp = 0;
                bool spPopped = false;
                for (int i = 0; i < 16; i++)
                {
                    if ((mask & (1u << i)) == 0) continue;
                    uint v = GuestMemory.R32(sp);
                    sp += 4;
                    if (i == 13) { spPopped = true; newSp = v; } else GuestMemory.W32(Core(ctx, i), v);
                }
                GuestMemory.W32(Core(ctx, 13), spPopped ? newSp : sp);
                return UVRSR_OK;
            }
            case UVRSC_VFP:
            {
                if (representation != UVRSD_VFPX && representation != UVRSD_DOUBLE) return UVRSR_FAILED;
                int first = (int)(discriminator >> 16), count = (int)(discriminator & 0xFFFF);
                if (first + count > 32) return UVRSR_FAILED;
                for (int i = 0; i < count; i++, sp += 8) GuestMemory.Write(Vfp(ctx, first + i), GuestMemory.Read<ulong>(sp));
                if (representation == UVRSD_VFPX) sp += 4;
                GuestMemory.W32(Core(ctx, 13), sp);
                return UVRSR_OK;
            }
            case 3: case 4: return UVRSR_NOT_IMPLEMENTED;
            default: return UVRSR_FAILED;
        }
    }

    [Hle] uint _Unwind_GetCFA(uint ctx) => GuestMemory.R32(ctx + Libc.Vrs.GuestPrevSp);

    /// <summary>The LSDA of the frame whose UCB is in the context's r12: after the personality word and the unwind
    /// instruction words.</summary>
    [Hle]
    uint _Unwind_GetLanguageSpecificData(uint ctx)
    {
        uint ehtp = GuestMemory.R32(GuestMemory.R32(Core(ctx, 12)) + Libc.UcbEhtp);
        return ehtp + 8 + 4u * GuestMemory.Read<byte>(ehtp + 7);
    }

    [Hle] uint _Unwind_GetRegionStart(uint ctx) => GuestMemory.R32(GuestMemory.R32(Core(ctx, 12)) + Libc.UcbFnStart);

    /// <summary>Not available on ARM: libkernel aborts.</summary>
    [Hle("_Unwind_GetDataRelBase")]
    [Hle("_Unwind_GetTextRelBase")]
    void RelBase()
    {
        Util.Log.Error("[guest] _Unwind_GetDataRelBase/_Unwind_GetTextRelBase: not on ARM (abort)");
        Emu.Hle.Modules.OfType<Libc>().First().AbortProcess();
    }

    [Hle] void _Unwind_DeleteException(uint ucb) => C.DeleteException(ucb);

    [Hle]
    void _Unwind_RaiseException(HleContext ctx, uint ucb)
    {
        if (C.RaiseException(ctx, ucb, Libc.Vrs.AtCall(ctx)) != Libc.URC_INSTALL_CONTEXT) ctx.Return(Libc.URC_FAILURE);
    }

    [Hle("__Unwind_Resume")]
    void Resume(HleContext ctx, uint ucb)
    {
        if (C.ResumeUnwind(ctx, ucb, Libc.Vrs.AtCall(ctx)) != Libc.URC_INSTALL_CONTEXT) C.AbortProcess();
    }

    /// <summary>A rethrow: phase 1 and 2 again, or for a forced unwind its phase 2 from here.</summary>
    [Hle]
    void _Unwind_Resume_or_Rethrow(HleContext ctx, uint ucb)
    {
        var v = Libc.Vrs.AtCall(ctx);
        int r = GuestMemory.R32(ucb + Libc.UcbStopFn) == 0 ? C.RaiseException(ctx, ucb, v)
            : C.ForcedUnwind(ctx, ucb, GuestMemory.R32(ucb + Libc.UcbStopFn), GuestMemory.R32(ucb + Libc.UcbStopArg), v);
        if (r != Libc.URC_INSTALL_CONTEXT) ctx.Return(Libc.URC_FAILURE);
    }

    [Hle]
    void _Unwind_ForcedUnwind(HleContext ctx, uint ucb, uint stop, uint arg)
    {
        if (C.ForcedUnwind(ctx, ucb, stop, arg, Libc.Vrs.AtCall(ctx)) != Libc.URC_INSTALL_CONTEXT) ctx.Return(Libc.URC_FAILURE);
    }

    /// <summary>_Unwind_Backtrace(trace, arg): trace(context, arg) for each frame from the caller's, the frame then
    /// virtually unwound by its personality routine (VIRTUAL_UNWIND_FRAME | FORCE_UNWIND); END_OF_STACK at the end,
    /// FAILURE when a frame cannot be unwound or trace returns non-zero.</summary>
    [Hle]
    int _Unwind_Backtrace(HleContext ctx, uint trace, uint arg)
    {
        var t = GuestThread.Current;
        if (t == null) return Libc.URC_FAILURE;
        var v = Libc.Vrs.AtCall(ctx);
        uint ucb = C.AllocScratch(Libc.UcbSize), gctx = C.AllocScratch(Libc.Vrs.GuestSize);
        try
        {
            int code = Libc.URC_OK;
            while (code != Libc.URC_END_OF_STACK && code != Libc.URC_FAILURE)
            {
                if (C.FindFrame(ucb, v.Pc) != Libc.URC_OK) { code = Libc.URC_FAILURE; break; }
                v.R[12] = ucb;
                v.Store(gctx);
                if (t.CallGuest(trace, gctx, arg) != 0) { code = Libc.URC_FAILURE; break; }
                v = Libc.Vrs.Load(gctx);
                code = C.FramePersonality(Libc.US_VIRTUAL_UNWIND_FRAME | Libc.US_FORCE_UNWIND, ucb, v);
            }
            return code == Libc.URC_FAILURE ? Libc.URC_FAILURE : code;
        }
        finally
        {
            C.FreeScratch(ucb);
            C.FreeScratch(gctx);
        }
    }
}
