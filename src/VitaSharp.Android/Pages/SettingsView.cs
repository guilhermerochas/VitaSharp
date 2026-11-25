using VitaSharp.Core.Localization;
using MauiReactor;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Firmware;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Android.Pages;

public sealed class SettingsState
{
    public FirmwareStatus? Firmware { get; set; }
    public int Revision { get; set; }
}

/// <summary>
/// Ajustes (ajustes.html): Sistema (firmware status, Sistema do Vita), Desempenho (the default CPU backend, clock and
/// GPU profile of every game, VSync, FPS, GPU driver and GPU information), Rede e PSN, Armazenamento (where the data
/// folder is) and Depuracao (debug logging, the log files).
/// </summary>
public sealed class SettingsView : Screen<SettingsState>
{
    protected override void OnMounted()
    {
        MainActivity.Resumed += Refresh;
        Refresh();
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        MainActivity.Resumed -= Refresh;
        base.OnWillUnmount();
    }

    void Refresh() => _ = Task.Run(() =>
    {
        var fw = FirmwareStatus.Read(AppServices.Data.Fs);
        SetState(s => s.Firmware = fw);
    });

    public override VisualNode Render()
    {
        var p = AppServices.Prefs;
        var fw = State.Firmware;
        return PageBody(Loc.Get("MSettings"),
            VStack(
                SectionTitle(Loc.Get("MGames")),
                Card(
                    Row(Loc.Get("MInstallGame"), null, () =>
                    {
                        LibraryView.InstallRequested = true;
                        RootPage.Show(RootTab.Library);
                    }, detail: Loc.Get("MInstallGameDetail"))
                ),
                SectionTitle(Loc.Get("Appearance")),
                Card(
                    Row(Loc.Get("Language"), SettingsChoices.LabelOf(AppPreferences.Languages, p.Language), PickLanguage,
                        detail: Loc.Get("MLanguageDetail"))
                ),
                SectionTitle(Loc.Get("MSystem")),
                Card(
                    Row(Loc.Get("MFirmware"), fw?.Summary ?? "\u2026", () => Navigation?.PushAsync<FirmwarePage>(),
                        valueColor: fw?.Installed != null ? Palette.Ok : Palette.Muted),
                    Divider(),
                    Row(Loc.Get("MVitaSystem"), fw is { Shell: true } ? Loc.Get("MReady") : Loc.Get("MNeedsFirmware"), () =>
                    {
                        if (fw is { Shell: true })
                            Game.GameActivity.Launch(MainActivity.Current!, Game.GameActivity.SystemTitleId);
                        else
                            Navigation?.PushAsync<FirmwarePage>();
                    }, detail: Loc.Get("MShellDetail")),
                    Divider(),
                    Row(Loc.Get("StartupView"), SettingsChoices.LabelOf(StartupViews.Choices, p.StartupView),
                        () => Pick(Loc.Get("StartupView"), StartupViews.Choices, v => p.StartupView = v),
                        detail: Loc.Get("StartupViewDetail"))
                ),
                SectionTitle(Loc.Get("MCameras")),
                Card(
                    Row(Loc.Get("CameraFront"), CameraSettings.Label(p.CameraFront), () => PickCamera(CameraFacing.Front),
                        detail: Loc.Get("MCameraFrontDetail")),
                    Divider(),
                    Row(Loc.Get("CameraBack"), CameraSettings.Label(p.CameraBack), () => PickCamera(CameraFacing.Back),
                        detail: Loc.Get("MCameraBackDetail"))
                ),
                SectionTitle(Loc.Get("Microphone")),
                Card(
                    Row(Loc.Get("Microphone"), MicrophoneSettings.Label(p.Microphone),
                        () => Pick(Loc.Get("Microphone"), MicrophoneSettings.Choices, v => p.Microphone = v),
                        detail: Loc.Get("MMicrophoneDetail"))
                ),
                SectionTitle(Loc.Get("BluetoothKeyboardMouse")),
                Card(
                    ToggleRow(Loc.Get("BluetoothKeyboardMouse"), p.BluetoothKeyboardMouse, v => Change(x => x.BluetoothKeyboardMouse = v),
                        detail: Loc.Get("MBluetoothKeyboardMouseDetail"))
                ),
                SectionTitle(Loc.Get("Location")),
                Card(
                    Row(Loc.Get("Location"), LocationSettings.Label(p.Location),
                        () => Pick(Loc.Get("Location"), LocationSettings.Choices, v => p.Location = v),
                        detail: Loc.Get("MLocationDetail"))
                ),
                SectionTitle(Loc.Get("MPerformance")),
                Card(
                    Row("CPU", SettingsChoices.LabelOf(CpuChoices.Available(), p.CpuBackend),
                        () => Pick("CPU", CpuChoices.Available(), v => p.CpuBackend = v), detail: Loc.Get("MDefaultAllGames")),
                    Divider(),
                    Row(Loc.Get("MCpuClock"), SettingsChoices.LabelOf(SettingsChoices.Clocks, p.CpuMhz.ToString()),
                        () => Pick(Loc.Get("MCpuClock"), SettingsChoices.Clocks, v => p.CpuMhz = int.Parse(v))),
                    Divider(),
                    Row(Loc.Get("MGpuProfile"), SettingsChoices.LabelOf(SettingsChoices.GpuProfiles, p.GpuProfile),
                        () => Pick(Loc.Get("MGpuProfile"), SettingsChoices.GpuProfiles, v => p.GpuProfile = v),
                        detail: Loc.Get("MGpuProfileDetail")),
                    Divider(),
                    Row(Loc.Get("MInternalResolution"), SettingsChoices.LabelOf(SettingsChoices.ResScales, p.ResScale),
                        () => Pick(Loc.Get("MInternalResolution"), SettingsChoices.ResScales, v => p.ResScale = v),
                        detail: Loc.Get("MInternalResolutionGlobalDetail")),
                    Divider(),
                    ToggleRow("VSync", p.VSync, v => Change(x => x.VSync = v)),
                    Divider(),
                    ToggleRow(Loc.Get("MShowFps"), p.ShowFps, v => Change(x => x.ShowFps = v)),
                    Divider(),
                    ToggleRow(Loc.Get("FastShaders"), p.FastShaderCompile, v => Change(x => x.FastShaderCompile = v),
                        detail: Loc.Get("FastShadersDetail")),
                    Divider(),
                    ToggleRow(Loc.Get("AsyncShaders"), p.BackgroundShaderCompile, v => Change(x => x.BackgroundShaderCompile = v),
                        detail: Loc.Get("AsyncShadersDetail")),
                    Divider(),
                    ToggleRow(Loc.Get("LoadShaderCache"), p.LoadShaderCacheBeforePlaying, v => Change(x => x.LoadShaderCacheBeforePlaying = v),
                        detail: Loc.Get("LoadShaderCacheGlobalDetail")),
                    Divider(),
                    ToggleRow(Loc.Get("PrecompileCode"), p.PrecompileCode, v => Change(x => x.PrecompileCode = v),
                        detail: Loc.Get("PrecompileCodeDetail")),
                    Divider(),
                    ToggleRow(Loc.Get("FastLoading"), p.FastLoading, v => Change(x => x.FastLoading = v),
                        detail: Loc.Get("FastLoadingGlobalDetail")),
                    Divider(),
                    ToggleRow(Loc.Get("MTrophySound"), p.TrophySound, v => Change(x => x.TrophySound = v),
                        detail: Loc.Get("MTrophySoundDetail")),
                    Divider(),
                    Row(Loc.Get("MGpuDriver"), null, () => Navigation?.PushAsync<GpuDriverPage>()),
                    Divider(),
                    Row(Loc.Get("MGpuInfo"), null, () => Navigation?.PushAsync<GpuInfoPage>())
                ),
                SectionTitle(Loc.Get("MNetworkPsn")),
                Card(
                    Row(Loc.Get("MPsnAndNetwork"), p.Network.PsnSignedIn ? p.Network.PsnOnlineId : Loc.Get("MSignedOut"),
                        () => Navigation?.PushAsync<PsnPage>(),
                        detail: p.Network.RealInternet ? Loc.Get("MRealInternetOn") : Loc.Get("MNothingLeaves"))
                ),
                SectionTitle(Loc.Get("MStorage")),
                Card(
                    Row(Loc.Get("MDataFolder"), null, Copy(AppServices.Data.Root), detail: AppServices.Data.Root),
                    Divider(),
                    Row(Loc.Get("MInstalledGames"), null, Copy(AppServices.Data.Apps), detail: Loc.Format("MInstalledGamesDetail", VitaSharp.Core.Io.FirmwareLayout.Applications.Relative(), VitaSharp.Core.Io.FirmwareLayout.SaveData.Relative()))
                ),
                Note(Loc.Get("MStorageNote")),
                SectionTitle(Loc.Get("MDebugging")),
                Card(
                    ToggleRow(Loc.Get("MDebugMode"), p.DebugMode, v => Change(x => x.DebugMode = v),
                        detail: Loc.Get("MDebugModeDetail")),
                    Divider(),
                    ToggleRow(Loc.Get("MKernelPlugins"), p.KernelPlugins, v => Change(x => x.KernelPlugins = v),
                        detail: Loc.Get("MKernelPluginsDetail")),
                    Divider(),
                    Row(Loc.Get("MLastGameLog"), null, Copy(System.IO.Path.Combine(AppServices.Data.Logs, "last.log")), detail: "logs/last.log"),
                    Divider(),
                    Row(Loc.Get("MShareLog"), null, ShareLog)
#if VITASHARP_DEVELOPER
                    , Divider(),
                    Row(Loc.Get("MDeveloperOptions"), null, () => Navigation?.PushAsync<DeveloperPage>(), detail: Loc.Get("MDeveloperOptionsDetail"))
#endif
                ),

                Note($"VitaSharp \u00B7 {RuntimeReport.Runtime}")
            ));
    }

