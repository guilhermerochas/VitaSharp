using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Kernel-only entries of SceIofilemgrForDriver as the FW 3.74 iofilemgr.skprx has them (decompiled, study only): the
/// variants the VSH bridge and the module manager call, which run the same operation as the plain call.
/// </summary>
public sealed partial class IoFileMgr
{
    /// <summary>The error of a second path mapping function while one is installed.</summary>
    const int PathMappingBusy = unchecked((int)0x80010010);

    /// <summary>The path mapping function the application manager installed (0: none).</summary>
    [SnapshotMissing] // states of builds before it: none was installed
    uint _pathMappingFunction;

    /// <summary>ksceIoGetstatForVshbridge(path, stat, ?): the bridge's getstat, the same operation as ksceIoGetstat.</summary>
    [Hle(Nid = 0xD6503624, Library = "SceIofilemgrForDriver")]
    int ksceIoGetstatForVshbridge(string path, uint stat, uint unused) => sceIoGetstat(path, stat);

    /// <summary>ksceIoChstatForVshbridge(path, stat, bits): the bridge's chstat, the same operation as ksceIoChstat.</summary>
    [Hle(Nid = 0x1F98BD50, Library = "SceIofilemgrForDriver")]
    int ksceIoChstatForVshbridge(string path, uint stat, int bits) => sceIoChstat(path, stat, bits);

    /// <summary>ksceIoDreadForVshbridge(fd, dirent): the bridge's dread, the same operation as ksceIoDread.</summary>
    [Hle(Nid = 0x03051B02, Library = "SceIofilemgrForDriver")]
    int ksceIoDreadForVshbridge(int fd, uint dirent) => sceIoDreadThroughEntry(fd, dirent);

    /// <summary>ksceIoSyncByFd2(fd, flag): ksceIoSyncByFd with its flag (the application manager's and FIOS's call).</summary>
    [Hle(Nid = 0x43170575, Library = "SceIofilemgrForDriver")]
    int ksceIoSyncByFd2(int fd, int flag) => sceIoSyncByFdThroughEntry(fd, flag);

    /// <summary>ksceIoFlock(fd, op): the file lock of sceIoFlockForSystem (vshIoFlock's kernel side).</summary>
    [Hle(Nid = 0x16336A0D, Library = "SceIofilemgrForDriver")]
    int ksceIoFlock(int fd, int op) => sceIoFlockForSystemThroughEntry(fd, op);

    /// <summary>
    /// ksceIoOpenForPid(pid, path, flags, mode): opens <paramref name="path"/> as process <paramref name="pid"/> sees it
    /// (its FIOS overlays applied, as the firmware asks FIOS to resolve the path for the pid), then as ksceIoOpen.
    /// </summary>
    [Hle(Nid = 0xC3D34965, Library = "SceIofilemgrForDriver")]
    int ksceIoOpenForPid(int pid, string path, int flags, int mode)
    {
        if (KernelSpace.ProcessOf(Emu, pid) is { } process && !ReferenceEquals(process, Emu))
        {
            path = process.Hle.Modules.OfType<Fios2User>().FirstOrDefault()?.Resolve(path) ?? path;
        }

        return sceIoOpen(path, flags, mode);
    }

    /// <summary>ksceIoSetPathMappingFunction(function): installs the function path lookups go through (0 removes it); a
    /// second one while one is installed is 0x80010010. VitaSharp's path resolution does not call it.</summary>
    [Hle(Nid = 0xCFAECF18, Library = "SceIofilemgrForDriver")]
    int ksceIoSetPathMappingFunction(uint function)
    {
        if (_pathMappingFunction != 0 && function != 0)
        {
            return PathMappingBusy;
        }

        _pathMappingFunction = function;
        return 0;
    }
}
