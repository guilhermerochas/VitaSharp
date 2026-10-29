using System.IO.Compression;
using VitaSharp.Core.Crypto;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Loader.Self;

internal enum KeyType { METADATA = 0, NPDRM = 1 }
internal enum SceType { SELF = 1, SRVK = 2, SPKG = 3, DEV = 0xC0 }
internal enum SelfType { NONE = 0, KERNEL = 7, APP = 8, BOOT = 9, SECURE = 0xB, USER = 0xD }

internal sealed record KeyEntry(KeyType KeyType, SceType SceType, int KeyRev, string Key, string Iv, ulong MinVer, ulong MaxVer, SelfType SelfType);

internal static partial class SelfKeys
{
    public static KeyEntry? Find(KeyEntry[] set, KeyType kt, SceType st, ulong sysver, int keyrev, SelfType self)
        => set.FirstOrDefault(k => k.KeyType == kt && k.SceType == st && k.SelfType == self
                                   && sysver >= k.MinVer && sysver <= k.MaxVer && k.KeyRev == keyrev);
}

/// <summary>
/// Turns a SCE SELF (signed/encrypted executable, eboot.bin / *.suprx) into a plain ELF image.
/// Handles NPDRM app SELFs (klicensee), system SELFs and already-decrypted fake SELFs.
/// </summary>
public static class SelfDecryptor
{
    const uint SceMagic = 0x00454353;

    public static bool IsSelf(ReadOnlySpan<byte> data) => data.Length >= 4 && U32LE(data, 0) == SceMagic;
    public static bool IsElf(ReadOnlySpan<byte> data) => data.Length >= 4 && U32LE(data, 0) == 0x464C457F;

    /// <summary>
    /// True when <paramref name="header"/> (the start of a file, at least its SCE header) is an NPDRM application
    /// SELF with encrypted segments, i.e. it can only be loaded with the title's klicensee (work.bin / .rif).
    /// Plain ELFs and fake SELFs (homebrew, already decrypted dumps) return false.
    /// </summary>
    public static bool RequiresKlicensee(ReadOnlySpan<byte> header)
    {
        if (!IsSelf(header) || header.Length < 32 + 80) return false;
        const int sh = 32;
        long appinfoOff = (long)U64LE(header, sh + 24), elfOff = (long)U64LE(header, sh + 32);
        long phdrOff = (long)U64LE(header, sh + 40), segInfoOff = (long)U64LE(header, sh + 56);
        if (appinfoOff + 16 > header.Length || elfOff + 52 > header.Length) return false;
        if ((SelfType)U32LE(header, (int)appinfoOff + 12) != SelfType.APP) return false;
        ushort phnum = U16LE(header, (int)elfOff + 44);
        for (int i = 0; i < phnum; i++)
        {
            long s = segInfoOff + i * 32;
            if (s + 32 > header.Length || phdrOff + (i + 1) * 32 > header.Length) return false;
            if (U32LE(header, (int)s + 24) != 2) return true; // not plaintext
        }
        return false;
    }

