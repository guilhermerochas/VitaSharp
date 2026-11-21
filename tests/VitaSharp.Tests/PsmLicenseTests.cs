using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader.Self;
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
    private const int Unsupported = unchecked((int)0x80020004), KernelInvalid = unchecked((int)0x808F0101);
    private const int NotActivated = unchecked((int)0x808F0106), NotPsmRuntime = unchecked((int)0x80010058);
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

    /// <summary>scePsmDrmGetRifKey follows SceNpDrm 3.74: only the PSM runtime may call it (0x80010058), and without a
    /// PSM activation loaded the kernel answers 0x808F0106 before it reads the license, whatever the license: the key
    /// set of a license plugin's license (in clear) is not handed out by the firmware, only by the plugin's hook.</summary>
    [Fact]
    public void GetRifKeyNeedsThePsmRuntimeAndAnActivation()
    {
        SystemServicesVsh drm = _harness.Module<SystemServicesVsh>();
        uint license = _harness.Alloc(PsmLicense.Size), keys = _harness.Alloc(PsmLicense.KeySetSize);
        uint start = _harness.Alloc(8), end = _harness.Alloc(8), version = _harness.Alloc(4);
        License(signed: false).CopyTo(GuestMemory.Span(license, PsmLicense.Size));
        GuestMemory.W32(version, 0xFFFFFFFF);

        Assert.Equal(NotPsmRuntime, Call(drm, "PsmDrmGetRifKey", license, keys, version, start, end));
        _harness.Emu.Access = new ProgramAccess(ProgramAccess.PsmRuntimeAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        foreach (bool signed in new[] { false, true })
        {
            License(signed).CopyTo(GuestMemory.Span(license, PsmLicense.Size));
            Assert.Equal(NotActivated, Call(drm, "PsmDrmGetRifKey", license, keys, version, start, end));
            Assert.Equal(new byte[PsmLicense.KeySetSize], GuestMemory.ReadSpan(keys, PsmLicense.KeySetSize).ToArray());
            Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(version));
        }

        // the kernel's NULL checks come first; NULL times are allowed
        Assert.Equal(KernelInvalid, Call(drm, "PsmDrmGetRifKey", 0u, keys, 0u, 0u, 0u));
        Assert.Equal(KernelInvalid, Call(drm, "PsmDrmGetRifKey", license, 0u, 0u, 0u, 0u));
        Assert.Equal(NotActivated, Call(drm, "PsmDrmGetRifKey", license, keys, 0u, 0u, 0u));
        // the runtime's PSSE reader asks the same call: no content key
        Assert.Null(drm.PsmContentKey(License(signed: false)));
    }

    /// <summary>With activation data on the unit the license is checked (here no firmware: not available) and the
    /// key set stays the secure processor's.</summary>
    [Fact]
    public void GetRifKeyWithAnActivationLeavesTheKeySetToTheSecureProcessor()
    {
        SystemServicesVsh drm = _harness.Module<SystemServicesVsh>();
        _harness.Emu.Access = new ProgramAccess(ProgramAccess.PsmRuntimeAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        string act = _harness.Emu.Vfs.Resolve("tm0:psmdrm/act.dat")!;
        Directory.CreateDirectory(Path.GetDirectoryName(act)!);
        File.WriteAllBytes(act, new byte[0x400]);
        uint license = _harness.Alloc(PsmLicense.Size), keys = _harness.Alloc(PsmLicense.KeySetSize);
        License(signed: false).CopyTo(GuestMemory.Span(license, PsmLicense.Size));
        Assert.Equal(Unsupported, Call(drm, "PsmDrmGetRifKey", license, keys, 0u, 0u, 0u));
        Assert.Equal(new byte[PsmLicense.KeySetSize], GuestMemory.ReadSpan(keys, PsmLicense.KeySetSize).ToArray());
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
    public void TheLicenseOfTheContentIsFoundInTheTitlesInstalledLicenses()
    {
        string fs = Path.Combine(Path.GetTempPath(), "vitasharp-psmlicense-" + Guid.NewGuid().ToString("N"));
        try
        {
            string dir = VitaSharp.Core.Io.FirmwareLayout.PsmTitles.Host(fs, "NPXX99998", VitaSharp.Core.Io.FirmwareLayout.PsmReadOnly, VitaSharp.Core.Io.FirmwareLayout.PsmLicense);
            Assert.Null(PsmLicense.FindLicense(fs, "NPXX99998", ContentId)); // nothing installed: no license
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "other.rif"), License(signed: false, "EP0000-NPXX99997_00-0000000000000001"));
            Assert.Null(PsmLicense.FindLicense(fs, "NPXX99998", ContentId)); // another content's
            File.WriteAllBytes(Path.Combine(dir, "signed.rif"), License(signed: true));
            Assert.Equal(License(signed: true), PsmLicense.FindLicense(fs, "NPXX99998", ContentId));
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
