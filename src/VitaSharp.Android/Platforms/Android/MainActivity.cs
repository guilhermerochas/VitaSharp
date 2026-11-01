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
        base.OnCreate(savedInstanceState);
        // the screens are always dark (Ui/Theme), but the MAUI theme asks for dark status/navigation bar icons (a light
        // window): they were nearly invisible on the dark background
        if (Window is { } w && AndroidX.Core.View.WindowCompat.GetInsetsController(w, w.DecorView) is { } bars)
        {
            bars.AppearanceLightStatusBars = false;
            bars.AppearanceLightNavigationBars = false;
        }
        LaunchRequestedGame(Intent);
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