    /// <summary>Returns a plain ELF. Input may already be an ELF.</summary>
    public static byte[] ToElf(byte[] file, byte[]? klicensee)
    {
        if (IsElf(file)) return file;
        if (!IsSelf(file)) throw new InvalidDataException("Not a SELF/ELF file");

        ReadOnlySpan<byte> f = file;
        int keyRev = f[9];
        var sceType = (SceType)U16LE(f, 10);
        uint metadataOffset = U32LE(f, 12);
        ulong headerLength = U64LE(f, 16);

        const int sh = 32;
        long appinfoOff = (long)U64LE(f, sh + 24);
        long elfOff = (long)U64LE(f, sh + 32);
        long phdrOff = (long)U64LE(f, sh + 40);
        long segInfoOff = (long)U64LE(f, sh + 56);
        long ctrlOff = (long)U64LE(f, sh + 72);

        var selfType = (SelfType)U32LE(f, (int)appinfoOff + 12);
        ulong sysver = U64LE(f, (int)appinfoOff + 16);

        // Control infos: look for the NPDRM block to find the npdrm type.
        int npdrmType = 0;
        {
            int off = (int)ctrlOff;
            for (int guard = 0; guard < 16; guard++)
            {
                uint type = U32LE(f, off), size = U32LE(f, off + 4);
                ulong more = U64LE(f, off + 8);
                if (type == 5) npdrmType = (int)U32LE(f, off + 16 + 8);
                if (more == 0 || size == 0) break;
                off += (int)size;
            }
        }

        // ELF header + phdrs
        var ehdr = f.Slice((int)elfOff, 52);
        ushort phnum = U16LE(ehdr, 44);
        var phdrs = new (uint type, uint offset, uint vaddr, uint filesz, uint memsz, uint flags, uint align)[phnum];
        var segs = new (ulong offset, ulong size, bool compressed, bool plaintext)[phnum];
        bool encrypted = false;
        for (int i = 0; i < phnum; i++)
        {
            var p = f.Slice((int)phdrOff + i * 32, 32);
            phdrs[i] = (U32LE(p, 0), U32LE(p, 4), U32LE(p, 8), U32LE(p, 16), U32LE(p, 20), U32LE(p, 24), U32LE(p, 28));
            var s = f.Slice((int)segInfoOff + i * 32, 32);
            segs[i] = (U64LE(s, 0), U64LE(s, 8), U32LE(s, 16) == 2, U32LE(s, 24) == 2);
            if (!segs[i].plaintext) encrypted = true;
        }

        var segKeys = new Dictionary<int, (byte[] key, byte[] iv)>();
        if (encrypted)
        {
            if (selfType == SelfType.APP && (klicensee == null || IsZero(klicensee)))
                throw new InvalidOperationException("Encrypted application SELF needs a klicensee (license)");
            Exception? last = null;
            foreach (var set in new[] { SelfKeys.Set0, SelfKeys.Set1 })
            {
                try
                {
                    segKeys = DecryptMetadata(f, set, sceType, keyRev, metadataOffset, headerLength, sysver, selfType, npdrmType, klicensee);
                    break;
                }
                catch (Exception e) { last = e; }
            }
            if (segKeys.Count == 0) throw new InvalidDataException("Unable to decrypt SELF metadata", last);
        }

        // Build ELF: header, phdrs, then each segment at its p_offset.
        using var ms = new MemoryStream();
        var outHdr = ehdr.ToArray();
        BitConverter.TryWriteBytes(outHdr.AsSpan(28), 52u);  // e_phoff
        BitConverter.TryWriteBytes(outHdr.AsSpan(32), 0u);   // e_shoff
        BitConverter.TryWriteBytes(outHdr.AsSpan(46), (ushort)0); // e_shentsize
        BitConverter.TryWriteBytes(outHdr.AsSpan(48), (ushort)0); // e_shnum
        BitConverter.TryWriteBytes(outHdr.AsSpan(50), (ushort)0); // e_shstrndx
        ms.Write(outHdr);

        // Recompute offsets: segments are laid out sequentially after the phdr table.
        var bodies = new byte[phnum][];
        for (int i = 0; i < phnum; i++)
        {
            if (phdrs[i].filesz == 0) { bodies[i] = []; continue; }
            var raw = f.Slice((int)segs[i].offset, (int)segs[i].size).ToArray();
            if (!segs[i].plaintext)
            {
                var (key, iv) = segKeys[i];
                using var ctr = new AesUtil.Ctr(key);
                ctr.Transform(raw, iv.ToArray());
            }
            if (segs[i].compressed)
            {
                using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var o = new MemoryStream();
                z.CopyTo(o);
                raw = o.ToArray();
            }
            bodies[i] = raw;
        }

        uint cursor = (uint)(52 + phnum * 32);
        var newOffsets = new uint[phnum];
        for (int i = 0; i < phnum; i++)
        {
            cursor = (cursor + 15u) & ~15u;
            newOffsets[i] = cursor;
            cursor += (uint)bodies[i].Length;
        }
        for (int i = 0; i < phnum; i++)
        {
            var p = new byte[32];
            var ph = phdrs[i];
            BitConverter.TryWriteBytes(p.AsSpan(0), ph.type);
            BitConverter.TryWriteBytes(p.AsSpan(4), newOffsets[i]);
            BitConverter.TryWriteBytes(p.AsSpan(8), ph.vaddr);
            BitConverter.TryWriteBytes(p.AsSpan(12), ph.vaddr);
            BitConverter.TryWriteBytes(p.AsSpan(16), (uint)bodies[i].Length);
            BitConverter.TryWriteBytes(p.AsSpan(20), ph.memsz);
            BitConverter.TryWriteBytes(p.AsSpan(24), ph.flags);
            BitConverter.TryWriteBytes(p.AsSpan(28), ph.align);
            ms.Write(p);
        }
        for (int i = 0; i < phnum; i++)
        {
            while (ms.Length < newOffsets[i]) ms.WriteByte(0);
            ms.Write(bodies[i]);
        }
        return ms.ToArray();
    }

    static Dictionary<int, (byte[] key, byte[] iv)> DecryptMetadata(ReadOnlySpan<byte> f, KeyEntry[] set, SceType sceType, int keyRev,
        uint metadataOffset, ulong headerLength, ulong sysver, SelfType selfType, int npdrmType, byte[]? klic)
    {
        var result = new Dictionary<int, (byte[], byte[])>();
        foreach (var s in DecryptSections(f, set, sceType, keyRev, metadataOffset, headerLength, sysver, selfType, klic))
            if (s.Encryption == 3) result[s.SegmentIndex] = (s.Key, s.Iv);
        return result;
    }

