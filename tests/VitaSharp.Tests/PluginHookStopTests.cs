using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// What happens to a plugin's hooks and data injections when the plugin stops and unloads (option "Enable Kernel
/// Plugins"), as the framework's published source does it: the framework keeps no list of patches by module, so it is
/// the plugin's module_stop that releases them, in the plugin's own order, while the patched code is still in place;
/// a release in any order puts back exactly what that patch changed. A plugin unloaded without releasing leaves its
/// patches installed (the framework drops a process's patches only when the process ends, without writing the bytes
/// back). Synthetic: a user target module with an A32 function, a user plugin whose module_stop is A32 code calling the
/// framework's user release functions through its imports.
/// </summary>
[Collection("guest")]
public sealed class PluginHookStopTests : IDisposable
{
    private const uint TargetLib = 0x0BAD7A61, TargetNid = 0x2468ACE1;
    private const uint FrameworkLib = 0x0A2B3C4D;
    private const uint HookReleaseForUser = 0x1C30E799, InjectReleaseForUser = 0x21A4C8A6;

    /// <summary>The literal words of <see cref="ReleasingStop"/>: injection UID, hook UID, the two hooks' reference
    /// variables, the callers of the two releases.</summary>
    private const int InjectionUidWord = 20, HookUidWord = 21, FirstRefWord = 22, SecondRefWord = 23, InjectCallerWord = 24, HookCallerWord = 25;

    private readonly HleHarness _harness;

