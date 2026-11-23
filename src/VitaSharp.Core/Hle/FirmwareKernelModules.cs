using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle;

/// <summary>
/// The firmware's kernel modules as the kernel shows them to kernel code, for the modules whose code VitaSharp runs in
/// HLE. On the Vita every kernel module of the firmware is in the module manager's list of the kernel process
/// (ksceKernelGetModuleList(KERNEL_PID), ksceKernelGetModuleCB), with its module_info, export and import tables in
/// kernel memory, and another kernel module (one the user loads included) links to the functions in those tables and
/// may read them or write to the code they point at, as kernel code can write any kernel memory.
/// <para>
/// Each module of the installed firmware's kernel (<see cref="FirmwareKernelImage"/>) is loaded here as a module of
/// the kernel process with the firmware's own name, module NID and export tables (libraries, attributes, function
/// NIDs), through the module loader like any module, so the module manager's queries and the linker see it as they
/// see a loaded module. Every exported function's entry is real ARM code, <c>svc #n; bx lr</c> into the HLE
/// function, so code written over it runs for every kernel caller, and instructions moved elsewhere still reach the
/// HLE (the SVC number names the function, not the address it runs from). Kernel modules that import a firmware
/// library are linked to these entries. User modules keep reaching a syscall export through their HLE stubs (the
/// emulator's syscall table, the same function). An HLE function that calls another firmware function the way the
/// firmware module does goes through <see cref="Call"/>.
/// </para>
/// <para>
/// The variables a firmware module exports are not part of the tables yet. Its imports are, as the firmware module
/// has them, linked by the loader; HLE code that makes the call a firmware module makes through an import goes
/// through <see cref="CallImport"/>.
/// </para>
/// </summary>
public sealed class FirmwareKernelModules(Emulator emu)
{
    private const uint BxLr = 0xE12FFF1E, SvcOpcode = 0xEF000000;

    /// <summary>
    /// What fills the firmware module's code segments, whose functions VitaSharp does not run (no firmware code is
    /// copied): a word that stops the CPU whatever way code enters it. As A32 it is UDF (cccc 0111 1111 iiii iiii
    /// iiii 1111 iiii); as Thumb its low half is UDF #0xFE and its high half a B back to that UDF, so a Thumb entry
    /// at either halfword stops there too.
    /// </summary>
    public const uint TrapFillWord = 0xE7FDDEFE;

    /// <summary>module_info's segment is two bits of e_entry: the tables segment follows at most three.</summary>
    private const int MaxRealSegments = 3;

    private const uint ExecutableFlag = 1;


    /// <summary>(library NID, function NID) -> the entry's address and its unpatched first word.</summary>
    private readonly Dictionary<(uint LibNid, uint Nid), (uint Entry, uint Svc)> _entries = new();

    /// <summary>(importing module, library NID, function NID) -> the import stub and the code the linker put in it.</summary>
    private readonly Dictionary<(string Module, uint LibNid, uint Nid), (uint Stub, uint[] Bound)> _imports = new();

    private const int StubWords = 4;
    private readonly object _lock = new();
    private bool _loaded;

    /// <summary>The firmware modules' code segments (trap-filled), sorted by address: written once by <see cref="Load"/>.</summary>
    private (uint Start, uint End, string Module, uint ModuleNid)[] _codeSegments = [];

    /// <summary>(module NID, offset in its code segment with the Thumb bit) -> the HLE of a function the firmware module
    /// has there without exporting it.</summary>
    private readonly Dictionary<(uint ModuleNid, uint Offset), InternalFunction> _internalFunctions = new();

    /// <summary>The internal functions a caller reached that have no HLE (each one logged once).</summary>
    private readonly HashSet<(string Module, uint Offset)> _reportedTraps = new();

    /// <summary>The HLE of a firmware module's internal function: gets the call's arguments, returns its r0.</summary>
    public delegate uint InternalFunction(GuestThread thread, FirmwareCallArguments arguments);

    /// <summary>The AAPCS arguments of a call that reached a firmware code segment: r0-r3, then the stack.</summary>
    public readonly unsafe struct FirmwareCallArguments
    {
        private readonly CpuState* _state;

        internal FirmwareCallArguments(CpuState* state)
        {
            _state = state;
        }

        /// <summary>Argument <paramref name="index"/> (0-based).</summary>
        public uint this[int index] => index < 4 ? _state->R[index] : GuestMemory.R32(_state->R[13] + (uint)(index - 4) * 4);
    }

