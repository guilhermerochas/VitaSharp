using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Localization;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Settings > General > Firmware: the installed system software (or "not installed"), "Install firmware (.PUP)..."
/// with a progress bar and cancel, and "Use Vita3K's firmware" when a Vita3K installation with firmware is found
/// (its os0/vs0/sa0/pd0 are copied, never linked: VitaSharp then never writes into Vita3K's folders).
/// </summary>
public sealed class FirmwarePanel : StackPanel
{
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _detail = new() { Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    readonly Button _install = new() { Content = Loc.Get("FwInstallPup") };
    /// <summary>"Install font package (.PUP)...": shown while a firmware is installed without the system fonts (a
    /// system update carries no sa0); the same installer merges the font package into sa0.</summary>
    readonly Button _installFonts = new() { Content = Loc.Get("FwInstallFonts"), IsVisible = false };
    readonly Button _vita3k = new() { Content = Loc.Get("FwUseVita3K") };
    readonly Button _cancel = new() { Content = S.Cancel, IsVisible = false };
    readonly Button _uninstall = new() { Content = Loc.Get("FwUninstall") };
    readonly string? _vita3kRoot = FindVita3K();

    /// <summary>Where a Vita3K installation with firmware is (the tests give one so the panel shows every button).</summary>
    internal static Func<string?> FindVita3K { get; set; } = FirmwareInstaller.FindVita3KFirmware;
    CancellationTokenSource? _cts;

    /// <summary>Raised on the UI thread when an installation ends (done, failed or cancelled).</summary>
    public event Action? Finished;

    /// <summary>True while an installation runs (closing the settings window cancels it first).</summary>
    public bool Busy => _cts != null;

    /// <summary>Asks the user to confirm the uninstall (the launcher's dialog); without it nothing is removed.</summary>
    public Func<string, Task<bool>>? ConfirmUninstall { get; set; }

    public FirmwarePanel()
    {
        Spacing = 6;
        // the buttons go on in the next line when the card is narrow (a small window, the longer pt-BR labels)
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.Add(_install);
        buttons.Children.Add(_installFonts);
        if (_vita3kRoot != null)
        {
            ToolTip.SetTip(_vita3k, Loc.Format("FwUseVita3KTip", _vita3kRoot));
            buttons.Children.Add(_vita3k);
        }
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_uninstall);
        Children.Add(_status);
        Children.Add(buttons);
        Children.Add(_progress);
        Children.Add(_detail);
        Children.Add(new TextBlock
        {
            Foreground = LauncherTheme.Muted,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Text = Loc.Get("FwAbout"),
        });
        _install.Click += async (_, _) => await PickAndInstall();
        _installFonts.Click += async (_, _) => await PickAndInstall();
        _vita3k.Click += async (_, _) => await Run(Loc.Get("FwCopyingVita3K"),
            (p, ct) => FirmwareInstaller.ImportFrom(_vita3kRoot!, Program.FsRoot, "Vita3K", p, ct));
        _cancel.Click += (_, _) => { _cts?.Cancel(); _cancel.IsEnabled = false; };
        _uninstall.Click += async (_, _) => await Uninstall();
        Refresh();
    }

    /// <summary>"Uninstall firmware": after the confirmation, removes the partitions the installation created and its
    /// index files (FirmwareInstaller.Uninstall); games, saves and ux0/ur0 stay. The launcher then shows "no firmware".</summary>
    async Task Uninstall()
    {
        if (_cts != null || ConfirmUninstall == null || !await ConfirmUninstall(Loc.Get("FwUninstallConfirm")))
        {
            return;
        }

        _install.IsEnabled = false;
        _vita3k.IsEnabled = false;
        _uninstall.IsEnabled = false;
        _detail.IsVisible = true;
        _detail.Foreground = LauncherTheme.Muted;
        try
        {
            IReadOnlyList<string> removed = await Task.Run(() => FirmwareInstaller.Uninstall(Program.FsRoot));
            Refresh();
            _detail.Text = Loc.Format("FwUninstalled", removed.Count == 0 ? "-" : string.Join(", ", removed));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Refresh();
            _detail.Text = Loc.Format("FwFailed", e.Message);
            _detail.Foreground = Brushes.IndianRed;
        }
        finally
        {
            _install.IsEnabled = true;
            _vita3k.IsEnabled = true;
            _uninstall.IsEnabled = true;
        }
        Finished?.Invoke();
    }

