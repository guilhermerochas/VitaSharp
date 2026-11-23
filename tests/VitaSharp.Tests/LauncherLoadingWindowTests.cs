using Avalonia.Headless;
using Avalonia.Threading;
using System.Diagnostics;
using VitaSharp.Core.Boot;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Launcher;
using VitaSharp.Desktop.Launcher.Ui;

namespace VitaSharp.Tests;

/// <summary>
/// The launcher's loading window (<see cref="GameLoadingWindow"/>, Avalonia's headless platform, a scratch data folder
/// with one game): the click opens it before the game process is even started, the game process gets the options
/// that keep its own window hidden behind it, the window follows the steps the game's log reports, "Cancelar" reaches
/// the game process's input, a second click brings the same window back with one process only, its X stops the game,
/// the process's end closes it, and a start that fails leaves the reason and the log's place on it. The game process is
/// a stand-in: <c>sort</c> (on Windows, Linux and macOS), which reads its input until it closes and then writes it
/// back, so what the launcher sends to the game comes back on the game's output.
/// </summary>
[Collection("launcher")]
public sealed class LauncherLoadingWindowTests : IDisposable
{
    private const string TitleId = "PCSB00777";
    private const string GameTitle = "A Loading Game";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-loadwin-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedData = Environment.GetEnvironmentVariable(EnvVars.Data);
    private readonly LanguageScope _language = new();
    private readonly List<Process> _standIns = [];
    private readonly List<ProcessStartInfo> _asked = [];
    private MainWindow? _main;

    public LauncherLoadingWindowTests()
    {
        string data = Path.Combine(_root, "data");
        string game = Path.Combine(data, "fs", "ux0", "app", TitleId);
        Directory.CreateDirectory(Path.Combine(game, "sce_sys"));
        File.WriteAllBytes(Path.Combine(game, "eboot.bin"), new byte[64]);
        DesktopLogicTests.WriteSfo(Path.Combine(game, "sce_sys", "param.sfo"), ("APP_VER", "01.00"), ("CATEGORY", "gd"),
            ("TITLE", GameTitle), ("TITLE_ID", TitleId));
        File.WriteAllText(Path.Combine(data, "launcher.json"), "{ \"CheckUpdates\": false }");
        Environment.SetEnvironmentVariable(EnvVars.Data, data);
        LauncherApp.SetupHeadless();
    }

