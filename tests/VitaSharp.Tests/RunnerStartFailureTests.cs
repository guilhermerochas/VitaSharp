using VitaSharp.Core;
using VitaSharp.Core.Boot;
using VitaSharp.Core.Localization;
using VitaSharp.Desktop;

namespace VitaSharp.Tests;

/// <summary>
/// A game's start that fails anywhere before it is running (not only in the title's load: the window, the Vulkan
/// device, the emulator's set-up) ends as a failed start, as on the Vita, where the system shows an error when an
/// application does not start: the reason in plain words with the log's place on the error output (the launcher's
/// loading window shows it for <see cref="TitleLaunchFailure.ExitCode"/>), never a crash with only a stack in the log.
/// Once the game runs, an exception is a crash as before (<see cref="Runner.ReportStartFailure"/>).
/// </summary>
public sealed class RunnerStartFailureTests
{
    private const string AppPath = "ux0:app/PCSB00123";

    public static TheoryData<BootStep> StepsBeforeReady()
    {
        var steps = new TheoryData<BootStep>();
        foreach (BootStep step in Enum.GetValues<BootStep>())
        {
            if (step != BootStep.Ready)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    [Theory]
    [MemberData(nameof(StepsBeforeReady))]
    public void A_failure_before_the_game_runs_is_a_failed_start_with_its_reason(BootStep reached)
    {
        using var language = new LanguageScope(Loc.English);
        var failure = new InvalidOperationException("no Vulkan device");
        using var error = new StringWriter();
        Assert.True(Runner.ReportStartFailure(AppPath, failure, reached, error));
        string line = error.ToString().TrimEnd();
        Assert.Equal(TitleLaunchFailure.Message("PCSB00123", failure, VitaSharp.Core.Util.Log.FilePath), line);
        Assert.Contains("no Vulkan device", line, StringComparison.Ordinal);
        Assert.Contains("PCSB00123", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_once_the_game_runs_stays_a_crash()
    {
        using var error = new StringWriter();
        Assert.False(Runner.ReportStartFailure(AppPath, new InvalidOperationException("late"), BootStep.Ready, error));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Out_of_memory_is_never_reported_as_a_failed_start()
    {
        using var error = new StringWriter();
        Assert.False(Runner.ReportStartFailure(AppPath, new OutOfMemoryException(), BootStep.Gpu, error));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void The_systems_refusals_keep_their_own_text()
    {
        using var language = new LanguageScope(Loc.English);
        var noKey = new TitleKeyException("PCSB00123", unchecked((int)0x80020004));
        using var error = new StringWriter();
        Assert.True(Runner.ReportStartFailure(AppPath, noKey, BootStep.Game, error));
        Assert.Equal(noKey.UserMessage, error.ToString().TrimEnd());
    }
}
