using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// taiHEN's kernel API (taihenForKernel, taihenModuleUtils) for the kernel plugins that run on it (kubridge, fd_fix,
/// ...). taiHEN itself (taihen.skprx) is not run: its module_start only succeeds after installing its own hooks into
/// the firmware's signature and DRM checks, which VitaSharp does not make work (see docs/roadmap.md, kernel plugins).
/// So taiHEN stays the one HLE of a module that is not the firmware's, and its API is served here with the same
/// semantics and error codes, generically: modules are looked up in the caller's process (kernel modules for
/// KERNEL_PID), firmware functions VitaSharp implements are reachable through stubs in kernel memory, and hooks and
/// injections write guest code through the same paths as the user API.
/// </summary>
public sealed partial class Taihen
{
    const int TAI_ERROR_SYSTEM = unchecked((int)0x90010000);
    const int TAI_ERROR_STUB_NOT_RESOLVED = unchecked((int)0x9001000A);
    const uint TAI_IGNORE_MODULE_NID = 0xFFFFFFFF;

    // kernel-memory stubs that reach an HLE firmware function (module_get_export_func): NID -> stub address
    readonly Dictionary<uint, uint> _firmwareStubs = new();
    uint _firmwareStubPage, _firmwareStubNext;

    /// <summary>
    /// The module of process <paramref name="pid"/> named <paramref name="name"/> (taiHEN's module_get_by_name_nid:
    /// the newest match first; a null name with TAI_IGNORE_MODULE_NID is the newest module), or null.
    /// </summary>
    LoadedModule? ModuleByName(int pid, string? name, uint nid)
    {
        if (KernelSpace.ModulesOf(Emu, pid) is not { } modules)
        {
            return null;
        }

        for (int i = modules.Count - 1; i >= 0; i--)
        {
            var m = modules[i];
            if (name == null && nid == TAI_IGNORE_MODULE_NID)
            {
                return m;
            }

            if (name != null && m.Name == name && (nid == TAI_IGNORE_MODULE_NID || m.ModuleNid == nid))
            {
                return m;
            }

            if (name == null && m.ModuleNid == nid)
            {
                return m;
            }
        }

        return null;
    }

    /// <summary>tai_module_info_t (0x38 bytes): size, modid, module_nid, name[27], exports and imports ranges.</summary>
    static int WriteTaiModuleInfo(LoadedModule m, uint info)
    {
        if (info == 0)
        {
            return TAI_ERROR_INVALID_ARGS;
        }

        if (GuestMemory.R32(info) < 0x38)
        {
            return TAI_ERROR_SYSTEM;
        }

        GuestMemory.Span(info + 4, 0x34).Clear();
        GuestMemory.W32(info + 0x04, (uint)m.Uid);
        GuestMemory.W32(info + 0x08, m.ModuleNid);
        GuestMemory.WriteCString(info + 0x0C, m.Name, 27);
        GuestMemory.W32(info + 0x28, m.ExportsStart);
        GuestMemory.W32(info + 0x2C, m.ExportsEnd);
        GuestMemory.W32(info + 0x30, m.ImportsStart);
        GuestMemory.W32(info + 0x34, m.ImportsEnd);
        return 0;
    }

    static string? OptionalString(uint ptr) => ptr == 0 ? null : GuestMemory.ReadCString(ptr, 27);

    /// <summary>module_get_by_name_nid(pid, name, nid, tai_module_info_t *info).</summary>
    [Hle]
    int module_get_by_name_nid(int pid, uint name, uint nid, uint info)
    {
        if (name == 0 && nid == TAI_IGNORE_MODULE_NID && pid == KernelSpace.KernelPid)
        {
            return unchecked((int)0x9001000B); // TAI_ERROR_INVALID_MODULE
        }

        if (ModuleByName(pid, OptionalString(name), nid) is not { } m)
        {
            return TAI_ERROR_NOT_FOUND;
        }

        return WriteTaiModuleInfo(m, info);
    }

    /// <summary>taiGetModuleInfoForKernel(pid, name, tai_module_info_t *info): a module by name (NULL: the newest).</summary>
    [Hle]
    int taiGetModuleInfoForKernel(int pid, uint name, uint info)
    {
        if (ModuleByName(pid, OptionalString(name), TAI_IGNORE_MODULE_NID) is not { } m)
        {
            return TAI_ERROR_NOT_FOUND;
        }

        return WriteTaiModuleInfo(m, info);
    }

