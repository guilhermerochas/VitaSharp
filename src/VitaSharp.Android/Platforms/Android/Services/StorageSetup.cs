using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Storage;

namespace VitaSharp.Android.Services;

/// <summary>
/// The device side of <see cref="DataRoot"/>: root, the all-files permission and the shared storage, and the first-start
/// flow of the screens' process (docs/android/README.md, "Storage"): without root it asks once for "All files access"
/// (Android 11+: the system's ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION screen; Android 8-10: the storage
/// permission), then moves the old app-specific folder to /storage/emulated/0/.vitasharp with a progress dialog
/// (<see cref="DataMigration"/>) and restarts the app on the new folder. When the visible folder already holds data and
/// the permission is missing (a reinstall resets it), it says where the data is and offers the permission or the
/// app-specific folder; when both folders hold the user's data, it shows what each holds and lets the user pick one or
/// merge them without overwriting (<see cref="StorageQuestion"/>).
/// </summary>
public static class StorageSetup
{
    const int PermissionRequestCode = 0x5700;
    const string StateFile = "storage_setup";
    const string AskedKey = "all_files_asked";
    const string ChoiceKey = "data_folder_choice";

    static bool _migrating;
    static AlertDialog? _question;

    /// <summary>What <see cref="DataRoot.Resolve"/> sees on this device now.</summary>
    public static DataRootInputs Inputs(Context context)
    {
        string? shared = null;
        if (global::Android.OS.Environment.ExternalStorageState == global::Android.OS.Environment.MediaMounted)
        {
            shared = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        }

        return new DataRootInputs(context.GetExternalFilesDir(null)?.AbsolutePath, context.FilesDir!.AbsolutePath, shared,
            RootDetector.HasSu(), HasAllFilesAccess(context), ReadChoice(context));
    }

    /// <summary>The folder the user picked (<see cref="StorageChoice"/>), kept in the app's private preferences until
    /// the app is uninstalled (allowBackup is off, so a reinstall starts with none).</summary>
    public static StorageChoice ReadChoice(Context context)
    {
        string? value = context.GetSharedPreferences(StateFile, FileCreationMode.Private)?.GetString(ChoiceKey, null);
        return Enum.TryParse(value, out StorageChoice choice) ? choice : StorageChoice.None;
    }

    static void SaveChoice(Context context, StorageChoice choice)
    {
        Log.Info($"Storage: data folder choice {choice}");
        // Commit, not Apply: the restart that may follow kills the process
        context.GetSharedPreferences(StateFile, FileCreationMode.Private)?.Edit()?.PutString(ChoiceKey, choice.ToString())?.Commit();
    }

    /// <summary>The library's card after "use the app folder": forgets the choice and asks again.</summary>
    public static void ChooseAgain(Activity activity)
    {
        SaveChoice(activity, StorageChoice.None);
        Check(activity);
    }

    /// <summary>"All files access" (Android 11+) or the storage permission (Android 8-10).</summary>
    public static bool HasAllFilesAccess(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return global::Android.OS.Environment.IsExternalStorageManager;
        }

        return ContextCompat.CheckSelfPermission(context, global::Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;
    }

