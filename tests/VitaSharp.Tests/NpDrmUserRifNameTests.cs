using System.Buffers.Binary;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The user license name calls answer what the kernel answers (one source), as SceNpDrm 3.74's syscalls make them:
/// _sceNpDrmGetRifName, _sceNpDrmGetFixedRifName and _sceNpDrmGetRifNameForInstall call the SceNpDrmForDriver exports
/// into a zeroed buffer copied out whatever they answer; scePsmDrmGetRifName is the module's name of the account with
/// flag 0. With no firmware installed (the harness) only the fixed name of account 0 is known.
/// </summary>
[Collection("guest")]
public sealed class NpDrmUserRifNameTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80870001), NoAccount = unchecked((int)0x8087000C);
    private const int Unsupported = unchecked((int)0x80020004);
    private const string FixedName = "6488b73b912a753a492e2714e9b38bc7.rif";

    private readonly HleHarness _harness = new(fixture, "npdrmuser");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private uint NameBuffer()
    {
        uint name = _harness.Alloc(0x30);
        GuestMemory.Fill(name, 0xEE, 0x30);
        return name;
    }

    private static string Name(uint name) => GuestMemory.ReadCString(name, 0x30);

    [Fact]
    public void GetRifName_is_the_kernel_answer_for_the_account()
    {
        uint name = NameBuffer();
        Assert.Equal(NoAccount, Call(Services, "NpDrmGetRifName", name, 0u, 0u, 0u));
        Assert.Equal(new byte[0x30], GuestMemory.ReadSpan(name, 0x30).ToArray());
        Assert.Equal(Unsupported, Call(Services, "NpDrmGetRifName", name, 0u, 5u, 0u));
        Assert.Equal(0, Call(Services, "ksceNpDrmUpdateAccountId", 7u, 0u));
        Assert.Equal(Unsupported, Call(Services, "NpDrmGetRifName", name, 0u, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "NpDrmGetRifName", 0u, 0u, 7u, 0u));
    }

    [Fact]
    public void GetFixedRifName_is_known_only_for_account_0_without_the_firmware()
    {
        uint name = NameBuffer();
        Assert.Equal(0, Call(Services, "NpDrmGetFixedRifName", name, 0u, 0u, 0u));
        Assert.Equal(FixedName, Name(name));
        Assert.Equal(Unsupported, Call(Services, "NpDrmGetFixedRifName", name, 0u, 9u, 0u));
        Assert.Equal(new byte[0x30], GuestMemory.ReadSpan(name, 0x30).ToArray());
        Assert.Equal(Invalid, Call(Services, "NpDrmGetFixedRifName", 0u, 0u, 0u, 0u));
    }

    [Fact]
    public void GetRifNameForInstall_names_by_content_or_by_account()
    {
        byte[] rif = new byte[0x200];
        BinaryPrimitives.WriteUInt16BigEndian(rif.AsSpan(4), 1);
        "UP0000-PCSE00000_00-0000000000000000"u8.CopyTo(rif.AsSpan(0x10));
        uint license = _harness.Alloc(0x200);
        rif.CopyTo(GuestMemory.Span(license, 0x200));
        uint name = NameBuffer();
        Assert.Equal(0, Call(Services, "NpDrmGetRifNameForInstall", name, license, 0u));
        Assert.Equal("UP0000-PCSE00000_00-0000000000000000.rif", Name(name));
        Assert.Equal(0, Call(Services, "NpDrmGetRifNameForInstall", name, license, 1u));
        Assert.Equal(FixedName, Name(name));
        Assert.Equal(Invalid, Call(Services, "NpDrmGetRifNameForInstall", name, license, 2u));
    }

    [Fact]
    public void PsmGetRifName_is_the_account_name_with_flag_0()
    {
        uint name = NameBuffer();
        Assert.Equal(Unsupported, Call(Services, "PsmDrmGetRifName", name, 0u, 0u, 0u));
        Assert.Equal(new byte[0x30], GuestMemory.ReadSpan(name, 0x30).ToArray());
        Assert.Equal(Invalid, Call(Services, "PsmDrmGetRifName", 0u, 0u, 0u, 0u));
    }

    [Fact]
    public void A_psm_license_is_named_by_the_unit_account_without_the_firmware_unknown()
    {
        Assert.Equal(0UL, SystemServicesVsh.RegistryAccountId(_harness.Root));
        Assert.Null(SystemServicesVsh.PsmLicenseFileName(_harness.Root));
        Assert.Null(PsmPkgInstaller.LicenseFileName(_harness.Root));
    }
}