    /// <summary>module_get_offset(pid, modid, segidx, offset, uintptr_t *addr): the address of an offset in one of a
    /// module's segments (segidx 0..3; an offset past the segment's size is refused).</summary>
    [Hle]
    int module_get_offset(int pid, int modid, int segidx, uint offset, uint addrOut)
    {
        if (segidx is < 0 or > 3)
        {
            return TAI_ERROR_INVALID_ARGS;
        }

        if (KernelSpace.ModulesOf(Emu, pid)?.FirstOrDefault(x => x.Uid == modid) is not { } m)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        if (!m.Segments.TryGetValue(segidx, out var seg) || offset > m.SegmentFlags[segidx].MemSz)
        {
            return TAI_ERROR_INVALID_ARGS;
        }

        if (addrOut != 0)
        {
            GuestMemory.W32(addrOut, seg.Address + offset);
        }

        return 0;
    }

    /// <summary>A stub in kernel memory that calls HLE firmware function <paramref name="nid"/> (an SVC stub, ARM).</summary>
    uint FirmwareStub(uint nid)
    {
        lock (_firmwareStubs)
        {
            if (_firmwareStubs.TryGetValue(nid, out uint stub))
            {
                return stub;
            }

            if (_firmwareStubPage == 0 || _firmwareStubNext + 16 > _firmwareStubPage + GuestMemory.PageSize)
            {
                using (ProcessScope.Enter(KernelSpace.KernelPid))
                {
                    _firmwareStubPage = Mem.Alloc(GuestMemory.PageSize, "kernel:firmware stubs", MemoryRegion.Kernel);
                }

                _firmwareStubNext = _firmwareStubPage;
            }

            stub = _firmwareStubNext;
            _firmwareStubNext += 16;
            var info = NidDatabase.Lookup(nid);
            Linker.WriteHleStub(stub, Emu.Hle.GetHleFunctionIndex(nid, 0, info?.Library ?? ""), nid);
            Emu.Code.Invalidate(stub, 12);
            _firmwareStubs[nid] = stub;
            return stub;
        }
    }

    /// <summary>
    /// The address of function <paramref name="funcNid"/> exported by module <paramref name="moduleName"/> of process
    /// <paramref name="pid"/>, from its export table (library <paramref name="libNid"/>, any with TAI_ANY_LIBRARY), or
    /// 0. A firmware module VitaSharp implements itself has no export table: its function is a stub that calls the
    /// HLE, when the NID database places the function in that module and the HLE implements it.
    /// </summary>
    uint ExportAddress(int pid, string moduleName, uint libNid, uint funcNid)
    {
        if (ModuleByName(pid, moduleName, TAI_IGNORE_MODULE_NID) is { } m)
        {
            return ExportOf(m, libNid, funcNid);
        }

        if (NidDatabase.Lookup(funcNid) is { } info && info.Module == moduleName && Emu.Hle.IsImplemented(funcNid))
        {
            return FirmwareStub(funcNid);
        }

        return 0;
    }

    /// <summary>module_get_export_func(pid, modname, libnid, funcnid, uintptr_t *func).</summary>
    [Hle]
    int module_get_export_func(int pid, uint modname, uint libNid, uint funcNid, uint funcOut)
    {
        if (modname == 0)
        {
            return TAI_ERROR_INVALID_ARGS;
        }

        uint addr = ExportAddress(pid, GuestMemory.ReadCString(modname, 27), libNid, funcNid);
        if (addr == 0)
        {
            return TAI_ERROR_NOT_FOUND;
        }

        if (funcOut != 0)
        {
            GuestMemory.W32(funcOut, addr);
        }

        return 0;
    }

    /// <summary>The import stub of <paramref name="funcNid"/> (from library <paramref name="libNid"/>, any with
    /// TAI_ANY_LIBRARY) in module <paramref name="m"/>, or 0.</summary>
    uint ImportStub(LoadedModule m, uint libNid, uint funcNid)
    {
        var stubs = Emu.Linker.BoundStubs(funcNid, libNid == TAI_ANY_LIBRARY ? null : libNid);
        return stubs.FirstOrDefault(s => m.Segments.Values.Any(g => s >= g.Address && s - g.Address < g.Size));
    }

    /// <summary>module_get_import_func(pid, modname, target_libnid, funcnid, uintptr_t *stub).</summary>
    [Hle]
    int module_get_import_func(int pid, uint modname, uint libNid, uint funcNid, uint stubOut)
    {
        if (modname == 0 || ModuleByName(pid, GuestMemory.ReadCString(modname, 27), TAI_IGNORE_MODULE_NID) is not { } m)
        {
            return TAI_ERROR_NOT_FOUND;
        }

        uint stub = ImportStub(m, libNid, funcNid);
        if (stub == 0)
        {
            return TAI_ERROR_NOT_FOUND;
        }

        if (stubOut != 0)
        {
            GuestMemory.W32(stubOut, stub);
        }

        return 0;
    }

