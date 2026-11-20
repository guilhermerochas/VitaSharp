using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The DES entries of SceKernelUtilsForDriver (FW 3.74 SceSysmem, read in the module, study only): keys and blocks are
/// 64-bit values passed in r2:r3 (r1 is the alignment slot), the big-endian number of the eight bytes, as the module's
/// initial permutation takes the high word as the left half; the parity bits are each byte's low bit. The key schedule
/// (NID 0xAE6C45FB) writes the sixteen round keys, 8 bytes each (0x80), and the cipher (0xA0D45D25 forward, 0x801D0ED4
/// with the round keys reversed) returns the block in r0:r1. The parity helpers map each byte through the module's
/// odd-parity table (0x919C12E2) and compare a key with its mapped self (0x227B2E53: 1 when it already has odd parity).
/// The module keeps each round key as two 24-bit words in its own bit order; here they are the standard order (bits
/// 1..24 and 25..48), which only these entries read.
/// </summary>
public sealed partial class SysmemKernel
{
    const int DesScheduleWords = Des.Rounds * 2;

    /// <summary>NID 0xAE6C45FB (void *schedule, SceUInt64 key): the sixteen round keys (0x80 bytes).</summary>
    [Hle(Nid = 0xAE6C45FB, Library = "SceKernelUtilsForDriver")]
    void ksceDesSetKey(uint schedule, uint alignment, uint keyLow, uint keyHigh)
    {
        Span<uint> roundKeys = stackalloc uint[DesScheduleWords];
        Des.Schedule(((ulong)keyHigh << 32) | keyLow, roundKeys);
        for (int word = 0; word < DesScheduleWords; word++)
        {
            GuestMemory.W32(schedule + (uint)word * 4, roundKeys[word]);
        }
    }

    /// <summary>NID 0xA0D45D25 (const void *schedule, SceUInt64 block): the block encrypted.</summary>
    [Hle(Nid = 0xA0D45D25, Library = "SceKernelUtilsForDriver")]
    ulong ksceDesEncrypt(uint schedule, uint alignment, uint blockLow, uint blockHigh) => DesBlock(schedule, blockLow, blockHigh, false);

    /// <summary>NID 0x801D0ED4 (const void *schedule, SceUInt64 block): the block decrypted.</summary>
    [Hle(Nid = 0x801D0ED4, Library = "SceKernelUtilsForDriver")]
    ulong ksceDesDecrypt(uint schedule, uint alignment, uint blockLow, uint blockHigh) => DesBlock(schedule, blockLow, blockHigh, true);

    /// <summary>NID 0x919C12E2 (SceUInt64 key): the key with odd parity in every byte.</summary>
    [Hle(Nid = 0x919C12E2, Library = "SceKernelUtilsForDriver")]
    ulong ksceDesSetOddParity(uint keyLow, uint keyHigh) => Des.SetOddParity(((ulong)keyHigh << 32) | keyLow);

    /// <summary>NID 0x227B2E53 (SceUInt64 key): 1 when every byte has odd parity, else 0.</summary>
    [Hle(Nid = 0x227B2E53, Library = "SceKernelUtilsForDriver")]
    int ksceDesCheckOddParity(uint keyLow, uint keyHigh)
    {
        ulong key = ((ulong)keyHigh << 32) | keyLow;
        return Des.SetOddParity(key) == key ? 1 : 0;
    }

    static ulong DesBlock(uint schedule, uint blockLow, uint blockHigh, bool decrypt)
    {
        Span<uint> roundKeys = stackalloc uint[DesScheduleWords];
        for (int word = 0; word < DesScheduleWords; word++)
        {
            roundKeys[word] = GuestMemory.R32(schedule + (uint)word * 4);
        }

        return Des.Crypt(((ulong)blockHigh << 32) | blockLow, roundKeys, decrypt);
    }
}
