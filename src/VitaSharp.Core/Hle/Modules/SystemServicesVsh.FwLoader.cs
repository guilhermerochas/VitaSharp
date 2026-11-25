using System.Buffers.Binary;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceSblFwLoaderForDriver (post_ss_mgr.skprx 3.74, read in the module, study only): loads the firmware blob of a
// coprocessor (the Wi-Fi/Bluetooth chip, the PSP emulator's) for its driver. Lock(path) takes the loader for a
// firmware file (its name at most 0xFF characters), Load(1, buffer, size, out) has the module manager decrypt the
// file into the buffer, Unlock releases the loader. A blob is a SELF kernel module of two segments: the 0x90-byte
// module information, then the blob itself, which is what Load gives. The emulated unit decrypts the SELF layer as the
// loader does for modules; a blob whose content the secure processor decrypts once more (FwLoaderSecureInnerLayer) is
// given only from the decrypted image the user dumped from their own console, next to the blob with the extension
// ".bin" (that image is taken for any blob when present). Without it, Load answers 0x80020004 after the module's checks.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblFwLoader: a NULL argument, a context other than 1, a name too long.</summary>
    const int FWLOADER_ERR_INVALID_ARGUMENT = unchecked((int)0x800F0E16);

    /// <summary>SceSblFwLoader: Load or Unlock without the loader locked.</summary>
    const int FWLOADER_ERR_NOT_LOCKED = unchecked((int)0x800F0E29);

    /// <summary>The blob's file is missing (the open error of the file the loader reads).</summary>
    const int FWLOADER_ERR_NO_FILE = unchecked((int)0x80010002);

    /// <summary>The longest firmware path the loader takes.</summary>
    const int FwLoaderMaxPath = 0xFF;

    /// <summary>
    /// The blobs whose content is encrypted again inside the SELF, for the secure processor only: the PSP compat
    /// core's boot code (os0:kd/pcbc.skprx; after the SELF layer its 0x5CE0 bytes are still ciphertext, docs/notes/
    /// psp-compat.md). Compared by name because the path is fixed by the firmware (SceCompat loads it by this name)
    /// and nothing in the file tells the layers apart (its module information matches the plain blobs').
    /// </summary>
    static readonly string[] FwLoaderSecureInnerLayer = [Io.FirmwareLayout.KernelModules.Guest("pcbc.skprx")];

    [SaveState.SnapshotMissing] // states of builds before it: the loader was never locked
    bool _fwLoaderLocked;

    [SaveState.SnapshotMissing] // states of builds before it: no path kept
    string _fwLoaderPath = "";

    /// <summary>ksceSblFwLoaderLock (0xA6278D27)(const char *path, int zero): takes the loader for a firmware file.</summary>
    [Hle(Nid = 0xA6278D27, Library = "SceSblFwLoaderForDriver")]
    internal int ksceSblFwLoaderLock(uint path, int zero)
    {
        if (path == 0 || zero != 0)
        {
            return FWLOADER_ERR_INVALID_ARGUMENT;
        }

        return FwLoaderLock(Memory.GuestMemory.ReadCString(path, FwLoaderMaxPath + 1));
    }

    /// <summary>Lock with the path read.</summary>
    int FwLoaderLock(string name)
    {
        if (name.Length > FwLoaderMaxPath || (name.LastIndexOf('/') < 0 && name.LastIndexOf(':') < 0))
        {
            return FWLOADER_ERR_INVALID_ARGUMENT;
        }

        _fwLoaderLocked = true;
        _fwLoaderPath = name;
        return 0;
    }

    /// <summary>ksceSblFwLoaderLoad (0x91C73A54)(int context, void *buffer, SceSize size, SceSize *loaded): decrypts
    /// the locked firmware file into the buffer (context 1, buffer and output needed: 0x800F0E16; not locked:
    /// 0x800F0E29; a blob larger than the buffer: 0x800F0E16).</summary>
    [Hle(Nid = 0x91C73A54, Library = "SceSblFwLoaderForDriver")]
    internal int ksceSblFwLoaderLoad(int context, uint buffer, uint size, uint loaded)
    {
        if (context != 1 || buffer == 0 || loaded == 0)
        {
            return FWLOADER_ERR_INVALID_ARGUMENT;
        }

        int result = FwLoaderLoad(buffer, size, out uint length);
        if (result == 0)
        {
            Memory.GuestMemory.W32(loaded, length);
        }

        return result;
    }

    /// <summary>Load into guest memory, the length as a value.</summary>
    int FwLoaderLoad(uint buffer, uint size, out uint length)
    {
        length = 0;
        if (!_fwLoaderLocked)
        {
            return FWLOADER_ERR_NOT_LOCKED;
        }

        int read = FwLoaderBlob(_fwLoaderPath, out byte[] blob);
        if (read != 0)
        {
            return read;
        }

        if ((uint)blob.Length > size)
        {
            return FWLOADER_ERR_INVALID_ARGUMENT;
        }

        blob.CopyTo(Memory.GuestMemory.Span(buffer, blob.Length));
        length = (uint)blob.Length;
        return 0;
    }

    /// <summary>The decrypted blob of a firmware file: the user's dump next to it, else the second segment of the
    /// SELF (a plain ELF or a raw file as it is).</summary>
    int FwLoaderBlob(string path, out byte[] blob)
    {
        blob = [];
        int dot = path.LastIndexOf('.');
        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf(':'));
        string dumpPath = (dot > slash ? path[..dot] : path) + ".bin";
        string? dump = dumpPath == path ? null : Emu.Vfs.Resolve(dumpPath);
        if (dump != null && File.Exists(dump))
        {
            blob = File.ReadAllBytes(dump);
            return 0;
        }

        if (Array.IndexOf(FwLoaderSecureInnerLayer, path) >= 0)
        {
            Log.WarnOnce("fwloader-inner:" + path, $"SceSblFwLoader: {path} is decrypted by the secure processor: place the decrypted image dumped from your console at {dumpPath}");
            return KERNEL_ERR_UNSUP;
        }

        string? host = Emu.Vfs.Resolve(path);
        if (host == null || !File.Exists(host))
        {
            return FWLOADER_ERR_NO_FILE;
        }

        byte[] file = File.ReadAllBytes(host);
        if (SelfDecryptor.IsSelf(file))
        {
            try
            {
                file = SelfDecryptor.ToElf(file, null);
            }
            catch (Exception failure) when (failure is InvalidDataException or NotSupportedException or System.Security.Cryptography.CryptographicException)
            {
                Log.Warn($"SceSblFwLoader: {path}: {failure.Message}");
                return KERNEL_ERR_UNSUP;
            }
        }

        blob = SelfDecryptor.IsElf(file) ? FwLoaderPayload(file) : file;
        return 0;
    }

    /// <summary>The second loadable segment of a blob's ELF (the first is the module information), or the only one.</summary>
    static byte[] FwLoaderPayload(byte[] elf)
    {
        if (elf.Length < 0x34)
        {
            return [];
        }

        uint headers = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(0x1C));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x2C));
        byte[] last = [];
        int loadable = 0;
        for (int i = 0; i < count; i++)
        {
            long entry = headers + (long)i * 0x20;
            if (entry + 0x20 > elf.Length || BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)entry)) != 1)
            {
                continue;
            }

            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)entry + 4));
            uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)entry + 0x10));
            if ((long)offset + fileSize > elf.Length)
            {
                continue;
            }

            last = elf.AsSpan((int)offset, (int)fileSize).ToArray();
            loadable++;
            if (loadable == 2)
            {
                return last;
            }
        }

        return last;
    }

    /// <summary>ksceSblFwLoaderUnlock (0xBB59FC7A)(): releases the loader (0x800F0E29 when it is not locked).</summary>
    [Hle(Nid = 0xBB59FC7A, Library = "SceSblFwLoaderForDriver")]
    internal int ksceSblFwLoaderUnlock()
    {
        if (!_fwLoaderLocked)
        {
            return FWLOADER_ERR_NOT_LOCKED;
        }

        _fwLoaderLocked = false;
        _fwLoaderPath = "";
        return 0;
    }
}
