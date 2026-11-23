using System.Text.Json;
using System.Text.Json.Serialization;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;
using FrameRateMode = VitaSharp.Core.Hle.Modules.FrameRateMode;

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
    /// <summary>"Resolucao interna" of every game without its own choice (<see cref="GameSettings.ResScale"/>): "" =
    /// native (the default), "0.75", "0.5", "2", "3" or "4" (<see cref="SettingsChoices.ResScales"/>; the desktop
    /// launcher's global option of the same name).</summary>
    public string ResScale { get; set; } = "";
    /// <summary>"Compilacao rapida de shaders": shaderc without optimisation ("zero", fewer hitches when new shaders
    /// appear) or with it ("perf"); the desktop launcher's option of the same name (--shaderc-opt=, VITASHARP_SHADERC_OPT
    /// in env.txt wins).</summary>
    public bool FastShaderCompile { get; set; } = true;
    /// <summary>"Compilar shaders em segundo plano": shaders and pipelines prepared on worker threads (the desktop's
    /// --async-shaders=, VITASHARP_ASYNC_SHADERS in env.txt wins).</summary>
    public bool BackgroundShaderCompile { get; set; } = true;
    /// <summary>"Carregar o cache de shaders antes de jogar" of every game without its own choice: the shaders and
    /// pipelines recorded in the game's earlier sessions are built before it starts, with the progress and "Cancelar" over
    /// the game screen (Core/Gpu/ShaderCacheLoad.cs). On by default, as the desktop launcher's option of the same name
    /// (--load-shader-cache).</summary>
    public bool LoadShaderCacheBeforePlaying { get; set; } = true;
    /// <summary>"Precompilar codigo ARM" of every game without its own choice: the game's ARM code is translated ahead
    /// of execution (the blocks its earlier sessions ran, then the code near what it runs; Core/Cpu/CodeCache.cs). On the
    /// phone everything runs in the background, the game does not wait. On by default, as the desktop launcher's option of
    /// the same name (--precompile-code).</summary>
    public bool PrecompileCode { get; set; } = true;
    /// <summary>The shaderc level of <see cref="FastShaderCompile"/>, as the renderer's ShaderOptimization option.</summary>
    [JsonIgnore]
    public string ShaderOptimization => FastShaderCompile ? "zero" : "perf";
    /// <summary>GPU profile: "" = the real device (quirks from its vendor/driver), "desktop", "adreno" or "mali"
    /// (--gpu-profile=, docs/notes/mobile-gpu.md).</summary>
    public string GpuProfile { get; set; } = "";
    public bool VSync { get; set; } = true;
    public bool ShowFps { get; set; }
    /// <summary>"Modo de depuracao": debug logging (Log level Debug + DebugMode, docs/notes/debug-mode.md).</summary>
    public bool DebugMode { get; set; }
    /// <summary>The emulator option "Ativar Plugins do Kernel": the plugins of ur0:tai / ux0:tai config.txt load (off:
    /// the pure firmware, no plugin).</summary>
    public bool KernelPlugins { get; set; }
    /// <summary>Opacity of the on-screen controls, 0.1..1.</summary>
    public float OnScreenOpacity { get; set; } = 0.55f;
    /// <summary>Hide the on-screen controls while a physical controller is connected.</summary>
    public bool HideOnScreenWithGamepad { get; set; } = true;
    /// <summary>Vibrate briefly on every on-screen control press, also while another finger holds the stick.</summary>
    public bool Haptics { get; set; } = true;
    /// <summary>Strength of that vibration: 1 weak, 2 medium, 3 strong (Input.OnScreenHaptics).</summary>
    public int HapticStrength { get; set; } = 2;
    /// <summary>"Movimento": what drives SceMotion, "" = automatic (the phone's sensors when it has them, else a
    /// controller's), "phone", "controller" or "off" (Input.AndroidMotionModes).</summary>
    public string Motion { get; set; } = "";
    /// <summary>"Camera frontal": what the Vita's front camera shows, as the desktop launcher's option of the same name
    /// (--camera-front=, Core.Host.CameraSpec): "" = the phone's front camera, "none" = grey frames, a .png/.jpg picture
    /// (relative to the data folder: the one picked in Ajustes is copied to camera/, <see cref="CameraSettings"/>).</summary>
    public string CameraFront { get; set; } = "";
    /// <summary>"Camera traseira": the same for the Vita's back camera (--camera-back=; "" = the phone's back camera).</summary>
    public string CameraBack { get; set; } = "";
    /// <summary>"Ao abrir o emulador": "library" (the default) or "system" (the shell boots when the app opens), the
    /// desktop launcher's option (<see cref="StartupViews"/>).</summary>
    public string StartupView { get; set; } = StartupViews.Library;
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
    /// <summary>"Resolucao interna": render-target scale, "" (native), "0.75", "0.5", "2", "3" or "4" (null = the
    /// global <see cref="AppPreferences.ResScale"/>; VulkanRenderer.ResScale.cs; 2-4 are the desktop launcher's choices). Lower is faster on GPU-bound games, and
    /// softer; 2x is sharper and costs about four times the GPU work, 3x nine, 4x sixteen.</summary>
    public string? ResScale { get; set; }
    /// <summary>"MSAA": the desktop's modes (its F3 key): "ss" renders the game's multisampled scenes 2x supersampled,
    /// "off" with one sample (faster on the GPU, edges alias); null = native (as the game asks, 2x/4x). Per game only.</summary>
    public string? Msaa { get; set; }
    /// <summary>"Carregamento rapido" of this game (null = <see cref="AppPreferences.FastLoading"/>).</summary>
    public bool? FastLoading { get; set; }
    /// <summary>"Carregar o cache de shaders antes de jogar" of this game (null = <see cref="AppPreferences.LoadShaderCacheBeforePlaying"/>).</summary>
    public bool? LoadShaderCacheBeforePlaying { get; set; }
    /// <summary>"Precompilar codigo ARM" of this game (null = <see cref="AppPreferences.PrecompileCode"/>).</summary>
    public bool? PrecompileCode { get; set; }
    /// <summary>"Taxa de quadros": "30", "60" or "unlimited" (null = native, the game's own pacing; per game only, as the
    /// desktop launcher's --frame-rate=, Hle/Modules/Display.cs FrameRate).</summary>
    public string? FrameRate { get; set; }
    /// <summary>"Idioma do sistema": the SceSystemParamLang ("0".."19") this game reads instead of the system's language,
    /// for games without it (null = the system's; per game only, as the desktop launcher's --system-language=,
    /// EmulatorOptions.SystemLanguage).</summary>
    public string? SystemLanguage { get; set; }

    /// <summary>Total time played, in seconds.</summary>
    public long PlaySeconds { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    /// <summary>Average FPS of the last session (frames presented / seconds).</summary>
    public double AverageFps { get; set; }

    [JsonIgnore]
    public bool HasOverrides => CpuBackend != null || CpuMhz != null || GpuProfile != null || VSync != null || ShowFps != null || ResScale != null || Msaa != null || FastLoading != null || FrameRate != null
        || SystemLanguage != null || LoadShaderCacheBeforePlaying != null || PrecompileCode != null;

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
public sealed record EffectiveSettings(string CpuBackend, int CpuMhz, string GpuProfile, bool VSync, bool ShowFps, string ResScale = "", string Msaa = "", bool FastLoading = false, string FrameRate = "", string SystemLanguage = "", bool LoadShaderCacheBeforePlaying = true, bool PrecompileCode = true)
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
        string res = ResolveResScale(app.ResScale, game?.ResScale);
        string msaa = SettingsChoices.MsaaModes.Any(c => c.Value.Length > 0 && c.Value == game?.Msaa) ? game!.Msaa! : "";
        string rate = SettingsChoices.FrameRates.Any(c => c.Value.Length > 0 && c.Value == game?.FrameRate) ? game!.FrameRate! : "";
        string language = SettingsChoices.SystemLanguages.Any(c => c.Value.Length > 0 && c.Value == game?.SystemLanguage) ? game!.SystemLanguage! : "";
        return new(cpu, mhz, gpu, game?.VSync ?? app.VSync, game?.ShowFps ?? app.ShowFps, res, msaa, game?.FastLoading ?? app.FastLoading, rate, language,
            game?.LoadShaderCacheBeforePlaying ?? app.LoadShaderCacheBeforePlaying, game?.PrecompileCode ?? app.PrecompileCode);
    }

    /// <summary>The internal resolution a game runs at: its own valid choice (<paramref name="own"/>, "" = native),
    /// else the global one, else native ("").</summary>
    private static string ResolveResScale(string global, string? own)
    {
        if (own != null && SettingsChoices.ResScales.Any(c => c.Value == own))
        {
            return own;
        }

        return SettingsChoices.ResScales.Any(c => c.Value == global) ? global : "";
    }

    /// <summary>EmulatorOptions.SystemLanguage for <see cref="SystemLanguage"/> (null = the system's language).</summary>
    public int? SystemLanguageValue => int.TryParse(SystemLanguage, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : null;

    /// <summary>The frame rate mode the session starts with: the game's own choice, else VITASHARP_FRAME_RATE
    /// (<paramref name="environmentValue"/>, from config/env.txt), else native. Set at every start, so a mode never
    /// carries over from the game played before in the same process.</summary>
    public FrameRateMode StartFrameRate(string? environmentValue) =>
        VitaSharp.Core.Hle.Modules.FrameRate.Parse(FrameRate.Length > 0 ? FrameRate : environmentValue);

    /// <summary>VulkanRendererOptions.ResScale for <see cref="ResScale"/> (0 = native).</summary>
    /// <summary>VulkanRendererOptions.Msaa for <see cref="Msaa"/>: "ss" or "off", null = native (VITASHARP_MSAA in
    /// env.txt, else what the game asks).</summary>
    public string? MsaaValue => Msaa.Length > 0 ? Msaa : null;

    public float ResScaleValue => float.TryParse(ResScale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

    /// <summary>CpuCore.InstructionsPerSecond for <see cref="CpuMhz"/>.</summary>
    public long ClockHz => CpuMhz > 0 ? CpuMhz * 1_000_000L : CpuCore.DefaultClockHz;
}

/// <summary>The choices of the picker rows, with the labels the mockups use. Each list is built when read, in the
/// language of that moment: a list kept from the first read held the language the process started with (the labels and
/// the FPS overlay's mode stayed in Portuguese after the app was switched to English without restarting).</summary>
public static class SettingsChoices
{
    public static IReadOnlyList<Choice> Clocks =>
    [
        new("0", Loc.Get("MClock333")),
        new("444", Loc.Get("MClock444")),
    ];

    public static IReadOnlyList<Choice> GpuProfiles =>
    [
        new("", Loc.Get("MGpuAuto")),
        new("desktop", Loc.Get("MGpuNoWorkarounds")),
        new("adreno", Loc.Get("MGpuAdreno")),
        new("mali", Loc.Get("MGpuMali")),
    ];

    public static IReadOnlyList<Choice> ResScales =>
    [
        new("", Loc.Get("MResNative")),
        new("0.75", Loc.Get("MRes75")),
        new("0.5", Loc.Get("MRes50")),
        new("2", Loc.Get("MRes200")),
        new("3", Loc.Get("MRes300")),
        new("4", Loc.Get("MRes400")),
    ];

    /// <summary>"Taxa de quadros" (values of Hle/Modules/Display.cs FrameRate.Parse; "" = native).</summary>
    public static IReadOnlyList<Choice> FrameRates =>
    [
        new("", Loc.Get("RateNative")),
        new("30", Loc.Get("RateFixed30")),
        new("60", Loc.Get("RateFixed60")),
        new("unlimited", Loc.Get("RateUnlimited")),
    ];

    /// <summary>"Idioma do sistema": the system's language ("") or a SceSystemParamLang, each language in its own name
    /// (Core/Firmware/SystemLanguage.cs; the desktop launcher's list).</summary>
    public static IReadOnlyList<Choice> SystemLanguages =>
    [
        new("", Loc.Get("SystemLanguageFollow")),
        .. VitaSharp.Core.Firmware.SystemLanguage.Names.Select((name, language) => new Choice(language.ToString(System.Globalization.CultureInfo.InvariantCulture), name)),
    ];

    /// <summary>The frame rate choice after <paramref name="value"/>, wrapping around (the pause menu's row cycles
    /// through them); an unknown value starts over at native.</summary>
    public static string NextFrameRate(string? value)
    {
        IReadOnlyList<Choice> rates = FrameRates;
        int index = -1;
        for (int i = 0; i < rates.Count; i++)
        {
            if (rates[i].Value == (value ?? ""))
            {
                index = i;
            }
        }
        return rates[(index + 1) % rates.Count].Value;
    }

    /// <summary>The FPS overlay's text: the frames of the last second, and the frame rate mode when it is not native.</summary>
    public static string FpsText(long fps, string frameRate) =>
        frameRate.Length > 0 ? Loc.Format("MFpsWithMode", fps, LabelOf(FrameRates, frameRate)) : Loc.Format("MFpsCount", fps);

    /// <summary>"Intensidade da vibracao" of the on-screen controls (values of Input.OnScreenHaptics).</summary>
    public static IReadOnlyList<Choice> HapticStrengths =>
    [
        new("1", Loc.Get("MHapticWeak")),
        new("2", Loc.Get("MHapticMedium")),
        new("3", Loc.Get("MHapticStrong")),
    ];

    /// <summary>"MSAA" per game, the desktop's modes in the order of its F3 key: native, 2x supersampling, off.</summary>
    public static IReadOnlyList<Choice> MsaaModes =>
    [
        new("", Loc.Get("MMsaaNative")),
        new("ss", Loc.Get("MMsaaSs")),
        new("off", Loc.Get("MMsaaOff")),
    ];

    /// <summary>The pause menu's "MSAA": the mode after <paramref name="value"/> ("" = native), as the desktop's F3.</summary>
    public static string NextMsaa(string? value)
    {
        IReadOnlyList<Choice> modes = MsaaModes;
        int index = -1;
        for (int i = 0; i < modes.Count; i++)
        {
            if (modes[i].Value == (value ?? ""))
            {
                index = i;
            }
        }

        return modes[(index + 1) % modes.Count].Value;
    }

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
