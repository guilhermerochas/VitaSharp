using Avalonia.Headless;
using Avalonia.Threading;
using System.Diagnostics;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Launcher;
using VitaSharp.Desktop.Launcher.Ui;

namespace VitaSharp.Tests;

/// <summary>
/// Switching between the light and the dark theme in the launcher (Avalonia's headless platform, a scratch data folder
/// with 80 games): the window is drawn again once per switch, from the library and from Settings, and not at all when
/// the theme picked is the one already shown. It was drawn twice (the theme setter and the palette change each rebuilt
/// it), which doubled the wait the user saw.
/// </summary>
[Collection("launcher")]
public sealed class ThemeSwitchTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private static readonly byte[] s_png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-theme-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedData = Environment.GetEnvironmentVariable(EnvVars.Data);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVars.Data, _savedData);
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

    [Fact]
    public void ASwitchDrawsTheWindowAgainOnce()
    {
        string data = Path.Combine(_root, "data");
        for (int i = 0; i < 80; i++)
        {
            string id = $"PCSB{i:D5}";
            string game = Path.Combine(data, "fs", "ux0", "app", id);
            Directory.CreateDirectory(Path.Combine(game, "sce_sys"));
            File.WriteAllBytes(Path.Combine(game, "eboot.bin"), new byte[64]);
            DesktopLogicTests.WriteSfo(Path.Combine(game, "sce_sys", "param.sfo"), ("APP_VER", "01.00"), ("CATEGORY", "gd"), ("TITLE", "Game " + i), ("TITLE_ID", id));
            File.WriteAllBytes(Path.Combine(game, "sce_sys", "icon0.png"), s_png);
        }

        File.WriteAllText(Path.Combine(data, "launcher.json"), "{ \"CheckUpdates\": false }");
        Environment.SetEnvironmentVariable(EnvVars.Data, data);
        LauncherApp.SetupHeadless();
        var settings = LauncherSettings.Load();
        settings.UiTheme = LauncherSettings.ThemeDark;
        var main = new MainWindow(settings) { Width = 1440, Height = 900 };
        main.Show();
        try
        {
            Pump();
            foreach (string theme in new[] { LauncherSettings.ThemeLight, LauncherSettings.ThemeDark })
            {
                int before = main.Rebuilds;
                var watch = Stopwatch.StartNew();
                main.SetTheme(theme);
                Pump();
                output.WriteLine($"library -> {theme}: {watch.ElapsedMilliseconds} ms");
                Assert.Equal(before + 1, main.Rebuilds);
                Assert.Equal(theme == LauncherSettings.ThemeDark, Palette.Current.Dark);
            }

            // the theme already shown: nothing to draw again
            int same = main.Rebuilds;
            main.SetTheme(LauncherSettings.ThemeDark);
            Pump();
            Assert.Equal(same, main.Rebuilds);

            // from the Settings page's own theme control
            main.Navigate(MainWindow.PageSettings);
            Pump();
            SettingsPage page = main.SettingsPageForTests!;
            var themeChanged = (Action<string>?)typeof(SettingsPage).GetField(nameof(SettingsPage.ThemeChanged),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page);
            int fromSettings = main.Rebuilds;
            themeChanged!(LauncherSettings.ThemeLight);
            Pump();
            Assert.Equal(fromSettings + 1, main.Rebuilds);
            Assert.False(Palette.Current.Dark);
        }
        finally
        {
            main.Close();
            LauncherApp.ApplyTheme(LauncherSettings.ThemeDark);
        }
    }
}
