using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;
using VitaSharp.Core.Storage;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Configuracoes &gt; Avancado &gt; "Pasta de dados" &gt; "Alterar": the default folder, the portable one beside the
/// executable or a folder picked with the system's folder picker (<see cref="DataFolder"/>). The data in use is
/// copied or moved there first (<see cref="DataFolderTransfer"/>: free space checked, nothing overwritten, progress),
/// or left where it is; then the choice is saved outside every data folder and the launcher starts again on it.
/// The old folder is never deleted: a copy leaves it as it is, a move empties it file by file after each copy.
/// </summary>
public partial class MainWindow
{
    async Task ChangeDataFolder()
    {
        if (IsRunning)
        {
            await ShowMessage(S.DataFolderChange, S.DataFolderCloseGame);
            return;
        }

        if (Environment.GetEnvironmentVariable(EnvVars.Data) is { Length: > 0 })
        {
            await ShowMessage(S.DataFolderChange, S.DataFolderOverridden);
            return;
        }

        string exeDir = DataPaths.ExecutableDir();
        string defaultRoot = DataPaths.DefaultRoot();
        (DataFolderKind Kind, string? Custom)? picked = await AskDataFolderKind(exeDir, defaultRoot);
        if (picked is not { } choice)
        {
            return;
        }

        string? custom = choice.Custom;
        if (choice.Kind == DataFolderKind.Custom)
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = S.DataFolderPickTitle });
            custom = dirs.FirstOrDefault()?.TryGetLocalPath();
            if (custom == null)
            {
                return;
            }
        }

        string current = Program.DataRoot;
        string target = DataFolder.TargetOf(choice.Kind, custom, exeDir, defaultRoot);
        if (DataFolder.IsSameOrInside(target, current) && DataFolder.IsSameOrInside(current, target))
        {
            SaveDataFolderChoice(choice.Kind, custom, exeDir);
            await ShowMessage(S.DataFolderChange, S.DataFolderAlreadyInUse(target));
            return;
        }

        if (DataFolder.IsSameOrInside(target, current) || DataFolder.IsSameOrInside(current, target))
        {
            await ShowMessage(S.DataFolderChange, S.DataFolderNested(target));
            return;
        }

        DataFolderStep step = await AskDataTransfer(current, target);
        if (step == DataFolderStep.Cancel)
        {
            return;
        }

        if (step == DataFolderStep.Leave)
        {
            if (DataFolder.WhyUnusable(Directory.Exists(target) ? target : Path.GetDirectoryName(target) ?? target) is { } unusable)
            {
                await ShowMessage(S.DataFolderChange, S.DataFolderFailed(target + " " + unusable));
                return;
            }
        }
        else if (!await TransferData(current, target, step == DataFolderStep.Move ? DataTransferMode.Move : DataTransferMode.Copy))
        {
            return;
        }

        if (!SaveDataFolderChoice(choice.Kind, custom, exeDir))
        {
            await ShowMessage(S.DataFolderChange, S.DataFolderSaveFailed);
            return;
        }

        Log.Info($"Data folder: {choice.Kind} chosen; restarting on it");
        await ShowMessage(S.DataFolderChange, S.DataFolderRestarting(target));
        RestartLauncher();
    }

    /// <summary>The three choices; null when cancelled. "Leave the data where it is" is a separate question.</summary>
    async Task<(DataFolderKind Kind, string? Custom)?> AskDataFolderKind(string exeDir, string defaultRoot)
    {
        (DataFolderKind Kind, string? Custom)? result = null;
        Window? dlg = null;
        Button Option(string text, string detail, DataFolderKind kind, bool enabled)
        {
            var button = _k.Secondary(text, null, () =>
            {
                result = (kind, null);
                dlg?.Close();
            }, 40, enabled: enabled, tip: detail);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            return button;
        }

        bool portable = DataFolder.CanBePortable(exeDir);
        var cancel = _k.Secondary(S.Cancel, null, () => dlg?.Close(), 40);
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        var panel = _k.VStack(12,
            _k.Heading(S.DataFolderChange, 19),
            _k.Wrap(S.DataFolderChangeText(Program.DataRoot), 13.5, static p => p.TextSoft),
            Option(S.DataFolderDefault, defaultRoot, DataFolderKind.Default, true),
            _k.Wrap(defaultRoot, 12, static p => p.Muted),
            Option(S.DataFolderPortable, DataFolder.PortableRoot(exeDir), DataFolderKind.Portable, portable),
            _k.Wrap(portable ? DataFolder.PortableRoot(exeDir) : S.DataFolderPortableUnavailable, 12, static p => p.Muted),
            Option(S.DataFolderCustom, S.DataFolderPickTitle, DataFolderKind.Custom, true),
            cancel);
        panel.Margin = new Thickness(24);
        dlg = Dialog(panel, 520);
        await dlg.ShowDialog(this);
        return result;
    }

    /// <summary>What happens to the data in use: copied, moved, or left where it is (the new folder starts as it is).</summary>
    enum DataFolderStep
    {
        Cancel,
        Copy,
        Move,
        Leave,
    }

    /// <summary>Copy, move or leave the data (the sizes and the free space shown); a move is confirmed again.</summary>
    async Task<DataFolderStep> AskDataTransfer(string current, string target)
    {
        (long files, long bytes) = await Task.Run(() => DataFolderTransfer.Measure(current));
        long free = -1;
        try
        {
            free = (long)Math.Min(HostDisk.Of(target).Free, long.MaxValue);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        DataFolderStep result = DataFolderStep.Cancel;
        Window? dlg = null;
        var copy = _k.Primary(S.DataFolderCopy, null, () =>
        {
            result = DataFolderStep.Copy;
            dlg?.Close();
        }, 40, 90);
        var move = _k.Secondary(S.DataFolderMove, null, () =>
        {
            result = DataFolderStep.Move;
            dlg?.Close();
        }, 40);
        var none = _k.Secondary(S.DataFolderLeave, null, () =>
        {
            result = DataFolderStep.Leave;
            dlg?.Close();
        }, 40);
        var cancel = _k.Secondary(S.Cancel, null, () => dlg?.Close(), 40);
        var buttons = _k.HStack(8, cancel, none, move, copy);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var panel = _k.VStack(16, _k.Heading(S.DataFolderChange, 19),
            _k.Wrap(S.DataFolderTransferText(current, target, files, bytes >> 20, free < 0 ? "?" : (free >> 20).ToString(S.Culture)), 13.5, static p => p.TextSoft),
            buttons);
        panel.Margin = new Thickness(24);
        dlg = Dialog(panel, 600);
        await dlg.ShowDialog(this);
        if (result == DataFolderStep.Move && !await Confirm(S.DataFolderMoveConfirm(current), S.DataFolderMove, danger: true))
        {
            return DataFolderStep.Cancel;
        }

        return result;
    }

    /// <summary>The copy or move with a progress dialog; false when it did not end (the message says why; the choice is
    /// not saved, so the launcher stays on the folder in use).</summary>
    async Task<bool> TransferData(string current, string target, DataTransferMode mode)
    {
        if (!Directory.Exists(current))
        {
            Directory.CreateDirectory(target);
            return true;
        }

        var bar = new Border { Child = _k.Bar(0, 6) };
        var status = _k.Wrap(S.DataFolderCounting, 13, static p => p.TextSoft);
        var panel = _k.VStack(14, _k.Heading(mode == DataTransferMode.Move ? S.DataFolderMoving : S.DataFolderCopying, 18), bar, status);
        panel.Margin = new Thickness(24);
        Window dlg = Dialog(panel, 520);
        bool allowClose = false;
        dlg.Closing += (_, e) => e.Cancel = !allowClose;
        var progress = new Progress<DataTransferProgress>(p =>
        {
            bar.Child = _k.Bar(p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0, 6);
            status.Text = S.DataFolderProgress(p.FilesDone, p.FilesTotal, p.BytesDone >> 20, p.BytesTotal >> 20);
        });
        Task<DataTransferResult> work = Task.Run(() =>
        {
            try
            {
                return new DataFolderTransfer(current, target, mode).Run(progress);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new DataTransferResult(DataTransferStatus.Failed, e.Message, 0, 0, []);
            }
        });
        _ = work.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            allowClose = true;
            dlg.Close();
        }));
        await dlg.ShowDialog(this);
        DataTransferResult result = await work;
        Log.Info($"Data folder: {mode} {result.Status}, {result.Files} files, {result.Bytes} bytes {result.Message}");
        switch (result.Status)
        {
            case DataTransferStatus.Done when result.Kept.Count > 0:
                await ShowMessage(S.DataFolderChange, S.DataFolderKept(result.Kept.Count, string.Join("\n", result.Kept.Take(10))));
                return true;
            case DataTransferStatus.Done:
                return true;
            case DataTransferStatus.NoSpace:
                await ShowMessage(S.DataFolderChange, S.DataFolderNoSpace(result.Message));
                return false;
            default:
                await ShowMessage(S.DataFolderChange, S.DataFolderFailed(result.Message));
                return false;
        }
    }

    static bool SaveDataFolderChoice(DataFolderKind kind, string? custom, string exeDir)
    {
        try
        {
            DataFolder.SaveChoice(kind, custom, DataPaths.PointerPath(), exeDir);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Data folder: the choice could not be saved: {e.Message}");
            return false;
        }
    }

    /// <summary>A new launcher process (same command line) and this one closes: every path opens on the new folder.</summary>
    static void RestartLauncher()
    {
        string exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        string[] args = Environment.GetCommandLineArgs();
        // a framework-dependent build runs through "dotnet VitaSharp.Desktop.dll": the .dll path comes first
        int first = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        for (int i = first; i < args.Length; i++)
        {
            psi.ArgumentList.Add(args[i]);
        }

        Process.Start(psi);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
        {
            life.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
    }
}
