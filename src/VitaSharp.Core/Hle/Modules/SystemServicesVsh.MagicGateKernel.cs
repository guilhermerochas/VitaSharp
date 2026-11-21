using System.Buffers.Binary;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceMagicGateForDriver (FW 3.74 os0:kd/magicgate.skprx, read in the module, study only): the MagicGate
// authentication of Memory Stick media that SceMgVideo and SceUsbstorMg use: a driver lock, the module's start and
// stop, the authentication session with the medium (keys made with the secure processor, kept in 0x29 slots) and the
// MagicGate ciphers. The emulated unit has no MagicGate medium and does not run the secure processor: the lock and the
// start/stop bookkeeping succeed, no session ever starts (its queries answer "not started" / "no key in the slot"),
// and every step that authenticates, derives or ciphers answers "not supported" (0x80020004) after the module's size
// checks. The constant data the module hands out (its 16-byte identifier and two tables) is read from the installed
// firmware's module, never from the emulator; without it those calls answer "not supported".
public sealed partial class SystemServicesVsh
{
    /// <summary>SceMagicGate: a size that is zero or not a multiple of 8, a header too short (0x80501002).</summary>
    const int MG_ERR_SIZE = unchecked((int)0x80501002);

    /// <summary>SceMagicGate: a bad slot or a slot with no key (0x80501006).</summary>
    const int MG_ERR_SLOT = unchecked((int)0x80501006);

    /// <summary>SceMagicGate: a reader index it does not know (0x80501000).</summary>
    const int MG_ERR_READER = unchecked((int)0x80501000);

    /// <summary>SceMagicGate: no authentication session was started (0x80505005).</summary>
    const int MG_ERR_NOT_STARTED = unchecked((int)0x80505005);

    /// <summary>The module NID of FW 3.74's SceMagicGate and where its constant data is in its first segment.</summary>
    const uint MagicGate374ModuleNid = 0xD140CEAC;
    const uint MgIdentifierOffset = 0x69D0, MgTableAOffset = 0x6908, MgTableBOffset = 0x6800;
    const int MgTableASize = 0x8C, MgTableBSize = 0x104;

    /// <summary><paramref name="length"/> bytes at <paramref name="offset"/> of the first segment of FW 3.74's
    /// SceMagicGate in the installed firmware, or null.</summary>
    byte[]? MagicGateData(uint offset, int length)
    {
        if (Firmware.FirmwareKernelImage.For(Emu.Options.FsRoot).Named("SceMagicGate") is not { } module)
        {
            return null;
        }

        return FirmwareSegmentBytes(module.Elf, module.Image.ModuleNid, MagicGate374ModuleNid, offset, length);
    }

    /// <summary>Bytes of a module ELF's first segment, when the module is the expected one (by its module NID).</summary>
    internal static byte[]? FirmwareSegmentBytes(byte[] elf, uint moduleNid, uint expectedNid, uint offset, int length)
    {
        if (moduleNid != expectedNid || elf.Length < 0x34)
        {
            return null;
        }

        uint programHeaders = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(0x1C));
        if (programHeaders + 0x20 > elf.Length)
        {
            return null;
        }

