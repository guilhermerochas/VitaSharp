using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Desktop.Launcher;

/// <summary>The game's save states (quick save and slots 1-9, see SaveStateControl) with their pictures: clicking one
/// starts the game and loads it once the game has started.</summary>
public sealed partial class MainWindow
{
    Control? SaveStatesPanel(GameEntry g, bool busy)
    {
        var dir = SaveStates.Folder(Program.DataRoot, g.TitleId);
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, "*.vss").OrderByDescending(File.GetLastWriteTimeUtc).Take(10).ToList();
        if (files.Count == 0) return null;
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Save states", Foreground = Muted, FontSize = 12 });
        foreach (var f in files)
        {
            SaveStateHeader? h = null;
            try { h = SaveStateFile.ReadHeader(f); } catch (Exception e) when (e is SnapshotException or IOException) { }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (h?.Thumbnail is { Length: > 0 } png)
            {
                try { row.Children.Add(new Image { Source = new Bitmap(new MemoryStream(png)), Width = 96, Height = 54, Stretch = Stretch.Uniform }); }
                catch (Exception) { }
            }
            string name = Path.GetFileNameWithoutExtension(f) switch { "quick" => "Quick save", var n when n.StartsWith("slot") => $"Slot {n[4..]}", var n => n };
            row.Children.Add(new TextBlock { Text = $"{name}\n{File.GetLastWriteTime(f):g}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = row, IsEnabled = !busy && h != null, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4) };
            string? why = h == null ? "not a readable save state" : SaveStateFile.Incompatibility(h, g.TitleId);
            if (why != null) { b.IsEnabled = false; ToolTip.SetTip(b, why); }
            else ToolTip.SetTip(b, "Start the game and load this state");
            b.Click += (_, _) => StartEmulator(g.Title, g.TitleId, ["run", g.Path, $"--load-state={f}"], g);
            panel.Children.Add(b);
        }
        return panel;
    }
}
