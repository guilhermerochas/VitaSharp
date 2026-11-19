using System.Security.Cryptography;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The AES entries of SceKernelUtilsForDriver (sysmem.skprx 3.74): the FIPS 197 vectors, the three init modes (both
/// schedules, encryption only, decryption keys in place) and which keys each decrypt entry takes, the 192- and 256-bit
/// Rijndael blocks, the size and address checks, and AES-CMAC with the RFC 4493 vectors by block and in one call.
/// </summary>
[Collection("guest")]
public sealed class KernelUtilsAesTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalAddress = unchecked((int)0x80020006), Invalid = unchecked((int)0x80020005);

    private readonly HleHarness _harness = new(fixture, "kaes");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private uint Put(byte[] bytes)
    {
        uint address = _harness.Alloc((uint)Math.Max(bytes.Length, 1) + 16);
        bytes.CopyTo(GuestMemory.Span(address, bytes.Length));
        return address;
    }

    private static byte[] Sequence(int length) => Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData(128, "69c4e0d86a7b0430d8cdb78070b4c55a")]
    [InlineData(192, "dda97ca4864cdfe06eaf70a0ec0d7191")]
    [InlineData(256, "8ea2b7ca516745bfeafc49904b496089")]
    public void TheCipherMatchesFips197(int keyBits, string expected)
    {
        uint context = _harness.Alloc(0x3D0);
        uint key = Put(Sequence(keyBits / 8));
        uint plain = Put(Convert.FromHexString("00112233445566778899aabbccddeeff"));
        uint cipher = _harness.Alloc(16);
        uint back = _harness.Alloc(16);
        Assert.Equal(0, Call(Sysmem, "ksceAesInit1", context, 128u, (uint)keyBits, key));
        Assert.Equal(0x3D0u, GuestMemory.R32(context));
        Assert.Equal((uint)keyBits / 32 + 6 > 10 ? (uint)keyBits / 32 + 6 : 10u, GuestMemory.R32(context + 0xC));
        Assert.Equal(0, Call(Sysmem, "ksceAesEncrypt1", context, plain, cipher));
        Assert.Equal(expected, Convert.ToHexStringLower(GuestMemory.ReadSpan(cipher, 16)));
        Assert.Equal(0, Call(Sysmem, "ksceAesDecrypt1", context, cipher, back));
        Assert.Equal("00112233445566778899aabbccddeeff", Convert.ToHexStringLower(GuestMemory.ReadSpan(back, 16)));

        // Init2 keeps only the encryption keys; Init3 puts the decryption keys where Decrypt2 takes them
        uint encryptOnly = _harness.Alloc(0x1F0);
        Assert.Equal(0, Call(Sysmem, "ksceAesInit2", encryptOnly, 128u, (uint)keyBits, key));
        Assert.Equal(0x1F0u, GuestMemory.R32(encryptOnly));
        Assert.Equal(0, Call(Sysmem, "ksceAesEncrypt2", encryptOnly, plain, back));
        Assert.Equal(expected, Convert.ToHexStringLower(GuestMemory.ReadSpan(back, 16)));
        uint decryptOnly = _harness.Alloc(0x1F0);
        Assert.Equal(0, Call(Sysmem, "ksceAesInit3", decryptOnly, 128u, (uint)keyBits, key));
        Assert.Equal(0, Call(Sysmem, "ksceAesDecrypt2", decryptOnly, cipher, back));
        Assert.Equal("00112233445566778899aabbccddeeff", Convert.ToHexStringLower(GuestMemory.ReadSpan(back, 16)));

        // Decrypt2 on an Init1 context runs the inverse cipher with the encryption keys
        Assert.Equal(0, Call(Sysmem, "ksceAesDecrypt2", context, cipher, back));
        Assert.NotEqual("00112233445566778899aabbccddeeff", Convert.ToHexStringLower(GuestMemory.ReadSpan(back, 16)));
    }

    [Fact]
    public void TheCipherMatchesTheHostAes()
    {
        byte[] keyBytes = RandomNumberGenerator.GetBytes(32);
        byte[] block = RandomNumberGenerator.GetBytes(16);
        using Aes aes = Aes.Create();
        aes.Key = keyBytes;
        uint context = _harness.Alloc(0x3D0);
        uint output = _harness.Alloc(16);
        Assert.Equal(0, Call(Sysmem, "ksceAesInit1", context, 128u, 256u, Put(keyBytes)));
        Assert.Equal(0, Call(Sysmem, "ksceAesEncrypt1", context, Put(block), output));
        Assert.Equal(aes.EncryptEcb(block, PaddingMode.None), GuestMemory.ReadSpan(output, 16).ToArray());
    }

    [Theory]
    [InlineData(192, 128)]
    [InlineData(256, 192)]
    [InlineData(256, 256)]
    public void TheWideRijndaelBlocksRoundTrip(int blockBits, int keyBits)
    {
        int size = blockBits / 8;
        uint context = _harness.Alloc(0x3D0);
        byte[] plain = Sequence(size + 5)[5..];
        uint source = Put(plain);
        uint cipher = _harness.Alloc((uint)size);
        uint back = _harness.Alloc((uint)size);
        Assert.Equal(0, Call(Sysmem, "ksceAesInit1", context, (uint)blockBits, (uint)keyBits, Put(Sequence(keyBits / 8))));
        Assert.Equal((uint)(blockBits / 32), GuestMemory.R32(context + 8));
        Assert.Equal((uint)(Math.Max(blockBits, keyBits) / 32 + 6), GuestMemory.R32(context + 0xC));
        Assert.Equal(0, Call(Sysmem, "ksceAesEncrypt1", context, source, cipher));
        Assert.NotEqual(plain, GuestMemory.ReadSpan(cipher, size).ToArray());
        Assert.Equal(0, Call(Sysmem, "ksceAesDecrypt1", context, cipher, back));
        Assert.Equal(plain, GuestMemory.ReadSpan(back, size).ToArray());
    }

    [Fact]
    public void TheSizesAndAddressesAreChecked()
    {
        uint context = _harness.Alloc(0x3D0);
        uint key = Put(Sequence(32));
        Assert.Equal(IllegalAddress, Call(Sysmem, "ksceAesInit1", 0u, 128u, 128u, key));
        Assert.Equal(IllegalAddress, Call(Sysmem, "ksceAesInit2", context, 128u, 128u, 0u));
        Assert.Equal(Invalid, Call(Sysmem, "ksceAesInit1", context, 64u, 128u, key));
        Assert.Equal(Invalid, Call(Sysmem, "ksceAesInit1", context, 128u, 160u, key));
        GuestMemory.W32(context + 8, 5);
        Assert.Equal(-1, Call(Sysmem, "ksceAesEncrypt1", context, key, key));
        Assert.Equal(0x63636363u, (uint)Invoke(Sysmem, "ksceAesSubWord", 0u)!);
        Assert.Equal(0x7C7C777Cu, (uint)Invoke(Sysmem, "ksceAesSubWord", 0x01010201u)!);

        // InvMixColumns undoes MixColumns: db 13 53 45 -> 8e 4d a1 bc (FIPS 197 / the usual test column)
        Assert.Equal(0x4553_13DBu, (uint)Invoke(Sysmem, "ksceAesInverseMixColumn", 0xBCA1_4D8Eu)!);
    }

    [Fact]
    public void CmacMatchesRfc4493()
    {
        byte[] key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        byte[] message = Convert.FromHexString(
            "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
        (int Length, string Mac)[] vectors =
        [
            (0, "bb1d6929e95937287fa37d129b756746"),
            (16, "070a16b46b4d4144f79bdd9dd04a287c"),
            (40, "dfa66747de9ae63030ca32611497c827"),
            (64, "51f0bebf7e3b9d92fc49741779363cfe"),
        ];
        uint keyAddress = Put(key);
        uint data = Put(message);
        uint mac = _harness.Alloc(16);
        uint context = _harness.Alloc(0x2A0);
        foreach ((int length, string expected) in vectors)
        {
            Assert.Equal(0, Call(Sysmem, "ksceAesCmac", 128u, 128u, keyAddress, data, (uint)length, mac));
            Assert.Equal(expected, Convert.ToHexStringLower(GuestMemory.ReadSpan(mac, 16)));

            // by block, in uneven pieces
            Assert.Equal(0, Call(Sysmem, "ksceAesCmacInit", context, 128u, 128u, keyAddress));
            uint offset = 0;
            foreach (uint piece in new uint[] { 3, 13, 17, 0, 31 })
            {
                uint take = Math.Min(piece, (uint)length - offset);
                Assert.Equal(0, Call(Sysmem, "ksceAesCmacUpdate", context, data + offset, take));
                offset += take;
            }

            Assert.Equal((uint)length, offset);
            Assert.Equal(0, Call(Sysmem, "ksceAesCmacFinal", context, mac));
            Assert.Equal(expected, Convert.ToHexStringLower(GuestMemory.ReadSpan(mac, 16)));
            Assert.Equal(0, Call(Sysmem, "ksceAesCmacFinal", context, mac));
            Assert.Equal(expected, Convert.ToHexStringLower(GuestMemory.ReadSpan(mac, 16)));
        }

        Assert.Equal(IllegalAddress, Call(Sysmem, "ksceAesCmacInit", context, 128u, 128u, 0u));
        Assert.Equal(IllegalAddress, Call(Sysmem, "ksceAesCmacUpdate", context, 0u, 0u));
        Assert.Equal(IllegalAddress, Call(Sysmem, "ksceAesCmacFinal", context, 0u));
        Assert.Equal(Invalid, Call(Sysmem, "ksceAesCmac", 128u, 100u, keyAddress, data, 0u, mac));
    }
}
