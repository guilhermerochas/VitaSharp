using VitaSharp.Core.Io;
using VitaSharp.Core.Loader;

namespace VitaSharp.Core.SaveState;

/// <summary>
/// What a save state needs to be loaded on a computer that does not have the game, its update or the user's plugins
/// (a "Gravar tudo" capture analysed by someone else, docs/notes/session-capture.md): the program's identity as the
/// loader set it (title, executable, its guest folder, param.sfo, the program's authority), and every module the
/// process and the kernel had loaded, in load order, as the loader placed it: its guest path, segments and module info
/// address. The modules' code and tables are already in the state's memory, so this is enough to make the same
/// modules exist again without reading any file (<see cref="ModuleLoader.FromMemory"/>): no executable, no module and
/// no plugin is copied into the state beyond the memory it already holds. Written as the state's "boot" section.
/// </summary>
public sealed class StateBootImage
{
    private const int Version = 1;

    public string TitleId = "", Title = "", Self = "eboot.bin";
    /// <summary>The application's folder as the system names it ("ux0:app/PCSE00333"); empty outside the data folder.</summary>
    public string AppGuestPath = "";
    public byte[] ParamSfo = [];
    public ulong AuthorityId;
    public bool IsSystemProgram, RunsOnSystemCore, RetailApplication;
    public uint ProcessParam, TlsInit, TlsInitSize, TlsAreaSize;
    public int MainModuleUid;
    public List<ModuleEntry> Modules = [];

    /// <summary>One loaded module.</summary>
    public sealed class ModuleEntry
    {
        public int Uid;
        public string Path = "";
        public bool Kernel, FirmwareShell, Started, SyscallsForUsers;
        public ulong ImageHash;
        public int InfoSegment;
        public uint ModuleInfoAddress;
        public SortedDictionary<int, SegmentInfo> Segments = new();
        public SortedDictionary<int, (uint Flags, uint MemSz, uint FileSz)> SegmentFlags = new();
    }

    /// <summary>The boot image of the system's first process (and the kernel's modules).</summary>
    public static StateBootImage Of(Emulator emu)
    {
        var image = new StateBootImage
        {
            TitleId = emu.TitleId,
            Title = emu.Title,
            Self = emu.CurrentSelf,
            AppGuestPath = SafeGuestPath(emu.Options.FsRoot, emu.AppPath) ?? "",
            AuthorityId = emu.ProgramAuthorityId,
            IsSystemProgram = emu.IsSystemProgram,
            RunsOnSystemCore = emu.RunsOnSystemCore,
            RetailApplication = !emu.Access.IsNonGameProgram,
            ProcessParam = emu.Kernel.ProcessParam,
            TlsInit = emu.Kernel.TlsInit,
            TlsInitSize = emu.Kernel.TlsInitSize,
            TlsAreaSize = emu.Kernel.TlsAreaSize,
            MainModuleUid = emu.MainModule?.Uid ?? 0,
        };
        // the param.sfo app0 shows (the update's when one is installed): metadata, not the program
        if (emu.Vfs.Resolve("app0:sce_sys/param.sfo") is { } sfo && File.Exists(sfo))
        {
            try
            {
                image.ParamSfo = File.ReadAllBytes(sfo);
            }
            catch (IOException)
            {
            }
        }

        var firmwareShells = new HashSet<LoadedModule>();
        Emulator? kernel = emu.Vita.KernelProcess;
        if (kernel != null)
        {
            foreach (LoadedModule shell in kernel.FirmwareKernel.Modules)
            {
                firmwareShells.Add(shell);
            }

            AddModules(image, kernel, firmwareShells);
        }

        AddModules(image, emu, firmwareShells);
        image.Modules.Sort((a, b) => a.Uid.CompareTo(b.Uid));
        return image;
    }

    private static void AddModules(StateBootImage image, Emulator process, HashSet<LoadedModule> firmwareShells)
    {
        foreach (LoadedModule module in process.Kernel.Modules.Values)
        {
            var entry = new ModuleEntry
            {
                Uid = module.Uid,
                Path = module.Path,
                Kernel = module.IsKernel,
                FirmwareShell = firmwareShells.Contains(module),
                Started = module.Started,
                SyscallsForUsers = module.SyscallsForUsers,
                ImageHash = module.ImageHash,
                InfoSegment = module.ModuleInfoSegment,
                ModuleInfoAddress = module.ModuleInfoAddr,
            };
            lock (module)
            {
                foreach (KeyValuePair<int, SegmentInfo> segment in module.Segments)
                {
                    entry.Segments[segment.Key] = segment.Value;
                }

                foreach (KeyValuePair<int, (uint Flags, uint MemSz, uint FileSz)> flags in module.SegmentFlags)
                {
                    entry.SegmentFlags[flags.Key] = flags.Value;
                }
            }

            image.Modules.Add(entry);
        }
    }

