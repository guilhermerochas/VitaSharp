using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// End-to-end validation of the plugin framework's function hooks (option "Enable Kernel Plugins"): every kind of hook
/// the framework offers (export, import, offset, absolute; on A32 and Thumb code; kernel and user; on a guest (LLE)
/// function and on a firmware function the HLE serves, the kernel's syscall exports and the user modules' shells)
/// against every CPU path of the caller (<see cref="HookExecutionPath"/>: interpreter, IL, the host's native backend,
/// code translated ahead). Each cell warms the caller's code on the path (the JIT compiles it; or the precompilation
/// translates it before it ever runs), installs the hook and checks that the caller's call runs the hook with the
/// caller's arguments, that the hook's continuation reaches the original and that the hook's result reaches the
/// caller (the A32 hooks return the continuation's result times 10 plus their number), with the hooked code itself
/// compiled again on the JIT paths; then releases the hook and checks the original bytes and the original result are
/// back. Everything is synthetic (<see cref="SceModuleBuilder"/> modules, a firmware module in os0:kd and os0:us).
/// The process-level cells (a process made after the hook, save states) are in <see cref="PluginHookMatrixProcessTests"/>.
/// </summary>
[Collection("guest")]
public sealed class PluginHookMatrixTests : IDisposable
{
    private const uint ThreadmgrLib = 0x859A24B1, ThreadmgrForDriver = 0xE2C40624, GetProcessId = 0x9DCB4B7A;
    private const uint TargetLib = 0x0BAD7A60, ArmNid = 0x2468ACE0, ThumbNid = 0x2468ACE2;
    private const uint SyscallLib = 0x0BAD7A62, SyscallNid = 0x2468ACE4;
    private const uint UserLib = 0x0BAD7A64;
    private const uint LibKernelLib = 0xCAE9ACE6, Strnlen = 0xAC595E68, LibKernelModuleNid = 0x0C5A7F13;

    private readonly HleHarness _harness;

