using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The process information calls of the FW 3.74 processmgr.skprx (decompiled, study only), read from the process
/// object (<see cref="ProcessObject"/>): ksceKernelGetProcessInfo fills SceKernelProcessInfo (0xE8 bytes) and the
/// process list calls (NID_BA0A06A4, NID_E8005AFC, NID_EFFF2975, one function) walk every process: in mode 0 the
/// information of each but the kernel's, in mode 1 of every one, in mode 2 the UIDs, as many as the buffer holds.
/// </summary>
public sealed partial class ProcessmgrKernel
{
    const uint ProcessInfoSize = 0xE8;

    /// <summary>
    /// ksceKernelGetProcessInfo(pid, SceKernelProcessInfo *info): +4 the UID, +8, +0xC the +0x5C/+0x44 words, +0x10 the
    /// state word (bits 16-19 cleared), +0x14 +0x68, +0x18 +0x78, +0x1C +0x54, +0x20 +0x98, +0x24 the main module (or
    /// -1) and +0x28 its entry point (-1), +0x2C +0x40, +0x30 the process's name (32 bytes: its title ID, else the
    /// object's name), +0x50.. the +0x424, +0x428, +0x4C, +0x338, +0x33C words, +0x68 the 0x80 bytes at +0x208. A null
    /// info is SCE_KERNEL_ERROR_INVALID_ARGUMENT, a size other than 0xE8 0x80020009.
    /// </summary>
    [Hle(Nid = 0x0AFF3EAE, Library = "SceProcessmgrForDriver")]
    int ksceKernelGetProcessInfo(int pid, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != ProcessInfoSize)
        {
            return unchecked((int)0x80020009);
        }

