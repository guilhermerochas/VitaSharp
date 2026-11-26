using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Capturas: the F8 "Gravar tudo" recordings (captures/*.zip), the player's only debugging tool (each has the
/// detailed log, the GPU dump and the diagnostics), with their game, date and size; show in the folder or delete. The
/// GPU dumps and diagnostic folders of developer runs (gpudumps/, diagnostics/) are not listed.</summary>
public sealed partial class MainWindow
{
    public const string CaptureRecording = "recording";

    sealed record CaptureFile(string Kind, string TitleId, string Path, long Size, DateTime When);

    string _captureKind = "";
    string? _captureGame;

    static List<CaptureFile> CaptureFiles()
    {
        var list = new List<CaptureFile>();
        void Add(string dir, string pattern, string kind)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var f in Directory.GetFiles(dir, pattern))
            {
                try
                {
                    var fi = new FileInfo(f);
                    list.Add(new CaptureFile(kind, System.IO.Path.GetFileName(f).Split('_')[0], f, fi.Length, fi.LastWriteTime));
                }
                catch (IOException) { }
            }
        }
        Add(SessionCaptureControl.Root, "*.zip", CaptureRecording);
        return list.OrderByDescending(c => c.When).ToList();
    }

    Control CapturesPage()
    {
        var all = CaptureFiles();
        var shown = all.Where(c => (_captureKind.Length == 0 || c.Kind == _captureKind) && (_captureGame == null || c.TitleId == _captureGame)).ToList();
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(36, 28, 36, 0) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        header.Children.Add(_k.VStack(2, _k.Heading(S.Captures, 30), _k.Wrap(S.CapturesSubtitle(all.Count, S.Size(all.Sum(c => c.Size))), 13.5)));
        var old = all.Where(c => c.When < DateTime.Now.AddDays(-30)).ToList();
        var actions = _k.HStack(10,
            _k.Secondary(S.DeleteOld(old.Count), "trash", () => _ = DeleteCaptures(old), 44, enabled: old.Count > 0),
            _k.MenuButton(S.OpenFolder, "folder", 44,
                (S.RecordingsFolder, () => { Directory.CreateDirectory(SessionCaptureControl.Root); OpenPath(SessionCaptureControl.Root); }, true)));
        actions.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(Kit.At(actions, col: 1));
        page.Children.Add(header);

        var chips = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 0, 0, 18) };
        void Kind(string k)
        {
            _captureKind = k;
            ShowPage();
        }
        chips.Children.Add(_k.Chip(S.FilterAll(all.Count), _captureKind == "", () => Kind("")));
        chips.Children.Add(_k.Chip(S.Recordings(all.Count(c => c.Kind == CaptureRecording)), _captureKind == CaptureRecording, () => Kind(CaptureRecording), "camera"));
        var gameItems = new List<(string, bool, Action)> { (S.AllGames, _captureGame == null, () => { _captureGame = null; ShowPage(); }) };
        foreach (var id in all.Select(c => c.TitleId).Distinct().OrderBy(CaptureTitle))
        {
            gameItems.Add((CaptureTitle(id), _captureGame == id, () => { _captureGame = id; ShowPage(); }
            ));
        }

        chips.Children.Add(_k.ChipMenu(S.GameChip(_captureGame == null ? S.AllShort : CaptureTitle(_captureGame)), null, gameItems));
        page.Children.Add(Kit.At(chips, row: 1));

        Control body;
        if (shown.Count == 0)
        {
            body = _k.Card(_k.Wrap(S.NoCaptures, 13.5), 18, new Thickness(20)).Also(c => c.VerticalAlignment = VerticalAlignment.Top);
        }
        else
        {
            var rows = new StackPanel();
            foreach (var c in shown.Take(200))
            {
                if (rows.Children.Count > 0)
                {
                    rows.Children.Add(_k.Divider(14));
                }

                rows.Children.Add(CaptureRow(c, compact: false));
            }
            body = PageScroll(new ScrollViewer { Content = _k.Card(rows, 18, new Thickness(0, 4)).Also(c => c.Margin = new Thickness(0, 0, 0, 24)), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }
        page.Children.Add(Kit.At(body, row: 2));
        return page;
    }

    string CaptureTitle(string titleId) => titleId == "shell" ? S.VitaSystem : _all.FirstOrDefault(g => g.TitleId == titleId)?.Title ?? titleId;

    Control CaptureRow(CaptureFile c, bool compact)
    {
        var game = _all.FirstOrDefault(g => g.TitleId == c.TitleId);
        var r = new Grid { ColumnDefinitions = new ColumnDefinitions(compact ? "Auto,*,Auto" : "Auto,*,150,110,Auto"), Margin = new Thickness(14, 10) };
        Control thumb = game?.Pic0Path is { } pic
            ? _k.Picture(pic, compact ? 88 : 112, compact ? 50 : 63, 10, decodeWidth: 240)
            : new Border { Width = compact ? 88 : 112, Height = compact ? 50 : 63, CornerRadius = new CornerRadius(10), Background = Kit.B(static p => p.SurfaceHigh), Child = _k.Icon(c.TitleId == "shell" ? "vita" : "file", 24, static p => p.AccentSoft) };
        r.Children.Add(thumb);
        (string Label, Func<Palette, Color> Color) badge = (S.RecordingBadge, static p => p.Accent);
        var title = compact ? _k.HStack(8, _k.Badge(badge.Label, badge.Color), _k.Text(S.When(c.When), 13, FontWeight.SemiBold))
                            : _k.HStack(8, _k.Text(CaptureTitle(c.TitleId), 14, FontWeight.SemiBold), _k.Badge(badge.Label, badge.Color));
        var tx = _k.VStack(4, title, _k.Text(compact ? $"{System.IO.Path.GetFileName(c.Path)} \u00B7 {S.Size(c.Size)}" : System.IO.Path.GetFileName(c.Path), 12, color: static p => p.Muted));
        tx.Margin = new Thickness(16, 0);
        tx.VerticalAlignment = VerticalAlignment.Center;
        r.Children.Add(Kit.At(tx, col: 1));
        int col = 2;
        if (!compact)
        {
            r.Children.Add(Kit.At(_k.Text(S.When(c.When), 13, color: static p => p.TextSoft), col: col++));
            r.Children.Add(Kit.At(_k.Text(S.Size(c.Size), 13, FontWeight.Medium, static p => p.TextSoft), col: col++));
        }
        var acts = _k.HStack(6, _k.Secondary("", "folder", () => Reveal(c.Path), 36, tip: S.ShowInFolder), _k.Secondary("", "trash", () => _ = DeleteCaptures([c]), 36, static p => p.Danger, tip: S.Delete));
        acts.VerticalAlignment = VerticalAlignment.Center;
        r.Children.Add(Kit.At(acts, col: col));
        r.Background = Kit.Fixed(Colors.Transparent);
        r.GotFocus += (_, _) => r.Background = Kit.B(static p => p.Highlight);
        r.LostFocus += (_, _) => r.Background = Kit.Fixed(Colors.Transparent);
        ListItem(c.Path, r);
        return r;
    }

    async Task DeleteCaptures(List<CaptureFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (!await Confirm(files.Count == 1 ? S.ConfirmDeleteOne(System.IO.Path.GetFileName(files[0].Path)) : S.ConfirmDeleteMany(files.Count, S.Size(files.Sum(f => f.Size))), S.Delete, danger: true))
        {
            return;
        }

        // the list stays where it was, with the focus on the capture that followed the (first) deleted one
        KeepListPosition(NextListItem(files.Select(f => f.Path).ToList()));
        int done = 0;
        await Task.Run(() =>
        {
            foreach (var f in files)
            {
                try
                {
                    File.Delete(f.Path);
                    // a recording's unpacked folder (same name, no .zip) goes with it
                    var folder = System.IO.Path.ChangeExtension(f.Path, null);
                    if (f.Kind == CaptureRecording && Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }

                    done++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        });
        SetStatus(S.Deleted(done));
        ShowPage();
    }
}
