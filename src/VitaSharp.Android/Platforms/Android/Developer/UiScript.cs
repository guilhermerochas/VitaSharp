#if VITASHARP_DEVELOPER
using Android.App;
using Android.OS;
using Android.Views;
using AView = Android.Views.View;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Developer;

/// <summary>
/// Developer builds: VITASHARP_ANDROID_UI_INPUT names a file polled every 250 ms on the UI thread of an activity (the
/// game screen polls "&lt;path&gt;.game", the app's screens "&lt;path&gt;", since they run in two processes), so it also
/// works while the game is paused and the scripted pad is not polled. Each line is injected into that activity through
/// the activity's own dispatch (see <see cref="Inject"/>): "key:dpad_down" / "key:button_a" (any
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
                    Task.Run(() => Inject(activity, lines, w, h));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            handler.PostDelayed(Poll, 250);
        }
        handler.PostDelayed(Poll, 250);
    }

    /// <summary>Off the UI thread: each line is dispatched on the UI thread, 200 ms apart. HyperOS refuses
    /// Instrumentation's injection even into the app's own focused window (INJECT_EVENTS), so the events go straight
    /// into the activity: <see cref="Activity.DispatchKeyEvent"/> / <see cref="Activity.DispatchTouchEvent"/>, a D-pad key
    /// nobody took moves the focus as the view root would (FocusSearch), and "key:back" is the activity's back
    /// (OnBackPressed, which the system's back-invoked callback also reaches).</summary>
    static void Inject(Activity activity, string[] lines, int width, int height)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var raw in lines)
        {
            var p = raw.Trim().Split(':');
            if (p[0].Length == 0) continue;
            using var done = new ManualResetEventSlim();
            string? error = null;
            activity.RunOnUiThread(() =>
            {
                try
                {
                    error = Dispatch(activity, p, width, height, inv);
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait(2000);
            if (error != null) Log.Warn($"{EnvVars.AndroidUiInput}: '{raw}': {error}");
            else Log.Info($"{EnvVars.AndroidUiInput}: {raw}");
            Thread.Sleep(200);
        }
    }

    static string? Dispatch(Activity activity, string[] p, int width, int height, IFormatProvider inv)
    {
        if (p[0] == "key" && p.Length > 1 && Enum.TryParse<Keycode>(p[1].Replace("_", ""), true, out var code))
        {
            if (code == Keycode.Back)
            {
#pragma warning disable CA1422, CS0618 // the back of the activity itself (GameActivity opens the pause menu there)
                activity.OnBackPressed();
#pragma warning restore CA1422, CS0618
                return null;
            }
            FocusSearchDirection? dir = code switch
            {
                Keycode.DpadUp => FocusSearchDirection.Up, Keycode.DpadDown => FocusSearchDirection.Down,
                Keycode.DpadLeft => FocusSearchDirection.Left, Keycode.DpadRight => FocusSearchDirection.Right, _ => null,
            };
            // a real key leaves touch mode first and focuses the window's first focusable view when none has the focus
            // (ViewRootImpl.ensureTouchMode); a D-pad press that only did that is consumed, as on a device
            if (activity.Window?.DecorView is { IsInTouchMode: true } decor)
            {
                // GetFocusables in touch mode lists only the views focusable in touch mode (none in the pause menu):
                // the views a key can focus are all the focusable ones, and leaving touch mode restores the default
                // focus (View.FocusedByDefault) before the first one (ViewRootImpl.leaveTouchMode)
                var all = new List<AView>();
                decor.AddFocusables(all, FocusSearchDirection.Forward, FocusablesFlags.All);
                var target = activity.CurrentFocus ?? all.FirstOrDefault(v => v.FocusedByDefault) ?? all.FirstOrDefault();
                target?.RequestFocusFromTouch();
                if (dir != null && target != null && activity.CurrentFocus == target) return null;
            }
            long now = SystemClock.UptimeMillis();
            bool taken = activity.DispatchKeyEvent(new KeyEvent(now, now, KeyEventActions.Down, code, 0, 0, -1, 0, 0, InputSourceType.Gamepad));
            activity.DispatchKeyEvent(new KeyEvent(now, SystemClock.UptimeMillis(), KeyEventActions.Up, code, 0, 0, -1, 0, 0, InputSourceType.Gamepad));
            if (!taken && dir is { } d && activity.CurrentFocus is { } focus) focus.FocusSearch(d)?.RequestFocus(d);
            return null;
        }
        if (p[0] == "touch" && p.Length > 2)
        {
            float x = float.Parse(p[1], inv) * width, y = float.Parse(p[2], inv) * height;
            long down = SystemClock.UptimeMillis();
            using (var e = MotionEvent.Obtain(down, down, MotionEventActions.Down, x, y, 0)!) activity.DispatchTouchEvent(e);
            using (var e = MotionEvent.Obtain(down, down + 60, MotionEventActions.Up, x, y, 0)!) activity.DispatchTouchEvent(e);
            return null;
        }
        return "unknown line";
    }
}
#endif
