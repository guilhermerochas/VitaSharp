using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VitaSharp.Core.Package;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

public enum JobState { Waiting, Running, Done, Failed, Cancelled }

/// <summary>One item of the install queue: a VPK/zip, a PKG with its license or a game folder.</summary>
public sealed class InstallJob
{
    public required string Name { get; init; }
    public required Func<InstallJob, CancellationToken, Task<string?>> Work { get; init; }
    public JobState State { get; set; }
    public double Progress { get; set; }
    public string Detail { get; set; } = "";
    /// <summary>The title installed (to open its page or play it).</summary>
    public string? TitleId { get; set; }
    public DateTime Finished { get; set; }
    public CancellationTokenSource? Cts { get; set; }
}

/// <summary>Instalar pkg / vpk: drop zone, the pickers and the install queue (one install at a time, in order).</summary>
public sealed partial class MainWindow
{
    readonly List<InstallJob> _jobs = new();
    bool _queueRunning;

    void Enqueue(InstallJob job)
    {
        _jobs.Add(job);
        JobsChanged();
        if (!_queueRunning) _ = RunQueue();
    }

    async Task RunQueue()
    {
        _queueRunning = true;
        try
        {
            while (_jobs.FirstOrDefault(j => j.State == JobState.Waiting) is { } job)
            {
                job.State = JobState.Running;
                job.Cts = new CancellationTokenSource();
                JobsChanged();
                AppendLog($"> {S.Installing} {job.Name}");
                try
                {
                    job.TitleId = await job.Work(job, job.Cts.Token);
                    job.State = JobState.Done;
                    job.Progress = 1;
                }
                catch (OperationCanceledException)
                {
                    job.State = JobState.Cancelled;
                    job.Detail = S.InstallCancelled;
                    AppendLog(S.InstallCancelled);
                }
                catch (Exception e)
                {
                    job.State = JobState.Failed;
                    job.Detail = e.Message;
                    AppendLog(S.InstallFailed(e.Message));
                }
                finally
                {
                    job.Finished = DateTime.Now;
                    job.Cts.Dispose();
                    job.Cts = null;
                }
                Refresh(rebuild: false);
                SetStatus(job.State switch
                {
                    JobState.Done => job.Detail, JobState.Cancelled => S.InstallCancelledOf(job.Name), _ => S.InstallFailedOf(job.Name),
                });
                JobsChanged();
                if (job.State == JobState.Failed && _page != PageInstall) Navigate(PageInstall);
            }
        }
        finally { _queueRunning = false; }
    }

    bool _jobsDirty;

    /// <summary>Redraws the sidebar card and the install page, at most a few times per second.</summary>
    void JobsChanged()
    {
        if (_jobsDirty) return;
        _jobsDirty = true;
        DispatcherTimer.RunOnce(() =>
        {
            _jobsDirty = false;
            RebuildSidebar();
            if (_page == PageInstall) ShowPage();
            else if (_page == PageLibrary && _all.Count > 0 && _contentHost.Child is ScrollViewer) ShowPage(); // the first-run page becomes the library
        }, TimeSpan.FromMilliseconds(200));
    }

