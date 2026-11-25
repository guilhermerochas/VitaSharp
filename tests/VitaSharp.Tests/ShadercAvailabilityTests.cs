namespace VitaSharp.Tests;

/// <summary>
/// The shaderc probe of the tests (<see cref="ShadercAvailability"/>) keeps the library loaded, as the renderer does:
/// unloading it left its exit handler pointing into the unmapped library, and on Linux every test host that probed it
/// died with SIGSEGV at exit after a passing run (WSL: a crash dump each time). Checked in a process of its own, whose
/// exit code is the contract.
/// </summary>
public sealed class ShadercAvailabilityTests
{
    [ShadercFact]
    public void AProcessThatProbedShadercExitsCleanly()
    {
        TestProgram.Result result = TestProgram.Run(TestProgram.ShadercProbeMode);
        Assert.True(result.ExitCode == 0, $"exit code {result.ExitCode}: {result.Output}");
        Assert.Contains("shaderc loaded", result.Output);
    }

    [ShadercFact]
    public void TheProbeLoadsOnceAndAnswersTheSame()
    {
        Assert.Null(ShadercAvailability.Probe());
        Assert.Null(ShadercAvailability.Probe());
        Assert.Null(ShadercAvailability.SkipReason);
    }
}
