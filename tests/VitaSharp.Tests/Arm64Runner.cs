using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Native;

namespace VitaSharp.Tests;

/// <summary>Where the ARM64 backend's tests run its code: natively on ARM64 hosts, in <see cref="Arm64Sandbox"/> elsewhere.</summary>
public unsafe interface IArm64Runner : IDisposable
{
    /// <summary>Runtime entry points to compile against.</summary>
    Arm64Emitter.Runtime Stubs { get; }
    /// <summary>Runs generated code from <paramref name="entry"/> through the trampoline until it returns.</summary>
    void Run(CpuState* s, nint entry);
    /// <summary>Makes the indirect-exit table used by <see cref="Stubs"/> point pc/thumb at <paramref name="entry"/>.</summary>
    void Publish(uint pc, bool thumb, nint entry);
    /// <summary>Calls an AAPCS64 function with two arguments.</summary>
    void Call(ulong fn, ulong arg0, ulong arg1);
    /// <summary>Callouts run through this runner (sandbox only; -1 when not counted).</summary>
    long Callouts { get; }
}

public static class Arm64Runner
{
    public static bool Native => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>
    /// The ARM64 backend's tests check the block and instruction counts (CpuState.NBlocks/NInstr) on the counting
    /// trampoline, which is what <see cref="Arm64Sandbox"/> builds by default. The host runtime of an ARM64 process
    /// builds the device's budget-only trampoline instead (A64Opt.NoBlockCounts) unless VITASHARP_NATIVE_COUNTERS=1, so
    /// on an ARM64 host (a phone, Apple silicon with Native AOT, qemu-aarch64) every count check failed: the test
    /// process asks for the counting one before the runtime is built. The budget-only trampoline keeps its own tests
    /// (the sandbox with noBlockCounts).
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void CountBlocksOnArm64Hosts()
    {
        if (Native && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.NativeCounters)))
        {
            Environment.SetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.NativeCounters, "1");
        }
    }

    /// <summary>Whether a runner's code keeps the block and instruction counts (the counting trampoline).</summary>
    public static bool CountsBlocks(IArm64Runner runner) => !(runner.Stubs.PinnedCounters && runner.Stubs.NoBlockCounts);
    /// <summary>A native host can still be unable to write generated code (macOS on Apple silicon without Native AOT).</summary>
    public static string? SkipReason => Native ? ExecutableMemory.UnsupportedReason : Arm64Sandbox.SkipReason;
    public static IArm64Runner Create() => Native ? new Arm64HostRunner() : new Arm64Sandbox();
}

/// <summary>ARM64 host: the process's own runtime (trampoline, callout bridge, dispatch table).</summary>
public sealed unsafe class Arm64HostRunner : IArm64Runner
{
    public Arm64Emitter.Runtime Stubs => NativeRuntime.Instance.Arm64Stubs;
    public void Run(CpuState* s, nint entry) => NativeRuntime.Instance.Run(s, entry);
    public void Publish(uint pc, bool thumb, nint entry)
    {
        NativeRuntime.DispatchEntry* e = NativeRuntime.Instance.Dispatch + ((pc >> 1) & ((1u << NativeRuntime.DispatchBits) - 1));
        Volatile.Write(ref e->Key, NativeRuntime.NoKey);
        Volatile.Write(ref e->Code, entry);
        Volatile.Write(ref *(ulong*)e, NativeRuntime.Instance.PackedEntry(pc | (thumb ? 1u : 0u), entry));
    }
    public void Call(ulong fn, ulong arg0, ulong arg1) => ((delegate* unmanaged<ulong, ulong, void>)fn)(arg0, arg1);
    public long Callouts => -1;
    public void Dispose()
    {
    }
}
