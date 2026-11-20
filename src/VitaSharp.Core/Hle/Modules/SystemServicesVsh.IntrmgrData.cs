using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceIntrmgrForKernel's data words and queries (os0:kd/intrmgr.skprx 3.74, decompiled, study only). The firmware keeps,
// from 0x81008240, two words and a 16-byte record the kernel sets, then the interrupt stacks of the four cores (sixteen
// of 0x2000 bytes each per core, the top at 0x81088280), and after the top the words of the boot, the trace hooks and
// the syscall table. VitaSharp keeps a block of the same layout in kernel memory, made on first use, so the stack
// addresses NID_01911612 answers and the words read back are the firmware's. The trace hooks the firmware patches into
// its vector code (NID_8D6E1289, NID_217AC350) are kept as set: VitaSharp's interrupt entry is not that code.
// ksceKernelQueryIntrHandlerInfo reports a line (or one of its sub-interrupts) from the line table.
public sealed partial class SystemServicesVsh
{
    /// <summary>The firmware address the data block stands for.</summary>
    const uint IntrDataBase = 0x81008240;

    /// <summary>From 0x81008240 to past the last word read (0x810882E4).</summary>
    const uint IntrDataSize = 0x800B0;

    /// <summary>The top of the interrupt stacks (0x81088280) inside the block.</summary>
    const uint IntrStackTop = 0x81088280 - IntrDataBase;

    const uint IntrStackPerCore = 0x20000, IntrStackSize = 0x2000;

    /// <summary>The size of SceKernelIntrHandlerInfo.</summary>
    const uint IntrHandlerInfoSize = 0x70;

    [SnapshotMissing] // states of builds before it: no call had made it
    uint _intrData;

    [SnapshotIgnore]
    readonly object _intrDataLock = new();

    uint IntrData()
    {
        lock (_intrDataLock)
        {
            if (_intrData == 0)
            {
                using (ProcessScope.Enter(KernelSpace.KernelPid))
                {
                    _intrData = Mem.Alloc(IntrDataSize, "kernel:SceIntrmgrData", MemoryRegion.Kernel);
                }

                GuestMemory.Fill(_intrData, 0, IntrDataSize);
            }

            return _intrData;
        }
    }

    /// <summary>The thread manager's timer line (its SceLT5 timer's interrupt, "SceThreadmgrTimer").</summary>
    const uint ThreadmgrTimerLine = 0x8D;

    /// <summary>Arms the thread manager's timer line for guest microsecond <paramref name="deadlineUs"/>: the line is
    /// registered (enabled, priority 0x30, every core, as the thread manager registers it at boot) with
    /// <paramref name="body"/> as its handler the first time.</summary>
    internal void ArmThreadmgrTimer(ulong deadlineUs, Action<HleContext> body)
    {
        IntrDevices();
        if (_intrLines[ThreadmgrTimerLine].Handler == 0)
        {
            IntrRegisterDevice(ThreadmgrTimerLine, "SceThreadmgrTimer", 0, 0x30, 0xF, body);
            _intrLines[ThreadmgrTimerLine].Enabled = true;
        }

        IntrScheduler.Arm(ThreadmgrTimerLine, deadlineUs);
    }

    /// <summary>No alarm left: the thread manager's timer line no longer fires.</summary>
    internal void DisarmThreadmgrTimer() => _intrScheduler?.Disarm(ThreadmgrTimerLine);

    uint IntrWordAddress(uint firmwareAddress) => IntrData() + (firmwareAddress - IntrDataBase);

    /// <summary>NID_01911612(core, n): the top of interrupt stack <paramref name="n"/> (1-16) of core 0-3; 0 otherwise.</summary>
    [Hle(Nid = 0x01911612, Library = "SceIntrmgrForKernel", ReturnsValue = true)]
    uint NID_01911612(uint core, uint n)
    {
        if (core >= 4 || n == 0 || n > 16)
        {
            return 0;
        }

        return IntrData() + IntrStackTop - core * IntrStackPerCore - (n - 1) * IntrStackSize;
    }

    /// <summary>NID_ABEB5A69(value): the 0x81008240 word.</summary>
    [Hle(Nid = 0xABEB5A69, Library = "SceIntrmgrForKernel")]
    void NID_ABEB5A69(uint value) => GuestMemory.W32(IntrWordAddress(0x81008240), value);

    /// <summary>NID_7E3D4560(value): the 0x81008244 word.</summary>
    [Hle(Nid = 0x7E3D4560, Library = "SceIntrmgrForKernel")]
    void NID_7E3D4560(uint value) => GuestMemory.W32(IntrWordAddress(0x81008244), value);

    /// <summary>NID_04E635AA(record): copies the 16-byte record to 0x81008248.</summary>
    [Hle(Nid = 0x04E635AA, Library = "SceIntrmgrForKernel")]
    void NID_04E635AA(uint record) => GuestMemory.Copy(IntrWordAddress(0x81008248), record, 0x10);

    /// <summary>NID_9CB39268(value): the 0x81088280 word (at the top of the stacks).</summary>
    [Hle(Nid = 0x9CB39268, Library = "SceIntrmgrForKernel")]
    void NID_9CB39268(uint value) => GuestMemory.W32(IntrWordAddress(0x81088280), value);

