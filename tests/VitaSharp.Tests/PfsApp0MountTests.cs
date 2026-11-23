using System.Buffers.Binary;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Package;
using VitaSharp.Core.Package.Pfs;

namespace VitaSharp.Tests;

/// <summary>
/// An application whose folder is still a PFS image, as the own runtime of a Unity PSM title is (ux0:psm/&lt;id&gt;/
/// runtime, copied by the PSM promote as the package has it; its eboot.bin is no SELF until the PFS layer is removed:
/// "Not a SELF/ELF file" closed the emulator, 22/11/2026). The console's application manager mounts app0 through
/// ScePfsMgr with the title key it takes from the licence by SceNpDrm (FW 3.74 SceAppMgr/SceNpDrm, study only), whose
/// signature check and key decryption are the secure processor's: on the emulated unit that step answers 0x80020004
/// and the launch fails with a message. The tests stand in for the secure processor with a test key, never the
/// licence's +0x50 field.
/// </summary>
[Collection("guest")]
public sealed class PfsApp0MountTests : IDisposable
{
    private const string PsmTitle = "PFSM00001", RuntimeId = "PFSR00001", AppId = "PFSA00001";
    private static readonly byte[] s_testKey = Encoding.ASCII.GetBytes("test-title-key!!");
    private static readonly byte[] s_moduleData = Enumerable.Range(0, 0x8000 + 0x123).Select(i => (byte)(i * 7 + 3)).ToArray();
    private readonly HleHarness _harness;

