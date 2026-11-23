using VitaSharp.Core.Boot;
using VitaSharp.Core.Localization;
using VitaSharp.Desktop.Launcher;
using VitaSharp.Mobile.Core.Settings;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// "Resolucao interna" as a global default (desktop Configuracoes > Graficos, Android Ajustes) with a game's own choice
/// winning over it: a game without its own choice starts at the global one, a game with its own (native included)
/// at its own, old settings files keep meaning what they meant, and the loading screen and the session log say the
/// resolution the game starts with.
/// </summary>
public sealed class InternalResolutionDefaultTests
{
    private const string Game = "PCSB00001";

    private static string? ResOption(LauncherSettings settings, string titleId) =>
        settings.RunOptions(titleId).SingleOrDefault(o => o.StartsWith("--res-scale", StringComparison.Ordinal));

    [Fact]
    public void Desktop_a_game_without_its_own_choice_starts_at_the_global_resolution()
    {
        var settings = new LauncherSettings { ResScale = 2 };
        Assert.Equal("--res-scale=2", ResOption(settings, Game));
        Assert.Equal(2, settings.EffectiveResScale(Game));

        // a game with other options of its own but no resolution follows the global one too
        settings.Games[Game] = new GameOptions { FrameRate = "60" };
        Assert.Equal("--res-scale=2", ResOption(settings, Game));
    }

    [Fact]
    public void Desktop_a_games_own_choice_wins_over_the_global_one_native_included()
    {
        var settings = new LauncherSettings { ResScale = 3 };
        settings.Games[Game] = new GameOptions { ResScale = 4 };
        Assert.Equal("--res-scale=4", ResOption(settings, Game));

        settings.Games[Game].ResScale = 1;
        Assert.Null(ResOption(settings, Game));
        Assert.Equal(1, settings.EffectiveResScale(Game));
        Assert.False(settings.Games[Game].IsDefault);
    }

    [Fact]
    public void Desktop_native_by_default_and_an_invalid_global_value_is_native()
    {
        Assert.Null(ResOption(new LauncherSettings(), Game));
        Assert.Null(ResOption(new LauncherSettings { ResScale = 9 }, Game));
        Assert.True(new GameOptions().IsDefault);
    }

    [Fact]
    public void Desktop_a_games_native_choice_survives_saving_and_loading()
    {
        var settings = new LauncherSettings { ResScale = 2 };
        settings.Games[Game] = new GameOptions { ResScale = 1 };
        settings.Games["PCSB00002"] = new GameOptions { FrameRate = "30" };
        string json = System.Text.Json.JsonSerializer.Serialize(settings, LauncherJson.Default.LauncherSettings);

        LauncherSettings loaded = LauncherSettings.FromJson(json)!;
        Assert.Equal(2, loaded.ResScale);
        Assert.Equal(1, loaded.Games[Game].ResScale);
        Assert.Null(loaded.Games["PCSB00002"].ResScale);
        Assert.Null(ResOption(loaded, Game));
        Assert.Equal("--res-scale=2", ResOption(loaded, "PCSB00002"));
    }

    [Fact]
    public void Desktop_a_file_from_before_the_global_option_keeps_its_meaning()
    {
        // then every game entry carried "ResScale": 1 (native, the only default) unless its resolution was chosen
        const string OldFile = "{ \"CheckUpdates\": false, \"Games\": { \"PCSB00001\": { \"ResScale\": 1, \"FrameRate\": \"60\" },"
            + " \"PCSB00002\": { \"ResScale\": 3, \"FrameRate\": \"native\" } } }";
        LauncherSettings loaded = LauncherSettings.FromJson(OldFile)!;
        Assert.Equal(1, loaded.ResScale);
        Assert.Null(loaded.Games[Game].ResScale); // the old 1 was the default: it follows the global one now
        Assert.Equal(3, loaded.Games["PCSB00002"].ResScale); // a real choice stays

        loaded.ResScale = 2;
        Assert.Equal("--res-scale=2", ResOption(loaded, Game));
        Assert.Equal("--res-scale=3", ResOption(loaded, "PCSB00002"));
    }

