using System.Runtime.CompilerServices;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// Data aborts of guest accesses to pages no block maps. On the Vita such an access raises a data abort (abort mode,
/// LR = the instruction + 8, DFSR/DFAR) that the SceExcpmgr chain hands to the handlers kernel modules registered for
/// it (kubridge forwards it to a user handler). VitaSharp's guest memory is one host reservation where such a page
/// reads zeros, so the check is explicit, and it only exists once a data abort handler is registered in the process's
/// system (<see cref="BlockCache.ArmDataAborts"/>): before that every backend runs exactly the code it always ran.
/// <list type="bullet">
/// <item>Interpreter and IL backend: the memory helpers (<c>Mem.*</c>) test <see cref="CpuState.AbortGuard"/> (set from
/// <see cref="BlockCache.DataAbortGuards"/> when a thread enters the CPU) and throw <see cref="GuestDataAbortException"/>, which the
/// dispatcher turns into <see cref="ExitReason.DataAbort"/> at the faulting instruction.</item>
/// <item>Native backends (x64 and ARM64): the frontend puts a guard before every access of code translated after
/// arming: one byte of the mapping table (<see cref="GuestMemory.MappedTable"/>, below the dirty-page table), a
/// predictable branch, and an out-of-path exit that stores the state as it was before the instruction.</item>
/// </list>
/// Arming drops every translated block of the process, so code translated before keeps no access unchecked.
/// </summary>
public static unsafe class DataAborts
{
    /// <summary>DFSR of a translation fault on a page (short descriptors, FS = 0b00111), and the WnR bit of a write.</summary>
    public const uint TranslationFault = 0x7, WriteNotRead = 1u << 11;

    /// <summary>Checks an access of <paramref name="size"/> bytes at <paramref name="addr"/> (interpreter, IL backend).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check(CpuState* s, uint addr, uint size, bool write)
    {
        if (s->AbortGuard != 0)
        {
            CheckMapped(s, addr, size, write);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckMapped(CpuState* s, uint addr, uint size, bool write)
    {
        byte* table = GuestMemory.MappedTable(s->MemBase);
        if (table[addr >> 12] == 0)
        {
            Raise(s, addr, write);
        }

        uint last = addr + size - 1;
        if ((last >> 12) != (addr >> 12) && table[last >> 12] == 0)
        {
            Raise(s, last & ~0xFFFu, write); // the access runs into the next page: the first byte that faults
        }
    }

    /// <summary>True when the page of <paramref name="addr"/> may be accessed in the space of <paramref name="s"/>.</summary>
    public static bool IsAccessible(CpuState* s, uint addr) => GuestMemory.MappedTable(s->MemBase)[addr >> 12] != 0;

    /// <summary>The instruction at <paramref name="pc"/> took the data abort just thrown (IL backend, native callouts):
    /// the block stops there (<see cref="ExitReason.DataAbort"/>, R15 and SvcPc at the instruction).</summary>
    public static void RaiseAt(CpuState* s, uint pc)
    {
        s->R[15] = pc;
        s->SvcPc = pc;
        s->Exit = ExitReason.DataAbort;
    }

    private static void Raise(CpuState* s, uint addr, bool write)
    {
        s->AbortAddr = addr;
        s->AbortStatus = TranslationFault | (write ? WriteNotRead : 0);
        throw GuestDataAbortException.Instance;
    }
}

/// <summary>
/// Thrown by the interpreter's memory helpers on a data abort (<see cref="DataAborts"/>); the fault is in
/// <see cref="CpuState.AbortAddr"/>/<see cref="CpuState.AbortStatus"/>. One instance: no allocation on the fault path.
/// </summary>
public sealed class GuestDataAbortException : Exception
{
    public static readonly GuestDataAbortException Instance = new();

    private GuestDataAbortException()
        : base("guest data abort")
    {
    }
}
