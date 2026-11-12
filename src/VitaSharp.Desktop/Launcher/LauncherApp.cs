using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Runtime.InteropServices;
using VitaSharp.Desktop.Launcher.Ui;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Avalonia application hosting the game library window (started when VitaSharp runs without arguments).</summary>
public sealed class LauncherApp : Application
{
    public override void Initialize()
    {
        // the launcher's blue accent everywhere (check boxes, sliders, toggles), not the OS accent colour
        Styles.Add(new FluentTheme
        {
            Palettes =
            {
                [ThemeVariant.Dark] = new ColorPaletteResources { Accent = Palette.DarkSet.Accent },
                [ThemeVariant.Light] = new ColorPaletteResources { Accent = Palette.LightSet.Accent },
            },
        });
        // Inter everywhere (Sora is set on headings)
        Styles.Add(new Style(x => x.Is<TopLevel>()) { Setters = { new Setter(TextElement.FontFamilyProperty, Kit.Inter) } });
        // "flat" buttons: the page draws the button itself (Kit.Click); hover and press only dim it a little
        foreach (var state in new[] { ":pointerover", ":pressed", ":disabled", "" })
        {
            var sel = state switch
            {
                ":pointerover" => (Func<Selector?, Selector>)(x => x.OfType<Button>().Class("flat").Class(":pointerover").Template().OfType<ContentPresenter>()),
                ":pressed" => x => x.OfType<Button>().Class("flat").Class(":pressed").Template().OfType<ContentPresenter>(),
                ":disabled" => x => x.OfType<Button>().Class("flat").Class(":disabled").Template().OfType<ContentPresenter>(),
                _ => x => x.OfType<Button>().Class("flat").Template().OfType<ContentPresenter>(),
            };
            Styles.Add(new Style(sel)
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
                    new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                },
            });
        }
        // the panels' ordinary buttons (firmware, trophy import, controller page): rounder corners like the rest
        Styles.Add(new Style(x => x.OfType<Button>().Not(y => y.Class("flat"))) { Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("flat").Class(":pointerover")) { Setters = { new Setter(Visual.OpacityProperty, 0.86) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("flat").Class(":pressed")) { Setters = { new Setter(Visual.OpacityProperty, 0.7) } });
        // the VitaSharp logo on every launcher window (library, dialogs)
        if (VitaSharp.Desktop.Host.AppIcon.ForAvalonia() is { } icon)
        {
            Styles.Add(new Style(x => x.Is<Window>()) { Setters = { new Setter(Window.IconProperty, icon) } });
        }

        ActualThemeVariantChanged += (_, _) =>
        {
            if (FollowsSystem)
            {
                SyncPalette();
            }
        };
    }

    static bool FollowsSystem;

    /// <summary>Applies "system", "dark" or "light" (<see cref="LauncherSettings.UiTheme"/>) and updates <see cref="Palette.Current"/>.</summary>
    public static void ApplyTheme(string uiTheme)
    {
        if (Current is not { } app)
        {
            return;
        }

        FollowsSystem = uiTheme is not (LauncherSettings.ThemeDark or LauncherSettings.ThemeLight);
        app.RequestedThemeVariant = uiTheme switch
        {
            LauncherSettings.ThemeDark => ThemeVariant.Dark,
            LauncherSettings.ThemeLight => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
        SyncPalette();
    }

    /// <summary>Raised after the palette changed (theme picked or the OS switched while following it).</summary>
    public static event Action? PaletteChanged;

    static void SyncPalette()
    {
        var dark = Current?.ActualThemeVariant != ThemeVariant.Light;
        var p = dark ? Palette.DarkSet : Palette.LightSet;
        if (p == Palette.Current)
        {
            return;
        }

        Palette.Current = p;
        PaletteChanged?.Invoke();
    }

    /// <summary>Replaces the library window (ui-snapshot renders the settings screens instead).</summary>
    internal static Action<IClassicDesktopStyleApplicationLifetime>? StartupOverride;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (StartupOverride != null)
            {
                StartupOverride(desktop);
            }
            else
            {
                desktop.MainWindow = new MainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static int Run(string[] args)
    {
        if (StartupOverride == null)
        {
            HideOwnConsole();
        }

        return AppBuilder.Configure<LauncherApp>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Sets the app up on Avalonia's headless platform with Skia drawing: windows render to bitmaps and never
    /// appear on screen (ui-render).</summary>
    public static void SetupHeadless()
    {
        // once per process: Avalonia's platform cannot be set up twice (ui-render and the tests share it)
        if (Current != null)
        {
            return;
        }

        AppBuilder.Configure<LauncherApp>().UseSkia()
            .UseHeadless(new Avalonia.Headless.AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
    }

    /// <summary>When started from Explorer, Windows opens a console just for us; the launcher does not need it.</summary>
    static void HideOwnConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pids = new uint[2];
        if (GetConsoleProcessList(pids, 2) == 1)
        {
            FreeConsole();
        }
    }

    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] list, uint count);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
}