    /// <summary>
    /// Declares the HLE of a function firmware module <paramref name="moduleNid"/> (the module NID names the firmware
    /// build, whose layout the offsets are of) has at <paramref name="offset"/> of its code segment (Thumb bit set for
    /// Thumb code) without exporting it: kernel code that reaches it by offset (module_get_offset) runs
    /// <paramref name="function"/>.
    /// </summary>
    public void RegisterInternal(uint moduleNid, uint offset, InternalFunction function)
    {
        lock (_lock)
        {
            _internalFunctions[(moduleNid, offset)] = function;
        }
    }

    /// <summary>
    /// The CPU stopped on the trap fill of a firmware module's code segment at <paramref name="pc"/>: code called a
    /// function of the module that VitaSharp has no code for there (an internal one, found by offset). Runs its HLE
    /// when one is declared (<see cref="RegisterInternal"/>), otherwise logs the module and offset once and returns
    /// SCE_KERNEL_ERROR_NOT_IMPLEMENTED; either way the call returns to the caller's lr, as the function would.
    /// Returns false when <paramref name="pc"/> is not in a firmware code segment.
    /// </summary>
    internal unsafe bool HandleTrap(GuestThread thread, CpuState* state, uint pc)
    {
        (uint Start, uint End, string Module, uint ModuleNid)[] segments = Volatile.Read(ref _codeSegments);
        int found = -1;
        for (int i = 0; i < segments.Length; i++)
        {
            if (pc >= segments[i].Start && pc < segments[i].End)
            {
                found = i;
                break;
            }
        }

        if (found < 0 || GuestMemory.R32(pc & ~3u) != TrapFillWord)
        {
            return false;
        }

        (uint start, _, string module, uint moduleNid) = segments[found];
        uint word = (pc & ~3u) - start;
        bool thumb = state->Thumb != 0;
        // a Thumb entry at the word's high half branches to the UDF in its low half
        uint offset = thumb ? word | 1 : word;
        InternalFunction? function;
        lock (_lock)
        {
            if (!_internalFunctions.TryGetValue((moduleNid, offset), out function) && thumb)
            {
                _internalFunctions.TryGetValue((moduleNid, (word + 2) | 1), out function);
            }
        }

        uint lr = state->R[14];
        uint result;
        if (function != null)
        {
            result = function(thread, new FirmwareCallArguments(state));
        }
        else
        {
            bool first;
            lock (_lock)
            {
                first = _reportedTraps.Add((module, offset));
            }

            if (first)
            {
                Log.Warn($"{module}+0x{offset:X}: internal firmware function not emulated (called from {emu.Kernel.Symbolize(lr & ~1u)}); it returns SCE_KERNEL_ERROR_NOT_IMPLEMENTED");
            }

            result = unchecked((uint)SceError.NOT_IMPLEMENTED);
        }

        state->R[0] = result;
        state->R[15] = lr & ~1u;
        state->Thumb = lr & 1;
        return true;
    }

    /// <summary>The kernel modules loaded for the firmware's modules (empty until <see cref="Load"/>).</summary>
    public List<LoadedModule> Modules { get; } = new();

    /// <summary>
    /// Loads the firmware's kernel modules into the kernel process, once: each module of the installed firmware that no
    /// loaded kernel module already stands for (same name). Without an installed firmware there is nothing to show.
    /// Returns how many modules were loaded.
    /// </summary>
    public int Load()
    {
        lock (_lock)
        {
            if (_loaded)
            {
                return 0;
            }

            _loaded = true;
            int loaded = 0;
            foreach (FirmwareKernelImage.Module module in FirmwareKernelImage.For(emu.Options.FsRoot).Modules)
            {
                string name = module.Image.Name;
                if (emu.Kernel.Modules.Values.Any(m => m.IsKernel && m.Name == name))
                {
                    continue;
                }

                List<(string Library, uint LibNid, ushort Attributes, uint[] FuncNids)> libraries = module.Image.Exports
                    .Where(e => e.FuncNids.Length > 0)
                    .Select(e => (e.Library, e.LibNid, e.Attributes, e.FuncNids))
                    .ToList();
                List<(string Library, uint LibNid, uint[] FuncNids)> imports = module.Image.Imports
                    .Where(i => i.FuncNids.Length > 0)
                    .ToList();
                LoadedModule loadedModule = LoadShell(module.VitaPath + "/" + name, module.Image, libraries, imports);
                Modules.Add(loadedModule);
                loaded++;
            }

            // what the linker bound each import stub to, once every module is there (a later module that exports the
            // same library rebinds them; a hook rewrites them)
            foreach (KeyValuePair<(string Module, uint LibNid, uint Nid), (uint Stub, uint[] Bound)> import in _imports.ToList())
            {
                _imports[import.Key] = (import.Value.Stub, GuestMemory.Span<uint>(import.Value.Stub, StubWords).ToArray());
            }

            foreach (HleModule module in emu.Hle.Modules)
            {
                if (module is IFirmwareInternalFunctions internals)
                {
                    internals.RegisterInternalFunctions(this);
                }
            }

            if (loaded > 0)
            {
                Log.Info($"Firmware kernel modules: {loaded} shown to kernel code ({_entries.Count} exported functions, {_imports.Count} imports)");
            }

            return loaded;
        }
    }

