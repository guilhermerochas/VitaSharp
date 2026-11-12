using VitaSharp.Core.Memory;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Loader;

public sealed class LoadedModule
{
    public int Uid;
    public string Name = "";
    public string Path = "";
    public uint ModuleNid;
    public uint StartEntry, StopEntry, ExitEntry;
    public uint TlsInit, TlsInitSize, TlsAreaSize;
    public uint ExidxTop, ExidxEnd, ExtabTop, ExtabEnd;
    public uint ModuleInfoAddr;
    /// <summary>Guest address ranges of the export and import tables (taiGetModuleInfo).</summary>
    public uint ExportsStart, ExportsEnd, ImportsStart, ImportsEnd;
    public ushort Attributes, Version;
    public Dictionary<int, SegmentInfo> Segments = new();
    public Dictionary<int, (uint Flags, uint MemSz, uint FileSz)> SegmentFlags = new();
    public List<(string Library, uint LibNid, uint[] FuncNids)> Imports = new();
    public List<(string Library, uint LibNid, uint[] FuncNids, uint[] VarNids)> Exports = new();
    /// <summary>A kernel module: loaded in the kernel region by the kernel loader (<see cref="ModuleLoader.Load"/> with
    /// kernel set). Only its syscall-exported libraries are visible to user modules; it imports only kernel exports.</summary>
    public bool IsKernel;
    /// <summary>module_start already ran (a kernel module starts when it is loaded, before the process).</summary>
    public bool Started;

    public override string ToString() => $"{Name} uid={Uid:X} start={StartEntry:X8}";
}

/// <summary>Resolves imports that no loaded module exports (i.e. HLE functions/variables).</summary>
public interface IImportResolver
{
    /// <summary>Returns the 24-bit SVC number of an HLE function for this NID (always succeeds; unknown NIDs get a logging stub).</summary>
    uint GetHleFunctionIndex(uint nid, uint libNid, string libName);
    /// <summary>Guest address of an HLE-provided variable, or 0.</summary>
    uint GetHleVariable(uint nid);
    /// <summary>Guest address of an HLE function implemented as guest code (Hle/HleGuestCode), or 0: the import then
    /// jumps there instead of trapping.</summary>
    uint GetHleGuestCode(uint nid) => 0;
}

/// <summary>Who may import a library a module exports.</summary>
public enum ExportScope : byte
{
    /// <summary>A user module's library: user modules only (the kernel never links against user code).</summary>
    User,
    /// <summary>A kernel module's library: kernel modules only.</summary>
    Kernel,
    /// <summary>A kernel module's library marked as a syscall export: user modules call it (the kernel's user API) and
    /// kernel modules may import it too.</summary>
    Syscall,
}

/// <summary>
/// Cross-module symbol table. Function imports are bound through 16-byte stubs that are rewritten
/// whenever a module exporting the NID (LLE) is loaded; until then they trap into HLE via SVC. Kernel and user modules
/// see each other's exports only as the firmware allows (<see cref="ExportScope"/>).
/// </summary>
public sealed class Linker(IImportResolver importResolver)
{
    private const uint NidModuleStop = 0x79F8E492, NidModuleExit = 0x913482A9, NidModuleStart = 0x935CD196;
    private const uint NidModuleInfo = 0x6C2224BA, NidSyslib = 0x936C8A78, NidProcessParam = 0x70FBA1E7;

    /// <summary>The export entry attribute bit of a library user code reaches through syscalls
    /// (SCE_LIBRARY_ATTR_SYSCALL_EXPORT).</summary>
    public const ushort SyscallExportAttr = 0x4000;

    private readonly object _lock = new();
    private readonly Dictionary<uint, uint> _funcExports = new();
    private readonly Dictionary<uint, ExportScope> _funcExportScopes = new();
    private readonly Dictionary<(uint lib, uint nid), uint> _funcExportsByLib = new();
    private readonly Dictionary<(uint lib, uint nid), ExportScope> _funcExportScopesByLib = new();
    private readonly Dictionary<uint, uint> _varExports = new();
    private readonly List<(uint nid, uint libNid, string libName, uint stub, bool kernel)> _funcBindings = new();

