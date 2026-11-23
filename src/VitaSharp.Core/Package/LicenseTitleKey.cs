using System.Buffers.Binary;

namespace VitaSharp.Core.Package;

/// <summary>
/// The title key of a PS Vita licence as the application manager takes it to mount app0 through ScePfsMgr (FW 3.74
/// SceAppMgr and SceNpDrm, read in the modules, study only): the licence file is read whole (0x200 bytes),
/// ksceNpDrmGetRifInfo checks it with its signature, then ksceNpDrmGetRifVitaKey checks the kind (low byte of the
/// big-endian flags at +6: 0 an account licence, 2 one of the activated account, 0xD a free one), the game card
/// binding and the unit's activation data (tm0:npdrm/act.dat), and gives the key. The signature check and the key
/// itself are the secure processor's: a free licence's key comes out of SceSblAuthMgr's bind-data decryption, an
/// account licence's out of the activation keys the secure processor decrypted. The emulated unit has no secure
/// processor, so that step answers its "not supported" 0x80020004 (<see cref="SecureProcessorUnavailable"/>); the
/// field at +0x50 is never taken as the key here. Only a test stands in for the secure processor
/// (<see cref="Emulator.SecureProcessorRifKey"/>).
/// </summary>
public static class LicenseTitleKey
{
    /// <summary>The secure processor's service is not available (the emulated secure entries' answer).</summary>
    public const int SecureProcessorUnavailable = unchecked((int)0x80020004);

    /// <summary>SceNpDrm: an account licence while the unit has no activation data.</summary>
    public const int NotActivated = unchecked((int)0x80870003);

    /// <summary>SceNpDrm: a licence shorter than a PS Vita licence, or one whose key does not open the content.</summary>
    public const int BadLicense = unchecked((int)0x80870004);

    /// <summary>SceNpDrm: a licence kind the module does not know.</summary>
    public const int BadData = unchecked((int)0x80870005);

    /// <summary>SceNpDrm: a licence of the activated account while no activation data is loaded.</summary>
    public const int NoActivationData = unchecked((int)0x80870006);

    /// <summary>SceNpDrm: a licence of another account than the activated one.</summary>
    public const int OtherAccount = unchecked((int)0x80870008);

    /// <summary>SceNpDrm: a game-card-bound licence the game card authentication does not confirm.</summary>
    public const int GameCard = unchecked((int)0x8087000A);

    /// <summary>The size of a PS Vita licence file.</summary>
    public const int LicenseSize = 0x200;

    private const byte KindAccount = 0, KindActivated = 2, KindFree = 0xD;

    /// <summary>
    /// The title key of <paramref name="license"/> (0 and the key, or SceNpDrm's / the secure processor's error).
    /// <paramref name="activationData"/> is the unit's tm0:npdrm/act.dat (null: none); <paramref name="secureProcessor"/>
    /// verifies the licence and decrypts its key as the console's secure processor does (null on the emulated unit).
    /// </summary>
    public static int Get(ReadOnlySpan<byte> license, ReadOnlySpan<byte> activationData, Func<byte[], byte[]?>? secureProcessor, out byte[]? key)
    {
        key = null;
        if (license.Length < LicenseSize)
        {
            return BadLicense;
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(license[6..]);
        byte kind = (byte)flags;
        if ((kind & 0xFD) != 0 && kind != KindFree)
        {
            return BadData;
        }

        if ((flags & 0x400) != 0)
        {
            return GameCard;
        }

        bool activated = activationData.Length >= 0x10;
        if (kind == KindAccount && !activated)
        {
            return NotActivated;
        }

        if (kind == KindActivated)
        {
            if (!activated)
            {
                return NoActivationData;
            }

            if (BinaryPrimitives.ReadUInt64LittleEndian(license[8..]) != BinaryPrimitives.ReadUInt64LittleEndian(activationData[8..]))
            {
                return OtherAccount;
            }
        }

        if (secureProcessor == null)
        {
            return SecureProcessorUnavailable;
        }

        key = secureProcessor(license[..LicenseSize].ToArray());
        return key is { Length: 16 } ? 0 : SecureProcessorUnavailable;
    }
}
