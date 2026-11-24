using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Io;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;

namespace VitaSharp.Core;

/// <summary>
/// The system's boot and an application's launch in the console's order (FW 3.74 SceAppMgr, read in the modules, study
/// only; docs/notes/plugins.md). The kernel comes up first, with the plugin loader's <c>*KERNEL</c> modules, before any
/// application and whatever it is. The application manager then launches a title: it finds the title's licence, reads
/// it (0x200 bytes) with the kernel's own I/O (ksceIoOpen, ksceIoRead, ksceIoClose of SceIofilemgrForDriver), has
/// SceNpDrm check it and give the title key (ksceNpDrmGetRifInfo, ksceNpDrmGetRifVitaKey, whose signature check and key
/// decryption are the secure processor's), and mounts app0 through ScePfsMgr with that key. A failure in any of those
/// steps fails the launch; the process is never made.
/// </summary>
public sealed partial class Emulator
{
    /// <summary>What a kernel plugin's load record says of when it loaded (the recordings' plugin lines).</summary>
    internal const string KernelPluginMoment = "system boot";

    /// <summary>SceIofilemgrForDriver's library NID and the kernel I/O functions appmgr reads a licence with (fixed by the
    /// firmware's export table).</summary>
    private const uint IofilemgrForDriverLibNid = 0x40FD29C7, KernelIoOpenNid = 0x75192972, KernelIoReadNid = 0xE17EFC03,
        KernelIoCloseNid = 0xF99DD8A3;

    /// <summary>The open flags of appmgr 3.74's licence read (SCE_O_RDONLY with 0x10000).</summary>
    private const int LicenceOpenFlags = 0x10001;

    /// <summary>appmgr's error for a licence it could not read (any error but a missing file, or a short read).</summary>
    private const int LicenceReadError = unchecked((int)0x80800010);

    /// <summary>The steps of app0's mount at a launch, as the log names them.</summary>
    private static readonly string[] s_app0MountSteps =
    [
        "licence (appmgr: the title's licence file)",
        "licence read (the kernel's ksceIoOpen/ksceIoRead/ksceIoClose)",
        "title key (SceNpDrm's checks and the secure processor)",
        "PFS mount (ScePfsMgr with the title key)",
    ];

    /// <summary>SceProcessmgrForKernel's library NID from FW 3.63 on and its ksceKernelCreateProcess (fixed by the
    /// firmware's export table; 3.60 had 0x7A69DE86 and 0x71CF71FD, which 3.74 does not export).</summary>
    private const uint ProcessmgrForKernelLibNid = 0xEB1F8EF7, KernelCreateProcessNid = 0x68068618;

    /// <summary>The process types appmgr 3.74 creates an application (0x1000000) and a system application
    /// (0x2000000) with.</summary>
    private const uint ApplicationProcessType = 0x1000000, SystemProcessType = 0x2000000;

    /// <summary>The size of ksceKernelCreateProcess's option block (its first word).</summary>
    private const uint CreateProcessOptionSize = 0x40;

    /// <summary>This process is the one the HLE's ksceKernelCreateProcess made: that call already went through the
    /// function's entry, so its launch does not make it again (<see cref="CreateProcessThroughProcessmgr"/>).</summary>
    private bool _createdByProcessmgr;

