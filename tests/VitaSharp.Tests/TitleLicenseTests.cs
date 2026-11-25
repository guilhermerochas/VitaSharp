using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// An encrypted PS Vita application whose license is not installed (GitHub issue #2: a game installed without its
/// license closed the emulator with an unhandled "needs a klicensee" exception). On the console the application
/// manager reads the title's license before it creates the process; without it the launch fails with 0x80800004 (FW
/// 3.74 SceAppMgr) and the shell shows its launch-error dialog. A module of the application loaded at run time fails
/// with the module manager's loader error. The installers keep the title and warn, and the libraries mark it.
/// </summary>
[Collection("guest")]
public sealed class TitleLicenseTests(GuestMemoryFixture fixture) : IDisposable
{
    private const string TitleId = "LICX00001";
    private readonly HleHarness _harness = new(fixture, "license");

    public void Dispose() => _harness.Dispose();

    /// <summary>A minimal SCE header: an APP SELF with one segment, encrypted (an NPDRM retail executable) or
    /// plaintext (a fake SELF, as homebrew and decrypted dumps have).</summary>
    internal static byte[] AppSelf(bool encrypted)
    {
        byte[] file = new byte[0x200];
        BitConverter.TryWriteBytes(file.AsSpan(0), 0x00454353u);   // "SCE\0"
        BitConverter.TryWriteBytes(file.AsSpan(32 + 24), 0x80ul);   // app info
        BitConverter.TryWriteBytes(file.AsSpan(32 + 32), 0xA0ul);   // ELF header
        BitConverter.TryWriteBytes(file.AsSpan(32 + 40), 0xE0ul);   // program headers
        BitConverter.TryWriteBytes(file.AsSpan(32 + 56), 0x100ul);  // segment infos
        BitConverter.TryWriteBytes(file.AsSpan(0x80 + 12), 8u);     // SELF type APP
        BitConverter.TryWriteBytes(file.AsSpan(0xA0 + 44), (ushort)1);
        BitConverter.TryWriteBytes(file.AsSpan(0x100 + 24), encrypted ? 1u : 2u);
        return file;
    }

