using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;

namespace VitaSharp.Core.Hle.Modules;

// ksceKernelRegisterLibary / ksceKernelReleaseLibary (SceModulemgrForDriver, modulemgr 3.74, read in the module, study
// only): a kernel module adds to or takes out of the library database one of its own export entries, named by the
// entry's address. The module is the kernel module whose segments hold the address (none: 0x8002D082); the address must
// be one of the 0x20-byte entries of its export table (else 0x8002D009). At load the module manager registers only the
// entries marked AUTO_EXPORT (ModuleLoader); these calls handle the others (and any entry later on).
public sealed partial class ModulemgrKernel
{
    /// <summary>The address is not an export entry of the module (0x8002D009).</summary>
    const int LibraryNotAnEntry = unchecked((int)0x8002D009);

    /// <summary>The library is already in the database (0x8002D013).</summary>
    const int LibraryAlreadyRegistered = unchecked((int)0x8002D013);

    /// <summary>The library is not in the database (0x8002D081).</summary>
    const int LibraryNotRegistered = unchecked((int)0x8002D081);

    /// <summary>A module imports the library without the weak attribute: it cannot go (0x8002D002).</summary>
    const int LibraryInUse = unchecked((int)0x8002D002);

    /// <summary>The size of one export entry, the stride the module manager indexes the export table by.</summary>
    const uint ExportEntrySize = 0x20;

    /// <summary>The kernel module holding <paramref name="address"/> and the export entry the address names; the
    /// error when there is none.</summary>
    int FindExportEntry(uint address, out LoadedModule? owner, out ExportLibrary? library)
    {
        owner = null;
        library = null;
        if (ModulesOf(KernelSpace.KernelPid) is { } modules)
        {
            foreach (LoadedModule module in modules)
            {
                foreach (SegmentInfo segment in module.Segments.Values)
                {
                    if (address >= segment.Address && address - segment.Address < segment.Size)
                    {
                        owner = module;
                        break;
                    }
                }

                if (owner != null)
                {
                    break;
                }
            }
        }

        if (owner == null)
        {
            return SceError.LIBRARYDB_NO_MOD;
        }

        if (address < owner.ExportsStart || address >= owner.ExportsEnd || (address - owner.ExportsStart) % ExportEntrySize != 0)
        {
            return LibraryNotAnEntry;
        }

        foreach (ExportLibrary entry in owner.ExportLibraries)
        {
            if (entry.EntryAddress == address)
            {
                library = entry;
                return 0;
            }
        }

        return LibraryNotAnEntry;
    }

    static Linker KernelLinkerOf(Emulator emu)
    {
        return (emu.Vita.KernelProcess ?? emu).Linker;
    }

    /// <summary>ksceKernelRegisterLibary(const void *entry) (SceModulemgrForDriver 0x861638AD): puts the library of
    /// that export entry in the database and binds the imports waiting for it; already there: 0x8002D013; the main
    /// entry (0x8000) is no library and answers 0.</summary>
    [Hle(Nid = 0x861638AD, Library = "SceModulemgrForDriver")]
    int ksceKernelRegisterLibary(uint entry)
    {
        int found = FindExportEntry(entry, out LoadedModule? owner, out ExportLibrary? library);
        if (found < 0)
        {
            return found;
        }

        lock (owner!)
        {
            if (library!.Registered)
            {
                return LibraryAlreadyRegistered;
            }

            if ((library.Attributes & ExportLibrary.MainExportAttr) != 0)
            {
                return 0;
            }

            library.Registered = true;
        }

        KernelLinkerOf(Emu).RegisterExports(owner, library.LibNid, library.Nids, library.Entries, library.FuncCount, library.Scope);
        return 0;
    }

    /// <summary>ksceKernelReleaseLibary(const void *entry) (SceModulemgrForDriver 0x0975B104): takes the library of
    /// that export entry out of the database; its importers bind again to another provider or to the system's own
    /// (HLE). Not in the database: 0 for the main entry, else 0x8002D081; a module importing it without the weak
    /// attribute: 0x8002D002.</summary>
    [Hle(Nid = 0x0975B104, Library = "SceModulemgrForDriver")]
    int ksceKernelReleaseLibary(uint entry)
    {
        int found = FindExportEntry(entry, out LoadedModule? owner, out ExportLibrary? library);
        if (found < 0)
        {
            return found;
        }

        lock (owner!)
        {
            if (!library!.Registered)
            {
                return (library.Attributes & ExportLibrary.MainExportAttr) != 0 ? 0 : LibraryNotRegistered;
            }

            if (HasStrongImporter(owner, library))
            {
                return LibraryInUse;
            }

            library.Registered = false;
        }

        KernelLinkerOf(Emu).UnregisterLibrary(library.LibNid, library.Nids, library.Entries, library.FuncCount);
        return 0;
    }

    /// <summary>True when a module other than the provider imports the library without the weak attribute: a kernel
    /// module for a kernel library, any module for a library user code reaches through syscalls.</summary>
    bool HasStrongImporter(LoadedModule provider, ExportLibrary library)
    {
        foreach (Emulator process in Emu.Vita.ProcessesAndKernel)
        {
            foreach (LoadedModule module in process.Kernel.Modules.Values)
            {
                if (ReferenceEquals(module, provider) || (!module.IsKernel && library.Scope != ExportScope.Syscall))
                {
                    continue;
                }

                if (module.ImportAttributes.TryGetValue(library.LibNid, out ushort attributes) && (attributes & ExportLibrary.WeakImportAttr) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
