using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Cpu.Backends;

/// <summary>
/// Native AArch64 backend (Android, Apple silicon, Linux and Windows on ARM): the same IR as the x64 backend
/// (<see cref="ArmFrontend"/> + <see cref="IrPasses"/>), lowered by <see cref="Arm64Emitter"/> into the shared W^X code
/// arena and run through the same runtime (<see cref="NativeRuntime"/>: trampoline, callout bridge, link slots,
/// dispatch table). Unsupported instructions are interpreter callouts, exactly like on x64. It shares the command-line
/// name "native" with <see cref="NativeX64Compiler"/>; the chain keeps whichever the host supports.
/// </summary>
public sealed unsafe class NativeArm64Compiler : IBlockCompiler
{
    public static readonly NativeArm64Compiler Instance = new();

    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static Arm64Emitter? t_emitter;
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static ArmFrontend? t_frontend; // reuses its IR buffers (consumed before the next block)
    private static readonly bool[] s_neonOp = Enumerable.Range(0, (int)Op.Count).Select(i => ((Op)i).ToString().StartsWith("Neon_", StringComparison.Ordinal)).ToArray();

    public string Name => "native";
    public bool IsSupported => UnsupportedReason == null;
    public string? UnsupportedReason =>
        RuntimeInformation.ProcessArchitecture != Architecture.Arm64 ? $"emits ARM64 code and this host is {RuntimeInformation.ProcessArchitecture}"
        : ExecutableMemory.UnsupportedReason is { } r ? r
        : WriteWatch.Enabled ? "is off while write watchpoints are active" : null;
    public BackendStats Stats { get; } = new() { Phases = new() };

    public long HotInstructions { get; set; } = 2_000;

    /// <summary>The frontend configured for this emitter (its SIMD coverage, no FZ tininess slow paths).</summary>
    public static ArmFrontend NewFrontend(bool reuseIr = false) => new(Arm64Emitter.SupportsVec, tinyCheck: false, reuseIr)
    {
        DeferRegions = (Arm64Emitter.Opts & Arm64Emitter.A64Opt.DeferRegions) != 0,
    };

    public CompiledBlock? Compile(Block b) => Compile(b, NativeRuntime.Instance.Arm64Stubs, hleCallouts: HleCallouts,
        successors: b.Owner is { } cache ? cache.GetFromAnyThread : null);

    /// <summary>Statistics: state stores dropped because the exit's target overwrites the slot first (<see cref="StateLiveness"/>).</summary>
    public static long DeadStoresDropped;

    /// <summary>SVCs call the HLE dispatch from native code (<see cref="ArmFrontend.HleCallouts"/>): on unless
    /// VITASHARP_NATIVE_HLE_CALLOUT=off or the differential verifier runs (it would run every HLE call twice).</summary>
    public static readonly bool HleCallouts = !NativeVerifier.Enabled
        && Environment.GetEnvironmentVariable(Util.EnvVars.NativeHleCallout) is not ("off" or "0");

