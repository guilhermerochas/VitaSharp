using System.Security.Cryptography;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The action of a hook in its first calls (VITASHARP_PLUGIN_HOOK_TRACE, 8 by default): the arguments, whether it
/// continued or answered itself, the original's value and the value the caller got ("changed" when they differ), the
/// memory an argument points to that the call wrote; the caller's lr comes back through the stub, so every call returns
/// where it should, the traced ones and the later ones. A recording also keeps the plugin configuration and modules.
/// </summary>
public sealed partial class PluginHookCallLogTests
{
    /// <summary>A hook that calls the original through its record's old and returns its value plus one (A32).</summary>
    private static uint[] AddsOne(uint hookRef) =>
    [
        0xE92D4010,                                                            // push {r4, lr}
        0xE300C000 | ((hookRef & 0xF000) << 4) | (hookRef & 0xFFF),         // movw ip, #lo(hookRef)
        0xE340C000 | ((hookRef >> 12) & 0xF0000) | ((hookRef >> 16) & 0xFFF), // movt ip, #hi(hookRef)
        0xE59CC000,                                                            // ldr ip, [ip]        the record
        0xE59CC008,                                                            // ldr ip, [ip, #8]    old
        0xE12FFF3C,                                                            // blx ip
        0xE2800001,                                                            // add r0, r0, #1
        0xE8BD8010,                                                            // pop {r4, pc}
    ];

    /// <summary>A hook that answers itself and writes 0x55 to the word its second argument points to (A32).</summary>
    private static readonly uint[] s_writesOutput =
    [
        0xE3A02055, // mov r2, #0x55
        0xE5812000, // str r2, [r1]
        0xE3A00000, // mov r0, #0
        0xE12FFF1E, // bx lr
    ];

