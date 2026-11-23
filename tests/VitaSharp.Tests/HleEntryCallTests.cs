using System.Reflection;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The generated <c>XxxThroughEntry</c> wrappers (VitaSharp.Core.Generators.HleEntryCallGenerator): the HLE's own call of
/// an exported firmware function, made the way the firmware makes it, by the function's address. On the Vita a hook on
/// the export patches the function's code, so the firmware's own calls run the hook too. Covered here: a user plugin's
/// hook in the process (the process redirect) and a kernel plugin's patch of the firmware kernel entry each take the
/// call with the caller's arguments as AAPCS words (64-bit values in an even pair, the fifth word on the stack) and
/// their r0/r1 become the C# result; without a hook the C# method runs and nothing is allocated; every wrapper the
/// core has resolves its NIDs and reaches a hook on them.
/// </summary>
[Collection("guest")]
public sealed class HleEntryCallTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "hle-entry");

    public void Dispose() => _h.Dispose();

    /// <summary>A hook: a host function behind an HLE stub that records r0-r3 and the first stack word, and returns
    /// r0 = <paramref name="r0"/>, r1 = <paramref name="r1"/>.</summary>
    private uint Hook(List<uint[]> seen, uint r0 = 0x55, uint r1 = 0)
    {
        uint at = _h.Emu.Memory.Alloc(16, "entry call test hook");
        Linker.WriteHleStub(at, _h.Emu.Hle.RegisterHostFunction("entrytest:hook", context =>
        {
            seen.Add([context.Gpr(0), context.Gpr(1), context.Gpr(2), context.Gpr(3), context.Gpr(4)]);
            context.Return(r0 | ((ulong)r1 << 32));
        }), 0);
        _h.Emu.Code.Invalidate(at, 16);
        return at;
    }

    private EntryProbeModule Probe()
    {
        return new EntryProbeModule { Emu = _h.Emu };
    }

    [Fact]
    public void WithoutAHookTheWrapperCallsTheMethodAndAllocatesNothing()
    {
        EntryProbeModule probe = Probe();
        _h.OnGuest(_ =>
        {
            Assert.Equal(7 + 3, probe.probeWordsThroughEntry(7, 3, true, 0x1_0000_0000));
            long before = GC.GetAllocatedBytesForCurrentThread();
            int sum = 0;
            for (int i = 0; i < 1000; i++)
            {
                sum += probe.probeWordsThroughEntry((uint)i, 1, false, 2);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.True(sum > 0);
            Assert.Equal(1001, probe.Calls);
        });
    }

    /// <summary>A user plugin's hook on the function in this process takes the HLE's own call: the hook sees the words
    /// of every argument and its r0 is the result; releasing the hook gives the call back to the C# method.</summary>
    [Fact]
    public void AUserHookInTheProcessTakesTheCallWithTheCallersArguments()
    {
        EntryProbeModule probe = Probe();
        var seen = new List<uint[]>();
        uint hook = Hook(seen, 0xFFFF_FFF0);
        _h.OnGuest(_ =>
        {
            _h.Emu.Hle.RedirectInProcess(0x1234, EntryProbeModule.WordsNid, hook);
            Assert.Equal(-16, probe.probeWordsThroughEntry(0x11, 0xFFFF_FFFE, true, 0x0000_0002_0000_0003));
            uint[] words = Assert.Single(seen);
            // a, b, c in r0-r2; d (64-bit) skips r3 for the even pair and goes on the stack as a 0x8-aligned pair
            Assert.Equal([0x11u, 0xFFFF_FFFEu, 1u, 0u, 3u], words);
            Assert.Equal(0, probe.Calls);

            _h.Emu.Hle.ClearRedirectInProcess(0x1234, EntryProbeModule.WordsNid);
            Assert.Equal(0x13, probe.probeWordsThroughEntry(0x11, 2, false, 0));
            Assert.Single(seen);
            Assert.Equal(1, probe.Calls);
        });
    }

    /// <summary>A NID exported by two libraries is two functions (two modules' code): a user plugin's hook on the other
    /// library's function of the NID in this process is not the probe's and leaves its call to the C# method; a hook on
    /// the probe's own library's function of the NID takes it. The library of a hook is the one its export table names
    /// for its library NID.</summary>
    [Fact]
    public void AUserHookIsFoundByLibraryAndNid()
    {
        const uint ProbeUserLib = 0x7E57_3001, OtherUserLib = 0x7E57_3002;
        EntryProbeModule probe = Probe();
        var seen = new List<uint[]>();
        uint hook = Hook(seen, 1);
        // the user shells' export tables bind each function under its library (NID and name)
        _h.Emu.Hle.GetHleFunctionIndex(EntryProbeModule.FlagNid, ProbeUserLib, "SceEntryProbeForDriver");
        _h.Emu.Hle.GetHleFunctionIndex(EntryProbeModule.FlagNid, OtherUserLib, "SceOtherUser");
        _h.OnGuest(_ =>
        {
            _h.Emu.Hle.RedirectInProcess(OtherUserLib, EntryProbeModule.FlagNid, hook);
            Assert.False(probe.probeFlagThroughEntry(EntryProbeColor.Red));
            Assert.Empty(seen);
            Assert.Equal(1, probe.Calls);

            _h.Emu.Hle.RedirectInProcess(ProbeUserLib, EntryProbeModule.FlagNid, hook);
            Assert.True(probe.probeFlagThroughEntry(EntryProbeColor.Blue));
            Assert.Equal(2u, Assert.Single(seen)[0]);
            Assert.Equal(1, probe.Calls);
            _h.Emu.Hle.ClearRedirectInProcess(OtherUserLib, EntryProbeModule.FlagNid);
            _h.Emu.Hle.ClearRedirectInProcess(ProbeUserLib, EntryProbeModule.FlagNid);
        });
    }

    /// <summary>A 64-bit result comes back from r0:r1, a bool from r0, a void call returns; the wrapper of a function
    /// with more than four argument words puts the rest on the stack.</summary>
    [Fact]
    public void ResultsAndStackWordsFollowAapcs()
    {
        EntryProbeModule probe = Probe();
        var seen = new List<uint[]>();
        uint hook = Hook(seen, 0x89AB_CDEF, 0x0123_4567);
        _h.OnGuest(_ =>
        {
            _h.Emu.Hle.RedirectInProcess(0x1234, EntryProbeModule.WideNid, hook);
            _h.Emu.Hle.RedirectInProcess(0x1234, EntryProbeModule.FlagNid, hook);
            _h.Emu.Hle.RedirectInProcess(0x1234, EntryProbeModule.FiveNid, hook);
            Assert.Equal(0x0123_4567_89AB_CDEFL, probe.probeWideThroughEntry(5, new Ptr<uint>(0x8100_0000)));
            Assert.Equal([5u, 0u, 0x8100_0000u], seen[^1][..3]); // the 64-bit argument in r0:r1, the pointer in r2
            Assert.True(probe.probeFlagThroughEntry(EntryProbeColor.Blue));
            Assert.Equal(2u, seen[^1][0]);
            probe.probeFiveThroughEntry(1, 2, 3, 4, 5);
            Assert.Equal([1u, 2u, 3u, 4u, 5u], seen[^1]);
            Assert.Equal(0, probe.Calls);
        });
    }

    /// <summary>A kernel plugin's patch of the function's entry in the firmware kernel modules (code written over it,
    /// as the framework's export hook writes it) takes the call; a NID exported by another library is another
    /// function and keeps its C# call.</summary>
    [Fact]
    public void APatchedFirmwareKernelEntryTakesTheCall()
    {
        const uint ProbeLib = 0x7E57_0001, OtherLib = 0x7E57_0002;
        var module = new SceModuleBuilder("SceEntryProbe");
        int body = module.ReturnConstant(0);
        module.Export("SceEntryProbeForDriver", ProbeLib, 0x0001, (EntryProbeModule.WordsNid, body));
        module.Export("SceOtherForDriver", OtherLib, 0x0001, (EntryProbeModule.FlagNid, body));
        string kd = Path.Combine(_h.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "entryprobe.skprx"), module.Build());
        Core.Firmware.FirmwareKernelImage.Forget(_h.Root);
        try
        {
            Assert.True(_h.Emu.FirmwareKernel.Load() > 0);
            uint words = _h.Emu.FirmwareKernel.EntryOf(ProbeLib, EntryProbeModule.WordsNid);
            uint flag = _h.Emu.FirmwareKernel.EntryOf(OtherLib, EntryProbeModule.FlagNid);
            Assert.NotEqual(0u, words);
            EntryProbeModule probe = Probe();
            var seen = new List<uint[]>();
            uint hook = Hook(seen, 77);
            _h.OnGuest(_ =>
            {
                Assert.Equal(3, probe.probeWordsThroughEntry(1, 2, false, 0));
                Linker.WriteJumpStub(words, hook);
                Linker.WriteJumpStub(flag, hook);
                _h.Emu.InvalidateCode(words, 16);
                _h.Emu.InvalidateCode(flag, 16);
                Assert.Equal(77, probe.probeWordsThroughEntry(4, 5, false, 0));
                Assert.Equal([4u, 5u], Assert.Single(seen)[..2]);
                // the probe's flag function is SceEntryProbeForDriver's: the patched SceOtherForDriver export of its NID
                // is not it
                Assert.False(probe.probeFlagThroughEntry(EntryProbeColor.Red));
                Assert.Single(seen);
                Assert.Equal(2, probe.Calls);
            });
        }
        finally
        {
            Core.Firmware.FirmwareKernelImage.Forget(_h.Root);
        }
    }

    /// <summary>Every wrapper the core has: the export it names resolves to at least one NID, and a user hook on that
    /// NID takes the call with the first argument in r0 (no C# side effect: the hook returns without the
    /// original). A wrapper whose function the NID database does not know, or one that never reaches its hook, fails
    /// here.</summary>
    [Fact]
    public void EveryCoreWrapperReachesAHookOnItsFunction()
    {
        var wrappers = new List<(Core.Hle.HleModule Module, MethodInfo Wrapper, HleExportRef Export)>();
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (Type type in typeof(HleModule).Assembly.GetTypes())
        {
            if (!typeof(HleModule).IsAssignableFrom(type) || type.IsAbstract)
            {
                continue;
            }

            HleModule? module = _h.Emu.Hle.Modules.FirstOrDefault(m => m.GetType() == type);
            foreach (MethodInfo method in type.GetMethods(All))
            {
                if (!method.Name.EndsWith("ThroughEntry", StringComparison.Ordinal))
                {
                    continue;
                }

                string target = method.Name[..^"ThroughEntry".Length];
                FieldInfo field = type.GetField("s_hleEntry_" + target, BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new Xunit.Sdk.XunitException($"{type.Name}.{method.Name}: no export field");
                Assert.True(module != null, $"{type.Name} is not registered in a process");
                wrappers.Add((module!, method, (HleExportRef)field.GetValue(null)!));
            }
        }

        Assert.True(wrappers.Count > 100, $"only {wrappers.Count} wrappers");
        var seen = new List<uint[]>();
        uint hook = Hook(seen, 1);
        var failures = new List<string>();
        _h.OnGuest(_ =>
        {
            foreach ((HleModule module, MethodInfo wrapper, HleExportRef export) in wrappers)
            {
                (uint Nid, string? Library)[] nids = export.Resolved;
                string name = $"{module.GetType().Name}.{wrapper.Name}";
                if (nids.Length == 0)
                {
                    failures.Add($"{name}: no NID");
                    continue;
                }

                _h.Emu.Hle.RedirectInProcess(0x1234, nids[0].Nid, hook);
                seen.Clear();
                ParameterInfo[] parameters = wrapper.GetParameters();
                object?[] args = parameters.Select(p => Value(p.ParameterType, 0x21)).ToArray();
                wrapper.Invoke(module, args);
                _h.Emu.Hle.ClearRedirectInProcess(0x1234, nids[0].Nid);
                if (seen.Count != 1)
                {
                    failures.Add($"{name}: the hook ran {seen.Count} times");
                }
                else if (parameters.Length > 0 && Size(parameters[0].ParameterType) == 4 && seen[0][0] != 0x21)
                {
                    failures.Add($"{name}: r0 was 0x{seen[0][0]:X}");
                }
            }
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static int Size(Type type)
    {
        Type underlying = type.IsEnum ? Enum.GetUnderlyingType(type) : type;
        return underlying == typeof(long) || underlying == typeof(ulong) ? 8 : 4;
    }

    /// <summary>An argument of <paramref name="type"/> whose word is <paramref name="word"/>.</summary>
    private static object Value(Type type, uint word)
    {
        if (type == typeof(bool))
        {
            return true;
        }

        if (type.IsEnum)
        {
            return Enum.ToObject(type, word);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Ptr<>))
        {
            return Activator.CreateInstance(type, word)!;
        }

        return Convert.ChangeType(word, type, System.Globalization.CultureInfo.InvariantCulture);
    }
}

public enum EntryProbeColor
{
    Red = 1,
    Blue = 2,
}

/// <summary>An HLE module of the tests with functions whose wrappers <see cref="HleEntryCallTests"/> calls (the
/// generator makes them for this assembly too).</summary>
public sealed partial class EntryProbeModule : HleModule
{
    public const uint WordsNid = 0x7E57_1001, WideNid = 0x7E57_1002, FlagNid = 0x7E57_1003, FiveNid = 0x7E57_1004;

    public int Calls;

    [Hle(Nid = WordsNid, Library = "SceEntryProbeForDriver")]
    internal int probeWords(uint a, uint b, bool c, long d)
    {
        Calls++;
        return (int)(a + b);
    }

    [Hle(Nid = WideNid)]
    internal long probeWide(long value, Ptr<uint> pointer)
    {
        Calls++;
        return value;
    }

    [Hle(Nid = FlagNid, Library = "SceEntryProbeForDriver")]
    internal bool probeFlag(EntryProbeColor color)
    {
        Calls++;
        return false;
    }

    [Hle(Nid = FiveNid)]
    internal void probeFive(uint a, uint b, int c, short d, byte e)
    {
        Calls++;
    }
}
