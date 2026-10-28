using VitaSharp.Core.Util;
using System.Runtime.InteropServices;
using UnicornEngine;

namespace VitaSharp.Tests;

/// <summary>
/// The Unicorn NuGet package ships natives for win-x64/x86, linux-x64/arm64 and osx-x64 only (not osx-arm64, so not
/// GitHub's macos-latest runners). Tests that compare against Unicorn are skipped where the native library cannot be
/// loaded instead of failing; VITASHARP_SKIP_UNICORN=1 skips them everywhere (they take minutes).
/// </summary>
public static class UnicornAvailability
{
    static readonly Lazy<string?> Reason = new(() =>
    {
        if (Environment.GetEnvironmentVariable(EnvVars.SkipUnicorn) == "1") return "VITASHARP_SKIP_UNICORN=1";
        // same resolution as Unicorn's own [DllImport("unicorn")] (deps.json runtimes/<rid>/native first)
        if (NativeLibrary.TryLoad("unicorn", typeof(Unicorn).Assembly, null, out var h)) { NativeLibrary.Free(h); return null; }
        return $"Unicorn native library not available for {RuntimeInformation.RuntimeIdentifier}";
    });

    public static string? SkipReason => Reason.Value;
}

/// <summary>[Fact] that is skipped when Unicorn's native library is unavailable.</summary>
public sealed class UnicornFactAttribute : FactAttribute
{
    public UnicornFactAttribute() { if (UnicornAvailability.SkipReason is { } r) Skip = r; }
}

/// <summary>[Theory] that is skipped when Unicorn's native library is unavailable.</summary>
public sealed class UnicornTheoryAttribute : TheoryAttribute
{
    public UnicornTheoryAttribute() { if (UnicornAvailability.SkipReason is { } r) Skip = r; }
}
