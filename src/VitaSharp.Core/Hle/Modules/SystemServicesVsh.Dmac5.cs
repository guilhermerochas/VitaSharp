using System.Security.Cryptography;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceSblSsMgrForDriver (os0:kd/ss_mgr.skprx 3.74, read in the module, study only), first part: the unit's identity
// words, the random number generators and the DMAC5 AES operations with a key in memory. DMAC5 is the crypto DMA
// engine: the key is given in bits (64, 128, 192 or 256; anything else 0x800F1516) and the engine runs the cipher
// from src to dst; the "NP" forms load the key through the authenticator first, under a key id: id 0 is the key as
// given, the other ids (0x10000, 0x10001, 0x20000, 0x20001) derive it in the secure processor (0x80020004 here).
// CBC and CTR leave the chaining value (the last cipher block, the next counter) in the IV. The forms with a key slot use a key the
// secure processor put in the engine, which the emulated unit does not have: 0x80020004.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblDmac5: a NULL key, a key size it does not know, a random request over 0x40 bytes.</summary>
    const int DMAC5_ERR_INVALID_ARGUMENT = unchecked((int)0x800F1516);

    /// <summary>SceSblSsMgr's identity queries: a NULL output.</summary>
    const int SSMGR_AIMGR_ERR_INVALID_ARGUMENT = unchecked((int)0x800F0C16);

    /// <summary>SceSblSsMgr: the secure copy of the product code outside product (factory) mode.</summary>
    const int SSMGR_AIMGR_ERR_NOT_PRODUCT_MODE = unchecked((int)0x800F0C29);

    /// <summary>The largest DMAC5 random block.</summary>
    const uint Dmac5MaxRandom = 0x40;

    // ---- the unit's identity

    /// <summary>ksceSblAimgrGetConsoleId (0xFC6CDD68)(SceConsoleId *id): the 16-byte console id.</summary>
    [Hle(Nid = 0xFC6CDD68, Library = "SceSblSsMgrForDriver")]
    int ksceSblAimgrGetConsoleId(uint id)
    {
        return SsMgrCopyOut(id, ConsoleIdentity.ConsoleId);
    }

    /// <summary>ksceSblAimgrGetOpenPsId (0xA5B5D269)(SceOpenPsId *id): the 16-byte OpenPSID.</summary>
    [Hle(Nid = 0xA5B5D269, Library = "SceSblSsMgrForDriver")]
    int ksceSblAimgrGetOpenPsId(uint id)
    {
        return SsMgrCopyOut(id, ConsoleIdentity.OpenPsId);
    }

    /// <summary>ksceSblAimgrGetPscode (0xE0DC2587)(ScePsCode *pscode): company, product, sub code and factory code,
    /// four little-endian halfwords (the boot parameters keep them big-endian).</summary>
    [Hle(Nid = 0xE0DC2587, Library = "SceSblSsMgrForDriver")]
    int ksceSblAimgrGetPscode(uint pscode)
    {
        if (pscode == 0)
        {
            return SSMGR_AIMGR_ERR_INVALID_ARGUMENT;
        }

        GuestMemory.Write(pscode, ConsoleIdentity.CompanyCode);
        GuestMemory.Write(pscode + 2, ConsoleIdentity.ProductCode);
        GuestMemory.Write(pscode + 4, ConsoleIdentity.ProductSubCode);
        GuestMemory.Write(pscode + 6, ConsoleIdentity.FactoryCode);
        return 0;
    }

    /// <summary>ksceSblAimgrGetPscode2 (0x9A9676D0)(ScePsCode *pscode): the secure processor's copy of the product
    /// code, which the module reads only in product (factory) mode: off on a retail unit, 0x800F0C29.</summary>
    [Hle(Nid = 0x9A9676D0, Library = "SceSblSsMgrForDriver")]
    int ksceSblAimgrGetPscode2(uint pscode)
    {
        return pscode == 0 ? SSMGR_AIMGR_ERR_INVALID_ARGUMENT : SSMGR_AIMGR_ERR_NOT_PRODUCT_MODE;
    }

    /// <summary>ksceSblAimgrGetVisibleId (0x04843835)(SceVisibleId *id): the printed id of the unit (0x20 bytes; the
    /// emulated unit's ID storage has none: zeros).</summary>
    [Hle(Nid = 0x04843835, Library = "SceSblSsMgrForDriver", Default = true)]
    int ksceSblAimgrGetVisibleId(uint id)
    {
        return SsMgrCopyOut(id, new byte[0x20]);
    }

    static int SsMgrCopyOut(uint target, ReadOnlySpan<byte> value)
    {
        if (target == 0)
        {
            return SSMGR_AIMGR_ERR_INVALID_ARGUMENT;
        }

        value.CopyTo(GuestMemory.Span(target, value.Length));
        return 0;
    }

    // ---- random numbers and memory

    /// <summary>ksceSblDmac5Rnd (0x4DD1B2E5)(void *buffer, SceSize size, SceBool mask): up to 0x40 random bytes
    /// from the engine (more 0x800F1516).</summary>
    [Hle(Nid = 0x4DD1B2E5, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5Rnd(uint buffer, uint size, int mask)
    {
        if (size > Dmac5MaxRandom)
        {
            return DMAC5_ERR_INVALID_ARGUMENT;
        }

        RandomNumberGenerator.Fill(GuestMemory.Span(buffer, (int)size));
        return 0;
    }

    /// <summary>ksceSblRngGenuineRandomNumber (0xAC57F4F0)(void *dest): 0x40 bytes of the engine's random numbers.</summary>
    [Hle(Nid = 0xAC57F4F0, Library = "SceSblSsMgrForDriver")]
    int ksceSblRngGenuineRandomNumber(uint dest)
    {
        return ksceSblDmac5RndThroughEntry(dest, Dmac5MaxRandom, 1);
    }

    /// <summary>ksceSblRngPseudoRandomNumber (0x4F9BFBE5)(void *result, SceSize size): random bytes of the module's
    /// pseudo random generator, any length. 0.</summary>
    [Hle(Nid = 0x4F9BFBE5, Library = "SceSblSsMgrForDriver")]
    int ksceSblRngPseudoRandomNumber(uint result, uint size)
    {
        RandomNumberGenerator.Fill(GuestMemory.Span(result, (int)size));
        return 0;
    }

    /// <summary>ksceSblSsMemset (0xCD98CC92)(void *dest, int value, SceSize size): a byte fill; returns dest.</summary>
    [Hle(Nid = 0xCD98CC92, Library = "SceSblSsMgrForDriver")]
    uint ksceSblSsMemset(uint dest, int value, uint size)
    {
        if (size != 0)
        {
            GuestMemory.Fill(dest, (byte)value, size);
        }

        return dest;
    }

    // ---- DMAC5 AES with a key in memory

    enum Dmac5Mode
    {
        Ecb,
        Cbc,
        Ctr,
    }

    /// <summary>The engine's checks and the cipher: a key is needed and its size in bits must be one the engine
    /// takes (AES uses 128, 192 or 256 bits; 0x200 selects two 256-bit keys, of which the cipher uses the first).</summary>
    static int Dmac5Aes(uint src, uint dst, uint size, uint key, uint keyBits, uint iv, Dmac5Mode mode, bool encrypt)
    {
        if (key == 0)
        {
            return DMAC5_ERR_INVALID_ARGUMENT;
        }

        int keyBytes = keyBits switch
        {
            0x80 => 16,
            0xC0 => 24,
            0x100 or 0x200 => 32,
            _ => 0,
        };
        if (keyBytes == 0)
        {
            return DMAC5_ERR_INVALID_ARGUMENT;
        }

        using Aes aes = Aes.Create();
        aes.Key = GuestMemory.ReadSpan(key, keyBytes).ToArray();
        int length = (int)size;
        byte[] input = GuestMemory.ReadSpan(src, length).ToArray();
        byte[] output = new byte[length];
        int blocks = length & ~15;
        switch (mode)
        {
            case Dmac5Mode.Ecb:
                Dmac5Ecb(aes, input, output, blocks, encrypt);
                break;
            case Dmac5Mode.Cbc:
                byte[] chain = GuestMemory.ReadSpan(iv, 16).ToArray();
                if (encrypt)
                {
                    aes.EncryptCbc(input.AsSpan(0, blocks), chain, output.AsSpan(0, blocks), PaddingMode.None);
                }
                else
                {
                    aes.DecryptCbc(input.AsSpan(0, blocks), chain, output.AsSpan(0, blocks), PaddingMode.None);
                }

                if (blocks != 0)
                {
                    // the engine leaves the chaining value in the IV: the last cipher block
                    byte[] cipher = encrypt ? output : input;
                    cipher.AsSpan(blocks - 16, 16).CopyTo(GuestMemory.Span(iv, 16));
                }

                break;
            case Dmac5Mode.Ctr:
                byte[] counter = GuestMemory.ReadSpan(iv, 16).ToArray();
                Dmac5Ctr(aes, input, output, counter);
                counter.CopyTo(GuestMemory.Span(iv, 16)); // the next block's counter
                blocks = length;
                break;
        }

        input.AsSpan(blocks).CopyTo(output.AsSpan(blocks)); // a partial last block passes through as the engine leaves it
        output.CopyTo(GuestMemory.Span(dst, length));
        return 0;
    }

    static void Dmac5Ecb(Aes aes, byte[] input, byte[] output, int blocks, bool encrypt)
    {
        if (encrypt)
        {
            aes.EncryptEcb(input.AsSpan(0, blocks), output.AsSpan(0, blocks), PaddingMode.None);
        }
        else
        {
            aes.DecryptEcb(input.AsSpan(0, blocks), output.AsSpan(0, blocks), PaddingMode.None);
        }
    }

    /// <summary>AES-CTR: the 16-byte counter (big-endian) is encrypted and XORed with each block, then incremented.</summary>
    static void Dmac5Ctr(Aes aes, byte[] input, byte[] output, byte[] counter)
    {
        byte[] stream = new byte[16];
        for (int offset = 0; offset < input.Length; offset += 16)
        {
            aes.EncryptEcb(counter, stream, PaddingMode.None);
            int count = Math.Min(16, input.Length - offset);
            for (int i = 0; i < count; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ stream[i]);
            }

            for (int i = 15; i >= 0; i--)
            {
                counter[i]++;
                if (counter[i] != 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>ksceSblDmac5AesEcbEnc (0xC517770D)(src, dst, size, key, keyBits, mask).</summary>
    [Hle(Nid = 0xC517770D, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesEcbEnc(uint src, uint dst, uint size, uint key, uint keyBits, int mask)
    {
        return Dmac5Aes(src, dst, size, key, keyBits, 0, Dmac5Mode.Ecb, encrypt: true);
    }

    /// <summary>ksceSblDmac5AesEcbDec (0x7C978BE7)(src, dst, size, key, keyBits, mask).</summary>
    [Hle(Nid = 0x7C978BE7, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesEcbDec(uint src, uint dst, uint size, uint key, uint keyBits, int mask)
    {
        return Dmac5Aes(src, dst, size, key, keyBits, 0, Dmac5Mode.Ecb, encrypt: false);
    }

    /// <summary>ksceSblDmac5AesEcbEncNP (0x0F7D28AF)(src, dst, size, key, keyBits, keyId, mask).</summary>
    [Hle(Nid = 0x0F7D28AF, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesEcbEncNP(uint src, uint dst, uint size, uint key, uint keyBits, uint keyId, int mask)
    {
        if (keyId != 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        return Dmac5Aes(src, dst, size, key, keyBits, 0, Dmac5Mode.Ecb, encrypt: true);
    }

    /// <summary>ksceSblDmac5AesEcbDecNP (0x197ACF6F)(src, dst, size, key, keyBits, keyId, mask).</summary>
    [Hle(Nid = 0x197ACF6F, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesEcbDecNP(uint src, uint dst, uint size, uint key, uint keyBits, uint keyId, int mask)
    {
        if (keyId != 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        return Dmac5Aes(src, dst, size, key, keyBits, 0, Dmac5Mode.Ecb, encrypt: false);
    }

    /// <summary>ksceSblDmac5AesCbcEnc (0xE6E1AD15)(src, dst, size, key, keyBits, iv, mask).</summary>
    [Hle(Nid = 0xE6E1AD15, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesCbcEnc(uint src, uint dst, uint size, uint key, uint keyBits, uint iv, int mask)
    {
        return Dmac5Aes(src, dst, size, key, keyBits, iv, Dmac5Mode.Cbc, encrypt: true);
    }

    /// <summary>ksceSblDmac5AesCbcDec (0x121FA69F)(src, dst, size, key, keyBits, iv, mask).</summary>
    [Hle(Nid = 0x121FA69F, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesCbcDec(uint src, uint dst, uint size, uint key, uint keyBits, uint iv, int mask)
    {
        return Dmac5Aes(src, dst, size, key, keyBits, iv, Dmac5Mode.Cbc, encrypt: false);
    }

    /// <summary>ksceSblDmac5AesCbcEncNP (0x711C057A)(src, dst, size, key, keyBits, iv, keyId, mask).</summary>
    [Hle(Nid = 0x711C057A, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesCbcEncNP(uint src, uint dst, uint size, uint key, uint keyBits, uint iv, uint keyId, int mask)
    {
        if (keyId != 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        return Dmac5Aes(src, dst, size, key, keyBits, iv, Dmac5Mode.Cbc, encrypt: true);
    }

    /// <summary>ksceSblDmac5AesCbcDecNP (0x1901CB5E)(src, dst, size, key, keyBits, iv, keyId, mask).</summary>
    [Hle(Nid = 0x1901CB5E, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesCbcDecNP(uint src, uint dst, uint size, uint key, uint keyBits, uint iv, uint keyId, int mask)
    {
        if (keyId != 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        return Dmac5Aes(src, dst, size, key, keyBits, iv, Dmac5Mode.Cbc, encrypt: false);
    }

    /// <summary>ksceSblDmac5AesCtrEnc (0x82B5DCEF)(src, dst, size, key, keyBits, iv, mask).</summary>
    [Hle(Nid = 0x82B5DCEF, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesCtrEnc(uint src, uint dst, uint size, uint key, uint keyBits, uint iv, int mask)
    {
        return Dmac5Aes(src, dst, size, key, keyBits, iv, Dmac5Mode.Ctr, encrypt: true);
    }

    /// <summary>ksceSblDmac5AesCtrDec (0x7D46768C)(src, dst, size, key, keyBits, iv, mask): the same keystream.</summary>
    [Hle(Nid = 0x7D46768C, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesCtrDec(uint src, uint dst, uint size, uint key, uint keyBits, uint iv, int mask)
    {
        return Dmac5Aes(src, dst, size, key, keyBits, iv, Dmac5Mode.Ctr, encrypt: false);
    }

    // ---- DMAC5 hashes: one call over the whole input gives the digest. The chaining forms (flags 0x400: return the
    // engine's intermediate state instead of a digest; 0x800: continue from such a state) carry the engine's own
    // register image (SceSblDmac5HashTransformContext), whose meaning the module does not show: not available here
    // (0x80020004).

    /// <summary>The hash flags that ask for a chaining context.</summary>
    const uint Dmac5HashChainFlags = 0x400 | 0x800;

    /// <summary>The HMAC key the hash commands take: 0x20 bytes.</summary>
    const int Dmac5HmacKeySize = 0x20;

    enum Dmac5Hash
    {
        Sha1,
        HmacSha1,
        HmacSha256,
    }

    static int Dmac5Digest(uint src, uint dst, uint length, uint key, uint flags, Dmac5Hash hash)
    {
        if ((flags & Dmac5HashChainFlags) != 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        if (hash != Dmac5Hash.Sha1 && key == 0)
        {
            return DMAC5_ERR_INVALID_ARGUMENT;
        }

        ReadOnlySpan<byte> input = GuestMemory.ReadSpan(src, (int)length);
        byte[] digest = hash switch
        {
            Dmac5Hash.Sha1 => SHA1.HashData(input),
            Dmac5Hash.HmacSha1 => HMACSHA1.HashData(GuestMemory.ReadSpan(key, Dmac5HmacKeySize), input),
            _ => HMACSHA256.HashData(GuestMemory.ReadSpan(key, Dmac5HmacKeySize), input),
        };
        if (dst != 0)
        {
            digest.CopyTo(GuestMemory.Span(dst, digest.Length));
        }

        return 0;
    }

    /// <summary>ksceSblDmac5Sha1 (0xEB3AF9B5)(src, dst 0x14, length, ctx, mask, flags): the SHA-1 of the input.</summary>
    [Hle(Nid = 0xEB3AF9B5, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5Sha1(uint src, uint dst, uint length, uint context, int mask, uint flags)
    {
        return Dmac5Digest(src, dst, length, 0, flags, Dmac5Hash.Sha1);
    }

    /// <summary>ksceSblDmac5Sha1HmacTransform (0x6704D985)(src, dst 0x14, length, key 0x20, ctx, mask, flags): HMAC-SHA1
    /// with the caller's key.</summary>
    [Hle(Nid = 0x6704D985, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5Sha1HmacTransform(uint src, uint dst, uint length, uint key, uint context, int mask, uint flags)
    {
        return Dmac5Digest(src, dst, length, key, flags, Dmac5Hash.HmacSha1);
    }

    /// <summary>ksceSblDmac5Sha1HmacNP (0x92E37656)(src, dst 0x14, length, key 0x20, ctx, keyId, mask, flags):
    /// HMAC-SHA1 with the key loaded under a key id (0: as given; the others are derived by the secure processor).</summary>
    [Hle(Nid = 0x92E37656, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5Sha1HmacNP(uint src, uint dst, uint length, uint key, uint context, uint keyId, int mask, uint flags)
    {
        if (keyId != 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        return Dmac5Digest(src, dst, length, key, flags, Dmac5Hash.HmacSha1);
    }

    /// <summary>ksceSblDmac5Sha256Hmac (0x79F38554)(src, dst 0x20, length, key 0x20, ctx, mask, flags): HMAC-SHA256
    /// with the caller's key.</summary>
    [Hle(Nid = 0x79F38554, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5Sha256Hmac(uint src, uint dst, uint length, uint key, uint context, int mask, uint flags)
    {
        return Dmac5Digest(src, dst, length, key, flags, Dmac5Hash.HmacSha256);
    }

    // ---- DMAC5 with a key slot (the secure processor's keys)

    /// <summary>The key slot forms (AES ECB, DES ECB/CBC, AES-CMAC): the key is one the secure processor loaded into
    /// the engine, which the emulated unit does not have.</summary>
    [Hle("ksceSblDmac5AesEcbEncWithKeyslot", Nid = 0x01BE0374, Library = "SceSblSsMgrForDriver", Default = true)]
    [Hle("ksceSblDmac5AesEcbDecWithKeyslot", Nid = 0x8B4700CB, Library = "SceSblSsMgrForDriver", Default = true)]
    [Hle("ksceSblDmac5DesEcbEncWithKeyslot", Nid = 0x37DD5CBF, Library = "SceSblSsMgrForDriver", Default = true)]
    [Hle("ksceSblDmac5DesEcbDecWithKeyslot", Nid = 0x8EAFB18A, Library = "SceSblSsMgrForDriver", Default = true)]
    [Hle("ksceSblDmac5DesCbcEncWithKeyslot", Nid = 0x05B38698, Library = "SceSblSsMgrForDriver", Default = true)]
    [Hle("ksceSblDmac5DesCbcDecWithKeyslot", Nid = 0x926BCCF0, Library = "SceSblSsMgrForDriver", Default = true)]
    [Hle("ksceSblDmac5AesCmacWithKeyslot", Nid = 0xEA6ACB6D, Library = "SceSblSsMgrForDriver", Default = true)]
    int Dmac5WithKeyslot()
    {
        return KERNEL_ERR_UNSUP;
    }
}
