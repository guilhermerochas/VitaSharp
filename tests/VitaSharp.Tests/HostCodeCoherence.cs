using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// Code rewritten in the native code arena runs as rewritten: a block placed again in a range it gave back
/// (<see cref="ExecutableMemory.Free"/>, the JIT's evicted ahead-of-time code), a word patched in place
/// (<see cref="ExecutableMemory.PatchWord"/>, block linking). On a real ARM64 processor the arena's cache maintenance
/// after each write (DC CVAU, DSB ISH, IC IVAU, DSB ISH, ISB: the ARM ARM's sequence for modified instructions) makes
/// that so; x64 keeps instruction fetch coherent with stores.
/// qemu-user 7.2 does not: it detects code changes only through writes to the address the code runs at, and the arena
/// writes through its second (read/write) mapping of the same memory (W^X), while IC IVAU is a no-op there. It keeps
/// running its translation of the old code, whose literal loads then read the new bytes: a branch to an instruction
/// pair as an address (SIGSEGV at pc 0x806b01009f, x16 = 0x540000806b01009f, in Arm64SuperblocksMatchInterpreter).
/// qemu 10.0.2 runs the new code. The probe below runs the same three rewrites once per process.
/// </summary>
public static unsafe class HostCodeCoherence
{
    private static readonly Lazy<int[]> s_results = new(Run);

    /// <summary>The qemu-user version the run is emulated by (<see cref="EnvVars.QemuUser"/>), or null.</summary>
    public static string? QemuVersion => Environment.GetEnvironmentVariable(EnvVars.QemuUser) is { Length: > 0 } version ? version : null;

    /// <summary>What the three rewritten functions returned (1, 2 and 3 when the new code ran; empty without an arena).</summary>
    public static int[] Results => s_results.Value;

    /// <summary>Every rewrite ran as rewritten (or nothing could be probed on this host).</summary>
    public static bool RewrittenCodeRuns => Results.Length == 0 || (Results[0] == 1 && Results[1] == 2 && Results[2] == 3);

    /// <summary>
    /// Why generated code cannot be tested on this host: only under qemu-user (<see cref="EnvVars.QemuUser"/> set) when
    /// that qemu runs stale code after a rewrite. Without qemu a stale run is a real failure and nothing is skipped.
    /// </summary>
    public static string? QemuSkipReason => QemuVersion is { } version && !RewrittenCodeRuns
        ? $"qemu-user {version} runs stale code after a rewrite through the code arena's write mapping (returned {string.Join(",", Results)}, expected 1,2,3; qemu 7.2 does, 10.0.2 does not: build/qemu-arm64 --install sets qemu 10 up)"
        : null;

    /// <summary>A function returning <paramref name="value"/> (0..65535): movz w0 / ret, or mov eax / ret.</summary>
    internal static byte[] ReturnConstant(int value)
    {
        byte[] code = new byte[64]; // more than ExecutableMemory.MinFreeRange: the freed range is reused
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            BitConverter.GetBytes(0x52800000u | (uint)value << 5).CopyTo(code, 0); // movz w0, #value
            BitConverter.GetBytes(0xD65F03C0u).CopyTo(code, 4);                    // ret
        }
        else
        {
            code[0] = 0xB8;                                                        // mov eax, imm32
            BitConverter.GetBytes(value).CopyTo(code, 1);
            code[5] = 0xC3;                                                        // ret
        }
        return code;
    }

    /// <summary>The first word of <see cref="ReturnConstant"/> (patched in place).</summary>
    internal static uint FirstWord(int value) => BitConverter.ToUInt32(ReturnConstant(value), 0);

    private static int[] Run()
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;
        if (ExecutableMemory.UnsupportedReason != null || (arch != Architecture.Arm64 && arch != Architecture.X64))
        {
            return [];
        }

        using var arena = new ExecutableMemory();
        int[] results = new int[3];
        byte* code = arena.Place(ReturnConstant(1));
        results[0] = ((delegate* unmanaged<int>)code)();
        // linking: one word replaced where the code already ran
        arena.PatchWord(code, FirstWord(2));
        results[1] = ((delegate* unmanaged<int>)code)();
        // the range given back and placed again
        arena.Free(code, 64);
        byte* again = arena.Place(ReturnConstant(3), reuseFreed: true);
        results[2] = again == code ? ((delegate* unmanaged<int>)again)() : -1;
        return results;
    }
}

/// <summary>The code arena's rewrites run as rewritten (<see cref="HostCodeCoherence"/>), on every host with an arena.</summary>
public sealed class HostCodeCoherenceTests
{
    [HostCodeFact]
    public void RewrittenCodeRunsTheNewInstructions()
    {
        if (HostCodeCoherence.Results.Length == 0)
        {
            return; // neither ARM64 nor x64: no native backend
        }

        Assert.Equal([1, 2, 3], HostCodeCoherence.Results);
    }
}

/// <summary>
/// [Fact] that runs generated code on this host: skipped without a code arena
/// (<see cref="ExecutableMemory.UnsupportedReason"/>) and under a qemu-user that runs stale code
/// (<see cref="HostCodeCoherence.QemuSkipReason"/>).
/// </summary>
public sealed class HostCodeFactAttribute : FactAttribute
{
    public HostCodeFactAttribute()
    {
        if ((ExecutableMemory.UnsupportedReason ?? HostCodeCoherence.QemuSkipReason) is { } reason)
        {
            Skip = reason;
        }
    }
}
