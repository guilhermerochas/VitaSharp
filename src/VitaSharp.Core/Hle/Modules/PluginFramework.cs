using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The plugin framework libraries the emulator offers to plugins when the emulator option "Enable Kernel Plugins" is on
/// (<see cref="Emulator.KernelPluginsEnabled"/>): module information by name or NID, a module's segment offsets, its
/// export and import entries, and data injections into a module (the original bytes kept and written back on release).
/// The Vita has no such libraries: they are the homebrew environment's, and with the option off (the pure firmware) each
/// call does nothing, as an import nothing provides. The functions follow the scene framework's published behaviour
/// (its error codes 0x9001xxxx, the 0x38-byte module information, the newest-module-first search).
/// </summary>
public sealed partial class PluginFramework : HleModule
{
    // the framework's error codes
    const int Success = 0;
    const int ErrorSystem = unchecked((int)0x90010000);
    const int ErrorNotFound = unchecked((int)0x90010002);
    const int ErrorInvalidArgs = unchecked((int)0x90010003);
    const int ErrorPatchExists = unchecked((int)0x90010005);
    const int ErrorUserMemory = unchecked((int)0x90010008);

    /// <summary>Any module NID / any library NID.</summary>
    const uint IgnoreNid = 0xFFFFFFFF;

    /// <summary>The module information record: size, modid, module NID, name[27], exports start/end, imports
    /// start/end.</summary>
    const uint ModuleInfoSize = 0x38;

    /// <summary>The longest name a user call copies in (MAX_NAME_LEN).</summary>
    const int MaxNameLength = 256;

    /// <summary>The bytes of a module name the search compares.</summary>
    const int ModuleNameLength = 27;

    /// <summary>One data injection: the process, where it wrote and what was there before.</summary>
    internal sealed class Injection : KernelObject
    {
        public int Pid;
        public uint Address;
        public byte[] Saved = [];

        /// <summary>Save states: the record of the kernel object table (<see cref="SaveState.KernelSnapshot"/>).</summary>
        internal void Save(SaveState.SnapshotWriter writer)
        {
            writer.Write(Pid);
            writer.Write(Address);
            writer.Write(Saved);
        }

        internal static Injection Load(SaveState.SnapshotReader reader, string name)
        {
            return new Injection
            {
                Name = name,
                Pid = reader.ReadInt32(),
                Address = reader.ReadUInt32(),
                Saved = reader.ReadBytes() ?? [],
            };
        }
    }

    readonly List<Injection> _injections = new();

    /// <summary>Guards the injections and the hooks (one patch map per process, as the framework keeps).</summary>
    readonly object _patchLock = new();

    bool Enabled => Emu.KernelPluginsEnabled;

    /// <summary>The NIDs of the functions this module offers (read once from its [Hle] declarations).</summary>
    static readonly HashSet<uint> s_nids = typeof(PluginFramework)
        .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .SelectMany(m => m.GetCustomAttributes(typeof(HleAttribute), false).Cast<HleAttribute>())
        .Select(a => a.Nid)
        .ToHashSet();

    /// <summary>True when <paramref name="nid"/> is one of the framework functions the emulator offers.</summary>
    internal bool Provides(uint nid) => s_nids.Contains(nid);

    // ------------------------------------------------------------------ modules

    /// <summary>The modules of <paramref name="pid"/> newest first (the framework walks the firmware's module list from
    /// its end), or null for an unknown process.</summary>
    List<LoadedModule>? ModulesNewestFirst(int pid)
    {
        List<LoadedModule>? modules = KernelSpace.ModulesOf(Emu, pid);
        if (modules == null)
        {
            return null;
        }

        modules.Reverse();
        return modules;
    }