    public PluginHookMatrixTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "hookmatrix", kernelPlugins: true);
        var threadmgr = new SceModuleBuilder("SceKernelThreadMgr");
        int body = threadmgr.ReturnConstant(0);
        threadmgr.Export("SceThreadmgr", ThreadmgrLib, Linker.SyscallExportAttr | 1, (GetProcessId, body));
        threadmgr.Export("SceThreadmgrForDriver", ThreadmgrForDriver, 0x0001, (GetProcessId, body));
        Directory.CreateDirectory(Path.Combine(_harness.Root, "os0", "kd"));
        File.WriteAllBytes(Path.Combine(_harness.Root, "os0", "kd", "threadmgr.skprx"), threadmgr.Build());
        SceModuleBuilder libkernel = new SceModuleBuilder("SceLibKernel").ModuleNid(LibKernelModuleNid);
        int strnlen = libkernel.ReturnConstant(0);
        libkernel.Export("SceLibKernel", LibKernelLib, 0x0001, (Strnlen, strnlen));
        Directory.CreateDirectory(Path.Combine(_harness.Root, "os0", "us"));
        File.WriteAllBytes(Path.Combine(_harness.Root, "os0", "us", "libkernel.suprx"), libkernel.Build());
        FirmwareKernelImage.Forget(_harness.Root);
        FirmwareUserImage.Forget(_harness.Root);
    }

    public void Dispose()
    {
        FirmwareKernelImage.Forget(_harness.Root);
        FirmwareUserImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    public static TheoryData<string> Paths => HookExecutionPath.Supported;

    private PluginFramework Framework => _harness.Module<PluginFramework>();

    private string Write(string folder, string file, SceModuleBuilder module)
    {
        string dir = Path.Combine(_harness.Root, "ur0", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), module.Build());
        return $"ur0:{folder}/{file}";
    }

    private uint KernelWord()
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uint word = _harness.Emu.Memory.Alloc(16, "test:hook ref", MemoryRegion.Kernel, 16);
            GuestMemory.W32(word, 0);
            return word;
        }
    }

    /// <summary>Hooks of a module: each one's reference variable written into its literal.</summary>
    private uint[] Hooks(SceModuleBuilder builder, LoadedModule module, int[] hooks, uint[] refs)
    {
        uint[] addresses = new uint[hooks.Length];
        for (int i = 0; i < hooks.Length; i++)
        {
            addresses[i] = builder.AddressOf(module.Segments[0].Address, hooks[i]);
            GuestMemory.W32(addresses[i] + HookTestKit.RefLiteral, refs[i]);
            _harness.Emu.InvalidateCode(addresses[i], HookTestKit.ChainHookSize);
        }

        return addresses;
    }

    /// <summary>The kernel side, loaded from the plugin configuration's *KERNEL (after the firmware's modules): a target
    /// module exporting an A32 and a Thumb sum and a syscall library with another A32 sum, a caller module importing the
    /// two sums and ksceKernelGetProcessId, and the plugin with three hooks.</summary>
    private sealed record KernelSide(LoadedModule Target, uint ArmSum, uint ThumbSum, uint SyscallSum, LoadedModule Caller, uint CallArm, uint CallThumb,
        uint[] Hooks, uint[] Refs);

    private KernelSide LoadKernel()
    {
        SceModuleBuilder target = new SceModuleBuilder("MatrixTarget").Start(0);
        int arm = HookTestKit.ArmSum(target);
        int thumb = HookTestKit.ThumbSum(target);
        int syscall = HookTestKit.ArmSum(target);
        target.Export("MatrixTargetLib", TargetLib, 0x0001, (ArmNid, arm), (ThumbNid, thumb));
        target.Export("MatrixSyscallLib", SyscallLib, Linker.SyscallExportAttr | 1, (SyscallNid, syscall));
        SceModuleBuilder caller = new SceModuleBuilder("MatrixCaller").Start(0).Import("MatrixTargetLib", TargetLib, ArmNid, ThumbNid);
        int callArm = caller.CallImport(0, 0);
        int callThumb = caller.CallImport(0, 1);
        SceModuleBuilder plugin = new SceModuleBuilder("MatrixPlugin").Start(0);
        int[] hooks = [HookTestKit.ChainHook(plugin, 1), HookTestKit.ChainHook(plugin, 2), HookTestKit.ChainHook(plugin, 3)];
        string targetPath = Write("tai", "matrixtarget.skprx", target);
        string callerPath = Write("tai", "matrixcaller.skprx", caller);
        string pluginPath = Write("tai", "matrixplugin.skprx", plugin);
        File.WriteAllText(Path.Combine(_harness.Root, "ur0", "tai", "config.txt"), $"*KERNEL\n{targetPath}\n{callerPath}\n{pluginPath}\n");
        List<string> results = _harness.Emu.LoadPlugins();
        Assert.All(results, r => Assert.Contains(": loaded and started (", r));
        LoadedModule targetModule = KernelModules(_harness.Emu).Values.Single(m => m.Name == "MatrixTarget");
        LoadedModule callerModule = KernelModules(_harness.Emu).Values.Single(m => m.Name == "MatrixCaller");
        LoadedModule pluginModule = KernelModules(_harness.Emu).Values.Single(m => m.Name == "MatrixPlugin");
        uint targetBase = targetModule.Segments[0].Address;
        uint callerBase = callerModule.Segments[0].Address;
        uint[] refs = [KernelWord(), KernelWord(), KernelWord()];
        return new KernelSide(targetModule, target.AddressOf(targetBase, arm), target.AddressOf(targetBase, thumb), target.AddressOf(targetBase, syscall),
            callerModule, SceModuleBuilder.FunctionAddress(callerBase, callArm), SceModuleBuilder.FunctionAddress(callerBase, callThumb),
            Hooks(plugin, pluginModule, hooks, refs), refs);
    }

    /// <summary>The user side, in the test's process: a target module exporting an A32 and a Thumb sum, a caller module
    /// importing them, the kernel module's syscall sum, sceKernelGetProcessId (the HLE's syscall) and sceClibStrnlen
    /// (served by the HLE, listed as the firmware module's shell), and the user plugin with three hooks.</summary>
    private sealed record UserSide(LoadedModule Target, uint ArmSum, uint ThumbSum, LoadedModule Caller, uint CallArm, uint CallThumb, uint CallSyscall,
        uint CallProcessId, uint CallStrnlen, uint[] Hooks, uint[] Refs);

    private UserSide LoadUser()
    {
        var target = new SceModuleBuilder("MatrixUserTarget");
        int arm = HookTestKit.ArmSum(target);
        int thumb = HookTestKit.ThumbSum(target);
        target.Export("MatrixUserLib", UserLib, 0x0001, (ArmNid, arm), (ThumbNid, thumb));
        LoadedModule targetModule = _harness.Emu.LoadModule(Write("data", "matrixusertarget.suprx", target));
        SceModuleBuilder caller = new SceModuleBuilder("MatrixUserCaller")
            .Import("MatrixUserLib", UserLib, ArmNid, ThumbNid)
            .Import("MatrixSyscallLib", SyscallLib, SyscallNid)
            .Import("SceThreadmgr", ThreadmgrLib, GetProcessId)
            .Import("SceLibKernel", LibKernelLib, Strnlen);
        int callArm = caller.CallImport(0, 0);
        int callThumb = caller.CallImport(0, 1);
        int callSyscall = caller.CallImport(1, 0);
        int callProcessId = caller.CallImport(2, 0);
        int callStrnlen = caller.CallImport(3, 0);
        LoadedModule callerModule = _harness.Emu.LoadModule(Write("data", "matrixusercaller.suprx", caller));
        var plugin = new SceModuleBuilder("MatrixUserPlugin");
        int[] hooks = [HookTestKit.ChainHook(plugin, 1), HookTestKit.ChainHook(plugin, 2), HookTestKit.ChainHook(plugin, 3)];
        LoadedModule pluginModule = _harness.Emu.LoadModule(Write("data", "matrixuserplugin.suprx", plugin));
        uint targetBase = targetModule.Segments[0].Address;
        uint callerBase = callerModule.Segments[0].Address;
        uint[] refs = [_harness.Word(0), _harness.Word(0), _harness.Word(0)];
        return new UserSide(targetModule, target.AddressOf(targetBase, arm), target.AddressOf(targetBase, thumb), callerModule,
            SceModuleBuilder.FunctionAddress(callerBase, callArm), SceModuleBuilder.FunctionAddress(callerBase, callThumb),
            SceModuleBuilder.FunctionAddress(callerBase, callSyscall), SceModuleBuilder.FunctionAddress(callerBase, callProcessId),
            SceModuleBuilder.FunctionAddress(callerBase, callStrnlen), Hooks(plugin, pluginModule, hooks, refs), refs);
    }

    /// <summary>A call a cell makes: the function, whether the hook takes it, the arguments of call i and the
    /// original's result for them.</summary>
    private sealed record Caller(uint Function, bool Hooked, Func<int, (uint A, uint B, uint C)> Arguments, Func<(uint A, uint B, uint C), uint> Original);

    private static Caller Sum(uint function, bool hooked = true) =>
        new(function, hooked, HookTestKit.Arguments, args => args.A + args.B + args.C);

    /// <summary>Runs every caller <paramref name="count"/> times on one guest thread, checking each result: the original's,
    /// or (<paramref name="hookNumber"/> > 0, for the hooked callers) the original's times 10 plus the hook's number.</summary>
    private void Run(bool kernelMode, IReadOnlyList<Caller> callers, int count, uint hookNumber)
    {
        _harness.OnGuest(t =>
        {
            t.KernelMode = kernelMode;
            for (int i = 0; i < count; i++)
            {
                foreach (Caller caller in callers)
                {
                    (uint A, uint B, uint C) args = caller.Arguments(i);
                    uint expected = caller.Original(args);
                    if (hookNumber != 0 && caller.Hooked)
                    {
                        expected = expected * 10 + hookNumber;
                    }

                    uint result = t.CallGuest(caller.Function, args.A, args.B, args.C);
                    Assert.True(expected == result, $"call {i} of 0x{caller.Function:X8}: {result}, expected {expected}");
                }
            }
        });
    }

    /// <summary>
    /// One cell: the callers' code warmed on <paramref name="path"/> (compiled, or translated ahead and never run), the
    /// hook installed (its UID from <paramref name="install"/>), the calls checked with the hook (whose own code is then
    /// compiled on the JIT paths and checked again), the hook released (<paramref name="release"/>) and the calls and
    /// the <paramref name="patched"/> bytes checked to be the original's again.
    /// </summary>
    private void Cell(HookExecutionPath path, bool kernelMode, Caller[] callers, uint[] translated, uint patched, uint hook, uint hookNumber, Func<int> install,
        Func<int, int> release)
    {
        if (path.Name == HookExecutionPath.Precompiled)
        {
            HookTestKit.Precompile(_harness.Emu, translated);
        }
        else
        {
            Run(kernelMode, callers, path.WarmCalls, 0);
            path.WaitForJit();
        }

        path.AssertTranslated(_harness.Emu, "before the hook", translated);
        uint[] before = HookTestKit.Words(patched, 3);

        int uid = install();
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        Assert.NotEqual(before, HookTestKit.Words(patched, 3));
        Run(kernelMode, callers, path.WarmCalls, hookNumber);
        if (path.CompilesHotCode)
        {
            path.WaitForJit();
            path.AssertTranslated(_harness.Emu, "the hook", hook);
            Run(kernelMode, callers, 1, hookNumber);
        }

        Assert.Equal(0, release(uid));
        Assert.Equal(before, HookTestKit.Words(patched, 3));
        Run(kernelMode, callers, path.WarmCalls, 0);
        path.WaitForJit();
        Run(kernelMode, callers, 1, 0);
    }

    private int KernelRelease(int uid, uint refVariable) => Call(Framework, "HookReleaseForKernel", uid, GuestMemory.R32(refVariable));

    private int UserRelease(int uid, uint refVariable) => Call(Framework, "HookReleaseForUser", uid, GuestMemory.R32(refVariable));

    /// <summary>tai_hook_args (size 0x14: module name, library NID, function NID, hook).</summary>
    private uint UserHookArgs(string module, uint libNid, uint nid, uint hook)
    {
        uint args = _harness.Alloc(0x14);
        GuestMemory.W32(args, 0x14);
        GuestMemory.W32(args + 4, _harness.Str(module));
        GuestMemory.W32(args + 8, libNid);
        GuestMemory.W32(args + 0xC, nid);
        GuestMemory.W32(args + 0x10, hook);
        return args;
    }

    /// <summary>tai_offset_args (size 0x1C: module UID, segment, offset, thumb, hook).</summary>
    private uint UserOffsetArgs(LoadedModule module, uint function, uint hook)
    {
        uint args = _harness.Alloc(0x1C);
        GuestMemory.W32(args, 0x1C);
        GuestMemory.W32(args + 4, (uint)module.Uid);
        GuestMemory.W32(args + 8, 0);
        GuestMemory.W32(args + 0xC, (function & ~1u) - module.Segments[0].Address);
        GuestMemory.W32(args + 0x10, function & 1);
        GuestMemory.W32(args + 0x14, hook);
        return args;
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void KernelExportHook_Arm(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        KernelSide k = LoadKernel();
        Caller[] callers = [Sum(k.CallArm), Sum(k.ArmSum)];
        Cell(path, true, callers, [k.CallArm, k.ArmSum], k.ArmSum, k.Hooks[0], 1,
            () => Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, k.Refs[0], _harness.Str("MatrixTarget"), TargetLib, ArmNid, k.Hooks[0]),
            uid => KernelRelease(uid, k.Refs[0]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void KernelExportHook_Thumb(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        KernelSide k = LoadKernel();
        Caller[] callers = [Sum(k.CallThumb), Sum(k.ThumbSum)];
        Cell(path, true, callers, [k.CallThumb, k.ThumbSum], k.ThumbSum, k.Hooks[0], 1,
            () => Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, k.Refs[0], _harness.Str("MatrixTarget"), TargetLib, ThumbNid, k.Hooks[0]),
            uid => KernelRelease(uid, k.Refs[0]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void KernelImportHook(string pathName)
    {
        // the caller module's stub of the A32 sum: its calls run the hook, a direct call of the export does not
        using var path = new HookExecutionPath(pathName);
        KernelSide k = LoadKernel();
        uint stubOut = _harness.Word(0);
        Assert.Equal(0, Call(Framework, "ModuleGetImportFunc", KernelSpace.KernelPid, _harness.Str("MatrixCaller"), TargetLib, ArmNid, stubOut));
        uint stub = GuestMemory.R32(stubOut);
        Caller[] callers = [Sum(k.CallArm), Sum(k.ArmSum, hooked: false)];
        Cell(path, true, callers, [k.CallArm, stub, k.ArmSum], stub, k.Hooks[0], 1,
            () => Call(Framework, "HookFunctionImportForKernel", KernelSpace.KernelPid, k.Refs[0], _harness.Str("MatrixCaller"), TargetLib, ArmNid, k.Hooks[0]),
            uid => KernelRelease(uid, k.Refs[0]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void KernelOffsetHook_Thumb(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        KernelSide k = LoadKernel();
        uint offset = (k.ThumbSum & ~1u) - k.Target.Segments[0].Address;
        Caller[] callers = [Sum(k.CallThumb), Sum(k.ThumbSum)];
        Cell(path, true, callers, [k.CallThumb, k.ThumbSum], k.ThumbSum, k.Hooks[1], 2,
            () => Call(Framework, "HookFunctionOffsetForKernel", KernelSpace.KernelPid, k.Refs[1], k.Target.Uid, 0, offset, 1, k.Hooks[1]),
            uid => KernelRelease(uid, k.Refs[1]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void KernelAbsoluteHook_Arm(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        KernelSide k = LoadKernel();
        Caller[] callers = [Sum(k.CallArm), Sum(k.ArmSum)];
        Cell(path, true, callers, [k.CallArm, k.ArmSum], k.ArmSum, k.Hooks[2], 3,
            () => Call(Framework, "HookFunctionAbsForKernel", KernelSpace.KernelPid, k.Refs[2], k.ArmSum, k.Hooks[2]),
            uid => KernelRelease(uid, k.Refs[2]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void KernelExportHook_OnAnLleSyscallExport_TakesTheUsersSyscall(string pathName)
    {
        // a kernel module's own syscall library (as kubridge has): the user program's call runs the kernel hook
        using var path = new HookExecutionPath(pathName);
        KernelSide k = LoadKernel();
        UserSide u = LoadUser();
        Caller[] callers = [Sum(u.CallSyscall)];
        Cell(path, false, callers, [u.CallSyscall, k.SyscallSum], k.SyscallSum, k.Hooks[0], 1,
            () => Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, k.Refs[0], _harness.Str("MatrixTarget"), SyscallLib, SyscallNid, k.Hooks[0]),
            uid => KernelRelease(uid, k.Refs[0]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void KernelExportHook_OnAFirmwareSyscallTheHleServes_TakesTheUsersSyscallInTheCallingProcess(string pathName)
    {
        // sceKernelGetProcessId: the hook's continuation reports the calling process (kernel code a syscall runs sees
        // the caller's process as the current one)
        using var path = new HookExecutionPath(pathName);
        KernelSide k = LoadKernel();
        UserSide u = LoadUser();
        uint pid = (uint)_harness.Emu.Pid;
        uint entry = _harness.Emu.FirmwareKernel.EntryOf(ThreadmgrLib, GetProcessId);
        Caller[] callers = [new(u.CallProcessId, true, HookTestKit.Arguments, _ => pid)];
        Cell(path, false, callers, [u.CallProcessId], entry, k.Hooks[0], 1,
            () => Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, k.Refs[0], _harness.Str("SceKernelThreadMgr"), ThreadmgrLib, GetProcessId, k.Hooks[0]),
            uid => KernelRelease(uid, k.Refs[0]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void UserExportHook_Arm(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        UserSide u = LoadUser();
        Caller[] callers = [Sum(u.CallArm), Sum(u.ArmSum)];
        Cell(path, false, callers, [u.CallArm, u.ArmSum], u.ArmSum, u.Hooks[0], 1,
            () => Call(Framework, "HookFunctionExportForUser", u.Refs[0], UserHookArgs("MatrixUserTarget", UserLib, ArmNid, u.Hooks[0])),
            uid => UserRelease(uid, u.Refs[0]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void UserExportHook_Thumb(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        UserSide u = LoadUser();
        Caller[] callers = [Sum(u.CallThumb), Sum(u.ThumbSum)];
        Cell(path, false, callers, [u.CallThumb, u.ThumbSum], u.ThumbSum, u.Hooks[0], 1,
            () => Call(Framework, "HookFunctionExportForUser", u.Refs[0], UserHookArgs("MatrixUserTarget", UserLib, ThumbNid, u.Hooks[0])),
            uid => UserRelease(uid, u.Refs[0]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void UserImportHook(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        UserSide u = LoadUser();
        uint stubOut = _harness.Word(0);
        Assert.Equal(0, Call(Framework, "ModuleGetImportFunc", _harness.Emu.Pid, _harness.Str("MatrixUserCaller"), UserLib, ThumbNid, stubOut));
        uint stub = GuestMemory.R32(stubOut);
        Caller[] callers = [Sum(u.CallThumb), Sum(u.ThumbSum, hooked: false)];
        Cell(path, false, callers, [u.CallThumb, stub, u.ThumbSum], stub, u.Hooks[1], 2,
            () => Call(Framework, "HookFunctionImportForUser", u.Refs[1], UserHookArgs("MatrixUserCaller", UserLib, ThumbNid, u.Hooks[1])),
            uid => UserRelease(uid, u.Refs[1]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void UserOffsetHook_ArmAndThumb(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        UserSide u = LoadUser();
        Caller[] thumbCallers = [Sum(u.CallThumb), Sum(u.ThumbSum)];
        Cell(path, false, thumbCallers, [u.CallThumb, u.ThumbSum], u.ThumbSum, u.Hooks[2], 3,
            () => Call(Framework, "HookFunctionOffsetForUser", u.Refs[2], UserOffsetArgs(u.Target, u.ThumbSum, u.Hooks[2])),
            uid => UserRelease(uid, u.Refs[2]));
        Caller[] armCallers = [Sum(u.CallArm), Sum(u.ArmSum)];
        Cell(path, false, armCallers, [u.CallArm, u.ArmSum], u.ArmSum, u.Hooks[2], 3,
            () => Call(Framework, "HookFunctionOffsetForUser", u.Refs[2], UserOffsetArgs(u.Target, u.ArmSum, u.Hooks[2])),
            uid => UserRelease(uid, u.Refs[2]));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void UserExportHook_OnAFirmwareUserModuleTheHleServes(string pathName)
    {
        // sceClibStrnlen of SceLibKernel (the shell of os0:us/libkernel.suprx): the HLE gets the caller's arguments
        using var path = new HookExecutionPath(pathName);
        UserSide u = LoadUser();
        uint text = _harness.Str("abcdefghijklmnopqrstuvwxyz");
        Caller[] callers = [new(u.CallStrnlen, true, i => (text, (uint)(i % 30), 0u), args => Math.Min(26u, args.B))];
        uint entryOut = _harness.Word(0);
        Assert.Equal(0, Call(Framework, "ModuleGetExportFunc", _harness.Emu.Pid, _harness.Str("SceLibKernel"), LibKernelLib, Strnlen, entryOut));
        uint entry = GuestMemory.R32(entryOut);
        Cell(path, false, callers, [u.CallStrnlen], entry, u.Hooks[0], 1,
            () => Call(Framework, "HookFunctionExportForUser", u.Refs[0], UserHookArgs("SceLibKernel", LibKernelLib, Strnlen, u.Hooks[0])),
            uid => UserRelease(uid, u.Refs[0]));
    }
}
