using System.Buffers.Binary;
using System.Security.Cryptography;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// scePsmDrmGetRifInfo as SceNpDrm 3.74 answers it: the syscall passes its five arguments (license, content ID,
/// account, start time, expiration time) to kscePsmDrmGetRifInfo, which checks the license as for its installation
/// (signature, header, activation) and answers with that check. A license whose signature does not verify (0x808F0104,
/// the form of the license plugins' licenses) gets nothing written; on any other answer a license with the "PSM-RIF"
/// magic has its fields written, the times (ms since 1970, big-endian) as SceRtcTicks (v * 1000 + 0xDCBFFEFF2BC000,
/// 0 as 0x7FFFFFFFFFFFFFFF). A synthetic FW 3.74 SceNpDrm carries a test key where the module keeps its PSM license
/// key (first segment +0x10064).
/// </summary>
[Collection("guest")]
public sealed class PsmRifInfoTests : IDisposable
{
    private const int Signature = unchecked((int)0x808F0104), BadLicense = unchecked((int)0x808F0105);
    private const int OtherAccount = unchecked((int)0x808F0108), OtherActivation = unchecked((int)0x808F0109);
    private const int Unsupported = unchecked((int)0x80020004), Invalid = unchecked((int)0x808F0101);
    private const uint NpDrm374ModuleNid = 0xAC6D91D5, PsmLicenseModulusOffset = 0x10064;
    private const ulong TickEpoch1970 = 0xDCBFFEFF2BC000, Never = 0x7FFFFFFFFFFFFFFF;
    private const string ContentId = "UV0000-NPOA00001_00-0000000000000000";
    private const byte Untouched = 0xEE;

    /// <summary>A 2048-bit test key (exponent 65537) standing for the module's.</summary>
    private static readonly RSA s_key = RSA.Create(2048);

    private readonly HleHarness _harness;
    private readonly uint _license, _content, _account, _start, _end;

