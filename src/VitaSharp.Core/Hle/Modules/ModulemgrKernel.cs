using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The kernel module manager for kernel code (SceModulemgrForDriver, SceModulemgrForKernel): kernel modules load into
/// the kernel region and start on the calling thread; the *ForPid calls load user modules into a process. Modules of
/// the kernel process (<see cref="KernelSpace.KernelPid"/>) are the kernel modules; every other pid has its user
/// modules.
/// </summary>
public sealed partial class ModulemgrKernel : HleModule
{
    // the module control block kernel code reads (ksceKernelGetModuleCB): module UID -> its record in kernel memory
    readonly Dictionary<int, uint> _moduleBlocks = new();

    Emulator? ProcessOf(int pid) => KernelSpace.ProcessOf(Emu, pid);

    List<LoadedModule>? ModulesOf(int pid) => KernelSpace.ModulesOf(Emu, pid);


    LoadedModule? KernelModule(int modid)
    {
        return K.Modules.TryGetValue(modid, out var m) && m.IsKernel ? m : null;
    }

    /// <summary>Runs module_start or module_stop: on the calling guest thread (as the firmware's module manager does),
    /// or on a new kernel thread when the call comes from the host.</summary>
    int RunEntry(LoadedModule m, uint entry, uint args, uint argp)
    {
        if (entry == 0)
        {
            return Emulator.ModuleStartSuccess;
        }

        if (GuestThread.Current is { } t)
        {
            return (int)t.CallGuest(entry, args, argp);
        }

        return Emu.StartKernelModule(m, args, argp);
    }

    /// <summary>A start that does not report success leaves the module unloaded, as the module manager does for
    /// SCE_KERNEL_START_NO_RESIDENT and SCE_KERNEL_START_FAILED.</summary>
    int Start(LoadedModule m, uint args, uint argp, uint statusOut)
    {
        m.Started = true;
        int status = RunEntry(m, m.StartEntry, args, argp);
        if (statusOut != 0)
        {
            GuestMemory.W32(statusOut, (uint)status);
        }

        if (status != Emulator.ModuleStartSuccess)
        {
            Emu.UnloadModule(m);
        }

        return 0;
    }

    /// <summary>ksceKernelLoadModule(path, flags, opt): a kernel module, not started yet.</summary>
    [Hle]
    internal int ksceKernelLoadModule(string path, int flags, uint opt)
    {
        try
        {
            return Emu.LoadModule(path, kernel: true).Uid;
        }
        catch (FileNotFoundException)
        {
            return SceError.MODULEMGR_NOENT;
        }
        catch (InvalidDataException e) when (e.Message.StartsWith("Not a SELF", StringComparison.Ordinal))
        {
            return SceError.ILLEGAL_SELF_HEADER;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // not a loadable module: the firmware's loader refuses its ELF header
            Log.Warn($"ksceKernelLoadModule({path}): {e.Message}");
            return SceError.ILLEGAL_ELF_HEADER;
        }
    }

    /// <summary>ksceKernelStartModule(modid, args, argp, flags, opt, int *status): module_start(args, argp); status is
    /// what it returned.</summary>
    [Hle]
    internal int ksceKernelStartModule(int modid, uint args, uint argp, int flags, uint opt, uint status)
    {
        if (KernelModule(modid) is not { } m)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        return Start(m, args, argp, status);
    }

    /// <summary>ksceKernelLoadStartModule(path, args, argp, flags, opt, int *status): load and start; returns the
    /// module's UID.</summary>
    [Hle]
    internal int ksceKernelLoadStartModule(string path, uint args, uint argp, int flags, uint opt, uint status)
    {
        int uid = ksceKernelLoadModule(path, flags, opt);
        if (uid < 0)
        {
            return uid;
        }

        Start(K.Modules[uid], args, argp, status);
        return uid;
    }

