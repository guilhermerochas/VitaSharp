using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A launch whose executable cannot be loaded fails the launch call itself, as in the console's appmgr (its launch
/// creates the process and loads the executable before it returns; the FW 3.74 shell's launch job hands the error to
/// its launch-error dialog, 0x8129456C, and stays on the LiveArea). VitaSharp answered the launch with success and an
/// application id and failed on a host thread afterwards: the shell kept waiting on its start screen for an
/// application id every later appmgr query answered 0x80802012 for (Cave Squid's Unity runtime, still PFS-encrypted).
/// </summary>
[Collection("guest")]
public sealed class AppLaunchFailureTests(GuestMemoryFixture fixture) : IDisposable
{
    private const string BadTitle = "BADX00001";
    private readonly HleHarness _harness = new(fixture, "launchfail");

    public void Dispose() => _harness.Dispose();

    /// <summary>An installed application whose eboot.bin is no SELF (an encrypted or damaged file).</summary>
    private void InstallUnloadableApplication()
    {
        string dir = Path.Combine(_harness.Root, "ux0", "app", BadTitle);
        Directory.CreateDirectory(dir);
        byte[] junk = new byte[0x2000];
        new Random(7).NextBytes(junk);
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), junk);
    }

    /// <summary>The caller becomes the shell, with an empty event queue.</summary>
    private void BecomeShell()
    {
        Assert.Equal(0, Call(_harness.Module<AppMgrShell>(), "sceAppMgrDeclareShellProcess2", _harness.Str("NPXS19999")));
        while (_harness.Emu.Vita.TakeShellEvent() != null)
        {
        }
    }

    [Fact]
    public void LaunchByPathOfAnUnloadableExecutableFailsWithoutAnApplication()
    {
        InstallUnloadableApplication();
        BecomeShell();
        Emulator shell = _harness.Emu;
        int processesBefore = shell.Vita.Processes.Count;
        // the shell's launch parameters: size 0x80, +0x28 where the new application's status goes
        uint launchParam = _harness.Alloc(0x80);
        uint status = _harness.Alloc(0x80);
        GuestMemory.Fill(status, 0xCC, 0x80);
        GuestMemory.W32(launchParam, 0x80);
        GuestMemory.W32(launchParam + 0x28, status);

        int result = Call(_harness.Module<SystemSoftware>(), "sceAppMgrLaunchAppByPath4", $"ux0:app/{BadTitle}/eboot.bin",
            _harness.Str(BadTitle), 0x2000000u, 0u, 0u, launchParam);

        Assert.Equal(SceError.ILLEGAL_SELF_HEADER, result);
        // no application id reached the shell: its status record is untouched and it got no process event
        Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(status + 0x30));
        Assert.Null(shell.Vita.TakeShellEvent());
        Assert.Equal(processesBefore, shell.Vita.Processes.Count);
        Assert.DoesNotContain(shell.Vita.Processes, p => p.TitleId == BadTitle);
    }

    [Fact]
    public void LaunchByNameOfAnUnloadableExecutableFailsWithoutAProcess()
    {
        InstallUnloadableApplication();
        BecomeShell();
        Emulator shell = _harness.Emu;
        int processesBefore = shell.Vita.Processes.Count;

        int result = Call(_harness.Module<AppMgrShell>(), "sceAppMgrLaunchAppByName", 0x20000, _harness.Str(BadTitle), 0u);

        Assert.Equal(SceError.ILLEGAL_SELF_HEADER, result);
        Assert.Null(shell.Vita.TakeShellEvent());
        Assert.Equal(processesBefore, shell.Vita.Processes.Count);
    }

    /// <summary>The loader's errors, shared by the launch and the kernel module manager.</summary>
    [Fact]
    public void ModuleLoadErrorsAreTheModuleManagers()
    {
        Assert.Equal(SceError.MODULEMGR_NOENT, Emulator.ModuleLoadError(new FileNotFoundException("app0:eboot.bin")));
        Assert.Equal(SceError.ILLEGAL_SELF_HEADER, Emulator.ModuleLoadError(new InvalidDataException("Not a SELF/ELF file")));
        Assert.Equal(SceError.ILLEGAL_ELF_HEADER, Emulator.ModuleLoadError(new InvalidOperationException("Encrypted application SELF needs a klicensee (license)")));
    }
}