    /// <summary>
    /// The process's creation as appmgr 3.74 makes it at a launch (decompiled SceAppMgr, study only; also for a
    /// LoadExec, which replaces the process with a new one): ksceKernelCreateProcess(title ID, type, the executable's
    /// path, a 0x40-byte option) of SceProcessmgrForKernel, by the function's address, before app0 is mounted for the
    /// process. VitaSharp makes the process on the host, so the call is made through the entry only when kernel code
    /// hooked it (a kernel plugin loaded at the boot): the hook runs on a kernel thread with the launch's arguments, and
    /// the original it may continue to answers with this process (<see cref="ProcessmgrKernel.CreateLaunchedProcess"/>).
    /// An error the call returns fails the launch with it (<see cref="ProcessLoadException"/>), as appmgr's launch fails.
    /// Without a hook nothing runs.
    /// </summary>
    private void CreateProcessThroughProcessmgr(string self)
    {
        bool createdByProcessmgr = _createdByProcessmgr;
        _createdByProcessmgr = false;
        if (createdByProcessmgr || IsKernelProcess || Vita.KernelProcess is not { } kernel)
        {
            return;
        }

        FirmwareKernelModules firmware = kernel.FirmwareKernel;
        if (!firmware.IsPatched(ProcessmgrForKernelLibNid, KernelCreateProcessNid))
        {
            return;
        }

        ProcessmgrKernel processmgr = kernel.Hle.Modules.OfType<ProcessmgrKernel>().First();
        string executable = FirmwareLayout.HostToGuest(Options.FsRoot, Path.Combine(AppPath, self)) ?? "app0:" + self.Replace('\\', '/');
        string vs0 = Path.GetFullPath(Path.Combine(Options.FsRoot, "vs0")) + Path.DirectorySeparatorChar;
        uint type = AppPath.StartsWith(vs0, StringComparison.OrdinalIgnoreCase) ? SystemProcessType : ApplicationProcessType;
        const uint TitleIdSize = 0x20, PathSize = 0x400;
        uint block;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            block = Memory.Alloc(TitleIdSize + PathSize + CreateProcessOptionSize, "appmgr:create process", MemoryRegion.Kernel, 16);
        }