    /// <summary>ksceKernelStopModule(modid, args, argp, flags, opt, int *status): module_stop(args, argp).</summary>
    [Hle]
    internal int ksceKernelStopModule(int modid, uint args, uint argp, int flags, uint opt, uint status)
    {
        if (KernelModule(modid) is not { } m)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        int r = RunEntry(m, m.StopEntry, args, argp);
        if (status != 0)
        {
            GuestMemory.W32(status, (uint)r);
        }

        return 0;
    }

    /// <summary>ksceKernelUnloadModule(modid, flags, opt).</summary>
    [Hle]
    internal int ksceKernelUnloadModule(int modid, int flags, uint opt)
    {
        if (KernelModule(modid) is not { } m)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        Forget(m);
        Emu.UnloadModule(m);
        return 0;
    }

    /// <summary>ksceKernelStopUnloadModule(modid, args, argp, flags, opt, int *status).</summary>
    [Hle]
    internal int ksceKernelStopUnloadModule(int modid, uint args, uint argp, int flags, uint opt, uint status)
    {
        int r = ksceKernelStopModule(modid, args, argp, flags, opt, status);
        return r < 0 ? r : ksceKernelUnloadModule(modid, flags, opt);
    }

    void Forget(LoadedModule m)
    {
        lock (_moduleBlocks)
        {
            if (_moduleBlocks.Remove(m.Uid, out uint block))
            {
                Mem.Free(block);
            }
        }
    }

