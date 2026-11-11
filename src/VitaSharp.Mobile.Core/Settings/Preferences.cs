using System.Text.Json;
using System.Text.Json.Serialization;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;

namespace VitaSharp.Mobile.Core.Settings;

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
    /// <summary>"Carregamento rapido" of every game without its own choice: storage reads with no speed limit
    /// (Core/Util/IoTiming.cs FastLoading). Off by default: games load at the Vita memory card's speed, which some need.</summary>
    public bool FastLoading { get; set; }
    /// <summary>GPU profile: "" = the real device (quirks from its vendor/driver), "desktop", "adreno" or "mali"
    /// (--gpu-profile=, docs/notes/mobile-gpu.md).</summary>
    public string GpuProfile { get; set; } = "";
    public bool VSync { get; set; } = true;
    public bool ShowFps { get; set; }
    /// <summary>"Modo de depuracao": debug logging (Log level Debug + DebugMode, docs/notes/debug-mode.md).</summary>
    public bool DebugMode { get; set; }
    /// <summary>Opacity of the on-screen controls, 0.1..1.</summary>
    public float OnScreenOpacity { get; set; } = 0.55f;
    /// <summary>Hide the on-screen controls while a physical controller is connected.</summary>
    public bool HideOnScreenWithGamepad { get; set; } = true;
    /// <summary>Vibrate briefly on an on-screen button press.</summary>
    public bool Haptics { get; set; } = true;
    /// <summary>"Movimento": what drives SceMotion, "" = automatic (the phone's sensors when it has them, else a
    /// controller's), "phone", "controller" or "off" (Input.AndroidMotionModes).</summary>
    public string Motion { get; set; } = "";
    /// <summary>"Som de trofeu": a chime with the in-game trophy notification (the firmware's notice sound, else a
    /// built-in one; same as the desktop launcher's option, --trophy-sound=).</summary>
    public bool TrophySound { get; set; } = true;
    /// <summary>"Conta PSN e rede" (password excluded: it lives in the Android Keystore).</summary>
    public NetworkSettings Network { get; set; } = new();
    /// <summary>Title id of the last game played ("Continuar jogando").</summary>
    public string LastPlayed { get; set; } = "";
    /// <summary>Language of the app's texts: "" = the phone's, "en" or "pt-BR" (<see cref="Loc.SetLanguage"/>, same
    /// choice as the desktop launcher's).</summary>
    public string Language { get; set; } = "";

    /// <summary>The choices of the language row: the phone's language, English, Portuguese.</summary>
    public static IReadOnlyList<Choice> Languages =>
    [
        new("", Loc.Get("ThemeSystem")),
        new(Loc.English, Loc.Get("LanguageEnglish")),
        new(Loc.Portuguese, Loc.Get("LanguagePortuguese")),
    ];

    public static AppPreferences Load(string path) => SettingsFile.Load(path, PrefsJson.Default.AppPreferences) ?? new();
    public void Save(string path) => SettingsFile.Save(path, this, PrefsJson.Default.AppPreferences);
}

/// <summary>
/// Per-game settings ("Configuracoes deste jogo" in detalhes.html) and play statistics, stored in
/// &lt;data&gt;/config/games/TITLEID.json. Null settings follow <see cref="AppPreferences"/>.
/// </summary>
public sealed class GameSettings
{
    public string? CpuBackend { get; set; }
    public int? CpuMhz { get; set; }
    public string? GpuProfile { get; set; }
    public bool? VSync { get; set; }
    public bool? ShowFps { get; set; }
    /// <summary>"Resolucao interna": render-target scale below native, "0.75" or "0.5" (null = native; per game only,
    /// VulkanRenderer.ResScale.cs). Lower is faster on GPU-bound games, and softer.</summary>
    public string? ResScale { get; set; }
    /// <summary>"MSAA": "off" renders the game's multisampled scenes with one sample (faster on the GPU, edges alias);
    /// null = native (as the game asks, 2x/4x). Per game only.</summary>
    public string? Msaa { get; set; }
    /// <summary>"Carregamento rapido" of this game (null = <see cref="AppPreferences.FastLoading"/>).</summary>
    public bool? FastLoading { get; set; }

