#if VITASHARP_DEVELOPER
using Android.App;
using Android.OS;
using Android.Views;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Developer;

/// <summary>
/// Developer builds: VITASHARP_ANDROID_UI_INPUT names a file polled every 250 ms on the UI thread of an activity (the
/// game screen polls "&lt;path&gt;.game", the app's screens "&lt;path&gt;", since they run in two processes), so it also
/// works while the game is paused and the scripted pad is not polled. Each line is injected into that activity through
/// <see cref="Instrumentation"/> (an app may inject into its own windows): "key:dpad_down" / "key:button_a" (any
/// KEYCODE_ name, sent as a gamepad key; "key:back" is the system back) or "touch:x:y" (a tap at x, y in 0..1 of the
/// window). The file is deleted once read. Device checks of the pause menu and of the screens use it on phones that
/// refuse <c>adb shell input</c>.
/// </summary>
public static class UiScript
{
    public static void Start(Activity activity, string suffix = "")
    {
        if (System.Environment.GetEnvironmentVariable(EnvVars.AndroidUiInput) is not { Length: > 0 } basePath) return;
        string path = basePath + suffix;
        Log.Info($"{EnvVars.AndroidUiInput}: polling {path}");
        var handler = new Handler(Looper.MainLooper!);
        void Poll()
        {
            if (activity.IsDestroyed) return;
            try
            {
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    File.Delete(path);
                    int w = activity.Window?.DecorView.Width ?? 0, h = activity.Window?.DecorView.Height ?? 0;
                    Task.Run(() => Inject(lines, w, h));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            handler.PostDelayed(Poll, 250);
        }
        handler.PostDelayed(Poll, 250);
    }

    /// <summary>Off the UI thread (Instrumentation's sync calls must not run on it).</summary>
    static void Inject(string[] lines, int width, int height)
    {
        var inst = new Instrumentation();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var raw in lines)
        {
            var p = raw.Trim().Split(':');
            if (p[0].Length == 0) continue;
            try
            {
                if (p[0] == "key" && p.Length > 1 && Enum.TryParse<Keycode>(p[1].Replace("_", ""), true, out var code))
                {
                    long now = SystemClock.UptimeMillis();
                    var source = code == Keycode.Back ? InputSourceType.Keyboard : InputSourceType.Gamepad;
                    foreach (var action in new[] { KeyEventActions.Down, KeyEventActions.Up })
                        inst.SendKeySync(new KeyEvent(now, SystemClock.UptimeMillis(), action, code, 0, 0, -1, 0, 0, source));
                }
                else if (p[0] == "touch" && p.Length > 2)
                {
                    float x = float.Parse(p[1], inv) * width, y = float.Parse(p[2], inv) * height;
                    long down = SystemClock.UptimeMillis();
                    inst.SendPointerSync(MotionEvent.Obtain(down, down, MotionEventActions.Down, x, y, 0));
                    inst.SendPointerSync(MotionEvent.Obtain(down, SystemClock.UptimeMillis() + 60, MotionEventActions.Up, x, y, 0));
                }
                else { Log.Warn($"{EnvVars.AndroidUiInput}: unknown line '{raw}'"); continue; }
                Log.Info($"{EnvVars.AndroidUiInput}: {raw}");
            }
            catch (Exception e) { Log.Warn($"{EnvVars.AndroidUiInput}: '{raw}': {e.Message}"); }
            Thread.Sleep(200);
        }
    }
}
#endif
