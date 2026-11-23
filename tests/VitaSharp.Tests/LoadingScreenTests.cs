using VitaSharp.Core.Boot;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Localization;

namespace VitaSharp.Tests;

/// <summary>
/// The loading screen of a game being started (<see cref="LoadingScreen"/>, shown by the desktop launcher's loading
/// window and the Android game screen) and the boot step protocol it reads (<see cref="BootProgress"/>): the steps of
/// a real start as the game process logs them (the 21/11 Spider-Man session: firmware, controllers, audio, Vulkan
/// device, ~12 s of shader cache, the ARM code precompilation) go by in order, each with its own text, the bar follows
/// the two waits' counts, and the screen always ends: at the ready step, when the process ends first, and when a
/// wait was cancelled. The shader cache and the ARM code precompilation keep their own names in both languages.
/// </summary>
[Collection("language")]
public sealed class LoadingScreenTests
{
    /// <summary>The lines of a start as the game process writes them (log time, level and thread first).</summary>
    private static readonly string[] s_startLines =
    [
        "[23:23:32.890] [Info ] [2] VitaSharp session PCSE00333 20261121_232332",
        "[23:23:32.891] [Info ] [2] [boot-step] firmware",
        "[23:23:32.974] [Info ] [2] Firmware 3.74 installed (os0, vs0, pd0)",
        "[23:23:33.050] [Info ] [2] [boot-step] input",
        "[23:23:38.461] [Info ] [2] Input: profiles from <data>/input.json",
        "[23:23:38.470] [Info ] [2] [boot-step] audio",
        "[23:23:40.700] [Info ] [2] [boot-step] gpu",
        "[23:23:43.720] [Info ] [2] Vulkan device: a GPU (API 1.3)",
        "[23:23:45.492] [Info ] [2] Loading PCSE00333 \"A Game\" from <data>/ux0/app/PCSE00333",
        "[23:23:45.492] [Info ] [2] [boot-step] game",
        "[23:23:45.493] [Info ] [2] [boot-step] shader-cache",
        "[23:23:52.593] [Info ] [2] [shadercache-load] progress 0 1942",
        "[23:23:58.447] [Info ] [2] [shadercache-load] progress 971 1942",
        "[23:24:04.768] [Info ] [2] [shadercache-load] finished 1942 1942",
        "[23:24:05.545] [Info ] [GPU worker] Boot picture: 960x544 shown until the game's first frame that is not black",
        "[23:24:05.600] [Info ] [2] [boot-step] code-precompile",
        "[23:24:05.773] [Info ] [2] [code-precompile] progress 2533 4512",
        "[23:24:07.454] [Info ] [2] [code-precompile] progress 74497 75159",
        "[23:24:07.909] [Info ] [2] [code-precompile] finished 94859 94859",
        "[23:24:07.920] [Info ] [2] [boot-step] ready",
    ];

    [Fact]
    public void EveryStepNameReadsBackAndOtherLinesAreNotSteps()
    {
        foreach (BootStep step in Enum.GetValues<BootStep>())
        {
            Assert.True(BootProgress.TryParse("[12:00:00.000] [Info ] [2] " + BootProgress.Format(step), out BootStep read));
            Assert.Equal(step, read);
        }

        Assert.False(BootProgress.TryParse("[12:00:00.000] [Info ] [2] Vulkan device: a GPU", out _));
        Assert.False(BootProgress.TryParse("[boot-step] warp-drive", out _));
    }