    void Report(InstallJob job, InstallProgress p)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (job.State != JobState.Running) return; // a late report after the install finished
            job.Progress = p.Fraction;
            job.Detail = p.Describe();
            SetStatus($"{S.Installing} {job.Name}\u2026 {job.Detail}");
            JobsChanged();
        });
    }

    Control? InstallCard()
    {
        var job = _jobs.FirstOrDefault(j => j.State == JobState.Running);
        if (job == null) return null;
        int waiting = _jobs.Count(j => j.State == JobState.Waiting);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        head.Children.Add(new Border { Margin = new Thickness(0, 0, 8, 0), Child = _k.Icon("download", 16, P.Accent) });
        head.Children.Add(Kit.At(_k.Text(waiting > 0 ? S.InstallingMore(waiting) : S.Installing, 12.5, FontWeight.SemiBold, P.Accent), col: 1));
        head.Children.Add(Kit.At(_k.Text($"{job.Progress * 100:F0}%", 12.5, FontWeight.SemiBold, P.TextSoft), col: 2));
        var body = _k.VStack(8, head, _k.Text(job.Name, 13.5, FontWeight.Medium), _k.Bar(job.Progress, 5), _k.Text(job.Detail, 11.5, color: P.Muted));
        return Kit.Click(new Border
        {
            Background = Kit.B(P.Surface), BorderBrush = Kit.B(P.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 12), Child = body,
        }, () => Navigate(PageInstall));
    }

    // ------------------------------------------------------------------ page

    Control InstallPage()
    {
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 32) };
        page.Children.Add(_k.VStack(2, _k.Heading(S.InstallTitle, 30), _k.Text(S.InstallSubtitle, 13.5, color: P.Muted)).Also(h => h.Margin = new Thickness(0, 0, 0, 22)));
        var drop = _k.VStack(10,
            _k.IconChip("download", 52).Also(c => c.HorizontalAlignment = HorizontalAlignment.Center),
            _k.Text(S.DropHere, 15, FontWeight.SemiBold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            _k.Text(S.DropHint, 12.5, color: P.Muted).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            _k.HStack(10,
                _k.Primary(S.ChooseVpk, "folder", () => _ = InstallVpk(), 42, 0),
                _k.Secondary(S.ChoosePkg, "key", () => _ = InstallPkg(), 42),
                _k.Secondary(S.ChooseFolder, null, () => _ = AddFolder(), 42)).Also(b => { b.HorizontalAlignment = HorizontalAlignment.Center; b.Margin = new Thickness(0, 6, 0, 0); }));
        page.Children.Add(_k.Dashed(drop, 230));

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 26, 0, 12) };
        head.Children.Add(_k.Text(S.Queue, 17, FontWeight.SemiBold));
        if (_jobs.Any(j => j.State is JobState.Done or JobState.Failed or JobState.Cancelled))
            head.Children.Add(Kit.At(Kit.Click(_k.Text(S.ClearFinished, 13, FontWeight.SemiBold, P.Accent), () =>
            {
                _jobs.RemoveAll(j => j.State is JobState.Done or JobState.Failed or JobState.Cancelled);
                JobsChanged();
            }), col: 1));
        page.Children.Add(head);
        if (_jobs.Count == 0) page.Children.Add(_k.Card(_k.Wrap(S.QueueEmpty, 13.5), 18, new Thickness(20)));
        else
        {
            var rows = new StackPanel();
            foreach (var j in _jobs.AsEnumerable().Reverse())
            {
                if (rows.Children.Count > 0) rows.Children.Add(_k.Divider(16));
                rows.Children.Add(JobRow(j));
            }
            page.Children.Add(_k.Card(rows, 18, new Thickness(0, 4)));
        }
        return new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    Control JobRow(InstallJob j)
    {
        var game = j.TitleId != null ? _all.FirstOrDefault(g => g.TitleId == j.TitleId) : null;
        var (icon, color) = j.State switch
        {
            JobState.Running => ("download", P.Accent), JobState.Waiting => ("clock", P.Muted), JobState.Done => ("check", P.Ok),
            _ => ("x", P.Danger),
        };
        var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(16, 12) };
        r.Children.Add(game != null && j.State == JobState.Done ? _k.Picture(game.IconPath, 44, 44, 11, game.Initials, 128)
            : _k.IconChip(icon, 44, Color.FromArgb(40, color.R, color.G, color.B), color));
        string detail = j.State switch
        {
            JobState.Waiting => S.Waiting,
            JobState.Running => $"{S.Installing} \u00B7 {j.Detail}",
            JobState.Done => S.InstalledAt(j.Detail, j.Finished),
            JobState.Cancelled => S.InstallCancelled,
            _ => S.FailedBecause(j.Detail),
        };
        var t = _k.VStack(5, _k.HStack(8, _k.Text(game?.Title ?? j.Name, 14, FontWeight.SemiBold), j.State == JobState.Done ? _k.Icon("check", 15, P.Ok, 2.4) : null),
            _k.Wrap(detail, 12.5, j.State == JobState.Failed ? P.Danger : P.Muted));
        if (j.State == JobState.Running) t.Children.Add(new Border { Width = 520, HorizontalAlignment = HorizontalAlignment.Left, Child = _k.Bar(j.Progress, 5) });
        t.Margin = new Thickness(14, 0, 16, 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        r.Children.Add(Kit.At(t, col: 1));
        Control action = j.State switch
        {
            JobState.Running => _k.Secondary(S.Cancel, null, () => j.Cts?.Cancel(), 36),
            JobState.Waiting => _k.Secondary("", "x", () => { _jobs.Remove(j); JobsChanged(); }, 36, tip: S.RemoveFromQueue),
            JobState.Done when game != null => _k.HStack(8, _k.Secondary(S.OpenPage, null, () => Navigate(PageGame, game), 36), PlayButton(game, 36, 0)),
            _ => _k.Secondary("", "x", () => { _jobs.Remove(j); JobsChanged(); }, 36, tip: S.RemoveFromQueue),
        };
        action.VerticalAlignment = VerticalAlignment.Center;
        r.Children.Add(Kit.At(action, col: 2));
        return r;
    }

    // ------------------------------------------------------------------ pickers and drop

    async Task InstallVpk()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = S.PickVpkTitle,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(S.VpkFileType) { Patterns = ["*.vpk", "*.zip"] }, FilePickerFileTypes.All],
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path) EnqueueVpk(path);
    }

    async Task InstallPkg()
    {
        var pkgs = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = S.PickPkgTitle,
            FileTypeFilter = [new FilePickerFileType(S.PkgFileType) { Patterns = ["*.pkg"] }],
        });
        var pkg = pkgs.FirstOrDefault()?.TryGetLocalPath();
        if (pkg != null) await AskLicenseAndQueue(pkg);
    }

    async Task AddFolder()
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = S.PickFolderTitle });
        var dir = dirs.FirstOrDefault()?.TryGetLocalPath();
        if (dir != null) await EnqueueFolder(dir);
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList() ?? [];
        var pkg = items.FirstOrDefault(p => p.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase));
        var lic = items.FirstOrDefault(p => p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".rif", StringComparison.OrdinalIgnoreCase));
        var archives = items.Where(p => File.Exists(p) && (p.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))).ToList();
        if (archives.Count > 0) foreach (var a in archives) EnqueueVpk(a);
        else if (pkg != null) await QueuePkg(pkg, lic, items.FirstOrDefault(p => Path.GetFileName(p).Equals("act.dat", StringComparison.OrdinalIgnoreCase)));
        else foreach (var dir in items.Where(Directory.Exists)) await EnqueueFolder(dir);
    }

    // ------------------------------------------------------------------ the installers

    void EnqueueVpk(string archive) => Enqueue(new InstallJob
    {
        Name = Path.GetFileName(archive),
        Work = async (job, ct) =>
        {
            int lastPermille = -1;
            var results = await Task.Run(() => VpkInstaller.Install(archive, Program.Ux0, new VpkInstallOptions
            {
                Log = msg => Dispatcher.UIThread.Post(() => AppendLog(msg)),
                Progress = p =>
                {
                    int permille = (int)(p.Fraction * 1000);
                    if (permille == lastPermille) return;
                    lastPermille = permille;
                    Report(job, p);
                },
                // runs on the install thread: show the dialog on the UI thread and wait for the answer
                ConfirmOverwrite = c => Dispatcher.UIThread.InvokeAsync(() => Confirm(c.Kind switch
                {
                    VpkContentKind.Patch => S.ReplacePatch(c.TitleId, c.Version),
                    VpkContentKind.AddCont => S.ReplaceAddOn(c.TitleId),
                    _ => S.ReplaceGame(c.Title, c.TitleId),
                })).GetAwaiter().GetResult(),
            }, ct), ct);
            foreach (var r in results)
                AppendLog(r.Installed ? $"Installed {r.TitleId} \"{r.Content.Title}\" ({r.Content.Kind}) -> {r.InstallPath}"
                                      : $"Skipped {r.TitleId} ({r.Content.Kind}): already installed");
            var installed = results.Where(r => r.Installed).ToList();
            if (installed.Count == 0) { job.Detail = S.NothingInstalled(job.Name); return null; }
            var app = installed.FirstOrDefault(r => r.Content.Kind == VpkContentKind.App) ?? installed[0];
            job.Detail = string.Join("  ", installed.Select(r => r.Content.Kind switch
            {
                VpkContentKind.Patch => S.InstalledPatch(r.TitleId, r.Content.Version),
                VpkContentKind.AddCont => S.InstalledAddOn(r.TitleId),
                _ => r.Replaced ? S.Reinstalled(r.Content.Title) : S.Installed(r.Content.Title),
            }));
            return app.TitleId;
        },
    });

    void EnqueuePkg(string pkg, string lic) => Enqueue(new InstallJob
    {
        Name = Path.GetFileName(pkg),
        Work = async (job, ct) =>
        {
            var t = await Task.Run(() => PkgInstaller.Install(pkg, lic, Program.Ux0,
                msg => Dispatcher.UIThread.Post(() => AppendLog(msg)), p => Report(job, p), ct), ct);
            AppendLog($"Installed {t.TitleId} \"{t.Title}\"");
            job.Detail = S.Installed(t.Title);
            return t.TitleId;
        },
    });

    /// <summary>A PSP or PSone Classic package: no license to ask for, it goes to ux0:pspemu.</summary>
    void EnqueuePspPkg(string pkg, PkgInfo info, string? rif = null, string? act = null) => Enqueue(new InstallJob
    {
        Name = Path.GetFileName(pkg),
        Work = async (job, ct) =>
        {
            var t = await Task.Run(() => PkgInstaller.InstallWithoutLicense(pkg, Program.Ux0,
                new PspInstallOptions { License = rif != null ? File.ReadAllBytes(rif) : null, ActDat = act != null ? File.ReadAllBytes(act) : null },
                msg => Dispatcher.UIThread.Post(() => AppendLog(msg)), p => Report(job, p), ct), ct);
            AppendLog($"Installed {t.TitleId} \"{t.Title}\" ({info.Description}) -> {t.Path}");
            if (t.IsoPath != null) AppendLog($"Decrypted UMD image -> {t.IsoPath}");
            job.Detail = S.InstalledPspEmu(t.Title, info.Kind);
            return t.TitleId;
        },
    });

    /// <summary>A .pkg picked or dropped (with the license dropped next to it, if any): reads what it is, installs a
    /// PSP/PSone package right away (with the user's own .rif and act.dat dropped with it) and asks for the license of a PS Vita one. False when nothing was queued.</summary>
    async Task<bool> QueuePkg(string pkg, string? lic, string? act = null)
    {
        PkgInfo info;
        try { info = await Task.Run(() => PkgInstaller.Inspect(pkg)); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            SetStatus(S.PkgUnreadable(e.Message));
            AppendLog(S.PkgUnreadable(e.Message));
            return false;
        }
        if (!info.NeedsLicense)
        {
            SetStatus(S.PkgNoLicense(info));
            AppendLog(S.PkgNoLicense(info));
            EnqueuePspPkg(pkg, info, lic, act); // the user's own license and act.dat, when dropped with the package
            return true;
        }
        lic ??= await AskPkgLicense(pkg, info);
        if (lic == null) return false;
        EnqueuePkg(pkg, lic);
        return true;
    }

    async Task EnqueueFolder(string dir)
    {
        var entry = GameLibrary.Read(dir);
        if (entry == null) { SetStatus(S.NotAGameFolder); return; }
        var dest = Path.Combine(Program.Ux0, "app", entry.TitleId);
        if (Path.GetFullPath(dir).TrimEnd('\\', '/').Equals(Path.GetFullPath(dest).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            Refresh();
            return;
        }
        if (Directory.Exists(dest) && !await Confirm(S.ReplaceFolder(entry.TitleId))) return;
        Enqueue(new InstallJob
        {
            Name = entry.Title,
            Work = async (job, ct) =>
            {
                job.Detail = S.Copying;
                await Task.Run(() =>
                {
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    CopyDirectory(dir, dest, ct);
                }, ct);
                job.Detail = S.Added(entry.Title);
                return entry.TitleId;
            },
        });
    }

    static void CopyDirectory(string src, string dst, CancellationToken ct)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        }
        foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)), ct);
    }

    /// <summary>The license of a package: a work.bin/.rif file chosen with the picker, or a zRIF pasted in the text
    /// field (the text NoNpDrm and pkg2zip lists give). Returns the file path or the zRIF text; null when cancelled.</summary>
    async Task<string?> AskPkgLicense(string pkg, PkgInfo? info = null)
    {
        string? result = null;
        Window? dlg = null;
        var zrif = new TextBox { Watermark = S.ZrifWatermark, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MinHeight = 60, CornerRadius = new CornerRadius(12) };
        var error = _k.Wrap("", 12.5, P.Danger).Also(t => t.IsVisible = false);
        void Fail(string message) { error.Text = message; error.IsVisible = true; }
        void Submit()
        {
            string text = new string((zrif.Text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (text.Length == 0) { Fail(S.ZrifEmpty); return; }
            if (!Zrif.LooksLikeZrif(text)) { Fail(S.ZrifNot); return; }
            try { Zrif.Decode(text); }
            catch (Exception e) { Fail(S.ZrifUnreadable(e.Message)); return; }
            result = text;
            dlg?.Close();
        }
        var install = _k.Primary(S.Install, null, Submit, 40, 100);
        var file = _k.Secondary(S.ChooseLicenseFile, "key", async () =>
        {
            var lics = await dlg!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = S.PickLicenseTitle,
                FileTypeFilter = [new FilePickerFileType(S.LicenseFileType) { Patterns = ["*.bin", "*.rif"] }, FilePickerFileTypes.All],
            });
            if (lics.FirstOrDefault()?.TryGetLocalPath() is { } path) { result = path; dlg.Close(); }
        }, 40);
        var cancel = _k.Secondary(S.Cancel, null, () => dlg?.Close(), 40);
        var buttons = _k.HStack(8, cancel, install);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var panel = _k.VStack(12, _k.Heading(S.LicenseFor(Path.GetFileName(pkg)), 18),
            info != null ? _k.Text(S.PkgNeedsLicense(info), 13, FontWeight.SemiBold, P.Accent) : null,
            _k.Wrap(S.LicenseText, 13.5, P.TextSoft), zrif, file.Also(f => f.HorizontalAlignment = HorizontalAlignment.Left), error, buttons);
        panel.Margin = new Thickness(24);
        dlg = Dialog(panel, 500);
        if (LicenseDialogForTests is { } hook) dlg.Opened += (_, _) => hook(dlg, zrif, Submit);
        await dlg.ShowDialog(this);
        return result;
    }

    /// <summary>ui-render's install check: called when the license dialog opens with the dialog, its zRIF field and
    /// the "Instalar" action.</summary>
    internal Action<Window, TextBox, Action>? LicenseDialogForTests;

    /// <summary>The "Escolher pkg" flow after the file picker: asks for the license and queues the install; false when
    /// the dialog was cancelled.</summary>
    internal Task<bool> AskLicenseAndQueue(string pkg) => QueuePkg(pkg, null);

    internal IReadOnlyList<InstallJob> JobsForTests => _jobs;

    async Task Uninstall(GameEntry g)
    {
        if (!await Confirm(S.ConfirmUninstall(g.Title, g.TitleId), S.Uninstall, danger: true)) return;
        try
        {
            await Task.Run(() => Directory.Delete(g.Path, true));
            Kit.ForgetImages(g.Path);
            SetStatus(S.Uninstalled(g.Title));
        }
        catch (Exception e) { SetStatus(S.UninstallFailed(e.Message)); }
        if (_page == PageGame) _page = PageLibrary;
        Refresh();
    }
}
