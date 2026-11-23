using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The function hook contract of the plugin framework libraries (option "Enable Kernel Plugins"), as plugins written
/// against it rely on: each hook gets a reference record (next, func, old) and continues the chain the way the
/// framework's TAI_CONTINUE does (next's func when there is a next, else old, the trampoline to the original); hooks
/// on one function are linked as the framework links them (its published source: the first hook stays the one the
/// function jumps to, each new one goes right after it, so the order is first, newest, ..., second); releasing a hook
/// in the middle or at the head keeps the others; releasing the last puts the code back. Kernel and user hooks alike,
/// on export, import and offset; and a kernel hook on a firmware function the HLE serves: a hook on a syscall export
/// sees the user programs' calls in the calling process (kernel code a syscall runs sees the caller's process as the
/// current one), and a hook on a kernel library's function does not take the user library's function with the same
/// NID (sceKernelGetProcessId and its kernel twin). Everything is synthetic: a firmware module in os0:kd exporting the
/// two libraries of sceKernelGetProcessId, and A32 hooks that continue the chain and then append their number to the
/// result (r0 * 10 + n), so the result spells the order the hooks ran in.
/// </summary>
[Collection("guest")]
public sealed class PluginHookContractTests : IDisposable
{
    private const uint ThreadmgrLib = 0x859A24B1, ThreadmgrForDriver = 0xE2C40624, GetProcessId = 0x9DCB4B7A;
    private const uint TargetLib = 0x0BAD7A60, TargetNid = 0x2468ACE0;

    /// <summary>The byte offset of the reference variable's address in <see cref="ChainHook"/>.</summary>
    private const uint RefLiteral = 12 * 4;

    private readonly HleHarness _harness;