    public PsmRifInfoTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "psmrifinfo");
        byte[] modulus = s_key.ExportParameters(false).Modulus!;
        Array.Reverse(modulus); // the module's layout: little-endian words
        SceModuleBuilder npdrm = new SceModuleBuilder("SceNpDrm").ModuleNid(NpDrm374ModuleNid)
            .Payload((int)PsmLicenseModulusOffset, modulus);
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "npdrm.skprx"), npdrm.Build());
        FirmwareKernelImage.Forget(_harness.Root);
        _license = _harness.Alloc(PsmLicense.Size);
        _content = _harness.Alloc(0x30);
        _account = _harness.Alloc(8);
        _start = _harness.Alloc(8);
        _end = _harness.Alloc(8);
    }

    public void Dispose()
    {
        FirmwareKernelImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private static byte[] License(ulong accountId, ulong startMs = 1000, ulong endMs = 2000, Action<byte[]>? change = null,
        bool sign = true)
    {
        byte[] license = new byte[PsmLicense.Size];
        "PSM-RIF\0"u8.CopyTo(license);
        BinaryPrimitives.WriteUInt32BigEndian(license.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(license.AsSpan(0x10), accountId);
        BinaryPrimitives.WriteUInt64BigEndian(license.AsSpan(0x20), startMs);
        BinaryPrimitives.WriteUInt64BigEndian(license.AsSpan(0x28), endMs);
        System.Text.Encoding.ASCII.GetBytes(ContentId).CopyTo(license, 0x50);
        change?.Invoke(license);
        if (sign)
        {
            s_key.SignData(license.AsSpan(0, 0x300), license.AsSpan(0x300, 0x100), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        return license;
    }

    /// <summary>The user call with <paramref name="license"/> and all five outputs set to <see cref="Untouched"/>.</summary>
    private int GetRifInfo(byte[] license)
    {
        license.CopyTo(GuestMemory.Span(_license, PsmLicense.Size));
        GuestMemory.Fill(_content, Untouched, 0x30);
        GuestMemory.Fill(_account, Untouched, 8);
        GuestMemory.Fill(_start, Untouched, 8);
        GuestMemory.Fill(_end, Untouched, 8);
        return Call(Services, "PsmDrmGetRifInfo", _license, _content, _account, _start, _end);
    }

    private void AssertNothingWritten()
    {
        Assert.Equal(Enumerable.Repeat(Untouched, 0x30).ToArray(), GuestMemory.ReadSpan(_content, 0x30).ToArray());
        Assert.Equal(0xEEEEEEEEEEEEEEEEUL, GuestMemory.Read<ulong>(_account));
        Assert.Equal(0xEEEEEEEEEEEEEEEEUL, GuestMemory.Read<ulong>(_start));
        Assert.Equal(0xEEEEEEEEEEEEEEEEUL, GuestMemory.Read<ulong>(_end));
    }

    private void AssertFields(ulong account, ulong start, ulong end)
    {
        Assert.Equal(ContentId, GuestMemory.ReadCString(_content, 0x30));
        Assert.Equal(account, GuestMemory.Read<ulong>(_account));
        Assert.Equal(start, GuestMemory.Read<ulong>(_start));
        Assert.Equal(end, GuestMemory.Read<ulong>(_end));
    }

    [Fact]
    public void The_synthetic_module_key_is_the_one_read()
    {
        Assert.NotNull(SystemServicesVsh.PsmLicenseModulusFor(_harness.Root));
    }

    [Fact]
    public void A_signed_license_gives_its_fields_with_the_times_as_rtc_ticks()
    {
        Assert.Equal(0, GetRifInfo(License(0)));
        AssertFields(0, 1000 * 1000 + TickEpoch1970, 2000 * 1000 + TickEpoch1970);
    }

    [Fact]
    public void Zero_times_are_never_and_never_stays_never()
    {
        Assert.Equal(0, GetRifInfo(License(0, startMs: 0, endMs: 0)));
        AssertFields(0, Never, Never);
        Assert.Equal(0, GetRifInfo(License(0, startMs: Never, endMs: Never)));
        AssertFields(0, Never, Never);
        Assert.Equal(0, GetRifInfo(License(0, startMs: 0, endMs: 5)));
        AssertFields(0, Never, 5 * 1000 + TickEpoch1970);
    }

    /// <summary>The licenses of the license plugins carry no signature: 0x808F0104, nothing written, with or without
    /// the magic (the console's answer; a plugin that wants another answer hooks the export).</summary>
    [Fact]
    public void A_license_whose_signature_fails_gets_nothing_written()
    {
        Assert.Equal(Signature, GetRifInfo(License(0x0123456789ABCDEFUL, sign: false)));
        AssertNothingWritten();
        Assert.Equal(Signature, GetRifInfo(License(0, change: l => l.AsSpan(0, 8).Clear(), sign: false)));
        AssertNothingWritten();
        byte[] tampered = License(0);
        tampered[0x60] ^= 1;
        Assert.Equal(Signature, GetRifInfo(tampered));
        AssertNothingWritten();
    }

    /// <summary>After the signature the fields are written whatever the rest of the check answers, as long as the
    /// license has the magic.</summary>
    [Fact]
    public void The_fields_are_written_with_the_answers_after_the_signature()
    {
        Assert.Equal(OtherAccount, GetRifInfo(License(5)));
        AssertFields(5, 1000 * 1000 + TickEpoch1970, 2000 * 1000 + TickEpoch1970);
        Assert.Equal(OtherActivation, GetRifInfo(License(0, change: l => l[0x30] = 1)));
        AssertFields(0, 1000 * 1000 + TickEpoch1970, 2000 * 1000 + TickEpoch1970);
        Assert.Equal(OtherActivation, GetRifInfo(License(0, change: l => l[0x18] = 1)));
        AssertFields(0, 1000 * 1000 + TickEpoch1970, 2000 * 1000 + TickEpoch1970);
        Assert.Equal(BadLicense, GetRifInfo(License(0, change: l => BinaryPrimitives.WriteUInt32BigEndian(l.AsSpan(8), 2))));
        AssertFields(0, 1000 * 1000 + TickEpoch1970, 2000 * 1000 + TickEpoch1970);
        Assert.Equal(BadLicense, GetRifInfo(License(0, startMs: 3000)));
        AssertFields(0, 3000 * 1000 + TickEpoch1970, 2000 * 1000 + TickEpoch1970);
        // no magic: the header answer, nothing written
        Assert.Equal(BadLicense, GetRifInfo(License(0, change: l => l[0] = (byte)'X')));
        AssertNothingWritten();
    }

    [Fact]
    public void With_activation_data_the_secure_part_is_not_available_and_the_fields_are_written()
    {
        string host = _harness.Emu.Vfs.Resolve("tm0:psmdrm/act.dat")!;
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllBytes(host, new byte[0x400]);
        Assert.Equal(Unsupported, GetRifInfo(License(0)));
        AssertFields(0, 1000 * 1000 + TickEpoch1970, 2000 * 1000 + TickEpoch1970);
    }

    [Fact]
    public void Null_outputs_are_skipped_and_a_null_license_refused()
    {
        License(0).CopyTo(GuestMemory.Span(_license, PsmLicense.Size));
        Assert.Equal(0, Call(Services, "PsmDrmGetRifInfo", _license, 0u, 0u, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "PsmDrmGetRifInfo", 0u, _content, _account, _start, _end));
        Assert.Equal(Invalid, Call(Services, "kscePsmDrmGetRifInfo", 0u, 0u, 0u, 0u, 0u));
    }
}
