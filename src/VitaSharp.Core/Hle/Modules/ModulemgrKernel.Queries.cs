using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceModulemgrForKernel queries and small entries as the FW 3.74 modulemgr.skprx has them (decompiled, study only):
/// a module's path, entry point and fingerprint (the module object's +0x70, +0xBC and +0x38), the module that holds an
/// address in a process (its segments), the process's main module, loading and starting a module in a process, and
/// the words kernel code registers (the syscall table, the owner word, two hook words).
/// </summary>
public sealed partial class ModulemgrKernel
{
    /// <summary>The flags ksceKernelLoadModuleForPid accepts (anything else is SCE_KERNEL_ERROR_INVALID_FLAGS).</summary>
    const uint LoadForPidFlags = ~0xFFF8260Fu;

    /// <summary>A second owner in NID_74A4E0EE, and a release by a different one in NID_ACBC97C9.</summary>
    const int OwnerTaken = unchecked((int)0x8002D01D), OwnerNotFound = unchecked((int)0x8002D01C);

    /// <summary>No module list for the process (ksceKernelGetModuleCBByAddr).</summary>
    const int NoProcessModules = unchecked((int)0x8002D080);

    /// <summary>The syscall table kernel code fills (ksceKernelRegisterSyscall): number -> function.</summary>
    [SnapshotMissing] // states of builds before it: no syscall had been registered
    readonly Dictionary<uint, uint> _syscalls = new();

    [SnapshotMissing] // states of builds before it: never set
    int _ownerWord;

    [SnapshotMissing] // states of builds before it: never set
    uint _hookWord0, _hookWord1;

    /// <summary>The function kernel code registered for syscall <paramref name="number"/> (0: none).</summary>
    internal uint Syscall(uint number)
    {
        lock (_syscalls)
        {
            return _syscalls.GetValueOrDefault(number);
        }
    }

    /// <summary>The two words NID_B23286B8 keeps.</summary>
    internal (uint First, uint Second) HookWords => (_hookWord0, _hookWord1);

    /// <summary>ksceKernelGetModulePath(modid, char *path, size): the module's path (strncpy of size bytes);
    /// SCE_KERNEL_ERROR_LIBRARYDB_NO_MOD without such a module.</summary>
    [Hle(Nid = 0x79E761B5, Library = "SceModulemgrForKernel")]
    int ksceKernelGetModulePath(int modid, uint path, uint size)
    {
        if (AnyModule(modid) is not { } module)
        {
            return SceError.LIBRARYDB_NO_MOD;
        }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(module.Path);
        Span<byte> destination = GuestMemory.Span(path, (int)size);
        destination.Clear();
        bytes.AsSpan(0, Math.Min(bytes.Length, (int)size)).CopyTo(destination);
        return 0;
    }

    /// <summary>ksceKernelGetModuleEntryPoint(modid): the module's entry (module_start); 0 without such a module.</summary>
    [Hle(Nid = 0x7B302C5D, Library = "SceModulemgrForKernel", ReturnsValue = true)]
    uint ksceKernelGetModuleEntryPoint(int modid) => AnyModule(modid)?.StartEntry ?? 0;

    /// <summary>ksceKernelGetModuleFingerprint(modid, uint *fingerprint): the module's fingerprint (its module NID).</summary>
    [Hle(Nid = 0x337A3908, Library = "SceModulemgrForKernel")]
    int ksceKernelGetModuleFingerprint(int modid, uint fingerprint)
    {
        if (AnyModule(modid) is not { } module)
        {
            return SceError.LIBRARYDB_NO_MOD;
        }

        GuestMemory.W32(fingerprint, module.ModuleNid);
        return 0;
    }

    /// <summary>ksceKernelGetModuleIdByPid(pid): the process's main module; the kernel's pid or a process without one is
    /// SCE_KERNEL_ERROR_LIBRARYDB_NO_MOD.</summary>
    [Hle(Nid = 0x679F5144, Library = "SceModulemgrForKernel")]
    int ksceKernelGetModuleIdByPid(int pid)
    {
        if (pid == KernelSpace.KernelPid || ProcessOf(pid)?.MainModule is not { } main)
        {
            return SceError.LIBRARYDB_NO_MOD;
        }

        return main.Uid;
    }

    /// <summary>The process <paramref name="pid"/> stands for (0: the current thread's).</summary>
    int PidOrCurrent(int pid)
    {
        if (pid != 0)
        {
            return pid;
        }

        return GuestThread.Current is { KernelMode: false } thread ? thread.Pid : KernelSpace.KernelPid;
    }

