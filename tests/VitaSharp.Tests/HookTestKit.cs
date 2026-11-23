using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Guest code and execution paths shared by the plugin hook validation tests (<see cref="PluginHookMatrixTests"/>,
/// <see cref="PluginHookChainFuzzTests"/>, <see cref="PluginHookMatrixProcessTests"/>): an A32 hook that continues the
/// chain as the framework's TAI_CONTINUE does and appends its number to the result, target functions in A32 and Thumb
/// whose first instructions can run from a trampoline, and the CPU paths a caller of a hooked function can take.
/// </summary>
internal static class HookTestKit
{
    /// <summary>The byte offset of the reference variable's address in <see cref="ChainHook"/>.</summary>
    public const uint RefLiteral = 12 * 4;

    /// <summary>The size of <see cref="ChainHook"/>'s code.</summary>
    public const uint ChainHookSize = 13 * 4;

    /// <summary>
    /// A hook in A32 that continues the chain as TAI_CONTINUE does (next = ref->next; next ? next->func : ref->old,
    /// called with the caller's r0-r3) and returns what that returned times 10 plus <paramref name="number"/>. The
    /// address of the variable that holds its reference goes at <see cref="RefLiteral"/>.
    /// </summary>
    public static int ChainHook(SceModuleBuilder module, byte number)
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

    /// <summary>An A32 function returning r0 + r1 + r2 whose first three instructions can run from a trampoline.</summary>
    public static int ArmSum(SceModuleBuilder module)
    {
        return module.Arm(0xE0800001, 0xE0800002, 0xE2800000, 0xE12FFF1E); // add r0, r1; add r0, r2; add r0, #0; bx lr
    }

    /// <summary>A Thumb function returning r0 + r1 + r2: a 16-bit, a 32-bit and two 16-bit instructions (the 10 bytes
    /// the jump replaces), then the return.</summary>
    public static int ThumbSum(SceModuleBuilder module)
    {
        // adds r0, r0, r1; add.w r0, r0, r2; nop; nop; nop; bx lr
        return module.Thumb(0x1840, 0xEB00, 0x0002, 0xBF00, 0xBF00, 0xBF00, 0x4770);
    }

    /// <summary>The arguments of the <paramref name="index"/>th call of a cell: all different, so a hook that loses
    /// or swaps one gives another sum.</summary>
    public static (uint A, uint B, uint C) Arguments(int index) => ((uint)index + 1, (uint)index * 3 + 100, (uint)index * 7 + 10_000);

    /// <summary>Whether the block at <paramref name="address"/> (bit 0: Thumb) of <paramref name="emu"/> is
    /// translated: compiled by a backend, or translated ahead by the precompilation.</summary>
    public static bool Translated(Emulator emu, uint address)
    {
        return emu.Code.TryGet(address & ~1u, (address & 1) != 0, out Block? block) && block is { Invalidated: false }
            && (block.NativeEntry != 0 || block.Compiled != null || block.JitState == Jit.AheadPending);
    }

    /// <summary>Translates the blocks at <paramref name="entries"/> ahead, as the code precompilation does before a
    /// game reaches them (published when the guest first gets there), and waits for it.</summary>
    public static void Precompile(Emulator emu, params uint[] entries)
    {
        var seeds = new List<((uint Pc, bool Thumb) Target, CodePrecompiler.Priority Priority)>();
        foreach (uint entry in entries)
        {
            seeds.Add(((entry & ~1u, (entry & 1) != 0), CodePrecompiler.Priority.Cached));
        }

        var run = CodePrecompiler.Run(emu.Code, emu.Space, seeds, null, CodePrecompiler.Priority.Cached, 1, null, CancellationToken.None);
        Assert.True(run.WaitFinished(TimeSpan.FromMinutes(2)), "the precompilation did not finish");
        run.Stop();
    }

    /// <summary>The words at <paramref name="address"/> (the bytes a hook's jump replaces).</summary>
    public static uint[] Words(uint address, int count)
    {
        uint start = address & ~1u;
        uint[] words = new uint[count];
        for (int i = 0; i < count; i++)
        {
            words[i] = GuestMemory.R32(start + (uint)i * 4);
        }

        return words;
    }
}

/// <summary>
/// The CPU path the callers of a hooked function take, for the life of a test: the interpreter, the IL backend, the
/// native backend of the host (x64, or ARM64 on an ARM64 host: the macOS runners and Android, the same core), or code
/// translated ahead by the precompilation (the code cache's path). The JIT switches are process-wide: the previous
/// ones come back on <see cref="Dispose"/>. The thresholds make every block compile once it ran
/// <see cref="Jit.MinExecutions"/> times, so a test warms its code with a loop and then waits for the JIT.
/// </summary>
public sealed class HookExecutionPath : IDisposable
{
    public const string Interpreter = "interp";
    public const string Il = "il";
    public const string Native = "native";
    public const string Precompiled = "precompiled";

    private readonly bool _enabled;
    private readonly string _mode;
    private readonly long _rate;
    private readonly long _x64Hot;
    private readonly long _arm64Hot;
    private readonly long _ilHot;

    public string Name { get; }

    /// <summary>The names of the paths this host has (the native one only where a native backend runs).</summary>
    public static IReadOnlyList<string> SupportedNames
    {
        get
        {
            var names = new List<string> { Interpreter };
            if (IlJitCompiler.Instance.IsSupported)
            {
                names.Add(Il);
            }

            if (Jit.All.Any(b => b.Name == Native && b.IsSupported))
            {
                names.Add(Native);
            }

            if (Jit.All.Any(b => b.IsSupported))
            {
                names.Add(Precompiled);
            }

            return names;
        }
    }

    /// <summary><see cref="SupportedNames"/> as theory data.</summary>
    public static TheoryData<string> Supported
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in SupportedNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>Selects <paramref name="name"/>. Call it after the test's emulator is made (its constructor sets the
    /// JIT switches) and before guest code runs.</summary>
    public HookExecutionPath(string name)
    {
        Name = name;
        _enabled = Jit.Enabled;
        _mode = Jit.Mode;
        _rate = CpuCore.InstructionsPerSecond;
        _x64Hot = NativeX64Compiler.Instance.HotInstructions;
        _arm64Hot = NativeArm64Compiler.Instance.HotInstructions;
        _ilHot = IlJitCompiler.Instance.HotInstructions;
        CpuCore.InstructionsPerSecond = 0;
        // the thresholds first: a backend chain takes its first threshold when it is selected
        long hot = name == Precompiled ? long.MaxValue / 4 : 1;
        NativeX64Compiler.Instance.HotInstructions = hot;
        NativeArm64Compiler.Instance.HotInstructions = hot;
        IlJitCompiler.Instance.HotInstructions = hot;
        Jit.Enabled = name != Interpreter;
        Assert.True(Jit.Select(name == Precompiled ? null : name), $"backend {name} not available");
    }

    /// <summary>Whether blocks get compiled while they run (the IL and native paths).</summary>
    public bool CompilesHotCode => Name is Il or Native;

    /// <summary>How many times a test runs a call before it checks the code is translated.</summary>
    public int WarmCalls => CompilesHotCode ? Jit.MinExecutions + 16 : 2;

    /// <summary>Waits for the JIT's queue (a no-op on the interpreter and precompiled paths).</summary>
    public void WaitForJit()
    {
        if (CompilesHotCode)
        {
            Assert.True(Jit.WaitIdle(TimeSpan.FromMinutes(2)), "the JIT did not finish");
        }
    }

    /// <summary>Checks the blocks at <paramref name="entries"/> are translated on this path (nothing on the
    /// interpreter).</summary>
    public void AssertTranslated(Emulator emu, string what, params uint[] entries)
    {
        if (Name == Interpreter)
        {
            return;
        }

        foreach (uint entry in entries)
        {
            Assert.True(HookTestKit.Translated(emu, entry), $"{what}: the block at 0x{entry:X8} is not translated on the {Name} path");
        }
    }

    public void Dispose()
    {
        NativeX64Compiler.Instance.HotInstructions = _x64Hot;
        NativeArm64Compiler.Instance.HotInstructions = _arm64Hot;
        IlJitCompiler.Instance.HotInstructions = _ilHot;
        CpuCore.InstructionsPerSecond = _rate;
        Jit.Select(_mode);
        Jit.Enabled = _enabled;
    }
}
