using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The DES entries of SceKernelUtilsForDriver (sysmem 3.74): key schedule, encryption and decryption of a 64-bit block
/// (the big-endian value of its bytes, in r2:r3) against the FIPS 46-3 worked example and NIST SP 800-17 known
/// answers, the weak keys the standard allows, and the odd-parity helpers.
/// </summary>
[Collection("guest")]
public sealed class KernelUtilsDesTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "kdes");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private uint Schedule(ulong key)
    {
        uint schedule = _harness.Alloc(0x80);
        Invoke(Sysmem, "ksceDesSetKey", schedule, 0u, (uint)key, (uint)(key >> 32));
        return schedule;
    }

    private ulong Encrypt(uint schedule, ulong block) => (ulong)Invoke(Sysmem, "ksceDesEncrypt", schedule, 0u, (uint)block, (uint)(block >> 32))!;

    private ulong Decrypt(uint schedule, ulong block) => (ulong)Invoke(Sysmem, "ksceDesDecrypt", schedule, 0u, (uint)block, (uint)(block >> 32))!;

    [Theory]
    [InlineData(0xAE6C45FBu)]
    [InlineData(0xA0D45D25u)]
    [InlineData(0x801D0ED4u)]
    [InlineData(0x919C12E2u)]
    [InlineData(0x227B2E53u)]
    [InlineData(0xAFD56168u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceKernelUtilsForDriver").ToList();
        Assert.Single(bindings);
    }

    [Theory]
    [InlineData(0x133457799BBCDFF1UL, 0x0123456789ABCDEFUL, 0x85E813540F0AB405UL)] // FIPS 46-3 worked example
    [InlineData(0x0123456789ABCDEFUL, 0x4E6F772069732074UL, 0x3FA40E8A984D4815UL)] // "Now is t" (FIPS 81)
    [InlineData(0x0101010101010101UL, 0x8000000000000000UL, 0x95F8A5E5DD31D900UL)] // SP 800-17, a weak key
    [InlineData(0x0101010101010101UL, 0x0000000000000001UL, 0x166B40B44ABA4BD6UL)] // SP 800-17 variable plaintext
    [InlineData(0x8001010101010101UL, 0x0000000000000000UL, 0x95A8D72813DAA94DUL)] // SP 800-17 variable key
    public void BlocksMatchTheStandard(ulong key, ulong plain, ulong cipher)
    {
        uint schedule = Schedule(key);
        Assert.Equal(cipher, Encrypt(schedule, plain));
        Assert.Equal(plain, Decrypt(schedule, cipher));
    }

    [Fact]
    public void TheScheduleIsSixteenRoundKeysAndIgnoresParity()
    {
        uint schedule = Schedule(0x133457799BBCDFF1UL);
        // the first round key of the FIPS example, K1 = 000110 110000 001011 101111 111111 000111 000001 110010
        Assert.Equal(0b000110_110000_001011_101111u, Core.Memory.GuestMemory.R32(schedule));
        Assert.Equal(0b111111_000111_000001_110010u, Core.Memory.GuestMemory.R32(schedule + 4));
        uint flipped = Schedule(0x133457799BBCDFF1UL ^ 0x0101010101010101UL);
        Assert.Equal(0x85E813540F0AB405UL, Encrypt(flipped, 0x0123456789ABCDEFUL));
    }

    [Fact]
    public void TheParityHelpersWorkOnEachByte()
    {
        Assert.Equal(0x0101010101010101UL, (ulong)Invoke(Sysmem, "ksceDesSetOddParity", 0u, 0u)!);
        Assert.Equal(0x0123456789ABCDEFUL, (ulong)Invoke(Sysmem, "ksceDesSetOddParity", 0x89ABCDEEu, 0x01234566u)!);
        Assert.Equal(0xFEFEFEFEFEFEFEFEUL, (ulong)Invoke(Sysmem, "ksceDesSetOddParity", 0xFFFFFFFFu, 0xFFFFFFFFu)!);
        Assert.Equal(1, Call(Sysmem, "ksceDesCheckOddParity", 0x89ABCDEFu, 0x01234567u));
        Assert.Equal(1, Call(Sysmem, "ksceDesCheckOddParity", 0x9BBCDFF1u, 0x13345779u));
        Assert.Equal(0, Call(Sysmem, "ksceDesCheckOddParity", 0x89ABCDEEu, 0x01234567u));
        Assert.Equal(0, Call(Sysmem, "ksceDesCheckOddParity", 0u, 0u));
    }

    [Theory]
    [InlineData(0x0101010101010101UL, 1)]
    [InlineData(0x1F1F1F1F0E0E0E0EUL, 1)]
    [InlineData(0x01FE01FE01FE01FEUL, 1)]
    [InlineData(0xFEE0FEE0FEF1FEF1UL, 1)]
    [InlineData(0x0000000000000000UL, 0)] // the weak key without its parity bits is not in the table
    [InlineData(0x133457799BBCDFF1UL, 0)]
    public void TheWeakKeysAreTheStandardsSixteen(ulong key, int weak)
    {
        Assert.Equal(weak, Call(Sysmem, "ksceDesIsWeakKey", (uint)key, (uint)(key >> 32)));
    }
}
