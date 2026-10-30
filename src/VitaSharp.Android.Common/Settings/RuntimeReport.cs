using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;

namespace VitaSharp.Android.Settings;

/// <summary>
/// What the .NET runtime of this build can do for the CPU backends (logged at startup and shown under Ajustes >
/// Desempenho). On .NET for Android the app runs on Mono (JIT, with profiled AOT in Release): dynamic code is supported,
/// so the IL backend exists next to the native ARM64 JIT; under Native AOT only the native backend and the interpreter
/// would remain (docs/android/README.md, "Runtime and CPU backends").
/// </summary>
public static class RuntimeReport
{
    public static string Runtime => RuntimeInformation.FrameworkDescription;
    public static bool DynamicCodeSupported => RuntimeFeature.IsDynamicCodeSupported;
    public static bool DynamicCodeCompiled => RuntimeFeature.IsDynamicCodeCompiled;

    /// <summary>Mono (the default .NET for Android runtime) or CoreCLR.</summary>
    public static bool IsMono => Type.GetType("Mono.RuntimeStructs") != null;

    public static IEnumerable<string> Lines()
    {
        yield return $"Runtime: {Runtime} ({(IsMono ? "Mono" : "CoreCLR")}), {RuntimeInformation.ProcessArchitecture}, " +
                     $"{RuntimeInformation.RuntimeIdentifier}";
        yield return $"Dynamic code: supported {DynamicCodeSupported}, compiled {DynamicCodeCompiled}";
        foreach (var b in Jit.All)
            yield return $"CPU backend {b.Name} ({b.GetType().Name}): {(b.IsSupported ? "available" : b.UnsupportedReason ?? "unavailable")}";
        yield return $"CPU backend selected: {Jit.Mode} (chain: {string.Join(" > ", Jit.Chain.Select(b => b.Name).Append("interp"))})";
    }
}
