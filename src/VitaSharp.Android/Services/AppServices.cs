using Android.Content;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Input;
using VitaSharp.Mobile.Core.Library;
using VitaSharp.Mobile.Core.Logging;
using VitaSharp.Mobile.Core.Settings;
using VitaSharp.Mobile.Core.Storage;
using Microsoft.Maui.Storage;

namespace VitaSharp.Android.Services;

/// <summary>
/// Process-wide state of the app: the data folder, the settings, the controller profiles and connected gamepads.
/// Initialised once per process by MainApplication.OnCreate: the UI process (MAUI) and the game process (GameActivity
/// runs in ":game", so every game starts in a fresh process like on the desktop) each have their own copy and share
/// only the files.
/// </summary>
public static class AppServices
{
    static readonly object Lock = new();
    static readonly Dictionary<string, GameSettings> GameSettingsCache = new();

    public static DataLayout Data { get; private set; } = null!;
    public static AppPreferences Prefs { get; private set; } = new();
    public static InputProfileStore InputProfiles { get; private set; } = new();
    /// <summary>The trophy sets' texts the library and the trophy screens read (icons stay in the TRP files), kept
    /// while a TRP is unchanged, so coming back to the library reads no TRP again.</summary>
    public static VitaSharp.Core.Hle.Np.TrophyTextsCache TrophyTexts { get; } = new();
    public static AndroidGamepads Gamepads { get; private set; } = new();
    public static bool Initialized { get; private set; }

    /// <summary>The data folder decision of this process (<see cref="DataRoot.Resolve"/>), refreshed by StorageSetup.</summary>
    public static DataRootPlan StoragePlan { get; set; }

    /// <summary>The folder this process resolved at its start (<see cref="Data"/> may be the internal one when it was
    /// unusable); a later decision for another folder restarts the app.</summary>
    public static string PlannedRoot { get; private set; } = "";

    /// <summary>The app's package context (Application), for content resolvers and system services.</summary>
    public static Context AppContext { get; private set; } = null!;

    /// <summary>
    /// Picks the data root (<see cref="DataRoot"/>: the visible .vitasharp folder, the app-specific external storage or
    /// the internal files), installs the logcat sink, opens the
    /// file log and loads the settings. The UI process logs to logs/app.log; the game process opens
    /// the log per title (SessionLog).
    /// </summary>
    /// <summary>Reads the settings file again (the other process may have changed it) and applies the texts'
    /// language: every text looked up from now on follows it.</summary>
    public static void RefreshPreferences()
    {
        lock (Lock)
        {
            if (!Initialized)
            {
                return;
            }

            Prefs = AppPreferences.Load(Data.AppSettingsFile);
            VitaSharp.Core.Localization.Loc.SetLanguage(Prefs.Language);
        }
    }

    /// <summary>
    /// The Vita's clock follows the phone's own time zone rules (java.util.TimeZone, kept up to date by Android), not the
    /// .NET runtime's copy of the zone data, which may be older than the phone's (VitaSharp.Core.Firmware.SystemTimeZone.Platform).
    /// The log line compares both, so a report shows a disagreement.
    /// </summary>
    static void UsePhoneTimeZone()
    {
        try
        {
            string id = Java.Util.TimeZone.Default?.ID ?? "UTC";
            VitaSharp.Core.Firmware.SystemTimeZone.Platform = (id, PhoneOffsetMinutes);
            DateTime now = DateTime.UtcNow;
            Log.Info($"Time zone: phone {id} UTC{PhoneOffsetMinutes(now) / 60.0:+0.##;-0.##}, "
                + $"runtime {TimeZoneInfo.Local.Id} UTC{TimeZoneInfo.Local.GetUtcOffset(now).TotalHours:+0.##;-0.##}; "
                + $"Vita zone {VitaSharp.Core.Firmware.SystemTimeZone.Host}, summer time {VitaSharp.Core.Firmware.SystemTimeZone.HostSummerTime}");
        }
        catch (Exception e) when (e is Java.Lang.Throwable or InvalidOperationException)
        {
            Log.Warn($"Time zone of the phone not read ({e.Message}): the runtime's is used");
        }
    }

    /// <summary>The phone's offset from UTC at <paramref name="utc"/>, in minutes, summer time included.</summary>
    static int PhoneOffsetMinutes(DateTime utc)
    {
        long milliseconds = (long)(utc - DateTime.UnixEpoch).TotalMilliseconds;
        return (Java.Util.TimeZone.Default?.GetOffset(milliseconds) ?? 0) / 60_000;
    }