    /// <summary>True when an importer (kernel or user) may bind to an export of <paramref name="scope"/>.</summary>
    private static bool Visible(ExportScope scope, bool kernelImporter)
    {
        return kernelImporter ? scope != ExportScope.User : scope != ExportScope.Kernel;
    }

    /// <summary>The export an import of <paramref name="nid"/> from <paramref name="libNid"/> binds to: the library's
    /// own export first, then the same function exported by any library, both only when visible to the importer.</summary>
    private bool TryFindExport(uint nid, uint libNid, bool kernelImporter, out uint target)
    {
        if (_funcExportsByLib.TryGetValue((libNid, nid), out target) && Visible(_funcExportScopesByLib[(libNid, nid)], kernelImporter))
        {
            return true;
        }

        if (!_funcExports.TryGetValue(nid, out target))
        {
            // no loaded module exports the function at all (the common case of an HLE import)
            return false;
        }

        if (Visible(_funcExportScopes[nid], kernelImporter))
        {
            return true;
        }

        foreach (((uint lib, uint nid) key, uint addr) in _funcExportsByLib)

        {
            if (key.nid == nid && Visible(_funcExportScopesByLib[key], kernelImporter))
            {
                target = addr;
                return true;
            }
        }

        target = 0;
        return false;
    }
    private readonly List<(uint nid, byte[] relocs, Dictionary<int, SegmentInfo> segs)> _varBindings = new();
    private readonly Dictionary<uint, string> _varSources = new(); // variable import NID -> "library (importing module)", for the log

    public uint ProcessParam { get; private set; }

    /// <summary>Address ranges whose import stubs keep their binding while modules load (a module loaded for the
    /// emulator's own use must not take over the program's imports); null = none.</summary>
    public List<(uint Start, uint Size)>? KeepBindingsIn { get; set; }

    private bool Kept(uint stub)
    {
        if (KeepBindingsIn is not { } ranges)
        {
            return false;
        }

        foreach ((uint s, uint n) in ranges)
        {
            if (stub - s < n)
            {
                return true;
            }
        }

        return false;
    }

    public Action<uint, uint>? InvalidateCode { get; set; }
    public Action<string>? Log { get; set; }

    public bool TryGetFunctionExport(uint nid, out uint addr)
    {
        lock (_lock)
        {
            return _funcExports.TryGetValue(nid, out addr);
        }
    }

    /// <summary>Import stubs bound to function <paramref name="nid"/> (any library when <paramref name="libNid"/> is
    /// null) by kernel importers (<paramref name="kernel"/>) or by user importers, in binding order.</summary>
    public List<uint> BoundStubs(uint nid, uint? libNid, bool kernel)
    {
        lock (_lock)
        {
            return _funcBindings.Where(b => b.nid == nid && (libNid == null || b.libNid == libNid) && b.kernel == kernel).Select(b => b.stub).ToList();
        }
    }

    /// <summary>The export an import of <paramref name="nid"/> from library <paramref name="libNid"/> by a kernel
    /// (<paramref name="kernelImporter"/>) or user module binds to, when a loaded module provides one it may see.</summary>
    public bool TryGetVisibleExport(uint nid, uint libNid, bool kernelImporter, out uint addr)
    {
        lock (_lock)
        {
            return TryFindExport(nid, libNid, kernelImporter, out addr);
        }
    }

    /// <summary>The function <paramref name="nid"/> of library <paramref name="libNid"/> a loaded module exports.</summary>
    public bool TryGetFunctionExport(uint libNid, uint nid, out uint addr)
    {
        lock (_lock)
        {
            return _funcExportsByLib.TryGetValue((libNid, nid), out addr);
        }
    }

    public static void WriteHleStub(uint stub, uint svcIndex, uint nid)
    {
        GuestMemory.W32(stub, 0xEF000000 | (svcIndex & 0xFFFFFF)); // svc #index
        GuestMemory.W32(stub + 4, 0xE12FFF1E);                     // bx lr
        GuestMemory.W32(stub + 8, nid);
    }

