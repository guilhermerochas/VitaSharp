using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// VSHLE010: these syscalls share the C# body of the user-library function they stand behind. On the Vita the user
// library's function calls the syscall, never the reverse, so a hook on the user library's export must not see the
// syscall's calls: they stay direct.
#pragma warning disable VSHLE010

/// <summary>
/// The SceIofilemgr syscalls (<c>_sceIo*</c>) behind the SceLibKernel wrappers, for firmware modules loaded LLE that
/// call them directly. Packing read from the 3.74 libkernel wrappers: the last argument points to a small struct on
/// the caller's stack holding the arguments that did not fit in r0-r3 (64-bit offsets included); calls without extra
/// arguments still pass one (unused scratch the kernel ignores). The SceIoAsyncParam of the async forms travels in the
/// same struct, after the other arguments: Open and Chstat opt+0, Pread/Pwrite opt+8 (after the offset), Lseek opt+12
/// (after offset and whence), Devctl/Ioctl opt+12 (after inLen, outData, outLen). The Remove, Mkdir, Rmdir, Rename,
/// Getstat and Sync async forms have their arguments in registers, so the 3.74 wrappers pass the SceIoAsyncParam
/// through in the register after them (r1 for Remove/Rmdir, r2 for Mkdir/Rename/Getstat/Sync) and the unwritten
/// scratch struct comes last; the 3.74 iofilemgr hands that register to its async request.
/// </summary>
public sealed partial class IoFileMgr
{
    static uint Word(uint opt, int index) => opt == 0 ? 0 : GuestMemory.R32(opt + (uint)index * 4);
    static long Off64(uint opt) => opt == 0 ? 0 : (long)(Word(opt, 0) | ((ulong)Word(opt, 1) << 32));

    [Hle(Cost = HleCost.KernelEntry)] int _sceIoOpen(string path, int flags, int mode, uint opt) => sceIoOpen(path, flags, mode);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoOpenAsync(string path, int flags, int mode, uint opt) => sceIoOpenAsync(path, flags, mode, Word(opt, 0));
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoRemove(string path, uint opt) => sceIoRemove(path);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoRemoveAsync(string path, uint param, uint opt) => sceIoRemoveAsync(path, param);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoMkdir(string path, int mode, uint opt) => sceIoMkdir(path, mode);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoMkdirAsync(string path, int mode, uint param, uint opt) => sceIoMkdirAsync(path, mode, param);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoRmdir(string path, uint opt) => sceIoRmdir(path);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoRmdirAsync(string path, uint param, uint opt) => sceIoRmdirAsync(path, param);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoRename(string oldPath, string newPath, uint opt) => sceIoRename(oldPath, newPath);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoRenameAsync(string oldPath, string newPath, uint param, uint opt) => sceIoRenameAsync(oldPath, newPath, param);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoDopen(string path, uint opt) => sceIoDopen(path);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoDread(int fd, uint dirent) => sceIoDread(fd, dirent);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoGetstat(string path, uint stat, uint opt) => sceIoGetstat(path, stat);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoGetstatAsync(string path, uint stat, uint param, uint opt) => sceIoGetstatAsync(path, stat, param);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoGetstatByFd(int fd, uint stat) => sceIoGetstatByFd(fd, stat);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoChstat(string path, uint stat, int bits, uint opt) => sceIoChstat(path, stat, bits);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoChstatAsync(string path, uint stat, int bits, uint opt) => sceIoChstatAsync(path, stat, bits, Word(opt, 0));
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoChstatByFd(int fd, uint stat, int bits) => sceIoChstatByFd(fd, stat, bits);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoSync(uint device, int flag, uint opt) => sceIoSync(device, flag);
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoSyncAsync(uint device, int flag, uint param, uint opt) => sceIoSyncAsync(device, flag, param);

    /// <summary>(fd, opt {offset u64, whence[, SceIoAsyncParam*]}).</summary>
    [Hle(Cost = HleCost.KernelEntry)] long _sceIoLseek(int fd, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoLseek(fd, Off64(opt), (int)Word(opt, 2));
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoLseekAsync(int fd, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoLseekAsync(fd, Off64(opt), (int)Word(opt, 2), Word(opt, 3));

    /// <summary>(fd, buf, size, opt {offset u64[, SceIoAsyncParam*]}).</summary>
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoPread(int fd, uint buf, uint size, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoPread(fd, buf, size, Off64(opt));
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoPreadAsync(int fd, uint buf, uint size, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoPreadAsync(fd, buf, size, Off64(opt), Word(opt, 2));
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoPwrite(int fd, uint buf, uint size, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoPwrite(fd, buf, size, Off64(opt));
    [Hle(Cost = HleCost.KernelEntry)] int _sceIoPwriteAsync(int fd, uint buf, uint size, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoPwriteAsync(fd, buf, size, Off64(opt), Word(opt, 2));

    /// <summary>(device/fd, cmd, inData, opt {inLen, outData, outLen[, SceIoAsyncParam*]}).</summary>
    [Hle(Cost = HleCost.KernelEntry)]
    int _sceIoDevctl(string dev, int cmd, uint arg, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoDevctl(dev, cmd, arg, Word(opt, 0), Word(opt, 1), Word(opt, 2));
    [Hle(Cost = HleCost.KernelEntry)]
    int _sceIoDevctlAsync(string dev, int cmd, uint arg, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoDevctlAsync(dev, cmd, arg, Word(opt, 0), Word(opt, 1), Word(opt, 2), Word(opt, 3));
    [Hle(Cost = HleCost.KernelEntry)]
    int _sceIoIoctl(int fd, int cmd, uint arg, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoIoctl(fd, cmd, arg, Word(opt, 0), Word(opt, 1), Word(opt, 2));
    [Hle(Cost = HleCost.KernelEntry)]
    int _sceIoIoctlAsync(int fd, int cmd, uint arg, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoIoctlAsync(fd, cmd, arg, Word(opt, 0), Word(opt, 1), Word(opt, 2), Word(opt, 3));
}
