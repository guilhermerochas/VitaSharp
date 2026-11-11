using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The kernel side of the emulated system: the kernel process id and what kernel code may and may not touch.
/// <para>
/// Kernel modules (the <c>.skprx</c> plugins of the plugin configuration, or modules loaded with the kernel module
/// manager) live in the kernel region (<see cref="GuestMemory.KernelStart"/>..<see cref="GuestMemory.KernelEnd"/>),
/// which every process's address space maps, as the Vita maps its kernel into every process. Their segments, the kernel
/// memory blocks and the kernel heaps belong to <see cref="KernelPid"/>: a process that ends does not free them.
/// </para>
/// <para>
/// The emulated CPU has no privilege levels, so the permission check of the firmware's user/kernel copies (the MMU
/// faults an unprivileged access to kernel memory, and the copy returns an error) is made here: a user range must be
/// mapped in the caller's address space and must not lie in memory the kernel owns.
/// </para>
/// </summary>
public static class KernelSpace
{
    /// <summary>The kernel's process id (KERNEL_PID in the SDK headers).</summary>
    public const int KernelPid = 0x10005;

    /// <summary>
    /// What a copy from or to user memory returns when the user range is not accessible to the caller. The firmware's
    /// copy routines fault on such a range and its fault handler returns an error; which one is not known (the
    /// handler is not in the decompiled modules), so this is the kernel's generic illegal-address error.
    /// </summary>
    public const int UserCopyFault = SceError.ILLEGAL_ADDR;

    /// <summary>True when <paramref name="addr"/> lies in a block the kernel owns (kernel module segment, kernel memory
    /// block, kernel heap).</summary>
    public static bool IsKernelOwned(uint addr)
    {
        return GuestMemory.Instance?.FindBlock(addr) is { Owner: KernelPid };
    }

    /// <summary>
    /// True when user code of the calling process may access the whole range: every byte is in a block mapped in the
    /// caller's address space that the kernel does not own. A zero-length range is always accessible.
    /// </summary>
    public static bool IsUserRange(uint addr, uint length)
    {
        if (length == 0)
        {
            return true;
        }

        if (GuestMemory.Instance is not { } memory)
        {
            return false;
        }

        ulong end = (ulong)addr + length;
        if (end > 0x1_0000_0000ul)
        {
            return false;
        }

        ulong at = addr;
        while (at < end)
        {
            if (memory.FindBlock((uint)at) is not { } block || block.Owner == KernelPid || !GuestMemory.IsMapped((uint)at))
            {
                return false;
            }

            at = (ulong)block.Address + block.Size;
        }

        return true;
    }

    /// <summary>The process with id <paramref name="pid"/> as kernel code names it: 0 is the caller's process,
    /// <see cref="KernelPid"/> the kernel (whose modules live in the caller's emulator, see
    /// <see cref="ModulesOf"/>); null when there is no such process.</summary>
    public static Emulator? ProcessOf(Emulator caller, int pid)
    {
        if (pid == 0 || pid == caller.Pid || pid == KernelPid)
        {
            return caller;
        }

        return caller.Vita.ByPid(pid);
    }

    /// <summary>The modules of process <paramref name="pid"/> in load order: the kernel modules for
    /// <see cref="KernelPid"/>, the process's user modules otherwise; null when there is no such process.</summary>
    public static List<Loader.LoadedModule>? ModulesOf(Emulator caller, int pid)
    {
        if (ProcessOf(caller, pid) is not { } process)
        {
            return null;
        }

        bool kernel = pid == KernelPid;
        return process.Kernel.Modules.Values.Where(m => m.IsKernel == kernel).OrderBy(m => m.Uid).ToList();
    }

    /// <summary>True when kernel code may access the whole range: every byte is in a mapped block.</summary>

    public static bool IsMappedRange(uint addr, uint length)
    {
        if (length == 0)
        {
            return true;
        }

        if (GuestMemory.Instance is not { } memory)
        {
            return false;
        }

        ulong end = (ulong)addr + length;
        if (end > 0x1_0000_0000ul)
        {
            return false;
        }

        ulong at = addr;
        while (at < end)
        {
            if (memory.FindBlock((uint)at) is not { } block)
            {
                return false;
            }

            at = (ulong)block.Address + block.Size;
        }

        return true;
    }
}
