using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A PlayStation Mobile title's content key comes from its license (ScePsmDrmLicense, 0x400 bytes): scePsmDrmGetRifKey
/// gives the license's key set (0x200 bytes at 0x100: HMAC key, AES content key, signature) when the license carries it
/// in clear (no signature in 0x300-0x3FF); a signed store license is the security processor's to open and is refused.
/// The runtime opens the title's PSSE files with the content key (psdevwiki "PSSE"; psse-decrypt): synthetic licenses
/// and files, no title's data.
/// </summary>
[Collection("guest")]
public sealed class PsmLicenseTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Unsupported = unchecked((int)0x80020004), Invalid = unchecked((int)0x80870001);
    private const string ContentId = "EP0000-NPXX99998_00-0000000000000001";

    private readonly HleHarness _harness = new(fixture, "psmlicense");

    public void Dispose() => _harness.Dispose();

    private static readonly byte[] s_contentKey = Enumerable.Range(0x40, 16).Select(i => (byte)i).ToArray();

    /// <summary>A license with its key set in clear (an empty signature area), or a signed one.</summary>
    private static byte[] License(bool signed, string contentId = ContentId)
    {
        byte[] rif = new byte[PsmLicense.Size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(rif.AsSpan(0x10), 0x0123456789ABCDEFUL); // big-endian, as the module reads it
        Encoding.ASCII.GetBytes(contentId).CopyTo(rif, 0x50);
        for (int i = 0; i < 0x20; i++)
        {
            rif[0x100 + i] = (byte)(0xA0 + i); // HMAC key
        }

        s_contentKey.CopyTo(rif, PsmLicense.ContentKeyOffset);
        if (signed)
        {
            rif[0x300] = 0x5A;
            rif[0x3FF] = 0xA5;
        }

        return rif;
    }

    [Fact]
    public void GetRifKeyGivesTheKeySetOfALicenseThatCarriesItInClear()
    {
        SystemServicesVsh drm = _harness.Module<SystemServicesVsh>();
        byte[] rif = License(signed: false);
        uint license = _harness.Alloc(PsmLicense.Size), keys = _harness.Alloc(PsmLicense.KeySetSize);
        uint times = _harness.Alloc(0x10), start = _harness.Alloc(8), end = _harness.Alloc(8), version = _harness.Alloc(4);
        rif.CopyTo(GuestMemory.Span(license, rif.Length));
        GuestMemory.W32(times, start);
        GuestMemory.W32(times + 4, end);
        GuestMemory.W32(version, 0xFFFFFFFF);

        Assert.Equal(0, Call(drm, "PsmDrmGetRifKey", license, keys, version, times));

        Assert.Equal(rif.AsSpan(PsmLicense.KeySetOffset, PsmLicense.KeySetSize).ToArray(), GuestMemory.ReadSpan(keys, PsmLicense.KeySetSize).ToArray());
        Assert.Equal(0u, GuestMemory.R32(version));
        Assert.Equal(0UL, GuestMemory.Read<ulong>(start));
        Assert.Equal(0x7FFFFFFFFFFFFFFFUL, GuestMemory.Read<ulong>(end)); // no expiration in the license
        // its plain fields
        uint content = _harness.Alloc(0x30), account = _harness.Alloc(8);
        Assert.Equal(0, Call(drm, "PsmDrmGetRifInfo", license, content, account, times));
        Assert.Equal(ContentId, GuestMemory.ReadCString(content, 0x30));
        Assert.Equal(0x0123456789ABCDEFUL, GuestMemory.Read<ulong>(account));
    }

    [Fact]
    public void GetRifKeyRefusesASignedLicenseAndMissingArguments()
    {
        SystemServicesVsh drm = _harness.Module<SystemServicesVsh>();
        byte[] rif = License(signed: true);
        uint license = _harness.Alloc(PsmLicense.Size), keys = _harness.Alloc(PsmLicense.KeySetSize), times = _harness.Alloc(0x10);
        rif.CopyTo(GuestMemory.Span(license, rif.Length));

        // the store's key set is encrypted for the account's activation: only the security processor opens it
        Assert.Equal(Unsupported, Call(drm, "PsmDrmGetRifKey", license, keys, 0u, times));
        Assert.Equal(0u, GuestMemory.R32(keys + PsmLicense.ContentKeyOffset - PsmLicense.KeySetOffset));
        Assert.Equal(Invalid, Call(drm, "PsmDrmGetRifKey", 0u, keys, 0u, times));
        Assert.Equal(Invalid, Call(drm, "PsmDrmGetRifKey", license, 0u, 0u, times));
        Assert.Equal(Invalid, Call(drm, "PsmDrmGetRifKey", license, keys, 0u, 0u));
        Assert.Null(PsmLicense.ContentKey(rif));
        Assert.Equal(s_contentKey, PsmLicense.ContentKey(License(signed: false)));
    }

    /// <summary>A file of more than 0x80000 bytes crosses a signature block; the last block is a partial one.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0x7980)]
    [InlineData(0x7981)]
    [InlineData(0x90123)]
    public void PsseFilesOpenWithTheContentKeyOfTheirLicense(int size)
    {
        byte[] plain = new byte[size];
        new Random(size).NextBytes(plain);
        byte[] iv = Enumerable.Range(0x10, 16).Select(i => (byte)i).ToArray();
        byte[] file = PsseFile.Encrypt(plain, s_contentKey, ContentId, iv);

        Assert.True(PsseFile.IsPsse(file));
        Assert.Equal(ContentId, PsseFile.ContentId(file));
        Assert.Equal(plain, PsseFile.Decrypt(file, s_contentKey));
        byte[] wrong = (byte[])s_contentKey.Clone();
        wrong[0] ^= 1;
        Assert.Null(PsseFile.Decrypt(file, wrong)); // its MD5 does not match
        Assert.False(PsseFile.IsPsse(plain));
    }

    [Fact]
    public void TheContentKeyIsFoundInTheTitlesInstalledLicenses()
    {
        string fs = Path.Combine(Path.GetTempPath(), "vitasharp-psmlicense-" + Guid.NewGuid().ToString("N"));
        try
        {
            string dir = VitaSharp.Core.Io.FirmwareLayout.PsmTitles.Host(fs, "NPXX99998", VitaSharp.Core.Io.FirmwareLayout.PsmReadOnly, VitaSharp.Core.Io.FirmwareLayout.PsmLicense);
            Assert.Null(PsmLicense.FindContentKey(fs, "NPXX99998", ContentId)); // nothing installed: no license
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "other.rif"), License(signed: false, "EP0000-NPXX99997_00-0000000000000001"));
            File.WriteAllBytes(Path.Combine(dir, "signed.rif"), License(signed: true));
            Assert.Null(PsmLicense.FindContentKey(fs, "NPXX99998", ContentId)); // another content's, and a signed one
            File.WriteAllBytes(Path.Combine(dir, PkgInstaller.AccountRifName), License(signed: false));
            Assert.Equal(s_contentKey, PsmLicense.FindContentKey(fs, "NPXX99998", ContentId));
        }
        finally
        {
            if (Directory.Exists(fs))
            {
                Directory.Delete(fs, true);
            }
        }
    }
}
