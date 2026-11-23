using VitaSharp.Core.Cpu;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Localization;

namespace VitaSharp.Core.Boot;

/// <summary>
/// The loading screen of a game being started, platform-neutral (the desktop launcher's loading window and the Android
/// game screen's overlay both show this model; unit-tested on the desktop). It opens with the click, before any of the
/// heavy start-up runs, and shows the game's name, the current <see cref="BootStep"/> with its text, a bar and, during
/// the two waits that have one, "Cancelar" (the game starts at once; the rest goes on in the background). On the Vita
/// the system shows the application's start-up picture (sce_sys/pic0.png) while its process loads; this screen is
/// that moment, with the emulator's own steps on it.
/// <para>The steps only move forward (a step reported again, or an earlier one, changes nothing: a LoadExec's second
/// load does not reopen the screen) and the screen always ends: at <see cref="BootStep.Ready"/>, or <see cref="End"/>
/// when the game process ended first.</para>
/// </summary>
public sealed class LoadingScreen
{
    public LoadingScreen(string title, string? iconPath = null, string? backgroundPath = null)
    {
        Title = title;
        IconPath = iconPath;
        BackgroundPath = backgroundPath;
    }

    /// <summary>The game's name.</summary>
    public string Title { get; }

    /// <summary>The game's icon (sce_sys/icon0.png), null when it has none.</summary>
    public string? IconPath { get; }

    /// <summary>The game's start-up picture (sce_sys/pic0.png), null when it has none.</summary>
    public string? BackgroundPath { get; }

    public BootStep Step { get; private set; } = BootStep.Starting;

    /// <summary>Done and total of the step's own progress (shader cache, code precompilation); 0 when it has none yet.</summary>
    public int Done { get; private set; }

    public int Total { get; private set; }

    /// <summary>The loading screen is over: the game shows its picture, or its process ended.</summary>
    public bool IsDone { get; private set; }

    /// <summary>Why the start failed, in plain words (<see cref="Fail"/>); null while it has not.</summary>
    public string? Failure { get; private set; }

    /// <summary>The game session's log file as the game process reported it (<see cref="BootProgress.LogFileTag"/>);
    /// null until it did.</summary>
    public string? LogPath { get; private set; }

    /// <summary>The game session's log, where the failure's details are; null when it is not known.</summary>
    public string? FailureLogPath { get; private set; }

    /// <summary>The start failed: the screen shows <see cref="Failure"/> and stays until the user closes it.</summary>
    public bool IsFailed => Failure != null;

    /// <summary>Raised after every change (on the thread that made it).</summary>
    public event Action? Changed;

    private bool _waitEnded;

    /// <summary>The bar's fraction (0..1), null while the step has no count (the bar runs without an end).</summary>
    public double? Fraction
    {
        get
        {
            if (IsDone || IsFailed)
            {
                return 1;
            }

            if (Step is not (BootStep.ShaderCache or BootStep.CodePrecompile) || Total <= 0)
            {
                return _waitEnded ? 1 : null;
            }

            return Math.Clamp((double)Done / Total, 0, 1);
        }
    }

    /// <summary>"Cancelar" is offered: one of the two waits runs.</summary>
    public bool CanCancel => !IsDone && !IsFailed && !_waitEnded && Step is BootStep.ShaderCache or BootStep.CodePrecompile;

    /// <summary>The text of the current step, in the interface language; the failure once the start failed.</summary>
    public string StepText => Failure ?? StepTextOf(Step, Done, Total);

    /// <summary>"Log completo: &lt;path&gt;" under a failure; null without one or without a known log.</summary>
    public string? FailureLogText => IsFailed && FailureLogPath != null ? Loc.Format("LoadingFailureLog", FailureLogPath) : null;

    /// <summary>The button that closes a failed screen.</summary>
    public static string CloseText => Loc.Get("LoadingFailureClose");

    /// <summary>The text of <paramref name="step"/> (with its count when it has one).</summary>
    public static string StepTextOf(BootStep step, int done = 0, int total = 0) => step switch
    {
        BootStep.ShaderCache when total > 0 => Loc.Format("ShaderCacheLoadStatus", done, total),
        BootStep.CodePrecompile when total > 0 => Loc.Format("PrecompileCodeStatus", done, total),
        BootStep.Firmware => Loc.Get("BootStepFirmware"),
        BootStep.Input => Loc.Get("BootStepInput"),
        BootStep.Audio => Loc.Get("BootStepAudio"),
        BootStep.Gpu => Loc.Get("BootStepGpu"),
        BootStep.Game => Loc.Get("BootStepGame"),
        BootStep.ShaderCache => Loc.Get("BootStepShaderCache"),
        BootStep.CodePrecompile => Loc.Get("BootStepCodePrecompile"),
        BootStep.Ready => Loc.Get("BootStepReady"),
        _ => Loc.Get("BootStepStarting"),
    };

