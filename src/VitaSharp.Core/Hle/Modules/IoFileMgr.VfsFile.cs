using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The file objects of SceIofilemgrForDriver as os0:kd/iofilemgr.skprx 3.74 has them (read in the decompiled module,
// study only). A file object is a kernel object of the class "SceVfsFile" (0x8102117c): vfsAllocateFile (0x81007a80)
// creates it (0x8100bbb4, its name "SceVfsFile" or "file <assign name> <name>") and returns its UID, the descriptor
// the I/O manager keeps, with *file the object's body (the object + 8), which 0x8100ac88 fills:
//   +0x00 1 for a directory, +0x04 the open flags, +0x08 the position (64-bit), +0x10 the state (1 open), +0x14 the
//   process, +0x18 the vnode, +0x1C the next file of the vnode (the vnode's list at +0x90, their number at +0x94),
//   +0x20 0, +0x24 0, +0x28 the mount's +0xC8, +0x2C the mount flags' bit 16 (| 2 with bit 17), +0x30 0.
// The mount counts its files at +0x64. vfsFreeFile (0x81007a9c) takes the UID: off the vnode's list, the count down
// and the object deleted. The vnode op wrappers that take a file check its state first (0x8100ada4: open 0; 0x400 or
// 0x800 0x80010013; 0x200 0x80010009; else 0x80010016; close and dclose 0x8100add8: only 0x200 is refused) and the
// read wrappers end at the file's position against the vnode's size (+0x80) without the driver's op; the driver
// keeps the position.
public sealed partial class IoFileMgr
{
    /// <summary>The kind of the kernel object behind a file's UID.</summary>
    const string VfsFileKind = "vfsfile";

    /// <summary>The object header before a file's body (SceObjectBase: the object, its class).</summary>
    const uint VfsFileHeader = 8;

    /// <summary>The body of a file object.</summary>
    const uint VfsFileBodySize = 0x40;

    /// <summary>The state of an open file (+0x10).</summary>
    const uint VfsFileStateOpen = 1;

    /// <summary>The state of a dummy file (vfsAllocateFileDummy).</summary>
    const uint VfsFileStateDummy = 0x10000;

    const int VFS_ERR_BADF = unchecked((int)0x80010009);

    /// <summary>The type bits of a device vnode (a tty): its reads and writes go to the driver whatever the size.</summary>
    const uint VnodeTypeDevice = 0x2010;

    /// <summary>A new file object (0x8100bb24): its UID, <paramref name="file"/> its body (zeroed).</summary>
    int NewVfsFile(string name, out uint file)
    {
        uint body = Emu.Memory.Alloc(VfsFileHeader + VfsFileBodySize, "vfs file", MemoryRegion.Kernel, 8);
        GuestMemory.Fill(body, 0, VfsFileHeader + VfsFileBodySize);
        GuestMemory.W32(body, body);
        int uid;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uid = K.Objects.Register(new KernelData { Name = name, Kind = VfsFileKind, Address = body, Size = VfsFileHeader + VfsFileBodySize });
        }

        file = body + VfsFileHeader;
        return uid;
    }

    /// <summary>The body of the file object behind <paramref name="uid"/>, or 0 (0x8100ad3c).</summary>
    uint VfsFileOf(int uid)
    {
        return K.Objects.Get<KernelData>(uid) is { Kind: VfsFileKind } data ? data.Address + VfsFileHeader : 0;
    }

    /// <summary>ksceKernelGetProcessId of the calling thread: the kernel's for a kernel-mode thread.</summary>
    uint CurrentProcessId()
    {
        return GuestThread.Current is { KernelMode: false } ? (uint)Emu.Pid : (uint)KernelSpace.KernelPid;
    }

    /// <summary>
    /// vfsAllocateFile(vnode, file out, name) as the module has it (0x81007a80): a file object for the vnode (see the
    /// file's header), its UID returned and *file its body; the vnode's file list (+0x90, +0x94) and the mount's
    /// file count (+0x64) take it.
    /// </summary>
    [Hle(Nid = 0xBBAC1751, Library = "SceIofilemgrForDriver")]
    int vfsAllocateFile(uint vnode, uint fileOut, uint name)
    {
        if (vnode == 0 || fileOut == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint mount = GuestMemory.R32(vnode + 0x4C);
        string objectName = "SceVfsFile";
        if (name != 0)
        {
            uint misc = mount == 0 ? 0 : GuestMemory.R32(mount + 0x7C);
            uint assign = misc == 0 ? 0 : GuestMemory.R32(misc);
            string text = "file " + (assign == 0 ? "" : GuestMemory.ReadCString(assign, 0x20)) + " " + GuestMemory.ReadCString(name, 0x20);
            objectName = text.Length > 0x1E ? text[..0x1E] : text;
        }

        int uid = NewVfsFile(objectName, out uint file);
        if (mount != 0)
        {
            GuestMemory.W32(mount + 0x64, GuestMemory.R32(mount + 0x64) + 1);
        }

        // 0x8100ac88
        GuestMemory.W32(vnode + 0x94, GuestMemory.R32(vnode + 0x94) + 1);
        GuestMemory.W32(file + 0x18, vnode);
        GuestMemory.W32(file + 0x10, VfsFileStateOpen);
        GuestMemory.W32(file + 0x14, CurrentProcessId());
        uint flags = mount == 0 ? 0 : GuestMemory.R32(mount + 0x50);
        uint mode = (flags & 0x1FFFF) >> 16;
        if ((flags & 0x20000) != 0)
        {
            mode |= 2;
        }

        GuestMemory.W32(file + 0x28, mount == 0 ? 0 : GuestMemory.R32(mount + 0xC8));
        GuestMemory.W32(file + 0x2C, mode);
        GuestMemory.W32(file + 0x1C, GuestMemory.R32(vnode + 0x90));
        GuestMemory.W32(vnode + 0x90, file);
        GuestMemory.W32(fileOut, file);
        return uid;
    }

    /// <summary>vfsAllocateFileDummy(flags, directory, name) as the module has it (0x81007ae4): a file object with no
    /// vnode (+0 1 for a directory, +4 the flags, state 0x10000, +0x14 the process); its UID.</summary>
    [Hle(Nid = 0xCDF3EF52, Library = "SceIofilemgrForDriver")]
    int vfsAllocateFileDummy(uint flags, uint directory, uint name)
    {
        int uid = NewVfsFile(name == 0 ? "SceVfsFile" : GuestMemory.ReadCString(name, 0x1F), out uint file);
        GuestMemory.W32(file, directory != 0 ? 1u : 0u);
        GuestMemory.W32(file + 4, flags);
        GuestMemory.W32(file + 0x10, VfsFileStateDummy);
        GuestMemory.W32(file + 0x14, CurrentProcessId());
        return uid;
    }

    /// <summary>vfsFreeFile(vnode, uid) as the module has it (0x81007a9c): a UID that is not a file object 0x80010009;
    /// the file leaves the vnode's list (0x8100b6c0: +0x90 / +0x94, its +0x18 and +0x1C cleared), the mount's file
    /// count (+0x64) goes down and the object is deleted; 0.</summary>
    [Hle(Nid = 0xC85E33A2, Library = "SceIofilemgrForDriver")]
    int vfsFreeFile(uint vnode, uint uid)
    {
        uint file = VfsFileOf((int)uid);
        if (file == 0)
        {
            return VFS_ERR_BADF;
        }

        if (vnode != 0)
        {
            UnlinkVnodeFile(vnode, file);
            uint mount = GuestMemory.R32(vnode + 0x4C);
            if (mount != 0 && GuestMemory.R32(mount + 0x64) > 0)
            {
                GuestMemory.W32(mount + 0x64, GuestMemory.R32(mount + 0x64) - 1);
            }
        }

        K.Objects.Remove((int)uid);
        Emu.Memory.Free(file - VfsFileHeader);
        return 0;
    }

    static void UnlinkVnodeFile(uint vnode, uint file)
    {
        uint at = GuestMemory.R32(vnode + 0x90);
        if (at == 0)
        {
            return;
        }

        if (at == file)
        {
            GuestMemory.W32(vnode + 0x90, GuestMemory.R32(file + 0x1C));
        }
        else
        {
            while (at != 0 && GuestMemory.R32(at + 0x1C) != file)
            {
                at = GuestMemory.R32(at + 0x1C);
            }

            if (at == 0)
            {
                return;
            }

            GuestMemory.W32(at + 0x1C, GuestMemory.R32(file + 0x1C));
        }

        GuestMemory.W32(file + 0x1C, 0);
        GuestMemory.W32(file + 0x18, 0);
        GuestMemory.W32(vnode + 0x94, GuestMemory.R32(vnode + 0x94) - 1);
    }

    /// <summary>0x8100ada4: the state of a file an operation reads, writes or seeks: open 0; 0x400 / 0x800
    /// 0x80010013; 0x200 0x80010009; anything else 0x80010016.</summary>
    static int CheckFileUsable(uint file)
    {
        if (file == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint state = GuestMemory.R32(file + 0x10);
        if ((state & 1) != 0)
        {
            return 0;
        }

        return state switch
        {
            0x400 or 0x800 => VFS_ERR_UNMOUNTING,
            0x200 => VFS_ERR_BADF,
            _ => VFS_ERR_INVAL,
        };
    }

    /// <summary>0x8100add8: the state of a file being closed: only 0x200 (already bad) is refused, 0x80010009.</summary>
    static int CheckFileClosable(uint file)
    {
        if (file == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint state = GuestMemory.R32(file + 0x10);
        return (state & 1) == 0 && state == 0x200 ? VFS_ERR_BADF : 0;
    }
}
