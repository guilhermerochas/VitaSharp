using System.Buffers.Binary;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Package;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The PSM licenses earlier versions stored under the fixed name of the PS Vita licenses get the name the PSM runtime
/// opens, once: libpsm (PCSI00011 2.01) reads the registry's /CONFIG/NP/account_id and opens
/// RO/License/&lt;scePsmDrmGetRifName(that account)&gt; (flag 0), whatever account the license itself names (a license
/// made on another unit names that unit's account, or none). Copied, compared, then the old file removed; a different
/// file under the new name keeps both; files with other names and an fs without the firmware are left alone.
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

    /// <summary>A license in the firmware's format: "PSM-RIF", version 1, its account big-endian at +0x10.</summary>
    private static byte[] StoreLicense(ulong accountId)
    {
        byte[] license = new byte[PsmPkgInstaller.RifSize];
        "PSM-RIF\0"u8.CopyTo(license);
        BinaryPrimitives.WriteUInt32BigEndian(license.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(license.AsSpan(0x10), accountId);
        "UV0000-NPOA00001_00-0000000000000000"u8.CopyTo(license.AsSpan(PsmPkgInstaller.RifContentIdOffset));
        return license;
    }

    /// <summary>A license made on another unit by a license plugin: no header, a fixed account (little-endian) that is
    /// not this unit's, the key set in clear.</summary>
    private static byte[] OtherUnitLicense()
    {
        byte[] license = new byte[PsmPkgInstaller.RifSize];
        BinaryPrimitives.WriteUInt32BigEndian(license.AsSpan(0xC), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(license.AsSpan(0x10), 0x0123456789ABCDEFUL);
        "UV0000-NPOA00002_00-0000000000000000"u8.CopyTo(license.AsSpan(PsmPkgInstaller.RifContentIdOffset));
        return license;
    }

    private void SetAccount(ulong accountId)
    {
        var registry = Registry.For(_root);
        registry.Define("/CONFIG/NP/account_id", Registry.KeyType.Bin, 8);
        byte[] value = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(value, accountId);
        Assert.True(registry.Set("/CONFIG/NP", "account_id", value));
    }

    /// <summary>The file the PSM runtime opens, as libpsm asks for it: the registry's account, then the module's name.</summary>
    private string RuntimeLicensePath(string dir) =>
        Path.Combine(dir, SystemServicesVsh.RifFileName(s_key, 0, SystemServicesVsh.RegistryAccountId(_root))!);

    [Theory]
    [InlineData(0x1122334455667788UL)]
    [InlineData(0UL)]
    public void Installed_then_migrated_the_runtime_finds_the_license(ulong account)
    {
        SetAccount(account);
        string store = LicenseFolder("NPOA00001");
        string other = LicenseFolder("NPOA00002");
        byte[] storeLicense = StoreLicense(account);
        byte[] otherLicense = OtherUnitLicense();
        // what the earlier installer wrote (the fixed name)
        File.WriteAllBytes(Path.Combine(store, PkgInstaller.AccountRifName), storeLicense);
        File.WriteAllBytes(Path.Combine(other, PkgInstaller.AccountRifName), otherLicense);
        File.WriteAllBytes(Path.Combine(other, "FAKE.rif"), otherLicense);

        int renamed = PsmLicenseNameMigration.Run(_root, s_key, SystemServicesVsh.RegistryAccountId(_root), null);

        Assert.Equal(2, renamed);
        Assert.Equal(storeLicense, File.ReadAllBytes(RuntimeLicensePath(store)));
        Assert.Equal(otherLicense, File.ReadAllBytes(RuntimeLicensePath(other)));
        Assert.False(File.Exists(Path.Combine(store, PkgInstaller.AccountRifName)));
        Assert.False(File.Exists(Path.Combine(other, PkgInstaller.AccountRifName)));
        Assert.True(File.Exists(Path.Combine(other, "FAKE.rif")));
        // a store license names the same account: the name scePsmDrmGetRifNameForInstall gives it is the same file
        Assert.Equal(Path.GetFileName(RuntimeLicensePath(store)),
            SystemServicesVsh.RifFileName(s_key, 0, BinaryPrimitives.ReadUInt64BigEndian(storeLicense.AsSpan(0x10))));
    }

    [Fact]
    public void The_old_name_becomes_the_runtime_name_once()
    {
        SetAccount(0x0123456789ABCDEFUL);
        string dir = LicenseFolder("NPOA00001");
        byte[] license = StoreLicense(0x0123456789ABCDEFUL);
        File.WriteAllBytes(Path.Combine(dir, PkgInstaller.AccountRifName), license);
        string expected = SystemServicesVsh.RifFileName(s_key, 0, 0x0123456789ABCDEFUL)!;
        var log = new List<string>();

        Assert.Equal(1, PsmLicenseNameMigration.Run(_root, s_key, 0x0123456789ABCDEFUL, log.Add));

        Assert.False(File.Exists(Path.Combine(dir, PkgInstaller.AccountRifName)));
        Assert.Equal(license, File.ReadAllBytes(Path.Combine(dir, expected)));
        Assert.Contains(log, line => line.Contains(expected, StringComparison.Ordinal));
        Assert.Equal(0, PsmLicenseNameMigration.Run(_root, s_key, 0x0123456789ABCDEFUL, log.Add));
        Assert.Equal(license, File.ReadAllBytes(Path.Combine(dir, expected)));
    }

    [Fact]
    public void A_different_license_under_the_new_name_keeps_both()
    {
        string dir = LicenseFolder("NPOA00002");
        byte[] license = StoreLicense(5);
        string expected = SystemServicesVsh.RifFileName(s_key, 0, 5)!;
        File.WriteAllBytes(Path.Combine(dir, PkgInstaller.AccountRifName), license);
        File.WriteAllBytes(Path.Combine(dir, expected), StoreLicense(6));

        Assert.Equal(0, PsmLicenseNameMigration.Run(_root, s_key, 5, null));

        Assert.Equal(license, File.ReadAllBytes(Path.Combine(dir, PkgInstaller.AccountRifName)));
        Assert.Equal(StoreLicense(6), File.ReadAllBytes(Path.Combine(dir, expected)));
    }

    [Fact]
    public void Without_the_firmware_nothing_changes()
    {
        string dir = LicenseFolder("NPOA00003");
        File.WriteAllBytes(Path.Combine(dir, PkgInstaller.AccountRifName), StoreLicense(5));

        Assert.Equal(0, PsmLicenseNameMigration.Run(_root, null, 5, null));
        Assert.Equal(0, PsmLicenseNameMigration.Run(_root));

        Assert.True(File.Exists(Path.Combine(dir, PkgInstaller.AccountRifName)));
    }
}
