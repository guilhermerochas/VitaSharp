using System.Buffers.Binary;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceNpDrmForDriver's license queries (bootimage SceNpDrm 3.74, read in the module, study only): GetRifInfo,
// GetRifVitaKey and GetRifPspKey read a license file (rif) the caller loaded, big-endian: version (+0), version
// flag (+2), type (+4: 0 a PSP license, 1 a PS Vita one), flags (+6), account id (+8), content id (+0x10, 0x30
// bytes), key (+0x50), start and expiration times in ms since 1970 (+0x60, +0x68), flags2 (+0x98), the OpenPSID a
// unit-bound license is for (+0xC0) and the SKU flag (+0xFC). The low byte of the flags is the license kind: 0 an
// account license, which needs the unit's activation data (tm0:npdrm/act.dat); 2 a license of the activated
// account; 0xD a free one. Bit 0x100 binds it to one unit, bit 0x400 to a game card.
// What the secure module does is not here: the signature check (the module then answers 0x80870004, 0x80870005 for
// a PSP license checked by RSA) and the game card authentication (0x8087000A). The key of a license that passes the
// checks is decrypted by the secure processor (SceSblAuthMgrForDriver, from the license's encrypted key and the
// activation's keys), which the emulated unit does not have: GetRifVitaKey and GetRifPspKey answer its "not
// supported" (0x80020004) with the key buffer zeroed; the license's +0x50 field is never given as a key.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceNpDrm: a license that needs the unit's activation data when the unit has none.</summary>
    const int NPDRM_ERR_NOT_ACTIVATED = unchecked((int)0x80870003);

    /// <summary>SceNpDrm: a license shorter than its header (0x98 bytes), or a signature that does not verify.</summary>
    const int NPDRM_ERR_BAD_LICENSE = unchecked((int)0x80870004);

    /// <summary>SceNpDrm: data that does not verify (a license kind it does not know, a PSP license's RSA check).</summary>
    const int NPDRM_ERR_BAD_DATA = unchecked((int)0x80870005);

    /// <summary>SceNpDrm: a license of the activated account while no activation data is loaded.</summary>
    const int NPDRM_ERR_NO_ACTIVATION_DATA = unchecked((int)0x80870006);

    /// <summary>SceNpDrm: a license newer than the unit's activation data.</summary>
    const int NPDRM_ERR_ACTIVATION_TOO_OLD = unchecked((int)0x80870007);

    /// <summary>SceNpDrm: a license of another account than the activated one.</summary>
    const int NPDRM_ERR_OTHER_ACCOUNT = unchecked((int)0x80870008);

    /// <summary>SceNpDrm: a unit-bound license for another unit (OpenPSID).</summary>
    const int NPDRM_ERR_OTHER_UNIT = unchecked((int)0x80870009);

    /// <summary>SceNpDrm: a game-card-bound license the game card authentication does not confirm.</summary>
    const int NPDRM_ERR_GAME_CARD = unchecked((int)0x8087000A);

    /// <summary>The header part of a license (a PSP one is this long), the PS Vita license, and the size from which the
    /// flags2 field is read.</summary>
    const uint RifHeaderSize = 0x98, RifVitaSize = 0x200, RifFlags2Size = 0xA0;

    /// <summary>SceRtcTick values: 1970-01-01 (the epoch of the license times), 2001-01-01 (the start the module
    /// reports for a license without one) and "never expires".</summary>
    const ulong RtcTickEpoch1970 = 0xDCBFFEFF2BC000, RtcTickYear2001 = 0xE039BB591E1C00;
    const ulong RtcTickNever = 0x7FFFFFFFFFFFFFFF;

    /// <summary>The license kinds (low byte of the flags) the module knows: an account license, a license of the
    /// activated account and a free license.</summary>
    const byte RifKindAccount = 0, RifKindActivated = 2, RifKindFree = 0xD;

    /// <summary>The unit's activation data as SceNpDrm loads it at boot: present, its version (+0) and its account id
    /// (+8, as stored).</summary>
    readonly record struct NpDrmActivation(bool Present, ushort Version, ulong AccountId);

    NpDrmActivation ReadActivation()
    {
        string? host = Emu.Vfs.Resolve("tm0:npdrm/act.dat");
        if (host == null || !File.Exists(host))
        {
            return new NpDrmActivation(false, 0, 0);
        }

        byte[] data = File.ReadAllBytes(host);
        if (data.Length < 0x10)
        {
            return new NpDrmActivation(false, 0, 0);
        }

        ushort version = BinaryPrimitives.ReadUInt16BigEndian(data);
        return new NpDrmActivation(true, version, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)));
    }

    /// <summary>A license time field (ms since 1970, big-endian) as a SceRtcTick; 0 is <paramref name="whenZero"/>.</summary>
    static ulong RifTime(ReadOnlySpan<byte> rif, int offset, ulong whenZero)
    {
        ulong ms = BinaryPrimitives.ReadUInt64BigEndian(rif[offset..]);
        if (ms == 0)
        {
            return whenZero;
        }

        return ms == RtcTickNever ? RtcTickNever : ms * 1000 + RtcTickEpoch1970;
    }

    /// <summary>The firmware's answer to a signature check (checkSign): the check is the secure module's, which the
    /// emulated unit does not run, so it fails as the module reports a failed check: 0x80870005 for a PSP license on
    /// the RSA path, 0x80870004 otherwise.</summary>
    static int RifSignatureCheck(ushort type, ushort flags, bool fullCheck)
    {
        bool rsa = (flags & 0x400) != 0 || flags == 0 || (flags & 0x100) != 0 || fullCheck;
        return rsa && type == 0 ? NPDRM_ERR_BAD_DATA : NPDRM_ERR_BAD_LICENSE;
    }

    /// <summary>The license's checks after its fields were reported: the kind, the unit and game card bindings, and
    /// the activation (the account's license needs activation data; the activated account's must match it).</summary>
    int RifCheck(ReadOnlySpan<byte> rif, uint size)
    {
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(rif[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(rif[6..]);
        byte kind = (byte)flags;
        bool header = type == 0 || size < RifVitaSize;
        if ((kind & 0xFD) != 0 && kind != RifKindFree)
        {
            return NPDRM_ERR_BAD_DATA;
        }

        if ((flags & 0x100) != 0)
        {
            if (header)
            {
                return NPDRM_ERR_BAD_DATA;
            }

            if (!rif.Slice(0xC0, 0x10).SequenceEqual(ConsoleIdentity.OpenPsId))
            {
                return NPDRM_ERR_OTHER_UNIT;
            }
        }

        if ((flags & 0x400) != 0)
        {
            return NPDRM_ERR_GAME_CARD;
        }

        NpDrmActivation activation = ReadActivation();
        if (kind == RifKindAccount)
        {
            return activation.Present ? 0 : NPDRM_ERR_NOT_ACTIVATED;
        }

        if (kind == RifKindActivated)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(rif[8..]) != activation.AccountId)
            {
                return NPDRM_ERR_OTHER_ACCOUNT;
            }

            if (!activation.Present)
            {
                return NPDRM_ERR_NO_ACTIVATION_DATA;
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(rif) > activation.Version)
            {
                return NPDRM_ERR_ACTIVATION_TOO_OLD;
            }
        }

        return 0;
    }

    /// <summary>The derived license flags: bit 0 a full (not trial) license (flags bit 0x2000, or SKU flag 1), bit 1
    /// the flags2 bit 0, bit 16 unit-bound, bit 17 game-card-bound.</summary>
    static uint RifDerivedFlags(ReadOnlySpan<byte> rif, uint skuFlag)
    {
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(rif[6..]);
        uint derived = (flags & 0x2000) != 0 || skuFlag == 1 ? 1u : 0u;
        if ((rif[0x9F] & 1) != 0)
        {
            derived |= 2;
        }

        if ((flags & 0x100) != 0)
        {
            derived |= 0x10000;
        }

        if ((flags & 0x400) != 0)
        {
            derived |= 0x20000;
        }

        return derived;
    }

    /// <summary>The SKU flag: 0 for a PSP license, the +0xFC word of a full PS Vita license.</summary>
    static uint RifSkuFlag(ReadOnlySpan<byte> rif, uint size)
    {
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(rif[4..]);
        if (type == 0 || size < RifVitaSize)
        {
            return 0;
        }

        return BinaryPrimitives.ReadUInt32BigEndian(rif[0xFC..]);
    }

    /// <summary>
    /// ksceNpDrmGetRifInfo (0xDB406EAE)(const SceNpDrmLicense *license, SceSize size, int checkSign,
    /// char contentId[0x30], SceUInt64 *accountId, int *version, int *kind, int *flags, int *skuFlag,
    /// SceRtcTick *start, SceRtcTick *expiration, SceUInt64 *flags2): the license's fields (each output optional),
    /// then its checks. NULL is 0x80870001, less than the 0x98-byte header 0x80870004; the kind is written in every
    /// case after the pointer check.
    /// </summary>
    [Hle(Nid = 0xDB406EAE, Library = "SceNpDrmForDriver")]
    internal int ksceNpDrmGetRifInfo(uint license, uint size, int checkSign, uint contentId, uint accountId, uint version,
        uint kind, uint flags, uint skuFlag, uint start, uint expiration, uint flags2)
    {
        if (license == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        if (size < RifHeaderSize)
        {
            WriteIfSet(kind, 0);
            return NPDRM_ERR_BAD_LICENSE;
        }

        ReadOnlySpan<byte> rif = GuestMemory.ReadSpan(license, (int)RifVitaSize);
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(rif[4..]);
        ushort licenseFlags = BinaryPrimitives.ReadUInt16BigEndian(rif[6..]);
        if (checkSign != 0)
        {
            WriteIfSet(kind, 0);
            return RifSignatureCheck(type, licenseFlags, size >= RifVitaSize && type != 0);
        }

        uint sku = RifSkuFlag(rif, size);
        if (contentId != 0)
        {
            rif.Slice(0x10, 0x30).CopyTo(GuestMemory.Span(contentId, 0x30));
        }

        if (version != 0)
        {
            GuestMemory.W32(version, BinaryPrimitives.ReadUInt16BigEndian(rif));
        }

        if (accountId != 0)
        {
            rif.Slice(8, 8).CopyTo(GuestMemory.Span(accountId, 8));
        }

        WriteIfSet(flags, RifDerivedFlags(rif, sku));
        WriteIfSet(skuFlag, sku);
        if (start != 0)
        {
            GuestMemory.Write(start, RifTime(rif, 0x60, RtcTickYear2001));
        }

        if (expiration != 0)
        {
            GuestMemory.Write(expiration, RifTime(rif, 0x68, RtcTickNever));
        }

        if (flags2 != 0)
        {
            bool header = type == 0 || size < RifVitaSize;
            bool noExpiration = BinaryPrimitives.ReadUInt64BigEndian(rif[0x68..]) == 0;
            bool noFlags2 = header && ((licenseFlags & 0x1000) == 0 || noExpiration || size < RifFlags2Size);
            GuestMemory.Write(flags2, noFlags2 ? 0UL : BinaryPrimitives.ReadUInt64BigEndian(rif[0x98..]) & ~1UL);
        }

        WriteIfSet(kind, (byte)licenseFlags);
        return RifCheck(rif, size);
    }

    static void WriteIfSet(uint address, uint value)
    {
        if (address != 0)
        {
            GuestMemory.W32(address, value);
        }
    }

    /// <summary>The key query shared by GetRifVitaKey and GetRifPspKey: the key buffer zeroed, the license's
    /// metadata outputs (SKU flag, times, derived flags), the checks, then the key. The key is the secure processor's
    /// work (SceSblAuthMgrForDriver decrypts it in SceNpDrm 3.74), so a license that passes the checks gets its
    /// "not supported" 0x80020004 and the key buffer stays zeroed.</summary>
    int RifKey(uint license, uint key, uint flags, uint skuFlag, uint start, uint expiration)
    {
        if (license == 0 || key == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        GuestMemory.Fill(key, 0, 0x10);
        ReadOnlySpan<byte> rif = GuestMemory.ReadSpan(license, (int)RifVitaSize);
        uint sku = RifSkuFlag(rif, RifVitaSize);
        WriteIfSet(skuFlag, sku);
        if (start != 0)
        {
            GuestMemory.Write(start, RifTime(rif, 0x60, RtcTickYear2001));
        }

        if (expiration != 0)
        {
            GuestMemory.Write(expiration, RifTime(rif, 0x68, RtcTickNever));
        }

        WriteIfSet(flags, RifDerivedFlags(rif, sku));
        int check = RifCheck(rif, RifVitaSize);
        if (check < 0)
        {
            return check;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceNpDrmGetRifVitaKey (0x723322B5)(const SceNpDrmLicense *license, void *key 0x10, int *flags,
    /// int *skuFlag, SceRtcTick *start, SceRtcTick *expiration): the title key of a PS Vita license (the secure
    /// processor's: not available, see <see cref="RifKey"/>).</summary>
    [Hle(Nid = 0x723322B5, Library = "SceNpDrmForDriver")]
    int ksceNpDrmGetRifVitaKey(uint license, uint key, uint flags, uint skuFlag, uint start, uint expiration)
    {
        return RifKey(license, key, flags, skuFlag, start, expiration);
    }

    /// <summary>ksceNpDrmGetRifPspKey (0xDACB71F4)(const SceNpDrmLicense *license, void *key 0x10, int *flags,
    /// SceRtcTick *start, SceRtcTick *expiration): the title key of a PSP license (the secure processor's: not
    /// available, see <see cref="RifKey"/>).</summary>
    [Hle(Nid = 0xDACB71F4, Library = "SceNpDrmForDriver")]
    internal int ksceNpDrmGetRifPspKey(uint license, uint key, uint flags, uint start, uint expiration)
    {
        return RifKey(license, key, flags, 0, start, expiration);
    }
}
