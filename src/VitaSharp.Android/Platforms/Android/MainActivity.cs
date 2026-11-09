using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using VitaSharp.Android.Services;

namespace VitaSharp.Android;

/// <summary>The MAUI host activity of the screens (portrait). Storage Access Framework results come back here.</summary>
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, Exported = true,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : MauiAppCompatActivity
{
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        ReleaseWindowFromOtherActivity();
        base.OnCreate(savedInstanceState);
        // the screens are always dark (Ui/Theme), but the MAUI theme asks for dark status/navigation bar icons (a light
        // window): they were nearly invisible on the dark background
        if (Window is { } w && AndroidX.Core.View.WindowCompat.GetInsetsController(w, w.DecorView) is { } bars)
        {
            bars.AppearanceLightStatusBars = false;
            bars.AppearanceLightNavigationBars = false;
        }
#if VITASHARP_DEVELOPER
        Developer.UiScript.Start(this);
#endif
        LaunchRequestedGame(Intent);
    }

    /// <summary>
    /// The app has one MAUI window (MauiReactor's application hands the same one to every activity). MAUI refuses to
    /// attach it to a new MainActivity while it still belongs to another live one ("This window is already associated
    /// with an active Activity"), which killed 4 of 10 cold launches of the Native AOT build. The new activity is the
    /// one on screen: the window is detached from the old one first (logged, with what the old one was).
    /// </summary>
    void ReleaseWindowFromOtherActivity()
    {
        try
        {
            if (Microsoft.Maui.Controls.Application.Current?.Windows is not { Count: > 0 } windows) return;
            foreach (var w in windows)
            {
                if (w.Handler?.PlatformView is not Activity old || ReferenceEquals(old, this) || old.IsDestroyed) continue;
                bool sameJava = old.Handle == Handle;
                Core.Util.Log.Warn($"MAUI window still attached to another {old.GetType().Name} (same Java object {sameJava}, " +
                                   $"finishing {old.IsFinishing}): detached for the new activity");
                w.Handler?.DisconnectHandler();
                if (!sameJava && !old.IsFinishing) old.Finish();
            }
        }
        catch (Exception e) { Core.Util.Log.Warn($"MAUI window check: {e.Message}"); }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        LaunchRequestedGame(intent);
    }

    /// <summary>
    /// A launcher intent with a "titleId" extra starts that game directly (home-screen shortcuts, and testing on a
    /// device: <c>adb shell am start -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p io.github.vitasharp
    /// --es titleId PCSB01240</c>).
    /// </summary>
    void LaunchRequestedGame(Intent? intent)
    {
#if VITASHARP_DEVELOPER
        // developer APK: a "bench" extra runs the GPU bench (Developer/BenchActivity) instead
        if (intent?.GetStringExtra(Developer.BenchActivity.ExtraBench) is { Length: > 0 } bench)
        {
            intent.RemoveExtra(Developer.BenchActivity.ExtraBench);
            Developer.BenchActivity.Launch(this, bench);
            return;
        }
#endif

        if (intent?.GetStringExtra(Game.GameActivity.ExtraTitleId) is not { Length: > 0 } titleId) return;
        intent.RemoveExtra(Game.GameActivity.ExtraTitleId);
        Core.Util.Log.Info($"Launch intent: starting {titleId}");
        Game.GameActivity.Launch(this, titleId, wakeScreen: true);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        // the game process may have changed the statistics and "last played"
        if (AppServices.Initialized) AppServices.ReloadPrefs();
        Resumed?.Invoke();
    }

    /// <summary>Raised when the activity comes back (after a game, a picker).</summary>
    public static event Action? Resumed;

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        Saf.OnActivityResult(requestCode, resultCode, data);
    }
}