    /// <summary>Finds a module by name (first 27 bytes) and/or module NID: no name and any NID is the newest module.</summary>
    int FindModule(int pid, string? name, uint nid, out LoadedModule? found)
    {
        found = null;
        if (ModulesNewestFirst(pid) is not { } modules)
        {
            return SceError.INVALID_PID;
        }

        foreach (LoadedModule module in modules)
        {
            bool match;
            if (name != null)
            {
                match = string.CompareOrdinal(Truncate(module.Name), Truncate(name)) == 0 && (nid == IgnoreNid || module.ModuleNid == nid);
            }
            else
            {
                match = nid == IgnoreNid || module.ModuleNid == nid;
            }

            if (match)
            {
                found = module;
                return Success;
            }
        }

        return ErrorNotFound;
    }

    static string Truncate(string name) => name.Length > ModuleNameLength ? name[..ModuleNameLength] : name;

    /// <summary>Writes the module information record at <paramref name="info"/> (its size word is the caller's).</summary>
    static void WriteModuleInfo(uint info, LoadedModule module)
    {
        GuestMemory.W32(info + 4, (uint)module.Uid);
        GuestMemory.W32(info + 8, module.ModuleNid);
        GuestMemory.Fill(info + 0xC, 0, ModuleNameLength);
        GuestMemory.WriteCString(info + 0xC, module.Name, ModuleNameLength);
        GuestMemory.W32(info + 0x28, module.ExportsStart);
        GuestMemory.W32(info + 0x2C, module.ExportsEnd);
        GuestMemory.W32(info + 0x30, module.ImportsStart);
        GuestMemory.W32(info + 0x34, module.ImportsEnd);
    }

    int GetModuleInfoByNameNid(int pid, uint namePtr, uint nid, uint info)
    {
        if (GuestMemory.R32(info) < ModuleInfoSize)
        {
            return ErrorSystem;
        }

        string? name = namePtr == 0 ? null : GuestMemory.ReadCString(namePtr, MaxNameLength);
        int result = FindModule(pid, name, nid, out LoadedModule? module);
        if (result == Success)
        {
            WriteModuleInfo(info, module!);
        }

        return result;
    }

    /// <summary>Kernel: module information by name and module NID in process <paramref name="pid"/>.</summary>
    [Hle(Nid = 0x87E73973, ReturnsValue = true)]
    int ModuleGetByNameNid(int pid, uint namePtr, uint nid, uint info)
    {
        if (!Enabled)
        {
            return 0;
        }

        return GetModuleInfoByNameNid(pid, namePtr, nid, info);
    }

    /// <summary>Kernel: module information by name in process <paramref name="pid"/> (no name: the newest module).</summary>
    [Hle(Nid = 0x6E931197, ReturnsValue = true)]
    int GetModuleInfoForKernel(int pid, uint namePtr, uint info)
    {
        if (!Enabled)
        {
            return 0;
        }

        return GetModuleInfoByNameNid(pid, namePtr, IgnoreNid, info);
    }

