using System.Collections.Concurrent;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceFios2 (vs0:sys/external/libfios2.suprx, FW 3.74): the FIOS2 file API. Games ship and run the firmware module
/// (app0:sce_module/libfios2.suprx; <see cref="Emulator.FirmwareCodeLibraries"/>); this serves a process that does not
/// load it. File handles (SceFiosFH) and directory handles map to sceIo descriptors.
/// </summary>
public sealed partial class Fios2 : HleModule
{
    readonly ConcurrentDictionary<int, int> _fhToFd = new();

    /// <summary>The sceIo descriptor behind a FIOS2 file handle, -1 when unknown.</summary>
    internal int FdOfHandle(int fh) => _fhToFd.TryGetValue(fh, out var fd) ? fd : -1;

    /// <summary>The FIOS2 file handle of a descriptor, -1 when none.</summary>
    internal int HandleOfFd(int fd)
    {
        foreach (var (fh, d) in _fhToFd) if (d == fd) return fh;
        return -1;
    }
}
