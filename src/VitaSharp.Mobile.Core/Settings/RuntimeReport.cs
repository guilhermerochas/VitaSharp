using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;

namespace VitaSharp.Mobile.Core.Settings;

/// <summary>
/// What the .NET runtime of this build can do for the CPU backends (logged at startup and shown under Ajustes >
/// Desempenho). On .NET for Android the app runs on Mono (Release: every method AOT-compiled, the JIT kept for the rest)
/// or, as experiments, CoreCLR or Native AOT: dynamic code is supported on the first two, so the IL backend exists next to
/// the native ARM64 JIT; under Native AOT only the native backend and the interpreter remain (docs/android/README.md,
/// "Runtime and CPU backends").
/// </summary>
public static class RuntimeReport
{
    public static string Runtime => RuntimeInformation.FrameworkDescription;
    public static bool DynamicCodeSupported => RuntimeFeature.IsDynamicCodeSupported;
    public static bool DynamicCodeCompiled => RuntimeFeature.IsDynamicCodeCompiled;

    /// <summary>Mono (the default .NET for Android runtime) or CoreCLR.</summary>
    public static bool IsMono => Type.GetType("Mono.RuntimeStructs") != null;

    /// <summary>Native AOT: no JIT, so no dynamic code (Mono and CoreCLR always have it here).</summary>
    public static bool IsNativeAot => !IsMono && !RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>Mono, CoreCLR or NativeAOT.</summary>
    public static string Kind => IsMono ? "Mono" : IsNativeAot ? "NativeAOT" : "CoreCLR";

    /// <summary>
    /// The <c>VitaSharpAndroidRuntime</c> the APK was built with (mono-jit, mono-aot, mono-aot-llvm, coreclr, nativeaot), from the
    /// app assembly's <see cref="AssemblyMetadataAttribute"/>; null outside the Android app.
    /// </summary>
    public static string? BuildRuntime => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "VitaSharp.Android")?
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "VitaSharpAndroidRuntime")?.Value;

    public static IEnumerable<string> Lines()
    {
        yield return $"Runtime: {Runtime} ({Kind}), {RuntimeInformation.ProcessArchitecture}, " +
                     $"{RuntimeInformation.RuntimeIdentifier}, build {BuildRuntime ?? "?"}";
        yield return $"Dynamic code: supported {DynamicCodeSupported}, compiled {DynamicCodeCompiled}";
        foreach (IBlockCompiler b in Jit.All)
        {
            yield return $"CPU backend {b.Name} ({b.GetType().Name}): {(b.IsSupported ? "available" : b.UnsupportedReason ?? "unavailable")}";
        }

        yield return $"CPU backend selected: {Jit.Mode} (chain: {string.Join(" > ", Jit.Chain.Select(b => b.Name).Append("interp"))})";
    }
}