    /// <summary>True for a linker import stub (an SVC stub into the HLE or a movw/movt/bx r12 jump): ARM code that
    /// runs from anywhere, hooked by redirecting it.</summary>
    static bool IsStub(uint addr)
    {
        if ((addr & 3) != 0 || !GuestMemory.IsMapped(addr))
        {
            return false;
        }

        uint w0 = GuestMemory.R32(addr), w1 = GuestMemory.R32(addr + 4);
        return ((w0 >> 24) == 0xEF && w1 == 0xE12FFF1E) || JumpTarget(addr) != 0;
    }

    /// <summary>
    /// taiHookFunctionAbs(pid, tai_hook_ref_t *p_hook, dest_func, hook_func): every call of dest_func goes to the hook;
    /// TAI_CONTINUE reaches the original. A stub is redirected (with every other import stub that calls the same HLE
    /// function), a Thumb function is patched in place; an ARM function cannot be hooked yet (NOT_IMPLEMENTED).
    /// </summary>
    [Hle]
    int taiHookFunctionAbs(int pid, uint hook, uint dest, uint func)
    {
        if (hook == 0 || dest == 0 || func == 0)
        {
            return TAI_ERROR_INVALID_ARGS;
        }

        if (IsStub(dest))
        {
            var stubs = new List<uint> { dest };
            uint w0 = GuestMemory.R32(dest);
            if ((w0 >> 24) == 0xEF)
            {
                // an HLE firmware function: every importer's stub of its NID calls the hook too
                uint nid = GuestMemory.R32(dest + 8);
                stubs.AddRange(Emu.Linker.BoundStubs(nid).Where(s => s != dest && (GuestMemory.R32(s) >> 24) == 0xEF && GuestMemory.R32(s + 8) == nid));
            }

            return HookStubs(hook, stubs, func);
        }

        if ((dest & 1) != 0)
        {
            return InlineHook(hook, dest & ~1u, func, K.Symbolize(dest));
        }

        Log.WarnOnce($"taihook-abs-arm-{dest:X8}", $"taiHookFunctionAbs: ARM function {K.Symbolize(dest)} not hooked (Thumb only)");
        return TAI_ERROR_NOT_IMPLEMENTED;
    }

    /// <summary>taiHookFunctionExportForKernel(pid, p_hook, module, library_nid, func_nid, hook_func): hooks an
    /// exported function (module_get_export_func, then taiHookFunctionAbs).</summary>
    [Hle]
    int taiHookFunctionExportForKernel(int pid, uint hook, uint module, uint libNid, uint funcNid, uint func)
    {
        int r = module_get_export_func(pid, module, libNid, funcNid, 0);
        if (r < 0)
        {
            return r;
        }

        return taiHookFunctionAbs(pid, hook, ExportAddress(pid, GuestMemory.ReadCString(module, 27), libNid, funcNid), func);
    }

    /// <summary>taiHookFunctionImportForKernel(pid, p_hook, module, import_library_nid, import_func_nid, hook_func):
    /// hooks a module's import stub.</summary>
    [Hle]
    int taiHookFunctionImportForKernel(int pid, uint hook, uint module, uint libNid, uint funcNid, uint func)
    {
        if (module == 0 || ModuleByName(pid, GuestMemory.ReadCString(module, 27), TAI_IGNORE_MODULE_NID) is not { } m)
        {
            return TAI_ERROR_NOT_FOUND;
        }

        uint stub = ImportStub(m, libNid, funcNid);
        if (stub == 0)
        {
            return TAI_ERROR_NOT_FOUND;
        }

        if (!IsStub(stub))
        {
            return TAI_ERROR_STUB_NOT_RESOLVED;
        }

        return HookStubs(hook, [stub], func);
    }

    /// <summary>taiHookFunctionOffsetForKernel(pid, p_hook, modid, segidx, offset, thumb, hook_func).</summary>
    [Hle]
    int taiHookFunctionOffsetForKernel(int pid, uint hook, int modid, int segidx, uint offset, int thumb, uint func)
    {
        uint scratch = 0;
        int r = OffsetAddress(pid, modid, segidx, offset, ref scratch);
        if (r < 0)
        {
            return r;
        }

        return taiHookFunctionAbs(pid, hook, thumb != 0 ? scratch | 1 : scratch, func);
    }

    int OffsetAddress(int pid, int modid, int segidx, uint offset, ref uint addr)
    {
        if (segidx is < 0 or > 3)
        {
            return TAI_ERROR_INVALID_ARGS;
        }

        if (KernelSpace.ModulesOf(Emu, pid)?.FirstOrDefault(x => x.Uid == modid) is not { } m)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        if (!m.Segments.TryGetValue(segidx, out var seg) || offset > m.SegmentFlags[segidx].MemSz)
        {
            return TAI_ERROR_INVALID_ARGS;
        }

        addr = seg.Address + offset;
        return 0;
    }

    /// <summary>taiHookReleaseForKernel(tai_uid, hook): the same as taiHookRelease.</summary>
    [Hle]
    int taiHookReleaseForKernel(int uid, uint hook) => taiHookRelease(uid, hook);