    public PfsApp0MountTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "pfsapp0");
    }

    public void Dispose()
    {
        _harness.Dispose();
        string views = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_harness.Root)))!, "cache", "pfs");
        if (!Directory.Exists(views))
        {
            return;
        }

        foreach (string view in Directory.GetDirectories(views))
        {
            string name = Path.GetFileName(view);
            if (name.StartsWith(RuntimeId + "-", StringComparison.Ordinal) || name.StartsWith(AppId + "-", StringComparison.Ordinal))
            {
                Directory.Delete(view, true);
            }
        }
    }

    /// <summary>A PS Vita licence of kind <paramref name="kind"/> (0xD: a free licence, as every Unity runtime's is)
    /// whose +0x50 field is not the title key (an original licence holds it encrypted there).</summary>
    private static byte[] License(byte kind = 0xD, ushort extraFlags = 0, ulong account = 0)
    {
        byte[] license = new byte[LicenseTitleKey.LicenseSize];
        BinaryPrimitives.WriteUInt16BigEndian(license, 1);
        BinaryPrimitives.WriteUInt16BigEndian(license.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(license.AsSpan(6), (ushort)(kind | extraFlags));
        BinaryPrimitives.WriteUInt64LittleEndian(license.AsSpan(8), account);
        Encoding.ASCII.GetBytes("IP9100-PFSR00001_00-TESTRUNTIME00000").CopyTo(license, 0x10);
        Encoding.ASCII.GetBytes("encrypted-key-50").CopyTo(license, 0x50);
        return license;
    }

    /// <summary>The test's secure processor: the test key for the test licence.</summary>
    private static byte[]? TestSecureProcessor(byte[] license) =>
        Encoding.ASCII.GetString(license, 0x10, 9) == "IP9100-PF" ? s_testKey : null;

    private static IReadOnlyList<PfsImageBuilder.Entry> RuntimeFiles(string titleId) =>
    [
        new("eboot.bin", SyntheticTitle.Elf(), Encrypted: true),
        new("module/data.bin", s_moduleData, Encrypted: true),
        new("sce_sys/param.sfo", SyntheticTitle.ParamSfo(titleId), Encrypted: false),
    ];

    /// <summary>A PSM title with its own runtime as a PFS image made with <paramref name="imageKey"/>, and the runtime's
    /// licence where the promote stores it (ux0:license/app/&lt;PSM title&gt;).</summary>
    private string InstallPsmTitle(byte[] imageKey, bool license = true)
    {
        string title = Path.Combine(_harness.Root, "ux0", "psm", PsmTitle);
        PfsImageBuilder.Write(Path.Combine(title, "runtime"), imageKey, RuntimeFiles(RuntimeId));
        if (license)
        {
            string licenses = Path.Combine(_harness.Root, "ux0", "license", "app", PsmTitle);
            Directory.CreateDirectory(licenses);
            File.WriteAllBytes(Path.Combine(licenses, "6488b73b912a753a492e2714e9b38bc7.rif"), License());
        }

        return title;
    }

    [Fact]
    public void WithoutTheSecureProcessorAPfsApplicationFailsWithItsErrorAndAMessage()
    {
        string title = InstallPsmTitle(s_testKey);

        TitleKeyException e = Assert.Throws<TitleKeyException>(() => _harness.Emu.LoadTitle(title));

        Assert.Equal(RuntimeId, e.TitleId);
        Assert.Equal(unchecked((int)0x80020004), e.Error);
        Assert.Equal(unchecked((int)0x80020004), Emulator.ModuleLoadError(e));
        Assert.True(Emulator.IsModuleLoadFailure(e));
        Assert.Null(_harness.Emu.MainModule);
        Assert.Contains(RuntimeId, e.UserMessage);
        Assert.Contains("0x80020004", e.UserMessage);
        Assert.Equal(e.UserMessage, TitleLaunchFailure.Message(RuntimeId, e, "last.log"));
    }

    [Fact]
    public void ThePsmTitlesOwnRuntimeMountsWithTheKeyOfItsLicence()
    {
        string title = InstallPsmTitle(s_testKey);
        _harness.Emu.SecureProcessorRifKey = TestSecureProcessor;

        _harness.Emu.LoadTitle(title);

        Assert.NotNull(_harness.Emu.MainModule);
        Assert.Equal(RuntimeId, _harness.Emu.TitleId);
        Assert.Equal(s_testKey, _harness.Emu.Klicensee);
        string? module = _harness.Emu.Vfs.Resolve("app0:module/data.bin");
        Assert.NotNull(module);
        Assert.Equal(s_moduleData, File.ReadAllBytes(module));
        Assert.Equal(SyntheticTitle.ParamSfo(RuntimeId), File.ReadAllBytes(_harness.Emu.Vfs.Resolve("app0:sce_sys/param.sfo")!));
    }

    [Fact]
    public void AnApplicationCopiedAsAPfsImageMountsTheSameWay()
    {
        string app = Path.Combine(_harness.Root, "ux0", "app", AppId);
        PfsImageBuilder.Write(app, s_testKey, RuntimeFiles(AppId));
        Directory.CreateDirectory(Path.Combine(app, "sce_sys", "package"));
        File.WriteAllBytes(Path.Combine(app, "sce_sys", "package", "work.bin"), License());
        Assert.True(TitleLicense.NeedsLicense(app));

        Assert.Equal(unchecked((int)0x80020004), Assert.Throws<TitleKeyException>(() => _harness.Emu.LoadTitle(app)).Error);

        _harness.Emu.SecureProcessorRifKey = TestSecureProcessor;
        _harness.Emu.LoadTitle(app);
        Assert.NotNull(_harness.Emu.MainModule);
        Assert.Equal(s_moduleData, File.ReadAllBytes(_harness.Emu.Vfs.Resolve("app0:module/data.bin")!));
    }

    [Fact]
    public void AKeyThatDoesNotOpenTheImageIsALicenceError()
    {
        string title = InstallPsmTitle(Encoding.ASCII.GetBytes("another-title-ky"));
        _harness.Emu.SecureProcessorRifKey = TestSecureProcessor;

        TitleKeyException e = Assert.Throws<TitleKeyException>(() => _harness.Emu.LoadTitle(title));

        Assert.Equal(LicenseTitleKey.BadLicense, e.Error);
        Assert.IsType<PfsKeyMismatchException>(e.InnerException);
        Assert.Null(_harness.Emu.MainModule);
    }

    [Fact]
    public void APfsImageWithoutLicenceIsAppMgrsMissingLicenceError()
    {
        string title = InstallPsmTitle(s_testKey, license: false);
        _harness.Emu.SecureProcessorRifKey = TestSecureProcessor;

        Assert.Equal(TitleLicense.NoLicenseError, Assert.Throws<TitleLicenseMissingException>(() => _harness.Emu.LoadTitle(title)).Error);
    }

    [Fact]
    public void TheLicencesChecksComeBeforeTheSecureProcessor()
    {
        Func<byte[], byte[]?> secure = TestSecureProcessor;
        byte[] activation = new byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(activation.AsSpan(8), 0x1111);

        Assert.Equal(LicenseTitleKey.BadLicense, LicenseTitleKey.Get(License().AsSpan(0, 0x98), [], secure, out _));
        Assert.Equal(LicenseTitleKey.BadData, LicenseTitleKey.Get(License(kind: 1), [], secure, out _));
        Assert.Equal(LicenseTitleKey.GameCard, LicenseTitleKey.Get(License(extraFlags: 0x400), [], secure, out _));
        Assert.Equal(LicenseTitleKey.NotActivated, LicenseTitleKey.Get(License(kind: 0), [], secure, out _));
        Assert.Equal(LicenseTitleKey.NoActivationData, LicenseTitleKey.Get(License(kind: 2, account: 0x1111), [], secure, out _));
        Assert.Equal(LicenseTitleKey.OtherAccount, LicenseTitleKey.Get(License(kind: 2, account: 0x2222), activation, secure, out _));

        Assert.Equal(0, LicenseTitleKey.Get(License(kind: 2, account: 0x1111), activation, secure, out byte[]? key));
        Assert.Equal(s_testKey, key);
        Assert.Equal(0, LicenseTitleKey.Get(License(kind: 0), activation, secure, out _));
        Assert.Equal(LicenseTitleKey.SecureProcessorUnavailable, LicenseTitleKey.Get(License(), [], null, out key));
        Assert.Null(key);
        Assert.Equal(LicenseTitleKey.SecureProcessorUnavailable, LicenseTitleKey.Get(License(kind: 2, account: 0x1111), activation, null, out _));
    }

    [Fact]
    public void TheViewIsMadeOnceAndRemadeForAnotherImage()
    {
        string app = Path.Combine(_harness.Root, "image");
        string view = Path.Combine(_harness.Root, "view");
        PfsImageBuilder.Write(app, s_testKey, RuntimeFiles(AppId));

        PfsApp0View.Open(app, s_testKey, view);
        string decrypted = Path.Combine(view, "module", "data.bin");
        DateTime made = File.GetLastWriteTimeUtc(decrypted);
        File.WriteAllBytes(decrypted, s_moduleData);
        File.SetLastWriteTimeUtc(decrypted, made);
        PfsApp0View.Open(app, s_testKey, view);
        Assert.Equal(made, File.GetLastWriteTimeUtc(decrypted));
        Assert.False(File.Exists(Path.Combine(view, "sce_sys", "param.sfo"))); // plain files stay in the folder

        byte[] changed = (byte[])s_moduleData.Clone();
        changed[5] ^= 0xFF;
        Directory.Delete(app, true);
        PfsImageBuilder.Write(app, s_testKey, [new("eboot.bin", SyntheticTitle.Elf(), true), new("module/data.bin", changed, true)]);
        PfsApp0View.Open(app, s_testKey, view);
        Assert.Equal(changed, File.ReadAllBytes(decrypted));
        Assert.False(Directory.Exists(view + ".tmp"));
    }

    [Fact]
    public void AnyOtherLoadFailureNamesTheTitleTheReasonAndTheLog()
    {
        string message = TitleLaunchFailure.Message("ABCD00001", new InvalidDataException("Not a SELF/ELF file"), "logs/last.log");

        Assert.Contains("ABCD00001", message);
        Assert.Contains("Not a SELF/ELF file", message);
        Assert.Contains("logs/last.log", message);
        Assert.Equal(new TitleLicenseMissingException("ABCD00001", new IOException()).UserMessage,
                     TitleLaunchFailure.Message("ABCD00001", new TitleLicenseMissingException("ABCD00001", new IOException()), null));
    }
}
