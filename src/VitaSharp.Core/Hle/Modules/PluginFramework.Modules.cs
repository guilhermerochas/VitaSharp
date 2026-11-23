using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The plugin framework's module calls for user programs (its "unsafe" library, emulator option "Enable Kernel
/// Plugins"): load, start, stop and unload a kernel module, or a user module in another process, and the user/kernel
/// copies. They follow the framework's published contract: only a program with the system class (SceSblACMgr's
/// capability bit 1, ksceSblACMgrIsShell of the caller) may call them (otherwise 0x90010009); the arguments come in a
/// 0x14-byte record (size, pid, args, argp, flags) copied in from user memory, and up to 256 bytes of argp are copied
/// to a kernel buffer that module_start/module_stop receive; the path is at most 255 characters; each call then goes
/// through the firmware's kernel module manager (<see cref="ModulemgrKernel"/>), the loader the plugin loader uses for
/// the *KERNEL plugins.
/// <para>
/// The framework gives the caller a process handle for the module (ksceKernelCreateUserUid) and turns it back with
/// ksceKernelKernelUidForUserUid; VitaSharp has one UID space for the kernel and the processes (see
/// <see cref="SysmemKernel"/>), so the handle is the module's own UID and the module stays the kernel's (a kernel
/// module outlives the program that loaded it, as on the Vita).
/// </para>
/// </summary>
public sealed partial class PluginFramework
{
    const int ErrorNotAllowed = unchecked((int)0x90010009);

    /// <summary>sizeof(tai_module_args_t): size, pid, args, argp, flags.</summary>
    const uint ModuleArgsSize = 0x14;

    /// <summary>The most bytes of argp the framework copies for module_start/module_stop (MAX_ARGS_SIZE).</summary>
    const uint MaxArgsSize = 256;

    ModulemgrKernel Modulemgr => Emu.Hle.Modules.OfType<ModulemgrKernel>().First();

    SysmemKernel Sysmem => Emu.Hle.Modules.OfType<SysmemKernel>().First();

    /// <summary>ksceSblACMgrIsShell(0): the calling program has the system class (capability bit 1 of its auth info;
    /// the kernel's own auth info has it clear).</summary>
    bool CallerIsSystemProgram()
    {
        if (GuestThread.Current is { KernelMode: true })
        {
            return SysmemKernel.KernelAccess.IsSystemProgram;
        }

        return KernelSpace.ProcessOf(Emu, 0) is { Access.IsSystemProgram: true };
    }

    /// <summary>tai_module_args_t as the framework copies it in (a record it cannot read keeps size 0).</summary>
    readonly struct ModuleArgs(uint size, int pid, uint length, uint argp, int flags)
    {
        public readonly uint Size = size;
        public readonly int Pid = pid;
        public readonly uint Length = length;
        public readonly uint Argp = argp;
        public readonly int Flags = flags;

        public bool SizeMatches => Size == ModuleArgsSize;
    }

    static ModuleArgs ReadModuleArgs(uint args)
    {
        if (!KernelSpace.IsUserRange(args, ModuleArgsSize))
        {
            return default;
        }

        return new ModuleArgs(GuestMemory.R32(args), (int)GuestMemory.R32(args + 4), GuestMemory.R32(args + 8),
            GuestMemory.R32(args + 0xC), (int)GuestMemory.R32(args + 0x10));
    }

    /// <summary>
    /// The framework's kernel stack buffers for one call: the copy of argp (<see cref="MaxArgsSize"/> bytes), the path
    /// (<see cref="MaxNameLength"/> bytes) and the status word, in kernel memory, freed when the call ends.
    /// </summary>
    sealed class KernelBuffers : IDisposable
    {
        const uint PathOffset = MaxArgsSize;
        const uint StatusOffset = MaxArgsSize + MaxNameLength;
        const uint Size = StatusOffset + 4;

        readonly GuestMemory _memory;