    /// <summary>
    /// ksceKernelLoadStartModuleForPid(pid, path, args, argp, flags, opt, int *status): a user module loaded into
    /// process <paramref name="pid"/> and started on a thread of that process (the caller's own thread when it is the
    /// caller's process). A start that does not report success unloads it again.
    /// </summary>
    [Hle]
    internal int ksceKernelLoadStartModuleForPid(int pid, string path, uint args, uint argp, int flags, uint opt, uint status)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return ksceKernelLoadStartModule(path, args, argp, flags, opt, status);
        }

        if (ProcessOf(pid) is not { } process)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        LoadedModule m;
        try
        {
            using (ProcessScope.Enter(process))
            {
                m = process.LoadModule(path);
            }
        }
        catch (FileNotFoundException)
        {
            return SceError.MODULEMGR_NOENT;
        }

        m.Started = true;
        int r = Emulator.ModuleStartSuccess;
        if (m.StartEntry != 0)
        {
            if (process == Emu && GuestThread.Current is { KernelMode: false } caller)
            {
                r = (int)caller.CallGuest(m.StartEntry, args, argp);
            }
            else
            {
                var t = process.Kernel.CreateThread($"start:{m.Name}", m.StartEntry, 0x10000100, 0x40000, 0, 0x70000);
                t.Start(args, argp);
                r = t.WaitForExit(30_000) ? t.ExitStatus : Emulator.ModuleStartFailed;
            }
        }

        if (status != 0)
        {
            GuestMemory.W32(status, (uint)r);
        }

        if (r != Emulator.ModuleStartSuccess)
        {
            process.UnloadModule(m);
        }

        return m.Uid;
    }

    /// <summary>ksceKernelStopModuleForPid(pid, modid, args, argp, flags, opt, int *status).</summary>
    [Hle]
    int ksceKernelStopModuleForPid(int pid, int modid, uint args, uint argp, int flags, uint opt, uint status)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return ksceKernelStopModule(modid, args, argp, flags, opt, status);
        }

        if (ModulesOf(pid)?.FirstOrDefault(m => m.Uid == modid) is not { } module)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        int r = module.StopEntry != 0 && GuestThread.Current is { } t ? (int)t.CallGuest(module.StopEntry, args, argp) : 0;
        if (status != 0)
        {
            GuestMemory.W32(status, (uint)r);
        }

        return 0;
    }

    /// <summary>ksceKernelUnloadModuleForPid(pid, modid, flags, opt).</summary>
    [Hle]
    int ksceKernelUnloadModuleForPid(int pid, int modid, int flags, uint opt)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return ksceKernelUnloadModule(modid, flags, opt);
        }

        if (ProcessOf(pid) is not { } process || ModulesOf(pid)?.FirstOrDefault(m => m.Uid == modid) is not { } module || module == process.MainModule)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        process.UnloadModule(module);
        return 0;
    }

    /// <summary>ksceKernelStopUnloadModuleForPid(pid, modid, args, argp, flags, opt, int *status).</summary>
    [Hle]
    int ksceKernelStopUnloadModuleForPid(int pid, int modid, uint args, uint argp, int flags, uint opt, uint status)
    {
        int r = ksceKernelStopModuleForPid(pid, modid, args, argp, flags, opt, status);
        return r < 0 ? r : ksceKernelUnloadModuleForPid(pid, modid, flags, opt);
    }

    /// <summary>ksceKernelGetModuleInfo(pid, modid, SceKernelModuleInfo *info): the 0x1B8-byte record of a module of
    /// process <paramref name="pid"/>.</summary>
    [Hle]
    int ksceKernelGetModuleInfo(int pid, int modid, uint info)
    {
        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (ModulesOf(pid) is not { } modules)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        if (modules.FirstOrDefault(m => m.Uid == modid) is not { } module)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        return SystemMisc.WriteModuleInfo(module, info);
    }

    /// <summary>ksceKernelGetModuleList(pid, flags1, flags2, SceUID *modids, SceSize *num): the UIDs of the process's
    /// modules in load order, at most *num; *num becomes the number written.</summary>
    [Hle]
    int ksceKernelGetModuleList(int pid, int flags1, int flags2, uint modids, uint num)
    {
        if (modids == 0 || num == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (ModulesOf(pid) is not { } modules)
        {
            return SceError.MODULEMGR_INVALID_PROCESS_UID;
        }

        int n = (int)Math.Min((uint)modules.Count, GuestMemory.R32(num));
        for (int i = 0; i < n; i++)
        {
            GuestMemory.W32(modids + (uint)i * 4, (uint)modules[i].Uid);
        }

        GuestMemory.W32(num, (uint)n);
        return 0;
    }

    /// <summary>
    /// ksceKernelGetModuleCB (ksceKernelGetModuleInternal)(modid, void **cb): the module manager's control block of a
    /// module, in kernel memory. Only the fields kernel plugins read (FW 3.60 layout, as taiHEN reads it) are filled:
    /// +0xC the module's kernel UID, +0x10 its process UID (the same here), +0x1C a pointer to its name, +0x20/+0x24
    /// the export table range, +0x28/+0x2C the import table range, +0x30 the module NID.
    /// </summary>
    [Hle]
    int ksceKernelGetModuleCB(int modid, uint cbOut)
    {
        if (cbOut == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (!K.Modules.TryGetValue(modid, out var m))
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        uint cb;
        lock (_moduleBlocks)
        {
            if (!_moduleBlocks.TryGetValue(modid, out cb))
            {
                using (ProcessScope.Enter(KernelSpace.KernelPid))
                {
                    cb = Mem.Alloc(0x40, $"kernel:modulecb:{m.Name}", MemoryRegion.Kernel);
                }

                GuestMemory.W32(cb + 0x0C, (uint)m.Uid);
                GuestMemory.W32(cb + 0x10, (uint)m.Uid);
                GuestMemory.W32(cb + 0x1C, m.ModuleInfoAddr + 4);
                GuestMemory.W32(cb + 0x20, m.ExportsStart);
                GuestMemory.W32(cb + 0x24, m.ExportsEnd);
                GuestMemory.W32(cb + 0x28, m.ImportsStart);
                GuestMemory.W32(cb + 0x2C, m.ImportsEnd);
                GuestMemory.W32(cb + 0x30, m.ModuleNid);
                _moduleBlocks[modid] = cb;
            }
        }

        GuestMemory.W32(cbOut, cb);
        return 0;
    }
}
