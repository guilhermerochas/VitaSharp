using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Launcher;
using VitaSharp.Desktop.Launcher.Ui;

namespace VitaSharp.Tests;

/// <summary>
/// Switching between the light and the dark theme in the launcher (Avalonia's headless platform, a scratch data folder
/// with 80 games). The user saw the switch take long: the whole window was built again (twice until af01cb0d, then
/// once, ~500 ms with 80 games). Now every brush, gradient stop and shadow is bound to how its colour is picked from
/// the palette and the switch only re-colours them: no page is built again, and the window must look exactly like one
/// built fresh in the new theme, while the colours that never follow the theme (overlays on cover art) keep theirs.
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

    private LauncherSettings PrepareLibrary()
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
            // a cover picture on some games: the region badge and the shade over the art are drawn on it
            if (i % 4 == 0)
            {
                File.WriteAllBytes(Path.Combine(game, "sce_sys", "pic0.png"), s_png);
            }
        }

        File.WriteAllText(Path.Combine(data, "launcher.json"), "{ \"CheckUpdates\": false }");
        Environment.SetEnvironmentVariable(EnvVars.Data, data);
        LauncherApp.SetupHeadless();
        var settings = LauncherSettings.Load();
        settings.UiTheme = LauncherSettings.ThemeDark;
        return settings;
    }

    /// <summary>Every brush the window draws with, in tree order: what a viewer sees of its colours.</summary>
    private static List<string> Colours(Window window)
    {
        List<string> colours = new();
        foreach (Visual visual in window.GetVisualDescendants())
        {
            // Avalonia copies the window's transparency fallback (black or white) into this border only when the
            // transparency level changes; the launcher's opaque window background is drawn over it in every theme
            if (visual is Control { Name: "PART_TransparencyFallback" })
            {
                continue;
            }

            foreach (AvaloniaProperty property in AvaloniaPropertyRegistry.Instance.GetRegistered(visual))
            {
                if (property.PropertyType != typeof(IBrush))
                {
                    continue;
                }

                if (visual.GetValue(property) is IBrush brush)
                {
                    colours.Add($"{visual.GetType().Name}{((visual as Control)?.Name is { } name ? "#" + name : "")}.{property.Name}={Describe(brush)}");
                }
            }

            if (visual is Border border && border.BoxShadow.Count > 0)
            {
                colours.Add($"Border.BoxShadow={border.BoxShadow}");
            }
        }

        return colours;
    }

    private static string Describe(IBrush brush)
    {
        return brush switch
        {
            ISolidColorBrush solid => $"{solid.Color}@{solid.Opacity:F2}",
            IGradientBrush gradient => string.Join(",", gradient.GradientStops.Select(s => $"{s.Color}:{s.Offset:F2}")),
            _ => brush.GetType().Name,
        };
    }

    private static List<string> Differences(List<string> switched, List<string> fresh)
    {
        List<string> differences = new();
        int count = Math.Max(switched.Count, fresh.Count);
        for (int i = 0; i < count && differences.Count < 10; i++)
        {
            string left = i < switched.Count ? switched[i] : "(none)";
            string right = i < fresh.Count ? fresh[i] : "(none)";
            if (left != right)
            {
                differences.Add($"#{i}: switched {left} / fresh {right}");
            }
        }

        return differences;
    }

    /// <summary>A window built fresh in the current theme on <paramref name="page"/>, its colours.</summary>
    private static List<string> FreshColours(LauncherSettings settings, string page)
    {
        MainWindow fresh = new(settings) { Width = 1440, Height = 900 };
        fresh.Show();
        try
        {
            Pump();
            if (page != MainWindow.PageLibrary)
            {
                fresh.Navigate(page);
                Pump();
            }

            return Colours(fresh);
        }
        finally
        {
            fresh.Close();
        }
    }

    [Fact]
    public void ASwitchOnlyReColoursTheWindow()
    {
        LauncherSettings settings = PrepareLibrary();
        MainWindow main = new(settings) { Width = 1440, Height = 900 };
        main.Show();
        try
        {
            Pump();
            foreach (string theme in new[] { LauncherSettings.ThemeLight, LauncherSettings.ThemeDark, LauncherSettings.ThemeLight })
            {
                int before = main.Rebuilds;
                var watch = Stopwatch.StartNew();
                main.SetTheme(theme);
                Pump();
                output.WriteLine($"library -> {theme}: {watch.ElapsedMilliseconds} ms");
                Assert.Equal(before, main.Rebuilds);
                Assert.Equal(theme == LauncherSettings.ThemeDark, Palette.Current.Dark);
                List<string> switched = Colours(main);
                List<string> fresh = FreshColours(settings, MainWindow.PageLibrary);
                Assert.True(switched.Count > 500, $"only {switched.Count} brushes found");
                List<string> differences = Differences(switched, fresh);
                Assert.True(differences.Count == 0, string.Join("\n", differences));
            }

            // light now: the shade over cover art keeps its dark colour (the dark theme's sidebar colour, which the
            // light palette has nowhere)
            string coverShade = MainWindow.OverCoverShade.ToString();
            Assert.Contains(Colours(main), c => c.Contains(coverShade, StringComparison.Ordinal));

            // the theme already shown: nothing to draw again
            int same = main.Rebuilds;
            main.SetTheme(LauncherSettings.ThemeLight);
            Pump();
            Assert.Equal(same, main.Rebuilds);

            // from the Settings page's own theme control: the page stays, the sun becomes the moon
            main.Navigate(MainWindow.PageSettings);
            Pump();
            SettingsPage page = main.SettingsPageForTests!;
            var themeChanged = (Action<string>?)typeof(SettingsPage).GetField(nameof(SettingsPage.ThemeChanged),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page);
            int fromSettings = main.Rebuilds;
            var settingsWatch = Stopwatch.StartNew();
            themeChanged!(LauncherSettings.ThemeDark);
            Pump();
            output.WriteLine($"settings -> dark: {settingsWatch.ElapsedMilliseconds} ms");
            Assert.Equal(fromSettings, main.Rebuilds);
            Assert.Same(page, main.SettingsPageForTests);
            Assert.True(Palette.Current.Dark);
            List<string> differencesInSettings = Differences(Colours(main), FreshColours(settings, MainWindow.PageSettings));
            Assert.True(differencesInSettings.Count == 0, string.Join("\n", differencesInSettings));
        }
        finally
        {
            main.Close();
            LauncherApp.ApplyTheme(LauncherSettings.ThemeDark);
        }
    }

    /// <summary>The sidebar switch on each of the other pages (game page with cover art, Vita system, trophies,
    /// captures, install queue): nothing built again and the page looks like one built fresh in the new theme.</summary>
    [Fact]
    public void EveryPageOnlyReColours()
    {
        LauncherSettings settings = PrepareLibrary();
        MainWindow main = new(settings) { Width = 1440, Height = 900 };
        main.Show();
        try
        {
            Pump();
            GameEntry withCover = main.Games.First(g => g.Pic0Path != null);
            foreach (string page in new[] { MainWindow.PageGame, MainWindow.PageSystem, MainWindow.PageTrophies, MainWindow.PageCaptures, MainWindow.PageInstall })
            {
                foreach (string theme in new[] { LauncherSettings.ThemeLight, LauncherSettings.ThemeDark })
                {
                    main.Navigate(page, withCover);
                    Pump();
                    int before = main.Rebuilds;
                    main.SetTheme(theme);
                    Pump();
                    Assert.Equal(before, main.Rebuilds);
                    Assert.Equal(page, main.CurrentPage);
                    List<string> switched = Colours(main);
                    MainWindow fresh = new(settings) { Width = 1440, Height = 900 };
                    fresh.Show();
                    try
                    {
                        Pump();
                        fresh.Navigate(page, fresh.Games.First(g => g.TitleId == withCover.TitleId));
                        Pump();
                        List<string> differences = Differences(switched, Colours(fresh));
                        Assert.True(differences.Count == 0, page + " -> " + theme + "\n" + string.Join("\n", differences));
                    }
                    finally
                    {
                        fresh.Close();
                    }
                }
            }
        }
        finally
        {
            main.Close();
            LauncherApp.ApplyTheme(LauncherSettings.ThemeDark);
        }
    }

    [Fact]
    public void BoundColoursFollowThePaletteAndFixedOnesDoNot()
    {
        LauncherSettings settings = PrepareLibrary();
        LauncherApp.ApplyTheme(LauncherSettings.ThemeDark);
        try
        {
            Kit kit = new();
            var muted = (SolidColorBrush)Kit.B(static p => p.Muted);
            var translucent = (SolidColorBrush)Kit.B(static p => p.Accent, 0.55);
            var themedOpacity = (SolidColorBrush)Kit.B(static p => p.Accent, static p => p.Dark ? 0.05 : 0.04);
            var fixedBrush = (SolidColorBrush)Kit.Fixed(MainWindow.OverCoverShade, 0.55);
            GradientStop scrim = Kit.Stop(static p => p.Scrim, 150, 0.45);
            TextBlock overCover = kit.Text("Region", color: static _ => MainWindow.OverCoverText);
            Border segmentedHost = kit.Segmented(0, _ => { }, ("A", null, null), ("B", null, null));
            Border card = kit.Card(new TextBlock());

            Assert.Equal(Palette.DarkSet.Muted, muted.Color);
            Assert.Equal(default, card.BoxShadow);
            Assert.Equal(Palette.DarkSet.Background, ((ISolidColorBrush)segmentedHost.Background!).Color);

            settings.UiTheme = LauncherSettings.ThemeLight;
            LauncherApp.ApplyTheme(LauncherSettings.ThemeLight);

            Assert.Equal(Palette.LightSet.Muted, muted.Color);
            Assert.Equal(Palette.LightSet.Accent, translucent.Color);
            Assert.Equal(0.55, translucent.Opacity);
            Assert.Equal(Palette.LightSet.Accent, themedOpacity.Color);
            Assert.Equal(0.04, themedOpacity.Opacity);
            Assert.Equal(Color.FromArgb(150, Palette.LightSet.Scrim.R, Palette.LightSet.Scrim.G, Palette.LightSet.Scrim.B), scrim.Color);
            // the segmented control's track is the sidebar colour in the light theme, the background in the dark one
            Assert.Equal(Palette.LightSet.Sidebar, ((ISolidColorBrush)segmentedHost.Background!).Color);
            Assert.NotEqual(default, card.BoxShadow);
            // never re-coloured, though the dark palette has the same colour (Sidebar #0B0E13, Text #E8ECF2)
            Assert.Equal(MainWindow.OverCoverShade, fixedBrush.Color);
            Assert.Equal(0.55, fixedBrush.Opacity);
            Assert.Equal(MainWindow.OverCoverText, ((ISolidColorBrush)overCover.Foreground!).Color);
        }
        finally
        {
            LauncherApp.ApplyTheme(LauncherSettings.ThemeDark);
        }
    }
}
