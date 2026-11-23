using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A user plugin's hook on the export of a firmware user module the HLE serves (option "Enable Kernel Plugins"). On the
/// Vita SceLibKernel (os0:us/libkernel.suprx) is in every process's module list, so
/// taiHookFunctionExport(&amp;ref, "SceLibKernel", library, function, hook) finds it, patches its code and every call of
/// the process goes through the hook. VitaSharp lists a shell of the module (its name, module NID and export tables,
/// each entry an HLE stub) the first time the framework looks at the process's modules; before, the search returned
/// 0x90010002 (not found). The shell is older than the loaded modules (the newest module is still the program's), the
/// hook takes the process's import stubs of the function and its release gives them back, and nothing changes in a
/// process without the module's library imported. Synthetic: a firmware os0:us module of <see cref="SceModuleBuilder"/>
/// exporting sceClibStrnlen of SceLibKernel, a user module calling it through its import.
/// </summary>
[Collection("guest")]
public sealed class PluginHookUserShellTests : IDisposable
{
    private const uint LibKernelLib = 0xCAE9ACE6, Strnlen = 0xAC595E68, LibKernelModuleNid = 0x0C5A7F13;
    private const int ErrorNotFound = unchecked((int)0x90010002);

    /// <summary>The byte offset of the reference variable's address in <see cref="ChainHook"/>.</summary>
    private const uint RefLiteral = 12 * 4;

    private readonly HleHarness _harness;