        public KernelBuffers(GuestMemory memory)
        {
            _memory = memory;
            Base = memory.Alloc(Size, "kernel:taiargs", MemoryRegion.Kernel, 0x10);
            GuestMemory.Fill(Base, 0, Size);
        }

        public uint Base { get; }

        public uint Args => Base;

        public uint Path => Base + PathOffset;

        public uint Status => Base + StatusOffset;

        public void Dispose() => _memory.Free(Base);
    }

    /// <summary>Copies argp in (args bytes) for a start or stop; a negative result is the copy's error.</summary>
    int CopyArgs(in ModuleArgs moduleArgs, KernelBuffers buffers) =>
        Sysmem.ksceKernelMemcpyUserToKernel(buffers.Args, moduleArgs.Argp, moduleArgs.Length);

    /// <summary>Copies the user path in; false when it does not end within <see cref="MaxNameLength"/> bytes.</summary>
    bool CopyPath(uint path, KernelBuffers buffers, out string kernelPath)
    {
        int length = Sysmem.ksceKernelStrncpyUserToKernel(buffers.Path, path, MaxNameLength);
        kernelPath = GuestMemory.ReadCString(buffers.Path, MaxNameLength);
        return length < MaxNameLength;
    }

    /// <summary>Writes the module function's status to the user's res (when given), as the framework does whatever the
    /// call returned.</summary>
    void WriteResult(uint res, KernelBuffers buffers)
    {
        if (res != 0)
        {
            Sysmem.ksceKernelMemcpyKernelToUser(res, buffers.Status, 4);
        }
    }

