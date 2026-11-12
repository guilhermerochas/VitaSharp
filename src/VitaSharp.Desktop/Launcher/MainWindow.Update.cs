using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VitaSharp.Core.Update;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Emulator updates (PCSX2-style): the check at start and from Configuracoes, and the update dialog.</summary>
public sealed partial class MainWindow
{
    bool _checkingUpdates;

    /// <summary>Checks the releases of the update channel; shows the dialog when there is a newer build. At start
    /// (<paramref name="manual"/> false) a skipped version and every error stay quiet.</summary>
    internal async Task CheckForUpdates(bool manual)
    {
        if (_checkingUpdates)
        {
            return;
        }

        var feed = Updater.Feed();
        if (feed == null)
        {
            if (manual)
            {
                SetStatus(S.NoUpdateFeed);
            }

            return;
        }
        _checkingUpdates = true;
        if (manual)
        {
            SetStatus(S.Checking);
        }

        try
        {
            string channel = _settings.UpdateChannel == UpdateFeed.ChannelReleases ? UpdateFeed.ChannelReleases : UpdateFeed.ChannelBuilds;
            var release = await Task.Run(() => Updater.CheckAsync(channel, feed));
            if (release == null)
            {
                if (manual)
                {
                    SetStatus(S.UpToDate(Updater.Current.Display));
                }

                return;
            }
            if (!manual && release.Tag == _settings.SkippedUpdate)
            {
                return;
            }

            await ShowUpdateDialog(release);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or TaskCanceledException)
        {
            AppendLog($"Update check failed: {e.Message}");
            if (manual)
            {
                SetStatus(S.UpdateCheckFailed(e.Message));
            }
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    /// <summary>The update dialog: version in use -> new one, date, the release notes, and Pular esta versao / Lembrar
    /// depois / Atualizar agora.</summary>
    internal Window UpdateDialog(UpdateRelease release)
    {
        Window? dlg = null;
        bool busy = false;
        var status = _k.Wrap(S.UpdateDataSafe, 12.5, static p => p.Muted);
        var progress = new Border { IsVisible = false, Child = _k.Bar(0, 6) };
        var notes = new ScrollViewer
        {
            MaxHeight = 280,
            Content = _k.Wrap(ReleaseNotes(release.Body), 12.5, static p => p.TextSoft),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var skip = _k.Secondary(S.SkipVersion, null, () =>
        {
            if (busy)
            {
                return;
            }

            _settings.SkippedUpdate = release.Tag;
            SaveSettings();
            dlg?.Close();
        }, 40);
        var later = _k.Secondary(S.RemindLater, null, () =>
        {
            if (!busy)
            {
                dlg?.Close();
            }
        }, 40);
        Button? now = null;
        now = _k.Primary(S.UpdateNow, "download", async () =>
        {
            if (busy)
            {
                return;
            }

            if (IsRunning)
            {
                status.Text = S.UpdateCloseGame;
                status.Foreground = Kit.B(static p => p.Danger);
                return;
            }
            busy = true;
            now!.IsEnabled = skip.IsEnabled = later.IsEnabled = false;
            progress.IsVisible = true;
            try
            {
                await Task.Run(() => Updater.InstallAsync(release, p => Dispatcher.UIThread.Post(() =>
                {
                    progress.Child = _k.Bar(p, 6);
                    status.Text = S.UpdateDownloading(p);
                }), msg => Dispatcher.UIThread.Post(() => AppendLog(msg)), []));
                status.Text = S.UpdateRestarting;
                // the new package's updater waits for this process to exit, copies itself over it and starts it again
                DispatcherTimer.RunOnce(() =>
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
                    {
                        life.Shutdown();
                    }
                    else
                    {
                        Environment.Exit(0);
                    }
                }, TimeSpan.FromMilliseconds(600));
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                busy = false;
                now!.IsEnabled = skip.IsEnabled = later.IsEnabled = true;
                progress.IsVisible = false;
                status.Text = S.UpdateFailed(e.Message);
                status.Foreground = Kit.B(static p => p.Danger);
                AppendLog($"Update failed: {e.Message}");
            }
        }, 40, 150);

        string next = release.IsBuild ? $"{release.Name} ({release.Tag})" : release.Name.Length > 0 ? release.Name : release.Tag;
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        buttons.Children.Add(skip);
        buttons.Children.Add(Kit.At(_k.HStack(8, later, now).Also(h => h.HorizontalAlignment = HorizontalAlignment.Right), col: 2));
        var panel = _k.VStack(12,
            _k.HStack(10, _k.IconChip("download", 40), _k.Heading(S.UpdateAvailable, 20).Also(h => h.VerticalAlignment = VerticalAlignment.Center)),
            _k.Text(S.UpdateFromTo(Updater.Current.Display, next), 14, FontWeight.SemiBold),
            _k.Text(S.UpdatePublished(release.Published), 12.5, color: static p => p.Muted),
            _k.Text(S.UpdateNotes, 13.5, FontWeight.SemiBold).Also(t => t.Margin = new Thickness(0, 6, 0, 0)),
            new Border
            {
                Background = Kit.B(static p => p.Background),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10),
                BorderBrush = Kit.B(static p => p.Border),
                BorderThickness = new Thickness(1),
                Child = notes,
            },
            progress, status, buttons);
        panel.Margin = new Thickness(24);
        dlg = Dialog(panel, 620);
        dlg.Closing += (_, e) =>
        {
            if (busy)
            {
                e.Cancel = true;
            }
        };
        return dlg;
    }

    async Task ShowUpdateDialog(UpdateRelease release) => await UpdateDialog(release).ShowDialog(this);

    /// <summary>The release body as plain text: the Markdown list of commits without links, headings without #.</summary>
    static string ReleaseNotes(string body)
    {
        var lines = body.Replace("\r", "").Split('\n').Select(l =>
        {
            l = l.TrimEnd();
            if (l.StartsWith('#'))
            {
                l = l.TrimStart('#', ' ');
            }

            l = System.Text.RegularExpressions.Regex.Replace(l, @"\[([^\]]*)\]\([^)]*\)", "$1").Replace("**", "").Replace("`", "");
            return l.StartsWith("- ") ? "\u2022  " + l[2..] : l;
        });
        return string.Join('\n', lines).Trim();
    }
}