    /// <summary>
    /// The firmware modules a save state booted without its files brought back (<see cref="SaveState.SaveStates.BootFromState"/>):
    /// they are already loaded, from the state's memory, as <see cref="Load"/> had loaded them; their entries, import
    /// stubs and code segments are taken from there, so no firmware has to be installed for the state to run.
    /// </summary>
    public void AdoptFromState(IReadOnlyList<LoadedModule> modules)
    {
        lock (_lock)
        {
            _loaded = true;
            var codeSegments = new List<(uint Start, uint End, string Module, uint ModuleNid)>(_codeSegments);
            foreach (LoadedModule loadedModule in modules)
            {
                Modules.Add(loadedModule);
                for (int library = 0; library < loadedModule.ExportLibraries.Count && library < loadedModule.Exports.Count; library++)
                {
                    ExportLibrary export = loadedModule.ExportLibraries[library];
                    string libName = loadedModule.Exports[library].Library;
                    for (int function = 0; function < export.FuncCount; function++)
                    {
                        uint nid = export.Nids[function], entry = export.Entries[function];
                        uint svc = emu.Hle.GetHleFunctionIndex(nid, export.LibNid, libName);
                        Linker.WriteHleStub(entry, svc, nid);
                        _entries[(export.LibNid, nid)] = (entry, SvcOpcode | (svc & 0xFFFFFF));
                    }
                }

                foreach ((uint libNid, uint nid, uint stub) in loadedModule.ImportStubs)
                {
                    _imports[(loadedModule.Name, libNid, nid)] = (stub, GuestMemory.Span<uint>(stub, StubWords).ToArray());
                }

                // the code segments come before the tables segment (the one module_info is in)
                foreach (KeyValuePair<int, SegmentInfo> segment in loadedModule.Segments)
                {
                    if (segment.Key < loadedModule.ModuleInfoSegment && loadedModule.SegmentFlags.TryGetValue(segment.Key, out (uint Flags, uint MemSz, uint FileSz) flags)
                        && (flags.Flags & ExecutableFlag) != 0)
                    {
                        codeSegments.Add((segment.Value.Address, segment.Value.Address + segment.Value.Size, loadedModule.Name, loadedModule.ModuleNid));
                    }
                }
            }

            codeSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
            Volatile.Write(ref _codeSegments, codeSegments.ToArray());
            foreach (HleModule module in emu.Hle.Modules)
            {
                if (module is IFirmwareInternalFunctions internals)
                {
                    internals.RegisterInternalFunctions(this);
                }
            }

            Log.Info($"Firmware kernel modules: {modules.Count} taken from the save state ({_entries.Count} exported functions, {_imports.Count} imports)");
        }
    }

    /// <summary>Loads one firmware module's tables as a kernel module and writes its function entries.</summary>
    private LoadedModule LoadShell(string path, ModuleImage image, List<(string Library, uint LibNid, ushort Attributes, uint[] FuncNids)> libraries,
        List<(string Library, uint LibNid, uint[] FuncNids)> imports)
    {
        (byte[] elf, List<(int Library, int Function, uint Offset)> stubs, List<(int Library, int Function, uint Offset)> importStubs, int tablesSegment) =
            BuildShell(image.Name, image.ModuleNid, image.Attributes, libraries, imports, image.Segments);
        LoadedModule loadedModule;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            loadedModule = ModuleLoader.Load(elf, path, emu.Memory, emu.Linker, 0, kernel: true, syscallsForUsers: false);
            loadedModule.Uid = emu.Kernel.Objects.Register(new ModuleObject(loadedModule));
        }

