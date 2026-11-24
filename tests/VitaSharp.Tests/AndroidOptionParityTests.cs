using VitaSharp.Desktop.Launcher;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Tests;

/// <summary>
/// Options that exist in the desktop launcher and in the Android app (README, "Options on both apps"): the same
/// default and the same emulator setting on both sides. The Android side is <see cref="AppPreferences"/>; the desktop
/// side is what <see cref="LauncherSettings.RunOptions"/> passes to the game.
/// </summary>
public sealed class AndroidOptionParityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vitasharp-option-parity-" + Guid.NewGuid().ToString("N"));

    public AndroidOptionParityTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, true);
    }

    [Fact]
    public void ShaderOptionsHaveTheDesktopDefaults()
    {
        var android = new AppPreferences();
        var desktop = new LauncherSettings().RunOptions().ToList();
        Assert.True(android.FastShaderCompile);
        Assert.True(android.BackgroundShaderCompile);
        Assert.Contains($"--shaderc-opt={android.ShaderOptimization}", desktop);
        Assert.Contains("--async-shaders=on", desktop);
    }

    [Fact]
    public void ShaderOptionsTurnedOffMeanTheSameOnBothSides()
    {
        var android = new AppPreferences { FastShaderCompile = false, BackgroundShaderCompile = false };
        var desktop = new LauncherSettings { FastShaderCompile = false, BackgroundShaderCompile = false }.RunOptions().ToList();
        Assert.Equal("perf", android.ShaderOptimization);
        Assert.Contains($"--shaderc-opt={android.ShaderOptimization}", desktop);
        Assert.Contains("--async-shaders=off", desktop);
    }

    [Fact]
    public void ShaderOptionsSurviveTheSettingsFileAndOldFilesGetTheDefaults()
    {
        string path = Path.Combine(_folder, "app.json");
        File.WriteAllText(path, "{\"VSync\":true}"); // a settings file from before these options
        var old = AppPreferences.Load(path);
        Assert.True(old.FastShaderCompile);
        Assert.True(old.BackgroundShaderCompile);

        new AppPreferences { FastShaderCompile = false, BackgroundShaderCompile = false }.Save(path);
        var saved = AppPreferences.Load(path);
        Assert.False(saved.FastShaderCompile);
        Assert.False(saved.BackgroundShaderCompile);
        Assert.DoesNotContain("ShaderOptimization", File.ReadAllText(path));
    }

    [Fact]
    public void EveryDesktopInternalResolutionIsOfferedOnAndroidWithTheSameScale()
    {
        foreach ((int factor, string _) in GameOptions.ResScales)
        {
            if (factor == 1)
            {
                continue; // native: the empty value on both sides
            }

            string value = factor.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains(SettingsChoices.ResScales, c => c.Value == value);
            var effective = EffectiveSettings.Resolve(new AppPreferences(), new GameSettings { ResScale = value });
            Assert.Equal(factor, effective.ResScaleValue);
            var desktop = new LauncherSettings { Games = { ["PCSB00001"] = new GameOptions { ResScale = factor } } };
            Assert.Contains($"--res-scale={factor}", desktop.RunOptions("PCSB00001"));
            // the global default the same way on both
            Assert.Equal(factor, EffectiveSettings.Resolve(new AppPreferences { ResScale = value }, null).ResScaleValue);
            Assert.Contains($"--res-scale={factor}", new LauncherSettings { ResScale = factor }.RunOptions("PCSB00001"));
        }
    }

    [Fact]
    public void StartupViewHasTheDesktopValuesAndDefault()
    {
        Assert.Equal(new LauncherSettings().StartupView, new AppPreferences().StartupView);
        Assert.Equal(LauncherSettings.StartLibrary, StartupViews.Library);
        Assert.Equal(LauncherSettings.StartSystem, StartupViews.System);
        Assert.Equal([StartupViews.Library, StartupViews.System], StartupViews.Choices.Select(c => c.Value));
    }

    [Theory]
    [InlineData("system", true, false, false, true)]
    [InlineData("library", true, false, false, false)]
    [InlineData("system", false, false, false, false)] // no firmware shell: the screens
    [InlineData("system", true, true, false, false)] // the activity restored by Android: no second boot
    [InlineData("system", true, false, true, false)] // a shortcut or adb opens a game instead
    [InlineData(null, true, false, false, false)]
    public void TheSystemOpensOnlyOnARealStartWithTheShellInstalled(string? setting, bool shell, bool restored, bool game, bool expected)
    {
        Assert.Equal(expected, StartupViews.OpensSystem(setting, shell, restored, game));
    }

    [Fact]
    public void StartupViewSurvivesTheSettingsFile()
    {
        string path = Path.Combine(_folder, "app.json");
        File.WriteAllText(path, "{\"VSync\":true}");
        Assert.Equal(StartupViews.Library, AppPreferences.Load(path).StartupView);
        new AppPreferences { StartupView = StartupViews.System }.Save(path);
        Assert.Equal(StartupViews.System, AppPreferences.Load(path).StartupView);
    }

    [Fact]
    public void KernelPluginsOptionExistsOnBothSidesAndIsOffByDefault()
    {
        Assert.False(new AppPreferences().KernelPlugins);
        Assert.False(new LauncherSettings().KernelPlugins);
        Assert.Contains("--kernel-plugins=off", new LauncherSettings().RunOptions());
        Assert.Contains("--kernel-plugins=on", new LauncherSettings { KernelPlugins = true }.RunOptions());
    }

    /// <summary>Every desktop option, by where Android has it: the same property on <see cref="AppPreferences"/>
    /// (checked for the same default), elsewhere on Android (the reason says where), or desktop-only (the reason says
    /// why). A new launcher option fails <see cref="EveryDesktopOptionIsOnAndroidOrExplained"/> until it is added to the
    /// app or placed here: every option of the PC is on Android in the same delivery.</summary>
    private static readonly string[] s_sameOnAndroid =
    [
        nameof(LauncherSettings.VSync), nameof(LauncherSettings.CameraFront), nameof(LauncherSettings.CameraBack),
        nameof(LauncherSettings.CpuBackend), nameof(LauncherSettings.CpuMhz), nameof(LauncherSettings.FastLoading), nameof(LauncherSettings.ResScale),
        nameof(LauncherSettings.FastShaderCompile), nameof(LauncherSettings.BackgroundShaderCompile),
        nameof(LauncherSettings.LoadShaderCacheBeforePlaying), nameof(LauncherSettings.PrecompileCode), nameof(LauncherSettings.DebugMode),
        nameof(LauncherSettings.KernelPlugins), nameof(LauncherSettings.StartupView), nameof(LauncherSettings.TrophySound),
        nameof(LauncherSettings.Language),
    ];

    private static readonly Dictionary<string, string> s_elsewhereOnAndroid = new()
    {
        [nameof(LauncherSettings.PsnSignedIn)] = "AppPreferences.Network (Ajustes > Conta PSN e rede)",
        [nameof(LauncherSettings.PsnOnlineId)] = "AppPreferences.Network",
        [nameof(LauncherSettings.PsnLocalServices)] = "AppPreferences.Network",
        [nameof(LauncherSettings.RealInternet)] = "AppPreferences.Network",
        [nameof(LauncherSettings.RpcnServer)] = "AppPreferences.Network",
        [nameof(LauncherSettings.RpcnUser)] = "AppPreferences.Network",
        [nameof(LauncherSettings.RpcnToken)] = "AppPreferences.Network",
        [nameof(LauncherSettings.RpcnPassword)] = "the Android Keystore (AppServices.RpcnPasswordAsync)",
        [nameof(LauncherSettings.CapturePreRoll)] = "the pause menu's Pre-gravacao (EmulationSession.PreRollSeconds)",
        [nameof(LauncherSettings.Games)] = "GameSettings, one file per game (EveryDesktopGameOptionIsOnAndroidOrExplained)",
        [nameof(LauncherSettings.Microphone)] = "MicrophoneSettings (Ajustes > Microfone)",
        [nameof(LauncherSettings.Location)] = "LocationSettings (Ajustes > Localizacao)",
    };

    private static readonly Dictionary<string, string> s_desktopOnly = new()
    {
        [nameof(LauncherSettings.Fullscreen)] = "the game window of a PC (the phone is always full screen)",
        [nameof(LauncherSettings.Scale)] = "the game window's size on a PC",
        [nameof(LauncherSettings.Validation)] = "Vulkan validation layers, a PC developer tool",
        [nameof(LauncherSettings.ShowLog)] = "the launcher's log panel (Android: Ajustes > Depuracao shares the log)",
        [nameof(LauncherSettings.Camera)] = "an old launcher.json key, migrated to CameraFront/CameraBack",
        [nameof(LauncherSettings.PrecompileShaders)] = "an old launcher.json key, migrated to LoadShaderCacheBeforePlaying",
        [nameof(LauncherSettings.HleModules)] = "a PC developer option (--hle-modules)",
        [nameof(LauncherSettings.View)] = "the launcher's library views (grid/list/recreated home)",
        [nameof(LauncherSettings.RecreatedHome)] = "the launcher's provisional recreated home screen",
        [nameof(LauncherSettings.GameClick)] = "the launcher's recreated LiveArea",
        [nameof(LauncherSettings.Theme)] = "the launcher's recreated home screen theme",
        [nameof(LauncherSettings.HomeBgm)] = "the launcher's recreated home screen",
        [nameof(LauncherSettings.SystemSounds)] = "the launcher's recreated home screen",
        [nameof(LauncherSettings.UiTheme)] = "the launcher's light/dark theme (the app follows its dark mockups)",
        [nameof(LauncherSettings.LibrarySort)] = "the launcher's library sort",
        [nameof(LauncherSettings.LibraryRegion)] = "the launcher's library region filter",
        [nameof(LauncherSettings.CheckUpdates)] = "the launcher's updater (the APK updates through its install)",
        [nameof(LauncherSettings.UpdateChannel)] = "the launcher's updater",
        [nameof(LauncherSettings.SkippedUpdate)] = "the launcher's updater",
        [nameof(LauncherSettings.AudioDevice)] = "an OpenAL output of a PC (the phone routes audio itself)",
        [nameof(LauncherSettings.Model)] = "the PlayStation TV model option; its Android side (controllers on the phone) is the roadmap's PS TV item",
    };

    /// <summary>Per-game options: the same property on <see cref="GameSettings"/> or desktop-only.</summary>
    private static readonly string[] s_sameOnAndroidPerGame =
    [
        nameof(GameOptions.ResScale), nameof(GameOptions.FrameRate), nameof(GameOptions.FastLoading),
        nameof(GameOptions.LoadShaderCacheBeforePlaying), nameof(GameOptions.PrecompileCode), nameof(GameOptions.SystemLanguage),
    ];

    private static readonly Dictionary<string, string> s_desktopOnlyPerGame = new()
    {
        [nameof(GameOptions.PrecompileShaders)] = "an old launcher.json key, migrated to LoadShaderCacheBeforePlaying",
    };

    private static IEnumerable<string> SettableOptions(Type type) =>
        type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetSetMethod() != null)
            .Select(p => p.Name);

    [Fact]
    public void EveryDesktopOptionIsOnAndroidOrExplained()
    {
        var missing = new List<string>();
        foreach (string name in SettableOptions(typeof(LauncherSettings)))
        {
            if (s_sameOnAndroid.Contains(name))
            {
                if (typeof(AppPreferences).GetProperty(name) == null)
                {
                    missing.Add(name + " (listed as on Android, but AppPreferences has no such option)");
                }
            }
            else if (!s_elsewhereOnAndroid.ContainsKey(name) && !s_desktopOnly.ContainsKey(name))
            {
                missing.Add(name);
            }
        }

        Assert.True(missing.Count == 0, "Desktop options missing on Android (add them to the app, or explain them in this test): " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryDesktopGameOptionIsOnAndroidOrExplained()
    {
        var missing = new List<string>();
        foreach (string name in SettableOptions(typeof(GameOptions)))
        {
            if (s_sameOnAndroidPerGame.Contains(name))
            {
                if (typeof(GameSettings).GetProperty(name) == null)
                {
                    missing.Add(name + " (listed as on Android, but GameSettings has no such option)");
                }
            }
            else if (!s_desktopOnlyPerGame.ContainsKey(name))
            {
                missing.Add(name);
            }
        }

        Assert.True(missing.Count == 0, "Per-game desktop options missing on Android (add them to GameSettings, or explain them in this test): " + string.Join(", ", missing));
    }

    /// <summary>The options that are the same on both sides start the same (Language: "" on Android and "system" on the
    /// desktop both mean the system's language).</summary>
    [Fact]
    public void TheSharedOptionsHaveTheSameDefaults()
    {
        var desktop = new LauncherSettings();
        var android = new AppPreferences();
        foreach (string name in s_sameOnAndroid)
        {
            if (name == nameof(LauncherSettings.Language))
            {
                continue;
            }

            if (name == nameof(LauncherSettings.ResScale))
            {
                // native on both, written as the factor 1 on the desktop and as the empty choice on Android
                Assert.Equal(1, desktop.ResScale);
                Assert.Equal(0f, EffectiveSettings.Resolve(android, null).ResScaleValue);
                continue;
            }

            object? desktopValue = typeof(LauncherSettings).GetProperty(name)!.GetValue(desktop);
            object? androidValue = typeof(AppPreferences).GetProperty(name)!.GetValue(android);
            Assert.True(Equals(desktopValue, androidValue), $"{name}: desktop {desktopValue}, Android {androidValue}");
        }

        Assert.Equal(desktop.PsnSignedIn, android.Network.PsnSignedIn);
        Assert.Equal(desktop.PsnLocalServices, android.Network.PsnLocalServices);
        Assert.Equal(desktop.RealInternet, android.Network.RealInternet);
        Assert.Equal(desktop.PsnOnlineId, android.Network.PsnOnlineId);
    }
}
