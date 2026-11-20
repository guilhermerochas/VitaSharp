using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblACMgrForKernel and SceSblACMgrForDriver as acmgr.skprx 3.74 answers them: the capability bits of the
/// process's auth info, the authority IDs of the system's programs, the media type and mount tables, and the file
/// attribute conversions of the file systems, by the caller's access level.
/// </summary>
[Collection("guest")]
public sealed class SblAcMgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NoProcess = unchecked((int)0x800F0903), Invalid = unchecked((int)0x800F0916);
    private const int NotFound = unchecked((int)0x800F0902), MediaInvalid = unchecked((int)0x800F090E);
    private const int Unsupported = unchecked((int)0x800F0925), Permission = unchecked((int)0x8001000D);
    private const int BadMode = unchecked((int)0x80010016), ModeNotFound = unchecked((int)0x80010002);
    private const int UnknownPid = 0x7777;

    private readonly HleHarness _harness = new(fixture, "acmgrk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    /// <summary>A program with the given authority and capability bits set (numbered from the most significant bit).</summary>
    private void Program(ulong authorityId, params int[] bits)
    {
        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        foreach (int bit in bits)
        {
            capability[bit >> 3] |= (byte)(0x80 >> (bit & 7));
        }

        _harness.Emu.Access = new ProgramAccess(authorityId, capability, false);
    }

    private int Query(string name, int pid = 0) => Call(Services, name, pid);

    /// <summary>Every export of both libraries is served (by NID).</summary>
    [Theory]
    [InlineData(0x9EDAF856u)]
    [InlineData(0xC2D1F2FCu)]
    [InlineData(0x31C23B66u)]
    [InlineData(0x0948F41Cu)]
    [InlineData(0x930CD037u)]
    [InlineData(0x570D6AD3u)]
    [InlineData(0x8A54DF3Au)]
    [InlineData(0x3F99279Fu)]
    [InlineData(0x6C5AB07Fu)]
    [InlineData(0x05FDC646u)]
    [InlineData(0x1298C647u)]
    [InlineData(0x5AC59172u)]
    [InlineData(0x426A4E8Cu)]
    [InlineData(0xF9FEF5F0u)]
    [InlineData(0x991FDC15u)]
    [InlineData(0xD442962Eu)]
    [InlineData(0x4322F188u)]
    [InlineData(0x2AE6CF27u)]
    [InlineData(0x6210D745u)]
    [InlineData(0x0606B87Eu)]
    [InlineData(0x7C2AF978u)]
    [InlineData(0x96AF69BDu)]
    [InlineData(0xFB2AC5B4u)]
    [InlineData(0xD7AD8471u)]
    [InlineData(0x8241AB5Cu)]
    [InlineData(0xFBA1A256u)]
    [InlineData(0x1948E9DBu)]
    [InlineData(0x2E992B02u)]
    [InlineData(0xBBA13D9Cu)]
    [InlineData(0xE87D1777u)]
    [InlineData(0x75AAF981u)]
    [InlineData(0xBE5667C5u)]
    public void Exports_are_served(uint nid)
    {
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void Capability_bits_answer_the_auth_info()
    {
        Program(0x2800000000000025UL, 0, 3, 0x1F, 0x80, 0x81);
        Assert.Equal(1, Query("ksceSblACMgrIsRootProgram"));
        Assert.Equal(0, Query("ksceSblACMgrIsSystemProgram2"));
        Assert.Equal(1, Query("ksceSblACMgrIsNonGameProgram"));
        Assert.Equal(1, Query("ksceSblACMgrIsNonGameOrGameProgram"));
        Assert.Equal(1, Query("SblAcMgrCapability1F"));
        Assert.Equal(0, Query("SblAcMgrCapability20"));
        Assert.Equal(1, Query("SblAcMgrCapability80"));
        Assert.Equal(1, Query("SblAcMgrCapability81"));
        Assert.Equal(0, Query("SblAcMgrCapability87"));
        Assert.Equal(1, Call(Services, "ksceSblACMgrHasCapability", 0, 0x81u));
        Assert.Equal(0, Call(Services, "ksceSblACMgrHasCapability", 0, 0x82u));
        Assert.Equal(NotFound, Call(Services, "SblAcMgrRequireCapability", 0, 1u));
        Assert.Equal(Invalid, Call(Services, "SblAcMgrRequireCapability", 0, 2u));
        Assert.Equal(NotFound, Call(Services, "SblAcMgrRequireCapability", 0, 0x85u));
        Program(0x2800000000000025UL, 1);
        Assert.Equal(0, Call(Services, "SblAcMgrRequireCapability", 0, 1u));
        Assert.Equal(1, Query("ksceSblACMgrIsAllowedUsbSerial"));
    }

    [Fact]
    public void Authority_ids_name_the_system_programs()
    {
        Program(0x2800000000000001UL);
        Assert.Equal(1, Query("ksceSblACMgrIsSceShell"));
        Assert.Equal(1, Query("ksceSblACMgrIsNotSandboxed"));
        Assert.Equal(0, Query("ksceSblACMgrIsGameProgram"));
        Program(0x2100000000000001UL, 2);
        Assert.Equal(1, Query("ksceSblACMgrIsGameProgram"));
        Assert.Equal(0, Query("ksceSblACMgrIsFself"));
        Assert.Equal(0, Query("ksceSblACMgrIsSceShell"));
        Assert.Equal(1, Query("SblAcMgrIsGameOrNotSandboxed"));
        Program(0x2F00000000000002UL);
        Assert.Equal(1, Query("ksceSblACMgrIsFself"));
        Assert.Equal(0, Query("ksceSblACMgrIsGameProgram"));
        Program(ProgramAccess.PsmRuntimeAuthorityId);
        Assert.Equal(1, Query("ksceSblACMgrIsPSMProgram"));
        Assert.Equal(1, Query("ksceSblACMgrIsPSMRuntime"));
        Assert.Equal(0, Query("ksceSblACMgrIsSIEApp"));
        Program(0x210000101CD20008UL);
        Assert.Equal(1, Query("ksceSblACMgrIsSIEApp"));
        Assert.Equal(1, Query("ksceSblACMgrIsPSMProgram"));
        Assert.Equal(0, Query("ksceSblACMgrIsPSMRuntime"));
        Program(0x2800000000008005UL);
        Assert.Equal(1, Query("ksceSblACMgrIsWebCoreOrWebKitProcess"));
        Program(0x2800000000007009UL);
        Assert.Equal(1, Query("ksceSblACMgrIsPspEmu"));
        Program(0x2800800000000003UL);
        Assert.Equal(1, Query("ksceSblACMgrIsUpdaterUISetupperOrPkgInstallerSpawn"));
        Program(0x2801000000000003UL);
        Assert.Equal(1, Query("SblAcMgrIsAuthority2801"));
        Program(0x2800000000000010UL);
        Assert.Equal(1, Query("ksceSblACMgrIsMiniSettingsForQA"));
        Assert.Equal(1, Query("ksceSblACMgrIsAllowedExtendedMemory"));
        Program(0x2800000000000025UL, 0x82);
        Assert.Equal(1, Query("ksceSblACMgrIsAllowedExtendedMemory"));
        Assert.Equal(0, Call(Services, "ksceSblACMgrIsDevelopmentMode"));
    }

    [Fact]
    public void A_process_that_does_not_exist_has_no_rights()
    {
        Program(0x2800000000000001UL, 0, 1, 3);
        Assert.Equal(0, Query("ksceSblACMgrIsRootProgram", UnknownPid));
        Assert.Equal(0, Query("ksceSblACMgrIsSceShell", UnknownPid));
        uint authId = _harness.Alloc(8);
        Assert.Equal(Invalid, Call(Services, "ksceSblACMgrGetProcessProgramAuthId", UnknownPid, authId));
        Assert.Equal(Invalid, Call(Services, "ksceSblACMgrGetProcessProgramAuthId", 0, 0u));
        Assert.Equal(0, Call(Services, "ksceSblACMgrGetProcessProgramAuthId", 0, authId));
        Assert.Equal(0x2800000000000001UL, GuestMemory.Read<ulong>(authId));
        Assert.Equal(1, Query("ksceSblACMgrIsRootProgram", KernelSpace.KernelPid));
        Assert.Equal(0, Call(Services, "ksceSblACMgrGetProcessProgramAuthId", KernelSpace.KernelPid, authId));
        Assert.Equal(0x2808000000000001UL, GuestMemory.Read<ulong>(authId));
        uint attribute = Attribute(1, 1);
        Assert.Equal(NoProcess, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", UnknownPid, attribute, 0x106u));
    }

    [Theory]
    [InlineData("ux0:app/PCSE00000/eboot.bin", 0x17u)]
    [InlineData("ux0:/patch/PCSE00000", 0x18u)]
    [InlineData("ux0:data/x", 0x19u)]
    [InlineData("ux0:user/00", 0u)]
    [InlineData("ux0:music", 0xCu)]
    [InlineData("host0:x", 7u)]
    [InlineData("sd0:", 1u)]
    [InlineData("gro0:app/PCSE00000", 0xDu)]
    [InlineData("uma0:x", 0x1Bu)]
    public void Media_types_follow_the_path_table(string path, uint expected)
    {
        uint type = _harness.Word(0xFFFF);
        Assert.Equal(0, Call(Services, "ksceSblACMgrGetMediaType", _harness.Str(path), type));
        Assert.Equal(expected, GuestMemory.R32(type));
    }

    [Fact]
    public void Unknown_media_and_mounts_are_refused()
    {
        uint type = _harness.Word(0xFFFF);
        Assert.Equal(NoProcess, Call(Services, "ksceSblACMgrGetMediaType", _harness.Str("zz0:x"), type));
        Assert.Equal(0u, GuestMemory.R32(type));
        Assert.Equal(MediaInvalid, Call(Services, "ksceSblACMgrGetMediaType", 0u, type));
        uint mode = _harness.Word(0);
        Assert.Equal(0, Call(Services, "SblAcMgrGetMountMode", 0, _harness.Str("sd0:"), mode));
        Assert.Equal(0x186u, GuestMemory.R32(mode));
        Assert.Equal(0, Call(Services, "SblAcMgrGetMountMode", 0, _harness.Str("lma0:"), mode));
        Assert.Equal(0x1000000u, GuestMemory.R32(mode));
        Assert.Equal(NotFound, Call(Services, "SblAcMgrGetMountMode", 0, _harness.Str("zz0:"), mode));
        Assert.Equal(Invalid, Call(Services, "SblAcMgrGetMountMode", 0, 0u, mode));
    }

    [Fact]
    public void Mounting_needs_a_system_program_or_the_kernel()
    {
        Program(0x2100000000000001UL, 2);
        uint ux0 = _harness.Str("ux0:");
        // a call from kernel code (no user thread): the kernel level mounts anything known
        Assert.Equal(1, Call(Services, "SblAcMgrIsMountAllowed", 0, ux0));
        Assert.Equal(0, Call(Services, "SblAcMgrIsMountAllowed", 0, _harness.Str("zz0:")));
        int fromGame = -1;
        RunOnUserThread(() => fromGame = Call(Services, "SblAcMgrIsMountAllowed", 0, ux0));
        Assert.Equal(0, fromGame);
        Program(0x2800000000000025UL, 3);
        int fromNonGame = -1;
        RunOnUserThread(() => fromNonGame = Call(Services, "SblAcMgrIsMountAllowed", 0, ux0));
        Assert.Equal(1, fromNonGame);
    }

    private void RunOnUserThread(Action body)
    {
        GuestRun run = _harness.Start(_ => body());
        _harness.Join(run);
    }

    /// <summary>A SceSblACMgrFsAttribute { type, value, size } with its value storage.</summary>
    private uint Attribute(uint type, uint size, uint value = 0)
    {
        uint attribute = _harness.Alloc(0x20);
        uint storage = attribute + 0x10;
        GuestMemory.W32(attribute, type);
        GuestMemory.W32(attribute + 4, storage);
        GuestMemory.W32(attribute + 8, size);
        GuestMemory.W32(storage, value);
        return attribute;
    }

    private static uint Value(uint attribute) => GuestMemory.R32(attribute + 0x10);

    [Fact]
    public void Modes_convert_to_fat_attributes_by_the_thread_level()
    {
        uint attribute = Attribute(1, 1, 0xF8);
        Assert.Equal(0, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, attribute, 0x106u));
        Assert.Equal(0xFCu, Value(attribute));
        Assert.Equal(0, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, attribute, 0x1000000u));
        Assert.Equal(0x0Fu, Value(attribute));
        Assert.Equal(BadMode, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, attribute, 0x2u));
        Assert.Equal(Unsupported, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, Attribute(7, 1), 0x106u));
        Assert.Equal(Invalid, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, Attribute(9, 1), 0x106u));
        Assert.Equal(Invalid, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, 0u, 0x106u));

        // a user thread: only read-write (0x180) and read-only (0x100); bit 2 of the old value needs the kernel
        Program(0x2100000000000001UL, 2);
        uint user = Attribute(4, 2, 0);
        int readOnly = 0;
        int fromKernelValue = 0;
        uint kernelValue = Attribute(4, 2, 4);
        RunOnUserThread(() =>
        {
            readOnly = Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, user, 0x100u);
            fromKernelValue = Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, kernelValue, 0x100u);
        });
        Assert.Equal(0, readOnly);
        Assert.Equal(1u, Value(user));
        Assert.Equal(Permission, fromKernelValue);
    }

    [Fact]
    public void Exfat_modes_follow_the_program_level()
    {
        Program(0x2100000000000001UL, 2);
        uint attribute = Attribute(6, 2);
        // a game (level 0x20): read-write is value 0; the kernel's flags 0x300000 are refused
        Assert.Equal(0, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, attribute, 0x180u));
        Assert.Equal(0u, Value(attribute));
        Assert.Equal(BadMode, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", 0, attribute, 0x180u | 0x100000u));
        uint mode = _harness.Word(0);
        Assert.Equal(0, Call(Services, "SblAcMgrFsAttributeToMode", attribute, mode));
        Assert.Equal(0x186u | 0x2000u, GuestMemory.R32(mode));
        // the kernel's auth info (level 0x80): a write-protected executable (0x104) is value 5, 0x100000 bit 0x4000
        Assert.Equal(0, Call(Services, "ksceSblACMgrConvertModeToFsAttribute", KernelSpace.KernelPid, attribute, 0x104u | 0x100000u));
        Assert.Equal(0x4005u, Value(attribute));
        Assert.Equal(0, Call(Services, "SblAcMgrFsAttributeToMode", attribute, mode));
        Assert.Equal(0x104u | 0x100000u | 0x2000u, GuestMemory.R32(mode));
    }

    [Fact]
    public void Access_checks_use_the_value_tables()
    {
        uint readOnly = Attribute(1, 1, 1);
        // kernel thread: value 1 grants read, write, execute (0xB)
        Assert.Equal(0, Call(Services, "SblAcMgrCheckFsAccess", 0, readOnly, 0xBu));
        Assert.Equal(Invalid, Call(Services, "SblAcMgrCheckFsAccess", 0, Attribute(4, 1, 1), 1u));
        Assert.Equal(Unsupported, Call(Services, "SblAcMgrCheckFsAccess", 0, Attribute(7, 1, 1), 1u));
        Program(0x2100000000000001UL, 2);
        int write = 0;
        int read = -1;
        RunOnUserThread(() =>
        {
            write = Call(Services, "SblAcMgrCheckFsAccess", 0, readOnly, 2u);
            read = Call(Services, "SblAcMgrCheckFsAccess", 0, readOnly, 1u);
        });
        Assert.Equal(Permission, write);
        Assert.Equal(0, read);
    }

    [Fact]
    public void Attribute_copies_and_explicit_levels()
    {
        uint from = Attribute(2, 1, 0xF);
        uint to = Attribute(1, 1, 0xF0);
        Assert.Equal(0, Call(Services, "SblAcMgrCopyFsAttribute", 0, to, from));
        Assert.Equal(0xF7u, Value(to));
        Assert.Equal(Invalid, Call(Services, "SblAcMgrCopyFsAttribute", 0, to, 0u));
        Assert.Equal(0, Call(Services, "SblAcMgrClearRawFsAttribute", 0, to, from, 0x10000u));
        Assert.Equal(0u, Value(to));
        Assert.Equal(Invalid, Call(Services, "SblAcMgrClearRawFsAttribute", 0, to, from, 0u));
        uint explicitLevel = Attribute(1, 1);
        Assert.Equal(0, Call(Services, "SblAcMgrModeToFsAttributeForLevel", 0x10104u, 0x80u, explicitLevel));
        Assert.Equal(5u, Value(explicitLevel));
        Assert.Equal(ModeNotFound, Call(Services, "SblAcMgrModeToFsAttributeForLevel", 0x10104u, 0x20u, explicitLevel));
        Assert.Equal(Invalid, Call(Services, "SblAcMgrModeToFsAttributeForLevel", 0x180u, 0x8u, explicitLevel));
        Assert.Equal(Unsupported, Call(Services, "SblAcMgrModeToFsAttributeForLevel", 0x180u, 0x20u, Attribute(6, 2)));
    }
}