        uint fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)programHeaders + 4));
        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)programHeaders + 0x10));
        if (offset + (uint)length > fileSize || fileOffset + offset + (uint)length > elf.Length)
        {
            return null;
        }

        return elf.AsSpan((int)(fileOffset + offset), length).ToArray();
    }

    // ------------------------------------------------------------------ the driver lock, start and stop

    /// <summary>The driver lock (lock, try-lock, unlock): nothing else runs a session, so it is always free: 0.</summary>
    [Hle("NID_A49B9D70", Nid = 0xA49B9D70, Library = "SceMagicGateForDriver")]
    [Hle("NID_43518F19", Nid = 0x43518F19, Library = "SceMagicGateForDriver")]
    [Hle("NID_492EE950", Nid = 0x492EE950, Library = "SceMagicGateForDriver")]
    int MagicGateLock() => 0;

    /// <summary>The module's initialisation, termination and slot clearing (no slot holds a key): 0.</summary>
    [Hle("NID_46A28FAA", Nid = 0x46A28FAA, Library = "SceMagicGateForDriver")]
    [Hle("NID_6D3B42E6", Nid = 0x6D3B42E6, Library = "SceMagicGateForDriver")]
    [Hle("NID_38AE3ACA", Nid = 0x38AE3ACA, Library = "SceMagicGateForDriver")]
    [Hle("NID_7B052A10", Nid = 0x7B052A10, Library = "SceMagicGateForDriver")]
    [Hle("NID_8E2E577E", Nid = 0x8E2E577E, Library = "SceMagicGateForDriver")]
    int MagicGateBookkeeping() => 0;

    /// <summary>The key files of a reader slot the module keeps: tm0:/drm/magicgate/ibd&lt;reader, 3 digits&gt;&lt;slot, 2
    /// digits&gt;.dat.</summary>
    const string MgKeyFileFormat = "tm0:drm/magicgate/ibd{0:D3}{1:D2}.dat";

    /// <summary>Removes the key files of <paramref name="reader"/> for the slots <paramref name="firstSlot"/> to
    /// <paramref name="lastSlot"/> (the module names a slot by its low 5 bits), as the module does with ksceIoRemove;
    /// a file that is not there is not an error.</summary>
    void MagicGateRemoveKeyFiles(uint reader, uint firstSlot, uint lastSlot)
    {
        for (uint slot = firstSlot; slot <= lastSlot; slot++)
        {
            string path = string.Format(System.Globalization.CultureInfo.InvariantCulture, MgKeyFileFormat,
                reader & 0xFF, slot & 0x1F);
            if (Emu.Vfs.Resolve(path, forWrite: true) is { } host && File.Exists(host))
            {
                File.Delete(host);
            }
        }
    }

    /// <summary>(int, int reader): resets a reader 0..4 (its key files of slots 0..31 removed), or with -1 readers
    /// 0..3 (and the module reloads its key table: empty, no session): 0; another index 0x80501000.</summary>
    [Hle("NID_8E87BA88", Nid = 0x8E87BA88, Library = "SceMagicGateForDriver")]
    int MagicGateResetReader(uint unused, uint reader)
    {
        if (reader != 0xFFFFFFFF && reader >= 5)
        {
            return MG_ERR_READER;
        }

        uint first = reader == 0xFFFFFFFF ? 0 : reader;
        uint last = reader == 0xFFFFFFFF ? 3 : reader;
        for (uint index = first; index <= last; index++)
        {
            MagicGateRemoveKeyFiles(index, 0, 0x1F);
        }

        return 0;
    }

    /// <summary>(int, int slot): clears slot -1..0x20 (-1 every slot 0..31) of readers 0..3 (their key files removed)
    /// and the module reloads its key table (empty, no session): 0; another slot 0x80501000.</summary>
    [Hle("NID_3EDD73EB", Nid = 0x3EDD73EB, Library = "SceMagicGateForDriver")]
    int MagicGateClearSlot(uint unused, uint slot)
    {
        if (slot + 1 >= 0x22)
        {
            return MG_ERR_READER;
        }

        uint first = slot == 0xFFFFFFFF ? 0 : slot;
        uint last = slot == 0xFFFFFFFF ? 0x1F : slot;
        for (uint reader = 0; reader < 4; reader++)
        {
            MagicGateRemoveKeyFiles(reader, first, last);
        }

        return 0;
    }

    /// <summary>(): starts the authentication session with the MagicGate medium: there is none.</summary>
    [Hle("NID_30DF2E82", Nid = 0x30DF2E82, Library = "SceMagicGateForDriver", Default = true)]
    int MagicGateStart() => KERNEL_ERR_UNSUP;

    /// <summary>The session's queries (its keys, its state; E62B8A67 (int, void *out 0x20, int index)): no session
    /// was started (only 30DF2E82 starts one), which the module answers before any argument check: 0x80505005.</summary>
    [Hle("NID_D819D5C6", Nid = 0xD819D5C6, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_E62B8A67", Nid = 0xE62B8A67, Library = "SceMagicGateForDriver", Default = true)]
    int MagicGateNotStarted() => MG_ERR_NOT_STARTED;

    /// <summary>(int slot): whether the slot holds a key: a slot past 0x28 or an empty one 0x80501006 (none is filled).</summary>
    [Hle("NID_144BFA76", Nid = 0x144BFA76, Library = "SceMagicGateForDriver", Default = true)]
    int MagicGateSlotReady(int slot) => MG_ERR_SLOT;

    // ------------------------------------------------------------------ authentication and ciphers (secure)

    /// <summary>The ciphers over a buffer (in, out, iv, size, slot...): a size of 0 or not a multiple of 8
    /// 0x80501002, then not supported.</summary>
    [Hle("NID_4F4B2DC9", Nid = 0x4F4B2DC9, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_B0827B4F", Nid = 0xB0827B4F, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_593016E1", Nid = 0x593016E1, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_9CEF4965", Nid = 0x9CEF4965, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_A495FA95", Nid = 0xA495FA95, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_0770CBEF", Nid = 0x0770CBEF, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_CFD943E0", Nid = 0xCFD943E0, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_6A240950", Nid = 0x6A240950, Library = "SceMagicGateForDriver", Default = true)]
    int MagicGateCipher(uint input, uint output, uint iv, uint size) =>
        size == 0 || (size & 7) != 0 ? MG_ERR_SIZE : KERNEL_ERR_UNSUP;

    /// <summary>The authentication messages (ctx, message, size, ...): a message shorter than its 0x2C-byte header
    /// 0x80501002, then not supported.</summary>
    [Hle("NID_0B01FE3F", Nid = 0x0B01FE3F, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_CA74A13D", Nid = 0xCA74A13D, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_7A233CBC", Nid = 0x7A233CBC, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_465A7B76", Nid = 0x465A7B76, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_B20497C7", Nid = 0xB20497C7, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_2D5069DE", Nid = 0x2D5069DE, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_42A9D74E", Nid = 0x42A9D74E, Library = "SceMagicGateForDriver", Default = true)]
    int MagicGateMessage(uint context, uint message, uint size) => size < 0x2C ? MG_ERR_SIZE : KERNEL_ERR_UNSUP;

    /// <summary>The key derivations, MACs, random challenges and the session exchange steps: not supported.</summary>
    [Hle("NID_01FD0260", Nid = 0x01FD0260, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_07BDCE2F", Nid = 0x07BDCE2F, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_4E8B4EB5", Nid = 0x4E8B4EB5, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_C9658A76", Nid = 0xC9658A76, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_05912BF5", Nid = 0x05912BF5, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_ACA0F529", Nid = 0xACA0F529, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_2B026FFF", Nid = 0x2B026FFF, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_F2502A3B", Nid = 0xF2502A3B, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_3270BEE6", Nid = 0x3270BEE6, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_A855C045", Nid = 0xA855C045, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_12CE769D", Nid = 0x12CE769D, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_E0888D78", Nid = 0xE0888D78, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_FEF11D84", Nid = 0xFEF11D84, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_1BCE34AD", Nid = 0x1BCE34AD, Library = "SceMagicGateForDriver", Default = true)]
    [Hle("NID_75B445DA", Nid = 0x75B445DA, Library = "SceMagicGateForDriver", Default = true)]
    int MagicGateSecure() => KERNEL_ERR_UNSUP;

    // ------------------------------------------------------------------ the module's constant data

    /// <summary>(void *out 0x10): the module's 16-byte identifier, from the installed firmware.</summary>
    [Hle("NID_6A4D4F91", Nid = 0x6A4D4F91, Library = "SceMagicGateForDriver")]
    [Hle("NID_0201E54B", Nid = 0x0201E54B, Library = "SceMagicGateForDriver")]
    int MagicGateIdentifier(uint output)
    {
        if (MagicGateData(MgIdentifierOffset, 0x10) is not { } data)
        {
            return KERNEL_ERR_UNSUP;
        }

        data.CopyTo(GuestMemory.Span(output, data.Length));
        return 0;
    }

    int MagicGateTable(uint output, uint size, uint offset, int length)
    {
        if (output == 0)
        {
            return length;
        }

        if (MagicGateData(offset, length) is not { } data)
        {
            return KERNEL_ERR_UNSUP;
        }

        int copied = (int)Math.Min(size, (uint)length);
        data.AsSpan(0, copied).CopyTo(GuestMemory.Span(output, copied));
        return length;
    }

    /// <summary>(void *out, SceSize size): the module's 0x8C-byte table (at most <paramref name="size"/> bytes copied);
    /// returns its size.</summary>
    [Hle("NID_758D7158", Nid = 0x758D7158, Library = "SceMagicGateForDriver", ReturnsValue = true)]
    int MagicGateTableA(uint output, uint size) => MagicGateTable(output, size, MgTableAOffset, MgTableASize);

    /// <summary>(void *out, SceSize size): the module's 0x104-byte table; returns its size.</summary>
    [Hle("NID_0EBA5958", Nid = 0x0EBA5958, Library = "SceMagicGateForDriver", ReturnsValue = true)]
    int MagicGateTableB(uint output, uint size) => MagicGateTable(output, size, MgTableBOffset, MgTableBSize);
}
