using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The debugger's thread control of the FW 3.74 threadmgr.skprx (decompiled, study only), the calls SceCoredump and
/// the process manager make on another thread:
/// <list type="bullet">
/// <item>ksceKernelDebugSuspendThread / ksceKernelDebugResumeThread (thid, reasons): each call adds or takes away
/// suspend reasons (bits 0xF7F03) in the thread's +0x98 word; the first reason stops the thread, taking the last away
/// lets it run again. A thread cannot suspend or resume itself (or thid 0: SCE_KERNEL_ERROR_ILLEGAL_THREAD_ID), not
/// with interrupts masked (SCE_KERNEL_ERROR_CAN_NOT_WAIT), a reason it already has is 0x8002802F, taking away one it
/// does not have 0x80028030;</item>
/// <item>ksceKernelIsThreadDebugSuspended answers the +0x98 word, ksceKernelChangeThreadSuspendStatus replaces it on a
/// suspended thread;</item>
/// <item>the registers of a suspended thread (0x80028030 otherwise): ksceKernelGetThreadCpuRegisters (r0-r14, pc and
/// cpsr at +0..+0x40, the user thread ID register at +0x44, the rest of the 0x90 bytes zero) and the VFP registers
/// (d0-d31, 0x100 bytes), and setting them from a mask of which ones (r0-r14 bits 0-14, pc bit 15, cpsr bit 16 with
/// only its user-writable bits; q0-q15 one bit each).</item>
/// </list>
/// VitaSharp stops the thread with the same suspension the managed runtimes' VM calls use
/// (<see cref="GuestThread.SuspendForVm"/>): it parks at its next block boundary, its registers saved in its core's
/// state, which these calls read and write. The ForKernel and ForDriver entries of the register calls differ by the
/// register set they name (the kernel's or the user's); an HLE thread has one, so both read it.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    /// <summary>The suspend reasons the debugger calls accept.</summary>
    const uint DebugSuspendReasons = 0xF7F03;

    const int IllegalThreadId = unchecked((int)0x80028022), AlreadyDebugSuspended = unchecked((int)0x8002802F),
        NotDebugSuspended = unchecked((int)0x80028030);

    /// <summary>The CPU register structure (0x90 bytes) and the VFP one (0x100).</summary>
    const uint CpuRegistersSize = 0x90, VfpRegistersSize = 0x100;

    /// <summary>The cpsr bits a debugger may change (the rest are kept).</summary>
    const uint WritableCpsrBits = 0xF90FFE20;

    /// <summary>The suspend reasons of each debug-suspended thread (its +0x98 word), by thread UID.</summary>
    [SnapshotMissing] // states of builds before it: no thread was debug-suspended
    readonly Dictionary<int, uint> _debugSuspend = new();

    /// <summary>The thread a debugger call names: thid 0 or the caller is SCE_KERNEL_ERROR_ILLEGAL_THREAD_ID, no
    /// thread SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID.</summary>
    int DebugTarget(int thid, out GuestThread? thread)
    {
        thread = null;
        if (thid == 0 || GuestThread.Current?.Uid == thid)
        {
            return IllegalThreadId;
        }

        thread = K.Objects.Get<GuestThread>(thid);
        return thread == null ? SceError.UNKNOWN_THREAD_ID : 0;
    }

    uint DebugStatus(int thid)
    {
        lock (_debugSuspend)
        {
            return _debugSuspend.GetValueOrDefault(thid);
        }
    }

    /// <summary>ksceKernelDebugSuspendThread(thid, reasons): adds suspend reasons; the first stops the thread.</summary>
    [Hle(Nid = 0xFB3706CB, Library = "SceThreadmgrForDriver")]
    int ksceKernelDebugSuspendThread(int thid, uint reasons)
    {
        if ((reasons & DebugSuspendReasons) == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int check = DebugTarget(thid, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        if (Sysmem.InterruptsMaskedForCurrentThread)
        {
            return SceError.CAN_NOT_WAIT;
        }

        bool stop;
        lock (_debugSuspend)
        {
            uint status = _debugSuspend.GetValueOrDefault(thid);
            if ((status & reasons) != 0)
            {
                return AlreadyDebugSuspended;
            }

            stop = status == 0;
            _debugSuspend[thid] = status | reasons;
        }

        if (stop)
        {
            thread!.SuspendForVm();
        }

        return 0;
    }

    /// <summary>ksceKernelDebugResumeThread(thid, reasons): takes suspend reasons away; the last lets the thread run.</summary>
    [Hle(Nid = 0xEC8343DF, Library = "SceThreadmgrForDriver")]
    int ksceKernelDebugResumeThread(int thid, uint reasons)
    {
        if ((reasons & DebugSuspendReasons) == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int check = DebugTarget(thid, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        bool run;
        lock (_debugSuspend)
        {
            uint status = _debugSuspend.GetValueOrDefault(thid);
            if ((status & reasons) == 0)
            {
                return NotDebugSuspended;
            }

            status &= ~reasons;
            run = status == 0;
            if (run)
            {
                _debugSuspend.Remove(thid);
            }
            else
            {
                _debugSuspend[thid] = status;
            }
        }

        if (run)
        {
            thread!.ResumeForVm();
        }

        return 0;
    }

    /// <summary>ksceKernelIsThreadDebugSuspended(thid): the thread's suspend reasons (0: running); thid 0 is
    /// SCE_KERNEL_ERROR_ILLEGAL_THREAD_ID without a current thread, else not a thread.</summary>
    [Hle(Nid = 0xA0B1AB21, Library = "SceThreadmgrForDriver")]
    int ksceKernelIsThreadDebugSuspended(int thid)
    {
        if (thid == 0 && GuestThread.Current == null)
        {
            return IllegalThreadId;
        }

        if (K.Objects.Get<GuestThread>(thid) == null)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        return (int)DebugStatus(thid);
    }

    /// <summary>ksceKernelChangeThreadSuspendStatus(thid, status): replaces the suspend reasons of a debug-suspended
    /// thread and answers the earlier ones.</summary>
    [Hle(Nid = 0x04C6764B, Library = "SceThreadmgrForDriver")]
    int ksceKernelChangeThreadSuspendStatus(int thid, uint status)
    {
        if ((status & DebugSuspendReasons) == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (thid == 0 && GuestThread.Current == null)
        {
            return IllegalThreadId;
        }

        if (K.Objects.Get<GuestThread>(thid) == null)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        lock (_debugSuspend)
        {
            if (!_debugSuspend.TryGetValue(thid, out uint earlier))
            {
                return NotDebugSuspended;
            }

            _debugSuspend[thid] = status;
            return (int)earlier;
        }
    }

    /// <summary>The thread named for a register call, debug-suspended (0x80028030 otherwise).</summary>
    int SuspendedTarget(int thid, uint buffer, out GuestThread? thread)
    {
        thread = null;
        if (buffer == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        int check = DebugTarget(thid, out thread);
        if (check < 0)
        {
            return check;
        }

        return DebugStatus(thid) == 0 ? NotDebugSuspended : 0;
    }

    /// <summary>NID_C4735A7E(thid, regs): the kernel register set of a suspended thread.</summary>
    [Hle(Nid = 0xC4735A7E, Library = "SceThreadmgrForKernel")]
    int NID_C4735A7E(int thid, uint registers) => GetCpuRegisters(thid, registers);

    /// <summary>ksceKernelGetThreadCpuRegisters(thid, regs): the user register set of a suspended thread.</summary>
    [Hle(Nid = 0x5022689D, Library = "SceThreadmgrForDriver")]
    int ksceKernelGetThreadCpuRegisters(int thid, uint registers) => GetCpuRegisters(thid, registers);

    unsafe int GetCpuRegisters(int thid, uint registers)
    {
        int check = SuspendedTarget(thid, registers, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        Cpu.CpuState* state = thread!.Core.State;
        GuestMemory.Fill(registers, 0, CpuRegistersSize);
        for (int i = 0; i < 16; i++)
        {
            GuestMemory.W32(registers + (uint)i * 4, state->R[i]);
        }

        GuestMemory.W32(registers + 0x40, state->Cpsr);
        GuestMemory.W32(registers + 0x44, state->TpidrUrw);
        return 0;
    }

    /// <summary>NID_5075AE5E(thid, regs, mask): sets the registers of a suspended thread the mask names.</summary>
    [Hle(Nid = 0x5075AE5E, Library = "SceThreadmgrForKernel")]
    int NID_5075AE5E(int thid, uint registers, uint mask) => SetCpuRegisters(thid, registers, mask);

    /// <summary>NID_64E89DE9(thid, regs, mask): the SceThreadmgrForDriver entry of the same.</summary>
    [Hle(Nid = 0x64E89DE9, Library = "SceThreadmgrForDriver")]
    int NID_64E89DE9(int thid, uint registers, uint mask) => SetCpuRegisters(thid, registers, mask);

    unsafe int SetCpuRegisters(int thid, uint registers, uint mask)
    {
        int check = SuspendedTarget(thid, registers, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        Cpu.CpuState* state = thread!.Core.State;
        for (int i = 0; i < 16; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                state->R[i] = GuestMemory.R32(registers + (uint)i * 4);
            }
        }

        if ((mask & (1u << 16)) != 0)
        {
            state->Cpsr = (GuestMemory.R32(registers + 0x40) & WritableCpsrBits) | (state->Cpsr & ~WritableCpsrBits);
        }

        return 0;
    }

    /// <summary>NID_444CE648(thid, vfp): the VFP registers (d0-d31) of a suspended thread.</summary>
    [Hle(Nid = 0x444CE648, Library = "SceThreadmgrForKernel")]
    int NID_444CE648(int thid, uint vfp) => GetVfpRegisters(thid, vfp);

    /// <summary>ksceKernelGetVfpRegisterForDebugger(thid, vfp): the same (SceThreadmgrForDriver).</summary>
    [Hle(Nid = 0x5CDE387A, Library = "SceThreadmgrForDriver")]
    int ksceKernelGetVfpRegisterForDebugger(int thid, uint vfp) => GetVfpRegisters(thid, vfp);

    unsafe int GetVfpRegisters(int thid, uint vfp)
    {
        int check = SuspendedTarget(thid, vfp, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        new ReadOnlySpan<byte>(thread!.Core.State->D, (int)VfpRegistersSize).CopyTo(GuestMemory.Span(vfp, (int)VfpRegistersSize));
        return 0;
    }

    /// <summary>NID_AC726660(thid, vfp, mask): sets the q registers of a suspended thread the mask names (bit i: q i).</summary>
    [Hle(Nid = 0xAC726660, Library = "SceThreadmgrForKernel")]
    int NID_AC726660(int thid, uint vfp, uint mask) => SetVfpRegisters(thid, vfp, mask);

    /// <summary>NID_49A0B679(thid, vfp, mask): the SceThreadmgrForDriver entry of the same.</summary>
    [Hle(Nid = 0x49A0B679, Library = "SceThreadmgrForDriver")]
    int NID_49A0B679(int thid, uint vfp, uint mask) => SetVfpRegisters(thid, vfp, mask);

    unsafe int SetVfpRegisters(int thid, uint vfp, uint mask)
    {
        int check = SuspendedTarget(thid, vfp, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        var registers = new Span<byte>(thread!.Core.State->D, (int)VfpRegistersSize);
        for (int i = 0; i < 16; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                GuestMemory.Span(vfp + (uint)i * 16, 16).CopyTo(registers.Slice(i * 16, 16));
            }
        }

        return 0;
    }
}
