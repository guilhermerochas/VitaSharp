using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The AES (Rijndael) and AES-CMAC entries of SceKernelUtilsForDriver (FW 3.74 SceSysmem, read in the module, study
/// only).
/// <para>
/// The AES context: +0 its size (0x3D0 with the decryption keys, else 0x1F0), +4 the key words, +8 the block words,
/// +0xC the rounds (the larger of the two plus 6), the encryption round keys at +0x10 and, for Init1, the decryption
/// keys of the equivalent inverse cipher at +0x1F0. Init1, Init2 and Init3 are one function with a mode: 0 both
/// schedules, 1 only encryption, 2 the decryption keys in place of the encryption ones. Block and key sizes are 128, 192
/// or 256 bits (else 0x80020005; a NULL context or key 0x80020006). Encrypt1 and Encrypt2 are one entry (keys at
/// +0x10); Decrypt1 takes the keys at +0x1F0, Decrypt2 those at +0x10; a block size other than 4, 6 or 8 words is -1.
/// </para>
/// <para>
/// The CMAC context (0x2A0): the AES context of Init2, the length (u64) at +0x1F0, the buffered bytes at +0x1F8,
/// "result computed" at +0x1FC, the result at +0x200, the running block at +0x220, the subkeys K1 at +0x240 and K2 at
/// +0x260 (Rb 0x87) and the buffer at +0x280. The update keeps the last whole block buffered for the final step (K1
/// for a whole block, K2 after 0x80 padding).
/// </para>
/// </summary>
public sealed partial class SysmemKernel
{
    const uint AesHeaderSize = 0x10, AesDecryptionKeys = 0x1F0, AesMaxScheduleWords = 120;

    [Hle(Nid = 0xF12B6451, Library = "SceKernelUtilsForDriver")]
    int ksceAesInit1(uint context, uint blockBits, uint keyBits, uint key) => AesInit(context, blockBits, keyBits, key, 0);

    [Hle(Nid = 0xEDA97D6D, Library = "SceKernelUtilsForDriver")]
    int ksceAesInit2(uint context, uint blockBits, uint keyBits, uint key) => AesInit(context, blockBits, keyBits, key, 1);

    [Hle(Nid = 0x72408E29, Library = "SceKernelUtilsForDriver")]
    int ksceAesInit3(uint context, uint blockBits, uint keyBits, uint key) => AesInit(context, blockBits, keyBits, key, 2);

    [Hle(Nid = 0xC2A61770, Library = "SceKernelUtilsForDriver")]
    int ksceAesEncrypt1(uint context, uint source, uint destination) => AesBlock(context, source, destination, AesHeaderSize, true);

    [Hle(Nid = 0x302947B6, Library = "SceKernelUtilsForDriver")]
    int ksceAesEncrypt2(uint context, uint source, uint destination) => AesBlock(context, source, destination, AesHeaderSize, true);

    [Hle(Nid = 0xD8678061, Library = "SceKernelUtilsForDriver")]
    int ksceAesDecrypt1(uint context, uint source, uint destination) => AesBlock(context, source, destination, AesDecryptionKeys, false);

    [Hle(Nid = 0xE39CD272, Library = "SceKernelUtilsForDriver")]
    int ksceAesDecrypt2(uint context, uint source, uint destination) => AesBlock(context, source, destination, AesHeaderSize, false);

    /// <summary>NID 0x60ED6EA9 (word): the S-box on each byte.</summary>
    [Hle(Nid = 0x60ED6EA9, Library = "SceKernelUtilsForDriver")]
    uint ksceAesSubWord(uint word) => Rijndael.SubWord(word);

    /// <summary>NID 0xC76A7685 (word): InvMixColumns of one column.</summary>
    [Hle(Nid = 0xC76A7685, Library = "SceKernelUtilsForDriver")]
    uint ksceAesInverseMixColumn(uint word) => Rijndael.InverseMixColumn(word);

    static int AesInit(uint context, uint blockBits, uint keyBits, uint key, int mode)
    {
        if (context == 0 || key == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        if (!AesSize(blockBits) || !AesSize(keyBits))
        {
            return UTILS_ERR_INVALID_ARGUMENT;
        }

        int blockWords = (int)(blockBits >> 5);
        int keyWords = (int)(keyBits >> 5);
        int rounds = Math.Max(blockWords, keyWords) + 6;
        GuestMemory.W32(context, mode == 0 ? 0x3D0u : 0x1F0u);
        GuestMemory.W32(context + 4, (uint)keyWords);
        GuestMemory.W32(context + 8, (uint)blockWords);
        GuestMemory.W32(context + 0xC, (uint)rounds);
        int words = (rounds + 1) * blockWords;
        Span<uint> schedule = stackalloc uint[(int)AesMaxScheduleWords];
        Rijndael.ExpandKey(GuestMemory.ReadSpan(key, keyWords * 4), keyWords, blockWords, rounds, schedule);
        Span<uint> inverse = stackalloc uint[(int)AesMaxScheduleWords];
        Rijndael.InverseSchedule(schedule, blockWords, rounds, inverse);
        WriteWords(context + AesHeaderSize, mode == 2 ? inverse[..words] : schedule[..words]);
        if (mode == 0)
        {
            WriteWords(context + AesDecryptionKeys, inverse[..words]);
        }

        return 0;
    }

    static bool AesSize(uint bits)
    {
        return (bits & ~0x40u) == 0x80 || bits == 0x100;
    }

    static void WriteWords(uint address, ReadOnlySpan<uint> words)
    {
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(address + (uint)(i * 4), words[i]);
        }
    }

    static int AesBlock(uint context, uint source, uint destination, uint keys, bool encrypt)
    {
        int blockWords = (int)GuestMemory.R32(context + 8);
        if (blockWords is not (4 or 6 or 8))
        {
            return -1;
        }

        int rounds = (int)GuestMemory.R32(context + 0xC);
        Span<uint> schedule = stackalloc uint[(int)AesMaxScheduleWords];
        int words = Math.Min((rounds + 1) * blockWords, (int)AesMaxScheduleWords);
        for (int i = 0; i < words; i++)
        {
            schedule[i] = GuestMemory.R32(context + keys + (uint)(i * 4));
        }

        Span<byte> output = stackalloc byte[32];
        ReadOnlySpan<byte> input = GuestMemory.ReadSpan(source, blockWords * 4);
        if (encrypt)
        {
            Rijndael.Encrypt(schedule, blockWords, rounds, input, output);
        }
        else
        {
            Rijndael.Decrypt(schedule, blockWords, rounds, input, output);
        }

        output[..(blockWords * 4)].CopyTo(GuestMemory.Span(destination, blockWords * 4));
        return 0;
    }

    // ------------------------------------------------------------------ AES-CMAC

    const uint CmacSize = 0x2A0, CmacLength = 0x1F0, CmacBuffered = 0x1F8, CmacDone = 0x1FC, CmacResult = 0x200;
    const uint CmacState = 0x220, CmacFirstKey = 0x240, CmacSecondKey = 0x260, CmacBuffer = 0x280;

    /// <summary>NID 0x0C39954E (context, block bits, key bits, key): the context cleared, Init2, the subkeys.</summary>
    [Hle(Nid = 0x0C39954E, Library = "SceKernelUtilsForDriver")]
    int ksceAesCmacInit(uint context, uint blockBits, uint keyBits, uint key)
    {
        if (context == 0 || key == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        GuestMemory.Fill(context, 0, CmacSize);
        int result = AesInit(context, blockBits, keyBits, key, 1);
        if (result < 0)
        {
            return result;
        }

        int size = (int)GuestMemory.R32(context + 8) * 4;
        Span<byte> zero = stackalloc byte[32];
        zero.Clear();
        Span<byte> encrypted = stackalloc byte[32];
        zero[..size].CopyTo(GuestMemory.Span(context + CmacState, size));
        AesBlock(context, context + CmacState, context + CmacState, AesHeaderSize, true);
        GuestMemory.ReadSpan(context + CmacState, size).CopyTo(encrypted);
        GuestMemory.Fill(context + CmacState, 0, (uint)size);
        Span<byte> first = GuestMemory.Span(context + CmacFirstKey, size);
        Span<byte> second = GuestMemory.Span(context + CmacSecondKey, size);
        ShiftSubkey(encrypted[..size], first);
        ShiftSubkey(first, second);
        return 0;
    }

    /// <summary>The CMAC subkey step: the block shifted left one bit, 0x87 into the last byte when the top bit was
    /// set.</summary>
    static void ShiftSubkey(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int size = input.Length;
        for (int i = 0; i < size - 1; i++)
        {
            output[i] = (byte)((input[i] << 1) | (input[i + 1] >> 7));
        }

        output[size - 1] = (byte)(input[size - 1] << 1);
        if ((input[0] & 0x80) != 0)
        {
            output[size - 1] ^= 0x87;
        }
    }

    /// <summary>NID 0xBB6E35CD (context, data, size): the whole blocks before the last one enciphered.</summary>
    [Hle(Nid = 0xBB6E35CD, Library = "SceKernelUtilsForDriver")]
    int ksceAesCmacUpdate(uint context, uint data, uint size)
    {
        if (context == 0 || data == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        uint block = GuestMemory.R32(context + 8) * 4;
        GuestMemory.Write(context + CmacLength, GuestMemory.Read<ulong>(context + CmacLength) + size);
        uint buffered = GuestMemory.R32(context + CmacBuffered);
        while (size > 0)
        {
            if (buffered == block)
            {
                Span<byte> state = GuestMemory.Span(context + CmacState, (int)block);
                ReadOnlySpan<byte> pending = GuestMemory.ReadSpan(context + CmacBuffer, (int)block);
                for (int i = 0; i < block; i++)
                {
                    state[i] ^= pending[i];
                }

                AesBlock(context, context + CmacState, context + CmacState, AesHeaderSize, true);
                buffered = 0;
            }

            uint take = Math.Min(block - buffered, size);
            GuestMemory.Span(data, (int)take).CopyTo(GuestMemory.Span(context + CmacBuffer + buffered, (int)take));
            buffered += take;
            data += take;
            size -= take;
        }

        GuestMemory.W32(context + CmacBuffered, buffered);
        GuestMemory.W32(context + CmacDone, 0);
        return 0;
    }

    /// <summary>NID 0x222986EF (context, mac): the final block (K1 when whole, else padded with K2), kept; 0.</summary>
    [Hle(Nid = 0x222986EF, Library = "SceKernelUtilsForDriver")]
    int ksceAesCmacFinal(uint context, uint mac)
    {
        if (context == 0 || mac == 0)
        {
            return UTILS_ERR_ILLEGAL_ADDR;
        }

        int block = (int)GuestMemory.R32(context + 8) * 4;
        if (GuestMemory.R32(context + CmacDone) == 0)
        {
            Span<byte> state = GuestMemory.Span(context + CmacState, block);
            int buffered = (int)GuestMemory.R32(context + CmacBuffered);
            ReadOnlySpan<byte> pending = GuestMemory.ReadSpan(context + CmacBuffer, block);
            bool whole = buffered == block && GuestMemory.Read<ulong>(context + CmacLength) != 0;
            ReadOnlySpan<byte> subkey = GuestMemory.ReadSpan(context + (whole ? CmacFirstKey : CmacSecondKey), block);
            for (int i = 0; i < block; i++)
            {
                byte message = i < buffered ? pending[i] : (byte)0;
                if (!whole && i == buffered)
                {
                    message = 0x80;
                }

                state[i] ^= (byte)(message ^ subkey[i]);
            }

            AesBlock(context, context + CmacState, context + CmacResult, AesHeaderSize, true);
            GuestMemory.W32(context + CmacDone, 1);
        }

        GuestMemory.ReadSpan(context + CmacResult, block).CopyTo(GuestMemory.Span(mac, block));
        return 0;
    }

    /// <summary>NID 0x1DF3792A (block bits, key bits, key, data, size, mac): Init, Update and Final on a stack
    /// context; 0 or the first failure.</summary>
    [Hle(Nid = 0x1DF3792A, Library = "SceKernelUtilsForDriver")]
    int ksceAesCmac(uint blockBits, uint keyBits, uint key, uint data, uint size, uint mac)
    {
        uint context = Mem.Alloc(CmacSize, "SceAesCmac", MemoryRegion.Kernel, 0x10);
        try
        {
            int result = ksceAesCmacInitThroughEntry(context, blockBits, keyBits, key);
            if (result >= 0)
            {
                result = ksceAesCmacUpdateThroughEntry(context, data, size);
            }

            if (result >= 0)
            {
                result = ksceAesCmacFinalThroughEntry(context, mac);
            }

            return Math.Min(result, 0);
        }
        finally
        {
            Mem.Free(context);
        }
    }
}
