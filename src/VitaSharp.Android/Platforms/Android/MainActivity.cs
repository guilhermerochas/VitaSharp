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

    /// <summary>Set on a second instance that only hands its intent to the first one and closes.</summary>
    bool _duplicate;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        if (Current is { IsDestroyed: false, IsFinishing: false } first && !ReferenceEquals(first, this))
        {
            ForwardToFirst(first, savedInstanceState);
            return;
        }
        Current = this;
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
    /// A launch intent that arrives while a game is in front (GameActivity on top of the task, so singleTop does not
    /// reuse the first MainActivity) creates a second MainActivity. The app has one MAUI window, which MAUI cannot hand
    /// to a second live activity ("This window is already associated with an active Activity"; detaching it from the
    /// first one fails later with "Window was already created"), so the second instance closes at once and the first
    /// one handles the intent (the game in front gets it as a new intent). MAUI's OnCreate runs Android's onCreate
    /// before it throws for the window, so the activity is in a valid state to finish.
    /// </summary>
    void ForwardToFirst(MainActivity first, Bundle? savedInstanceState)
    {
        _duplicate = true;
        Core.Util.Log.Info("Second MainActivity: its launch intent goes to the first one");
        try
        {
            base.OnCreate(savedInstanceState);
        }
        catch (InvalidOperationException e)
        {
            Core.Util.Log.Info($"Second MainActivity: {e.Message}");
        }
        var intent = Intent;
        Finish();
        first.LaunchRequestedGame(intent);
    }

    protected override void OnPostCreate(Bundle? savedInstanceState)
    {
        if (!_duplicate)
        {
            base.OnPostCreate(savedInstanceState);
            return;
        }
        // Android's onPostCreate runs first; MAUI's window events after it have no window for this instance
        try
        {
            base.OnPostCreate(savedInstanceState);
        }
        catch (InvalidOperationException e)
        {
            Core.Util.Log.Info($"Second MainActivity: {e.Message}");
        }
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

        if (intent?.GetStringExtra(Game.GameActivity.ExtraTitleId) is not { Length: > 0 } titleId)
            return;
        intent.RemoveExtra(Game.GameActivity.ExtraTitleId);
        Core.Util.Log.Info($"Launch intent: starting {titleId}");
        Game.GameActivity.Launch(this, titleId, wakeScreen: true);
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_duplicate)
            return;
        Current = this;
        // the game process may have changed the statistics and "last played"
        if (AppServices.Initialized)
            AppServices.ReloadPrefs();
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