    /// <summary>Total time played, in seconds.</summary>
    public long PlaySeconds { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    /// <summary>Average FPS of the last session (frames presented / seconds).</summary>
    public double AverageFps { get; set; }

    [JsonIgnore]
    public bool HasOverrides => CpuBackend != null || CpuMhz != null || GpuProfile != null || VSync != null || ShowFps != null || ResScale != null || Msaa != null || FastLoading != null;

    public static GameSettings Load(string path) => SettingsFile.Load(path, PrefsJson.Default.GameSettings) ?? new();
    public void Save(string path) => SettingsFile.Save(path, this, PrefsJson.Default.GameSettings);

    /// <summary>Adds one finished session to the statistics.</summary>
    public void RecordSession(TimeSpan played, long framesPresented, DateTime endUtc)
    {
        if (played <= TimeSpan.Zero)
        {
            return;
        }

        PlaySeconds += (long)played.TotalSeconds;
        LastPlayedUtc = endUtc;
        if (played.TotalSeconds >= 5)
        {
            AverageFps = Math.Round(framesPresented / played.TotalSeconds, 1);
        }
    }
}

/// <summary>What a game runs with: the game's settings over the global ones, validated.</summary>
public sealed record EffectiveSettings(string CpuBackend, int CpuMhz, string GpuProfile, bool VSync, bool ShowFps, string ResScale = "", string Msaa = "", bool FastLoading = false)
{
    public static EffectiveSettings Resolve(AppPreferences app, GameSettings? game)
    {
        string cpu = game?.CpuBackend ?? app.CpuBackend;
        if (cpu.Length > 0 && cpu != "interp" && !Jit.All.Any(b => b.Name == cpu && b.IsSupported))
        {
            cpu = ""; // not on this device
        }

        int mhz = game?.CpuMhz ?? app.CpuMhz;
        if (mhz is not (0 or 444))
        {
            mhz = 0;
        }

        string gpu = SettingsChoices.GpuProfiles.Any(c => c.Value == (game?.GpuProfile ?? app.GpuProfile)) ? game?.GpuProfile ?? app.GpuProfile : "";
        string res = SettingsChoices.ResScales.Any(c => c.Value == game?.ResScale) ? game!.ResScale! : "";
        string msaa = game?.Msaa == "off" ? "off" : "";
        return new(cpu, mhz, gpu, game?.VSync ?? app.VSync, game?.ShowFps ?? app.ShowFps, res, msaa, game?.FastLoading ?? app.FastLoading);
    }

    /// <summary>VulkanRendererOptions.ResScale for <see cref="ResScale"/> (0 = native).</summary>
    public float ResScaleValue => float.TryParse(ResScale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

    /// <summary>CpuCore.InstructionsPerSecond for <see cref="CpuMhz"/>.</summary>
    public long ClockHz => CpuMhz > 0 ? CpuMhz * 1_000_000L : CpuCore.DefaultClockHz;
}

/// <summary>The choices of the picker rows, with the labels the mockups use.</summary>
public static class SettingsChoices
{
    public static IReadOnlyList<Choice> Clocks { get; } =
    [
        new("0", Loc.Get("MClock333")),
        new("444", Loc.Get("MClock444")),
    ];

    public static IReadOnlyList<Choice> GpuProfiles { get; } =
    [
        new("", Loc.Get("MGpuAuto")),
        new("desktop", Loc.Get("MGpuNoWorkarounds")),
        new("adreno", Loc.Get("MGpuAdreno")),
        new("mali", Loc.Get("MGpuMali")),
    ];

    public static IReadOnlyList<Choice> ResScales { get; } =
    [
        new("", Loc.Get("MResNative")),
        new("0.75", Loc.Get("MRes75")),
        new("0.5", Loc.Get("MRes50")),
    ];

    public static IReadOnlyList<Choice> MsaaModes { get; } =
    [
        new("", Loc.Get("MMsaaNative")),
        new("off", Loc.Get("MMsaaOff")),
    ];

    /// <summary>The label of <paramref name="value"/> in <paramref name="choices"/>, or the value itself.</summary>
    public static string LabelOf(IReadOnlyList<Choice> choices, string value) => choices.FirstOrDefault(c => c.Value == value)?.Label ?? value;

    /// <summary>A per-game row's choices: "Padrao (...)" (follow the global setting, value null) first.</summary>
    public static IReadOnlyList<Choice> WithDefault(IReadOnlyList<Choice> choices, string globalValue) =>
        [new(DefaultValue, Loc.Format("MDefaultParen", LabelOf(choices, globalValue))), .. choices];

    /// <summary>The value standing for "follow the global setting" in a per-game picker.</summary>
    public const string DefaultValue = "\u0001default";
}

internal static class SettingsFile
{
    public static T? Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), info);
            }
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
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, info));
        File.Move(tmp, path, true);
    }
}

/// <summary>Source-generated JSON metadata (trimmed/AOT Android builds have no reflection-based serializer).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppPreferences))]
[JsonSerializable(typeof(GameSettings))]
internal sealed partial class PrefsJson : JsonSerializerContext;