    /// <summary>NID_46F04D82(value): the 0x81088284 word.</summary>
    [Hle(Nid = 0x46F04D82, Library = "SceIntrmgrForKernel")]
    void NID_46F04D82(uint value) => GuestMemory.W32(IntrWordAddress(0x81088284), value);

    /// <summary>NID_764F140A(value): the 0x81088288 word.</summary>
    [Hle(Nid = 0x764F140A, Library = "SceIntrmgrForKernel")]
    void NID_764F140A(uint value) => GuestMemory.W32(IntrWordAddress(0x81088288), value);

    /// <summary>NID_807B2004(): the 0x810882D0 word.</summary>
    [Hle(Nid = 0x807B2004, Library = "SceIntrmgrForKernel", ReturnsValue = true)]
    uint NID_807B2004() => GuestMemory.R32(IntrWordAddress(0x810882D0));

    /// <summary>NID_8D6E1289(irqHook, fiqHook): the IRQ and FIQ entry trace hooks (0x810882E4, 0x810882C4).</summary>
    [Hle(Nid = 0x8D6E1289, Library = "SceIntrmgrForKernel")]
    void NID_8D6E1289(uint irqHook, uint fiqHook)
    {
        GuestMemory.W32(IntrWordAddress(0x810882E4), irqHook);
        GuestMemory.W32(IntrWordAddress(0x810882C4), fiqHook);
    }

    /// <summary>NID_217AC350(entryHook, exitHook): the handler entry and exit trace hooks (0x810882C8, 0x810882CC).</summary>
    [Hle(Nid = 0x217AC350, Library = "SceIntrmgrForKernel")]
    void NID_217AC350(uint entryHook, uint exitHook)
    {
        GuestMemory.W32(IntrWordAddress(0x810882C8), entryHook);
        GuestMemory.W32(IntrWordAddress(0x810882CC), exitHook);
    }

    /// <summary>NID_BCD3BB05(): enables IRQs on the current core (what ksceKernelCpuResumeIntr(0) does).</summary>
    [Hle(Nid = 0xBCD3BB05, Library = "SceIntrmgrForKernel")]
    unsafe void NID_BCD3BB05(HleContext context)
    {
        if (Emu.Hle.ImplOf(0xF5BAD43B) is { } resume)
        {
            uint saved = context.Cpu->R[0];
            context.Cpu->R[0] = 0;
            resume(context);
            context.Cpu->R[0] = saved;
        }
    }

    /// <summary>NID_B5A665C7(count, uint *base): allocates the syscall table once (a kernel block of
    /// <paramref name="count"/> words, "SceSyscallTable") and answers its base; -1 for a count of 0 or a second call.</summary>
    [Hle(Nid = 0xB5A665C7, Library = "SceIntrmgrForKernel")]
    int NID_B5A665C7(uint count, uint baseOut)
    {
        uint table = IntrWordAddress(0x810882D4);
        if (count == 0 || GuestMemory.R32(table) != 0)
        {
            return -1;
        }

        uint block;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            block = Mem.Alloc(count * 4, "SceSyscallTable", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(block, 0, count * 4);
        GuestMemory.W32(table, block);
        GuestMemory.W32(baseOut, block);
        return 0;
    }

    /// <summary>
    /// ksceKernelQueryIntrHandlerInfo(line, sub, SceKernelIntrHandlerInfo *info): the info of a line (sub -1, or a
    /// line without sub-interrupts) or of one sub-interrupt: +4 the handler, +8 its argument, +0xC the line, +0xE the
    /// trigger flags, +0x12 the priority, +0x14 the target CPUs, +0x16 the GIC's enable bit. A line past 255 or a sub
    /// past the line's count is 0x80027102, a size other than 0x70 0x80027103.
    /// </summary>
    [Hle(Nid = 0xF769CEFA, Library = "SceIntrmgrForKernel")]
    int ksceKernelQueryIntrHandlerInfo(uint line, int sub, uint info)
    {
        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if (GuestMemory.R32(info) != IntrHandlerInfoSize)
        {
            return INTR_ERR_OPTION;
        }

        IntrDevices();
        IntrLine entry;
        IntrSub subEntry = default;
        bool isSub = false;
        entry = _intrLines[line];
        if (entry.SubCount != 0 && sub != -1)
        {
            if (sub < 0 || (uint)sub >= entry.SubCount)
            {
                return INTR_ERR_CODE;
            }

            isSub = _intrSubs.TryGetValue(IntrSubKey(line, (uint)sub), out subEntry) && subEntry.Handler != 0;
        }

        uint handler = isSub ? subEntry.Handler : (entry.Hooked != 0 ? entry.Hooked : entry.Handler);
        GuestMemory.W32(info + 4, handler);
        GuestMemory.W32(info + 8, isSub ? subEntry.Argument : entry.Argument);
        GuestMemory.Write(info + 0xC, (ushort)line);
        GuestMemory.Write(info + 0xE, (ushort)(entry.Flags & 0xFF));
        GuestMemory.Write(info + 0x10, (ushort)0);
        GuestMemory.Write(info + 0x12, (ushort)entry.Priority);
        GuestMemory.Write(info + 0x14, (ushort)entry.Target);
        GuestMemory.Write(info + 0x16, (ushort)(entry.Enabled ? 1 : 0));
        GuestMemory.Fill(info + 0x18, 0, 0x50);
        return 0;
    }
}
