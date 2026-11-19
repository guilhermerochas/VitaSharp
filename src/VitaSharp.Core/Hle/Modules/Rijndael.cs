namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Rijndael (FIPS 197 for the 128-bit block; the 192- and 256-bit blocks of the original proposal, which SceKernelUtils
/// also takes) on round keys kept as little-endian words, the first byte of a column in the low byte, as the firmware
/// keeps them: the key expansion, the cipher and the equivalent inverse cipher (decryption keys reversed, the middle
/// ones through InvMixColumns).
/// </summary>
internal static class Rijndael
{
    private static readonly byte[] s_sbox = new byte[256];
    private static readonly byte[] s_inverseSbox = new byte[256];

    static Rijndael()
    {
        byte p = 1;
        byte q = 1;
        do
        {
            p = (byte)(p ^ (p << 1) ^ ((p & 0x80) != 0 ? 0x1B : 0));
            q ^= (byte)(q << 1);
            q ^= (byte)(q << 2);
            q ^= (byte)(q << 4);
            if ((q & 0x80) != 0)
            {
                q ^= 0x09;
            }

            byte value = (byte)(q ^ byte.RotateLeft(q, 1) ^ byte.RotateLeft(q, 2) ^ byte.RotateLeft(q, 3) ^ byte.RotateLeft(q, 4) ^ 0x63);
            s_sbox[p] = value;
        }
        while (p != 1);

        s_sbox[0] = 0x63;
        for (int i = 0; i < 256; i++)
        {
            s_inverseSbox[s_sbox[i]] = (byte)i;
        }
    }

    /// <summary>The S-box on each byte of a word.</summary>
    public static uint SubWord(uint word)
    {
        return s_sbox[word & 0xFF] | ((uint)s_sbox[(word >> 8) & 0xFF] << 8) | ((uint)s_sbox[(word >> 16) & 0xFF] << 16)
            | ((uint)s_sbox[word >> 24] << 24);
    }

    private static byte Times2(byte value)
    {
        return (byte)((value << 1) ^ ((value & 0x80) != 0 ? 0x1B : 0));
    }

    private static byte Multiply(byte value, int factor)
    {
        byte result = 0;
        while (factor != 0)
        {
            if ((factor & 1) != 0)
            {
                result ^= value;
            }

            value = Times2(value);
            factor >>= 1;
        }

        return result;
    }

    /// <summary>InvMixColumns of one column word.</summary>
    public static uint InverseMixColumn(uint word)
    {
        byte a0 = (byte)word;
        byte a1 = (byte)(word >> 8);
        byte a2 = (byte)(word >> 16);
        byte a3 = (byte)(word >> 24);
        uint b0 = (uint)(Multiply(a0, 14) ^ Multiply(a1, 11) ^ Multiply(a2, 13) ^ Multiply(a3, 9));
        uint b1 = (uint)(Multiply(a0, 9) ^ Multiply(a1, 14) ^ Multiply(a2, 11) ^ Multiply(a3, 13));
        uint b2 = (uint)(Multiply(a0, 13) ^ Multiply(a1, 9) ^ Multiply(a2, 14) ^ Multiply(a3, 11));
        uint b3 = (uint)(Multiply(a0, 11) ^ Multiply(a1, 13) ^ Multiply(a2, 9) ^ Multiply(a3, 14));
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    /// <summary>The (rounds + 1) * blockWords round key words of a key of <paramref name="keyWords"/> words.</summary>
    public static void ExpandKey(ReadOnlySpan<byte> key, int keyWords, int blockWords, int rounds, Span<uint> schedule)
    {
        int total = (rounds + 1) * blockWords;
        for (int i = 0; i < keyWords; i++)
        {
            schedule[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        }

        byte roundConstant = 1;
        for (int i = keyWords; i < total; i++)
        {
            uint previous = schedule[i - 1];
            if (i % keyWords == 0)
            {
                previous = SubWord(uint.RotateRight(previous, 8)) ^ roundConstant;
                roundConstant = Times2(roundConstant);
            }
            else if (keyWords > 6 && i % keyWords == 4)
            {
                previous = SubWord(previous);
            }

            schedule[i] = schedule[i - keyWords] ^ previous;
        }
    }

    /// <summary>The decryption keys of the equivalent inverse cipher from the encryption keys.</summary>
    public static void InverseSchedule(ReadOnlySpan<uint> encryption, int blockWords, int rounds, Span<uint> decryption)
    {
        for (int round = 0; round <= rounds; round++)
        {
            for (int column = 0; column < blockWords; column++)
            {
                uint word = encryption[(rounds - round) * blockWords + column];
                decryption[round * blockWords + column] = round == 0 || round == rounds ? word : InverseMixColumn(word);
            }
        }
    }

    private static int Shift(int row, int blockWords)
    {
        return blockWords == 8 && row >= 2 ? row + 1 : row;
    }

    /// <summary>One block (4 * <paramref name="blockWords"/> bytes) through the cipher.</summary>
    public static void Encrypt(ReadOnlySpan<uint> schedule, int blockWords, int rounds, ReadOnlySpan<byte> input, Span<byte> output)
    {
        int size = blockWords * 4;
        Span<byte> state = stackalloc byte[32];
        Span<byte> shifted = stackalloc byte[32];
        input[..size].CopyTo(state);
        AddRoundKey(state, schedule, 0, blockWords);
        for (int round = 1; round <= rounds; round++)
        {
            for (int i = 0; i < size; i++)
            {
                state[i] = s_sbox[state[i]];
            }

            for (int column = 0; column < blockWords; column++)
            {
                for (int row = 0; row < 4; row++)
                {
                    shifted[column * 4 + row] = state[((column + Shift(row, blockWords)) % blockWords) * 4 + row];
                }
            }

            shifted[..size].CopyTo(state);
            if (round != rounds)
            {
                for (int column = 0; column < blockWords; column++)
                {
                    Span<byte> cell = state.Slice(column * 4, 4);
                    byte a0 = cell[0];
                    byte a1 = cell[1];
                    byte a2 = cell[2];
                    byte a3 = cell[3];
                    cell[0] = (byte)(Times2(a0) ^ Times2(a1) ^ a1 ^ a2 ^ a3);
                    cell[1] = (byte)(a0 ^ Times2(a1) ^ Times2(a2) ^ a2 ^ a3);
                    cell[2] = (byte)(a0 ^ a1 ^ Times2(a2) ^ Times2(a3) ^ a3);
                    cell[3] = (byte)(Times2(a0) ^ a0 ^ a1 ^ a2 ^ Times2(a3));
                }
            }

            AddRoundKey(state, schedule, round, blockWords);
        }

        state[..size].CopyTo(output);
    }

    /// <summary>One block through the equivalent inverse cipher with decryption keys.</summary>
    public static void Decrypt(ReadOnlySpan<uint> schedule, int blockWords, int rounds, ReadOnlySpan<byte> input, Span<byte> output)
    {
        int size = blockWords * 4;
        Span<byte> state = stackalloc byte[32];
        Span<byte> shifted = stackalloc byte[32];
        input[..size].CopyTo(state);
        AddRoundKey(state, schedule, 0, blockWords);
        for (int round = 1; round <= rounds; round++)
        {
            for (int i = 0; i < size; i++)
            {
                state[i] = s_inverseSbox[state[i]];
            }

            for (int column = 0; column < blockWords; column++)
            {
                for (int row = 0; row < 4; row++)
                {
                    shifted[((column + Shift(row, blockWords)) % blockWords) * 4 + row] = state[column * 4 + row];
                }
            }

            shifted[..size].CopyTo(state);
            if (round != rounds)
            {
                for (int column = 0; column < blockWords; column++)
                {
                    uint word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(state[(column * 4)..]);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(state[(column * 4)..], InverseMixColumn(word));
                }
            }

            AddRoundKey(state, schedule, round, blockWords);
        }

        state[..size].CopyTo(output);
    }

    private static void AddRoundKey(Span<byte> state, ReadOnlySpan<uint> schedule, int round, int blockWords)
    {
        for (int column = 0; column < blockWords; column++)
        {
            uint key = schedule[round * blockWords + column];
            Span<byte> cell = state.Slice(column * 4, 4);
            cell[0] ^= (byte)key;
            cell[1] ^= (byte)(key >> 8);
            cell[2] ^= (byte)(key >> 16);
            cell[3] ^= (byte)(key >> 24);
        }
    }
}