        uint titleId = block;
        uint path = block + TitleIdSize;
        uint option = path + PathSize;
        GuestMemory.Fill(block, 0, TitleIdSize + PathSize + CreateProcessOptionSize);
        GuestMemory.WriteCString(titleId, TitleId, (int)TitleIdSize - 1);
        GuestMemory.WriteCString(path, executable, (int)PathSize - 1);
        GuestMemory.W32(option, CreateProcessOptionSize);
        int result = 0;
        RunOnKernelThread(kernel, "SceAppMgrLaunch", _ =>
        {
            result = processmgr.CreateLaunchedProcess(this, () => (int)firmware.Call(ProcessmgrForKernelLibNid, KernelCreateProcessNid,
                () => (uint)Pid, titleId, type, path, option));
        }, "the launch's ksceKernelCreateProcess");
        Memory.Free(block);
        if (result < 0)
        {
            Log.Error($"{TitleId}: the launch stopped at the process's creation: ksceKernelCreateProcess returned 0x{result:X8}");
            throw new ProcessLoadException(result, new InvalidOperationException($"ksceKernelCreateProcess returned 0x{result:X8}"));
        }
    }

    /// <summary>
    /// The system's boot with the emulator option "Enable Kernel Plugins" on: the plugin configuration's <c>*KERNEL</c>
    /// modules load into the kernel and start, once per system, before any application is launched, as the Vita's
    /// plugin loader starts them while the system boots; the launch of a title that then fails (its app0 does not
    /// mount, its runtime is not installed) finds them loaded. Off, the pure firmware: nothing. Returns what happened
    /// to each module (empty when they were already loaded).
    /// </summary>
    internal List<string> BootKernelPlugins()
    {
        if (!KernelPluginsEnabled || Vita.KernelPluginsLoaded)
        {
            return [];
        }

        MigratePluginConfiguration();
        List<string> results = LoadKernelPlugins();
        if (results.Count > 0)
        {
            int started = results.Count(r => r.Contains(": loaded and started", StringComparison.Ordinal));
            Log.Info($"kernel plugins: {started} of {results.Count} loaded and started at system boot, before any application is launched");
        }

        return results;
    }

    /// <summary>
    /// An application folder that is still a PFS image gets app0 as appmgr mounts it at the launch: the licence where the
    /// firmware keeps it, read through the kernel's I/O, its title key by SceNpDrm's checks and the secure processor
    /// (<see cref="LicenseTitleKey"/>), then the PFS layer opened with that key (<see cref="Package.Pfs.PfsApp0View"/>),
    /// the key the executable's SELF is decrypted with too. Without a licence the launch fails as appmgr's does
    /// (<see cref="TitleLicenseMissingException"/>); without a key, or with one that does not open the image, as
    /// <see cref="TitleKeyException"/>. The log names the step that failed. The decrypted view is kept with the
    /// emulator's caches, next to the file system.
    /// </summary>
    private void OpenApp0Image()
    {
        if (!Package.Pfs.PfsApp0View.IsImage(AppPath))
        {
            return;
        }

        if (TitleLicense.FindFile(Options.FsRoot, AppPath, TitleId) is not { } licenseFile)
        {
            LogApp0MountFailure(0, TitleLicense.NoLicenseError, $"{AppPath} is a PFS image and has no licence");
            throw new TitleLicenseMissingException(TitleId, new FileNotFoundException($"{AppPath} is a PFS image and has no licence"));
        }

        int read = ReadLicence(licenseFile, out byte[] license);
        if (read == SceError.IO_ENOENT)
        {
            LogApp0MountFailure(1, TitleLicense.NoLicenseError, $"the licence {LogPath(licenseFile)} could not be opened");
            throw new TitleLicenseMissingException(TitleId, new FileNotFoundException(licenseFile));
        }

        if (read < 0)
        {
            LogApp0MountFailure(1, LicenceReadError, $"the licence {LogPath(licenseFile)} could not be read (0x{read:X8})");
            throw new TitleKeyException(TitleId, LicenceReadError);
        }

        string? activationFile = Vfs.Resolve("tm0:npdrm/act.dat");
        byte[] activation = activationFile != null && File.Exists(activationFile) ? File.ReadAllBytes(activationFile) : [];
        int error = LicenseTitleKey.Get(license, activation, SecureProcessorRifKey, out byte[]? key);
        if (error != 0 || key == null)
        {
            LogApp0MountFailure(2, error, $"app0 is a PFS image; the title key of its licence {LogPath(licenseFile)} is not available");
            throw new TitleKeyException(TitleId, error);
        }

        string fsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Options.FsRoot));
        string dataRoot = Path.GetDirectoryName(fsRoot) ?? fsRoot;
        byte[] folderHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPath.ToUpperInvariant()));
        string view = Path.Combine(dataRoot, "cache", "pfs", $"{TitleId}-{Convert.ToHexString(folderHash, 0, 8)}");
        Package.Pfs.PfsKeyMismatchException? mismatch = null;
        int mounted = MountApp0ThroughPfsMgr(key, () =>
        {
            try
            {
                Vfs.AddApp0BaseLayer(Package.Pfs.PfsApp0View.Open(AppPath, key, view, Log.Info));
                return 0;
            }
            catch (Package.Pfs.PfsKeyMismatchException e)
            {
                mismatch = e;
                return LicenseTitleKey.BadLicense;
            }
        });
        if (mismatch != null)
        {
            LogApp0MountFailure(3, LicenseTitleKey.BadLicense, mismatch.Message);
            throw new TitleKeyException(TitleId, LicenseTitleKey.BadLicense, mismatch);
        }

        if (mounted < 0)
        {
            LogApp0MountFailure(3, mounted, $"kscePfsMount returned 0x{mounted:X8}");
            throw new ProcessLoadException(mounted, new InvalidOperationException($"kscePfsMount returned 0x{mounted:X8}"));
        }

        Klicensee = key;
    }

    /// <summary>ScePfsMgrForKernel's library NID and its kscePfsMount (fixed by the firmware's export table).</summary>
    private const uint PfsMgrForKernelLibNid = 0xA067B56F, PfsMountNid = 0xA772209C;

    /// <summary>The mode appmgr 3.74 mounts an application's image with when it has the title's key (its last
    /// argument; 4 and 6 are other kinds of content).</summary>
    private const uint PfsMountModeWithKey = 5;

    /// <summary>The sizes of appmgr's kscePfsMount arguments: the path buffer (0x124), the mount point (0x10) and the
    /// title key (0x10).</summary>
    private const uint PfsMountPathSize = 0x124, PfsMountPointSize = 0x10, PfsMountKeySize = 0x10;

    /// <summary>
    /// app0's PFS mount as appmgr 3.74 makes it at a launch (decompiled SceAppMgr, study only): kscePfsMount(the
    /// application's path, a mount point named "ts" and twelve hex digits of the secure processor's random number and
    /// "d", the process's program authority ID as two words, the 16-byte title key, mode 5) of ScePfsMgrForKernel, by
    /// the function's address, on appmgr's thread; a failure stops the launch. VitaSharp opens the image itself
    /// (<paramref name="mount"/>), so the call is made through the entry only when kernel code hooked it: the hook runs on
    /// a kernel thread with those arguments, and the original it continues to answers with <paramref name="mount"/>
    /// (<see cref="IoFileMgr.MountLaunchedApp0"/>). The title key is the one the launch already has; nothing of the
    /// licence's checks changes. The authority ID is what the process has then: VitaSharp reads it from the executable,
    /// which is inside the image, after the mount, so it is 0 for a first launch. Without a hook the mount runs directly.
    /// Returns 0 or the error.
    /// </summary>
    private int MountApp0ThroughPfsMgr(byte[] key, Func<int> mount)
    {
        if (IsKernelProcess || Vita.KernelProcess is not { } kernel || !kernel.FirmwareKernel.IsPatched(PfsMgrForKernelLibNid, PfsMountNid))
        {
            return mount();
        }

        FirmwareKernelModules firmware = kernel.FirmwareKernel;
        IoFileMgr io = kernel.Hle.Modules.OfType<IoFileMgr>().First();
        string source = FirmwareLayout.HostToGuest(Options.FsRoot, AppPath) ?? AppPath;
        Span<byte> random = stackalloc byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        string mountPoint = "ts" + Convert.ToHexString(random).ToLowerInvariant() + "d";
        uint block;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            block = Memory.Alloc(PfsMountPathSize + PfsMountPointSize + PfsMountKeySize, "appmgr:pfs mount", MemoryRegion.Kernel, 16);
        }

        uint path = block;
        uint point = block + PfsMountPathSize;
        uint keyAddress = point + PfsMountPointSize;
        GuestMemory.Fill(block, 0, PfsMountPathSize + PfsMountPointSize + PfsMountKeySize);
        GuestMemory.WriteCString(path, source, (int)PfsMountPathSize - 1);
        GuestMemory.WriteCString(point, mountPoint, (int)PfsMountPointSize - 1);
        key.AsSpan(0, Math.Min(key.Length, (int)PfsMountKeySize)).CopyTo(GuestMemory.Span(keyAddress, (int)PfsMountKeySize));
        ulong authority = ProgramAuthorityId;
        int result = 0;
        RunOnKernelThread(kernel, "SceAppMgrLaunch", _ =>
        {
            result = io.MountLaunchedApp0(mount, () => (int)firmware.Call(PfsMgrForKernelLibNid, PfsMountNid, () => (uint)mount(),
                path, point, (uint)authority, (uint)(authority >> 32), keyAddress, PfsMountModeWithKey));
        }, "the launch's kscePfsMount");
        Memory.Free(block);
        return result;
    }

    /// <summary>The log line of a launch whose app0 mount failed: which step of the launch, and its error.</summary>
    private void LogApp0MountFailure(int step, int error, string detail)
    {
        Log.Error($"{TitleId}: the launch stopped at app0's mount, step {step + 1} of {s_app0MountSteps.Length} ({s_app0MountSteps[step]}): {detail} (0x{error:X8})");
    }

    /// <summary>A licence's path for the log: its Vita path (no host path of the user's computer).</summary>
    private string LogPath(string hostFile) => FirmwareLayout.HostToGuest(Options.FsRoot, hostFile) ?? Path.GetFileName(hostFile);

    /// <summary>
    /// The licence read of the launch: 0x200 bytes of <paramref name="licenceFile"/>, as appmgr reads them with the
    /// kernel's ksceIoOpen, ksceIoRead and ksceIoClose. When kernel code hooked any of those functions (a kernel plugin
    /// loaded at the boot), the read is made through their entries on a kernel thread, so the hooks run for the launch
    /// as they do on the Vita; otherwise the file is read directly, with nothing run. Returns the bytes read, or the
    /// open's error (0x80010002 for a missing file) or <see cref="LicenceReadError"/>.
    /// </summary>
    private int ReadLicence(string licenceFile, out byte[] licence)
    {
        licence = [];
        if (IsKernelProcess || Vita.KernelProcess is not { } kernel || FirmwareLayout.HostToGuest(Options.FsRoot, licenceFile) is not { } guestPath)
        {
            return ReadLicenceDirectly(licenceFile, out licence);
        }

        FirmwareKernelModules firmware = kernel.FirmwareKernel;
        if (!firmware.IsPatched(IofilemgrForDriverLibNid, KernelIoOpenNid) && !firmware.IsPatched(IofilemgrForDriverLibNid, KernelIoReadNid)
            && !firmware.IsPatched(IofilemgrForDriverLibNid, KernelIoCloseNid))
        {
            return ReadLicenceDirectly(licenceFile, out licence);
        }

        IoFileMgr io = kernel.Hle.Modules.OfType<IoFileMgr>().First();
        const int PathSize = 0x400;
        uint block;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            block = Memory.Alloc(PathSize + LicenseTitleKey.LicenseSize, "appmgr:licence", MemoryRegion.Kernel, 16);
        }

        uint path = block;
        uint buffer = block + PathSize;
        GuestMemory.Fill(block, 0, PathSize + LicenseTitleKey.LicenseSize);
        GuestMemory.WriteCString(path, guestPath, PathSize);
        int result = LicenceReadError;
        // VSHLE010: the lambdas below are the originals behind the kernel entries ksceIoOpen/Read/Close, which
        // firmware.Call already routes through when a hook took them; the user exports sceIoRead/sceIoClose are other
        // functions on the Vita, so a hook on them must not see the kernel's licence read