    private static string? SafeGuestPath(string fsRoot, string host)
    {
        try
        {
            return string.IsNullOrEmpty(host) ? null : FirmwareLayout.HostToGuest(fsRoot, host);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public byte[] Write()
    {
        var w = new SnapshotWriter();
        w.Write(Version);
        w.Write(TitleId);
        w.Write(Title);
        w.Write(Self);
        w.Write(AppGuestPath);
        w.Write(ParamSfo);
        w.Write(AuthorityId);
        w.Write(IsSystemProgram);
        w.Write(RunsOnSystemCore);
        w.Write(RetailApplication);
        w.Write(ProcessParam);
        w.Write(TlsInit);
        w.Write(TlsInitSize);
        w.Write(TlsAreaSize);
        w.Write(MainModuleUid);
        w.Write(Modules.Count);
        foreach (ModuleEntry m in Modules)
        {
            w.Write(m.Uid);
            w.Write(m.Path);
            w.Write(m.Kernel);
            w.Write(m.FirmwareShell);
            w.Write(m.Started);
            w.Write(m.SyscallsForUsers);
            w.Write(m.ImageHash);
            w.Write(m.InfoSegment);
            w.Write(m.ModuleInfoAddress);
            w.Write(m.Segments.Count);
            foreach (KeyValuePair<int, SegmentInfo> segment in m.Segments)
            {
                w.Write(segment.Key);
                w.Write(segment.Value.Address);
                w.Write(segment.Value.OriginalVaddr);
                w.Write(segment.Value.Size);
            }

            w.Write(m.SegmentFlags.Count);
            foreach (KeyValuePair<int, (uint Flags, uint MemSz, uint FileSz)> flags in m.SegmentFlags)
            {
                w.Write(flags.Key);
                w.Write(flags.Value.Flags);
                w.Write(flags.Value.MemSz);
                w.Write(flags.Value.FileSz);
            }
        }

        return w.Data.ToArray();
    }

    public static StateBootImage Read(byte[] data)
    {
        var r = new SnapshotReader(data);
        int version = r.ReadInt32();
        if (version != Version)
        {
            throw new SnapshotException($"the state's boot section has version {version}, this build reads {Version}");
        }

        var image = new StateBootImage
        {
            TitleId = r.ReadStringNotNull(),
            Title = r.ReadStringNotNull(),
            Self = r.ReadStringNotNull(),
            AppGuestPath = r.ReadStringNotNull(),
            ParamSfo = r.ReadBytes() ?? [],
            AuthorityId = r.ReadUInt64(),
            IsSystemProgram = r.ReadBool(),
            RunsOnSystemCore = r.ReadBool(),
            RetailApplication = r.ReadBool(),
            ProcessParam = r.ReadUInt32(),
            TlsInit = r.ReadUInt32(),
            TlsInitSize = r.ReadUInt32(),
            TlsAreaSize = r.ReadUInt32(),
            MainModuleUid = r.ReadInt32(),
        };
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var m = new ModuleEntry
            {
                Uid = r.ReadInt32(),
                Path = r.ReadStringNotNull(),
                Kernel = r.ReadBool(),
                FirmwareShell = r.ReadBool(),
                Started = r.ReadBool(),
                SyscallsForUsers = r.ReadBool(),
                ImageHash = r.ReadUInt64(),
                InfoSegment = r.ReadInt32(),
                ModuleInfoAddress = r.ReadUInt32(),
            };
            int segments = r.ReadInt32();
            for (int s = 0; s < segments; s++)
            {
                int index = r.ReadInt32();
                m.Segments[index] = new SegmentInfo(r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
            }

            int flags = r.ReadInt32();
            for (int s = 0; s < flags; s++)
            {
                int index = r.ReadInt32();
                m.SegmentFlags[index] = (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
            }

            image.Modules.Add(m);
        }

        return image;
    }
}
