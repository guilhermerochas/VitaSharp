using System.Buffers.Binary;
using System.Security.Cryptography;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The rest of SceNpDrmForDriver as SceNpDrm 3.74 answers it: the license file names (the AES name of the account
/// with the module's key, the fixed name of account 0 without the firmware), the activation data read and checked
/// as GetRifInfo's activation check reads it, the current account, and the secure services (signatures, re-signing,
/// writing an activation) refusing after their checks.
/// </summary>
[Collection("guest")]
public sealed class NpDrmKernelRestTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80870001), ActSize = unchecked((int)0x80870002);
    private const int BadData = unchecked((int)0x80870005), NoActivation = unchecked((int)0x80870006);
    private const int OtherAccount = unchecked((int)0x80870008), OtherUnit = unchecked((int)0x80870009);
    private const int GameCard = unchecked((int)0x8087000A), NoAccount = unchecked((int)0x8087000C);
    private const int Unsupported = unchecked((int)0x80020004);
    private const ulong Never = 0x7FFFFFFFFFFFFFFF;

    private readonly HleHarness _harness = new(fixture, "npdrmrest");

    public void Dispose()
    {
        string? act = _harness.Emu.Vfs.Resolve("tm0:npdrm/act.dat");
        if (act != null && File.Exists(act))
        {
            File.Delete(act);
        }

        _harness.Dispose();
    }

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private void Activate(ulong accountId, uint version = 1, int size = 0x1038)
    {
        string act = _harness.Emu.Vfs.Resolve("tm0:npdrm/act.dat", forWrite: true)!;
        Directory.CreateDirectory(Path.GetDirectoryName(act)!);
        byte[] data = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(data, 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), version);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), accountId);
        if (size >= 0x880)
        {
            ConsoleIdentity.OpenPsId.CopyTo(data.AsSpan(0x850));
            BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0x878), 1000);
        }

        File.WriteAllBytes(act, data);
    }

    private uint License(ushort flags, ushort type = 1, ulong accountId = 0)
    {
        byte[] rif = new byte[0x200];
        BinaryPrimitives.WriteUInt16BigEndian(rif.AsSpan(4), type);
        BinaryPrimitives.WriteUInt16BigEndian(rif.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(rif.AsSpan(8), accountId);
        "UP0000-PCSE00000_00-0000000000000000"u8.CopyTo(rif.AsSpan(0x10));
        uint address = _harness.Alloc(0x200);
        rif.CopyTo(GuestMemory.Span(address, 0x200));
        return address;
    }

    private static string Name(uint name) => GuestMemory.ReadCString(name, 0x30);

    [Fact]
    public void The_rif_name_is_the_account_encrypted_with_the_module_key()
    {
        byte[] key = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();
        byte[] block = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), 0x0123456789ABCDEFUL);
        using Aes aes = Aes.Create();
        aes.Key = key;
        string expected = Convert.ToHexStringLower(aes.EncryptEcb(block, PaddingMode.None)) + ".rif";
        Assert.Equal(expected, SystemServicesVsh.RifFileName(key, 0, 0x0123456789ABCDEFUL));
        block[0] = 1;
        string fixedName = Convert.ToHexStringLower(aes.EncryptEcb(block, PaddingMode.None)) + ".rif";
        Assert.Equal(fixedName, SystemServicesVsh.RifFileName(key, 1, 0x0123456789ABCDEFUL));
        // without the firmware's key only the fixed name of account 0 is known
        Assert.Equal("6488b73b912a753a492e2714e9b38bc7.rif", SystemServicesVsh.RifFileName(null, 1, 0));
        Assert.Null(SystemServicesVsh.RifFileName(null, 0, 5));
    }

    [Fact]
    public void The_key_is_read_only_from_the_374_module()
    {
        byte[] elf = new byte[0x200 + 0x11290 + 0x10];
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x1C), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x40 + 4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x40 + 0x10), 0x11290 + 0x10);
        elf[0x200 + 0x11290] = 0xAB;
        Assert.Equal(0xAB, SystemServicesVsh.RifNameKeyFrom(elf, 0xAC6D91D5)![0]);
        Assert.Null(SystemServicesVsh.RifNameKeyFrom(elf, 0x12345678));
    }

    [Fact]
    public void Rif_names_by_the_kernel_entries()
    {
        uint name = _harness.Alloc(0x30);
        Assert.Equal(Invalid, Call(Services, "ksceNpDrmGetFixedRifName", 0u, 0u, 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceNpDrmGetFixedRifName", name, 0u, 0u, 0u));
        Assert.Equal("6488b73b912a753a492e2714e9b38bc7.rif", Name(name));
        // no account signed in and none given: 0x8087000C; another account needs the firmware's key
        Assert.Equal(NoAccount, Call(Services, "ksceNpDrmGetRifName", name, 0u, 0u, 0u));
        Assert.Equal(Unsupported, Call(Services, "ksceNpDrmGetRifName", name, 0u, 5u, 0u));
        Assert.Equal(0, Call(Services, "ksceNpDrmGetRifNameForInstall", name, License(0), 0u));
        Assert.Equal("UP0000-PCSE00000_00-0000000000000000.rif", Name(name));
        Assert.Equal(0, Call(Services, "ksceNpDrmGetRifNameForInstall", name, License(0xD, accountId: 9), 1u));
        Assert.Equal("6488b73b912a753a492e2714e9b38bc7.rif", Name(name));
        Assert.Equal(BadData, Call(Services, "ksceNpDrmGetRifNameForInstall", name, License(0x3), 0u));
        Assert.Equal(BadData, Call(Services, "ksceNpDrmGetRifNameForInstall", name, License(0, type: 0), 1u));
        Assert.Equal(OtherUnit, Call(Services, "ksceNpDrmGetRifNameForInstall", name, License(0x100), 0u));
        Assert.Equal(GameCard, Call(Services, "ksceNpDrmGetRifNameForInstall", name, License(0x400), 0u));
        Assert.Equal(Invalid, Call(Services, "ksceNpDrmGetRifNameForInstall", name, License(0), 2u));
    }

    [Fact]
    public void Activation_data_is_read_and_checked()
    {
        uint data = _harness.Alloc(0x1038);
        GuestMemory.Fill(data, 0xEE, 0x1038);
        Assert.Equal(Invalid, Call(Services, "ksceNpDrmReadActData", 0u));
        Assert.Equal(NoActivation, Call(Services, "ksceNpDrmReadActData", data));
        Assert.Equal(0u, GuestMemory.R32(data));
        Assert.Equal(NoActivation, Call(Services, "ksceNpDrmUpdateActData"));
        Activate(0x1111, size: 0x100);
        Assert.Equal(ActSize, Call(Services, "ksceNpDrmUpdateActData"));
        Activate(0x1111);
        Assert.Equal(OtherAccount, Call(Services, "ksceNpDrmUpdateActData"));
        Assert.Equal(0, Call(Services, "ksceNpDrmUpdateAccountId", 0x1111u, 0u));
        Assert.Equal(0, Call(Services, "ksceNpDrmUpdateActData"));
        Assert.Equal(0, Call(Services, "ksceNpDrmReadActData", data));
        Assert.Equal(0x1111UL, GuestMemory.Read<ulong>(data + 8));
        uint name = _harness.Alloc(0x30);
        Assert.Equal(Unsupported, Call(Services, "ksceNpDrmGetRifName", name, 0u, 0u, 0u));
        uint version = _harness.Word(0xFFFF), account = _harness.Alloc(8), start = _harness.Alloc(8), end = _harness.Alloc(8);
        Assert.Equal(0, Call(Services, "ksceNpDrmCheckActData", version, 0u, account, start, end));
        Assert.Equal(1u, GuestMemory.R32(version));
        Assert.Equal(0x1111UL, GuestMemory.Read<ulong>(account));
        Assert.Equal(0UL, GuestMemory.Read<ulong>(start));
        Assert.Equal(Never, GuestMemory.Read<ulong>(end));
        // version 2 is bound to this unit's OpenPSID and has a period
        Activate(0x1111, version: 2);
        Assert.Equal(0, Call(Services, "ksceNpDrmCheckActData", version, 0u, account, start, end));
        Assert.Equal(1000UL * 1000 + 0xDCBFFEFF2BC000UL, GuestMemory.Read<ulong>(end));
        uint removed = _harness.Alloc(8);
        Assert.Equal(0, Call(Services, "ksceNpDrmRemoveActData", removed));
        Assert.Equal(0x1111UL, GuestMemory.Read<ulong>(removed));
        Assert.Equal(NoActivation, Call(Services, "ksceNpDrmRemoveActData", removed));
        Assert.Equal(0UL, GuestMemory.Read<ulong>(removed));
    }

    [Fact]
    public void Secure_services_never_validate()
    {
        Assert.Equal(Invalid, Call(Services, "ksceNpDrmWriteActData", 0u, 0u));
        Assert.Equal(Unsupported, Call(Services, "ksceNpDrmWriteActData", _harness.Alloc(0x1040), 0u));
        Assert.Equal(Invalid, Call(Services, "ksceNpDrmVerifyRif", 0u));
        Assert.Equal(GameCard, Call(Services, "ksceNpDrmVerifyRif", License(0x400)));
        Assert.Equal(Unsupported, Call(Services, "ksceNpDrmVerifyRif", License(0)));
        Assert.Equal(Invalid, Call(Services, "ksceNpDrmPresetRifProvisionalFlag", License(0), 2u));
        Assert.Equal(Unsupported, Call(Services, "ksceNpDrmPresetRifProvisionalFlag", License(0), 1u));
        uint key = _harness.Alloc(0x10);
        GuestMemory.Fill(key, 0xEE, 0x10);
        Assert.Equal(BadData, Call(Services, "ksceNpDrmGetLegacyDocKey", 0u, _harness.Alloc(0x100), 0x80u, key));
        Assert.Equal(0u, GuestMemory.R32(key));
        Assert.Equal(Unsupported, Call(Services, "ksceNpDrmGetLegacyDocKey", 0u, _harness.Alloc(0x100), 0x100u, key));
        Assert.Equal(Invalid, Call(Services, "ksceNpDrmEbootSignature", 0u, 1u, 1u));
        Assert.Equal(Unsupported, Call(Services, "ksceNpDrmEbootSignature", _harness.Str("ux0:pspemu/x"), 1u, 1u));
        Assert.Equal(0, Call(Services, "ksceNpDrmIsLooseAccountBind"));
        Assert.Equal(0, Call(Services, "ksceNpDrmPackageSetGameExist", 1u));
    }
}