    private (Rig Rig, uint Target, uint HookRef, uint Code, SceModuleBuilder Hooks) HookWith(Func<uint, uint[]> hook, string backend = "interp")
    {
        var rig = new Rig(fixture, backend);
        (uint target, _) = rig.LoadTarget();
        uint hookRef = rig.Harness.Word(0);
        var hooks = new SceModuleBuilder("TraceHooks");
        int function = hooks.Arm(hook(hookRef));
        uint code = rig.Load("trace.skprx", hooks).Segments[0].Address;
        Assert.True(rig.HookExport(hookRef, TargetNid, hooks.AddressOf(code, function)) > 0);
        return (rig, target, hookRef, code, hooks);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void AHookThatPassesTheCallOnShowsTheArgumentsAndTheSameValue(string backend)
    {
        (Rig rig, uint target, _, _, _) = HookWith(Continuing, backend);
        using (rig)
        {
            rig.Harness.OnGuest(t =>
            {
                for (uint i = 0; i < 20; i++)
                {
                    Assert.Equal(7u, t.CallGuest(target, 5, i, 0, 0));
                }
            });

            List<string> calls = rig.Sink.Starting("plugin hook 1 call ");
            Assert.Equal(8, calls.Count);
            Assert.StartsWith("plugin hook 1 call 1: r0=0x00000005 r1=0x00000000", calls[0]);
            Assert.StartsWith("plugin hook 1 call 8: r0=0x00000005 r1=0x00000007", calls[7]);
            Assert.All(calls, l => Assert.Contains("; continued to the original, which returned 0x00000007; returned 0x00000007 to the caller", l));
            Assert.All(calls, l => Assert.DoesNotContain("(changed)", l));
            Assert.Contains(rig.Summary(), l => l.Contains("plugin hook 1 (", StringComparison.Ordinal)
                && l.Contains("20 calls", StringComparison.Ordinal)
                && l.EndsWith("; first 8 calls traced: returned 0x7 x8, changed 0, answered itself 0, wrote outputs 0", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void AHookThatChangesTheReturnIsMarkedChanged(string backend)
    {
        (Rig rig, uint target, _, _, _) = HookWith(AddsOne, backend);
        using (rig)
        {
            rig.Harness.OnGuest(t =>
            {
                for (int i = 0; i < 12; i++)
                {
                    Assert.Equal(8u, t.CallGuest(target));
                }
            });

            List<string> calls = rig.Sink.Starting("plugin hook 1 call ");
            Assert.Equal(8, calls.Count);
            Assert.All(calls, l => Assert.Contains("; continued to the original, which returned 0x00000007; returned 0x00000008 to the caller (changed)", l));
            Assert.Contains(rig.Summary(), l => l.EndsWith("; first 8 calls traced: returned 0x8 x8, changed 8, answered itself 0, wrote outputs 0", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AHookThatAnswersItselfAndWritesTheOutputShowsTheWrite()
    {
        (Rig rig, uint target, _, _, _) = HookWith(_ => s_writesOutput);
        using (rig)
        {
            uint output = rig.Harness.Word(0);
            rig.Harness.OnGuest(t =>
            {
                Assert.Equal(0u, t.CallGuest(target, 1, output));
                Assert.Equal(0x55u, GuestMemory.R32(output));
            });

            string call = Assert.Single(rig.Sink.Starting("plugin hook 1 call "));
            Assert.Contains($"; answered itself; returned 0x00000000 to the caller; wrote r1 0x{output:X8} bytes 0-0 (now 55)", call);
            Assert.Contains(rig.Summary(), l => l.Contains("answered itself 1", StringComparison.Ordinal)
                && l.EndsWith("first 1 calls traced: returned 0x0 x1, changed 0, answered itself 1, wrote outputs 1", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AChainShowsWhatTheNextHookReturned()
    {
        using var rig = new Rig(fixture, "interp");
        (uint target, _) = rig.LoadTarget();
        uint firstRef = rig.Harness.Word(0);
        uint secondRef = rig.Harness.Word(0);
        var hooks = new SceModuleBuilder("ChainTrace");
        int first = hooks.Arm(Continuing(firstRef));
        int second = hooks.Arm(AddsOne(secondRef));
        uint code = rig.Load("chaintrace.skprx", hooks).Segments[0].Address;
        Assert.True(rig.HookExport(firstRef, TargetNid, hooks.AddressOf(code, first)) > 0);
        Assert.True(rig.HookExport(secondRef, TargetNid, hooks.AddressOf(code, second)) > 0);
        rig.Harness.OnGuest(t => Assert.Equal(8u, t.CallGuest(target)));

        // the first hook goes on to the second (whose value it passes on), the second to the original
        Assert.Contains("; continued to the next hook, which returned 0x00000008; returned 0x00000008 to the caller", Assert.Single(rig.Sink.Starting("plugin hook 1 call ")));
        Assert.Contains("; continued to the original, which returned 0x00000007; returned 0x00000008 to the caller (changed)", Assert.Single(rig.Sink.Starting("plugin hook 2 call ")));
    }

    [Fact]
    public void ARecordingKeepsThePluginConfigurationAndModulesWithoutHostPaths()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-hookplugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string root;
            byte[] started;
            using (var rig = new Rig(fixture, "interp"))
            {
                root = rig.Harness.Root;
                string tai = Path.Combine(root, "ur0", "tai");
                Directory.CreateDirectory(tai);
                started = new SceModuleBuilder("StartedPlugin").Start(0).Build();
                File.WriteAllBytes(Path.Combine(tai, "started.skprx"), started);
                File.WriteAllBytes(Path.Combine(tai, "missing.skprx"), new SceModuleBuilder("MissingPlugin").Import("SceNothingHere", 0x0BADBEEF, 0x12345678).Start(0).Build());
                File.WriteAllText(Path.Combine(tai, "config.txt"), "# plugins\n*KERNEL\nur0:tai/started.skprx\nur0:tai/missing.skprx\nur0:tai/absent.skprx\n");
                rig.Harness.Emu.LoadPlugins();
                File.WriteAllLines(Path.Combine(dir, "context.txt"),
                    ["[stop of the recording]", .. PluginFramework.HookContextLines(rig.Harness.Emu)]);
            }

            // the analysis reads the recording alone
            List<string> section = PluginHookStats.ReportSection([], Core.Capture.CaptureAnalyzer.ContextSection(dir, stop: true));
            string text = string.Join("\n", section);
            Assert.Equal("Plugins (at the stop of the recording):", section[0]);
            Assert.Contains("  build ", text);
            Assert.Contains(", firmware ", text);
            Assert.Contains("configuration ur0:", text);
            Assert.Contains("| ur0:tai/started.skprx", text);
            string hash = Convert.ToHexString(SHA256.HashData(started)).ToLowerInvariant();
            Assert.Contains($"module ur0:tai/started.skprx kernel StartedPlugin sha256 {hash}, {started.Length} bytes loaded and started (StartedPlugin)", text);
            Assert.Contains("module ur0:tai/missing.skprx kernel MissingPlugin", text);
            Assert.Contains("not started: no module provides SceNothingHere unresolved imports: SceNothingHere:", text);
            Assert.Contains("module ur0:tai/absent.skprx kernel - file not found not loaded", text);
            Assert.DoesNotContain(root, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