        return WithProcess(pid, block =>
        {
            int uid = (int)GuestMemory.R32(block + UidWord);
            GuestMemory.W32(info + 0x04, (uint)uid);
            GuestMemory.W32(info + 0x08, GuestMemory.R32(block + 0x5C));
            GuestMemory.W32(info + 0x0C, GuestMemory.R32(block + 0x44));
            GuestMemory.W32(info + 0x10, GuestMemory.R32(block + StateWord) & 0xFFF0FFFF);
            GuestMemory.W32(info + 0x14, GuestMemory.R32(block + 0x68));
            GuestMemory.W32(info + 0x18, GuestMemory.R32(block + 0x78));
            GuestMemory.W32(info + 0x1C, GuestMemory.R32(block + 0x54));
            GuestMemory.W32(info + 0x20, GuestMemory.R32(block + 0x98));
            Emulator? process = uid == KernelSpace.KernelPid ? null : ProcessOf(uid);
            if (process?.MainModule is { } main)
            {
                GuestMemory.W32(info + 0x24, (uint)main.Uid);
                GuestMemory.W32(info + 0x28, main.StartEntry);
            }
            else
            {
                GuestMemory.W32(info + 0x24, 0xFFFFFFFF);
                GuestMemory.W32(info + 0x28, 0xFFFFFFFF);
            }

            GuestMemory.W32(info + 0x2C, GuestMemory.R32(block + 0x40));
            Span<byte> name = GuestMemory.Span(info + 0x30, 0x20);
            name.Clear();
            string text = process == null ? "SceKernelProcess" : (process.TitleId.Length > 0 ? process.TitleId : process.Describe());
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            bytes.AsSpan(0, Math.Min(bytes.Length, 0x1F)).CopyTo(name);
            GuestMemory.W32(info + 0x50, GuestMemory.R32(block + 0x424));
            GuestMemory.W32(info + 0x54, GuestMemory.R32(block + 0x428));
            GuestMemory.W32(info + 0x58, GuestMemory.R32(block + 0x4C));
            GuestMemory.W32(info + 0x5C, GuestMemory.R32(block + 0x338));
            GuestMemory.W32(info + 0x60, GuestMemory.R32(block + 0x33C));
            GuestMemory.W32(info + 0x64, 0);
            GuestMemory.Copy(info + 0x68, block + 0x208, 0x80);
            return 0;
        });
    }

    /// <summary>NID_BA0A06A4(level, buffer, size, int *count, mode): the process list.</summary>
    [Hle(Nid = 0xBA0A06A4, Library = "SceProcessmgrForDriver")]
    int NID_BA0A06A4(int level, uint buffer, uint size, uint count, int mode) => ProcessList(level, buffer, size, count, mode);

    /// <summary>NID_E8005AFC: the same list (another entry of the function).</summary>
    [Hle(Nid = 0xE8005AFC, Library = "SceProcessmgrForDriver")]
    int NID_E8005AFC(int level, uint buffer, uint size, uint count, int mode) => ProcessList(level, buffer, size, count, mode);

    /// <summary>NID_EFFF2975: the same list (the SceProcessmgrForKernel entry).</summary>
    [Hle(Nid = 0xEFFF2975, Library = "SceProcessmgrForKernel")]
    int NID_EFFF2975(int level, uint buffer, uint size, uint count, int mode) => ProcessList(level, buffer, size, count, mode);

    /// <summary>
    /// The process list: a null count or a mode outside 0-2 is SCE_KERNEL_ERROR_INVALID_ARGUMENT. Mode 0 and 1 fill a
    /// SceKernelProcessInfo per process (0 leaves the kernel's out), mode 2 a UID per process, up to what
    /// <paramref name="size"/> holds; *count is the number written and the answer the number of processes looked at.
    /// Every VitaSharp process is UID level 0, which no level leaves out.
    /// </summary>
    int ProcessList(int level, uint buffer, uint size, uint count, int mode)
    {
        if (count == 0 || mode < 0 || mode > 2)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint capacity = mode == 2 ? size / 4 : size / ProcessInfoSize;
        var pids = new List<int> { KernelSpace.KernelPid };
        foreach (Emulator process in Emu.Vita.Processes)
        {
            pids.Add(process.Pid);
        }

        int written = 0;
        int seen = 0;
        foreach (int pid in pids)
        {
            if ((uint)seen >= capacity)
            {
                seen++;
                continue;
            }

            seen++;
            if (mode == 0 && pid == KernelSpace.KernelPid)
            {
                continue;
            }

            if (mode == 2)
            {
                GuestMemory.W32(buffer + (uint)written * 4, (uint)pid);
            }
            else
            {
                uint entry = buffer + (uint)written * ProcessInfoSize;
                GuestMemory.W32(entry, ProcessInfoSize);
                int result = ksceKernelGetProcessInfoThroughEntry(pid, entry);
                if (result < 0)
                {
                    GuestMemory.W32(count, 0);
                    return result;
                }
            }

            written++;
        }

        GuestMemory.W32(count, (uint)written);
        return seen;
    }

    /// <summary>ksceKernelGetProcessLocalStorageAddr(key): the current process's block of key <paramref name="key"/>
    /// (made on first use), NULL without a current thread or for a key above 31.</summary>
    [Hle(Nid = 0xEE694840, Library = "SceProcessmgrForDriver", ReturnsValue = true)]
    uint ksceKernelGetProcessLocalStorageAddr(int key)
    {
        if (GuestThread.Current == null || (uint)key > 0x1F)
        {
            return 0;
        }

        uint address = KernelAlloc(4, "kernel:pls-out");
        try
        {
            int result = ksceKernelGetProcessLocalStorageAddrForPidThroughEntry(0, key, address, 1);
            return result < 0 ? 0 : GuestMemory.R32(address);
        }
        finally
        {
            using (ProcessScope.Enter(KernelSpace.KernelPid))
            {
                Mem.Free(address);
            }
        }
    }

    /// <summary>The process cannot change state that way (0x80029004), and the kernel's process cannot be suspended
    /// (0x80029002).</summary>
    const int ProcessStateError = unchecked((int)0x80029004), KernelProcessRefused = unchecked((int)0x80029002);

    /// <summary>The word that keeps the state a suspended process had (+0x294).</summary>
    const uint SuspendedFromWord = 0x294;

    /// <summary>
    /// ksceKernelSuspendProcess(pid, status): suspends a running process (its threads stop, the process events of a
    /// stop are sent: <see cref="Emulator.Suspend"/>), keeps the state it had at +0x294 and puts
    /// <paramref name="status"/> in the state byte. A status without bit 4 is SCE_KERNEL_ERROR_INVALID_ARGUMENT, the
    /// kernel's process 0x80029002, a process that is not running (or already suspended) 0x80029004.
    /// </summary>
    [Hle(Nid = 0x93B8E785, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int ksceKernelSuspendProcess(int pid, uint status)
    {
        int uid = ResolvePid(pid);
        if (uid == 0)
        {
            return SceError.INVALID_PID;
        }

        if (uid == KernelSpace.KernelPid)
        {
            return KernelProcessRefused;
        }

        if ((status & 0x10) == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint block = ProcessObject(uid);
        uint state = GuestMemory.R32(block + StateWord);
        if (ProcessOf(uid) is not { } process || (state & 0xFF) != RunningState || process.Suspended)
        {
            return ProcessStateError;
        }

        process.Suspend();
        GuestMemory.W32(block + SuspendedFromWord, state & 0xFF);
        GuestMemory.W32(block + StateWord, (state & 0xFFFFFF00) | (status & 0xFF));
        return 0;
    }

    /// <summary>
    /// ksceKernelResumeProcess(pid): resumes a process suspended from the running state (its state byte has bit 4 and
    /// +0x294 is 2): the threads run again (<see cref="Emulator.Resume"/>) and the state is running again; anything
    /// else is 0x80029004, a call from an interrupt handler 0x80027101.
    /// </summary>
    [Hle(Nid = 0x6894499C, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int ksceKernelResumeProcess(int pid)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        int uid = ResolvePid(pid);
        if (uid == 0 || uid == KernelSpace.KernelPid || ProcessOf(uid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        uint block = ProcessObject(uid);
        uint state = GuestMemory.R32(block + StateWord);
        if ((state & 0x10) == 0 || GuestMemory.R32(block + SuspendedFromWord) != RunningState)
        {
            return ProcessStateError;
        }

        process.Resume();
        GuestMemory.W32(block + StateWord, (state & 0xFFFFFF00) | RunningState);
        return 0;
    }
}