    /// <summary>taiLoadKernelModule(path, flags, opt): loads a kernel module, not started; returns its UID.</summary>
    [Hle(Nid = 0xD6D086E4, ReturnsValue = true)]
    int LoadKernelModule(uint path, int flags, uint opt)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!CallerIsSystemProgram())
        {
            return ErrorNotAllowed;
        }

        if (opt != 0)
        {
            return ErrorInvalidArgs;
        }

        using var buffers = new KernelBuffers(Mem);
        if (!CopyPath(path, buffers, out string kernelPath))
        {
            return ErrorUserMemory;
        }

        return Modulemgr.ksceKernelLoadModule(kernelPath, flags, 0);
    }

    /// <summary>taiStartKernelModuleForUser(modid, args, opt, res): module_start of a loaded kernel module with the
    /// copied arguments; *res is what it returned.</summary>
    [Hle(Nid = 0xA9EB128E, ReturnsValue = true)]
    int StartKernelModuleForUser(int moduleId, uint args, uint opt, uint res)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!CallerIsSystemProgram())
        {
            return ErrorNotAllowed;
        }

        ModuleArgs moduleArgs = ReadModuleArgs(args);
        if (!moduleArgs.SizeMatches)
        {
            return ErrorInvalidArgs;
        }

        // the framework answers USER_MEMORY here (its other calls swap the two codes)
        if (moduleArgs.Length > MaxArgsSize || opt != 0)
        {
            return ErrorUserMemory;
        }

        int kernelId = Sysmem.kscePUIDtoGUID(0, moduleId);
        if (kernelId < 0)
        {
            return kernelId;
        }

        using var buffers = new KernelBuffers(Mem);
        int result = CopyArgs(moduleArgs, buffers);
        if (result < 0)
        {
            return result;
        }

        result = Modulemgr.ksceKernelStartModule(kernelId, moduleArgs.Length, buffers.Args, moduleArgs.Flags, 0, buffers.Status);
        WriteResult(res, buffers);
        return result;
    }

    /// <summary>taiLoadStartKernelModuleForUser(path, args): loads a kernel module and runs its module_start with the
    /// copied arguments; returns its UID.</summary>
    [Hle(Nid = 0x9890A8D3, ReturnsValue = true)]
    int LoadStartKernelModuleForUser(uint path, uint args)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!CallerIsSystemProgram())
        {
            return ErrorNotAllowed;
        }

        return LoadStart(path, args, forPid: false);
    }

    /// <summary>taiLoadStartModuleForPidForUser(path, args): loads a user module into process args.pid and runs its
    /// module_start there with the copied arguments; returns its UID.</summary>
    [Hle(Nid = 0xE853ABCB, ReturnsValue = true)]
    int LoadStartModuleForPidForUser(uint path, uint args)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!CallerIsSystemProgram())
        {
            return ErrorNotAllowed;
        }

        return LoadStart(path, args, forPid: true);
    }

    int LoadStart(uint path, uint args, bool forPid)
    {
        ModuleArgs moduleArgs = ReadModuleArgs(args);
        if (!moduleArgs.SizeMatches)
        {
            return ErrorUserMemory;
        }

        if (moduleArgs.Length > MaxArgsSize)
        {
            return ErrorInvalidArgs;
        }

        using var buffers = new KernelBuffers(Mem);
        int result = CopyArgs(moduleArgs, buffers);
        if (result < 0)
        {
            return result;
        }

        if (!CopyPath(path, buffers, out string kernelPath))
        {
            return ErrorUserMemory;
        }

        // the framework passes no status word: the module manager's own result is the answer
        if (forPid)
        {
            return Modulemgr.ksceKernelLoadStartModuleForPid(moduleArgs.Pid, kernelPath, moduleArgs.Length, buffers.Args,
                moduleArgs.Flags, 0, 0);
        }

        return Modulemgr.ksceKernelLoadStartModule(kernelPath, moduleArgs.Length, buffers.Args, moduleArgs.Flags, 0, 0);
    }

    /// <summary>What a stop call does with the module once its arguments are in the kernel.</summary>
    enum StopKind
    {
        Stop,
        StopUnload,
        StopForPid,
        StopUnloadForPid,
    }

    /// <summary>The four stop calls: the arguments checked and copied, the module found by the caller's handle (for a
    /// process's module: by args.pid's), the module manager's call made and *res written.</summary>
    int Stop(int moduleId, uint args, uint opt, uint res, StopKind kind)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!CallerIsSystemProgram())
        {
            return ErrorNotAllowed;
        }

        ModuleArgs moduleArgs = ReadModuleArgs(args);
        if (!moduleArgs.SizeMatches)
        {
            return ErrorUserMemory;
        }

        if (moduleArgs.Length > MaxArgsSize || opt != 0)
        {
            return ErrorInvalidArgs;
        }

        bool forPid = kind is StopKind.StopForPid or StopKind.StopUnloadForPid;
        int kernelId = Sysmem.kscePUIDtoGUID(forPid ? moduleArgs.Pid : 0, moduleId);
        if (kernelId < 0)
        {
            return kernelId;
        }

        using var buffers = new KernelBuffers(Mem);
        int result = CopyArgs(moduleArgs, buffers);
        if (result < 0)
        {
            return result;
        }

        ModulemgrKernel modulemgr = Modulemgr;
        result = kind switch
        {
            StopKind.Stop => modulemgr.ksceKernelStopModule(kernelId, moduleArgs.Length, buffers.Args, moduleArgs.Flags, 0,
                buffers.Status),
            StopKind.StopUnload => modulemgr.ksceKernelStopUnloadModule(kernelId, moduleArgs.Length, buffers.Args,
                moduleArgs.Flags, 0, buffers.Status),
            StopKind.StopForPid => modulemgr.ksceKernelStopModuleForPid(moduleArgs.Pid, kernelId, moduleArgs.Length,
                buffers.Args, moduleArgs.Flags, 0, buffers.Status),
            _ => modulemgr.ksceKernelStopUnloadModuleForPid(moduleArgs.Pid, kernelId, moduleArgs.Length, buffers.Args,
                moduleArgs.Flags, 0, buffers.Status),
        };
        WriteResult(res, buffers);
        return result;
    }

    /// <summary>taiStopKernelModuleForUser(modid, args, opt, res): module_stop of a kernel module.</summary>
    [Hle(Nid = 0x1CB59A88, ReturnsValue = true)]
    int StopKernelModuleForUser(int moduleId, uint args, uint opt, uint res) => Stop(moduleId, args, opt, res, StopKind.Stop);

    /// <summary>taiStopUnloadKernelModuleForUser(modid, args, opt, res): module_stop, then the unload.</summary>
    [Hle(Nid = 0x4C1DF0F8, ReturnsValue = true)]
    int StopUnloadKernelModuleForUser(int moduleId, uint args, uint opt, uint res) =>
        Stop(moduleId, args, opt, res, StopKind.StopUnload);

    /// <summary>taiStopModuleForPidForUser(modid, args, opt, res): module_stop of a module of process args.pid.</summary>
    [Hle(Nid = 0x2C9D2A25, ReturnsValue = true)]
    int StopModuleForPidForUser(int moduleId, uint args, uint opt, uint res) =>
        Stop(moduleId, args, opt, res, StopKind.StopForPid);

    /// <summary>taiStopUnloadModuleForPidForUser(modid, args, opt, res): module_stop and unload of a module of process
    /// args.pid.</summary>
    [Hle(Nid = 0x612A461A, ReturnsValue = true)]
    int StopUnloadModuleForPidForUser(int moduleId, uint args, uint opt, uint res) =>
        Stop(moduleId, args, opt, res, StopKind.StopUnloadForPid);

    /// <summary>taiUnloadKernelModule(modid, flags, opt): unloads a kernel module (no module_stop).</summary>
    [Hle(Nid = 0x4C099402, ReturnsValue = true)]
    int UnloadKernelModule(int moduleId, int flags, uint opt) => Unload(0, moduleId, flags, opt, forPid: false);

    /// <summary>taiUnloadModuleForPid(pid, modid, flags, opt): unloads a module of process <paramref name="pid"/>.</summary>
    [Hle(Nid = 0xF4CA2439, ReturnsValue = true)]
    int UnloadModuleForPid(int pid, int moduleId, int flags, uint opt) => Unload(pid, moduleId, flags, opt, forPid: true);

    int Unload(int pid, int moduleId, int flags, uint opt, bool forPid)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!CallerIsSystemProgram())
        {
            return ErrorNotAllowed;
        }

        if (opt != 0)
        {
            return ErrorInvalidArgs;
        }

        int kernelId = Sysmem.kscePUIDtoGUID(pid, moduleId);
        if (kernelId < 0)
        {
            return kernelId;
        }

        if (forPid)
        {
            return Modulemgr.ksceKernelUnloadModuleForPid(pid, kernelId, flags, 0);
        }

        return Modulemgr.ksceKernelUnloadModule(kernelId, flags, 0);
    }

    /// <summary>taiMemcpyUserToKernel(kernel_dst, user_src, len): the kernel's user copy, for a system program.</summary>
    [Hle(Nid = 0xF6B202EE, ReturnsValue = true)]
    int MemcpyUserToKernel(uint kernelDestination, uint userSource, uint length)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!CallerIsSystemProgram())
        {
            return ErrorNotAllowed;
        }

        return Sysmem.ksceKernelMemcpyUserToKernel(kernelDestination, userSource, length);
    }

    /// <summary>taiMemcpyKernelToUser(user_dst, kernel_src, len): the kernel's copy to user memory, for a system
    /// program.</summary>
    [Hle(Nid = 0x89BB6025, ReturnsValue = true)]
    int MemcpyKernelToUser(uint userDestination, uint kernelSource, uint length)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!CallerIsSystemProgram())
        {
            return ErrorNotAllowed;
        }

        return Sysmem.ksceKernelMemcpyKernelToUser(userDestination, kernelSource, length);
    }
}