#pragma warning disable VSHLE010
        RunOnKernelThread(kernel, "SceAppMgrLaunch", _ =>
        {
            int fd = (int)firmware.Call(IofilemgrForDriverLibNid, KernelIoOpenNid,
                () => (uint)io.sceIoOpen(guestPath, LicenceOpenFlags, 0), path, LicenceOpenFlags, 0);
            if (fd < 0)
            {
                result = fd;
                return;
            }

            int count = (int)firmware.Call(IofilemgrForDriverLibNid, KernelIoReadNid,
                () => (uint)io.sceIoRead(fd, buffer, LicenseTitleKey.LicenseSize), (uint)fd, buffer, LicenseTitleKey.LicenseSize);
            firmware.Call(IofilemgrForDriverLibNid, KernelIoCloseNid, () => (uint)io.sceIoClose(fd), (uint)fd);
            result = count == LicenseTitleKey.LicenseSize ? count : LicenceReadError;
        }, "the launch's licence read");
#pragma warning restore VSHLE010

        if (result == LicenseTitleKey.LicenseSize)
        {
            licence = GuestMemory.ReadSpan(buffer, LicenseTitleKey.LicenseSize).ToArray();
        }

        Memory.Free(block);
        return result;
    }

    private static int ReadLicenceDirectly(string licenceFile, out byte[] licence)
    {
        if (!File.Exists(licenceFile))
        {
            licence = [];
            return SceError.IO_ENOENT;
        }

        licence = File.ReadAllBytes(licenceFile);
        return licence.Length;
    }
}
