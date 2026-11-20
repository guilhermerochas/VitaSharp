using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// CPU exceptions delivered to the kernel's exception handlers. On the Vita an undefined instruction, a prefetch abort
/// or a data abort enters the exception vector in undefined or abort mode, and the SceExcpmgr vector runs the chain of
/// handlers kernel modules registered for that kind (<see cref="ProcessmgrKernel.ExceptionVector"/>); the last one is
/// the firmware's default handler, which ends the process. VitaSharp does the same when a handler is registered: the
/// thread enters the exception mode (<see cref="ExceptionModes.EnterException"/>) and runs the chain; a handler that
/// takes the exception returns to user code itself (RFE, SUBS PC, LR), and one that reaches the default handler gets
/// the fault reported as before. With no handler registered nothing changes.
/// </summary>
public sealed unsafe partial class GuestThread
{
    /// <summary>SCE_EXCP_* kinds (SceExcpmgr): undefined instruction, prefetch abort, data abort.</summary>
    public const uint ExceptionUndefined = 1, ExceptionPrefetchAbort = 3, ExceptionDataAbort = 4;

    /// <summary>DFSR/IFSR status of a translation fault on a page (short descriptors, FS = 0b00111) and the WnR bit.</summary>
    public const uint FaultTranslationPage = 0x7, FaultWrite = 1u << 11;

    /// <summary>Size of the abort and undefined mode stacks a thread gets the first time an exception enters a handler.</summary>
    private const uint ExceptionStackSize = 0x4000;

    private uint _exceptionStack;
    private string? _deliveredFault;
    private uint _deliveredFaultPc;
    private ProcessmgrKernel? _exceptionManager; // the one whose chain the thread entered

    /// <summary>CPSR.M of the thread's CPU (diagnostics and tests).</summary>
    internal uint CpuMode => Core.State->Mode;

    /// <summary>The exception manager of <paramref name="process"/> (null when it has none).</summary>
    private static ProcessmgrKernel? ExceptionManagerOf(Emulator? process)
    {
        if (process?.Hle is not { } hle)
        {
            return null;
        }

        foreach (Hle.HleModule module in hle.Modules)
        {
            if (module is ProcessmgrKernel manager)
            {
                return manager;
            }
        }

        return null;
    }

    /// <summary>
    /// The exception manager that has handlers for <paramref name="kind"/> and the vector it enters: the kernel
    /// process's (where kernel modules register them), else the thread's own process's (the kernel before a kernel
    /// process exists). Null when no handler is registered.
    /// </summary>
    private ProcessmgrKernel? ExceptionManager(uint kind, out uint vector)
    {
        vector = 0;
        if (_emu == null)
        {
            return null;
        }

        foreach (Emulator? process in new[] { _emu.Vita.KernelProcess, _emu })
        {
            if (ExceptionManagerOf(process) is { } manager && manager.ExceptionVector(kind) is not 0 and uint entry)
            {
                vector = entry;
                return manager;
            }
        }

        return null;
    }

    /// <summary>
    /// Enters the kernel's handler chain for exception <paramref name="kind"/> raised by the instruction at
    /// <paramref name="faultPc"/> (the state's pc and execution state are the faulting instruction's). False when no
    /// handler is registered or the fault happened in a handler (an exception mode), and the caller reports it.
    /// </summary>
    internal bool TryDeliverException(CpuState* s, uint kind, uint faultPc, string message)
    {
        if (s->Mode is ExceptionModes.Abort or ExceptionModes.Undefined)
        {
            return false;
        }

        if (ExceptionManager(kind, out uint vector) is not { } manager)
        {
            return false;
        }

        EnsureExceptionStacks(s, manager);
        _exceptionManager = manager;
        manager.EnterExceptionNesting();
        uint mode = kind == ExceptionUndefined ? ExceptionModes.Undefined : ExceptionModes.Abort;
        uint link;
        if (kind == ExceptionDataAbort)
        {
            link = faultPc + 8;
        }
        else if (kind == ExceptionUndefined && s->Thumb != 0)
        {
            // the address of the next instruction
            link = faultPc + (Decoder.IsThumb32(GuestMemory.Read<ushort>(faultPc)) ? 4u : 2u);
        }
        else
        {
            link = faultPc + 4;
        }

        if (kind == ExceptionPrefetchAbort)
        {
            s->Ifar = faultPc;
            s->Ifsr = FaultTranslationPage;
        }

        _deliveredFault = message;
        _deliveredFaultPc = faultPc;
        s->Exit = ExitReason.None;
        ExceptionModes.EnterException(s, mode, link, vector);
        return true;
    }