        var codeSegments = new List<(uint Start, uint End, string Module, uint ModuleNid)>(_codeSegments);
        for (int i = 0; i < tablesSegment; i++)
        {
            if ((image.Segments[i].Flags & ExecutableFlag) != 0 && loadedModule.Segments.TryGetValue(i, out SegmentInfo code))
            {
                codeSegments.Add((code.Address, code.Address + code.Size, image.Name, image.ModuleNid));
            }
        }

        codeSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
        Volatile.Write(ref _codeSegments, codeSegments.ToArray());


        loadedModule.Started = true;
        emu.Kernel.Modules[loadedModule.Uid] = loadedModule;
        uint segment = loadedModule.Segments[tablesSegment].Address;
        foreach ((int library, int function, uint offset) in stubs)
        {
            (string libName, uint libNid, _, uint[] nids) = libraries[library];
            uint nid = nids[function];
            uint entry = segment + offset;
            uint svc = emu.Hle.GetHleFunctionIndex(nid, libNid, libName);
            Linker.WriteHleStub(entry, svc, nid);
            _entries[(libNid, nid)] = (entry, SvcOpcode | (svc & 0xFFFFFF));
        }

        foreach ((int library, int function, uint offset) in importStubs)
        {
            (_, uint libNid, uint[] nids) = imports[library];
            _imports[(image.Name, libNid, nids[function])] = (segment + offset, []);
        }

