using System.Numerics;
using System.Security.Cryptography;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ksceSblRSA2048CreateSignature / ksceSblRSA2048VerifySignature as SceSblPostSsMgr 3.74 does them in software:
/// PKCS#1 v1.5 on the caller's key (CRT private key, little-endian word arrays), the signature 0x100 bytes
/// big-endian, 0 or -1. The key is made from a fixed seed (the same every run) and the signatures are compared with
/// .NET's own RSA (an independent implementation) for every hash type it knows.
/// </summary>
[Collection("guest")]
public sealed class PostSsMgrRsaTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int HashMd2 = 2, HashMd5 = 4, HashSha1 = 5, HashSha256 = 11, HashSha384 = 12, HashSha512 = 13;
    private const int HashSha224 = 14;

    private static readonly TestKey s_key = TestKey.FromSeed(0x56495441);

    private readonly HleHarness _harness = new(fixture, "postssrsa");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    /// <summary>A { data, size } block holding <paramref name="data"/> (room for 0x100 bytes).</summary>
    private uint DataParam(ReadOnlySpan<byte> data, uint size)
    {
        uint buffer = _harness.Alloc(0x100);
        data.CopyTo(GuestMemory.Span(buffer, data.Length));
        uint param = _harness.Alloc(8);
        GuestMemory.W32(param, buffer);
        GuestMemory.W32(param + 4, size);
        return param;
    }

    /// <summary>A number as <paramref name="bytes"/> little-endian bytes in guest memory.</summary>
    private uint Number(BigInteger value, int bytes)
    {
        uint address = _harness.Alloc((uint)bytes);
        Span<byte> target = GuestMemory.Span(address, bytes);
        target.Clear();
        value.TryWriteBytes(target, out _, isUnsigned: true, isBigEndian: false);
        return address;
    }

    private uint PrivateKey(TestKey key)
    {
        uint param = _harness.Alloc(0x40);
        GuestMemory.Span(param, 0x40).Clear();
        GuestMemory.W32(param + 0x10, Number(key.P, 0x80));
        GuestMemory.W32(param + 0x14, Number(key.Q, 0x80));
        GuestMemory.W32(param + 0x18, Number(key.Dp, 0x80));
        GuestMemory.W32(param + 0x1C, Number(key.Dq, 0x80));
        GuestMemory.W32(param + 0x20, Number(key.InverseQ, 0x80));
        return param;
    }

    private uint PublicKey(BigInteger modulus, BigInteger exponent)
    {
        uint param = _harness.Alloc(8);
        GuestMemory.W32(param, Number(modulus, 0x100));
        GuestMemory.W32(param + 4, Number(exponent, 0x100));
        return param;
    }

    private byte[] Sign(byte[] hash, int hashType, out int result)
    {
        uint signature = DataParam([], 0);
        result = Call(Services, "ksceSblRSA2048CreateSignature", signature, DataParam(hash, (uint)hash.Length),
            PrivateKey(s_key), (uint)hashType);
        Assert.Equal(result == 0 ? 0x100u : 0u, GuestMemory.R32(signature + 4));
        return GuestMemory.ReadSpan(GuestMemory.R32(signature), 0x100).ToArray();
    }

    private int Verify(byte[] signature, byte[] hash, int hashType, uint size = 0x100)
    {
        return Call(Services, "ksceSblRSA2048VerifySignature", DataParam(signature, size),
            DataParam(hash, (uint)hash.Length), PublicKey(s_key.Modulus, s_key.Exponent), (uint)hashType);
    }

    public static TheoryData<int, string> DotNetHashes => new()
    {
        { HashMd5, "MD5" },
        { HashSha1, "SHA1" },
        { HashSha256, "SHA256" },
        { HashSha384, "SHA384" },
        { HashSha512, "SHA512" },
    };

    [Theory]
    [MemberData(nameof(DotNetHashes))]
    public void Signatures_match_an_independent_pkcs1_v15_and_verify_both_ways(int hashType, string hashName)
    {
        HashAlgorithmName algorithm = new(hashName);
        byte[] hash = hashName switch
        {
            "MD5" => MD5.HashData("VitaSharp"u8),
            "SHA1" => SHA1.HashData("VitaSharp"u8),
            "SHA256" => SHA256.HashData("VitaSharp"u8),
            "SHA384" => SHA384.HashData("VitaSharp"u8),
            _ => SHA512.HashData("VitaSharp"u8),
        };
        using var reference = RSA.Create();
        reference.ImportParameters(s_key.Parameters());
        byte[] expected = reference.SignHash(hash, algorithm, RSASignaturePadding.Pkcs1);

        byte[] signature = Sign(hash, hashType, out int result);

        Assert.Equal(0, result);
        Assert.Equal(expected, signature);
        Assert.Equal(0, Verify(expected, hash, hashType));
        Assert.True(reference.VerifyHash(hash, signature, algorithm, RSASignaturePadding.Pkcs1));
    }

    [Theory]
    [InlineData(HashMd2, 0x10, "3020300C06082A864886F70D020205000410")]
    [InlineData(HashSha224, 0x1C, "302D300D06096086480165030402040500041C")]
    public void Hashes_dotnet_does_not_sign_carry_their_digest_info(int hashType, int size, string prefixHex)
    {
        byte[] hash = new byte[size];
        hash.AsSpan().Fill(0xA5);

        byte[] signature = Sign(hash, hashType, out int result);

        Assert.Equal(0, result);
        var recovered = BigInteger.ModPow(new BigInteger(signature, isUnsigned: true, isBigEndian: true),
            s_key.Exponent, s_key.Modulus);
        byte[] encoded = new byte[0x100];
        recovered.TryWriteBytes(encoded.AsSpan(1), out _, isUnsigned: true, isBigEndian: true);
        byte[] prefix = Convert.FromHexString(prefixHex);
        int padEnd = 0x100 - size - prefix.Length;
        Assert.Equal(new byte[] { 0x00, 0x01 }, encoded[..2]);
        Assert.All(encoded[2..(padEnd - 1)], value => Assert.Equal(0xFF, value));
        Assert.Equal(0, encoded[padEnd - 1]);
        Assert.Equal(prefix, encoded[padEnd..(padEnd + prefix.Length)]);
        Assert.Equal(hash, encoded[(padEnd + prefix.Length)..]);
        Assert.Equal(0, Verify(signature, hash, hashType));
    }

    [Fact]
    public void A_changed_hash_signature_or_type_does_not_verify()
    {
        byte[] hash = SHA256.HashData("VitaSharp"u8);
        byte[] signature = Sign(hash, HashSha256, out _);
        byte[] otherHash = (byte[])hash.Clone();
        otherHash[0] ^= 1;
        byte[] otherSignature = (byte[])signature.Clone();
        otherSignature[0x80] ^= 1;

        Assert.Equal(0, Verify(signature, hash, HashSha256));
        Assert.Equal(-1, Verify(signature, otherHash, HashSha256));
        Assert.Equal(-1, Verify(otherSignature, hash, HashSha256));
        Assert.Equal(-1, Verify(signature, hash, HashSha1));
        Assert.Equal(-1, Verify(signature, hash, HashSha256, 0xFF));
    }

    [Fact]
    public void Unknown_types_wrong_hash_sizes_and_bad_keys_answer_minus_one()
    {
        byte[] hash = SHA256.HashData("VitaSharp"u8);
        Sign(hash, 0, out int raw);
        Sign(hash, 3, out int md4);
        Sign(hash[..0x1F], HashSha256, out int shortHash);
        Assert.Equal(-1, raw);
        Assert.Equal(-1, md4);
        Assert.Equal(-1, shortHash);

        uint signature = DataParam([], 0);
        TestKey evenPrime = s_key with { P = s_key.P - 1 };
        Assert.Equal(-1, Call(Services, "ksceSblRSA2048CreateSignature", signature, DataParam(hash, 0x20),
            PrivateKey(evenPrime), (uint)HashSha256));
        TestKey shortPrime = s_key with { Q = s_key.Q >> 1 };
        Assert.Equal(-1, Call(Services, "ksceSblRSA2048CreateSignature", signature, DataParam(hash, 0x20),
            PrivateKey(shortPrime), (uint)HashSha256));

        byte[] good = Sign(hash, HashSha256, out _);
        Assert.Equal(-1, Call(Services, "ksceSblRSA2048VerifySignature", DataParam(good, 0x100), DataParam(hash, 0x20),
            PublicKey(s_key.Modulus - 1, s_key.Exponent), (uint)HashSha256));
        byte[] aboveModulus = new byte[0x100];
        s_key.Modulus.TryWriteBytes(aboveModulus, out _, isUnsigned: true, isBigEndian: true);
        Assert.Equal(-1, Verify(aboveModulus, hash, HashSha256));
    }

    /// <summary>An RSA-2048 key made deterministically from a seed (Miller-Rabin primes of 1024 bits, e = 65537).</summary>
    private sealed record TestKey(BigInteger P, BigInteger Q, BigInteger Exponent)
    {
        public BigInteger Modulus => P * Q;

        public BigInteger PrivateExponent => ModInverse(Exponent, (P - 1) * (Q - 1));

        public BigInteger Dp => PrivateExponent % (P - 1);

        public BigInteger Dq => PrivateExponent % (Q - 1);

        public BigInteger InverseQ => ModInverse(Q, P);

        public static TestKey FromSeed(int seed)
        {
            Random random = new(seed);
            BigInteger exponent = 65537;
            BigInteger first = Prime(random, exponent);
            BigInteger second = Prime(random, exponent);
            while (second == first)
            {
                second = Prime(random, exponent);
            }

            return new TestKey(BigInteger.Max(first, second), BigInteger.Min(first, second), exponent);
        }

        public RSAParameters Parameters()
        {
            return new RSAParameters
            {
                Modulus = BigEndian(Modulus, 0x100),
                Exponent = BigEndian(Exponent, 3),
                D = BigEndian(PrivateExponent, 0x100),
                P = BigEndian(P, 0x80),
                Q = BigEndian(Q, 0x80),
                DP = BigEndian(Dp, 0x80),
                DQ = BigEndian(Dq, 0x80),
                InverseQ = BigEndian(InverseQ, 0x80),
            };
        }

        private static byte[] BigEndian(BigInteger value, int size)
        {
            byte[] output = new byte[size];
            int length = value.GetByteCount(isUnsigned: true);
            value.TryWriteBytes(output.AsSpan(size - length), out _, isUnsigned: true, isBigEndian: true);
            return output;
        }

        private static BigInteger Prime(Random random, BigInteger exponent)
        {
            byte[] bytes = new byte[0x80];
            while (true)
            {
                random.NextBytes(bytes);
                bytes[0] |= 1;
                bytes[^1] |= 0xC0;
                BigInteger candidate = new(bytes, isUnsigned: true, isBigEndian: false);
                if ((candidate - 1) % exponent != 0 && IsProbablePrime(candidate))
                {
                    return candidate;
                }
            }
        }

        private static bool IsProbablePrime(BigInteger candidate)
        {
            int[] bases = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53];
            foreach (int small in bases)
            {
                if (candidate % small == 0)
                {
                    return false;
                }
            }

            BigInteger odd = candidate - 1;
            int shifts = 0;
            while (odd.IsEven)
            {
                odd >>= 1;
                shifts++;
            }

            foreach (int witness in bases)
            {
                var value = BigInteger.ModPow(witness, odd, candidate);
                if (value.IsOne || value == candidate - 1)
                {
                    continue;
                }

                bool composite = true;
                for (int round = 1; round < shifts; round++)
                {
                    value = BigInteger.ModPow(value, 2, candidate);
                    if (value == candidate - 1)
                    {
                        composite = false;
                        break;
                    }
                }

                if (composite)
                {
                    return false;
                }
            }

            return true;
        }

        private static BigInteger ModInverse(BigInteger value, BigInteger modulus)
        {
            BigInteger oldRemainder = value % modulus;
            BigInteger remainder = modulus;
            BigInteger oldCoefficient = 1;
            BigInteger coefficient = 0;
            while (!remainder.IsZero)
            {
                BigInteger quotient = oldRemainder / remainder;
                (oldRemainder, remainder) = (remainder, oldRemainder - (quotient * remainder));
                (oldCoefficient, coefficient) = (coefficient, oldCoefficient - (quotient * coefficient));
            }

            return ((oldCoefficient % modulus) + modulus) % modulus;
        }
    }
}