    /// <summary>
    /// Compiles for the given runtime entry points. On an ARM64 host these are <see cref="NativeRuntime.Arm64Stubs"/>;
    /// tests on other hosts pass their own (the code then runs under an AArch64 emulator).
    /// </summary>
    /// <param name="successors">Finds the already decoded block at a guest address (null: none): exits to such a block
    /// skip the stores of the state it overwrites first (<see cref="Arm64Emitter.A64Opt.Successors"/>).</param>
    public CompiledBlock? Compile(Block b, Arm64Emitter.Runtime stubs, bool hleCallouts = false, Func<uint, bool, Block?>? successors = null)
    {
        long t0 = Stopwatch.GetTimestamp();
        ArmFrontend armFrontend = t_frontend ??= new ArmFrontend(Arm64Emitter.SupportsVec, tinyCheck: false, reuseIr: true);
        armFrontend.HleCallouts = hleCallouts;
        armFrontend.DeferRegions = (Arm64Emitter.Opts & Arm64Emitter.A64Opt.DeferRegions) != 0;
        armFrontend.DynamicExitOperands = true;
        List<uint> deps = t_deps ??= new();
        deps.Clear();
        bool cross = successors != null && !NativeVerifier.Enabled && CpuCore.Probes == null; // analyses beyond the block
        List<Block>? chain = cross && (Arm64Emitter.Opts & Arm64Emitter.A64Opt.Superblocks) != 0 ? BuildChain(b, successors!, deps) : null;
        IrBlock ir = armFrontend.Translate(b, chain);
        long t1 = Stopwatch.GetTimestamp();
        if (cross && (Arm64Emitter.Opts & Arm64Emitter.A64Opt.Successors) != 0)
        {
            DropDeadStores(b, ir, armFrontend, successors!, coldPc: stubs.PinnedCounters && stubs.PatchLinks && (Arm64Emitter.Opts & Arm64Emitter.A64Opt.ExitStubs) != 0, deps);
        }

        b._deps = deps.Count > 0 ? deps.ToArray() : null;
        if ((Arm64Emitter.Opts & Arm64Emitter.A64Opt.MergeFences) != 0)
        {
            IrPasses.MergeFences(ir);
        }

        IrPasses.DeadCode(ir);
        if (NativeX64Compiler.ValidateIr)
        {
            IrPasses.Validate(ir);
        }

        long t2 = Stopwatch.GetTimestamp();
        NativeRuntime rt = NativeRuntime.Instance;
        int exits = 0;
        foreach (IrInst x in ir.Insts)
        {
            if (x.Op == IrOp.ExitStatic)
            {
                exits++;
            }
        }
        // patched links: two words per exit, the target and the patch site (Arm64Emitter.Runtime.PatchLinks)
        int stride = stubs.PatchLinks ? 2 : 1;
        nint* slots = rt.AllocateSlots(exits * stride);
        Arm64Emitter arm64Emitter = t_emitter ??= new Arm64Emitter();
        nint tierUp = 0;
        if (NativeX64Compiler.TierUp && Jit.Chain.Contains(IlJitCompiler.Instance))
        {
            int simd = 0;
            foreach (IrInst x in ir.Insts)
            {
                if (x.Op == IrOp.Callout && x.Imm < (uint)s_neonOp.Length && s_neonOp[x.Imm])
                {
                    simd++;
                }
            }

            if (simd >= 2 && simd * 4 >= b.Insts.Length)
            {
                tierUp = (nint)rt.AllocateSlots(1);
                *(int*)tierUp = (int)Math.Clamp(IlJitCompiler.Instance.HotInstructions / Math.Max(1, b.Insts.Length), 64, int.MaxValue);
            }
        }
        if (!arm64Emitter.Prepare(ir, stubs, k => (nint)(slots + k * stride), tierUp))
        {
            BackendStats.RejectReason = "register allocation out of spill slots";
            return null;
        }
        long t3 = Stopwatch.GetTimestamp();
        int size = 0;
        long encode = 0;
        byte* code = rt.Code.Place(addr =>
        {
            long e0 = Stopwatch.GetTimestamp();
            byte[] bytes = arm64Emitter.Assemble(addr);
            encode = Stopwatch.GetTimestamp() - e0;
            size = bytes.Length;
            return bytes;
        });
        Interlocked.Add(ref NativeX64Compiler.CodeBytes, size); // one counter for both native backends (Report)
        if (stubs.PatchLinks)
        {
            for (int k = 0; k < arm64Emitter.PatchSites.Count; k++)
            {
                if (arm64Emitter.PatchSites[k] >= 0)
                {
                    slots[k * 2 + 1] = (nint)(code + arm64Emitter.PatchSites[k]);
                }
            }
        }

        rt.Register((nint)code, size, b);
        if (NativeCodeMap.Enabled)
        {
            NativeCodeMap.Add((nint)code, size, b.Start, b.Thumb);
        }

        Stats.Phases!.Add(t1 - t0, t2 - t1, arm64Emitter.AllocTicks, t3 - t2 - arm64Emitter.AllocTicks, encode, Stopwatch.GetTimestamp() - t3 - encode);
        CompilePhases.Record(b);
        if (NativeX64Compiler.DumpCode)
        {
            Console.Error.WriteLine($"[native-arm64] {b}\n{ir}\n{Arm64Dump.Dump((nint)code, size)}");
        }

        bool unlogged = false;
        foreach (IrInst x in ir.Insts)
        {
            if (x.Op is IrOp.StrEx or IrOp.StoreD || (x.Op == IrOp.Callout && (x.Imm == ArmFrontend.HleCalloutOp || NativeVerifier.MayStore((Op)x.Imm))))
            {
                unlogged = true;
            }
        }

        return new CompiledBlock(this, (nint)code, new NativeCodeInfo(size, exits, unlogged, Arm64: true));
    }

    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static List<(uint Pc, bool Thumb, int Dead)>? t_targets;
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static List<uint>? t_deps;

