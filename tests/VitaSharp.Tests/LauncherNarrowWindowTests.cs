using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Launcher;
using VitaSharp.Desktop.Launcher.Ui;

namespace VitaSharp.Tests;

/// <summary>
/// The launcher in a narrow window (Avalonia's headless platform, a scratch data folder with one game): at the
/// smallest window width and two widths above it, in both interface languages (pt-BR has the longest texts) and both
/// themes, every page (library views, the game page, Vita system, trophies, captures, install, settings and each of
/// its sections) must fit: no control sticks out sideways past the card or panel holding it, and no button label is
/// cut (<see cref="LayoutAudit"/>). Rows of buttons wrap to the next line, long texts wrap, two-column pages become
/// one column. The user saw "Uninstall firmware" cut in half on the Vita system page at ~1500 px.
/// </summary>
[Collection("launcher")]
public sealed class LauncherNarrowWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-narrow-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedData = Environment.GetEnvironmentVariable(EnvVars.Data);
    private readonly Func<string?> _savedFindVita3K = FirmwarePanel.FindVita3K;

    // the launcher switches the process-wide language to the one each case asks for: put back the one before
    private readonly LanguageScope _language = new();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVars.Data, _savedData);
        _language.Dispose();
        FirmwarePanel.FindVita3K = _savedFindVita3K;
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

    /// <summary>A setting row with a wide control (the "Manter em HLE" text field) keeps its title and detail at least
    /// 200 px wide: in a narrow card the control goes below the texts, and back beside them when there is room.</summary>
    [Fact]
    public void ASettingRowsWideControlGoesBelowItsTextsWhenNarrow()
    {
        LauncherApp.SetupHeadless();
        var kit = new Kit();
        TextBox field = kit.Field("", _ => { }, 240);
        Control row = kit.Row("box", "Keep on HLE", "Libraries that stay on VitaSharp's implementation even when the game brings the module.", field);
        var host = new Border { Child = row };
        var window = new Window { Content = host, Width = 900, Height = 300 };
        window.Show();
        try
        {
            host.Width = 420;
            Pump();
            Assert.Equal(1, Grid.GetRow(field));
            Assert.Empty(LayoutAudit.Check(window));
            Assert.True(field.Bounds.Width >= 240, $"field width {field.Bounds.Width}");

            host.Width = 860;
            Pump();
            Assert.Equal(0, Grid.GetRow(field));
            Assert.Empty(LayoutAudit.Check(window));
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData(Loc.Portuguese, LauncherSettings.ThemeDark)]
    [InlineData(Loc.Portuguese, LauncherSettings.ThemeLight)]
    [InlineData(Loc.English, LauncherSettings.ThemeDark)]
    [InlineData(Loc.English, LauncherSettings.ThemeLight)]
    public void EveryPageFitsANarrowWindow(string language, string theme)
    {
        string data = Path.Combine(_root, "data");
        string game = Path.Combine(data, "fs", "ux0", "app", "PCSB00123");
        Directory.CreateDirectory(Path.Combine(game, "sce_sys"));
        File.WriteAllBytes(Path.Combine(game, "eboot.bin"), new byte[64]);
        DesktopLogicTests.WriteSfo(Path.Combine(game, "sce_sys", "param.sfo"), ("APP_VER", "01.00"), ("CATEGORY", "gd"),
            ("TITLE", "A Game With A Rather Long Title: The Definitive Edition"), ("TITLE_ID", "PCSB00123"));
        // system fonts and a Vita3K installation: the firmware card shows all of its buttons, as on the user's PC
        string fonts = Path.Combine(data, "fs", "sa0", "data", "font", "pgf");
        Directory.CreateDirectory(fonts);
        File.WriteAllBytes(Path.Combine(fonts, "ltn0.pgf"), new byte[16]);
        FirmwarePanel.FindVita3K = () => Path.Combine(_root, "vita3k");
        File.WriteAllText(Path.Combine(data, "launcher.json"), "{ \"CheckUpdates\": false }");
        Environment.SetEnvironmentVariable(EnvVars.Data, data);
        LauncherApp.SetupHeadless();

        List<string> problems = new();
        // the smallest window, widths where the two-column pages have one column, and widths just past the switch to two
        foreach (int width in new[] { 1000, 1100, 1180, 1250, 1300 })
        {
            var settings = LauncherSettings.Load();
            settings.UiTheme = theme;
            settings.Language = language;
            settings.RecreatedHome = true;
            settings.View = LauncherSettings.ViewGrid;
            MainWindow main = new(settings) { Width = width, Height = 760 };
            main.Show();
            try
            {
                void Audit(string page)
                {
                    Pump();
                    foreach (string problem in LayoutAudit.Check(main))
                    {
                        problems.Add($"{width} px, {page}: {problem}");
                    }
                }

                Audit("library");
                settings.View = LauncherSettings.ViewList;
                main.Navigate(MainWindow.PageLibrary);
                Audit("library-list");
                settings.View = LauncherSettings.ViewGrid;
                main.Navigate(MainWindow.PageGame, main.Games.First());
                Audit("game");
                foreach (string page in new[] { MainWindow.PageSystem, MainWindow.PageTrophies, MainWindow.PageCaptures, MainWindow.PageInstall, MainWindow.PageSettings })
                {
                    main.Navigate(page);
                    Audit(page);
                }

                foreach (string section in new[]
                {
                    SettingsPage.SectionGeneral, SettingsPage.SectionGraphics, SettingsPage.SectionControls, SettingsPage.SectionEmulation,
                    SettingsPage.SectionAudio, SettingsPage.SectionNetwork, SettingsPage.SectionAdvanced, SettingsPage.SectionSystem,
                })
                {
                    main.SettingsPageForTests!.ShowSection(section);
                    Audit("settings-" + section);
                }
            }
            finally
            {
                main.Close();
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} layout problem(s) in {language}/{theme}:\n" + string.Join("\n", problems.Distinct().Take(80)));
    }
}
