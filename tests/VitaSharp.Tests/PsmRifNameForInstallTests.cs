using System.Buffers.Binary;
using System.Security.Cryptography;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// scePsmDrmGetRifNameForInstall as SceNpDrm 3.74 checks a license before it names it: the RSA-2048 signature of the
/// first 0x300 bytes (SHA-256, PKCS#1 v1.5, at +0x300, the module's key 5) first and alone (0x808F0104), then the
/// header ("PSM-RIF\0", version 1, reserved words zero, start not after the end: 0x808F0105), then the loaded
/// activation: its account (0x808F0108) and digest (0x808F0109); the activation is the secure processor's, so with
/// activation data present the answer is "not available". A synthetic key stands for the module's; the user call in
/// the harness (no firmware) answers "not available" with the name zeroed.
/// </summary>
[Collection("guest")]
public sealed class PsmRifNameForInstallTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Signature = unchecked((int)0x808F0104), BadLicense = unchecked((int)0x808F0105);
    private const int OtherAccount = unchecked((int)0x808F0108), OtherActivation = unchecked((int)0x808F0109);
    private const int Unsupported = unchecked((int)0x80020004), Invalid = unchecked((int)0x808F0101);

    /// <summary>A 2048-bit test key (exponent 65537) standing for the module's.</summary>
    private static readonly RSA s_key = RSA.Create(2048);

    private readonly HleHarness _harness = new(fixture, "psmrifinstall");

    public void Dispose() => _harness.Dispose();

    /// <summary>The key's modulus in the module's layout (little-endian words).</summary>
    private static byte[] ModuleModulus()
    {
        byte[] modulus = s_key.ExportParameters(false).Modulus!;
        Array.Reverse(modulus);
        return modulus;
    }

    private static byte[] License(ulong accountId, Action<byte[]>? change = null, bool sign = true)
    {
        byte[] license = new byte[PsmLicense.Size];
        "PSM-RIF\0"u8.CopyTo(license);
        BinaryPrimitives.WriteUInt32BigEndian(license.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(license.AsSpan(0x10), accountId);
        BinaryPrimitives.WriteUInt64BigEndian(license.AsSpan(0x20), 1000);
        BinaryPrimitives.WriteUInt64BigEndian(license.AsSpan(0x28), 2000);
        "UV0000-NPOA00001_00-0000000000000000"u8.CopyTo(license.AsSpan(0x50));
        change?.Invoke(license);
        if (sign)
        {
            s_key.SignData(license.AsSpan(0, 0x300), license.AsSpan(0x300, 0x100), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        return license;
    }

    private static int Check(byte[] license, bool activation, out ulong account) =>
        SystemServicesVsh.PsmLicenseInstallCheck(license, ModuleModulus(), activation, out account);

    [Fact]
    public void A_signed_license_of_no_activation_is_named_by_its_account()
    {
        Assert.Equal(0, Check(License(0), activation: false, out ulong account));
        Assert.Equal(0UL, account);
        Assert.True(SystemServicesVsh.PsmLicenseSignatureValid(License(0), ModuleModulus()));
    }

    [Fact]
    public void The_signature_is_checked_first_and_alone()
    {
        // unsigned (a license plugin's), and a signed one changed after signing: nothing else is looked at
        Assert.Equal(Signature, Check(License(0x0123456789ABCDEFUL, l => l.AsSpan(0, 8).Clear(), sign: false), false, out _));
        byte[] tampered = License(0);
        tampered[0x60] ^= 1;
        Assert.Equal(Signature, Check(tampered, true, out _));
        Assert.False(SystemServicesVsh.PsmLicenseSignatureValid(tampered, ModuleModulus()));
    }

    [Fact]
    public void A_bad_header_is_refused_after_the_signature()
    {
        Assert.Equal(BadLicense, Check(License(0, l => l[0] = (byte)'X'), true, out _));
        Assert.Equal(BadLicense, Check(License(0, l => BinaryPrimitives.WriteUInt32BigEndian(l.AsSpan(8), 2)), true, out _));
        Assert.Equal(BadLicense, Check(License(0, l => l[0xC] = 1), true, out _));
        Assert.Equal(BadLicense, Check(License(0, l => l[0x18] = 1), true, out _));
        Assert.Equal(BadLicense, Check(License(0, l => l[0x1F] = 1), true, out _));
        Assert.Equal(BadLicense, Check(License(0, l => BinaryPrimitives.WriteUInt64BigEndian(l.AsSpan(0x20), 3000)), true, out _));
        // start equal to the end is accepted
        Assert.Equal(0, Check(License(0, l => BinaryPrimitives.WriteUInt64BigEndian(l.AsSpan(0x20), 2000)), false, out _));
    }

    [Fact]
    public void The_activation_decides_after_the_header()
    {
        Assert.Equal(Unsupported, Check(License(5), activation: true, out _));
        Assert.Equal(OtherAccount, Check(License(5), activation: false, out _));
        Assert.Equal(OtherActivation, Check(License(0, l => l[0x30] = 1), activation: false, out _));
        Assert.Equal(Unsupported, SystemServicesVsh.PsmLicenseInstallCheck(License(0), null, false, out _));
    }

    [Fact]
    public void The_user_call_copies_the_license_and_zeroes_the_name()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        uint name = _harness.Alloc(0x30);
        uint license = _harness.Alloc(PsmLicense.Size);
        License(0).CopyTo(GuestMemory.Span(license, PsmLicense.Size));
        GuestMemory.Fill(name, 0xEE, 0x30);
        Assert.Equal(Invalid, Call(services, "PsmDrmGetRifNameForInstall", name, 0u));
        Assert.Equal(new byte[0x30], GuestMemory.ReadSpan(name, 0x30).ToArray());
        // no firmware in the harness: the module's key is not known
        GuestMemory.Fill(name, 0xEE, 0x30);
        Assert.Equal(Unsupported, Call(services, "PsmDrmGetRifNameForInstall", name, license));
        Assert.Equal(new byte[0x30], GuestMemory.ReadSpan(name, 0x30).ToArray());
        Assert.Null(SystemServicesVsh.PsmLicenseModulusFor(_harness.Root));
    }
}