    private void BindHle(uint stub, uint nid, uint libNid, string libName)
    {
        uint code = importResolver.GetHleGuestCode(nid);
        if (code != 0)
        {
            WriteJumpStub(stub, code);
        }
        else
        {
            WriteHleStub(stub, importResolver.GetHleFunctionIndex(nid, libNid, libName), nid);
        }
    }

    /// <summary>
    /// After a save state load: binds again every import stub that is still one of ours for an HLE function (an SVC
    /// stub for its NID, or a jump into <paramref name="isOldGuestCode"/>, the guest-code routines of the saved session),
    /// so the loaded session runs this build's routines (Hle/HleGuestCode) and its SVCs, not the ones of the build that
    /// saved it. Stubs bound to a module's export and stubs guest code patched (hooks) are left alone.

    /// Returns the number of stubs rewritten.
    /// </summary>
    public int RebindHleStubs(Func<uint, bool> isOldGuestCode)
    {
        int n = 0;
        lock (_lock)
        {
            foreach ((uint nid, uint libNid, string? libName, uint stub, bool kernel) in _funcBindings)
            {
                if (TryFindExport(nid, libNid, kernel, out _) || Kept(stub))
                {
                    continue;
                }

                uint w0 = GuestMemory.R32(stub), w1 = GuestMemory.R32(stub + 4), w2 = GuestMemory.R32(stub + 8);
                bool svc = (w0 & 0xFF000000) == 0xEF000000 && w1 == 0xE12FFF1E && w2 == nid;
                bool jump = (w0 & 0xFFF0F000) == 0xE300C000 && (w1 & 0xFFF0F000) == 0xE340C000 && w2 == 0xE12FFF1C
                    && isOldGuestCode(((w1 & 0xF0000) << 12) | ((w1 & 0xFFF) << 16) | ((w0 & 0xF0000) >> 4) | (w0 & 0xFFF));
                if (!svc && !jump)
                {
                    continue;
                }

                BindHle(stub, nid, libNid, libName);
                InvalidateCode?.Invoke(stub, 12);
                n++;
            }
        }

        return n;
    }

    public static void WriteJumpStub(uint stub, uint target)
    {
        GuestMemory.W32(stub, 0xE300C000 | ((target & 0xF000) << 4) | (target & 0xFFF));                 // movw r12, #lo
        GuestMemory.W32(stub + 4, 0xE340C000 | (((target >> 16) & 0xF000) << 4) | ((target >> 16) & 0xFFF)); // movt r12, #hi
        GuestMemory.W32(stub + 8, 0xE12FFF1C);                                                             // bx r12
    }

    /// <summary>Binds one function import stub: to a loaded module's export the importer may see (a kernel importer
    /// when <paramref name="kernel"/>), else to the HLE.</summary>
    internal void BindFunctionImport(uint nid, uint libNid, string libName, uint stub, Dictionary<int, SegmentInfo> segs, bool kernel = false)
    {
        lock (_lock)
        {
            if (TryFindExport(nid, libNid, kernel, out uint target))
            {
                WriteJumpStub(stub, target);
            }
            else
            {
                BindHle(stub, nid, libNid, libName);
            }

            _funcBindings.Add((nid, libNid, libName, stub, kernel));
        }
        // reftable: relocations applied against the stub address
        uint reftable = GuestMemory.R32(stub + 12);
        if (reftable != 0)
        {
            uint size = (GuestMemory.R32(reftable) >> 4) & 0xFFFFFF;
            if (size > 4)
            {
                Relocation.Relocate(GuestMemory.Span(reftable + 4, (int)(size - 4)), segs, true, stub);
            }
        }
    }

