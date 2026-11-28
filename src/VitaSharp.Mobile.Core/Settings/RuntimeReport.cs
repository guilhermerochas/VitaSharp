using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
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
    public static string? BuildRuntime => AppMetadata("VitaSharpAndroidRuntime");

    /// <summary>
    /// How Native AOT compiled the app ("optimization Speed, instruction set baseline"; the csproj's
    /// OptimizationPreference and IlcInstructionSet), from the app assembly's metadata; null outside the Android app.
    /// </summary>
    public static string? BuildCodegen => AppMetadata("VitaSharpIlcCodegen");

    /// <summary>
    /// The arm64 instruction sets the compiled code may use (a set compiled in is reported as present; a set ILC
    /// checks at run time reports the CPU), so a capture says which code generation ran on which CPU.
    /// </summary>
    public static string CpuFeatures()
    {
        if (!ArmBase.Arm64.IsSupported)
        {
            return "not arm64";
        }

        List<string> features = ["neon"];
        AddIf(features, Crc32.IsSupported, "crc");
        AddIf(features, System.Runtime.Intrinsics.Arm.Aes.IsSupported, "aes");
        AddIf(features, Sha256.IsSupported, "sha2");
        AddIf(features, Rdm.IsSupported, "rdma");
        AddIf(features, Dp.IsSupported, "dotprod");
        return string.Join(",", features);
    }

    /// <summary>The startup log line of the code generation (<see cref="BuildCodegen"/>, <see cref="CpuFeatures"/>).</summary>
    public static string CodegenLine(string? buildCodegen, string cpuFeatures) =>
        $"Native code generation: {buildCodegen ?? "?"}; CPU features usable by the compiled code: {cpuFeatures}";

    private static void AddIf(List<string> features, bool supported, string name)
    {
        if (supported)
        {
            features.Add(name);
        }
    }

    private static string? AppMetadata(string key) => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "VitaSharp.Android")?
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    public static IEnumerable<string> Lines()
    {
        yield return $"Runtime: {Runtime} ({Kind}), {RuntimeInformation.ProcessArchitecture}, " +
                     $"{RuntimeInformation.RuntimeIdentifier}, build {BuildRuntime ?? "?"}";
        yield return $"Dynamic code: supported {DynamicCodeSupported}, compiled {DynamicCodeCompiled}";
        yield return CodegenLine(BuildCodegen, CpuFeatures());
        foreach (IBlockCompiler blockCompiler in Jit.All)
        {
            yield return $"CPU backend {blockCompiler.Name} ({blockCompiler.GetType().Name}): {(blockCompiler.IsSupported ? "available" : blockCompiler.UnsupportedReason ?? "unavailable")}";
        }

        yield return $"CPU backend selected: {Jit.Mode} (chain: {string.Join(" > ", Jit.Chain.Select(b => b.Name).Append("interp"))})";
    }
}