    /// <summary>Runs <paramref name="body"/> with guest memory in process <paramref name="pid"/>'s address space.</summary>
    int InProcess(int pid, Func<int> body)
    {
        if (KernelSpace.ProcessOf(Emu, pid) is not { } process)
        {
            return TAI_ERROR_NOT_FOUND;
        }

        using (AddressSpace.Enter(process.Space))
        {
            return body();
        }
    }

    /// <summary>taiInjectAbsForKernel(pid, dest, src, size): writes size bytes over dest in process
    /// <paramref name="pid"/> (src is kernel memory); returns the injection id for taiInjectReleaseForKernel.</summary>
    [Hle]
    int taiInjectAbsForKernel(int pid, uint dest, uint src, uint size)
    {
        if (dest == 0 || src == 0 || size == 0 || size > 0x100000)
        {
            return TAI_ERROR_INVALID_ARGS;
        }

        byte[] data = GuestMemory.ReadSpan(src, (int)size).ToArray();
        return InProcess(pid, () => Inject(dest, data));

    }

    /// <summary>taiInjectDataForKernel(pid, modid, segidx, offset, data, size): an injection at an offset of a module's
    /// segment.</summary>
    [Hle]
    int taiInjectDataForKernel(int pid, int modid, int segidx, uint offset, uint data, uint size)
    {
        uint addr = 0;
        int r = OffsetAddress(pid, modid, segidx, offset, ref addr);
        if (r < 0)
        {
            return r;
        }

        return taiInjectAbsForKernel(pid, addr, data, size);
    }

    /// <summary>taiInjectReleaseForKernel(tai_uid): the same as taiInjectRelease.</summary>
    [Hle]
    int taiInjectReleaseForKernel(int uid) => taiInjectRelease(uid);

    /// <summary>taiLoadPluginsForTitleForKernel(pid, titleid, flags): the user plugins of the plugin configuration for
    /// <paramref name="titleid"/> (and *ALL) load into process <paramref name="pid"/>, started at once.</summary>
    [Hle]
    int taiLoadPluginsForTitleForKernel(int pid, string titleid, int flags)
    {
        if (KernelSpace.ProcessOf(Emu, pid) is not { } process)
        {
            return TAI_ERROR_NOT_FOUND;
        }

        using (ProcessScope.Enter(process))
        {
            process.LoadUserPlugins(titleid, startNow: true);
        }

        return 0;
    }

    /// <summary>taiReloadConfigForKernel(schedule, load_kernel): the configuration is read again on every load here;
    /// with load_kernel the *KERNEL plugins not loaded yet load and start.</summary>
    [Hle]
    int taiReloadConfigForKernel(int schedule, int loadKernel)
    {
        if (loadKernel != 0)
        {
            Emu.LoadKernelPlugins();
        }

        return 0;
    }

    // ------------------------------------------------------------------ taihenUnsafe: user modules of a process

    /// <summary>taiStopModuleForPidForUser(pid, modid, tai_module_args_t *args, int *res): module_stop of a user module
    /// of process <paramref name="pid"/> (args = { size, pid, args, argp, flags }).</summary>
    [Hle]
    int taiStopModuleForPidForUser(int pid, int modid, uint args, Ptr<int> res)
    {
        uint argc = 0, argp = 0;
        if (args != 0 && GuestMemory.R32(args) >= 0x10)
        {
            argc = GuestMemory.R32(args + 8);
            argp = GuestMemory.R32(args + 12);
        }

        if (KernelSpace.ModulesOf(Emu, pid)?.FirstOrDefault(m => m.Uid == modid) is not { } module)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        int r = module.StopEntry != 0 ? (int)GuestThread.Current!.CallGuest(module.StopEntry, argc, argp) : 0;
        res.TrySet(r);
        return 0;
    }

    /// <summary>taiUnloadModuleForPid(pid, modid, tai_module_args_t *args, int *res).</summary>
    [Hle]
    int taiUnloadModuleForPid(int pid, int modid, uint args, Ptr<int> res)
    {
        if (KernelSpace.ProcessOf(Emu, pid) is not { } process
            || KernelSpace.ModulesOf(Emu, pid)?.FirstOrDefault(m => m.Uid == modid) is not { } module || module == process.MainModule)
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        process.UnloadModule(module);
        res.TrySet(0);
        return 0;
    }

    /// <summary>taiStopUnloadModuleForPidForUser(pid, modid, tai_module_args_t *args, int *res).</summary>
    [Hle]
    int taiStopUnloadModuleForPidForUser(int pid, int modid, uint args, Ptr<int> res)
    {
        int r = taiStopModuleForPidForUser(pid, modid, args, res);
        return r < 0 ? r : taiUnloadModuleForPid(pid, modid, args, default);
    }
}