    /// <summary>Superblock limits (<see cref="Arm64Emitter.A64Opt.Superblocks"/>).</summary>
    private const int MaxChainBlocks = 4, MaxChainInstructions = 128;

    /// <summary>
    /// <see cref="Arm64Emitter.A64Opt.Superblocks"/>: the blocks that follow <paramref name="b"/> through the not-taken
    /// path of its final conditional branch (and so on), when they already ran (decoded and executed, or compiled): one
    /// translation keeps the guest state in temps across them and saves an exit and an entry per conditional branch.
    /// Their ranges become dependencies. Null when the block does not continue.
    /// </summary>
    private static List<Block>? BuildChain(Block b, Func<uint, bool, Block?> successors, List<uint> deps)
    {
        List<Block>? chain = null;
        Block cur = b;
        int n = b.Insts.Length;
        while ((chain?.Count ?? 1) < MaxChainBlocks && cur.Insts.Length > 0 && ArmFrontend.Continues(cur.Insts[^1]))
        {
            Block? next;
            try
            {
                next = successors(cur.End, cur.Thumb);
            }
            catch (Exception)
            {
                break;
            }
            if (next == null || next == b || next.Thumb != cur.Thumb || next.Insts.Length == 0 || next.Start != cur.End
                || (next.ExecCount == 0 && next.NativeEntry == 0) || n + next.Insts.Length > MaxChainInstructions)
            {
                break;
            }

            (chain ??= [b]).Add(next);
            n += next.Insts.Length;
            deps.Add(next.Start);
            deps.Add(next.End);
            cur = next;
        }
        return chain;
    }

    /// <summary>
    /// <see cref="StateLiveness.DropDeadStores"/> against the decoded targets of the block's static exits. The targets'
    /// code ranges become dependencies of the block (rewriting them drops it, <see cref="Block._deps"/>), published
    /// before the code exists, so an invalidation racing with this compile drops the block too.
    /// </summary>
    private static void DropDeadStores(Block b, IrBlock ir, ArmFrontend armFrontend, Func<uint, bool, Block?> successors, bool coldPc, List<uint> deps)
    {
        Span<IrInst> insts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ir.Insts);
        foreach (ref readonly IrInst x in insts)
        {
            if (x.Op == IrOp.Callout)
            {
                return; // see StateLiveness.DropDeadStores
            }
        }

        List<(uint Pc, bool Thumb, int Dead)> targets = t_targets ??= new();
        targets.Clear();
        foreach (ref IrInst x in insts)
        {
            if (x.Op != IrOp.ExitStatic)
            {
                continue;
            }

            if (coldPc)
            {
                x.Imm3 = StateLiveness.ColdPc; // patched exits store R15 on their dispatcher paths only
            }

            bool thumb = x.Imm2 != 0;
            Block? t;
            int dead;
            try
            {
                t = successors(x.Imm, thumb);
                dead = t == null || t.Insts.Length == 0 ? 0 : StateLiveness.DeadIn(t, armFrontend);
            }
            catch (Exception) // whatever the target holds, this block still compiles (with the stores)
            {
                continue;
            }
            if (dead == 0)
            {
                continue;
            }

            targets.Add((x.Imm, thumb, dead));
            if (t!.Start < b.Start || t.End > b.End)
            {
                deps.Add(t.Start);
                deps.Add(t.End);
            }
        }
        if (targets.Count == 0 && !coldPc)
        {
            return;
        }

        int n = StateLiveness.DropDeadStores(ir, (pc, thumb) =>
        {
            foreach ((uint p, bool th, int d) in targets)
            {
                if (p == pc && th == thumb)
                {
                    return d;
                }
            }

            return 0;
        });
        if (n > 0)
        {
            Interlocked.Add(ref DeadStoresDropped, n);
        }
    }
}
