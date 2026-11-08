using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The SceIofilemgr syscalls (<c>_sceIo*</c>) behind the SceLibKernel wrappers, for firmware modules loaded LLE that
/// call them directly. Packing read from the 3.74 libkernel wrappers: the last argument points to a small struct on
/// the caller's stack holding the arguments that did not fit in r0-r3 (64-bit offsets included); calls without extra
/// arguments still pass one (unused scratch the kernel ignores).
/// </summary>
public sealed partial class IoFileMgr
{
    static uint Word(uint opt, int index) => opt == 0 ? 0 : GuestMemory.R32(opt + (uint)index * 4);
    static long Off64(uint opt) => opt == 0 ? 0 : (long)(Word(opt, 0) | ((ulong)Word(opt, 1) << 32));

    [Hle] int _sceIoOpen(string path, int flags, int mode, uint opt) => sceIoOpen(path, flags, mode);
    [Hle] int _sceIoOpenAsync(string path, int flags, int mode, uint opt) => sceIoOpenAsync(path, flags, mode);
    [Hle] int _sceIoRemove(string path, uint opt) => sceIoRemove(path);
    [Hle] int _sceIoRemoveAsync(string path, uint opt) => sceIoRemoveAsync(path);
    [Hle] int _sceIoMkdir(string path, int mode, uint opt) => sceIoMkdir(path, mode);
    [Hle] int _sceIoMkdirAsync(string path, int mode, uint opt) => sceIoMkdirAsync(path, mode);
    [Hle] int _sceIoRmdir(string path, uint opt) => sceIoRmdir(path);
    [Hle] int _sceIoRmdirAsync(string path, uint opt) => sceIoRmdirAsync(path);
    [Hle] int _sceIoRename(string oldPath, string newPath, uint opt) => sceIoRename(oldPath, newPath);
    [Hle] int _sceIoRenameAsync(string oldPath, string newPath, uint opt) => sceIoRenameAsync(oldPath, newPath);
    [Hle] int _sceIoDopen(string path, uint opt) => sceIoDopen(path);
    [Hle] int _sceIoDread(int fd, uint dirent) => sceIoDread(fd, dirent);
    [Hle] int _sceIoGetstat(string path, uint stat, uint opt) => sceIoGetstat(path, stat);
    [Hle] int _sceIoGetstatAsync(string path, uint stat, uint opt) => sceIoGetstatAsync(path, stat);
    [Hle] int _sceIoGetstatByFd(int fd, uint stat) => sceIoGetstatByFd(fd, stat);
    [Hle] int _sceIoChstat(string path, uint stat, int bits, uint opt) => sceIoChstat(path, stat, bits);
    [Hle] int _sceIoChstatAsync(string path, uint stat, int bits, uint opt) => sceIoChstatAsync(path, stat, bits);
    [Hle] int _sceIoChstatByFd(int fd, uint stat, int bits) => sceIoChstatByFd(fd, stat, bits);
    [Hle] int _sceIoSync(uint device, int flag, uint opt) => sceIoSync(device, flag);
    [Hle] int _sceIoSyncAsync(uint device, int flag, uint opt) => sceIoSyncAsync(device, flag);

    /// <summary>(fd, opt {offset u64, whence}).</summary>
    [Hle] long _sceIoLseek(int fd, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoLseek(fd, Off64(opt), (int)Word(opt, 2));
    [Hle] int _sceIoLseekAsync(int fd, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoLseekAsync(fd, Off64(opt), (int)Word(opt, 2));

    /// <summary>(fd, buf, size, opt {offset u64}).</summary>
    [Hle] int _sceIoPread(int fd, uint buf, uint size, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoPread(fd, buf, size, Off64(opt));
    [Hle] int _sceIoPreadAsync(int fd, uint buf, uint size, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoPreadAsync(fd, buf, size, Off64(opt));
    [Hle] int _sceIoPwrite(int fd, uint buf, uint size, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoPwrite(fd, buf, size, Off64(opt));
    [Hle] int _sceIoPwriteAsync(int fd, uint buf, uint size, uint opt) => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoPwriteAsync(fd, buf, size, Off64(opt));

    /// <summary>(device/fd, cmd, inData, opt {inLen, outData, outLen}).</summary>
    [Hle]
    int _sceIoDevctl(string dev, int cmd, uint arg, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoDevctl(dev, cmd, arg, Word(opt, 0), Word(opt, 1), Word(opt, 2));
    [Hle]
    int _sceIoDevctlAsync(string dev, int cmd, uint arg, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoDevctlAsync(dev, cmd, arg, Word(opt, 0), Word(opt, 1), Word(opt, 2));
    [Hle]
    int _sceIoIoctl(int fd, int cmd, uint arg, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoIoctl(fd, cmd, arg, Word(opt, 0), Word(opt, 1), Word(opt, 2));
    [Hle]
    int _sceIoIoctlAsync(int fd, int cmd, uint arg, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceIoIoctlAsync(fd, cmd, arg, Word(opt, 0), Word(opt, 1), Word(opt, 2));
}