    /// <summary>The module of process <paramref name="pid"/> one of whose segments holds <paramref name="address"/>.</summary>
    LoadedModule? ModuleAt(List<LoadedModule> modules, uint address)
    {
        foreach (LoadedModule module in modules)
        {
            foreach (SegmentInfo segment in module.Segments.Values)
            {
                if (address - segment.Address < segment.Size)
                {
                    return module;
                }
            }
        }

        return null;
    }

    /// <summary>ksceKernelGetModuleIdByAddrForDebugger(pid, addr): the module holding the address (pid 0: the caller's
    /// process); SCE_KERNEL_ERROR_LIBRARYDB_NO_MOD when none does.</summary>
    [Hle(Nid = 0x0C668636, Library = "SceModulemgrForKernel")]
    int ksceKernelGetModuleIdByAddrForDebugger(int pid, uint address)
    {
        if (ModulesOf(PidOrCurrent(pid)) is not { } modules)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        return ModuleAt(modules, address)?.Uid ?? SceError.LIBRARYDB_NO_MOD;
    }

    /// <summary>ksceKernelGetModuleCBByAddr(pid, addr, void **cb): the control block of the module holding the address;
    /// 0x8002D080 without the process, SCE_KERNEL_ERROR_LIBRARYDB_NO_MOD when no module holds it.</summary>
    [Hle(Nid = 0x1728612F, Library = "SceModulemgrForKernel")]
    int ksceKernelGetModuleCBByAddr(int pid, uint address, uint cbOut)
    {
        if (ModulesOf(PidOrCurrent(pid)) is not { } modules)
        {
            return NoProcessModules;
        }

        if (ModuleAt(modules, address) is not { } module)
        {
            return SceError.LIBRARYDB_NO_MOD;
        }

        return ksceKernelGetModuleCBThroughEntry(module.Uid, cbOut);
    }

    /// <summary>ksceKernelGetModuleIsSharedByAddr(pid, addr): 1 when the module holding the address is a shared module
    /// (bit 12 of its attributes), 0 otherwise or when none holds it.</summary>
    [Hle(Nid = 0xF1DE6949, Library = "SceModulemgrForKernel")]
    int ksceKernelGetModuleIsSharedByAddr(int pid, uint address)
    {
        if (ModulesOf(pid) is not { } modules || ModuleAt(modules, address) is not { } module)
        {
            return 0;
        }

        return (module.Attributes >> 12) & 1;
    }

    /// <summary>ksceKernelGetModuleInfoMinByAddr(pid, addr, SceUID *modid, uint *base, char *name): the module holding
    /// the address: its UID, the base of its first segment and its name (27 characters); each out may be null.
    /// No process is 0x8002D082.</summary>
    [Hle(Nid = 0x5564A860, Library = "SceModulemgrForKernel")]
    int ksceKernelGetModuleInfoMinByAddr(int pid, uint address, uint modid, uint baseOut, uint name)
    {
        if (ModulesOf(pid) is not { } modules)
        {
            return SceError.LIBRARYDB_NO_MOD;
        }

        if (ModuleAt(modules, address) is not { } module)
        {
            return SceError.LIBRARYDB_NO_MOD;
        }

        if (modid != 0)
        {
            GuestMemory.W32(modid, (uint)module.Uid);
        }

        if (baseOut != 0)
        {
            GuestMemory.W32(baseOut, module.Segments.TryGetValue(0, out SegmentInfo first) ? first.Address : 0);
        }

        if (name != 0)
        {
            Span<byte> destination = GuestMemory.Span(name, 0x1B);
            destination.Clear();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(module.Name);
            bytes.AsSpan(0, Math.Min(bytes.Length, 0x1B)).CopyTo(destination);
        }

        return 0;
    }

