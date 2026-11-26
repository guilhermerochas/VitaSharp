using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using VitaSharp.Android.Services;
using VitaSharp.Mobile.Core.Input;

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
        bool gameRequested = LaunchRequestedGame(Intent);
        // "Ao abrir o emulador: Sistema do Vita": the firmware's shell boots, like turning the console on
        bool shellInstalled = File.Exists(System.IO.Path.Combine(AppServices.Data.Fs, "vs0", "vsh", "shell", "shell.self"));
        if (Mobile.Core.Settings.StartupViews.OpensSystem(AppServices.Prefs.StartupView, shellInstalled, savedInstanceState != null, gameRequested))
        {
            Core.Util.Log.Info("Startup view: Sistema do Vita");
            // without the system fonts the shell and its first-time setup draw no text: said once, the start goes on
            if (!Core.Firmware.FirmwareInfo.SystemReady(AppServices.Data.Fs))
            {
                Core.Util.Log.Warn("Startup view: the system fonts (sa0:data/font) are missing, the Vita system draws no text");
                global::Android.Widget.Toast.MakeText(this, VitaSharp.Core.Localization.Loc.Get("MFontsMissingToast"), global::Android.Widget.ToastLength.Long)?.Show();
            }
            Game.GameActivity.Launch(this, Game.GameActivity.SystemTitleId);
        }
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
    /// --es titleId PCSB01240</c>; <c>--ei loadSlot 4</c> opens it already in save state slot 4).
    /// Returns whether a game (or the developer bench) was started.
    /// </summary>
    bool LaunchRequestedGame(Intent? intent)
    {
#if VITASHARP_DEVELOPER
        // developer APK: a "bench" extra runs the GPU bench (Developer/BenchActivity) instead
        if (intent?.GetStringExtra(Developer.BenchActivity.ExtraBench) is { Length: > 0 } bench)
        {
            intent.RemoveExtra(Developer.BenchActivity.ExtraBench);
            Developer.BenchActivity.Launch(this, bench);
            return true;
        }
#endif

        if (intent?.GetStringExtra(Game.GameActivity.ExtraTitleId) is not { Length: > 0 } titleId)
        {
            return false;
        }

        intent.RemoveExtra(Game.GameActivity.ExtraTitleId);
        // --ei loadSlot N: the game opens already in that save state, as a slot tapped on the game page
        int loadSlot = Mobile.Core.Library.GameStates.LaunchSlot(intent.GetIntExtra(Game.GameActivity.ExtraLoadSlot, 0));
        intent.RemoveExtra(Game.GameActivity.ExtraLoadSlot);
        Core.Util.Log.Info(loadSlot > 0 ? $"Launch intent: starting {titleId} in save state slot {loadSlot}" : $"Launch intent: starting {titleId}");
        Game.GameActivity.Launch(this, titleId, wakeScreen: true, loadSlot: loadSlot);
        return true;
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
        // the data folder: asks for "All files access" once, moves the old folder when granted (StorageSetup)
        StorageSetup.Check(this);
        Resumed?.Invoke();
    }

    /// <summary>Raised when the activity comes back (after a game, a picker).</summary>
    public static event Action? Resumed;

    PadCapture? _capture;
    Action<VitaSharp.Core.Input.RecordedInput>? _captured;
    AlertDialog? _captureDialog;
    string _captureMessage = "";

    /// <summary>
    /// Controles > Mapeamento: the recording of <paramref name="capture"/> (the core's BindingRecorder: a game control
    /// takes the next press, the pause hotkey the whole combination held together, once released) goes to
    /// <paramref name="captured"/>. A dialog says what to press and, for a combination, the buttons held so far; Cancel
    /// or the Back key stops it. The controller's keys and motion events are taken while it waits, so they do not move
    /// the screens' focus.
    /// </summary>
    public void BeginPadCapture(PadCapture capture, string control, Action<VitaSharp.Core.Input.RecordedInput> captured)
    {
        EndPadCapture();
        _capture = capture;
        _captured = captured;
        _captureMessage = VitaSharp.Core.Localization.Loc.Get(capture.RecordsCombination ? "CsHoldCombo" : "CsPressButton");
        _captureDialog = new AlertDialog.Builder(this)
            .SetTitle(control)!
            .SetMessage(_captureMessage)!
            .SetNegativeButton(VitaSharp.Core.Localization.Loc.Get("MCancel"), (_, _) => EndPadCapture())!
            .SetOnCancelListener(new CaptureCancel(this))!
            .Show();
        if (capture.RecordsCombination)
        {
            TickPadCapture(capture);
        }
    }

    /// <summary>A combination ends once every button has been released for a moment, which no event announces: the
    /// recording is polled every 100 ms while it runs.</summary>
    void TickPadCapture(PadCapture capture)
    {
        Window?.DecorView?.PostDelayed(() =>
        {
            if (_capture != capture)
            {
                return;
            }

            if (capture.Tick() is { } recorded)
            {
                Captured(recorded);
                return;
            }

            TickPadCapture(capture);
        }, 100);
    }

    void EndPadCapture()
    {
        _capture = null;
        _captured = null;
        AlertDialog? dialog = _captureDialog;
        _captureDialog = null;
        dialog?.Dismiss();
    }

    void Captured(VitaSharp.Core.Input.RecordedInput recorded)
    {
        Action<VitaSharp.Core.Input.RecordedInput>? done = _captured;
        EndPadCapture();
        done?.Invoke(recorded);
    }

    /// <summary>The buttons of the combination held so far, under the dialog's prompt.</summary>
    void ShowCaptureParts(PadCapture capture)
    {
        if (!capture.RecordsCombination || _captureDialog == null || capture.Parts.Count == 0)
        {
            return;
        }

        string held = new VitaSharp.Core.Input.InputCombo(capture.Parts.ToArray()).Describe(capture.Kind);
        _captureDialog.SetMessage(_captureMessage + "\n\n" + held);
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (_capture is { } capture && e != null && e.DeviceId == capture.DeviceId && e.KeyCode != Keycode.Back)
        {
            if (e.RepeatCount == 0 && e.Action is KeyEventActions.Down or KeyEventActions.Up)
            {
                if (capture.OnKey(e.DeviceId, (int)e.KeyCode, e.Action == KeyEventActions.Down) is { } recorded)
                {
                    Captured(recorded);
                }
                else
                {
                    ShowCaptureParts(capture);
                }
            }

            return true;
        }

        return base.DispatchKeyEvent(e);
    }

    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        if (_capture is { } capture && e != null && e.DeviceId == capture.DeviceId)
        {
            if (capture.OnMotion(e.DeviceId, axis => e.GetAxisValue((Axis)axis)) is { } recorded)
            {
                Captured(recorded);
            }
            else
            {
                ShowCaptureParts(capture);
            }

            return true;
        }

        return base.DispatchGenericMotionEvent(e);
    }

    /// <summary>The capture dialog closed by Back or a touch outside it.</summary>
    sealed class CaptureCancel(MainActivity activity) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => activity.EndPadCapture();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        Saf.OnActivityResult(requestCode, resultCode, data);
    }
}
