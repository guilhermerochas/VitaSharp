using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Localization;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Settings > General > Trophies: imports trophies earned elsewhere (<see cref="TrophyImport"/>): Vita3K's (merged into
/// VitaSharp's list) or a PS Vita's ur0:user/00/trophy folder copied decrypted (becomes the system's trophy data, which the
/// real Trophy Collection reads). Same as <c>import-trophies</c> on the command line.
/// </summary>
public sealed class TrophyImportPanel : StackPanel
{
    readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    readonly string? _vita3k = TrophyImport.FindVita3K();

    public TrophyImportPanel()
    {
        Spacing = 6;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var folder = new Button { Content = Loc.Get("TiImportFolder") };
        folder.Click += async (_, _) => await PickAndImport();
        buttons.Children.Add(folder);
        if (_vita3k != null)
        {
            var v3k = new Button { Content = Loc.Get("TiImportVita3K") };
            ToolTip.SetTip(v3k, Loc.Format("TiImportVita3KTip", _vita3k));
            v3k.Click += async (_, _) => await Import(_vita3k);
            buttons.Children.Add(v3k);
        }
        Children.Add(buttons);
        Children.Add(_result);
        Children.Add(new TextBlock
        {
            Foreground = LauncherTheme.Muted,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Text = Loc.Get("TiAbout"),
        });
    }

    async Task PickAndImport()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null)
        {
            return;
        }

        var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Get("TiPickTitle"),
            AllowMultiple = false,
        });
        var path = dirs.Count > 0 ? dirs[0].TryGetLocalPath() : null;
        if (path != null)
        {
            await Import(path);
        }
    }

    async Task Import(string source)
    {
        _result.IsVisible = true;
        _result.Foreground = LauncherTheme.Muted;
        _result.Text = Loc.Get("TiImporting");
        try
        {
            var r = await Task.Run(() => TrophyImport.Import(source, Program.FsRoot));
            bool ok = r.Kind is TrophyImport.SourceKind.Vita3K or TrophyImport.SourceKind.VitaDecrypted;
            _result.Foreground = ok ? LauncherTheme.Muted : Brushes.IndianRed;
            _result.Text = r.Kind switch
            {
                TrophyImport.SourceKind.Vita3K =>
                    Loc.Format("TiVita3KDone", r.Titles.Sum(t => t.Imported), r.Titles.Count(t => t.Total > 0)),
                TrophyImport.SourceKind.VitaDecrypted =>
                    Loc.Format("TiVitaDone", r.Titles.Count) + (r.Backup != null ? Loc.Format("TiBackup", r.Backup) : ""),
                TrophyImport.SourceKind.VitaEncrypted =>
                    Loc.Get("TiEncrypted"),
                _ => Loc.Get("TiNothing"),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _result.Foreground = Brushes.IndianRed;
            _result.Text = Loc.Format("TiFailed", e.Message);
        }
    }
}
