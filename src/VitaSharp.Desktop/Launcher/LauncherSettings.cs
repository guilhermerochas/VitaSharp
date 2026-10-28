using System.Text.Json;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Launcher preferences, stored as JSON in the data folder.</summary>
public sealed class LauncherSettings
{
    public bool Fullscreen { get; set; }
    public int Scale { get; set; } = 1;
    public bool VSync { get; set; } = true;
    public bool Validation { get; set; }
    public bool ShowLog { get; set; }
    /// <summary>Webcam used for the Vita cameras: "" = first device, "none" = no webcam (grey frames), or a device name.</summary>
    public string Camera { get; set; } = "";
    /// <summary>CPU backend: "" = best supported on this PC, or "native", "il", "interp" (--cpu=).</summary>
    public string CpuBackend { get; set; } = "";
    /// <summary>"Compilação rápida de shaders": shaderc optimisation Zero (on) or Performance (off), --shaderc-opt=.</summary>
    public bool FastShaderCompile { get; set; } = true;
    /// <summary>"Compilar shaders e pipelines em segundo plano": shader/pipeline prefetch workers, --async-shaders=.</summary>
    public bool BackgroundShaderCompile { get; set; } = true;
    /// <summary>"Modo de depuração": detailed diagnostic logging (--debug-mode, docs/notes/debug-mode.md).</summary>
    public bool DebugMode { get; set; }
    /// <summary>"Simular conta PSN conectada (offline)": the emulated PSN account is signed in (--psn=on, the default) or
    /// signed out (--psn=off). Signed in, sign-in checks pass and PSN server requests fail as unreachable.</summary>
    public bool PsnSignedIn { get; set; } = true;
    /// <summary>Online ID of the emulated PSN account (--psn-id=).</summary>
    public string PsnOnlineId { get; set; } = VitaSharp.Core.Hle.PsnAccount.DefaultOnlineId;

    static string FilePath => Path.Combine(Program.DataRoot, "launcher.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize(File.ReadAllText(FilePath), LauncherJson.Default.LauncherSettings) ?? new();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Program.DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, LauncherJson.Default.LauncherSettings));
    }

    /// <summary>Command-line options for "run".</summary>
    public IEnumerable<string> RunOptions()
    {
        if (Fullscreen) yield return "--fullscreen";
        if (Scale > 1) yield return $"--scale={Scale}";
        if (!VSync) yield return "--no-vsync";
        if (Validation) yield return "--validation";
        if (!string.IsNullOrWhiteSpace(Camera)) yield return $"--camera={Camera}";
        if (!string.IsNullOrWhiteSpace(CpuBackend)) yield return $"--cpu={CpuBackend}";
        yield return FastShaderCompile ? "--shaderc-opt=zero" : "--shaderc-opt=perf";
        yield return BackgroundShaderCompile ? "--async-shaders=on" : "--async-shaders=off";
        if (DebugMode) yield return "--debug-mode";
        yield return PsnSignedIn ? "--psn=on" : "--psn=off";
        if (VitaSharp.Core.Hle.PsnAccount.IsValidOnlineId(PsnOnlineId)) yield return $"--psn-id={PsnOnlineId}";
    }
}

/// <summary>Source-generated JSON metadata for the settings file (Native AOT has no reflection-based serializer).</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LauncherSettings))]
internal sealed partial class LauncherJson : System.Text.Json.Serialization.JsonSerializerContext;
