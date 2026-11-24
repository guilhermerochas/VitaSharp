using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Function hooks of the plugin framework libraries (option "Enable Kernel Plugins"): the hooked function runs the hook;
/// the hook's reference record (next, func, old) chains to the next hook and to a trampoline that runs the original; a
/// second hook goes right after the first; releasing the first aims the function at the next one, releasing the last
/// puts the original code back. A hook on an HLE function's stub also takes the import stubs bound later. Synthetic
/// kernel modules only.
/// </summary>
[Collection("guest")]
public sealed class PluginHookTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint TargetLib = 0x0BAD7A60, TargetNid = 0x2468ACE0, ThreadmgrForDriver = 0xE2C40624, GetThreadId = 0x59D06540;
    private const int ErrorHookError = unchecked((int)0x90010006), ErrorNotFound = unchecked((int)0x90010002);

    private readonly HleHarness _harness = new(fixture, "kplughook", kernelPlugins: true);

    public void Dispose() => _harness.Dispose();

    private PluginFramework Framework => _harness.Module<PluginFramework>();

    private LoadedModule Load(string file, SceModuleBuilder builder)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), builder.Build());
        return _harness.Emu.LoadModule("ur0:tai/" + file, kernel: true);
    }

    /// <summary>A module exporting a function that returns 7 and one that calls an import (a PC-relative literal).</summary>
    private (uint Returns7, uint CallsImport) LoadTarget()
    {
        SceModuleBuilder builder = new SceModuleBuilder("HookTarget").Import("SceThreadmgrForDriver", ThreadmgrForDriver, GetThreadId);
        int returns7 = builder.ReturnConstant(7);
        int callsImport = builder.CallImport(0, 0);
        builder.Export("HookTargetLib", TargetLib, 0x0001, (TargetNid, returns7));
        LoadedModule module = Load("target.skprx", builder);
        uint codeBase = module.Segments[0].Address;
        return (SceModuleBuilder.FunctionAddress(codeBase, returns7), SceModuleBuilder.FunctionAddress(codeBase, callsImport));
    }

    private uint[] LoadHooks(params byte[] values)
    {
        var builder = new SceModuleBuilder("HookProvider");
        int[] functions = values.Select(builder.ReturnConstant).ToArray();
        LoadedModule module = Load("hooks.skprx", builder);
        return functions.Select(f => SceModuleBuilder.FunctionAddress(module.Segments[0].Address, f)).ToArray();
    }

    /// <summary>The hook function a reference record's counting stub goes on to.</summary>
    internal static uint HookFunctionOf(uint record) => GuestMemory.R32(StubData(record) + PluginFramework.StubHookFunction);

    /// <summary>The trampoline a reference record's counting stub continues to (the original).</summary>
    internal static uint TrampolineOf(uint record) => GuestMemory.R32(StubData(record) + PluginFramework.StubTrampoline);

    /// <summary>The data of the counting stub a record's func points at (the address in its code).</summary>
    internal static uint StubData(uint record) => GuestMemory.R32(GuestMemory.R32(record + 4) + PluginFramework.StubLiteralWord * 4);

    [Fact]
    public void AnExportHookRunsTheHookAndOldRunsTheOriginal()
    {
        (uint target, _) = LoadTarget();
        uint[] hooks = LoadHooks(42, 43);
        uint firstRef = _harness.Word(0);
        uint secondRef = _harness.Word(0);
        _harness.OnGuest(t =>
        {
            Assert.Equal(7u, t.CallGuest(target));
            int uid = Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, firstRef, _harness.Str("HookTarget"), TargetLib, TargetNid, hooks[0]);
            Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
            Assert.Equal(42u, t.CallGuest(target));
            uint first = GuestMemory.R32(firstRef);
            Assert.Equal(0u, GuestMemory.R32(first));
            // func and old are the hook's counting stub, which goes on to the hook function and to the trampoline
            Assert.Equal(hooks[0], HookFunctionOf(first));
            Assert.Equal(7u, t.CallGuest(GuestMemory.R32(first + 8)));

            // a second hook goes after the first: the first's next is its record, both share the trampoline
            Assert.Equal(uid, Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, secondRef, _harness.Str("HookTarget"), TargetLib, TargetNid, hooks[1]));
            uint second = GuestMemory.R32(secondRef);
            Assert.Equal(second, GuestMemory.R32(first));
            Assert.Equal(TrampolineOf(first), TrampolineOf(second));
            Assert.Equal(42u, t.CallGuest(target));

            // releasing the first aims the function at the second; releasing the last puts the code back
            Assert.Equal(0, Call(Framework, "HookReleaseForKernel", uid, first));
            Assert.Equal(43u, t.CallGuest(target));
            Assert.Equal(ErrorNotFound, Call(Framework, "HookReleaseForKernel", uid, first));
            Assert.Equal(0, Call(Framework, "HookReleaseForKernel", uid, second));
            Assert.Equal(7u, t.CallGuest(target));
        });
    }

    [Fact]
    public void ImportAndOffsetHooksAndTheirErrors()
    {
        (uint target, uint callsImport) = LoadTarget();
        uint[] hooks = LoadHooks(42, 43);
        LoadedModule module = HleHarness.KernelModules(_harness.Emu).Values.Single(m => m.Name == "HookTarget");
        uint importRef = _harness.Word(0);
        uint offsetRef = _harness.Word(0);
        _harness.OnGuest(t =>
        {
            // an import hook takes the module's stub: its call runs the hook, old runs the import
            int importUid = Call(Framework, "HookFunctionImportForKernel", KernelSpace.KernelPid, importRef, _harness.Str("HookTarget"), ThreadmgrForDriver, GetThreadId, hooks[0]);
            Assert.True(importUid > 0, $"import hook returned 0x{importUid:X8}");
            Assert.Equal(42u, t.CallGuest(callsImport));
            Assert.Equal((uint)t.Uid, t.CallGuest(GuestMemory.R32(GuestMemory.R32(importRef) + 8)));
            Assert.Equal(0, Call(Framework, "HookReleaseForKernel", importUid, GuestMemory.R32(importRef)));
            Assert.Equal((uint)t.Uid, t.CallGuest(callsImport));

            // an offset hook: the Thumb function at its offset in segment 0
            uint offset = (target & ~1u) - module.Segments[0].Address;
            int offsetUid = Call(Framework, "HookFunctionOffsetForKernel", KernelSpace.KernelPid, offsetRef, module.Uid, 0, offset, 1, hooks[1]);
            Assert.True(offsetUid > 0, $"offset hook returned 0x{offsetUid:X8}");
            Assert.Equal(43u, t.CallGuest(target));
            Assert.Equal(0, Call(Framework, "HookReleaseForKernel", offsetUid, GuestMemory.R32(offsetRef)));
            Assert.Equal(7u, t.CallGuest(target));
        });

        // the framework's errors: a hook function in the shared range, an unknown export, a short user argument record
        Assert.Equal(unchecked((int)0x90010004), Call(Framework, "HookFunctionAbsForKernel", KernelSpace.KernelPid, _harness.Word(0), target, 0xE0000001u));
        Assert.Equal(ErrorNotFound, Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, _harness.Word(0), _harness.Str("HookTarget"), TargetLib, 0x1u, hooks[0]));
        uint shortArgs = _harness.Word(0x10);
        Assert.Equal(unchecked((int)0x90010008), Call(Framework, "HookFunctionExportForUser", _harness.Word(0), shortArgs));
    }

    [Fact]
    public void AHookDropsOnlyTheTranslatedCodeItPatches()
    {
        // cost: installing and releasing a hook keeps every other block of translated code (no flush of the whole cache)
        (uint target, _) = LoadTarget();
        uint[] hooks = LoadHooks(42, 9);
        uint other = hooks[1];
        uint hookRef = _harness.Word(0);
        _harness.OnGuest(t =>
        {
            Assert.Equal(9u, t.CallGuest(other));
            Assert.Equal(7u, t.CallGuest(target));
            Assert.True(_harness.Emu.Code.TryGet(other & ~1u, thumb: true, out _), "the other function was not translated");
            int uid = Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, hookRef, _harness.Str("HookTarget"), TargetLib, TargetNid, hooks[0]);
            Assert.True(_harness.Emu.Code.TryGet(other & ~1u, thumb: true, out _), "the hook dropped unrelated code");
            Assert.Equal(42u, t.CallGuest(target));
            Assert.Equal(0, Call(Framework, "HookReleaseForKernel", uid, GuestMemory.R32(hookRef)));
            Assert.True(_harness.Emu.Code.TryGet(other & ~1u, thumb: true, out _), "the release dropped unrelated code");
        });
    }

    /// <summary>
    /// An offset hook on a Thumb function whose first instructions hold a short branch (CBZ, as the FW 3.74 ScePspemu
    /// at +0x6364 that Adrenaline 8 hooks, 24/11/2026; or B&lt;cond&gt;): the branch is relocated into the trampoline,
    /// as the framework's relocator does, instead of refusing the hook with TAI_ERROR_HOOK_ERROR. The function runs the
    /// hook, and the hook's continue takes both ways of the branch as the original does.
    /// </summary>
    [Theory]
    [InlineData((ushort)0x4615, (ushort)0xB112)] // mov r5, r2; cbz r2, +4
    [InlineData((ushort)0x2A00, (ushort)0xD002)] // cmp r2, #0; beq +4
    public void AnOffsetHookRelocatesTheShortBranchItCovers(ushort second, ushort branch)
    {
        // push {r4-r6, lr}; <second>; <branch> to zero; orr.w r0, r2, r0; pop {r4-r6, pc}; zero: movs r0, #2; pop
        var builder = new SceModuleBuilder("BranchTarget");
        int function = builder.Thumb(0xB570, second, branch, 0xEA42, 0x0000, 0xBD70, 0x2002, 0xBD70);
        LoadedModule module = Load("branch.skprx", builder);
        uint target = SceModuleBuilder.FunctionAddress(module.Segments[0].Address, function);
        uint[] hooks = LoadHooks(42);
        uint hookRef = _harness.Word(0);
        _harness.OnGuest(t =>
        {
            Assert.Equal(0x15u, t.CallGuest(target, 0x10, 0, 5));
            Assert.Equal(2u, t.CallGuest(target, 0x10, 0, 0));
            uint offset = (target & ~1u) - module.Segments[0].Address;

            int uid = Call(Framework, "HookFunctionOffsetForKernel", KernelSpace.KernelPid, hookRef, module.Uid, 0, offset, 1, hooks[0]);

            Assert.True(uid > 0, $"offset hook returned 0x{uid:X8}");
            Assert.Equal(42u, t.CallGuest(target, 0x10, 0, 5));
            uint original = GuestMemory.R32(GuestMemory.R32(hookRef) + 8);
            Assert.Equal(0x15u, t.CallGuest(original, 0x10, 0, 5));
            Assert.Equal(2u, t.CallGuest(original, 0x10, 0, 0));
            Assert.Equal(0, Call(Framework, "HookReleaseForKernel", uid, GuestMemory.R32(hookRef)));
            Assert.Equal(2u, t.CallGuest(target, 0x10, 0, 0));
        });
    }

    [Fact]
    public void CodeThatCannotMoveIsNotHooked()
    {
        (_, uint callsImport) = LoadTarget();
        uint[] hooks = LoadHooks(42);
        // the function's third instruction loads a PC-relative literal: it cannot run from the trampoline
        Assert.Equal(ErrorHookError, Call(Framework, "HookFunctionAbsForKernel", KernelSpace.KernelPid, _harness.Word(0), callsImport, hooks[0]));
    }

    [Fact]
    public void AHookOnAnHleStubAlsoTakesTheStubsBoundLater()
    {
        (_, uint callsImport) = LoadTarget();
        uint[] hooks = LoadHooks(42);
        uint stubOut = _harness.Word(0);
        Assert.Equal(0, Call(Framework, "ModuleGetImportFunc", KernelSpace.KernelPid, _harness.Str("HookTarget"), ThreadmgrForDriver, GetThreadId, stubOut));
        uint stub = GuestMemory.R32(stubOut);
        uint hookRef = _harness.Word(0);
        int uid = Convert.ToInt32(Invoke(Framework, "HookAbsolute", KernelSpace.KernelPid, hookRef, stub, hooks[0], ThreadmgrForDriver, GetThreadId));
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");

        // a module loaded after the hook imports the same function: its stub runs the hook too
        SceModuleBuilder later = new SceModuleBuilder("LaterClient").Import("SceThreadmgrForDriver", ThreadmgrForDriver, GetThreadId);
        int call = later.CallImport(0, 0);
        LoadedModule laterModule = Load("later.skprx", later);
        uint laterCall = SceModuleBuilder.FunctionAddress(laterModule.Segments[0].Address, call);
        _harness.OnGuest(t =>
        {
            Assert.Equal(42u, t.CallGuest(callsImport));
            Assert.Equal(42u, t.CallGuest(laterCall));
            Assert.Equal(0, Call(Framework, "HookReleaseForKernel", uid, GuestMemory.R32(hookRef)));
            Assert.Equal((uint)t.Uid, t.CallGuest(callsImport));
            Assert.Equal((uint)t.Uid, t.CallGuest(laterCall));
        });
    }
}
