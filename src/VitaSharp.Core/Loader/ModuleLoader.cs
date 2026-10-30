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

    public override string ToString() => $"{Name} uid={Uid:X} start={StartEntry:X8}";
}

/// <summary>Resolves imports that no loaded module exports (i.e. HLE functions/variables).</summary>
public interface IImportResolver
{
    /// <summary>Returns the 24-bit SVC number of an HLE function for this NID (always succeeds; unknown NIDs get a logging stub).</summary>
    uint GetHleFunctionIndex(uint nid, uint libNid, string libName);
    /// <summary>Guest address of an HLE-provided variable, or 0.</summary>
    uint GetHleVariable(uint nid);
}

/// <summary>
/// Cross-module symbol table. Function imports are bound through 16-byte stubs that are rewritten
/// whenever a module exporting the NID (LLE) is loaded; until then they trap into HLE via SVC.
/// </summary>
public sealed class Linker(IImportResolver hle)
{
    const uint NidModuleStop = 0x79F8E492, NidModuleExit = 0x913482A9, NidModuleStart = 0x935CD196;
    const uint NidModuleInfo = 0x6C2224BA, NidSyslib = 0x936C8A78, NidProcessParam = 0x70FBA1E7;

    readonly object _lock = new();
    readonly Dictionary<uint, uint> _funcExports = new();
    readonly Dictionary<(uint lib, uint nid), uint> _funcExportsByLib = new();
    readonly Dictionary<uint, uint> _varExports = new();
    readonly List<(uint nid, uint libNid, string libName, uint stub)> _funcBindings = new();
    readonly List<(uint nid, byte[] relocs, Dictionary<int, SegmentInfo> segs)> _varBindings = new();
    readonly Dictionary<uint, string> _varSources = new(); // variable import NID -> "library (importing module)", for the log

    public uint ProcessParam { get; private set; }
    public Action<uint, uint>? InvalidateCode { get; set; }
    public Action<string>? Log { get; set; }

    public bool TryGetFunctionExport(uint nid, out uint addr)
    {
        lock (_lock) return _funcExports.TryGetValue(nid, out addr);
    }

    public static void WriteHleStub(uint stub, uint svcIndex, uint nid)
    {
        GuestMemory.W32(stub, 0xEF000000 | (svcIndex & 0xFFFFFF)); // svc #index
        GuestMemory.W32(stub + 4, 0xE12FFF1E);                     // bx lr
        GuestMemory.W32(stub + 8, nid);
    }

    public static void WriteJumpStub(uint stub, uint target)
    {
        GuestMemory.W32(stub, 0xE300C000 | ((target & 0xF000) << 4) | (target & 0xFFF));                 // movw r12, #lo
        GuestMemory.W32(stub + 4, 0xE340C000 | (((target >> 16) & 0xF000) << 4) | ((target >> 16) & 0xFFF)); // movt r12, #hi
        GuestMemory.W32(stub + 8, 0xE12FFF1C);                                                             // bx r12
    }