    public PluginHookStopTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "khooks", kernelPlugins: true);
    }

    public void Dispose()
    {
        _harness.Dispose();
    }

    private PluginFramework Framework => _harness.Module<PluginFramework>();

    private static uint LoadLiteral(int register, int at, int literal) => 0xE59F0000 | (uint)register << 12 | (uint)((literal - at - 2) * 4);

    /// <summary>
    /// module_stop in A32: releases the injection, then the newer hook, then the first one (each result ORed into the
    /// return value, so 0 means every release succeeded), as a plugin writes its module_stop.
    /// </summary>
    private static uint[] ReleasingStop()
    {
        const uint Blx12 = 0xE12FFF3C, Accumulate = 0xE1844000, LoadThroughR1 = 0xE5911000;
        return
        [
            0xE92D4010,                                   // 0 push {r4, lr}
            0xE3A04000,                                   // 1 mov r4, #0
            LoadLiteral(0, 2, InjectionUidWord),          // 2 ldr r0, injection uid
            LoadLiteral(12, 3, InjectCallerWord),         // 3 ldr r12, inject release caller
            Blx12,                                        // 4 blx r12
            Accumulate,                                   // 5 orr r4, r4, r0
            LoadLiteral(0, 6, HookUidWord),               // 6 ldr r0, hook uid
            LoadLiteral(1, 7, SecondRefWord),             // 7 ldr r1, &second ref
            LoadThroughR1,                                // 8 ldr r1, [r1]
            LoadLiteral(12, 9, HookCallerWord),           // 9 ldr r12, hook release caller
            Blx12,                                        // 10 blx r12
            Accumulate,                                   // 11 orr r4, r4, r0
            LoadLiteral(0, 12, HookUidWord),              // 12 ldr r0, hook uid
            LoadLiteral(1, 13, FirstRefWord),             // 13 ldr r1, &first ref
            LoadThroughR1,                                // 14 ldr r1, [r1]
            LoadLiteral(12, 15, HookCallerWord),          // 15 ldr r12, hook release caller
            Blx12,                                        // 16 blx r12
            Accumulate,                                   // 17 orr r4, r4, r0
            0xE1A00004,                                   // 18 mov r0, r4
            0xE8BD8010,                                   // 19 pop {r4, pc}
            0, 0, 0, 0, 0, 0,                             // 20-25 literals
        ];
    }

    private string Write(string file, SceModuleBuilder module)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "data");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), module.Build());
        return $"ur0:data/{file}";
    }

    private uint CallAsUser(uint function, uint first = 0, uint second = 0)
    {
        uint result = 0;
        _harness.OnGuest(t => result = t.CallGuest(function, first, second));
        return result;
    }

    /// <summary>The target function (returns r0 + r1), a data word to inject into, the plugin with two hooks (return 77
    /// and 88) on the function's export and an injection, everything installed; returns what the test needs.</summary>
    private sealed record Installed(uint Target, uint[] Original, uint Data, LoadedModule Plugin, uint StopCode, int HookUid, int InjectionUid);

    private Installed Install(bool releasesInStop)
    {
        var target = new SceModuleBuilder("StopTarget");
        int add = target.Arm(0xE0800001, 0xE2800000, 0xE2800000, 0xE12FFF1E); // add r0, r0, r1; add r0, #0 (x2); bx lr
        target.Export("StopTargetLib", TargetLib, 0x0001, (TargetNid, add));
        LoadedModule targetModule = _harness.Emu.LoadModule(Write("stoptarget.suprx", target));
        uint targetFunction = target.AddressOf(targetModule.Segments[0].Address, add);

        SceModuleBuilder plugin = new SceModuleBuilder("StopPlugin").Import("taihen", FrameworkLib, InjectReleaseForUser, HookReleaseForUser);
        int injectCaller = plugin.CallImport(0, 0);
        int hookCaller = plugin.CallImport(0, 1);
        int firstHook = plugin.Arm(0xE3A0004D, 0xE12FFF1E);  // mov r0, #77; bx lr
        int secondHook = plugin.Arm(0xE3A00058, 0xE12FFF1E); // mov r0, #88; bx lr
        int stop = releasesInStop ? plugin.Arm(ReleasingStop()) : plugin.ReturnConstant(0);
        plugin.Start(0).Stop(stop);
        LoadedModule pluginModule = _harness.Emu.LoadModule(Write("stopplugin.suprx", plugin));
        uint pluginBase = pluginModule.Segments[0].Address;

        uint[] refs = [_harness.Word(0), _harness.Word(0)];
        uint args = _harness.Alloc(0x14);
        GuestMemory.W32(args, 0x14);
        GuestMemory.W32(args + 4, _harness.Str("StopTarget"));
        GuestMemory.W32(args + 8, TargetLib);
        GuestMemory.W32(args + 0xC, TargetNid);
        uint[] original = [GuestMemory.R32(targetFunction), GuestMemory.R32(targetFunction + 4), GuestMemory.R32(targetFunction + 8)];
        GuestMemory.W32(args + 0x10, plugin.AddressOf(pluginBase, firstHook));
        int hookUid = Call(Framework, "HookFunctionExportForUser", refs[0], args);
        Assert.True(hookUid > 0, $"hook returned 0x{hookUid:X8}");
        GuestMemory.W32(args + 0x10, plugin.AddressOf(pluginBase, secondHook));
        Assert.Equal(hookUid, Call(Framework, "HookFunctionExportForUser", refs[1], args));

        uint data = _harness.Word(0x11111111);
        uint replacement = _harness.Word(0x22222222);
        int injectionUid = Call(Framework, "InjectAbsForUser", data, replacement, 4u);
        Assert.True(injectionUid > 0, $"injection returned 0x{injectionUid:X8}");

        uint stopCode = plugin.AddressOf(pluginBase, stop);
        if (releasesInStop)
        {
            GuestMemory.W32(stopCode + InjectionUidWord * 4, (uint)injectionUid);
            GuestMemory.W32(stopCode + HookUidWord * 4, (uint)hookUid);
            GuestMemory.W32(stopCode + FirstRefWord * 4, refs[0]);
            GuestMemory.W32(stopCode + SecondRefWord * 4, refs[1]);
            GuestMemory.W32(stopCode + InjectCallerWord * 4, SceModuleBuilder.FunctionAddress(pluginBase, injectCaller));
            GuestMemory.W32(stopCode + HookCallerWord * 4, SceModuleBuilder.FunctionAddress(pluginBase, hookCaller));
            _harness.Emu.InvalidateCode(stopCode, 26 * 4);
        }

        Assert.Equal(77u, CallAsUser(targetFunction, 3, 4));
        Assert.Equal(0x22222222u, GuestMemory.R32(data));
        Assert.Equal((1, 1), Framework.PatchesOf(_harness.Emu.Pid));
        return new Installed(targetFunction, original, data, pluginModule, stopCode, hookUid, injectionUid);
    }

    private int StopUnload(LoadedModule plugin, out int stopStatus)
    {
        SystemMisc modulemgr = _harness.Module<SystemMisc>();
        uint status = _harness.Word(0xDEADBEEF);
        int result = 0;
        _harness.OnGuest(_ => result = Call(modulemgr, "sceKernelStopUnloadModule", plugin.Uid, 0u, 0u, 0u, 0u, new Ptr<int>(status)));
        stopStatus = (int)GuestMemory.R32(status);
        return result;
    }

    [Fact]
    public void ThePluginsModuleStopReleasesItsHooksAndInjectionInItsOwnOrderAndTheCodeAndDataComeBack()
    {
        Installed installed = Install(releasesInStop: true);

        Assert.Equal(0, StopUnload(installed.Plugin, out int stopStatus));

        // every release from module_stop succeeded (the patches were still there while it ran)
        Assert.Equal(0, stopStatus);
        Assert.Equal((0, 0), Framework.PatchesOf(_harness.Emu.Pid));
        Assert.Equal(installed.Original, new[] { GuestMemory.R32(installed.Target), GuestMemory.R32(installed.Target + 4), GuestMemory.R32(installed.Target + 8) });
        Assert.Equal(0x11111111u, GuestMemory.R32(installed.Data));
        Assert.Equal(7u, CallAsUser(installed.Target, 3, 4));
        Assert.False(_harness.Emu.Kernel.Modules.ContainsKey(installed.Plugin.Uid));
    }

    [Fact]
    public void APluginUnloadedWithoutReleasingLeavesItsPatchesAsTheFrameworkKeepsNoListByModule()
    {
        Installed installed = Install(releasesInStop: false);
        uint hookedFirstWord = GuestMemory.R32(installed.Target);

        Assert.Equal(0, StopUnload(installed.Plugin, out int stopStatus));

        Assert.Equal(0, stopStatus);
        Assert.Equal((1, 1), Framework.PatchesOf(_harness.Emu.Pid));
        Assert.Equal(hookedFirstWord, GuestMemory.R32(installed.Target));
        Assert.NotEqual(installed.Original[0], hookedFirstWord);
        Assert.Equal(0x22222222u, GuestMemory.R32(installed.Data));

        // the uids still name the patches: a later release (another module that kept them) still works
        Assert.Equal(0, Call(Framework, "InjectReleaseForUser", installed.InjectionUid));
        Assert.Equal(0x11111111u, GuestMemory.R32(installed.Data));
    }
}