    /// <summary>An application in ux0:app with the given eboot.bin, and its license where the firmware keeps it.</summary>
    private string InstallApplication(byte[] eboot, bool workBin = false, bool rif = false)
    {
        string dir = Path.Combine(_harness.Root, "ux0", "app", TitleId);
        Directory.CreateDirectory(Path.Combine(dir, "sce_sys", "package"));
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), eboot);
        if (workBin)
        {
            File.WriteAllBytes(Path.Combine(dir, "sce_sys", "package", "work.bin"), new byte[0x200]);
        }

        if (rif)
        {
            string licenses = Path.Combine(_harness.Root, "ux0", "license", "app", TitleId);
            Directory.CreateDirectory(licenses);
            File.WriteAllBytes(Path.Combine(licenses, "6488b73b912a753a492e2714e9b38bc7.rif"), new byte[0x200]);
        }

        return dir;
    }

    [Fact]
    public void DecryptingAnEncryptedSelfWithoutKlicenseeIsTheLicenseError()
    {
        Assert.Throws<SelfLicenseMissingException>(() => SelfDecryptor.ToElf(AppSelf(encrypted: true), null));
        Assert.Throws<SelfLicenseMissingException>(() => SelfDecryptor.ToElf(AppSelf(encrypted: true), new byte[16]));
    }

    [Fact]
    public void LoadingAnUnlicensedTitleFailsWithTheAppMgrErrorAndAMessage()
    {
        string dir = InstallApplication(AppSelf(encrypted: true));

        TitleLicenseMissingException e = Assert.Throws<TitleLicenseMissingException>(() => _harness.Emu.LoadTitle(dir, "eboot.bin"));

        Assert.Equal(TitleId, e.TitleId);
        Assert.Equal(unchecked((int)0x80800004), e.Error);
        Assert.Equal(unchecked((int)0x80800004), Emulator.ModuleLoadError(e));
        Assert.Null(_harness.Emu.MainModule);
        Assert.Contains("0x80800004", e.UserMessage);
        Assert.Contains("work.bin", e.UserMessage);
        Assert.Contains("zRIF", e.UserMessage);
        Assert.Contains(TitleId, e.UserMessage);
    }

    [Fact]
    public void TheShellsLaunchOfAnUnlicensedTitleFailsWithoutAProcess()
    {
        InstallApplication(AppSelf(encrypted: true));
        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrDeclareShellProcess2", _harness.Str("NPXS19999")));
        while (_harness.Emu.Vita.TakeShellEvent() != null)
        {
        }

        int processesBefore = _harness.Emu.Vita.Processes.Count;

        // the shell's request posts event 6; the second stage the shell answers with loads the executable and fails
        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrLaunchAppByName", 0x20000, _harness.Str(TitleId), 0u));
        int appId = BitConverter.ToInt32(_harness.Emu.Vita.TakeShellEvent()!, 4);

        int result = Call(_harness.Module<AppMgrShell>(), "sceAppMgrLaunchAppByName2ndStage", appId, 0u, 0x20000, 0, 0u);

        Assert.Equal(TitleLicense.NoLicenseError, result);
        Assert.Null(_harness.Emu.Vita.TakeShellEvent());
        Assert.Equal(processesBefore, _harness.Emu.Vita.Processes.Count);
    }

    [Fact]
    public void AnEncryptedModuleWithoutLicenseIsTheModuleManagersLoaderError()
    {
        string data = Path.Combine(_harness.Root, "ux0", "data");
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, "encrypted.suprx"), AppSelf(encrypted: true));
        File.WriteAllBytes(Path.Combine(data, "junk.suprx"), new byte[0x100]);
        SystemMisc systemMisc = _harness.Module<SystemMisc>();
        _harness.OnGuest(_ =>
        {
            uint status = _harness.Word(9);
            Assert.Equal(SceError.ILLEGAL_ELF_HEADER, Call(systemMisc, "sceKernelLoadStartModule", "ux0:data/encrypted.suprx", 0u, 0u, 0u, 0u, new Ptr<int>(status)));
            Assert.Equal(SceError.ILLEGAL_ELF_HEADER, Call(systemMisc, "sceKernelLoadModule", "ux0:data/encrypted.suprx", 0u, 0u));
            Assert.Equal(SceError.ILLEGAL_ELF_HEADER, Call(systemMisc, "sceKernelOpenModule", "ux0:data/encrypted.suprx", 0u, 0u, 0u));
            Assert.Equal(SceError.ILLEGAL_SELF_HEADER, Call(systemMisc, "sceKernelLoadStartModule", "ux0:data/junk.suprx", 0u, 0u, 0u, 0u, new Ptr<int>(status)));
            Assert.Equal(SceError.ILLEGAL_SELF_HEADER, Call(systemMisc, "sceKernelLoadModule", "ux0:data/junk.suprx", 0u, 0u));
        });
    }

    [Fact]
    public void ALicenseIsFoundAsWorkBinOrInTheLicenseFolder()
    {
        string fs = _harness.Root;
        string dir = InstallApplication(AppSelf(encrypted: true));
        Assert.True(TitleLicense.NeedsLicense(dir));
        Assert.True(TitleLicense.IsMissing(fs, dir));
        Assert.NotNull(TitleLicense.InstallWarning(fs, dir, TitleId));

        InstallApplication(AppSelf(encrypted: true), rif: true);
        Assert.False(TitleLicense.IsMissing(fs, dir));
        Assert.EndsWith(".rif", TitleLicense.FindFile(fs, dir, TitleId));

        InstallApplication(AppSelf(encrypted: true), workBin: true);
        Assert.EndsWith("work.bin", TitleLicense.FindFile(fs, dir, TitleId));
        Assert.Null(TitleLicense.InstallWarning(fs, dir, TitleId));
    }

    [Fact]
    public void APlaintextExecutableNeedsNoLicense()
    {
        string fs = _harness.Root;
        string dir = InstallApplication(AppSelf(encrypted: false));
        Assert.False(TitleLicense.NeedsLicense(dir));
        Assert.False(TitleLicense.IsMissing(fs, dir));
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), [0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1, 0]);
        Assert.False(TitleLicense.IsMissing(fs, dir));
    }

    [Fact]
    public void TheLibrariesMarkAnUnlicensedTitleOnBothApps()
    {
        // the desktop launcher and the Android app read the same data layout (<root>/fs)
        var layout = new VitaSharp.Mobile.Core.Storage.DataLayout(Path.Combine(_harness.Root, "apps"));
        string dir = Path.Combine(layout.Apps, TitleId);
        Directory.CreateDirectory(Path.Combine(dir, "sce_sys", "package"));
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), AppSelf(encrypted: true));
        File.WriteAllBytes(Path.Combine(dir, "sce_sys", "param.sfo"),
            PkgFixture.Sfo(("TITLE_ID", TitleId), ("TITLE", "Encrypted"), ("CATEGORY", "gd"), ("APP_VER", "01.00")));

        Assert.True(Assert.Single(InstalledContent.Scan(layout.Fs)).LicenseMissing);
        Assert.True(Assert.Single(VitaSharp.Desktop.Launcher.GameLibrary.Scan(layout.Fs)).LicenseMissing);
        Assert.True(Assert.Single(VitaSharp.Mobile.Core.Library.GameLibrary.Scan(layout)).LicenseMissing);

        File.WriteAllBytes(Path.Combine(dir, "sce_sys", "package", "work.bin"), new byte[0x200]);
        Assert.False(Assert.Single(VitaSharp.Desktop.Launcher.GameLibrary.Scan(layout.Fs)).LicenseMissing);
        Assert.False(Assert.Single(VitaSharp.Mobile.Core.Library.GameLibrary.Scan(layout)).LicenseMissing);
    }
}
