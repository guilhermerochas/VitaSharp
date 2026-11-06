#if VITASHARP_DEVELOPER
using Android.App;
using Android.OS;

namespace VitaSharp.Android.Developer;

/// <summary>
/// Developer APK only: a blank screen that keeps the phone awake between scripted runs (HyperOS refuses
/// <c>svc power stayon</c>, secure settings and <c>adb shell input</c>). It turns the screen on, shows over the lock
/// screen and keeps it on while in front:
/// <c>adb shell am start -n io.github.vitasharp/io.github.vitasharp.AwakeActivity</c>; <c>--ez finish true</c> closes it.
/// </summary>
[Activity(Name = "io.github.vitasharp.AwakeActivity", Label = "VitaSharp Awake", Process = ":awake", Exported = true,
    Theme = "@android:style/Theme.Black.NoTitleBar", LaunchMode = global::Android.Content.PM.LaunchMode.SingleTask)]
public sealed class AwakeActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Close()) return;
        Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
        if (OperatingSystem.IsAndroidVersionAtLeast(27)) { SetTurnScreenOn(true); SetShowWhenLocked(true); } // CA1416: API 27
        SetContentView(new global::Android.Widget.TextView(this) { Text = "VitaSharp Dev: tela mantida ligada", TextSize = 16 });
    }

    protected override void OnNewIntent(global::Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        Close();
    }

    bool Close()
    {
        if (Intent?.GetBooleanExtra("finish", false) != true) return false;
        FinishAndRemoveTask();
        return true;
    }
}
#endif