    /// <summary>One entry of a SCE container's decrypted metadata: where a section lives in the file and how to decode
    /// it (encryption 3 = AES-128-CTR with <see cref="Key"/>/<see cref="Iv"/>, compression 2 = zlib).</summary>
    internal readonly record struct MetadataSection(ulong Offset, ulong Size, int SegmentIndex, uint Encryption, uint Compression, byte[] Key, byte[] Iv);

    /// <summary>Decrypts the metadata of any SCE container (SELF, SPKG, SRVK) with the first key set that fits.</summary>
    internal static List<MetadataSection> DecryptSections(ReadOnlySpan<byte> f, SceType sceType, int keyRev, uint metadataOffset,
        ulong headerLength, ulong sysver, SelfType selfType, byte[]? klic)
    {
        Exception? last = null;
        foreach (var set in new[] { SelfKeys.Set0, SelfKeys.Set1 })
        {
            try { return DecryptSections(f, set, sceType, keyRev, metadataOffset, headerLength, sysver, selfType, klic); }
            catch (Exception e) when (e is KeyNotFoundException or InvalidDataException or System.Security.Cryptography.CryptographicException) { last = e; }
        }
        throw new InvalidDataException("Unable to decrypt SCE metadata", last);
    }

    static List<MetadataSection> DecryptSections(ReadOnlySpan<byte> f, KeyEntry[] set, SceType sceType, int keyRev,
        uint metadataOffset, ulong headerLength, ulong sysver, SelfType selfType, byte[]? klic)
    {
        var meta = SelfKeys.Find(set, KeyType.METADATA, sceType, sysver, keyRev, selfType)
                   ?? throw new KeyNotFoundException($"No metadata key for {sceType}/{selfType} rev {keyRev} sysver 0x{sysver:X}");

        int datOff = (int)metadataOffset + 48;
        if ((long)headerLength > f.Length || datOff + 64 > (long)headerLength) throw new InvalidDataException("SCE metadata out of range");
        var dat = f.Slice(datOff, (int)headerLength - datOff).ToArray();
        var info = new byte[64];

        if (selfType == SelfType.APP)
        {
            int kt = keyRev >= 2 ? 1 : 0;
            var np = SelfKeys.Find(set, KeyType.NPDRM, sceType, sysver, kt, selfType)
                     ?? throw new KeyNotFoundException("No NPDRM key");
            var npKey = FromHex(np.Key);
            var npIv = FromHex(np.Iv);
            var predec = new byte[16];
            AesUtil.CbcDecrypt(npKey, npIv, klic!, predec);
            AesUtil.CbcDecrypt(predec, npIv, dat.AsSpan(0, 64), info);
        }
        else dat.AsSpan(0, 64).CopyTo(info);

        var dec = new byte[64];
        AesUtil.CbcDecrypt(FromHex(meta.Key), FromHex(meta.Iv), info, dec);
        if (!IsZero(dec.AsSpan(16, 16)) || !IsZero(dec.AsSpan(48, 16)))
            throw new InvalidDataException("SELF metadata info padding mismatch (wrong key)");
        var mKey = dec.AsSpan(0, 16).ToArray();
        var mIv = dec.AsSpan(32, 16).ToArray();

        int restLen = (dat.Length - 64) & ~15;
        var rest = new byte[restLen];
        AesUtil.CbcDecrypt(mKey, mIv, dat.AsSpan(64, restLen), rest);

        uint sectionCount = U32LE(rest, 12);
        uint keyCount = U32LE(rest, 16);
        if (sectionCount > 4096 || keyCount > 4096 || 32L + sectionCount * 48 + 16L * keyCount > rest.Length)
            throw new InvalidDataException("SCE metadata header out of range (wrong key)");
        int start = 32 + (int)sectionCount * 48;
        var vault = new byte[keyCount][];
        for (int i = 0; i < keyCount; i++) vault[i] = rest.AsSpan(start + 16 * i, 16).ToArray();

        var result = new List<MetadataSection>((int)sectionCount);
        for (int i = 0; i < sectionCount; i++)
        {
            var s = rest.AsSpan(32 + i * 48, 48);
            uint enc = U32LE(s, 32);
            int keyIdx = (int)U32LE(s, 36), ivIdx = (int)U32LE(s, 40);
            byte[] key = [], iv = [];
            if (enc == 3)
            {
                if ((uint)keyIdx >= keyCount || (uint)ivIdx >= keyCount) throw new InvalidDataException("SCE metadata key index out of range");
                key = vault[keyIdx];
                iv = vault[ivIdx];
            }
            result.Add(new MetadataSection(U64LE(s, 0), U64LE(s, 8), (int)U32LE(s, 20), enc, U32LE(s, 44), key, iv));
        }
        return result;
    }
}