    /// <summary>The "Cancelar" button's text.</summary>
    public static string CancelText => Loc.Get("ShaderCacheLoadCancel");

    /// <summary>A line of the game process's log: the boot step, shader cache or code precompilation progress it
    /// carries, if any; true when it was one of them.</summary>
    public bool OnLine(string line)
    {
        if (line.Contains(BootProgress.LineTag, StringComparison.Ordinal) && BootProgress.TryParse(line, out BootStep step))
        {
            OnStep(step);
            return true;
        }

        if (line.Contains(BootProgress.LogFileTag, StringComparison.Ordinal) && BootProgress.TryParseLogFile(line, out string? log))
        {
            LogPath = log;
            return true;
        }

        if (line.Contains(ShaderCacheLoad.LineTag, StringComparison.Ordinal) && ShaderCacheLoad.TryParse(line, out ShaderCacheLoadProgress shaders))
        {
            OnShaderCache(shaders);
            return true;
        }

        if (line.Contains(CodePrecompileLog.LineTag, StringComparison.Ordinal) && CodePrecompileLog.TryParse(line, out CodePrecompileProgress code))
        {
            OnCodePrecompile(code);
            return true;
        }

        return false;
    }

    /// <summary>A <see cref="BootProgress.Reported"/> step.</summary>
    public void OnStep(BootStep step)
    {
        if (IsDone || IsFailed || step <= Step)
        {
            return;
        }

        if (step == BootStep.Ready)
        {
            End();
            return;
        }

        MoveTo(step);
        Changed?.Invoke();
    }

    /// <summary>A <see cref="ShaderCacheLoad.Reported"/> progress.</summary>
    public void OnShaderCache(ShaderCacheLoadProgress progress) => OnWait(BootStep.ShaderCache, progress.Done, progress.Total, progress.Finished);

    /// <summary>A <see cref="CodePrecompileLog.Reported"/> progress.</summary>
    public void OnCodePrecompile(CodePrecompileProgress progress) => OnWait(BootStep.CodePrecompile, progress.Done, progress.Total, progress.Finished);

    private void OnWait(BootStep step, int done, int total, bool finished)
    {
        if (IsDone || IsFailed || step < Step || (step == Step && _waitEnded))
        {
            return;
        }

        if (step > Step)
        {
            MoveTo(step);
        }

        // the finished line of a cancelled or empty wait carries 0/0: the bar keeps what it had and fills
        if (total > 0 || !finished)
        {
            Done = Math.Min(done, Math.Max(total, 0));
            Total = Math.Max(total, 0);
        }

        if (finished)
        {
            Done = Total;
            _waitEnded = true;
        }

        Changed?.Invoke();
    }

    private void MoveTo(BootStep step)
    {
        Step = step;
        Done = 0;
        Total = 0;
        _waitEnded = false;
    }

    /// <summary>"Cancelar": the wait that runs now, which the platform stops (the desktop launcher writes its
    /// <see cref="ShaderCacheLoad.CancelCommand"/> / <see cref="CodePrecompileLog.CancelCommand"/> to the game process,
    /// Android calls <see cref="ShaderCacheLoad.Cancel"/> / <see cref="CodePrecompileLog.Cancel"/>); null when none runs.
    /// The button goes at once; the wait's own finished progress still comes and ends the step.</summary>
    public BootStep? Cancel()
    {
        if (!CanCancel)
        {
            return null;
        }

        BootStep step = Step;
        _waitEnded = true;
        Changed?.Invoke();
        return step;
    }

    /// <summary>
    /// The start failed (the title could not be loaded, its license is missing, the process ended with an error before
    /// the game was ready): the screen shows <paramref name="message"/> and, when known, where the log is, instead of
    /// going without a word; the steps stop and "Cancelar" goes. The first failure stays (a later one changes nothing).
    /// </summary>
    public void Fail(string message, string? logPath)
    {
        if (IsDone || IsFailed)
        {
            return;
        }

        Failure = message;
        FailureLogPath = logPath;
        Changed?.Invoke();
    }

    /// <summary>The screen is over (the game is ready, or its process ended): always the last change.</summary>
    public void End()
    {
        if (IsDone)
        {
            return;
        }

        IsDone = true;
        Step = BootStep.Ready;
        Changed?.Invoke();
    }
}
