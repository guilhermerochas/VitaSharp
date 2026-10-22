using Avalonia.Media;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Colours shared by the launcher windows.</summary>
static class LauncherTheme
{
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#3D7BFF"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA3B2"));
    public static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#1B1F27"));
    public static readonly IBrush WindowBackground = new SolidColorBrush(Color.Parse("#12151B"));
}
