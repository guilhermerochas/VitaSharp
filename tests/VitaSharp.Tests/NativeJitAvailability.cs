using System.Runtime.InteropServices;

namespace VitaSharp.Tests;

/// <summary>
/// The native backend emits x64 machine code only. On other hosts (GitHub's macos-latest runners are ARM64) its tests
/// are skipped: they would either fail on <c>Jit.Select("native")</c> or execute x64 bytes and kill the test host.
/// </summary>
public static class NativeJitAvailability
{
    public static string? SkipReason { get; } = RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? null
        : $"the native backend emits x64 code only (host is {RuntimeInformation.ProcessArchitecture})";

    /// <summary>The native backend's skip reason, or (when <paramref name="unicorn"/>) Unicorn's.</summary>
    internal static string? Reason(bool unicorn) => SkipReason ?? (unicorn ? UnicornAvailability.SkipReason : null);
}

/// <summary>[Fact] that is skipped when the native backend cannot run on this host.</summary>
public sealed class NativeFactAttribute : FactAttribute
{
    /// <param name="unicorn">The test also needs Unicorn's native library (e.g. to filter encodings).</param>
    public NativeFactAttribute(bool unicorn = false) { if (NativeJitAvailability.Reason(unicorn) is { } r) Skip = r; }
}

/// <summary>[Theory] that is skipped when the native backend cannot run on this host.</summary>
public sealed class NativeTheoryAttribute : TheoryAttribute
{
    /// <param name="unicorn">The test also needs Unicorn's native library (e.g. to filter encodings).</param>
    public NativeTheoryAttribute(bool unicorn = false) { if (NativeJitAvailability.Reason(unicorn) is { } r) Skip = r; }
}
