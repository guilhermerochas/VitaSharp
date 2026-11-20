using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceKernelUtilsForDriver (FW 3.74 SceSysmem, read in the module, study only): the hashes, the Mersenne twister and
/// the deflate decoder the kernel modules share. The xorshift128 entries keep Marsaglia's four words (an all-zero
/// state is set to ones first).
/// <para>
/// SHA-1 context (in guest memory): h[5] at +0, the buffered byte count (u16) at +0x14, "result computed" (u16) at
/// +0x16, the total length in bytes (u64) at +0x18, the 64-byte buffer at +0x20 and the digest bytes at +0x60. The
/// SHA-224/256 context: h[8] at +0, the total length at +0x28, the buffer at +0x30 whose last byte, +0x6F, holds the
/// buffered count with bit 7 set once the result is computed, and the result state words at +0x70. A NULL context or
/// data is 0x80020006. The result is computed on a copy of the state (the stream can go on after it) and kept until the
/// next update. The digests are Init, Update, Result on a stack context; the HMACs (key, key length, data, length,
/// digest; NULL key, data or digest 0x80020006) hash a key longer than the 64-byte block first.
/// </para>
/// <para>
/// Mersenne twister context: the index at +0, the 624 state words at +4. Init seeds them (MT19937's linear generator)
/// and draws 624 numbers; each draw tempers the word at the index and twists it in place, which gives MT19937's
/// sequence. The kernel keeps a global one, seeded at boot with switch word 3; its fill entry (buffer and size
/// word-aligned, else 0x80020005) writes draws of it.
/// </para>
/// <para>
/// Deflate: the raw decoder (dst, dst size, src, *end of the stream) returns the bytes written, 0x80560100 for bad data
/// and 0x80560101 when the output does not fit. Zlib: the header (CMF, FLG) must be a multiple of 31 (else 0x80020001);
/// a preset dictionary (FLG bit 5) adds a big-endian id; decompression needs method 8 and no dictionary (else
/// 0x80020001) and returns the big-endian Adler-32 after the stream. Gzip: magic 1F 8B (else 0x80020001; NULL
/// 0x80020006); the optional fields by the flags byte at +3: FEXTRA (the firmware reports +10, the length field, and
/// skips the length from there), FNAME, FCOMMENT (NUL-terminated), FHCRC (a little-endian halfword); decompression
/// needs method 8 and a trailer length equal to the bytes written (else 0x80020001) and returns the trailer CRC-32.
/// </para>
/// </summary>
public sealed partial class SysmemKernel
{
    const int UTILS_ERR_ILLEGAL_ADDR = unchecked((int)0x80020006);
    const int UTILS_ERR_INVALID_ARGUMENT = unchecked((int)0x80020005);
    const int UTILS_ERR_FORMAT = unchecked((int)0x80020001);

    const uint MtWords = 624;

    /// <summary>The kernel's global twister: the index and the 624 state words (0 words: not seeded yet).</summary>
    [SnapshotMissing] // states of builds before it: seeded on first use
    readonly uint[] _globalTwister = new uint[MtWords + 1];

    [SnapshotMissing] // states of builds before it: seeded on first use
    bool _globalTwisterSeeded;

    // ------------------------------------------------------------------ SHA-1