    /// <summary>
    /// ksceKernelLoadModuleForPid(pid, path, flags, opt): a user module loaded into process <paramref name="pid"/>, not
    /// started. pid 0 is 0x8002D017 (SCE_KERNEL_ERROR_MODULEMGR_INVALID_PROCESS_UID), flags outside the accepted ones
    /// SCE_KERNEL_ERROR_INVALID_FLAGS; the kernel's pid loads a kernel module.
    /// </summary>
    [Hle(Nid = 0x4E85022D, Library = "SceModulemgrForKernel")]
    int ksceKernelLoadModuleForPid(int pid, string path, uint flags, uint opt)
    {
        if (pid == 0)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        if ((flags & ~LoadForPidFlags) != 0)
        {
            return SceError.INVALID_FLAGS;
        }

        if (pid == KernelSpace.KernelPid)
        {
            return ksceKernelLoadModule(path, (int)flags, opt);
        }

        if (ProcessOf(pid) is not { } process)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        try
        {
            using (ProcessScope.Enter(process))
            {
                return process.LoadModule(path).Uid;
            }
        }
        catch (Exception e) when (Emulator.IsModuleLoadFailure(e))
        {
            return Emulator.ModuleLoadError(e);
        }
    }

    /// <summary>
    /// ksceKernelStartModuleForPid(pid, modid, args, argp, flags, opt, int *status): starts a module of process
    /// <paramref name="pid"/> as ksceKernelLoadStartModuleForPid starts it (pid 0 is 0x8002D017, flags other than 0
    /// SCE_KERNEL_ERROR_INVALID_FLAGS; a start that does not report success unloads the module).
    /// </summary>
    [Hle(Nid = 0x3FE47DDF, Library = "SceModulemgrForKernel")]
    int ksceKernelStartModuleForPid(int pid, int modid, uint args, uint argp, int flags, uint opt, uint status)
    {
        if (pid == 0)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        if (flags != 0)
        {
            return SceError.INVALID_FLAGS;
        }

        if (pid == KernelSpace.KernelPid)
        {
            return ksceKernelStartModuleThroughEntry(modid, args, argp, flags, opt, status);
        }

        if (ProcessOf(pid) is not { } process)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        if (process.Kernel.Modules.TryGetValue(modid, out LoadedModule? module) is false || module.IsKernel)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        module.Started = true;
        int result = Emulator.ModuleStartSuccess;
        if (module.StartEntry != 0)
        {
            if (process == Emu && GuestThread.Current is { KernelMode: false } caller)
            {
                result = (int)caller.CallGuest(module.StartEntry, args, argp);
            }
            else
            {
                GuestThread starter = process.Kernel.CreateThread($"start:{module.Name}", module.StartEntry, 0x10000100, 0x40000, 0, 0x70000);
                starter.Start(args, argp);
                result = starter.WaitForExit(30_000) ? starter.ExitStatus : Emulator.ModuleStartFailed;
            }
        }

        if (status != 0)
        {
            GuestMemory.W32(status, (uint)result);
        }

        if (result != Emulator.ModuleStartSuccess)
        {
            process.UnloadModule(module);
        }

        return 0;
    }

    /// <summary>ksceKernelRegisterSyscall(number, function): puts a function in syscall slot <paramref name="number"/>
    /// (below 0x1000; others are ignored).</summary>
    [Hle(Nid = 0x2E4A10A0, Library = "SceModulemgrForKernel")]
    void ksceKernelRegisterSyscall(uint number, uint function)
    {
        if (number >= 0x1000)
        {
            return;
        }

        lock (_syscalls)
        {
            _syscalls[number] = function;
        }
    }

    /// <summary>NID_74A4E0EE(owner): takes the owner word when it is free or already <paramref name="owner"/>'s; another
    /// owner's is 0x8002D01D.</summary>
    [Hle(Nid = 0x74A4E0EE, Library = "SceModulemgrForKernel")]
    int NID_74A4E0EE(int owner)
    {
        lock (_syscalls)
        {
            if (_ownerWord != 0 && _ownerWord != owner)
            {
                return OwnerTaken;
            }

            _ownerWord = owner;
            return 0;
        }
    }

    /// <summary>NID_ACBC97C9(owner): releases the owner word held by <paramref name="owner"/>; anyone else is 0x8002D01C.</summary>
    [Hle(Nid = 0xACBC97C9, Library = "SceModulemgrForKernel")]
    int NID_ACBC97C9(int owner)
    {
        lock (_syscalls)
        {
            if (_ownerWord != owner)
            {
                return OwnerNotFound;
            }

            _ownerWord = 0;
            return 0;
        }
    }

    /// <summary>NID_B23286B8(a, b): keeps two hook words.</summary>
    [Hle(Nid = 0xB23286B8, Library = "SceModulemgrForKernel")]
    void NID_B23286B8(uint first, uint second)
    {
        _hookWord0 = first;
        _hookWord1 = second;
    }
}