    public void Dispose()
    {
        _main?.Close();
        foreach (Process standIn in _standIns)
        {
            try
            {
                if (!standIn.HasExited)
                {
                    standIn.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            standIn.Dispose();
        }

        Environment.SetEnvironmentVariable(EnvVars.Data, _savedData);
        _language.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Pump()
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Runs the launcher's jobs until <paramref name="done"/> (the stand-in's output and exit come as posted
    /// jobs); the limit only ends a test that would otherwise hang, it decides nothing.</summary>
    private static void PumpUntil(Func<bool> done, string what)
    {
        var limit = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(limit.Elapsed < TimeSpan.FromMinutes(2), "never happened: " + what);
            Pump();
            Thread.Sleep(5);
        }

        Pump();
    }

    /// <summary>The launcher with the stand-in as its game process. The loading window must already be open and on
    /// screen when the process is asked for.</summary>
    private MainWindow NewLauncher(string language)
    {
        var settings = LauncherSettings.Load();
        settings.Language = language;
        settings.RecreatedHome = true;
        MainWindow main = _main = new MainWindow(settings) { Width = 1280, Height = 760 };
        main.StartProcess = psi =>
        {
            GameLoadingWindow? loading = main.LoadingWindowForTests;
            Assert.NotNull(loading);
            Assert.True(loading.IsVisible, "the loading window opens before the game process starts");
            _asked.Add(psi);
            var standIn = new ProcessStartInfo("sort")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            Process process = Process.Start(standIn)!;
            _standIns.Add(process);
            return process;
        };
        main.Show();
        Pump();
        return main;
    }

    private static GameLoadingWindow Loading(MainWindow main) => main.LoadingWindowForTests ?? throw new Xunit.Sdk.XunitException("no loading window");

    [Fact]
    public void TheClickOpensTheLoadingWindowFirstAndItFollowsTheStepsAndCancel()
    {
        MainWindow main = NewLauncher(Loc.English);
        Assert.True(main.LaunchForTests(TitleId));
        Pump();
        GameLoadingWindow loading = Loading(main);
        Assert.Single(_asked);
        // the game's own window waits hidden at the loading window's place
        string[] arguments = [.. _asked[0].ArgumentList];
        Assert.Contains(BootProgress.LauncherScreenOption, arguments);
        Assert.All(arguments.Where(a => a.StartsWith("--window-pos=", StringComparison.Ordinal)),
            a => Assert.Matches(@"^--window-pos=-?\d+,-?\d+$", a));
        Assert.Equal(Strings.LoadingWindowTitle(GameTitle), loading.Title);
        Assert.Equal(Loc.Get("BootStepStarting"), loading.StepTextShown);
        Assert.Null(loading.BarShown);
        Assert.False(loading.CancelShown);

        main.OnGameOutputForTests("[12:00:00.000] [Info ] [2] [boot-step] firmware");
        Pump();
        Assert.Equal(Loc.Get("BootStepFirmware"), loading.StepTextShown);
        main.OnGameOutputForTests("[12:00:01.000] [Info ] [2] [boot-step] gpu");
        main.OnGameOutputForTests("[12:00:02.000] [Info ] [2] [boot-step] game");
        main.OnGameOutputForTests("[12:00:02.001] [Info ] [2] [boot-step] shader-cache");
        main.OnGameOutputForTests("[12:00:03.000] [Info ] [2] " + ShaderCacheLoad.Format(new ShaderCacheLoadProgress(50, 100, false)));
        Pump();
        Assert.Equal(0.5, loading.BarShown);
        Assert.True(loading.CancelShown);
        Assert.StartsWith(Loc.Get("BootStepShaderCache") + ":", loading.StepTextShown, StringComparison.Ordinal);

        // "Cancelar": the button goes at once and the game process gets the shader cache's cancel command
        loading.ClickCancelForTests();
        Pump();
        Assert.False(loading.CancelShown);

        // a second click on the game: the same loading window comes to the front, no second process
        Assert.True(main.LaunchForTests(TitleId));
        Pump();
        Assert.Same(loading, main.LoadingWindowForTests);
        Assert.Equal(1, loading.BroughtToFrontCount);
        Assert.Single(_asked);
        Assert.Equal("focus: loading window", main.LastFocusResultForTests);

        // the stand-in ends when its input closes and writes back what it got: the cancel command reached it, and
        // the process's end closes the loading window
        Process standIn = main.RunningProcessForTests!;
        standIn.StandardInput.Close();
        PumpUntil(() => !main.IsRunning, "the stand-in's exit");
        Assert.Contains(main.LogLines, line => line.Trim() == ShaderCacheLoad.CancelCommand);
        Assert.Null(main.LoadingWindowForTests);
        Assert.False(loading.IsVisible);
    }

    [Fact]
    public void TheReadyStepClosesTheLoadingWindowAndTheGameGoesOn()
    {
        MainWindow main = NewLauncher(Loc.English);
        Assert.True(main.LaunchForTests(TitleId));
        Pump();
        GameLoadingWindow loading = Loading(main);
        // the ready line comes back on the stand-in's output, through the launcher's real output path
        Process standIn = main.RunningProcessForTests!;
        standIn.StandardInput.WriteLine("[12:00:05.000] [Info ] [2] [boot-step] ready");
        standIn.StandardInput.Flush();
        standIn.StandardInput.Close();
        PumpUntil(() => main.LoadingWindowForTests == null, "the ready step");
        Assert.False(loading.IsVisible);
        Assert.True(loading.Screen.IsDone);
        Assert.False(loading.Screen.IsFailed);
        PumpUntil(() => !main.IsRunning, "the stand-in's exit");
    }

    [Fact]
    public void ClosingTheLoadingWindowStopsTheGameBeingStarted()
    {
        MainWindow main = NewLauncher(Loc.English);
        Assert.True(main.LaunchForTests(TitleId));
        Pump();
        GameLoadingWindow loading = Loading(main);
        // a handle of the test's own: the launcher disposes its process object once it ended
        using var standIn = Process.GetProcessById(main.RunningProcessForTests!.Id);
        int? exitCode = null;
        main.EmulatorExited += code => exitCode = code;
        loading.Close(); // its X
        PumpUntil(() => !main.IsRunning, "the game's stop");
        Assert.True(standIn.WaitForExit(60_000), "the stand-in was not stopped");
        Assert.NotNull(exitCode);
        Assert.Null(main.LoadingWindowForTests);
        Assert.Single(_asked);
    }

    /// <summary>The title cannot be loaded (<see cref="VitaSharp.Core.TitleLaunchFailure.ExitCode"/>, its reason as the
    /// last error line): the loading window stays with the reason and the session's log, in place of the steps, and
    /// "Fechar" closes it; the status bar says the same. Other failures (a missing license, an exit code) too.</summary>
    [Theory]
    [InlineData(VitaSharp.Core.TitleLaunchFailure.ExitCode)]
    [InlineData(VitaSharp.Core.Package.TitleLicense.ExitCode)]
    [InlineData(-1073741819)]
    public void AFailedStartShowsTheReasonAndTheLogOnTheLoadingWindow(int exitCode)
    {
        MainWindow main = NewLauncher(Loc.Portuguese);
        Assert.True(main.LaunchForTests(TitleId));
        Pump();
        GameLoadingWindow loading = Loading(main);
        string log = Path.Combine(_root, "data", "logs", "last.log");
        main.OnGameOutputForTests("[12:00:00.000] [Info ] [2] " + BootProgress.LogFileTag + log);
        main.OnGameOutputForTests("[12:00:00.001] [Info ] [2] [boot-step] firmware");
        main.OnGameOutputForTests("[12:00:00.002] [Info ] [2] [boot-step] game");
        string reason = VitaSharp.Core.TitleLaunchFailure.Message(TitleId, new InvalidDataException("Not a SELF/ELF file"), log);
        main.OnGameErrorForTests(reason);
        // the stand-in cannot end with the emulator's codes: the launcher gets the end as the process's exit gives it
        main.OnGameExited(exitCode);
        Pump();

        Assert.Same(loading, main.LoadingWindowForTests);
        Assert.True(loading.IsVisible);
        string expected = exitCode switch
        {
            VitaSharp.Core.TitleLaunchFailure.ExitCode => reason,
            VitaSharp.Core.Package.TitleLicense.ExitCode => Strings.LicenseMissing(TitleId),
            _ => Strings.StoppedWithCode(GameTitle, exitCode),
        };
        Assert.Equal(expected, loading.StepTextShown);
        Assert.Equal("Log completo: " + log, loading.LogTextShown);
        Assert.True(loading.CloseShown);
        Assert.False(loading.CancelShown);
        Assert.False(loading.BarVisible);
        Assert.False(main.IsRunning);

        loading.ClickCloseForTests();
        Pump();
        Assert.Null(main.LoadingWindowForTests);
        Assert.False(loading.IsVisible);
    }
}
