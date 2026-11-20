namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// DES (FIPS 46-3) on 64-bit values: a key or block is the big-endian number of its eight bytes (bit 1 of the standard
/// is the value's bit 63), the parity bits are each byte's low bit. Written from the standard's tables; the key schedule
/// is the sixteen 48-bit round keys, each kept as two 24-bit words (bits 1..24 and 25..48).
/// </summary>
internal static class Des
{
    public const int Rounds = 16;

    static readonly byte[] s_initialPermutation =
    [
        58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4,
        62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
        57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3,
        61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7,
    ];

    static readonly byte[] s_finalPermutation =
    [
        40, 8, 48, 16, 56, 24, 64, 32, 39, 7, 47, 15, 55, 23, 63, 31,
        38, 6, 46, 14, 54, 22, 62, 30, 37, 5, 45, 13, 53, 21, 61, 29,
        36, 4, 44, 12, 52, 20, 60, 28, 35, 3, 43, 11, 51, 19, 59, 27,
        34, 2, 42, 10, 50, 18, 58, 26, 33, 1, 41, 9, 49, 17, 57, 25,
    ];

    static readonly byte[] s_expansion =
    [
        32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11, 12, 13, 12, 13, 14, 15, 16, 17,
        16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25, 24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1,
    ];

    static readonly byte[] s_permutation =
    [
        16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10,
        2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25,
    ];

    static readonly byte[] s_choice1 =
    [
        57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36,
        63, 55, 47, 39, 31, 23, 15, 7, 62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 28, 20, 12, 4,
    ];

    static readonly byte[] s_choice2 =
    [
        14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4, 26, 8, 16, 7, 27, 20, 13, 2,
        41, 52, 31, 37, 47, 55, 30, 40, 51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32,
    ];

    static readonly byte[] s_shifts = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];

    static readonly byte[,] s_boxes =
    {
        {
            14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7, 0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
            4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0, 15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13,
        },
        {
            15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10, 3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
            0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15, 13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9,
        },
        {
            10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8, 13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
            13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7, 1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12,
        },
        {
            7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15, 13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
            10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4, 3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14,
        },
        {
            2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9, 14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
            4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14, 11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3,
        },
        {
            12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11, 10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
            9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6, 4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13,
        },
        {
            4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1, 13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
            1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2, 6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12,
        },
        {
            13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7, 1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
            7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8, 2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11,
        },
    };

    /// <summary>The bits of <paramref name="value"/> (<paramref name="width"/> bits wide, bit 1 the highest) picked by
    /// <paramref name="table"/>, the first entry the result's highest bit.</summary>
    static ulong Permute(ulong value, int width, ReadOnlySpan<byte> table)
    {
        ulong result = 0;
        foreach (byte position in table)
        {
            result = (result << 1) | ((value >> (width - position)) & 1);
        }

        return result;
    }

    /// <summary>The sixteen 48-bit round keys of <paramref name="key"/>, each as (bits 1..24, bits 25..48).</summary>
    public static void Schedule(ulong key, Span<uint> roundKeys)
    {
        ulong chosen = Permute(key, 64, s_choice1);
        uint left = (uint)(chosen >> 28) & 0x0FFFFFFF;
        uint right = (uint)chosen & 0x0FFFFFFF;
        for (int round = 0; round < Rounds; round++)
        {
            int shift = s_shifts[round];
            left = ((left << shift) | (left >> (28 - shift))) & 0x0FFFFFFF;
            right = ((right << shift) | (right >> (28 - shift))) & 0x0FFFFFFF;
            ulong roundKey = Permute(((ulong)left << 28) | right, 56, s_choice2);
            roundKeys[round * 2] = (uint)(roundKey >> 24) & 0xFFFFFF;
            roundKeys[round * 2 + 1] = (uint)roundKey & 0xFFFFFF;
        }
    }

    /// <summary>The cipher function f(R, K).</summary>
    static uint Feistel(uint half, ulong roundKey)
    {
        ulong mixed = Permute(half, 32, s_expansion) ^ roundKey;
        uint substituted = 0;
        for (int box = 0; box < 8; box++)
        {
            int six = (int)(mixed >> (42 - box * 6)) & 0x3F;
            int row = ((six >> 4) & 2) | (six & 1);
            int column = (six >> 1) & 0xF;
            substituted = (substituted << 4) | s_boxes[box, row * 16 + column];
        }

        return (uint)Permute(substituted, 32, s_permutation);
    }

    /// <summary>One block through the sixteen rounds, with the round keys in order (encryption) or reversed
    /// (decryption).</summary>
    public static ulong Crypt(ulong block, ReadOnlySpan<uint> roundKeys, bool decrypt)
    {
        ulong permuted = Permute(block, 64, s_initialPermutation);
        uint left = (uint)(permuted >> 32);
        uint right = (uint)permuted;
        for (int round = 0; round < Rounds; round++)
        {
            int index = decrypt ? Rounds - 1 - round : round;
            ulong roundKey = ((ulong)(roundKeys[index * 2] & 0xFFFFFF) << 24) | (roundKeys[index * 2 + 1] & 0xFFFFFF);
            uint next = left ^ Feistel(right, roundKey);
            left = right;
            right = next;
        }

        return Permute(((ulong)right << 32) | left, 64, s_finalPermutation);
    }

    /// <summary>The four weak and twelve semi-weak keys of FIPS 74 (with odd parity).</summary>
    static readonly ulong[] s_weakKeys =
    [
        0x0101010101010101, 0xFEFEFEFEFEFEFEFE, 0x1F1F1F1F0E0E0E0E, 0xE0E0E0E0F1F1F1F1,
        0x01FE01FE01FE01FE, 0xFE01FE01FE01FE01, 0x1FE01FE00EF10EF1, 0xE01FE01FF10EF10E,
        0x01E001E001F101F1, 0xE001E001F101F101, 0x1FFE1FFE0EFE0EFE, 0xFE1FFE1FFE0EFE0E,
        0x011F011F010E010E, 0x1F011F010E010E01, 0xE0FEE0FEF1FEF1FE, 0xFEE0FEE0FEF1FEF1,
    ];

    /// <summary>Whether <paramref name="key"/> is one of the weak or semi-weak keys, as written (parity included).</summary>
    public static bool IsWeak(ulong key) => Array.IndexOf(s_weakKeys, key) >= 0;

    /// <summary>Each byte's low bit set so the byte has odd parity.</summary>
    public static ulong SetOddParity(ulong key)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 8)
        {
            uint high = (uint)(key >> shift) & 0xFE;
            uint parity = (uint)System.Numerics.BitOperations.PopCount(high) & 1;
            result |= (ulong)(high | (parity ^ 1)) << shift;
        }

        return result;
    }
}