    internal void BindVariableImport(uint nid, uint entry, Dictionary<int, SegmentInfo> segs, string source = "")
    {
        uint hdr = GuestMemory.R32(entry);
        uint size = (hdr >> 4) & 0xFFFFFF;
        byte[] relocs = size > 4 ? GuestMemory.Span(entry + 4, (int)(size - 4)).ToArray() : [];
        uint addr;
        lock (_lock)
        {
            if (!_varExports.TryGetValue(nid, out addr))
            {
                // 0 = not provided yet: a module loaded later (the game's own libc.suprx, say) can still export it;
                // RegisterExports then applies these relocations, and UnresolvedVariables lists what is left
                addr = importResolver.GetHleVariable(nid);
            }
            _varBindings.Add((nid, relocs, segs));
            _varSources.TryAdd(nid, source);
        }
        if (relocs.Length > 0 && addr != 0)
        {
            Relocation.Relocate(relocs, segs, true, addr);
        }
    }

    /// <summary>The library a variable import NID is imported from and the module that imports it.</summary>
    public string VariableImportSource(uint nid)
    {
        lock (_lock)
        {
            return _varSources.GetValueOrDefault(nid, "");
        }
    }

    /// <summary>NIDs of variable imports that neither a loaded module nor HLE provides (their relocations were never
    /// applied, so the importing code reads whatever the linker left in its literal pools).</summary>
    public List<uint> UnresolvedVariables()
    {
        lock (_lock)
        {
            return _varBindings.Select(b => b.nid).Distinct()
                .Where(n => !_varExports.ContainsKey(n) && importResolver.GetHleVariable(n) == 0).ToList();
        }
    }

    /// <summary>Registers one exported library of <paramref name="loadedModule"/> and rebinds the import stubs that may see it.</summary>
    internal void RegisterExports(LoadedModule loadedModule, uint libNid, uint[] nids, uint[] entries, int funcCount, ExportScope scope = ExportScope.User)
    {
        lock (_lock)
        {
            for (int i = 0; i < nids.Length; i++)
            {
                uint nid = nids[i], addr = entries[i];
                if (i < funcCount)
                {
                    switch (nid)
                    {
                        case NidModuleStart:
                            loadedModule.StartEntry = addr;
                            continue;
                        case NidModuleStop:
                            loadedModule.StopEntry = addr;
                            continue;
                        case NidModuleExit:
                            loadedModule.ExitEntry = addr;
                            continue;
                    }
                    if (_funcExports.TryAdd(nid, addr))
                    {
                        _funcExportScopes[nid] = scope;
                    }

                    _funcExportsByLib[(libNid, nid)] = addr;
                    _funcExportScopesByLib[(libNid, nid)] = scope;
                    foreach ((uint bnid, uint blib, string _, uint stub, bool kernel) in _funcBindings)
                    {
                        if (bnid != nid || blib != libNid || Kept(stub) || !Visible(scope, kernel))
                        {
                            continue;
                        }

                        WriteJumpStub(stub, addr);
                        InvalidateCode?.Invoke(stub, 12);
                    }
                }
                else
                {
                    if (nid == NidProcessParam)
                    {
                        if (ProcessParam == 0)
                        {
                            ProcessParam = addr;
                        }

                        continue;
                    }
                    if (nid is NidModuleInfo or NidSyslib)
                    {
                        continue;
                    }

                    _varExports[nid] = addr;
                    foreach ((uint bnid, byte[]? relocs, Dictionary<int, SegmentInfo>? segs) in _varBindings)
                    {
                        if (bnid == nid && relocs.Length > 0)
                        {
                            Relocation.Relocate(relocs, segs, true, addr);
                        }
                    }
                }
            }
        }
    }

