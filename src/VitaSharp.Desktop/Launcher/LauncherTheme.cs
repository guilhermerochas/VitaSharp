using Avalonia.Media;
using VitaSharp.Desktop.Launcher.Ui;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Colours shared by the launcher's panels and dialogs, from the current theme's palette (<see cref="Palette.Current"/>); the brushes follow every theme switch (<see cref="Kit.B(Func{Palette, Color})"/>).</summary>
static class LauncherTheme
{
    public static Color AccentColor => Palette.Current.Accent;
    public static IBrush Accent => Kit.B(static p => p.Accent);
    public static IBrush OnAccent => Kit.B(static p => p.OnAccent);
    public static IBrush Text => Kit.B(static p => p.Text);
    public static IBrush Muted => Kit.B(static p => p.Muted);
    public static IBrush Panel => Kit.B(static p => p.Surface);
    public static IBrush WindowBackground => Kit.B(static p => p.Background);
    public static IBrush Danger => Kit.B(static p => p.Danger);
}
