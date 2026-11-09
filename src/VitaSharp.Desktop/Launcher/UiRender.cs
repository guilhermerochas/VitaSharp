using Avalonia.Headless;
using Avalonia.Threading;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// <c>ui-render &lt;out-dir&gt; [--size=WxH] [--title=ID] [--theme=dark|light] [--launch=ID|shell] [--seconds=N] [--install-pkg=P --license=zRIF]</c> (use a
/// scratch --data folder): the real launcher on Avalonia's headless platform (no window on screen), every page saved
/// as a PNG in the dark and light themes, from the data folder's games, trophies, captures and save states.
/// <c>--launch</c> also starts a game (or the Vita system) through the launcher with --headless --seconds=N and reports
/// how the process ended and the play time recorded (<c>--affinity=F0</c> pins it); with <c>--focus-check</c> it runs
/// in its window instead and "Ir para o jogo" must restore it (see <see cref="FocusCheck"/>);<c>--install-pkg=P --license=zRIF</c>
/// installs a package through the license dialog (see <see cref="InstallPkg"/>); <c>--update-feed=&lt;folder|URL&gt;</c>
/// renders the update dialog for the newest release of that feed. Settings are not saved.
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
            if (Opt("--lang") is { } lang) settings.Language = lang;
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
                main.ShowGameFoldersForTests();
                Shot("game-folders");
                main.Navigate(MainWindow.PageGame, game);
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
            foreach (var section in new[] { SettingsPage.SectionGraphics, SettingsPage.SectionEmulation, SettingsPage.SectionNetwork, SettingsPage.SectionControls })
            {
                main.SettingsPageForTests!.ShowSection(section);
                Shot("settings-" + section);
            }
            main.Navigate(MainWindow.PageLibrary);

            if (Opt("--install-pkg") is { } pkg && theme == themes[0])
                failures += InstallPkg(main, Path.GetFullPath(pkg), Opt("--license") ?? "", outDir, theme, Shot);

            if (Opt("--update-feed") is { } feed && Updater.Feed(feed) is { } feedUri)
            {
                var release = Updater.CheckAsync(Opt("--channel") ?? Core.Update.UpdateFeed.ChannelBuilds, feedUri).GetAwaiter().GetResult();
                if (release == null) { Console.WriteLine($"  update-feed: no update for {Updater.Current.Display}"); failures++; }
                else
                {
                    var dlg = main.UpdateDialog(release);
                    dlg.Show(main);
                    for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
                    var path = Path.Combine(outDir, $"update-dialog_{theme}.png");
                    dlg.CaptureRenderedFrame()?.Save(path);
                    Console.WriteLine($"  update-feed: {Updater.Current.Display} -> {release.Tag}; wrote {path}");
                    dlg.Close();
                }
            }

            if (launch != null && theme == themes[0] && args.Contains("--focus-check"))
            {
                failures += FocusCheck(main, launch, seconds, Shot);
                main.Close();
                continue;
            }
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

    /// <summary>
    /// <c>--launch=ID --focus-check</c>: "Ir para o jogo". The game starts in its window (not headless; run it with
    /// VITASHARP_HIDDEN_WINDOW=1, on a desktop nobody looks at), the window is minimized, then "Jogar" again (the
    /// re-start path of the cover, the LiveArea and the sidebar button) and the "Ir para o jogo" button must restore it;
    /// each step's window state is printed. The game is closed after <paramref name="seconds"/> at most.
    /// </summary>
    static int FocusCheck(MainWindow main, string launch, int seconds, Action<string> shot)
    {
        main.ExtraRunOptions = [];
        int? exit = null;
        main.EmulatorExited += code => exit = code;
        if (!main.LaunchForTests(launch) || main.RunningProcessForTests is not { } proc) { Console.WriteLine($"  focus {launch}: not started"); return 1; }
        if (OperatingSystem.IsWindows())
        {
            try { proc.ProcessorAffinity = (nint)0xF0; proc.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        void Pump(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); } }
        int failures = 0;
        nint hwnd = 0;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(seconds, 30));
        while (hwnd == 0 && exit == null && DateTime.UtcNow < deadline) { Pump(250); hwnd = GameWindowActivator.FindWindow(proc.Id); }
        if (hwnd == 0) { Console.WriteLine($"  focus {launch}: no game window (exit {exit?.ToString() ?? "-"})"); failures++; }
        else
        {
            Pump(3000); // the game draws its first frames
            Console.WriteLine($"  focus {launch}: window 0x{hwnd:X} {GameWindowActivator.Describe(hwnd)}");
            foreach (var (name, act) in new (string, Action)[] { ("Jogar again", () => main.LaunchForTests(launch)), ("Ir para o jogo", main.GoToGame) })
            {
                GameWindowActivator.MinimizeForTests(hwnd);
                Pump(300);
                string before = GameWindowActivator.Describe(hwnd);
                act();
                Pump(300);
                string after = GameWindowActivator.Describe(hwnd);
                bool ok = before.StartsWith("minimized") && !after.StartsWith("minimized") && main.LastFocusResultForTests != null;
                Console.WriteLine($"  focus {launch}: {name}: {before} -> {after}; {main.LastFocusResultForTests} [{(ok ? "ok" : "FAIL")}]");
                if (!ok) failures++;
            }
            shot("focus-running");
        }
        if (main.RunningProcessForTests is { } p)
        {
            try { if (!p.CloseMainWindow()) p.Kill(true); } catch (InvalidOperationException) { }
            var end = DateTime.UtcNow.AddSeconds(30);
            while (exit == null && DateTime.UtcNow < end) Pump(100);
            if (exit == null) { try { p.Kill(true); } catch (InvalidOperationException) { } Pump(1000); }
        }
        Console.WriteLine($"  focus {launch}: game exit {exit?.ToString() ?? "killed"}");
        return failures;
    }

    /// <summary>
    /// <c>--install-pkg=&lt;pkg&gt; --license=&lt;zRIF&gt;</c>: the "Escolher pkg" flow after the file picker. The license
    /// dialog opens, the zRIF is typed in its field and "Instalar" pressed; the queue then installs the package into
    /// the --data folder, and the .rif in ux0:license and the work.bin are checked against the decoded zRIF.
    /// </summary>
    static int InstallPkg(MainWindow main, string pkg, string zrif, string outDir, string theme, Action<string> shot)
    {
        main.Navigate(MainWindow.PageInstall);
        bool dialogShown = false;
        main.LicenseDialogForTests = (dlg, field, submit) =>
        {
            dialogShown = true;
            field.Text = zrif;
            for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
            var path = Path.Combine(outDir, $"license-dialog_{theme}.png");
            dlg.CaptureRenderedFrame()?.Save(path);
            Console.WriteLine($"  wrote {path}");
            submit();
        };
        var asked = main.AskLicenseAndQueue(pkg);
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (!asked.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        main.LicenseDialogForTests = null;
        if (!dialogShown || !asked.IsCompleted || !asked.Result) { Console.WriteLine($"  install-pkg: the license dialog did not accept the zRIF (shown {dialogShown})"); return 1; }

        while (main.JobsForTests.Any(j => j.State is JobState.Waiting or JobState.Running) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Thread.Sleep(300); // the queue page redraws after 200 ms
        Dispatcher.UIThread.RunJobs();
        shot("install-done");
        var job = main.JobsForTests.LastOrDefault();
        Console.WriteLine($"  install-pkg: {job?.State} {job?.TitleId} {job?.Detail}");
        if (job is not { State: JobState.Done, TitleId: { } titleId }) return 1;

        var rif = Core.Package.Zrif.Decode(zrif);
        string contentId = Core.Package.Zrif.ContentId(rif);
        var licFile = Path.Combine(Program.Ux0, "license", titleId, contentId + ".rif");
        var workBin = Path.Combine(Program.Ux0, "app", titleId, "sce_sys", "package", "work.bin");
        bool ok = File.Exists(licFile) && File.ReadAllBytes(licFile).AsSpan().SequenceEqual(rif)
               && File.Exists(workBin) && File.ReadAllBytes(workBin).AsSpan().SequenceEqual(rif);
        Console.WriteLine($"  install-pkg: license {licFile} and work.bin {(ok ? "match the zRIF" : "MISSING or different")}");
        return ok ? 0 : 1;
    }
}