    public PluginHookUserShellTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "ushell", kernelPlugins: true);
        FirmwareUserImage.Forget(_harness.Root);
    }

    public void Dispose()
    {
        FirmwareUserImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    private PluginFramework Framework => _harness.Module<PluginFramework>();

    /// <summary>The firmware's os0:us/libkernel.suprx (its code is never run: the HLE serves its functions).</summary>
    private void InstallFirmwareLibKernel()
    {
        SceModuleBuilder libkernel = new SceModuleBuilder("SceLibKernel").ModuleNid(LibKernelModuleNid);
        int body = libkernel.ReturnConstant(0);
        libkernel.Export("SceLibKernel", LibKernelLib, 0x0001, (Strnlen, body));
        string us = Path.Combine(_harness.Root, "os0", "us");
        Directory.CreateDirectory(us);
        File.WriteAllBytes(Path.Combine(us, "libkernel.suprx"), libkernel.Build());
        FirmwareUserImage.Forget(_harness.Root);
    }

    /// <summary>An A32 hook that continues the chain as TAI_CONTINUE does and returns that times 10 plus
    /// <paramref name="number"/>; the address of its reference variable goes at <see cref="RefLiteral"/>.</summary>
    private static int ChainHook(SceModuleBuilder module, byte number)
    {
        return module.Arm(
            0xE92D4010,          // push {r4, lr}
            0xE59FC024,          // ldr r12, [pc, #36]   the reference variable (word 12)
            0xE59CC000,          // ldr r12, [r12]       the reference
            0xE59C4000,          // ldr r4, [r12]        next
            0xE3540000,          // cmp r4, #0
            0x059CC008,          // ldreq r12, [r12, #8] old
            0x1594C004,          // ldrne r12, [r4, #4]  next->func
            0xE12FFF3C,          // blx r12
            0xE3A0100A,          // mov r1, #10
            0xE0000091,          // mul r0, r1, r0
            0xE2800000 | number, // add r0, r0, #number
            0xE8BD8010,          // pop {r4, pc}
            0x00000000);         // the reference variable's address
    }

    private LoadedModule LoadUser(string file, SceModuleBuilder module)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "data");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), module.Build());
        return _harness.Emu.LoadModule($"ur0:data/{file}");
    }

    private sealed record Setup(uint CallStrnlen, uint Hook, uint Ref, uint Text, LoadedModule Client);

    /// <summary>The program's module calling sceClibStrnlen through its import, and the plugin's hook.</summary>
    private Setup Load()
    {
        SceModuleBuilder client = new SceModuleBuilder("StrnlenClient").Import("SceLibKernel", LibKernelLib, Strnlen);
        int call = client.CallImport(0, 0);
        LoadedModule clientModule = LoadUser("client.suprx", client);
        var plugin = new SceModuleBuilder("ShellHookPlugin");
        int hook = ChainHook(plugin, 1);
        LoadedModule pluginModule = LoadUser("plugin.suprx", plugin);
        uint hookAddress = plugin.AddressOf(pluginModule.Segments[0].Address, hook);
        uint reference = _harness.Word(0);
        GuestMemory.W32(hookAddress + RefLiteral, reference);
        _harness.Emu.InvalidateCode(hookAddress, 13 * 4);
        return new Setup(SceModuleBuilder.FunctionAddress(clientModule.Segments[0].Address, call), hookAddress, reference, _harness.Str("abc"), clientModule);
    }

    private uint CallAsUser(uint function, uint first, uint second)
    {
        uint result = 0;
        _harness.OnGuest(t => result = t.CallGuest(function, first, second));
        return result;
    }

    private int HookExport(Setup setup, string module)
    {
        uint args = _harness.Alloc(0x14);
        GuestMemory.W32(args, 0x14);
        GuestMemory.W32(args + 4, _harness.Str(module));
        GuestMemory.W32(args + 8, LibKernelLib);
        GuestMemory.W32(args + 0xC, Strnlen);
        GuestMemory.W32(args + 0x10, setup.Hook);
        return Call(Framework, "HookFunctionExportForUser", setup.Ref, args);
    }

    [Fact]
    public void AUserExportHookOnAFirmwareUserModuleTheHleServesFindsItAndTakesTheProcesssCalls()
    {
        InstallFirmwareLibKernel();
        Setup setup = Load();
        Assert.Equal(3u, CallAsUser(setup.CallStrnlen, setup.Text, 10));

        int uid = HookExport(setup, "SceLibKernel");

        Assert.True(uid > 0, $"export hook returned 0x{uid:X8}");
        // the program's call goes through the hook, which continues to the HLE function (3) and appends its number
        Assert.Equal(31u, CallAsUser(setup.CallStrnlen, setup.Text, 10));
        // a module of the process loaded after the hook binds to it too
        SceModuleBuilder later = new SceModuleBuilder("LaterClient").Import("SceLibKernel", LibKernelLib, Strnlen);
        int laterCall = later.CallImport(0, 0);
        LoadedModule laterModule = LoadUser("later.suprx", later);
        uint callLater = SceModuleBuilder.FunctionAddress(laterModule.Segments[0].Address, laterCall);
        Assert.Equal(31u, CallAsUser(callLater, setup.Text, 10));

        Assert.Equal(0, Call(Framework, "HookReleaseForUser", uid, GuestMemory.R32(setup.Ref)));
        Assert.Equal(3u, CallAsUser(setup.CallStrnlen, setup.Text, 10));
        Assert.Equal(3u, CallAsUser(callLater, setup.Text, 10));
    }

    [Fact]
    public void TheShellIsListedWithTheFirmwaresTablesAndIsOlderThanTheLoadedModules()
    {
        InstallFirmwareLibKernel();
        Setup setup = Load();

        // module information by name: the firmware module's NID; the export entry is an HLE stub of the function
        uint info = _harness.Alloc(0x38);
        GuestMemory.W32(info, 0x38);
        Assert.Equal(0, Call(Framework, "GetModuleInfoForUser", _harness.Str("SceLibKernel"), info));
        Assert.Equal(LibKernelModuleNid, GuestMemory.R32(info + 8));
        int shellUid = (int)GuestMemory.R32(info + 4);
        LoadedModule shell = Assert.Single(Framework.Shells);
        Assert.Equal(shellUid, shell.Uid);
        Assert.Equal("SceLibKernel", shell.Name);
        uint function = _harness.Alloc(4);
        Assert.Equal(0, Call(Framework, "GetModuleExportFuncForUser", _harness.Str("SceLibKernel"), LibKernelLib, Strnlen, function));
        uint entry = GuestMemory.R32(function);
        Assert.Equal(0xEF000000u, GuestMemory.R32(entry) & 0xFF000000);
        Assert.Equal(0xE12FFF1Eu, GuestMemory.R32(entry + 4));
        Assert.Equal(Strnlen, GuestMemory.R32(entry + 8));

        // the newest module is the program's last one, never the shell (it was preloaded)
        GuestMemory.W32(info, 0x38);
        Assert.Equal(0, Call(Framework, "GetModuleInfoForUser", 0u, info));
        Assert.Equal("ShellHookPlugin", GuestMemory.ReadCString(info + 0xC, 27));

        // looked at again: the same shell at the same address
        Assert.Equal(0, Call(Framework, "GetModuleInfoForUser", _harness.Str("SceLibKernel"), info));
        Assert.Equal(shellUid, (int)GuestMemory.R32(info + 4));
        Assert.Single(Framework.Shells);
        Assert.Equal(setup.Client.Uid, _harness.Emu.Kernel.Modules[setup.Client.Uid].Uid);
    }

    [Fact]
    public void WithoutTheFirmwareModuleTheSearchStillReportsNotFound()
    {
        Setup setup = Load();

        Assert.Equal(ErrorNotFound, HookExport(setup, "SceLibKernel"));
        Assert.Empty(Framework.Shells);
        Assert.Equal(3u, CallAsUser(setup.CallStrnlen, setup.Text, 10));
    }
}