    /// <summary>
    /// True when the thread reached the default handler at the end of the chain (no handler took the exception): the
    /// interrupted state is put back and <paramref name="message"/> is the fault as VitaSharp reports it.
    /// </summary>
    internal bool ReachedDefaultExceptionHandler(CpuState* s, out string message)
    {
        message = "";
        uint code = _exceptionManager?.DefaultExceptionHandlerCode ?? 0;
        if (code == 0 || s->SvcPc != code || _deliveredFault == null)
        {
            return false;
        }

        ExceptionModes.WriteCpsr(s, ExceptionModes.ReadSpsr(s));
        s->R[15] = _deliveredFaultPc;
        message = $"{_deliveredFault} (no exception handler took it)";
        _deliveredFault = null;
        return true;
    }

    /// <summary>The thread's abort and undefined mode stacks (kernel memory), made the first time it takes an exception.</summary>
    private void EnsureExceptionStacks(CpuState* s, ProcessmgrKernel manager)
    {
        if (_exceptionStack == 0)
        {
            _exceptionStack = manager.AllocateExceptionStack(ExceptionStackSize, Name);
        }

        // an exception taken from user mode starts on an empty stack (one exception at a time per thread)
        s->BankedSp[ExceptionModes.Bank(ExceptionModes.Abort)] = _exceptionStack + ExceptionStackSize;
        s->BankedSp[ExceptionModes.Bank(ExceptionModes.Undefined)] = _exceptionStack + ExceptionStackSize / 2;
    }

    /// <summary>Save states: the privileged CPU state (<see cref="CpuState.ControlXor"/>, the banked registers and the
    /// fault registers), the exception stacks and the fault being delivered.</summary>
    private void WriteExceptionState(SaveState.SnapshotWriter w, CpuState* s)
    {
        w.Write(s->ControlXor);
        for (int i = 0; i < 6; i++)
        {
            w.Write(s->BankedSp[i]);
            w.Write(s->BankedLr[i]);
            w.Write(s->Spsr[i]);
        }

        for (int i = 0; i < 10; i++)
        {
            w.Write(s->BankedHigh[i]);
        }

        w.Write(s->Dfsr);
        w.Write(s->Dfar);
        w.Write(s->Ifsr);
        w.Write(s->Ifar);
        w.Write(s->Adfsr);
        w.Write(s->Aifsr);
        w.Write(_exceptionStack);
        w.Write(_deliveredFault);
        w.Write(_deliveredFaultPc);
    }

    private void ReadExceptionState(SaveState.SnapshotReader r, CpuState* s)
    {
        s->ControlXor = r.ReadUInt32();
        for (int i = 0; i < 6; i++)
        {
            s->BankedSp[i] = r.ReadUInt32();
            s->BankedLr[i] = r.ReadUInt32();
            s->Spsr[i] = r.ReadUInt32();
        }

        for (int i = 0; i < 10; i++)
        {
            s->BankedHigh[i] = r.ReadUInt32();
        }

        s->Dfsr = r.ReadUInt32();
        s->Dfar = r.ReadUInt32();
        s->Ifsr = r.ReadUInt32();
        s->Ifar = r.ReadUInt32();
        s->Adfsr = r.ReadUInt32();
        s->Aifsr = r.ReadUInt32();
        _exceptionStack = r.ReadUInt32();
        _deliveredFault = r.ReadString();
        _deliveredFaultPc = r.ReadUInt32();
        if (_exceptionStack != 0 || _deliveredFault != null)
        {
            // the manager whose chain the thread entered: the kernel process's, where kernel modules register handlers
            _exceptionManager = ExceptionManagerOf(_emu?.Vita.KernelProcess) ?? ExceptionManagerOf(_emu);
        }
    }

    /// <summary>Frees the exception stacks when the thread ends.</summary>
    private void FreeExceptionStacks()
    {
        if (_exceptionStack == 0)
        {
            return;
        }

        _exceptionManager?.FreeExceptionStack(_exceptionStack);
        _exceptionStack = 0;
    }
}
