using Avalonia.Headless;
using Avalonia.Threading;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// <c>ui-render &lt;out-dir&gt; [--size=WxH] [--title=ID] [--theme=dark|light] [--launch=ID|shell] [--seconds=N]</c> (use a
/// scratch --data folder): the real launcher on Avalonia's headless platform (no window on screen), every page saved
/// as a PNG in the dark and light themes, from the data folder's games, trophies, captures and save states.
/// <c>--launch</c> also starts a game (or the Vita system) through the launcher with --headless --seconds=N and reports
/// how the process ended and the play time recorded (<c>--affinity=F0</c> pins it). Settings are not saved.
/// </summary>
static class UiRender
{
    public static int Run(string[] args)
    {
        string outDir = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "ui-render");
        Directory.CreateDirectory(outDir);
        string? Opt(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        var size = Opt("--size")?.Split('x') is [var ws, var hs] && int.TryParse(ws, out int w) && int.TryParse(hs, out int h) ? (w, h) : (1440, 900);
        string? titleId = Opt("--title");
        string? launch = Opt("--launch");
        int seconds = int.TryParse(Opt("--seconds"), out var s) ? s : 20;
        string[] themes = Opt("--theme") is { } t ? [t] : [LauncherSettings.ThemeDark, LauncherSettings.ThemeLight];

        LauncherApp.SetupHeadless();
        int failures = 0;
        foreach (var theme in themes)
        {
            var settings = LauncherSettings.Load();
            settings.UiTheme = theme;
            settings.RecreatedHome = true;
            settings.View = LauncherSettings.ViewGrid;
            var main = new MainWindow(settings) { Width = size.Item1, Height = size.Item2 };
            main.Show();
            void Shot(string name)
            {
                for (int i = 0; i < 4; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
                var frame = main.CaptureRenderedFrame();
                var path = Path.Combine(outDir, $"{name}_{theme}.png");
                if (frame == null) { Console.WriteLine($"  {path}: no frame"); failures++; return; }
                frame.Save(path);
                Console.WriteLine($"  wrote {path}");
            }
            Shot("library");
            settings.View = LauncherSettings.ViewList;
            main.Navigate(MainWindow.PageLibrary);
            Shot("library-list");
            settings.View = LauncherSettings.ViewHome;
            main.Navigate(MainWindow.PageLibrary);
            Shot("library-home");
            settings.View = LauncherSettings.ViewGrid;
            var game = main.Games.FirstOrDefault(g => g.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase)) ?? main.Games.FirstOrDefault();
            if (game != null)
            {
                main.Navigate(MainWindow.PageGame, game);
                Shot("game");
                main.OpenLiveAreaForTests(game);
                Shot("livearea");
            }
            foreach (var page in new[] { MainWindow.PageSystem, MainWindow.PageTrophies, MainWindow.PageCaptures, MainWindow.PageInstall })
            {
                main.Navigate(page);
                Shot(page);
            }
            main.Navigate(MainWindow.PageSettings);
            Shot("settings");
            foreach (var section in new[] { SettingsPage.SectionGraphics, SettingsPage.SectionNetwork, SettingsPage.SectionControls })
            {
                main.SettingsPageForTests!.ShowSection(section);
                Shot("settings-" + section);
            }
            main.Navigate(MainWindow.PageLibrary);

            if (launch != null && theme == themes[0])
            {
                main.ExtraRunOptions = ["--headless", $"--seconds={seconds}"];
                int? exit = null;
                main.EmulatorExited += code => exit = code;
                var before = PlayHistory.Load().GetValueOrDefault(launch)?.Seconds ?? 0;
                if (!main.LaunchForTests(launch)) { Console.WriteLine($"  launch {launch}: not started"); failures++; }
                else if (OperatingSystem.IsWindows() && Opt("--affinity") is { } mask && main.RunningProcessForTests is { } proc)
                {
                    // keep the check inside the CPU budget: half of the cores, below normal priority
                    try { proc.ProcessorAffinity = (nint)Convert.ToInt64(mask, 16); proc.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                if (main.IsRunning)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(seconds + 90);
                    bool shotRunning = false;
                    while (exit == null && DateTime.UtcNow < deadline)
                    {
                        Dispatcher.UIThread.RunJobs();
                        Thread.Sleep(50);
                        if (!shotRunning && main.IsRunning) { Shot("running"); shotRunning = true; }
                    }
                    var after = PlayHistory.Load().GetValueOrDefault(launch)?.Seconds ?? 0;
                    Console.WriteLine($"  launch {launch}: exit {(exit?.ToString() ?? "TIMEOUT")}, play time {before} -> {after} s, log lines {main.LogLines.Count}");
                    foreach (var line in main.LogLines.Where(l => l.Contains("frame", StringComparison.OrdinalIgnoreCase) || l.Contains("fps", StringComparison.OrdinalIgnoreCase)).TakeLast(4))
                        Console.WriteLine("    " + line);
                    if (exit != 0 || after <= before) failures++;
                    Shot("after-launch");
                }
            }
            main.Close();
        }
        Console.WriteLine(failures == 0 ? "PASS" : $"{failures} problem(s)");
        return failures == 0 ? 0 : 1;
    }
}