    public PluginHookContractTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "khookc", kernelPlugins: true);
        var threadmgr = new SceModuleBuilder("SceKernelThreadMgr");
        int body = threadmgr.ReturnConstant(0);
        threadmgr.Export("SceThreadmgr", ThreadmgrLib, Linker.SyscallExportAttr | 1, (GetProcessId, body));
        threadmgr.Export("SceThreadmgrForDriver", ThreadmgrForDriver, 0x0001, (GetProcessId, body));
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "threadmgr.skprx"), threadmgr.Build());
        FirmwareKernelImage.Forget(_harness.Root);
    }

    public void Dispose()
    {
        FirmwareKernelImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    private PluginFramework Framework => _harness.Module<PluginFramework>();

    /// <summary>
    /// A hook in A32 that continues the chain as TAI_CONTINUE does (next = ref->next; next ? next->func : ref->old,
    /// called with the caller's r0-r3) and returns what that returned times 10 plus <paramref name="number"/>. The
    /// address of the variable that holds its reference goes at <see cref="RefLiteral"/>.
    /// </summary>
    private static int ChainHook(SceModuleBuilder module, byte number)
    {
        return module.Arm(
            0xE92D4010,          // 0 push {r4, lr}
            0xE59FC024,          // 1 ldr r12, [pc, #36]   the reference variable (word 12)
            0xE59CC000,          // 2 ldr r12, [r12]       the reference
            0xE59C4000,          // 3 ldr r4, [r12]        next
            0xE3540000,          // 4 cmp r4, #0
            0x059CC008,          // 5 ldreq r12, [r12, #8] old
            0x1594C004,          // 6 ldrne r12, [r4, #4]  next->func
            0xE12FFF3C,          // 7 blx r12
            0xE3A0100A,          // 8 mov r1, #10
            0xE0000091,          // 9 mul r0, r1, r0
            0xE2800000 | number, // 10 add r0, r0, #number
            0xE8BD8010,          // 11 pop {r4, pc}
            0x00000000);         // 12 the reference variable's address
    }

    /// <summary>An A32 function returning r0 + r1 whose first three instructions can run from a trampoline.</summary>
    private static int AddFunction(SceModuleBuilder module)
    {
        return module.Arm(0xE0800001, 0xE2800000, 0xE2800000, 0xE12FFF1E); // add r0, r0, r1; add r0, #0 (x2); bx lr
    }

    private string Write(string folder, string file, SceModuleBuilder module)
    {
        string dir = Path.Combine(_harness.Root, "ur0", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), module.Build());
        return $"ur0:{folder}/{file}";
    }

    /// <summary>A word of kernel memory (every process maps it), set to 0.</summary>
    private uint KernelWord()
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uint word = _harness.Emu.Memory.Alloc(16, "test:hook ref", MemoryRegion.Kernel, 16);
            GuestMemory.W32(word, 0);
            return word;
        }
    }

    /// <summary>The kernel plugin of these tests, loaded from the plugin configuration's *KERNEL (so the firmware's
    /// modules are shown first): hooks 1-3, each with its own reference variable, and a caller of
    /// ksceKernelGetProcessId; plus a kernel module whose export returns r0 + r1.</summary>
    private sealed record KernelPlugin(uint[] Hooks, uint[] Refs, uint KernelCaller, uint Target);

    private KernelPlugin LoadKernelPlugin()
    {
        SceModuleBuilder plugin = new SceModuleBuilder("HookPlugin").Start(0).Import("SceThreadmgrForDriver", ThreadmgrForDriver, GetProcessId);
        int caller = plugin.CallImport(0, 0);
        int[] hooks = [ChainHook(plugin, 1), ChainHook(plugin, 2), ChainHook(plugin, 3)];
        SceModuleBuilder target = new SceModuleBuilder("HookTarget").Start(0);
        int add = AddFunction(target);
        target.Export("HookTargetLib", TargetLib, 0x0001, (TargetNid, add));
        string pluginPath = Write("tai", "hookplugin.skprx", plugin);
        string targetPath = Write("tai", "hooktarget.skprx", target);
        File.WriteAllText(Path.Combine(_harness.Root, "ur0", "tai", "config.txt"), $"*KERNEL\n{pluginPath}\n{targetPath}\n");
        List<string> results = _harness.Emu.LoadPlugins();
        Assert.All(results, r => Assert.Contains(": loaded and started (", r));
        uint pluginBase = KernelModules(_harness.Emu).Values.Single(m => m.Name == "HookPlugin").Segments[0].Address;
        uint targetBase = KernelModules(_harness.Emu).Values.Single(m => m.Name == "HookTarget").Segments[0].Address;
        uint[] hookAddresses = hooks.Select(h => plugin.AddressOf(pluginBase, h)).ToArray();
        uint[] refs = [KernelWord(), KernelWord(), KernelWord()];
        for (int i = 0; i < hooks.Length; i++)
        {
            GuestMemory.W32(hookAddresses[i] + RefLiteral, refs[i]);
        }

        return new KernelPlugin(hookAddresses, refs, SceModuleBuilder.FunctionAddress(pluginBase, caller), target.AddressOf(targetBase, add));
    }

    /// <summary>A user module of the test's process that calls sceKernelGetProcessId (the syscall of SceThreadmgr).</summary>
    private uint LoadUserClient()
    {
        SceModuleBuilder client = new SceModuleBuilder("ProcessIdClient").Import("SceThreadmgr", ThreadmgrLib, GetProcessId);
        int call = client.CallImport(0, 0);
        LoadedModule module = _harness.Emu.LoadModule(Write("data", "client.suprx", client));
        return SceModuleBuilder.FunctionAddress(module.Segments[0].Address, call);
    }

    private uint CallAsKernel(uint function, uint first = 0, uint second = 0)
    {
        uint result = 0;
        _harness.OnGuest(t =>
        {
            t.KernelMode = true;
            result = t.CallGuest(function, first, second);
        });
        return result;
    }

    private uint CallAsUser(uint function, uint first = 0, uint second = 0)
    {
        uint result = 0;
        _harness.OnGuest(t =>
        {
            Assert.False(t.KernelMode);
            result = t.CallGuest(function, first, second);
        });
        return result;
    }

    private int HookKernelExport(string module, uint libNid, uint nid, uint hook, uint refVariable)
    {
        int uid = Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, refVariable, _harness.Str(module), libNid, nid, hook);
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        return uid;
    }

    private int ReleaseKernel(int uid, uint refVariable) => Call(Framework, "HookReleaseForKernel", uid, GuestMemory.R32(refVariable));

    [Fact]
    public void ThreeKernelHooksRunInTheFrameworksOrderEachContinuingTheChainAndReleasesKeepTheRest()
    {
        KernelPlugin plugin = LoadKernelPlugin();
        uint[] original = [GuestMemory.R32(plugin.Target), GuestMemory.R32(plugin.Target + 4), GuestMemory.R32(plugin.Target + 8)];
        Assert.Equal(7u, CallAsKernel(plugin.Target, 3, 4));

        int uid = HookKernelExport("HookTarget", TargetLib, TargetNid, plugin.Hooks[0], plugin.Refs[0]);
        Assert.Equal(71u, CallAsKernel(plugin.Target, 3, 4));
        Assert.Equal(uid, HookKernelExport("HookTarget", TargetLib, TargetNid, plugin.Hooks[1], plugin.Refs[1]));
        Assert.Equal(uid, HookKernelExport("HookTarget", TargetLib, TargetNid, plugin.Hooks[2], plugin.Refs[2]));

        // first, newest, second: hook 1, then 3, then 2, then the original (7) with the caller's arguments
        Assert.Equal(7231u, CallAsKernel(plugin.Target, 3, 4));

        // the middle one out: 1 then 2; the head out: 2 alone (the function now jumps to it); the last out: the code back
        Assert.Equal(0, ReleaseKernel(uid, plugin.Refs[2]));
        Assert.Equal(721u, CallAsKernel(plugin.Target, 3, 4));
        Assert.Equal(0, ReleaseKernel(uid, plugin.Refs[0]));
        Assert.Equal(72u, CallAsKernel(plugin.Target, 3, 4));
        Assert.Equal(0, ReleaseKernel(uid, plugin.Refs[1]));
        Assert.Equal(7u, CallAsKernel(plugin.Target, 3, 4));
        Assert.Equal(original, new[] { GuestMemory.R32(plugin.Target), GuestMemory.R32(plugin.Target + 4), GuestMemory.R32(plugin.Target + 8) });
        Assert.Equal(SceError.UNKNOWN_UID, ReleaseKernel(uid, plugin.Refs[1]));
    }

    [Fact]
    public void UserHooksOnExportImportAndOffsetChainAndReleaseTheSameWay()
    {
        // a user target module, a user module that imports its function, and the user plugin's hooks (all in the
        // test's process; the references in user memory, as the framework gives a user hook its record)
        var target = new SceModuleBuilder("UserTarget");
        int add = AddFunction(target);
        target.Export("UserTargetLib", TargetLib, 0x0001, (TargetNid, add));
        LoadedModule targetModule = _harness.Emu.LoadModule(Write("data", "usertarget.suprx", target));
        uint targetFunction = target.AddressOf(targetModule.Segments[0].Address, add);
        SceModuleBuilder importer = new SceModuleBuilder("UserImporter").Import("UserTargetLib", TargetLib, TargetNid);
        int importCall = importer.CallImport(0, 0);
        LoadedModule importerModule = _harness.Emu.LoadModule(Write("data", "userimporter.suprx", importer));
        uint callThroughImport = SceModuleBuilder.FunctionAddress(importerModule.Segments[0].Address, importCall);
        var hooks = new SceModuleBuilder("UserHooks");
        int[] hookFunctions = [ChainHook(hooks, 1), ChainHook(hooks, 2), ChainHook(hooks, 3)];
        LoadedModule hooksModule = _harness.Emu.LoadModule(Write("data", "userhooks.suprx", hooks));
        uint[] hook = hookFunctions.Select(h => hooks.AddressOf(hooksModule.Segments[0].Address, h)).ToArray();
        uint[] refs = [_harness.Word(0), _harness.Word(0), _harness.Word(0)];
        for (int i = 0; i < hook.Length; i++)
        {
            GuestMemory.W32(hook[i] + RefLiteral, refs[i]);
        }

        Assert.Equal(7u, CallAsUser(targetFunction, 3, 4));
        Assert.Equal(7u, CallAsUser(callThroughImport, 3, 4));

        // tai_hook_args: size 0x14, module, library NID, function NID, hook
        uint exportArgs = _harness.Alloc(0x14);
        GuestMemory.W32(exportArgs, 0x14);
        GuestMemory.W32(exportArgs + 4, _harness.Str("UserTarget"));
        GuestMemory.W32(exportArgs + 8, TargetLib);
        GuestMemory.W32(exportArgs + 0xC, TargetNid);
        GuestMemory.W32(exportArgs + 0x10, hook[0]);
        int exportUid = Call(Framework, "HookFunctionExportForUser", refs[0], exportArgs);
        Assert.True(exportUid > 0, $"export hook returned 0x{exportUid:X8}");
        GuestMemory.W32(exportArgs + 0x10, hook[1]);
        Assert.Equal(exportUid, Call(Framework, "HookFunctionExportForUser", refs[1], exportArgs));
        Assert.Equal(721u, CallAsUser(targetFunction, 3, 4));

        // an import hook on the importer's stub: its calls run hook 3, then the export's chain
        uint importArgs = _harness.Alloc(0x14);
        GuestMemory.W32(importArgs, 0x14);
        GuestMemory.W32(importArgs + 4, _harness.Str("UserImporter"));
        GuestMemory.W32(importArgs + 8, TargetLib);
        GuestMemory.W32(importArgs + 0xC, TargetNid);
        GuestMemory.W32(importArgs + 0x10, hook[2]);
        int importUid = Call(Framework, "HookFunctionImportForUser", refs[2], importArgs);
        Assert.True(importUid > 0, $"import hook returned 0x{importUid:X8}");
        Assert.Equal(7213u, CallAsUser(callThroughImport, 3, 4));
        Assert.Equal(721u, CallAsUser(targetFunction, 3, 4));
        Assert.Equal(0, Call(Framework, "HookReleaseForUser", importUid, GuestMemory.R32(refs[2])));
        Assert.Equal(721u, CallAsUser(callThroughImport, 3, 4));

        // both export hooks out, then an offset hook on the same function (A32: thumb 0)
        Assert.Equal(0, Call(Framework, "HookReleaseForUser", exportUid, GuestMemory.R32(refs[0])));
        Assert.Equal(0, Call(Framework, "HookReleaseForUser", exportUid, GuestMemory.R32(refs[1])));
        Assert.Equal(7u, CallAsUser(targetFunction, 3, 4));
        uint offsetArgs = _harness.Alloc(0x1C);
        GuestMemory.W32(offsetArgs, 0x1C);
        GuestMemory.W32(offsetArgs + 4, (uint)targetModule.Uid);
        GuestMemory.W32(offsetArgs + 8, 0);
        GuestMemory.W32(offsetArgs + 0xC, targetFunction - targetModule.Segments[0].Address);
        GuestMemory.W32(offsetArgs + 0x10, 0);
        GuestMemory.W32(offsetArgs + 0x14, hook[2]);
        int offsetUid = Call(Framework, "HookFunctionOffsetForUser", refs[2], offsetArgs);
        Assert.True(offsetUid > 0, $"offset hook returned 0x{offsetUid:X8}");
        Assert.Equal(73u, CallAsUser(targetFunction, 3, 4));
        Assert.Equal(0, Call(Framework, "HookReleaseForUser", offsetUid, GuestMemory.R32(refs[2])));
        Assert.Equal(7u, CallAsUser(callThroughImport, 3, 4));
    }

    [Fact]
    public void AKernelHookOnASyscallExportSeesTheUsersCallsInTheCallingProcessAndNotTheKernelTwin()
    {
        KernelPlugin plugin = LoadKernelPlugin();
        uint userCall = LoadUserClient();
        uint pid = (uint)_harness.Emu.Pid;
        Assert.Equal(pid, CallAsUser(userCall));
        Assert.Equal((uint)KernelSpace.KernelPid, CallAsKernel(plugin.KernelCaller));

        int uid = HookKernelExport("SceKernelThreadMgr", ThreadmgrLib, GetProcessId, plugin.Hooks[0], plugin.Refs[0]);

        // the user's syscall runs the hook, whose continuation reports the calling process; the kernel library's
        // function of the same NID is another function, its callers do not run the hook
        Assert.Equal(pid * 10 + 1, CallAsUser(userCall));
        Assert.Equal((uint)KernelSpace.KernelPid, CallAsKernel(plugin.KernelCaller));
        Assert.Equal(0, ReleaseKernel(uid, plugin.Refs[0]));
        Assert.Equal(pid, CallAsUser(userCall));
    }

    [Fact]
    public void AKernelHookOnTheKernelLibrarysFunctionDoesNotTakeTheUserSyscallWithTheSameNid()
    {
        KernelPlugin plugin = LoadKernelPlugin();
        uint userCall = LoadUserClient();
        uint pid = (uint)_harness.Emu.Pid;

        int uid = HookKernelExport("SceKernelThreadMgr", ThreadmgrForDriver, GetProcessId, plugin.Hooks[1], plugin.Refs[1]);
        Assert.Equal((uint)KernelSpace.KernelPid * 10 + 2, CallAsKernel(plugin.KernelCaller));
        Assert.Equal(pid, CallAsUser(userCall));
        Assert.Equal(0, ReleaseKernel(uid, plugin.Refs[1]));
        Assert.Equal((uint)KernelSpace.KernelPid, CallAsKernel(plugin.KernelCaller));
    }

    [Fact]
    public void AHookTheHleReachesFromAUserThreadRunsInTheCallingProcess()
    {
        // an HLE syscall that calls a firmware function the way the firmware module does (through its export entry,
        // FirmwareKernelModules.Call): a hook on that entry runs on the calling thread, whose process stays current
        KernelPlugin plugin = LoadKernelPlugin();
        uint pid = (uint)_harness.Emu.Pid;
        HookKernelExport("SceKernelThreadMgr", ThreadmgrLib, GetProcessId, plugin.Hooks[2], plugin.Refs[2]);
        uint result = 0;
        _harness.OnGuest(t =>
        {
            Assert.False(t.KernelMode);
            result = _harness.Emu.FirmwareKernel.Call(ThreadmgrLib, GetProcessId, () => 999u);
            Assert.False(t.KernelMode);
        });
        Assert.Equal(pid * 10 + 3, result);
    }
}
