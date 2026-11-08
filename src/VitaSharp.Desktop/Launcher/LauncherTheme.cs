using Avalonia.Media;
using VitaSharp.Desktop.Launcher.Ui;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Colours shared by the launcher's panels and dialogs: the current theme's palette (<see cref="Palette.Current"/>).</summary>
static class LauncherTheme
{
    static Palette P => Palette.Current;
    public static Color AccentColor => P.Accent;
    public static IBrush Accent => new SolidColorBrush(P.Accent);
    public static IBrush OnAccent => new SolidColorBrush(P.OnAccent);
    public static IBrush Text => new SolidColorBrush(P.Text);
    public static IBrush Muted => new SolidColorBrush(P.Muted);
    public static IBrush Panel => new SolidColorBrush(P.Surface);
    public static IBrush WindowBackground => new SolidColorBrush(P.Background);
    public static IBrush Danger => new SolidColorBrush(P.Danger);
}