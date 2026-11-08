using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace VitaSharp.Desktop.Launcher.Prototype;

/// <summary>
/// <c>ui-prototype &lt;out-dir&gt; [--size=WxH] [--title=ID]</c>: renders the proposed launcher screens (library, game
/// page, settings; dark and light) to PNGs without opening a window (Avalonia headless platform with Skia), from the
/// games installed under the data folder. Design stage only: the launcher itself is unchanged.
/// </summary>
static class LauncherPrototype
{
    sealed class ProtoApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    public static int Run(string[] args)
    {
        string outDir = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "ui-prototype");
        Directory.CreateDirectory(outDir);
        var size = args.FirstOrDefault(a => a.StartsWith("--size="))?["--size=".Length..].Split('x') is [var ws, var hs]
            && int.TryParse(ws, out int w) && int.TryParse(hs, out int h) ? (w, h) : (1440, 900);
        string? heroId = args.FirstOrDefault(a => a.StartsWith("--title="))?["--title=".Length..];

        AppBuilder.Configure<ProtoApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

        var games = ProtoGame.Scan(Program.Ux0);
        Console.WriteLine($"ui-prototype: {games.Count} game(s) under {Program.Ux0}");
        var hero = games.FirstOrDefault(g => g.TitleId.Equals(heroId, StringComparison.OrdinalIgnoreCase)) ?? games.FirstOrDefault()
            ?? new ProtoGame("PCSA00000", "Jogo de exemplo", "1.00", "", null, null, "gd");
        if (games.Count == 0) games.Add(hero);

        foreach (var (palette, theme, suffix) in new[] { (ProtoPalette.DarkSet, ThemeVariant.Dark, "dark"), (ProtoPalette.LightSet, ThemeVariant.Light, "light") })
        {
            var screens = new PrototypeScreens(new ProtoKit(palette), games, hero);
            foreach (var screen in new[] { PrototypeScreens.Library, PrototypeScreens.Game, PrototypeScreens.Settings })
            {
                var window = new Window
                {
                    Width = size.Item1, Height = size.Item2, RequestedThemeVariant = theme, SystemDecorations = SystemDecorations.None,
                    Content = screens.Build(screen),
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var frame = window.CaptureRenderedFrame();
                var path = Path.Combine(outDir, $"{screen}_{suffix}.png");
                if (frame == null) { Console.WriteLine($"  {path}: no frame"); return 1; }
                frame.Save(path);
                Console.WriteLine($"  wrote {path}");
                window.Close();
            }
        }
        return 0;
    }
}