    internal void BindFunctionImport(uint nid, uint libNid, string libName, uint stub, Dictionary<int, SegmentInfo> segs)
    {
        lock (_lock)
        {
            uint target;
            if (_funcExportsByLib.TryGetValue((libNid, nid), out target) || _funcExports.TryGetValue(nid, out target))
                WriteJumpStub(stub, target);
            else
                WriteHleStub(stub, hle.GetHleFunctionIndex(nid, libNid, libName), nid);
            _funcBindings.Add((nid, libNid, libName, stub));
        }
        // reftable: relocations applied against the stub address
        uint reftable = GuestMemory.R32(stub + 12);
        if (reftable != 0)
        {
            uint size = (GuestMemory.R32(reftable) >> 4) & 0xFFFFFF;
            if (size > 4) Relocation.Relocate(GuestMemory.Span(reftable + 4, (int)(size - 4)), segs, true, stub);
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
                addr = hle.GetHleVariable(nid);
            }
            _varBindings.Add((nid, relocs, segs));
            _varSources.TryAdd(nid, source);
        }
        if (relocs.Length > 0 && addr != 0) Relocation.Relocate(relocs, segs, true, addr);
    }

    /// <summary>The library a variable import NID is imported from and the module that imports it.</summary>
    public string VariableImportSource(uint nid)
    {
        lock (_lock) return _varSources.GetValueOrDefault(nid, "");
    }

    /// <summary>NIDs of variable imports that neither a loaded module nor HLE provides (their relocations were never
    /// applied, so the importing code reads whatever the linker left in its literal pools).</summary>
    public List<uint> UnresolvedVariables()
    {
        lock (_lock)
            return _varBindings.Select(b => b.nid).Distinct()
                .Where(n => !_varExports.ContainsKey(n) && hle.GetHleVariable(n) == 0).ToList();
    }

    internal void RegisterExports(LoadedModule m, uint libNid, uint[] nids, uint[] entries, int funcCount)
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
                        case NidModuleStart: m.StartEntry = addr; continue;
                        case NidModuleStop: m.StopEntry = addr; continue;
                        case NidModuleExit: m.ExitEntry = addr; continue;
                    }
                    _funcExports.TryAdd(nid, addr);
                    _funcExportsByLib[(libNid, nid)] = addr;
                    foreach (var (bnid, blib, _, stub) in _funcBindings)
                    {
                        if (bnid != nid || blib != libNid) continue;
                        WriteJumpStub(stub, addr);
                        InvalidateCode?.Invoke(stub, 12);
                    }
                }
                else
                {
                    if (nid == NidProcessParam) { if (ProcessParam == 0) ProcessParam = addr; continue; }
                    if (nid is NidModuleInfo or NidSyslib) continue;
                    _varExports[nid] = addr;
                    foreach (var (bnid, relocs, segs) in _varBindings)
                        if (bnid == nid && relocs.Length > 0) Relocation.Relocate(relocs, segs, true, addr);
                }
            }
        }
    }

    static bool InModule(LoadedModule m, uint addr)
    {
        addr &= ~1u; // Thumb entry points
        foreach (var s in m.Segments.Values)
            if (addr >= s.Address && addr - s.Address < s.Size) return true;
        return false;
    }

    /// <summary>
    /// Undoes what loading <paramref name="m"/> did to the symbol table (sceKernelUnloadModule): drops its exports and
    /// its own import bindings, then rebinds every other module's import that pointed into it to the next provider
    /// (another loaded module exporting the NID) or back to the HLE stub. Variable imports are relocated to an HLE
    /// variable when one exists. Returns the number of function stubs rebound.
    /// </summary>
    public int UnregisterModule(LoadedModule m)
    {
        int rebound = 0;
        lock (_lock)
        {
            // the module's own import stubs/relocations live in memory that is about to be freed
            _funcBindings.RemoveAll(b => InModule(m, b.stub));
            _varBindings.RemoveAll(b => ReferenceEquals(b.segs, m.Segments));

            foreach (var key in _funcExportsByLib.Where(kv => InModule(m, kv.Value)).Select(kv => kv.Key).ToArray())
                _funcExportsByLib.Remove(key);
            foreach (var nid in _funcExports.Where(kv => InModule(m, kv.Value)).Select(kv => kv.Key).ToArray())
            {
                _funcExports.Remove(nid);
                foreach (var kv in _funcExportsByLib)
                    if (kv.Key.nid == nid) { _funcExports[nid] = kv.Value; break; }
            }

            foreach (var (nid, libNid, libName, stub) in _funcBindings)
            {
                // only stubs that jumped into the unloaded module change
                uint w0 = GuestMemory.R32(stub), w1 = GuestMemory.R32(stub + 4);
                if ((w0 & 0xFFF0F000) != 0xE300C000 || (w1 & 0xFFF0F000) != 0xE340C000) continue;
                uint target = ((w0 >> 4) & 0xF000) | (w0 & 0xFFF) | ((((w1 >> 4) & 0xF000) | (w1 & 0xFFF)) << 16);
                if (!InModule(m, target)) continue;
                if (_funcExportsByLib.TryGetValue((libNid, nid), out uint next) || _funcExports.TryGetValue(nid, out next))
                    WriteJumpStub(stub, next);
                else
                    WriteHleStub(stub, hle.GetHleFunctionIndex(nid, libNid, libName), nid);
                InvalidateCode?.Invoke(stub, 12);
                rebound++;
            }

            var removedVars = _varExports.Where(kv => InModule(m, kv.Value)).Select(kv => kv.Key).ToHashSet();
            foreach (var nid in removedVars) _varExports.Remove(nid);
            foreach (var (bnid, relocs, segs) in _varBindings)
            {
                if (!removedVars.Contains(bnid) || relocs.Length == 0) continue;
                uint addr = hle.GetHleVariable(bnid);
                if (addr != 0) Relocation.Relocate(relocs, segs, true, addr);
                else Log?.Invoke($"Variable import NID {bnid:X8} pointed into unloaded module {m.Name}");
            }
            if (InModule(m, ProcessParam)) ProcessParam = 0;
        }
        return rebound;
    }
}

