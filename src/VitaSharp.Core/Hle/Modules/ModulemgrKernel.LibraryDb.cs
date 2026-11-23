using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The library database of the FW 3.74 modulemgr.skprx (decompiled, study only) and the module manager's process and
/// boot entries:
/// <list type="bullet">
/// <item>every library a loaded module exports and every stub (imported library) it holds has a UID in the firmware's
/// database; the lists answer them by process (ksceKernelGetLibraryList, ksceKernelGetStubList, the libraries of a
/// module and its stubs), the clients of a library, the stubs no library satisfies (the "lost" ones), their infos
/// and the NIDs. VitaSharp's loader keeps a module's export and import tables (<see cref="LoadedModule"/>), so the
/// database is built from them: each library and stub gets a kernel data object of the kernel process the first time
/// a list names it, and the same UID afterwards. A list call takes (pid, buffer, *num): a null buffer answers the
/// count, otherwise at most *num UIDs are written and *num says how many; a process without modules is
/// SCE_KERNEL_ERROR_LIBRARYDB_NO_MOD (0x8002D080 for the process lists);</item>
/// <item>the process image and its preloaded modules (ksceKernelLoadProcessImage, ksceKernelLoadPreloadingModules,
/// ksceKernelStartPreloadingModules, ksceKernelUnloadProcessModules): VitaSharp loads a process's executable and the
/// modules it preloads when the process is made (<see cref="Emulator.SpawnProcess"/>), so these answer what is
/// already there;</item>
/// <item>the boot file system of the kernel image (ksceKernelMountBootfs once, else 0x8002D021; ksceKernelUmountBootfs),
/// the end of the kernel loader and the boot registration (ksceKernelFinalizeKbl, ksceKernelRegisterModulesAfterBoot),
/// a segment read for the firmware loader (ksceKernelLoadPtLoadSegForFwloader).</item>
/// </list>
/// </summary>
public sealed partial class ModulemgrKernel
{
    const string LibraryKind = "library", StubKind = "libstub";

    const int BootfsMounted = unchecked((int)0x8002D021), NothingRelinked = unchecked((int)0x8002D081),
        UnknownLibrary = unchecked((int)0x8002D003);

    /// <summary>The UID of each library and stub, by (module UID, index in its export or import table, stub or not).</summary>
    [SnapshotMissing] // states of builds before it: no list had named one
    readonly Dictionary<long, int> _libraryUids = new();

    [SnapshotMissing] // states of builds before it: not mounted by a call
    bool _bootfsMounted;

    static long LibraryKey(int module, int index, bool stub) => ((long)module << 32) | ((long)index << 1) | (stub ? 1L : 0L);

    int LibraryUid(LoadedModule module, int index, bool stub)
    {
        long key = LibraryKey(module.Uid, index, stub);
        lock (_libraryUids)
        {
            if (_libraryUids.TryGetValue(key, out int uid) && K.Objects.Get<KernelData>(uid) != null)
            {
                return uid;
            }

            string name = stub ? module.Imports[index].Library : module.Exports[index].Library;
            uint libNid = stub ? module.Imports[index].LibNid : module.Exports[index].LibNid;
            var data = new KernelData { Name = name, Kind = stub ? StubKind : LibraryKind, Address = libNid, Aux = (uint)module.Uid, Size = (uint)index };
            using (ProcessScope.Enter(KernelSpace.KernelPid))
            {
                uid = K.Objects.Register(data);
            }

            _libraryUids[key] = uid;
            return uid;
        }
    }

    /// <summary>The module and table index a library or stub UID stands for.</summary>
    bool LibraryOf(int uid, bool stub, out LoadedModule? module, out int index)
    {
        module = null;
        index = -1;
        if (K.Objects.Get<KernelData>(uid) is not { } data || data.Kind != (stub ? StubKind : LibraryKind))
        {
            return false;
        }

        module = AnyModule((int)data.Aux);
        index = (int)data.Size;
        return module != null;
    }

    /// <summary>Writes a UID list as the database's list calls do.</summary>
    static int WriteList(List<int> uids, uint buffer, uint num)
    {
        if (buffer == 0)
        {
            return uids.Count;
        }

        uint room = num == 0 ? 0 : GuestMemory.R32(num);
        uint written = 0;
        foreach (int uid in uids)
        {
            if (written >= room)
            {
                break;
            }

            GuestMemory.W32(buffer + written * 4, (uint)uid);
            written++;
        }

        if (num != 0)
        {
            GuestMemory.W32(num, written);
        }

        return 0;
    }