    [Fact]
    public void AStartGoesThroughItsStepsInOrderAndEndsWhenReady()
    {
        using var language = new LanguageScope(Loc.English);
        var screen = new LoadingScreen("A Game");
        var steps = new List<BootStep>();
        var texts = new List<string>();
        screen.Changed += () =>
        {
            steps.Add(screen.Step);
            texts.Add(screen.StepText);
        };

        Assert.Equal(BootStep.Starting, screen.Step);
        Assert.Equal(Loc.Get("BootStepStarting"), screen.StepText);
        Assert.Null(screen.Fraction);
        foreach (string line in s_startLines)
        {
            screen.OnLine(line);
        }

        Assert.True(screen.IsDone);
        Assert.Equal(1.0, screen.Fraction);
        // every step once, in the order they run, the last change is the end
        Assert.Equal(
            [BootStep.Firmware, BootStep.Input, BootStep.Audio, BootStep.Gpu, BootStep.Game, BootStep.ShaderCache],
            steps.Distinct().Take(6));
        Assert.Equal(BootStep.Ready, steps[^1]);
        for (int i = 1; i < steps.Count; i++)
        {
            Assert.True(steps[i] >= steps[i - 1], $"{steps[i - 1]} then {steps[i]}");
        }

        Assert.Contains("Loading the shader cache: 971 of 1942", texts);
        Assert.Contains("Precompiling ARM code: 2533 of 4512", texts);
        Assert.Contains(Loc.Get("BootStepGpu"), texts);
    }

    [Fact]
    public void TheBarFollowsTheWaitsAndCancelIsOfferedOnlyDuringThem()
    {
        var screen = new LoadingScreen("A Game");
        screen.OnStep(BootStep.Gpu);
        Assert.Null(screen.Fraction);
        Assert.False(screen.CanCancel);
        Assert.Null(screen.Cancel());

        screen.OnStep(BootStep.ShaderCache);
        Assert.Null(screen.Fraction); // the caches are read: no count yet
        Assert.True(screen.CanCancel);
        screen.OnShaderCache(new ShaderCacheLoadProgress(25, 100, false));
        Assert.Equal(0.25, screen.Fraction);
        screen.OnShaderCache(new ShaderCacheLoadProgress(100, 100, true));
        Assert.Equal(1.0, screen.Fraction);
        Assert.False(screen.CanCancel);

        // the ARM code step starts from its own progress line too (no step line before it)
        screen.OnCodePrecompile(new CodePrecompileProgress(10, 40, false));
        Assert.Equal(BootStep.CodePrecompile, screen.Step);
        Assert.Equal(0.25, screen.Fraction);
        Assert.True(screen.CanCancel);
    }

    [Fact]
    public void ACancelledWaitStillEndsAndTheScreenGoesOnToTheNextStep()
    {
        var screen = new LoadingScreen("A Game");
        screen.OnShaderCache(new ShaderCacheLoadProgress(5, 10, false));
        Assert.Equal(BootStep.ShaderCache, screen.Cancel());
        Assert.False(screen.CanCancel); // the button goes at once
        Assert.Null(screen.Cancel());
        // the cancelled wait's finished line carries 0/0: the step ends where it was
        screen.OnLine("[boot] " + ShaderCacheLoad.Format(new ShaderCacheLoadProgress(0, 0, true)));
        Assert.Equal(0.5, screen.Fraction);
        Assert.False(screen.IsDone);

        screen.OnCodePrecompile(new CodePrecompileProgress(1, 4, false));
        Assert.Equal(BootStep.CodePrecompile, screen.Cancel());
        screen.OnCodePrecompile(new CodePrecompileProgress(1, 4, true));
        screen.OnStep(BootStep.Ready);
        Assert.True(screen.IsDone);
    }