public static class ModuleLoader
{
    const uint PT_LOAD = 1, PT_SCE_RELA = 0x60000000;
    const ushort ET_SCE_EXEC = 0xFE00, ET_SCE_RELEXEC = 0xFE04;

    public static LoadedModule Load(byte[] elf, string path, GuestMemory mem, Linker linker, int uid)
    {
        ReadOnlySpan<byte> e = elf;
        if (U32LE(e, 0) != 0x464C457F) throw new InvalidDataException($"{path}: not an ELF");
        ushort type = U16LE(e, 16);
        if (type != ET_SCE_EXEC && type != ET_SCE_RELEXEC) throw new NotSupportedException($"{path}: ELF type 0x{type:X}");
        uint entry = U32LE(e, 24), phoff = U32LE(e, 28);
        ushort phnum = U16LE(e, 44);
        bool relocatable = type == ET_SCE_RELEXEC;

        var m = new LoadedModule { Uid = uid, Path = path };
        var relas = new List<byte[]>();
        for (int i = 0; i < phnum; i++)
        {
            var ph = e.Slice((int)phoff + i * 32, 32);
            uint ptype = U32LE(ph, 0), off = U32LE(ph, 4), vaddr = U32LE(ph, 8), filesz = U32LE(ph, 16), memsz = U32LE(ph, 20), flags = U32LE(ph, 24);
            if (ptype == PT_LOAD && memsz != 0)
            {
                string name = $"{System.IO.Path.GetFileName(path)}:seg{i}";
                // a fixed segment only needs its range free in this process's address space (another process may use
                // the same addresses in its own); a relocatable module takes its preferred address only when no process
                // uses it, else any free range
                uint addr = vaddr != 0 ? mem.TryAllocAt(vaddr, memsz, name, exclusive: relocatable) : 0;
                if (addr == 0)
                {
                    if (!relocatable) throw new InvalidOperationException($"{path}: cannot map fixed segment at 0x{vaddr:X8}");
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
        foreach (var r in relas) Relocation.Relocate(r, m.Segments);

        // module_info
        int infoSeg = (int)(entry >> 30);
        uint infoBase = m.Segments[infoSeg].Address;
        uint mi = infoBase + (entry & 0x3FFFFFFF);
        m.ModuleInfoAddr = mi;
        var info = GuestMemory.Span(mi, 0x5C);
        m.Attributes = U16LE(info, 0);
        m.Version = U16LE(info, 2);
        m.Name = CString(info.Slice(4, 27));
        uint exportTop = U32LE(info, 0x24), exportEnd = U32LE(info, 0x28);
        uint importTop = U32LE(info, 0x2C), importEnd = U32LE(info, 0x30);
        m.ModuleNid = U32LE(info, 0x34);
        m.ExportsStart = infoBase + exportTop; m.ExportsEnd = infoBase + exportEnd;
        m.ImportsStart = infoBase + importTop; m.ImportsEnd = infoBase + importEnd;
        uint tlsStart = U32LE(info, 0x38);
        m.TlsInitSize = U32LE(info, 0x3C);
        m.TlsAreaSize = U32LE(info, 0x40);
        uint modStart = U32LE(info, 0x44), modStop = U32LE(info, 0x48);
        m.TlsInit = tlsStart != 0 ? infoBase + tlsStart : 0;
        if (modStart != 0 && modStart != 0xFFFFFFFF) m.StartEntry = infoBase + modStart;
        if (modStop != 0 && modStop != 0xFFFFFFFF) m.StopEntry = infoBase + modStop;
        uint exidxTop = U32LE(info, 0x4C), exidxEnd = U32LE(info, 0x50);
        if (exidxTop != 0) { m.ExidxTop = infoBase + exidxTop; m.ExidxEnd = infoBase + exidxEnd; }
        uint extabTop = U32LE(info, 0x54), extabEnd = U32LE(info, 0x58);
        if (extabTop != 0) { m.ExtabTop = infoBase + extabTop; m.ExtabEnd = infoBase + extabEnd; }

        // exports first (so a module can resolve against itself), then imports
        for (uint p = infoBase + exportTop; p < infoBase + exportEnd;)
        {
            var ex = GuestMemory.Span(p, 0x20);
            ushort size = U16LE(ex, 0);
            ushort nfunc = U16LE(ex, 6);
            uint nvar = U32LE(ex, 8);
            uint libNid = U32LE(ex, 0x10), libName = U32LE(ex, 0x14), nidTable = U32LE(ex, 0x18), entryTable = U32LE(ex, 0x1C);
            int total = nfunc + (int)nvar;
            var nids = GuestMemory.Span<uint>(nidTable, total).ToArray();
            var entries = GuestMemory.Span<uint>(entryTable, total).ToArray();
            m.Exports.Add((GuestMemory.ReadCString(libName), libNid, nids[..nfunc], nids[nfunc..]));
            linker.RegisterExports(m, libNid, nids, entries, nfunc);
            if (size == 0) break;
            p += size;
        }

        for (uint p = infoBase + importTop; p < infoBase + importEnd;)
        {
            var im = GuestMemory.Span(p, 0x34);
            ushort size = U16LE(im, 0);
            ushort nfunc = U16LE(im, 6), nvar = U16LE(im, 8);
            uint libNid, libName, funcNids, funcEntries, varNids, varEntries;
            if (size == 0x24)
            {
                libNid = U32LE(im, 0x0C); libName = U32LE(im, 0x10);
                funcNids = U32LE(im, 0x14); funcEntries = U32LE(im, 0x18);
                varNids = U32LE(im, 0x1C); varEntries = U32LE(im, 0x20);
            }
            else
            {
                libNid = U32LE(im, 0x10); libName = U32LE(im, 0x14);
                funcNids = U32LE(im, 0x1C); funcEntries = U32LE(im, 0x20);
                varNids = U32LE(im, 0x24); varEntries = U32LE(im, 0x28);
            }
            string lib = GuestMemory.ReadCString(libName);
            var fn = nfunc > 0 ? GuestMemory.Span<uint>(funcNids, nfunc).ToArray() : [];
            var fe = nfunc > 0 ? GuestMemory.Span<uint>(funcEntries, nfunc).ToArray() : [];
            for (int i = 0; i < nfunc; i++) linker.BindFunctionImport(fn[i], libNid, lib, fe[i], m.Segments);
            if (nvar > 0)
            {
                var vn = GuestMemory.Span<uint>(varNids, nvar).ToArray();
                var ve = GuestMemory.Span<uint>(varEntries, nvar).ToArray();
                for (int i = 0; i < nvar; i++) linker.BindVariableImport(vn[i], ve[i], m.Segments, $"{lib} (imported by {m.Name})");
            }
            m.Imports.Add((lib, libNid, fn));
            if (size == 0) break;
            p += size;
        }
        return m;
    }
}
