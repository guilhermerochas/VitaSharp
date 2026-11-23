using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// An HLE import stub is "svc #index; bx lr; nid": the index must name the same function in every process of the
/// emulated Vita, because kernel memory (the kernel modules' import stubs, the HLE kernel exports, the trampolines a
/// plugin's hook copies from them) is shared and runs on threads of any process. Synthetic kernel modules only.
/// </summary>
[Collection("guest")]
public sealed class HleSharedIndexTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint ThreadmgrForDriver = 0xE2C40624, GetThreadId = 0x59D06540, OtherNid = 0x0BAD0004;
    private const uint FirstNid = 0x0BAD0001, SecondNid = 0x0BAD0002, ThirdNid = 0x0BAD0003;

    private readonly HleHarness _harness = new(fixture, "hleindex", kernelPlugins: true);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ProcessesThatBindInAnotherOrderGetTheSameIndexes()
    {
        // two processes' registries of one emulated Vita
        var first = new HleRegistry(_harness.Emu);
        var second = new HleRegistry(_harness.Emu);
        uint a = first.GetHleFunctionIndex(FirstNid, 0, "TestLib");
        uint b = first.GetHleFunctionIndex(SecondNid, 0, "TestLib");
        Assert.Equal(b, second.GetHleFunctionIndex(SecondNid, 0, "TestLib"));
        Assert.Equal(a, second.GetHleFunctionIndex(FirstNid, 0, "TestLib"));

        // an index the second process never bound still names the first process's function there
        uint c = first.GetHleFunctionIndex(ThirdNid, 0, "TestLib");
        Assert.Equal(ThirdNid, second.GetFunction(c).Nid);
    }

    [Fact]
    public void AHookedHleStubCalledFromAnotherProcessRunsItsFunction()
    {
        // a kernel module importing an HLE function: its stub holds this process's index
        SceModuleBuilder builder = new SceModuleBuilder("SharedTarget").Import("SceThreadmgrForDriver", ThreadmgrForDriver, GetThreadId);
        int callsImport = builder.CallImport(0, 0);
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "shared.skprx"), builder.Build());
        LoadedModule module = _harness.Emu.LoadModule("ur0:tai/shared.skprx", kernel: true);
        uint call = SceModuleBuilder.FunctionAddress(module.Segments[0].Address, callsImport);

        // a plugin hooks the HLE stub with a hook that continues to the original (TAI_CONTINUE: record->old)
        uint hookRef = _harness.Word(0);
        var hooks = new SceModuleBuilder("ContinueHook");
        int hook = hooks.Arm(
            0xE300C000 | ((hookRef & 0xF000) << 4) | (hookRef & 0xFFF),         // movw ip, #lo(hookRef)
            0xE340C000 | ((hookRef >> 12) & 0xF0000) | ((hookRef >> 16) & 0xFFF), // movt ip, #hi(hookRef)
            0xE59CC000,                                                            // ldr ip, [ip]
            0xE59CC008,                                                            // ldr ip, [ip, #8]
            0xE12FFF1C);                                                           // bx ip
        File.WriteAllBytes(Path.Combine(dir, "hook.skprx"), hooks.Build());
        LoadedModule hookModule = _harness.Emu.LoadModule("ur0:tai/hook.skprx", kernel: true);
        uint hookFunction = hooks.AddressOf(hookModule.Segments[0].Address, hook);
        uint stubOut = _harness.Word(0);
        PluginFramework framework = _harness.Module<PluginFramework>();
        Assert.Equal(0, Call(framework, "ModuleGetImportFunc", KernelSpace.KernelPid, _harness.Str("SharedTarget"), ThreadmgrForDriver, GetThreadId, stubOut));
        int uid = Convert.ToInt32(Invoke(framework, "HookAbsolute", KernelSpace.KernelPid, hookRef, GuestMemory.R32(stubOut), hookFunction, ThreadmgrForDriver, GetThreadId));
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        _harness.OnGuest(t => Assert.Equal((uint)t.Uid, t.CallGuest(call)));

        // another process, created after the hook, whose registry binds other functions first
        var other = new HleRegistry(_harness.Emu);
        other.GetHleFunctionIndex(OtherNid, 0, "TestLib");
        uint index = GuestMemory.R32(PluginHookTests.TrampolineOf(GuestMemory.R32(hookRef))) & 0xFFFFFF; // the trampoline's svc
        Assert.Equal(GetThreadId, other.GetFunction(index).Nid);
    }
}