    public static void Initialize(Context context, bool gameProcess)
    {
        lock (Lock)
        {
            if (Initialized)
            {
                // Android keeps the process for the next activity (a game after another one, the screens after a
                // game): the settings saved meanwhile by the other process (the language above all) apply now
                RefreshPreferences();
                return;
            }
            AppContext = context.ApplicationContext ?? context;
            LogcatSink.Install();
            // the data folder (Mobile.Core DataRoot): with root the app-specific folder; without root
            // /storage/emulated/0/.vitasharp once "All files access" is granted and the old folder was moved there
            // (StorageSetup, in the screens' process), the app-specific folder until then
            StoragePlan = DataRoot.Resolve(StorageSetup.Inputs(context));
            PlannedRoot = Path.GetFullPath(StoragePlan.Root);
            string root = StoragePlan.Root;
            Data = new DataLayout(root, context.FilesDir!.AbsolutePath);
            try
            {
                Data.EnsureCreated();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Data folder {root} unusable ({e.Message}): internal storage");
                Data = new DataLayout(context.FilesDir!.AbsolutePath);
                Data.EnsureCreated();
            }
            // config/env.txt: the desktop's VITASHARP_* switches, set before anything below reads them
            List<KeyValuePair<string, string>> env = [];
            try
            {
                env = EnvironmentFile.Apply(Path.Combine(Data.Config, EnvironmentFile.FileName));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"{EnvironmentFile.FileName}: {e.Message}");
            }
            // the UI process keeps logs/app.log (truncated per start); a game session writes logs/last.log (SessionLog)
            // (not while the old folder waits to be moved: the move checks every file's size)
            if (!gameProcess && !StoragePlan.NeedsMigration)
            {
                Log.OpenFile(Path.Combine(Data.Logs, "app.log"));
            }
            foreach (var (name, value) in env)
                Log.Info($"{EnvironmentFile.FileName}: {name}={value}");
            UsePhoneTimeZone();
            Prefs = AppPreferences.Load(Data.AppSettingsFile);
            // the texts' language (Ajustes > Language): set before any screen or the game reads a text
            VitaSharp.Core.Localization.Loc.SetLanguage(Prefs.Language);
            // debug mode: the setting, or VITASHARP_DEBUG_MODE=1 in config/env.txt (as --debug-mode / the variable on desktop)
            if (Prefs.DebugMode || System.Environment.GetEnvironmentVariable(EnvVars.DebugMode) is "1" or "on" or "true")
            {
                Log.Level = LogLevel.Debug;
                DebugMode.Enable();
            }
            InputProfiles = InputProfileStore.Load(Data.InputProfiles);
            Gamepads = new AndroidGamepads(InputProfiles);
#if VITASHARP_DEVELOPER
            Log.Info($"VitaSharp Dev (developer build) Android {(gameProcess ? "game" : "UI")} process, data in {Data.Root}");
#else
            Log.Info($"VitaSharp Android {(gameProcess ? "game" : "UI")} process, data in {Data.Root}");
#endif

            foreach (var line in RuntimeReport.Lines())
                Log.Info(line);
            Initialized = true;
        }
    }

    public static void SavePrefs()
    {
        lock (Lock)
        {
            try
            {
                Prefs.Save(Data.AppSettingsFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Settings: {e.Message}");
            }
        }
    }

    /// <summary>Re-reads the global settings (another process changed them).</summary>
    public static void ReloadPrefs()
    {
        lock (Lock)
        {
            Prefs = AppPreferences.Load(Data.AppSettingsFile);
            GameSettingsCache.Clear();
        }
    }

    public static GameSettings GameSettingsFor(string titleId)
    {
        lock (Lock)
        {
            if (!GameSettingsCache.TryGetValue(titleId, out var s))
                GameSettingsCache[titleId] = s = GameSettings.Load(Data.GameSettingsFile(titleId));
            return s;
        }
    }

    public static void SaveGameSettings(string titleId, GameSettings s)
    {
        lock (Lock)
        {
            GameSettingsCache[titleId] = s;
            try
            {
                s.Save(Data.GameSettingsFile(titleId));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Settings of {titleId}: {e.Message}");
            }
        }
    }

    public static GameEntry? FindGame(string titleId) => GameLibrary.Scan(Data).FirstOrDefault(g => g.TitleId == titleId);

    /// <summary>The RPCN password, kept in the Android Keystore (MAUI SecureStorage: EncryptedSharedPreferences).</summary>
    public static async Task<string> RpcnPasswordAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(RpcnPasswordKey) ?? "";
        }
        catch (Exception e)
        {
            Log.Warn($"Keystore: {e.Message}");
            return "";
        }
    }

    public static async Task SetRpcnPasswordAsync(string password)
    {
        try
        {
            if (password.Length == 0)
                SecureStorage.Default.Remove(RpcnPasswordKey);
            else
                await SecureStorage.Default.SetAsync(RpcnPasswordKey, password);
        }
        catch (Exception e)
        {
            Log.Warn($"Keystore: {e.Message}");
        }
    }

    const string RpcnPasswordKey = "rpcn_password";
}
