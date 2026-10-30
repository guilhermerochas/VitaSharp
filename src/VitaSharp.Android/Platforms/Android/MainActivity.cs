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
