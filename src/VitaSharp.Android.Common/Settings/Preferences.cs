using System.Text.Json;
using System.Text.Json.Serialization;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Settings;

/// <summary>A labelled choice of a picker row (value stored in the settings file, label shown).</summary>
public sealed record Choice(string Value, string Label);

/// <summary>
/// Global settings ("Ajustes", docs/android/mockups/ajustes.html), stored in &lt;data&gt;/config/app.json. The CPU,
/// clock and GPU values are the defaults of every game; a game's own settings (<see cref="GameSettings"/>) override
/// them field by field.
/// </summary>
public sealed class AppPreferences
{
    /// <summary>CPU backend: "" = automatic (the native ARM64 JIT on a phone), "native", "il" or "interp" (--cpu=).</summary>
    public string CpuBackend { get; set; } = "";
    /// <summary>Emulated ARM clock in MHz: 0 = the Vita's default for applications (333), 444 = the most a game may ask
    /// for (--cpu-mhz=, same meaning as the desktop launcher).</summary>
    public int CpuMhz { get; set; }
    /// <summary>GPU profile: "" = the real device (quirks from its vendor/driver), "desktop", "adreno" or "mali"
    /// (--gpu-profile=, docs/notes/mobile-gpu.md).</summary>
    public string GpuProfile { get; set; } = "";
    public bool VSync { get; set; } = true;
    public bool ShowFps { get; set; }
    /// <summary>"Modo de depuração": debug logging (Log level Debug + DebugMode, docs/notes/debug-mode.md).</summary>
    public bool DebugMode { get; set; }
    /// <summary>Opacity of the on-screen controls, 0.1..1.</summary>
    public float OnScreenOpacity { get; set; } = 0.55f;
    /// <summary>Hide the on-screen controls while a physical controller is connected.</summary>
    public bool HideOnScreenWithGamepad { get; set; } = true;
    /// <summary>Vibrate briefly on an on-screen button press.</summary>
    public bool Haptics { get; set; } = true;
    /// <summary>"Conta PSN e rede" (password excluded: it lives in the Android Keystore).</summary>
    public NetworkSettings Network { get; set; } = new();
    /// <summary>Title id of the last game played ("Continuar jogando").</summary>
    public string LastPlayed { get; set; } = "";

    public static AppPreferences Load(string path) => SettingsFile.Load(path, PrefsJson.Default.AppPreferences) ?? new();
    public void Save(string path) => SettingsFile.Save(path, this, PrefsJson.Default.AppPreferences);
}

/// <summary>
/// Per-game settings ("Configurações deste jogo" in detalhes.html) and play statistics, stored in
/// &lt;data&gt;/config/games/TITLEID.json. Null settings follow <see cref="AppPreferences"/>.
/// </summary>
public sealed class GameSettings
{
    public string? CpuBackend { get; set; }
    public int? CpuMhz { get; set; }
    public string? GpuProfile { get; set; }
    public bool? VSync { get; set; }
    public bool? ShowFps { get; set; }

    /// <summary>Total time played, in seconds.</summary>
    public long PlaySeconds { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    /// <summary>Average FPS of the last session (frames presented / seconds).</summary>
    public double AverageFps { get; set; }

    [JsonIgnore]
    public bool HasOverrides => CpuBackend != null || CpuMhz != null || GpuProfile != null || VSync != null || ShowFps != null;

    public static GameSettings Load(string path) => SettingsFile.Load(path, PrefsJson.Default.GameSettings) ?? new();
    public void Save(string path) => SettingsFile.Save(path, this, PrefsJson.Default.GameSettings);

    /// <summary>Adds one finished session to the statistics.</summary>
    public void RecordSession(TimeSpan played, long framesPresented, DateTime endUtc)
    {
        if (played <= TimeSpan.Zero) return;
        PlaySeconds += (long)played.TotalSeconds;
        LastPlayedUtc = endUtc;
        if (played.TotalSeconds >= 5) AverageFps = Math.Round(framesPresented / played.TotalSeconds, 1);
    }
}

/// <summary>What a game runs with: the game's settings over the global ones, validated.</summary>
public sealed record EffectiveSettings(string CpuBackend, int CpuMhz, string GpuProfile, bool VSync, bool ShowFps)
{
    public static EffectiveSettings Resolve(AppPreferences app, GameSettings? game)
    {
        string cpu = game?.CpuBackend ?? app.CpuBackend;
        if (cpu.Length > 0 && cpu != "interp" && !Jit.All.Any(b => b.Name == cpu && b.IsSupported)) cpu = ""; // not on this device
        int mhz = game?.CpuMhz ?? app.CpuMhz;
        if (mhz is not (0 or 444)) mhz = 0;
        string gpu = SettingsChoices.GpuProfiles.Any(c => c.Value == (game?.GpuProfile ?? app.GpuProfile)) ? game?.GpuProfile ?? app.GpuProfile : "";
        return new(cpu, mhz, gpu, game?.VSync ?? app.VSync, game?.ShowFps ?? app.ShowFps);
    }

    /// <summary>CpuCore.InstructionsPerSecond for <see cref="CpuMhz"/>.</summary>
    public long ClockHz => CpuMhz > 0 ? CpuMhz * 1_000_000L : CpuCore.DefaultClockHz;
}

/// <summary>The choices of the picker rows, with the labels the mockups use.</summary>
public static class SettingsChoices
{
    public static IReadOnlyList<Choice> Clocks { get; } =
    [
        new("0", "333 MHz (como o Vita)"),
        new("444", "444 MHz (carregamentos mais rápidos)"),
    ];

    public static IReadOnlyList<Choice> GpuProfiles { get; } =
    [
        new("", "Automático (este aparelho)"),
        new("desktop", "Sem contornos de driver"),
        new("adreno", "Simular Adreno"),
        new("mali", "Simular Mali"),
    ];

    /// <summary>The label of <paramref name="value"/> in <paramref name="choices"/>, or the value itself.</summary>
    public static string LabelOf(IReadOnlyList<Choice> choices, string value) => choices.FirstOrDefault(c => c.Value == value)?.Label ?? value;

    /// <summary>A per-game row's choices: "Padrão (…)" (follow the global setting, value null) first.</summary>
    public static IReadOnlyList<Choice> WithDefault(IReadOnlyList<Choice> choices, string globalValue) =>
        [new(DefaultValue, $"Padrão ({LabelOf(choices, globalValue)})"), .. choices];

    /// <summary>The value standing for "follow the global setting" in a per-game picker.</summary>
    public const string DefaultValue = "\u0001default";
}

static class SettingsFile
{
    public static T? Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize(File.ReadAllText(path), info);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Settings: could not read {path}: {e.Message}");
        }
        return null;
    }

    public static void Save<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, info));
        File.Move(tmp, path, true);
    }
}

/// <summary>Source-generated JSON metadata (trimmed/AOT Android builds have no reflection-based serializer).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppPreferences))]
[JsonSerializable(typeof(GameSettings))]
internal sealed partial class PrefsJson : JsonSerializerContext;
