using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VitaSharp.Core.Hle.Np;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Configurações > Geral > Troféus: imports trophies earned elsewhere (<see cref="TrophyImport"/>): Vita3K's (merged into
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
        var folder = new Button { Content = "Importar pasta de troféus…" };
        folder.Click += async (_, _) => await PickAndImport();
        buttons.Children.Add(folder);
        if (_vita3k != null)
        {
            var v3k = new Button { Content = "Importar troféus do Vita3K" };
            ToolTip.SetTip(v3k, $"Lê {_vita3k} (só leitura) e junta os troféus à lista do VitaSharp.");
            v3k.Click += async (_, _) => await Import(_vita3k);
            buttons.Children.Add(v3k);
        }
        Children.Add(buttons);
        Children.Add(_result);
        Children.Add(new TextBlock
        {
            Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Text = "Pasta de um PS Vita: ur0:user/00/trophy copiada já descriptografada (no console os dados ficam " +
                   "criptografados com as chaves daquele aparelho; uma cópia feita com um gerenciador de arquivos não serve). " +
                   "No Coleção de Troféus original, um jogo aparece depois de ser iniciado uma vez pelo Sistema do Vita.",
        });
    }

    async Task PickAndImport()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Selecione a pasta trophy (ur0:user/00/trophy) ou a pasta de dados do Vita3K",
            AllowMultiple = false,
        });
        var path = dirs.Count > 0 ? dirs[0].TryGetLocalPath() : null;
        if (path != null) await Import(path);
    }

    async Task Import(string source)
    {
        _result.IsVisible = true;
        _result.Foreground = LauncherTheme.Muted;
        _result.Text = "Importando…";
        try
        {
            var r = await Task.Run(() => TrophyImport.Import(source, Program.FsRoot));
            bool ok = r.Kind is TrophyImport.SourceKind.Vita3K or TrophyImport.SourceKind.VitaDecrypted;
            _result.Foreground = ok ? LauncherTheme.Muted : Brushes.IndianRed;
            _result.Text = r.Kind switch
            {
                TrophyImport.SourceKind.Vita3K =>
                    $"{r.Titles.Sum(t => t.Imported)} troféus importados do Vita3K ({r.Titles.Count(t => t.Total > 0)} jogos com troféus).",
                TrophyImport.SourceKind.VitaDecrypted =>
                    $"Dados de troféus do PS Vita importados ({r.Titles.Count} jogos)." + (r.Backup != null ? $" Os dados anteriores ficaram em {r.Backup}." : ""),
                TrophyImport.SourceKind.VitaEncrypted =>
                    "Esses arquivos estão criptografados com as chaves do seu PS Vita e só podem ser lidos nele. " +
                    "Copie a pasta já descriptografada (por um homebrew que a lê pelo próprio sistema) e importe essa cópia.",
                _ => "Nenhum dado de troféu encontrado nessa pasta.",
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _result.Foreground = Brushes.IndianRed;
            _result.Text = "Falha ao importar: " + e.Message;
        }
    }
}