    [Fact]
    public void Desktop_the_game_page_offers_the_global_default_first_with_its_value()
    {
        using var language = new LanguageScope(Loc.English);
        (int? Factor, string Label)[] choices = GameOptions.ResScalesWithDefault(2);
        Assert.Null(choices[0].Factor);
        Assert.Equal(Loc.Format("MDefaultParen", GameOptions.ResScales[1].Label), choices[0].Label);
        Assert.Equal(GameOptions.ResScales.Select(c => (int?)c.Factor), choices.Skip(1).Select(c => c.Factor));
    }

    [Fact]
    public void Android_a_game_without_its_own_choice_starts_at_the_global_resolution()
    {
        var app = new AppPreferences { ResScale = "2" };
        Assert.Equal(2f, EffectiveSettings.Resolve(app, null).ResScaleValue);
        Assert.Equal(2f, EffectiveSettings.Resolve(app, new GameSettings { FrameRate = "60" }).ResScaleValue);
        Assert.Equal(0f, EffectiveSettings.Resolve(new AppPreferences(), null).ResScaleValue); // native by default
        Assert.Equal(0f, EffectiveSettings.Resolve(new AppPreferences { ResScale = "8" }, null).ResScaleValue); // invalid: native
    }

    [Fact]
    public void Android_a_games_own_choice_wins_over_the_global_one_native_included()
    {
        var app = new AppPreferences { ResScale = "3" };
        Assert.Equal(0.5f, EffectiveSettings.Resolve(app, new GameSettings { ResScale = "0.5" }).ResScaleValue);
        var native = new GameSettings { ResScale = "" };
        Assert.Equal(0f, EffectiveSettings.Resolve(app, native).ResScaleValue);
        Assert.True(native.HasOverrides);
        Assert.Equal(3f, EffectiveSettings.Resolve(app, new GameSettings { ResScale = "8" }).ResScaleValue); // invalid: the global one
    }

    [Fact]
    public void Android_the_global_choice_is_saved_and_a_games_native_choice_too()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-resdefault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            new AppPreferences { ResScale = "2" }.Save(Path.Combine(dir, "app.json"));
            Assert.Equal("2", AppPreferences.Load(Path.Combine(dir, "app.json")).ResScale);
            new GameSettings { ResScale = "" }.Save(Path.Combine(dir, "game.json"));
            Assert.Equal("", GameSettings.Load(Path.Combine(dir, "game.json")).ResScale);
            new GameSettings().Save(Path.Combine(dir, "other.json"));
            Assert.Null(GameSettings.Load(Path.Combine(dir, "other.json")).ResScale);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(2f, "2x (1920\u00D71088)")]
    [InlineData(4f, "4x (3840\u00D72176)")]
    [InlineData(1f, "1x (960\u00D7544)")]
    [InlineData(0f, "1x (960\u00D7544)")]
    [InlineData(0.75f, "75% (720\u00D7408)")]
    public void The_loading_screen_says_the_resolution_the_game_starts_with(float scale, string shown)
    {
        using var language = new LanguageScope(Loc.English);
        Assert.Equal("Internal resolution: " + shown, LoadingScreen.ResolutionDetail(scale));
        Assert.Equal(LoadingScreen.ResolutionDetail(scale), new LoadingScreen("A game", detail: LoadingScreen.ResolutionDetail(scale)).Detail);
        Assert.Null(new LoadingScreen("A game").Detail);
    }

    [Theory]
    [InlineData(1f, "Internal resolution: native (960x544)")]
    [InlineData(2f, "Internal resolution: 2x (1920x1088)")]
    [InlineData(0.5f, "Internal resolution: 0.5x (480x272)")]
    public void The_session_log_says_the_resolution_the_renderer_runs_at(float scale, string line)
    {
        Assert.Equal(line, VulkanRenderer.InternalResolutionLine(scale));
    }
}
