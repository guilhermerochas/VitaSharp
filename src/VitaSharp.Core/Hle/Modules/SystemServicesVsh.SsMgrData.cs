using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceSblSsMgrForDriver (ss_mgr.skprx 3.74, read in the module, study only), second part: the typed NVS values, the
// portability encryption and the pass phrase. Each typed value is a fixed place of the syscon's NVS (the module's
// table: offset and size per type); a read or write moves it in syscon transfers of up to 16 bytes, into a buffer
// of 1, 2, 4, 8, 16 or 32 bytes at least as large as the value. Portability key type 0 is a plain copy; the other
// key types and the pass phrase are the secure processor's (0x80020004).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblSsMgr: an unknown NVS value type, a NULL buffer or one of a size it does not take.</summary>
    const int SSMGR_ERR_NVS_ARGUMENT = unchecked((int)0x800F0016);

    /// <summary>SceSblSsMgr: a portability request with a NULL pointer, a key type above 0x14 or sizes that differ,
    /// exceed 0x20 bytes or are not a multiple of 16.</summary>
    const int SSMGR_ERR_PORTABILITY_ARGUMENT = unchecked((int)0x800F1716);

    /// <summary>The NVS place of each value type (offset, size), as the module's table has it.</summary>
    static readonly (uint Offset, uint Size)[] NvsValues =
    [
        (0x4A4, 4), (0x500, 1), (0x482, 1), (0x4E0, 0x20), (0x483, 1), (0x486, 1),
    ];

    /// <summary>The portability message: a size word, then up to 0x20 bytes.</summary>
    const uint PortabilityMaxSize = 0x20, PortabilityMaxKeyType = 0x14;

    static bool NvsValueBufferValid(uint size, uint valueSize)
    {
        return size >= valueSize && size is 1 or 2 or 4 or 8 or 0x10 or 0x20;
    }

    /// <summary>ksceSblSsGetNvsData (0xFDD6D5DE)(int type 0..5, void *data, SceSize size): reads a typed NVS value.</summary>
    [Hle(Nid = 0xFDD6D5DE, Library = "SceSblSsMgrForDriver")]
    int ksceSblSsGetNvsData(uint type, uint data, uint size)
    {
        if (type >= NvsValues.Length || data == 0 || !NvsValueBufferValid(size, NvsValues[type].Size))
        {
            return SSMGR_ERR_NVS_ARGUMENT;
        }

        return NvsValueTransfer(NvsValues[type], data, write: false);
    }

    /// <summary>ksceSblSsSetNvsData (0x249ADB07)(int type 0..5, const void *data, SceSize size): writes a typed NVS
    /// value.</summary>
    [Hle(Nid = 0x249ADB07, Library = "SceSblSsMgrForDriver")]
    int ksceSblSsSetNvsData(uint type, uint data, uint size)
    {
        if (type >= NvsValues.Length || data == 0 || !NvsValueBufferValid(size, NvsValues[type].Size))
        {
            return SSMGR_ERR_NVS_ARGUMENT;
        }

        return NvsValueTransfer(NvsValues[type], data, write: true);
    }

    int NvsValueTransfer((uint Offset, uint Size) value, uint data, bool write)
    {
        uint done = 0;
        while (done < value.Size)
        {
            uint chunk = Math.Min(value.Size - done, 0x10);
            int result = write
                ? ksceSysconNvsWriteDataThroughEntry(value.Offset + done, data + done, chunk)
                : ksceSysconNvsReadDataThroughEntry(value.Offset + done, data + done, chunk);
            if (result != 0)
            {
                return result;
            }

            done += chunk;
        }

        return 0;
    }

    /// <summary>The portability request checks (both directions) and key type 0's plain copy of the message.</summary>
    static int Portability(uint keyType, uint iv, uint source, uint target)
    {
        if (iv == 0 || source == 0 || target == 0 || keyType > PortabilityMaxKeyType)
        {
            return SSMGR_ERR_PORTABILITY_ARGUMENT;
        }

        uint size = GuestMemory.R32(source);
        if (size != GuestMemory.R32(target) || size > PortabilityMaxSize || (size & 0xF) != 0)
        {
            return SSMGR_ERR_PORTABILITY_ARGUMENT;
        }

        if (keyType != 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        GuestMemory.ReadSpan(source + 4, (int)size).CopyTo(GuestMemory.Span(target + 4, (int)size));
        return 0;
    }

    /// <summary>ksceSblSsEncryptWithPortability (0x21EC51F6)(uint keyType, const void *iv, const ScePortabilityData
    /// *plain, ScePortabilityData *encrypted).</summary>
    [Hle(Nid = 0x21EC51F6, Library = "SceSblSsMgrForDriver")]
    int ksceSblSsEncryptWithPortability(uint keyType, uint iv, uint plain, uint encrypted)
    {
        return Portability(keyType, iv, plain, encrypted);
    }

    /// <summary>ksceSblSsDecryptWithPortability (0x934DB6B5)(uint keyType, void *iv, ScePortabilityData *encrypted,
    /// ScePortabilityData *plain).</summary>
    [Hle(Nid = 0x934DB6B5, Library = "SceSblSsMgrForDriver")]
    int ksceSblSsDecryptWithPortability(uint keyType, uint iv, uint encrypted, uint plain)
    {
        return Portability(keyType, iv, encrypted, plain);
    }

    /// <summary>ksceSblSsCreatePassPhrase (0xB8B298FD)(const SceSblSsCreatePassPhraseParam *param, void *passPhrase):
    /// the secure processor derives it (NULL 0x800F0C16).</summary>
    [Hle(Nid = 0xB8B298FD, Library = "SceSblSsMgrForDriver", Default = true)]
    int ksceSblSsCreatePassPhrase(uint param, uint passPhrase)
    {
        return param == 0 || passPhrase == 0 ? SSMGR_AIMGR_ERR_INVALID_ARGUMENT : KERNEL_ERR_UNSUP;
    }
}
