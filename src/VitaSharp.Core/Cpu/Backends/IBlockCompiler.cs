namespace VitaSharp.Core.Cpu.Backends;

/// <summary>
/// A CPU execution backend that turns a decoded guest <see cref="Block"/> into executable code. Backends form a chain
/// (see <see cref="Jit"/>): a block is offered to the first one once it is hot enough for it; a backend that cannot
/// handle the block returns null and the next backend gets it when the block reaches that backend's threshold. The
/// interpreter (<see cref="CpuCore.Interpret"/>) is the implicit last resort and runs every block until then.
/// Block lookup, invalidation, counters and cycle accounting live in <see cref="CpuCore"/> and are shared by all.
/// </summary>
public interface IBlockCompiler
{
    /// <summary>Short id used on the command line (--cpu=NAME) and in logs.</summary>
    string Name { get; }

    /// <summary>False when the backend cannot run on this host or configuration (CPU architecture, debug features).</summary>
    bool IsSupported { get; }

    /// <summary>Executed guest instructions (executions x block length) before a block is worth compiling.</summary>
    long HotInstructions { get; }

    /// <summary>Compiles the block, or returns null when this backend cannot handle it.</summary>
    CompiledBlock? Compile(Block b);

    /// <summary>Compilation statistics for logs.</summary>
    BackendStats Stats { get; }
}

public sealed class BackendStats
{
    public long Blocks, Instructions, Ticks, Rejected;

    public override string ToString() =>
        $"{Blocks} blocks ({Instructions} instructions, {Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms compiling, " +
        $"{Ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, Blocks):F0} us/block, {Rejected} rejected)";
}

/// <summary>Code produced by a backend for one block: a managed delegate or a native entry point.</summary>
public sealed unsafe class CompiledBlock
{
    public IBlockCompiler Backend { get; }
    /// <summary>Managed code (IL backend): called with the CPU state.</summary>
    public JitBlockFn? Managed { get; }
    /// <summary>Native machine code entry (native backend), run through the native backend's entry trampoline.</summary>
    public nint NativeEntry { get; }
    /// <summary>Backend-specific data kept alive with the block (native: link slots).</summary>
    public object? Tag { get; }

    public CompiledBlock(IBlockCompiler backend, JitBlockFn managed) { Backend = backend; Managed = managed; }
    public CompiledBlock(IBlockCompiler backend, nint nativeEntry, object? tag) { Backend = backend; NativeEntry = nativeEntry; Tag = tag; }
}