    [Hle(Nid = 0xE4390FFA, Library = "SceKernelUtilsForDriver")]
    int ksceSha1BlockInit(uint context)
    {
        if (context == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        for (int i = 0; i < 5; i++)
        {
            GuestMemory.W32(context + (uint)(i * 4), ShaBlocks.Sha1Initial[i]);
        }

        GuestMemory.Write(context + 0x14, (ushort)0);
        GuestMemory.Write(context + 0x16, (ushort)0);
        GuestMemory.Write(context + 0x18, 0UL);
        return 0;
    }

    [Hle(Nid = 0x478A6F3C, Library = "SceKernelUtilsForDriver")]
    int ksceSha1BlockUpdate(uint context, uint data, uint size)
    {
        if (context == 0 || data == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        Span<uint> state = stackalloc uint[5];
        ReadState(context, state);
        uint buffered = GuestMemory.Read<ushort>(context + 0x14);
        uint left = ShaUpdate(state, context + 0x20, buffered, data, size, ShaBlocks.Sha1);
        WriteState(context, state);
        GuestMemory.Write(context + 0x18, GuestMemory.Read<ulong>(context + 0x18) + size);
        GuestMemory.Write(context + 0x14, (ushort)left);
        GuestMemory.Write(context + 0x16, (ushort)0);
        return 0;
    }

    [Hle(Nid = 0x48F24106, Library = "SceKernelUtilsForDriver")]
    int ksceSha1BlockResult(uint context, uint digest)
    {
        if (context == 0 || digest == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        if (GuestMemory.Read<ushort>(context + 0x16) == 0)
        {
            Span<uint> state = stackalloc uint[5];
            ReadState(context, state);
            int buffered = GuestMemory.Read<ushort>(context + 0x14) & 0x3F;
            ShaBlocks.Finish(state, GuestMemory.ReadSpan(context + 0x20, buffered), GuestMemory.Read<ulong>(context + 0x18), ShaBlocks.Sha1);
            for (int i = 0; i < 5; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(GuestMemory.Span(context + 0x60 + (uint)(i * 4), 4), state[i]);
            }

            GuestMemory.Write(context + 0x16, (ushort)1);
        }

        GuestMemory.Span(context + 0x60, 20).CopyTo(GuestMemory.Span(digest, 20));
        return 0;
    }

    /// <summary>ksceSha1Digest(const void *data, size, digest): Init, Update and Result on a stack context (the
    /// update's failure is not checked: NULL data gives the digest of nothing; NULL digest 0x80020006).</summary>
    [Hle(Nid = 0x87DC7F2F, Library = "SceKernelUtilsForDriver")]
    int ksceSha1Digest(uint data, uint size, uint digest)
    {
        if (digest == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        ReadOnlySpan<byte> message = data == 0 ? ReadOnlySpan<byte>.Empty : GuestMemory.ReadSpan(data, (int)size);
        HashHost(message, ReadOnlySpan<byte>.Empty, ShaBlocks.Sha1Initial, ShaBlocks.Sha1, 20, GuestMemory.Span(digest, 20));
        return 0;
    }

    // ------------------------------------------------------------------ SHA-224 and SHA-256

    [Hle(Nid = 0xC762EA6D, Library = "SceKernelUtilsForDriver")]
    int ksceSha224BlockInit(uint context) => Sha2Init(context, ShaBlocks.Sha224Initial);

    [Hle(Nid = 0x7556E611, Library = "SceKernelUtilsForDriver")]
    int ksceSha224BlockUpdate(uint context, uint data, uint size) => Sha2Update(context, data, size);

    [Hle(Nid = 0xFBF2A442, Library = "SceKernelUtilsForDriver")]
    int ksceSha224BlockResult(uint context, uint digest) => Sha2Result(context, digest, 28);

    [Hle(Nid = 0x9EA9D4DC, Library = "SceKernelUtilsForDriver")]
    int ksceSha224Digest(uint data, uint size, uint digest) => Sha2Digest(data, size, digest, ShaBlocks.Sha224Initial, 28);

    [Hle(Nid = 0xD909FA2C, Library = "SceKernelUtilsForDriver")]
    int ksceSha256BlockInit(uint context) => Sha2Init(context, ShaBlocks.Sha256Initial);

    [Hle(Nid = 0x236A9097, Library = "SceKernelUtilsForDriver")]
    int ksceSha256BlockUpdate(uint context, uint data, uint size) => Sha2Update(context, data, size);

    [Hle(Nid = 0x4899CD4B, Library = "SceKernelUtilsForDriver")]
    int ksceSha256BlockResult(uint context, uint digest) => Sha2Result(context, digest, 32);

    [Hle(Nid = 0xA773A6A8, Library = "SceKernelUtilsForDriver")]
    int ksceSha256Digest(uint data, uint size, uint digest) => Sha2Digest(data, size, digest, ShaBlocks.Sha256Initial, 32);

    int Sha2Init(uint context, ReadOnlySpan<uint> initial)
    {
        if (context == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        for (int i = 0; i < 8; i++)
        {
            GuestMemory.W32(context + (uint)(i * 4), initial[i]);
        }

        GuestMemory.Write(context + 0x28, 0UL);
        GuestMemory.Write(context + 0x6F, (byte)0);
        return 0;
    }

    int Sha2Update(uint context, uint data, uint size)
    {
        if (context == 0 || data == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        Span<uint> state = stackalloc uint[8];
        ReadState(context, state);
        GuestMemory.Write(context + 0x28, GuestMemory.Read<ulong>(context + 0x28) + size);
        uint buffered = GuestMemory.Read<byte>(context + 0x6F) & 0x3Fu;
        uint left = ShaUpdate(state, context + 0x30, buffered, data, size, ShaBlocks.Sha256);
        WriteState(context, state);
        GuestMemory.Write(context + 0x6F, (byte)left);
        return 0;
    }

    int Sha2Result(uint context, uint digest, int digestSize)
    {
        if (context == 0 || digest == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        byte count = GuestMemory.Read<byte>(context + 0x6F);
        if (count < 0x40)
        {
            Span<uint> state = stackalloc uint[8];
            ReadState(context, state);
            ShaBlocks.Finish(state, GuestMemory.ReadSpan(context + 0x30, count), GuestMemory.Read<ulong>(context + 0x28), ShaBlocks.Sha256);
            for (int i = 0; i < 8; i++)
            {
                GuestMemory.W32(context + 0x70 + (uint)(i * 4), state[i]);
            }

            GuestMemory.Write(context + 0x6F, (byte)(count + 0x80));
        }

        for (int i = 0; i < digestSize / 4; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(GuestMemory.Span(digest + (uint)(i * 4), 4), GuestMemory.R32(context + 0x70 + (uint)(i * 4)));
        }

        return 0;
    }

    static int Sha2Digest(uint data, uint size, uint digest, ReadOnlySpan<uint> initial, int digestSize)
    {
        if (data == 0 || digest == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        HashHost(GuestMemory.ReadSpan(data, (int)size), ReadOnlySpan<byte>.Empty, initial, ShaBlocks.Sha256, digestSize, GuestMemory.Span(digest, digestSize));
        return 0;
    }

    static void ReadState(uint context, Span<uint> state)
    {
        for (int i = 0; i < state.Length; i++)
        {
            state[i] = GuestMemory.R32(context + (uint)(i * 4));
        }
    }

    static void WriteState(uint context, Span<uint> state)
    {
        for (int i = 0; i < state.Length; i++)
        {
            GuestMemory.W32(context + (uint)(i * 4), state[i]);
        }
    }

    /// <summary>Feeds <paramref name="size"/> bytes into a state whose partial block is at <paramref name="buffer"/>;
    /// the bytes left buffered.</summary>
    static uint ShaUpdate(Span<uint> state, uint buffer, uint buffered, uint data, uint size, ShaBlocks.BlockFunction compress)
    {
        Span<byte> block = stackalloc byte[64];
        while (size > 0)
        {
            uint take = Math.Min(64 - buffered, size);
            GuestMemory.Span(data, (int)take).CopyTo(GuestMemory.Span(buffer + buffered, (int)take));
            buffered += take;
            data += take;
            size -= take;
            if (buffered == 64)
            {
                GuestMemory.ReadSpan(buffer, 64).CopyTo(block);
                compress(state, block);
                buffered = 0;
            }
        }

        return buffered;
    }

    // ------------------------------------------------------------------ HMAC

    [Hle(Nid = 0x29A28957, Library = "SceKernelUtilsForDriver")]
    int ksceHmacSha1Digest(uint key, uint keySize, uint data, uint size, uint digest)
        => Hmac(key, keySize, data, size, digest, 20, ShaBlocks.Sha1Initial, ShaBlocks.Sha1);

    [Hle(Nid = 0x7F2A7B99, Library = "SceKernelUtilsForDriver")]
    int ksceHmacSha224Digest(uint key, uint keySize, uint data, uint size, uint digest)
        => Hmac(key, keySize, data, size, digest, 28, ShaBlocks.Sha224Initial, ShaBlocks.Sha256);

    [Hle(Nid = 0x83EFA1CC, Library = "SceKernelUtilsForDriver")]
    int ksceHmacSha256Digest(uint key, uint keySize, uint data, uint size, uint digest)
        => Hmac(key, keySize, data, size, digest, 32, ShaBlocks.Sha256Initial, ShaBlocks.Sha256);

    static int Hmac(uint key, uint keySize, uint data, uint size, uint digest, int digestSize, ReadOnlySpan<uint> initial, ShaBlocks.BlockFunction compress)
    {
        if (key == 0 || data == 0 || digest == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        Span<byte> pad = stackalloc byte[64];
        pad.Clear();
        if (keySize > 64)
        {
            HashHost(GuestMemory.ReadSpan(key, (int)keySize), ReadOnlySpan<byte>.Empty, initial, compress, digestSize, pad);
        }
        else
        {
            GuestMemory.ReadSpan(key, (int)keySize).CopyTo(pad);
        }

        Span<byte> inner = stackalloc byte[32];
        for (int i = 0; i < 64; i++)
        {
            pad[i] ^= 0x36;
        }

        HashHost(pad, GuestMemory.ReadSpan(data, (int)size), initial, compress, digestSize, inner);
        for (int i = 0; i < 64; i++)
        {
            pad[i] ^= 0x36 ^ 0x5C;
        }

        Span<byte> outer = stackalloc byte[32];
        HashHost(pad, inner[..digestSize], initial, compress, digestSize, outer);
        outer[..digestSize].CopyTo(GuestMemory.Span(digest, digestSize));
        return 0;
    }

    /// <summary>The digest of <paramref name="first"/> followed by <paramref name="second"/>.</summary>
    static void HashHost(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<uint> initial, ShaBlocks.BlockFunction compress, int digestSize, Span<byte> output)
    {
        Span<uint> state = stackalloc uint[initial.Length];
        initial.CopyTo(state);
        byte[] message = new byte[first.Length + second.Length];
        first.CopyTo(message);
        second.CopyTo(message.AsSpan(first.Length));
        int whole = message.Length & ~63;
        for (int offset = 0; offset < whole; offset += 64)
        {
            compress(state, message.AsSpan(offset, 64));
        }

        ShaBlocks.Finish(state, message.AsSpan(whole), (ulong)message.Length, compress);
        for (int i = 0; i < digestSize / 4; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(output[(i * 4)..], state[i]);
        }
    }

    // ------------------------------------------------------------------ Mersenne twister

    /// <summary>ksceMt19937Init(SceMt19937Context *context, uint32_t seed): seeded and 624 numbers drawn; 0.</summary>
    [Hle(Nid = 0x4C9A5730, Library = "SceKernelUtilsForDriver")]
    int ksceMt19937Init(uint context, uint seed)
    {
        Span<uint> twister = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(GuestMemory.Span(context, (int)(MtWords + 1) * 4));
        TwisterSeed(twister, seed);
        return 0;
    }

    /// <summary>ksceMt19937UInt(SceMt19937Context *context): the next number.</summary>
    [Hle(Nid = 0x92AEDFBC, Library = "SceKernelUtilsForDriver")]
    uint ksceMt19937UInt(uint context)
    {
        return TwisterNext(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(GuestMemory.Span(context, (int)(MtWords + 1) * 4)));
    }

    /// <summary>NID 0xD428CC2A (seed): the global twister seeded; 0.</summary>
    [Hle(Nid = 0xD428CC2A, Library = "SceKernelUtilsForDriver")]
    int ksceMt19937GlobalInit(uint seed)
    {
        TwisterSeed(_globalTwister, seed);
        _globalTwisterSeeded = true;
        return 0;
    }

    /// <summary>NID 0x875B2A1C (uint32_t *buffer, size): the words of the buffer drawn from the global twister; an
    /// unaligned buffer or size 0x80020005.</summary>
    [Hle(Nid = 0x875B2A1C, Library = "SceKernelUtilsForDriver")]
    int ksceMt19937GlobalFill(uint buffer, uint size)
    {
        if ((buffer & 3) != 0 || (size & 3) != 0)
        {
            return UTILS_ERR_INVALID_ARGUMENT;
        }

        if (!_globalTwisterSeeded)
        {
            ksceMt19937GlobalInit(Emu.Hle.Modules.OfType<SystemServicesVsh>().First().DipswWord(3));
        }

        for (uint offset = 0; offset < size; offset += 4)
        {
            GuestMemory.W32(buffer + offset, TwisterNext(_globalTwister));
        }

        return 0;
    }

    static void TwisterSeed(Span<uint> twister, uint seed)
    {
        twister[1] = seed;
        for (uint i = 1; i < MtWords; i++)
        {
            uint previous = twister[(int)i];
            twister[(int)i + 1] = (previous ^ (previous >> 30)) * 0x6C078965 + i;
        }

        twister[0] = 0;
        for (uint i = 0; i < MtWords; i++)
        {
            TwisterNext(twister);
        }
    }

    /// <summary>Tempers the word at the index and twists it in place (the firmware's incremental MT19937).</summary>
    static uint TwisterNext(Span<uint> twister)
    {
        uint index = twister[0];
        uint next = index == MtWords - 1 ? 0 : index + 1;
        uint far = index < MtWords - 397 ? index + 397 : index + 397 - MtWords;
        uint current = twister[(int)index + 1];
        uint following = twister[(int)next + 1];
        uint mixed = (current & 0x80000000) | (following & 0x7FFFFFFF);
        uint twisted = twister[(int)far + 1] ^ (mixed >> 1);
        if ((following & 1) != 0)
        {
            twisted ^= 0x9908B0DF;
        }

        twister[0] = next;
        twister[(int)index + 1] = twisted;
        uint value = current ^ (current >> 11);
        value ^= (value << 7) & 0x9D2C5680;
        value ^= (value << 15) & 0xEFC60000;
        return value ^ (value >> 18);
    }

    // ------------------------------------------------------------------ xorshift128

    /// <summary>NID 0xB55C69B7 (uint32_t state[4]): an all-zero state set to ones; 0.</summary>
    [Hle(Nid = 0xB55C69B7, Library = "SceKernelUtilsForDriver")]
    int ksceXorshift128Init(uint state)
    {
        bool zero = true;
        for (uint i = 0; i < 4; i++)
        {
            zero &= GuestMemory.R32(state + i * 4) == 0;
        }

        if (zero)
        {
            for (uint i = 0; i < 4; i++)
            {
                GuestMemory.W32(state + i * 4, 1);
            }
        }

        return 0;
    }

    /// <summary>NID 0x335AF34D (uint32_t state[4]): Marsaglia's xorshift128 step; the new word.</summary>
    [Hle(Nid = 0x335AF34D, Library = "SceKernelUtilsForDriver")]
    uint ksceXorshift128Next(uint state)
    {
        uint x = GuestMemory.R32(state);
        uint w = GuestMemory.R32(state + 12);
        x ^= x << 11;
        uint next = w ^ (w >> 19) ^ x ^ (x >> 8);
        GuestMemory.W32(state, GuestMemory.R32(state + 4));
        GuestMemory.W32(state + 4, GuestMemory.R32(state + 8));
        GuestMemory.W32(state + 8, w);
        GuestMemory.W32(state + 12, next);
        return next;
    }

    // ------------------------------------------------------------------ SFMT19937

    /// <summary>The SFMT context words: the index, then the 624 state words (156 blocks of 128 bits).</summary>
    const int SfmtContextWords = (int)MtWords + 1;

    static Span<uint> SfmtContext(uint context)
    {
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(GuestMemory.Span(context, SfmtContextWords * 4));
    }

    /// <summary>ksceSfmt19937InitGenRand(SceSfmt19937Context *context, uint32_t seed): MT19937's linear seeding of
    /// the 624 words, the period certified; 0.</summary>
    [Hle(Nid = 0xAB48C3CA, Library = "SceKernelUtilsForDriver")]
    int ksceSfmt19937InitGenRand(uint context, uint seed)
    {
        Span<uint> sfmt = SfmtContext(context);
        sfmt[0] = MtWords;
        sfmt[1] = seed;
        for (uint i = 1; i < MtWords; i++)
        {
            uint previous = sfmt[(int)i];
            sfmt[(int)i + 1] = (previous ^ (previous >> 30)) * 0x6C078965 + i;
        }

        Sfmt.CertifyPeriod(sfmt[1..]);
        return 0;
    }

    /// <summary>ksceSfmt19937InitByArray(SceSfmt19937Context *context, uint32_t *key, int length): the array seeding
    /// of SFMT (a NULL key adds nothing, not even its length); 0.</summary>
    [Hle(Nid = 0x33AE1203, Library = "SceKernelUtilsForDriver")]
    int ksceSfmt19937InitByArray(uint context, uint key, int length)
    {
        Span<uint> sfmt = SfmtContext(context);
        sfmt[0] = MtWords;
        Sfmt.SeedByArray(sfmt[1..], key, length);
        return 0;
    }

    /// <summary>ksceSfmt19937GenRand32(SceSfmt19937Context *context): the next word (the state regenerated after
    /// 624).</summary>
    [Hle(Nid = 0xBBE4701A, Library = "SceKernelUtilsForDriver")]
    uint ksceSfmt19937GenRand32(uint context)
    {
        Span<uint> sfmt = SfmtContext(context);
        if ((int)sfmt[0] >= (int)MtWords)
        {
            Sfmt.Generate(sfmt[1..], Span<uint>.Empty);
            sfmt[0] = 0;
        }

        uint value = sfmt[(int)sfmt[0] + 1];
        sfmt[0]++;
        return value;
    }

    /// <summary>ksceSfmt19937GenRand64(SceSfmt19937Context *context): the next two words; at an odd index the
    /// firmware returns that word with a zero high word and steps one.</summary>
    [Hle(Nid = 0x45DEAAD6, Library = "SceKernelUtilsForDriver")]
    ulong ksceSfmt19937GenRand64(uint context)
    {
        Span<uint> sfmt = SfmtContext(context);
        uint index = sfmt[0];
        if ((int)index >= (int)MtWords)
        {
            Sfmt.Generate(sfmt[1..], Span<uint>.Empty);
            sfmt[0] = 2;
            return sfmt[1] | ((ulong)sfmt[2] << 32);
        }

        sfmt[0] = index + 2 - (index & 1);
        uint high = (index & 1) == 0 ? sfmt[(int)index + 2] : 0;
        return sfmt[(int)index + 1] | ((ulong)high << 32);
    }

    /// <summary>ksceSfmt19937FillArray32(SceSfmt19937Context *context, uint32_t *array, int count): count a multiple
    /// of 4 and at least 624, the context fresh (index 624), else -1; the array filled with the next words.</summary>
    [Hle(Nid = 0x2B30548B, Library = "SceKernelUtilsForDriver")]
    int ksceSfmt19937FillArray32(uint context, uint array, uint count)
    {
        return SfmtFill(context, array, count, (count & 3) == 0);
    }

    /// <summary>ksceSfmt19937FillArray64(SceSfmt19937Context *context, uint64_t *array, int count): count even and at
    /// least 312, the context fresh, else -1.</summary>
    [Hle(Nid = 0x49B41540, Library = "SceKernelUtilsForDriver")]
    int ksceSfmt19937FillArray64(uint context, uint array, uint count)
    {
        return SfmtFill(context, array, count * 2, (count & 1) == 0);
    }

    static int SfmtFill(uint context, uint array, uint words, bool aligned)
    {
        Span<uint> sfmt = SfmtContext(context);
        if (!aligned || sfmt[0] != MtWords || words < MtWords)
        {
            return -1;
        }

        Span<uint> output = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(GuestMemory.Span(array, (int)words * 4));
        Sfmt.Generate(sfmt[1..], output);
        return 0;
    }

    // ------------------------------------------------------------------ deflate, zlib, gzip

    /// <summary>ksceDeflateDecompress(void *dst, size, const void *src, const void **next): the bytes written.</summary>
    [Hle(Nid = 0x8AF1FAD4, Library = "SceKernelUtilsForDriver")]
    int ksceDeflateDecompress(uint destination, uint size, uint source, uint next)
    {
        int result = GuestInflater.Inflate(destination, size, source, out uint end);
        if (result >= 0 && next != 0)
        {
            GuestMemory.W32(next, end);
        }

        return result;
    }

    /// <summary>ksceZlibGetInfo(const void *src, uint8_t *cmf, uint8_t *flg, uint32_t *dictId, const void **data).</summary>
    [Hle(Nid = 0x5B9BCD75, Library = "SceKernelUtilsForDriver")]
    int ksceZlibGetInfo(uint source, uint cmf, uint flg, uint dictionaryId, uint data)
    {
        byte method = GuestMemory.Read<byte>(source);
        byte flags = GuestMemory.Read<byte>(source + 1);
        if ((method * 256u + flags) % 31 != 0)
        {
            return UTILS_ERR_FORMAT;
        }

        if (cmf != 0)
        {
            GuestMemory.Write(cmf, method);
        }

        if (flg != 0)
        {
            GuestMemory.Write(flg, flags);
        }

        uint start = source + 2;
        if ((flags & 0x20) != 0)
        {
            if (dictionaryId != 0)
            {
                GuestMemory.W32(dictionaryId, BigEndianWord(source + 2));
            }

            start = source + 6;
        }

        if (data != 0)
        {
            GuestMemory.W32(data, start);
        }

        return 0;
    }

    /// <summary>ksceZlibGetCompressedData(const void *src): the deflate stream, NULL for a bad header.</summary>
    [Hle(Nid = 0x01EB6C45, Library = "SceKernelUtilsForDriver")]
    uint ksceZlibGetCompressedData(uint source)
    {
        byte method = GuestMemory.Read<byte>(source);
        byte flags = GuestMemory.Read<byte>(source + 1);
        if ((method * 256u + flags) % 31 != 0)
        {
            return 0;
        }

        return (flags & 0x20) != 0 ? source + 6 : source + 2;
    }

    /// <summary>ksceZlibDecompress(void *dst, size, const void *src, uint32_t *adler32): method 8 without a preset
    /// dictionary (else 0x80020001); the bytes written and the stream's Adler-32.</summary>
    [Hle(Nid = 0x900148DB, Library = "SceKernelUtilsForDriver")]
    int ksceZlibDecompress(uint destination, uint size, uint source, uint adler)
    {
        byte method = GuestMemory.Read<byte>(source);
        byte flags = GuestMemory.Read<byte>(source + 1);
        if ((method * 256u + flags) % 31 != 0)
        {
            return UTILS_ERR_FORMAT;
        }

        if ((method & 0xF) != 8 || (flags & 0x20) != 0)
        {
            return UTILS_ERR_FORMAT;
        }

        int result = GuestInflater.Inflate(destination, size, source + 2, out uint end);
        if (result >= 0 && adler != 0)
        {
            GuestMemory.W32(adler, BigEndianWord(end));
        }

        return result;
    }

    /// <summary>ksceGzipIsValid(const void *src): 1 when it starts with the gzip magic.</summary>
    [Hle(Nid = 0xD8FAEFD4, Library = "SceKernelUtilsForDriver")]
    int ksceGzipIsValid(uint source)
    {
        return GuestMemory.Read<ushort>(source) == 0x8B1F ? 1 : 0;
    }

    /// <summary>ksceGzipGetInfo(const void *src, const void **extra, const char **name, const char **comment,
    /// uint16_t *crc, const void **data): the outputs cleared, then filled from the header's flags.</summary>
    [Hle(Nid = 0xFFC6A10F, Library = "SceKernelUtilsForDriver")]
    int ksceGzipGetInfo(uint source, uint extra, uint name, uint comment, uint crc, uint data)
    {
        int result = GzipParse(source, out GzipHeader header);
        if (extra != 0)
        {
            GuestMemory.W32(extra, header.Extra);
        }

        if (name != 0)
        {
            GuestMemory.W32(name, header.Name);
        }

        if (comment != 0)
        {
            GuestMemory.W32(comment, header.Comment);
        }

        if (crc != 0)
        {
            GuestMemory.Write(crc, header.Crc);
        }

        if (data != 0)
        {
            GuestMemory.W32(data, header.Data);
        }

        return result;
    }

    /// <summary>The pointers of a gzip header (0 when absent or the header is bad).</summary>
    struct GzipHeader
    {
        public uint Extra;
        public uint Name;
        public uint Comment;
        public ushort Crc;
        public uint Data;
    }

    static int GzipParse(uint source, out GzipHeader header)
    {
        header = default;
        if (source == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        if (GuestMemory.Read<ushort>(source) != 0x8B1F)
        {
            return UTILS_ERR_FORMAT;
        }

        byte flags = GuestMemory.Read<byte>(source + 3);
        uint cursor = source + 10;
        if ((flags & 4) != 0)
        {
            // the firmware reports the length field itself and skips the length from it (not from the field's end)
            header.Extra = cursor;
            cursor += GuestMemory.Read<ushort>(source + 10);
        }

        if ((flags & 8) != 0)
        {
            header.Name = cursor;
            cursor = PastNul(cursor);
        }

        if ((flags & 0x10) != 0)
        {
            header.Comment = cursor;
            cursor = PastNul(cursor);
        }

        if ((flags & 2) != 0)
        {
            header.Crc = GuestMemory.Read<ushort>(cursor);
            cursor += 2;
        }

        header.Data = cursor;
        return 0;
    }

    /// <summary>ksceGzipGetName(const void *src): the file name, NULL without one or for a bad header.</summary>
    [Hle(Nid = 0xF901FD3E, Library = "SceKernelUtilsForDriver")]
    uint ksceGzipGetName(uint source) => GzipParse(source, out GzipHeader header) < 0 ? 0 : header.Name;

    /// <summary>ksceGzipGetComment(const void *src): the comment, NULL without one or for a bad header.</summary>
    [Hle(Nid = 0xCD3AE08F, Library = "SceKernelUtilsForDriver")]
    uint ksceGzipGetComment(uint source) => GzipParse(source, out GzipHeader header) < 0 ? 0 : header.Comment;

    /// <summary>ksceGzipGetCompressedData(const void *src): the deflate stream, NULL for a bad header.</summary>
    [Hle(Nid = 0x63619124, Library = "SceKernelUtilsForDriver")]
    uint ksceGzipGetCompressedData(uint source) => GzipParse(source, out GzipHeader header) < 0 ? 0 : header.Data;

    /// <summary>ksceGzipDecompress(void *dst, size, const void *src, uint32_t *crc32): method 8 and the trailer's
    /// length equal to the bytes written (else 0x80020001); the bytes written and the trailer's CRC-32.</summary>
    [Hle(Nid = 0x367EE3DF, Library = "SceKernelUtilsForDriver")]
    int ksceGzipDecompress(uint destination, uint size, uint source, uint crc)
    {
        int parsed = GzipParse(source, out GzipHeader header);
        if (parsed < 0)
        {
            return parsed;
        }

        if (GuestMemory.Read<byte>(source + 2) != 8)
        {
            return UTILS_ERR_FORMAT;
        }

        int result = GuestInflater.Inflate(destination, size, header.Data, out uint end);
        if (result < 0)
        {
            return result;
        }

        if (GuestMemory.Read<uint>(end + 4) != (uint)result)
        {
            return UTILS_ERR_FORMAT;
        }

        if (crc != 0)
        {
            GuestMemory.W32(crc, GuestMemory.Read<uint>(end));
        }

        return result;
    }

    static uint PastNul(uint cursor)
    {
        while (GuestMemory.Read<byte>(cursor) != 0)
        {
            cursor++;
        }

        return cursor + 1;
    }

    static uint BigEndianWord(uint address)
    {
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(GuestMemory.ReadSpan(address, 4));
    }
}