    /// <summary>User: module information by name in the calling process; the record's size must be exactly 0x38.</summary>
    [Hle(Nid = 0xA5899384, ReturnsValue = true)]
    int GetModuleInfoForUser(uint namePtr, uint info)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!KernelSpace.IsUserRange(info, 4) || GuestMemory.R32(info) != ModuleInfoSize)
        {
            return ErrorUserMemory;
        }

        if (namePtr != 0 && GuestMemory.ReadCString(namePtr, MaxNameLength + 1).Length >= MaxNameLength)
        {
            return ErrorUserMemory;
        }

        return GetModuleInfoByNameNid(Emu.Pid, namePtr, IgnoreNid, info);
    }

    /// <summary>The address of byte <paramref name="offset"/> of segment <paramref name="segment"/> (0-3) of a module;
    /// an offset up to the segment's size is accepted.</summary>
    int SegmentOffset(int pid, int moduleId, int segment, uint offset, out uint address)
    {
        address = 0;
        if (segment > 3 || segment < 0)
        {
            return ErrorInvalidArgs;
        }

        if (KernelSpace.ModulesOf(Emu, pid)?.FirstOrDefault(m => m.Uid == moduleId) is not { } module)
        {
            return SceError.INVALID_PID;
        }

        if (!module.Segments.TryGetValue(segment, out SegmentInfo info) || offset > info.Size)
        {
            return ErrorInvalidArgs;
        }

        address = info.Address + offset;
        return Success;
    }

    /// <summary>Kernel: the address of an offset in a module's segment.</summary>
    [Hle(Nid = 0x85FDDCB0, ReturnsValue = true)]
    int ModuleGetOffset(int pid, int moduleId, int segment, uint offset, uint addressOut)
    {
        if (!Enabled)
        {
            return 0;
        }

        int result = SegmentOffset(pid, moduleId, segment, offset, out uint address);
        if (result == Success)
        {
            GuestMemory.W32(addressOut, address);
        }

        return result;
    }

    /// <summary>The entry of export <paramref name="functionNid"/> of library <paramref name="libraryNid"/> (any:
    /// 0xFFFFFFFF) in a module's export table; <paramref name="exportLibNid"/> is the library it was found in.</summary>
    int ExportFunction(int pid, uint namePtr, uint libraryNid, uint functionNid, out uint function, out uint exportLibNid)
    {
        function = 0;
        exportLibNid = 0;
        string? name = namePtr == 0 ? null : GuestMemory.ReadCString(namePtr, MaxNameLength);
        using ProcessScope.Scope space = EnterPatchSpace(pid);
        if (FindModule(pid, name, IgnoreNid, out LoadedModule? module) != Success)
        {
            return ErrorNotFound;
        }

        uint entry = module!.ExportsStart;
        while (entry < module.ExportsEnd)
        {
            ushort size = GuestMemory.Read<ushort>(entry);
            if (size == 0)
            {
                break;
            }

            ushort functions = GuestMemory.Read<ushort>(entry + 6);
            if (libraryNid == IgnoreNid || GuestMemory.R32(entry + 0x10) == libraryNid)
            {
                uint nids = GuestMemory.R32(entry + 0x18);
                uint entries = GuestMemory.R32(entry + 0x1C);
                for (uint i = 0; i < functions; i++)
                {
                    if (GuestMemory.R32(nids + i * 4) == functionNid)
                    {
                        function = GuestMemory.R32(entries + i * 4);
                        exportLibNid = GuestMemory.R32(entry + 0x10);
                        return Success;
                    }
                }
            }

            entry += size;
        }

        return ErrorNotFound;
    }

    /// <summary>Kernel: a module's export by library and function NID.</summary>
    [Hle(Nid = 0xF28E9A36, ReturnsValue = true)]
    int ModuleGetExportFunc(int pid, uint namePtr, uint libraryNid, uint functionNid, uint functionOut)
    {
        if (!Enabled)
        {
            return 0;
        }

        int result = ExportFunction(pid, namePtr, libraryNid, functionNid, out uint function, out _);
        if (result == Success)
        {
            GuestMemory.W32(functionOut, function);
        }

        return result;
    }

    /// <summary>User: an export of a module of the calling process.</summary>
    [Hle(Nid = 0x6231AAD1, ReturnsValue = true)]
    int GetModuleExportFuncForUser(uint namePtr, uint libraryNid, uint functionNid, uint functionOut)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (namePtr == 0 || GuestMemory.ReadCString(namePtr, MaxNameLength + 1).Length >= MaxNameLength)
        {
            return ErrorUserMemory;
        }

        int result = ExportFunction(Emu.Pid, namePtr, libraryNid, functionNid, out uint function, out _);
        if (result == Success)
        {
            GuestMemory.W32(functionOut, function);
        }

        return result;
    }

    /// <summary>Kernel: the import stub of function <paramref name="functionNid"/> of library
    /// <paramref name="libraryNid"/> in a module's import table (both import entry layouts, 0x34 and 0x24 bytes).</summary>
    [Hle(Nid = 0xF2AA3654, ReturnsValue = true)]
    int ModuleGetImportFunc(int pid, uint namePtr, uint libraryNid, uint functionNid, uint stubOut)
    {
        if (!Enabled)
        {
            return 0;
        }

        return ModuleImportStub(pid, namePtr, libraryNid, functionNid, stubOut);
    }

    /// <summary>The import stub of a function in a module's import table, written to <paramref name="stubOut"/>.</summary>
    int ModuleImportStub(int pid, uint namePtr, uint libraryNid, uint functionNid, uint stubOut)
    {
        string? name = namePtr == 0 ? null : GuestMemory.ReadCString(namePtr, MaxNameLength);
        uint stub = 0;
        int result = ImportStubOf(pid, name, libraryNid, functionNid, ref stub);
        if (result == Success)
        {
            GuestMemory.W32(stubOut, stub);
        }

        return result;
    }

    /// <summary>The import stub of a function in a module's import table of process <paramref name="pid"/> (read in
    /// its address space).</summary>
    int ImportStubOf(int pid, string? name, uint libraryNid, uint functionNid, ref uint stub)
    {
        using ProcessScope.Scope space = EnterPatchSpace(pid);
        if (FindModule(pid, name, IgnoreNid, out LoadedModule? module) != Success)
        {
            return ErrorNotFound;
        }

        uint entry = module!.ImportsStart;
        while (entry < module.ImportsEnd)
        {
            ushort size = GuestMemory.Read<ushort>(entry);
            if (size == 0)
            {
                break;
            }

            ushort functions = GuestMemory.Read<ushort>(entry + 6);
            uint entryLibrary;
            uint nids;
            uint stubs;
            if (size == 0x34)
            {
                entryLibrary = GuestMemory.R32(entry + 0x10);
                nids = GuestMemory.R32(entry + 0x1C);
                stubs = GuestMemory.R32(entry + 0x20);
            }
            else if (size == 0x24)
            {
                entryLibrary = GuestMemory.R32(entry + 0x0C);
                nids = GuestMemory.R32(entry + 0x14);
                stubs = GuestMemory.R32(entry + 0x18);
            }
            else
            {
                entry += size;
                continue;
            }

            if (libraryNid == IgnoreNid || entryLibrary == libraryNid)
            {
                for (uint i = 0; i < functions; i++)
                {
                    if (GuestMemory.R32(nids + i * 4) == functionNid)
                    {
                        stub = GuestMemory.R32(stubs + i * 4);
                        return Success;
                    }
                }
            }

            entry += size;
        }

        return ErrorNotFound;
    }

    // ------------------------------------------------------------------ injections

    /// <summary>Writes <paramref name="size"/> bytes from <paramref name="source"/> over <paramref name="destination"/>
    /// in process <paramref name="pid"/>, ignoring page protection, keeping the original bytes; a range that overlaps
    /// another patch of the process is refused. Returns the injection's UID.</summary>
    int Inject(int pid, uint destination, uint source, uint size)
    {
        // the source is read in the caller's address space, the destination written in the process's
        if (size == 0 || !KernelSpace.IsMappedRange(source, size))
        {
            return ErrorInvalidArgs;
        }

        byte[] data = GuestMemory.ReadSpan(source, (int)size).ToArray();
        using ProcessScope.Scope space = EnterPatchSpace(pid);
        if (!KernelSpace.IsMappedRange(destination, size))
        {
            return ErrorInvalidArgs;
        }

        var injection = new Injection
        {
            Pid = pid,
            Address = destination,
            Saved = GuestMemory.ReadSpan(destination, (int)size).ToArray(),
            Name = "plugin inject",
        };
        lock (_patchLock)
        {
            foreach (Injection other in _injections)
            {
                if (other.Pid == pid && destination < other.Address + (uint)other.Saved.Length && other.Address < destination + size)
                {
                    return ErrorPatchExists;
                }
            }

            lock (_patchLock)
            {
                if (_hooks.Any(h => h.Pid == pid && destination < h.Address + (uint)h.Saved.Length && h.Address < destination + size))
                {
                    return ErrorPatchExists;
                }
            }

            data.CopyTo(GuestMemory.Span(destination, (int)size));
            int uid = K.Objects.Register(injection);
            _injections.Add(injection);
            InvalidateEverywhere(destination, size);
            return uid;
        }
    }

    /// <summary>Makes the address space of process <paramref name="pid"/> the one guest memory accesses use: the
    /// framework makes a patch of a process in that process's memory (it switches to the process's address space), and
    /// a patch of the kernel in the caller's (kernel memory is mapped in every process).</summary>
    ProcessScope.Scope EnterPatchSpace(int pid)
    {
        if (pid != KernelSpace.KernelPid && KernelSpace.ProcessOf(Emu, pid) is { } process)
        {
            return ProcessScope.Enter(process);
        }

        return ProcessScope.Enter(Emu);
    }

    void InvalidateEverywhere(uint address, uint size)
    {
        foreach (Emulator process in Emu.Vita.ProcessesAndKernel)
        {
            process.Code.Invalidate(address, size);
        }

        Emu.Code.Invalidate(address, size);
    }

    /// <summary>Writes the original bytes of injection <paramref name="uid"/> back.</summary>
    int ReleaseInjection(int uid)
    {
        lock (_patchLock)
        {
            if (K.Objects.Get<Injection>(uid) is not { } injection || !_injections.Remove(injection))
            {
                return ErrorSystem;
            }

            using ProcessScope.Scope space = EnterPatchSpace(injection.Pid);
            injection.Saved.CopyTo(GuestMemory.Span(injection.Address, injection.Saved.Length));
            K.Objects.Remove(uid);
            InvalidateEverywhere(injection.Address, (uint)injection.Saved.Length);
            return Success;
        }
    }

    /// <summary>Kernel: injects data at an absolute address of process <paramref name="pid"/>.</summary>
    [Hle(Nid = 0xFC2F926F, ReturnsValue = true)]
    int InjectAbsForKernel(int pid, uint destination, uint source, uint size)
    {
        if (!Enabled)
        {
            return 0;
        }

        return Inject(pid, destination, source, size);
    }

    /// <summary>Kernel: injects data at an offset of a module's segment.</summary>
    [Hle(Nid = 0x9FA50038, ReturnsValue = true)]
    int InjectDataForKernel(int pid, int moduleId, int segment, uint offset, uint source, uint size)
    {
        if (!Enabled)
        {
            return 0;
        }

        int result = SegmentOffset(pid, moduleId, segment, offset, out uint destination);
        if (result != Success)
        {
            return result;
        }

        return Inject(pid, destination, source, size);
    }

    /// <summary>Kernel: undoes an injection.</summary>
    [Hle(Nid = 0x51BB3EC9, ReturnsValue = true)]
    int InjectReleaseForKernel(int uid)
    {
        if (!Enabled)
        {
            return 0;
        }

        return ReleaseInjection(uid);
    }

    /// <summary>User: injects data at an absolute address of the calling process.</summary>
    [Hle(Nid = 0x4A588904, ReturnsValue = true)]
    int InjectAbsForUser(uint destination, uint source, uint size)
    {
        if (!Enabled)
        {
            return 0;
        }

        return Inject(Emu.Pid, destination, source, size);
    }

    /// <summary>User: injects data at a module offset (tai_offset_args: size, modid, segidx, offset, thumb, source,
    /// source_size).</summary>
    [Hle(Nid = 0xCE25B9BC, ReturnsValue = true)]
    int InjectDataForUser(uint args)
    {
        if (!Enabled)
        {
            return 0;
        }

        if (!KernelSpace.IsUserRange(args, 0x1C) || GuestMemory.R32(args) != 0x1C)
        {
            return ErrorUserMemory;
        }

        int result = SegmentOffset(Emu.Pid, (int)GuestMemory.R32(args + 4), (int)GuestMemory.R32(args + 8),
            GuestMemory.R32(args + 0xC), out uint destination);
        if (result != Success)
        {
            return result;
        }

        return Inject(Emu.Pid, destination, GuestMemory.R32(args + 0x14), GuestMemory.R32(args + 0x18));
    }

    /// <summary>User: undoes an injection.</summary>
    [Hle(Nid = 0x21A4C8A6, ReturnsValue = true)]
    int InjectReleaseForUser(int uid)
    {
        if (!Enabled)
        {
            return 0;
        }

        return ReleaseInjection(uid);
    }
}