    List<int> Libraries(List<LoadedModule> modules, bool stubs, Func<LoadedModule, int, bool>? keep = null)
    {
        List<int> uids = new();
        foreach (LoadedModule module in modules)
        {
            int count = stubs ? module.Imports.Count : module.Exports.Count;
            for (int i = 0; i < count; i++)
            {
                if (keep == null || keep(module, i))
                {
                    uids.Add(LibraryUid(module, i, stubs));
                }
            }
        }

        return uids;
    }

    /// <summary>A stub whose library no loaded module of the process (or the kernel) exports.</summary>
    bool IsLost(int pid, LoadedModule module, int index)
    {
        uint libNid = module.Imports[index].LibNid;
        foreach (int owner in new[] { pid, KernelSpace.KernelPid })
        {
            if (ModulesOf(owner) is not { } modules)
            {
                continue;
            }

            foreach (LoadedModule exporter in modules)
            {
                foreach ((string _, uint exportNid, uint[] _, uint[] _) in exporter.Exports)
                {
                    if (exportNid == libNid)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>ksceKernelGetLibraryList(pid, SceUID *list, SceSize *num): the libraries of the process's modules.</summary>
    [Hle(Nid = 0x12AD6DE3, Library = "SceModulemgrForKernel")]
    int ksceKernelGetLibraryList(int pid, uint list, uint num)
    {
        pid = PidOrCurrent(pid);
        return ModulesOf(pid) is { } modules ? WriteList(Libraries(modules, false), list, num) : NoProcessModules;
    }

    /// <summary>ksceKernelGetExportedLibraryListInModule(pid, modid, SceUID *list, SceSize *num).</summary>
    [Hle(Nid = 0x7E3F9F55, Library = "SceModulemgrForKernel")]
    int ksceKernelGetExportedLibraryListInModule(int pid, int modid, uint list, uint num) => InModule(pid, modid, false, list, num, lost: false);

    /// <summary>ksceKernelGetImportedLibraryListInModule(pid, modid, SceUID *list, SceSize *num): the module's stubs.</summary>
    [Hle(Nid = 0x1360E9A8, Library = "SceModulemgrForKernel")]
    int ksceKernelGetImportedLibraryListInModule(int pid, int modid, uint list, uint num) => InModule(pid, modid, true, list, num, lost: false);

    /// <summary>ksceKernelGetLostLibraryListInModule(pid, modid, SceUID *list, SceSize *num): its unsatisfied stubs.</summary>
    [Hle(Nid = 0x6A5DFBBA, Library = "SceModulemgrForKernel")]
    int ksceKernelGetLostLibraryListInModule(int pid, int modid, uint list, uint num) => InModule(pid, modid, true, list, num, lost: true);

    int InModule(int pid, int modid, bool stubs, uint list, uint num, bool lost)
    {
        pid = PidOrCurrent(pid);
        if (ModulesOf(pid) is not { } modules)
        {
            return NoProcessModules;
        }

        LoadedModule? module = modules.Find(m => m.Uid == modid);
        if (module == null)
        {
            return SceError.LIBRARYDB_NO_MOD;
        }

        return WriteList(Libraries([module], stubs, lost ? (m, i) => IsLost(pid, m, i) : null), list, num);
    }

    /// <summary>ksceKernelGetStubList(pid, SceUID *list, SceSize *num): the stubs of the process's modules.</summary>
    [Hle(Nid = 0xE48CDE07, Library = "SceModulemgrForKernel")]
    int ksceKernelGetStubList(int pid, uint list, uint num)
    {
        pid = PidOrCurrent(pid);
        return ModulesOf(pid) is { } modules ? WriteList(Libraries(modules, true), list, num) : NoProcessModules;
    }

    /// <summary>ksceKernelGetLostLibraryList(pid, SceUID *list, SceSize *num): the process's unsatisfied stubs.</summary>
    [Hle(Nid = 0x7D71892B, Library = "SceModulemgrForKernel")]
    int ksceKernelGetLostLibraryList(int pid, uint list, uint num)
    {
        pid = PidOrCurrent(pid);
        return ModulesOf(pid) is { } modules ? WriteList(Libraries(modules, true, (m, i) => IsLost(pid, m, i)), list, num) : NoProcessModules;
    }

    /// <summary>ksceKernelGetLibraryClientList(pid, libid, SceUID *modids, SceSize *num, skip): the modules that import
    /// the library, after the first <paramref name="skip"/>.</summary>
    [Hle(Nid = 0xAAF6E971, Library = "SceModulemgrForKernel")]
    int ksceKernelGetLibraryClientList(int pid, int library, uint modids, uint num, uint skip)
    {
        pid = PidOrCurrent(pid);
        if (!LibraryOf(library, false, out LoadedModule? exporter, out int index))
        {
            return UnknownLibrary;
        }

        uint libNid = exporter!.Exports[index].LibNid;
        List<int> clients = new();
        foreach (int owner in new[] { pid, KernelSpace.KernelPid })
        {
            if (ModulesOf(owner) is not { } modules)
            {
                continue;
            }

            foreach (LoadedModule module in modules)
            {
                if (module.Imports.Exists(i => i.LibNid == libNid) && !clients.Contains(module.Uid))
                {
                    clients.Add(module.Uid);
                }
            }
        }

        return WriteList(clients.Skip((int)Math.Min(skip, (uint)clients.Count)).ToList(), modids, num);
    }

    /// <summary>ksceKernelGetLibraryExportInfoForDebugger(pid, libid, list, SceSize *num, skip): {NID, address} of
    /// each function the library exports, after the first <paramref name="skip"/>.</summary>
    [Hle(Nid = 0xACDB6FEF, Library = "SceModulemgrForKernel")]
    int ksceKernelGetLibraryExportInfoForDebugger(int pid, int library, uint list, uint num, uint skip)
    {
        if (!LibraryOf(library, false, out LoadedModule? module, out int index))
        {
            return UnknownLibrary;
        }

        (string _, uint libNid, uint[] functions, uint[] _) = module!.Exports[index];
        Linker? linker = (KernelSpace.ProcessOf(Emu, module.IsKernel ? KernelSpace.KernelPid : PidOrCurrent(pid)) ?? Emu).Linker;
        uint room = num == 0 ? 0 : GuestMemory.R32(num);
        uint written = 0;
        for (int i = (int)skip; i < functions.Length && written < room; i++)
        {
            linker.TryGetFunctionExport(libNid, functions[i], out uint address);
            GuestMemory.W32(list + written * 8, functions[i]);
            GuestMemory.W32(list + written * 8 + 4, address);
            written++;
        }

        if (num != 0)
        {
            GuestMemory.W32(num, written);
        }

        return 0;
    }

    /// <summary>ksceKernelGetLibraryInfoForDebugger(pid, libid, info): SceKernelModuleLibraryInfo (0x120: size, UID,
    /// library NID, version, function and variable counts, name[0x100], the number of importers, the module).</summary>
    [Hle(Nid = 0xB54482AF, Library = "SceModulemgrForKernel")]
    int ksceKernelGetLibraryInfoForDebugger(int pid, int library, uint info)
    {
        if (!LibraryOf(library, false, out LoadedModule? module, out int index))
        {
            return UnknownLibrary;
        }

        (string name, uint libNid, uint[] functions, uint[] variables) = module!.Exports[index];
        GuestMemory.Fill(info, 0, 0x120);
        GuestMemory.W32(info, 0x120);
        GuestMemory.W32(info + 4, (uint)library);
        GuestMemory.W32(info + 8, libNid);
        GuestMemory.Write(info + 0xC, (ushort)1);
        GuestMemory.Write(info + 0xE, (ushort)1);
        GuestMemory.Write(info + 0x10, (ushort)functions.Length);
        GuestMemory.Write(info + 0x12, (ushort)variables.Length);
        byte[] text = System.Text.Encoding.UTF8.GetBytes(name);
        text.AsSpan(0, Math.Min(text.Length, 0xFF)).CopyTo(GuestMemory.Span(info + 0x18, 0xFF));
        uint clients = ClientCount(pid, library);
        GuestMemory.W32(info + 0x118, clients);
        GuestMemory.W32(info + 0x11C, (uint)module.Uid);
        return 0;
    }

    /// <summary>How many modules import the library.</summary>
    uint ClientCount(int pid, int library) => (uint)ksceKernelGetLibraryClientListThroughEntry(pid, library, 0, 0, 0);

    /// <summary>ksceKernelGetStubInfoForDebugger(pid, stubid, info): the stub's module, library NID and name, and its
    /// function and variable counts (0x118 bytes).</summary>
    [Hle(Nid = 0x07DBB649, Library = "SceModulemgrForKernel")]
    int ksceKernelGetStubInfoForDebugger(int pid, int stub, uint info) => StubInfo(stub, info);

    /// <summary>ksceKernelGetLostLibraryInfo(pid, stubid, info): the same for an unsatisfied stub.</summary>
    [Hle(Nid = 0x388D4DE0, Library = "SceModulemgrForKernel")]
    int ksceKernelGetLostLibraryInfo(int pid, int stub, uint info) => StubInfo(stub, info);

    int StubInfo(int stub, uint info)
    {
        if (!LibraryOf(stub, true, out LoadedModule? module, out int index))
        {
            return UnknownLibrary;
        }

        (string name, uint libNid, uint[] functions) = module!.Imports[index];
        GuestMemory.Fill(info, 0, 0x118);
        GuestMemory.W32(info, 0x118);
        GuestMemory.W32(info + 4, (uint)module.Uid);
        GuestMemory.W32(info + 8, libNid);
        byte[] text = System.Text.Encoding.UTF8.GetBytes(name);
        text.AsSpan(0, Math.Min(text.Length, 0xFF)).CopyTo(GuestMemory.Span(info + 0xC, 0xFF));
        GuestMemory.W32(info + 0x10C, (uint)functions.Length);
        GuestMemory.W32(info + 0x110, 0);
        return 0;
    }

    /// <summary>ksceKernelGetStubNidTableForDebugger(pid, stubid, table, SceSize *num, skip): the function NIDs of a stub.</summary>
    [Hle(Nid = 0xCE201AFC, Library = "SceModulemgrForKernel")]
    int ksceKernelGetStubNidTableForDebugger(int pid, int stub, uint table, uint num, uint skip)
    {
        if (!LibraryOf(stub, true, out LoadedModule? module, out int index))
        {
            return UnknownLibrary;
        }

        uint[] functions = module!.Imports[index].FuncNids;
        uint room = num == 0 ? 0 : GuestMemory.R32(num);
        uint written = 0;
        for (int i = (int)skip; i < functions.Length && written < room; i++)
        {
            GuestMemory.W32(table + written * 4, functions[i]);
            written++;
        }

        if (num != 0)
        {
            GuestMemory.W32(num, written);
        }

        return 0;
    }

    /// <summary>ksceKernelGetLibEntCBListForSyslibtrace(ppList, SceSize *num): the library control blocks the system
    /// library trace walks; VitaSharp keeps no such blocks, so the list is empty.</summary>
    [Hle(Nid = 0x534519CD, Library = "SceModulemgrForKernel")]
    int ksceKernelGetLibEntCBListForSyslibtrace(uint list, uint num)
    {
        if (list != 0)
        {
            GuestMemory.W32(list, 0);
        }

        if (num != 0)
        {
            GuestMemory.W32(num, 0);
        }

        return 0;
    }

    /// <summary>ksceKernelGetLibraryDBFlags(pid, int *flags): the flags of the process's library database (0).</summary>
    [Hle(Nid = 0x1C82E9F7, Library = "SceModulemgrForKernel")]
    int ksceKernelGetLibraryDBFlags(int pid, uint flags)
    {
        if (ModulesOf(PidOrCurrent(pid)) == null)
        {
            return NoProcessModules;
        }

        GuestMemory.W32(flags, 0);
        return 0;
    }

    /// <summary>NID_26C28FA4(pid, libid): links the process's stubs of the library again; no such library is
    /// 0x8002D003, nothing to link 0x8002D081.</summary>
    [Hle(Nid = 0x26C28FA4, Library = "SceModulemgrForKernel")]
    int NID_26C28FA4(int pid, int library)
    {
        if (library == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (!LibraryOf(library, false, out LoadedModule? exporter, out int index))
        {
            return UnknownLibrary;
        }

        uint libNid = exporter!.Exports[index].LibNid;
        List<int> stubs = ModulesOf(PidOrCurrent(pid)) is { } modules ? Libraries(modules, true, (m, i) => m.Imports[i].LibNid == libNid) : new();
        return stubs.Count == 0 ? NothingRelinked : 0;
    }

    /// <summary>NID_353821B2(pid): the module manager's stop of a process's modules for the core dump; pid 0 or the
    /// kernel's is SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0x353821B2, Library = "SceModulemgrForKernel")]
    int NID_353821B2(int pid)
    {
        if (pid == 0 || pid == KernelSpace.KernelPid)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ModulesOf(pid) == null ? SceError.MODULEMGR_INVALID_PROCESS_UID : 0;
    }

    /// <summary>ksceKernelGetMetaDataForDebugger(pid, modid, void **start, SceSize *size): the module's module_info.</summary>
    [Hle(Nid = 0x2E6569F3, Library = "SceModulemgrForKernel")]
    int ksceKernelGetMetaDataForDebugger(int pid, int modid, uint start, uint size)
    {
        if (ModulesOf(PidOrCurrent(pid))?.Find(m => m.Uid == modid) is not { } module)
        {
            return SceError.LIBRARYDB_NO_MOD;
        }

        if (start != 0)
        {
            GuestMemory.W32(start, module.ModuleInfoAddr);
        }

        if (size != 0)
        {
            GuestMemory.W32(size, 0x34);
        }

        return 0;
    }

    /// <summary>ksceKernelGetModuleInfoForDebugger(pid, infos, SceSize *num): the SceKernelModuleInfo (0x1B8) of each of
    /// the process's modules, as many as *num says.</summary>
    [Hle(Nid = 0x0155FE40, Library = "SceModulemgrForKernel")]
    int ksceKernelGetModuleInfoForDebugger(int pid, uint infos, uint num)
    {
        pid = PidOrCurrent(pid);
        if (ModulesOf(pid) is not { } modules)
        {
            return NoProcessModules;
        }

        uint room = num == 0 ? 0 : GuestMemory.R32(num);
        uint written = 0;
        foreach (LoadedModule module in modules)
        {
            if (written >= room)
            {
                break;
            }

            uint info = infos + written * 0x1B8;
            GuestMemory.W32(info, 0x1B8);
            if (ksceKernelGetModuleInfoThroughEntry(pid, module.Uid, info) >= 0)
            {
                written++;
            }
        }

        if (num != 0)
        {
            GuestMemory.W32(num, written);
        }

        return 0;
    }

    /// <summary>ksceKernelGetProgramIdentificationInfo(path, info): the program authority ID of the executable at the
    /// path (+8, 64 bits) and its SELF type (+4); a file that is not one is SCE_KERNEL_ERROR_MODULEMGR_NOENT.</summary>
    [Hle(Nid = 0x5D60CD77, Library = "SceModulemgrForKernel")]
    int ksceKernelGetProgramIdentificationInfo(string path, uint info)
    {
        string? host = Emu.Vfs.Resolve(path);
        if (host == null || !File.Exists(host))
        {
            return SceError.MODULEMGR_NOENT;
        }

        byte[] head = new byte[0x100];
        using (FileStream file = File.OpenRead(host))
        {
            file.ReadExactly(head, 0, (int)Math.Min(head.Length, file.Length));
        }

        if (head[0] != (byte)'S' || head[1] != (byte)'C' || head[2] != (byte)'E')
        {
            return SceError.ILLEGAL_SELF_HEADER;
        }

        ulong appInfoOffset = BitConverter.ToUInt64(head, 0x28);
        GuestMemory.W32(info + 4, BitConverter.ToUInt16(head, 0xA));
        if (appInfoOffset + 8 <= (ulong)head.Length)
        {
            GuestMemory.Write(info + 8, BitConverter.ToUInt64(head, (int)appInfoOffset));
        }

        return 0;
    }

    // ------------------------------------------------------------------ the process image and its preloaded modules

    /// <summary>ksceKernelLoadProcessImage(pid, path, flags, auth, param, moduleInfo): the process's executable, loaded
    /// with the process: its main module's UID.</summary>
    [Hle(Nid = 0xA85A44D7, Library = "SceModulemgrForKernel")]
    int ksceKernelLoadProcessImage(int pid, uint path, uint flags, uint auth, uint parameters, uint moduleInfo) => ksceKernelGetModuleIdByPidThroughEntry(pid);

    /// <summary>ksceKernelLoadPreloadingModules(pid, param, flags): the preloaded modules, loaded with the process; 0
    /// for a process (SCE_KERNEL_ERROR_MODULEMGR_INVALID_PROCESS_UID otherwise).</summary>
    [Hle(Nid = 0xE3C1AAA1, Library = "SceModulemgrForKernel")]
    int ksceKernelLoadPreloadingModules(int pid, uint parameters, uint flags) => ProcessOf(pid) != null ? 0 : SceError.MODULEMGR_INVALID_PROCESS_UID;

    /// <summary>ksceKernelStartPreloadingModules(pid): started with the process.</summary>
    [Hle(Nid = 0x998C7AE9, Library = "SceModulemgrForKernel")]
    int ksceKernelStartPreloadingModules(int pid) => ProcessOf(pid) != null ? 0 : SceError.MODULEMGR_INVALID_PROCESS_UID;

    /// <summary>ksceKernelUnloadProcessModules(pid): the process's modules go with the process.</summary>
    [Hle(Nid = 0xE71530D7, Library = "SceModulemgrForKernel")]
    int ksceKernelUnloadProcessModules(int pid) => ProcessOf(pid) != null ? 0 : SceError.MODULEMGR_INVALID_PROCESS_UID;

    // ------------------------------------------------------------------ boot

    /// <summary>ksceKernelMountBootfs(path): mounts the kernel image's file system (VitaSharp's is the installed
    /// firmware's, always there); a second mount is 0x8002D021.</summary>
    [Hle(Nid = 0x185FF1BC, Library = "SceModulemgrForKernel")]
    int ksceKernelMountBootfs(uint path)
    {
        lock (_libraryUids)
        {
            if (_bootfsMounted)
            {
                return BootfsMounted;
            }

            _bootfsMounted = true;
            return 0;
        }
    }

    /// <summary>ksceKernelUmountBootfs(): unmounts it.</summary>
    [Hle(Nid = 0xBD61AD4D, Library = "SceModulemgrForKernel")]
    int ksceKernelUmountBootfs()
    {
        lock (_libraryUids)
        {
            _bootfsMounted = false;
            return 0;
        }
    }

    /// <summary>ksceKernelFinalizeKbl(): the kernel loader's end; nothing of it runs in VitaSharp.</summary>
    [Hle(Nid = 0xB911516F, Library = "SceModulemgrForKernel")]
    int ksceKernelFinalizeKbl() => 0;

    /// <summary>ksceKernelRegisterModulesAfterBoot(): the modules the boot loaded join the module list (they are in it
    /// from the start in VitaSharp: <see cref="FirmwareKernelModules"/>).</summary>
    [Hle(Nid = 0xD0AF9DB7, Library = "SceModulemgrForKernel")]
    int ksceKernelRegisterModulesAfterBoot() => 0;

    /// <summary>ksceKernelModuleUnloadMySelf(): the calling kernel module asks to be unloaded after its start; it stays
    /// in VitaSharp's list (its code is the firmware's HLE), and the call answers 0.</summary>
    [Hle(Nid = 0x2F82EEBC, Library = "SceModulemgrForKernel")]
    int ksceKernelModuleUnloadMySelf() => 0;

    /// <summary>ksceKernelLoadPtLoadSegForFwloader(path, segment, buffer, size, unused, SceSize *read): copies a PT_LOAD
    /// segment of the ELF (a decrypted SELF) at the path into the buffer.</summary>
    [Hle(Nid = 0xA07063EA, Library = "SceModulemgrForKernel")]
    int ksceKernelLoadPtLoadSegForFwloader(string path, int segment, uint buffer, uint size, uint unused, uint read)
    {
        string? host = Emu.Vfs.Resolve(path);
        if (host == null || !File.Exists(host))
        {
            return SceError.MODULEMGR_NOENT;
        }

        byte[] elf;
        try
        {
            elf = Loader.Self.SelfDecryptor.ToElf(File.ReadAllBytes(host), null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
        {
            return SceError.ILLEGAL_ELF_HEADER;
        }

        uint phoff = BitConverter.ToUInt32(elf, 0x1C);
        int phnum = BitConverter.ToUInt16(elf, 0x2C);
        if ((uint)segment >= (uint)phnum)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int header = (int)phoff + segment * 0x20;
        uint offset = BitConverter.ToUInt32(elf, header + 4);
        uint fileSize = BitConverter.ToUInt32(elf, header + 0x10);
        uint copy = Math.Min(fileSize, size);
        elf.AsSpan((int)offset, (int)copy).CopyTo(GuestMemory.Span(buffer, (int)copy));
        if (read != 0)
        {
            GuestMemory.W32(read, copy);
        }

        return 0;
    }
}