        emu.InvalidateCode(0, uint.MaxValue); // kernel code runs on every process's threads
        return loadedModule;
    }

    /// <summary>The address of the firmware's function <paramref name="nid"/> of library <paramref name="libNid"/>,
    /// or 0 when no firmware module is shown for it.</summary>
    public uint EntryOf(uint libNid, uint nid)
    {
        lock (_lock)
        {
            return _entries.TryGetValue((libNid, nid), out (uint Entry, uint Svc) e) ? e.Entry : 0;
        }
    }

    /// <summary>The library whose export of function <paramref name="nid"/> has its entry at <paramref name="entry"/>,
    /// or 0.</summary>
    public uint LibraryOfEntry(uint entry, uint nid)
    {
        lock (_lock)
        {
            foreach (KeyValuePair<(uint LibNid, uint Nid), (uint Entry, uint Svc)> e in _entries)
            {
                if (e.Key.Nid == nid && e.Value.Entry == entry)
                {
                    return e.Key.LibNid;
                }
            }

            return 0;
        }
    }

    /// <summary>Guest code rewrote the start of the function (a hook): its entry no longer is the SVC into the HLE.</summary>
    public bool IsPatched(uint libNid, uint nid)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue((libNid, nid), out (uint Entry, uint Svc) e))
            {
                return false;
            }

            return GuestMemory.R32(e.Entry) != e.Svc || GuestMemory.R32(e.Entry + 4) != BxLr;
        }
    }

    /// <summary>The import stub through which firmware module <paramref name="module"/> calls function
    /// <paramref name="nid"/> of library <paramref name="libNid"/>, or 0.</summary>
    public uint ImportStubOf(string module, uint libNid, uint nid)
    {
        lock (_lock)
        {
            return _imports.TryGetValue((module, libNid, nid), out (uint Stub, uint[] Bound) i) ? i.Stub : 0;
        }
    }

    /// <summary>Guest code rewrote the import stub (an import hook): it is no longer what the linker bound.</summary>
    public bool IsImportPatched(string module, uint libNid, uint nid)
    {
        lock (_lock)
        {
            if (!_imports.TryGetValue((module, libNid, nid), out (uint Stub, uint[] Bound) i) || i.Bound.Length != StubWords)
            {
                return false;
            }

            return !GuestMemory.Span<uint>(i.Stub, StubWords).SequenceEqual(i.Bound);
        }
    }

    /// <summary>
    /// A call firmware module <paramref name="module"/> makes through its import of function <paramref name="nid"/> of
    /// library <paramref name="libNid"/>: when guest code hooked the import stub, the call runs the stub on the calling
    /// thread (<see cref="RunGuest"/>); otherwise it is the call of the export (<see cref="Call"/>: the export's own hook, else
    /// <paramref name="direct"/>).
    /// </summary>
    public uint CallImport(string module, uint libNid, uint nid, Func<uint> direct, params ReadOnlySpan<uint> args)
    {
        if (!IsImportPatched(module, libNid, nid) || GuestThread.Current is not { } thread)
        {
            return Call(libNid, nid, direct, args);
        }

        return RunGuest(thread, ImportStubOf(module, libNid, nid), args);
    }

    /// <summary>Runs the kernel code at <paramref name="address"/> on the calling thread. A kernel thread stays the
    /// kernel's; a user thread in a syscall stays its process's, as on the Vita, where kernel code a syscall runs sees
    /// the calling process as the current one (ksceKernelGetProcessId, the user/kernel copies, the access level).
    /// The emulated CPU has no privilege levels to switch: kernel memory is mapped in every process.</summary>
    private static uint RunGuest(GuestThread thread, uint address, ReadOnlySpan<uint> args)
    {
        return thread.CallGuest(address, args);
    }

    /// <summary>How many exported functions' entries guest code had patched when <see cref="_patchesCheckedAt"/> was
    /// <see cref="Cpu.BlockCache.CodeChanges"/> (-1: never counted).</summary>
    private int _patchedEntries;

    private long _patchesCheckedAt = -1;

    /// <summary>
    /// Whether any exported function's entry is patched (a hook): the fast check of <see cref="TryCallPatched"/>. Guest
    /// code that patches code drops its translations (the framework does, and so does a plugin's own copy followed by
    /// the cache maintenance the hardware needs), which moves <see cref="Cpu.BlockCache.CodeChanges"/>; only then are
    /// the entries counted again. With nothing patched and no code change since, it reads two fields.
    /// </summary>
    private bool AnyEntryPatched()
    {
        long changes = Cpu.BlockCache.CodeChanges;
        if (Volatile.Read(ref _patchesCheckedAt) == changes)
        {
            return Volatile.Read(ref _patchedEntries) != 0;
        }

        lock (_lock)
        {
            int patched = 0;
            foreach ((uint Entry, uint Svc) e in _entries.Values)
            {
                if (GuestMemory.R32(e.Entry) != e.Svc || GuestMemory.R32(e.Entry + 4) != BxLr)
                {
                    patched++;
                }
            }

            Volatile.Write(ref _patchedEntries, patched);
            Volatile.Write(ref _patchesCheckedAt, changes);
            return patched != 0;
        }
    }

    /// <summary>
    /// A call the HLE makes to the firmware's own exported function <paramref name="nid"/> of library
    /// <paramref name="libNid"/> (the firmware calls it by its address, so a hook on its entry sees the call, as a hook
    /// on the Vita patches the function's code itself): when guest code patched the entry, runs it on the calling
    /// thread with <paramref name="a0"/>-<paramref name="a3"/>, puts its r0 in <paramref name="result"/> and returns
    /// true; otherwise returns false and the caller runs the HLE function directly. Without a patch it allocates
    /// nothing and reads two fields (<see cref="AnyEntryPatched"/>).
    /// </summary>
    public bool TryCallPatched(uint libNid, uint nid, out int result, uint a0, uint a1 = 0, uint a2 = 0, uint a3 = 0)
    {
        result = 0;
        if (!AnyEntryPatched() || GuestThread.Current is not { } thread || !IsPatched(libNid, nid))
        {
            return false;
        }

        result = (int)RunGuest(thread, EntryOf(libNid, nid), [a0, a1, a2, a3]);
        return true;
    }

    /// <summary>
    /// A call of the firmware's function <paramref name="nid"/> of library <paramref name="libNid"/> the way the firmware
    /// makes it, by its address: when guest code hooked the function, the call runs the guest code at its entry on the
    /// calling thread, in the calling thread's process (as the kernel runs a syscall's driver call: <see cref="RunGuest"/>)
    /// and returns its r0; otherwise
    /// <paramref name="direct"/>, the HLE function itself, with no guest round trip.
    /// </summary>
    public uint Call(uint libNid, uint nid, Func<uint> direct, params ReadOnlySpan<uint> args)
    {
        if (!IsPatched(libNid, nid) || GuestThread.Current is not { } thread)
        {
            return direct();
        }

        return RunGuest(thread, EntryOf(libNid, nid), args);
    }

    /// <summary>
    /// A relocatable SCE module (ET_SCE_RELEXEC) laid out as the firmware module is: its loadable segments with the
    /// firmware's sizes, in the same order (so module_get_offset, ksceKernelGetModuleInfo and code that reaches the
    /// module by segment offset see the console's addresses), the executable ones filled with
    /// <see cref="TrapFillWord"/>, then one read/write/execute segment with the firmware module's module_info and
    /// export tables and a 16-byte entry per exported function; the pointers in the tables carry R_ARM_ABS32
    /// relocations so the loader places it anywhere in the kernel region; the module's imports are 0x34-byte import
    /// entries with a 16-byte stub per function, which the loader links. A module with more loadable segments than the
    /// tables segment can follow (module_info's segment is two bits of e_entry) is shown with the tables segment only.
    /// Returns the ELF, where each exported function's entry is in the tables segment, where each import stub is, and
    /// the tables segment's number.
    /// </summary>
    private static (byte[] Elf, List<(int Library, int Function, uint Offset)> Stubs, List<(int Library, int Function, uint Offset)> ImportStubs, int TablesSegment) BuildShell(
        string name, uint moduleNid, ushort attributes, List<(string Library, uint LibNid, ushort Attributes, uint[] FuncNids)> libraries,
        List<(string Library, uint LibNid, uint[] FuncNids)> imports, IReadOnlyList<(uint MemSize, uint Flags)> firmwareSegments)
    {
        IReadOnlyList<(uint MemSize, uint Flags)> realSegments = firmwareSegments.Count <= MaxRealSegments ? firmwareSegments : [];
        int tablesSegment = realSegments.Count;
        const int InfoSize = 0x5C, ExportSize = 0x20, ImportSize = 0x34, StubSize = 0x10;
        int functions = libraries.Sum(l => l.FuncNids.Length);
        int importedFunctions = imports.Sum(i => i.FuncNids.Length);
        int exportsAt = (InfoSize + 0xF) & ~0xF;
        int importsAt = exportsAt + libraries.Count * ExportSize;
        int tablesAt = importsAt + imports.Count * ImportSize;
        int stringsAt = tablesAt + (functions + importedFunctions) * 8;
        int stringBytes = libraries.Sum(l => Encoding.ASCII.GetByteCount(l.Library) + 1) + imports.Sum(i => Encoding.ASCII.GetByteCount(i.Library) + 1);
        int stubsAt = (stringsAt + stringBytes + 0xF) & ~0xF;
        int importStubsAt = stubsAt + functions * StubSize;
        int size = (importStubsAt + importedFunctions * StubSize + 0xFFF) & ~0xFFF;

        byte[] segment = new byte[size];
        var relocations = new List<(uint Offset, uint Value)>();
        var stubs = new List<(int Library, int Function, uint Offset)>();
        var importStubs = new List<(int Library, int Function, uint Offset)>();
        void Pointer(int at, uint target)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(at), target);
            relocations.Add(((uint)at, target));
        }

        // module_info
        BinaryPrimitives.WriteUInt16LittleEndian(segment.AsSpan(0), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(segment.AsSpan(2), 0x0101);
        Encoding.ASCII.GetBytes(name.Length > 26 ? name[..26] : name).CopyTo(segment, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(0x24), (uint)exportsAt);
        BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(0x28), (uint)importsAt);
        BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(0x2C), (uint)importsAt);
        BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(0x30), (uint)tablesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(0x34), moduleNid);

        int table = tablesAt, strings = stringsAt, stub = stubsAt;
        for (int i = 0; i < libraries.Count; i++)
        {
            (string library, uint libNid, ushort libraryAttributes, uint[] nids) = libraries[i];
            int export = exportsAt + i * ExportSize;
            int nidTable = table;
            int entryTable = table + nids.Length * 4;
            table += nids.Length * 8;
            BinaryPrimitives.WriteUInt16LittleEndian(segment.AsSpan(export), ExportSize);
            BinaryPrimitives.WriteUInt16LittleEndian(segment.AsSpan(export + 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(segment.AsSpan(export + 4), libraryAttributes);
            BinaryPrimitives.WriteUInt16LittleEndian(segment.AsSpan(export + 6), (ushort)nids.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(export + 0x10), libNid);
            Encoding.ASCII.GetBytes(library).CopyTo(segment, strings);
            Pointer(export + 0x14, (uint)strings);
            strings += Encoding.ASCII.GetByteCount(library) + 1;
            Pointer(export + 0x18, (uint)nidTable);
            Pointer(export + 0x1C, (uint)entryTable);
            for (int k = 0; k < nids.Length; k++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(nidTable + k * 4), nids[k]);
                Pointer(entryTable + k * 4, (uint)stub);
                stubs.Add((i, k, (uint)stub));
                stub += StubSize;
            }
        }

        int importStub = importStubsAt;
        for (int i = 0; i < imports.Count; i++)
        {
            (string library, uint libNid, uint[] nids) = imports[i];
            int import = importsAt + i * ImportSize;
            int nidTable = table;
            int entryTable = table + nids.Length * 4;
            table += nids.Length * 8;
            BinaryPrimitives.WriteUInt16LittleEndian(segment.AsSpan(import), ImportSize);
            BinaryPrimitives.WriteUInt16LittleEndian(segment.AsSpan(import + 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(segment.AsSpan(import + 6), (ushort)nids.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(import + 0x10), libNid);
            Encoding.ASCII.GetBytes(library).CopyTo(segment, strings);
            Pointer(import + 0x14, (uint)strings);
            strings += Encoding.ASCII.GetByteCount(library) + 1;
            Pointer(import + 0x1C, (uint)nidTable);
            Pointer(import + 0x20, (uint)entryTable);
            for (int k = 0; k < nids.Length; k++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(nidTable + k * 4), nids[k]);
                Pointer(entryTable + k * 4, (uint)importStub);
                importStubs.Add((i, k, (uint)importStub));
                importStub += StubSize;
            }
        }

        // the ELF: header, the firmware's PT_LOADs, the tables' PT_LOAD and PT_SCE_RELA (format 0 entries: R_ARM_ABS32
        // within the tables segment), the segments, the relocations
        const int HeaderSize = 0x100;
        byte[] rela = new byte[relocations.Count * 12];
        uint relocationInfo = (uint)tablesSegment << 4 | 2u << 8 | (uint)tablesSegment << 16;
        for (int i = 0; i < relocations.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rela.AsSpan(i * 12), relocationInfo);
            BinaryPrimitives.WriteUInt32LittleEndian(rela.AsSpan(i * 12 + 4), relocations[i].Value);
            BinaryPrimitives.WriteUInt32LittleEndian(rela.AsSpan(i * 12 + 8), relocations[i].Offset);
        }

        long realBytes = 0;
        foreach ((uint memSize, uint flags) in realSegments)
        {
            if ((flags & ExecutableFlag) != 0)
            {
                realBytes += memSize;
            }
        }

        byte[] elf = new byte[HeaderSize + realBytes + size + rela.Length];
        Span<byte> header = elf;
        header[0] = 0x7F;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = 1;
        header[5] = 1;
        header[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], 0xFE04);
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..], 0x28);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)tablesSegment << 30);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 0x34);
        BinaryPrimitives.WriteUInt16LittleEndian(header[40..], 0x34);
        BinaryPrimitives.WriteUInt16LittleEndian(header[42..], 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(header[44..], (ushort)(realSegments.Count + 2));
        int at = HeaderSize;
        for (int i = 0; i < realSegments.Count; i++)
        {
            (uint memSize, uint flags) = realSegments[i];
            uint fileSize = (flags & ExecutableFlag) != 0 ? memSize : 0;
            ProgramHeader(header, 0x34 + i * 0x20, 1, (uint)at, fileSize, memSize, flags);
            MemoryMarshal.Cast<byte, uint>(elf.AsSpan(at, (int)fileSize & ~3)).Fill(TrapFillWord);
            at += (int)fileSize;
        }

        int tablesHeader = 0x34 + realSegments.Count * 0x20;
        ProgramHeader(header, tablesHeader, 1, (uint)at, (uint)size, (uint)size, 7);
        ProgramHeader(header, tablesHeader + 0x20, 0x60000000, (uint)(at + size), (uint)rela.Length, (uint)rela.Length, 0);
        segment.CopyTo(elf, at);
        rela.CopyTo(elf, at + size);
        return (elf, stubs, importStubs, tablesSegment);
    }

    private static void ProgramHeader(Span<byte> header, int at, uint type, uint offset, uint fileSize, uint memSize, uint flags)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header[at..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(at + 4)..], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(at + 16)..], fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(at + 20)..], memSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(at + 24)..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(at + 28)..], 0x10);
    }
}