    private static bool InModule(LoadedModule loadedModule, uint addr)
    {
        addr &= ~1u; // Thumb entry points
        foreach (SegmentInfo segmentInfo in loadedModule.Segments.Values)
        {
            if (addr >= segmentInfo.Address && addr - segmentInfo.Address < segmentInfo.Size)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Undoes what loading <paramref name="loadedModule"/> did to the symbol table (sceKernelUnloadModule): drops its exports and
    /// its own import bindings, then rebinds every other module's import that pointed into it to the next provider
    /// (another loaded module exporting the NID) or back to the HLE stub. Variable imports are relocated to an HLE
    /// variable when one exists. Returns the number of function stubs rebound.
    /// </summary>
    public int UnregisterModule(LoadedModule loadedModule)
    {
        int rebound = 0;
        lock (_lock)
        {
            // the module's own import stubs/relocations live in memory that is about to be freed
            _funcBindings.RemoveAll(b => InModule(loadedModule, b.stub));
            _varBindings.RemoveAll(b => ReferenceEquals(b.segs, loadedModule.Segments));

            foreach ((uint lib, uint nid) key in _funcExportsByLib.Where(kv => InModule(loadedModule, kv.Value)).Select(kv => kv.Key).ToArray())
            {
                _funcExportsByLib.Remove(key);
                _funcExportScopesByLib.Remove(key);
            }

            foreach (uint nid in _funcExports.Where(kv => InModule(loadedModule, kv.Value)).Select(kv => kv.Key).ToArray())
            {
                _funcExports.Remove(nid);
                _funcExportScopes.Remove(nid);
                foreach (KeyValuePair<(uint lib, uint nid), uint> kv in _funcExportsByLib)
                {
                    if (kv.Key.nid == nid)
                    {
                        _funcExports[nid] = kv.Value;
                        _funcExportScopes[nid] = _funcExportScopesByLib[kv.Key];
                        break;
                    }
                }
            }

            foreach ((uint nid, uint libNid, string? libName, uint stub, bool kernel) in _funcBindings)
            {
                // only stubs that jumped into the unloaded module change
                uint w0 = GuestMemory.R32(stub), w1 = GuestMemory.R32(stub + 4);
                if ((w0 & 0xFFF0F000) != 0xE300C000 || (w1 & 0xFFF0F000) != 0xE340C000)
                {
                    continue;
                }

                uint target = ((w0 >> 4) & 0xF000) | (w0 & 0xFFF) | ((((w1 >> 4) & 0xF000) | (w1 & 0xFFF)) << 16);
                if (!InModule(loadedModule, target))
                {
                    continue;
                }

                if (TryFindExport(nid, libNid, kernel, out uint next))
                {
                    WriteJumpStub(stub, next);
                }
                else
                {
                    BindHle(stub, nid, libNid, libName);
                }

                InvalidateCode?.Invoke(stub, 12);
                rebound++;
            }

            var removedVars = _varExports.Where(kv => InModule(loadedModule, kv.Value)).Select(kv => kv.Key).ToHashSet();
            foreach (uint nid in removedVars)
            {
                _varExports.Remove(nid);
            }

            foreach ((uint bnid, byte[]? relocs, Dictionary<int, SegmentInfo>? segs) in _varBindings)
            {
                if (!removedVars.Contains(bnid) || relocs.Length == 0)
                {
                    continue;
                }

                uint addr = importResolver.GetHleVariable(bnid);
                if (addr != 0)
                {
                    Relocation.Relocate(relocs, segs, true, addr);
                }
                else
                {
                    Log?.Invoke($"Variable import NID {bnid:X8} pointed into unloaded module {loadedModule.Name}");
                }
            }
            if (InModule(loadedModule, ProcessParam))
            {
                ProcessParam = 0;
            }
        }
        return rebound;
    }
}

public static class ModuleLoader
{
    private const uint PT_LOAD = 1, PT_SCE_RELA = 0x60000000;
    private const ushort ET_SCE_EXEC = 0xFE00, ET_SCE_RELEXEC = 0xFE04;

    /// <summary>
    /// Loads a module: segments, relocations, module info, exports and imports. A kernel module
    /// (<paramref name="kernel"/>) goes to the kernel region, which every address space maps, at any free address (the
    /// kernel loader relocates its modules); its exports are kernel exports except the libraries marked as syscall
    /// exports, and its imports bind only to kernel exports.
    /// </summary>
    public static LoadedModule Load(byte[] elf, string path, GuestMemory mem, Linker linker, int uid, bool kernel = false)
    {
        ReadOnlySpan<byte> e = elf;
        if (U32LE(e, 0) != 0x464C457F)
        {
            throw new InvalidDataException($"{path}: not an ELF");
        }

        ushort type = U16LE(e, 16);
        if (type != ET_SCE_EXEC && type != ET_SCE_RELEXEC)
        {
            throw new NotSupportedException($"{path}: ELF type 0x{type:X}");
        }

        uint entry = U32LE(e, 24), phoff = U32LE(e, 28);
        ushort phnum = U16LE(e, 44);
        bool relocatable = type == ET_SCE_RELEXEC;

        if (kernel && !relocatable)
        {
            throw new NotSupportedException($"{path}: a kernel module must be relocatable");
        }

        var m = new LoadedModule { Uid = uid, Path = path, IsKernel = kernel };
        var relas = new List<byte[]>();
        for (int i = 0; i < phnum; i++)
        {
            ReadOnlySpan<byte> ph = e.Slice((int)phoff + i * 32, 32);
            uint ptype = U32LE(ph, 0), off = U32LE(ph, 4), vaddr = U32LE(ph, 8), filesz = U32LE(ph, 16), memsz = U32LE(ph, 20), flags = U32LE(ph, 24);
            if (ptype == PT_LOAD && memsz != 0)
            {
                string name = $"{System.IO.Path.GetFileName(path)}:seg{i}";
                // a fixed segment only needs its range free in this process's address space (another process may use
                // the same addresses in its own); a relocatable module takes its preferred address only when no process
                // uses it, else any free range
                uint addr;
                if (kernel)
                {
                    addr = mem.Alloc(memsz, name, MemoryRegion.Kernel, 0x1000);
                }
                else
                {
                    addr = vaddr != 0 ? mem.TryAllocAt(vaddr, memsz, name, exclusive: relocatable) : 0;
                }

                if (addr == 0)
                {
                    if (!relocatable)
                    {
                        throw new InvalidOperationException($"{path}: cannot map fixed segment at 0x{vaddr:X8}");
                    }

                    addr = mem.Alloc(memsz, name, MemoryRegion.User, 0x10000);
                }
                e.Slice((int)off, (int)filesz).CopyTo(GuestMemory.Span(addr, (int)filesz));
                m.Segments[i] = new SegmentInfo(addr, vaddr, memsz);
                m.SegmentFlags[i] = (flags, memsz, filesz);
            }
            else if (ptype == PT_SCE_RELA)
            {
                relas.Add(e.Slice((int)off, (int)filesz).ToArray());
            }
        }
        foreach (byte[] r in relas)
        {
            Relocation.Relocate(r, m.Segments);
        }

        // module_info
        int infoSeg = (int)(entry >> 30);
        uint infoBase = m.Segments[infoSeg].Address;
        uint mi = infoBase + (entry & 0x3FFFFFFF);
        m.ModuleInfoAddr = mi;
        Span<byte> info = GuestMemory.Span(mi, 0x5C);
        m.Attributes = U16LE(info, 0);
        m.Version = U16LE(info, 2);
        m.Name = CString(info.Slice(4, 27));
        uint exportTop = U32LE(info, 0x24), exportEnd = U32LE(info, 0x28);
        uint importTop = U32LE(info, 0x2C), importEnd = U32LE(info, 0x30);
        m.ModuleNid = U32LE(info, 0x34);
        m.ExportsStart = infoBase + exportTop;
        m.ExportsEnd = infoBase + exportEnd;
        m.ImportsStart = infoBase + importTop;
        m.ImportsEnd = infoBase + importEnd;
        uint tlsStart = U32LE(info, 0x38);
        m.TlsInitSize = U32LE(info, 0x3C);
        m.TlsAreaSize = U32LE(info, 0x40);
        uint modStart = U32LE(info, 0x44), modStop = U32LE(info, 0x48);
        m.TlsInit = tlsStart != 0 ? infoBase + tlsStart : 0;
        if (modStart != 0 && modStart != 0xFFFFFFFF)
        {
            m.StartEntry = infoBase + modStart;
        }

        if (modStop != 0 && modStop != 0xFFFFFFFF)
        {
            m.StopEntry = infoBase + modStop;
        }

        uint exidxTop = U32LE(info, 0x4C), exidxEnd = U32LE(info, 0x50);
        if (exidxTop != 0)
        {
            m.ExidxTop = infoBase + exidxTop;
            m.ExidxEnd = infoBase + exidxEnd;
        }
        uint extabTop = U32LE(info, 0x54), extabEnd = U32LE(info, 0x58);
        if (extabTop != 0)
        {
            m.ExtabTop = infoBase + extabTop;
            m.ExtabEnd = infoBase + extabEnd;
        }

        // exports first (so a module can resolve against itself), then imports
        for (uint p = infoBase + exportTop; p < infoBase + exportEnd;)
        {
            Span<byte> ex = GuestMemory.Span(p, 0x20);
            ushort size = U16LE(ex, 0);
            ushort attr = U16LE(ex, 4);
            ushort nfunc = U16LE(ex, 6);
            uint nvar = U32LE(ex, 8);
            uint libNid = U32LE(ex, 0x10), libName = U32LE(ex, 0x14), nidTable = U32LE(ex, 0x18), entryTable = U32LE(ex, 0x1C);
            int total = nfunc + (int)nvar;
            uint[] nids = GuestMemory.Span<uint>(nidTable, total).ToArray();
            uint[] entries = GuestMemory.Span<uint>(entryTable, total).ToArray();
            m.Exports.Add((GuestMemory.ReadCString(libName), libNid, nids[..nfunc], nids[nfunc..]));
            ExportScope scope = !kernel ? ExportScope.User : (attr & Linker.SyscallExportAttr) != 0 ? ExportScope.Syscall : ExportScope.Kernel;
            linker.RegisterExports(m, libNid, nids, entries, nfunc, scope);
            if (size == 0)
            {
                break;
            }

            p += size;
        }

        for (uint p = infoBase + importTop; p < infoBase + importEnd;)
        {
            Span<byte> im = GuestMemory.Span(p, 0x34);
            ushort size = U16LE(im, 0);
            ushort nfunc = U16LE(im, 6), nvar = U16LE(im, 8);
            uint libNid, libName, funcNids, funcEntries, varNids, varEntries;
            if (size == 0x24)
            {
                libNid = U32LE(im, 0x0C);
                libName = U32LE(im, 0x10);
                funcNids = U32LE(im, 0x14);
                funcEntries = U32LE(im, 0x18);
                varNids = U32LE(im, 0x1C);
                varEntries = U32LE(im, 0x20);
            }
            else
            {
                libNid = U32LE(im, 0x10);
                libName = U32LE(im, 0x14);
                funcNids = U32LE(im, 0x1C);
                funcEntries = U32LE(im, 0x20);
                varNids = U32LE(im, 0x24);
                varEntries = U32LE(im, 0x28);
            }
            string lib = GuestMemory.ReadCString(libName);
            uint[] fn = nfunc > 0 ? GuestMemory.Span<uint>(funcNids, nfunc).ToArray() : [];
            uint[] fe = nfunc > 0 ? GuestMemory.Span<uint>(funcEntries, nfunc).ToArray() : [];
            for (int i = 0; i < nfunc; i++)
            {
                linker.BindFunctionImport(fn[i], libNid, lib, fe[i], m.Segments, kernel);
            }

            if (nvar > 0)
            {
                uint[] vn = GuestMemory.Span<uint>(varNids, nvar).ToArray();
                uint[] ve = GuestMemory.Span<uint>(varEntries, nvar).ToArray();
                for (int i = 0; i < nvar; i++)
                {
                    linker.BindVariableImport(vn[i], ve[i], m.Segments, $"{lib} (imported by {m.Name})");
                }
            }
            m.Imports.Add((lib, libNid, fn));
            if (size == 0)
            {
                break;
            }

            p += size;
        }
        return m;
    }
}
