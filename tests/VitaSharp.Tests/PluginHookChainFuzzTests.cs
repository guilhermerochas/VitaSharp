using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A deterministic fuzz of hook chains on one function (option "Enable Kernel Plugins"): up to eight hooks installed
/// and released in a random order (fixed seeds), each through a random kind of hook on the same function (export,
/// offset, absolute for a kernel function; export and offset for a user one), checked after every step against a
/// reference model of the framework's chain (its published source: the first hook stays the one the function jumps
/// to, each new hook goes right after it, a release unlinks the hook and the function jumps to the new first one; the
/// last release writes the original bytes back). After each step the call result spells the chain's order (each A32
/// hook continues the chain and appends its number), the reference records are linked as the model says (next, func,
/// one trampoline for all), every hook of the function answers the patch's UID, and released references and UIDs give
/// the framework's errors. Run on each CPU path (<see cref="HookExecutionPath"/>), the calls coming from a caller's
/// compiled code on the JIT paths.
/// </summary>
// Quarantined (Category=Slow) on 23/11/2026: in a full suite run, other classes of the shared "guest" collection
// leave kernel memory freed under these tests (registers of the framework read as zero); isolated they pass. Roadmap
// P1 "Hook tests: shared memory interference" tracks it; they come back to the fast suite when it is fixed.
[Collection("guest")]
[Trait("Category", "Slow")]
public sealed class PluginHookChainFuzzTests : IDisposable
{
    private const uint TargetLib = 0x0BAD7A70, TargetNid = 0x2468ACF0;
    private const int HookCount = 8;
    private const int ErrorNotFound = unchecked((int)0x90010002);

    private readonly HleHarness _harness;

