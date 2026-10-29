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
    public static string? SkipReason => Native ? null : Arm64Sandbox.SkipReason;
    public static IArm64Runner Create() => Native ? new Arm64HostRunner() : new Arm64Sandbox();
}

/// <summary>ARM64 host: the process's own runtime (trampoline, callout bridge, dispatch table).</summary>
public sealed unsafe class Arm64HostRunner : IArm64Runner
{
    public Arm64Emitter.Runtime Stubs => NativeRuntime.Instance.Arm64Stubs;
    public void Run(CpuState* s, nint entry) => NativeRuntime.Instance.Run(s, entry);
    public void Publish(uint pc, bool thumb, nint entry)
    {
        var e = NativeRuntime.Instance.Dispatch + ((pc >> 1) & ((1u << NativeRuntime.DispatchBits) - 1));
        Volatile.Write(ref e->Key, NativeRuntime.NoKey);
        Volatile.Write(ref e->Code, entry);
        Volatile.Write(ref e->Key, pc | (thumb ? 1u : 0u));
    }
    public void Call(ulong fn, ulong arg0, ulong arg1) => ((delegate* unmanaged<ulong, ulong, void>)fn)(arg0, arg1);
    public long Callouts => -1;
    public void Dispose() { }
}