    [Fact]
    public void StepsNeverGoBackAndAnEndedScreenStaysEnded()
    {
        var screen = new LoadingScreen("A Game");
        int changes = 0;
        screen.Changed += () => changes++;
        screen.OnStep(BootStep.Game);
        screen.OnStep(BootStep.Firmware); // late or repeated: nothing
        screen.OnStep(BootStep.Game);
        Assert.Equal(BootStep.Game, screen.Step);
        Assert.Equal(1, changes);

        screen.OnStep(BootStep.CodePrecompile);
        screen.OnShaderCache(new ShaderCacheLoadProgress(3, 9, false)); // an earlier wait's line: nothing
        Assert.Equal(BootStep.CodePrecompile, screen.Step);
        Assert.Equal(2, changes);

        screen.OnStep(BootStep.Ready);
        Assert.True(screen.IsDone);
        // a LoadExec loads the title again in the same process: the screen does not come back
        screen.OnStep(BootStep.Game);
        screen.OnShaderCache(new ShaderCacheLoadProgress(1, 2, false));
        screen.End();
        Assert.True(screen.IsDone);
        Assert.Equal(BootStep.Ready, screen.Step);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void AProcessThatEndsDuringTheStartEndsTheScreen()
    {
        var screen = new LoadingScreen("A Game");
        bool lastWasDone = false;
        screen.Changed += () => lastWasDone = screen.IsDone;
        screen.OnShaderCache(new ShaderCacheLoadProgress(1, 10, false));
        screen.End(); // the launcher saw the process exit (a missing license, a crash, the user's Stop)
        Assert.True(screen.IsDone);
        Assert.True(lastWasDone);
        Assert.False(screen.CanCancel);
    }

    /// <summary>A start that fails (a title that cannot be loaded: <see cref="VitaSharp.Core.TitleLaunchFailure"/>) keeps
    /// the screen with the reason and the log's place instead of closing it without a word: the steps stop, the bar and
    /// "Cancelar" go, a second failure or a late step changes nothing, and only the user's close ends it.</summary>
    [Fact]
    public void AFailedStartStaysOnTheScreenWithTheReasonAndTheLog()
    {
        using var language = new LanguageScope(Loc.Portuguese);
        var screen = new LoadingScreen("A Game");
        int changes = 0;
        screen.Changed += () => changes++;
        screen.OnShaderCache(new ShaderCacheLoadProgress(1, 10, false));
        Assert.True(screen.CanCancel);
        Assert.Null(screen.FailureLogText);

        string reason = VitaSharp.Core.TitleLaunchFailure.Message("ABCD00001", new InvalidDataException("Not a SELF/ELF file"), "logs/a.log");
        screen.Fail(reason, "logs/a.log");
        Assert.True(screen.IsFailed);
        Assert.False(screen.IsDone);
        Assert.Equal(reason, screen.StepText);
        Assert.Equal("Log completo: logs/a.log", screen.FailureLogText);
        Assert.False(screen.CanCancel);
        Assert.Null(screen.Cancel());
        Assert.Equal("Fechar", LoadingScreen.CloseText);
        Assert.Equal(2, changes);

        screen.Fail("another", null);
        screen.OnStep(BootStep.CodePrecompile);
        screen.OnShaderCache(new ShaderCacheLoadProgress(5, 10, false));
        screen.OnLine("[boot-step] ready");
        Assert.Equal(reason, screen.StepText);
        Assert.False(screen.IsDone);
        Assert.Equal(2, changes);

        screen.End(); // the user closed it
        Assert.True(screen.IsDone);
        screen.Fail("late", null);
        Assert.Equal(3, changes);
    }

    [Theory]
    [InlineData(Loc.Portuguese, "Carregando o cache de shaders", "Pré-compilando código ARM")]
    [InlineData(Loc.English, "Loading the shader cache", "Precompiling ARM code")]
    public void TheShaderCacheAndTheArmCodeKeepTheirOwnNames(string language, string shaders, string code)
    {
        using var scope = new LanguageScope(language);
        Assert.Equal(shaders, LoadingScreen.StepTextOf(BootStep.ShaderCache));
        Assert.Equal(code, LoadingScreen.StepTextOf(BootStep.CodePrecompile));
        Assert.StartsWith(shaders + ":", LoadingScreen.StepTextOf(BootStep.ShaderCache, 1, 2), StringComparison.Ordinal);
        Assert.StartsWith(code + ":", LoadingScreen.StepTextOf(BootStep.CodePrecompile, 1, 2), StringComparison.Ordinal);
        // every step has its own text in both languages, none is a missing key
        var all = Enum.GetValues<BootStep>().Select(step => LoadingScreen.StepTextOf(step)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.DoesNotContain(all, text => text.StartsWith("BootStep", StringComparison.Ordinal));
    }
}