    public PluginHookChainFuzzTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "hookfuzz", kernelPlugins: true);
    }

    public void Dispose() => _harness.Dispose();

    private PluginFramework Framework => _harness.Module<PluginFramework>();

    public static TheoryData<string, bool, int> Cases
    {
        get
        {
            var data = new TheoryData<string, bool, int>();
            foreach (string path in HookExecutionPath.SupportedNames)
            {
                foreach (bool kernel in new[] { true, false })
                {
                    foreach (int seed in new[] { 1, 2, 3 })
                    {
                        data.Add(path, kernel, seed);
                    }
                }
            }

            return data;
        }
    }

    private string Write(string file, SceModuleBuilder module)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), module.Build());
        return "ur0:tai/" + file;
    }

    private uint Word(bool kernel)
    {
        if (!kernel)
        {
            return _harness.Word(0);
        }

        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uint word = _harness.Emu.Memory.Alloc(16, "test:hook ref", MemoryRegion.Kernel, 16);
            GuestMemory.W32(word, 0);
            return word;
        }
    }

    /// <summary>The ways a step installs a hook on the target.</summary>
    private enum Kind
    {
        Export,
        Offset,
        Absolute,
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RandomInstallsAndReleasesFollowTheFrameworksChain(string pathName, bool kernel, int seed)
    {
        using var path = new HookExecutionPath(pathName);

        // the target (an A32 sum for the kernel, a Thumb one for the user), its caller through an import, the hooks
        var target = new SceModuleBuilder("FuzzTarget");
        int sum = kernel ? HookTestKit.ArmSum(target) : HookTestKit.ThumbSum(target);
        target.Export("FuzzTargetLib", TargetLib, 0x0001, (TargetNid, sum));
        SceModuleBuilder caller = new SceModuleBuilder("FuzzCaller").Import("FuzzTargetLib", TargetLib, TargetNid);
        int call = caller.CallImport(0, 0);
        var plugin = new SceModuleBuilder("FuzzPlugin");
        int[] hookFunctions = Enumerable.Range(1, HookCount).Select(n => HookTestKit.ChainHook(plugin, (byte)n)).ToArray();
        LoadedModule targetModule = _harness.Emu.LoadModule(Write(kernel ? "fuzztarget.skprx" : "fuzztarget.suprx", target), kernel);
        LoadedModule callerModule = _harness.Emu.LoadModule(Write(kernel ? "fuzzcaller.skprx" : "fuzzcaller.suprx", caller), kernel);
        LoadedModule pluginModule = _harness.Emu.LoadModule(Write(kernel ? "fuzzplugin.skprx" : "fuzzplugin.suprx", plugin), kernel);
        uint function = target.AddressOf(targetModule.Segments[0].Address, sum);
        uint callAddress = SceModuleBuilder.FunctionAddress(callerModule.Segments[0].Address, call);
        uint[] hooks = new uint[HookCount];
        uint[] refs = new uint[HookCount];
        for (int i = 0; i < HookCount; i++)
        {
            hooks[i] = plugin.AddressOf(pluginModule.Segments[0].Address, hookFunctions[i]);
            refs[i] = Word(kernel);
            GuestMemory.W32(hooks[i] + HookTestKit.RefLiteral, refs[i]);
            _harness.Emu.InvalidateCode(hooks[i], HookTestKit.ChainHookSize);
        }

        uint[] original = HookTestKit.Words(function, 3);
        uint name = kernel ? KernelString("FuzzTarget") : _harness.Str("FuzzTarget");
        uint userArgs = _harness.Alloc(0x1C);
        Kind[] kinds = kernel ? [Kind.Export, Kind.Offset, Kind.Absolute] : [Kind.Export, Kind.Offset];
        var random = new Random(seed);
        var chain = new List<int>(); // the model: hook indices in the order the framework runs them
        uint[] records = new uint[HookCount];
        int uid = 0;
        int steps = path.CompilesHotCode ? 40 : 300;
        int calls = path.WarmCalls;

        int Install(int hook)
        {
            Kind kind = kinds[random.Next(kinds.Length)];
            uint offset = (function & ~1u) - targetModule.Segments[0].Address;
            if (kernel)
            {
                return kind switch
                {
                    Kind.Export => Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, refs[hook], name, TargetLib, TargetNid, hooks[hook]),
                    Kind.Offset => Call(Framework, "HookFunctionOffsetForKernel", KernelSpace.KernelPid, refs[hook], targetModule.Uid, 0, offset, (int)(function & 1), hooks[hook]),
                    _ => Call(Framework, "HookFunctionAbsForKernel", KernelSpace.KernelPid, refs[hook], function, hooks[hook]),
                };
            }

            if (kind == Kind.Export)
            {
                GuestMemory.W32(userArgs, 0x14);
                GuestMemory.W32(userArgs + 4, name);
                GuestMemory.W32(userArgs + 8, TargetLib);
                GuestMemory.W32(userArgs + 0xC, TargetNid);
                GuestMemory.W32(userArgs + 0x10, hooks[hook]);
                return Call(Framework, "HookFunctionExportForUser", refs[hook], userArgs);
            }

            GuestMemory.W32(userArgs, 0x1C);
            GuestMemory.W32(userArgs + 4, (uint)targetModule.Uid);
            GuestMemory.W32(userArgs + 8, 0);
            GuestMemory.W32(userArgs + 0xC, offset);
            GuestMemory.W32(userArgs + 0x10, function & 1);
            GuestMemory.W32(userArgs + 0x14, hooks[hook]);
            return Call(Framework, "HookFunctionOffsetForUser", refs[hook], userArgs);
        }

        int Release(int patchUid, uint record) =>
            kernel ? Call(Framework, "HookReleaseForKernel", patchUid, record) : Call(Framework, "HookReleaseForUser", patchUid, record);

        _harness.OnGuest(t =>
        {
            t.KernelMode = kernel;
            for (int step = 0; step < steps; step++)
            {
                var free = Enumerable.Range(0, HookCount).Where(h => !chain.Contains(h)).ToList();
                bool install = chain.Count == 0 || (free.Count > 0 && random.Next(2) == 0);
                if (install)
                {
                    int hook = free[random.Next(free.Count)];
                    int result = Install(hook);
                    Assert.True(result > 0, $"step {step}: hook {hook + 1} returned 0x{result:X8}");
                    if (chain.Count == 0)
                    {
                        Assert.NotEqual(uid, result); // a new patch
                        uid = result;
                        chain.Add(hook);
                    }
                    else
                    {
                        Assert.Equal(uid, result);
                        chain.Insert(1, hook);
                    }

                    records[hook] = GuestMemory.R32(refs[hook]);
                }
                else
                {
                    int hook = chain[random.Next(chain.Count)];
                    Assert.Equal(0, Release(uid, records[hook]));
                    chain.Remove(hook);
                    // the released reference is not the patch's any more; with the patch gone, neither is the UID
                    Assert.Equal(chain.Count > 0 ? ErrorNotFound : SceError.UNKNOWN_UID, Release(uid, records[hook]));
                }

                CheckChain(step, chain, records, hooks);
                for (int i = 0; i < calls; i++)
                {
                    (uint a, uint b, uint c) = HookTestKit.Arguments(i + step);
                    uint want = Expected(chain, a + b + c);
                    uint got = t.CallGuest(callAddress, a, b, c);
                    Assert.True(want == got, $"step {step} (chain {string.Join(",", chain.Select(h => h + 1))}), call {i}: {got}, expected {want}");
                }

                path.WaitForJit();
                if (chain.Count == 0)
                {
                    Assert.Equal(original, HookTestKit.Words(function, 3));
                }
            }

            // everything out: the original code and result
            foreach (int hook in chain.ToArray())
            {
                Assert.Equal(0, Release(uid, records[hook]));
                chain.Remove(hook);
            }

            Assert.Equal(original, HookTestKit.Words(function, 3));
            Assert.Equal(1u + 100 + 10_000, t.CallGuest(callAddress, 1, 100, 10_000));
        });
    }

    /// <summary>The result of a call through <paramref name="chain"/> whose original returns
    /// <paramref name="original"/>: each hook appends its number after the hooks it continues to.</summary>
    private static uint Expected(List<int> chain, uint original)
    {
        uint value = original;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            value = value * 10 + (uint)(chain[i] + 1);
        }

        return value;
    }

    /// <summary>The framework's records are linked in the model's order: each next is the following hook's record (the
    /// last one's is 0), each func the hook's function, and every old the one trampoline of the patch (through the hook's
    /// counting stub).</summary>
    private static void CheckChain(int step, List<int> chain, uint[] records, uint[] hooks)
    {
        for (int i = 0; i < chain.Count; i++)
        {
            uint record = records[chain[i]];
            uint next = i + 1 < chain.Count ? records[chain[i + 1]] : 0;
            Assert.True(next == GuestMemory.R32(record), $"step {step}: hook {chain[i] + 1}'s next is 0x{GuestMemory.R32(record):X8}, expected 0x{next:X8}");
            // func and old are the hook's counting stub (its entry and its continuation), which goes on to the hook's
            // function and to the one trampoline of the patch
            Assert.Equal(GuestMemory.R32(record + 4) + PluginFramework.StubOldWord * 4u, GuestMemory.R32(record + 8));
            Assert.Equal(hooks[chain[i]], PluginHookTests.HookFunctionOf(record));
            Assert.Equal(PluginHookTests.TrampolineOf(records[chain[0]]), PluginHookTests.TrampolineOf(record));
        }
    }

    private uint KernelString(string text)
    {
        uint address;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            address = _harness.Emu.Memory.Alloc(0x40, "test:kernel string", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(address, 0, 0x40);
        GuestMemory.WriteCString(address, text);
        return address;
    }
}