    async void Pick(string title, IReadOnlyList<Choice> choices, Action<string> apply)
    {
        var v = await Choose(title, choices);
        if (v != null)
            Change(_ => apply(v));
    }

    /// <summary>"Camera frontal" / "Camera traseira": the phone's camera, a picture (picked with the system's file
    /// picker and copied into the data folder, so the game process can read it) or none. Applies on the next start.</summary>
    async void PickCamera(CameraFacing facing)
    {
        string title = Loc.Get(facing == CameraFacing.Front ? "CameraFront" : "CameraBack");
        string? choice = await Choose(title, CameraSettings.Choices);
        if (choice == null)
        {
            return;
        }

        if (choice != CameraSettings.PictureChoice)
        {
            SetCamera(facing, choice);
            return;
        }

        PickedDocument? picked = await Saf.PickAsync("image/png", "image/jpeg");
        if (picked == null)
        {
            return;
        }

        if (CameraSettings.PictureSetting(facing, picked.DisplayName) is not { } setting)
        {
            await Alert(title, Loc.Get("MCameraPictureInvalid"));
            return;
        }

        await Run(title, async () =>
        {
            string root = AppServices.Data.Root;
            string cache = System.IO.Path.Combine(AppServices.AppContext.CacheDir!.AbsolutePath, "imports");
            await Task.Run(() =>
            {
                string target = System.IO.Path.Combine(root, setting);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                string temporary = target + ".tmp";
                using (Stream source = picked.OpenSeekable(cache))
                {
                    using FileStream output = File.Create(temporary);
                    source.CopyTo(output);
                }

                File.Move(temporary, target, true);
            });
            SetCamera(facing, setting);
        });
    }

