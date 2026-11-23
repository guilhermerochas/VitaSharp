using VitaSharp.Core.Util;

namespace VitaSharp.Core.Boot;

/// <summary>The steps of starting a game, in the order they run: what the loading screen shows between the click and
/// the game's picture (<see cref="LoadingScreen"/>). The two waits with their own progress keep their own protocols:
/// <see cref="ShaderCache"/> is <see cref="Gpu.ShaderCacheLoad"/> (the recorded shaders and pipelines are built) and
/// <see cref="CodePrecompile"/> is <see cref="Cpu.CodePrecompileLog"/> (the ARM code is translated); they are different
/// work and keep different names.</summary>
public enum BootStep
{
    /// <summary>The game process was asked for (the click), nothing reported yet.</summary>
    Starting,
    /// <summary>The emulator and the firmware's file systems (os0, vs0) come up.</summary>
    Firmware,
    /// <summary>The keyboard and controller profiles (input.json) and the controller library.</summary>
    Input,
    /// <summary>The audio output device.</summary>
    Audio,
    /// <summary>The Vulkan instance, device and swapchain.</summary>
    Gpu,
    /// <summary>The title's param.sfo, mounts and license (the start of <see cref="Emulator.LoadTitle"/>).</summary>
    Game,
    /// <summary>"Carregando o cache de shaders": the title's shader and pipeline caches.</summary>
    ShaderCache,
    /// <summary>"Pré-compilando código ARM": the boot step that translates the game's ARM code.</summary>
    CodePrecompile,
    /// <summary>The game runs and its window shows it: the loading screen goes.</summary>
    Ready,
}

/// <summary>
/// The protocol of the boot steps: each one is a <see cref="LineTag"/> line in the game process's log, which the
/// desktop launcher reads to show its loading window (the game runs in its own process), and a <see cref="Reported"/>
/// event in the process itself (the Android game screen). Reported by whoever runs the step: the hosts (firmware,
/// input, audio, GPU, ready) and <see cref="Emulator"/> (game, shader cache, code precompilation).
/// </summary>
public static class BootProgress
{
    /// <summary>Log line: "[boot-step] &lt;name&gt;" (<see cref="Name"/>).</summary>
    public const string LineTag = "[boot-step] ";

    /// <summary>The command-line option the launcher passes when it shows the loading window: the game's own window
    /// stays hidden until <see cref="BootStep.Ready"/>, so the loading window is what the user sees until then.</summary>
    public const string LauncherScreenOption = "--loading-screen=launcher";

    /// <summary>Raised (on the thread that runs the step) for every step reported.</summary>
    public static event Action<BootStep>? Reported;

    /// <summary>Logs <paramref name="step"/> and raises <see cref="Reported"/>.</summary>
    public static void Report(BootStep step)
    {
        Log.Info(Format(step));
        Reported?.Invoke(step);
    }

    /// <summary>Log line: "[boot-log] &lt;path&gt;", the game session's log file (<see cref="ReportLogFile"/>).</summary>
    public const string LogFileTag = "[boot-log] ";

    /// <summary>Logs where this session's log file is: a start that fails shows it on the loading screen.</summary>
    public static void ReportLogFile(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            Log.Info(LogFileTag + path);
        }
    }

    /// <summary>Parses a <see cref="LogFileTag"/> line (anywhere in a log line, after the log's time and level).</summary>
    public static bool TryParseLogFile(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? path)
    {
        path = null;
        int at = line.IndexOf(LogFileTag, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        string value = line[(at + LogFileTag.Length)..].Trim();
        if (value.Length == 0)
        {
            return false;
        }

        path = value;
        return true;
    }

    /// <summary>The name of a step in the log line (fixed by this protocol, it never changes).</summary>
    public static string Name(BootStep step) => step switch
    {
        BootStep.Starting => "starting",
        BootStep.Firmware => "firmware",
        BootStep.Input => "input",
        BootStep.Audio => "audio",
        BootStep.Gpu => "gpu",
        BootStep.Game => "game",
        BootStep.ShaderCache => "shader-cache",
        BootStep.CodePrecompile => "code-precompile",
        BootStep.Ready => "ready",
        _ => "unknown",
    };

    /// <summary>The log line of <paramref name="step"/>.</summary>
    public static string Format(BootStep step) => LineTag + Name(step);

    /// <summary>Parses a <see cref="LineTag"/> line (anywhere in a log line, after the log's time and level).</summary>
    public static bool TryParse(string line, out BootStep step)
    {
        step = BootStep.Starting;
        int at = line.IndexOf(LineTag, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        ReadOnlySpan<char> name = line.AsSpan(at + LineTag.Length).Trim();
        foreach (BootStep candidate in Enum.GetValues<BootStep>())
        {
            if (name.Equals(Name(candidate), StringComparison.Ordinal))
            {
                step = candidate;
                return true;
            }
        }

        return false;
    }
}