    void Refresh()
    {
        FirmwareInfo? info = null;
        try
        {
            info = FirmwareInfo.Read(Program.FsRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // fonts count on their own: sa0 may have been filled without a firmware install (copied by hand)
        bool fonts = FirmwareInfo.HasSystemFonts(Program.FsRoot);
        _uninstall.IsVisible = info != null || fonts;
        _installFonts.IsVisible = info != null && !fonts;
        _status.Text = info == null
            ? Loc.Get("FwStatusMissing") + (fonts ? Loc.Get("FwFontsPresent") : "")
            : $"Firmware: {info.Display}" + (info.Partitions.Count > 0 ? $"  ({string.Join(", ", info.Partitions)})" : "") +
              Loc.Get(fonts ? "FwWithFonts" : "FwWithoutFonts");
    }

    async Task PickAndInstall()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Get("FwPickTitle"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc.Get("FwPupFileType")) { Patterns = ["*.PUP", "*.pup"] }, FilePickerFileTypes.All],
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null)
        {
            return;
        }

        await Run(Loc.Format("FwInstalling", Path.GetFileName(path)), (p, ct) =>
        {
            var info = FirmwareInstaller.Install(path, Program.FsRoot, p, ct);
            // a system update has no pd0 (the home screen music) and no sa0 (the system fonts): Vita3K's, when it has them
            info = FirmwareInstaller.ImportFactoryDataFromVita3KIfMissing(Program.FsRoot, ct) ?? info;
            return FirmwareInstaller.ImportSystemFontsFromVita3KIfMissing(Program.FsRoot, ct) ?? info;
        });
    }

    async Task Run(string title, Func<IProgress<FirmwareProgress>, CancellationToken, FirmwareInfo> job)
    {
        if (_cts != null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _install.IsEnabled = _vita3k.IsEnabled = _installFonts.IsEnabled = false;
        _cancel.IsVisible = _cancel.IsEnabled = true;
        _progress.IsVisible = _detail.IsVisible = true;
        _progress.Value = 0;
        _status.Text = title;
        _detail.Text = "";
        _detail.Foreground = LauncherTheme.Muted;
        var progress = new Progress<FirmwareProgress>(p =>
        {
            _progress.Value = p.Fraction;
            _detail.Text = $"{p.Stage} {p.Fraction:P0}" + (p.Detail != null ? "  " + p.Detail : "");
        });
        string? error = null;
        FirmwareInfo? result = null;
        try
        {
            var token = _cts.Token;
            result = await Task.Run(() => job(progress, token));
        }
        catch (OperationCanceledException)
        {
            error = Loc.Get("FwCancelled");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = Loc.Format("FwFailed", e.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _install.IsEnabled = _vita3k.IsEnabled = _installFonts.IsEnabled = true;
            _cancel.IsVisible = false;
            _progress.IsVisible = false;
        }
        // Progress<T> posts to the UI thread: let the last report land before the final text
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Refresh();
        if (error != null)
        {
            _detail.Text = error;
            _detail.Foreground = Brushes.IndianRed;
        }
        else if (!result!.FontPackage)
        {
            // a system update alone: the shell and many games draw no text until the font package is installed
            _detail.Text = Loc.Format("FwInstalledIn", result.Display, Program.FsRoot) + " " + Loc.Get("FwFontsNeeded");
            _detail.Foreground = Brushes.Orange;
        }
        else
        {
            _detail.Text = Loc.Format("FwInstalledIn", result.Display, Program.FsRoot);
        }

        Finished?.Invoke();
    }

    /// <summary>"Install font package (.PUP)...": the file picker at once (the Vita system's missing-fonts warning).</summary>
    public void PickFontPackage()
    {
        if (_cts == null)
        {
            _ = PickAndInstall();
        }
    }

    /// <summary>Cancels a running installation (window closing); <see cref="Finished"/> follows once it stopped.</summary>
    public void CancelRunning() => _cts?.Cancel();
}
