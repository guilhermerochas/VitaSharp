using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The plugin hooks say whether they are used: the first call into a hook writes one log line (function, caller's
/// process and thread, hook function), the first continuation to the original another, and the session's summary lists
/// every hook with its installer, calls and continuations, "never called" for those no call entered. The counting is
/// guest code (each hook's counting stub), so it holds in every CPU backend and on every path into the hook: the patched
/// function, a call through the hook chain, the import stubs a hook on an HLE function took. Synthetic kernel modules.
/// </summary>
[Collection("guest")]
public sealed class PluginHookCallLogTests(GuestMemoryFixture fixture)
{
    private const uint TargetLib = 0x0BAD7A60, TargetNid = 0x2468ACE0, OtherNid = 0x2468ACE1;
    private const uint ThreadmgrForDriver = 0xE2C40624, GetThreadId = 0x59D06540;

    private sealed class CollectingSink : ILogSink
    {
        public readonly List<string> Messages = [];

        public void Write(LogLevel level, string thread, string message)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }
        }

        public List<string> Starting(string prefix)
        {
            lock (Messages)
            {
                return Messages.Where(m => m.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            }
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly HleHarness Harness;
        public readonly CollectingSink Sink = new();
        private readonly ILogSink? _saved;

        public Rig(GuestMemoryFixture fixture, string backend)
        {
            Harness = new HleHarness(fixture, "hookcalls", kernelPlugins: true, cpuBackend: backend);
            _saved = Log.Sink;
            Log.Sink = Sink;
        }

        public PluginFramework Framework => Harness.Module<PluginFramework>();

        public List<string> Summary() => PluginFramework.HookSummaryLines(Harness.Emu).ToList();

        public LoadedModule Load(string file, SceModuleBuilder builder)
        {
            string dir = Path.Combine(Harness.Root, "ur0", "tai");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, file), builder.Build());
            return Harness.Emu.LoadModule("ur0:tai/" + file, kernel: true);
        }

        /// <summary>The target module: two exported functions returning 7 and 8.</summary>
        public (uint Returns7, uint Returns8) LoadTarget()
        {
            var builder = new SceModuleBuilder("HookTarget");
            int returns7 = builder.ReturnConstant(7);
            int returns8 = builder.ReturnConstant(8);
            builder.Export("HookTargetLib", TargetLib, 0x0001, (TargetNid, returns7), (OtherNid, returns8));
            uint code = Load("target.skprx", builder).Segments[0].Address;
            return (SceModuleBuilder.FunctionAddress(code, returns7), SceModuleBuilder.FunctionAddress(code, returns8));
        }

        public int HookExport(uint hookRef, uint nid, uint hookFunction)
        {
            return Call(Framework, "HookFunctionExportForKernel", KernelSpace.KernelPid, hookRef, Harness.Str("HookTarget"), TargetLib, nid, hookFunction);
        }

        public void Dispose()
        {
            Log.Sink = _saved;
            Harness.Dispose();
        }
    }

    /// <summary>A hook that continues as the framework's continuation macro does: the next hook's function, or the
    /// record's old after the last one (A32, r1 kept on the stack, r12 scratch).</summary>
    private static uint[] Continuing(uint hookRef) =>
    [
        0xE300C000 | ((hookRef & 0xF000) << 4) | (hookRef & 0xFFF),         // movw ip, #lo(hookRef)
        0xE340C000 | ((hookRef >> 12) & 0xF0000) | ((hookRef >> 16) & 0xFFF), // movt ip, #hi(hookRef)
        0xE59CC000,                                                            // ldr ip, [ip]        the record
        0xE52D1004,                                                            // push {r1}
        0xE59C1000,                                                            // ldr r1, [ip]        next
        0xE3510000,                                                            // cmp r1, #0
        0x1591C004,                                                            // ldrne ip, [r1, #4]  next->func
        0x059CC008,                                                            // ldreq ip, [ip, #8]  old
        0xE49D1004,                                                            // pop {r1}
        0xE12FFF1C,                                                            // bx ip
    ];

    private static int Count(List<string> lines, string part) => lines.Count(l => l.Contains(part, StringComparison.Ordinal));

    public static TheoryData<string> Backends()
    {
        var names = new List<string> { "interp" };
        foreach (Core.Cpu.Backends.IBlockCompiler backend in Core.Cpu.Jit.All)
        {
            if (backend.IsSupported && !names.Contains(backend.Name))
            {
                names.Add(backend.Name);
            }
        }

        var data = new TheoryData<string>();
        foreach (string name in names)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void ACalledHookAndANeverCalledHookAreLoggedAndSummed(string backend)
    {
        using var rig = new Rig(fixture, backend);
        (uint target, _) = rig.LoadTarget();
        uint calledRef = rig.Harness.Word(0);
        uint unusedRef = rig.Harness.Word(0);
        var hooks = new SceModuleBuilder("HookProvider");
        int continuing = hooks.Arm(Continuing(calledRef));
        int answers9 = hooks.ReturnConstant(9);
        uint code = rig.Load("hooks.skprx", hooks).Segments[0].Address;
        uint continuingAddress = hooks.AddressOf(code, continuing);
        Assert.True(rig.HookExport(calledRef, TargetNid, continuingAddress) > 0);
        Assert.True(rig.HookExport(unusedRef, OtherNid, hooks.AddressOf(code, answers9)) > 0);

        // many calls: the JIT backends compile the stub and the hook after the first runs
        const int Calls = 300;
        int callerPid = 0;
        rig.Harness.OnGuest(t =>
        {
            callerPid = t.Pid;
            for (int i = 0; i < Calls; i++)
            {
                Assert.Equal(7u, t.CallGuest(target));
            }
        }, "hook-caller");

        // one line on the first call and one on the first continuation, none for the hook no call entered
        List<string> called = rig.Sink.Starting("plugin hook 1 called: ");
        Assert.Single(called);
        Assert.Contains($"of library 0x{TargetLib:X8}", called[0]);
        Assert.True(called[0].Contains($"from pid 0x{callerPid:X} (thread hook-caller) -> hook function 0x{continuingAddress:X8}", StringComparison.Ordinal), called[0]);
        Assert.Single(rig.Sink.Starting("plugin hook 1 continued to the original: "));
        Assert.Empty(rig.Sink.Starting("plugin hook 2 called"));

        List<string> summary = rig.Summary();
        Assert.Equal("Plugin hooks: 2 installed, 1 called, 1 never called, 0 refused", summary[0]);
        Assert.Equal(1, Count(summary, $"plugin hook 1 (export hook, kernel, pid 0x{KernelSpace.KernelPid:X}): "));
        Assert.Equal(1, Count(summary, $"installed by HookProvider: {Calls} calls (first from pid 0x{callerPid:X} (thread hook-caller)), continued to the original {Calls}, to the next hook 0"));
        Assert.Equal(1, Count(summary, "plugin hook 2 (export hook, kernel"));
        Assert.Equal(1, Count(summary, "installed by HookProvider: never called"));
    }

    [Fact]
    public void AChainCountsEachHookAndWhereItContinued()
    {
        using var rig = new Rig(fixture, "interp");
        (uint target, _) = rig.LoadTarget();
        uint firstRef = rig.Harness.Word(0);
        uint secondRef = rig.Harness.Word(0);
        var hooks = new SceModuleBuilder("ChainProvider");
        int first = hooks.Arm(Continuing(firstRef));
        int second = hooks.Arm(Continuing(secondRef));
        uint code = rig.Load("chain.skprx", hooks).Segments[0].Address;
        Assert.True(rig.HookExport(firstRef, TargetNid, hooks.AddressOf(code, first)) > 0);
        Assert.True(rig.HookExport(secondRef, TargetNid, hooks.AddressOf(code, second)) > 0);
        rig.Harness.OnGuest(t =>
        {
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(7u, t.CallGuest(target));
            }
        });

        // the first hook continues to the second (through its record's next), the second to the original
        Assert.Single(rig.Sink.Starting("plugin hook 1 continued to the next hook (plugin hook 2): "));
        Assert.Single(rig.Sink.Starting("plugin hook 2 called: "));
        Assert.Single(rig.Sink.Starting("plugin hook 2 continued to the original: "));
        Assert.Empty(rig.Sink.Starting("plugin hook 1 continued to the original"));
        List<string> summary = rig.Summary();
        Assert.Contains(summary, l => l.Contains("plugin hook 1 (", StringComparison.Ordinal) && l.Contains("5 calls", StringComparison.Ordinal) && l.EndsWith("continued to the original 0, to the next hook 5", StringComparison.Ordinal));
        Assert.Contains(summary, l => l.Contains("plugin hook 2 (", StringComparison.Ordinal) && l.Contains("5 calls", StringComparison.Ordinal) && l.EndsWith("continued to the original 5, to the next hook 0", StringComparison.Ordinal));
    }

    [Fact]
    public void AHookThatAnswersItselfIsCalledButNeverContinues()
    {
        using var rig = new Rig(fixture, "interp");
        (uint target, _) = rig.LoadTarget();
        uint hookRef = rig.Harness.Word(0);
        var hooks = new SceModuleBuilder("Answers");
        int answers = hooks.ReturnConstant(42);
        uint code = rig.Load("answers.skprx", hooks).Segments[0].Address;
        int uid = rig.HookExport(hookRef, TargetNid, hooks.AddressOf(code, answers));
        Assert.True(uid > 0);
        int callerPid = 0;
        rig.Harness.OnGuest(t =>
        {
            callerPid = t.Pid;
            Assert.Equal(42u, t.CallGuest(target));
            Assert.Equal(42u, t.CallGuest(target));
            Assert.Equal(42u, t.CallGuest(target));
            Assert.Equal(0, Call(rig.Framework, "HookReleaseForKernel", uid, GuestMemory.R32(hookRef)));
            Assert.Equal(7u, t.CallGuest(target));
        });

        Assert.Single(rig.Sink.Starting("plugin hook 1 called: "));
        Assert.Empty(rig.Sink.Starting("plugin hook 1 continued"));
        Assert.Contains(rig.Summary(), l => l.Contains("plugin hook 1 (", StringComparison.Ordinal) && l.EndsWith($"3 calls (first from pid 0x{callerPid:X} (thread hle-test)), continued to the original 0, to the next hook 0, released", StringComparison.Ordinal));
    }

    [Fact]
    public void AHookOnAnHleFunctionCountsTheCallsThroughEveryImportStub()
    {
        using var rig = new Rig(fixture, "interp");
        SceModuleBuilder client = new SceModuleBuilder("HleClient").Import("SceThreadmgrForDriver", ThreadmgrForDriver, GetThreadId);
        int callsImport = client.CallImport(0, 0);
        uint clientCall = SceModuleBuilder.FunctionAddress(rig.Load("client.skprx", client).Segments[0].Address, callsImport);
        uint hookRef = rig.Harness.Word(0);
        var hooks = new SceModuleBuilder("HleHook");
        int continuing = hooks.Arm(Continuing(hookRef));
        uint hookFunction = hooks.AddressOf(rig.Load("hlehook.skprx", hooks).Segments[0].Address, continuing);
        uint stubOut = rig.Harness.Word(0);
        Assert.Equal(0, Call(rig.Framework, "ModuleGetImportFunc", KernelSpace.KernelPid, rig.Harness.Str("HleClient"), ThreadmgrForDriver, GetThreadId, stubOut));
        int uid = Convert.ToInt32(Invoke(rig.Framework, "HookAbsolute", KernelSpace.KernelPid, hookRef, GuestMemory.R32(stubOut), hookFunction, ThreadmgrForDriver, GetThreadId));
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");

        // a module loaded after the hook: its stub reaches the hook too, and the hook continues into the HLE function
        SceModuleBuilder later = new SceModuleBuilder("LaterClient").Import("SceThreadmgrForDriver", ThreadmgrForDriver, GetThreadId);
        int laterCall = later.CallImport(0, 0);
        uint laterAddress = SceModuleBuilder.FunctionAddress(rig.Load("later.skprx", later).Segments[0].Address, laterCall);
        rig.Harness.OnGuest(t =>
        {
            Assert.Equal((uint)t.Uid, t.CallGuest(clientCall));
            Assert.Equal((uint)t.Uid, t.CallGuest(laterAddress));
        });

        List<string> called = rig.Sink.Starting("plugin hook 1 called: ");
        Assert.Single(called);
        Assert.Contains($"of library 0x{ThreadmgrForDriver:X8}", called[0]);
        Assert.Single(rig.Sink.Starting("plugin hook 1 continued to the original: "));
        Assert.Contains(rig.Summary(), l => l.Contains("plugin hook 1 (absolute hook, kernel", StringComparison.Ordinal) && l.Contains("installed by HleHook: 2 calls", StringComparison.Ordinal) && l.Contains("continued to the original 2", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProcessServesTheCountingStubsSvc()
    {
        // the stubs are kernel memory, run by threads of any process: another process's registry, which never
        // registered anything, serves the stub's SVC with the same host function
        using var rig = new Rig(fixture, "interp");
        (uint target, _) = rig.LoadTarget();
        uint hookRef = rig.Harness.Word(0);
        var hooks = new SceModuleBuilder("SvcHook");
        int answers = hooks.ReturnConstant(1);
        uint code = rig.Load("svc.skprx", hooks).Segments[0].Address;
        Assert.True(rig.HookExport(hookRef, TargetNid, hooks.AddressOf(code, answers)) > 0);
        uint stub = GuestMemory.R32(GuestMemory.R32(hookRef) + 4);
        uint svc = GuestMemory.R32(stub + 27 * 4);
        Assert.Equal(0xEF000000u, svc & 0xFF000000);
        Assert.Equal(svc, GuestMemory.R32(stub + 49 * 4));
        var other = new HleRegistry(rig.Harness.Emu);
        HleFunction function = other.GetFunction(svc & 0xFFFFFF);
        Assert.Equal("PluginFramework:hookFirstEvent", function.Name);
        Assert.True(function.Implemented);
        _ = target;
    }

    [Fact]
    public void ARecordingsReportListsTheHooksWithoutThePluginsPresent()
    {
        // a recording: context.txt keeps the hooks at its start and its stop; the analysis reads only that file
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-hookcapture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var rig = new Rig(fixture, "interp"))
            {
                (uint target, _) = rig.LoadTarget();
                uint calledRef = rig.Harness.Word(0);
                uint unusedRef = rig.Harness.Word(0);
                var hooks = new SceModuleBuilder("CaptureHooks");
                int continuing = hooks.Arm(Continuing(calledRef));
                int answers = hooks.ReturnConstant(9);
                uint code = rig.Load("capture.skprx", hooks).Segments[0].Address;
                Assert.True(rig.HookExport(calledRef, TargetNid, hooks.AddressOf(code, continuing)) > 0);
                Assert.True(rig.HookExport(unusedRef, OtherNid, hooks.AddressOf(code, answers)) > 0);
                rig.Harness.OnGuest(t => t.CallGuest(target)); // before the recording
                string start = string.Join(Environment.NewLine, PluginFramework.HookContextLines(rig.Harness.Emu));
                rig.Harness.OnGuest(t =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        t.CallGuest(target);
                    }
                });
                string stop = string.Join(Environment.NewLine, PluginFramework.HookContextLines(rig.Harness.Emu));
                File.WriteAllLines(Path.Combine(dir, "context.txt"), ["[start of the recording]", start, "", "[stop of the recording]", stop]);
            }

            // the emulator, the game and the plugins are gone: the table comes from the recording alone
            List<string> section = PluginHookStats.ReportSection(Core.Capture.CaptureAnalyzer.ContextSection(dir, stop: false),
                Core.Capture.CaptureAnalyzer.ContextSection(dir, stop: true));
            Assert.Equal("Plugin hooks (calls and continuations: during the recording / in the session):", section[0]);
            Assert.Contains(section, l => l.StartsWith("  hook 1: export hook, kernel, ", StringComparison.Ordinal) && l.Contains("installed by CaptureHooks", StringComparison.Ordinal)
                && l.Contains("calls 4 / 5, continued to the original 4 / 5, to the next hook 0 / 0; first call from pid 0x", StringComparison.Ordinal));
            Assert.Contains(section, l => l.StartsWith("  hook 2: export hook, kernel, ", StringComparison.Ordinal) && l.EndsWith(": never called", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ARefusedHookIsInTheSummaryWithItsError()
    {
        using var rig = new Rig(fixture, "interp");
        rig.LoadTarget();
        var hooks = new SceModuleBuilder("Refused");
        int answers = hooks.ReturnConstant(1);
        uint code = rig.Load("refused.skprx", hooks).Segments[0].Address;
        Assert.Equal(unchecked((int)0x90010002), rig.HookExport(rig.Harness.Word(0), 0x1u, hooks.AddressOf(code, answers)));
        List<string> summary = rig.Summary();
        Assert.Equal("Plugin hooks: 0 installed, 0 called, 0 never called, 1 refused", summary[0]);
        Assert.Contains(summary, l => l.Contains("installed by Refused: refused (0x90010002)", StringComparison.Ordinal));
        Assert.Single(rig.Sink.Starting("plugin hook 1 refused (0x90010002): "));
    }
}