    /// <summary>Opens the system screen that grants the permission (also from the library's warning card).</summary>
    public static void RequestAllFilesAccess(Activity activity)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            try
            {
                activity.StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission,
                    global::Android.Net.Uri.Parse("package:" + activity.PackageName)));
            }
            catch (ActivityNotFoundException)
            {
                activity.StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission));
            }

            return;
        }

        ActivityCompat.RequestPermissions(activity,
            [global::Android.Manifest.Permission.WriteExternalStorage, global::Android.Manifest.Permission.ReadExternalStorage],
            PermissionRequestCode);
    }

    /// <summary>
    /// Called when the screens' activity starts or comes back (from the permission screen above all): asks for the
    /// permission the first time, moves the data when there is something to move, and restarts the app when the data
    /// folder changed.
    /// </summary>
    public static void Check(Activity activity)
    {
        if (_migrating || !AppServices.Initialized)
        {
            return;
        }

        DataRootPlan plan = DataRoot.Resolve(Inputs(activity));
        AppServices.StoragePlan = plan;
        if (plan.NeedsMigration)
        {
            StartMigration(activity, plan);
            return;
        }

        if (!string.Equals(Path.GetFullPath(plan.Root), AppServices.PlannedRoot, StringComparison.Ordinal))
        {
            Log.Info($"Storage: the data folder is now {plan.Root}: restarting");
            Restart(activity);
            return;
        }

        if (_question != null)
        {
            return;
        }

        if (plan.Question == StorageQuestion.GrantForSharedData)
        {
            // a reinstall resets "All files access": the user's data is still in the visible folder, so say it
            // (every time the screens come back without the permission) instead of starting empty in silence
            Log.Warn($"Storage: VitaSharp data found in {plan.VisibleRoot} without All files access: asking");
            AskForSharedData(activity, plan);
            return;
        }

        if (plan.Question == StorageQuestion.ChooseBetweenFolders)
        {
            Log.Warn($"Storage: data in both {plan.Root} and {plan.VisibleRoot}: nothing is moved until the user picks");
            AskWhichFolder(activity, plan);
            return;
        }

        if (plan.NeedsPermission)
        {
            ISharedPreferences? state = activity.GetSharedPreferences(StateFile, FileCreationMode.Private);
            if (state != null && !state.GetBoolean(AskedKey, false))
            {
                state.Edit()?.PutBoolean(AskedKey, true)?.Apply();
                AskForPermission(activity);
            }
        }
    }

    static void AskForPermission(Activity activity)
    {
        new AlertDialog.Builder(activity)
            .SetTitle(Loc.Get("MStorageAccessTitle"))!
            .SetMessage(Loc.Get("MStorageAccessExplain"))!
            .SetPositiveButton(Loc.Get("MStorageAccessAllow"), (_, _) => RequestAllFilesAccess(activity))!
            .SetNegativeButton(Loc.Get("MLater"), (_, _) => { })!
            .Show();
    }

    /// <summary>The move or merge, once no game process writes into the old folder.</summary>
    static void StartMigration(Activity activity, DataRootPlan plan)
    {
        if (GameProcessMarker.Read(AppServices.Data.GameProcessFile, GameProcessMarker.IsGameProcess) is { } running)
        {
            // the game process writes into the old folder: the move waits until it closed
            Log.Info($"Storage: the data move waits for the game process ({running.TitleId}) to close");
            Toast.MakeText(activity, Loc.Get("MStorageMoveWaitsGame"), ToastLength.Long)?.Show();
            return;
        }

        Migrate(activity, plan);
    }

    /// <summary>No permission, data in the visible folder: grant (and come back to that folder) or use the app folder.</summary>
    static void AskForSharedData(Activity activity, DataRootPlan plan)
    {
        AlertDialog dialog = new AlertDialog.Builder(activity)
            .SetTitle(Loc.Get("MStorageSharedFoundTitle"))!
            .SetMessage(Loc.Format("MStorageSharedFoundExplain", plan.VisibleRoot ?? ""))!
            .SetPositiveButton(Loc.Get("MStorageGrantAccess"), (_, _) => RequestAllFilesAccess(activity))!
            .SetNegativeButton(Loc.Get("MStorageUseAppFolder"), (_, _) =>
            {
                SaveChoice(activity, StorageChoice.AppFolder);
                AppServices.StoragePlan = DataRoot.Resolve(Inputs(activity));
            })!
            .SetCancelable(false)!
            .Create()!;
        ShowQuestion(dialog);
    }

    /// <summary>Data in both folders: what each holds, and the visible one, the merge (overwrites nothing) or the app one.</summary>
    static void AskWhichFolder(Activity activity, DataRootPlan plan)
    {
        string visible = plan.VisibleRoot!;
        string old = plan.Root;
        AlertDialog dialog = new AlertDialog.Builder(activity)
            .SetTitle(Loc.Get("MStorageBothTitle"))!
            .SetMessage(Loc.Get("MStorageBothCounting"))!
            .SetPositiveButton(Loc.Get("MStorageUseShared"), (_, _) =>
            {
                SaveChoice(activity, StorageChoice.SharedFolder);
                Restart(activity);
            })!
            .SetNeutralButton(Loc.Get("MStorageMerge"), (_, _) =>
            {
                SaveChoice(activity, StorageChoice.None);
                StartMigration(activity, plan with { MigrateFrom = old });
            })!
            .SetNegativeButton(Loc.Get("MStorageUseAppFolder"), (_, _) =>
            {
                SaveChoice(activity, StorageChoice.AppFolder);
                AppServices.StoragePlan = DataRoot.Resolve(Inputs(activity));
            })!
            .SetCancelable(false)!
            .Create()!;
        ShowQuestion(dialog);
        // the counts walk both folders (thousands of files): off the UI thread
        Task.Run(() => (DataInventory.Summarize(visible), DataInventory.Summarize(old))).ContinueWith(task =>
        {
            (DataSummary shared, DataSummary app) = task.Result;
            activity.RunOnUiThread(() => dialog.SetMessage(Loc.Format("MStorageBothExplain", Describe(shared), Describe(app),
                DataMigration.KeptSuffix)));
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>One storage question on screen at a time (Check runs on every resume).</summary>
    static void ShowQuestion(AlertDialog dialog)
    {
        _question = dialog;
        dialog.DismissEvent += (_, _) => _question = null;
        dialog.Show();
    }

    static string Describe(DataSummary summary)
    {
        string firmware = summary.FirmwareVersion switch
        {
            null => Loc.Get("MStorageNoFirmware"),
            "" => Loc.Get("MStorageFirmwareInstalled"),
            string version => version,
        };
        return Loc.Format("MStorageSummary", summary.Folder, summary.Games, firmware, summary.Saves, summary.SaveStateTitles,
            summary.Files, summary.Bytes >> 20);
    }

    static void Migrate(Activity activity, DataRootPlan plan)
    {
        _migrating = true;
        var bar = new global::Android.Widget.ProgressBar(activity, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
        var text = new TextView(activity);
        var layout = new LinearLayout(activity) { Orientation = global::Android.Widget.Orientation.Vertical };
        int padding = (int)(20 * activity.Resources!.DisplayMetrics!.Density);
        layout.SetPadding(padding, padding / 2, padding, 0);
        layout.AddView(bar);
        layout.AddView(text);
        AlertDialog dialog = new AlertDialog.Builder(activity)
            .SetTitle(Loc.Get("MStorageMoving"))!
            .SetMessage(Loc.Format("MStorageMovingDetail", plan.VisibleRoot ?? ""))!
            .SetView(layout)!
            .SetCancelable(false)!
            .Create()!;
        dialog.Show();
        var progress = new Progress<MigrationProgress>(p =>
        {
            bar.Progress = p.BytesTotal > 0 ? (int)(p.BytesDone * 1000 / p.BytesTotal) : 0;
            text.Text = Loc.Format("MStorageMovingProgress", p.FilesDone, p.FilesTotal, p.BytesDone >> 20, p.BytesTotal >> 20);
        });
        string source = plan.MigrateFrom!;
        string target = plan.VisibleRoot!;
        Log.Info($"Storage: moving {source} to {target}");
        Task.Run(() =>
        {
            try
            {
                return new DataMigration(source, target).Run(progress);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new MigrationResult(MigrationStatus.Failed, e.Message, 0, 0);
            }
        }).ContinueWith(task =>
        {
            MigrationResult result = task.Result;
            Log.Info($"Storage: move {result.Status}, {result.Files} files, {result.Bytes} bytes {result.Message}");
            activity.RunOnUiThread(() =>
            {
                dialog.Dismiss();
                _migrating = false;
                if (result.Status is MigrationStatus.Done)
                {
                    Restart(activity);
                    return;
                }

                string message = result.Status switch
                {
                    MigrationStatus.NoSpace => Loc.Format("MStorageMoveNoSpace", result.Message),
                    MigrationStatus.VerifyFailed => Loc.Format("MStorageMoveProblems", target, DataMigration.ProblemsFileName),
                    _ => Loc.Format("MStorageMoveFailed", result.Message),
                };
                new AlertDialog.Builder(activity)
                    .SetTitle(Loc.Get("MStorageMoving"))!
                    .SetMessage(message)!
                    .SetPositiveButton(result.Status is MigrationStatus.VerifyFailed ? Loc.Get("MDone") : Loc.Get("MStorageMoveRetry"),
                        (_, _) =>
                        {
                            if (result.Status is MigrationStatus.VerifyFailed)
                            {
                                Restart(activity);
                            }
                            else
                            {
                                Check(activity);
                            }
                        })!
                    .SetNegativeButton(Loc.Get("MLater"), (_, _) => { })!
                    .SetCancelable(false)!
                    .Show();
            });
        });
    }

    /// <summary>A fresh process on the new data folder (every screen, the settings and the log open there).</summary>
    static void Restart(Activity activity)
    {
        Intent? launch = activity.PackageManager?.GetLaunchIntentForPackage(activity.PackageName!);
        if (launch == null)
        {
            return;
        }

        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
        activity.StartActivity(launch);
        activity.FinishAffinity();
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }
}
