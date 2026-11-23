using System.Text;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The firmware's user modules the HLE serves, as a process's module list shows them to the plugin framework (emulator
/// option "Enable Kernel Plugins"). On the Vita a process has the firmware's user modules it uses in its module list
/// (the os0:us modules every process has preloaded, the vs0 modules it loads), with their export tables, and a user
/// plugin hooks their exports by module name (taiHookFunctionExport(&amp;ref, "SceLibKernel", ...)). VitaSharp runs those
/// libraries in HLE, so here each such module is a shell: its name, module NID and the export tables of the firmware
/// module (libraries, attributes, function NIDs), in the process's memory, each exported function's entry an HLE stub
/// (<c>svc #n; bx lr</c> into the same HLE function the program's imports reach). A hook on an entry patches that code
/// and takes the process's import stubs of the function, as every caller of the Vita's module goes through the hooked
/// code. The shells are made, at stable addresses, the first time the framework looks at the process's modules, for
/// the firmware modules exporting a library the process imports that no loaded module of the process exports; they are
/// older than every loaded module (they were preloaded), so "the newest module" is never one. They are not linked:
/// imports keep binding to the HLE as before.
/// </summary>
public sealed partial class PluginFramework
{
    /// <summary>A shell module of a process (<see cref="PluginFramework"/> of that process): where its tables are.</summary>
    internal sealed class FirmwareUserShell : KernelObject
    {
        public int Pid;
        public string Path = "";
        public uint ModuleNid;
        public ushort Attributes;
        public uint Base;
        public uint Size;
        public uint ExportsStart;
        public uint ExportsEnd;

        private LoadedModule? _module;

        /// <summary>The module as the framework's searches see it (segment 0: the tables).</summary>
        public LoadedModule Module => _module ??= new LoadedModule
        {
            Uid = Uid,
            Name = Name,
            Path = Path,
            ModuleNid = ModuleNid,
            Attributes = Attributes,
            ExportsStart = ExportsStart,
            ExportsEnd = ExportsEnd,
            ImportsStart = ExportsEnd,
            ImportsEnd = ExportsEnd,
            Segments = { [0] = new SegmentInfo(Base, 0, Size) },
            Started = true,
        };

        /// <summary>Save states: the record of the kernel object table (<see cref="KernelSnapshot"/>).</summary>
        internal void Save(SnapshotWriter writer)
        {
            writer.Write(Pid);
            writer.Write(Path);
            writer.Write(ModuleNid);
            writer.Write(Attributes);
            writer.Write(Base);
            writer.Write(Size);
            writer.Write(ExportsStart);
            writer.Write(ExportsEnd);
        }

        internal static FirmwareUserShell Load(SnapshotReader reader, string name)
        {
            return new FirmwareUserShell
            {
                Name = name,
                Pid = reader.ReadInt32(),
                Path = reader.ReadStringNotNull(),
                ModuleNid = reader.ReadUInt32(),
                Attributes = reader.ReadUInt16(),
                Base = reader.ReadUInt32(),
                Size = reader.ReadUInt32(),
                ExportsStart = reader.ReadUInt32(),
                ExportsEnd = reader.ReadUInt32(),
            };
        }
    }

    /// <summary>This process's shells, in the order they were made.</summary>
    [SnapshotMissing] // states of builds before it: no shell was made (they are made again when the framework looks)
    readonly List<FirmwareUserShell> _shells = new();

    /// <summary>The framework of <paramref name="process"/> (the one that keeps its shells).</summary>
    static PluginFramework? FrameworkOf(Emulator process) => process.Hle.Modules.OfType<PluginFramework>().FirstOrDefault();

    /// <summary>The shell modules of process <paramref name="pid"/>, made first for what it imports now; none for the
    /// kernel or an unknown process.</summary>
    List<LoadedModule> ShellsOf(int pid)
    {
        if (pid == KernelSpace.KernelPid || KernelSpace.ProcessOf(Emu, pid) is not { } process || FrameworkOf(process) is not { } owner)
        {
            return [];
        }

        return owner.EnsureShells(process);
    }

    /// <summary>Makes the shells this process's imports call for and returns all of them (oldest first).</summary>
    List<LoadedModule> EnsureShells(Emulator process)
    {
        lock (_patchLock)
        {
            List<LoadedModule> loaded = process.Kernel.Modules.Values.Where(m => !m.IsKernel).OrderBy(m => m.Uid).ToList();
            FirmwareUserImage firmware = FirmwareUserImage.For(process.Options.FsRoot);
            foreach (LoadedModule module in loaded)
            {
                foreach ((string library, uint libNid, uint[] funcNids) in module.Imports)
                {
                    if (funcNids.Length == 0 || _shells.Any(s => ShellExports(s, libNid)) || process.Linker.TryGetFunctionExport(libNid, funcNids[0], out _))
                    {
                        continue;
                    }

                    if (firmware.Exporting(library, libNid) is not { } source || loaded.Any(m => m.Name == source.Image.Name)
                        || _shells.Any(s => s.Name == source.Image.Name))
                    {
                        continue;
                    }

                    MakeShell(process, source);
                }
            }

            return _shells.Select(s => s.Module).ToList();
        }
    }

    /// <summary>Whether shell <paramref name="shell"/>'s export tables have library <paramref name="libNid"/>.</summary>
    static bool ShellExports(FirmwareUserShell shell, uint libNid)
    {
        for (uint entry = shell.ExportsStart; entry < shell.ExportsEnd; entry += GuestMemory.Read<ushort>(entry))
        {
            if (GuestMemory.R32(entry + 0x10) == libNid)
            {
                return true;
            }

            if (GuestMemory.Read<ushort>(entry) == 0)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the shell of firmware module <paramref name="source"/> into <paramref name="process"/>'s memory: the
    /// export entries (0x20 bytes: size, version, attributes, function count, library NID, name, NID table, entry
    /// table), the tables and names, and a 16-byte HLE stub per function, then lists it.
    /// </summary>
    void MakeShell(Emulator process, FirmwareUserImage.Module source)
    {
        const int ExportSize = 0x20, StubSize = 0x10;
        List<(string Library, uint LibNid, ushort Attributes, uint[] FuncNids, uint[] VarNids)> libraries =
            source.Image.Exports.Where(e => e.Library.Length > 0 && e.FuncNids.Length > 0).ToList();
        int functions = libraries.Sum(l => l.FuncNids.Length);
        int tablesAt = libraries.Count * ExportSize;
        int stringsAt = tablesAt + functions * 8;
        int stubsAt = (stringsAt + libraries.Sum(l => Encoding.ASCII.GetByteCount(l.Library) + 1) + 0xF) & ~0xF;
        uint size = (uint)((stubsAt + functions * StubSize + 0xFFF) & ~0xFFF);
        string name = source.Image.Name;
        using ProcessScope.Scope scope = ProcessScope.Enter(process);
        uint shellBase = process.Memory.Alloc(size, $"firmware module shell {name}", MemoryRegion.User, 16);
        GuestMemory.Fill(shellBase, 0, size);
        uint table = shellBase + (uint)tablesAt;
        uint strings = shellBase + (uint)stringsAt;
        uint stub = shellBase + (uint)stubsAt;
        for (int i = 0; i < libraries.Count; i++)
        {
            (string library, uint libNid, ushort attributes, uint[] nids, _) = libraries[i];
            uint export = shellBase + (uint)(i * ExportSize);
            uint nidTable = table;
            uint entryTable = table + (uint)nids.Length * 4;
            table += (uint)nids.Length * 8;
            GuestMemory.Write(export, (ushort)ExportSize);
            GuestMemory.Write(export + 2, (ushort)1);
            GuestMemory.Write(export + 4, attributes);
            GuestMemory.Write(export + 6, (ushort)nids.Length);
            GuestMemory.W32(export + 0x10, libNid);
            GuestMemory.WriteCString(strings, library);
            GuestMemory.W32(export + 0x14, strings);
            strings += (uint)Encoding.ASCII.GetByteCount(library) + 1;
            GuestMemory.W32(export + 0x18, nidTable);
            GuestMemory.W32(export + 0x1C, entryTable);
            for (int k = 0; k < nids.Length; k++)
            {
                GuestMemory.W32(nidTable + (uint)k * 4, nids[k]);
                GuestMemory.W32(entryTable + (uint)k * 4, stub);
                Linker.WriteHleStub(stub, process.Hle.GetHleFunctionIndex(nids[k], libNid, library), nids[k]);
                stub += StubSize;
            }
        }

        process.InvalidateCode(shellBase, size);
        var shell = new FirmwareUserShell
        {
            Name = name,
            Pid = process.Pid,
            Path = source.VitaPath,
            ModuleNid = source.Image.ModuleNid,
            Attributes = source.Image.Attributes,
            Base = shellBase,
            Size = size,
            ExportsStart = shellBase,
            ExportsEnd = shellBase + (uint)(libraries.Count * ExportSize),
        };
        K.Objects.Register(shell);
        _shells.Add(shell);
        Util.Log.Info($"plugin framework: {name} ({source.VitaPath}) listed in process 0x{process.Pid:X} at 0x{shellBase:X8} ({libraries.Count} libraries, {functions} functions served by the HLE)");
    }

    /// <summary>The process ended: its shells go with its memory.</summary>
    void ForgetShells()
    {
        lock (_patchLock)
        {
            foreach (FirmwareUserShell shell in _shells)
            {
                K.Objects.Remove(shell.Uid);
            }

            _shells.Clear();
        }
    }

    /// <summary>The shells this framework keeps (tests).</summary>
    internal IReadOnlyList<LoadedModule> Shells
    {
        get
        {
            lock (_patchLock)
            {
                return _shells.Select(s => s.Module).ToList();
            }
        }
    }
}
