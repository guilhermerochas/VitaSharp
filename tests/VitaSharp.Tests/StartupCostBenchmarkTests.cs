using System.Diagnostics;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using Xunit;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// A measurement, not a check (Category=Slow, no assertion on time): what the core does once per start. Every static
/// constructor of the core assembly is run in turn and timed with the bytes it allocated (the twenty most costly are
/// printed), then one emulator is made. Run by hand, alone, so the constructors have not run yet in the process:
/// <c>dotnet test --filter FullyQualifiedName~StartupCostBenchmarkTests</c>.
/// </summary>
[Trait("Category", "Slow")]
public sealed class StartupCostBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public void TheCoresStaticConstructorsAndOneEmulator()
    {
        List<(string Name, double Ms, long Bytes)> costs = [];
        foreach (Type type in typeof(Emulator).Assembly.GetTypes())
        {
            if (type.TypeInitializer == null || type.ContainsGenericParameters)
            {
                continue;
            }

            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            try
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch (TypeInitializationException failure)
            {
                output.WriteLine($"{type.FullName}: {failure.InnerException?.Message}");
                continue;
            }

            costs.Add((type.FullName!, Stopwatch.GetElapsedTime(start).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytes));
        }

        output.WriteLine($"{costs.Count} static constructors, {costs.Sum(c => c.Ms):F1} ms, {costs.Sum(c => c.Bytes) / 1048576.0:F2} MB allocated");
        foreach ((string name, double ms, long allocated) in costs.OrderByDescending(c => c.Ms).Take(20))
        {
            output.WriteLine($"  {ms,7:F2} ms {allocated / 1024.0,9:F0} KB  {name}");
        }

        output.WriteLine($"NidDatabase: {NidDatabase.Count} NIDs");
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            using (Emulator emulator = new(new EmulatorOptions { FsRoot = root, EnableJit = false, CpuBackend = "interp" }))
            {
                output.WriteLine($"one emulator: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms, "
                    + $"{(GC.GetAllocatedBytesForCurrentThread() - bytes) / 1048576.0:F2} MB allocated, {emulator.Hle.Modules.Count()} HLE modules");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
