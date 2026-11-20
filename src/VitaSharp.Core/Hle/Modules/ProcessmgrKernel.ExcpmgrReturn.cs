using VitaSharp.Core.Cpu;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// NID_3E55B5C3 of SceExcpmgrForKernel as the FW 3.74 excpmgr.skprx has it (decompiled, study only): the return from
/// an exception to the state an exception context holds, which the thread manager calls when a thread that took an
/// exception goes back to it. The context: r0-r12 at +0x00, sp +0x34, lr +0x38, pc +0x3C, +0x40 non-zero when the VFP
/// state is saved (d0-d31 at +0x200, FPSCR +0x300), the cpsr +0x44, CPACR +0x48, FPEXC +0x4C, CONTEXTIDR +0x54,
/// TPIDRURO +0x5C, TTBR1 +0x64, DACR +0x6C, PAR +0x80. The call checks that the current core is inside one exception
/// (its word at +0 of the exception manager's data block is 1; anything else is a fatal kernel error), clears that
/// word, restores the registers and returns from the exception (the firmware's RFE from abort mode) to the saved pc in
/// the saved cpsr's mode and state. VitaSharp sets the word when it enters an exception handler chain
/// (<see cref="EnterExceptionNesting"/>); of the coprocessor registers its CPU keeps the thread ID ones.
/// </summary>
public sealed partial class ProcessmgrKernel
{
    const uint ExceptionFrameSp = 0x34, ExceptionFrameLr = 0x38, ExceptionFramePc = 0x3C, ExceptionFrameVfpSaved = 0x40,
        ExceptionFrameCpsr = 0x44, ExceptionFrameTpidrUro = 0x5C, ExceptionFrameVfp = 0x200, ExceptionFrameFpscr = 0x300;

    /// <summary>The current core is inside an exception: its nesting word (+0 of the data block, core 0) is 1.</summary>
    internal void EnterExceptionNesting() => GuestMemory.W32(ExcpmgrData(), 1);

    /// <summary>NID_3E55B5C3(context): returns from the exception to the state <paramref name="frame"/> holds.</summary>
    [Hle(Nid = 0x3E55B5C3, Library = "SceExcpmgrForKernel")]
    unsafe void NID_3E55B5C3(HleContext context, uint frame)
    {
        uint nesting = ExcpmgrData();
        if (GuestMemory.R32(nesting) != 1)
        {
            Log.Error($"exception return outside an exception (context at 0x{frame:X8})");
            throw new InvalidOperationException("kernel panic: exception return outside an exception");
        }

        GuestMemory.W32(nesting, 0);
        CpuState* state = context.Cpu;
        if (GuestMemory.R32(frame + ExceptionFrameVfpSaved) != 0)
        {
            GuestMemory.Span(frame + ExceptionFrameVfp, 0x100).CopyTo(new Span<byte>(state->D, 0x100));
            state->Fpscr = GuestMemory.R32(frame + ExceptionFrameFpscr);
        }

        state->TpidrUro = GuestMemory.R32(frame + ExceptionFrameTpidrUro);
        uint cpsr = GuestMemory.R32(frame + ExceptionFrameCpsr);
        // the firmware switches to abort mode and returns from there (RFE): CPSR = the saved cpsr, then its mode's sp
        // and lr are the saved ones
        ExceptionModes.SwitchMode(state, ExceptionModes.Abort);
        ExceptionModes.WriteCpsr(state, cpsr);
        for (int i = 0; i < 13; i++)
        {
            state->R[i] = GuestMemory.R32(frame + (uint)i * 4);
        }

        state->R[13] = GuestMemory.R32(frame + ExceptionFrameSp);
        state->R[14] = GuestMemory.R32(frame + ExceptionFrameLr);
        uint pc = GuestMemory.R32(frame + ExceptionFramePc);
        state->R[15] = state->Thumb != 0 ? pc & ~1u : pc & ~3u;
        if (context.Thread != null)
        {
            context.Thread.ContextReplaced = true;
        }
    }
}