    void SetCamera(CameraFacing facing, string value)
    {
        if (facing == CameraFacing.Front)
        {
            Change(x => x.CameraFront = value);
        }
        else
        {
            Change(x => x.CameraBack = value);
        }
    }

    /// <summary>The language row: the choice is saved and applied at once; the screens already built keep their texts
    /// until the app starts again, which it offers to do.</summary>
    async void PickLanguage()
    {
        var v = await Choose(Loc.Get("Language"), AppPreferences.Languages);
        if (v == null || v == AppServices.Prefs.Language)
            return;
        Change(x => x.Language = v);
        Loc.SetLanguage(v);
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentCulture = Loc.Culture;
        if (await Confirm(Loc.Get("Language"), Loc.Get("MLanguageRestart"), Loc.Get("MRestart"), Loc.Get("MLater")))
            RestartApp();
    }

    /// <summary>Starts the app's main screen in a new task and ends this process (the UI process only: a game runs in
    /// its own).</summary>
    static void RestartApp()
    {
        var context = MainActivity.Current ?? (global::Android.Content.Context)AppServices.AppContext;
        if (context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!) is not { } intent)
            return;
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask | global::Android.Content.ActivityFlags.ClearTask);
        context.StartActivity(intent);
        Log.Info("Language changed: restarting the app");
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    void Change(Action<AppPreferences> change)
    {
        change(AppServices.Prefs);
        AppServices.SavePrefs();
        if (AppServices.Prefs.DebugMode)
        {
            Log.Level = LogLevel.Debug;
            DebugMode.Enable();
        }
        else
        {
            Log.Level = LogLevel.Info;
            DebugMode.Disable();
        }
        SetState(s => s.Revision++);
    }

    Action Copy(string text) => async () =>
    {
        await Clipboard.Default.SetTextAsync(text);
        await Alert(Loc.Get("MCopied"), text);
    };

    async void ShareLog()
    {
        string path = System.IO.Path.Combine(AppServices.Data.Logs, "last.log");
        if (!File.Exists(path))
            path = System.IO.Path.Combine(AppServices.Data.Logs, "app.log");
        if (!File.Exists(path))
        {
            await Alert("Log", Loc.Get("MNoLogYet"));
            return;
        }
        await Share.Default.RequestAsync(new ShareFileRequest(Loc.Get("MVitaSharpLog"), new ShareFile(path, "text/plain")));
    }
}
