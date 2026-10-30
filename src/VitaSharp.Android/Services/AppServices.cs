using Android.Content;
using VitaSharp.Android.Input;
using VitaSharp.Android.Library;
using VitaSharp.Android.Logging;
using VitaSharp.Android.Settings;
using VitaSharp.Android.Storage;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;
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
    public static AndroidGamepads Gamepads { get; private set; } = new();
    public static bool Initialized { get; private set; }

    /// <summary>The app's package context (Application), for content resolvers and system services.</summary>
    public static Context AppContext { get; private set; } = null!;

    /// <summary>
    /// Picks the data root (app-specific external storage, else internal files), installs the logcat sink, opens the
    /// file log and loads the settings. The UI process logs to logs/app.log; the game process opens
    /// the log per title (SessionLog).
    /// </summary>
    public static void Initialize(Context context, bool gameProcess)
    {
        lock (Lock)
        {
            if (Initialized) return;
            AppContext = context.ApplicationContext ?? context;
            LogcatSink.Install();
            string root = context.GetExternalFilesDir(null)?.AbsolutePath is { Length: > 0 } ext ? ext : context.FilesDir!.AbsolutePath;
            Data = new DataLayout(root);
            try { Data.EnsureCreated(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Data folder {root} unusable ({e.Message}): internal storage");
                Data = new DataLayout(context.FilesDir!.AbsolutePath);
                Data.EnsureCreated();
            }
            // the UI process keeps logs/app.log (truncated per start); a game session writes logs/last.log (SessionLog)
            if (!gameProcess) Log.OpenFile(Path.Combine(Data.Logs, "app.log"));
            Prefs = AppPreferences.Load(Data.AppSettingsFile);
            if (Prefs.DebugMode) { Log.Level = LogLevel.Debug; DebugMode.Enable(); }
            InputProfiles = InputProfileStore.Load(Data.InputProfiles);
            Gamepads = new AndroidGamepads(InputProfiles);
            Log.Info($"VitaSharp Android {(gameProcess ? "game" : "UI")} process, data in {Data.Root}");
            foreach (var line in RuntimeReport.Lines()) Log.Info(line);
            Initialized = true;
        }
    }

    public static void SavePrefs()
    {
        lock (Lock)
        {
            try { Prefs.Save(Data.AppSettingsFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Settings: {e.Message}"); }
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
            try { s.Save(Data.GameSettingsFile(titleId)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Settings of {titleId}: {e.Message}"); }
        }
    }

    public static GameEntry? FindGame(string titleId) => GameLibrary.Scan(Data).FirstOrDefault(g => g.TitleId == titleId);

    /// <summary>The RPCN password, kept in the Android Keystore (MAUI SecureStorage: EncryptedSharedPreferences).</summary>
    public static async Task<string> RpcnPasswordAsync()
    {
        try { return await SecureStorage.Default.GetAsync(RpcnPasswordKey) ?? ""; }
        catch (Exception e) { Log.Warn($"Keystore: {e.Message}"); return ""; }
    }

    public static async Task SetRpcnPasswordAsync(string password)
    {
        try
        {
            if (password.Length == 0) SecureStorage.Default.Remove(RpcnPasswordKey);
            else await SecureStorage.Default.SetAsync(RpcnPasswordKey, password);
        }
        catch (Exception e) { Log.Warn($"Keystore: {e.Message}"); }
    }

    const string RpcnPasswordKey = "rpcn_password";
}
