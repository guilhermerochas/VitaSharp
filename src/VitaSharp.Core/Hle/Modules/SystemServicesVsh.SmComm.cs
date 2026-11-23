namespace VitaSharp.Core.Hle.Modules;

// SceSblSmCommForKernel (os0:kd/sm_comm.skprx 3.74, read in the module, study only): the kernel's channel to the
// secure processor's modules. StartSmFromFile/StartSmFromData load a secure module image (at most 0x20000 bytes)
// into the secure processor and open a session; CallFunc sends a command to a session's module; StopSm ends it.
// The emulated unit has no secure processor: no image can be started (the HLE's "secure service not available",
// 0x80020004, after the module's own checks), so no session ever exists and CallFunc/StopSm answer as the module
// does for an unknown session (0x800F0026).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblSmComm: a secure module image larger than 0x20000 bytes.</summary>
    const int SMCOMM_ERR_TOO_LARGE = unchecked((int)0x800F0016);

    /// <summary>SceSblSmComm: no session with that id.</summary>
    const int SMCOMM_ERR_NO_SESSION = unchecked((int)0x800F0026);

    /// <summary>The largest secure module image the module loads.</summary>
    const long SmCommMaxImage = 0x20000;

    /// <summary>ksceSblSmCommStartSmFromFile (0x7863A0CC)(int priority, const char *path, int cmdId,
    /// SceSblSmCommAuthInfo *authInfo, int *id): reads the image file (its open error when it is missing; larger
    /// than 0x20000 bytes 0x800F0016), then starts it in the secure processor.</summary>
    [Hle(Nid = 0x7863A0CC, Library = "SceSblSmCommForKernel", Default = true)]
    int ksceSblSmCommStartSmFromFile(int priority, string path, int cmdId, uint authInfo, uint id)
    {
        string? host = Emu.Vfs.Resolve(path);
        if (host == null || !File.Exists(host))
        {
            return Kernel.SceError.IO_ENOENT;
        }

        if (new FileInfo(host).Length > SmCommMaxImage)
        {
            return SMCOMM_ERR_TOO_LARGE;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblSmCommStartSmFromData (0x039C73B1)(int priority, const void *data, SceSize size, int cmdId,
    /// SceSblSmCommAuthInfo *authInfo, int *id): an image in memory (larger than 0x20000 bytes 0x800F0016), started
    /// in the secure processor.</summary>
    [Hle(Nid = 0x039C73B1, Library = "SceSblSmCommForKernel", Default = true)]
    int ksceSblSmCommStartSmFromData(int priority, uint data, uint size, int cmdId, uint authInfo, uint id)
    {
        return size > SmCommMaxImage ? SMCOMM_ERR_TOO_LARGE : KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblSmCommCallFunc (0xDB9FC204)(int id, int serviceId, int *result, void *data, SceSize size):
    /// a command to a session's module: no session exists (0x800F0026).</summary>
    [Hle(Nid = 0xDB9FC204, Library = "SceSblSmCommForKernel")]
    int ksceSblSmCommCallFunc(int id, int serviceId, uint result, uint data, uint size)
    {
        return SMCOMM_ERR_NO_SESSION;
    }

    /// <summary>ksceSblSmCommStopSm (0x0631F8ED)(int id, SceSblSmCommPair *result): ends a session (its stop command
    /// first): no session exists (0x800F0026).</summary>
    [Hle(Nid = 0x0631F8ED, Library = "SceSblSmCommForKernel")]
    int ksceSblSmCommStopSm(int id, uint result)
    {
        return ksceSblSmCommCallFuncThroughEntry(id, -1, 0, 0, 0);
    }
}
