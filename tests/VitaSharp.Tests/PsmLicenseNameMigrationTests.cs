using System.Buffers.Binary;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Package;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The PSM licenses earlier versions stored under the fixed name of the PS Vita licenses get the firmware's name
/// (scePsmDrmGetRifName of their account) once: copied, compared, then the old file removed; a different file under
/// the new name keeps both; files with other names and an fs without the firmware are left alone.
/// </summary>
public sealed class PsmLicenseNameMigrationTests : IDisposable
{
    private static readonly byte[] s_key = Enumerable.Range(0, 16).Select(i => (byte)(0x50 + i)).ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-psmrif-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string LicenseFolder(string titleId)
    {
        string dir = Path.Combine(_root, "ux0", "psm", titleId, "RO", "License");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] PsmLicense(ulong accountId)
    {
        byte[] license = new byte[PsmPkgInstaller.RifSize];
        "PSM-RIF\0"u8.CopyTo(license);
        BinaryPrimitives.WriteUInt64BigEndian(license.AsSpan(0x10), accountId);
        "UV0000-NPOA00001_00-0000000000000000"u8.CopyTo(license.AsSpan(PsmPkgInstaller.RifContentIdOffset));
        return license;
    }

    [Fact]
    public void The_old_name_becomes_the_firmware_name_once()
    {
        string dir = LicenseFolder("NPOA00001");
        byte[] license = PsmLicense(0x0123456789ABCDEFUL);
        File.WriteAllBytes(Path.Combine(dir, PkgInstaller.AccountRifName), license);
        File.WriteAllBytes(Path.Combine(dir, "FAKE.rif"), [1, 2, 3]);
        string expected = SystemServicesVsh.RifFileName(s_key, 0, 0x0123456789ABCDEFUL)!;
        var log = new List<string>();

        Assert.Equal(1, PsmLicenseNameMigration.Run(_root, s_key, log.Add));

        Assert.False(File.Exists(Path.Combine(dir, PkgInstaller.AccountRifName)));
        Assert.Equal(license, File.ReadAllBytes(Path.Combine(dir, expected)));
        Assert.True(File.Exists(Path.Combine(dir, "FAKE.rif")));
        Assert.Contains(log, line => line.Contains(expected, StringComparison.Ordinal));
        Assert.Equal(0, PsmLicenseNameMigration.Run(_root, s_key, log.Add));
        Assert.Equal(license, File.ReadAllBytes(Path.Combine(dir, expected)));
    }

    [Fact]
    public void A_different_license_under_the_new_name_keeps_both()
    {
        string dir = LicenseFolder("NPOA00002");
        byte[] license = PsmLicense(5);
        string expected = SystemServicesVsh.RifFileName(s_key, 0, 5)!;
        File.WriteAllBytes(Path.Combine(dir, PkgInstaller.AccountRifName), license);
        File.WriteAllBytes(Path.Combine(dir, expected), PsmLicense(6));

        Assert.Equal(0, PsmLicenseNameMigration.Run(_root, s_key, null));

        Assert.Equal(license, File.ReadAllBytes(Path.Combine(dir, PkgInstaller.AccountRifName)));
        Assert.Equal(PsmLicense(6), File.ReadAllBytes(Path.Combine(dir, expected)));
    }

    [Fact]
    public void Without_the_firmware_nothing_changes()
    {
        string dir = LicenseFolder("NPOA00003");
        File.WriteAllBytes(Path.Combine(dir, PkgInstaller.AccountRifName), PsmLicense(5));

        Assert.Equal(0, PsmLicenseNameMigration.Run(_root, null, null));
        Assert.Equal(0, PsmLicenseNameMigration.Run(_root));

        Assert.True(File.Exists(Path.Combine(dir, PkgInstaller.AccountRifName)));
    }
}
